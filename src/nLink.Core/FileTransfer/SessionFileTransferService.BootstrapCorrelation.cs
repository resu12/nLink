using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private static bool UsesBootstrapCorrelation(FileTransferCoordinatorState state)
        => state.PersistentState.FileTransferRouteTransition.BootstrapAgreement?.Accept?.FallbackBootstrapCorrelationVersion == 1;

    private static void AdoptBootstrapAgreementLocked(OutboundTransferContext context, FileTransferBootstrapAgreementDecision decision)
        => _ = AdoptCoordinatorAuthoredEnvelopeLocked(context.CoordinatorInstanceId, context.SessionId, context.TransferId,
            FileTransferDirection.Outbound, decision.State, decision.SnapshotEnvelope,
            adopted => context.CoordinatorPersistentState = adopted.PersistentState, context.CoordinatorSnapshotEmitter);

    private static void AdoptBootstrapAgreementLocked(InboundTransferContext context, FileTransferBootstrapAgreementDecision decision)
        => _ = AdoptCoordinatorAuthoredEnvelopeLocked(context.CoordinatorInstanceId, context.SessionId, context.TransferId,
            FileTransferDirection.Inbound, decision.State, decision.SnapshotEnvelope,
            adopted => context.CoordinatorPersistentState = adopted.PersistentState, context.CoordinatorSnapshotEmitter);

    private void ObserveBootstrapAgreementSelectionLocked(FileTransferBootstrapAgreementDecision decision)
    {
        if (decision.Accepted && decision.State.PersistentState.FileTransferRouteTransition.BootstrapAgreement is { } agreement)
        {
            (transport as IFileTransferRouteTransitionBridge)?.ObserveFallbackBootstrapAgreement(agreement);
        }
    }

    private bool TryPrepareBootstrapSessionOpenLocked(OutboundTransferContext context, ref FileTransferSessionOpenV2 open)
    {
        if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal) { return false; }
        var state = CreateCoordinatorStateLocked(context);
        if (state.PersistentState.FileTransferRouteTransition.BootstrapAgreement is not { } agreement)
        {
            return open.FallbackBootstrapCorrelationVersion is null && open.FallbackBootstrapIssuerId is null;
        }
        open = open with
        {
            FallbackBootstrapCorrelationVersion = agreement.Accept?.FallbackBootstrapCorrelationVersion,
            FallbackBootstrapIssuerId = agreement.Accept?.FallbackBootstrapCorrelationVersion is null ? null : agreement.Offer.FallbackBootstrapIssuerId,
        };
        var decision = FileTransferCoordinator.ApplyFallbackBootstrapSessionOpen(state, open);
        AdoptBootstrapAgreementLocked(context, decision);
        return decision.Accepted;
    }

    private void InvalidatePendingBootstrapCorrelationLocked(FileTransferCoordinatorState state, string reason)
    {
        if (!UsesBootstrapCorrelation(state)) { return; }
        if (state.Direction == FileTransferDirection.Outbound && outboundTransfer is { } outbound &&
            state.PersistentState.CoordinatorInstanceId == outbound.CoordinatorInstanceId)
        {
            outbound.PendingFallbackV6ProofBootstrapPeerFrameEvidence = null;
            outbound.PendingFallbackV6ProofBootstrapPeerFrameBinding = null;
        }
        if (state.Direction == FileTransferDirection.Inbound && inboundTransfer is { } inbound &&
            state.PersistentState.CoordinatorInstanceId == inbound.CoordinatorInstanceId)
        {
            inbound.PendingFallbackV6ProofBootstrapPeerFrameEvidence = null;
            inbound.PendingFallbackV6ProofBootstrapPeerFrameBinding = null;
            inbound.CurrentFallbackV6ProofBootstrapPendingWriteWake = null;
        }
        if (state.PersistentState.FileTransferRouteTransition.BootstrapExchange is not { Completed: false, Invalidated: false }) { return; }
        var decision = FileTransferCoordinator.InvalidateFallbackBootstrapExchange(state, reason);
        AdoptBootstrapCorrelationStateLocked(decision.State, decision.SnapshotEnvelope);
    }

    private static void LogBootstrapCorrelationRejection(FileTransferDirection direction, string sessionId, string transferId, string reason)
        => LocalOperationalLog.Info("FileTransferService",
            $"event=filetransfer_fallback_bootstrap_correlation_rejected; direction={direction.ToString().ToLowerInvariant()}; session_id={FormatProtocolLogValue(sessionId)}; transfer_id={FormatProtocolLogValue(transferId)}; decision_reason={FormatProtocolLogValue(reason)}");

    private FileTransferCoordinatorState? GetBootstrapCorrelationStateLocked(FileTransferDirection direction,
        IFileTransferDataSession dataSession, FileTransferDataFrame frame,
        out FileTransferFallbackV6ProofBootstrapPeerFrameBinding binding)
    {
        binding = null!;
        if (direction == FileTransferDirection.Outbound && outboundTransfer is { IsTerminal: false, UserPaused: false } outbound &&
            ReferenceEquals(outbound.DataSession, dataSession) && frame.SessionId == outbound.SessionId && frame.TransferId == outbound.TransferId &&
            TryCreateFallbackV6ProofBootstrapPeerFrameBinding(outbound, frame, direction, out binding))
        {
            return CreateCoordinatorStateLocked(outbound);
        }
        if (direction == FileTransferDirection.Inbound && inboundTransfer is { IsTerminal: false, UserPaused: false } inbound &&
            ReferenceEquals(inbound.DataSession, dataSession) && frame.SessionId == inbound.SessionId && frame.TransferId == inbound.TransferId &&
            TryCreateFallbackV6ProofBootstrapPeerFrameBinding(inbound, frame, direction, out binding))
        {
            return CreateCoordinatorStateLocked(inbound);
        }
        return null;
    }

    private bool IsBootstrapCorrelationExecutionCurrentLocked(FileTransferBootstrapPhaseCommand command,
        IFileTransferDataSession dataSession, FileTransferFallbackV6ProofBootstrapPeerFrameBinding binding)
    {
        var state = GetBootstrapCorrelationStateLocked(command.Bootstrap.Observation.Direction, dataSession, command.Frame, out var current);
        return state is not null && MatchesFallbackV6ProofBootstrapPeerFrameBinding(binding, current) &&
            current.DurableCheckpoint == command.Bootstrap.Observation.DurableCheckpoint &&
            FileTransferCoordinator.IsFallbackBootstrapPhaseCommandCurrent(state, command);
    }

    private async Task ExecuteBootstrapCorrelationPhaseAsync(IFileTransferRouteTransitionBridge bridge,
        FileTransferBootstrapPhaseCommand command, IFileTransferDataSession dataSession)
    {
        FileTransferFallbackV6ProofBootstrapPeerFrameBinding binding;
        CancellationToken ct;
        lock (gate)
        {
            var state = GetBootstrapCorrelationStateLocked(command.Bootstrap.Observation.Direction, dataSession, command.Frame, out binding);
            if (state is null || !IsBootstrapCorrelationExecutionCurrentLocked(command, dataSession, binding)) { return; }
            ct = command.Bootstrap.Observation.Direction == FileTransferDirection.Outbound
                ? outboundTransfer!.LifetimeCts.Token : inboundTransfer!.LifetimeCts.Token;
        }
        bool Current()
        {
            lock (gate) { return IsBootstrapCorrelationExecutionCurrentLocked(command, dataSession, binding); }
        }
        FileTransferBootstrapPhaseExecution execution;
        try { execution = bridge.ExecuteFallbackBootstrapPhase(command, dataSession, Current, ct); }
        catch (Exception) { execution = FileTransferBootstrapPhaseExecution.Rejected(command, "bootstrap_phase_bridge_failed"); }
        foreach (var pending in new[] { execution.Delivery, execution.Settlement })
        {
            FileTransferBootstrapPhaseReceipt receipt;
            try { receipt = await pending.ConfigureAwait(false); }
            catch (Exception) { receipt = new(command, FileTransferBootstrapPhaseReceiptStatus.Failed, true, [], "bootstrap_phase_bridge_failed"); }
            FileTransferBootstrapPhaseCommand? next = null;
            SessionFileTransferSnapshot? snapshot = null;
            lock (gate)
            {
                var state = GetBootstrapCorrelationStateLocked(command.Bootstrap.Observation.Direction, dataSession, command.Frame, out var current);
                if (state is null || !MatchesFallbackV6ProofBootstrapPeerFrameBinding(binding, current) ||
                    current.DurableCheckpoint != command.Bootstrap.Observation.DurableCheckpoint)
                {
                    LogBootstrapCorrelationRejection(command.Bootstrap.Observation.Direction, command.Frame.SessionId,
                        command.Frame.TransferId, "bootstrap_phase_callback_scope_stale");
                    continue;
                }
                var decision = FileTransferCoordinator.ApplyFallbackBootstrapPhaseReceipt(state, receipt);
                AdoptBootstrapCorrelationStateLocked(decision.State, decision.SnapshotEnvelope);
                if (decision.Accepted)
                {
                    next = decision.Command;
                    TryRetainCompletedBootstrapCorrelationLocked(decision.State, dataSession, out snapshot);
                }
                else
                {
                    LogBootstrapCorrelationRejection(command.Bootstrap.Observation.Direction, command.Frame.SessionId,
                        command.Frame.TransferId, "bootstrap_phase_callback_rejected");
                }
            }
            if (snapshot is not null) { RaiseTransferChanged(snapshot); }
            if (next is not null) { _ = ExecuteBootstrapCorrelationPhaseAsync(bridge, next, dataSession); }
        }
    }

    private void AdoptBootstrapCorrelationStateLocked(FileTransferCoordinatorState state,
        FileTransferCoordinatorTransitionEnvelope? envelope)
    {
        if (state.Direction == FileTransferDirection.Outbound && outboundTransfer is { } outbound)
        {
            _ = AdoptCoordinatorAuthoredEnvelopeLocked(outbound.CoordinatorInstanceId, outbound.SessionId, outbound.TransferId,
                state.Direction, state, envelope, adopted => outbound.CoordinatorPersistentState = adopted.PersistentState,
                outbound.CoordinatorSnapshotEmitter);
        }
        else if (state.Direction == FileTransferDirection.Inbound && inboundTransfer is { } inbound)
        {
            _ = AdoptCoordinatorAuthoredEnvelopeLocked(inbound.CoordinatorInstanceId, inbound.SessionId, inbound.TransferId,
                state.Direction, state, envelope, adopted => inbound.CoordinatorPersistentState = adopted.PersistentState,
                inbound.CoordinatorSnapshotEmitter);
        }
    }

    private bool TryHandleBootstrapCorrelationFrame(FileTransferDirection direction,
        IFileTransferDataSession dataSession, FileTransferReceivedDataFrame received)
    {
        if (received.Frame is FileTransferReceiverStateFrameV6 legacyState && FileTransferProtocol.IsFallbackV6ProofBootstrapReceiverState(legacyState) ||
            received.Frame is FileTransferFrontierRequestFrameV6 legacyRequest && FileTransferProtocol.TryGetFallbackV6ProofBootstrapGeneration(legacyRequest, out _) ||
            received.Frame is FileTransferTransportEpochFrameV6 legacyEpoch && legacyEpoch.RecoveryMode == FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode)
        {
            lock (gate)
            {
                var state = direction == FileTransferDirection.Outbound && outboundTransfer is { } outbound && ReferenceEquals(outbound.DataSession, dataSession)
                    ? CreateCoordinatorStateLocked(outbound)
                    : direction == FileTransferDirection.Inbound && inboundTransfer is { } inbound && ReferenceEquals(inbound.DataSession, dataSession)
                        ? CreateCoordinatorStateLocked(inbound) : null;
                if (state is not null && UsesBootstrapCorrelation(state))
                {
                    LogBootstrapCorrelationRejection(direction, received.Frame.SessionId, received.Frame.TransferId, "bootstrap_peer_admission_rejected");
                    return true;
                }
            }
        }
        if (received.Frame is FileTransferTransportEpochFrameV6 epoch && epoch.RecoveryMode == FileTransferBootstrapCorrelation.RecoveryMode)
        {
            return true; // Epoch is evidence only, never an ordinary epoch-adoption command.
        }
        if (received.Frame is not FileTransferFallbackBootstrapFrameV6 frame) { return false; }
        FileTransferBootstrapPhaseCommand? command = null;
        FileTransferFallbackV6ProofBootstrapEvaluation? wakeEvaluation = null;
        IFileTransferRouteTransitionBridge? bridge;
        SessionFileTransferSnapshot? snapshot = null;
        lock (gate)
        {
            bridge = transport as IFileTransferRouteTransitionBridge;
            var state = GetBootstrapCorrelationStateLocked(direction, dataSession, frame, out var binding);
            if (bridge is not { SupportsFallbackBootstrapCorrelation: true } || state is null || !UsesBootstrapCorrelation(state) ||
                received.TransportKind != FileTransferTransportKind.RegularNkn)
            {
                LogBootstrapCorrelationRejection(direction, frame.SessionId, frame.TransferId, "bootstrap_peer_admission_rejected");
                return true;
            }
            if (direction == FileTransferDirection.Inbound && inboundTransfer is { } inbound &&
                (inbound.ReceiverSparseChunksPendingWrite.Count != 0 || inbound.PendingChunks.Count != 0))
            {
                if (FileTransferCoordinator.CanRetainFallbackBootstrapPeerFrame(state, frame))
                {
                    if (!TryStageBootstrapCorrelationFrame(inbound, frame, binding)) { return true; }
                    wakeEvaluation = CreateBootstrapCorrelationWakeEvaluation(state, binding);
                }
            }
            else if (state.PersistentState.FileTransferRouteTransition.BootstrapExchange is { } exchange &&
                FileTransferCoordinator.IsFallbackBootstrapExchangeCurrent(state, exchange))
            {
                if (binding.DurableCheckpoint != exchange.Bootstrap.Observation.DurableCheckpoint) { return true; }
                var decision = FileTransferCoordinator.ObserveFallbackBootstrapPeerFrame(state, frame,
                    state.CurrentDataSessionIdentity!, state.CurrentDataSessionGeneration);
                AdoptBootstrapCorrelationStateLocked(decision.State, decision.SnapshotEnvelope);
                command = decision.Command;
                if (decision.Accepted) { TryRetainCompletedBootstrapCorrelationLocked(decision.State, dataSession, out snapshot); }
                else { LogBootstrapCorrelationRejection(direction, frame.SessionId, frame.TransferId, "bootstrap_peer_admission_rejected"); }
            }
            else if (FileTransferCoordinator.IsFallbackBootstrapWakeFrameCurrent(state, frame))
            {
                if (direction == FileTransferDirection.Inbound && inboundTransfer is { } current)
                {
                    if (!TryStageBootstrapCorrelationFrame(current, frame, binding)) { return true; }
                }
                wakeEvaluation = CreateBootstrapCorrelationWakeEvaluation(state, binding);
            }
            else
            {
                LogBootstrapCorrelationRejection(direction, frame.SessionId, frame.TransferId, "bootstrap_peer_admission_rejected");
            }
        }
        if (snapshot is not null) { RaiseTransferChanged(snapshot); }
        if (command is not null) { _ = ExecuteBootstrapCorrelationPhaseAsync(bridge!, command, dataSession); }
        if (wakeEvaluation is not null) { _ = ProcessFallbackV6ProofBootstrapEvaluationAsync(bridge!, wakeEvaluation); }
        return true;
    }

    private static bool TryStageBootstrapCorrelationFrame(InboundTransferContext context,
        FileTransferFallbackBootstrapFrameV6 frame, FileTransferFallbackV6ProofBootstrapPeerFrameBinding binding)
    {
        if (context.PendingFallbackV6ProofBootstrapPeerFrameBinding is { } prior &&
            prior.SourceRouteCommit == binding.SourceRouteCommit &&
            (prior.FrameIdentity != binding.FrameIdentity || !MatchesFallbackV6ProofBootstrapPeerFrameBinding(prior, binding))) { return false; }
        context.PendingFallbackV6ProofBootstrapPeerFrameEvidence = frame;
        context.PendingFallbackV6ProofBootstrapPeerFrameBinding = binding;
        return true;
    }

    private FileTransferBootstrapPhaseCommand? IssueBootstrapCorrelationPhaseLocked(FileTransferCoordinatorState state,
        FileTransferFallbackV6ProofBootstrapObservation observation, FileTransferFallbackBootstrapStateFrameV6? localState,
        FileTransferFallbackBootstrapFrameV6? retained)
    {
        FileTransferBootstrapExchangeDecision decision;
        if (state.PersistentState.FileTransferRouteTransition.BootstrapExchange is { } exchange &&
            FileTransferCoordinator.IsFallbackBootstrapExchangeCurrent(state, exchange))
        {
            if (exchange.Bootstrap.Observation.DurableCheckpoint != observation.DurableCheckpoint)
            {
                decision = FileTransferCoordinator.InvalidateFallbackBootstrapExchange(state, "bootstrap_checkpoint_scope_replaced");
            }
            else if (retained is not null)
            {
                decision = FileTransferCoordinator.ObserveFallbackBootstrapPeerFrame(state, retained,
                    state.CurrentDataSessionIdentity!, state.CurrentDataSessionGeneration);
            }
            else
            {
                return FileTransferCoordinator.GetCurrentFallbackBootstrapPhaseCommand(state, heldOnly: true);
            }
        }
        else
        {
            decision = FileTransferCoordinator.StartFallbackBootstrapExchange(state, observation, localState,
                retained as FileTransferFallbackBootstrapRequestFrameV6);
        }
        AdoptBootstrapCorrelationStateLocked(decision.State, decision.SnapshotEnvelope);
        return decision.Accepted ? decision.Command : null;
    }

    private static FileTransferFallbackV6ProofBootstrapEvaluation CreateBootstrapCorrelationWakeEvaluation(
        FileTransferCoordinatorState state, FileTransferFallbackV6ProofBootstrapPeerFrameBinding binding)
    {
        var checkpoint = binding.DurableCheckpoint;
        var generation = Math.Max(1, state.PersistentState.FileTransferRouteTransition.LastFallbackV6ProofBootstrapGeneration + 1);
        return new(state.SessionId, state.Direction, generation, generation, "correlated_bootstrap_peer_frame",
            state.Direction == FileTransferDirection.Outbound ? "file_transfer_v6_state_frame_received" : "file_transfer_v6_frontier_request_frame_received",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), state.TransferId, binding.FrameIdentity.TransportEpoch,
            binding.FrameIdentity.Sha256, binding.DataSession, binding.DataSessionReferenceGeneration,
            new(binding.SourceRouteCommit.RouteToken, 4, binding.SourceRouteCommit.LiveRouteEpoch,
                checkpoint.SourceRuntimeOwner.RuntimeGeneration, checkpoint.SourceRoutePublication.PublicationGeneration,
                binding.SourceRouteCommit.CommitGeneration, checkpoint.SourceTerminalGeneration, checkpoint.SourceLegGeneration,
                checkpoint.SourceTransportEpoch, checkpoint.SourceBridgeRecoveryGeneration, binding.CheckpointRequestId, binding.CheckpointGeneration));
    }

    private void TryRetainCompletedBootstrapCorrelationLocked(FileTransferCoordinatorState state,
        IFileTransferDataSession dataSession, out SessionFileTransferSnapshot? snapshot)
    {
        snapshot = null;
        if (state.PersistentState.FileTransferRouteTransition.BootstrapExchange is not { Completed: true, RetainedPeerFrame: { } frame } ||
            !FileTransferCoordinator.TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(state, frame, out var boundary, out _)) { return; }
        if (state.Direction == FileTransferDirection.Outbound && outboundTransfer is { } outbound && ReferenceEquals(outbound.DataSession, dataSession))
        {
            TryRetainOutboundFileTransferRouteTransitionPeerFrameLocked(outbound, frame, "bootstrap_correlated_exchange_completed",
                boundary.PeerProofEvidence, out snapshot, out _);
        }
        else if (state.Direction == FileTransferDirection.Inbound && inboundTransfer is { } inbound && ReferenceEquals(inbound.DataSession, dataSession))
        {
            TryRetainInboundFileTransferRouteTransitionPeerFrameLocked(inbound, frame, "bootstrap_correlated_exchange_completed",
                boundary.PeerProofEvidence, out snapshot, out _);
        }
    }

    private static bool IsRetainableBootstrapCorrelationProof(FileTransferCoordinatorState state, FileTransferDataFrame frame,
        PostTunaFallbackPeerProofEvidence? evidence)
        => frame is FileTransferFallbackBootstrapFrameV6
            ? evidence is not null && FileTransferCoordinator.TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(
                state, frame, out var boundary, out _) && boundary.PeerProofEvidence == evidence
            : IsPeerPostTunaFallbackV6RuntimeProofFrame(frame);

    private static FileTransferReceiverStateFrameV6 CreateRetainedBootstrapReceiverState(FileTransferFallbackBootstrapStateFrameV6 frame)
        => new()
        {
            SessionId = frame.SessionId, TransferId = frame.TransferId, TransportEpoch = frame.TransportEpoch, Epoch = frame.Epoch,
            ContiguousCommittedChunkIndex = frame.ContiguousCommittedChunkIndex, DurableReceivedHighestChunkIndex = frame.DurableReceivedHighestChunkIndex,
            CreditUntilChunkIndexExclusive = frame.CreditUntilChunkIndexExclusive, BytesCommitted = frame.BytesCommitted,
            TransferPaused = frame.TransferPaused, TransferPauseReason = FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
            Priority = frame.Priority, RecoveryMode = FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
        };
}
