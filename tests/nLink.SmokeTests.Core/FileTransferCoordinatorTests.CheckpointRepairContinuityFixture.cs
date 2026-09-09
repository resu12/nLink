using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    internal static (FileTransferCoordinatorState State, FallbackFrontierRepairAuthority Authority,
        IReadOnlyList<FileTransferCoordinatorTransitionEnvelope> Envelopes)
        CreateCheckpointRepairContinuityFixture(int frontierChunkIndex = 100, int highestObservedChunkIndex = 255)
    {
        var established = ActiveFallbackStateWithoutCheckpoint(FileTransferDirection.Inbound);
        var state = established with
        {
            CurrentFallbackCheckpointLease = null,
            CurrentDataSessionIdentity = "checkpoint-continuity-data-session",
            CurrentDataSessionGeneration = 1,
            PersistentState = established.PersistentState with
            {
                CoordinatorInstanceId = "checkpoint-continuity-coordinator",
                SnapshotStateGeneration = 0,
                SnapshotStateFingerprint = string.Empty,
                CheckpointRepairContinuityOrigin = null,
            },
        };
        var envelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        const string requestId = "checkpoint-continuity:accepted:1";
        var requested = RequestFallbackCheckpoint(state, state.RouteSelection, requestId, state.CurrentLeg!.TransportEpochId);
        Assert.Null(requested.MutationRejectedReason);
        envelopes.Add(requested.SnapshotEnvelope!);
        var source = FileTransferCoordinator.CreateCheckpointContinuitySourceStamp(requested.State);
        var accepted = AcceptFallbackCheckpoint(requested.State, state.RouteSelection, requestId,
            state.CurrentLeg.TransportEpochId, frontierChunkIndex, highestObservedChunkIndex);
        Assert.Null(accepted.MutationRejectedReason);
        envelopes.Add(accepted.SnapshotEnvelope!);
        Assert.Equal(requested.State.PersistentState.SnapshotStateGeneration + 1,
            accepted.State.PersistentState.SnapshotStateGeneration);
        Assert.Null(accepted.State.CurrentFallbackCheckpointLease);
        Assert.Equal(requested.State.CurrentLeg!.CheckpointGeneration, accepted.State.CurrentLeg!.CheckpointGeneration);

        var authorized = FileTransferCoordinator.AuthorizeAction(accepted.State,
            RecoveryActionRequest(FileTransferCoordinatorActionKind.RepairReplay,
                accepted.State.CurrentRuntimeOwner!.Value, accepted.State.CurrentLeg!,
                repairReplayKind: RepairReplayKind.FrontierStallRepair));
        Assert.True(authorized.Accepted, authorized.RejectionReason);
        envelopes.Add(authorized.SnapshotEnvelope!);
        Assert.True(FileTransferCoordinator.TryIssueFallbackFrontierRepairAuthority(
            authorized.RepairReplayDecision!.Value,
            new($"fallback-frontier:{frontierChunkIndex}:1:frng-continuity", "frng-continuity",
                frontierChunkIndex, highestObservedChunkIndex, 1, 1,
                FallbackFrontierRepairSourceKind.ReceiverRequest, $"v6-frontier:{frontierChunkIndex}:1", "frq-continuity",
                "(none)", "frontier", "regular_nkn_frontier_stall_control_bulk", "fallback_frontier_request"),
            out var authority, out var rejection), rejection);
        var validation = FileTransferCoordinator.ValidateCheckpointRepairContinuity(authorized.State, authority!.Value);
        Assert.Equal("Validated", validation.Status);
        Assert.Same(authorized.State, validation.State);
        Assert.Equal(authorized.State.PersistentState.SnapshotStateGeneration,
            validation.State.PersistentState.SnapshotStateGeneration);
        Assert.Equal(authorized.State.PersistentState.SnapshotStateFingerprint,
            validation.State.PersistentState.SnapshotStateFingerprint);
        Assert.Equal(source!.SourceScopeFingerprint, validation.Stamp!.SourceScopeFingerprint);
        envelopes.Add(validation.SnapshotEnvelope!);
        return (validation.State, authority.Value, envelopes.AsReadOnly());
    }
}
