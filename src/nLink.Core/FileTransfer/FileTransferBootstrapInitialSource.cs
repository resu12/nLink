namespace NLink.Core.FileTransfer;

// An explicit source alternative, not permission to skip hold/drain or peer proof.
internal sealed record FileTransferInitialTunaFallbackSource(
    RoutePublicationToken Publication, string DataSessionIdentity, long DataSessionGeneration)
{
    public bool IsDefault => Publication.IsDefault || Publication.RuntimeOwner.IsDefault ||
        Publication.Route != FileTransferRoute.FileTunaV4 || Publication.ProtocolVersion != 4 ||
        Publication.ActionKind != FileTransferCoordinatorActionKind.RoutePublication ||
        Publication.PublicationGeneration != 1 || Publication.RouteCommitGeneration is not null ||
        Publication.LiveRouteEpoch != 0 || Publication.TerminalGeneration != 0 ||
        string.IsNullOrWhiteSpace(DataSessionIdentity) || DataSessionGeneration <= 0;
}

internal static partial class FileTransferCoordinator
{
    internal static FileTransferInitialTunaFallbackSource? CaptureInitialTunaFallbackSource(FileTransferCoordinatorState state)
    {
        var transition = state.PersistentState.FileTransferRouteTransition;
        if (transition.CurrentOperation is not null ||
            transition.PendingSupersession is not null || transition.LastTerminalOperation is not null ||
            transition.LastTransitionGeneration != 0)
        {
            return null;
        }
        return CaptureInitialTunaFallbackPublicationSource(state);
    }

    private static FileTransferInitialTunaFallbackSource? CaptureInitialTunaFallbackPublicationSource(FileTransferCoordinatorState state)
    {
        if (state.CurrentRouteCommit is not null || CaptureFallbackBootstrapSourceScope(state) is null) { return null; }
        var source = new FileTransferInitialTunaFallbackSource(state.CurrentRoutePublicationToken!.Value,
            state.CurrentDataSessionIdentity!, state.CurrentDataSessionGeneration);
        return source.IsDefault ? null : source;
    }

    private static bool CanStartInitialTunaFallbackExchange(FileTransferCoordinatorState state)
    {
        var transition = state.PersistentState.FileTransferRouteTransition;
        return transition.BootstrapExchange is null && transition.PendingFallbackV6ProofBootstrap is null &&
            transition.LastFallbackV6ProofBootstrapReceipt is null && transition.LastFallbackV6ProofBootstrapGeneration == 0 &&
            CaptureInitialTunaFallbackSource(state) is not null;
    }

    private static bool IsInitialTunaFallbackCommandSourceCurrent(FileTransferCoordinatorState state,
        FileTransferFallbackV6ProofBootstrapCommand command)
        => command.InitialSource is { IsDefault: false } source && command.SourceRouteCommit is null &&
            source == CaptureInitialTunaFallbackPublicationSource(state) && source.Publication == command.SourceRoutePublication &&
            source.Publication.RuntimeOwner == command.SourceRuntimeOwner && source.DataSessionGeneration == command.SourceDataSessionGeneration &&
            command.SourceLiveRouteEpoch == 0 && command.SourceTerminalGeneration == 0 &&
            IsInitialTunaFallbackTransitionHistoryCurrent(state, command);

    private static bool IsInitialTunaFallbackTransitionHistoryCurrent(FileTransferCoordinatorState state,
        FileTransferFallbackV6ProofBootstrapCommand command)
    {
        var transition = state.PersistentState.FileTransferRouteTransition;
        if (transition.PendingSupersession is not null || transition.LastTerminalOperation is not null) { return false; }
        if (transition.CurrentOperation is null) { return transition.LastTransitionGeneration == 0; }
        // Only this completed exchange's first, still-live transition can retain the source.
        // Do not call exchange currentness here: it calls command source currentness.
        return transition.CurrentOperation is
            { State: FileTransferRouteTransitionState.Prepared or FileTransferRouteTransitionState.Draining, Authority: var authority } &&
            transition.LastTransitionGeneration == 1 && authority.TransitionGeneration == 1 &&
            authority.SourceRoute == FileTransferRoute.FileTunaV4 && authority.SourceProtocolVersion == 4 &&
            authority.SourceTransport == FileTransferTransportKind.Tuna &&
            authority.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
            authority.PredecessorTransitionAuthorityId is null &&
            IsFileTransferFallbackV6ProofBootstrapTransitionAuthorityScope(authority, command) &&
            authority.ProofBoundary is FileTransferFallbackTransitionProofBoundary { IsDefault: false } proof &&
            transition.BootstrapExchange?.Bootstrap == command &&
            IsFallbackBootstrapProofBoundToExchange(transition.BootstrapExchange,
                transition.LastFallbackV6ProofBootstrapReceipt, proof, authority.DurableCheckpoint);
    }

    private static bool IsInitialTunaFallbackTransitionSourceCurrent(FileTransferCoordinatorState state,
        in FileTransferRouteTransitionAuthority authority)
        => authority.ProofBoundary is FileTransferFallbackTransitionProofBoundary { InitialSource: { } } proof &&
            IsInitialTunaFallbackCurrentTransitionAuthority(state, authority) &&
            IsFallbackBootstrapProofCurrent(state, proof, authority.DurableCheckpoint);

    // Unwind the large persistent-state value copy before validating the exchange.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static bool IsInitialTunaFallbackCurrentTransitionAuthority(FileTransferCoordinatorState state,
        in FileTransferRouteTransitionAuthority authority)
        => state.PersistentState.FileTransferRouteTransition.CurrentOperation?.Authority == authority;
}
