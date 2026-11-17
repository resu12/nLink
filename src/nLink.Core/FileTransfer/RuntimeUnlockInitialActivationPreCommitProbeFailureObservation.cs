namespace NLink.Core.FileTransfer;

internal sealed record RuntimeUnlockInitialActivationPreCommitProbeFailureObservation(
    FileTransferRouteTransitionPauseBindReceipt PauseBinding,
    RuntimeUnlockRouteCommitSnapshot Probe,
    IFileTransferDataSession SourceDataSession,
    string SourceDataSessionIdentity,
    long SourceDataSessionGeneration,
    string? SourceCheckpointRequestId,
    int SourceCheckpointGeneration,
    string SidecarReason)
{
    public FileTransferRouteTransitionAuthority Authority => PauseBinding.Command.Authority;

    public bool IsDefault
    {
        get
        {
            var authority = Authority;
            if (authority.IsDefault ||
                authority.Kind != FileTransferRouteTransitionKind.RegularV4ToTunaV4 ||
                authority.Direction != FileTransferDirection.Outbound ||
                authority.SourceRoute != FileTransferRoute.RegularNknV4Fast ||
                authority.SourceProtocolVersion != FileTransferProtocol.ProtocolVersionV4 ||
                authority.SourceTransport != FileTransferTransportKind.RegularNkn ||
                authority.SourceLiveRouteEpoch != 0 ||
                PauseBinding.Status is not (FileTransferRouteTransitionPauseBindStatus.Installed or
                    FileTransferRouteTransitionPauseBindStatus.Idempotent) ||
                authority.ProofBoundary is not FileTransferTunaActivationTransitionProofBoundary
                {
                    EffectiveRetryInstallReceipt: null,
                    FallbackTailPairedReleaseReceipt: null,
                    ResponderPeerProbeHandoffAuthority: null,
                    InitialResponderPeerProbeObservation: null,
                } activation ||
                Probe is null || SourceDataSession is null ||
                SourceDataSessionGeneration != authority.DataSessionGeneration ||
                string.IsNullOrWhiteSpace(SourceDataSessionIdentity) ||
                SourceCheckpointGeneration < 0 || string.IsNullOrWhiteSpace(SidecarReason) ||
                Probe.SessionId != authority.SessionId || Probe.TransferId != authority.TransferId ||
                Probe.LaunchGeneration != activation.LaunchGeneration ||
                Probe.PathProbeState != nameof(RuntimeUnlockPathProbeState.Started) ||
                string.IsNullOrWhiteSpace(Probe.PathProbeId) ||
                Probe.PathProbeTransport != FileTransferTransportKind.Tuna ||
                Probe.PathProbeAckedUtcMs != 0 || Probe.PathProbeFailureReason is not null)
            {
                return true;
            }

            // Starting the probe changes only these three fields of the exact
            // accepted launch. A child/paired/responder launch cannot substitute.
            var accepted = activation.AcceptedLaunchBoundary;
            var started = RuntimeUnlockTransaction.CreateRouteCommitProof(Probe, activation.RuntimeUnlockAdmission);
            return accepted.PathProbeState != RuntimeUnlockPathProbeState.None ||
                (started with
                {
                    PathProbeId = accepted.PathProbeId,
                    PathProbeState = accepted.PathProbeState,
                    PathProbeTransport = accepted.PathProbeTransport,
                }) != accepted;
        }
    }
}

internal enum RuntimeUnlockInitialActivationPreCommitProbeFailureStatus
{
    Accepted,
    Rejected,
    Stale,
}

internal sealed record RuntimeUnlockInitialActivationPreCommitProbeFailureDecision(
    RuntimeUnlockInitialActivationPreCommitProbeFailureStatus Status,
    string Reason);

internal sealed class RuntimeUnlockInitialActivationPreCommitProbeFailureObservedEventArgs(
    RuntimeUnlockInitialActivationPreCommitProbeFailureObservation observation) : EventArgs
{
    public RuntimeUnlockInitialActivationPreCommitProbeFailureObservation Observation { get; } = observation;

    public RuntimeUnlockInitialActivationPreCommitProbeFailureDecision Decision { get; set; } = new(
        RuntimeUnlockInitialActivationPreCommitProbeFailureStatus.Rejected,
        "initial_activation_precommit_probe_failure_subscriber_missing");
}
