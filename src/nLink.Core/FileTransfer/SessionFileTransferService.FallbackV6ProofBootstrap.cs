using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

internal sealed record FileTransferFallbackV6ProofBootstrapPeerFrameBinding(
    string SessionId,
    string TransferId,
    FileTransferDirection Direction,
    PostTunaFallbackPeerProofFrameIdentity FrameIdentity,
    IFileTransferDataSession DataSession,
    long DataSessionReferenceGeneration,
    long DataSessionGeneration,
    long BootstrapGeneration,
    FileTransferRouteTransitionDurableCheckpoint DurableCheckpoint,
    RouteCommitToken SourceRouteCommit,
    FallbackCheckpointLease? FallbackCheckpointLease,
    string CheckpointRequestId,
    int CheckpointGeneration);

public sealed partial class SessionFileTransferService
{
    private sealed record InboundFallbackV6ProofBootstrapPendingWriteWake(
        long WakeGeneration,
        IFileTransferRouteTransitionBridge Bridge,
        FileTransferFallbackV6ProofBootstrapEvaluation Evaluation,
        IFileTransferDataSession DataSession,
        long DataSessionGeneration,
        RouteCommitToken RouteCommit,
        int PendingSparseWriteCount,
        int PendingContiguousChunkCount,
        bool RecheckClaimed);

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
        => await ProcessFallbackV6ProofBootstrapEvaluationCoreAsync(
                bridge,
                evaluation,
                expectedPendingWriteWake: null)
            .ConfigureAwait(false);

    private Task ProcessFallbackV6ProofBootstrapPendingWriteRecheckAsync(
        InboundFallbackV6ProofBootstrapPendingWriteWake wake)
        => ProcessFallbackV6ProofBootstrapEvaluationCoreAsync(
            wake.Bridge,
            wake.Evaluation,
            wake);

