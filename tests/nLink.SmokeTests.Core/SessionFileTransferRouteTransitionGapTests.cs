using System.Collections;
using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
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
            SetPrivateProperty(
                context,
                "CoordinatorPersistentState",
                draining.State.PersistentState);
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
                return parameters.Length == 3 &&
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
}
