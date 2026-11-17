namespace NLink.Core.FileTransfer;

internal static partial class FileTransferCoordinator
{
    private static bool IsResponderPairedReleaseCurrent(
        FileTransferCoordinatorState state,
        RuntimeUnlockResponderPeerProbeRouteTransitionObservation observation,
        FileTransferFallbackTailYieldOperation operation)
    {
        var tail = state.PersistentState.FallbackTailYield;
        if (operation.PairedRelease is not { IsDefault: false } release ||
            tail.CurrentReservation is not null)
            return false;

        if (observation.ConsumingRetryInstallReceipt is not { } receipt)
            return operation.PairingState == FileTransferFallbackTailYieldPairingState.Paired &&
                   tail.LastConsumedReservation is null && tail.LastConsumingInstallReceipt is null;

        // Consumption authorizes this exact installed retry, not another release
        // or another dispatch. Revalidate it again at bind, held work and commit.
        var lifecycle = GetRuntimeUnlockRetryLifecycle(state);
        var receiptCurrent = TryGetCurrentRuntimeUnlockRetryDispatchInstallReceipt(state, out var currentReceipt) &&
                             currentReceipt == receipt;
        var attempt = receiptCurrent ? lifecycle.InstalledAttempt ?? lifecycle.LastTerminalAttempt : null;
        if (!receiptCurrent && state.PersistentState.FileTransferRouteTransition.CurrentOperation is
            { State: FileTransferRouteTransitionState.Draining, Authority: var transition } &&
            transition.Kind == FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4 &&
            transition.ProofBoundary is FileTransferTunaActivationTransitionProofBoundary
            { ResponderPeerProbeHandoffAuthority: { } handoff } &&
            handoff == state.PersistentState.RuntimeUnlockResponderPeerProbeRouteTransitionHandoff.CurrentAuthority &&
            handoff.Observation == observation && handoff.PairedRelease == release)
        {
            // The coordinator retires the old retry before committing this exact
            // prepared transition. That receipt is not reusable for a new bind.
            attempt = TryGetExactlyInvalidatedRuntimeUnlockRetryAttempt(lifecycle, receipt);
        }
        return !receipt.IsDefault &&
               observation.ResponderTransitionMode == RuntimeUnlockResponderPeerProbeTransitionMode.PostFallbackV6Paired &&
               string.Equals(observation.AnswerAcceptanceSource, "AuthenticatedAnswer", StringComparison.Ordinal) &&
               observation.TunaPathLeaseListenerRunId?.StartsWith("listener-start-", StringComparison.Ordinal) == true &&
               operation.PairingState == FileTransferFallbackTailYieldPairingState.ReleaseConsumed &&
               tail.LastConsumedReservation is { IsDefault: false } consumed &&
               consumed.Release == release && consumed.RetryAuthority == receipt.Authority &&
               tail.LastConsumingInstallReceipt == receipt &&
               lifecycle.PendingAttempt is null &&
               attempt is { } current && current.InstallReceipt == receipt &&
               RuntimeUnlockRetryDispatchAuthorityMatchesCurrentScope(state, current.Authority, out _) &&
               current.OfferGeneration == observation.OfferGeneration;
    }
}
