namespace NLink.Core.FileTransfer;

// Local execution evidence captured with a transition pause, never route authority.
internal sealed record FileTransferFallbackProofEpisode(
    FileTransferRouteTransitionAuthority Transition,
    long PendingGeneration,
    long FallbackEpoch);

internal static partial class FileTransferCoordinator
{
    // The caller must have accepted this exact current pause disposition with no
    // retained predecessor, then revalidate its applied receipt before local effects.
    // The committed disposition authorizes cleanup; the episode only identifies its local target.
    internal static bool AuthorizesFallbackProofEpisodeCleanup(
        FileTransferRouteTransitionDispositionCommand command, FileTransferFallbackProofEpisode episode)
    {
        var authority = command.Authority;
        return episode.Transition == authority && episode.PendingGeneration > 0 && episode.FallbackEpoch > 0 &&
            IsFileTransferRouteTransitionDispositionCommandValid(command) &&
            command.Kind == FileTransferRouteTransitionDispositionKind.Committed &&
            authority.TargetRoute == FileTransferRoute.PostTunaFallbackV6 && authority.TargetProtocolVersion == 6 &&
            authority.TargetTransport == FileTransferTransportKind.RegularNkn &&
            authority.HandoffKind == FileTransferTransportHandoffKind.TunaToNormalFallback &&
            authority.ProofBoundary is FileTransferFallbackTransitionProofBoundary fallback &&
            fallback.PeerProofEvidence.SessionId == authority.SessionId &&
            fallback.PeerProofEvidence.TransferId == authority.TransferId &&
            fallback.PeerProofEvidence.Direction == authority.Direction &&
            fallback.PeerProofEvidence.TransportEpoch == fallback.TargetTransportEpoch &&
            command.RoutePublicationToken is { } publication &&
            publication.SessionId == authority.SessionId && publication.TransferId == authority.TransferId &&
            publication.Direction == authority.Direction &&
            publication.TerminalGeneration == authority.SourceTerminalGeneration &&
            publication.RuntimeOwner.SessionId == authority.SessionId &&
            publication.RuntimeOwner.TransferId == authority.TransferId && publication.RuntimeOwner.Direction == authority.Direction &&
            publication.RuntimeOwner.TerminalGeneration == publication.TerminalGeneration &&
            publication.RuntimeOwner.RuntimeGeneration == publication.RuntimeGeneration &&
            publication.RuntimeOwner.LiveRouteEpoch == publication.LiveRouteEpoch &&
            publication.RuntimeGeneration > authority.SourceRuntimeOwner.RuntimeGeneration &&
            publication.LiveRouteEpoch > authority.SourceLiveRouteEpoch &&
            publication.LegGeneration > authority.SourceLegGeneration &&
            publication.PublicationGeneration > authority.SourceRoutePublication.PublicationGeneration;
    }
}
