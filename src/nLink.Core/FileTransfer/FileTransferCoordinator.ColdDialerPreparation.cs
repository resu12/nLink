namespace NLink.Core.FileTransfer;

internal static partial class FileTransferCoordinator
{
    internal static RuntimeUnlockPostFallbackPairedRetryOfferBindingDecision BindOutboundFallbackDialerPreparation(
        FileTransferCoordinatorState state, RuntimeUnlockPostFallbackPairedRetryOfferBindingRequest request,
        RuntimeUnlockPostFallbackPairedRetryOfferBindingReceipt? previous)
    {
        var tail = GetFallbackTailYieldSubstate(state);
        if (request.IsDefault || request.PublicationAuthority.Direction != FileTransferDirection.Outbound ||
            state.Direction != FileTransferDirection.Outbound || state.IsTerminal ||
            state.CurrentRoutePublicationToken is not { } publication ||
            !MatchesRoutePublicationAuthority(request.PublicationAuthority, publication) ||
            !IsRoutePublicationTokenCurrent(state, publication, out _) ||
            !RuntimeUnlockDataSessionScopeMatches(state, request.DataSessionIdentity,
                request.DataSessionGeneration, request.DataSessionGenerationKind) ||
            GetRuntimeUnlockRetryLifecycle(state).PendingScopeInvalidationCommand is not null ||
            tail.CurrentOperation is not { PairedRelease: { } release } operation ||
            !TryGetCurrentFallbackTailYieldOperationScope(state, operation.Decision,
                out var current, out var scope, out _) || current != operation ||
            scope.TransportEpoch != request.TransportEpoch ||
            scope.BridgeRecoveryGeneration != request.BridgeRecoveryGeneration ||
            scope.CheckpointRequestIdSnapshot != request.CheckpointRequestId ||
            scope.DataSessionIdentity != state.CurrentDataSessionIdentity ||
            scope.DataSessionGeneration != state.CurrentDataSessionGeneration)
            return new(RuntimeUnlockPostFallbackPairedRetryOfferBindingStatus.Stale,
                "outbound_dialer_preparation_source_not_current");

        var receipt = new RuntimeUnlockPostFallbackPairedRetryOfferBindingReceipt(request, default, release, scope);
        // A retry install may consume this same pair while the authenticated
        // answer is in flight. Only the previously bound exact receipt survives
        // that transition, and only under the consuming coordinator install.
        var consumedByCurrentInstall = previous == receipt &&
            operation.PairingState == FileTransferFallbackTailYieldPairingState.ReleaseConsumed &&
            tail.LastConsumingInstallReceipt is { } install &&
            GetRuntimeUnlockRetryLifecycle(state).InstalledAttempt?.InstallReceipt == install &&
            MatchesRoutePublicationAuthority(install.Request.SourcePublicationAuthority, publication);
        if (receipt.IsDefault ||
            operation.PairingState != FileTransferFallbackTailYieldPairingState.Paired && !consumedByCurrentInstall)
            return new(RuntimeUnlockPostFallbackPairedRetryOfferBindingStatus.Rejected,
                "outbound_dialer_preparation_pair_not_current");

        return new(previous == receipt ? RuntimeUnlockPostFallbackPairedRetryOfferBindingStatus.Idempotent :
            RuntimeUnlockPostFallbackPairedRetryOfferBindingStatus.Accepted,
            "outbound_dialer_preparation_bound_no_retry_authority", receipt);
    }
}
