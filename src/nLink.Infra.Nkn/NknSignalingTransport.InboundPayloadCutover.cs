using NLink.Core.FileTransfer;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    // Caller holds gate then accelerationGate. Only an accepted Coordinator
    // binding can pause payloads; control and v6 bootstrap remain independent.
    private FileTransferRouteTransitionPauseBindingShadow? GetInboundPayloadCutoverPauseUnsafe(FileTransferDataFrame frame)
    {
        if (!IsFileTransferV4PayloadDataPathFrame(frame.Type, ResolveFileTransferDataPathBatchProfile(frame),
                GetFrameRawPayloadBytes(frame), GetFrameBatchChunkCount(frame)))
            return null;

        var shadow = fileTransferRouteTransitionExecutionShadow;
        var binding = shadow.Current ?? shadow.RetainedPredecessor;
        return binding is { Command.Authority: var authority } &&
               authority.Kind == FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6 &&
               authority.Direction == FileTransferDirection.Inbound &&
               authority.SessionId == frame.SessionId && authority.TransferId == frame.TransferId
            ? binding : null;
    }

    private async Task ResumeInboundPayloadAfterCutoverAsync(
        FileTransferRouteTransitionPauseBindingShadow binding, long dispatchGeneration, Action resume)
    {
        var reset = Volatile.Read(ref inboundFileTransferDispatchReset).Task;
        try
        {
            await Task.WhenAny(binding.PayloadCutoverCompletion.Task, reset).ConfigureAwait(false);
            if (disposed || reset.IsCompleted ||
                dispatchGeneration != Volatile.Read(ref inboundFileTransferDispatchGeneration) ||
                !await binding.PayloadCutoverCompletion.Task.ConfigureAwait(false))
                return;
            if (!IsFileTransferRouteTransitionDataSessionCurrent(binding.Command.Authority))
                return;

            // Re-enter the ordinary guard with the current publication. The
            // completion itself supplies no route/runtime authority.
            resume();
        }
        catch (Exception ex)
        {
            NknRuntimeDiagnostics.SetLastError(ex);
            NknRuntimeDiagnostics.SetLastEnvelopeDropReason($"dispatch_{ex.GetType().Name}");
            Log($"Envelope dispatch failed (type={MsgType.FileTransferDataFrame}, transfer_id={binding.Command.Authority.TransferId}, ex={ex.GetType().Name})");
        }
    }
}
