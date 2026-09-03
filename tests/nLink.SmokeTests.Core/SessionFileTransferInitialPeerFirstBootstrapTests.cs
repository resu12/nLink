using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task InitialPeerFirstFallbackV6Bootstrap_ExactInitialActivation_AdmitsOnceInEitherArrivalOrder(
        bool evaluationFirst,
        bool pendingWrite)
    {
        const string transferId = "initial_peer_first_bootstrap_arrival_order";
        using var service = new SessionFileTransferService();
        using var bridge = new LoopbackFileTransferTransport($"session_{transferId}");
        var dataSession = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(
            service, bridge, dataSession);
        var (frame, evaluation) = CreateInitialPeerFirstBootstrapTrigger(context, dataSession);
        var pendingWrites = GetPrivateProperty<HashSet<int>>(context, "ReceiverSparseChunksPendingWrite");
        if (pendingWrite)
        {
            pendingWrites.Add(120);
        }

        if (!evaluationFirst)
        {
            AssertInitialPeerFirstBootstrapFrameReceived(service, context, frame);
            Assert.Same(frame, GetPrivatePropertyValue(context, "PendingFallbackV6ProofBootstrapPeerFrameEvidence"));
            AssertInitialPeerFirstBootstrapFrameReceived(service, context, frame);
            Assert.Same(frame, GetPrivatePropertyValue(context, "PendingFallbackV6ProofBootstrapPeerFrameEvidence"));
        }

        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        if (pendingWrite)
        {
            Assert.NotNull(GetPrivatePropertyValue(context, "CurrentFallbackV6ProofBootstrapPendingWriteWake"));
            var held = GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState");
            Assert.Null(held.FileTransferRouteTransition.PendingFallbackV6ProofBootstrap);
            Assert.Equal(0, held.FileTransferRouteTransition.LastFallbackV6ProofBootstrapGeneration);
            Assert.Empty(dataSession.SentFrames.OfType<FileTransferTransportEpochFrameV6>());
            await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        }

        if (evaluationFirst)
        {
            AssertInitialPeerFirstBootstrapFrameReceived(service, context, frame);
        }

        if (pendingWrite)
        {
            Assert.Same(frame, GetPrivatePropertyValue(context, "PendingFallbackV6ProofBootstrapPeerFrameEvidence"));
            pendingWrites.Clear();
            object?[] takeArguments = [context, null, null, null];
            var wake = typeof(SessionFileTransferService).GetMethod(
                    "TakeInboundFallbackV6ProofBootstrapPendingWriteWakeAfterDrainLocked",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, takeArguments);
            Assert.NotNull(wake);
            Assert.True(GetPrivateProperty<bool>(wake, "RecheckClaimed"));
            await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod(
                    "ProcessFallbackV6ProofBootstrapPendingWriteRecheckAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [wake]));
        }

        await WaitUntilAsync(
            () => GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState")
                .FileTransferRouteTransition.LastTerminalOperation is
                {
                    Authority.Kind: FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6,
                    DispositionReceipt.Status: FileTransferRouteTransitionDispositionApplyStatus.Applied,
                    RetainedFrameApplyReceipt.Status: FileTransferRouteTransitionRetainedFrameApplyStatus.Applied,
                },
            timeoutMs: 5000);
        var completed = GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState");
        var receipt = Assert.IsType<FileTransferFallbackV6ProofBootstrapReceipt>(
            completed.FileTransferRouteTransition.LastFallbackV6ProofBootstrapReceipt);
        Assert.Equal(FileTransferFallbackV6ProofBootstrapReceiptStatus.Delivered, receipt.Status);
        Assert.Equal(1, receipt.Command.BootstrapGeneration);
        Assert.Equal(1, receipt.Command.Observation.FallbackEpoch);
        Assert.Equal(1, receipt.Command.TargetTransportEpoch);
        Assert.Equal(1, receipt.Command.SourceLiveRouteEpoch);
        Assert.Equal(
            PostTunaFallbackPeerProofFrameIdentity.Create(frame),
            completed.FileTransferRouteTransition.LastTerminalOperation!.Value
                .RetainedFrameApplyReceipt!.Value.Command.FrameIdentity);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6,
            GetPrivateProperty<FileTransferRouteSelection>(context, "RouteSelection").Route);
        Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackV6ProofBootstrapPendingWriteWake"));
        Assert.Null(GetPrivatePropertyValue(context, "PendingFallbackV6ProofBootstrapPeerFrameEvidence"));
        Assert.Null(GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution"));

        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation with { ObservationGeneration = 12 });
        var duplicate = GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState");
        Assert.Equal(receipt, duplicate.FileTransferRouteTransition.LastFallbackV6ProofBootstrapReceipt);
        Assert.Equal(1, duplicate.FileTransferRouteTransition.LastFallbackV6ProofBootstrapGeneration);
        Assert.Single(dataSession.SentFrames.OfType<FileTransferTransportEpochFrameV6>());
        Assert.Single(dataSession.SentFrames.OfType<FileTransferReceiverStateFrameV6>()
            .Where(FileTransferProtocol.IsFallbackV6ProofBootstrapReceiverState));
    }

    [Theory]
    [InlineData("missing_disposition")]
    [InlineData("wrong_fallback_epoch")]
    [InlineData("wrong_bootstrap_generation")]
    [InlineData("wrong_transport_epoch")]
    [InlineData("wrong_source_scope")]
    [InlineData("wrong_data_session")]
    [InlineData("wrong_data_session_generation")]
    [InlineData("bootstrap_history")]
    [InlineData("recovered_history")]
    [InlineData("terminal")]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task InitialPeerFirstFallbackV6Bootstrap_NoncurrentInitialTrigger_HasNoAuthorityEffects(string mismatch)
    {
        var transferId = $"initial_peer_first_bootstrap_{mismatch}";
        using var service = new SessionFileTransferService();
        using var bridge = new LoopbackFileTransferTransport($"session_{transferId}");
        var dataSession = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, dataSession);
        var (_, evaluation) = CreateInitialPeerFirstBootstrapTrigger(context, dataSession);
        var persistent = GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState");
        switch (mismatch)
        {
            case "missing_disposition":
                SetPrivateProperty(context, "CoordinatorPersistentState", persistent with
                {
                    FileTransferRouteTransition = persistent.FileTransferRouteTransition with
                    {
                        LastTerminalOperation = persistent.FileTransferRouteTransition.LastTerminalOperation!.Value with
                        {
                            DispositionReceipt = null,
                        },
                    },
                });
                break;
            case "wrong_fallback_epoch":
                evaluation = evaluation with { FallbackEpoch = 2 };
                break;
            case "wrong_bootstrap_generation":
                evaluation = evaluation with { TriggerBootstrapGeneration = 2 };
                break;
            case "wrong_transport_epoch":
                evaluation = evaluation with { TriggerTransportEpoch = 2 };
                break;
            case "wrong_source_scope":
                evaluation = evaluation with
                {
                    TriggerSourceScope = evaluation.TriggerSourceScope! with { RoutePublicationGeneration = 99 },
                };
                break;
            case "wrong_data_session":
                evaluation = evaluation with
                {
                    TriggerDataSession = new InitialBootstrapRecordingDataSession(dataSession.SessionId, transferId),
                };
                break;
            case "wrong_data_session_generation":
                evaluation = evaluation with { TriggerDataSessionGeneration = 2 };
                break;
            case "bootstrap_history":
                SetPrivateProperty(context, "CoordinatorPersistentState", persistent with
                {
                    FileTransferRouteTransition = persistent.FileTransferRouteTransition with
                    {
                        LastFallbackV6ProofBootstrapGeneration = 1,
                    },
                });
                break;
            case "recovered_history":
                SetPrivateProperty(context, "LastRecoveredV6TransportLiveRouteEpochId", 1);
                break;
            case "terminal":
                SetPrivateProperty(context, "State", FileTransferTransferState.Canceled);
                SetPrivateProperty(context, "TerminalGeneration", 1);
                break;
        }

        GetPrivateProperty<HashSet<int>>(context, "ReceiverSparseChunksPendingWrite").Add(120);
        var before = GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState");
        var ownerBefore = GetPrivatePropertyValue(context, "CurrentRuntimeOwner");
        var publicationBefore = GetPrivatePropertyValue(context, "CurrentRoutePublicationToken");
        var progressBefore = GetPrivateProperty<int>(context, "NextChunkIndex");
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        Assert.Equal(before, GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState"));
        Assert.Equal(ownerBefore, GetPrivatePropertyValue(context, "CurrentRuntimeOwner"));
        Assert.Equal(publicationBefore, GetPrivatePropertyValue(context, "CurrentRoutePublicationToken"));
        Assert.Equal(progressBefore, GetPrivateProperty<int>(context, "NextChunkIndex"));
        Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackV6ProofBootstrapPendingWriteWake"));
        Assert.Null(GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution"));
        Assert.Empty(dataSession.SentFrames.OfType<FileTransferTransportEpochFrameV6>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task InitialPeerFirstFallbackV6Bootstrap_DeferredWakeReplacementOrTerminalization_CannotIssue(bool terminalize)
    {
        const string transferId = "initial_peer_first_bootstrap_stale_wake";
        using var service = new SessionFileTransferService();
        using var bridge = new LoopbackFileTransferTransport($"session_{transferId}");
        var dataSession = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, dataSession);
        var (frame, evaluation) = CreateInitialPeerFirstBootstrapTrigger(context, dataSession);
        var pendingWrites = GetPrivateProperty<HashSet<int>>(context, "ReceiverSparseChunksPendingWrite");
        pendingWrites.Add(120);
        AssertInitialPeerFirstBootstrapFrameReceived(service, context, frame);
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        Assert.NotNull(GetPrivatePropertyValue(context, "CurrentFallbackV6ProofBootstrapPendingWriteWake"));

        if (terminalize)
        {
            SetPrivateProperty(context, "State", FileTransferTransferState.Canceled);
            SetPrivateProperty(context, "TerminalGeneration", 1);
        }
        else
        {
            Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod(
                    "ReplaceInboundDataSessionLocked", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, [context, new InitialBootstrapRecordingDataSession(dataSession.SessionId, transferId)])));
        }

        pendingWrites.Clear();
        object?[] takeArguments = [context, null, null, null];
        Assert.Null(typeof(SessionFileTransferService).GetMethod(
                "TakeInboundFallbackV6ProofBootstrapPendingWriteWakeAfterDrainLocked",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, takeArguments));
        await ProcessInitialPeerFirstBootstrapEvaluationAsync(service, bridge, evaluation);
        var final = GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState");
        Assert.Null(final.FileTransferRouteTransition.PendingFallbackV6ProofBootstrap);
        Assert.Null(final.FileTransferRouteTransition.LastFallbackV6ProofBootstrapReceipt);
        Assert.Equal(0, final.FileTransferRouteTransition.LastFallbackV6ProofBootstrapGeneration);
        Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackV6ProofBootstrapPendingWriteWake"));
        Assert.Null(GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution"));
        Assert.Empty(dataSession.SentFrames.OfType<FileTransferTransportEpochFrameV6>());
    }

    [Theory]
    [InlineData("missing_disposition")]
    [InlineData("authority_session")]
    [InlineData("authority_transfer")]
    [InlineData("authority_direction")]
    [InlineData("current_publication")]
    [InlineData("current_commit")]
    [InlineData("data_session_generation")]
    [InlineData("terminal_generation")]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task InitialPeerFirstFallbackV6Bootstrap_InitialActivationPredicate_IsReadOnlyAndRejectsChangedAuthority(string mismatch)
    {
        var transferId = $"initial_bootstrap_predicate_{mismatch}";
        using var service = new SessionFileTransferService();
        using var bridge = new LoopbackFileTransferTransport($"session_{transferId}");
        var dataSession = new InitialBootstrapRecordingDataSession($"session_{transferId}", transferId);
        var context = await CreateCommittedInitialInboundTunaForBootstrapTestAsync(service, bridge, dataSession);
        var state = CreateCoordinatorStateForTest(context);
        var before = state.PersistentState;
        Assert.True(FileTransferCoordinator.IsCommittedInitialRegularV4ToTunaV4ActivationCurrent(state, out _));
        Assert.Equal(before, state.PersistentState);
        var terminal = state.PersistentState.FileTransferRouteTransition.LastTerminalOperation!.Value;
        var changedTerminal = mismatch switch
        {
            "missing_disposition" => terminal with { DispositionReceipt = null },
            "authority_session" => terminal with { Authority = terminal.Authority with { SessionId = "stale_session" } },
            "authority_transfer" => terminal with { Authority = terminal.Authority with { TransferId = "stale_transfer" } },
            "authority_direction" => terminal with { Authority = terminal.Authority with { Direction = FileTransferDirection.Outbound } },
            _ => terminal,
        };
        var changed = state with
        {
            PersistentState = state.PersistentState with
            {
                FileTransferRouteTransition = state.PersistentState.FileTransferRouteTransition with
                {
                    LastTerminalOperation = changedTerminal,
                },
            },
        };
        changed = mismatch switch
        {
            "current_publication" => changed with { CurrentRoutePublicationToken = state.CurrentRoutePublicationToken!.Value with { PublicationGeneration = 99 } },
            "current_commit" => changed with { CurrentRouteCommit = state.CurrentRouteCommit!.Value with { CommitGeneration = 99 } },
            "data_session_generation" => changed with { CurrentDataSessionGeneration = 2 },
            "terminal_generation" => changed with { TerminalGeneration = state.TerminalGeneration + 1 },
            _ => changed,
        };
        var changedBefore = changed.PersistentState;
        Assert.False(FileTransferCoordinator.IsCommittedInitialRegularV4ToTunaV4ActivationCurrent(changed, out _));
        Assert.Equal(changedBefore, changed.PersistentState);
        Assert.Equal(before.SnapshotStateGeneration, state.PersistentState.SnapshotStateGeneration);
        Assert.Equal(before.SnapshotStateFingerprint, state.PersistentState.SnapshotStateFingerprint);
        Assert.Equal(before, GetPrivateProperty<FileTransferCoordinatorPersistentState>(context, "CoordinatorPersistentState"));
    }

    private static async Task<object> CreateCommittedInitialInboundTunaForBootstrapTestAsync(
        SessionFileTransferService service,
        LoopbackFileTransferTransport bridge,
        InitialBootstrapRecordingDataSession dataSession)
    {
        await Task.Yield();
        const string reason = "test_initial_peer_first_bootstrap";
        service.AttachTransport(bridge);
        var context = CreateRegularNknV4InboundContext(dataSession.TransferId,
            chunkCount: 256, nextChunkIndex: 100,
            durableHighestChunkIndex: 99, creditUntilChunkIndexExclusive: 128);
        SeedInitialRegularV4RuntimeScopeForTest(context, reason);
        SeedRuntimeUnlockAdmissionForTest(context, FileTransferDirection.Inbound, reason);
        SetPrivateProperty(context, "DataSession", dataSession);
        SetPrivateProperty(context, "DataSessionGeneration", 1L);
        SetPrivateProperty(context, "V4StateEpoch", 1);
        SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 99);
        var written = new BitArray(256);
        for (var index = 0; index < 100; index++)
        {
            written[index] = true;
        }
        SetPrivateProperty(context, "ReceiverSparseChunksWritten", written);
        StartCoordinatorSnapshotStreamForTest(context);
        typeof(SessionFileTransferService).GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        bridge.SeedRuntimeUnlockRouteCommitProofForTests(dataSession.TransferId, reason,
            transactionGeneration: 3, offerGeneration: 5, leaseGeneration: 7, launchGeneration: 9);
        var staged = StageInboundFileTransferRouteTransitionForGapTest(service, context,
            Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(bridge.RuntimeUnlockRouteCommitSnapshot), reason);
        Assert.True(staged.Accepted, staged.Reason);
        var execution = GetPrivatePropertyValue(context, "FileTransferRouteTransitionExecution");
        Assert.NotNull(execution);
        await Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod(
                "RecheckInboundFileTransferRouteTransitionDrainAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context, execution]));
        var state = CreateCoordinatorStateForTest(context);
        var terminal = Assert.IsType<FileTransferRouteTransitionOperation>(
            state.PersistentState.FileTransferRouteTransition.LastTerminalOperation);
        Assert.Equal(FileTransferRouteTransitionKind.RegularV4ToTunaV4, terminal.Authority.Kind);
        Assert.Equal(FileTransferRouteTransitionState.Committed, terminal.State);
        Assert.Equal(FileTransferRouteTransitionDispositionApplyStatus.Applied, terminal.DispositionReceipt!.Value.Status);
        Assert.NotNull(terminal.DrainReceipt);
        Assert.NotNull(terminal.RouteCommitToken);
        Assert.NotNull(terminal.RoutePublicationToken);
        Assert.Equal(1, state.CurrentLiveRouteEpoch!.EpochId);
        Assert.Equal(0, state.CurrentLeg!.TransportEpochId);
        Assert.Null(state.PersistentState.FileTransferRouteTransition.LastFallbackV6ProofBootstrapReceipt);
        Assert.Equal(0, state.PersistentState.FileTransferRouteTransition.LastFallbackV6ProofBootstrapGeneration);
        Assert.True(FileTransferCoordinator.IsCommittedInitialRegularV4ToTunaV4ActivationCurrent(state, out var currentReason), currentReason);
        return context;
    }

    private static (FileTransferFrontierRequestFrameV6 Frame, FileTransferFallbackV6ProofBootstrapEvaluation Evaluation)
        CreateInitialPeerFirstBootstrapTrigger(object context, InitialBootstrapRecordingDataSession dataSession)
    {
        var state = CreateCoordinatorStateForTest(context);
        var frame = new FileTransferFrontierRequestFrameV6
        {
            SessionId = dataSession.SessionId,
            TransferId = dataSession.TransferId,
            TransportEpoch = 1,
            RequestCorrelationId = FileTransferProtocol.CreateFallbackV6ProofBootstrapCorrelationId(1),
            MissingRanges = [],
            Priority = FileTransferProtocol.FallbackV6ProofBootstrapPriority,
            RecoveryMode = FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
        };
        return (frame, new FileTransferFallbackV6ProofBootstrapEvaluation(
            dataSession.SessionId, FileTransferDirection.Inbound,
            ObservationGeneration: 11, FallbackEpoch: 1,
            Source: "unit_test_initial_peer_first_bootstrap",
            ProofKind: "file_transfer_v6_frontier_request_frame_received",
            ObservedUtcMilliseconds: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TriggerTransferId: dataSession.TransferId,
            TriggerTransportEpoch: 1,
            TriggerFrameFingerprint: PostTunaFallbackPeerProofFrameIdentity.Create(frame).Sha256,
            TriggerDataSession: dataSession, TriggerDataSessionGeneration: dataSession.Generation,
            TriggerSourceScope: CreateFallbackV6ProofBootstrapTriggerSourceScope(state),
            TriggerBootstrapGeneration: 1));
    }

    private static Task ProcessInitialPeerFirstBootstrapEvaluationAsync(
        SessionFileTransferService service,
        LoopbackFileTransferTransport bridge,
        FileTransferFallbackV6ProofBootstrapEvaluation evaluation)
        => Assert.IsAssignableFrom<Task>(typeof(SessionFileTransferService).GetMethod(
                "ProcessFallbackV6ProofBootstrapEvaluationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [bridge, evaluation]));

    private static void AssertInitialPeerFirstBootstrapFrameReceived(
        SessionFileTransferService service, object context, FileTransferFrontierRequestFrameV6 frame)
    {
        object?[] arguments = [context, frame, null];
        Assert.True(Assert.IsType<bool>(typeof(SessionFileTransferService).GetMethod(
                "TryPromoteInboundFileTunaV4FallbackFromPeerV6Proof", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, arguments)));
    }

    private sealed class InitialBootstrapRecordingDataSession(string sessionId, string transferId) :
        IFileTransferDataSession, IFileTransferDataSessionGenerationProvider
    {
        private readonly RecordingDataSession recording = new(sessionId, transferId);

        public string SessionId => recording.SessionId;
        public string TransferId => recording.TransferId;
        public long Generation => 7;
        public bool IsAvailable => true;
        public IReadOnlyList<FileTransferDataFrame> SentFrames => recording.SentFrames;

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged
        {
            add { }
            remove { }
        }

        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => recording.ReceiveAsync(ct);

        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(CancellationToken ct)
            => recording.ReceiveWithMetadataAsync(ct);

        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
            => recording.SendAsync(frame, ct);

        public void Dispose() => recording.Dispose();
    }
}
