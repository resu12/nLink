namespace NLink.Core.FileTransfer;

// Read-only correlation to an already authored snapshot receipt. This is not a
// route token and must never participate in runtime admission or token equality.
internal sealed record FileTransferRoutePublicationEvidence(
    string CoordinatorId,
    string SessionId,
    string TransferId,
    string BoundaryId,
    long StateGeneration,
    long ObservedStateGeneration,
    string OperationKey,
    string TokenFingerprint)
{
    internal string FormatLogSuffix()
        => $"; publication_evidence_version=1; publication_evidence_coordinator_id={CoordinatorId}; " +
           $"publication_evidence_session_id={SessionId}; publication_evidence_transfer_id={TransferId}; " +
           $"publication_evidence_boundary_id={BoundaryId}; publication_evidence_state_generation={StateGeneration}; " +
           $"publication_evidence_observed_state_generation={ObservedStateGeneration}; " +
           $"publication_evidence_operation_key={OperationKey}; publication_evidence_token_fingerprint={TokenFingerprint}";
}
