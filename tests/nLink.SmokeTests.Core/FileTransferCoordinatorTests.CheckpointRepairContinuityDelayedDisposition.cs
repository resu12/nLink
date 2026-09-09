using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class FileTransferCoordinatorTests
{
    internal static FileTransferCoordinatorDecision CreateCheckpointContinuityTunaServiceSource(
        string sessionId, string transferId)
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var started = FileTransferCoordinator.Apply(CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer,
            regular, state: FileTransferLegState.Active, canSendData: true),
            EmptyState(regular, FileTransferDirection.Inbound) with { SessionId = sessionId, TransferId = transferId });
        return ActivateInitialResponderTunaForFallbackFixture(started.State);
    }

    internal static (FileTransferCoordinatorDecision RuntimeDecision,
        FileTransferRouteTransitionAuthority Authority, FileTransferRouteTransitionDispositionCommand Disposition)
        CreateCheckpointContinuityFallbackBeforeDisposition(FileTransferCoordinatorState state,
            Action<FileTransferCoordinatorTransitionEnvelope> capture)
    {
        Assert.Equal(FileTransferRoute.FileTunaV4, state.RouteSelection.Route);
        var owner = state.CurrentRuntimeOwner!.Value;
        var publication = state.CurrentRoutePublicationToken!.Value;
        var routeCommit = state.CurrentRouteCommit!.Value;
        var leg = state.CurrentLeg!;
        const long transportEpoch = 3;
        var frame = new PostTunaFallbackPeerProofFrameIdentity("filetransfer_receiver_state_v6", transportEpoch, new string('f', 64));
        var peer = new PostTunaFallbackPeerProofEvidence(state.SessionId, state.TransferId, state.Direction,
            ProofGeneration: 3, TransportEpoch: transportEpoch, ProofKind: "fallback_peer_state",
            Reason: "tuna_disabled", FrameIdentity: frame);
        var request = new FileTransferRouteTransitionPrepareRequest(
            FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6, owner, publication, routeCommit,
            FileTransferRoute.FileTunaV4, FileTransferProtocol.ProtocolVersionV4, FileTransferTransportKind.Tuna,
            new FileTransferFallbackTransitionProofBoundary(routeCommit, peer, frame, transportEpoch, TargetBridgeRecoveryGeneration: 1),
            new FileTransferRouteTransitionDurableCheckpoint(owner, publication, state.TerminalGeneration,
                leg.Generation, leg.TransportEpochId, leg.BridgeRecoveryGeneration, state.CurrentDataSessionGeneration,
                StateEpoch: 3, ContiguousCommittedChunkIndex: 100, DurableHighestChunkIndex: 255,
                ChunkCount: 256, CheckpointGeneration: 1),
            FileTransferRoute.PostTunaFallbackV6, FileTransferProtocol.ProtocolVersionV6,
            FileTransferTransportKind.RegularNkn, FileTransferTransportHandoffKind.TunaToNormalFallback,
            PredecessorTransitionAuthorityId: null, "tuna_disabled");
        var prepared = FileTransferCoordinator.PrepareFileTransferRouteTransition(state, request);
        Assert.True(prepared.Status == FileTransferRouteTransitionDecisionStatus.Prepared, prepared.Reason);
        capture(prepared.SnapshotEnvelope!);
        var authority = prepared.Authority!.Value;
        var draining = FileTransferCoordinator.BeginFileTransferRouteTransitionPhysicalDrain(prepared.State, authority,
            new(prepared.PauseBindCommand!.Value, FileTransferRouteTransitionPauseBindStatus.Installed,
                "filetransfer_route_transition_pause_bound"), 0, [],
            FileTransferCoordinator.CreateFileTransferRouteTransitionPhysicalDrainFingerprint([]));
        Assert.True(draining.Status == FileTransferRouteTransitionDecisionStatus.Draining, draining.Reason);
        capture(draining.SnapshotEnvelope!);
        var drain = draining.DrainCommand!.Value;
        var committed = FileTransferCoordinator.CommitFileTransferRouteTransition(draining.State, authority,
            new(drain, drain.CapturedPhysicalSendLeaseIds, drain.CapturedPhysicalSendCount,
                drain.CapturedPhysicalSendFingerprint, FileTransferRouteTransitionPhysicalDrainOutcome.Completed,
                "physical_drain_completed"), finalProof: null);
        Assert.True(committed.Status == FileTransferRouteTransitionDecisionStatus.Committed, committed.Reason);
        capture(committed.SnapshotEnvelope!);
        var committedState = committed.State;
        if (committed.RetainedFrameApplyCommand is { } frameCommand)
        {
            var applied = FileTransferCoordinator.ApplyFileTransferRouteTransitionRetainedFrameReceipt(committedState,
                new(frameCommand, FileTransferRouteTransitionRetainedFrameApplyStatus.Applied,
                    "filetransfer_route_transition_retained_frame_applied"));
            capture(applied.SnapshotEnvelope!);
            committedState = applied.State;
        }
        Assert.Null(committedState.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value.DispositionReceipt);
        return (Assert.IsType<FileTransferCoordinatorDecision>(committed.RouteDecision) with { State = committedState },
            authority, committed.DispositionCommand!.Value);
    }
}
