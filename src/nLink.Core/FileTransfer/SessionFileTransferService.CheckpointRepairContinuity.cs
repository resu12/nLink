using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NLink.Core.FileTransfer;

public sealed partial class SessionFileTransferService
{
    private static void AdoptCoordinatorStateLocked(
        InboundTransferContext context, CheckpointRepairContinuityDecision decision)
        => AdoptCoordinatorAuthoredEnvelopeLocked(
            context.CoordinatorInstanceId, context.SessionId, context.TransferId,
            FileTransferDirection.Inbound, decision.State, decision.SnapshotEnvelope,
            state => context.CoordinatorPersistentState = state.PersistentState,
            context.CoordinatorSnapshotEmitter);

    private static void InvalidateCheckpointRepairContinuityLocked(
        InboundTransferContext context, string reason)
    {
        if (context.CurrentFallbackFrontierRepairContract is { } contract)
        {
            contract.CheckpointContinuityStamp = null;
        }
        if (context.CoordinatorPersistentState.CheckpointRepairContinuityOrigin is null)
        {
            return;
        }
        var decision = FileTransferCoordinator.InvalidateCheckpointRepairContinuity(
            CreateCoordinatorStateLocked(context), reason);
        if (decision.SnapshotEnvelope is not null)
        {
            AdoptCoordinatorStateLocked(context, decision);
        }
    }

    private static CheckpointRepairContinuityStamp? GetCheckpointRepairContinuityStamp(
        FallbackFrontierRepairContract contract, string? requestId, string? correlationId)
        => contract.CheckpointContinuityStamp is { } stamp &&
           string.Equals(stamp.ProtocolRepairRequestId, requestId, StringComparison.Ordinal) &&
           string.Equals(stamp.RequestCorrelationId, correlationId, StringComparison.Ordinal)
            ? stamp : null;

    private static CheckpointRepairContinuityStamp? GetCheckpointRepairContinuityStamp(
        InboundFallbackFrontierProgressReceipt? receipt)
        => receipt is null ? null : GetCheckpointRepairContinuityStamp(
            receipt.Contract, receipt.ProtocolRepairRequestId, receipt.RequestCorrelationId);

    private static string CheckpointContinuityEvidenceToken(string value)
        => string.IsNullOrWhiteSpace(value) || value == "(none)"
            ? "(none)"
            : "fte1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
                .ToLowerInvariant()[..24];

    private static string FormatCheckpointContinuitySource(CheckpointContinuitySourceStamp? stamp)
        => stamp is null ? string.Empty :
            $"continuity_profile={CoordinatorSnapshotCatalogVersion}; " +
            $"continuity_coordinator_id={CheckpointContinuityEvidenceToken(stamp.CoordinatorInstanceId)}; " +
            $"continuity_source_fingerprint={CheckpointContinuityEvidenceToken(stamp.SourceScopeFingerprint)}; ";

    private static string FormatCheckpointRepairContinuity(CheckpointRepairContinuityStamp? stamp)
        => stamp is null ? string.Empty :
            FormatCheckpointContinuitySource(new(stamp.CoordinatorInstanceId, stamp.SourceScopeFingerprint)) +
            $"continuity_acceptance_id={CheckpointContinuityEvidenceToken(stamp.AcceptanceIdentity)}; " +
            $"continuity_accepted_state_generation={stamp.AcceptedStateGeneration.ToString(CultureInfo.InvariantCulture)}; " +
            $"continuity_origin_receipt_id={CheckpointContinuityEvidenceToken(stamp.OriginReceiptIdentity)}; " +
            $"continuity_origin_receipt_fingerprint={CheckpointContinuityEvidenceToken(stamp.OriginReceiptFingerprint)}; " +
            $"continuity_receipt_id={CheckpointContinuityEvidenceToken(stamp.ContinuityReceiptIdentity)}; " +
            $"continuity_receipt_fingerprint={CheckpointContinuityEvidenceToken(stamp.ContinuityReceiptFingerprint)}; " +
            $"continuity_current_scope_fingerprint={CheckpointContinuityEvidenceToken(stamp.CurrentScopeFingerprint)}; " +
            $"continuity_repair_authority_id={CheckpointContinuityEvidenceToken(stamp.RepairAuthorityId)}; " +
            $"continuity_repair_generation={stamp.RepairGeneration.ToString(CultureInfo.InvariantCulture)}; " +
            $"continuity_request_id={CheckpointContinuityEvidenceToken(stamp.ProtocolRepairRequestId)}; " +
            $"continuity_correlation_id={CheckpointContinuityEvidenceToken(stamp.RequestCorrelationId)}; ";
}
