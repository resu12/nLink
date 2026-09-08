using NLink.Core.FileTransfer;
using NLink.Core.Logging;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    private event EventHandler<RuntimeUnlockInitialActivationPreCommitProbeFailureObservedEventArgs>?
        runtimeUnlockInitialActivationPreCommitProbeFailureObserved;

    event EventHandler<RuntimeUnlockInitialActivationPreCommitProbeFailureObservedEventArgs>?
        IRuntimeUnlockRetryDispatchAuthorityBridge.RuntimeUnlockInitialActivationPreCommitProbeFailureObserved
    {
        add => runtimeUnlockInitialActivationPreCommitProbeFailureObserved += value;
        remove => runtimeUnlockInitialActivationPreCommitProbeFailureObserved -= value;
    }

    private bool TryCaptureRuntimeUnlockInitialActivationPreCommitProbeFailure(
        string? sidecarReason,
        out RuntimeUnlockInitialActivationPreCommitProbeFailureObservation? observation)
    {
        observation = null;
        if (!IsRuntimeUnlockListenerRearmSidecarDropReason(sidecarReason))
        {
            return false;
        }

        FileTransferRouteTransitionPauseBindingShadow binding;
        RuntimeUnlockRouteCommitSnapshot probe;
        lock (accelerationGate)
        {
            var shadow = fileTransferRouteTransitionExecutionShadow;
            if (disposed || shadow.Current is not { } current ||
                shadow.RetainedPredecessor is not null || shadow.PendingAvailabilityDisposition is not null ||
                runtimeUnlockPreCommitProbeStartedSnapshot is not { } started ||
                !IsInitialActivationOfferProofForProbeUnsafe(started) ||
                !RuntimeUnlockInitialActivationFailureProbeMatchesTransactionUnsafe(started, failed: false))
            {
                return false;
            }

            binding = current;
            probe = started;
        }

        var authority = binding.Command.Authority;
        var publication = FileTransferCoordinator.CreateRoutePublicationAuthority(authority.SourceRoutePublication);
        if (publication is null || !TryCaptureRuntimeUnlockRetryNegotiationSourceScope(
                publication, out var source, out var dataSession, out var checkpointGeneration) ||
            dataSession is null || source.TransportEpoch != authority.SourceTransportEpoch ||
            source.BridgeRecoveryGeneration != authority.SourceBridgeRecoveryGeneration)
        {
            return false;
        }

        var candidate = new RuntimeUnlockInitialActivationPreCommitProbeFailureObservation(
            binding.Receipt, probe, dataSession, source.DataSessionIdentity, source.DataSessionGeneration,
            source.CheckpointRequestId, checkpointGeneration, NormalizeAccelerationSidecarResetReason(sidecarReason));
        if (candidate.IsDefault)
        {
            return false;
        }

        lock (accelerationGate)
        {
            if (disposed || dataSession.IsDisposed ||
                !ReferenceEquals(fileTransferRouteTransitionExecutionShadow.Current, binding) ||
                runtimeUnlockPreCommitProbeStartedSnapshot != probe ||
                !IsInitialActivationOfferProofForProbeUnsafe(probe) ||
                !RuntimeUnlockInitialActivationFailureProbeMatchesTransactionUnsafe(probe, failed: false))
            {
                return false;
            }

            observation = candidate;
            return true;
        }
    }

    bool IRuntimeUnlockRetryDispatchAuthorityBridge.IsRuntimeUnlockInitialActivationPreCommitProbeFailureCurrent(
        RuntimeUnlockInitialActivationPreCommitProbeFailureObservation observation)
        => IsRuntimeUnlockInitialActivationPreCommitProbeFailureCurrent(observation);

    private bool IsRuntimeUnlockInitialActivationPreCommitProbeFailureCurrent(
        RuntimeUnlockInitialActivationPreCommitProbeFailureObservation observation)
    {
        if (observation is null || observation.IsDefault ||
            FileTransferCoordinator.CreateRoutePublicationAuthority(observation.Authority.SourceRoutePublication) is not { } publication ||
            !TryCaptureRuntimeUnlockRetryNegotiationSourceScope(publication, out var source, out var session, out var checkpoint) ||
            !ReferenceEquals(session, observation.SourceDataSession) || session is null ||
            source.DataSessionIdentity != observation.SourceDataSessionIdentity ||
            source.DataSessionGeneration != observation.SourceDataSessionGeneration ||
            source.TransportEpoch != observation.Authority.SourceTransportEpoch ||
            source.BridgeRecoveryGeneration != observation.Authority.SourceBridgeRecoveryGeneration ||
            (source.CheckpointRequestId ?? "(none)") != (observation.SourceCheckpointRequestId ?? "(none)") ||
            checkpoint != observation.SourceCheckpointGeneration)
        {
            return false;
        }

        lock (accelerationGate)
        {
            var shadow = fileTransferRouteTransitionExecutionShadow;
            return !disposed && !session.IsDisposed &&
                shadow.Current?.Receipt == observation.PauseBinding &&
                shadow.RetainedPredecessor is null && shadow.PendingAvailabilityDisposition is null &&
                runtimeUnlockPreCommitProbeStartedSnapshot == observation.Probe &&
                IsInitialActivationOfferProofForProbeUnsafe(observation.Probe) &&
                RuntimeUnlockInitialActivationFailureProbeMatchesTransactionUnsafe(observation.Probe, failed: true);
        }
    }

    private bool RuntimeUnlockInitialActivationFailureProbeMatchesTransactionUnsafe(
        RuntimeUnlockRouteCommitSnapshot probe,
        bool failed)
    {
        var current = runtimeUnlockTransactionState;
        var lease = runtimeUnlockTunaPathLeaseState;
        return probe.LaunchGeneration > 0 &&
            probe.PathProbeState == nameof(RuntimeUnlockPathProbeState.Started) &&
            probe.PathProbeTransport == FileTransferTransportKind.Tuna &&
            !string.IsNullOrWhiteSpace(probe.PathProbeId) &&
            current.SessionId == probe.SessionId &&
            (string.IsNullOrWhiteSpace(current.TransferId) || current.TransferId == probe.TransferId) &&
            current.TransactionGeneration == probe.TransactionGeneration &&
            current.OfferGeneration == probe.OfferGeneration &&
            current.TunaPathLeaseGeneration == probe.TunaPathLeaseGeneration &&
            current.TunaPathLeaseListenerRunId == probe.TunaPathLeaseListenerRunId &&
            lease.SessionId == probe.SessionId &&
            (string.IsNullOrWhiteSpace(lease.TransferId) || lease.TransferId == probe.TransferId) &&
            lease.TransactionGeneration == probe.TransactionGeneration &&
            lease.OfferGeneration == probe.OfferGeneration &&
            lease.LeaseGeneration == probe.TunaPathLeaseGeneration &&
            lease.ListenerRunId == probe.TunaPathLeaseListenerRunId &&
            current.PathProbeId == probe.PathProbeId && current.PathProbeTransport == probe.PathProbeTransport &&
            current.PathProbeAckedUtcMs == 0 && !current.RouteCommitted &&
            current.PathProbeState == (failed ? RuntimeUnlockPathProbeState.Failed : RuntimeUnlockPathProbeState.Started) &&
            (failed ? current.State == RuntimeUnlockTransactionState.Failed : !current.IsTerminal);
    }

    private bool IsInitialActivationOfferProofForProbeUnsafe(RuntimeUnlockRouteCommitSnapshot probe)
        => runtimeUnlockOfferProofState is { AuthorityMode: RuntimeUnlockOfferAuthorityMode.InitialActivation } proof &&
            proof.SessionId == probe.SessionId && proof.Generation == probe.OfferGeneration;

    private void PublishRuntimeUnlockInitialActivationPreCommitProbeFailure(
        RuntimeUnlockInitialActivationPreCommitProbeFailureObservation observation)
    {
        if (!IsRuntimeUnlockInitialActivationPreCommitProbeFailureCurrent(observation))
        {
            return;
        }

        var args = new RuntimeUnlockInitialActivationPreCommitProbeFailureObservedEventArgs(observation);
        runtimeUnlockInitialActivationPreCommitProbeFailureObserved?.Invoke(this, args);
        LocalOperationalLog.Info("NKN.Tuna",
            $"event=runtime_unlock_initial_activation_precommit_probe_sidecar_failure_observed; {FormatFileTransferRouteTransitionAuthorityTuple(observation.Authority)}; transaction_generation={observation.Probe.TransactionGeneration}; offer_generation={observation.Probe.OfferGeneration}; launch_generation={observation.Probe.LaunchGeneration}; tuna_path_lease_generation={observation.Probe.TunaPathLeaseGeneration}; probe_id={SanitizeLogToken(observation.Probe.PathProbeId)}; data_session_identity={SanitizeLogToken(observation.SourceDataSessionIdentity)}; data_session_generation={observation.SourceDataSessionGeneration}; sidecar_reason={SanitizeLogToken(observation.SidecarReason)}; decision_status={args.Decision.Status}; decision_reason={SanitizeLogToken(args.Decision.Reason)}; authority_effect=none");
    }
}
