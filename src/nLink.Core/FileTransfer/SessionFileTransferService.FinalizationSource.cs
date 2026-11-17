namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    // Captured execution evidence; runtime authority remains the coordinator-issued owner.
    private sealed record InboundFinalizationSource(
        InboundTransferContext Context, RuntimeOwnerToken Owner, RoutePublicationToken? Publication,
        IFileTransferDataSession? Session, long SessionGeneration, long LegGeneration, long TransportEpoch,
        Stream? Stream, FileTransferReceiveDestination? Destination, InboundBootstrapWriteSource? WriteSource);

    private InboundFinalizationSource? CaptureInboundFinalizationSourceLocked(InboundTransferContext context,
        InboundBootstrapWriteSource? writeSource)
    {
        if (context.CurrentRuntimeOwner is not { } owner) { return null; }
        var source = new InboundFinalizationSource(context, owner, context.CurrentRoutePublicationToken,
            context.DataSession, context.DataSessionGeneration, context.CurrentTransferLeg?.Generation ?? 0,
            context.CurrentTransferLeg?.TransportEpochId ?? 0, context.WriteStream, context.WriteDestination, writeSource);
        return IsInboundFinalizationSourceCurrentLocked(context, source) ? source : null;
    }

    private bool IsInboundFinalizationSourceCurrentLocked(InboundTransferContext context, InboundFinalizationSource source)
        => ReferenceEquals(inboundTransfer, context) && ReferenceEquals(source.Context, context) &&
           !context.IsTerminal && !context.LifetimeCts.IsCancellationRequested &&
           ReferenceEquals(context.WriteStream, source.Stream) && ReferenceEquals(context.WriteDestination, source.Destination) &&
           ReferenceEquals(context.DataSession, source.Session) && context.DataSessionGeneration == source.SessionGeneration &&
           context.CurrentRoutePublicationToken == source.Publication &&
           (context.CurrentTransferLeg?.Generation ?? 0) == source.LegGeneration &&
           (context.CurrentTransferLeg?.TransportEpochId ?? 0) == source.TransportEpoch &&
           FileTransferCoordinator.IsRuntimeOwnerCurrent(CreateCoordinatorStateLocked(context), source.Owner, out _) &&
           (source.WriteSource is null || IsInboundBootstrapWriteSourceCurrentLocked(context, source.WriteSource));
}
