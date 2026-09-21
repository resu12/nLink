namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    internal static Func<string, Task>? BootstrapFeedbackBeforeSubmissionForTests { get; set; }
    internal static Func<string, Task>? BootstrapFeedbackBeforeFinalizationForTests { get; set; }

    // Execution evidence only. A source hold must still capture/accept it through Coordinator decisions.
    private sealed record InboundBootstrapFeedbackOperation(
        long Generation, string CoordinatorInstanceId, FileTransferBootstrapAgreement Agreement,
        FileTransferBootstrapLocalStopScope Source, IFileTransferDataSession DataSession,
        string DataSessionIdentity, long ProviderGeneration, long ServiceGeneration,
        bool TransportPaused, DateTimeOffset? PausedSinceUtc, string FrameFingerprint)
    {
        public FileTransferPhysicalSendSubmission? Submission { get; set; }
        public InboundBootstrapWriteSource? WriteSource { get; init; }
        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private bool UsesInboundBootstrapFeedbackTrackingLocked(InboundTransferContext context)
        => context.RouteRuntime.UsesFileTunaV4Runtime && UsesBootstrapCorrelation(CreateCoordinatorStateLocked(context));

    private bool TryRegisterInboundBootstrapFeedbackLocked(InboundTransferContext context,
        IFileTransferDataSession session, FileTransferStateFrameV4 frame, out InboundBootstrapFeedbackOperation? operation)
    {
        operation = null;
        if (!UsesInboundBootstrapFeedbackTrackingLocked(context)) { return true; }
        var state = CreateCoordinatorStateLocked(context);
        if (session is not IFileTransferPhysicalSendSubmissionSession ||
            session is not IFileTransferDataSessionGenerationProvider { Generation: > 0 } provider ||
            FileTransferCoordinator.CaptureFallbackBootstrapSourceScope(state) is not { } source ||
            state.CurrentDataSessionIdentity is not { Length: > 0 } identity ||
            state.PersistentState.FileTransferRouteTransition.BootstrapAgreement is not { IsEnabled: true } agreement)
        {
            return false;
        }
        operation = new(checked(++context.LastBootstrapFeedbackOperationGeneration), context.CoordinatorInstanceId, agreement,
            source, session, identity, provider.Generation, context.DataSessionGeneration,
            context.PullTransportPaused, context.PullTransportPausedSinceUtc, CreateFileTransferV4StateWireFingerprint(frame));
        context.ActiveBootstrapFeedbackOperations.Add(operation.Generation, operation);
        return true;
    }

    private bool IsInboundBootstrapFeedbackCurrentLocked(InboundTransferContext context, InboundBootstrapFeedbackOperation operation)
    {
        if (operation.WriteSource is { } source && !IsInboundBootstrapWriteSourceCurrentLocked(context, source)) { return false; }
        if (!ReferenceEquals(inboundTransfer, context) || context.IsTerminal || context.UserPaused ||
            context.CoordinatorInstanceId != operation.CoordinatorInstanceId ||
            !ReferenceEquals(context.DataSession, operation.DataSession) || context.DataSessionGeneration != operation.ServiceGeneration ||
            context.DataSession is not IFileTransferDataSessionGenerationProvider provider || provider.Generation != operation.ProviderGeneration ||
            ((context.PullTransportPaused != operation.TransportPaused || context.PullTransportPausedSinceUtc != operation.PausedSinceUtc) &&
                !IsBootstrapHoldCapturedFeedbackPauseCurrentLocked(context, operation)) ||
            !context.ActiveBootstrapFeedbackOperations.TryGetValue(operation.Generation, out var current) || !ReferenceEquals(current, operation))
        {
            return false;
        }
        var state = CreateCoordinatorStateLocked(context);
        return state.CurrentDataSessionIdentity == operation.DataSessionIdentity &&
            state.PersistentState.FileTransferRouteTransition.BootstrapAgreement == operation.Agreement &&
            FileTransferCoordinator.IsFallbackBootstrapLocalStopScopeCurrent(state, operation.Source);
    }

    private async Task RetireInboundBootstrapFeedbackAfterFinalizationAsync(InboundTransferContext context,
        InboundBootstrapFeedbackOperation operation)
    {
        // Called only after the local send routine is finalized. Delivery and cancellation are not drain proof.
        if (operation.Submission is { } submission)
        {
            try { await submission.PhysicalCompletionTask.ConfigureAwait(false); }
            catch { /* The physical-submission contract reports actual settlement, independently of delivery outcome. */ }
        }
        lock (gate)
        {
            if (context.ActiveBootstrapFeedbackOperations.TryGetValue(operation.Generation, out var current) && ReferenceEquals(current, operation))
            {
                context.ActiveBootstrapFeedbackOperations.Remove(operation.Generation);
            }
            operation.Settled.TrySetResult();
        }
    }
}
