namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private readonly FrontierAppLifetime? frontierAppLifetime;
    private readonly FrontierProducerRegistry.ServiceScope? frontierProducerScope;

    private SessionFileTransferService(FrontierAppLifetime? lifetime) : this()
    {
        frontierAppLifetime = lifetime;
        frontierProducerScope = lifetime?.RegisterService();
    }

    // Only the desktop-owned service opts in. Ordinary standalone services do
    // not discover a process owner or create diagnostic observations implicitly.
    internal static SessionFileTransferService CreateForApp()
        => new(FrontierAppLifetime.ServiceOwner);

    private CoordinatorSnapshotStreamEmitter CreateCoordinatorEmitter()
        => frontierProducerScope?.CreateEmitter() as CoordinatorSnapshotStreamEmitter ?? new();

    private void TryAdmitFrontierRepairObservation(InboundTransferContext context,
        FallbackFrontierRepairContract? contract, IReadOnlyList<(int ChunkIndex, byte[] ChunkBytes)> chunks)
    {
        if (frontierAppLifetime is null || contract is null) { return; }
        IFileTransferDataSession? dataSession;
        long dataGeneration;
        int frontier;
        lock (gate)
        {
            if (context.FrontierAdmissionAttempted || context.FrontierBoundaryObservation is not null || !Eligible()) { return; }
            context.FrontierAdmissionAttempted = true;
            dataSession = context.DataSession;
            dataGeneration = context.DataSessionGeneration;
            frontier = context.NextChunkIndex;
        }

        // No native lease checks, catalog admission or producer construction
        // under the service gate. Failure does not alter the authorized write.
        var observation = frontierAppLifetime.TryAdmitService(gate) as FrontierBoundaryObservation;
        lock (gate)
        {
            if (observation is not null && !observation.IsClosed && Eligible() &&
                context.FrontierBoundaryObservation is null && ReferenceEquals(dataSession, context.DataSession) &&
                dataGeneration == context.DataSessionGeneration && frontier == context.NextChunkIndex)
            { context.FrontierBoundaryObservation = observation; }
            else { observation?.Close("EvidenceIncomplete"); }
        }

        // Scheduling evidence only: the existing receive path already selected
        // this coordinator-issued contract. This predicate grants no authority.
        bool Eligible() => ReferenceEquals(inboundTransfer, context) && !context.IsTerminal &&
            !context.UserPaused && !context.PeerPaused && !context.PullTransportPaused &&
            context.RouteSelection.Route == FileTransferRoute.PostTunaFallbackV6 &&
            context.NegotiatedDataProtocolVersion == FileTransferProtocol.ProtocolVersionV6 &&
            context.DataSession is not null && context.DataSessionGeneration > 0 &&
            ReferenceEquals(context.CurrentFallbackFrontierRepairContract, contract) && !contract.Lifecycle.IsSettled &&
            chunks.Any(chunk => chunk.ChunkIndex == context.NextChunkIndex);
    }
}
