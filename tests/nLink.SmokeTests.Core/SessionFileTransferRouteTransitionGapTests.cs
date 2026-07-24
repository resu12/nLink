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
            SeedCurrentRuntimeOwnerForTest(
                context,
                FileTransferDirection.Inbound,
                reason);
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
        SeedCurrentRuntimeOwnerForTest(
            context,
            FileTransferDirection.Inbound,
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
    public void RegularV4ToTunaRouteTransition_AcceptedInboundDrainPausesCoreTransport()
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
        SeedCurrentRuntimeOwnerForTest(
            context,
            FileTransferDirection.Inbound,
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
