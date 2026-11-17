using System.Collections.Immutable;

namespace NLink.Core.FileTransfer;

internal sealed record CheckpointRepairContinuityScope(
    FileTransferCoordinatorStateBoundarySnapshot Boundary,
    string RuntimeOwnerFingerprint,
    string PublicationFingerprint,
    string CommitFingerprint,
    int BridgeDecisionGeneration,
    int AggregateBridgeDecisionGeneration,
    long AggregateTransportEpoch,
    int RetainedLegCheckpointGeneration,
    string RetainedLegCheckpointRequestId,
    int LeaseGeneration,
    string ScopeFingerprint);

internal sealed record CheckpointRepairContinuityAcceptedCheckpoint(
    int LeaseGeneration, int CheckpointGeneration, string CheckpointRequestId,
    string Status, string Reason, string LeaseFingerprint);

internal sealed record CheckpointRepairContinuityRepairSnapshot(
    string AuthorityId, string PredecessorAuthorityId, int DecisionGeneration,
    string CanonicalRepairKey, string RangeFingerprint, int FrontierChunkIndex,
    int DurableReceivedHighestChunkIndex, int RangeCount, int RequestedChunkCount,
    string SourceKind, string TargetTransport, string ProtocolRepairRequestId,
    string RequestCorrelationId, string CheckpointRequestId, string RepairFingerprint);

internal sealed record FileTransferCoordinatorCheckpointRepairContinuityOperationSnapshot(
    string Role, string CoordinatorInstanceId, string SessionId, string TransferId,
    string Direction, long AcceptedStateGeneration, string AcceptanceIdentity,
    string OriginOperationKey, string OriginReceiptIdentity, string OriginReceiptFingerprint,
    CheckpointRepairContinuityScope SourceScope, CheckpointRepairContinuityScope ClearedScope,
    CheckpointRepairContinuityScope CurrentScope,
    CheckpointRepairContinuityAcceptedCheckpoint AcceptedCheckpoint,
    CheckpointRepairContinuityRepairSnapshot? Repair,
    ImmutableArray<FileTransferCoordinatorReceiptTransitionSnapshot> Receipts);

// One retained immutable evidence reference; this is not a runtime authority.
internal sealed record CheckpointRepairContinuityOrigin(
    FileTransferCoordinatorCheckpointRepairContinuityOperationSnapshot Snapshot);

internal sealed record CheckpointContinuitySourceStamp(
    string CoordinatorInstanceId, string SourceScopeFingerprint);

internal sealed record CheckpointRepairContinuityStamp(
    string CoordinatorInstanceId, string SourceScopeFingerprint, string AcceptanceIdentity,
    long AcceptedStateGeneration, string OriginReceiptIdentity, string OriginReceiptFingerprint,
    string ContinuityReceiptIdentity, string ContinuityReceiptFingerprint,
    string CurrentScopeFingerprint, string RepairAuthorityId, int RepairGeneration,
    string ProtocolRepairRequestId, string RequestCorrelationId);

internal sealed record CheckpointRepairContinuityDecision(
    FileTransferCoordinatorState State, string Status, string Reason,
    FileTransferCoordinatorTransitionEnvelope? SnapshotEnvelope,
    FileTransferCoordinatorReceiptTransitionSnapshot? Receipt = null,
    CheckpointRepairContinuityStamp? Stamp = null);

internal static partial class FileTransferCoordinator
{
    private const string CheckpointContinuityKind = "FallbackCheckpointRepairScopeContinuity";
    private const string CheckpointAcceptanceRole = "checkpoint_acceptance_retained";
    private const string CheckpointContinuityAttemptRole = "checkpoint_repair_continuity_attempt";

    internal static CheckpointContinuitySourceStamp? CreateCheckpointContinuitySourceStamp(
        FileTransferCoordinatorState state)
        => string.IsNullOrWhiteSpace(state.PersistentState.CoordinatorInstanceId)
            ? null
            : new(state.PersistentState.CoordinatorInstanceId!,
                CreateCheckpointRepairContinuityScope(state).ScopeFingerprint);

