using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private void OnFallbackV6ProofBootstrapEvaluationRequested(
        object? sender,
        FileTransferFallbackV6ProofBootstrapEvaluationRequestedEventArgs e)
    {
        if (sender is not IFileTransferRouteTransitionBridge bridge)
        {
            return;
        }

        _ = ProcessFallbackV6ProofBootstrapEvaluationAsync(
            bridge,
            e.Evaluation);
    }

    private async Task ProcessFallbackV6ProofBootstrapEvaluationAsync(
        IFileTransferRouteTransitionBridge bridge,
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation)
    {
        FileTransferFallbackV6ProofBootstrapCommand command;
        FileTransferTransportEpochFrameV6 epochFrame;
        FileTransferDataFrame proofFrame;
        IFileTransferDataSession dataSession;
        SessionFileTransferSnapshot? issuedSnapshot;

        lock (gate)
        {
            if (!TryIssueFallbackV6ProofBootstrapLocked(
                    evaluation,
                    out command,
                    out epochFrame,
                    out proofFrame,
                    out dataSession,
                    out issuedSnapshot))
            {
                return;
            }
        }

        if (issuedSnapshot is not null)
        {
            RaiseTransferChanged(issuedSnapshot);
        }

        FileTransferFallbackV6ProofBootstrapReceipt receipt;
        try
        {
            receipt = await bridge.ExecuteFileTransferFallbackV6ProofBootstrapAsync(
                    command,
                    epochFrame,
                    proofFrame,
                    dataSession,
                    () => IsFallbackV6ProofBootstrapCommandCurrent(command),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            receipt = new FileTransferFallbackV6ProofBootstrapReceipt(
                command,
                FileTransferFallbackV6ProofBootstrapReceiptStatus.Rejected,
                null,
                "filetransfer_fallback_v6_proof_bootstrap_bridge_failed");
        }

        SessionFileTransferSnapshot? receiptSnapshot = null;
        lock (gate)
        {
            if (TryApplyFallbackV6ProofBootstrapReceiptLocked(
                    command,
                    receipt,
                    out receiptSnapshot))
            {
                LogFallbackV6ProofBootstrapReceipt(command, receipt, "Applied");
            }
            else
            {
                LogFallbackV6ProofBootstrapReceipt(command, receipt, "Rejected");
            }
        }

        if (receiptSnapshot is not null)
        {
            RaiseTransferChanged(receiptSnapshot);
        }
    }

    private bool IsFallbackV6ProofBootstrapCommandCurrent(
        FileTransferFallbackV6ProofBootstrapCommand command)
    {
        lock (gate)
        {
            if (command.Observation.Direction == FileTransferDirection.Outbound &&
                outboundTransfer is { IsTerminal: false } outbound &&
                string.Equals(outbound.TransferId, command.Observation.TransferId, StringComparison.Ordinal))
            {
                return FileTransferCoordinator.IsFileTransferFallbackV6ProofBootstrapCommandCurrent(
                    CreateCoordinatorStateLocked(outbound),
                    command);
            }

            if (command.Observation.Direction == FileTransferDirection.Inbound &&
                inboundTransfer is { IsTerminal: false } inbound &&
                string.Equals(inbound.TransferId, command.Observation.TransferId, StringComparison.Ordinal))
            {
                return FileTransferCoordinator.IsFileTransferFallbackV6ProofBootstrapCommandCurrent(
                    CreateCoordinatorStateLocked(inbound),
                    command);
            }

            return false;
        }
    }

    private bool TryIssueFallbackV6ProofBootstrapLocked(
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation,
        out FileTransferFallbackV6ProofBootstrapCommand command,
        out FileTransferTransportEpochFrameV6 epochFrame,
        out FileTransferDataFrame proofFrame,
        out IFileTransferDataSession dataSession,
        out SessionFileTransferSnapshot? snapshot)
    {
        command = null!;
        epochFrame = null!;
        proofFrame = null!;
        dataSession = null!;
        snapshot = null;

        if (evaluation.Direction == FileTransferDirection.Outbound)
        {
            if (outboundTransfer is not { IsTerminal: false } context ||
                !string.Equals(context.SessionId, evaluation.SessionId, StringComparison.Ordinal) ||
                !context.RouteRuntime.UsesFileTunaV4Runtime ||
                context.DataSession is not { } currentDataSession ||
                context.CurrentRuntimeOwner is not { IsDefault: false } owner ||
                context.CurrentRoutePublicationToken is not { IsDefault: false } publication ||
                context.RuntimeUnlockRouteCommitToken is not { IsDefault: false } ||
                context.CurrentTransferLeg is not { } leg)
            {
                return false;
            }

            var checkpoint = CreateOutboundFileTransferRouteTransitionCheckpoint(
                context,
                owner,
                publication,
                leg);
            var observation = CreateFallbackV6ProofBootstrapObservation(
                evaluation,
                context.TransferId,
                checkpoint);
            var decision = FileTransferCoordinator.IssueFileTransferFallbackV6ProofBootstrap(
                CreateCoordinatorStateLocked(context),
                observation);
            if (decision.Status is not (
                    FileTransferFallbackV6ProofBootstrapDecisionStatus.Issued or
                    FileTransferFallbackV6ProofBootstrapDecisionStatus.Idempotent) ||
                decision.Command is not { } issued)
            {
                LogFallbackV6ProofBootstrapDecision(
                    evaluation,
                    context.TransferId,
                    decision.Command,
                    decision.Status.ToString(),
                    decision.Reason);
                return false;
            }

            context.CoordinatorPersistentState = decision.State.PersistentState;
            command = issued;
            dataSession = currentDataSession;
            epochFrame = CreateFallbackV6ProofBootstrapEpochFrame(command);
            proofFrame = new FileTransferFrontierRequestFrameV6
            {
                SessionId = context.SessionId,
                TransferId = context.TransferId,
                TransportEpoch = command.TargetTransportEpoch,
                RequestCorrelationId =
                    FileTransferProtocol.CreateFallbackV6ProofBootstrapCorrelationId(
                        command.BootstrapGeneration),
                MissingRanges = [],
                Priority = FileTransferProtocol.FallbackV6ProofBootstrapPriority,
                RecoveryMode =
                    FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
            };
            snapshot = CreateSnapshotLocked();
            LogFallbackV6ProofBootstrapDecision(
                evaluation,
                context.TransferId,
                command,
                decision.Status.ToString(),
                decision.Reason);
            return true;
        }

        if (inboundTransfer is not { IsTerminal: false } inbound ||
            !string.Equals(inbound.SessionId, evaluation.SessionId, StringComparison.Ordinal) ||
            !inbound.RouteRuntime.UsesFileTunaV4Runtime ||
            inbound.DataSession is not { } inboundDataSession ||
            inbound.CurrentRuntimeOwner is not { IsDefault: false } inboundOwner ||
            inbound.CurrentRoutePublicationToken is not { IsDefault: false } inboundPublication ||
            inbound.RuntimeUnlockRouteCommitToken is not { IsDefault: false } ||
            inbound.CurrentTransferLeg is not { } inboundLeg ||
            inbound.ReceiverSparseChunksPendingWrite.Count != 0 ||
            inbound.PendingChunks.Count != 0)
        {
            return false;
        }

        var inboundCheckpoint = CreateInboundFileTransferRouteTransitionCheckpoint(
            inbound,
            inboundOwner,
            inboundPublication,
            inboundLeg);
        var inboundObservation = CreateFallbackV6ProofBootstrapObservation(
            evaluation,
            inbound.TransferId,
            inboundCheckpoint);
        var inboundDecision = FileTransferCoordinator.IssueFileTransferFallbackV6ProofBootstrap(
            CreateCoordinatorStateLocked(inbound),
            inboundObservation);
        if (inboundDecision.Status is not (
                FileTransferFallbackV6ProofBootstrapDecisionStatus.Issued or
                FileTransferFallbackV6ProofBootstrapDecisionStatus.Idempotent) ||
            inboundDecision.Command is not { } inboundCommand)
        {
            LogFallbackV6ProofBootstrapDecision(
                evaluation,
                inbound.TransferId,
                inboundDecision.Command,
                inboundDecision.Status.ToString(),
                inboundDecision.Reason);
            return false;
        }

        inbound.CoordinatorPersistentState = inboundDecision.State.PersistentState;
        command = inboundCommand;
        dataSession = inboundDataSession;
        epochFrame = CreateFallbackV6ProofBootstrapEpochFrame(command);
        proofFrame = new FileTransferReceiverStateFrameV6
        {
            SessionId = inbound.SessionId,
            TransferId = inbound.TransferId,
            Epoch = Math.Max(1, inboundCheckpoint.StateEpoch + 1),
            ContiguousCommittedChunkIndex = inboundCheckpoint.ContiguousCommittedChunkIndex,
            DurableReceivedHighestChunkIndex = inboundCheckpoint.DurableHighestChunkIndex,
            CreditUntilChunkIndexExclusive = inboundCheckpoint.ContiguousCommittedChunkIndex,
            MissingRanges = [],
            BytesCommitted = ResolveInboundV4StateBytesCommittedLocked(inbound),
            ReceiverMemoryPressure = false,
            ReceiverDiskPressure = false,
            TerminalReady = false,
            TransferPaused = true,
            TransferPauseReason =
                FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
            TransportEpoch = command.TargetTransportEpoch,
            Priority = FileTransferProtocol.FallbackV6ProofBootstrapPriority,
            RecoveryMode =
                FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
        };
        snapshot = CreateSnapshotLocked();
        LogFallbackV6ProofBootstrapDecision(
            evaluation,
            inbound.TransferId,
            command,
            inboundDecision.Status.ToString(),
            inboundDecision.Reason);
        return true;
    }

    private bool TryApplyFallbackV6ProofBootstrapReceiptLocked(
        FileTransferFallbackV6ProofBootstrapCommand command,
        FileTransferFallbackV6ProofBootstrapReceipt receipt,
        out SessionFileTransferSnapshot? snapshot)
    {
        snapshot = null;
        if (command.Observation.Direction == FileTransferDirection.Outbound &&
            outboundTransfer is { IsTerminal: false } outbound &&
            string.Equals(outbound.TransferId, command.Observation.TransferId, StringComparison.Ordinal))
        {
            var decision = FileTransferCoordinator.ApplyFileTransferFallbackV6ProofBootstrapReceipt(
                CreateCoordinatorStateLocked(outbound),
                receipt);
            if (decision.Status is not (
                    FileTransferFallbackV6ProofBootstrapDecisionStatus.Applied or
                    FileTransferFallbackV6ProofBootstrapDecisionStatus.Idempotent))
            {
                return false;
            }

            outbound.CoordinatorPersistentState = decision.State.PersistentState;
            if (receipt.Status == FileTransferFallbackV6ProofBootstrapReceiptStatus.Delivered &&
                TryBindOutboundFallbackV6ProofBootstrapRetainedPeerFrameLocked(
                    outbound,
                    out var boundSnapshot))
            {
                snapshot = boundSnapshot ?? CreateSnapshotLocked();
            }
            else
            {
                snapshot = CreateSnapshotLocked();
            }
            return true;
        }

        if (command.Observation.Direction == FileTransferDirection.Inbound &&
            inboundTransfer is { IsTerminal: false } inbound &&
            string.Equals(inbound.TransferId, command.Observation.TransferId, StringComparison.Ordinal))
        {
            var decision = FileTransferCoordinator.ApplyFileTransferFallbackV6ProofBootstrapReceipt(
                CreateCoordinatorStateLocked(inbound),
                receipt);
            if (decision.Status is not (
                    FileTransferFallbackV6ProofBootstrapDecisionStatus.Applied or
                    FileTransferFallbackV6ProofBootstrapDecisionStatus.Idempotent))
            {
                return false;
            }

            inbound.CoordinatorPersistentState = decision.State.PersistentState;
            if (receipt.Status == FileTransferFallbackV6ProofBootstrapReceiptStatus.Delivered &&
                TryBindInboundFallbackV6ProofBootstrapRetainedPeerFrameLocked(
                    inbound,
                    out var boundSnapshot))
            {
                snapshot = boundSnapshot ?? CreateSnapshotLocked();
            }
            else
            {
                snapshot = CreateSnapshotLocked();
            }
            return true;
        }

        return false;
    }

    private bool TryBindOutboundFallbackV6ProofBootstrapRetainedPeerFrameLocked(
        OutboundTransferContext context,
        out SessionFileTransferSnapshot? snapshot)
    {
        snapshot = null;
        if (!ReferenceEquals(outboundTransfer, context) ||
            context.IsTerminal ||
            context.DataSession is not { } dataSession ||
            context.FileTransferRouteTransitionExecution?.RetainedPeerFrame is not
                { } retainedFrame ||
            !FileTransferCoordinator
                .TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(
                    CreateCoordinatorStateLocked(context),
                    retainedFrame,
                    out var boundary,
                    out _))
        {
            return false;
        }

        var duplicate = context.FileTransferRouteTransitionExecution?.PeerProof ==
            boundary.PeerProofEvidence;
        if (!TryRetainOutboundFileTransferRouteTransitionPeerProofLocked(
                context,
                dataSession,
                boundary.PeerProofEvidence,
                "coordinator_fallback_v6_proof_bootstrap_peer_frame",
                out snapshot))
        {
            return false;
        }

        LogFallbackV6ProofBootstrapPeerBoundaryBound(
            boundary,
            duplicate ? "Idempotent" : "Bound",
            duplicate
                ? "bootstrap_peer_frame_binding_duplicate"
                : "bootstrap_peer_frame_bound_after_delivery");
        return true;
    }

    private bool TryBindInboundFallbackV6ProofBootstrapRetainedPeerFrameLocked(
        InboundTransferContext context,
        out SessionFileTransferSnapshot? snapshot)
    {
        snapshot = null;
        if (!ReferenceEquals(inboundTransfer, context) ||
            context.IsTerminal ||
            context.DataSession is not { } dataSession ||
            context.FileTransferRouteTransitionExecution?.RetainedPeerFrame is not
                { } retainedFrame ||
            !FileTransferCoordinator
                .TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(
                    CreateCoordinatorStateLocked(context),
                    retainedFrame,
                    out var boundary,
                    out _))
        {
            return false;
        }

        var duplicate = context.FileTransferRouteTransitionExecution?.PeerProof ==
            boundary.PeerProofEvidence;
        if (!TryRetainInboundFileTransferRouteTransitionPeerProofLocked(
                context,
                dataSession,
                boundary.PeerProofEvidence,
                "coordinator_fallback_v6_proof_bootstrap_peer_frame",
                out snapshot))
        {
            return false;
        }

        LogFallbackV6ProofBootstrapPeerBoundaryBound(
            boundary,
            duplicate ? "Idempotent" : "Bound",
            duplicate
                ? "bootstrap_peer_frame_binding_duplicate"
                : "bootstrap_peer_frame_bound_after_delivery");
        return true;
    }

    private static FileTransferFallbackV6ProofBootstrapObservation
        CreateFallbackV6ProofBootstrapObservation(
            FileTransferFallbackV6ProofBootstrapEvaluation evaluation,
            string transferId,
            FileTransferRouteTransitionDurableCheckpoint checkpoint)
        => new(
            evaluation.SessionId,
            transferId,
            evaluation.Direction,
            evaluation.ObservationGeneration,
            evaluation.FallbackEpoch,
            evaluation.Source,
            evaluation.ProofKind,
            evaluation.ObservedUtcMilliseconds,
            checkpoint);

    private static FileTransferTransportEpochFrameV6
        CreateFallbackV6ProofBootstrapEpochFrame(
            FileTransferFallbackV6ProofBootstrapCommand command)
        => new()
        {
            SessionId = command.Observation.SessionId,
            TransferId = command.Observation.TransferId,
            TransportEpoch = command.TargetTransportEpoch,
            RecoveryMode =
                FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
        };

    private static void LogFallbackV6ProofBootstrapDecision(
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation,
        string transferId,
        FileTransferFallbackV6ProofBootstrapCommand? command,
        string status,
        string reason)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_v6_proof_bootstrap_decided; session_id={FormatProtocolLogValue(evaluation.SessionId)}; transfer_id={FormatProtocolLogValue(transferId)}; direction={evaluation.Direction.ToString().ToLowerInvariant()}; observation_generation={evaluation.ObservationGeneration}; fallback_epoch={evaluation.FallbackEpoch}; authority_id={FormatProtocolLogValue(command?.AuthorityId.ToString() ?? "(none)")}; bootstrap_generation={command?.BootstrapGeneration ?? 0}; source_route=file_tuna_v4; source_protocol_version=4; source_live_route_epoch={command?.SourceLiveRouteEpoch ?? 0}; source_runtime_generation={command?.SourceRuntimeOwner.RuntimeGeneration ?? 0}; source_route_publication_generation={command?.SourceRoutePublication.PublicationGeneration ?? 0}; source_route_commit_generation={command?.SourceRouteCommit.CommitGeneration ?? 0}; source_terminal_generation={command?.SourceTerminalGeneration ?? 0}; source_leg_generation={command?.SourceLegGeneration ?? 0}; source_transport_epoch={command?.SourceTransportEpoch ?? 0}; source_bridge_recovery_generation={command?.SourceBridgeRecoveryGeneration ?? 0}; source_data_session_generation={command?.SourceDataSessionGeneration ?? 0}; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; target_transport_epoch={command?.TargetTransportEpoch ?? 0}; frame_kind={FormatProtocolLogValue(command?.FrameKind.ToString() ?? "(none)")}; decision_status={FormatProtocolLogValue(status)}; decision_reason={FormatProtocolLogValue(reason)}");

    private static void LogFallbackV6ProofBootstrapReceipt(
        FileTransferFallbackV6ProofBootstrapCommand command,
        FileTransferFallbackV6ProofBootstrapReceipt receipt,
        string applicationStatus)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_v6_proof_bootstrap_receipt_applied; session_id={FormatProtocolLogValue(command.Observation.SessionId)}; transfer_id={FormatProtocolLogValue(command.Observation.TransferId)}; direction={command.Observation.Direction.ToString().ToLowerInvariant()}; authority_id={FormatProtocolLogValue(command.AuthorityId.ToString())}; bootstrap_generation={command.BootstrapGeneration}; fallback_epoch={command.Observation.FallbackEpoch}; target_transport_epoch={command.TargetTransportEpoch}; frame_kind={FormatProtocolLogValue(command.FrameKind.ToString())}; frame_fingerprint={FormatProtocolLogValue(receipt.FrameIdentity?.Sha256 ?? "(none)")}; receipt_status={FormatProtocolLogValue(receipt.Status.ToString())}; application_status={FormatProtocolLogValue(applicationStatus)}; reason_code={FileTransferProtocol.GetFallbackV6ProofBootstrapReceiptReasonCode(receipt)}; decision_reason={FormatProtocolLogValue(receipt.Reason)}");
}
