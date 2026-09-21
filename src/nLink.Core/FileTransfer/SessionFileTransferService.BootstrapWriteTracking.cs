using System.Collections;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private sealed record InboundBootstrapWriteIngress(IFileTransferDataSession Session, long ProviderGeneration, long ServiceGeneration,
        bool RequiresSourceBinding);

    // Execution evidence only; neither the source binding nor chunk membership grants hold/route authority.
    private sealed record InboundBootstrapWriteSource(
        InboundTransferContext Context, string CoordinatorInstanceId, FileTransferBootstrapAgreement Agreement,
        FileTransferBootstrapLocalStopScope Source, InboundBootstrapWriteIngress Ingress,
        Stream Stream, BitArray Written, int ChunkSize, int ChunkCount, long FileSize,
        bool TransportPaused, DateTimeOffset? PausedSinceUtc)
    {
        public InboundBootstrapWriteOperation? Operation { get; set; }
    }

    private sealed record InboundBootstrapWriteOperation(long Generation, InboundBootstrapWriteSource Source,
        IReadOnlyList<(int ChunkIndex, byte[] ChunkBytes)> Chunks)
    {
        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    internal static Func<string, Task>? BootstrapWriteBeforeCommitForTests { get; set; }
    internal static Func<string, Task>? BootstrapWriteBeforeFinalizationForTests { get; set; }

    private static InboundBootstrapWriteIngress? CaptureInboundBootstrapWriteIngress(InboundTransferContext context,
        IFileTransferDataSession? session)
        => ReferenceEquals(context.DataSession, session) && session is IFileTransferDataSessionGenerationProvider { Generation: > 0 } provider
            ? new(session, provider.Generation, context.DataSessionGeneration,
                context.RouteRuntime.UsesFileTunaV4Runtime &&
                context.CoordinatorPersistentState.FileTransferRouteTransition.BootstrapAgreement?.Accept?.FallbackBootstrapCorrelationVersion == 1) : null;

    private bool TryCaptureInboundBootstrapWriteSourceLocked(InboundTransferContext context,
        InboundBootstrapWriteIngress? ingress, out InboundBootstrapWriteSource? source)
    {
        source = null;
        var state = CreateCoordinatorStateLocked(context);
        if (!context.RouteRuntime.UsesFileTunaV4Runtime || !UsesBootstrapCorrelation(state)) { return ingress?.RequiresSourceBinding != true; }
        if (HasBootstrapSourceHoldLocked(context)) { return false; }
        if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal || context.LifetimeCts.IsCancellationRequested || context.UserPaused || context.PeerPaused ||
            ingress is null || !ReferenceEquals(context.DataSession, ingress.Session) || context.DataSessionGeneration != ingress.ServiceGeneration ||
            ingress.Session is not IFileTransferDataSessionGenerationProvider provider || provider.Generation != ingress.ProviderGeneration ||
            context.WriteStream is not { } stream || context.ReceiverSparseChunksWritten is not { } written ||
            FileTransferCoordinator.CaptureFallbackBootstrapSourceScope(state) is not { } scope ||
            state.PersistentState.FileTransferRouteTransition.BootstrapAgreement is not { IsEnabled: true } agreement)
        {
            return false;
        }
        source = new(context, context.CoordinatorInstanceId, agreement, scope, ingress, stream, written,
            context.ChunkSizeBytes, context.ChunkCount, context.FileSizeBytes, context.PullTransportPaused, context.PullTransportPausedSinceUtc);
        return true;
    }

    private bool IsInboundBootstrapWriteSourceCurrentLocked(InboundTransferContext context, InboundBootstrapWriteSource source)
    {
        if (!ReferenceEquals(inboundTransfer, context) || !ReferenceEquals(source.Context, context) || context.IsTerminal ||
            context.LifetimeCts.IsCancellationRequested || context.UserPaused || context.PeerPaused || context.CoordinatorInstanceId != source.CoordinatorInstanceId ||
            !ReferenceEquals(context.DataSession, source.Ingress.Session) || context.DataSessionGeneration != source.Ingress.ServiceGeneration ||
            context.DataSession is not IFileTransferDataSessionGenerationProvider provider || provider.Generation != source.Ingress.ProviderGeneration ||
            !ReferenceEquals(context.WriteStream, source.Stream) || !ReferenceEquals(context.ReceiverSparseChunksWritten, source.Written) ||
            context.ChunkSizeBytes != source.ChunkSize || context.ChunkCount != source.ChunkCount || context.FileSizeBytes != source.FileSize ||
            ((context.PullTransportPaused != source.TransportPaused || context.PullTransportPausedSinceUtc != source.PausedSinceUtc) &&
                !IsBootstrapHoldCapturedWritePauseCurrentLocked(context, source)) ||
            source.Operation is { } operation &&
                (!context.ActiveBootstrapWriteOperations.TryGetValue(operation.Generation, out var active) || !ReferenceEquals(active, operation)))
        {
            return false;
        }
        var state = CreateCoordinatorStateLocked(context);
        return state.PersistentState.FileTransferRouteTransition.BootstrapAgreement == source.Agreement &&
            FileTransferCoordinator.IsFallbackBootstrapLocalStopScopeCurrent(state, source.Source);
    }

    private static void RegisterInboundBootstrapWriteLocked(InboundTransferContext context, InboundBootstrapWriteSource source,
        IReadOnlyList<(int ChunkIndex, byte[] ChunkBytes)> chunks)
    {
        var operation = new InboundBootstrapWriteOperation(checked(++context.LastBootstrapWriteOperationGeneration), source, chunks);
        source.Operation = operation;
        context.ActiveBootstrapWriteOperations.Add(operation.Generation, operation);
        foreach (var (index, _) in chunks) { context.BootstrapPendingWriteOwners[index] = operation; }
    }

    private static bool OwnsInboundBootstrapWriteChunks(InboundTransferContext context, InboundBootstrapWriteOperation operation)
        => operation.Chunks.All(chunk => context.BootstrapPendingWriteOwners.TryGetValue(chunk.ChunkIndex, out var owner) && ReferenceEquals(owner, operation));

    private static bool HasUnsettledInboundBootstrapWrites(InboundTransferContext context)
        => context.ActiveBootstrapWriteOperations.Count != 0;

    private async Task HandleInboundV4ChunkBatchFromReceiveAsync(InboundTransferContext context,
        FileTransferChunkBatchFrameV4 batch, FileTransferTransportKind receivedTransportKind, InboundBootstrapWriteIngress? ingress)
    {
        InboundBootstrapWriteSource? source;
        lock (gate) { if (!TryCaptureInboundBootstrapWriteSourceLocked(context, ingress, out source)) { return; } }
        if (source is not null && batch is FileTransferChunkBatchFrameV6) { return; }
        try { await HandleInboundV4ChunkBatchCoreAsync(context, batch, receivedTransportKind, source).ConfigureAwait(false); }
        finally
        {
            if (source?.Operation is { } operation)
            {
                try
                {
                    if (BootstrapWriteBeforeFinalizationForTests is { } barrier) { await barrier(context.TransferId).ConfigureAwait(false); }
                }
                finally
                {
                    InboundFallbackV6ProofBootstrapPendingWriteWake? wake = null;
                    lock (gate)
                    {
                        foreach (var (index, bytes) in operation.Chunks)
                        {
                            if (context.BootstrapPendingWriteOwners.TryGetValue(index, out var owner) && ReferenceEquals(owner, operation))
                            {
                                context.BootstrapPendingWriteOwners.Remove(index);
                                if (ReferenceEquals(context.ReceiverSparseChunksWritten, source.Written) && context.ReceiverSparseChunksPendingWrite.Remove(index))
                                {
                                    context.BufferedBytes = Math.Max(0, context.BufferedBytes - bytes.Length);
                                }
                            }
                        }
                        if (context.ActiveBootstrapWriteOperations.TryGetValue(operation.Generation, out var active) && ReferenceEquals(active, operation))
                        {
                            context.ActiveBootstrapWriteOperations.Remove(operation.Generation);
                        }
                        operation.Settled.TrySetResult();
                        if (ReferenceEquals(inboundTransfer, context) && IsInboundBootstrapWriteSourceCurrentWithoutOperationLocked(context, source))
                        {
                            wake = TakeInboundFallbackV6ProofBootstrapPendingWriteWakeAfterDrainLocked(context, out _, out _, out _);
                        }
                    }
                    if (wake is not null) { _ = ProcessFallbackV6ProofBootstrapPendingWriteRecheckAsync(wake); }
                }
            }
        }
    }

    private bool IsInboundBootstrapWriteSourceCurrentWithoutOperationLocked(InboundTransferContext context, InboundBootstrapWriteSource source)
        => IsInboundBootstrapWriteSourceCurrentLocked(context, source with { Operation = null });
}
