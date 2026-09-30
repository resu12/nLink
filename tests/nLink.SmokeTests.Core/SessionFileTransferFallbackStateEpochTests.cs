using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [Trait("FileTransferRuntimeSuite", "FallbackRouteTransition")]
    public async Task FallbackStateEpoch_CommittedSuccessorStartsFreshAndRejectsOldOrDuplicateState(
        int predecessorEpoch)
    {
        var transferId = $"fallback_state_epoch_{predecessorEpoch}";
        var sessionId = $"session_{transferId}";
        using var service = new SessionFileTransferService();
        using var transport = new LoopbackFileTransferTransport(sessionId);
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        service.AttachTransport(transport);
        var context = CreateFileTunaV4OutboundContext(transferId,
            remoteNextExpectedChunkIndex: 0, chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0, chunkCount: 1024);
        var dataSession = await transport.OpenFileTransferDataSessionAsync(
            sessionId, transferId, CancellationToken.None);
        SetPrivateProperty(context, "DataSession", dataSession);
        var serviceType = typeof(SessionFileTransferService);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        serviceType.GetField("outboundTransfer", flags)!.SetValue(service, context);
        var predecessorOwner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var staged = StageSparsePumpInstall(service, context, null, "epoch_test_predecessor");
        Assert.True(staged.Accepted, staged.Reason);
        var installed = CommitSparsePumpInstall(service, context, staged.Install!, dataSession);
        Assert.True(installed.Accepted, installed.Reason);
        var predecessorInstall = installed.Install!;
        var apply = serviceType.GetMethod("ApplyOutboundV4StateAsync", flags)!;

        async Task<string> ApplyAsync(FileTransferStateFrameV4 state, object? install, RuntimeOwnerToken owner)
        {
            var task = Assert.IsAssignableFrom<Task>(apply.Invoke(service,
                [context, state, install, owner, dataSession, 1L]));
            await task;
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            return GetPrivatePropertyValue(result, "Status")!.ToString()!;
        }

        Assert.Equal("Applied", await ApplyAsync(new FileTransferStateFrameV4
        {
            SessionId = sessionId, TransferId = transferId, Epoch = predecessorEpoch,
            ContiguousCommittedChunkIndex = 0, DurableReceivedHighestChunkIndex = -1,
            CreditUntilChunkIndexExclusive = 512,
        }, predecessorInstall, predecessorOwner));
        Assert.Equal(predecessorEpoch, GetPrivateProperty<int>(context, "V4LastStateEpoch"));

        // Commit the real coordinator transition and replay its exact retained
        // paused bootstrap state through the production retained-frame path.
        var bootstrap = new FileTransferFallbackBootstrapStateFrameV6
        {
            SessionId = sessionId, TransferId = transferId,
            TransportEpoch = GetCurrentContextLeg(context).TransportEpochId + 1,
            Epoch = 3, ContiguousCommittedChunkIndex = 0,
            DurableReceivedHighestChunkIndex = -1, CreditUntilChunkIndexExclusive = 0,
            TransferPaused = true, TransferPauseReason = FileTransferBootstrapCorrelation.RecoveryMode,
            SenderRequestId = $"fbc1:{new string('a', 64)}:1:{new string('b', 64)}",
            ReceiverChallengeId = $"fbc1:{new string('b', 64)}:1:{new string('a', 64)}",
        };
        var fixture = CreateFallbackCheckpointDispatchFixture(context, transport, bootstrap);
        Assert.Equal(bootstrap.TransportEpoch, fixture.TargetTransportEpoch);
        var execution = fixture.CreateExecution();
        SetPrivateProperty(context, "FileTransferRouteTransitionExecution", execution);
        var commit = serviceType.GetMethod(
            "TryCommitOutboundFileTransferRouteTransitionAndCaptureFallbackCheckpointLocked", flags)!;
        object?[] arguments = [context, execution, null, null, null];
        Assert.True(Assert.IsType<bool>(commit.Invoke(service, arguments)), arguments[3] as string);
        var successorOwner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        Assert.NotEqual(predecessorOwner, successorOwner);
        Assert.Equal(FileTransferRoute.PostTunaFallbackV6,
            GetPrivateProperty<FileTransferRouteSelection>(context, "RouteSelection").Route);
        Assert.Equal(6, GetPrivateProperty<int>(context, "NegotiatedDataProtocolVersion"));

        staged = StageSparsePumpInstall(service, context, predecessorInstall, "epoch_test_successor");
        Assert.True(staged.Accepted, staged.Reason);
        installed = CommitSparsePumpInstall(service, context, staged.Install!, dataSession);
        Assert.True(installed.Accepted, installed.Reason);
        var successorInstall = installed.Install!;
        await Assert.IsAssignableFrom<Task>(serviceType.GetMethod(
            "TryApplyOutboundCommittedFileTransferRouteTransitionRetainedFrameAsync", flags)!
            .Invoke(service, [context, successorInstall, dataSession]));
        await WaitUntilAsync(() => GetPrivateProperty<int>(context, "V6LastReceiverStateEpoch") == 3,
            timeoutMs: 5000);
        Assert.Contains(transport.SentDataFrames, frame => frame is FileTransferFrontierRequestFrameV6 request &&
            request.RepairRequestId == fixture.ExpectedCheckpointRequestId);
        Assert.True(GetPrivateProperty<bool>(context, "PeerPaused"));
        Assert.Equal(0, GetPrivateProperty<int>(context, "RemoteGrantedUntilExclusive"));
        Assert.Equal(3, GetPrivateProperty<int>(context, "V6LastReceiverStateEpoch"));

        var current = new FileTransferReceiverStateFrameV6
        {
            SessionId = sessionId, TransferId = transferId, Epoch = 2,
            TransportEpoch = fixture.TargetTransportEpoch,
            ContiguousCommittedChunkIndex = 0, DurableReceivedHighestChunkIndex = -1,
            CreditUntilChunkIndexExclusive = 512, TransferPaused = false,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 512 }],
            Priority = "frontier",
        };
        Assert.Equal("Applied", await ApplyAsync(current, successorInstall, successorOwner));
        Assert.False(GetPrivateProperty<bool>(context, "PeerPaused"));
        Assert.Equal(512, GetPrivateProperty<int>(context, "RemoteGrantedUntilExclusive"));
        Assert.Equal(2, GetPrivateProperty<int>(context, "V4LastStateEpoch"));
        Assert.True(GetCurrentContextLeg(context).CanSendData);
        Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));

        var currentOwner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
        var currentPublication = GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken");
        var applicationCount = GetPrivateProperty<int>(context, "PullV4StateAppliedCountTotal");
        Assert.Equal("Idempotent", await ApplyAsync(current, successorInstall, successorOwner));
        Assert.Equal(applicationCount, GetPrivateProperty<int>(context, "PullV4StateAppliedCountTotal"));
        Assert.Equal("Rejected", await ApplyAsync(current with { Epoch = 4, TransferPaused = true },
            predecessorInstall, predecessorOwner));
        Assert.Equal("Rejected", await ApplyAsync(current with { Epoch = 4, TransferPaused = true },
            null, successorOwner));

        // The original commit cannot be replayed after its source owner has
        // been replaced, so it cannot clear the successor's epoch floor again.
        arguments = [context, execution, null, null, null];
        Assert.False(Assert.IsType<bool>(commit.Invoke(service, arguments)));
        Assert.Equal(2, GetPrivateProperty<int>(context, "V4LastStateEpoch"));
        Assert.False(GetPrivateProperty<bool>(context, "PeerPaused"));
        Assert.Equal(512, GetPrivateProperty<int>(context, "RemoteGrantedUntilExclusive"));
        Assert.Equal(currentOwner, GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"));
        Assert.Equal(currentPublication, GetPrivateProperty<RoutePublicationToken>(context, "CurrentRoutePublicationToken"));
        Assert.Empty(GetQueuedV4RepairSends(context));
        Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackFrontierRepairContract"));
    }
}