    private async Task ProcessFallbackV6ProofBootstrapEvaluationCoreAsync(
        IFileTransferRouteTransitionBridge bridge,
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation,
        InboundFallbackV6ProofBootstrapPendingWriteWake?
            expectedPendingWriteWake)
    {
        FileTransferFallbackV6ProofBootstrapCommand command;
        FileTransferTransportEpochFrameV6 epochFrame;
        FileTransferDataFrame proofFrame;
        IFileTransferDataSession dataSession;
        SessionFileTransferSnapshot? issuedSnapshot;
        InboundFallbackV6ProofBootstrapPendingWriteWake? deferredWake = null;
        InboundFallbackV6ProofBootstrapPendingWriteWake? replacedWake = null;
        string? deferredStatus = null;

        lock (gate)
        {
            if (expectedPendingWriteWake is not null)
            {
                if (inboundTransfer?.CurrentFallbackV6ProofBootstrapPendingWriteWake
                        is not { } currentWake ||
                    !ReferenceEquals(currentWake, expectedPendingWriteWake) ||
                    !currentWake.RecheckClaimed)
                {
                    return;
                }

                if (inboundTransfer.ReceiverSparseChunksPendingWrite.Count == 0 &&
                    inboundTransfer.PendingChunks.Count == 0 &&
                    !TryRefreshInboundFallbackV6ProofBootstrapPeerFrameBindingAfterPendingWriteDrainLocked(
                        inboundTransfer,
                        expectedPendingWriteWake.Evaluation,
                        out _))
                {
                    inboundTransfer
                            .CurrentFallbackV6ProofBootstrapPendingWriteWake =
                        null;
                    return;
                }
            }
            else if (inboundTransfer?.CurrentFallbackV6ProofBootstrapPendingWriteWake
                         is { } claimedWake &&
                     (claimedWake.RecheckClaimed ||
                      (inboundTransfer.ReceiverSparseChunksPendingWrite.Count == 0 &&
                       inboundTransfer.PendingChunks.Count == 0)))
            {
                return;
            }

            if (!TryIssueFallbackV6ProofBootstrapLockedCore(
                    evaluation,
                    out command,
                    out epochFrame,
                    out proofFrame,
                    out dataSession,
                    out issuedSnapshot,
                    out var pendingWriteContext))
            {
                if (pendingWriteContext is not null)
                {
                    if (expectedPendingWriteWake is not null &&
                        ReferenceEquals(
                            pendingWriteContext
                                .CurrentFallbackV6ProofBootstrapPendingWriteWake,
                            expectedPendingWriteWake))
                    {
                        expectedPendingWriteWake =
                            expectedPendingWriteWake with
                            {
                                RecheckClaimed = false,
                            };
                        pendingWriteContext
                                .CurrentFallbackV6ProofBootstrapPendingWriteWake =
                            expectedPendingWriteWake;
                    }

                    deferredWake = HoldInboundFallbackV6ProofBootstrapPendingWriteWakeLocked(
                        pendingWriteContext,
                        bridge,
                        evaluation,
                        out replacedWake,
                        out deferredStatus);
                }
                else if (expectedPendingWriteWake is not null &&
                         inboundTransfer is { } staleContext &&
                         ReferenceEquals(
                             staleContext
                                 .CurrentFallbackV6ProofBootstrapPendingWriteWake,
                             expectedPendingWriteWake))
                {
                    staleContext.CurrentFallbackV6ProofBootstrapPendingWriteWake =
                        null;
                    RetireInboundFallbackV6ProofBootstrapPeerFrameForWakeLocked(
                        staleContext,
                        expectedPendingWriteWake);
                }
            }
            else if (evaluation.Direction == FileTransferDirection.Inbound &&
                     inboundTransfer is { } inbound &&
                     (expectedPendingWriteWake is null ||
                      ReferenceEquals(
                          inbound.CurrentFallbackV6ProofBootstrapPendingWriteWake,
                          expectedPendingWriteWake)))
            {
                inbound.CurrentFallbackV6ProofBootstrapPendingWriteWake = null;
            }
        }

        if (deferredWake is not null)
        {
            LogInboundFallbackV6ProofBootstrapPendingWriteDeferred(
                deferredWake,
                replacedWake,
                deferredStatus ?? "Held");
            return;
        }

        if (command is null)
        {
            return;
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
        => TryIssueFallbackV6ProofBootstrapLockedCore(
            evaluation,
            out command,
            out epochFrame,
            out proofFrame,
            out dataSession,
            out snapshot,
            out _);

    private bool TryIssueFallbackV6ProofBootstrapLockedCore(
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation,
        out FileTransferFallbackV6ProofBootstrapCommand command,
        out FileTransferTransportEpochFrameV6 epochFrame,
        out FileTransferDataFrame proofFrame,
        out IFileTransferDataSession dataSession,
        out SessionFileTransferSnapshot? snapshot,
        out InboundTransferContext? pendingWriteContext)
    {
        command = null!;
        epochFrame = null!;
        proofFrame = null!;
        dataSession = null!;
        snapshot = null;
        pendingWriteContext = null;

        if (evaluation.Direction == FileTransferDirection.Outbound)
        {
            if (outboundTransfer is { IsTerminal: false } currentOutbound &&
                string.Equals(
                    currentOutbound.SessionId,
                    evaluation.SessionId,
                    StringComparison.Ordinal))
            {
                RetireFallbackV6ProofBootstrapPeerFrameBindingIfSourceChanged(
                    currentOutbound);
            }

            if (outboundTransfer is not { IsTerminal: false } context ||
                !string.Equals(context.SessionId, evaluation.SessionId, StringComparison.Ordinal) ||
                !MatchesFallbackV6ProofBootstrapTrigger(
                    evaluation,
                    context.TransferId,
                    context.DataSession,
                    context.DataSessionGeneration) ||
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
            if (!MatchesFallbackV6ProofBootstrapPhysicalTrigger(
                    evaluation,
                    checkpoint) ||
                (IsPeerFirstFallbackV6ProofBootstrapPhysicalTrigger(evaluation) &&
                 (evaluation.FallbackEpoch <= 1 ||
                  !IsPostRecoveryFileTunaV4LiveActivationEpoch(
                     context.CurrentLiveRouteEpoch,
                     context.LastRecoveredV6TransportLiveRouteEpochId) ||
                  !IsStalePostReactivationFallbackV6Proof(
                      context,
                      evaluation.TriggerTransportEpoch) ||
                  !MatchesFallbackV6ProofBootstrapTriggerSourceScope(
                      evaluation,
                      checkpoint,
                      context.RuntimeUnlockRouteCommitToken.Value,
                      context.CurrentTransferLeg.CheckpointRequestId,
                      context.CurrentTransferLeg.CheckpointGeneration))))
            {
                return false;
            }

            var observation = CreateFallbackV6ProofBootstrapObservation(
                evaluation,
                context.TransferId,
                checkpoint);
            var decision = FileTransferCoordinator.IssueFileTransferFallbackV6ProofBootstrap(
                CreateCoordinatorStateLocked(context),
                observation);
            _ = AdoptCoordinatorStateLocked(context, decision);
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

            command = issued;
            TryAdmitOutboundStagedFallbackV6ProofBootstrapPeerFrameLocked(
                context,
                command,
                evaluation);
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

        if (inboundTransfer is { IsTerminal: false } currentInbound &&
            string.Equals(
                currentInbound.SessionId,
                evaluation.SessionId,
                StringComparison.Ordinal))
        {
            RetireFallbackV6ProofBootstrapPeerFrameBindingIfSourceChanged(
                currentInbound);
        }

        if (inboundTransfer is not { IsTerminal: false } inbound ||
            !string.Equals(inbound.SessionId, evaluation.SessionId, StringComparison.Ordinal) ||
            !MatchesFallbackV6ProofBootstrapTrigger(
                evaluation,
                inbound.TransferId,
                inbound.DataSession,
                inbound.DataSessionGeneration) ||
            !inbound.RouteRuntime.UsesFileTunaV4Runtime ||
            inbound.DataSession is not { } inboundDataSession ||
            inbound.CurrentRuntimeOwner is not { IsDefault: false } inboundOwner ||
            inbound.CurrentRoutePublicationToken is not { IsDefault: false } inboundPublication ||
            inbound.RuntimeUnlockRouteCommitToken is not { IsDefault: false } ||
            inbound.CurrentTransferLeg is not { } inboundLeg)
        {
            return false;
        }

        var inboundCheckpoint = CreateInboundFileTransferRouteTransitionCheckpoint(
            inbound,
            inboundOwner,
            inboundPublication,
            inboundLeg);
        if (!MatchesFallbackV6ProofBootstrapPhysicalTrigger(
                evaluation,
                inboundCheckpoint) ||
            (IsPeerFirstFallbackV6ProofBootstrapPhysicalTrigger(evaluation) &&
             (evaluation.FallbackEpoch <= 1 ||
              !IsCurrentInboundPeerFirstFallbackV6ProofBootstrapCycle(
                  inbound,
                  evaluation) ||
              !MatchesFallbackV6ProofBootstrapTriggerSourceScope(
                  evaluation,
                  inboundCheckpoint,
                  inbound.RuntimeUnlockRouteCommitToken.Value,
                  inbound.CurrentTransferLeg.CheckpointRequestId,
                  inbound.CurrentTransferLeg.CheckpointGeneration))))
        {
            return false;
        }

        if (inbound.ReceiverSparseChunksPendingWrite.Count != 0 ||
            inbound.PendingChunks.Count != 0)
        {
            if (IsPeerFirstFallbackV6ProofBootstrapPhysicalTrigger(evaluation))
            {
                pendingWriteContext = inbound;
            }

            return false;
        }

        var inboundObservation = CreateFallbackV6ProofBootstrapObservation(
            evaluation,
            inbound.TransferId,
            inboundCheckpoint);
        var inboundDecision = FileTransferCoordinator.IssueFileTransferFallbackV6ProofBootstrap(
            CreateCoordinatorStateLocked(inbound),
            inboundObservation);
        _ = AdoptCoordinatorStateLocked(inbound, inboundDecision);
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

        command = inboundCommand;
        TryAdmitInboundStagedFallbackV6ProofBootstrapPeerFrameLocked(
            inbound,
            command,
            evaluation);
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
            _ = AdoptCoordinatorStateLocked(outbound, decision);
            if (decision.Status is not (
                    FileTransferFallbackV6ProofBootstrapDecisionStatus.Applied or
                    FileTransferFallbackV6ProofBootstrapDecisionStatus.Idempotent))
            {
                return false;
            }

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
            _ = AdoptCoordinatorStateLocked(inbound, decision);
            if (decision.Status is not (
                    FileTransferFallbackV6ProofBootstrapDecisionStatus.Applied or
                    FileTransferFallbackV6ProofBootstrapDecisionStatus.Idempotent))
            {
                return false;
            }

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
                     out _) ||
             !TryCreateFallbackV6ProofBootstrapPeerFrameBinding(
                 context,
                 retainedFrame,
                 FileTransferDirection.Outbound,
                 out var binding))
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
                : "bootstrap_peer_frame_bound_after_delivery",
            binding);
        return true;
    }

    private InboundFallbackV6ProofBootstrapPendingWriteWake?
        HoldInboundFallbackV6ProofBootstrapPendingWriteWakeLocked(
            InboundTransferContext context,
            IFileTransferRouteTransitionBridge bridge,
            FileTransferFallbackV6ProofBootstrapEvaluation evaluation,
            out InboundFallbackV6ProofBootstrapPendingWriteWake? replacedWake,
            out string status)
    {
        replacedWake = null;
        var current = context.CurrentFallbackV6ProofBootstrapPendingWriteWake;
        if (current is not null &&
            ReferenceEquals(current.Bridge, bridge) &&
            current.Evaluation == evaluation &&
            ReferenceEquals(current.DataSession, context.DataSession) &&
            current.DataSessionGeneration == context.DataSessionGeneration)
        {
            status = "Coalesced";
            return current;
        }

        if (current is not null &&
            IsInboundFallbackV6ProofBootstrapPendingWriteWakeCurrentLocked(
                context,
                current,
                out _) &&
            (!MatchesInboundFallbackV6ProofBootstrapPendingWriteWakeSourceLineage(
                 current.Evaluation,
                 evaluation) ||
             evaluation.ObservationGeneration <=
                 current.Evaluation.ObservationGeneration))
        {
            status = "Stale";
            return null;
        }

        var stagedFrame =
            context.PendingFallbackV6ProofBootstrapPeerFrameEvidence;
        var stagedBinding =
            context.PendingFallbackV6ProofBootstrapPeerFrameBinding;
        if ((stagedFrame is null) != (stagedBinding is null) ||
            (stagedFrame is not null &&
             (!MatchesFallbackV6ProofBootstrapPeerFrameEvaluationTrigger(
                  stagedBinding!,
                  evaluation) ||
              !PostTunaFallbackPeerProofFrameIdentity.TryCreate(
                  stagedFrame,
                  out var stagedFrameIdentity) ||
              stagedFrameIdentity != stagedBinding!.FrameIdentity)))
        {
            context.PendingFallbackV6ProofBootstrapPeerFrameEvidence = null;
            context.PendingFallbackV6ProofBootstrapPeerFrameBinding = null;
        }

        var wakeGeneration =
            context.LastFallbackV6ProofBootstrapPendingWriteWakeGeneration <
                long.MaxValue
                ? context.LastFallbackV6ProofBootstrapPendingWriteWakeGeneration + 1
                : long.MaxValue;
        status = context.CurrentFallbackV6ProofBootstrapPendingWriteWake is null
            ? "Held"
            : "Replaced";
        if (string.Equals(status, "Replaced", StringComparison.Ordinal))
        {
            replacedWake = current;
        }

        context.LastFallbackV6ProofBootstrapPendingWriteWakeGeneration =
            wakeGeneration;
        var wake = new InboundFallbackV6ProofBootstrapPendingWriteWake(
            wakeGeneration,
            bridge,
            evaluation,
            context.DataSession!,
            context.DataSessionGeneration,
            context.RuntimeUnlockRouteCommitToken!.Value,
            context.ReceiverSparseChunksPendingWrite.Count,
            context.PendingChunks.Count,
            RecheckClaimed: false);
        context.CurrentFallbackV6ProofBootstrapPendingWriteWake = wake;
        return wake;
    }

    private static bool
        MatchesInboundFallbackV6ProofBootstrapPendingWriteWakeSourceLineage(
            FileTransferFallbackV6ProofBootstrapEvaluation left,
            FileTransferFallbackV6ProofBootstrapEvaluation right)
        => string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal) &&
           left.Direction == FileTransferDirection.Inbound &&
           right.Direction == FileTransferDirection.Inbound &&
           left.FallbackEpoch == right.FallbackEpoch &&
           string.Equals(left.Source, right.Source, StringComparison.Ordinal) &&
           string.Equals(left.ProofKind, right.ProofKind, StringComparison.Ordinal) &&
           string.Equals(
               left.TriggerTransferId,
               right.TriggerTransferId,
               StringComparison.Ordinal) &&
           left.TriggerTransportEpoch == right.TriggerTransportEpoch &&
           ReferenceEquals(left.TriggerDataSession, right.TriggerDataSession) &&
           left.TriggerDataSessionGeneration ==
               right.TriggerDataSessionGeneration &&
           left.TriggerSourceScope == right.TriggerSourceScope;

