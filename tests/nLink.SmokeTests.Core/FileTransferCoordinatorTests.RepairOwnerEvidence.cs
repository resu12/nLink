using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Fact]
    [Trait("FileTransferCoordinatorSuite", "RepairOwnerEvidence")]
    public void RepairOwnerEvidence_AcceptedCheckpointRetainsLegGenerationWithoutReissuingLease()
    {
        var fixture = CreateCheckpointRepairContinuityFixture();
        var evidence = FileTransferCoordinator.CaptureRepairOwnerEvidence(fixture.State, fixture.Authority);
        Assert.Same(fixture.State, evidence.State);
        Assert.Same(fixture.State.PersistentState.CheckpointRepairContinuityOrigin,
            evidence.State.PersistentState.CheckpointRepairContinuityOrigin);
        var scope = Assert.Single(evidence.SnapshotEnvelope!.Aggregate.Operations).RepairOwner!.Scope;
        Assert.True(scope.RetainedLegCheckpointGeneration > 0);
        Assert.Equal(fixture.State.CurrentLeg!.CheckpointGeneration, scope.RetainedLegCheckpointGeneration);
        Assert.Equal(0, scope.Boundary.CheckpointGeneration);
        Assert.Equal(0, scope.LeaseGeneration);
        Assert.Equal("(none)", scope.RetainedLegCheckpointRequestId);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "RepairOwnerEvidence")]
    public void RepairOwnerEvidence_WithoutCheckpointAcceptanceIsReadOnlyAndRoleExact()
    {
        var fixture = CreateRepairOwnerEvidenceFixture();
        var result = FileTransferCoordinator.CaptureRepairOwnerEvidence(fixture.State, fixture.Authority);
        Assert.Same(fixture.State, result.State);
        Assert.Null(result.State.PersistentState.CheckpointRepairContinuityOrigin);
        var operation = Assert.Single(result.SnapshotEnvelope!.Aggregate.Operations);
        Assert.False(operation.StateApplied);
        Assert.True(operation.ReceiptValidated);
        Assert.Equal("Idempotent", result.SnapshotEnvelope.DecisionStatus);
        Assert.Equal(fixture.State.PersistentState.SnapshotStateGeneration,
            result.SnapshotEnvelope.Aggregate.AfterStateGeneration);
        var payload = operation.RepairOwner!;
        Assert.Equal(fixture.State.CurrentLeg!.CheckpointGeneration, payload.Scope.RetainedLegCheckpointGeneration);
        Assert.Equal(0, payload.Scope.Boundary.CheckpointGeneration);
        Assert.Equal(0, payload.Scope.LeaseGeneration);
        Assert.Equal(fixture.Authority.AuthorityId.ToString(), payload.Repair.AuthorityId);
        Assert.Equal(fixture.Authority.RangeFingerprint, payload.Repair.RangeFingerprint);
        Assert.Equal(fixture.Authority.ProtocolRepairRequestId, payload.Repair.ProtocolRepairRequestId);
        Assert.Equal(fixture.Authority.RequestCorrelationId, payload.Repair.RequestCorrelationId);
        Assert.Equal(result.SnapshotEnvelope.TransitionId, result.Reference!.TransitionId);
        Assert.Equal(operation.OperationKey, result.Reference.OperationKey);
        Assert.Equal(operation.ReceiptFingerprint, result.Reference.ReceiptFingerprint);
    }

    [Theory]
    [InlineData("missing_coordinator")]
    [InlineData("missing_repair")]
    [InlineData("terminal")]
    [InlineData("publication")]
    [InlineData("runtime")]
    [InlineData("direction")]
    [InlineData("generation")]
    [InlineData("range")]
    [InlineData("checkpoint")]
    [InlineData("missing_data_session")]
    [Trait("FileTransferCoordinatorSuite", "RepairOwnerEvidence")]
    public void RepairOwnerEvidence_RejectsUnboundOrStaleAuthorityWithoutEffects(string mutation)
    {
        var fixture = CreateRepairOwnerEvidenceFixture();
        var state = fixture.State;
        var authority = fixture.Authority;
        switch (mutation)
        {
            case "missing_coordinator": state = state with { PersistentState = state.PersistentState with { CoordinatorInstanceId = null } }; break;
            case "missing_repair": state = state with { CurrentRepairReplayDecision = null }; break;
            case "terminal": state = state with { IsTerminal = true }; break;
            case "publication": state = state with { CurrentRoutePublicationToken = state.CurrentRoutePublicationToken!.Value with { PublicationGeneration = 99 } }; break;
            case "runtime": state = state with { CurrentRuntimeOwner = state.CurrentRuntimeOwner!.Value with { RuntimeGeneration = 99 } }; break;
            case "direction": authority = authority with { Direction = FileTransferDirection.Outbound }; break;
            case "generation": authority = authority with { DecisionGeneration = authority.DecisionGeneration + 1 }; break;
            case "range": authority = authority with { RangeFingerprint = "frng-replacement" }; break;
            case "checkpoint": authority = authority with { CheckpointRequestId = "replacement" }; break;
            case "missing_data_session": state = state with { CurrentDataSessionIdentity = null }; break;
        }
        var result = FileTransferCoordinator.CaptureRepairOwnerEvidence(state, authority);
        Assert.Same(state, result.State);
        Assert.Null(result.Reference);
        Assert.Null(result.SnapshotEnvelope);
    }

    internal static (FileTransferCoordinatorState State, FallbackFrontierRepairAuthority Authority)
        CreateRepairOwnerEvidenceFixture()
    {
        var established = ActiveFallbackStateWithoutCheckpoint(FileTransferDirection.Inbound);
        var state = established with
        {
            CurrentFallbackCheckpointLease = null,
            CurrentDataSessionIdentity = "repair-owner-data-session",
            CurrentDataSessionGeneration = 1,
            PersistentState = established.PersistentState with
            {
                CoordinatorInstanceId = "repair-owner-coordinator",
                SnapshotStateGeneration = 0,
                SnapshotStateFingerprint = string.Empty,
                CheckpointRepairContinuityOrigin = null,
            },
        };
        var authorized = FileTransferCoordinator.AuthorizeAction(state,
            RecoveryActionRequest(FileTransferCoordinatorActionKind.RepairReplay,
                state.CurrentRuntimeOwner!.Value, state.CurrentLeg!, repairReplayKind: RepairReplayKind.FrontierStallRepair));
        Assert.True(authorized.Accepted, authorized.RejectionReason);
        Assert.True(FileTransferCoordinator.TryIssueFallbackFrontierRepairAuthority(
            authorized.RepairReplayDecision!.Value,
            new("fallback-frontier:100:1:frng-owner", "frng-owner", 100, 255, 1, 1,
                FallbackFrontierRepairSourceKind.ReceiverRequest, "v6-frontier:100:1", "frq-owner",
                "(none)", "frontier", "regular_nkn_frontier_stall_control_bulk", "fallback_frontier_request"),
            out var authority, out var rejection), rejection);
        return (authorized.State, authority!.Value);
    }
}