    private static CheckpointRepairContinuityScope CreateCheckpointRepairContinuityScope(
        FileTransferCoordinatorState state)
    {
        var leg = state.CurrentLeg;
        var aggregateBoundary = CreateStateBoundarySnapshot(state);
        var boundary = aggregateBoundary with
        {
            TransportEpoch = leg?.TransportEpochId ?? 0,
            BridgeRecoveryGeneration = leg?.BridgeRecoveryGeneration ?? 0,
        };
        var owner = CalculateSnapshotFingerprint(state.CurrentRuntimeOwner);
        var publication = CalculateSnapshotFingerprint(state.CurrentRoutePublicationToken);
        var commit = CalculateSnapshotFingerprint(state.CurrentRouteCommit);
        var bridgeDecision = state.LastBridgeRecoveryDecisionGeneration;
        var legCheckpointGeneration = leg?.CheckpointGeneration ?? 0;
        var legCheckpointRequest = NormalizeFallbackFrontierRepairCheckpointRequestId(leg?.CheckpointRequestId);
        var leaseGeneration = state.CurrentFallbackCheckpointLease?.LeaseGeneration ?? 0;
        var fingerprint = CalculateSnapshotFingerprint(new
        {
            Boundary = boundary, RuntimeOwnerFingerprint = owner, PublicationFingerprint = publication,
            CommitFingerprint = commit, BridgeDecisionGeneration = bridgeDecision,
            AggregateBridgeDecisionGeneration = aggregateBoundary.BridgeRecoveryGeneration,
            AggregateTransportEpoch = aggregateBoundary.TransportEpoch,
            RetainedLegCheckpointGeneration = legCheckpointGeneration,
            RetainedLegCheckpointRequestId = legCheckpointRequest, LeaseGeneration = leaseGeneration,
        });
        return new(boundary, owner, publication, commit, bridgeDecision,
            aggregateBoundary.BridgeRecoveryGeneration, aggregateBoundary.TransportEpoch,
            legCheckpointGeneration, legCheckpointRequest, leaseGeneration, fingerprint);
    }

    private static FileTransferCoordinatorState RetainCheckpointRepairContinuityOrigin(
        FileTransferCoordinatorState state, CheckpointRepairContinuityScope source,
        FallbackCheckpointLease acceptedLease, FileTransferCoordinatorEvent command)
    {
        var cleared = CreateCheckpointRepairContinuityScope(state);
        if (state.Direction != FileTransferDirection.Inbound ||
            string.IsNullOrWhiteSpace(state.PersistentState.CoordinatorInstanceId) ||
            state.RouteSelection.Route != FileTransferRoute.PostTunaFallbackV6 ||
            state.RouteSelection.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            source.Boundary.Route != state.RouteSelection.TelemetryToken ||
            source.Boundary.ProtocolVersion != FileTransferProtocol.ProtocolVersionV6 ||
            source.Boundary.TransportEpoch <= 0 || source.Boundary.RoutePublicationGeneration <= 0 ||
            source.RuntimeOwnerFingerprint == "(none)" || source.PublicationFingerprint == "(none)" ||
            string.IsNullOrWhiteSpace(source.Boundary.DataSessionIdentity) ||
            source.Boundary.DataSessionIdentity == "(none)" || source.Boundary.DataSessionGeneration <= 0 ||
            acceptedLease.LeaseGeneration <= 0 || acceptedLease.CheckpointGeneration <= 0 ||
            source.LeaseGeneration != acceptedLease.LeaseGeneration ||
            source.Boundary.CheckpointGeneration != acceptedLease.CheckpointGeneration ||
            source.Boundary.CheckpointRequestId != acceptedLease.CheckpointRequestId ||
            !IsOnlyCheckpointScopeCleared(source, cleared))
        {
            return state.PersistentState.CheckpointRepairContinuityOrigin is null ? state :
                state with { PersistentState = state.PersistentState with { CheckpointRepairContinuityOrigin = null } };
        }
        var coordinatorId = state.PersistentState.CoordinatorInstanceId!;
        var generation = checked(state.PersistentState.SnapshotStateGeneration + 1);
        var leaseFingerprint = CalculateSnapshotFingerprint(acceptedLease);
        var identity = CalculateSnapshotFingerprint(new { CoordinatorInstanceId = coordinatorId, LeaseFingerprint = leaseFingerprint });
        var commandFingerprint = CalculateSnapshotFingerprint(command);
        var receiptFingerprint = CalculateSnapshotFingerprint(new
        {
            AcceptanceIdentity = identity, AcceptedStateGeneration = generation,
            CommandFingerprint = commandFingerprint, SourceScope = source, ClearedScope = cleared,
            Status = "Accepted", acceptedLease.Reason,
        });
        var receipt = new FileTransferCoordinatorReceiptTransitionSnapshot("checkpoint_acceptance",
            identity + ":command", generation, commandFingerprint, identity + ":receipt", generation,
            "Accepted", acceptedLease.Reason, receiptFingerprint, true);
        var operationKey = CreateCoordinatorOperationKey("route_runtime", CheckpointAcceptanceRole,
            identity, generation, CheckpointContinuityKind);
        var snapshot = new FileTransferCoordinatorCheckpointRepairContinuityOperationSnapshot(
            CheckpointAcceptanceRole, coordinatorId, state.SessionId, state.TransferId,
            state.Direction.ToString().ToLowerInvariant(), generation, identity, operationKey,
            receipt.ReceiptIdentity, receipt.ReceiptFingerprint, source, cleared, cleared,
            new(acceptedLease.LeaseGeneration, acceptedLease.CheckpointGeneration,
                acceptedLease.CheckpointRequestId, "Accepted", acceptedLease.Reason, leaseFingerprint),
            null, ImmutableArray.Create(receipt));
        return state with
        {
            PersistentState = state.PersistentState with
            {
                CheckpointRepairContinuityOrigin = new(snapshot),
            },
        };
    }

