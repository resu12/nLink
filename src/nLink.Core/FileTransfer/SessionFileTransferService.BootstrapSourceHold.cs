using System.Collections;
using System.Collections.Immutable;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private sealed record BootstrapSourceHoldExecution(
        object Context, FileTransferBootstrapSourceHoldCapture Capture,
        IFileTransferRouteTransitionBridge Bridge, FileTransferFallbackV6ProofBootstrapEvaluation Evaluation,
        IFileTransferDataSession DataSession, long ProviderGeneration, long ServiceGeneration,
        bool PriorPaused, DateTimeOffset? PriorPauseSinceUtc, DateTimeOffset PauseSinceUtc,
        Stream? Stream, BitArray? Written, int ChunkSize, int ChunkCount, long FileSize,
        OutboundPhysicalSendLease[] OutboundLeases, InboundBootstrapWriteOperation[] Writes,
        InboundBootstrapFeedbackOperation[] Feedback)
    {
        public bool DrainStarted { get; set; }
    }

    private bool HasBootstrapSourceHoldLocked(OutboundTransferContext context)
        => context.RouteRuntime.UsesFileTunaV4Runtime && context.BootstrapSourceHoldExecution is { } execution &&
            context.CurrentRoutePublicationToken is { } publication &&
            FileTransferCoordinator.CreateRoutePublicationAuthority(publication) == execution.Capture.Command.Source.Publication;

    private bool HasBootstrapSourceHoldLocked(InboundTransferContext context)
        => context.RouteRuntime.UsesFileTunaV4Runtime && context.BootstrapSourceHoldExecution is { } execution &&
            context.CurrentRoutePublicationToken is { } publication &&
            FileTransferCoordinator.CreateRoutePublicationAuthority(publication) == execution.Capture.Command.Source.Publication;

    private bool IsBootstrapSourceHoldExecutionCurrentLocked(BootstrapSourceHoldExecution execution)
    {
        if (!ReferenceEquals(transport, execution.Bridge) ||
            execution.DataSession is not IFileTransferDataSessionGenerationProvider provider || provider.Generation != execution.ProviderGeneration) { return false; }
        FileTransferCoordinatorState state;
        if (execution.Context is OutboundTransferContext outbound)
        {
            if (!ReferenceEquals(outboundTransfer, outbound) || !ReferenceEquals(outbound.BootstrapSourceHoldExecution, execution) ||
                outbound.IsTerminal || outbound.UserPaused || outbound.PeerPaused || outbound.LifetimeCts.IsCancellationRequested ||
                !ReferenceEquals(outbound.DataSession, execution.DataSession) || outbound.DataSessionGeneration != execution.ServiceGeneration ||
                !outbound.PullTransportPaused || outbound.PullTransportPausedSinceUtc != execution.PauseSinceUtc) { return false; }
            state = CreateCoordinatorStateLocked(outbound);
        }
        else if (execution.Context is InboundTransferContext inbound)
        {
            if (!ReferenceEquals(inboundTransfer, inbound) || !ReferenceEquals(inbound.BootstrapSourceHoldExecution, execution) ||
                inbound.IsTerminal || inbound.UserPaused || inbound.PeerPaused || inbound.LifetimeCts.IsCancellationRequested ||
                !ReferenceEquals(inbound.DataSession, execution.DataSession) || inbound.DataSessionGeneration != execution.ServiceGeneration ||
                !inbound.PullTransportPaused || inbound.PullTransportPausedSinceUtc != execution.PauseSinceUtc ||
                !ReferenceEquals(inbound.WriteStream, execution.Stream) || !ReferenceEquals(inbound.ReceiverSparseChunksWritten, execution.Written) ||
                inbound.ChunkSizeBytes != execution.ChunkSize || inbound.ChunkCount != execution.ChunkCount || inbound.FileSizeBytes != execution.FileSize) { return false; }
            state = CreateCoordinatorStateLocked(inbound);
        }
        else { return false; }
        return FileTransferCoordinator.IsFallbackBootstrapSourceHoldCurrent(state, execution.Capture.Command) &&
            state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold?.Capture == execution.Capture;
    }

    private bool IsBootstrapHoldCapturedWritePauseCurrentLocked(InboundTransferContext context, InboundBootstrapWriteSource source)
        => context.BootstrapSourceHoldExecution is { } execution && source.Operation is { } operation &&
            execution.Writes.Any(captured => ReferenceEquals(captured, operation)) && source.TransportPaused == execution.PriorPaused &&
            source.PausedSinceUtc == execution.PriorPauseSinceUtc && IsBootstrapSourceHoldExecutionCurrentLocked(execution);

    private bool IsBootstrapHoldCapturedFeedbackPauseCurrentLocked(InboundTransferContext context, InboundBootstrapFeedbackOperation operation)
        => context.BootstrapSourceHoldExecution is { } execution &&
            execution.Feedback.Any(captured => ReferenceEquals(captured, operation)) && operation.TransportPaused == execution.PriorPaused &&
            operation.PausedSinceUtc == execution.PriorPauseSinceUtc && IsBootstrapSourceHoldExecutionCurrentLocked(execution);

    private bool TryEnsureBootstrapSourceHoldLocked(object context, FileTransferCoordinatorState state,
        FileTransferFallbackV6ProofBootstrapObservation observation, FileTransferFallbackBootstrapFrameV6? peerWake,
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation)
    {
        var prior = context is OutboundTransferContext o ? o.BootstrapSourceHoldExecution : ((InboundTransferContext)context).BootstrapSourceHoldExecution;
        if (prior is not null && state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold?.Command.Source == prior.Capture.Command.Source &&
            FileTransferCoordinator.CreateRoutePublicationAuthority(state.CurrentRoutePublicationToken!.Value) == prior.Capture.Command.Source.Publication)
        {
            return IsBootstrapSourceHoldExecutionCurrentLocked(prior) &&
                state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold?.Receipt is not null;
        }
        if (transport is not IFileTransferRouteTransitionBridge { SupportsFallbackBootstrapCorrelation: true } bridge) { return false; }
        OutboundBootstrapSourceCallback[] outboundCallbacks = [];
        if (context is OutboundTransferContext callbackContext)
        {
            outboundCallbacks = callbackContext.ActiveBootstrapSourceCallbacks.Values
                .Where(callback => !IsBootstrapSourceCallbackRetiredLocked(state, callback)).ToArray();
            // A transport pause may stale a callback before its completion is
            // consumed. Do not create and invalidate a same-source hold for it:
            // a later peer wake must still be able to request a fresh hold.
            if (outboundCallbacks.Any(callback =>
                !ReferenceEquals(callback.DataSession, callbackContext.DataSession) ||
                callback.DataSession is not IFileTransferDataSessionGenerationProvider callbackProvider ||
                callbackProvider.Generation != callback.Authority.ProviderGeneration ||
                callback.Authority.TransportPaused != callbackContext.PullTransportPaused ||
                callback.Authority.PauseInstance != (callbackContext.PullTransportPausedSinceUtc?.UtcTicks ?? 0) ||
                !FileTransferCoordinator.IsFallbackBootstrapSourceCallbackSourceCurrent(state, callback.Authority)))
            {
                LogBootstrapCorrelationRejection(state.Direction, state.SessionId, state.TransferId,
                    "bootstrap_source_hold_outbound_callback_unbound");
                return false;
            }
        }
        var prepared = FileTransferCoordinator.PrepareFallbackBootstrapSourceHold(state, observation, peerWake);
        AdoptBootstrapCorrelationStateLocked(prepared.State, prepared.SnapshotEnvelope);
        if (!prepared.Accepted || prepared.Command is not { } command) { return false; }
        IFileTransferDataSession? session;
        long serviceGeneration;
        bool paused;
        DateTimeOffset? pausedSince;
        OutboundPhysicalSendLease[] outboundLeases = [];
        InboundBootstrapWriteOperation[] writes = [];
        InboundBootstrapFeedbackOperation[] feedback = [];
        Stream? stream = null;
        BitArray? written = null;
        int chunkSize = 0, chunkCount = 0;
        long fileSize = 0;
        if (context is OutboundTransferContext outbound)
        {
            session = outbound.DataSession; serviceGeneration = outbound.DataSessionGeneration;
            paused = outbound.PullTransportPaused; pausedSince = outbound.PullTransportPausedSinceUtc;
            outboundLeases = outbound.ActiveOutboundPhysicalSendLeases.Values.ToArray();
            if (outboundLeases.Any(lease => lease.PhysicalCompletionTask is null || !ReferenceEquals(lease.DataSession, session) ||
                lease.DataSessionGeneration != serviceGeneration || lease.RuntimeOwner != state.CurrentRuntimeOwner ||
                lease.RoutePublication != state.CurrentRoutePublicationToken || lease.TerminalGeneration != state.TerminalGeneration ||
                lease.LegGeneration != state.CurrentLeg?.Generation || lease.TransportEpoch != state.CurrentLeg?.TransportEpochId ||
                lease.BridgeRecoveryGeneration != state.CurrentLeg?.BridgeRecoveryGeneration)) { return RejectCapture("bootstrap_source_hold_outbound_work_unbound"); }
        }
        else
        {
            var inbound = (InboundTransferContext)context;
            session = inbound.DataSession; serviceGeneration = inbound.DataSessionGeneration;
            paused = inbound.PullTransportPaused; pausedSince = inbound.PullTransportPausedSinceUtc;
            writes = inbound.ActiveBootstrapWriteOperations.Values.ToArray();
            feedback = inbound.ActiveBootstrapFeedbackOperations.Values.ToArray();
            stream = inbound.WriteStream; written = inbound.ReceiverSparseChunksWritten;
            chunkSize = inbound.ChunkSizeBytes; chunkCount = inbound.ChunkCount; fileSize = inbound.FileSizeBytes;
            if (writes.Any(write => !IsInboundBootstrapWriteSourceCurrentLocked(inbound, write.Source)) ||
                feedback.Any(send => !IsInboundBootstrapFeedbackCurrentLocked(inbound, send)) || inbound.PendingChunks.Count != 0 ||
                inbound.ReceiverSparseChunksPendingWrite.Any(index => !inbound.BootstrapPendingWriteOwners.ContainsKey(index)))
            { return RejectCapture("bootstrap_source_hold_inbound_work_unbound"); }
        }
        if (session is not IFileTransferDataSessionGenerationProvider { Generation: > 0 } provider) { return RejectCapture("bootstrap_source_hold_session_unbound"); }
        var pauseSince = paused && pausedSince is { } existingPause ? existingPause : DateTimeOffset.UtcNow;
        var capture = new FileTransferBootstrapSourceHoldCapture(command, pauseSince.UtcTicks,
            outboundLeases.Select(lease => lease.LeaseGeneration).ToImmutableArray(), writes.Select(write => write.Generation).ToImmutableArray(),
            feedback.Select(send => send.Generation).ToImmutableArray())
        { OutboundCallbacks = outboundCallbacks.Select(callback => callback.Authority).ToImmutableArray() };
        var execution = new BootstrapSourceHoldExecution(context, capture, bridge, evaluation, session, provider.Generation, serviceGeneration,
            paused, pausedSince, pauseSince, stream, written, chunkSize, chunkCount, fileSize, outboundLeases, writes, feedback);
        // The command closes admission under this same gate; its capture does not create a new route or owner.
        if (context is OutboundTransferContext targetOutbound)
        {
            targetOutbound.BootstrapSourceHoldExecution = execution;
            targetOutbound.PullTransportPaused = true; targetOutbound.PullTransportPausedSinceUtc = pauseSince;
            targetOutbound.PullTransportGraceDeadlineUtc ??= pauseSince.AddMilliseconds(PullSessionTransportRecoveryGraceMs);
            targetOutbound.PullTransportPauseReason = "bootstrap_source_hold";
        }
        else
        {
            var targetInbound = (InboundTransferContext)context;
            targetInbound.BootstrapSourceHoldExecution = execution;
            targetInbound.PullTransportPaused = true; targetInbound.PullTransportPausedSinceUtc = pauseSince;
            targetInbound.PullTransportGraceDeadlineUtc ??= pauseSince.AddMilliseconds(PullSessionTransportRecoveryGraceMs);
            targetInbound.PullTransportPauseReason = "bootstrap_source_hold";
        }
        var applied = FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldCapture(prepared.State, capture);
        AdoptBootstrapCorrelationStateLocked(applied.State, applied.SnapshotEnvelope);
        if (applied.Accepted)
        {
            foreach (var callback in outboundCallbacks) { callback.Capture = capture; }
        }
        return false;

        bool RejectCapture(string reason)
        {
            var rejected = FileTransferCoordinator.InvalidateFallbackBootstrapSourceHold(prepared.State, reason);
            AdoptBootstrapCorrelationStateLocked(rejected.State, rejected.SnapshotEnvelope);
            return false;
        }
    }

    private void StartBootstrapSourceHoldDrainIfNeeded(FileTransferDirection direction)
    {
        BootstrapSourceHoldExecution? execution;
        lock (gate)
        {
            execution = direction == FileTransferDirection.Outbound ? outboundTransfer?.BootstrapSourceHoldExecution : inboundTransfer?.BootstrapSourceHoldExecution;
            if (execution is null || execution.DrainStarted || !IsBootstrapSourceHoldExecutionCurrentLocked(execution)) { return; }
            execution.DrainStarted = true;
        }
        _ = CompleteBootstrapSourceHoldDrainAsync(execution);
    }

    private async Task CompleteBootstrapSourceHoldDrainAsync(BootstrapSourceHoldExecution execution)
    {
        try
        {
            await Task.WhenAll(execution.OutboundLeases.Select(lease => lease.PhysicalCompletionTask!)
                .Concat(execution.Writes.Select(write => write.Settled.Task))
                .Concat(execution.Feedback.Select(send => send.Settled.Task))).ConfigureAwait(false);
        }
        catch { /* All captured tasks have actually settled; failures are not delivery proof. */ }
        InboundFallbackV6ProofBootstrapPendingWriteWake? wake = null;
        lock (gate)
        {
            if (!IsBootstrapSourceHoldExecutionCurrentLocked(execution)) { return; }
            var state = execution.Context is OutboundTransferContext outbound ? CreateCoordinatorStateLocked(outbound)
                : CreateCoordinatorStateLocked((InboundTransferContext)execution.Context);
            var checkpoint = execution.Context is OutboundTransferContext sender
                ? CreateOutboundFileTransferRouteTransitionCheckpoint(sender, sender.CurrentRuntimeOwner!.Value, sender.CurrentRoutePublicationToken!.Value, sender.CurrentTransferLeg!)
                : CreateInboundFileTransferRouteTransitionCheckpoint((InboundTransferContext)execution.Context, state.CurrentRuntimeOwner!.Value, state.CurrentRoutePublicationToken!.Value, state.CurrentLeg!);
            var receipt = new FileTransferBootstrapSourceHoldReceipt(execution.Capture, execution.Capture.OutboundPhysicalLeases,
                execution.Capture.InboundWrites, execution.Capture.InboundFeedback, checkpoint);
            var applied = FileTransferCoordinator.ApplyFallbackBootstrapSourceHoldReceipt(state, receipt);
            AdoptBootstrapCorrelationStateLocked(applied.State, applied.SnapshotEnvelope);
            if (!applied.Accepted) { return; }
            if (execution.Context is InboundTransferContext receiver && receiver.CurrentFallbackV6ProofBootstrapPendingWriteWake is not null)
            {
                wake = TakeInboundFallbackV6ProofBootstrapPendingWriteWakeAfterDrainLocked(receiver, out _, out _, out _);
                if (wake is null) { return; }
            }
        }
        if (wake is not null) { await ProcessFallbackV6ProofBootstrapPendingWriteRecheckAsync(wake).ConfigureAwait(false); }
        else { await ProcessFallbackV6ProofBootstrapEvaluationAsync(execution.Bridge, execution.Evaluation).ConfigureAwait(false); }
    }
}
