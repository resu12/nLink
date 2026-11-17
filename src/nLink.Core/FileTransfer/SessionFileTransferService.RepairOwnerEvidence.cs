namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private static FileTransferRepairOwnerEvidenceReference? CaptureRepairOwnerEvidenceLocked(
        InboundTransferContext context, FallbackFrontierRepairAuthority authority)
    {
        var evidence = FileTransferCoordinator.CaptureRepairOwnerEvidence(CreateCoordinatorStateLocked(context), authority);
        if (evidence.SnapshotEnvelope is null) { return null; }
        AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId, context.SessionId, context.TransferId, FileTransferDirection.Inbound,
            evidence.State, evidence.SnapshotEnvelope,
            state => context.CoordinatorPersistentState = state.PersistentState, context.CoordinatorSnapshotEmitter);
        return evidence.Reference;
    }

    private static string FormatRepairOwnerEvidence(FileTransferRepairOwnerEvidenceReference? reference)
    {
        if (reference is null) { return string.Empty; }
        var operationKey = reference.OperationKey.Split('|');
        operationKey[2] = CheckpointContinuityEvidenceToken(operationKey[2]);
        return
            $"repair_owner_profile={CoordinatorSnapshotCatalogVersion}; " +
            $"repair_owner_coordinator_id={CheckpointContinuityEvidenceToken(reference.CoordinatorInstanceId)}; " +
            $"repair_owner_transition_id={CheckpointContinuityEvidenceToken(reference.TransitionId)}; " +
            $"repair_owner_state_generation={reference.StateGeneration}; " +
            $"repair_owner_operation_key={string.Join('|', operationKey)}; " +
            $"repair_owner_receipt_id={CheckpointContinuityEvidenceToken(reference.ReceiptIdentity)}; " +
            $"repair_owner_receipt_fingerprint={CheckpointContinuityEvidenceToken(reference.ReceiptFingerprint)}; ";
    }
}