    private InboundFallbackV6ProofBootstrapPendingWriteWake?
        TakeInboundFallbackV6ProofBootstrapPendingWriteWakeAfterDrainLocked(
            InboundTransferContext context,
            out InboundFallbackV6ProofBootstrapPendingWriteWake? observedWake,
            out string status,
            out string result)
    {
        observedWake = context.CurrentFallbackV6ProofBootstrapPendingWriteWake;
        if (observedWake is null)
        {
            status = "Stale";
            result = "wake_missing";
            return null;
        }

        if (observedWake.RecheckClaimed)
        {
            observedWake = null;
            status = "Held";
            result = "recheck_already_claimed";
            return null;
        }

        if (context.ReceiverSparseChunksPendingWrite.Count != 0 ||
            context.PendingChunks.Count != 0)
        {
            status = "Held";
            result = "pending_writes_remain";
            return null;
        }

        if (!IsInboundFallbackV6ProofBootstrapPendingWriteWakeCurrentLocked(
                context,
                observedWake,
                out result))
        {
            context.CurrentFallbackV6ProofBootstrapPendingWriteWake = null;
            RetireInboundFallbackV6ProofBootstrapPeerFrameForWakeLocked(
                context,
                observedWake);
            status = "Stale";
            return null;
        }

        if (!TryRefreshInboundFallbackV6ProofBootstrapPeerFrameBindingAfterPendingWriteDrainLocked(
                context,
                observedWake.Evaluation,
                out result))
        {
            context.CurrentFallbackV6ProofBootstrapPendingWriteWake = null;
            status = "Stale";
            return null;
        }

        observedWake = observedWake with
        {
            RecheckClaimed = true,
        };
        context.CurrentFallbackV6ProofBootstrapPendingWriteWake = observedWake;
        status = "Rechecked";
        result = "pending_writes_drained";
        return observedWake;
    }

    private static bool
        TryRefreshInboundFallbackV6ProofBootstrapPeerFrameBindingAfterPendingWriteDrainLocked(
            InboundTransferContext context,
            FileTransferFallbackV6ProofBootstrapEvaluation evaluation,
            out string reason)
    {
        var frame = context.PendingFallbackV6ProofBootstrapPeerFrameEvidence;
        var pendingBinding =
            context.PendingFallbackV6ProofBootstrapPeerFrameBinding;
        if (frame is null && pendingBinding is null)
        {
            reason = "current";
            return true;
        }

        if (frame is null ||
            pendingBinding is null ||
            !MatchesFallbackV6ProofBootstrapPeerFrameEvaluationTrigger(
                pendingBinding,
                evaluation) ||
            !TryCreateFallbackV6ProofBootstrapPeerFrameBinding(
                context,
                frame,
                FileTransferDirection.Inbound,
                out var currentBinding) ||
            !MatchesFallbackV6ProofBootstrapPeerFrameBindingAfterPendingWriteDrain(
                pendingBinding,
                currentBinding))
        {
            context.PendingFallbackV6ProofBootstrapPeerFrameEvidence = null;
            context.PendingFallbackV6ProofBootstrapPeerFrameBinding = null;
            reason = "peer_frame_binding_changed";
            return false;
        }

        context.PendingFallbackV6ProofBootstrapPeerFrameBinding = currentBinding;
        reason = "current";
        return true;
    }

    private bool IsInboundFallbackV6ProofBootstrapPendingWriteWakeCurrentLocked(
        InboundTransferContext context,
        InboundFallbackV6ProofBootstrapPendingWriteWake wake,
        out string reason)
    {
        if (!ReferenceEquals(inboundTransfer, context) ||
            !ReferenceEquals(transport, wake.Bridge))
        {
            reason = "source_not_current";
            return false;
        }

        return IsInboundFallbackV6ProofBootstrapPendingWriteWakeContextCurrentLocked(
            context,
            wake,
            out reason);
    }

    private static bool
        IsInboundFallbackV6ProofBootstrapPendingWriteWakeContextCurrentLocked(
            InboundTransferContext context,
            InboundFallbackV6ProofBootstrapPendingWriteWake wake,
            out string reason)
    {
        var evaluation = wake.Evaluation;
        if (context.IsTerminal ||
            evaluation.Direction != FileTransferDirection.Inbound ||
            !string.Equals(
                context.SessionId,
                evaluation.SessionId,
                StringComparison.Ordinal) ||
            !MatchesFallbackV6ProofBootstrapTrigger(
                evaluation,
                context.TransferId,
                context.DataSession,
                context.DataSessionGeneration) ||
            !ReferenceEquals(context.DataSession, wake.DataSession) ||
            context.DataSessionGeneration != wake.DataSessionGeneration)
        {
            reason = context.IsTerminal
                ? "transfer_terminal"
                : "source_not_current";
            return false;
        }

        if (!context.RouteRuntime.UsesFileTunaV4Runtime ||
            context.CurrentRuntimeOwner is not { IsDefault: false } owner ||
            context.CurrentRoutePublicationToken is not
                { IsDefault: false } publication ||
            context.RuntimeUnlockRouteCommitToken is not
                { IsDefault: false } routeCommit ||
            context.CurrentTransferLeg is not { } leg ||
            routeCommit != wake.RouteCommit)
        {
            reason = "route_scope_changed";
            return false;
        }

        var checkpoint = CreateInboundFileTransferRouteTransitionCheckpoint(
            context,
            owner,
            publication,
            leg);
        if (!MatchesFallbackV6ProofBootstrapPhysicalTrigger(
                evaluation,
                checkpoint) ||
            !IsPeerFirstFallbackV6ProofBootstrapPhysicalTrigger(evaluation) ||
            evaluation.FallbackEpoch <= 1 ||
            !IsCurrentInboundPeerFirstFallbackV6ProofBootstrapCycle(
                context,
                evaluation) ||
            !MatchesFallbackV6ProofBootstrapTriggerSourceScope(
                evaluation,
                checkpoint,
                routeCommit,
                leg.CheckpointRequestId,
                leg.CheckpointGeneration))
        {
            reason = "bootstrap_trigger_scope_changed";
            return false;
        }

        reason = "current";
        return true;
    }

