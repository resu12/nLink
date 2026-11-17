namespace NLink.Core.FileTransfer;

internal static partial class FileTransferCoordinator
{
    // The caller must also hold the exact current transition execution binding.
    // A publication observation alone cannot authorize session projection retirement.
    internal static bool AuthorizesFileFallbackProjectionRetirement(
        FileTransferRouteTransitionDispositionCommand command,
        RoutePublicationToken fallbackPublication)
    {
        var authority = command.Authority;
        if (!IsFileTransferRouteTransitionDispositionCommandValid(command) ||
            command.Kind != FileTransferRouteTransitionDispositionKind.Committed ||
            authority.Kind != FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4 ||
            authority.SourceRoute != FileTransferRoute.PostTunaFallbackV6 || authority.SourceProtocolVersion != 6 ||
            authority.SourceTransport != FileTransferTransportKind.RegularNkn ||
            authority.TargetRoute != FileTransferRoute.FileTunaV4 || authority.TargetProtocolVersion != 4 ||
            authority.TargetTransport != FileTransferTransportKind.Tuna ||
            authority.HandoffKind != FileTransferTransportHandoffKind.NormalToTunaActivation ||
            authority.SourceRoutePublication != fallbackPublication ||
            fallbackPublication.Route != FileTransferRoute.PostTunaFallbackV6 || fallbackPublication.ProtocolVersion != 6 ||
            fallbackPublication.RuntimeOwner != authority.SourceRuntimeOwner ||
            fallbackPublication.SessionId != authority.SessionId || fallbackPublication.TransferId != authority.TransferId ||
            fallbackPublication.Direction != authority.Direction ||
            fallbackPublication.TerminalGeneration != authority.SourceTerminalGeneration ||
            fallbackPublication.LiveRouteEpoch != authority.SourceLiveRouteEpoch ||
            fallbackPublication.LegGeneration != authority.SourceLegGeneration ||
            command.RoutePublicationToken is not { } publication || command.RouteCommitToken is not { } commit)
        {
            return false;
        }

        return publication.SessionId == authority.SessionId && publication.TransferId == authority.TransferId &&
            publication.Direction == authority.Direction &&
            publication.RuntimeOwner.SessionId == authority.SessionId &&
            publication.RuntimeOwner.TransferId == authority.TransferId && publication.RuntimeOwner.Direction == authority.Direction &&
            publication.TerminalGeneration == authority.SourceTerminalGeneration &&
            publication.RuntimeOwner.TerminalGeneration == authority.SourceTerminalGeneration &&
            publication.RuntimeGeneration == publication.RuntimeOwner.RuntimeGeneration &&
            publication.RuntimeGeneration > authority.SourceRuntimeOwner.RuntimeGeneration &&
            publication.LiveRouteEpoch == publication.RuntimeOwner.LiveRouteEpoch &&
            publication.LiveRouteEpoch > authority.SourceLiveRouteEpoch &&
            publication.LegGeneration > authority.SourceLegGeneration &&
            publication.PublicationGeneration > fallbackPublication.PublicationGeneration &&
            commit.SessionId == publication.SessionId && commit.TransferId == publication.TransferId &&
            commit.Direction == publication.Direction && commit.TerminalGeneration == publication.TerminalGeneration &&
            commit.RuntimeGeneration == publication.RuntimeGeneration && commit.LiveRouteEpoch == publication.LiveRouteEpoch &&
            commit.LegGeneration == publication.LegGeneration;
    }
}
