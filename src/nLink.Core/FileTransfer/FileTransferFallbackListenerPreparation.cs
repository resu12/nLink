namespace NLink.Core.FileTransfer;

// Permission for revocable physical listener startup, including exact cleanup
// of the prior dialer while this fallback publication remains current. It grants
// no retry, runtime unlock, route, fallback-release, or publication authority.
internal sealed record FileTransferFallbackListenerPreparationPermit(
    RoutePublicationToken Publication, Guid SecurityIncarnation, DateTimeOffset ExpiresUtc);

internal static partial class FileTransferCoordinator
{
    internal static FileTransferFallbackListenerPreparationPermit? CreateFallbackListenerPreparationPermit(
        FileTransferCoordinatorState state, Guid securityIncarnation,
        DateTimeOffset approvalExpiresUtc, DateTimeOffset now)
        => securityIncarnation != Guid.Empty && approvalExpiresUtc > now && !state.IsTerminal &&
           state.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
           state.CurrentRoutePublicationToken is { } publication &&
           publication.ProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
           IsRoutePublicationTokenCurrent(state, publication, out _)
            ? new(publication, securityIncarnation, approvalExpiresUtc) : null;

    internal static bool IsFallbackListenerPreparationPermitCurrent(
        FileTransferCoordinatorState state, FileTransferFallbackListenerPreparationPermit permit,
        Guid securityIncarnation, DateTimeOffset now)
        => permit.SecurityIncarnation == securityIncarnation && now < permit.ExpiresUtc &&
           !state.IsTerminal && state.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
           state.CurrentRoutePublicationToken == permit.Publication &&
           IsRoutePublicationTokenCurrent(state, permit.Publication, out _);
}
