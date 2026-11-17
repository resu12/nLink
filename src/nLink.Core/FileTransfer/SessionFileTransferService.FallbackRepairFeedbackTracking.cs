namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    internal static Func<string, Task>? FallbackRepairFeedbackBeforeFinalizationForTests { get; set; }
    // Execution evidence only. Coordinator capture/drain must accept these exact operations.
    private sealed record InboundFallbackRepairFeedbackOperation(long Generation, InboundBootstrapWriteSource Source)
    {
        public Task? PhysicalCompletionTask { get; set; }
        public TaskCompletionSource Settled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private bool CanTrackInboundFallbackRepairFeedback(IFileTransferDataSession session)
        => transport is IFileTransferControlPlaneDeliveryTransport
            ? transport is IFileTransferControlPlanePhysicalSendSubmissionTransport
            : session is IFileTransferPhysicalSendSubmissionSession;

    private bool TryRegisterInboundFallbackRepairFeedbackLocked(InboundTransferContext context,
        IFileTransferDataSession session, FileTransferControlPlaneDeliveryRequest request,
        out InboundFallbackRepairFeedbackOperation? operation)
    {
        operation = null;
        if (request.RouteToken != FileTransferRouteResolver.PostTunaFallbackV6Token ||
            request.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6) { return true; }
        // Unsupported transports keep their existing delivery behavior, but cannot supply handoff drain proof.
        if (!CanTrackInboundFallbackRepairFeedback(session)) { return true; }
        if (!ReferenceEquals(context.DataSession, session) ||
            !TryCaptureInboundFallbackRepairWriteSourceLocked(context, CaptureInboundBootstrapWriteIngress(context, session), out var source) ||
            source is null) { return false; }
        operation = new(checked(++context.LastFallbackRepairFeedbackOperationGeneration), source);
        context.ActiveFallbackRepairFeedbackOperations.Add(operation.Generation, operation);
        return true;
    }

    private bool IsInboundFallbackRepairFeedbackCurrentLocked(InboundTransferContext context,
        InboundFallbackRepairFeedbackOperation operation)
        => context.ActiveFallbackRepairFeedbackOperations.TryGetValue(operation.Generation, out var current) &&
            ReferenceEquals(current, operation) && IsInboundBootstrapWriteSourceCurrentWithoutOperationLocked(context, operation.Source);

    private FileTransferControlPlaneDeliveryRequest BindInboundFallbackRepairFeedbackRequest(InboundTransferContext context,
        FileTransferControlPlaneDeliveryRequest request, InboundFallbackRepairFeedbackOperation? operation)
    {
        if (operation is null) { return request; }
        var prior = request.PreTransportDeliveryValidator;
        return request with { PreTransportDeliveryValidator = () =>
        {
            lock (gate) { return (prior is null || prior()) && IsInboundFallbackRepairFeedbackCurrentLocked(context, operation); }
        } };
    }

    private async Task RetireInboundFallbackRepairFeedbackAsync(InboundTransferContext context,
        InboundFallbackRepairFeedbackOperation operation)
    {
        if (operation.PhysicalCompletionTask is { } physical)
        {
            try { await physical.ConfigureAwait(false); }
            catch { /* Physical completion is settlement, not delivery acceptance. */ }
        }
        lock (gate)
        {
            if (context.ActiveFallbackRepairFeedbackOperations.TryGetValue(operation.Generation, out var current) && ReferenceEquals(current, operation))
            {
                context.ActiveFallbackRepairFeedbackOperations.Remove(operation.Generation);
            }
            operation.Settled.TrySetResult();
            if ((context.CoordinatorPersistentState.FallbackTailYield.PendingPeerProofBeforeLocalYield is not null ||
                 context.CoordinatorPersistentState.FallbackTailYield.CurrentOperation?.PeerProof is not null) &&
                IsInboundBootstrapWriteSourceCurrentWithoutOperationLocked(context, operation.Source))
            {
                ResolveInboundFallbackTailSurvivalLocked(context, "fallback_repair_feedback_settled");
            }
        }
    }

    private async Task FinalizeInboundFallbackRepairFeedbackAsync(InboundTransferContext context,
        InboundFallbackRepairFeedbackOperation? operation)
    {
        if (operation is null) { return; }
        try
        {
            if (FallbackRepairFeedbackBeforeFinalizationForTests is { } barrier) { await barrier(context.TransferId).ConfigureAwait(false); }
        }
        finally { _ = RetireInboundFallbackRepairFeedbackAsync(context, operation); }
    }
}