    private static bool IsCheckpointRepairContinuityOriginCurrent(
        FileTransferCoordinatorState state, CheckpointRepairContinuityOrigin origin)
        => !state.IsTerminal && state.Direction == FileTransferDirection.Inbound &&
           state.SessionId == origin.Snapshot.SessionId && state.TransferId == origin.Snapshot.TransferId &&
           state.PersistentState.CoordinatorInstanceId == origin.Snapshot.CoordinatorInstanceId &&
           state.CurrentFallbackCheckpointLease is null &&
           origin.Snapshot.ClearedScope == CreateCheckpointRepairContinuityScope(state);

    private static FileTransferCoordinatorState ReconcileCheckpointRepairContinuityOrigin(
        FileTransferCoordinatorState state)
        => state.PersistentState.CheckpointRepairContinuityOrigin is { } origin &&
           !IsCheckpointRepairContinuityOriginCurrent(state, origin)
            ? state with { PersistentState = state.PersistentState with { CheckpointRepairContinuityOrigin = null } }
            : state;

    internal static CheckpointRepairContinuityDecision InvalidateCheckpointRepairContinuity(
        FileTransferCoordinatorState state, string reason)
    {
        if (state.PersistentState.CheckpointRepairContinuityOrigin is null)
        {
            return new(state, "NotApplicable", reason, null);
        }
        var after = state with
        {
            PersistentState = state.PersistentState with { CheckpointRepairContinuityOrigin = null },
        };
        var snapshot = CreateTransitionSnapshot(state, after, state.PersistentState.CoordinatorInstanceId!,
            state.PersistentState.SnapshotStateGeneration, "CheckpointRepairContinuityInvalidated",
            "Accepted", reason, suppliedOperationsAreComplete: true);
        return new(snapshot.State, "Invalidated", reason, snapshot.Envelope);
    }