    private static void
        RetireInboundFallbackV6ProofBootstrapPeerFrameForWakeLocked(
            InboundTransferContext context,
            InboundFallbackV6ProofBootstrapPendingWriteWake wake)
    {
        var frame = context.PendingFallbackV6ProofBootstrapPeerFrameEvidence;
        var binding = context.PendingFallbackV6ProofBootstrapPeerFrameBinding;
        if (frame is null && binding is null)
        {
            return;
        }

        if (frame is null ||
            binding is null ||
            MatchesFallbackV6ProofBootstrapPeerFrameEvaluationTrigger(
                binding,
                wake.Evaluation))
        {
            context.PendingFallbackV6ProofBootstrapPeerFrameEvidence = null;
            context.PendingFallbackV6ProofBootstrapPeerFrameBinding = null;
        }
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
                     out _) ||
             !TryCreateFallbackV6ProofBootstrapPeerFrameBinding(
                 context,
                 retainedFrame,
                 FileTransferDirection.Inbound,
                 out var binding))
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
                : "bootstrap_peer_frame_bound_after_delivery",
            binding);
        return true;
    }

    private static bool TryStageOutboundFallbackV6ProofBootstrapPeerFrameLocked(
        OutboundTransferContext context,
        FileTransferDataFrame frame,
        out string reason)
    {
        reason = "peer_frame_profile_mismatch";
        if (!context.RouteRuntime.UsesFileTunaV4Runtime ||
            context.FileTransferRouteTransitionExecution is not null ||
            context.CoordinatorPersistentState.FileTransferRouteTransition
                .CurrentOperation is not null ||
            frame is not FileTransferReceiverStateFrameV6 receiverState ||
            !FileTransferProtocol.IsFallbackV6ProofBootstrapReceiverState(
                receiverState) ||
            !TryCreateFallbackV6ProofBootstrapPeerFrameBinding(
                context,
                frame,
                FileTransferDirection.Outbound,
                out var candidateBinding))
        {
            return false;
        }

        if (context.PendingFallbackV6ProofBootstrapPeerFrameEvidence is
            { } existing)
        {
            if (!PostTunaFallbackPeerProofFrameIdentity.TryCreate(
                    existing,
                    out var existingIdentity) ||
                context.PendingFallbackV6ProofBootstrapPeerFrameBinding is not
                    { } existingBinding ||
                !MatchesFallbackV6ProofBootstrapPeerFrameBinding(
                    existingBinding,
                    candidateBinding))
            {
                context.PendingFallbackV6ProofBootstrapPeerFrameEvidence = null;
                context.PendingFallbackV6ProofBootstrapPeerFrameBinding = null;
                reason = "precommand_peer_frame_source_changed";
                return false;
            }

            if (existingIdentity != candidateBinding.FrameIdentity)
            {
                reason = "precommand_peer_frame_conflict";
                return false;
            }

            reason = "precommand_peer_frame_duplicate";
            return true;
        }

        context.PendingFallbackV6ProofBootstrapPeerFrameEvidence = frame;
        context.PendingFallbackV6ProofBootstrapPeerFrameBinding =
            candidateBinding;
        reason = "precommand_peer_frame_staged";
        return true;
    }

    private static bool TryStageInboundFallbackV6ProofBootstrapPeerFrameLocked(
        InboundTransferContext context,
        FileTransferDataFrame frame,
        out string reason)
    {
        reason = "peer_frame_profile_mismatch";
        if (!context.RouteRuntime.UsesFileTunaV4Runtime ||
            context.FileTransferRouteTransitionExecution is not null ||
            context.CoordinatorPersistentState.FileTransferRouteTransition
                .CurrentOperation is not null ||
            frame is not FileTransferFrontierRequestFrameV6 frontierRequest ||
            !FileTransferProtocol.TryGetFallbackV6ProofBootstrapGeneration(
                frontierRequest,
                out _) ||
            !TryCreateFallbackV6ProofBootstrapPeerFrameBinding(
                context,
                frame,
                FileTransferDirection.Inbound,
                out var candidateBinding))
        {
            return false;
        }

        if (context.CurrentFallbackV6ProofBootstrapPendingWriteWake is
                { } pendingWriteWake)
        {
            if (!IsInboundFallbackV6ProofBootstrapPendingWriteWakeContextCurrentLocked(
                    context,
                    pendingWriteWake,
                    out _))
            {
                context.CurrentFallbackV6ProofBootstrapPendingWriteWake = null;
                RetireInboundFallbackV6ProofBootstrapPeerFrameForWakeLocked(
                    context,
                    pendingWriteWake);
            }
            else if (!MatchesFallbackV6ProofBootstrapPeerFrameEvaluationTrigger(
                         candidateBinding,
                         pendingWriteWake.Evaluation))
            {
                reason = "precommand_peer_frame_pending_write_wake_mismatch";
                return false;
            }
        }

        if (context.PendingFallbackV6ProofBootstrapPeerFrameEvidence is
            { } existing)
        {
            if (!PostTunaFallbackPeerProofFrameIdentity.TryCreate(
                    existing,
                    out var existingIdentity) ||
                context.PendingFallbackV6ProofBootstrapPeerFrameBinding is not
                    { } existingBinding ||
                !MatchesFallbackV6ProofBootstrapPeerFrameBinding(
                    existingBinding,
                    candidateBinding))
            {
                context.PendingFallbackV6ProofBootstrapPeerFrameEvidence = null;
                context.PendingFallbackV6ProofBootstrapPeerFrameBinding = null;
                reason = "precommand_peer_frame_source_changed";
                return false;
            }

            if (existingIdentity != candidateBinding.FrameIdentity)
            {
                reason = "precommand_peer_frame_conflict";
                return false;
            }

            reason = "precommand_peer_frame_duplicate";
            return true;
        }

        context.PendingFallbackV6ProofBootstrapPeerFrameEvidence = frame;
        context.PendingFallbackV6ProofBootstrapPeerFrameBinding =
            candidateBinding;
        reason = "precommand_peer_frame_staged";
        return true;
    }

    private void TryAdmitOutboundStagedFallbackV6ProofBootstrapPeerFrameLocked(
        OutboundTransferContext context,
        FileTransferFallbackV6ProofBootstrapCommand command,
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation)
    {
        if (context.PendingFallbackV6ProofBootstrapPeerFrameEvidence is not
            { } frame)
        {
            return;
        }

        var coordinatorState = CreateCoordinatorStateLocked(context);
        var reasonCode = "precommand_peer_frame_trigger_mismatch";
        if (!TryCreateFallbackV6ProofBootstrapPeerFrameBinding(
                context,
                frame,
                FileTransferDirection.Outbound,
                out var currentBinding) ||
            context.PendingFallbackV6ProofBootstrapPeerFrameBinding is not
                { } pendingBinding ||
            !MatchesFallbackV6ProofBootstrapPeerFrameBinding(
                pendingBinding,
                currentBinding) ||
            !MatchesFallbackV6ProofBootstrapPeerFrameEvaluation(
                pendingBinding,
                evaluation,
                command) ||
            !FileTransferCoordinator
                .TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerBoundary(
                    coordinatorState,
                    frame,
                    out var currentCommand,
                    out reasonCode) ||
            currentCommand != command)
        {
            context.PendingFallbackV6ProofBootstrapPeerFrameEvidence = null;
            context.PendingFallbackV6ProofBootstrapPeerFrameBinding = null;
            LogFallbackV6ProofBootstrapPeerFrameDecision(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Outbound,
                frame,
                command,
                "rejected",
                reasonCode,
                "none");
            return;
        }

        var handled = TryRetainOutboundFileTransferRouteTransitionPeerFrameLocked(
            context,
            frame,
            "coordinator_fallback_v6_proof_bootstrap_precommand_peer_frame",
            coordinatorEvidence: null,
            out _,
            out var retained);
        context.PendingFallbackV6ProofBootstrapPeerFrameEvidence = null;
        context.PendingFallbackV6ProofBootstrapPeerFrameBinding = null;
        LogFallbackV6ProofBootstrapPeerFrameDecision(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Outbound,
            frame,
            command,
            handled && retained ? "accepted" : "rejected",
            handled && retained
                ? "precommand_peer_frame_admitted"
                : "peer_frame_retention_rejected",
            handled && retained ? "peer_frame_retention_only" : "none",
            pendingBinding);
    }

    private void TryAdmitInboundStagedFallbackV6ProofBootstrapPeerFrameLocked(
        InboundTransferContext context,
        FileTransferFallbackV6ProofBootstrapCommand command,
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation)
    {
        if (context.PendingFallbackV6ProofBootstrapPeerFrameEvidence is not
            { } frame)
        {
            return;
        }

        var coordinatorState = CreateCoordinatorStateLocked(context);
        var reasonCode = "precommand_peer_frame_trigger_mismatch";
        if (!TryCreateFallbackV6ProofBootstrapPeerFrameBinding(
                context,
                frame,
                FileTransferDirection.Inbound,
                out var currentBinding) ||
            context.PendingFallbackV6ProofBootstrapPeerFrameBinding is not
                { } pendingBinding ||
            !MatchesFallbackV6ProofBootstrapPeerFrameBinding(
                pendingBinding,
                currentBinding) ||
            !MatchesFallbackV6ProofBootstrapPeerFrameEvaluation(
                pendingBinding,
                evaluation,
                command) ||
            !FileTransferCoordinator
                .TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerBoundary(
                    coordinatorState,
                    frame,
                    out var currentCommand,
                    out reasonCode) ||
            currentCommand != command)
        {
            context.PendingFallbackV6ProofBootstrapPeerFrameEvidence = null;
            context.PendingFallbackV6ProofBootstrapPeerFrameBinding = null;
            LogFallbackV6ProofBootstrapPeerFrameDecision(
                context.SessionId,
                context.TransferId,
                FileTransferDirection.Inbound,
                frame,
                command,
                "rejected",
                reasonCode,
                "none");
            return;
        }

        var handled = TryRetainInboundFileTransferRouteTransitionPeerFrameLocked(
            context,
            frame,
            "coordinator_fallback_v6_proof_bootstrap_precommand_peer_frame",
            coordinatorEvidence: null,
            out _,
            out var retained);
        context.PendingFallbackV6ProofBootstrapPeerFrameEvidence = null;
        context.PendingFallbackV6ProofBootstrapPeerFrameBinding = null;
        LogFallbackV6ProofBootstrapPeerFrameDecision(
            context.SessionId,
            context.TransferId,
            FileTransferDirection.Inbound,
            frame,
            command,
            handled && retained ? "accepted" : "rejected",
            handled && retained
                ? "precommand_peer_frame_admitted"
                : "peer_frame_retention_rejected",
            handled && retained ? "peer_frame_retention_only" : "none",
            pendingBinding);
    }

    private static bool TryCreateFallbackV6ProofBootstrapPeerFrameBinding(
        OutboundTransferContext context,
        FileTransferDataFrame frame,
        FileTransferDirection direction,
        out FileTransferFallbackV6ProofBootstrapPeerFrameBinding binding)
    {
        binding = null!;
        if (direction != FileTransferDirection.Outbound ||
            context.DataSession is not { } dataSession ||
            dataSession is not IFileTransferDataSessionGenerationProvider provider ||
            provider.Generation <= 0 ||
            context.DataSessionGeneration <= 0 ||
            context.CurrentRuntimeOwner is not { IsDefault: false } owner ||
            context.CurrentRoutePublicationToken is not { IsDefault: false } publication ||
            context.RuntimeUnlockRouteCommitToken is not { IsDefault: false } routeCommit ||
            context.CurrentTransferLeg is not { } leg ||
            !PostTunaFallbackPeerProofFrameIdentity.TryCreate(
                frame,
                out var frameIdentity))
        {
            return false;
        }

        binding = new FileTransferFallbackV6ProofBootstrapPeerFrameBinding(
            context.SessionId,
            context.TransferId,
            direction,
            frameIdentity,
            dataSession,
            provider.Generation,
            context.DataSessionGeneration,
            0,
            CreateOutboundFileTransferRouteTransitionCheckpoint(
                context,
                owner,
                publication,
                leg),
            routeCommit,
            context.CurrentFallbackCheckpointLease,
            leg.CheckpointRequestId ?? "(none)",
            leg.CheckpointGeneration);
        return true;
    }

    private static bool TryCreateFallbackV6ProofBootstrapPeerFrameBinding(
        InboundTransferContext context,
        FileTransferDataFrame frame,
        FileTransferDirection direction,
        out FileTransferFallbackV6ProofBootstrapPeerFrameBinding binding)
    {
        binding = null!;
        var bootstrapGeneration =
            frame is FileTransferFrontierRequestFrameV6 frontierRequest &&
            FileTransferProtocol.TryGetFallbackV6ProofBootstrapGeneration(
                frontierRequest,
                out var parsedBootstrapGeneration)
                ? parsedBootstrapGeneration
                : 0;
        if (direction != FileTransferDirection.Inbound ||
            context.DataSession is not { } dataSession ||
            dataSession is not IFileTransferDataSessionGenerationProvider provider ||
            provider.Generation <= 0 ||
            context.DataSessionGeneration <= 0 ||
            context.CurrentRuntimeOwner is not { IsDefault: false } owner ||
            context.CurrentRoutePublicationToken is not { IsDefault: false } publication ||
            context.RuntimeUnlockRouteCommitToken is not { IsDefault: false } routeCommit ||
            context.CurrentTransferLeg is not { } leg ||
            !PostTunaFallbackPeerProofFrameIdentity.TryCreate(
                frame,
                out var frameIdentity))
        {
            return false;
        }

        binding = new FileTransferFallbackV6ProofBootstrapPeerFrameBinding(
            context.SessionId,
            context.TransferId,
            direction,
            frameIdentity,
            dataSession,
            provider.Generation,
            context.DataSessionGeneration,
            bootstrapGeneration,
            CreateInboundFileTransferRouteTransitionCheckpoint(
                context,
                owner,
                publication,
                leg),
            routeCommit,
            context.CurrentFallbackCheckpointLease,
            leg.CheckpointRequestId ?? "(none)",
            leg.CheckpointGeneration);
        return true;
    }

    private static bool MatchesFallbackV6ProofBootstrapPeerFrameBinding(
        FileTransferFallbackV6ProofBootstrapPeerFrameBinding left,
        FileTransferFallbackV6ProofBootstrapPeerFrameBinding right)
        => string.Equals(left.SessionId, right.SessionId, StringComparison.Ordinal) &&
           string.Equals(left.TransferId, right.TransferId, StringComparison.Ordinal) &&
           left.Direction == right.Direction &&
           ReferenceEquals(left.DataSession, right.DataSession) &&
           left.DataSessionReferenceGeneration ==
               right.DataSessionReferenceGeneration &&
           left.DataSessionGeneration == right.DataSessionGeneration &&
           left.BootstrapGeneration == right.BootstrapGeneration &&
           left.DurableCheckpoint == right.DurableCheckpoint &&
           left.SourceRouteCommit == right.SourceRouteCommit &&
           left.FallbackCheckpointLease == right.FallbackCheckpointLease &&
           string.Equals(
               left.CheckpointRequestId,
               right.CheckpointRequestId,
               StringComparison.Ordinal) &&
           left.CheckpointGeneration == right.CheckpointGeneration;

    private static bool
        MatchesFallbackV6ProofBootstrapPeerFrameBindingAfterPendingWriteDrain(
            FileTransferFallbackV6ProofBootstrapPeerFrameBinding before,
            FileTransferFallbackV6ProofBootstrapPeerFrameBinding after)
        => string.Equals(
               before.SessionId,
               after.SessionId,
               StringComparison.Ordinal) &&
           string.Equals(
               before.TransferId,
               after.TransferId,
               StringComparison.Ordinal) &&
           before.Direction == after.Direction &&
           before.FrameIdentity == after.FrameIdentity &&
           ReferenceEquals(before.DataSession, after.DataSession) &&
           before.DataSessionReferenceGeneration ==
               after.DataSessionReferenceGeneration &&
           before.DataSessionGeneration == after.DataSessionGeneration &&
           before.SourceRouteCommit == after.SourceRouteCommit &&
           before.FallbackCheckpointLease == after.FallbackCheckpointLease &&
           string.Equals(
               before.CheckpointRequestId,
               after.CheckpointRequestId,
               StringComparison.Ordinal) &&
           before.CheckpointGeneration == after.CheckpointGeneration &&
           before.DurableCheckpoint.SourceRuntimeOwner ==
               after.DurableCheckpoint.SourceRuntimeOwner &&
           before.DurableCheckpoint.SourceRoutePublication ==
               after.DurableCheckpoint.SourceRoutePublication &&
           before.DurableCheckpoint.SourceTerminalGeneration ==
               after.DurableCheckpoint.SourceTerminalGeneration &&
           before.DurableCheckpoint.SourceLegGeneration ==
               after.DurableCheckpoint.SourceLegGeneration &&
           before.DurableCheckpoint.SourceTransportEpoch ==
               after.DurableCheckpoint.SourceTransportEpoch &&
           before.DurableCheckpoint.SourceBridgeRecoveryGeneration ==
               after.DurableCheckpoint.SourceBridgeRecoveryGeneration &&
           before.DurableCheckpoint.DataSessionGeneration ==
               after.DurableCheckpoint.DataSessionGeneration &&
           before.DurableCheckpoint.StateEpoch ==
               after.DurableCheckpoint.StateEpoch &&
           before.DurableCheckpoint.ChunkCount ==
               after.DurableCheckpoint.ChunkCount &&
           before.DurableCheckpoint.CheckpointGeneration ==
               after.DurableCheckpoint.CheckpointGeneration &&
           after.DurableCheckpoint.ContiguousCommittedChunkIndex >=
               before.DurableCheckpoint.ContiguousCommittedChunkIndex &&
           after.DurableCheckpoint.DurableHighestChunkIndex >=
               before.DurableCheckpoint.DurableHighestChunkIndex;

    private static bool MatchesFallbackV6ProofBootstrapPeerFrameEvaluationTrigger(
        FileTransferFallbackV6ProofBootstrapPeerFrameBinding binding,
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation)
        => string.Equals(
               binding.SessionId,
               evaluation.SessionId,
               StringComparison.Ordinal) &&
           string.Equals(
               binding.TransferId,
               evaluation.TriggerTransferId,
               StringComparison.Ordinal) &&
           binding.Direction == evaluation.Direction &&
           binding.FrameIdentity.TransportEpoch ==
               evaluation.TriggerTransportEpoch &&
           string.Equals(
               binding.FrameIdentity.Sha256,
               evaluation.TriggerFrameFingerprint,
               StringComparison.Ordinal) &&
           ReferenceEquals(binding.DataSession, evaluation.TriggerDataSession) &&
           binding.DataSessionReferenceGeneration ==
               evaluation.TriggerDataSessionGeneration &&
           (binding.Direction != FileTransferDirection.Inbound ||
            binding.BootstrapGeneration ==
                evaluation.TriggerBootstrapGeneration) &&
           MatchesFallbackV6ProofBootstrapTriggerSourceScope(
               evaluation,
               binding.DurableCheckpoint,
               binding.SourceRouteCommit,
               binding.CheckpointRequestId,
               binding.CheckpointGeneration);

    private static bool MatchesFallbackV6ProofBootstrapPeerFrameEvaluation(
        FileTransferFallbackV6ProofBootstrapPeerFrameBinding binding,
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation,
        FileTransferFallbackV6ProofBootstrapCommand command)
        => MatchesFallbackV6ProofBootstrapPeerFrameEvaluationTrigger(
               binding,
               evaluation) &&
           binding.DurableCheckpoint == command.Observation.DurableCheckpoint &&
           binding.SourceRouteCommit == command.SourceRouteCommit &&
           binding.DataSessionGeneration == command.SourceDataSessionGeneration;

    private static void
        RetireFallbackV6ProofBootstrapPeerFrameBindingIfSourceChanged(
            OutboundTransferContext context)
    {
        if (context.PendingFallbackV6ProofBootstrapPeerFrameEvidence is not
                { } frame ||
            context.PendingFallbackV6ProofBootstrapPeerFrameBinding is not
                { } pendingBinding)
        {
            return;
        }

        if (!TryCreateFallbackV6ProofBootstrapPeerFrameBinding(
                context,
                frame,
                FileTransferDirection.Outbound,
                out var currentBinding) ||
            !MatchesFallbackV6ProofBootstrapPeerFrameBinding(
                pendingBinding,
                currentBinding))
        {
            context.PendingFallbackV6ProofBootstrapPeerFrameEvidence = null;
            context.PendingFallbackV6ProofBootstrapPeerFrameBinding = null;
        }
    }

    private static void
        RetireFallbackV6ProofBootstrapPeerFrameBindingIfSourceChanged(
            InboundTransferContext context)
    {
        if (context.PendingFallbackV6ProofBootstrapPeerFrameEvidence is not
                { } frame ||
            context.PendingFallbackV6ProofBootstrapPeerFrameBinding is not
                { } pendingBinding)
        {
            return;
        }

        if (!TryCreateFallbackV6ProofBootstrapPeerFrameBinding(
                context,
                frame,
                FileTransferDirection.Inbound,
                out var currentBinding) ||
            !MatchesFallbackV6ProofBootstrapPeerFrameBinding(
                pendingBinding,
                currentBinding))
        {
            context.PendingFallbackV6ProofBootstrapPeerFrameEvidence = null;
            context.PendingFallbackV6ProofBootstrapPeerFrameBinding = null;
        }
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

    private static bool MatchesFallbackV6ProofBootstrapTrigger(
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation,
        string transferId,
        IFileTransferDataSession? dataSession,
        long dataSessionGeneration)
    {
        if (string.IsNullOrWhiteSpace(evaluation.TriggerTransferId))
        {
            return evaluation.TriggerTransportEpoch == 0 &&
                   string.IsNullOrWhiteSpace(evaluation.TriggerFrameFingerprint) &&
                   evaluation.TriggerDataSession is null &&
                   evaluation.TriggerDataSessionGeneration == 0;
        }

        var triggerKindMatchesDirection = evaluation.Direction switch
        {
            FileTransferDirection.Outbound =>
                string.Equals(
                    evaluation.ProofKind,
                    "file_transfer_v6_state_frame_received",
                    StringComparison.Ordinal),
            FileTransferDirection.Inbound =>
                string.Equals(
                    evaluation.ProofKind,
                    "file_transfer_v6_frontier_request_frame_received",
                    StringComparison.Ordinal) ||
                string.Equals(
                    evaluation.ProofKind,
                    "file_transfer_bulk_frame_received",
                    StringComparison.Ordinal),
            _ => false,
        };

        return triggerKindMatchesDirection &&
               string.Equals(
                   evaluation.TriggerTransferId,
                   transferId,
               StringComparison.Ordinal) &&
               evaluation.TriggerTransportEpoch >= 0 &&
               !string.IsNullOrWhiteSpace(evaluation.TriggerFrameFingerprint) &&
               dataSession is not null &&
               ReferenceEquals(evaluation.TriggerDataSession, dataSession) &&
               dataSession is IFileTransferDataSessionGenerationProvider generationProvider &&
               evaluation.TriggerDataSessionGeneration > 0 &&
               evaluation.TriggerDataSessionGeneration == generationProvider.Generation &&
               dataSessionGeneration > 0;
    }

    private static bool MatchesFallbackV6ProofBootstrapPhysicalTrigger(
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation,
        FileTransferRouteTransitionDurableCheckpoint checkpoint)
    {
        if (string.IsNullOrWhiteSpace(evaluation.TriggerTransferId) ||
            string.Equals(
                evaluation.ProofKind,
                "file_transfer_bulk_frame_received",
                StringComparison.Ordinal))
        {
            return true;
        }

        return evaluation.TriggerTransportEpoch > 0 &&
               evaluation.TriggerTransportEpoch ==
                   checkpoint.SourceTransportEpoch + 1;
    }

    private static bool IsPeerFirstFallbackV6ProofBootstrapPhysicalTrigger(
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation)
        => !string.IsNullOrWhiteSpace(evaluation.TriggerTransferId) &&
           evaluation.ProofKind is
               "file_transfer_v6_state_frame_received" or
               "file_transfer_v6_frontier_request_frame_received";

    private static bool IsCurrentInboundPeerFirstFallbackV6ProofBootstrapCycle(
        InboundTransferContext context,
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation)
    {
        if (IsStalePostReactivationFallbackV6Proof(
                context,
                evaluation.TriggerTransportEpoch))
        {
            return IsPostRecoveryFileTunaV4LiveActivationEpoch(
                context.CurrentLiveRouteEpoch,
                context.LastRecoveredV6TransportLiveRouteEpochId);
        }

        var transitionState =
            context.CoordinatorPersistentState.FileTransferRouteTransition;
        var priorReceipt = transitionState.LastFallbackV6ProofBootstrapReceipt;
        var priorCommand = priorReceipt?.Command;
        if (evaluation.Direction != FileTransferDirection.Inbound ||
            !IsRecoveredFileTunaV4LiveActivationEpoch(
                context.CurrentLiveRouteEpoch) ||
            evaluation.TriggerBootstrapGeneration <= 0 ||
            evaluation.TriggerBootstrapGeneration != evaluation.FallbackEpoch ||
            transitionState.PendingFallbackV6ProofBootstrap is not null ||
            priorReceipt is not
            {
                IsDefault: false,
                Status:
                    FileTransferFallbackV6ProofBootstrapReceiptStatus.Delivered,
                FrameIdentity.IsComplete: true,
            } ||
            priorCommand is null ||
            transitionState.LastFallbackV6ProofBootstrapGeneration !=
                priorCommand.BootstrapGeneration ||
            priorCommand.BootstrapGeneration == long.MaxValue ||
            evaluation.TriggerBootstrapGeneration !=
                priorCommand.BootstrapGeneration + 1 ||
            priorCommand.Observation.FallbackEpoch == long.MaxValue ||
            evaluation.FallbackEpoch !=
                priorCommand.Observation.FallbackEpoch + 1 ||
            priorCommand.Observation.Direction != evaluation.Direction ||
            !string.Equals(
                priorCommand.Observation.SessionId,
                evaluation.SessionId,
                StringComparison.Ordinal) ||
            !string.Equals(
                priorCommand.Observation.TransferId,
                evaluation.TriggerTransferId,
                StringComparison.Ordinal) ||
            priorCommand.SourceDataSessionGeneration !=
                context.DataSessionGeneration ||
            priorCommand.TargetTransportEpoch !=
                evaluation.TriggerTransportEpoch ||
            priorReceipt.FrameIdentity.TransportEpoch !=
                evaluation.TriggerTransportEpoch ||
            context.CurrentLiveRouteEpoch is not { } currentEpoch ||
            currentEpoch.EpochId <= priorCommand.SourceLiveRouteEpoch)
        {
            return false;
        }

        return FileTransferCoordinator
            .IsCommittedPostFallbackV6ToTunaV4ReactivationCurrent(
                CreateCoordinatorStateLocked(context),
                priorCommand,
                out _);
    }

    private static bool MatchesFallbackV6ProofBootstrapTriggerSourceScope(
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation,
        FileTransferRouteTransitionDurableCheckpoint checkpoint,
        RouteCommitToken routeCommit,
        string? checkpointRequestId,
        int checkpointGeneration)
    {
        var source = evaluation.TriggerSourceScope;
        return source is not null &&
               string.Equals(
                   source.RouteToken,
                   routeCommit.RouteToken,
                   StringComparison.Ordinal) &&
               source.ProtocolVersion == routeCommit.ProtocolVersion &&
               source.LiveRouteEpoch == routeCommit.LiveRouteEpoch &&
               source.RuntimeGeneration ==
                   checkpoint.SourceRuntimeOwner.RuntimeGeneration &&
               source.RoutePublicationGeneration ==
                   checkpoint.SourceRoutePublication.PublicationGeneration &&
               source.RouteCommitGeneration == routeCommit.CommitGeneration &&
               source.TerminalGeneration == checkpoint.SourceTerminalGeneration &&
               source.LegGeneration == checkpoint.SourceLegGeneration &&
               source.TransportEpoch == checkpoint.SourceTransportEpoch &&
               source.BridgeRecoveryGeneration ==
                   checkpoint.SourceBridgeRecoveryGeneration &&
                string.Equals(
                    source.CheckpointRequestId,
                    checkpointRequestId ?? "(none)",
                    StringComparison.Ordinal) &&
                source.CheckpointGeneration == checkpointGeneration;
    }

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
            $"event=filetransfer_fallback_v6_proof_bootstrap_decided; session_id={FormatProtocolLogValue(evaluation.SessionId)}; transfer_id={FormatProtocolLogValue(transferId)}; direction={evaluation.Direction.ToString().ToLowerInvariant()}; observation_generation={evaluation.ObservationGeneration}; fallback_epoch={evaluation.FallbackEpoch}; trigger_transfer_id={FormatProtocolLogValue(evaluation.TriggerTransferId ?? "(none)")}; trigger_transport_epoch={evaluation.TriggerTransportEpoch}; trigger_bootstrap_generation={evaluation.TriggerBootstrapGeneration}; trigger_frame_fingerprint={FormatProtocolLogValue(evaluation.TriggerFrameFingerprint ?? "(none)")}; trigger_data_session_generation={evaluation.TriggerDataSessionGeneration}; authority_id={FormatProtocolLogValue(command?.AuthorityId.ToString() ?? "(none)")}; bootstrap_generation={command?.BootstrapGeneration ?? 0}; source_route=file_tuna_v4; source_protocol_version=4; source_live_route_epoch={command?.SourceLiveRouteEpoch ?? 0}; source_runtime_generation={command?.SourceRuntimeOwner.RuntimeGeneration ?? 0}; source_route_publication_generation={command?.SourceRoutePublication.PublicationGeneration ?? 0}; source_route_commit_generation={command?.SourceRouteCommit.CommitGeneration ?? 0}; source_terminal_generation={command?.SourceTerminalGeneration ?? 0}; source_leg_generation={command?.SourceLegGeneration ?? 0}; source_transport_epoch={command?.SourceTransportEpoch ?? 0}; source_bridge_recovery_generation={command?.SourceBridgeRecoveryGeneration ?? 0}; source_data_session_generation={command?.SourceDataSessionGeneration ?? 0}; source_checkpoint_request_id={FormatProtocolLogValue(evaluation.TriggerSourceScope?.CheckpointRequestId ?? "(none)")}; source_checkpoint_generation={evaluation.TriggerSourceScope?.CheckpointGeneration ?? 0}; source_durable_checkpoint_generation={command?.Observation.DurableCheckpoint.CheckpointGeneration ?? 0}; source_state_epoch={command?.Observation.DurableCheckpoint.StateEpoch ?? 0}; source_contiguous_frontier={command?.Observation.DurableCheckpoint.ContiguousCommittedChunkIndex ?? 0}; source_durable_highest={command?.Observation.DurableCheckpoint.DurableHighestChunkIndex ?? -1}; source_chunk_count={command?.Observation.DurableCheckpoint.ChunkCount ?? 0}; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; target_transport_epoch={command?.TargetTransportEpoch ?? 0}; frame_kind={FormatProtocolLogValue(command?.FrameKind.ToString() ?? "(none)")}; decision_status={FormatProtocolLogValue(status)}; decision_reason={FormatProtocolLogValue(reason)}");

    private static void LogInboundFallbackV6ProofBootstrapPendingWriteDeferred(
        InboundFallbackV6ProofBootstrapPendingWriteWake wake,
        InboundFallbackV6ProofBootstrapPendingWriteWake? replacedWake,
        string status)
    {
        var evaluation = wake.Evaluation;
        var source = evaluation.TriggerSourceScope;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_v6_proof_bootstrap_pending_write_deferred; decision_status={FormatProtocolLogValue(status)}; decision_result=inbound_pending_write_current; authority_effect=none; wake_generation={wake.WakeGeneration}; predecessor_wake_generation={replacedWake?.WakeGeneration ?? 0}; session_id={FormatProtocolLogValue(evaluation.SessionId)}; transfer_id={FormatProtocolLogValue(evaluation.TriggerTransferId ?? "(none)")}; direction=inbound; observation_generation={evaluation.ObservationGeneration}; fallback_epoch={evaluation.FallbackEpoch}; evaluation_source={FormatProtocolLogValue(evaluation.Source)}; proof_kind={FormatProtocolLogValue(evaluation.ProofKind)}; trigger_transport_epoch={evaluation.TriggerTransportEpoch}; trigger_bootstrap_generation={evaluation.TriggerBootstrapGeneration}; trigger_frame_fingerprint={FormatProtocolLogValue(evaluation.TriggerFrameFingerprint ?? "(none)")}; trigger_data_session_generation={evaluation.TriggerDataSessionGeneration}; source_route={FormatProtocolLogValue(source?.RouteToken ?? "(none)")}; source_protocol_version={source?.ProtocolVersion ?? 0}; source_live_route_epoch={source?.LiveRouteEpoch ?? 0}; source_runtime_generation={source?.RuntimeGeneration ?? 0}; source_route_publication_generation={source?.RoutePublicationGeneration ?? 0}; source_route_commit_generation={source?.RouteCommitGeneration ?? 0}; source_terminal_generation={source?.TerminalGeneration ?? 0}; source_leg_generation={source?.LegGeneration ?? 0}; source_transport_epoch={source?.TransportEpoch ?? 0}; source_bridge_recovery_generation={source?.BridgeRecoveryGeneration ?? 0}; source_data_session_generation={wake.DataSessionGeneration}; source_checkpoint_request_id={FormatProtocolLogValue(source?.CheckpointRequestId ?? "(none)")}; source_checkpoint_generation={source?.CheckpointGeneration ?? 0}; pending_sparse_write_count={wake.PendingSparseWriteCount}; pending_contiguous_chunk_count={wake.PendingContiguousChunkCount}");
    }

    private static void
        LogInboundFallbackV6ProofBootstrapPendingWriteRecheckDecision(
            InboundFallbackV6ProofBootstrapPendingWriteWake wake,
            string status,
            string result)
    {
        var evaluation = wake.Evaluation;
        var source = evaluation.TriggerSourceScope;
        LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_v6_proof_bootstrap_pending_write_recheck_decided; decision_status={FormatProtocolLogValue(status)}; decision_result={FormatProtocolLogValue(result)}; authority_effect=none; wake_generation={wake.WakeGeneration}; session_id={FormatProtocolLogValue(evaluation.SessionId)}; transfer_id={FormatProtocolLogValue(evaluation.TriggerTransferId ?? "(none)")}; direction=inbound; observation_generation={evaluation.ObservationGeneration}; fallback_epoch={evaluation.FallbackEpoch}; evaluation_source={FormatProtocolLogValue(evaluation.Source)}; proof_kind={FormatProtocolLogValue(evaluation.ProofKind)}; trigger_transport_epoch={evaluation.TriggerTransportEpoch}; trigger_bootstrap_generation={evaluation.TriggerBootstrapGeneration}; trigger_frame_fingerprint={FormatProtocolLogValue(evaluation.TriggerFrameFingerprint ?? "(none)")}; trigger_data_session_generation={evaluation.TriggerDataSessionGeneration}; source_route={FormatProtocolLogValue(source?.RouteToken ?? "(none)")}; source_protocol_version={source?.ProtocolVersion ?? 0}; source_live_route_epoch={source?.LiveRouteEpoch ?? 0}; source_runtime_generation={source?.RuntimeGeneration ?? 0}; source_route_publication_generation={source?.RoutePublicationGeneration ?? 0}; source_route_commit_generation={source?.RouteCommitGeneration ?? 0}; source_terminal_generation={source?.TerminalGeneration ?? 0}; source_leg_generation={source?.LegGeneration ?? 0}; source_transport_epoch={source?.TransportEpoch ?? 0}; source_bridge_recovery_generation={source?.BridgeRecoveryGeneration ?? 0}; source_data_session_generation={wake.DataSessionGeneration}; source_checkpoint_request_id={FormatProtocolLogValue(source?.CheckpointRequestId ?? "(none)")}; source_checkpoint_generation={source?.CheckpointGeneration ?? 0}; pending_sparse_write_count=0; pending_contiguous_chunk_count=0");
    }

    private static void LogFallbackV6ProofBootstrapReceipt(
        FileTransferFallbackV6ProofBootstrapCommand command,
        FileTransferFallbackV6ProofBootstrapReceipt receipt,
        string applicationStatus)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_v6_proof_bootstrap_receipt_applied; session_id={FormatProtocolLogValue(command.Observation.SessionId)}; transfer_id={FormatProtocolLogValue(command.Observation.TransferId)}; direction={command.Observation.Direction.ToString().ToLowerInvariant()}; authority_id={FormatProtocolLogValue(command.AuthorityId.ToString())}; bootstrap_generation={command.BootstrapGeneration}; fallback_epoch={command.Observation.FallbackEpoch}; target_transport_epoch={command.TargetTransportEpoch}; frame_kind={FormatProtocolLogValue(command.FrameKind.ToString())}; frame_fingerprint={FormatProtocolLogValue(receipt.FrameIdentity?.Sha256 ?? "(none)")}; receipt_status={FormatProtocolLogValue(receipt.Status.ToString())}; application_status={FormatProtocolLogValue(applicationStatus)}; reason_code={FileTransferProtocol.GetFallbackV6ProofBootstrapReceiptReasonCode(receipt)}; decision_reason={FormatProtocolLogValue(receipt.Reason)}");
}
