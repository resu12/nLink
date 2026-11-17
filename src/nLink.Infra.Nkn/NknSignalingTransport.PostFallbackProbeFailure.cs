using NLink.Core.FileTransfer;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    private FileTransferRouteTransitionPauseBindingShadow? GetRuntimeUnlockPostFallbackProbeFailureBindingUnsafe()
    {
        // Answer acceptance clears offer bookkeeping before the probe finishes.
        // Only the coordinator-issued current pause binding owns that probe;
        // an install shadow or a retired offer alone cannot replace this proof.
        var shadow = fileTransferRouteTransitionExecutionShadow;
        if (disposed || shadow.Current is not { } binding ||
            shadow.RetainedPredecessor is not null || shadow.PendingAvailabilityDisposition is not null ||
            binding.Receipt.Status != FileTransferRouteTransitionPauseBindStatus.Installed ||
            binding.Receipt.Command != binding.Command ||
            binding.Command.Authority is not
            {
                Kind: FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4,
                Direction: FileTransferDirection.Outbound,
                ProofBoundary: FileTransferTunaActivationTransitionProofBoundary
                { IsDefault: false, EffectiveRetryInstallReceipt: { IsDefault: false } installed } activation,
            } authority ||
            runtimeUnlockPreCommitProbeStartedSnapshot is not { } probe ||
            !runtimeUnlockTunaPathLeaseState.IsCurrent ||
            !runtimeUnlockTransactionState.TunaPathLeaseCurrent ||
            !RuntimeUnlockInitialActivationFailureProbeMatchesTransactionUnsafe(probe, failed: false) ||
            authority.SessionId != probe.SessionId || authority.TransferId != probe.TransferId ||
            activation.LaunchGeneration != probe.LaunchGeneration ||
            activation.AcceptedLaunchBoundary.TransactionGeneration != probe.TransactionGeneration ||
            activation.AcceptedLaunchBoundary.OfferGeneration != probe.OfferGeneration ||
            activation.AcceptedLaunchBoundary.TunaPathLeaseGeneration != probe.TunaPathLeaseGeneration ||
            activation.AcceptedLaunchBoundary.TunaPathLeaseListenerRunId != probe.TunaPathLeaseListenerRunId ||
            !FileTransferCoordinator.MatchesRoutePublicationAuthority(
                installed.Request.SourcePublicationAuthority, authority.SourceRoutePublication))
        {
            return null;
        }

        return binding;
    }
}
