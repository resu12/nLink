using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private sealed record FallbackFrontierRepairIdentity(
        IReadOnlyList<FileTransferRangeV4> Ranges,
        string CanonicalRepairKey,
        string RangeFingerprint,
        int FrontierChunkIndex,
        int DurableReceivedHighestChunkIndex,
        int RangeCount,
        int RequestedChunkCount);

    private sealed record InboundFallbackFrontierProgressReceipt(
        FallbackFrontierRepairContract Contract,
        FallbackFrontierRepairAuthority Authority,
        string ProtocolRepairRequestId,
        string RequestCorrelationId,
        string RangeFingerprint,
        FileTransferTransportKind ReceivedTransport,
        IFileTransferDataSession DataSession,
        string DataSessionIdentity,
        long DataSessionGeneration,
        int PreviousFrontierChunkIndex,
        int CurrentFrontierChunkIndex);

    private enum FallbackFrontierRepairAcceptance
    {
        Rejected = 0,
        ExactDuplicate = 1,
        ActiveAlias = 2,
        NewContract = 3,
    }

    private static FallbackFrontierRepairIdentity? CreateFallbackFrontierRepairIdentity(
        IReadOnlyList<FileTransferRangeV4> ranges,
        int chunkCount,
        int durableReceivedHighestChunkIndex)
    {
        if (chunkCount <= 0 || ranges.Count == 0)
        {
            return null;
        }

        var normalized = new List<FileTransferRangeV4>(ranges.Count);
        foreach (var range in ranges.OrderBy(static item => item.StartChunkIndex).ThenBy(static item => item.ChunkCount))
        {
            var start = Math.Clamp(range.StartChunkIndex, 0, chunkCount);
            var endExclusive = (int)Math.Clamp(
                (long)range.StartChunkIndex + Math.Max(0, range.ChunkCount),
                start,
                chunkCount);
            if (endExclusive <= start)
            {
                continue;
            }

            if (normalized.Count > 0)
            {
                var previous = normalized[^1];
                var previousEndExclusive = previous.StartChunkIndex + previous.ChunkCount;
                if (start <= previousEndExclusive)
                {
                    normalized[^1] = new FileTransferRangeV4
                    {
                        StartChunkIndex = previous.StartChunkIndex,
                        ChunkCount = Math.Max(previousEndExclusive, endExclusive) - previous.StartChunkIndex,
                    };
                    continue;
                }
            }

            normalized.Add(new FileTransferRangeV4
            {
                StartChunkIndex = start,
                ChunkCount = endExclusive - start,
            });
        }

        if (normalized.Count == 0)
        {
            return null;
        }

        var canonicalRanges = string.Join(
            ";",
            normalized.Select(static range => string.Create(
                CultureInfo.InvariantCulture,
                $"{range.StartChunkIndex}:{range.ChunkCount}")));
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRanges));
        var rangeFingerprint = $"frng-{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
        var frontierChunkIndex = normalized[0].StartChunkIndex;
        var requestedChunkCount = normalized.Sum(static range => range.ChunkCount);
        var canonicalRepairKey = string.Create(
            CultureInfo.InvariantCulture,
            $"fallback-frontier:{frontierChunkIndex}:{requestedChunkCount}:{rangeFingerprint}");
        return new FallbackFrontierRepairIdentity(
            normalized,
            canonicalRepairKey,
            rangeFingerprint,
            frontierChunkIndex,
            Math.Max(-1, durableReceivedHighestChunkIndex),
            normalized.Count,
            requestedChunkCount);
    }

    private static string CreateFallbackFrontierRequestCorrelationId(
        FileTransferAuthorityId authorityId,
        FallbackFrontierRepairIdentity identity,
        string protocolRepairRequestId,
        string priority,
        string recoveryMode)
    {
        var canonical = string.Join(
            "|",
            "fallback-frontier-request",
            authorityId.ToString(),
            identity.CanonicalRepairKey,
            identity.RangeFingerprint,
            protocolRepairRequestId,
            priority,
            recoveryMode);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return $"frq-{Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant()}";
    }

    private static int ResolveFallbackFrontierDurableHighest(
        IReadOnlyList<FileTransferRangeV4> ranges)
        => ranges.Count == 0
            ? -1
            : (int)Math.Clamp(
                ranges.Max(static range =>
                    (long)range.StartChunkIndex + Math.Max(0, range.ChunkCount) - 1),
                -1,
                int.MaxValue);

    private static bool IsFallbackFrontierRouteRuntime(OutboundTransferContext context)
        => context.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
           context.RouteSelection.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
           context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V6;

    private static bool IsStrictFallbackFrontierRuntime(OutboundTransferContext context)
        => IsFallbackFrontierRouteRuntime(context) &&
           context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
           IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg) &&
           context.CurrentRuntimeOwner is not null;

    private static bool IsFallbackFrontierRouteRuntime(InboundTransferContext context)
        => context.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
           context.RouteSelection.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
           context.RouteRuntime.UsesPostTunaFallbackV6Runtime &&
           context.RouteRuntime.FrameFamily == FileTransferFrameFamily.V6;

    private static bool IsStrictFallbackFrontierRuntime(InboundTransferContext context)
        => IsFallbackFrontierRouteRuntime(context) &&
           context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
           IsCurrentPostTunaFallbackLeg(context.CurrentTransferLeg) &&
           context.CurrentRuntimeOwner is not null;

    private static bool IsFallbackFrontierRepairContractCurrent(
        OutboundTransferContext context,
        FallbackFrontierRepairContract contract,
        out string rejectionReason)
    {
        if (!IsStrictFallbackFrontierRuntime(context))
        {
            rejectionReason = "fallback_frontier_runtime_mismatch";
            return false;
        }

        if (!IsOutboundFallbackFrontierPeerRequestBindingCurrent(context, contract))
        {
            rejectionReason = "fallback_frontier_peer_request_scope_changed";
            return false;
        }

        return FileTransferCoordinator.IsRepairReplayDecisionCurrent(
            CreateCoordinatorStateLocked(context),
            contract.RepairDecision,
            out rejectionReason);
    }

    private static bool IsFallbackFrontierRepairContractCurrent(
        InboundTransferContext context,
        FallbackFrontierRepairContract contract,
        out string rejectionReason)
    {
        if (!IsStrictFallbackFrontierRuntime(context))
        {
            rejectionReason = "fallback_frontier_runtime_mismatch";
            return false;
        }

        return FileTransferCoordinator.IsRepairReplayDecisionCurrent(
            CreateCoordinatorStateLocked(context),
            contract.RepairDecision,
            out rejectionReason);
    }

    private bool TryIssueFallbackFrontierRepairContractLocked(
        OutboundTransferContext context,
        FallbackFrontierRepairIdentity identity,
        FallbackFrontierRepairSourceKind sourceKind,
        string protocolRepairRequestId,
        string? requestCorrelationId,
        string priority,
        string recoveryMode,
        string decisionReason,
        FileTransferAuthorityId? predecessorAuthorityId,
        out FallbackFrontierRepairContract? contract,
        out string rejectionReason)
    {
        contract = null;
        rejectionReason = "fallback_frontier_runtime_mismatch";
        if (!IsStrictFallbackFrontierRuntime(context))
        {
            return false;
        }

        if (sourceKind == FallbackFrontierRepairSourceKind.PeerRequest &&
            string.IsNullOrWhiteSpace(requestCorrelationId))
        {
            rejectionReason = "request_correlation_missing";
            return false;
        }

        if (!TryAuthorizeRepairReplayLocked(
                context,
                context.CurrentRuntimeOwner,
                RepairReplayKind.FrontierStallRepair,
                decisionReason,
                identity.CanonicalRepairKey,
                context.CurrentTransferLeg,
                context.CurrentTransferLeg?.TransportEpochId,
                context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                out var repairDecision,
                predecessorAuthorityId) ||
            repairDecision is not { } decision)
        {
            rejectionReason = "coordinator_authority_rejected";
            return false;
        }

        var correlationId = string.IsNullOrWhiteSpace(requestCorrelationId)
            ? CreateFallbackFrontierRequestCorrelationId(
                decision.AuthorityId,
                identity,
                protocolRepairRequestId,
                priority,
                recoveryMode)
            : requestCorrelationId.Trim();
        if (!FileTransferCoordinator.TryIssueFallbackFrontierRepairAuthority(
                decision,
                new FallbackFrontierRepairAuthorityRequest(
                    identity.CanonicalRepairKey,
                    identity.RangeFingerprint,
                    identity.FrontierChunkIndex,
                    identity.DurableReceivedHighestChunkIndex,
                    identity.RangeCount,
                    identity.RequestedChunkCount,
                    sourceKind,
                    protocolRepairRequestId,
                    correlationId,
                    context.CurrentTransferLeg?.CheckpointRequestId ?? "(none)",
                    priority,
                    recoveryMode,
                    decisionReason),
                out var authority,
                out rejectionReason) ||
            authority is not { } issuedAuthority)
        {
            return false;
        }

        contract = CreateFallbackFrontierRepairContract(identity, decision, issuedAuthority);
        if (sourceKind == FallbackFrontierRepairSourceKind.PeerRequest)
        {
            contract.PeerRequestBinding = new FallbackFrontierPeerRequestBinding(
                issuedAuthority,
                context.DataSession,
                context.DataSessionGeneration,
                context.CurrentTransferLeg?.CheckpointGeneration ?? 0);
        }
        LogFallbackFrontierRepairAuthorityGranted(contract, "receive");
        return true;
    }

    private bool TryIssueFallbackFrontierRepairContractLocked(
        InboundTransferContext context,
        FallbackFrontierRepairIdentity identity,
        FallbackFrontierRepairSourceKind sourceKind,
        string protocolRepairRequestId,
        string? requestCorrelationId,
        string priority,
        string recoveryMode,
        string decisionReason,
        FileTransferAuthorityId? predecessorAuthorityId,
        out FallbackFrontierRepairContract? contract,
        out string rejectionReason)
    {
        contract = null;
        rejectionReason = "fallback_frontier_runtime_mismatch";
        if (!IsStrictFallbackFrontierRuntime(context))
        {
            return false;
        }

        if (!TryAuthorizeRepairReplayLocked(
                context,
                context.CurrentRuntimeOwner,
                RepairReplayKind.FrontierStallRepair,
                decisionReason,
                identity.CanonicalRepairKey,
                context.CurrentTransferLeg,
                context.CurrentTransferLeg?.TransportEpochId,
                context.CurrentTransferLeg?.BridgeRecoveryGeneration,
                null,
                out var repairDecision,
                predecessorAuthorityId) ||
            repairDecision is not { } decision)
        {
            rejectionReason = "coordinator_authority_rejected";
            return false;
        }

        var correlationId = string.IsNullOrWhiteSpace(requestCorrelationId)
            ? CreateFallbackFrontierRequestCorrelationId(
                decision.AuthorityId,
                identity,
                protocolRepairRequestId,
                priority,
                recoveryMode)
            : requestCorrelationId.Trim();
        if (!FileTransferCoordinator.TryIssueFallbackFrontierRepairAuthority(
                decision,
                new FallbackFrontierRepairAuthorityRequest(
                    identity.CanonicalRepairKey,
                    identity.RangeFingerprint,
                    identity.FrontierChunkIndex,
                    identity.DurableReceivedHighestChunkIndex,
                    identity.RangeCount,
                    identity.RequestedChunkCount,
                    sourceKind,
                    protocolRepairRequestId,
                    correlationId,
                    context.CurrentTransferLeg?.CheckpointRequestId ?? "(none)",
                    priority,
                    recoveryMode,
                    decisionReason),
                out var authority,
                out rejectionReason) ||
            authority is not { } issuedAuthority)
        {
            return false;
        }

        contract = CreateFallbackFrontierRepairContract(identity, decision, issuedAuthority);
        var continuity = FileTransferCoordinator.ValidateCheckpointRepairContinuity(
            CreateCoordinatorStateLocked(context), issuedAuthority);
        if (continuity.SnapshotEnvelope is not null)
        {
            AdoptCoordinatorStateLocked(context, continuity);
        }
        contract.CheckpointContinuityStamp = continuity.Stamp;
        LogFallbackFrontierRepairAuthorityGranted(contract, "request");
        return true;
    }

    private static FallbackFrontierRepairContract CreateFallbackFrontierRepairContract(
        FallbackFrontierRepairIdentity identity,
        RepairReplayDecision decision,
        FallbackFrontierRepairAuthority authority)
    {
        var contract = new FallbackFrontierRepairContract
        {
            Identity = identity,
            RepairDecision = decision,
            Authority = authority,
            Lifecycle = new FileTransferAuthorityLifecycle<FallbackFrontierRepairSettlementOutcome>(authority.AuthorityId),
        };
        contract.Aliases.Add(new FallbackFrontierRepairAlias(
            authority.ProtocolRepairRequestId,
            authority.RequestCorrelationId,
            authority.RangeFingerprint));
        return contract;
    }

    private static bool TryAddFallbackFrontierRepairAlias(
        FallbackFrontierRepairContract contract,
        string protocolRepairRequestId,
        string requestCorrelationId,
        string rangeFingerprint,
        out bool exactDuplicate)
    {
        exactDuplicate = contract.Aliases.Any(alias =>
            string.Equals(alias.RequestCorrelationId, requestCorrelationId, StringComparison.Ordinal));
        if (exactDuplicate)
        {
            return true;
        }

        if (contract.Aliases.Count >= FallbackFrontierRepairAliasLimit)
        {
            contract.Aliases.RemoveAt(0);
            contract.AliasOverflowCount++;
        }

        contract.Aliases.Add(new FallbackFrontierRepairAlias(
            protocolRepairRequestId,
            requestCorrelationId,
            rangeFingerprint));
        return true;
    }

    private static bool IsSameFallbackFrontierRepairIdentity(
        FallbackFrontierRepairContract contract,
        FallbackFrontierRepairIdentity identity)
        => string.Equals(
               contract.Authority.CanonicalRepairKey,
               identity.CanonicalRepairKey,
               StringComparison.Ordinal) &&
           string.Equals(
               contract.Authority.RangeFingerprint,
               identity.RangeFingerprint,
               StringComparison.Ordinal);

    private static FallbackFrontierRepairAlias? FindFallbackFrontierRepairCorrelationAlias(
        FallbackFrontierRepairContract contract,
        string? requestCorrelationId)
        => string.IsNullOrWhiteSpace(requestCorrelationId)
            ? null
            : contract.Aliases.FirstOrDefault(alias => string.Equals(
                alias.RequestCorrelationId,
                requestCorrelationId,
                StringComparison.Ordinal));

    private static bool IsFallbackFrontierRepairProtocolAlias(
        FallbackFrontierRepairContract contract,
        string protocolRepairRequestId)
        => !string.IsNullOrWhiteSpace(protocolRepairRequestId) &&
           contract.Aliases.Any(alias => string.Equals(
               alias.ProtocolRepairRequestId,
               protocolRepairRequestId,
               StringComparison.Ordinal));

    private static bool TryValidateFallbackFrontierRepairProofLocked(
        OutboundTransferContext context,
        string protocolRepairRequestId,
        long transportEpoch,
        out string rejectionReason)
    {
        var contract = context.CurrentFallbackFrontierRepairContract;
        if (contract is null)
        {
            rejectionReason = "fallback_frontier_contract_missing";
            return false;
        }

        if (contract.Lifecycle.IsSettled)
        {
            rejectionReason = "fallback_frontier_contract_settled";
            return false;
        }

        if (!IsFallbackFrontierRepairContractCurrent(context, contract, out rejectionReason))
        {
            return false;
        }

        if (transportEpoch != contract.Authority.TransportEpoch)
        {
            rejectionReason = "fallback_frontier_transport_epoch_mismatch";
            return false;
        }

        if (!IsFallbackFrontierRepairProtocolAlias(contract, protocolRepairRequestId))
        {
            rejectionReason = "fallback_frontier_protocol_alias_mismatch";
            return false;
        }

        rejectionReason = "accepted";
        return true;
    }

    private static bool TryValidateFallbackFrontierRepairProofLocked(
        InboundTransferContext context,
        string protocolRepairRequestId,
        long transportEpoch,
        out string rejectionReason)
    {
        var contract = context.CurrentFallbackFrontierRepairContract;
        if (contract is null)
        {
            rejectionReason = "fallback_frontier_contract_missing";
            return false;
        }

        if (contract.Lifecycle.IsSettled)
        {
            rejectionReason = "fallback_frontier_contract_settled";
            return false;
        }

        if (!IsFallbackFrontierRepairContractCurrent(context, contract, out rejectionReason))
        {
            return false;
        }

        if (transportEpoch != contract.Authority.TransportEpoch)
        {
            rejectionReason = "fallback_frontier_transport_epoch_mismatch";
            return false;
        }

        if (!IsFallbackFrontierRepairProtocolAlias(contract, protocolRepairRequestId))
        {
            rejectionReason = "fallback_frontier_protocol_alias_mismatch";
            return false;
        }

        rejectionReason = "accepted";
        return true;
    }

    private static bool TryGetInboundFallbackFrontierBatchContractLocked(
        InboundTransferContext context,
        FileTransferChunkBatchFrameV6 batch,
        out FallbackFrontierRepairContract? contract,
        out string rejectionReason)
    {
        contract = null;
        rejectionReason = "accepted";
        if (!IsFallbackFrontierRouteRuntime(context))
        {
            return true;
        }

        if (!IsStrictFallbackFrontierRuntime(context))
        {
            rejectionReason = "fallback_runtime_owner_or_leg_missing";
            return false;
        }

        var current = context.CurrentFallbackFrontierRepairContract;
        var batchEndExclusive = (long)batch.StartChunkIndex + Math.Max(0, batch.ChunkCount);
        var overlapsCurrentWindow = current is not null && current.Identity.Ranges.Any(range =>
            range.StartChunkIndex < batchEndExclusive &&
            (long)range.StartChunkIndex + range.ChunkCount > batch.StartChunkIndex);
        var contractBound = overlapsCurrentWindow ||
                            !string.IsNullOrWhiteSpace(batch.RepairRequestId);
        if (!contractBound)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(batch.RepairRequestId))
        {
            rejectionReason = "fallback_frontier_protocol_alias_missing";
            return false;
        }

        if (!TryValidateFallbackFrontierRepairProofLocked(
                context,
                batch.RepairRequestId.Trim(),
                batch.TransportEpoch,
                out rejectionReason))
        {
            return false;
        }

        contract = current;
        return true;
    }

    private static bool TryValidateInboundFallbackFrontierProgressProofLocked(
        InboundTransferContext context,
        FallbackFrontierRepairContract contract,
        string protocolRepairRequestId,
        long transportEpoch,
        out string rejectionReason)
    {
        if (!ReferenceEquals(context.CurrentFallbackFrontierRepairContract, contract))
        {
            rejectionReason = "fallback_frontier_contract_replaced";
            return false;
        }

        if (contract.Lifecycle.SettlementOutcome != FallbackFrontierRepairSettlementOutcome.ProgressObserved)
        {
            rejectionReason = "fallback_frontier_contract_not_progress_settled";
            return false;
        }

        if (!IsFallbackFrontierRepairContractCurrent(context, contract, out rejectionReason))
        {
            return false;
        }

        if (transportEpoch != contract.Authority.TransportEpoch)
        {
            rejectionReason = "fallback_frontier_transport_epoch_mismatch";
            return false;
        }

        if (!IsFallbackFrontierRepairProtocolAlias(contract, protocolRepairRequestId))
        {
            rejectionReason = "fallback_frontier_protocol_alias_mismatch";
            return false;
        }

        rejectionReason = "accepted";
        return true;
    }

    private static bool TryMarkFallbackFrontierRepairDispatched(
        FallbackFrontierRepairContract contract)
    {
        if (contract.Lifecycle.TryMarkDispatched())
        {
            return true;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_frontier_repair_dispatch_duplicate_ignored; {FormatFallbackFrontierRepairAuthorityTuple(contract.Authority)}; decision_reason=dispatch_already_observed_or_settled");
        return false;
    }

    private static bool TrySettleFallbackFrontierRepairContract(
        FallbackFrontierRepairContract? contract,
        FallbackFrontierRepairSettlementOutcome outcome,
        string decisionReason,
        FileTransferAuthorityId? successorAuthorityId = null)
    {
        if (contract is null)
        {
            return false;
        }

        var authority = contract.Authority;
        if (!contract.Lifecycle.TrySettle(outcome, decisionReason, successorAuthorityId))
        {
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_fallback_frontier_repair_settlement_duplicate_ignored; {FormatFallbackFrontierRepairAuthorityTuple(authority)}; successor_authority_id={FormatProtocolLogValue(successorAuthorityId?.ToString() ?? "(none)")}; attempted_outcome={FormatProtocolLogValue(outcome.ToString())}; settled_outcome={FormatProtocolLogValue(contract.Lifecycle.SettlementOutcome?.ToString() ?? "(none)")}; decision_reason={FormatProtocolLogValue(decisionReason)}");
            return false;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_frontier_repair_contract_settled; {FormatCheckpointRepairContinuity(contract.CheckpointContinuityStamp)}{FormatFallbackFrontierRepairAuthorityTuple(authority)}; successor_authority_id={FormatProtocolLogValue(successorAuthorityId?.ToString() ?? "(none)")}; protocol_repair_request_id={FormatProtocolLogValue(authority.ProtocolRepairRequestId)}; request_correlation_id={FormatProtocolLogValue(authority.RequestCorrelationId)}; alias_count={contract.Aliases.Count}; alias_overflow_count={contract.AliasOverflowCount}; settlement_outcome={FormatProtocolLogValue(outcome.ToString())}; dispatch_observed={(contract.Lifecycle.DispatchObserved ? 1 : 0)}; decision_reason={FormatProtocolLogValue(decisionReason)}");
        return true;
    }

    private static void SettleAndClearFallbackFrontierRepairContractLocked(
        OutboundTransferContext context,
        FallbackFrontierRepairSettlementOutcome outcome,
        string reason)
    {
        var contract = context.CurrentFallbackFrontierRepairContract;
        var settled = TrySettleFallbackFrontierRepairContract(
            contract,
            outcome,
            reason);
        context.CurrentFallbackFrontierRepairContract = null;
        if (settled && contract is not null)
        {
            context.LastSettledFallbackFrontierRepairAuthority =
                contract.Authority;
            context.LastSettledFallbackFrontierRepairOutcome =
                outcome.ToString();
            context.LastSettledFallbackFrontierRepairUtcMs =
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
    }

    private static void SettleAndClearFallbackFrontierRepairContractLocked(
        InboundTransferContext context,
        FallbackFrontierRepairSettlementOutcome outcome,
        string reason)
    {
        TrySettleFallbackFrontierRepairContract(
            context.CurrentFallbackFrontierRepairContract,
            outcome,
            reason);
        context.CurrentFallbackFrontierRepairContract = null;
    }

    private bool TryQueueFallbackTailYieldRetryDispatchUnblockRecheckLocked(
        OutboundTransferContext context,
        FileTransferFallbackTailPairedReleaseReceipt pairedRelease)
    {
        if (context.IsTerminal ||
            context.CoordinatorPersistentState.FallbackTailYield
                .CurrentRetryDispatchHold is not { } hold ||
            hold.Release != pairedRelease ||
            context.CurrentFallbackFrontierRepairContract is { Lifecycle.IsSettled: false } ||
            context.LastSettledFallbackFrontierRepairAuthority is not
                { } settledRepair ||
            hold.BlockingRepair != settledRepair ||
            string.IsNullOrWhiteSpace(
                context.LastSettledFallbackFrontierRepairOutcome) ||
            context.LastSettledFallbackFrontierRepairOutcome is
                nameof(FallbackFrontierRepairSettlementOutcome.Superseded) or
                nameof(FallbackFrontierRepairSettlementOutcome.TransferTerminal) or
                nameof(FallbackFrontierRepairSettlementOutcome.TerminalRejected) ||
            context.LastSettledFallbackFrontierRepairUtcMs <= 0)
        {
            return false;
        }

        var observation =
            new FileTransferFallbackTailYieldRetryDispatchRecheckObservation(
                hold,
                settledRepair,
                context.LastSettledFallbackFrontierRepairOutcome,
                SuccessorAuthorityId: null,
                context.LastSettledFallbackFrontierRepairUtcMs);
        var issuance =
            FileTransferCoordinator.IssueFallbackTailYieldRetryDispatchRecheck(
                CreateCoordinatorStateLocked(context),
                observation);
        _ = AdoptCoordinatorStateLocked(context, issuance);
        var command = issuance.State.PersistentState.FallbackTailYield
            .PendingRetryDispatchRecheckCommand;
        if (!issuance.Accepted || command is not { IsDefault: false } currentCommand)
        {
            LogFallbackTailYieldRetryDispatchUnblockRecheck(
                context,
                hold,
                command,
                issuance.Status.ToString(),
                issuance.Reason);
            return issuance.Accepted;
        }

        var queued =
            transport is IRuntimeUnlockRetryDispatchAuthorityBridge bridge &&
            bridge.QueueFallbackTailYieldRetryDispatchRecheck(currentCommand);
        var receipt =
            new FileTransferFallbackTailYieldRetryDispatchRecheckReceipt(
                currentCommand,
                queued
                    ? FileTransferFallbackTailYieldRetryDispatchRecheckReceiptStatus.Queued
                    : FileTransferFallbackTailYieldRetryDispatchRecheckReceiptStatus.Rejected,
                queued
                    ? "fallback_tail_retry_dispatch_recheck_queued"
                    : "fallback_tail_retry_dispatch_recheck_queue_rejected");
        var application =
            FileTransferCoordinator.ApplyFallbackTailYieldRetryDispatchRecheckReceipt(
                CreateCoordinatorStateLocked(context),
                receipt);
        _ = AdoptCoordinatorStateLocked(context, application);
        LogFallbackTailYieldRetryDispatchUnblockRecheck(
            context,
            hold,
            currentCommand,
            application.Status.ToString(),
            application.Reason);
        return queued;
    }

    private static void CarryFallbackTailYieldRetryDispatchHoldToRepairSuccessorLocked(
        OutboundTransferContext context,
        FallbackFrontierRepairContract? predecessor,
        FallbackFrontierRepairContract successor)
    {
        if (context.CoordinatorPersistentState.FallbackTailYield
                .CurrentRetryDispatchHold is not { } hold)
        {
            return;
        }

        FileTransferFallbackTailYieldTransitionDecision binding;
        var successorAuthority = successor.Authority;
        if (predecessor is null ||
            !ReferenceEquals(
                context.CurrentFallbackFrontierRepairContract,
                successor) ||
            predecessor.Lifecycle.SettlementOutcome !=
                FallbackFrontierRepairSettlementOutcome.Superseded ||
            predecessor.Lifecycle.SuccessorAuthorityId !=
                successorAuthority.AuthorityId ||
            successorAuthority.PredecessorAuthorityId !=
                predecessor.Authority.AuthorityId)
        {
            binding = FileTransferCoordinator
                .RejectFallbackTailYieldRetryDispatchHoldSuccessor(
                    CreateCoordinatorStateLocked(context),
                    predecessor?.Authority,
                    successorAuthority,
                    "fallback_tail_retry_dispatch_hold_successor_lifecycle_mismatch");
        }
        else
        {
            binding = FileTransferCoordinator
                .RebindFallbackTailYieldRetryDispatchHoldToRepairSuccessor(
                    CreateCoordinatorStateLocked(context),
                    predecessor.Authority,
                    successorAuthority);
        }

        _ = AdoptCoordinatorStateLocked(context, binding);

        LogFallbackTailYieldRetryDispatchHoldBindingDecision(
            context,
            hold.Request,
            successorAuthority,
            binding,
            "repair_successor",
            predecessor?.Authority.AuthorityId);
        if (binding.Status !=
            FileTransferFallbackTailYieldTransitionStatus.Accepted)
        {
            return;
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_retry_dispatch_fallback_tail_hold_bound; authority_schema={FileTransferAuthoritySchema.Current}; authority_id={FormatProtocolLogValue(successorAuthority.AuthorityId.ToString())}; predecessor_authority_id={FormatProtocolLogValue(predecessor?.Authority.AuthorityId.ToString() ?? "(none)")}; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; direction=outbound; repair_generation={successorAuthority.DecisionGeneration}; hold_generation={binding.State.PersistentState.FallbackTailYield.CurrentRetryDispatchHold?.HoldGeneration ?? 0}; binding_mode=repair_successor; decision_status={FormatProtocolLogValue(binding.Status.ToString())}; decision_reason={FormatProtocolLogValue(binding.Reason)}");
    }

    private static void LogFallbackTailYieldRetryDispatchUnblockRecheck(
        OutboundTransferContext context,
        FileTransferFallbackTailYieldRetryDispatchHold hold,
        FileTransferFallbackTailYieldRetryDispatchRecheckCommand? command,
        string status,
        string reason)
    {
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_decided; authority_schema={FileTransferAuthoritySchema.Current}; authority_id={FormatProtocolLogValue(hold.Release.ReleaseAuthorityId.ToString())}; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; direction=outbound; release_generation={hold.Release.ReleaseGeneration}; repair_authority_id={FormatProtocolLogValue(hold.BlockingRepair.AuthorityId.ToString())}; repair_generation={hold.BlockingRepair.DecisionGeneration}; hold_generation={hold.HoldGeneration}; recheck_generation={command?.RecheckGeneration ?? 0}; decision_status={FormatProtocolLogValue(status)}; decision_reason={FormatProtocolLogValue(reason)}");
    }

    private static string FormatFallbackFrontierRepairAuthorityTuple(
        FallbackFrontierRepairAuthority authority)
        => $"authority_schema={FileTransferAuthoritySchema.Current}; authority_id={FormatProtocolLogValue(authority.AuthorityId.ToString())}; predecessor_authority_id={FormatProtocolLogValue(authority.PredecessorAuthorityId?.ToString() ?? "(none)")}; direction={FormatFileTransferDirection(authority.Direction)}; transfer_id={FormatProtocolLogValue(authority.TransferId)}; session_id={FormatProtocolLogValue(authority.SessionId)}; route={FormatProtocolLogValue(authority.RouteToken)}; protocol_version={authority.ProtocolVersion}; live_route_epoch={authority.LiveRouteEpoch}; runtime_generation={authority.RuntimeGeneration}; terminal_generation={authority.TerminalGeneration}; leg_generation={authority.LegGeneration}; transport_epoch={authority.TransportEpoch}; bridge_recovery_generation={authority.BridgeRecoveryGeneration}; bridge_decision_generation={authority.BridgeDecisionGeneration}; checkpoint_request_id={FormatProtocolLogValue(authority.CheckpointRequestId)}; route_publication_generation={authority.RoutePublicationGeneration}; route_commit_generation={authority.RouteCommitGeneration ?? 0}; repair_generation={authority.DecisionGeneration}; repair_request_key={FormatProtocolLogValue(authority.CanonicalRepairKey)}; range_fingerprint={FormatProtocolLogValue(authority.RangeFingerprint)}; frontier_chunk_index={authority.FrontierChunkIndex}; durable_received_highest_chunk_index={authority.DurableReceivedHighestChunkIndex}; range_count={authority.RangeCount}; requested_chunk_count={authority.RequestedChunkCount}; source_kind={FormatProtocolLogValue(authority.SourceKind.ToString())}; primary_protocol_repair_request_id={FormatProtocolLogValue(authority.ProtocolRepairRequestId)}; primary_request_correlation_id={FormatProtocolLogValue(authority.RequestCorrelationId)}; priority={FormatProtocolLogValue(authority.Priority)}; recovery_mode={FormatProtocolLogValue(authority.RecoveryMode)}; target_transport={FormatFileTransferTransportKind(authority.TargetTransport)}; authority_decision_reason={FormatProtocolLogValue(authority.DecisionReason)}";

    private static void LogFallbackFrontierRepairAuthorityGranted(
        FallbackFrontierRepairContract contract,
        string stage)
    {
        var authority = contract.Authority;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_frontier_repair_authority_granted; {FormatCheckpointRepairContinuity(contract.CheckpointContinuityStamp)}{FormatFallbackFrontierRepairAuthorityTuple(authority)}; protocol_repair_request_id={FormatProtocolLogValue(authority.ProtocolRepairRequestId)}; request_correlation_id={FormatProtocolLogValue(authority.RequestCorrelationId)}; stage={FormatProtocolLogValue(stage)}; decision_reason={FormatProtocolLogValue(authority.DecisionReason)}");
    }

    private async Task<bool> SendInboundAuthorizedFallbackFrontierRequestAsync(
        InboundTransferContext context,
        string reason,
        bool forceSend)
    {
        FileTransferFrontierRequestFrameV6 request;
        IFileTransferDataSession dataSession;
        FallbackFrontierRepairContract contract;
        bool reusedContract;
        int previousRescueRequestCount;
        int rescueRequestCount;
        int rescueStep;
        int frontierChunkIndex;

        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context) ||
                context.IsTerminal ||
                context.DataSession is null ||
                !context.PullManifestReceived ||
                context.UserPaused ||
                context.PeerPaused ||
                context.NextChunkIndex >= context.ChunkCount ||
                !IsStrictFallbackFrontierRuntime(context))
            {
                return false;
            }

            if (TrySuppressInboundV6ControlSendForTunaActivationBarrierLocked(
                    context,
                    "frontier_request",
                    reason))
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            var unresolvedFrontierProofRequest =
                IsV6TransportEpochUnresolved(context.V6TransportEpoch) &&
                context.V6TransportEpoch!.State is V6TransportEpochState.TargetProofPending
                    or V6TransportEpochState.FrontierRepairOnly
                    or V6TransportEpochState.WaitingForTargetTransport;
            if (!ShouldSendInboundV6FrontierRequestLocked(
                    context,
                    now,
                    unresolvedFrontierProofRequest,
                    reason))
            {
                return false;
            }

            var retryInterval = ResolveInboundV6FrontierRequestRetryIntervalLocked(
                context,
                unresolvedFrontierProofRequest);
            if (forceSend &&
                ShouldCoalesceForcedInboundV6PostTunaFallbackControlLocked(
                    context,
                    now,
                    context.V6LastFrontierRequestSentUtc,
                    context.V6LastFrontierRequestChunkIndex,
                    retryInterval,
                    "frontier_request",
                    reason))
            {
                return false;
            }

            if (!forceSend &&
                context.V6LastFrontierRequestSentUtc is { } lastSent &&
                now - lastSent < retryInterval)
            {
                return false;
            }

            var rescueStateChanged =
                context.V6PostTunaFallbackFrontierRescueChunkIndex != context.NextChunkIndex;
            var previousRescueChunkIndex = context.V6PostTunaFallbackFrontierRescueChunkIndex;
            var previousRescueStep = context.V6PostTunaFallbackFrontierRescueStep;
            var previousRescueCount = context.V6PostTunaFallbackFrontierRescueRequestCount;
            var previousRescueStartedUtc = context.V6PostTunaFallbackFrontierRescueStartedUtc;
            if (rescueStateChanged)
            {
                context.V6PostTunaFallbackFrontierRescueChunkIndex = context.NextChunkIndex;
                context.V6PostTunaFallbackFrontierRescueStep = 0;
                context.V6PostTunaFallbackFrontierRescueRequestCount = 0;
                context.V6PostTunaFallbackFrontierRescueStartedUtc = now;
            }

            rescueStep = context.V6PostTunaFallbackFrontierRescueStep;
            previousRescueRequestCount = context.V6PostTunaFallbackFrontierRescueRequestCount;
            frontierChunkIndex = context.NextChunkIndex;
            var strictEpochFrontierProof = IsV6TransportEpochUnresolved(context.V6TransportEpoch);
            var frontierRequestChunks = strictEpochFrontierProof
                ? ResolveInboundV6EpochFrontierRequestChunksLocked(context)
                : ResolveInboundV6FrontierRequestChunksLocked(context);
            var nextRequestSequence = context.V6FrontierRequestSequence + 1;
            var repairRequestId = strictEpochFrontierProof
                ? $"v6-frontier:{context.V6TransportEpoch!.EpochId}:{context.NextChunkIndex}:{nextRequestSequence}"
                : $"v6-frontier:{context.NextChunkIndex}:{nextRequestSequence}";
            var ranges = strictEpochFrontierProof
                ?
                [
                    new FileTransferRangeV4
                    {
                        StartChunkIndex = context.NextChunkIndex,
                        ChunkCount = Math.Min(frontierRequestChunks, context.ChunkCount - context.NextChunkIndex),
                    },
                ]
                : BuildInboundV6FrontierRequestRangesLocked(context, frontierRequestChunks);
            var identity = CreateFallbackFrontierRepairIdentity(
                ranges,
                context.ChunkCount,
                context.PullHighestReceivedChunkIndex);
            if (identity is null)
            {
                RestoreRescueState();
                return false;
            }

            var recoveryMode = strictEpochFrontierProof
                ? FormatV6TransportEpochState(context.V6TransportEpoch!.State)
                : IsInboundV6RegularNknFrontierControlBulkEscalatedLocked(context, now)
                    ? "regular_nkn_frontier_stall_control_bulk"
                    : "regular_nkn_frontier_stall";
            const string priority = "frontier";
            var currentContract = context.CurrentFallbackFrontierRepairContract;
            if (currentContract is not null &&
                !IsFallbackFrontierRepairContractCurrent(context, currentContract, out var currentRejectionReason))
            {
                TrySettleFallbackFrontierRepairContract(
                    currentContract,
                    FallbackFrontierRepairSettlementOutcome.StaleRejected,
                    currentRejectionReason);
                currentContract = null;
                context.CurrentFallbackFrontierRepairContract = null;
            }

            if (currentContract is not null &&
                !currentContract.Lifecycle.IsSettled &&
                currentContract.Lifecycle.DispatchObserved &&
                previousRescueRequestCount >= V6PostTunaFallbackFrontierRescueRequestsBeforeWiden)
            {
                var stallAgeMs = context.V6PostTunaFallbackFrontierRescueStartedUtc is { } startedUtc
                    ? (long)Math.Max(0, (now - startedUtc).TotalMilliseconds)
                    : 0;
                LogFallbackFrontierRepairStalled(
                    currentContract,
                    "post_tuna_fallback_v6_frontier_rescue_without_progress",
                    currentContract.Authority.FrontierChunkIndex,
                    context.PullHighestReceivedChunkIndex,
                    currentContract.Authority.RequestedChunkCount,
                    previousRescueRequestCount,
                    stallAgeMs);
                if (TrySettleFallbackFrontierRepairContract(
                        currentContract,
                        FallbackFrontierRepairSettlementOutcome.SettledStalled,
                        "post_tuna_fallback_v6_frontier_rescue_without_progress"))
                {
                    ClearInboundPostTunaFallbackControlPlanePressureAfterFrontierSettlement(
                        context,
                        currentContract,
                        "fallback_frontier_repair_stalled");
                }
            }

            reusedContract = currentContract is not null &&
                !currentContract.Lifecycle.IsSettled &&
                IsSameFallbackFrontierRepairIdentity(currentContract, identity);
            string correlationId;
            if (reusedContract)
            {
                contract = currentContract!;
                correlationId = CreateFallbackFrontierRequestCorrelationId(
                    contract.Authority.AuthorityId,
                    identity,
                    repairRequestId,
                    priority,
                    recoveryMode);
                if (!TryAddFallbackFrontierRepairAlias(
                        contract,
                        repairRequestId,
                        correlationId,
                        identity.RangeFingerprint,
                        out _))
                {
                    RestoreRescueState();
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_v6_frontier_request_receive_dispatch_coalesced; {FormatFallbackFrontierRepairAuthorityTuple(contract.Authority)}; request_correlation_id={FormatProtocolLogValue(correlationId)}; repair_request_id={FormatProtocolLogValue(repairRequestId)}; decision_reason=alias_limit_reached");
                    return false;
                }
            }
            else
            {
                var predecessorId = currentContract?.Authority.AuthorityId;
                if (!TryIssueFallbackFrontierRepairContractLocked(
                        context,
                        identity,
                        FallbackFrontierRepairSourceKind.ReceiverRequest,
                        repairRequestId,
                        null,
                        priority,
                        recoveryMode,
                        "fallback_frontier_request",
                        predecessorId,
                        out var issuedContract,
                        out var rejectionReason) ||
                    issuedContract is null)
                {
                    RestoreRescueState();
                    LocalOperationalLog.Info(
                        "FileTransferService",
                        $"event=filetransfer_fallback_frontier_repair_authority_rejected; direction=inbound; transfer_id={context.TransferId}; session_id={context.SessionId}; repair_request_key={FormatProtocolLogValue(identity.CanonicalRepairKey)}; request_correlation_id=(none); repair_request_id={FormatProtocolLogValue(repairRequestId)}; decision_reason={FormatProtocolLogValue(rejectionReason)}");
                    return false;
                }

                contract = issuedContract;
                correlationId = contract.Authority.RequestCorrelationId;
                if (currentContract is not null && !currentContract.Lifecycle.IsSettled)
                {
                    TrySettleFallbackFrontierRepairContract(
                        currentContract,
                        FallbackFrontierRepairSettlementOutcome.Superseded,
                        "fallback_frontier_successor_authorized",
                        contract.Authority.AuthorityId);
                }
            }

            context.CurrentFallbackFrontierRepairContract = contract;
            ResetInboundV6FrontierRecentProgressGraceWakeLocked(context);
            context.V6FrontierRequestSequence = nextRequestSequence;
            if (strictEpochFrontierProof)
            {
                context.V6TransportEpoch!.LastRepairRequestId = repairRequestId;
            }

            var frontierTransportEpoch = context.CurrentTransferLeg!.TransportEpochId;
            request = new FileTransferFrontierRequestFrameV6
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                TransportEpoch = frontierTransportEpoch,
                RepairRequestId = repairRequestId,
                RequestCorrelationId = correlationId,
                MissingRanges = identity.Ranges,
                Priority = priority,
                RecoveryMode = recoveryMode,
            };
            context.V6LastFrontierRequestSentUtc = now;
            context.V6LastFrontierRequestChunkIndex = context.NextChunkIndex;
            context.V6LastFrontierRequestId = repairRequestId;
            rescueRequestCount = AdvanceInboundV6PostTunaFallbackFrontierRescueLocked(context, contract);
            dataSession = context.DataSession;

            void RestoreRescueState()
            {
                if (!rescueStateChanged)
                {
                    return;
                }

                context.V6PostTunaFallbackFrontierRescueChunkIndex = previousRescueChunkIndex;
                context.V6PostTunaFallbackFrontierRescueStep = previousRescueStep;
                context.V6PostTunaFallbackFrontierRescueRequestCount = previousRescueCount;
                context.V6PostTunaFallbackFrontierRescueStartedUtc = previousRescueStartedUtc;
            }
        }

        try
        {
            FileTransferControlPlaneDeliveryRequest? controlPlaneRequest = null;
            lock (gate)
            {
                if (ReferenceEquals(inboundTransfer, context) &&
                    !context.IsTerminal &&
                    ReferenceEquals(context.CurrentFallbackFrontierRepairContract, contract) &&
                    IsFallbackFrontierRepairContractCurrent(context, contract, out _) &&
                    ShouldUseFallbackControlPlaneForInboundFrame(context, request))
                {
                    controlPlaneRequest = CreateInboundControlPlaneDeliveryRequestLocked(
                        context,
                        FileTransferControlPlaneKind.FrontierRequest,
                        request,
                        reason);
                }
            }

            if (controlPlaneRequest is not null)
            {
                var delivery = await SendInboundFallbackControlPlaneOrDataSessionAsync(
                        context,
                        dataSession,
                        controlPlaneRequest,
                        context.LifetimeCts.Token,
                        GetCheckpointRepairContinuityStamp(contract,
                            request.RepairRequestId, request.RequestCorrelationId))
                    .ConfigureAwait(false);
                if (!delivery.Sent)
                {
                    SettleFailedInboundFallbackFrontierDispatch(context, contract, "control_plane_delivery_rejected");
                    return false;
                }
            }
            else
            {
                await dataSession.SendAsync(request, context.LifetimeCts.Token).ConfigureAwait(false);
            }

            lock (gate)
            {
                if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal)
                {
                    TrySettleFallbackFrontierRepairContract(
                        contract,
                        FallbackFrontierRepairSettlementOutcome.TerminalRejected,
                        context.IsTerminal ? "request_send_completed_after_terminal" : "request_send_completed_for_stale_context");
                    return false;
                }

                var rejectionReason = "fallback_frontier_contract_replaced";
                if (!ReferenceEquals(context.CurrentFallbackFrontierRepairContract, contract) ||
                    !IsFallbackFrontierRepairContractCurrent(context, contract, out rejectionReason))
                {
                    TrySettleFallbackFrontierRepairContract(
                        contract,
                        FallbackFrontierRepairSettlementOutcome.StaleRejected,
                        rejectionReason);
                    return false;
                }

                if (TryMarkFallbackFrontierRepairDispatched(contract))
                {
                    LogFallbackFrontierRepairDispatched(
                        contract,
                        request.RepairRequestId ?? contract.Authority.ProtocolRepairRequestId,
                        "receiver_request_send",
                        request.MissingRanges.Sum(static range => range.ChunkCount),
                        context.NextChunkIndex,
                        context.PullHighestReceivedChunkIndex + 1);
                }
            }

            var totalRequestedChunks = request.MissingRanges.Sum(static range => range.ChunkCount);
            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_frontier_request_sent; {FormatCheckpointRepairContinuity(GetCheckpointRepairContinuityStamp(contract, request.RepairRequestId, request.RequestCorrelationId))}{FormatFallbackFrontierRepairAuthorityTuple(contract.Authority)}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId)}; protocol_repair_request_id={FormatProtocolLogValue(request.RepairRequestId)}; request_correlation_id={FormatProtocolLogValue(request.RequestCorrelationId)}; start_chunk_index={request.MissingRanges[0].StartChunkIndex}; request_alias_chunk_count={request.MissingRanges[0].ChunkCount}; total_requested_chunk_count={totalRequestedChunks}; post_tuna_fallback_survival=1; reason={FormatProtocolLogValue(reason)}");

            if (reusedContract)
            {
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_frontier_request_receive_dispatch_coalesced; {FormatFallbackFrontierRepairAuthorityTuple(contract.Authority)}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId)}; request_correlation_id={FormatProtocolLogValue(request.RequestCorrelationId)}; decision_reason=same_authorized_repair_window");
            }

            LocalOperationalLog.Info(
                "FileTransferService",
                $"event=filetransfer_v6_post_tuna_fallback_frontier_rescue_requested; {FormatFallbackFrontierRepairAuthorityTuple(contract.Authority)}; reason={FormatProtocolLogValue(reason)}; observed_frontier_chunk_index={frontierChunkIndex}; rescue_step={rescueStep}; rescue_request_count={rescueRequestCount}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId)}; request_correlation_id={FormatProtocolLogValue(request.RequestCorrelationId)}; request_alias_chunk_count={request.MissingRanges[0].ChunkCount}; total_requested_chunk_count={totalRequestedChunks}");

            return true;
        }
        catch (Exception ex) when (!context.LifetimeCts.IsCancellationRequested)
        {
            var allowRecovery = SettleFailedInboundFallbackFrontierDispatch(
                context,
                contract,
                ex is OperationCanceledException ? "request_send_canceled" : "request_send_failed");
            if (allowRecovery &&
                TryDeferInboundV6ControlSendForTunaActivationBarrier(
                    context,
                    "frontier_request",
                    reason,
                    ex,
                    state: null,
                    request))
            {
                return false;
            }

            if (allowRecovery &&
                TryDeferInboundV6ControlSendFailureForRecovery(context, "frontier_request", reason, ex))
            {
                return false;
            }

            LocalOperationalLog.Warn(
                "FileTransferService",
                $"event=filetransfer_v6_frontier_request_failed; {FormatFallbackFrontierRepairAuthorityTuple(contract.Authority)}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId)}; request_correlation_id={FormatProtocolLogValue(request.RequestCorrelationId)}; reason={FormatProtocolLogValue(reason)}; error={FormatProtocolLogValue(ex.Message)}");
            return false;
        }
    }

    private bool SettleFailedInboundFallbackFrontierDispatch(
        InboundTransferContext context,
        FallbackFrontierRepairContract contract,
        string reason)
    {
        lock (gate)
        {
            if (!ReferenceEquals(inboundTransfer, context))
            {
                TrySettleFallbackFrontierRepairContract(
                    contract,
                    FallbackFrontierRepairSettlementOutcome.StaleRejected,
                    reason);
                return false;
            }

            if (context.IsTerminal)
            {
                TrySettleFallbackFrontierRepairContract(
                    contract,
                    FallbackFrontierRepairSettlementOutcome.TerminalRejected,
                    reason);
                return false;
            }

            var rejectionReason = "fallback_frontier_contract_replaced";
            if (!ReferenceEquals(context.CurrentFallbackFrontierRepairContract, contract) ||
                !IsFallbackFrontierRepairContractCurrent(context, contract, out rejectionReason))
            {
                TrySettleFallbackFrontierRepairContract(
                    contract,
                    FallbackFrontierRepairSettlementOutcome.StaleRejected,
                    rejectionReason);
                return false;
            }

            return TrySettleFallbackFrontierRepairContract(
                contract,
                contract.Lifecycle.DispatchObserved
                    ? FallbackFrontierRepairSettlementOutcome.SettledStalled
                    : FallbackFrontierRepairSettlementOutcome.MissingDispatch,
                reason);
        }
    }

    private static InboundFallbackFrontierProgressReceipt?
        TrySettleInboundFallbackFrontierProgressLocked(
        InboundTransferContext context,
        FallbackFrontierRepairContract? expectedContract,
        string? protocolRepairRequestId,
        long transportEpoch,
        FileTransferTransportKind receivedTransport,
        int previousFrontierChunkIndex,
        int currentFrontierChunkIndex,
        string reason)
    {
        if (expectedContract is null ||
            !ReferenceEquals(context.CurrentFallbackFrontierRepairContract, expectedContract) ||
            string.IsNullOrWhiteSpace(protocolRepairRequestId) ||
            context.DataSession is null ||
            context.DataSessionGeneration <= 0 ||
            expectedContract.Lifecycle.IsSettled ||
            transportEpoch != expectedContract.Authority.TransportEpoch ||
            receivedTransport != expectedContract.Authority.TargetTransport ||
            currentFrontierChunkIndex <= previousFrontierChunkIndex ||
            currentFrontierChunkIndex <= expectedContract.Authority.FrontierChunkIndex)
        {
            return null;
        }

        var normalizedProtocolRepairRequestId = protocolRepairRequestId.Trim();
        var alias = expectedContract.Aliases.FirstOrDefault(candidate =>
            string.Equals(
                candidate.ProtocolRepairRequestId,
                normalizedProtocolRepairRequestId,
                StringComparison.Ordinal));
        if (alias is null ||
            !string.Equals(
                alias.RangeFingerprint,
                expectedContract.Authority.RangeFingerprint,
                StringComparison.Ordinal))
        {
            return null;
        }

        if (!IsFallbackFrontierRepairContractCurrent(context, expectedContract, out var rejectionReason))
        {
            TrySettleFallbackFrontierRepairContract(
                expectedContract,
                FallbackFrontierRepairSettlementOutcome.StaleRejected,
                rejectionReason);
            return null;
        }

        if (!TrySettleFallbackFrontierRepairContract(
                expectedContract,
                FallbackFrontierRepairSettlementOutcome.ProgressObserved,
                reason))
        {
            return null;
        }

        var authority = expectedContract.Authority;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_frontier_repair_progress_observed; {FormatCheckpointRepairContinuity(GetCheckpointRepairContinuityStamp(expectedContract, normalizedProtocolRepairRequestId, alias.RequestCorrelationId))}{FormatFallbackFrontierRepairAuthorityTuple(authority)}; request_correlation_id={FormatProtocolLogValue(authority.RequestCorrelationId)}; reason={FormatProtocolLogValue(reason)}; previous_frontier_chunk_index={previousFrontierChunkIndex}; current_frontier_chunk_index={currentFrontierChunkIndex}");

        return new InboundFallbackFrontierProgressReceipt(
            expectedContract,
            authority,
            normalizedProtocolRepairRequestId,
            alias.RequestCorrelationId,
            alias.RangeFingerprint,
            receivedTransport,
            context.DataSession,
            CreateReceiveRecoveryDataSessionIdentity(
                context.TransferId,
                context.DataSession,
                context.DataSessionGeneration),
            context.DataSessionGeneration,
            previousFrontierChunkIndex,
            currentFrontierChunkIndex);
    }

    private static bool TryValidateInboundFallbackFrontierProgressReceiptLocked(
        InboundTransferContext context,
        InboundFallbackFrontierProgressReceipt receipt,
        out string rejectionReason)
    {
        if (!ReferenceEquals(context.CurrentFallbackFrontierRepairContract, receipt.Contract) ||
            receipt.Authority != receipt.Contract.Authority)
        {
            rejectionReason = "fallback_frontier_contract_replaced";
            return false;
        }

        if (receipt.Contract.Lifecycle.SettlementOutcome !=
            FallbackFrontierRepairSettlementOutcome.ProgressObserved)
        {
            rejectionReason = "fallback_frontier_contract_not_progress_settled";
            return false;
        }

        if (!IsFallbackFrontierRepairContractCurrent(context, receipt.Contract, out rejectionReason))
        {
            return false;
        }

        if (!ReferenceEquals(context.DataSession, receipt.DataSession) ||
            context.DataSessionGeneration != receipt.DataSessionGeneration ||
            !string.Equals(
                CreateReceiveRecoveryDataSessionIdentity(
                    context.TransferId,
                    context.DataSession,
                    context.DataSessionGeneration),
                receipt.DataSessionIdentity,
                StringComparison.Ordinal))
        {
            rejectionReason = "fallback_frontier_data_session_replaced";
            return false;
        }

        var aliasCurrent = receipt.Contract.Aliases.Any(alias =>
            string.Equals(
                alias.ProtocolRepairRequestId,
                receipt.ProtocolRepairRequestId,
                StringComparison.Ordinal) &&
            string.Equals(
                alias.RequestCorrelationId,
                receipt.RequestCorrelationId,
                StringComparison.Ordinal) &&
            string.Equals(
                alias.RangeFingerprint,
                receipt.RangeFingerprint,
                StringComparison.Ordinal));
        if (!aliasCurrent ||
            receipt.ReceivedTransport != receipt.Authority.TargetTransport ||
            receipt.CurrentFrontierChunkIndex <= receipt.PreviousFrontierChunkIndex ||
            receipt.CurrentFrontierChunkIndex <= receipt.Authority.FrontierChunkIndex ||
            context.NextChunkIndex < receipt.CurrentFrontierChunkIndex)
        {
            rejectionReason = "fallback_frontier_progress_receipt_not_current";
            return false;
        }

        rejectionReason = "accepted";
        return true;
    }

    private bool
        TryHoldReceiveRecoveryFallbackCheckpointFrontierRequestLocked(
            OutboundTransferContext context,
            FileTransferFrontierRequestFrameV6 request,
            FileTransferTransportKind receivedTransportKind,
            FallbackFrontierRepairIdentity identity,
            FileTransferReceiveRecoveryFallbackCheckpointFrontierRequestDispatchPath
                dispatchPath,
            string receivingDataSessionIdentity,
            long receivingDataSessionGeneration,
            out FileTransferReceiveRecoveryDecision decision)
    {
        var state = CreateCoordinatorStateLocked(context);
        var binding = state.PersistentState.FileTransferReceiveRecovery
            .CurrentFallbackCheckpointBinding;
        if (binding is null)
        {
            decision = new FileTransferReceiveRecoveryDecision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_fallback_checkpoint_frontier_request_binding_missing")
            {
                State = state,
            };
            return false;
        }

        if (!PostTunaFallbackPeerProofFrameIdentity.TryCreate(
                request,
                out var frameIdentity))
        {
            decision = new FileTransferReceiveRecoveryDecision(
                FileTransferReceiveRecoveryTransitionStatus.Rejected,
                "filetransfer_receive_recovery_fallback_checkpoint_frontier_request_frame_identity_invalid")
            {
                State = state,
            };
            return false;
        }

        var observation =
            new FileTransferReceiveRecoveryFallbackCheckpointFrontierRequestObservation(
                binding,
                request with
                {
                    MissingRanges = request.MissingRanges.ToArray(),
                },
                dispatchPath,
                receivedTransportKind,
                receivingDataSessionIdentity,
                receivingDataSessionGeneration,
                identity.RangeFingerprint,
                frameIdentity,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        decision = FileTransferCoordinator
            .ObserveReceiveRecoveryFallbackCheckpointFrontierRequest(
                state,
                observation);
        decision = decision with
        {
            State = AdoptCoordinatorStateLocked(
                context,
                decision.State,
                "ReceiveRecoveryFallbackCheckpointFrontierRequestObserved",
                decision.Status.ToString(),
                decision.Reason),
        };
        var hold = decision.FallbackCheckpointFrontierRequestHold;
        var scope = binding.OriginalObservation;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_receive_recovery_fallback_checkpoint_frontier_request_hold_decided; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; direction=outbound; recovery_operation_authority_id={FormatProtocolLogValue(binding.ParentAuthority.AuthorityId.ToString())}; recovery_operation_generation={binding.ParentAuthority.OperationGeneration}; root_checkpoint_request_id={FormatProtocolLogValue(binding.ParentCheckpointRequestId ?? "(none)")}; root_checkpoint_generation={binding.ParentCheckpointGeneration}; effective_checkpoint_request_id={FormatProtocolLogValue(binding.EffectiveCheckpointRequestId)}; effective_checkpoint_generation={binding.EffectiveCheckpointGeneration}; checkpoint_binding_generation={binding.BindingGeneration}; checkpoint_binding_state={binding.State.ToString().ToLowerInvariant()}; checkpoint_frame_fingerprint={FormatProtocolLogValue(binding.FrameIdentity.Sha256)}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId ?? "(none)")}; request_correlation_id={FormatProtocolLogValue(request.RequestCorrelationId ?? "(none)")}; range_fingerprint={FormatProtocolLogValue(identity.RangeFingerprint)}; frontier_frame_type={FormatProtocolLogValue(frameIdentity.FrameType)}; frontier_frame_transport_epoch={frameIdentity.TransportEpoch}; frontier_frame_fingerprint={FormatProtocolLogValue(frameIdentity.Sha256)}; hold_generation={hold?.HoldGeneration ?? 0}; dispatch_path={FormatProtocolLogValue(dispatchPath.ToString())}; received_transport={FormatFileTransferTransportKind(receivedTransportKind)}; receiving_data_session_identity={FormatProtocolLogValue(receivingDataSessionIdentity)}; receiving_data_session_generation={receivingDataSessionGeneration}; route={FormatProtocolLogValue(scope.RouteToken)}; protocol_version={scope.ProtocolVersion}; live_route_epoch={scope.LiveRouteEpoch}; runtime_generation={scope.RuntimeOwner.RuntimeGeneration}; route_publication_generation={scope.RoutePublication.PublicationGeneration}; route_commit_generation={scope.RoutePublication.RouteCommitGeneration ?? 0}; terminal_generation={scope.TerminalGeneration}; leg_generation={scope.LegGeneration}; transport_epoch={scope.TransportEpoch}; bridge_recovery_generation={scope.BridgeRecoveryGeneration}; data_session_identity={FormatProtocolLogValue(scope.DataSessionIdentity)}; data_session_generation={scope.DataSessionGeneration}; status={decision.Status.ToString().ToLowerInvariant()}; outcome={(decision.Status == FileTransferReceiveRecoveryTransitionStatus.Idempotent ? "coalesced" : decision.Accepted ? "held" : "rejected")}; decision_reason={FormatProtocolLogValue(decision.Reason)}");
        return decision.Accepted;
    }

    private void
        DispatchReceiveRecoveryFallbackCheckpointFrontierRequestRecheckLocked(
            OutboundTransferContext context,
            FileTransferReceiveRecoveryFallbackCheckpointFrontierRequestRecheckCommand
                command)
    {
        var hold = command.Hold;
        var observation = hold.Observation;
        var binding = command.AcceptedBinding;
        var scope = binding.OriginalObservation;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_receive_recovery_fallback_checkpoint_frontier_request_recheck_issued; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; direction=outbound; recovery_operation_authority_id={FormatProtocolLogValue(binding.ParentAuthority.AuthorityId.ToString())}; recovery_operation_generation={binding.ParentAuthority.OperationGeneration}; root_checkpoint_request_id={FormatProtocolLogValue(binding.ParentCheckpointRequestId ?? "(none)")}; root_checkpoint_generation={binding.ParentCheckpointGeneration}; effective_checkpoint_request_id={FormatProtocolLogValue(binding.EffectiveCheckpointRequestId)}; effective_checkpoint_generation={binding.EffectiveCheckpointGeneration}; checkpoint_binding_generation={binding.BindingGeneration}; checkpoint_binding_state={binding.State.ToString().ToLowerInvariant()}; checkpoint_frame_fingerprint={FormatProtocolLogValue(binding.FrameIdentity.Sha256)}; repair_request_id={FormatProtocolLogValue(observation.Frame.RepairRequestId ?? "(none)")}; request_correlation_id={FormatProtocolLogValue(observation.Frame.RequestCorrelationId ?? "(none)")}; range_fingerprint={FormatProtocolLogValue(observation.RangeFingerprint)}; frontier_frame_type={FormatProtocolLogValue(observation.FrameIdentity.FrameType)}; frontier_frame_transport_epoch={observation.FrameIdentity.TransportEpoch}; frontier_frame_fingerprint={FormatProtocolLogValue(observation.FrameIdentity.Sha256)}; hold_generation={hold.HoldGeneration}; recheck_generation={command.RecheckGeneration}; dispatch_path={FormatProtocolLogValue(observation.DispatchPath.ToString())}; received_transport={FormatFileTransferTransportKind(observation.ReceivedTransportKind)}; receiving_data_session_identity={FormatProtocolLogValue(observation.ReceivingDataSessionIdentity)}; receiving_data_session_generation={observation.ReceivingDataSessionGeneration}; route={FormatProtocolLogValue(scope.RouteToken)}; protocol_version={scope.ProtocolVersion}; live_route_epoch={scope.LiveRouteEpoch}; runtime_generation={scope.RuntimeOwner.RuntimeGeneration}; route_publication_generation={scope.RoutePublication.PublicationGeneration}; route_commit_generation={scope.RoutePublication.RouteCommitGeneration ?? 0}; terminal_generation={scope.TerminalGeneration}; leg_generation={scope.LegGeneration}; transport_epoch={scope.TransportEpoch}; bridge_recovery_generation={scope.BridgeRecoveryGeneration}; data_session_identity={FormatProtocolLogValue(scope.DataSessionIdentity)}; data_session_generation={scope.DataSessionGeneration}; status=issued; decision_reason=filetransfer_receive_recovery_fallback_checkpoint_frontier_request_recheck_issued");

        RetainedPeerProofApplicationResult application;
        if (observation.DispatchPath ==
            FileTransferReceiveRecoveryFallbackCheckpointFrontierRequestDispatchPath
                .NativeV6)
        {
            application = ApplyOutboundAuthorizedFallbackFrontierRequest(
                context,
                observation.Frame,
                observation.ReceivedTransportKind);
        }
        else
        {
            application = ApplyOutboundV6RepairRequestCore(
                context,
                observation.Frame,
                observation.ReceivedTransportKind);
        }

        var receiptStatus = application.Status switch
        {
            RetainedPeerProofApplicationStatus.Applied =>
                FileTransferReceiveRecoveryFallbackCheckpointFrontierRequestRecheckStatus
                    .Applied,
            RetainedPeerProofApplicationStatus.Idempotent =>
                FileTransferReceiveRecoveryFallbackCheckpointFrontierRequestRecheckStatus
                    .Idempotent,
            _ =>
                FileTransferReceiveRecoveryFallbackCheckpointFrontierRequestRecheckStatus
                    .Rejected,
        };
        var receiptReason = application.Reason;

        var receipt =
            new FileTransferReceiveRecoveryFallbackCheckpointFrontierRequestRecheckReceipt(
                command,
                receiptStatus,
                receiptReason,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        var receiptDecision = FileTransferCoordinator
            .ApplyReceiveRecoveryFallbackCheckpointFrontierRequestRecheckReceipt(
                CreateCoordinatorStateLocked(context),
                receipt);
        _ = AdoptCoordinatorStateLocked(
            context,
            receiptDecision.State,
            "ReceiveRecoveryFallbackCheckpointFrontierRequestReceiptApplied",
            receiptDecision.Status.ToString(),
            receiptDecision.Reason);
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_receive_recovery_fallback_checkpoint_frontier_request_recheck_applied; session_id={FormatProtocolLogValue(context.SessionId)}; transfer_id={FormatProtocolLogValue(context.TransferId)}; direction=outbound; recovery_operation_authority_id={FormatProtocolLogValue(binding.ParentAuthority.AuthorityId.ToString())}; recovery_operation_generation={binding.ParentAuthority.OperationGeneration}; root_checkpoint_request_id={FormatProtocolLogValue(binding.ParentCheckpointRequestId ?? "(none)")}; root_checkpoint_generation={binding.ParentCheckpointGeneration}; effective_checkpoint_request_id={FormatProtocolLogValue(binding.EffectiveCheckpointRequestId)}; effective_checkpoint_generation={binding.EffectiveCheckpointGeneration}; checkpoint_binding_generation={binding.BindingGeneration}; checkpoint_binding_state={binding.State.ToString().ToLowerInvariant()}; checkpoint_frame_fingerprint={FormatProtocolLogValue(binding.FrameIdentity.Sha256)}; repair_request_id={FormatProtocolLogValue(observation.Frame.RepairRequestId ?? "(none)")}; request_correlation_id={FormatProtocolLogValue(observation.Frame.RequestCorrelationId ?? "(none)")}; range_fingerprint={FormatProtocolLogValue(observation.RangeFingerprint)}; frontier_frame_type={FormatProtocolLogValue(observation.FrameIdentity.FrameType)}; frontier_frame_transport_epoch={observation.FrameIdentity.TransportEpoch}; frontier_frame_fingerprint={FormatProtocolLogValue(observation.FrameIdentity.Sha256)}; hold_generation={hold.HoldGeneration}; recheck_generation={command.RecheckGeneration}; dispatch_path={FormatProtocolLogValue(observation.DispatchPath.ToString())}; received_transport={FormatFileTransferTransportKind(observation.ReceivedTransportKind)}; receiving_data_session_identity={FormatProtocolLogValue(observation.ReceivingDataSessionIdentity)}; receiving_data_session_generation={observation.ReceivingDataSessionGeneration}; route={FormatProtocolLogValue(scope.RouteToken)}; protocol_version={scope.ProtocolVersion}; live_route_epoch={scope.LiveRouteEpoch}; runtime_generation={scope.RuntimeOwner.RuntimeGeneration}; route_publication_generation={scope.RoutePublication.PublicationGeneration}; route_commit_generation={scope.RoutePublication.RouteCommitGeneration ?? 0}; terminal_generation={scope.TerminalGeneration}; leg_generation={scope.LegGeneration}; transport_epoch={scope.TransportEpoch}; bridge_recovery_generation={scope.BridgeRecoveryGeneration}; data_session_identity={FormatProtocolLogValue(scope.DataSessionIdentity)}; data_session_generation={scope.DataSessionGeneration}; receipt_status={receiptStatus.ToString().ToLowerInvariant()}; decision_status={receiptDecision.Status.ToString().ToLowerInvariant()}; outcome={(receiptDecision.Accepted ? receiptStatus.ToString().ToLowerInvariant() : "rejected")}; decision_reason={FormatProtocolLogValue(receiptDecision.Reason)}");
    }

    private RetainedPeerProofApplicationResult
        ApplyOutboundAuthorizedFallbackFrontierRequest(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        FileTransferTransportKind receivedTransportKind)
    {
        SessionFileTransferSnapshot? snapshot = null;
        RetainedPeerProofApplicationResult result;
        lock (gate)
        {
            var firstRaw = request.MissingRanges.Count > 0 ? request.MissingRanges[0] : null;
            LogOutboundV6FrontierRequestReceiveDispatch(
                context,
                request,
                receivedTransportKind,
                "attempted",
                "authority_receive",
                firstRaw?.StartChunkIndex ?? -1,
                firstRaw?.ChunkCount ?? 0,
                request.MissingRanges.Count);
            if (!ReferenceEquals(outboundTransfer, context) ||
                context.IsTerminal ||
                !IsStrictFallbackFrontierRuntime(context))
            {
                LogAuthorizedFallbackFrontierReceiveRejected(
                    context,
                    request,
                    receivedTransportKind,
                    context.IsTerminal ? "transfer_terminal" : "stale_or_non_fallback_context");
                return new RetainedPeerProofApplicationResult(
                    RetainedPeerProofApplicationStatus.Rejected,
                    context.IsTerminal
                        ? "transfer_terminal"
                        : "stale_or_non_fallback_context");
            }

            if (request.MissingRanges.Count == 0 ||
                string.IsNullOrWhiteSpace(request.RepairRequestId) ||
                string.IsNullOrWhiteSpace(request.RequestCorrelationId))
            {
                var reason = request.MissingRanges.Count == 0
                    ? "empty_v6_frontier_request"
                    : string.IsNullOrWhiteSpace(request.RepairRequestId)
                        ? "repair_request_id_missing"
                        : "request_correlation_missing";
                LogAuthorizedFallbackFrontierReceiveRejected(
                    context,
                    request,
                    receivedTransportKind,
                    reason);
                return new RetainedPeerProofApplicationResult(
                    RetainedPeerProofApplicationStatus.Rejected,
                    reason);
            }

            var leg = context.CurrentTransferLeg!;
            if (request.TransportEpoch <= 0 || request.TransportEpoch != leg.TransportEpochId)
            {
                LogAuthorizedFallbackFrontierReceiveRejected(
                    context,
                    request,
                    receivedTransportKind,
                    request.TransportEpoch <= 0
                        ? "missing_transport_epoch"
                        : "frontier_transport_epoch_mismatch");
                return new RetainedPeerProofApplicationResult(
                    RetainedPeerProofApplicationStatus.Rejected,
                    request.TransportEpoch <= 0
                        ? "missing_transport_epoch"
                        : "frontier_transport_epoch_mismatch");
            }

            var durableHighest = ResolveFallbackFrontierDurableHighest(request.MissingRanges);
            var identity = CreateFallbackFrontierRepairIdentity(
                request.MissingRanges,
                context.ChunkCount,
                durableHighest);
            if (identity is null)
            {
                LogAuthorizedFallbackFrontierReceiveRejected(
                    context,
                    request,
                    receivedTransportKind,
                    "empty_normalized_repair_window");
                return new RetainedPeerProofApplicationResult(
                    RetainedPeerProofApplicationStatus.Rejected,
                    "empty_normalized_repair_window");
            }

            var first = identity.Ranges[0];
            var isBackfillRequest = string.Equals(request.Priority, "backfill", StringComparison.OrdinalIgnoreCase);
            var regularNknEpochRecoveredBeforeRequest =
                IsRecoveredOutboundV6RegularNknFrontierRepairEpoch(
                    context,
                    request.TransportEpoch);
            if (!isBackfillRequest &&
                ShouldIgnoreOutboundV6FrontierRequestLocked(
                    context,
                    request,
                    identity.Ranges,
                    out var ignoreReason))
            {
                LogAuthorizedFallbackFrontierReceiveRejected(
                    context,
                    request,
                    receivedTransportKind,
                    ignoreReason);
                return new RetainedPeerProofApplicationResult(
                    RetainedPeerProofApplicationStatus.Rejected,
                    ignoreReason);
            }

            if (IsCurrentPostTunaFallbackLegCheckpointPending(leg))
            {
                var receivingDataSessionIdentity =
                    CreateReceiveRecoveryDataSessionIdentity(
                        context.TransferId,
                        context.DataSession,
                        context.DataSessionGeneration);
                var held =
                    TryHoldReceiveRecoveryFallbackCheckpointFrontierRequestLocked(
                        context,
                        request,
                        receivedTransportKind,
                        identity,
                        FileTransferReceiveRecoveryFallbackCheckpointFrontierRequestDispatchPath
                            .NativeV6,
                        receivingDataSessionIdentity,
                        context.DataSessionGeneration,
                        out var holdDecision);
                if (held)
                {
                    LogOutboundV6FrontierRequestReceiveDispatch(
                        context,
                        request,
                        receivedTransportKind,
                        holdDecision.Status ==
                            FileTransferReceiveRecoveryTransitionStatus.Idempotent
                            ? "coalesced"
                            : "staged",
                        holdDecision.Status ==
                            FileTransferReceiveRecoveryTransitionStatus.Idempotent
                            ? "hold_idempotent"
                            : "checkpoint_pending_frontier_request_retained",
                        first.StartChunkIndex,
                        first.ChunkCount,
                        identity.RangeCount);
                    return new RetainedPeerProofApplicationResult(
                        holdDecision.Status ==
                            FileTransferReceiveRecoveryTransitionStatus.Idempotent
                            ? RetainedPeerProofApplicationStatus.Idempotent
                            : RetainedPeerProofApplicationStatus.Held,
                        holdDecision.Reason);
                }

                LogAuthorizedFallbackFrontierReceiveRejected(
                    context,
                    request,
                    receivedTransportKind,
                    "checkpoint_pending_frontier_request");
                return new RetainedPeerProofApplicationResult(
                    RetainedPeerProofApplicationStatus.Rejected,
                    holdDecision.Reason);
            }

            var currentContract = context.CurrentFallbackFrontierRepairContract;
            var issuedNewContract = false;
            var activeAliasCoalesced = false;
            var existingCorrelationAlias = currentContract is null
                ? null
                : FindFallbackFrontierRepairCorrelationAlias(
                    currentContract,
                    request.RequestCorrelationId);
            if (currentContract is not null && existingCorrelationAlias is not null)
            {
                if (!IsSameFallbackFrontierRepairIdentity(currentContract, identity) ||
                    !string.Equals(
                        existingCorrelationAlias.RangeFingerprint,
                        identity.RangeFingerprint,
                        StringComparison.Ordinal))
                {
                    LogAuthorizedFallbackFrontierReceiveRejected(
                        context,
                        request,
                        receivedTransportKind,
                        "request_correlation_identity_mismatch");
                    return new RetainedPeerProofApplicationResult(
                        RetainedPeerProofApplicationStatus.Rejected,
                        "request_correlation_identity_mismatch");
                }

                var duplicateReason = !IsFallbackFrontierRepairContractCurrent(
                        context,
                        currentContract,
                        out _)
                    ? "duplicate_stale_authorized_request_alias"
                    : currentContract.Lifecycle.IsSettled
                        ? "duplicate_settled_authorized_request_alias"
                        : "duplicate_authorized_request_alias";
                LogAuthorizedFallbackFrontierReceiveCoalesced(
                    context,
                    request,
                    currentContract,
                    receivedTransportKind,
                    duplicateReason);
                return new RetainedPeerProofApplicationResult(
                    RetainedPeerProofApplicationStatus.Idempotent,
                    duplicateReason);
            }

            if (currentContract is not null &&
                !IsFallbackFrontierRepairContractCurrent(context, currentContract, out var currentRejectionReason))
            {
                TrySettleFallbackFrontierRepairContract(
                    currentContract,
                    FallbackFrontierRepairSettlementOutcome.StaleRejected,
                    currentRejectionReason);
                currentContract = null;
                context.CurrentFallbackFrontierRepairContract = null;
            }

            if (currentContract is not null &&
                !currentContract.Lifecycle.IsSettled &&
                IsSameFallbackFrontierRepairIdentity(currentContract, identity))
            {
                if (!TryAddFallbackFrontierRepairAlias(
                        currentContract,
                        request.RepairRequestId!,
                        request.RequestCorrelationId!,
                        identity.RangeFingerprint,
                        out _))
                {
                    LogAuthorizedFallbackFrontierReceiveCoalesced(
                        context,
                        request,
                        currentContract,
                        receivedTransportKind,
                        "alias_limit_reached");
                    return new RetainedPeerProofApplicationResult(
                        RetainedPeerProofApplicationStatus.Idempotent,
                        "alias_limit_reached");
                }

                activeAliasCoalesced = true;
            }
            else
            {
                var predecessorContract = currentContract;
                var predecessorId = predecessorContract?.Authority.AuthorityId;
                if (!TryIssueFallbackFrontierRepairContractLocked(
                        context,
                        identity,
                        FallbackFrontierRepairSourceKind.PeerRequest,
                        request.RepairRequestId!,
                        request.RequestCorrelationId,
                        request.Priority ?? "frontier",
                        request.RecoveryMode ?? "(none)",
                        "fallback_frontier_request_received",
                        predecessorId,
                        out var issuedContract,
                        out var rejectionReason) ||
                    issuedContract is null)
                {
                    LogAuthorizedFallbackFrontierReceiveRejected(
                        context,
                        request,
                        receivedTransportKind,
                        rejectionReason);
                    return new RetainedPeerProofApplicationResult(
                        RetainedPeerProofApplicationStatus.Rejected,
                        rejectionReason);
                }

                if (predecessorContract is not null &&
                    !predecessorContract.Lifecycle.IsSettled)
                {
                    TrySettleFallbackFrontierRepairContract(
                        predecessorContract,
                        FallbackFrontierRepairSettlementOutcome.Superseded,
                        "fallback_frontier_successor_authorized",
                        issuedContract.Authority.AuthorityId);
                }

                currentContract = issuedContract;
                context.CurrentFallbackFrontierRepairContract = issuedContract;
                CarryFallbackTailYieldRetryDispatchHoldToRepairSuccessorLocked(
                    context,
                    predecessorContract,
                    issuedContract);
                issuedNewContract = true;
            }

            var contract = currentContract!;
            if (!isBackfillRequest &&
                TryRecoverOutboundV6RegularNknEpochFromPeerControlLocked(
                    context,
                    request.TransportEpoch,
                    receivedTransportKind,
                    "frontier_request_control_proof",
                    contract))
            {
                if (!TryIssueFallbackFrontierRepairContractLocked(
                        context,
                        identity,
                        FallbackFrontierRepairSourceKind.PeerRequest,
                        request.RepairRequestId!,
                        request.RequestCorrelationId,
                        request.Priority ?? "frontier",
                        request.RecoveryMode ?? "(none)",
                        "fallback_frontier_transport_epoch_handoff",
                        contract.Authority.AuthorityId,
                        out var handoffContract,
                        out var handoffRejectionReason) ||
                    handoffContract is null)
                {
                    TrySettleFallbackFrontierRepairContract(
                        contract,
                        FallbackFrontierRepairSettlementOutcome.StaleRejected,
                        handoffRejectionReason);
                    if (ReferenceEquals(context.CurrentFallbackFrontierRepairContract, contract))
                    {
                        context.CurrentFallbackFrontierRepairContract = null;
                    }

                    LogAuthorizedFallbackFrontierReceiveRejected(
                        context,
                        request,
                        receivedTransportKind,
                        handoffRejectionReason);
                    return new RetainedPeerProofApplicationResult(
                        RetainedPeerProofApplicationStatus.Rejected,
                        handoffRejectionReason);
                }

                var handoffPredecessor = contract;
                TrySettleFallbackFrontierRepairContract(
                    handoffPredecessor,
                    FallbackFrontierRepairSettlementOutcome.Superseded,
                    "fallback_frontier_transport_epoch_handoff",
                    handoffContract.Authority.AuthorityId);
                context.CurrentFallbackFrontierRepairContract = handoffContract;
                CarryFallbackTailYieldRetryDispatchHoldToRepairSuccessorLocked(
                    context,
                    handoffPredecessor,
                    handoffContract);
                contract = handoffContract;
                issuedNewContract = true;
                activeAliasCoalesced = false;
            }

            context.V6LastReceiverFeedbackReceivedUtc = DateTimeOffset.UtcNow;
            if (!isBackfillRequest)
            {
                PreemptOutboundV6NormalPipelineForFrontierRequestLocked(
                    context,
                    request,
                    identity.Ranges);
            }

            if (!isBackfillRequest && first.StartChunkIndex > context.RemoteNextExpectedChunkIndex)
            {
                var previousRemoteFrontier = context.RemoteNextExpectedChunkIndex;
                context.RemoteNextExpectedChunkIndex = Math.Clamp(first.StartChunkIndex, 0, context.ChunkCount);
                context.RemoteGrantedUntilExclusive = Math.Max(
                    context.RemoteGrantedUntilExclusive,
                    context.RemoteNextExpectedChunkIndex);
                context.ChunksTransferred = Math.Max(
                    context.ChunksTransferred,
                    context.RemoteNextExpectedChunkIndex);
                context.BytesTransferred = Math.Max(
                    context.BytesTransferred,
                    context.RemoteNextExpectedChunkIndex >= context.ChunkCount
                        ? context.FileSizeBytes
                        : Math.Min(
                            context.FileSizeBytes,
                            (long)context.RemoteNextExpectedChunkIndex * context.ChunkSizeBytes));
                context.BytesAcknowledgedByReceiver = Math.Max(
                    context.BytesAcknowledgedByReceiver,
                    context.BytesTransferred);
                foreach (var chunkIndex in context.SentAwaitingAck.Keys
                             .Where(chunkIndex => chunkIndex < context.RemoteNextExpectedChunkIndex)
                             .ToArray())
                {
                    context.SentAwaitingAck.Remove(chunkIndex);
                }

                foreach (var chunkIndex in context.V6ChunkSendsInFlight.Keys
                             .Where(chunkIndex => chunkIndex < context.RemoteNextExpectedChunkIndex)
                             .ToArray())
                {
                    context.V6ChunkSendsInFlight.Remove(chunkIndex);
                }

                PruneOutboundV6RequestedChunksBeforeLocked(context, context.RemoteNextExpectedChunkIndex);
                TrimSenderRepairCacheLocked(context, context.RemoteNextExpectedChunkIndex);
                MaybeReleaseOutboundV6PostTunaFallbackNormalSendAheadFreezeLocked(
                    context,
                    previousRemoteFrontier,
                    "frontier_request_progress");
                var authority = contract.Authority;
                LocalOperationalLog.Info(
                    "FileTransferService",
                    $"event=filetransfer_v6_frontier_request_advanced_remote_frontier; {FormatFallbackFrontierRepairAuthorityTuple(authority)}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId)}; request_correlation_id={FormatProtocolLogValue(request.RequestCorrelationId)}; previous_remote_frontier_chunk_index={previousRemoteFrontier}; inferred_remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}");
            }

            var forceRegularNknBulk =
                contract.Authority.TargetTransport == FileTransferTransportKind.RegularNkn &&
                (IsV6RegularNknFrontierControlBulkEscalationMode(contract.Authority.RecoveryMode) ||
                 (!regularNknEpochRecoveredBeforeRequest &&
                  ShouldForceOutboundV6PeerRequestedPriorityOverRegularNkn(
                      context,
                      contract.Authority.TransportEpoch,
                      contract.Authority.RecoveryMode,
                      receivedTransportKind)));
            var queuedChunkCount = QueueOutboundV6RequestedRangesLocked(
                context,
                identity.Ranges,
                new V6OutboundChunkRequestMetadata(
                    RequestKey: contract.Authority.CanonicalRepairKey,
                    Priority: true,
                    TransportEpoch: contract.Authority.TransportEpoch,
                    RepairRequestId: request.RepairRequestId,
                    PriorityName: request.Priority ?? "frontier",
                    RecoveryMode: request.RecoveryMode,
                    ForceRegularNknBulk: forceRegularNknBulk,
                    FallbackFrontierRepairContract: contract),
                obsoleteBeforeChunkIndex: context.RemoteNextExpectedChunkIndex);
            if (queuedChunkCount == 0 && issuedNewContract)
            {
                TrySettleFallbackFrontierRepairContract(
                    contract,
                    FallbackFrontierRepairSettlementOutcome.MissingDispatch,
                    "native_v6_request_had_no_schedulable_chunks");
            }
            result = queuedChunkCount > 0
                ? new RetainedPeerProofApplicationResult(
                    RetainedPeerProofApplicationStatus.Applied,
                    "frontier_request_dispatched")
                : issuedNewContract
                    ? new RetainedPeerProofApplicationResult(
                        RetainedPeerProofApplicationStatus.Rejected,
                        "native_v6_request_had_no_schedulable_chunks")
                    : new RetainedPeerProofApplicationResult(
                        RetainedPeerProofApplicationStatus.Idempotent,
                        "frontier_request_already_scheduled");
            context.V6SenderPumpLastWakeReason = "frontier_request";
            snapshot = CreateSnapshotLocked();
            if (activeAliasCoalesced)
            {
                LogAuthorizedFallbackFrontierReceiveCoalesced(
                    context,
                    request,
                    contract,
                    receivedTransportKind,
                    "same_authorized_repair_window");
            }
            LogAuthorizedFallbackFrontierReceiveQueued(
                context,
                request,
                contract,
                receivedTransportKind);
        }

        TouchOutboundV6PeerLiveness(context, "authorized_fallback_frontier_request");
        if (snapshot is not null)
        {
            RaiseTransferChanged(snapshot);
            MaybeLogProgressMilestone(context, FileTransferDirection.Outbound);
        }

        return result;
    }

    private static void LogAuthorizedFallbackFrontierReceiveRejected(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        FileTransferTransportKind receivedTransportKind,
        string reason)
    {
        var first = request.MissingRanges.Count > 0 ? request.MissingRanges[0] : null;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_frontier_request_receive_dispatch_rejected; authority_schema={FileTransferAuthoritySchema.Current}; authority_id=(none); direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; route={context.RouteSelection.TelemetryToken}; protocol_version={context.NegotiatedDataProtocolVersion}; live_route_epoch={context.CurrentLiveRouteEpoch?.EpochId ?? 0}; runtime_generation={context.CurrentRuntimeOwner?.RuntimeGeneration ?? 0}; terminal_generation={context.CurrentRuntimeOwner?.TerminalGeneration ?? 0}; leg_generation={context.CurrentTransferLeg?.Generation ?? 0}; transport_epoch={request.TransportEpoch}; current_transport_epoch={context.CurrentTransferLeg?.TransportEpochId ?? 0}; bridge_recovery_generation={context.CurrentTransferLeg?.BridgeRecoveryGeneration ?? 0}; checkpoint_request_id={FormatProtocolLogValue(context.CurrentTransferLeg?.CheckpointRequestId ?? "(none)")}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId ?? "(none)")}; request_correlation_id={FormatProtocolLogValue(request.RequestCorrelationId ?? "(none)")}; first_start_chunk_index={first?.StartChunkIndex ?? -1}; first_chunk_count={first?.ChunkCount ?? 0}; range_count={request.MissingRanges.Count}; received_transport={FormatFileTransferTransportKind(receivedTransportKind)}; decision_reason={FormatProtocolLogValue(reason)}");
    }

    private static void LogAuthorizedFallbackFrontierReceiveCoalesced(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        FallbackFrontierRepairContract contract,
        FileTransferTransportKind receivedTransportKind,
        string reason)
    {
        var authority = contract.Authority;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_frontier_request_receive_dispatch_coalesced; {FormatFallbackFrontierRepairAuthorityTuple(authority)}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId ?? "(none)")}; request_correlation_id={FormatProtocolLogValue(request.RequestCorrelationId ?? "(none)")}; alias_count={contract.Aliases.Count}; received_transport={FormatFileTransferTransportKind(receivedTransportKind)}; decision_reason={FormatProtocolLogValue(reason)}");
    }

    private static void LogAuthorizedFallbackFrontierReceiveQueued(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        FallbackFrontierRepairContract contract,
        FileTransferTransportKind receivedTransportKind)
    {
        var authority = contract.Authority;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_v6_frontier_request_receive_dispatched; {FormatFallbackFrontierRepairAuthorityTuple(authority)}; repair_request_id={FormatProtocolLogValue(request.RepairRequestId)}; request_correlation_id={FormatProtocolLogValue(request.RequestCorrelationId)}; first_start_chunk_index={authority.FrontierChunkIndex}; first_chunk_count={contract.Identity.Ranges[0].ChunkCount}; received_transport={FormatFileTransferTransportKind(receivedTransportKind)}; decision_reason=queued_for_authorized_sender_repair");
    }

    private static bool TryValidateOutboundFallbackFrontierDispatchLocked(
        OutboundTransferContext context,
        FallbackFrontierRepairContract contract,
        out string rejectionReason)
    {
        rejectionReason = "accepted";
        if (contract.Lifecycle.IsSettled)
        {
            rejectionReason = "repair_contract_settled";
            return false;
        }

        if (!ReferenceEquals(context.CurrentFallbackFrontierRepairContract, contract))
        {
            rejectionReason = "repair_contract_replaced";
            return false;
        }

        return IsFallbackFrontierRepairContractCurrent(context, contract, out rejectionReason);
    }

    private void TrySettleOutboundFallbackFrontierProgressLocked(
        OutboundTransferContext context,
        int previousFrontierChunkIndex,
        int currentFrontierChunkIndex,
        string reason)
    {
        var contract = context.CurrentFallbackFrontierRepairContract;
        if (contract is null ||
            contract.Lifecycle.IsSettled ||
            currentFrontierChunkIndex <= previousFrontierChunkIndex ||
            currentFrontierChunkIndex <= contract.Authority.FrontierChunkIndex)
        {
            return;
        }

        if (!IsFallbackFrontierRepairContractCurrent(context, contract, out var rejectionReason))
        {
            TrySettleFallbackFrontierRepairContract(
                contract,
                FallbackFrontierRepairSettlementOutcome.StaleRejected,
                rejectionReason);
            return;
        }

        if (!TrySettleFallbackFrontierRepairContract(
                contract,
                FallbackFrontierRepairSettlementOutcome.ProgressObserved,
                reason))
        {
            return;
        }

        context.LastSettledFallbackFrontierRepairAuthority = contract.Authority;
        context.LastSettledFallbackFrontierRepairOutcome =
            nameof(FallbackFrontierRepairSettlementOutcome.ProgressObserved);
        context.LastSettledFallbackFrontierRepairUtcMs =
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (context.CoordinatorPersistentState.FallbackTailYield
                .CurrentRetryDispatchHold is { } hold)
        {
            TryQueueFallbackTailYieldRetryDispatchUnblockRecheckLocked(
                context,
                hold.Release);
        }

        var authority = contract.Authority;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_frontier_repair_progress_observed; {FormatFallbackFrontierRepairAuthorityTuple(authority)}; request_correlation_id={FormatProtocolLogValue(authority.RequestCorrelationId)}; reason={FormatProtocolLogValue(reason)}; previous_frontier_chunk_index={previousFrontierChunkIndex}; current_frontier_chunk_index={currentFrontierChunkIndex}");
    }

    private bool TryAcceptOutboundFallbackFrontierRepairLocked(
        OutboundTransferContext context,
        FileTransferFrontierRequestFrameV6 request,
        FileTransferTransportKind receivedTransportKind,
        string receivingDataSessionIdentity,
        long receivingDataSessionGeneration,
        out FallbackFrontierRepairContract? contract,
        out FallbackFrontierRepairIdentity? identity,
        out FallbackFrontierRepairAcceptance acceptance,
        out string rejectionReason)
    {
        contract = null;
        identity = null;
        acceptance = FallbackFrontierRepairAcceptance.Rejected;
        rejectionReason = "stale_or_non_fallback_context";
        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            !IsStrictFallbackFrontierRuntime(context))
        {
            return false;
        }

        if (request.MissingRanges.Count == 0 ||
            string.IsNullOrWhiteSpace(request.RepairRequestId) ||
            string.IsNullOrWhiteSpace(request.RequestCorrelationId))
        {
            rejectionReason = request.MissingRanges.Count == 0
                ? "empty_v6_frontier_request"
                : string.IsNullOrWhiteSpace(request.RepairRequestId)
                    ? "repair_request_id_missing"
                    : "request_correlation_missing";
            return false;
        }

        var leg = context.CurrentTransferLeg!;
        if (request.TransportEpoch <= 0 || request.TransportEpoch != leg.TransportEpochId)
        {
            rejectionReason = request.TransportEpoch <= 0
                ? "missing_transport_epoch"
                : "frontier_transport_epoch_mismatch";
            return false;
        }

        var durableHighest = ResolveFallbackFrontierDurableHighest(request.MissingRanges);
        identity = CreateFallbackFrontierRepairIdentity(
            request.MissingRanges,
            context.ChunkCount,
            durableHighest);
        if (identity is null)
        {
            rejectionReason = "empty_normalized_repair_window";
            return false;
        }

        var isBackfillRequest = string.Equals(request.Priority, "backfill", StringComparison.OrdinalIgnoreCase);
        if (!isBackfillRequest &&
            ShouldIgnoreOutboundV6FrontierRequestLocked(
                context,
                request,
                identity.Ranges,
                out rejectionReason))
        {
            return false;
        }

        if (IsCurrentPostTunaFallbackLegCheckpointPending(leg))
        {
            TryHoldReceiveRecoveryFallbackCheckpointFrontierRequestLocked(
                context,
                request,
                receivedTransportKind,
                identity,
                FileTransferReceiveRecoveryFallbackCheckpointFrontierRequestDispatchPath
                    .RetainedSparseV4,
                receivingDataSessionIdentity,
                receivingDataSessionGeneration,
                out var holdDecision);
            rejectionReason = holdDecision.Accepted
                ? holdDecision.Status ==
                    FileTransferReceiveRecoveryTransitionStatus.Idempotent
                    ? "hold_idempotent"
                    : "checkpoint_pending_frontier_request_retained"
                : "checkpoint_pending_frontier_request";
            return false;
        }

        var currentContract = context.CurrentFallbackFrontierRepairContract;
        var existingCorrelationAlias = currentContract is null
            ? null
            : FindFallbackFrontierRepairCorrelationAlias(
                currentContract,
                request.RequestCorrelationId);
        if (currentContract is not null && existingCorrelationAlias is not null)
        {
            contract = currentContract;
            if (!IsSameFallbackFrontierRepairIdentity(currentContract, identity) ||
                !string.Equals(
                    existingCorrelationAlias.RangeFingerprint,
                    identity.RangeFingerprint,
                    StringComparison.Ordinal))
            {
                rejectionReason = "request_correlation_identity_mismatch";
                return false;
            }

            acceptance = FallbackFrontierRepairAcceptance.ExactDuplicate;
            rejectionReason = !IsFallbackFrontierRepairContractCurrent(
                    context,
                    currentContract,
                    out _)
                ? "duplicate_stale_authorized_request_correlation"
                : currentContract.Lifecycle.IsSettled
                    ? "duplicate_settled_authorized_request_correlation"
                    : "duplicate_authorized_request_correlation";
            return true;
        }

        if (currentContract is not null &&
            !IsFallbackFrontierRepairContractCurrent(context, currentContract, out var currentRejectionReason))
        {
            if (!currentContract.Lifecycle.IsSettled)
            {
                TrySettleFallbackFrontierRepairContract(
                    currentContract,
                    FallbackFrontierRepairSettlementOutcome.StaleRejected,
                    currentRejectionReason);
            }

            currentContract = null;
            context.CurrentFallbackFrontierRepairContract = null;
        }

        if (currentContract is not null &&
            !currentContract.Lifecycle.IsSettled &&
            IsSameFallbackFrontierRepairIdentity(currentContract, identity))
        {
            if (!TryAddFallbackFrontierRepairAlias(
                    currentContract,
                    request.RepairRequestId!,
                    request.RequestCorrelationId!,
                    identity.RangeFingerprint,
                    out _))
            {
                contract = currentContract;
                acceptance = FallbackFrontierRepairAcceptance.ExactDuplicate;
                rejectionReason = "alias_limit_reached";
                return true;
            }

            contract = currentContract;
            acceptance = FallbackFrontierRepairAcceptance.ActiveAlias;
            rejectionReason = "active_window_alias_coalesced";
            return true;
        }

        var predecessorContract = currentContract;
        var predecessorId = predecessorContract?.Authority.AuthorityId;
        if (!TryIssueFallbackFrontierRepairContractLocked(
                context,
                identity,
                FallbackFrontierRepairSourceKind.PeerRequest,
                request.RepairRequestId!,
                request.RequestCorrelationId,
                request.Priority ?? "frontier",
                request.RecoveryMode ?? "(none)",
                "fallback_frontier_request_received",
                predecessorId,
                out var issuedContract,
                out rejectionReason) ||
            issuedContract is null)
        {
            return false;
        }

        if (predecessorContract is not null &&
            !predecessorContract.Lifecycle.IsSettled)
        {
            TrySettleFallbackFrontierRepairContract(
                predecessorContract,
                FallbackFrontierRepairSettlementOutcome.Superseded,
                "fallback_frontier_successor_authorized",
                issuedContract.Authority.AuthorityId);
        }

        context.CurrentFallbackFrontierRepairContract = issuedContract;
        CarryFallbackTailYieldRetryDispatchHoldToRepairSuccessorLocked(
            context,
            predecessorContract,
            issuedContract);
        contract = issuedContract;
        acceptance = FallbackFrontierRepairAcceptance.NewContract;
        rejectionReason = "accepted";
        return true;
    }

    private bool TryGetOutboundFallbackFrontierPeerRepairLocked(
        OutboundTransferContext context,
        out FallbackFrontierRepairContract? contract,
        out string rejectionReason)
    {
        contract = null;
        rejectionReason = "stale_or_non_fallback_context";
        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            !IsStrictFallbackFrontierRuntime(context) ||
            context.UserPaused || context.PeerPaused || context.PullTransportPaused)
        {
            return false;
        }

        var currentContract = context.CurrentFallbackFrontierRepairContract;
        if (currentContract?.PeerRequestBinding is null)
        {
            rejectionReason = "fallback_frontier_peer_request_missing";
        }
        else if (!TryValidateOutboundFallbackFrontierDispatchLocked(context, currentContract, out rejectionReason))
        {
            // A stale rescue observation must not settle or replace the current contract.
        }
        else if (currentContract.Authority.FrontierChunkIndex != context.RemoteNextExpectedChunkIndex)
        {
            rejectionReason = "fallback_frontier_peer_request_frontier_changed";
        }
        else if (currentContract.Aliases.Any(alias =>
                     !string.Equals(alias.ProtocolRepairRequestId,
                         currentContract.Authority.ProtocolRepairRequestId, StringComparison.Ordinal) ||
                     !string.Equals(alias.RequestCorrelationId,
                         currentContract.Authority.RequestCorrelationId, StringComparison.Ordinal) ||
                     !string.Equals(alias.RangeFingerprint,
                         currentContract.Authority.RangeFingerprint, StringComparison.Ordinal)))
        {
            // Same-window peer requests may replace the receiver's contract. The local
            // alias set does not prove the original primary is still admitted there.
            rejectionReason = "fallback_frontier_peer_request_alias_replaced";
        }
        else
        {
            contract = currentContract;
            rejectionReason = "exact_peer_request_retry";
        }

        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_frontier_sender_replay_admission_decided; authority_effect=none; direction=outbound; transfer_id={context.TransferId}; session_id={context.SessionId}; authority_id={currentContract?.Authority.AuthorityId.ToString() ?? "(none)"}; repair_request_id={FormatProtocolLogValue(currentContract?.Authority.ProtocolRepairRequestId ?? "(none)")}; decision_reason={rejectionReason}; remote_frontier_chunk_index={context.RemoteNextExpectedChunkIndex}; data_session_generation={context.DataSessionGeneration}");
        return contract is not null;
    }

    private static bool IsOutboundFallbackFrontierPeerRequestBindingCurrent(
        OutboundTransferContext context,
        FallbackFrontierRepairContract contract)
    {
        if (contract.PeerRequestBinding is not { } binding ||
            !ReferenceEquals(binding.DataSession, context.DataSession) ||
            binding.DataSessionGeneration != context.DataSessionGeneration ||
            binding.CheckpointGeneration != (context.CurrentTransferLeg?.CheckpointGeneration ?? 0) ||
            !string.Equals(binding.Authority.CheckpointRequestId,
                context.CurrentTransferLeg?.CheckpointRequestId ?? "(none)", StringComparison.Ordinal))
        {
            return false;
        }

        // Only local retry authority changes. The admitted wire identity, ranges, and
        // complete source scope must remain the original Coordinator-issued peer request.
        var admittedAuthority = contract.Authority with
        {
            AuthorityId = binding.Authority.AuthorityId,
            PredecessorAuthorityId = binding.Authority.PredecessorAuthorityId,
            DecisionGeneration = binding.Authority.DecisionGeneration,
            DecisionReason = binding.Authority.DecisionReason,
            SourceKind = binding.Authority.SourceKind,
        };
        return binding.Authority.SourceKind == FallbackFrontierRepairSourceKind.PeerRequest &&
               admittedAuthority == binding.Authority;
    }

    private static void LogFallbackFrontierRepairDispatched(
        FallbackFrontierRepairContract contract,
        string protocolRepairRequestId,
        string stage,
        int dispatchedChunkCount,
        int remoteFrontierChunkIndex,
        int acceptedUntilChunkIndex)
    {
        var authority = contract.Authority;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_frontier_repair_dispatched; {FormatCheckpointRepairContinuity(GetCheckpointRepairContinuityStamp(contract, protocolRepairRequestId, contract.Authority.RequestCorrelationId))}{FormatFallbackFrontierRepairAuthorityTuple(authority)}; protocol_repair_request_id={FormatProtocolLogValue(protocolRepairRequestId)}; request_correlation_id={FormatProtocolLogValue(authority.RequestCorrelationId)}; stage={FormatProtocolLogValue(stage)}; dispatched_chunk_count={dispatchedChunkCount}; remote_frontier_chunk_index={remoteFrontierChunkIndex}; chunks_accepted_for_transport={acceptedUntilChunkIndex}; dispatch_observed={(contract.Lifecycle.DispatchObserved ? 1 : 0)}; decision_reason=authorized_repair_send_completed");
    }

    private static void LogFallbackFrontierRepairRetryDispatched(
        FallbackFrontierRepairContract contract,
        string protocolRepairRequestId,
        string stage,
        int dispatchedChunkCount,
        int remoteFrontierChunkIndex,
        int acceptedUntilChunkIndex)
    {
        var authority = contract.Authority;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_frontier_repair_retry_dispatched; {FormatFallbackFrontierRepairAuthorityTuple(authority)}; protocol_repair_request_id={FormatProtocolLogValue(protocolRepairRequestId)}; request_correlation_id={FormatProtocolLogValue(authority.RequestCorrelationId)}; stage={FormatProtocolLogValue(stage)}; dispatched_chunk_count={dispatchedChunkCount}; remote_frontier_chunk_index={remoteFrontierChunkIndex}; chunks_accepted_for_transport={acceptedUntilChunkIndex}; dispatch_observed=1; decision_reason=authorized_repair_retry_send_completed");
    }

    private static void LogFallbackFrontierRepairStalled(
        FallbackFrontierRepairContract contract,
        string reason,
        int frontierChunkIndex,
        int durableReceivedHighestChunkIndex,
        int requestedChunkCount,
        int attemptCount,
        long frontierStallAgeMs)
    {
        var authority = contract.Authority;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_frontier_repair_stalled; {FormatFallbackFrontierRepairAuthorityTuple(authority)}; request_correlation_id={FormatProtocolLogValue(authority.RequestCorrelationId)}; reason={FormatProtocolLogValue(reason)}; observed_frontier_chunk_index={frontierChunkIndex}; observed_durable_received_highest_chunk_index={durableReceivedHighestChunkIndex}; observed_requested_chunk_count={requestedChunkCount}; attempt_count={attemptCount}; frontier_stall_age_ms={frontierStallAgeMs}; decision_reason=authorized_fallback_frontier_repair_stalled");
    }

    private bool TryIssueOutboundFallbackFrontierRetrySuccessorLocked(
        OutboundTransferContext context,
        FallbackFrontierRepairContract predecessor,
        FallbackFrontierRepairSourceKind sourceKind,
        string decisionReason,
        out FallbackFrontierRepairContract? successor)
    {
        successor = null;
        if (!ReferenceEquals(context.CurrentFallbackFrontierRepairContract, predecessor) ||
            !TryGetOutboundFallbackFrontierPeerRepairLocked(context, out var currentPeerContract, out _) ||
            !ReferenceEquals(currentPeerContract, predecessor))
        {
            return false;
        }

        if (!TryIssueFallbackFrontierRepairContractLocked(
                context,
                predecessor.Identity,
                sourceKind,
                predecessor.Authority.ProtocolRepairRequestId,
                predecessor.Authority.RequestCorrelationId,
                predecessor.Authority.Priority,
                predecessor.Authority.RecoveryMode,
                decisionReason,
                predecessor.Authority.AuthorityId,
                out successor,
                out var successorRejectionReason) ||
            successor is null)
        {
            TrySettleFallbackFrontierRepairContract(
                predecessor,
                predecessor.Lifecycle.DispatchObserved
                    ? FallbackFrontierRepairSettlementOutcome.SettledStalled
                    : FallbackFrontierRepairSettlementOutcome.MissingDispatch,
                successorRejectionReason);
            return false;
        }

        successor.PeerRequestBinding = predecessor.PeerRequestBinding;
        TrySettleFallbackFrontierRepairContract(
            predecessor,
            FallbackFrontierRepairSettlementOutcome.Superseded,
            decisionReason,
            successor.Authority.AuthorityId);
        context.CurrentFallbackFrontierRepairContract = successor;
        CarryFallbackTailYieldRetryDispatchHoldToRepairSuccessorLocked(
            context,
            predecessor,
            successor);
        return true;
    }

    private bool TryRequeueOutboundFallbackFrontierV4RetryLocked(
        OutboundTransferContext context,
        PullV4QueuedRepairSend predecessorSend,
        out FallbackFrontierRepairContract? successor,
        out int requeuedChunkCount)
    {
        successor = null;
        requeuedChunkCount = 0;
        if (predecessorSend.FallbackFrontierRepairContract is not { } predecessor ||
            !TryIssueOutboundFallbackFrontierRetrySuccessorLocked(
                context,
                predecessor,
                FallbackFrontierRepairSourceKind.SenderTimeoutRetry,
                "fallback_frontier_legacy_sparse_send_retry_successor",
                out successor) ||
            successor is null)
        {
            return false;
        }

        var remoteFrontier = Math.Clamp(context.RemoteNextExpectedChunkIndex, 0, context.ChunkCount);
        var requeued = predecessorSend.ChunkIndices
            .Where(chunkIndex => chunkIndex >= remoteFrontier && chunkIndex < context.ChunkCount)
            .Where(context.PullV4SenderPumpRepairQueuedChunkIndices.Add)
            .ToList();
        if (requeued.Count == 0)
        {
            TrySettleFallbackFrontierRepairContract(
                successor,
                FallbackFrontierRepairSettlementOutcome.MissingDispatch,
                "fallback_frontier_legacy_sparse_retry_had_no_uncommitted_chunks");
            return false;
        }

        if (!context.PullV4SenderPumpRepairRequests.TryGetValue(
                successor.Authority.CanonicalRepairKey,
                out var repairState))
        {
            repairState = new V4SenderRepairRequestState();
            context.PullV4SenderPumpRepairRequests[successor.Authority.CanonicalRepairKey] = repairState;
        }

        repairState.Queued = true;
        repairState.QueuedUtc = DateTimeOffset.UtcNow;
        repairState.InFlight = false;
        repairState.FallbackFrontierRepairContract = successor;
        context.PullV4SenderPumpRepairQueue.Enqueue(predecessorSend with
        {
            ChunkIndices = requeued,
            RepairRequestKey = successor.Authority.CanonicalRepairKey,
            ProtocolRepairRequestId = successor.Authority.ProtocolRepairRequestId,
            RuntimeOwner = context.CurrentRuntimeOwner,
            FallbackFrontierRepairContract = successor,
        });
        context.SparseSenderPumpLastWakeReason = "fallback_frontier_legacy_sparse_retry_successor";
        context.SignalSparseSenderPump();
        requeuedChunkCount = requeued.Count;
        return true;
    }
}