    internal static CheckpointRepairContinuityDecision ValidateCheckpointRepairContinuity(
        FileTransferCoordinatorState state, FallbackFrontierRepairAuthority authority)
    {
        var origin = state.PersistentState.CheckpointRepairContinuityOrigin;
        var reason = "checkpoint_acceptance_missing";
        if (origin is null)
        {
            return new(state, "NotApplicable", reason, null);
        }
        var valid = origin is not null && IsCheckpointRepairContinuityValid(state, origin, authority, out reason);
        var status = valid ? "Validated" : origin is null ? "NotApplicable" : "Rejected";
        FileTransferCoordinatorReceiptTransitionSnapshot? receipt = null;
        CheckpointRepairContinuityStamp? stamp = null;
        var operations = ImmutableArray<FileTransferCoordinatorOperationTransitionSnapshot>.Empty;
        if (origin is not null)
        {
            var repair = CreateCheckpointRepairContinuityRepairSnapshot(authority);
            var current = CreateCheckpointRepairContinuityScope(state);
            var commandFingerprint = CalculateSnapshotFingerprint(new
            {
                origin.Snapshot.OriginReceiptFingerprint, origin.Snapshot.AcceptedStateGeneration,
                CurrentScope = current, Repair = repair,
            });
            var commandIdentity = commandFingerprint + ":command";
            var receiptIdentity = commandFingerprint + ":receipt";
            var receiptFingerprint = CalculateSnapshotFingerprint(new { CommandFingerprint = commandFingerprint, Status = status, Reason = reason });
            receipt = new("checkpoint_repair_continuity", commandIdentity, authority.DecisionGeneration,
                commandFingerprint, receiptIdentity, authority.DecisionGeneration,
                status, reason, receiptFingerprint, valid);
            var payload = origin.Snapshot with
            {
                Role = CheckpointContinuityAttemptRole, CurrentScope = current, Repair = repair,
                Receipts = ImmutableArray.Create(receipt),
            };
            var descriptor = CreateCheckpointRepairContinuityDescriptor(payload, status);
            operations = ImmutableArray.Create(CreateOperationTransitionSnapshot(descriptor, status, status, false));
            if (valid)
            {
                stamp = new(payload.CoordinatorInstanceId, payload.SourceScope.ScopeFingerprint,
                    payload.AcceptanceIdentity, payload.AcceptedStateGeneration, payload.OriginReceiptIdentity,
                    payload.OriginReceiptFingerprint, receiptIdentity, receiptFingerprint,
                    current.ScopeFingerprint, repair.AuthorityId, repair.DecisionGeneration,
                    repair.ProtocolRepairRequestId, repair.RequestCorrelationId);
            }
        }
        if (string.IsNullOrWhiteSpace(state.PersistentState.CoordinatorInstanceId))
        {
            return new(state, status, reason, null);
        }
        var snapshot = CreateTransitionSnapshot(state, state, state.PersistentState.CoordinatorInstanceId!,
            state.PersistentState.SnapshotStateGeneration, "CheckpointRepairContinuityValidated",
            valid ? "Accepted" : "Rejected", reason, operations, suppliedOperationsAreComplete: true);
        return new(snapshot.State, status, reason, snapshot.Envelope, valid ? receipt : null, stamp);
    }

    private static bool IsCheckpointRepairContinuityValid(FileTransferCoordinatorState state,
        CheckpointRepairContinuityOrigin origin, FallbackFrontierRepairAuthority authority, out string reason)
    {
        reason = "checkpoint_acceptance_scope_stale";
        if (!IsCheckpointRepairContinuityOriginCurrent(state, origin)) return false;
        var source = origin.Snapshot.SourceScope;
        var cleared = origin.Snapshot.ClearedScope;
        if (!IsOnlyCheckpointScopeCleared(source, cleared))
        {
            reason = "checkpoint_acceptance_changed_non_checkpoint_scope";
            return false;
        }
        reason = "checkpoint_repair_authority_mismatch";
        if (authority.IsDefault || authority.Direction != FileTransferDirection.Inbound ||
            authority.SourceKind != FallbackFrontierRepairSourceKind.ReceiverRequest ||
            state.CurrentRepairReplayDecision is not { } repair ||
            !IsRepairReplayDecisionCurrent(state, repair, out reason)) return false;
        var request = new FallbackFrontierRepairAuthorityRequest(authority.CanonicalRepairKey,
            authority.RangeFingerprint, authority.FrontierChunkIndex, authority.DurableReceivedHighestChunkIndex,
            authority.RangeCount, authority.RequestedChunkCount, authority.SourceKind,
            authority.ProtocolRepairRequestId, authority.RequestCorrelationId, authority.CheckpointRequestId,
            authority.Priority, authority.RecoveryMode, authority.DecisionReason);
        if (!TryIssueFallbackFrontierRepairAuthority(repair, request, out var expected, out reason) ||
            expected != authority || authority.CheckpointRequestId != cleared.RetainedLegCheckpointRequestId ||
            authority.BridgeDecisionGeneration != cleared.BridgeDecisionGeneration ||
            string.IsNullOrWhiteSpace(cleared.Boundary.DataSessionIdentity) ||
            cleared.Boundary.DataSessionIdentity == "(none)" || cleared.Boundary.DataSessionGeneration <= 0)
        {
            reason = "checkpoint_repair_authority_mismatch";
            return false;
        }
        reason = "checkpoint_repair_continuity_validated";
        return true;
    }

