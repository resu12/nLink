namespace NLink.Core.FileTransfer;

internal static partial class FileTransferCoordinator
{
    internal const string PostFallbackProbeRollbackRetryRetirementReason =
        "retry_dispatch_post_fallback_probe_rollback";

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool IsPostFallbackProbeRollbackRetryRetirementCurrent(FileTransferCoordinatorState state)
    {
        var lifecycle = state.PersistentState.RuntimeUnlockRetryLifecycle;
        var tail = state.PersistentState.FallbackTailYield;
        var transition = state.PersistentState.FileTransferRouteTransition;
        if (state.IsTerminal || state.Direction != FileTransferDirection.Outbound ||
            state.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            state.CurrentRoutePublicationToken is not { } publication ||
            state.CurrentRuntimeUnlockPendingPreCommitProbe is not null ||
            lifecycle.PendingAttempt is not null || lifecycle.InstalledAttempt is not null ||
            lifecycle.RetainedPredecessor is not null || lifecycle.BoundedClosure is not null ||
            lifecycle.LastTerminalAttempt is not
            {
                State: RuntimeUnlockRetryLifecycleAttemptState.Settled,
                Settlement.Outcome: RuntimeUnlockRetryDispatchSettlementOutcome.DispatchObserved,
                InstallReceipt: { } install,
                DispatchObserved: true,
            } settled ||
            tail.CurrentOperation is not
            {
                PairingState: FileTransferFallbackTailYieldPairingState.ReleaseConsumed,
                PairedRelease: { } release,
            } pair ||
            tail.LastConsumingInstallReceipt != install ||
            tail.LastConsumedReservation is not { } consumed ||
            consumed.Release != release || consumed.RetryAuthority != settled.Authority ||
            transition.CurrentOperation is not null ||
            transition.LastTerminalOperation is not
            {
                State: FileTransferRouteTransitionState.Invalidated,
                Authority.Kind: FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4,
                Authority.ProofBoundary: FileTransferTunaActivationTransitionProofBoundary activation,
                DispositionReceipt: { Status: FileTransferRouteTransitionDispositionApplyStatus.Applied } disposition,
            } rolledBack)
        {
            return false;
        }

        return activation.EffectiveRetryInstallReceipt == install &&
            activation.FallbackTailPairedReleaseReceipt == release &&
            rolledBack.Authority.SourceRoutePublication == publication &&
            disposition.Command == rolledBack.DispositionCommand &&
            disposition.Command.Authority == rolledBack.Authority &&
            disposition.Command.Kind == FileTransferRouteTransitionDispositionKind.RolledBack &&
            disposition.Command.Reason == "precommit_probe_failed_runtime_unlock_tuna_probe_sidecar_closed" &&
            install.Authority.SourcePublicationAuthority == publication &&
            IsRoutePublicationTokenCurrent(state, publication, out _) &&
            IsFallbackTailYieldScopeCurrent(state, GetEffectiveFallbackTailYieldScope(pair), out _);
    }
}
