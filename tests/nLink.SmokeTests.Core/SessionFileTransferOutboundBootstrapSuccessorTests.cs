using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task OutboundBootstrapSuccessor_RealCoordinatorHistoryStillRejectsPeerFirstAdmission(
        bool stageBeforeEvaluation)
    {
        const string transferId = "outbound_bootstrap_successor_history";
        using var service = new SessionFileTransferService();
        using var bridge = new LoopbackFileTransferTransport($"session_{transferId}");
        service.AttachTransport(bridge);
        var dataSession = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        var context = CreateRegularNknV4OutboundContext(transferId,
            remoteNextExpectedChunkIndex: 8, chunksAcceptedForTransport: 8,
            remoteGrantedUntilExclusive: 8, lastGrantReceivedUtc: DateTimeOffset.UtcNow,
            seedRuntimeOwner: false, chunkCount: 128);
        SetPrivateProperty(context, "DataSession", dataSession);
        SetPrivateProperty(context, "DataSessionGeneration", 17L);
        SetPrivateProperty(context, "V4LastStateEpoch", 9);
        SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 7);
        typeof(SessionFileTransferService).GetField("outboundTransfer",
            BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(service, context);

        var apply = typeof(SessionFileTransferService)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "ApplyCoordinatorDecisionToContextLocked" &&
                method.GetParameters().Length == 4 &&
                method.GetParameters()[0].ParameterType == context.GetType());
        var history = FileTransferCoordinatorTests.CreateOutboundBootstrapSuccessorHistory(
            CreateCoordinatorStateForTest(context), decision =>
            {
                // Copy only Coordinator-produced history, then use the production
                // runtime projection. No synthetic terminal or legacy epoch seeds.
                SetPrivateProperty(context, "CoordinatorPersistentState", decision.State.PersistentState);
                apply.Invoke(null, [context, decision, false, false]);
                Assert.Equal(0L, GetPrivateProperty<long>(context, "LastRecoveredV6TransportEpoch"));
                Assert.Equal(0, GetPrivateProperty<int>(context, "LastRecoveredV6TransportLiveRouteEpochId"));
                Assert.Equal(FileTransferTransportHandoffKind.None,
                    GetPrivateProperty<FileTransferTransportHandoffKind>(context, "LastRecoveredV6TransportEpochKind"));
                Assert.Equal(FileTransferTransportKind.Unknown,
                    GetPrivateProperty<FileTransferTransportKind>(context, "LastRecoveredV6TransportTargetTransport"));
            });
        var state = CreateCoordinatorStateForTest(context);
        var prior = history.PriorCommand;
        Assert.True(FileTransferCoordinator.IsCommittedPostFallbackV6ToTunaV4ReactivationCurrent(
            state, prior, out var successorReason), successorReason);
        Assert.Equal(prior, state.PersistentState.FileTransferRouteTransition
            .LastFallbackV6ProofBootstrapReceipt!.Command);
        Assert.Same(dataSession, GetPrivatePropertyValue(context, "DataSession"));
        var frame = history.OldReceiverState with { Epoch = 10 };
        var evaluation = new FileTransferFallbackV6ProofBootstrapEvaluation(
            state.SessionId, FileTransferDirection.Outbound, ObservationGeneration: 3,
            FallbackEpoch: 2, Source: "test_final_off_peer_first",
            ProofKind: "file_transfer_v6_state_frame_received",
            ObservedUtcMilliseconds: 30_000, TriggerTransferId: transferId,
            TriggerTransportEpoch: frame.TransportEpoch,
            TriggerFrameFingerprint: PostTunaFallbackPeerProofFrameIdentity.Create(frame).Sha256,
            TriggerDataSession: dataSession, TriggerDataSessionGeneration: dataSession.Generation,
            TriggerSourceScope: CreateFallbackV6ProofBootstrapTriggerSourceScope(state));
        Assert.Equal(0, evaluation.TriggerBootstrapGeneration);
        var checkpoint = Assert.IsType<FileTransferRouteTransitionDurableCheckpoint>(
            typeof(SessionFileTransferService).GetMethod("CreateOutboundFileTransferRouteTransitionCheckpoint",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                [context, state.CurrentRuntimeOwner!.Value,
                 state.CurrentRoutePublicationToken!.Value, state.CurrentLeg!]));
        bool Predicate(string name, params object?[] arguments) => Assert.IsType<bool>(
            typeof(SessionFileTransferService).GetMethod(name,
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, arguments));
        Assert.True(Predicate("MatchesFallbackV6ProofBootstrapTrigger",
            evaluation, transferId, dataSession, 17L));
        Assert.True(Predicate("MatchesFallbackV6ProofBootstrapPhysicalTrigger", evaluation, checkpoint));
        Assert.True(Predicate("MatchesFallbackV6ProofBootstrapTriggerSourceScope",
            evaluation, checkpoint, state.CurrentRouteCommit!.Value,
            state.CurrentLeg!.CheckpointRequestId, state.CurrentLeg.CheckpointGeneration));
        Assert.False(Predicate("IsPostRecoveryFileTunaV4LiveActivationEpoch",
            state.CurrentLiveRouteEpoch, 0));
        var stale = typeof(SessionFileTransferService).GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "IsStalePostReactivationFallbackV6Proof" &&
                method.GetParameters()[0].ParameterType == context.GetType());
        Assert.False(Assert.IsType<bool>(stale.Invoke(null, [context, frame.TransportEpoch])));

        void Stage()
        {
            object?[] arguments = [context, frame, null];
            Assert.True(Predicate("TryStageOutboundFallbackV6ProofBootstrapPeerFrameLocked", arguments));
            Assert.Equal("precommand_peer_frame_staged", arguments[2]);
        }
        var process = typeof(SessionFileTransferService).GetMethod(
            "ProcessFallbackV6ProofBootstrapEvaluationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Task Evaluate() => Assert.IsAssignableFrom<Task>(process.Invoke(service, [bridge, evaluation]));
        if (stageBeforeEvaluation)
        {
            Stage();
        }
        await Evaluate();
        if (!stageBeforeEvaluation)
        {
            Stage();
        }
        await Evaluate();

        // Characterize the current failure, not the desired fixed behavior.
        var after = CreateCoordinatorStateForTest(context);
        Assert.Equal(state.PersistentState, after.PersistentState);
        Assert.Equal(state.CurrentRuntimeOwner, after.CurrentRuntimeOwner);
        Assert.Equal(state.CurrentRoutePublicationToken, after.CurrentRoutePublicationToken);
        Assert.Equal(state.CurrentRouteCommit, after.CurrentRouteCommit);
        Assert.Equal(checkpoint, Assert.IsType<FileTransferRouteTransitionDurableCheckpoint>(
            typeof(SessionFileTransferService).GetMethod("CreateOutboundFileTransferRouteTransitionCheckpoint",
                BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null,
                [context, after.CurrentRuntimeOwner!.Value,
                 after.CurrentRoutePublicationToken!.Value, after.CurrentLeg!])));
        Assert.Equal(1, after.PersistentState.FileTransferRouteTransition.LastFallbackV6ProofBootstrapGeneration);
        Assert.Null(after.PersistentState.FileTransferRouteTransition.PendingFallbackV6ProofBootstrap);
        Assert.Null(GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution"));
        Assert.Same(frame, GetPrivatePropertyValue(context, "PendingFallbackV6ProofBootstrapPeerFrameEvidence"));
        Assert.Equal(8, GetPrivateProperty<int>(context, "RemoteNextExpectedChunkIndex"));
        Assert.Equal(8, GetPrivateProperty<int>(context, "ChunksAcceptedForTransport"));
        Assert.Empty(dataSession.SentFrames);
    }
}

