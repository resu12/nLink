using NLink.Core.FileTransfer;

namespace NLink.Infra.Nkn;

public sealed partial class NknSignalingTransport
{
    private static FileTransferFallbackTailYieldEvidence? CaptureYieldSendEvidence(
        FileTransferDataFrame frame, Envelope control, FileTransferSecurityEvidence? controlSecurity,
        Envelope? bulk, FileTransferSecurityEvidence? bulkSecurity)
    {
        if (frame is not FileTransferFallbackTailYieldFrameV6) { return null; }
        try
        {
            return CaptureYieldEvidence(frame,
                CreateFileTransferDataPathEnvelopeEvidence(control.MessageId, frame.SessionId, frame.TransferId)
                    with { SecurityEvidence = controlSecurity }, "control",
                bulk is null ? null : CreateFileTransferDataPathEnvelopeEvidence(bulk.MessageId, frame.SessionId, frame.TransferId)
                    with { SecurityEvidence = bulkSecurity });
        }
        catch (Exception) { return null; }
    }

    private static FileTransferFallbackTailYieldEvidence? CaptureYieldEvidence(
        FileTransferDataFrame frame, FileTransferDataPathEnvelopeEvidence first,
        string firstCopy, FileTransferDataPathEnvelopeEvidence? second = null)
    {
        if (frame is not FileTransferFallbackTailYieldFrameV6 yield)
        {
            return null;
        }
        try
        {
            return new(yield, Copy(firstCopy, first), second is { } bulk ? Copy("bulk", bulk) : null);
        }
        catch (Exception)
        {
            return null;
        }

        static FileTransferFallbackTailYieldEvidence.Copy Copy(
            string name, FileTransferDataPathEnvelopeEvidence captured)
            => new(name, () =>
            {
                var security = captured.SecurityEvidence;
                var status = SecurityContextStatus(security?.Context, security?.SessionId);
                return (status == "captured" && security?.PayloadFingerprint != "(none)" &&
                    captured.EnvelopeFingerprint != "(none)" ? "bound" : status == "retired" ? "retired" : "unbound",
                    FormatFileTransferDataPathEnvelopeEvidenceSuffix(captured));
            });
    }

    private static void LogYieldSendCopies(
        FileTransferFallbackTailYieldEvidence? evidence, LifecycleDeliveryResult result, bool current)
    {
        if (evidence is null) { return; }
        evidence.WriteCopy("send_copy", evidence.First,
            $"; control_queue={(result.ControlQueue ? 1 : 0)}; control_ack={(result.ControlAck ? 1 : 0)}" +
            $"; control_copy={(result.ControlCopy ? 1 : 0)}; control_queue_error={result.ControlQueueErrorName}" +
            $"; control_ack_error={result.ControlAckErrorName}; control_copy_error={result.ControlCopyErrorName}",
            diagnosticOnly: !current);
        if (evidence.Second is { } bulk)
        {
            evidence.WriteCopy("send_copy", bulk,
                $"; bulk_copy={(result.BulkCopy ? 1 : 0)}; bulk_copy_error={result.BulkCopyErrorName}",
                diagnosticOnly: !current);
        }
    }
}