    private static bool IsOnlyCheckpointScopeCleared(
        CheckpointRepairContinuityScope source, CheckpointRepairContinuityScope cleared)
        => (source with
        {
            Boundary = source.Boundary with { CheckpointGeneration = 0, CheckpointRequestId = "(none)" },
            RetainedLegCheckpointRequestId = "(none)", LeaseGeneration = 0,
            ScopeFingerprint = cleared.ScopeFingerprint,
        }) == cleared;

    private static CheckpointRepairContinuityRepairSnapshot CreateCheckpointRepairContinuityRepairSnapshot(
        FallbackFrontierRepairAuthority authority)
        => new(authority.AuthorityId.ToString(), authority.PredecessorAuthorityId?.ToString() ?? "(none)",
            authority.DecisionGeneration, authority.CanonicalRepairKey, authority.RangeFingerprint,
            authority.FrontierChunkIndex, authority.DurableReceivedHighestChunkIndex,
            authority.RangeCount, authority.RequestedChunkCount, authority.SourceKind.ToString(),
            authority.TargetTransport.ToString(), authority.ProtocolRepairRequestId,
            authority.RequestCorrelationId, authority.CheckpointRequestId, CalculateSnapshotFingerprint(authority));

    private static CoordinatorOperationDescriptor CreateCheckpointRepairContinuityDescriptor(
        FileTransferCoordinatorCheckpointRepairContinuityOperationSnapshot payload, string state = "Retained")
    {
        var receipt = payload.Receipts[0];
        var identity = payload.Repair?.AuthorityId ?? payload.AcceptanceIdentity;
        var generation = payload.Repair?.DecisionGeneration ?? payload.AcceptedStateGeneration;
        return new("route_runtime", CheckpointContinuityKind, identity,
            payload.Repair?.PredecessorAuthorityId ?? "(none)", generation, state,
            receipt.CommandIdentity, receipt.CommandGeneration, receipt.CommandFingerprint,
            receipt.ReceiptIdentity, receipt.ReceiptGeneration, receipt.ReceiptStatus,
            receipt.ReceiptFingerprint, receipt.ReceiptValidated, payload.Role,
            CreateCoordinatorOperationKey("route_runtime", payload.Role, identity, generation, CheckpointContinuityKind),
            CheckpointRepairContinuity: payload);
    }

    private static void AddCheckpointRepairContinuityDescriptors(FileTransferCoordinatorState state,
        ImmutableArray<CoordinatorOperationDescriptor>.Builder builder)
    {
        if (state.PersistentState.CheckpointRepairContinuityOrigin is { } origin)
            builder.Add(CreateCheckpointRepairContinuityDescriptor(origin.Snapshot));
    }

    private static ImmutableArray<FileTransferCoordinatorOperationTransitionSnapshot>
        CreateCheckpointRepairContinuityTransitions(FileTransferCoordinatorState before, FileTransferCoordinatorState after)
    {
        var prior = before.PersistentState.CheckpointRepairContinuityOrigin;
        var current = after.PersistentState.CheckpointRepairContinuityOrigin;
        if (ReferenceEquals(prior, current)) return ImmutableArray<FileTransferCoordinatorOperationTransitionSnapshot>.Empty;
        var builder = ImmutableArray.CreateBuilder<FileTransferCoordinatorOperationTransitionSnapshot>();
        if (prior is not null)
            builder.Add(CreateOperationTransitionSnapshot(CreateCheckpointRepairContinuityDescriptor(prior.Snapshot), "Retained", "None", true));
        if (current is not null)
            builder.Add(CreateOperationTransitionSnapshot(CreateCheckpointRepairContinuityDescriptor(current.Snapshot), "None", "Retained", true));
        return builder.ToImmutable();
    }
}
