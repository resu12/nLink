using NLink.Core.Logging;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    internal static string ProjectYieldEvidenceToken(string? value)
        => CoordinatorSnapshotStreamEmitter.EvidenceToken(value);

    private static void LogYieldReceiptAssociation(
        FileTransferFallbackTailYieldFrameV6 frame,
        FileTransferFallbackTailYieldEvidence? evidence,
        FileTransferCoordinatorTransitionEnvelope? envelope,
        bool stateChanged,
        string stage,
        string? decisionStatus = null)
    {
        try
        {
            var identity = ProjectYieldEvidenceToken(CreateFallbackTailYieldProofIdentity(frame));
            var fingerprint = ProjectYieldEvidenceToken(CreateFallbackTailYieldFrameFingerprint(frame));
            var status = evidence is null || evidence.ProofIdentity != identity ||
                evidence.FrameFingerprint != fingerprint ? "unbound" : evidence.Status;
            var suffix = $"; yield_receipt_stage={stage}; yield_receipt_state_changed={(stateChanged ? 1 : 0)}" +
                $"; yield_receipt_decision={envelope?.DecisionStatus ?? decisionStatus ?? "not_applied"}" +
                $"; yield_receipt_envelope={(envelope is null ? "missing" : "captured")}";
            if (envelope is not null)
            {
                // Use the returned boundary and the emitter's existing privacy
                // projection, never an earlier accepted decision or route cache.
                var projected = CoordinatorSnapshotStreamEmitter.CreateEvidenceEnvelope(envelope);
                suffix += $"; coordinator_instance_id={projected.CoordinatorInstanceId}" +
                    $"; transition_id={projected.TransitionId}" +
                    $"; coordinator_state_generation={projected.Aggregate.AfterStateGeneration}";
                var matches = projected.Aggregate.Operations
                    .Where(operation => operation.Family == "fallback_tail_yield" && operation.FallbackTailYield is not null)
                    .SelectMany(operation => operation.FallbackTailYield!.Receipts
                        .Where(receipt => receipt.Stage == stage && receipt.ReceiptIdentity == identity)
                        .Select(receipt => (Operation: operation, Receipt: receipt)))
                    .Take(2).ToArray();
                if (envelope.DecisionStatus is not "Accepted")
                {
                    // An idempotent/rejected invocation names its own attempted
                    // receipt, never a retained receipt from an earlier decision.
                    matches = projected.Aggregate.Operations
                        .Where(operation => operation.Family == "fallback_tail_yield" &&
                            operation.OperationRole == "attempted_decision" && operation.FallbackTailYield is not null)
                        .SelectMany(operation => operation.FallbackTailYield!.Receipts
                            .Where(receipt => receipt.Stage == "attempted_decision" && receipt.ReceiptStatus == envelope.DecisionStatus)
                            .Select(receipt => (Operation: operation, Receipt: receipt)))
                        .Take(2).ToArray();
                    if (status == "bound") { status = "diagnostic_only"; }
                }
                if (matches.Length == 1)
                {
                    var (operation, receipt) = matches[0];
                    suffix += $"; operation_key={operation.OperationKey}; operation_authority_id={operation.AuthorityId}" +
                        $"; operation_generation={operation.OperationGeneration}; receipt_identity={receipt.ReceiptIdentity}" +
                        $"; receipt_generation={receipt.ReceiptGeneration}; receipt_status={receipt.ReceiptStatus}" +
                        $"; receipt_fingerprint={receipt.ReceiptFingerprint}; receipt_stage={receipt.Stage}";
                    if (!receipt.ReceiptValidated && status == "bound") { status = "diagnostic_only"; }
                }
                else if (status == "bound") { status = "diagnostic_only"; }
            }
            else if (status == "bound") { status = decisionStatus is null ? "diagnostic_only" : "unbound"; }
            LocalOperationalLog.Info("FileTransferService",
                "event=filetransfer_fallback_tail_yield_evidence; yield_evidence_version=1" +
                $"; yield_evidence_role=receipt_association; yield_evidence_id={evidence?.Id ?? "(none)"}" +
                $"; yield_evidence_copy=none; yield_evidence_status={status}; yield_proof_identity={identity}" +
                $"; yield_frame_fingerprint={fingerprint}{suffix}; authority_effect=none");
        }
        catch (Exception)
        {
            // Capture/projection failure must not change the already-made decision.
        }
    }
}
