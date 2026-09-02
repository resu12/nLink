using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public void FallbackV6ProofBootstrap_ExactTriggerUsesDataSessionIdentityNotCrossLayerGeneration()
    {
        const string transferId = "transfer_fallback_v6_bootstrap_exact_trigger";
        using var service = new SessionFileTransferService();
        var exactDataSession = new GenerationBoundDataSession(
            $"session_{transferId}",
            transferId,
            generation: 7);
        var context = CreateFileTunaV4InboundContext(
            transferId,
            dataSession: exactDataSession);
        SetPrivateProperty(context, "V4StateEpoch", 1);
        SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 99);
        typeof(SessionFileTransferService)
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var evaluation = new FileTransferFallbackV6ProofBootstrapEvaluation(
            exactDataSession.SessionId,
            FileTransferDirection.Inbound,
            ObservationGeneration: 11,
            FallbackEpoch: 2,
            Source: "unit_test_v6_bulk_then_control",
            ProofKind: "file_transfer_bulk_frame_received",
            ObservedUtcMilliseconds: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            TriggerTransferId: transferId,
            TriggerTransportEpoch: 0,
            TriggerFrameFingerprint: new string('a', 64),
            TriggerDataSession: exactDataSession,
            TriggerDataSessionGeneration: exactDataSession.Generation);
        var issue = typeof(SessionFileTransferService).GetMethod(
            "TryIssueFallbackV6ProofBootstrapLocked",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var replacementDataSession = new GenerationBoundDataSession(
            exactDataSession.SessionId,
            transferId,
            generation: 8);
        object?[] staleArgs =
        [
            evaluation with
            {
                TriggerDataSession = replacementDataSession,
                TriggerDataSessionGeneration = replacementDataSession.Generation,
            },
            null,
            null,
            null,
            null,
            null,
        ];
        Assert.False(Assert.IsType<bool>(issue.Invoke(service, staleArgs)));

        var triggerMatcher = typeof(SessionFileTransferService).GetMethod(
            "MatchesFallbackV6ProofBootstrapTrigger",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True(Assert.IsType<bool>(triggerMatcher.Invoke(
            null,
            [evaluation, transferId, exactDataSession, 1L])));

        object?[] exactArgs = [evaluation, null, null, null, null, null];
        Assert.True(Assert.IsType<bool>(issue.Invoke(service, exactArgs)));
        var command = Assert.IsType<FileTransferFallbackV6ProofBootstrapCommand>(
            exactArgs[1]);
        Assert.Equal(FileTransferDirection.Inbound, command.Observation.Direction);
        Assert.Equal(2, command.Observation.FallbackEpoch);
        Assert.Same(exactDataSession, exactArgs[4]);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task InboundFallbackRetainedFrame_CommittedDuringPromotion_IsNotDispatchedToSuccessorRuntime()
    {
        const string transferId =
            "transfer_inbound_fallback_retained_frame_exclusive_dispatch";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport(
            $"session_{transferId}");
        service.AttachTransport(transport);
        var serviceType = typeof(SessionFileTransferService);
        var context = CreateFileTunaV4InboundContext(transferId);
        SetPrivateProperty(context, "V4StateEpoch", 1);
        SetPrivateProperty(context, "V4LastStateDurableHighestChunkIndex", 99);
        serviceType
            .GetField("inboundTransfer", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var state = CreateCoordinatorStateForTest(context);
        var owner = state.CurrentRuntimeOwner!.Value;
        var publication = state.CurrentRoutePublicationToken!.Value;
        var leg = state.CurrentLeg!;
        var createCheckpoint = serviceType
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method =>
                method.Name == "CreateInboundFileTransferRouteTransitionCheckpoint" &&
                method.GetParameters()[0].ParameterType == context.GetType());
        var checkpoint = Assert.IsType<FileTransferRouteTransitionDurableCheckpoint>(
            createCheckpoint.Invoke(null, [context, owner, publication, leg]));
        var issued = FileTransferCoordinator.IssueFileTransferFallbackV6ProofBootstrap(
            state,
            new FileTransferFallbackV6ProofBootstrapObservation(
                state.SessionId,
                state.TransferId,
                FileTransferDirection.Inbound,
                ObservationGeneration: 1,
                FallbackEpoch: 1,
                Source: "test_receive_loop_exclusive_dispatch",
                ProofKind: "file_transfer_v4_frontier_request_received",
                ObservedUtcMilliseconds: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                checkpoint));
        var command = Assert.IsType<FileTransferFallbackV6ProofBootstrapCommand>(issued.Command);
        var adoptBootstrapDecision = serviceType
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method =>
                method.Name == "AdoptCoordinatorStateLocked" &&
                method.GetParameters() is { Length: 2 } parameters &&
                parameters[0].ParameterType == context.GetType() &&
                parameters[1].ParameterType ==
                    typeof(FileTransferFallbackV6ProofBootstrapDecision));
        _ = adoptBootstrapDecision.Invoke(null, [context, issued]);

        var localReceiverState = new FileTransferReceiverStateFrameV6
        {
            SessionId = state.SessionId,
            TransferId = state.TransferId,
            Epoch = 2,
            ContiguousCommittedChunkIndex = 100,
            DurableReceivedHighestChunkIndex = 99,
            CreditUntilChunkIndexExclusive = 100,
            MissingRanges = [],
            BytesCommitted = 100L * 21 * 1024,
            TransferPaused = true,
            TransferPauseReason = FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
            TransportEpoch = command.TargetTransportEpoch,
            Priority = FileTransferProtocol.FallbackV6ProofBootstrapPriority,
            RecoveryMode = FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
        };
        var deliveredReceipt = new FileTransferFallbackV6ProofBootstrapReceipt(
            command,
            FileTransferFallbackV6ProofBootstrapReceiptStatus.Delivered,
            PostTunaFallbackPeerProofFrameIdentity.Create(localReceiverState),
            "filetransfer_fallback_v6_proof_bootstrap_delivered");
        var applyReceipt = serviceType.GetMethod(
            "TryApplyFallbackV6ProofBootstrapReceiptLocked",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        object?[] receiptArgs = [command, deliveredReceipt, null];
        Assert.True(Assert.IsType<bool>(applyReceipt.Invoke(service, receiptArgs)));

        var retainedRequest = new FileTransferFrontierRequestFrameV6
        {
            SessionId = state.SessionId,
            TransferId = state.TransferId,
            TransportEpoch = command.TargetTransportEpoch,
            RequestCorrelationId =
                FileTransferProtocol.CreateFallbackV6ProofBootstrapCorrelationId(
                    command.BootstrapGeneration),
            MissingRanges = [],
            Priority = FileTransferProtocol.FallbackV6ProofBootstrapPriority,
            RecoveryMode = FileTransferProtocol.FallbackV6ProofBootstrapRecoveryMode,
        };
        var dataSession = new SingleFrameRecordingDataSession(
            state.SessionId,
            state.TransferId,
            retainedRequest);
        SetPrivateProperty(context, "DataSession", dataSession);
        var runtimeKindType = serviceType.GetNestedType(
            "FileTransferSparseCreditRuntimeKind",
            BindingFlags.NonPublic)!;
        var runtimeKind = Enum.Parse(runtimeKindType, "PrimaryRegularNknV4");
        var receiveLoop = serviceType.GetMethod(
            "RunInboundSparseCreditReceiveLoopAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var sessionOpen = new FileTransferSessionOpenV2
        {
            SessionId = state.SessionId,
            TransferId = state.TransferId,
            ProtocolVersion = FileTransferProtocol.ProtocolVersionV4,
            FileTransferRoute = FileTransferRouteResolver.Resolve(FileTransferRoute.FileTunaV4).TelemetryToken,
            ChunkSizeBytes = 21 * 1024,
            InitialPipelineDepth = 1,
        };
        var logStart = GetOperationalLogLength();
        var loopTask = Assert.IsAssignableFrom<Task>(
            receiveLoop.Invoke(service, [context, sessionOpen, runtimeKind]));

        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        string log;
        do
        {
            await Task.Delay(20);
            log = ReadOperationalLogTail(logStart);
        }
        while (!log.Contains(
                   "event=filetransfer_route_transition_disposition_applied;",
                   StringComparison.Ordinal) &&
               DateTimeOffset.UtcNow < deadline);

        Assert.True(
            log.Contains(
                "event=filetransfer_route_transition_retained_frame_applied;",
                StringComparison.Ordinal),
            log);
        Assert.Contains(
            "event=filetransfer_route_transition_disposition_applied;",
            log,
            StringComparison.Ordinal);
        Assert.Contains(
            "decision_status=Claimed; decision_reason=retained_frame_owned_by_route_transition; authority_effect=none",
            log,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "event=filetransfer_v6_regular_nkn_state_refresh_received;",
            log,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "event=filetransfer_route_transition_retained_frame_rejected;",
            log,
            StringComparison.Ordinal);
        Assert.Single(dataSession.SentFrames);

        Assert.IsAssignableFrom<CancellationTokenSource>(
                GetPrivatePropertyValue(context, "LifetimeCts"))
            .Cancel();
        await loopTask.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public void RegularV4ToTunaRouteTransition_InvalidationPreservesUnconsumedPrecommitAuthority(
        FileTransferDirection direction)
    {
        var transferId =
            $"regular_v4_to_tuna_invalidation_{direction.ToString().ToLowerInvariant()}";
        var reason = $"test_{transferId}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport(
            $"session_{transferId}");
        service.AttachTransport(transport);
        object context;
        FileTransferRouteTransitionAuthority authority;
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            reason,
            transactionGeneration: 3,
            offerGeneration: 5,
            leaseGeneration: 7,
            launchGeneration: 9);

        if (direction == FileTransferDirection.Outbound)
        {
            context = CreateRegularNknV4OutboundContext(
                transferId,
                remoteNextExpectedChunkIndex: 4,
                chunksAcceptedForTransport: 4,
                remoteGrantedUntilExclusive: 32,
                lastGrantReceivedUtc: DateTimeOffset.UtcNow);
            SetPrivateProperty(
                context,
                "V4LastStateDurableHighestChunkIndex",
                3);
            typeof(SessionFileTransferService)
                .GetField(
                    "outboundTransfer",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, context);
            var staged = StageRegularV4RouteCommitSendDrain(
                service,
                context,
                reason);
            Assert.True(staged.Accepted);
            authority = AssertFileTransferRouteTransitionOperation(
                    context,
                    FileTransferRouteTransitionState.Draining)
                .Authority;
        }
        else
        {
            context = CreateRegularNknV4InboundContext(
                transferId,
                chunkCount: 64,
                nextChunkIndex: 4,
                durableHighestChunkIndex: 3,
                creditUntilChunkIndexExclusive: 32);
            SeedInitialRegularV4RuntimeScopeForTest(context, reason);
            SeedRuntimeUnlockAdmissionForTest(
                context,
                FileTransferDirection.Inbound,
                reason);
            SetPrivateProperty(context, "HighestBufferedChunkIndex", 3);
            SetPrivateProperty(
                context,
                "DataSession",
                new RecordingDataSession(
                    $"session_{transferId}",
                    transferId));
            SetPrivateProperty(context, "DataSessionGeneration", 1L);
            typeof(SessionFileTransferService)
                .GetField(
                    "inboundTransfer",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(service, context);
            var staged = StageInboundFileTransferRouteTransitionForGapTest(
                service,
                context,
                Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(
                    transport.RuntimeUnlockRouteCommitSnapshot),
                reason);
            Assert.True(staged.Accepted, staged.Reason);
            authority = staged.Command.Authority;
        }

        Assert.NotNull(
            GetPrivatePropertyValue(context, "RuntimeUnlockAdmissionToken"));
        Assert.NotNull(
            GetPrivatePropertyValue(
                context,
                "RuntimeUnlockPendingPreCommitProbe"));

        Assert.True(
            InvalidateFileTransferRouteTransitionForGapTest(
                service,
                context,
                authority,
                reason));

        var retainedAdmission =
            GetPrivateProperty<RuntimeUnlockAdmissionToken>(
                context,
                "RuntimeUnlockAdmissionToken");
        var retainedPrecommit =
            GetPrivateProperty<RuntimeUnlockPendingPreCommitProbeRequest>(
                context,
                "RuntimeUnlockPendingPreCommitProbe");
        var reconstructed = CreateCoordinatorStateForTest(context);
        Assert.Equal(
            retainedAdmission,
            reconstructed.CurrentRuntimeUnlockAdmission);
        Assert.Equal(
            retainedPrecommit,
            reconstructed.CurrentRuntimeUnlockPendingPreCommitProbe);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public void RegularV4ToTunaRouteTransition_OutboundCheckpointRequiresAcceptedPeerState()
    {
        const string transferId =
            "regular_v4_to_tuna_outbound_checkpoint_requires_peer_state";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport(
            $"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 64,
            chunksAcceptedForTransport: 64,
            remoteGrantedUntilExclusive: 128,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        SetPrivateProperty(
            context,
            "V4LastStateDurableHighestChunkIndex",
            63);
        SetPrivateProperty(context, "V4LastStateEpoch", -1);
        typeof(SessionFileTransferService)
            .GetField(
                "outboundTransfer",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "test_outbound_checkpoint_requires_peer_state",
            transactionGeneration: 7,
            offerGeneration: 11,
            leaseGeneration: 13,
            launchGeneration: 17);

        var withoutPeerState = StageRegularV4RouteCommitSendDrain(
            service,
            context,
            "test_outbound_checkpoint_requires_peer_state",
            seedAcceptedPeerState: false);

        Assert.False(withoutPeerState.Accepted);
        Assert.Null(withoutPeerState.Drain);
        Assert.Null(
            GetPrivateProperty<FileTransferCoordinatorPersistentState>(
                    context,
                    "CoordinatorPersistentState")
                .FileTransferRouteTransition.CurrentOperation);

        SetPrivateProperty(context, "V4LastStateEpoch", 1);

        var withPeerState = StageRegularV4RouteCommitSendDrain(
            service,
            context,
            "test_outbound_checkpoint_requires_peer_state");

        Assert.True(withPeerState.Accepted);
        var operation =
            GetPrivateProperty<FileTransferCoordinatorPersistentState>(
                    context,
                    "CoordinatorPersistentState")
                .FileTransferRouteTransition.CurrentOperation;
        Assert.True(operation.HasValue);
        Assert.Equal(
            FileTransferRouteTransitionState.Draining,
            operation.Value.State);
        Assert.Equal(1, operation.Value.Authority.DurableCheckpoint.StateEpoch);
        Assert.Equal(
            64,
            operation.Value.Authority.DurableCheckpoint
                .ContiguousCommittedChunkIndex);
        Assert.Equal(
            63,
            operation.Value.Authority.DurableCheckpoint
                .DurableHighestChunkIndex);

        var currentnessMethod = typeof(SessionFileTransferService)
            .GetMethod(
                "IsOutboundFileTransferRouteTransitionDurableCheckpointCurrentLocked",
                BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True(Assert.IsType<bool>(
            currentnessMethod.Invoke(
                null,
                [context, operation.Value.Authority.DurableCheckpoint])));

        SetPrivateProperty(context, "V4LastStateEpoch", -1);

        Assert.False(Assert.IsType<bool>(
            currentnessMethod.Invoke(
                null,
                [context, operation.Value.Authority.DurableCheckpoint])));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public void RegularV4ToTunaRouteTransition_InboundCheckpointExcludesObservedAndPendingWrites()
    {
        const string transferId =
            "regular_v4_to_tuna_inbound_durable_checkpoint";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport(
            $"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4InboundContext(
            transferId,
            chunkCount: 64,
            nextChunkIndex: 4,
            durableHighestChunkIndex: 20,
            creditUntilChunkIndexExclusive: 32);
        SeedInitialRegularV4RuntimeScopeForTest(
            context,
            "test_inbound_durable_checkpoint");
        SeedRuntimeUnlockAdmissionForTest(
            context,
            FileTransferDirection.Inbound,
            "test_inbound_durable_checkpoint");
        SetPrivateProperty(context, "V4StateEpoch", 9);
        SetPrivateProperty(context, "HighestBufferedChunkIndex", 8);
        var written = new BitArray(64);
        for (var index = 0; index < 4; index++)
        {
            written[index] = true;
        }

        written[8] = true;
        SetPrivateProperty(context, "ReceiverSparseChunksWritten", written);
        var pendingWrites = GetPrivateProperty<HashSet<int>>(
            context,
            "ReceiverSparseChunksPendingWrite");
        pendingWrites.Add(20);
        pendingWrites.Add(21);
        var excludedPendingWrites = pendingWrites.ToArray();
        var dataSession = new RecordingDataSession(
            $"session_{transferId}",
            transferId);
        SetPrivateProperty(context, "DataSession", dataSession);
        SetPrivateProperty(context, "DataSessionGeneration", 1L);
        typeof(SessionFileTransferService)
            .GetField(
                "inboundTransfer",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "test_inbound_durable_checkpoint",
            transactionGeneration: 7,
            offerGeneration: 11,
            leaseGeneration: 13,
            launchGeneration: 17);

        var staged = StageInboundFileTransferRouteTransitionForGapTest(
            service,
            context,
            Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(
                transport.RuntimeUnlockRouteCommitSnapshot),
            "test_inbound_durable_checkpoint");

        Assert.False(staged.Accepted);
        Assert.Null(
            GetPrivateProperty<FileTransferCoordinatorPersistentState>(
                    context,
                    "CoordinatorPersistentState")
                .FileTransferRouteTransition.CurrentOperation);

        pendingWrites.Clear();
        staged = StageInboundFileTransferRouteTransitionForGapTest(
            service,
            context,
            Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(
                transport.RuntimeUnlockRouteCommitSnapshot),
            "test_inbound_durable_checkpoint");

        Assert.True(staged.Accepted, staged.Reason);
        Assert.Equal(
            FileTransferDirection.Inbound,
            staged.Command.Authority.Direction);
        Assert.Equal(
            FileTransferRouteTransitionState.Draining,
            GetPrivateProperty<FileTransferCoordinatorPersistentState>(
                    context,
                    "CoordinatorPersistentState")
                .FileTransferRouteTransition.CurrentOperation!.Value.State);
        Assert.Equal(
            8,
            staged.Command.Authority.DurableCheckpoint
                .DurableHighestChunkIndex);
        Assert.NotEqual(
            GetPrivateProperty<int>(
                context,
                "PullHighestReceivedChunkIndex"),
            staged.Command.Authority.DurableCheckpoint
                .DurableHighestChunkIndex);
        Assert.DoesNotContain(
            staged.Command.Authority.DurableCheckpoint
                .DurableHighestChunkIndex,
            excludedPendingWrites);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "SparseWorkerHandoff")]
    public async Task RegularV4ToTunaRouteTransition_AcceptedInboundDrainPausesCoreTransport()
    {
        const string transferId =
            "regular_v4_to_tuna_inbound_draining_pause";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport(
            $"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4InboundContext(
            transferId,
            chunkCount: 64,
            nextChunkIndex: 4,
            durableHighestChunkIndex: 3,
            creditUntilChunkIndexExclusive: 32);
        SeedInitialRegularV4RuntimeScopeForTest(
            context,
            "test_inbound_draining_pause");
        SeedRuntimeUnlockAdmissionForTest(
            context,
            FileTransferDirection.Inbound,
            "test_inbound_draining_pause");
        SetPrivateProperty(context, "HighestBufferedChunkIndex", 3);
        var dataSession = new RecordingDataSession(
            $"session_{transferId}",
            transferId);
        SetPrivateProperty(context, "DataSession", dataSession);
        SetPrivateProperty(context, "DataSessionGeneration", 1L);
        typeof(SessionFileTransferService)
            .GetField(
                "inboundTransfer",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        transport.SeedRuntimeUnlockRouteCommitProofForTests(
            transferId,
            "test_inbound_draining_pause",
            transactionGeneration: 3,
            offerGeneration: 5,
            leaseGeneration: 7,
            launchGeneration: 9);

        var staged = StageInboundFileTransferRouteTransitionForGapTest(
            service,
            context,
            Assert.IsType<RuntimeUnlockRouteCommitSnapshot>(
                transport.RuntimeUnlockRouteCommitSnapshot),
            "test_inbound_draining_pause");

        Assert.True(staged.Accepted, staged.Reason);
        var transition =
            GetPrivateProperty<FileTransferCoordinatorPersistentState>(
                    context,
                    "CoordinatorPersistentState")
                .FileTransferRouteTransition;
        Assert.Equal(
            FileTransferRouteTransitionState.Draining,
            transition.CurrentOperation!.Value.State);
        Assert.True(GetPrivateProperty<bool>(
            context,
            "PullTransportPaused"));
        Assert.Equal(
            "filetransfer_route_transition_draining",
            GetPrivateProperty<string>(
                context,
                "PullTransportPauseReason"));
        Assert.False(GetPrivateProperty<bool>(
            context,
            "PullTransportResumeRequestPending"));

        var expectedDeadline =
            (DateTimeOffset?)GetPrivatePropertyValue(
                context,
                "PullTransportGraceDeadlineUtc");
        var expectedTerminalGeneration =
            GetPrivateProperty<int>(context, "TerminalGeneration");
        var pausedHandler = typeof(SessionFileTransferService).GetMethod(
            "HandlePausedInboundTransportAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.False(
            await (Task<bool>)pausedHandler.Invoke(service, [context])!);
        Assert.False(
            await (Task<bool>)pausedHandler.Invoke(service, [context])!);
        Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
        Assert.Equal(
            expectedTerminalGeneration,
            GetPrivateProperty<int>(context, "TerminalGeneration"));
        Assert.Equal(
            expectedDeadline,
            (DateTimeOffset?)GetPrivatePropertyValue(
                context,
                "PullTransportGraceDeadlineUtc"));
        Assert.Equal(
            FileTransferRouteTransitionState.Draining,
            GetPrivateProperty<FileTransferCoordinatorPersistentState>(
                    context,
                    "CoordinatorPersistentState")
                .FileTransferRouteTransition.CurrentOperation!.Value.State);
        Assert.Contains(
            "|Draining|Held|",
            GetPrivateProperty<string>(
                context,
                "FileTransferRouteTransitionTransportPauseEvidenceKey"),
            StringComparison.Ordinal);

        using var sparseWrite = new BlockingSparseWriteStream();
        SetPrivateProperty(context, "WriteStream", sparseWrite);
        SetPrivateProperty(context, "PullManifestReceived", true);
        SetPrivateProperty(context, "ReceiverSparseWriteActive", true);
        var writtenChunks = new BitArray(64);
        for (var index = 0; index < 4; index++)
        {
            writtenChunks[index] = true;
        }
        SetPrivateProperty(
            context,
            "ReceiverSparseChunksWritten",
            writtenChunks);
        var chunkSizeBytes = GetPrivateProperty<int>(context, "ChunkSizeBytes");
        var sparseBatch = new FileTransferChunkBatchFrameV4
        {
            SessionId = $"session_{transferId}",
            TransferId = transferId,
            StartChunkIndex = 4,
            ChunkCount = 1,
            DataSegments = [new byte[chunkSizeBytes]],
            BatchProfile = "v4_default_21k",
        };
        var sparseWriteTask = Assert.IsAssignableFrom<Task>(
            typeof(SessionFileTransferService)
                .GetMethod(
                    "HandleInboundV4ChunkBatchAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(
                    service,
                    [
                        context,
                        sparseBatch,
                        FileTransferTransportKind.RegularNkn,
                    ])!);
        await sparseWrite.WaitForWriteStartedAsync();
        var pendingSparseWrites = GetPrivateProperty<HashSet<int>>(
            context,
            "ReceiverSparseChunksPendingWrite");
        Assert.Contains(4, pendingSparseWrites);

        var logStart = GetOperationalLogLength();
        var serviceGate = typeof(SessionFileTransferService)
            .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        lock (serviceGate)
        {
            Assert.False(Assert.IsType<bool>(
                typeof(SessionFileTransferService)
                    .GetMethod(
                        "TryQueueInboundFileTransferRouteTransitionDrainAfterOwnerWorkClearedLocked",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(
                        service,
                        [
                            context,
                            "receiver_sparse_pending_write_cleared",
                        ])));
        }

        sparseWrite.ReleaseWrites();
        await sparseWriteTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(pendingSparseWrites);

        await WaitUntilAsync(
            () => GetPrivateProperty<FileTransferCoordinatorPersistentState>(
                      context,
                      "CoordinatorPersistentState")
                  .FileTransferRouteTransition.CurrentOperation is null,
            timeoutMs: 5000);
        Assert.Equal(
            FileTransferRoute.FileTunaV4,
            GetPrivateProperty<FileTransferRouteSelection>(
                context,
                "RouteSelection").Route);
        var committed = Assert.IsType<FileTransferRouteTransitionOperation>(
            GetPrivateProperty<FileTransferCoordinatorPersistentState>(
                    context,
                    "CoordinatorPersistentState")
                .FileTransferRouteTransition.LastTerminalOperation);
        Assert.Equal(FileTransferRouteTransitionState.Committed, committed.State);
        var log = ReadOperationalLogTail(logStart);
        Assert.Equal(
            1,
            CountOccurrences(
                log,
                "event=filetransfer_route_transition_drain_receipt_decided;"));
        Assert.Equal(
            1,
            CountOccurrences(
                log,
                "event=filetransfer_route_transition_committed;"));
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public Task FileTransferRouteTransitionFallbackCommit_IssuesExactCheckpointBeforeRetainedFrameReplay()
        => VerifyFileTransferRouteTransitionFallbackCheckpointDispatchAsync(
            "route_transition_fallback_checkpoint_before_retained_replay",
            verifyStaleBindings: false);

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public Task FileTransferRouteTransitionFallbackCheckpointIssuance_StaleOrReplacedTargetHasNoEffect()
        => VerifyFileTransferRouteTransitionFallbackCheckpointDispatchAsync(
            "route_transition_fallback_checkpoint_stale_target",
            verifyStaleBindings: true);

    [Theory]
    [InlineData(FileTransferDirection.Outbound)]
    [InlineData(FileTransferDirection.Inbound)]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public void FileTransferRouteTransitionLatePeerProof_AfterCommittedDisposition_DoesNotRecreateShadow(
        FileTransferDirection direction)
    {
        var transferId =
            $"route_transition_late_peer_proof_{direction.ToString().ToLowerInvariant()}";
        using var service = new SessionFileTransferService();
        object context = direction == FileTransferDirection.Outbound
            ? CreateFileTunaV4OutboundContext(transferId)
            : CreateFileTunaV4InboundContext(transferId);
        var contextField = typeof(SessionFileTransferService).GetField(
            direction == FileTransferDirection.Outbound
                ? "outboundTransfer"
                : "inboundTransfer",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        contextField.SetValue(service, context);

        var state = CreateCoordinatorStateForTest(context);
        var sourceOwner = Assert.IsType<RuntimeOwnerToken>(
            state.CurrentRuntimeOwner);
        var sourcePublication = Assert.IsType<RoutePublicationToken>(
            state.CurrentRoutePublicationToken);
        var sourceCommit = Assert.IsType<RouteCommitToken>(
            state.CurrentRouteCommit);
        var sourceLeg = Assert.IsType<FileTransferLeg>(state.CurrentLeg);
        var dataSession = Assert.IsAssignableFrom<IFileTransferDataSession>(
            GetPrivatePropertyValue(context, "DataSession"));
        var targetTransportEpoch = Math.Max(1, sourceLeg.TransportEpochId + 1);
        var targetBridgeRecoveryGeneration =
            Math.Max(1, sourceLeg.BridgeRecoveryGeneration + 1);

        FileTransferDataFrame proofFrame = new FileTransferReceiverStateFrameV6
        {
            SessionId = state.SessionId,
            TransferId = state.TransferId,
            Epoch = 1,
            ContiguousCommittedChunkIndex = 0,
            DurableReceivedHighestChunkIndex = -1,
            CreditUntilChunkIndexExclusive = 1,
            MissingRanges = [],
            BytesCommitted = 0,
            TransportEpoch = targetTransportEpoch,
        };
        var frameIdentity =
            PostTunaFallbackPeerProofFrameIdentity.Create(proofFrame);
        var peerProof = new PostTunaFallbackPeerProofEvidence(
            state.SessionId,
            state.TransferId,
            direction,
            ProofGeneration: 1,
            TransportEpoch: targetTransportEpoch,
            ProofKind: "fallback_v6_proof_bootstrap_peer_frame",
            Reason: "coordinator_fallback_v6_proof_bootstrap_peer_frame",
            FrameIdentity: frameIdentity);
        var transportAliasProof = peerProof with
        {
            ProofKind = "file_transfer_v6_state_frame_received",
            Reason = "transport_recovered",
        };
        var proofBoundary = new FileTransferFallbackTransitionProofBoundary(
            sourceCommit,
            peerProof,
            frameIdentity,
            targetTransportEpoch,
            targetBridgeRecoveryGeneration);
        var checkpoint = new FileTransferRouteTransitionDurableCheckpoint(
            sourceOwner,
            sourcePublication,
            state.TerminalGeneration,
            sourceLeg.Generation,
            sourceLeg.TransportEpochId,
            sourceLeg.BridgeRecoveryGeneration,
            state.CurrentDataSessionGeneration,
            StateEpoch: 0,
            ContiguousCommittedChunkIndex: 0,
            DurableHighestChunkIndex: -1,
            ChunkCount: 256,
            CheckpointGeneration: 1);
        var authority = new FileTransferRouteTransitionAuthority(
            FileTransferRouteTransitionKind.TunaV4ToPostTunaFallbackV6,
            state.SessionId,
            state.TransferId,
            direction,
            sourceOwner,
            sourcePublication,
            sourceCommit,
            FileTransferRoute.FileTunaV4,
            FileTransferProtocol.ProtocolVersionV4,
            FileTransferTransportKind.Tuna,
            state.TerminalGeneration,
            sourceOwner.LiveRouteEpoch,
            sourceLeg.Generation,
            sourceLeg.TransportEpochId,
            sourceLeg.BridgeRecoveryGeneration,
            state.CurrentDataSessionGeneration,
            proofBoundary,
            checkpoint,
            FileTransferRoute.PostTunaFallbackV6,
            FileTransferProtocol.ProtocolVersionV6,
            FileTransferTransportKind.RegularNkn,
            FileTransferTransportHandoffKind.TunaToNormalFallback,
            TransitionGeneration: 1,
            new FileTransferAuthorityId($"fta3-{transferId}"),
            PredecessorTransitionAuthorityId: null,
            "test_committed_fallback_disposition");
        var dispositionCommand =
            new FileTransferRouteTransitionDispositionCommand(
                authority,
                FileTransferRouteTransitionDispositionKind.Committed,
                RouteCommitToken: null,
                RoutePublicationToken: null,
                "test_committed_fallback_disposition");
        var terminalOperation = new FileTransferRouteTransitionOperation(
            authority,
            FileTransferRouteTransitionState.Committed,
            DispositionCommand: dispositionCommand,
            DispositionReceipt:
                new FileTransferRouteTransitionDispositionReceipt(
                    dispositionCommand,
                    FileTransferRouteTransitionDispositionApplyStatus.Applied,
                    "test_committed_fallback_disposition"));
        SetPrivateProperty(
            context,
            "CoordinatorPersistentState",
            state.PersistentState with
            {
                FileTransferRouteTransition =
                    new FileTransferRouteTransitionSubstate(
                        LastTerminalOperation: terminalOperation,
                        LastTransitionGeneration: 1),
            });

        var retainMethod = typeof(SessionFileTransferService).GetMethod(
            direction == FileTransferDirection.Outbound
                ? "TryRetainOutboundFileTransferRouteTransitionPeerProofLocked"
                : "TryRetainInboundFileTransferRouteTransitionPeerProofLocked",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        var logStart = GetOperationalLogLength();
        object?[] arguments =
            [context, dataSession, transportAliasProof, "transport_recovered", null];

        Assert.True(Assert.IsType<bool>(retainMethod.Invoke(service, arguments)));
        Assert.Null(arguments[4]);
        Assert.Null(GetPrivatePropertyValue(
            context,
            "FileTransferRouteTransitionExecution"));
        var log = ReadOperationalLogTail(logStart);
        Assert.Contains(
            "decision_status=Rejected; decision_reason=filetransfer_route_transition_peer_proof_operation_terminal",
            log,
            StringComparison.Ordinal);
        Assert.Contains(
            $"authority_id={authority.AuthorityId}",
            log,
            StringComparison.Ordinal);
        Assert.Contains(
            $"frame_type={frameIdentity.FrameType}",
            log,
            StringComparison.Ordinal);
        Assert.Contains(
            $"frame_transport_epoch={frameIdentity.TransportEpoch}",
            log,
            StringComparison.Ordinal);
        Assert.Contains(
            "frame_fingerprint=",
            log,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "frame_fingerprint=(none)",
            log,
            StringComparison.Ordinal);

        SetPrivateProperty(
            context,
            "DataSessionGeneration",
            state.CurrentDataSessionGeneration + 1);
        arguments =
            [context, dataSession, transportAliasProof, "transport_recovered", null];
        Assert.True(Assert.IsType<bool>(retainMethod.Invoke(service, arguments)));
        Assert.Null(GetPrivatePropertyValue(
            context,
            "FileTransferRouteTransitionExecution"));

        var successorFrame = new FileTransferReceiverStateFrameV6
        {
            SessionId = state.SessionId,
            TransferId = state.TransferId,
            Epoch = 2,
            ContiguousCommittedChunkIndex = 0,
            DurableReceivedHighestChunkIndex = -1,
            CreditUntilChunkIndexExclusive = 1,
            MissingRanges = [],
            BytesCommitted = 0,
            TransportEpoch = targetTransportEpoch + 1,
        };
        var successorIdentity =
            PostTunaFallbackPeerProofFrameIdentity.Create(successorFrame);
        var successorProof = transportAliasProof with
        {
            ProofGeneration = peerProof.ProofGeneration + 1,
            TransportEpoch = peerProof.TransportEpoch + 1,
            FrameIdentity = successorIdentity,
        };
        arguments =
            [context, dataSession, successorProof, "transport_recovered", null];

        Assert.True(Assert.IsType<bool>(retainMethod.Invoke(service, arguments)));
        var successorShadow = Assert.IsAssignableFrom<object>(
            GetPrivatePropertyValue(
                context,
                "FileTransferRouteTransitionExecution"));
        Assert.Null(GetPrivatePropertyValue(successorShadow, "Authority"));

        arguments =
            [context, dataSession, transportAliasProof, "transport_recovered", null];
        Assert.True(Assert.IsType<bool>(retainMethod.Invoke(service, arguments)));
        Assert.Same(
            successorShadow,
            GetPrivatePropertyValue(
                context,
                "FileTransferRouteTransitionExecution"));
    }

    private async Task
        VerifyFileTransferRouteTransitionFallbackCheckpointDispatchAsync(
        string transferId,
        bool verifyStaleBindings)
    {
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport(
            $"session_{transferId}");
        using var peerTransport = new LoopbackFileTransferTransport(
            $"session_{transferId}");
        transport.Connect(peerTransport);
        service.AttachTransport(transport);
        var context = CreateFileTunaV4OutboundContext(transferId);
        SetPrivateProperty(
            context,
            "DataSession",
            await transport.OpenFileTransferDataSessionAsync(
                $"session_{transferId}",
                transferId,
                CancellationToken.None));
        typeof(SessionFileTransferService)
            .GetField(
                "outboundTransfer",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);
        var sourceState = CreateCoordinatorStateForTest(context);
        var sourceOwner = Assert.IsType<RuntimeOwnerToken>(
            sourceState.CurrentRuntimeOwner);
        var sourcePublication = Assert.IsType<RoutePublicationToken>(
            sourceState.CurrentRoutePublicationToken);
        var sourceCommit = Assert.IsType<RouteCommitToken>(
            sourceState.CurrentRouteCommit);
        var sourceLeg = Assert.IsType<FileTransferLeg>(
            sourceState.CurrentLeg);
        var targetTransportEpoch = sourceLeg.TransportEpochId + 1;
        var targetBridgeRecoveryGeneration =
            sourceLeg.BridgeRecoveryGeneration + 1;
        var expectedTransitionGeneration = Math.Max(
            1,
            sourceState.PersistentState.FileTransferRouteTransition
                .LastTransitionGeneration + 1);
        var expectedCheckpointRequestId =
            $"v6-regular-nkn-state-refresh:route-transition:{expectedTransitionGeneration}:{targetTransportEpoch}";
        var retainedReceiverState = new FileTransferReceiverStateFrameV6
        {
            SessionId = sourceState.SessionId,
            TransferId = sourceState.TransferId,
            Epoch = 1,
            ContiguousCommittedChunkIndex = 0,
            DurableReceivedHighestChunkIndex = -1,
            CreditUntilChunkIndexExclusive = 256,
            BytesCommitted = 0,
            TransportEpoch = targetTransportEpoch,
            MissingRanges = [],
            RepairRequestId = "pre_transition_fallback_peer_proof",
        };
        var frameIdentity =
            PostTunaFallbackPeerProofFrameIdentity.Create(
                retainedReceiverState);
        var peerProof = new PostTunaFallbackPeerProofEvidence(
            sourceState.SessionId,
            sourceState.TransferId,
            sourceState.Direction,
            ProofGeneration: 1,
            TransportEpoch: targetTransportEpoch,
            ProofKind: "fallback_peer_state",
            Reason: "test_committed_fallback_pause",
            FrameIdentity: frameIdentity);
        var proofBoundary = new FileTransferFallbackTransitionProofBoundary(
            sourceCommit,
            peerProof,
            frameIdentity,
            targetTransportEpoch,
            targetBridgeRecoveryGeneration);
        var checkpoint = new FileTransferRouteTransitionDurableCheckpoint(
            sourceOwner,
            sourcePublication,
            sourceState.TerminalGeneration,
            sourceLeg.Generation,
            sourceLeg.TransportEpochId,
            sourceLeg.BridgeRecoveryGeneration,
            sourceState.CurrentDataSessionGeneration,
            StateEpoch: 0,
            ContiguousCommittedChunkIndex: 0,
            DurableHighestChunkIndex: -1,
            ChunkCount: 256,
            CheckpointGeneration: 1);
        var prepared =
            FileTransferCoordinator.PrepareFileTransferRouteTransition(
                sourceState,
                new FileTransferRouteTransitionPrepareRequest(
                    FileTransferRouteTransitionKind
                        .TunaV4ToPostTunaFallbackV6,
                    sourceOwner,
                    sourcePublication,
                    sourceCommit,
                    FileTransferRoute.FileTunaV4,
                    FileTransferProtocol.ProtocolVersionV4,
                    FileTransferTransportKind.Tuna,
                    proofBoundary,
                    checkpoint,
                    FileTransferRoute.PostTunaFallbackV6,
                    FileTransferProtocol.ProtocolVersionV6,
                    FileTransferTransportKind.RegularNkn,
                    FileTransferTransportHandoffKind.TunaToNormalFallback,
                    PredecessorTransitionAuthorityId: null,
                    "test_committed_fallback_pause"));
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Prepared,
            prepared.Status);
        var authority = Assert.IsType<FileTransferRouteTransitionAuthority>(
            prepared.Authority);
        Assert.Equal(
            expectedTransitionGeneration,
            authority.TransitionGeneration);
        AdoptFileTransferRouteTransitionDecisionForGapTest(
            context,
            prepared);
        var pauseReceipt =
            ((IFileTransferRouteTransitionBridge)transport)
                .BindFileTransferRouteTransitionPause(
                    Assert.IsType<
                        FileTransferRouteTransitionPauseBindCommand>(
                        prepared.PauseBindCommand));
        Assert.Equal(
            FileTransferRouteTransitionPauseBindStatus.Installed,
            pauseReceipt.Status);
        var emptyIds = Array.Empty<long>();
        var emptyFingerprint =
            FileTransferCoordinator
                .CreateFileTransferRouteTransitionPhysicalDrainFingerprint(
                    emptyIds);
        var draining =
            FileTransferCoordinator.BeginFileTransferRouteTransitionPhysicalDrain(
                prepared.State,
                authority,
                pauseReceipt,
                capturedThroughPhysicalSendGeneration: 0,
                emptyIds,
                emptyFingerprint);
        var drainCommand =
            Assert.IsType<FileTransferRouteTransitionPhysicalDrainCommand>(
                draining.DrainCommand);
        AdoptFileTransferRouteTransitionDecisionForGapTest(
            context,
            draining);
        var dataSession = Assert.IsAssignableFrom<IFileTransferDataSession>(
            GetPrivatePropertyValue(context, "DataSession"));
        var executionType = typeof(SessionFileTransferService).GetNestedType(
            "FileTransferRouteTransitionExecutionShadow",
            BindingFlags.NonPublic)!;

        object CreateExecution()
            => Activator.CreateInstance(
                executionType,
                BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic,
                binder: null,
                args:
                [
                    authority,
                    dataSession,
                    sourceState.CurrentDataSessionGeneration,
                    drainCommand,
                    new Dictionary<long, Task>(),
                    peerProof,
                    retainedReceiverState,
                    frameIdentity,
                    null,
                    false,
                    false,
                    "test_committed_fallback_pause",
                ],
                culture: null)!;

        if (!verifyStaleBindings)
        {
            var serviceExecution = CreateExecution();
            SetPrivateProperty(
                context,
                "FileTransferRouteTransitionExecution",
                serviceExecution);
            object?[] commitArguments =
            [
                context,
                serviceExecution,
                null,
                null,
                null,
            ];
            var committedThroughService = Assert.IsType<bool>(
                typeof(SessionFileTransferService)
                    .GetMethod(
                        "TryCommitOutboundFileTransferRouteTransitionAndCaptureFallbackCheckpointLocked",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, commitArguments));
            Assert.True(
                committedThroughService,
                commitArguments[3] as string ?? "commit rejected");
            Assert.Null(commitArguments[4]);

            var serviceCommittedCheckpointLease =
                Assert.IsType<FallbackCheckpointLease>(
                    GetPrivatePropertyValue(
                        context,
                        "CurrentFallbackCheckpointLease"));
            Assert.Equal(
                expectedCheckpointRequestId,
                serviceCommittedCheckpointLease.CheckpointRequestId);
            var terminalBeforeReplay =
                GetPrivateProperty<FileTransferCoordinatorPersistentState>(
                        context,
                        "CoordinatorPersistentState")
                    .FileTransferRouteTransition.LastTerminalOperation!.Value;
            Assert.NotNull(terminalBeforeReplay.RetainedFrameApplyCommand);
            Assert.Null(terminalBeforeReplay.RetainedFrameApplyReceipt);
            Assert.Null(terminalBeforeReplay.DispositionReceipt);

            var checkpointSendObserved = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseCheckpointSend = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var olderSendObserved = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseOlderSend = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            const string olderRequestId =
                "older-active-frontier-repair";
            var checkpointSendPrecededReplay = false;
            transport.OutboundDataFrameDeliveryOverrideAsync =
                async (_, frame, _) =>
                {
                    if (frame is not FileTransferFrontierRequestFrameV6 request)
                    {
                        return true;
                    }

                    if (string.Equals(
                            request.RepairRequestId,
                            olderRequestId,
                            StringComparison.Ordinal))
                    {
                        olderSendObserved.TrySetResult();
                        await releaseOlderSend.Task.ConfigureAwait(false);
                    }
                    else if (string.Equals(
                                 request.RepairRequestId,
                                 expectedCheckpointRequestId,
                                 StringComparison.Ordinal))
                    {
                        var terminalAtSend =
                            GetPrivateProperty<
                                    FileTransferCoordinatorPersistentState>(
                                    context,
                                    "CoordinatorPersistentState")
                                .FileTransferRouteTransition
                                .LastTerminalOperation!.Value;
                        checkpointSendPrecededReplay =
                            terminalAtSend.RetainedFrameApplyReceipt is null &&
                            terminalAtSend.DispositionReceipt is null;
                        checkpointSendObserved.TrySetResult();
                        await releaseCheckpointSend.Task.ConfigureAwait(false);
                    }

                    return true;
                };

            var targetOwner = GetPrivateProperty<RuntimeOwnerToken>(
                context,
                "CurrentRuntimeOwner");
            var targetPublication =
                GetPrivateProperty<RoutePublicationToken>(
                    context,
                    "CurrentRoutePublicationToken");
            var targetLeg = Assert.IsType<FileTransferLeg>(
                GetPrivatePropertyValue(context, "CurrentTransferLeg"));
            var installType = typeof(SessionFileTransferService).GetNestedType(
                "OutboundSparsePumpInstall",
                BindingFlags.NonPublic)!;
            var install = Activator.CreateInstance(
                installType,
                BindingFlags.Instance |
                    BindingFlags.Public |
                    BindingFlags.NonPublic,
                binder: null,
                args:
                [
                    1L,
                    0L,
                    targetOwner,
                    targetPublication,
                    GetPrivateProperty<int>(context, "TerminalGeneration"),
                    targetLeg.Generation,
                    GetPrivateProperty<long>(
                        context,
                        "DataSessionGeneration"),
                    "test_fallback_retained_replay",
                ],
                culture: null)!;
            SetPrivateProperty(
                context,
                "InstalledOutboundSparsePump",
                install);
            var replayMethod = typeof(SessionFileTransferService)
                .GetMethod(
                    "TryApplyOutboundCommittedFileTransferRouteTransitionRetainedFrameAsync",
                    BindingFlags.Instance | BindingFlags.NonPublic)!;

            Task InvokeReplayAsync()
                => Assert.IsAssignableFrom<Task>(
                    replayMethod.Invoke(
                        service,
                        [context, install, dataSession]));

            void AssertRetainedReplayPending()
            {
                var pendingTerminal =
                    GetPrivateProperty<
                            FileTransferCoordinatorPersistentState>(
                            context,
                            "CoordinatorPersistentState")
                        .FileTransferRouteTransition
                        .LastTerminalOperation!.Value;
                Assert.Null(pendingTerminal.RetainedFrameApplyReceipt);
                Assert.Null(pendingTerminal.DispositionReceipt);
                var pendingExecution = GetPrivatePropertyValue(
                    context,
                    "FileTransferRouteTransitionExecution");
                Assert.NotNull(pendingExecution);
                Assert.False(GetPrivateProperty<bool>(
                    pendingExecution!,
                    "FrameApplyInFlight"));
                Assert.DoesNotContain(
                    transport.SentDataFrames,
                    frame => frame is FileTransferFrontierRequestFrameV6 request &&
                        string.Equals(
                            request.RepairRequestId,
                            expectedCheckpointRequestId,
                            StringComparison.Ordinal));
            }

            var olderCheckpoint = new FileTransferFrontierRequestFrameV6
            {
                SessionId = sourceState.SessionId,
                TransferId = sourceState.TransferId,
                TransportEpoch = targetTransportEpoch,
                RepairRequestId = olderRequestId,
                MissingRanges = [],
                Priority = "frontier_repair",
                RecoveryMode = "regular_nkn_frontier_repair",
            };
            const long olderSendGeneration = 41;
            SetPrivateProperty(
                context,
                "V6RegularNknStateRefreshSendInFlight",
                1);
            SetPrivateProperty(
                context,
                "V6RegularNknStateRefreshActiveSendGeneration",
                olderSendGeneration);
            SetPrivateProperty(
                context,
                "V6RegularNknStateRefreshActiveRequestId",
                olderRequestId);
            SetPrivateProperty(
                context,
                "V6RegularNknStateRefreshActivePriority",
                "frontier_repair");
            SetPrivateProperty(
                context,
                "V6RegularNknStateRefreshActiveStartedUtc",
                DateTimeOffset.UtcNow);
            var targetLegBeforeOlderSend = Assert.IsType<FileTransferLeg>(
                GetPrivatePropertyValue(context, "CurrentTransferLeg"));
            SetPrivateProperty(
                context,
                "CurrentTransferLeg",
                null);
            var olderSendTask = Assert.IsAssignableFrom<Task>(
                typeof(SessionFileTransferService)
                    .GetMethod(
                        "SendOutboundV4SparseRuntimeStateRefreshAsync",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(
                        service,
                        [
                            context,
                            dataSession,
                            olderCheckpoint,
                            olderSendGeneration,
                        ]));
            await olderSendObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            SetPrivateProperty(
                context,
                "CurrentTransferLeg",
                targetLegBeforeOlderSend);
            await InvokeReplayAsync().WaitAsync(TimeSpan.FromSeconds(5));
            AssertRetainedReplayPending();

            SetPrivateProperty(
                context,
                "V6RegularNknFallbackCheckpointDeliveryRecoveryPending",
                true);
            SetPrivateProperty(
                context,
                "V6RegularNknFallbackCheckpointDeliveryRecoveryStartedUtc",
                DateTimeOffset.UtcNow);
            SetPrivateProperty(
                context,
                "V6RegularNknFallbackCheckpointDeliveryRecoveryReason",
                "test_checkpoint_delivery_deferred");
            SetPrivateProperty(
                context,
                "V6RegularNknFallbackCheckpointDeliveryRecoveryRequestId",
                expectedCheckpointRequestId);
            releaseOlderSend.TrySetResult();
            await olderSendTask.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => GetPrivatePropertyValue(
                    context,
                    "V6RegularNknDeferredStateRefreshRequest") is
                        FileTransferFrontierRequestFrameV6 request &&
                    string.Equals(
                        request.RepairRequestId,
                        expectedCheckpointRequestId,
                        StringComparison.Ordinal),
                timeoutMs: 5000);
            AssertRetainedReplayPending();
            typeof(SessionFileTransferService)
                .GetMethod(
                    "QueueDeferredOutboundPostTunaFallbackStateRefreshAfterResume",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(
                    service,
                    [
                        context,
                        "route_transition_checkpoint_recovery_completed",
                        FileTransferReceiveRecoveryFallbackCheckpointResumeTrigger
                            .RecoveryCompleted,
                    ]);
            await checkpointSendObserved.Task.WaitAsync(
                TimeSpan.FromSeconds(5));
            await WaitUntilAsync(
                () => GetPrivatePropertyValue(
                    context,
                    "FileTransferRouteTransitionExecution") is null,
                timeoutMs: 5000);
            releaseCheckpointSend.TrySetResult();
            await WaitUntilAsync(
                () => GetPrivateProperty<int>(
                    context,
                    "V6RegularNknStateRefreshSendInFlight") == 0,
                timeoutMs: 5000);

            Assert.True(checkpointSendPrecededReplay);
            var serviceSentCheckpoint = Assert.IsType<
                FileTransferFrontierRequestFrameV6>(
                Assert.Single(transport.SentDataFrames.Where(frame =>
                    frame is FileTransferFrontierRequestFrameV6 request &&
                    string.Equals(
                        request.RepairRequestId,
                        expectedCheckpointRequestId,
                        StringComparison.Ordinal))));
            Assert.Equal(
                expectedCheckpointRequestId,
                serviceSentCheckpoint.RepairRequestId);
            Assert.Equal(
                targetTransportEpoch,
                serviceSentCheckpoint.TransportEpoch);
            var terminalAfterReplay =
                GetPrivateProperty<FileTransferCoordinatorPersistentState>(
                        context,
                        "CoordinatorPersistentState")
                    .FileTransferRouteTransition.LastTerminalOperation!.Value;
            Assert.NotNull(terminalAfterReplay.RetainedFrameApplyReceipt);
            Assert.NotNull(terminalAfterReplay.DispositionReceipt);
            var pendingLegAfterReplay = Assert.IsType<FileTransferLeg>(
                GetPrivatePropertyValue(context, "CurrentTransferLeg"));
            Assert.Equal(
                FileTransferLegState.CheckpointPending,
                pendingLegAfterReplay.State);
            Assert.False(pendingLegAfterReplay.CanSendData);
            Assert.Equal(
                expectedCheckpointRequestId,
                pendingLegAfterReplay.CheckpointRequestId);
            Assert.Equal(
                serviceCommittedCheckpointLease,
                GetPrivateProperty<FallbackCheckpointLease>(
                    context,
                    "CurrentFallbackCheckpointLease"));
            Assert.False(GetPrivateProperty<bool>(
                context,
                "V6RegularNknFallbackCheckpointDeliveryRecoveryPending"));
            Assert.Null(GetPrivatePropertyValue(
                context,
                "V6RegularNknDeferredStateRefreshRequest"));
            Assert.Null(
                GetPrivatePropertyValue(
                    context,
                    "FileTransferRouteTransitionExecution"));
            return;
        }

        var committed =
            FileTransferCoordinator.CommitFileTransferRouteTransition(
                draining.State,
                authority,
                new FileTransferRouteTransitionPhysicalDrainReceipt(
                    drainCommand,
                    emptyIds,
                    CompletedPhysicalSendCount: 0,
                    emptyFingerprint,
                    FileTransferRouteTransitionPhysicalDrainOutcome.Completed,
                    "physical_drain_completed"));
        Assert.Equal(
            FileTransferRouteTransitionDecisionStatus.Committed,
            committed.Status);
        var terminalOperation = Assert.IsType<FileTransferRouteTransitionOperation>(
            committed.State.PersistentState.FileTransferRouteTransition
                .LastTerminalOperation);
        Assert.NotNull(terminalOperation.RetainedFrameApplyCommand);
        Assert.Null(terminalOperation.RetainedFrameApplyReceipt);
        Assert.NotNull(terminalOperation.DispositionCommand);
        Assert.Null(terminalOperation.DispositionReceipt);
        var committedFallbackLeg = Assert.IsType<FileTransferLeg>(
            committed.State.CurrentLeg);
        Assert.Equal(
            FileTransferLegState.CheckpointPending,
            committedFallbackLeg.State);
        Assert.False(committedFallbackLeg.CanSendData);
        Assert.False(
            string.IsNullOrWhiteSpace(
                committedFallbackLeg.CheckpointRequestId));
        var committedCheckpointLease =
            Assert.IsType<FallbackCheckpointLease>(
                committed.State.CurrentFallbackCheckpointLease);
        Assert.Equal(
            committedFallbackLeg.CheckpointRequestId,
            committedCheckpointLease.CheckpointRequestId);
        Assert.Equal(
            committedFallbackLeg.CheckpointGeneration,
            committedCheckpointLease.CheckpointGeneration);
        Assert.True(
            FileTransferCoordinator.IsFallbackCheckpointLeaseCurrent(
                committed.State,
                committedCheckpointLease,
                out var checkpointLeaseReason),
            checkpointLeaseReason);

        AdoptFileTransferRouteTransitionDecisionForGapTest(
            context,
            committed);

        typeof(SessionFileTransferService)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(candidate =>
            {
                if (!string.Equals(
                        candidate.Name,
                        "ApplyCoordinatorDecisionToContextLocked",
                        StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length == 4 &&
                       parameters[0].ParameterType == context.GetType() &&
                       parameters[1].ParameterType ==
                            typeof(FileTransferCoordinatorDecision);
            })
            .Invoke(
                null,
                [
                    context,
                    Assert.IsType<FileTransferCoordinatorDecision>(
                        committed.RouteDecision),
                    false,
                    false,
                ]);

        var execution = CreateExecution();
        SetPrivateProperty(
            context,
            "FileTransferRouteTransitionExecution",
            execution);
        SetPrivateProperty(context, "PullTransportPaused", true);
        SetPrivateProperty(
            context,
            "PullTransportPausedSinceUtc",
            DateTimeOffset.UtcNow);
        SetPrivateProperty(
            context,
            "PullTransportGraceDeadlineUtc",
            null);
        SetPrivateProperty(
            context,
            "PullTransportPauseReason",
            "filetransfer_route_transition_draining");

        var handled = await (Task<bool>)typeof(SessionFileTransferService)
            .GetMethod(
                "HandlePausedOutboundTransportAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context])!;

        Assert.False(handled);
        Assert.False(GetPrivateProperty<bool>(context, "IsTerminal"));
        Assert.True(GetPrivateProperty<bool>(context, "PullTransportPaused"));
        Assert.Contains(
            "|Committed|Held|True|True",
            GetPrivateProperty<string>(
                context,
                "FileTransferRouteTransitionTransportPauseEvidenceKey"),
            StringComparison.Ordinal);

        object?[] captureArguments =
        [
            context,
            authority,
            Assert.IsType<FileTransferRouteTransitionDispositionCommand>(
                committed.DispositionCommand),
            execution,
            null,
            null,
        ];
        var captured = Assert.IsType<bool>(
            typeof(SessionFileTransferService)
                .GetMethod(
                    "TryCaptureOutboundFileTransferRouteTransitionFallbackCheckpointDispatchLocked",
                    BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(service, captureArguments));
        Assert.True(
            captured,
            captureArguments[5] as string ?? "capture rejected");
        Assert.NotNull(captureArguments[4]);
        var dispatch = captureArguments[4]!;
        var dispatchObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDispatch = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        transport.OutboundDataFrameDeliveryOverrideAsync =
            async (_, frame, _) =>
            {
                if (frame is FileTransferFrontierRequestFrameV6)
                {
                    dispatchObserved.TrySetResult();
                    await releaseDispatch.Task.ConfigureAwait(false);
                    return true;
                }

                return false;
            };
        var dispatchMethod = typeof(SessionFileTransferService)
            .GetMethod(
                "TryDispatchOutboundFileTransferRouteTransitionFallbackCheckpoint",
                BindingFlags.Instance | BindingFlags.NonPublic)!;
        Assert.True(Assert.IsType<bool>(
            dispatchMethod.Invoke(service, [context, dispatch])));
        await dispatchObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var sentCheckpoint = Assert.IsType<FileTransferFrontierRequestFrameV6>(
            Assert.Single(transport.SentDataFrames));
        Assert.Equal(
            committedCheckpointLease.CheckpointRequestId,
            sentCheckpoint.RepairRequestId);
        Assert.Equal(
            committedCheckpointLease.TransportEpoch,
            sentCheckpoint.TransportEpoch);
        Assert.Equal("state_refresh", sentCheckpoint.Priority);
        Assert.Equal(
            "regular_nkn_state_refresh",
            sentCheckpoint.RecoveryMode);

        var requestSequence = GetPrivateProperty<long>(
            context,
            "V6RegularNknStateRefreshSequence");
        Assert.Equal(
            1,
            GetPrivateProperty<int>(
                context,
                "V6RegularNknStateRefreshSendInFlight"));
        Assert.Equal(
            committedCheckpointLease.CheckpointRequestId,
            GetPrivateProperty<string>(
                context,
                "V6RegularNknStateRefreshActiveRequestId"));
        var dispatchTargetLeg = Assert.IsType<FileTransferLeg>(
            GetPrivatePropertyValue(dispatch, "TargetLeg"));
        var currentTargetLeg = Assert.IsType<FileTransferLeg>(
            GetPrivatePropertyValue(context, "CurrentTransferLeg"));
        var differingLegProperties = typeof(FileTransferLeg)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(property => !Equals(
                property.GetValue(dispatchTargetLeg),
                property.GetValue(currentTargetLeg)))
            .Select(property =>
                $"{property.Name}={property.GetValue(dispatchTargetLeg)}->{property.GetValue(currentTargetLeg)}")
            .ToArray();
        Assert.True(
            differingLegProperties.Length == 0,
            string.Join(",", differingLegProperties));
        Assert.Same(
            GetPrivatePropertyValue(dispatch, "DataSession"),
            GetPrivatePropertyValue(context, "DataSession"));
        Assert.Equal(
            GetPrivatePropertyValue(dispatch, "RuntimeOwner"),
            GetPrivatePropertyValue(context, "CurrentRuntimeOwner"));
        Assert.Equal(
            GetPrivatePropertyValue(dispatch, "RoutePublication"),
            GetPrivatePropertyValue(context, "CurrentRoutePublicationToken"));
        Assert.Equal(
            GetPrivatePropertyValue(dispatch, "CheckpointLease"),
            GetPrivatePropertyValue(context, "CurrentFallbackCheckpointLease"));
        object?[] validateDispatchArguments =
        [
            context,
            dispatch,
            null,
        ];
        Assert.True(
            Assert.IsType<bool>(
                typeof(SessionFileTransferService)
                    .GetMethod(
                        "TryValidateOutboundFileTransferRouteTransitionFallbackCheckpointDispatchLocked",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, validateDispatchArguments)),
            validateDispatchArguments[2] as string ?? "dispatch not current");
        Assert.True(Assert.IsType<bool>(
            dispatchMethod.Invoke(service, [context, dispatch])));
        Assert.Equal(
            requestSequence,
            GetPrivateProperty<long>(
                context,
                "V6RegularNknStateRefreshSequence"));
        Assert.Single(transport.SentDataFrames);

        void AssertStaleDispatchHasNoEffect(
            Action mutate,
            Action restore)
        {
            mutate();
            try
            {
                Assert.False(Assert.IsType<bool>(
                    dispatchMethod.Invoke(service, [context, dispatch])));
                Assert.Equal(
                    requestSequence,
                    GetPrivateProperty<long>(
                        context,
                        "V6RegularNknStateRefreshSequence"));
                Assert.Single(transport.SentDataFrames);
            }
            finally
            {
                restore();
            }
        }

        AssertStaleDispatchHasNoEffect(
            () => SetPrivateProperty(
                context,
                "DataSessionGeneration",
                sourceState.CurrentDataSessionGeneration + 1),
            () => SetPrivateProperty(
                context,
                "DataSessionGeneration",
                sourceState.CurrentDataSessionGeneration));
        var replacementDataSession = new RecordingDataSession(
            dataSession.SessionId,
            dataSession.TransferId);
        AssertStaleDispatchHasNoEffect(
            () => SetPrivateProperty(
                context,
                "DataSession",
                replacementDataSession),
            () => SetPrivateProperty(
                context,
                "DataSession",
                dataSession));
        var currentTerminalGeneration =
            GetPrivateProperty<int>(context, "TerminalGeneration");
        AssertStaleDispatchHasNoEffect(
            () => SetPrivateProperty(
                context,
                "TerminalGeneration",
                currentTerminalGeneration + 1),
            () => SetPrivateProperty(
                context,
                "TerminalGeneration",
                currentTerminalGeneration));
        var currentCheckpointLease =
            GetPrivateProperty<FallbackCheckpointLease>(
                context,
                "CurrentFallbackCheckpointLease");
        AssertStaleDispatchHasNoEffect(
            () => SetPrivateProperty(
                context,
                "CurrentFallbackCheckpointLease",
                currentCheckpointLease with
                {
                    LeaseGeneration =
                        currentCheckpointLease.LeaseGeneration + 1,
                }),
            () => SetPrivateProperty(
                context,
                "CurrentFallbackCheckpointLease",
                currentCheckpointLease));
        var currentPublication =
            GetPrivateProperty<RoutePublicationToken>(
                context,
                "CurrentRoutePublicationToken");
        AssertStaleDispatchHasNoEffect(
            () => SetPrivateProperty(
                context,
                "CurrentRoutePublicationToken",
                currentPublication with
                {
                    PublicationGeneration =
                        currentPublication.PublicationGeneration + 1,
                }),
            () => SetPrivateProperty(
                context,
                "CurrentRoutePublicationToken",
                currentPublication));
        var currentOwner = GetPrivateProperty<RuntimeOwnerToken>(
            context,
            "CurrentRuntimeOwner");
        AssertStaleDispatchHasNoEffect(
            () => SetPrivateProperty(
                context,
                "CurrentRuntimeOwner",
                currentOwner with
                {
                    RuntimeGeneration = currentOwner.RuntimeGeneration + 1,
                }),
            () => SetPrivateProperty(
                context,
                "CurrentRuntimeOwner",
                currentOwner));
        var currentRoute = GetPrivateProperty<FileTransferRouteSelection>(
            context,
            "RouteSelection");
        AssertStaleDispatchHasNoEffect(
            () => SetPrivateProperty(
                context,
                "RouteSelection",
                FileTransferRouteResolver.Resolve(
                    FileTransferRoute.FileTunaV4)),
            () => SetPrivateProperty(
                context,
                "RouteSelection",
                currentRoute));
        var currentFallbackLeg = Assert.IsType<FileTransferLeg>(
            GetPrivatePropertyValue(context, "CurrentTransferLeg"));
        var currentFallbackLegGeneration = currentFallbackLeg.Generation;
        AssertStaleDispatchHasNoEffect(
            () => SetPrivateProperty(
                currentFallbackLeg,
                "Generation",
                currentFallbackLegGeneration + 1),
            () => SetPrivateProperty(
                currentFallbackLeg,
                "Generation",
                currentFallbackLegGeneration));
        AssertStaleDispatchHasNoEffect(
            () => currentFallbackLeg.TransportEpochId++,
            () => currentFallbackLeg.TransportEpochId--);
        AssertStaleDispatchHasNoEffect(
            () => currentFallbackLeg.BridgeRecoveryGeneration++,
            () => currentFallbackLeg.BridgeRecoveryGeneration--);
        var checkpointRequestId =
            currentFallbackLeg.CheckpointRequestId;
        AssertStaleDispatchHasNoEffect(
            () => currentFallbackLeg.CheckpointRequestId =
                $"{checkpointRequestId}:replacement",
            () => currentFallbackLeg.CheckpointRequestId =
                checkpointRequestId);
        AssertStaleDispatchHasNoEffect(
            () =>
            {
                currentFallbackLeg.State =
                    FileTransferLegState.RecoveryActive;
                currentFallbackLeg.CanSendData = true;
                currentFallbackLeg.CheckpointRequestId = null;
            },
            () =>
            {
                currentFallbackLeg.State =
                    FileTransferLegState.CheckpointPending;
                currentFallbackLeg.CanSendData = false;
                currentFallbackLeg.CheckpointRequestId =
                    checkpointRequestId;
            });
        releaseDispatch.TrySetResult();
        await WaitUntilAsync(
            () => GetPrivateProperty<int>(
                context,
                "V6RegularNknStateRefreshSendInFlight") == 0,
            timeoutMs: 5000);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task FileTransferRouteTransitionTransportGrace_ShapedReasonWithoutCoordinatorAuthorityFailsClosed()
    {
        const string transferId =
            "route_transition_grace_without_authority";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport(
            $"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateRegularNknV4OutboundContext(
            transferId,
            remoteNextExpectedChunkIndex: 0,
            chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0,
            lastGrantReceivedUtc: DateTimeOffset.UtcNow);
        SetPrivateProperty(context, "PullTransportPaused", true);
        SetPrivateProperty(
            context,
            "PullTransportPausedSinceUtc",
            DateTimeOffset.UtcNow);
        SetPrivateProperty(
            context,
            "PullTransportGraceDeadlineUtc",
            DateTimeOffset.UtcNow.AddMinutes(1));
        SetPrivateProperty(
            context,
            "PullTransportPauseReason",
            "filetransfer_route_transition_draining");
        typeof(SessionFileTransferService)
            .GetField(
                "outboundTransfer",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(service, context);

        var handled = await (Task<bool>)typeof(SessionFileTransferService)
            .GetMethod(
                "HandlePausedOutboundTransportAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [context])!;

        Assert.True(handled);
        Assert.True(GetPrivateProperty<bool>(context, "IsTerminal"));
        Assert.Contains(
            "|(none)|Rejected|",
            GetPrivateProperty<string>(
                context,
                "FileTransferRouteTransitionTransportPauseEvidenceKey"),
            StringComparison.Ordinal);
    }

    private static void AdoptFileTransferRouteTransitionDecisionForGapTest(
        object context,
        FileTransferRouteTransitionDecision decision)
        => typeof(SessionFileTransferService)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(candidate =>
            {
                if (!string.Equals(
                        candidate.Name,
                        "AdoptCoordinatorStateLocked",
                        StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length == 2 &&
                       parameters[0].ParameterType == context.GetType() &&
                       parameters[1].ParameterType ==
                           typeof(FileTransferRouteTransitionDecision);
            })
            .Invoke(null, [context, decision]);

    private static (
        bool Accepted,
        FileTransferRouteTransitionPhysicalDrainCommand Command,
        string Reason)
        StageInboundFileTransferRouteTransitionForGapTest(
            SessionFileTransferService service,
            object context,
            RuntimeUnlockRouteCommitSnapshot snapshot,
            string reason)
    {
        snapshot = CreateInitialResponderRouteCommitSnapshotForInboundTest(
            context,
            snapshot);
        object?[] arguments =
        [
            context,
            snapshot,
            reason,
            null,
        ];
        bool accepted;
        var serviceGate = typeof(SessionFileTransferService)
            .GetField("gate", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(service)!;
        lock (serviceGate)
        {
            accepted = Assert.IsType<bool>(
                typeof(SessionFileTransferService)
                    .GetMethod(
                        "TryPrepareInboundRegularV4ToTunaRouteTransitionLocked",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(service, arguments));
            if (accepted)
            {
                StabilizeFileTransferRouteTransitionDrainForTest(context);
            }
        }

        return (
            accepted,
            accepted
                ? GetPrivateProperty<FileTransferCoordinatorPersistentState>(
                        context,
                        "CoordinatorPersistentState")
                    .FileTransferRouteTransition.CurrentOperation!.Value
                    .DrainCommand!.Value
                : default,
            accepted ? "accepted" : "rejected");
    }

    private static RuntimeUnlockRouteCommitSnapshot
        CreateInitialResponderRouteCommitSnapshotForInboundTest(
            object context,
            RuntimeUnlockRouteCommitSnapshot snapshot)
    {
        var state = CreateCoordinatorStateForTest(context);
        var leg = Assert.IsType<FileTransferLeg>(state.CurrentLeg);
        var admission = Assert.IsType<RuntimeUnlockAdmissionToken>(
            state.CurrentRuntimeUnlockAdmission);
        var receiveScope =
            new RuntimeUnlockPeerPreCommitAnswerAcceptanceScope(
                state.CurrentRuntimeOwner!.Value,
                state.CurrentRoutePublicationToken!.Value,
                state.TerminalGeneration,
                leg.Generation,
                leg.TransportEpochId,
                leg.BridgeRecoveryGeneration,
                leg.CheckpointRequestId ?? "(none)",
                leg.CheckpointGeneration,
                state.CurrentDataSessionIdentity!,
                state.CurrentDataSessionGeneration,
                admission,
                RuntimeUnlockResponderPeerProbeTransitionMode
                    .InitialRegularV4);
        var observation =
            new RuntimeUnlockResponderPeerProbeRouteTransitionObservation(
                snapshot.SessionId,
                snapshot.TransferId,
                snapshot.TransactionGeneration,
                snapshot.OfferGeneration,
                PendingAnswerAckGeneration: snapshot.OfferGeneration,
                PayerDecisionId: snapshot.TransactionGeneration,
                snapshot.LaunchGeneration,
                Lanes: "file",
                AnswerAcceptanceSource: "SemanticAnswerAck",
                AckMessageId:
                    $"test-answer-ack-{snapshot.TransactionGeneration}-{snapshot.OfferGeneration}",
                AckSourceFingerprint: new string('a', 64),
                PeerProbeFrameFingerprint: new string('b', 64),
                snapshot.TunaPathLeaseGeneration,
                snapshot.TunaPathLeaseListenerRunId,
                ProbeId: snapshot.PathProbeId!,
                RuntimeUnlockPathProbeState.Acked,
                snapshot.PathProbeTransport,
                snapshot.PathProbeAckedUtcMs,
                snapshot.TargetRoute,
                snapshot.ProtocolVersion,
                snapshot.HandoffKind,
                snapshot.TargetTransport,
                receiveScope);
        return snapshot with
        {
            ResponderPeerProbeRouteTransitionObservation = observation,
        };
    }

    private static bool
        InvalidateFileTransferRouteTransitionForGapTest(
            SessionFileTransferService service,
            object context,
            FileTransferRouteTransitionAuthority authority,
            string reason)
    {
        var method = typeof(SessionFileTransferService)
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single(candidate =>
            {
                if (!string.Equals(
                        candidate.Name,
                        context.GetType().Name.Contains(
                            "Outbound",
                            StringComparison.Ordinal)
                            ? "InvalidateOutboundFileTransferRouteTransitionLocked"
                            : "InvalidateInboundFileTransferRouteTransitionLocked",
                        StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = candidate.GetParameters();
                return parameters.Length == 4 &&
                       parameters[0].ParameterType == context.GetType();
            });
        return Assert.IsType<bool>(
            method.Invoke(
                service,
                [context, authority, false, reason]));
    }

    private sealed class SingleFrameRecordingDataSession(
        string sessionId,
        string transferId,
        FileTransferDataFrame receivedFrame) : IFileTransferDataSession
    {
        private readonly List<FileTransferDataFrame> sentFrames = [];
        private int received;

        public string SessionId { get; } = sessionId;
        public string TransferId { get; } = transferId;
        public bool IsAvailable => true;
        public IReadOnlyList<FileTransferDataFrame> SentFrames => sentFrames.ToArray();

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged
        {
            add { }
            remove { }
        }

        public async ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => (await ReceiveWithMetadataAsync(ct)).Frame;

        public async ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(
            CancellationToken ct)
        {
            if (Interlocked.Exchange(ref received, 1) == 0)
            {
                return new FileTransferReceivedDataFrame(
                    receivedFrame,
                    FileTransferTransportKind.Tuna,
                    "file",
                    DateTimeOffset.UtcNow);
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new OperationCanceledException(ct);
        }

        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
        {
            sentFrames.Add(frame);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class GenerationBoundDataSession(
        string sessionId,
        string transferId,
        long generation) :
        IFileTransferDataSession,
        IFileTransferDataSessionGenerationProvider
    {
        public string SessionId { get; } = sessionId;
        public string TransferId { get; } = transferId;
        public long Generation { get; } = generation;
        public bool IsAvailable => true;

        public event EventHandler<FileTransferDataSessionAvailabilityChangedEventArgs>? AvailabilityChanged
        {
            add { }
            remove { }
        }

        public ValueTask<FileTransferDataFrame> ReceiveAsync(CancellationToken ct)
            => throw new NotSupportedException();

        public ValueTask<FileTransferReceivedDataFrame> ReceiveWithMetadataAsync(
            CancellationToken ct)
            => throw new NotSupportedException();

        public Task SendAsync(FileTransferDataFrame frame, CancellationToken ct)
            => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
