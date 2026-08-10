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
    public async Task FileTransferRouteTransitionTransportGrace_CommittedFallbackHandoffsRemainPaused()
    {
        const string transferId =
            "route_transition_grace_committed_fallback_handoffs_pending";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport(
            $"session_{transferId}");
        service.AttachTransport(transport);
        var context = CreateFileTunaV4OutboundContext(transferId);
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
        var frameIdentity = new PostTunaFallbackPeerProofFrameIdentity(
            "filetransfer_receiver_state_v6",
            targetTransportEpoch,
            new string('a', 64));
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
            StateEpoch: 1,
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
        var pauseReceipt =
            new FileTransferRouteTransitionPauseBindReceipt(
                Assert.IsType<FileTransferRouteTransitionPauseBindCommand>(
                    prepared.PauseBindCommand),
                FileTransferRouteTransitionPauseBindStatus.Installed,
                "filetransfer_route_transition_pause_bound");
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

        var dataSession = Assert.IsAssignableFrom<IFileTransferDataSession>(
            GetPrivatePropertyValue(context, "DataSession"));
        var executionType = typeof(SessionFileTransferService).GetNestedType(
            "FileTransferRouteTransitionExecutionShadow",
            BindingFlags.NonPublic)!;
        var execution = Activator.CreateInstance(
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
                null,
                frameIdentity,
                null,
                false,
                false,
                "test_committed_fallback_pause",
            ],
            culture: null)!;
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