public sealed partial class FileTransferCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferCoordinatorSuite", "FileTransferRouteTransition")]
    public void OutboundBootstrapSuccessor_ReceiverStateReplayIsIndistinguishableAfterLocalDelivery(
        bool identicalPayload)
    {
        var history = CreateOutboundBootstrapSuccessorHistory();
        AssertOutboundBootstrapSuccessorReplay(history, identicalPayload);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertOutboundBootstrapSuccessorReplay(
        (FileTransferCoordinatorState State, FileTransferFallbackV6ProofBootstrapCommand PriorCommand,
            FileTransferReceiverStateFrameV6 OldReceiverState) history, bool identicalPayload)
    {
        var state = history.State;
        var prior = history.PriorCommand;
        Assert.True(FileTransferCoordinator.IsCommittedPostFallbackV6ToTunaV4ReactivationCurrent(
            state, prior, out var reason), reason);
        Assert.False(FileTransferCoordinator.TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerBoundary(
            state, history.OldReceiverState, out _, out _));
        // Isolate the proposed admission change: issue through Coordinator directly,
        // without weakening the service guard or declaring it production-authorized.
        var issued = IssueOutboundSuccessorBootstrap(state, fallbackEpoch: 2);
        var command = Assert.IsType<FileTransferFallbackV6ProofBootstrapCommand>(issued.Command);
        Assert.Equal(prior.BootstrapGeneration + 1, command.BootstrapGeneration);
        Assert.NotEqual(prior.AuthorityId, command.AuthorityId);
        Assert.Equal(prior.TargetTransportEpoch, command.TargetTransportEpoch);
        Assert.False(FileTransferCoordinator.TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(
            issued.State, history.OldReceiverState, out _, out var pendingReason));
        Assert.Equal("bootstrap_local_delivery_pending", pendingReason);
        var delivered = DeliverOutboundSuccessorBootstrap(issued);
        var fresh = history.OldReceiverState with
        {
            Epoch = identicalPayload ? history.OldReceiverState.Epoch : history.OldReceiverState.Epoch + 1,
            ContiguousCommittedChunkIndex = identicalPayload ? 8 : 9,
            DurableReceivedHighestChunkIndex = identicalPayload ? 7 : 8,
            CreditUntilChunkIndexExclusive = identicalPayload ? 8 : 9,
            BytesCommitted = (identicalPayload ? 8L : 9L) * 21 * 1024,
        };
        var oldIdentity = PostTunaFallbackPeerProofFrameIdentity.Create(history.OldReceiverState);
        var freshIdentity = PostTunaFallbackPeerProofFrameIdentity.Create(fresh);
        Assert.Equal(identicalPayload, oldIdentity == freshIdentity);
        var before = delivered.State.PersistentState;
        // These assertions deliberately expose the representation gap. They must
        // become rejection assertions when an explicit correlation contract lands.
        Assert.True(FileTransferCoordinator.TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(
            delivered.State, history.OldReceiverState, out var replayBoundary, out var replayReason), replayReason);
        Assert.True(FileTransferCoordinator.TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(
            delivered.State, fresh, out var freshBoundary, out var freshReason), freshReason);
        Assert.Equal(command, replayBoundary.Command);
        Assert.Equal(command, freshBoundary.Command);
        Assert.Equal(2, replayBoundary.PeerProofEvidence.ProofGeneration);
        Assert.Equal(oldIdentity, replayBoundary.PeerFrameIdentity);
        Assert.False(FileTransferCoordinator.TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(
            delivered.State, fresh with { TransportEpoch = command.TargetTransportEpoch + 1 }, out _, out _));
        Assert.False(FileTransferCoordinator.TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(
            delivered.State, fresh with { TransferPaused = false }, out _, out _));
        Assert.False(FileTransferCoordinator.TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(
            delivered.State with { CurrentDataSessionGeneration = state.CurrentDataSessionGeneration + 1 },
            fresh, out _, out _));
        Assert.False(FileTransferCoordinator.TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(
            delivered.State with { IsTerminal = true }, fresh, out _, out _));
        var staleReceipt = FileTransferCoordinator.ApplyFileTransferFallbackV6ProofBootstrapReceipt(
            delivered.State, state.PersistentState.FileTransferRouteTransition.LastFallbackV6ProofBootstrapReceipt!);
        Assert.Same(delivered.State, staleReceipt.State);
        Assert.Equal(delivered.Receipt, staleReceipt.State.PersistentState.FileTransferRouteTransition
            .LastFallbackV6ProofBootstrapReceipt);
        AssertOutboundBootstrapPersistentStateUnchanged(in before, delivered.State);
        Assert.Equal(FileTransferRoute.FileTunaV4, delivered.State.RouteSelection.Route);
        Assert.Equal(3, delivered.State.CurrentLiveRouteEpoch!.EpochId);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void AssertOutboundBootstrapPersistentStateUnchanged(
        in FileTransferCoordinatorPersistentState expected, FileTransferCoordinatorState actual)
        => Assert.Equal(expected, actual.PersistentState);

    internal static (FileTransferCoordinatorState State,
        FileTransferFallbackV6ProofBootstrapCommand PriorCommand,
        FileTransferReceiverStateFrameV6 OldReceiverState)
        CreateOutboundBootstrapSuccessorHistory(FileTransferCoordinatorState? source = null,
            Action<FileTransferCoordinatorDecision>? project = null)
    {
        var regular = FileTransferRouteResolver.Resolve(FileTransferRoute.RegularNknV4Fast);
        var state = source ?? EmptyState(regular) with
        {
            CurrentDataSessionIdentity = "outbound-bootstrap-successor-session",
            CurrentDataSessionGeneration = 17,
        };
        if (source is null)
        {
            state = state with { PersistentState = state.PersistentState with
            {
                CoordinatorInstanceId = "outbound-bootstrap-successor-coordinator",
            } };
        }
        if (state.CurrentRuntimeOwner is null)
        {
            var started = FileTransferCoordinator.Apply(
                CoordinatorEvent(FileTransferCoordinatorEventKind.StartTransfer, regular,
                    state: FileTransferLegState.Active, canSendData: true), state);
            Assert.Null(started.MutationRejectedReason);
            state = started.State;
            project?.Invoke(started);
        }
        var setup = CreateRegularV4ToTunaRouteTransitionSetup(state);
        var firstTuna = CommitEmptyFileTransferRouteTransitionForTest(setup.State,
            setup.Request, setup.FinalProof);
        AssertCommitted(firstTuna.State, FileTransferRouteTransitionKind.RegularV4ToTunaV4, 1);
        project?.Invoke(firstTuna);
        var issued = IssueOutboundSuccessorBootstrap(firstTuna.State, fallbackEpoch: 1);
        var command = Assert.IsType<FileTransferFallbackV6ProofBootstrapCommand>(issued.Command);
        var delivered = DeliverOutboundSuccessorBootstrap(issued);
        var receiverState = new FileTransferReceiverStateFrameV6
        {
            SessionId = state.SessionId, TransferId = state.TransferId,
            Epoch = 9, ContiguousCommittedChunkIndex = 8,
            DurableReceivedHighestChunkIndex = 7, CreditUntilChunkIndexExclusive = 8,
            MissingRanges = [], BytesCommitted = 8L * 21 * 1024,
            TransferPaused = true,
            TransferPauseReason = FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
            TransportEpoch = command.TargetTransportEpoch,
            Priority = FileTransferProtocol.FallbackV6ProofBootstrapPriority,
            RecoveryMode = FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
        };
        Assert.True(FileTransferCoordinator.TryGetCurrentFileTransferFallbackV6ProofBootstrapPeerProofBoundary(
            delivered.State, receiverState, out var peer, out var peerReason), peerReason);
        var request = new FileTransferRouteTransitionPrepareRequest(
            FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6,
            command.SourceRuntimeOwner, command.SourceRoutePublication, command.SourceRouteCommit,
            FileTransferRoute.FileTunaV4, 4, FileTransferTransportKind.Tuna,
            new FileTransferFallbackTransitionProofBoundary(command.SourceRouteCommit,
                peer.PeerProofEvidence, peer.PeerFrameIdentity, command.TargetTransportEpoch,
                TargetBridgeRecoveryGeneration: command.SourceBridgeRecoveryGeneration + 1),
            command.Observation.DurableCheckpoint, FileTransferRoute.PostTunaFallbackV6, 6,
            FileTransferTransportKind.RegularNkn, FileTransferTransportHandoffKind.TunaToNormalFallback,
            PredecessorTransitionAuthorityId: null, "test_bootstrap_successor_fallback");
        var fallback = CommitEmptyFileTransferRouteTransitionForTest(delivered.State, request, finalProof: null);
        AssertCommitted(fallback.State, FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6, 2);
        project?.Invoke(fallback);
        state = fallback.State;
        if (state.CurrentFallbackCheckpointLease is { } lease)
        {
            var accepted = AcceptFallbackCheckpoint(state,
                FileTransferRouteResolver.Resolve(FileTransferRoute.PostTunaFallbackV6),
                lease.CheckpointRequestId, state.CurrentLeg!.TransportEpochId, 8, 8);
            Assert.Null(accepted.MutationRejectedReason);
            state = accepted.State;
        }
        var reactivationEnvelopes = new List<FileTransferCoordinatorTransitionEnvelope>();
        var reactivation = CreateFallbackV6ToTunaRouteTransitionSetup(state, reactivationEnvelopes.Add);
        Assert.Equal(state.CurrentDataSessionIdentity, reactivation.State.CurrentDataSessionIdentity);
        Assert.Equal(state.CurrentDataSessionGeneration, reactivation.State.CurrentDataSessionGeneration);
        var secondTuna = CommitEmptyFileTransferRouteTransitionForTest(reactivation.State,
            reactivation.Request, reactivation.FinalProof);
        AssertCommitted(secondTuna.State, FileTransferRouteTransitionKind.PostTunaFallbackV6ToTunaV4, 3);
        Assert.True(FileTransferCoordinator.IsCommittedPostFallbackV6ToTunaV4ReactivationCurrent(
            secondTuna.State, command, out var reason), reason);
        Assert.Equal(delivered.Receipt, secondTuna.State.PersistentState.FileTransferRouteTransition
            .LastFallbackV6ProofBootstrapReceipt);
        Assert.Equal(0L, secondTuna.State.CurrentLeg!.TransportEpochId);
        project?.Invoke(secondTuna);
        return (secondTuna.State, command, receiverState);

        static void AssertCommitted(FileTransferCoordinatorState current,
            FileTransferRouteTransitionKind kind, int epoch)
        {
            var terminal = current.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value;
            Assert.Equal(kind, terminal.Authority.Kind);
            Assert.Equal(FileTransferRouteTransitionState.Committed, terminal.State);
            Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied, terminal.DispositionReceipt!.Value.Status);
            Assert.Equal(epoch, current.CurrentLiveRouteEpoch!.EpochId);
            Assert.NotNull(terminal.DrainReceipt);
            Assert.NotNull(terminal.RoutePublicationToken);
        }
    }

    private static FileTransferFallbackV6ProofBootstrapDecision IssueOutboundSuccessorBootstrap(
        FileTransferCoordinatorState state, int fallbackEpoch)
    {
        var leg = state.CurrentLeg!;
        var checkpoint = new FileTransferRouteTransitionDurableCheckpoint(
            state.CurrentRuntimeOwner!.Value, state.CurrentRoutePublicationToken!.Value,
            state.TerminalGeneration, leg.Generation, leg.TransportEpochId, leg.BridgeRecoveryGeneration,
            state.CurrentDataSessionGeneration, StateEpoch: 9, ContiguousCommittedChunkIndex: 8,
            DurableHighestChunkIndex: 7, ChunkCount: 128, CheckpointGeneration: 2);
        var issued = FileTransferCoordinator.IssueFileTransferFallbackV6ProofBootstrap(state,
            new FileTransferFallbackV6ProofBootstrapObservation(state.SessionId, state.TransferId,
                FileTransferDirection.Outbound, fallbackEpoch, fallbackEpoch,
                "header_switch_off", "file_transfer_v4_state_frame_received", 10_000 * fallbackEpoch, checkpoint));
        Assert.Equal(FileTransferFallbackV6ProofBootstrapDecisionStatus.Issued, issued.Status);
        return issued;
    }

    private static FileTransferFallbackV6ProofBootstrapDecision DeliverOutboundSuccessorBootstrap(
        FileTransferFallbackV6ProofBootstrapDecision issued)
    {
        var command = issued.Command!;
        var request = new FileTransferFrontierRequestFrameV6
        {
            SessionId = issued.State.SessionId, TransferId = issued.State.TransferId,
            TransportEpoch = command.TargetTransportEpoch,
            RequestCorrelationId = FileTransferProtocol.CreateFallbackV6ProofBootstrapCorrelationId(command.BootstrapGeneration),
            MissingRanges = [], Priority = FileTransferProtocol.FallbackV6ProofBootstrapPriority,
            RecoveryMode = FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
        };
        var delivered = FileTransferCoordinator.ApplyFileTransferFallbackV6ProofBootstrapReceipt(issued.State,
            new FileTransferFallbackV6ProofBootstrapReceipt(command,
                FileTransferFallbackV6ProofBootstrapReceiptStatus.Delivered,
                PostTunaFallbackPeerProofFrameIdentity.Create(request), "filetransfer_fallback_v6_proof_bootstrap_delivered"));
        Assert.Equal(FileTransferFallbackV6ProofBootstrapDecisionStatus.Applied, delivered.Status);
        return delivered;
    }
}
