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
    FileTransferRouteTransitionDurableCheckpoint DurableCheckpoint,
    RouteCommitToken SourceRouteCommit,
    FallbackCheckpointLease? FallbackCheckpointLease,
    string CheckpointRequestId,
    int CheckpointGeneration);

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
        if (!MatchesFallbackV6ProofBootstrapPhysicalTrigger(
                evaluation,
                inboundCheckpoint) ||
            (IsPeerFirstFallbackV6ProofBootstrapPhysicalTrigger(evaluation) &&
             (evaluation.FallbackEpoch <= 1 ||
              !IsPostRecoveryFileTunaV4LiveActivationEpoch(
                 inbound.CurrentLiveRouteEpoch,
                 inbound.LastRecoveredV6TransportLiveRouteEpochId) ||
              !IsStalePostReactivationFallbackV6Proof(
                  inbound,
                  evaluation.TriggerTransportEpoch) ||
              !MatchesFallbackV6ProofBootstrapTriggerSourceScope(
                  evaluation,
                  inboundCheckpoint,
                  inbound.RuntimeUnlockRouteCommitToken.Value,
                  inbound.CurrentTransferLeg.CheckpointRequestId,
                  inbound.CurrentTransferLeg.CheckpointGeneration))))
        {
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
           left.DurableCheckpoint == right.DurableCheckpoint &&
           left.SourceRouteCommit == right.SourceRouteCommit &&
           left.FallbackCheckpointLease == right.FallbackCheckpointLease &&
           string.Equals(
               left.CheckpointRequestId,
               right.CheckpointRequestId,
               StringComparison.Ordinal) &&
           left.CheckpointGeneration == right.CheckpointGeneration;

    private static bool MatchesFallbackV6ProofBootstrapPeerFrameEvaluation(
        FileTransferFallbackV6ProofBootstrapPeerFrameBinding binding,
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation,
        FileTransferFallbackV6ProofBootstrapCommand command)
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
           MatchesFallbackV6ProofBootstrapTriggerSourceScope(
               evaluation,
               binding.DurableCheckpoint,
               binding.SourceRouteCommit,
               binding.CheckpointRequestId,
               binding.CheckpointGeneration) &&
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
            $"event=filetransfer_fallback_v6_proof_bootstrap_decided; session_id={FormatProtocolLogValue(evaluation.SessionId)}; transfer_id={FormatProtocolLogValue(transferId)}; direction={evaluation.Direction.ToString().ToLowerInvariant()}; observation_generation={evaluation.ObservationGeneration}; fallback_epoch={evaluation.FallbackEpoch}; trigger_transfer_id={FormatProtocolLogValue(evaluation.TriggerTransferId ?? "(none)")}; trigger_transport_epoch={evaluation.TriggerTransportEpoch}; trigger_frame_fingerprint={FormatProtocolLogValue(evaluation.TriggerFrameFingerprint ?? "(none)")}; trigger_data_session_generation={evaluation.TriggerDataSessionGeneration}; authority_id={FormatProtocolLogValue(command?.AuthorityId.ToString() ?? "(none)")}; bootstrap_generation={command?.BootstrapGeneration ?? 0}; source_route=file_tuna_v4; source_protocol_version=4; source_live_route_epoch={command?.SourceLiveRouteEpoch ?? 0}; source_runtime_generation={command?.SourceRuntimeOwner.RuntimeGeneration ?? 0}; source_route_publication_generation={command?.SourceRoutePublication.PublicationGeneration ?? 0}; source_route_commit_generation={command?.SourceRouteCommit.CommitGeneration ?? 0}; source_terminal_generation={command?.SourceTerminalGeneration ?? 0}; source_leg_generation={command?.SourceLegGeneration ?? 0}; source_transport_epoch={command?.SourceTransportEpoch ?? 0}; source_bridge_recovery_generation={command?.SourceBridgeRecoveryGeneration ?? 0}; source_data_session_generation={command?.SourceDataSessionGeneration ?? 0}; source_checkpoint_request_id={FormatProtocolLogValue(evaluation.TriggerSourceScope?.CheckpointRequestId ?? "(none)")}; source_checkpoint_generation={evaluation.TriggerSourceScope?.CheckpointGeneration ?? 0}; source_durable_checkpoint_generation={command?.Observation.DurableCheckpoint.CheckpointGeneration ?? 0}; source_state_epoch={command?.Observation.DurableCheckpoint.StateEpoch ?? 0}; source_contiguous_frontier={command?.Observation.DurableCheckpoint.ContiguousCommittedChunkIndex ?? 0}; source_durable_highest={command?.Observation.DurableCheckpoint.DurableHighestChunkIndex ?? -1}; source_chunk_count={command?.Observation.DurableCheckpoint.ChunkCount ?? 0}; target_route=post_tuna_fallback_v6; target_protocol_version=6; target_transport=regular_nkn; target_transport_epoch={command?.TargetTransportEpoch ?? 0}; frame_kind={FormatProtocolLogValue(command?.FrameKind.ToString() ?? "(none)")}; decision_status={FormatProtocolLogValue(status)}; decision_reason={FormatProtocolLogValue(reason)}");

    private static void LogFallbackV6ProofBootstrapReceipt(
        FileTransferFallbackV6ProofBootstrapCommand command,
        FileTransferFallbackV6ProofBootstrapReceipt receipt,
        string applicationStatus)
        => LocalOperationalLog.Info(
            "FileTransferService",
            $"event=filetransfer_fallback_v6_proof_bootstrap_receipt_applied; session_id={FormatProtocolLogValue(command.Observation.SessionId)}; transfer_id={FormatProtocolLogValue(command.Observation.TransferId)}; direction={command.Observation.Direction.ToString().ToLowerInvariant()}; authority_id={FormatProtocolLogValue(command.AuthorityId.ToString())}; bootstrap_generation={command.BootstrapGeneration}; fallback_epoch={command.Observation.FallbackEpoch}; target_transport_epoch={command.TargetTransportEpoch}; frame_kind={FormatProtocolLogValue(command.FrameKind.ToString())}; frame_fingerprint={FormatProtocolLogValue(receipt.FrameIdentity?.Sha256 ?? "(none)")}; receipt_status={FormatProtocolLogValue(receipt.Status.ToString())}; application_status={FormatProtocolLogValue(applicationStatus)}; reason_code={FileTransferProtocol.GetFallbackV6ProofBootstrapReceiptReasonCode(receipt)}; decision_reason={FormatProtocolLogValue(receipt.Reason)}");
}
