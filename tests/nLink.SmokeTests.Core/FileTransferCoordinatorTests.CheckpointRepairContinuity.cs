using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Fact]
    [Trait("FileTransferCoordinatorSuite", "CheckpointRepairContinuity")]
    [Trait("FileTransferCoordinatorSuite", "CoordinatorSnapshots")]
    public void CheckpointContinuity_RealAcceptanceAndDuplicateValidationRetainExactEvidence()
    {
        var fixture = CreateCheckpointRepairContinuityFixture();
        AssertCheckpointAcceptanceAndDuplicateEvidence(fixture);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertCheckpointAcceptanceAndDuplicateEvidence(
        (FileTransferCoordinatorState State, FallbackFrontierRepairAuthority Authority,
            IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> Envelopes) fixture)
    {
        var state = fixture.State;
        var origin = state.PersistentState.CheckpointRepairContinuityOrigin!;
        var acceptance = fixture.Envelopes.Single(envelope =>
            envelope.DecisionKind == "FallbackCheckpointAccepted");
        var immutableAcceptance = acceptance.ToCanonicalJson();
        var entering = Assert.Single(acceptance.Aggregate.Operations, operation =>
            operation.OperationRole == "checkpoint_acceptance_retained");
        Assert.Equal("None", entering.BeforeState);
        Assert.Equal("Retained", entering.AfterState);
        Assert.True(entering.StateApplied);
        Assert.Equal(acceptance.Aggregate.AfterStateGeneration, origin.Snapshot.AcceptedStateGeneration);
        Assert.Equal(origin.Snapshot.OriginOperationKey, entering.OperationKey);
        Assert.Equal(origin.Snapshot.OriginReceiptFingerprint, entering.ReceiptFingerprint);
        Assert.NotEqual(acceptance.TransitionId, entering.ReceiptFingerprint);

        var duplicate = FileTransferCoordinator.ValidateCheckpointRepairContinuity(state, fixture.Authority);
        Assert.Equal("Validated", duplicate.Status);
        Assert.Same(state, duplicate.State);
        Assert.Same(origin, duplicate.State.PersistentState.CheckpointRepairContinuityOrigin);
        Assert.Equal("Idempotent", duplicate.SnapshotEnvelope!.DecisionStatus);
        Assert.Equal(state.PersistentState.SnapshotStateGeneration, duplicate.SnapshotEnvelope.Aggregate.AfterStateGeneration);
        Assert.Equal(state.PersistentState.SnapshotStateFingerprint, duplicate.SnapshotEnvelope.Aggregate.AfterStateFingerprint);
        Assert.False(Assert.Single(duplicate.SnapshotEnvelope.Aggregate.Operations).StateApplied);
        Assert.Equal(immutableAcceptance, acceptance.ToCanonicalJson());
    }

    [Theory]
    [InlineData("data_identity")]
    [InlineData("data_generation")]
    [InlineData("terminal")]
    [InlineData("publication")]
    [InlineData("runtime")]
    [InlineData("session")]
    [InlineData("transfer")]
    [Trait("FileTransferCoordinatorSuite", "CheckpointRepairContinuity")]
    public void CheckpointContinuity_ScopeMutationRemovesOriginAndCannotReviveThroughAba(string mutation)
    {
        var fixture = CreateCheckpointRepairContinuityFixture();
        var before = fixture.State;
        var changed = mutation switch
        {
            "data_identity" => before with { CurrentDataSessionIdentity = "replacement" },
            "data_generation" => before with { CurrentDataSessionGeneration = before.CurrentDataSessionGeneration + 1 },
            "terminal" => before with { IsTerminal = true, TerminalGeneration = before.TerminalGeneration + 1 },
            "publication" => before with { CurrentRoutePublicationToken = before.CurrentRoutePublicationToken!.Value with
                { PublicationGeneration = before.CurrentRoutePublicationToken.Value.PublicationGeneration + 1 } },
            "runtime" => before with { CurrentRuntimeOwner = before.CurrentRuntimeOwner!.Value with
                { RuntimeGeneration = before.CurrentRuntimeOwner.Value.RuntimeGeneration + 1 } },
            "session" => before with { SessionId = "replacement-session" },
            _ => before with { TransferId = "replacement-transfer" },
        };
        var rejected = FileTransferCoordinator.ValidateCheckpointRepairContinuity(changed, fixture.Authority);
        Assert.Equal("Rejected", rejected.Status);
        Assert.Same(changed, rejected.State);
        Assert.Null(rejected.Receipt);
        Assert.Null(rejected.Stamp);

        var reconciled = FileTransferCoordinator.CreateTransitionSnapshot(before, changed,
            before.PersistentState.CoordinatorInstanceId!, before.PersistentState.SnapshotStateGeneration,
            "CheckpointContinuityScopeMutation", "Accepted", mutation, suppliedOperationsAreComplete: true);
        Assert.Null(reconciled.State.PersistentState.CheckpointRepairContinuityOrigin);
        Assert.Contains(reconciled.Envelope.Aggregate.Operations, operation =>
            operation.OperationRole == "checkpoint_acceptance_retained" && operation.AfterState == "None");
        var restored = before with { PersistentState = reconciled.State.PersistentState };
        var aba = FileTransferCoordinator.ValidateCheckpointRepairContinuity(restored, fixture.Authority);
        Assert.Equal("NotApplicable", aba.Status);
        Assert.Same(restored, aba.State);
        Assert.Null(aba.Stamp);
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "CheckpointRepairContinuity")]
    public void CheckpointContinuity_PauseInvalidationChangesOnlyEvidenceAndCannotResumeOrigin()
    {
        var fixture = CreateCheckpointRepairContinuityFixture();
        var invalidated = FileTransferCoordinator.InvalidateCheckpointRepairContinuity(fixture.State, "user_paused");
        Assert.Equal("Invalidated", invalidated.Status);
        Assert.Null(invalidated.State.PersistentState.CheckpointRepairContinuityOrigin);
        Assert.Equal(fixture.State.CurrentRuntimeOwner, invalidated.State.CurrentRuntimeOwner);
        Assert.Equal(fixture.State.CurrentRoutePublicationToken, invalidated.State.CurrentRoutePublicationToken);
        Assert.Equal(fixture.State.CurrentRepairReplayDecision, invalidated.State.CurrentRepairReplayDecision);
        Assert.Same(fixture.State.CurrentLeg, invalidated.State.CurrentLeg);
        Assert.Equal(fixture.State.PersistentState.SnapshotStateGeneration + 1,
            invalidated.State.PersistentState.SnapshotStateGeneration);
        var duplicate = FileTransferCoordinator.InvalidateCheckpointRepairContinuity(invalidated.State, "user_paused");
        Assert.Same(invalidated.State, duplicate.State);
        Assert.Null(duplicate.SnapshotEnvelope);
        Assert.Equal("NotApplicable", FileTransferCoordinator.ValidateCheckpointRepairContinuity(
            invalidated.State, fixture.Authority).Status);
    }

    [Theory]
    [InlineData("source")]
    [InlineData("generation")]
    [InlineData("transfer")]
    [InlineData("range")]
    [InlineData("checkpoint")]
    [Trait("FileTransferCoordinatorSuite", "CheckpointRepairContinuity")]
    public void CheckpointContinuity_RejectsWrongAlreadyIssuedAuthorityWithoutMutation(string mutation)
    {
        var fixture = CreateCheckpointRepairContinuityFixture();
        var authority = mutation switch
        {
            "source" => fixture.Authority with { SourceKind = FallbackFrontierRepairSourceKind.Unknown },
            "generation" => fixture.Authority with { DecisionGeneration = fixture.Authority.DecisionGeneration + 1 },
            "transfer" => fixture.Authority with { TransferId = "another-transfer" },
            "range" => fixture.Authority with { CanonicalRepairKey = "different-range" },
            _ => fixture.Authority with { CheckpointRequestId = "old-checkpoint" },
        };
        var result = FileTransferCoordinator.ValidateCheckpointRepairContinuity(fixture.State, authority);
        Assert.Equal("Rejected", result.Status);
        Assert.Same(fixture.State, result.State);
        Assert.Null(result.Receipt);
        Assert.Null(result.Stamp);
        Assert.All(result.SnapshotEnvelope!.Aggregate.Operations, operation =>
        {
            Assert.False(operation.StateApplied);
            Assert.False(operation.ReceiptValidated);
        });
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "CheckpointRepairContinuity")]
    public void CheckpointContinuity_NewAcceptanceReplacesOneOriginEvenWhenRequestIdentityIsReused()
    {
        var fixture = CreateCheckpointRepairContinuityFixture();
        var original = fixture.State.PersistentState.CheckpointRepairContinuityOrigin!;
        var serialized = fixture.Envelopes[1].ToCanonicalJson();
        const string requestId = "checkpoint-continuity:accepted:1";
        var requested = RequestFallbackCheckpoint(fixture.State, fixture.State.RouteSelection,
            requestId, fixture.State.CurrentLeg!.TransportEpochId);
        Assert.Null(requested.MutationRejectedReason);
        Assert.Null(requested.State.PersistentState.CheckpointRepairContinuityOrigin);
        var accepted = AcceptFallbackCheckpoint(requested.State, requested.State.RouteSelection,
            requestId, requested.State.CurrentLeg!.TransportEpochId, 100, 255);
        Assert.Null(accepted.MutationRejectedReason);
        var replacement = accepted.State.PersistentState.CheckpointRepairContinuityOrigin!;
        Assert.NotSame(original, replacement);
        Assert.NotEqual(original.Snapshot.AcceptanceIdentity, replacement.Snapshot.AcceptanceIdentity);
        Assert.True(replacement.Snapshot.AcceptedCheckpoint.LeaseGeneration > original.Snapshot.AcceptedCheckpoint.LeaseGeneration);
        Assert.Equal(serialized, fixture.Envelopes[1].ToCanonicalJson());
    }

    [Fact]
    [Trait("FileTransferCoordinatorSuite", "CheckpointRepairContinuity")]
    public void CheckpointContinuity_NullExtensionDoesNotAddOldEnvelopeProperty()
    {
        var fixture = CreateCheckpointRepairContinuityFixture();
        Assert.DoesNotContain("checkpointRepairContinuity", fixture.Envelopes[0].ToCanonicalJson(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("data_session")]
    [InlineData("publication")]
    [InlineData("transport")]
    [Trait("FileTransferCoordinatorSuite", "CheckpointRepairContinuity")]
    public void CheckpointContinuity_IneligibleAcceptanceKeepsRuntimeResultWithoutRetainedEvidence(string mutation)
    {
        var fixture = CreateCheckpointRepairContinuityFixture();
        const string requestId = "checkpoint-continuity-ineligible";
        var requested = RequestFallbackCheckpoint(fixture.State, fixture.State.RouteSelection,
            requestId, fixture.State.CurrentLeg!.TransportEpochId);
        Assert.Null(requested.MutationRejectedReason);
        var state = mutation switch
        {
            "data_session" => requested.State with { CurrentDataSessionIdentity = null, CurrentDataSessionGeneration = 0 },
            "publication" => requested.State with { CurrentRoutePublicationToken = null },
            _ => requested.State,
        };
        var accepted = AcceptFallbackCheckpoint(state, state.RouteSelection, requestId,
            state.CurrentLeg!.TransportEpochId + (mutation == "transport" ? 1 : 0), 100, 255);
        Assert.Null(accepted.MutationRejectedReason);
        Assert.Equal(FallbackCheckpointLeaseState.Accepted, accepted.FallbackCheckpointLease!.Value.State);
        Assert.Null(accepted.State.CurrentFallbackCheckpointLease);
        Assert.Null(accepted.State.PersistentState.CheckpointRepairContinuityOrigin);
        Assert.DoesNotContain(accepted.SnapshotEnvelope!.Aggregate.Operations,
            operation => operation.CheckpointRepairContinuity is not null);
    }
}
