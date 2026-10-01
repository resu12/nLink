using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    public void FallbackTailYield_EarlyCheckpointClearProofWaitsForExactCheckpointAcceptance(
        FileTransferDirection direction)
        => CreateEarlyCheckpointClearProofSnapshotFixture(direction);

    internal static FileTransferCoordinatorTransitionEnvelope[] CreateEarlyCheckpointClearProofSnapshotFixture(
        FileTransferDirection direction)
    {
        var (source, receipt) = CreateEarlyCheckpointClearProof(direction);
        var lease = source.CurrentFallbackCheckpointLease;
        var retained = FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt(source, receipt);

        Assert.True(retained.Accepted, retained.Reason);
        Assert.Equal(lease, retained.State.CurrentFallbackCheckpointLease);
        Assert.Null(retained.PairedRelease);
        var pending = FileTransferCoordinator.GetFallbackTailYieldSubstate(retained.State);
        Assert.Null(pending.CurrentOperation);
        Assert.Null(pending.PendingPeerProofBeforeLocalYield);
        Assert.Null(pending.PendingRetryRecheckRequest);
        Assert.Null(pending.CurrentReservation);
        Assert.Equal(receipt, pending.PendingPeerProofAwaitingCheckpoint);
        var retainedEnvelope = Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(retained.SnapshotEnvelope);
        var immutableRetained = retainedEnvelope.ToCanonicalJson();
        var heldDescriptor = Assert.Single(retainedEnvelope.Aggregate.Operations,
            operation => operation.OperationRole == "peer_proof_awaiting_checkpoint");
        Assert.Equal("None", heldDescriptor.BeforeState);
        Assert.Equal("Pending", heldDescriptor.AfterState);

        var duplicate = FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt(
            retained.State, receipt with { ObservedUtcMs = 3_100 });
        Assert.Equal(FileTransferFallbackTailYieldTransitionStatus.Idempotent, duplicate.Status);
        var conflict = FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt(
            duplicate.State, receipt with
            {
                Proof = receipt.Proof with { FrameFingerprint = "conflicting-early-proof" },
            });
        Assert.Equal(FileTransferFallbackTailYieldTransitionStatus.Rejected, conflict.Status);
        Assert.Equal(receipt, FileTransferCoordinator.GetFallbackTailYieldSubstate(conflict.State)
            .PendingPeerProofAwaitingCheckpoint);

        var wrongCheckpoint = AcceptFallbackCheckpoint(duplicate.State, source.RouteSelection,
            "unrelated-checkpoint", source.CurrentLeg!.TransportEpochId, 5, 5);
        Assert.Equal("checkpoint_request_mismatch", wrongCheckpoint.MutationRejectedReason);
        Assert.Equal(lease, wrongCheckpoint.State.CurrentFallbackCheckpointLease);
        Assert.Null(FileTransferCoordinator.GetFallbackTailYieldSubstate(wrongCheckpoint.State)
            .PendingPeerProofBeforeLocalYield);

        var accepted = AcceptFallbackCheckpoint(duplicate.State, source.RouteSelection,
            "early-proof-checkpoint", source.CurrentLeg!.TransportEpochId, 5, 5);
        Assert.Null(accepted.MutationRejectedReason);
        var promoted = Assert.IsType<FileTransferFallbackTailPeerProofReceipt>(
            FileTransferCoordinator.GetFallbackTailYieldSubstate(accepted.State)
                .PendingPeerProofBeforeLocalYield);
        Assert.Equal(receipt.Proof, promoted.Proof);
        Assert.Equal(receipt.ObservationIdentity, promoted.ObservationIdentity);
        Assert.Equal(receipt.ObservedUtcMs, promoted.ObservedUtcMs);
        Assert.Equal("(none)", promoted.ObservationScope.CheckpointRequestIdSnapshot);
        Assert.Equal(receipt.ObservationScope.CheckpointGeneration,
            promoted.ObservationScope.CheckpointGeneration);
        Assert.Null(FileTransferCoordinator.GetFallbackTailYieldSubstate(accepted.State)
            .PendingPeerProofAwaitingCheckpoint);
        var acceptedEnvelope = Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(accepted.SnapshotEnvelope);
        var retiredDescriptor = Assert.Single(acceptedEnvelope.Aggregate.Operations,
            operation => operation.OperationRole == "peer_proof_awaiting_checkpoint");
        var promotedDescriptor = Assert.Single(acceptedEnvelope.Aggregate.Operations,
            operation => operation.OperationRole == "peer_proof_pending_initial");
        Assert.Equal("Pending", retiredDescriptor.BeforeState);
        Assert.Equal("None", retiredDescriptor.AfterState);
        Assert.Equal("None", promotedDescriptor.BeforeState);
        Assert.Equal("Pending", promotedDescriptor.AfterState);
        Assert.Equal(retiredDescriptor.AuthorityId, promotedDescriptor.AuthorityId);
        Assert.Equal(immutableRetained, retainedEnvelope.ToCanonicalJson());

        var yielded = MarkFallbackTailQuiescent(accepted.State, source.RouteSelection,
            source.CurrentLeg.TransportEpochId, committedChunk: 5, highestObservedChunk: 5);
        var operation = Assert.IsType<FileTransferFallbackTailYieldOperation>(
            FileTransferCoordinator.GetFallbackTailYieldSubstate(yielded.State).CurrentOperation);
        Assert.Equal(FileTransferFallbackTailYieldPairingState.PeerProofObserved, operation.PairingState);
        Assert.Null(operation.PairedRelease);
        var local = FileTransferCoordinator.CreateFallbackTailLocalProofReceipt(
            yielded.State, operation.Decision, "local-after-checkpoint", "local-frame", 3_200);
        var paired = FileTransferCoordinator.ApplyFallbackTailLocalProofReceipt(yielded.State, local);
        Assert.True(paired.Accepted, paired.Reason);
        Assert.NotNull(paired.PairedRelease);
        return
        [
            retainedEnvelope,
            Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(duplicate.SnapshotEnvelope),
            Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(conflict.SnapshotEnvelope),
            Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(wrongCheckpoint.SnapshotEnvelope),
            acceptedEnvelope,
            Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(yielded.SnapshotEnvelope),
            Assert.IsType<FileTransferCoordinatorTransitionEnvelope>(paired.SnapshotEnvelope),
        ];
    }

    [Theory]
    [InlineData("session")]
    [InlineData("transfer")]
    [InlineData("direction")]
    [InlineData("epoch")]
    [InlineData("transport")]
    [InlineData("owner_key")]
    [InlineData("pair_key")]
    [InlineData("checkpoint")]
    [InlineData("predecessor")]
    [InlineData("authority")]
    [InlineData("observation_scope")]
    [InlineData("observation_owner_key")]
    public void FallbackTailYield_EarlyCheckpointClearProofRejectsNonExactProof(string mismatch)
    {
        var (source, receipt) = CreateEarlyCheckpointClearProof(FileTransferDirection.Outbound);
        var proof = receipt.Proof;
        receipt = receipt with
        {
            Proof = mismatch switch
            {
                "session" => proof with { SessionId = "other-session" },
                "transfer" => proof with { TransferId = "other-transfer" },
                "direction" => proof with { Direction = source.Direction },
                "epoch" => proof with { LiveRouteEpoch = proof.LiveRouteEpoch + 1 },
                "transport" => proof with { TransportEpoch = proof.TransportEpoch + 1 },
                "owner_key" => proof with { OwnerKey = "other-owner" },
                "pair_key" => proof with { PairKey = "other-pair" },
                "checkpoint" => proof with { CheckpointRequestIdSnapshot = "other-checkpoint" },
                "predecessor" => proof with { PredecessorAuthorityId = proof.AuthorityId },
                "authority" => proof with { AuthorityId = new FileTransferAuthorityId("invalid") },
                _ => proof,
            },
        };
        if (mismatch == "observation_scope")
        {
            receipt = receipt with
            {
                ObservationScope = receipt.ObservationScope with { DataSessionGeneration = 99 },
            };
        }
        if (mismatch == "observation_owner_key")
        {
            receipt = receipt with
            {
                ObservationScope = receipt.ObservationScope with { OwnerKey = "forged-observation-owner" },
            };
        }

        var result = FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt(source, receipt);
        Assert.Equal(FileTransferFallbackTailYieldTransitionStatus.Rejected, result.Status);
        Assert.Null(FileTransferCoordinator.GetFallbackTailYieldSubstate(result.State)
            .PendingPeerProofAwaitingCheckpoint);
        Assert.Null(result.PairedRelease);
        Assert.Equal(source.CurrentFallbackCheckpointLease, result.State.CurrentFallbackCheckpointLease);
    }

    [Theory]
    [InlineData("retired")]
    [InlineData("successor")]
    [InlineData("yield_before_acceptance")]
    [InlineData("data_session")]
    [InlineData("publication")]
    [InlineData("runtime")]
    [InlineData("terminal")]
    [InlineData("checkpoint_generation")]
    [InlineData("transport_advance")]
    public void FallbackTailYield_EarlyCheckpointClearProofCannotCrossOtherTransitions(string transition)
    {
        var (source, receipt) = CreateEarlyCheckpointClearProof(FileTransferDirection.Outbound);
        var retained = FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt(source, receipt);
        Assert.True(retained.Accepted, retained.Reason);
        var state = retained.State;
        var epoch = state.CurrentLeg!.TransportEpochId;
        if (transition == "retired")
        {
            var retired = RetireFallbackCheckpoint(state, state.RouteSelection, "early-proof-checkpoint", epoch);
            Assert.Null(retired.MutationRejectedReason);
            state = retired.State;
        }
        else if (transition == "successor")
        {
            var requested = RequestFallbackCheckpoint(state, state.RouteSelection, "successor-checkpoint", epoch);
            Assert.Null(requested.MutationRejectedReason);
            var accepted = AcceptFallbackCheckpoint(requested.State, state.RouteSelection,
                "successor-checkpoint", epoch, 5, 5);
            Assert.Null(accepted.MutationRejectedReason);
            state = accepted.State;
        }
        else if (transition == "yield_before_acceptance")
        {
            var yielded = MarkFallbackTailQuiescent(state, state.RouteSelection, epoch, 5, 5);
            Assert.Null(yielded.MutationRejectedReason);
            state = yielded.State;
        }
        else
        {
            state = transition switch
            {
                "data_session" => state with { CurrentDataSessionGeneration = 99 },
                "publication" => state with
                {
                    CurrentRoutePublicationToken = state.CurrentRoutePublicationToken!.Value with
                    {
                        PublicationGeneration = state.CurrentRoutePublicationToken.Value.PublicationGeneration + 1,
                    },
                },
                "runtime" => state with
                {
                    CurrentRuntimeOwner = state.CurrentRuntimeOwner!.Value with
                    {
                        RuntimeGeneration = state.CurrentRuntimeOwner.Value.RuntimeGeneration + 1,
                    },
                },
                "terminal" => state with { IsTerminal = true, TerminalGeneration = state.TerminalGeneration + 1 },
                _ => state,
            };
            if (transition == "checkpoint_generation") state.CurrentLeg!.CheckpointGeneration++;
            state = AcceptFallbackCheckpoint(state, state.RouteSelection, "early-proof-checkpoint",
                transition == "transport_advance" ? epoch + 1 : epoch, 5, 5).State;
        }

        var substate = FileTransferCoordinator.GetFallbackTailYieldSubstate(state);
        Assert.Null(substate.PendingPeerProofBeforeLocalYield);
        Assert.Null(substate.CurrentOperation?.PeerProof);
        Assert.Null(substate.CurrentOperation?.PairedRelease);
        Assert.Null(substate.CurrentReservation);
        Assert.Null(substate.PendingRetryRecheckRequest);
        if (transition is "retired" or "successor" or "yield_before_acceptance")
            Assert.Null(substate.PendingPeerProofAwaitingCheckpoint);
    }

    internal static FileTransferCoordinatorTransitionEnvelope[] CreateEarlyCheckpointProofDiscardFixture(
        FileTransferDirection direction, string transition)
    {
        var (source, receipt) = CreateEarlyCheckpointClearProof(direction);
        var retained = FileTransferCoordinator.ApplyFallbackTailPeerProofReceipt(source, receipt);
        var state = retained.State;
        var discarded = transition switch
        {
            "retired" => RetireFallbackCheckpoint(state, state.RouteSelection, "early-proof-checkpoint", state.CurrentLeg!.TransportEpochId),
            "successor" => RequestFallbackCheckpoint(state, state.RouteSelection, "next-checkpoint", state.CurrentLeg!.TransportEpochId),
            "yield" => MarkFallbackTailQuiescent(state, state.RouteSelection, state.CurrentLeg!.TransportEpochId, 5, 5),
            _ => throw new ArgumentOutOfRangeException(nameof(transition)),
        };
        Assert.Null(discarded.MutationRejectedReason);
        var substate = FileTransferCoordinator.GetFallbackTailYieldSubstate(discarded.State);
        Assert.Null(substate.PendingPeerProofAwaitingCheckpoint);
        Assert.Null(substate.PendingPeerProofBeforeLocalYield);
        Assert.Null(substate.CurrentOperation?.PeerProof);
        return [retained.SnapshotEnvelope!, discarded.SnapshotEnvelope!];
    }

    private static (FileTransferCoordinatorState State, FileTransferFallbackTailPeerProofReceipt Receipt)
        CreateEarlyCheckpointClearProof(FileTransferDirection direction)
    {
        var source = CreateSource();
        // The peer has independently yielded with a cleared checkpoint. Its proof arrives
        // before the local checkpoint response; it cannot stand in for that response.
        var peerPreview = MarkFallbackTailQuiescent(CreateSource(), source.RouteSelection,
            source.CurrentLeg!.TransportEpochId, committedChunk: 5, highestObservedChunk: 5);
        var proof = CreateCoordinatorFallbackTailPeerProof(peerPreview.State,
            Assert.IsType<FallbackTailYieldDecision>(peerPreview.FallbackTailYieldDecision));
        var requested = RequestFallbackCheckpoint(source, source.RouteSelection,
            "early-proof-checkpoint", source.CurrentLeg.TransportEpochId);
        Assert.Null(requested.MutationRejectedReason);
        var state = requested.State with
        {
            PersistentState = requested.State.PersistentState with
            {
                CoordinatorInstanceId = $"early-checkpoint-proof-{direction}",
                SnapshotStateGeneration = 0,
                FileTransferRouteTransition = default,
            },
        };
        return (state, FileTransferCoordinator.CreateFallbackTailPeerProofReceipt(
            state, proof, "early-checkpoint-clear-proof", 3_000));

        FileTransferCoordinatorState CreateSource() => ActiveFallbackStateWithoutCheckpoint(direction) with
        {
            CurrentDataSessionIdentity = "early-proof-data-session",
            CurrentDataSessionGeneration = 31,
        };
    }
}
