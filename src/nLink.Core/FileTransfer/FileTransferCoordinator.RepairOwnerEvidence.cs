using System.Collections.Immutable;

namespace NLink.Core.FileTransfer;

internal sealed record FileTransferCoordinatorRepairOwnerOperationSnapshot(
    int Version, string CoordinatorInstanceId, string SessionId, string TransferId, string Direction,
    CheckpointRepairContinuityScope Scope, CheckpointRepairContinuityRepairSnapshot Repair,
    ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot> Receipts);

internal sealed record FileTransferRepairOwnerEvidenceReference(
    string CoordinatorInstanceId, string TransitionId, long StateGeneration,
    string OperationKey, string ReceiptIdentity, string ReceiptFingerprint);

internal sealed record FileTransferRepairOwnerEvidenceDecision(
    FileTransferCoordinatorState State, FileTransferCoordinatorTransitionEnvelope? SnapshotEnvelope = null,
    FileTransferRepairOwnerEvidenceReference? Reference = null);

internal static partial class FileTransferCoordinator
{
    // Attests an already-issued repair. This receipt cannot authorize dispatch,
    // settlement, publication, or any other runtime effect.
    internal static FileTransferRepairOwnerEvidenceDecision CaptureRepairOwnerEvidence(
        FileTransferCoordinatorState state, FallbackFrontierRepairAuthority authority)
    {
        if (string.IsNullOrWhiteSpace(state.PersistentState.CoordinatorInstanceId) ||
            authority.IsDefault || authority.Direction != FileTransferDirection.Inbound ||
            authority.SourceKind != FallbackFrontierRepairSourceKind.ReceiverRequest ||
            state.CurrentRepairReplayDecision is not { } decision ||
            !IsRepairReplayDecisionCurrent(state, decision, out _) ||
            string.IsNullOrWhiteSpace(state.CurrentDataSessionIdentity) ||
            state.CurrentDataSessionIdentity == "(none)" || state.CurrentDataSessionGeneration <= 0)
        {
            return new(state);
        }

        var request = new FallbackFrontierRepairAuthorityRequest(authority.CanonicalRepairKey,
            authority.RangeFingerprint, authority.FrontierChunkIndex, authority.DurableReceivedHighestChunkIndex,
            authority.RangeCount, authority.RequestedChunkCount, authority.SourceKind,
            authority.ProtocolRepairRequestId, authority.RequestCorrelationId, authority.CheckpointRequestId,
            authority.Priority, authority.RecoveryMode, authority.DecisionReason);
        var scope = CreateCheckpointRepairContinuityScope(state);
        if (!TryIssueFallbackFrontierRepairAuthority(decision, request, out var expected, out _) ||
            expected != authority || authority.CheckpointRequestId != scope.RetainedLegCheckpointRequestId ||
            authority.BridgeDecisionGeneration != scope.BridgeDecisionGeneration)
        {
            return new(state);
        }

        const string role = "fallback_frontier_repair_owner";
        const string kind = "FallbackFrontierRepairOwnerEvidence";
        var repair = CreateCheckpointRepairContinuityRepairSnapshot(authority);
        var coordinatorId = state.PersistentState.CoordinatorInstanceId!;
        var commandFingerprint = CalculateSnapshotFingerprint(new
        {
            Version = 1, CoordinatorInstanceId = coordinatorId, state.SessionId, state.TransferId,
            state.Direction, Scope = scope, Repair = repair,
        });
        var receipt = new FileTransferCoordinatorReceiptTransitionSnapshot(
            "repair_owner_evidence", commandFingerprint + ":command", authority.DecisionGeneration,
            commandFingerprint, commandFingerprint + ":receipt", authority.DecisionGeneration,
            "Validated", "already_issued_repair_owner", CalculateSnapshotFingerprint(new
            {
                CommandFingerprint = commandFingerprint, Status = "Validated", Reason = "already_issued_repair_owner",
            }), true);
        var payload = new FileTransferCoordinatorRepairOwnerOperationSnapshot(1, coordinatorId,
            state.SessionId, state.TransferId, state.Direction.ToString().ToLowerInvariant(),
            scope, repair, ImmutableArray.Create(receipt));
        var operation = new FileTransferCoordinatorOperationTransitionSnapshot(
            "route_runtime", role, CreateCoordinatorOperationKey("route_runtime", role,
                repair.AuthorityId, repair.DecisionGeneration, kind), kind,
            repair.AuthorityId, repair.PredecessorAuthorityId, repair.DecisionGeneration,
            "Validated", "Validated", receipt.CommandIdentity, receipt.CommandGeneration,
            receipt.CommandFingerprint, receipt.ReceiptIdentity, receipt.ReceiptGeneration,
            receipt.ReceiptStatus, receipt.ReceiptFingerprint, true, false, null, null, RepairOwner: payload);
        var snapshot = CreateTransitionSnapshot(state, state, coordinatorId,
            state.PersistentState.SnapshotStateGeneration, "CaptureRepairOwnerEvidence", "Accepted",
            "already_issued_repair_owner", ImmutableArray.Create(operation), suppliedOperationsAreComplete: true);
        return new(snapshot.State, snapshot.Envelope, new(coordinatorId, snapshot.Envelope.TransitionId,
            snapshot.Envelope.Aggregate.AfterStateGeneration, operation.OperationKey,
            receipt.ReceiptIdentity, receipt.ReceiptFingerprint));
    }
}
