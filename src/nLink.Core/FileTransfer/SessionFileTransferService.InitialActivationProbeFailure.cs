using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private void OnRuntimeUnlockInitialActivationPreCommitProbeFailureObserved(
        object? sender,
        RuntimeUnlockInitialActivationPreCommitProbeFailureObservedEventArgs args)
    {
        if (sender is not IRuntimeUnlockRetryDispatchAuthorityBridge bridge)
        {
            return;
        }

        var observation = args.Observation;
        args.Decision = ObserveRuntimeUnlockInitialActivationProbeFailure(bridge, observation);
        if (observation?.Probe is null)
        {
            LocalOperationalLog.Info("FileTransferService",
                $"event=filetransfer_runtime_unlock_initial_activation_precommit_probe_sidecar_failure_decided; decision_status={args.Decision.Status}; decision_reason={FormatProtocolLogValue(args.Decision.Reason)}; authority_effect=none");
            return;
        }
        LocalOperationalLog.Info("FileTransferService",
            $"event=filetransfer_runtime_unlock_initial_activation_precommit_probe_sidecar_failure_decided; session_id={FormatProtocolLogValue(observation.Authority.SessionId)}; transfer_id={FormatProtocolLogValue(observation.Authority.TransferId)}; direction=outbound; authority_id={observation.Authority.AuthorityId}; transition_generation={observation.Authority.TransitionGeneration}; transaction_generation={observation.Probe.TransactionGeneration}; offer_generation={observation.Probe.OfferGeneration}; launch_generation={observation.Probe.LaunchGeneration}; tuna_path_lease_generation={observation.Probe.TunaPathLeaseGeneration}; tuna_path_lease_listener_run_id={FormatProtocolLogValue(observation.Probe.TunaPathLeaseListenerRunId)}; probe_id={FormatProtocolLogValue(observation.Probe.PathProbeId)}; data_session_identity={FormatProtocolLogValue(observation.SourceDataSessionIdentity)}; data_session_generation={observation.SourceDataSessionGeneration}; decision_status={args.Decision.Status}; decision_reason={FormatProtocolLogValue(args.Decision.Reason)}; authority_effect=none");
    }

    private RuntimeUnlockInitialActivationPreCommitProbeFailureDecision
        ObserveRuntimeUnlockInitialActivationProbeFailure(
            IRuntimeUnlockRetryDispatchAuthorityBridge bridge,
            RuntimeUnlockInitialActivationPreCommitProbeFailureObservation observation)
    {
        if (observation is null || observation.IsDefault)
        {
            return new(RuntimeUnlockInitialActivationPreCommitProbeFailureStatus.Rejected,
                "initial_activation_precommit_probe_failure_observation_invalid");
        }

        OutboundTransferContext context;
        FileTransferRouteTransitionExecutionShadow execution;
        lock (gate)
        {
            if (outboundTransfer is not { } current ||
                current.FileTransferRouteTransitionExecution is not { } currentExecution ||
                !IsInitialActivationProbeFailureCurrentLocked(bridge, observation, current, currentExecution))
            {
                return new(RuntimeUnlockInitialActivationPreCommitProbeFailureStatus.Stale,
                    "initial_activation_precommit_probe_failure_source_not_current");
            }

            context = current;
            execution = currentExecution;
        }

        // This read includes the concrete transport data-session lifetime. Never
        // take its source/acceleration locks while holding the service gate.
        if (!bridge.IsRuntimeUnlockInitialActivationPreCommitProbeFailureCurrent(observation))
        {
            return new(RuntimeUnlockInitialActivationPreCommitProbeFailureStatus.Stale,
                "initial_activation_precommit_probe_failure_transport_not_current");
        }

        lock (gate)
        {
            if (!IsInitialActivationProbeFailureCurrentLocked(bridge, observation, context, execution))
            {
                return new(RuntimeUnlockInitialActivationPreCommitProbeFailureStatus.Stale,
                    "initial_activation_precommit_probe_failure_source_replaced");
            }

            var activation = (FileTransferTunaActivationTransitionProofBoundary)observation.Authority.ProofBoundary;
            const string reason = "runtime_unlock_tuna_probe_sidecar_closed";
            if (!InvalidateOutboundFileTransferRouteTransitionLocked(context, observation.Authority,
                    terminalInvalidation: false, $"precommit_probe_failed_{reason}"))
            {
                return new(RuntimeUnlockInitialActivationPreCommitProbeFailureStatus.Rejected,
                    "initial_activation_precommit_probe_failure_disposition_rejected");
            }

            var retired = FileTransferCoordinator.Apply(
                RuntimeUnlockPendingPreCommitProbeCoordinatorEvent(
                    FileTransferCoordinatorEventKind.RuntimeUnlockPreCommitProbeRetired,
                    context.RouteSelection, context.CurrentTransferLeg, reason, activation.PendingPreCommitProbe),
                CreateCoordinatorStateLocked(context));
            if (retired.RuntimeUnlockPendingPreCommitProbeDecision?.Accepted != true)
            {
                return new(RuntimeUnlockInitialActivationPreCommitProbeFailureStatus.Rejected,
                    "initial_activation_precommit_probe_failure_pending_retirement_rejected");
            }

            ApplyCoordinatorDecisionToContextLocked(context, retired);
            return new(RuntimeUnlockInitialActivationPreCommitProbeFailureStatus.Accepted,
                "initial_activation_precommit_probe_failure_rollback_accepted");
        }
    }

    private bool IsInitialActivationProbeFailureCurrentLocked(
        IRuntimeUnlockRetryDispatchAuthorityBridge bridge,
        RuntimeUnlockInitialActivationPreCommitProbeFailureObservation observation,
        OutboundTransferContext context,
        FileTransferRouteTransitionExecutionShadow execution)
    {
        var authority = observation.Authority;
        var activation = (FileTransferTunaActivationTransitionProofBoundary)authority.ProofBoundary;
        return !disposed && ReferenceEquals(transport, bridge) &&
            ReferenceEquals(outboundTransfer, context) && !context.IsTerminal &&
            ReferenceEquals(context.FileTransferRouteTransitionExecution, execution) &&
            execution.Authority == authority &&
            context.SessionId == authority.SessionId && context.TransferId == authority.TransferId &&
            context.RouteRuntime.UsesRegularNknV4FastRuntime &&
            context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV4 &&
            ReferenceEquals(context.DataSession, observation.SourceDataSession) &&
            context.DataSessionGeneration == observation.SourceDataSessionGeneration &&
            CreateReceiveRecoveryDataSessionIdentity(context.TransferId, context.DataSession, context.DataSessionGeneration) ==
                observation.SourceDataSessionIdentity &&
            context.CurrentTransferLeg is { } leg &&
            (leg.CheckpointRequestId ?? "(none)") == (observation.SourceCheckpointRequestId ?? "(none)") &&
            leg.CheckpointGeneration == observation.SourceCheckpointGeneration &&
            context.RuntimeUnlockAdmissionToken == activation.RuntimeUnlockAdmission &&
            context.RuntimeUnlockPendingPreCommitProbe == activation.PendingPreCommitProbe &&
            RuntimeUnlockPreCommitProbeSnapshotMatchesPending(observation.Probe, activation.PendingPreCommitProbe, out _) &&
            context.CoordinatorPersistentState.FileTransferRouteTransition.CurrentOperation is
                { State: FileTransferRouteTransitionState.Draining, PauseBindReceipt: { } pause } &&
            pause == observation.PauseBinding &&
            IsOutboundFileTransferRouteTransitionExecutionCurrentLocked(context, execution, out _);
    }
}
