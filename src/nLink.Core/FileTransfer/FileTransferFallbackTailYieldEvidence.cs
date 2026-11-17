using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

// A bounded, process-local diagnostic handoff. The callbacks belong to the
// transport and inspect only the originally captured security installation.
internal sealed class FileTransferFallbackTailYieldEvidence
{
    internal sealed record Copy(string Name, Func<(string Status, string Suffix)> ReadCapture);

    internal string Id { get; } = "ftye1-" + Guid.NewGuid().ToString("N");
    internal string ProofIdentity { get; }
    internal string FrameFingerprint { get; }
    internal Copy First { get; }
    internal Copy? Second { get; }

    internal FileTransferFallbackTailYieldEvidence(
        FileTransferFallbackTailYieldFrameV6 frame, Copy first, Copy? second = null)
    {
        ProofIdentity = SessionFileTransferService.ProjectYieldEvidenceToken(
            SessionFileTransferService.CreateFallbackTailYieldProofIdentity(frame));
        FrameFingerprint = SessionFileTransferService.ProjectYieldEvidenceToken(
            SessionFileTransferService.CreateFallbackTailYieldFrameFingerprint(frame));
        First = first;
        Second = second;
    }

    internal string Status
    {
        get
        {
            var first = First.ReadCapture().Status;
            var second = Second?.ReadCapture().Status;
            return first == "retired" || second == "retired" ? "retired"
                : first == "bound" && (second is null or "bound") ? "bound" : "unbound";
        }
    }

    internal void WriteCopy(string role, Copy copy, string outcomes = "", bool diagnosticOnly = false)
    {
        try
        {
            var capture = copy.ReadCapture();
            Write(role, copy.Name, diagnosticOnly ? "diagnostic_only" : capture.Status,
                capture.Suffix + outcomes);
        }
        catch (Exception)
        {
            // Optional diagnostics cannot change delivery or receipt processing.
        }
    }

    internal void Write(string role, string copy, string status, string suffix)
        => LocalOperationalLog.Info("FileTransferService",
            "event=filetransfer_fallback_tail_yield_evidence; yield_evidence_version=1" +
            $"; yield_evidence_role={role}; yield_evidence_id={Id}; yield_evidence_copy={copy}" +
            $"; yield_evidence_status={status}; yield_proof_identity={ProofIdentity}" +
            $"; yield_frame_fingerprint={FrameFingerprint}{suffix}; authority_effect=none");
}
