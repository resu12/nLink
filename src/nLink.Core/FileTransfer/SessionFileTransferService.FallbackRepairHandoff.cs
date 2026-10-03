using System.Collections.Immutable;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private sealed record InboundFallbackRepairHandoffExecution(
        FileTransferFallbackRepairHandoffCapture Capture,
        InboundBootstrapWriteSource Source,
        DateTimeOffset PausedSinceUtc,
        InboundBootstrapWriteOperation[] Writes,
        InboundFallbackRepairFeedbackOperation[] Feedback);

    private static bool HasInboundFallbackRepairHandoffLocked(InboundTransferContext context)
        => context.CoordinatorPersistentState.FallbackTailYield.RepairHandoff is
            { State: FileTransferFallbackRepairHandoffState.Prepared or FileTransferFallbackRepairHandoffState.Captured or FileTransferFallbackRepairHandoffState.Drained };

    private bool IsInboundFallbackRepairHandoffExecutionCurrentLocked(InboundTransferContext context,
        InboundFallbackRepairHandoffExecution execution)
    {
        var source = execution.Source;
        return ReferenceEquals(inboundTransfer, context) && ReferenceEquals(context.FallbackRepairHandoffExecution, execution) &&
            !context.IsTerminal && !context.LifetimeCts.IsCancellationRequested && !context.UserPaused && !context.PeerPaused &&
            context.CoordinatorInstanceId == source.CoordinatorInstanceId &&
            ReferenceEquals(context.DataSession, source.Ingress.Session) && context.DataSessionGeneration == source.Ingress.ServiceGeneration &&
            context.DataSession is IFileTransferDataSessionGenerationProvider provider && provider.Generation == source.Ingress.ProviderGeneration &&
            ReferenceEquals(context.WriteStream, source.Stream) && ReferenceEquals(context.ReceiverSparseChunksWritten, source.Written) &&
            context.ChunkSizeBytes == source.ChunkSize && context.ChunkCount == source.ChunkCount && context.FileSizeBytes == source.FileSize &&
            context.PullTransportPaused && context.PullTransportPausedSinceUtc == execution.PausedSinceUtc &&
            FileTransferCoordinator.IsInboundFallbackRepairHandoffCurrent(CreateCoordinatorStateLocked(context), execution.Capture.Command);
    }

    private bool IsFallbackRepairHoldCapturedSourcePauseCurrentLocked(InboundTransferContext context, InboundBootstrapWriteSource source)
        => context.FallbackRepairHandoffExecution is { } execution &&
            IsInboundFallbackRepairHandoffExecutionCurrentLocked(context, execution) &&
            (execution.Writes.Any(write => write.Source with { Operation = null } == source with { Operation = null }) ||
             execution.Feedback.Any(feedback => feedback.Source == source with { Operation = null })) &&
            source.TransportPaused == execution.Source.TransportPaused && source.PausedSinceUtc == execution.Source.PausedSinceUtc;

    private static ImmutableArray<FileTransferRepairHandoffRange> CaptureInboundFallbackRepairGaps(InboundTransferContext context)
    {
        var result = ImmutableArray.CreateBuilder<FileTransferRepairHandoffRange>();
        var written = context.ReceiverSparseChunksWritten!;
        for (var index = context.NextChunkIndex; index < context.ChunkCount;)
        {
            if (written[index]) { index++; continue; }
            var start = index++;
            while (index < context.ChunkCount && !written[index]) { index++; }
            result.Add(new(start, index - start));
        }
        return result.ToImmutable();
    }

    private static FileTransferRouteTransitionDurableCheckpoint CaptureInboundFallbackRepairCheckpoint(
        InboundTransferContext context, FileTransferFallbackTailYieldScope scope)
    {
        var highest = context.NextChunkIndex - 1;
        for (var index = context.ChunkCount - 1; index >= context.NextChunkIndex; index--)
        {
            if (context.ReceiverSparseChunksWritten![index]) { highest = index; break; }
        }
        return new(scope.RuntimeOwner, scope.RoutePublication!.Value, scope.TerminalGeneration, scope.LegGeneration,
            scope.TransportEpoch, scope.BridgeRecoveryGeneration, scope.DataSessionGeneration,
            Math.Max(context.V4StateEpoch, context.V6ReceiverStateEpoch), context.NextChunkIndex, highest,
            context.ChunkCount, scope.CheckpointGeneration);
    }

    private void TryStartInboundFallbackRepairHandoffLocked(InboundTransferContext context)
    {
        if (HasInboundFallbackRepairHandoffLocked(context) || context.PullTransportPaused || context.PendingChunks.Count != 0 ||
            context.DataSession is not { } session || !CanTrackInboundFallbackRepairFeedback(session) ||
            context.ReceiverSparseChunksWritten is not { } written || written.Length != context.ChunkCount || context.ChunkCount <= 0 ||
            context.NextChunkIndex < 0 || context.NextChunkIndex >= context.ChunkCount || !written[context.ChunkCount - 1] ||
            !TryCaptureInboundFallbackRepairWriteSourceLocked(context, CaptureInboundBootstrapWriteIngress(context, session), out var source) ||
            source?.FallbackRepairSource is not { CheckpointGeneration: > 0 } scope)
        {
            return;
        }
        var state = CreateCoordinatorStateLocked(context);
        if ((state.PersistentState.FallbackTailYield.PendingPeerProofBeforeLocalYield ??
             state.PersistentState.FallbackTailYield.CurrentOperation?.PeerProof) is not { } peer) { return; }
        var writes = context.ActiveBootstrapWriteOperations.Values.OrderBy(write => write.Generation).ToArray();
        var feedback = context.ActiveFallbackRepairFeedbackOperations.Values.OrderBy(item => item.Generation).ToArray();
        if (writes.Any(write => write.Source.FallbackRepairSource != scope || !OwnsInboundBootstrapWriteChunks(context, write) ||
                !IsInboundBootstrapWriteSourceCurrentLocked(context, write.Source)) ||
            feedback.Any(item => item.Source.FallbackRepairSource != scope || item.PhysicalCompletionTask is null ||
                !IsInboundFallbackRepairFeedbackCurrentLocked(context, item)) ||
            context.ReceiverSparseChunksPendingWrite.Any(index => !context.BootstrapPendingWriteOwners.ContainsKey(index))) { return; }
        var gaps = CaptureInboundFallbackRepairGaps(context);
        if (gaps.IsEmpty) { return; }
        var prepared = FileTransferCoordinator.PrepareInboundFallbackRepairHandoff(state, peer);
        AdoptBootstrapCorrelationStateLocked(prepared.State, prepared.SnapshotEnvelope);
        if (!prepared.Accepted || prepared.HoldCommand is not { } command) { return; }
        var pausedSince = DateTimeOffset.UtcNow;
        var capture = new FileTransferFallbackRepairHandoffCapture(command, pausedSince.UtcTicks,
            writes.Select(write => write.Generation).ToImmutableArray(), feedback.Select(item => item.Generation).ToImmutableArray(),
            CaptureInboundFallbackRepairCheckpoint(context, scope), gaps);
        var captured = FileTransferCoordinator.CaptureInboundFallbackRepairHandoff(CreateCoordinatorStateLocked(context), capture);
        AdoptBootstrapCorrelationStateLocked(captured.State, captured.SnapshotEnvelope);
        if (!captured.Accepted) { return; }
        var execution = new InboundFallbackRepairHandoffExecution(capture, source, pausedSince, writes, feedback);
        context.FallbackRepairHandoffExecution = execution;
        context.PullTransportPaused = true;
        context.PullTransportPausedSinceUtc = pausedSince;
        context.PullTransportGraceDeadlineUtc = pausedSince.AddMilliseconds(PullSessionTransportRecoveryGraceMs);
        context.PullTransportPauseReason = "fallback_repair_handoff";
        context.PullTransportResumeRequestPending = false;
        _ = DrainInboundFallbackRepairHandoffAsync(context, execution);
    }

    private async Task DrainInboundFallbackRepairHandoffAsync(InboundTransferContext context, InboundFallbackRepairHandoffExecution execution)
    {
        await Task.Yield();
        await Task.WhenAll(execution.Writes.Select(write => write.Settled.Task)
            .Concat(execution.Feedback.Select(item => item.Settled.Task))).ConfigureAwait(false);
        Task? finalization = null;
        lock (gate)
        {
            if (!IsInboundFallbackRepairHandoffExecutionCurrentLocked(context, execution) ||
                context.ActiveBootstrapWriteOperations.Count != 0 || context.ActiveFallbackRepairFeedbackOperations.Count != 0 ||
                context.ReceiverSparseChunksPendingWrite.Count != 0 || context.PendingChunks.Count != 0) { return; }
            var receipt = new FileTransferFallbackRepairHandoffDrainReceipt(execution.Capture,
                execution.Capture.InboundWrites, execution.Capture.InboundFeedback,
                CaptureInboundFallbackRepairCheckpoint(context, execution.Capture.Command.Source), CaptureInboundFallbackRepairGaps(context));
            var drained = FileTransferCoordinator.DrainInboundFallbackRepairHandoff(CreateCoordinatorStateLocked(context), receipt);
            AdoptBootstrapCorrelationStateLocked(drained.State, drained.SnapshotEnvelope);
            if (drained.Accepted && !receipt.MissingRanges.IsEmpty)
            {
                ResolveInboundFallbackTailSurvivalLocked(context, "fallback_repair_handoff_drained");
            }
            else if (drained.Accepted && TryRestoreInboundFallbackRepairHandoffLocked(context))
            {
                finalization = FinalizeInboundTransferAsync(context, context.LifetimeCts.Token);
            }
        }
        if (finalization is not null) { await finalization.ConfigureAwait(false); }
    }

    private bool TryGetDrainedInboundFallbackRepairHandoffLocked(InboundTransferContext context,
        out FileTransferFallbackRepairHandoffDrainReceipt? receipt, bool requireGaps = true)
    {
        receipt = context.CoordinatorPersistentState.FallbackTailYield.RepairHandoff?.DrainReceipt;
        return receipt is not null && context.FallbackRepairHandoffExecution is { } execution &&
            IsInboundFallbackRepairHandoffExecutionCurrentLocked(context, execution) &&
            (requireGaps ? FileTransferCoordinator.HasDrainedInboundFallbackRepairHandoff(CreateCoordinatorStateLocked(context)) :
                context.CoordinatorPersistentState.FallbackTailYield.RepairHandoff?.State == FileTransferFallbackRepairHandoffState.Drained) &&
            context.ActiveBootstrapWriteOperations.Count == 0 && context.ActiveFallbackRepairFeedbackOperations.Count == 0 &&
            context.ReceiverSparseChunksPendingWrite.Count == 0 && context.PendingChunks.Count == 0 &&
            CaptureInboundFallbackRepairCheckpoint(context, execution.Capture.Command.Source) == receipt.Checkpoint &&
            CaptureInboundFallbackRepairGaps(context).SequenceEqual(receipt.MissingRanges);
    }

    private bool TryRestoreInboundFallbackRepairHandoffLocked(InboundTransferContext context,
        FileTransferRouteTransitionDispositionCommand? rollback = null)
    {
        if (!TryGetDrainedInboundFallbackRepairHandoffLocked(context, out var receipt, requireGaps: false) ||
            !FileTransferCoordinator.CanRestoreInboundFallbackRepairHandoffAfterRetryInvalidation(
                CreateCoordinatorStateLocked(context), receipt!.Capture.Command, rollback) ||
            !TryInvalidateRuntimeUnlockRetryLifecycleForScopeReplacementLocked(context, "fallback_repair_handoff_rollback")) { return false; }
        var restored = FileTransferCoordinator.RestoreInboundFallbackRepairHandoff(CreateCoordinatorStateLocked(context), receipt!.Capture.Command, rollback);
        AdoptBootstrapCorrelationStateLocked(restored.State, restored.SnapshotEnvelope);
        if (!restored.Accepted || restored.RestoreCommand is not { } command || command.Checkpoint != receipt.Checkpoint ||
            !command.MissingRanges.SequenceEqual(receipt.MissingRanges)) { return false; }
        ClearInboundRuntimeUnlockFallbackSeparationStateLocked(context);
        context.FallbackRepairHandoffExecution = null;
        context.PullTransportPaused = false;
        context.PullTransportPausedSinceUtc = null;
        context.PullTransportGraceDeadlineUtc = null;
        context.PullTransportPauseReason = null;
        context.PullTransportResumeRequestPending = false;
        return true;
    }

    private bool TryAdoptInboundFallbackRepairHandoffDispositionLocked(InboundTransferContext context,
        FileTransferRouteTransitionDispositionCommand command)
    {
        if (context.FallbackRepairHandoffExecution is not { } execution) { return !HasInboundFallbackRepairHandoffLocked(context); }
        if (command.Kind == FileTransferRouteTransitionDispositionKind.RolledBack)
        {
            return TryRestoreInboundFallbackRepairHandoffLocked(context, command);
        }
        if (command.Kind != FileTransferRouteTransitionDispositionKind.Committed ||
            context.CoordinatorPersistentState.FallbackTailYield.RepairHandoff is not
                { State: FileTransferFallbackRepairHandoffState.Adopted, TunaDisposition: { } retained } || retained != command ||
            !ReferenceEquals(context.WriteStream, execution.Source.Stream) ||
            !ReferenceEquals(context.ReceiverSparseChunksWritten, execution.Source.Written) ||
            context.ChunkSizeBytes != execution.Source.ChunkSize || context.ChunkCount != execution.Source.ChunkCount ||
            context.FileSizeBytes != execution.Source.FileSize ||
            !CaptureInboundFallbackRepairGaps(context).SequenceEqual(
                context.CoordinatorPersistentState.FallbackTailYield.RepairHandoff.DrainReceipt!.MissingRanges)) { return false; }
        context.FallbackRepairHandoffExecution = null;
        return true;
    }
}
