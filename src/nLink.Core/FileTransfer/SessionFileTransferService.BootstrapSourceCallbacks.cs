namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    // Execution identity only. Effect authority is resolved by the Coordinator at each effect boundary.
    private sealed record OutboundBootstrapSourceCallback(
        FileTransferBootstrapSourceCallback Authority, IFileTransferDataSession DataSession)
    {
        public FileTransferBootstrapSourceHoldCapture? Capture { get; set; }
        public FileTransferBootstrapSourceCallbackRetirement? Retirement { get; set; }
    }

    private static bool IsBootstrapSourceCallbackRetiredLocked(FileTransferCoordinatorState state, OutboundBootstrapSourceCallback callback)
    {
        callback.Retirement ??= FileTransferCoordinator.IssueFallbackBootstrapSourceCallbackRetirement(state, callback.Authority, callback.Capture);
        return FileTransferCoordinator.IsFallbackBootstrapSourceCallbackRetired(state, callback.Authority, callback.Capture, callback.Retirement);
    }

    private bool IsBootstrapSourceCallbackInstallAllowedLocked(OutboundTransferContext context)
    {
        var state = CreateCoordinatorStateLocked(context);
        if (context.BootstrapCallbackAwaitingSuccessor is { } source &&
            !FileTransferCoordinator.HasCommittedBootstrapSourceCallbackSuccessor(state, source)) { return false; }
        if (state.PersistentState.FileTransferRouteTransition.BootstrapSourceHold is { Capture: { } capture } hold &&
            !capture.OutboundCallbacks.IsEmpty &&
            !FileTransferCoordinator.HasCommittedBootstrapSourceCallbackSuccessor(state, hold.Command.Source)) { return false; }
        foreach (var callback in context.ActiveBootstrapSourceCallbacks.Values)
        {
            if (IsBootstrapSourceCallbackRetiredLocked(state, callback)) { continue; }
            if (ResolveBootstrapSourceCallbackLocked(context, callback) != FileTransferBootstrapSourceCallbackDisposition.Current &&
                !FileTransferCoordinator.HasCommittedBootstrapSourceCallbackSuccessor(state, callback.Authority.Source)) { return false; }
        }
        context.BootstrapCallbackAwaitingSuccessor = null;
        return true;
    }

    private sealed class OutboundBootstrapSourceCallbackException(OutboundBootstrapSourceCallback callback, Exception? cause)
        : InvalidOperationException("File-transfer V4 sender transport callback completed.", cause)
    {
        public OutboundBootstrapSourceCallback Callback { get; } = callback;
    }

    private FileTransferBootstrapSourceCallbackDisposition ResolveBootstrapSourceCallbackLocked(
        OutboundTransferContext context, OutboundBootstrapSourceCallback callback)
    {
        if (!ReferenceEquals(outboundTransfer, context) || context.IsTerminal || context.LifetimeCts.IsCancellationRequested ||
            context.UserPaused || context.PeerPaused || !ReferenceEquals(context.DataSession, callback.DataSession) ||
            !context.ActiveBootstrapSourceCallbacks.TryGetValue(callback.Authority.LeaseGeneration, out var active) ||
            !ReferenceEquals(active, callback) || callback.DataSession is not IFileTransferDataSessionGenerationProvider provider)
        { return FileTransferBootstrapSourceCallbackDisposition.Stale; }
        var disposition = FileTransferCoordinator.ResolveFallbackBootstrapSourceCallback(CreateCoordinatorStateLocked(context),
            callback.Authority, provider.Generation, context.PullTransportPaused, context.PullTransportPausedSinceUtc?.UtcTicks ?? 0);
        if (disposition == FileTransferBootstrapSourceCallbackDisposition.Held &&
            (context.BootstrapSourceHoldExecution is not { } hold || !IsBootstrapSourceHoldExecutionCurrentLocked(hold)))
        { return FileTransferBootstrapSourceCallbackDisposition.Stale; }
        return disposition;
    }

    private void EnsureBootstrapSourceCallbackCurrentLocked(OutboundTransferContext context, OutboundBootstrapSourceCallback? callback)
    {
        if (callback is not null && ResolveBootstrapSourceCallbackLocked(context, callback) != FileTransferBootstrapSourceCallbackDisposition.Current)
        { throw new OutboundBootstrapSourceCallbackException(callback, null); }
    }

    private void RecordBootstrapSourceCallbackLocked(OutboundTransferContext context, OutboundBootstrapSourceCallback callback,
        string stage, string allowedEffect)
    {
        var disposition = ResolveBootstrapSourceCallbackLocked(context, callback);
        var snapshot = FileTransferCoordinator.CreateBootstrapSourceCallbackSnapshot(CreateCoordinatorStateLocked(context),
            callback.Authority, stage, disposition,
            disposition == FileTransferBootstrapSourceCallbackDisposition.Current ? allowedEffect : "none", callback.Capture);
        AdoptBootstrapCorrelationStateLocked(snapshot.State, snapshot.Envelope);
    }

    private void RetireBootstrapSourceCallbackLocked(OutboundTransferContext context, OutboundBootstrapSourceCallback? callback,
        string allowedEffect = "none")
    {
        if (callback is not null && context.ActiveBootstrapSourceCallbacks.TryGetValue(callback.Authority.LeaseGeneration, out var active) &&
            ReferenceEquals(active, callback))
        {
            RecordBootstrapSourceCallbackLocked(context, callback, "Consumed", allowedEffect);
            context.ActiveBootstrapSourceCallbacks.Remove(callback.Authority.LeaseGeneration);
        }
    }

    private async Task<bool> CompleteBootstrapSourceCallbackFailureAsync(OutboundTransferContext context,
        IFileTransferDataSession dataSession, OutboundSparsePumpInstall? install, OutboundBootstrapSourceCallbackException failure)
    {
        try
        {
            if (OutboundSparseFailureDeferralBarrierForTests is { } barrier)
            { await barrier(context.TransferId, context.SessionId).ConfigureAwait(false); }
            if (failure.InnerException is null) { return false; }
            var errorCode = ClassifyOutboundFailureErrorCode(failure.InnerException, StreamReadFailedErrorCode);
            return await FailOwnedOutboundV4Async(context, dataSession, install?.RuntimeOwner, errorCode,
                ClassifyOutboundFailureStatusMessage(failure.InnerException, errorCode), true, install, failure.Callback).ConfigureAwait(false);
        }
        finally
        {
            lock (gate) { RetireBootstrapSourceCallbackLocked(context, failure.Callback); }
        }
    }
}
