using System.Reflection;
using NLink.Core.FileTransfer;

namespace NLink.SmokeTests;

public sealed partial class SessionFileTransferV4SenderTests
{
    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackStartupCheckpoint")]
    public async Task FallbackV6Startup_PreparedOrdinaryPayloadCannotSendAfterLegCloses()
    {
        using var service = new SessionFileTransferService();
        const string transferId = "fallback_startup_prepared_then_closed";
        var context = CreatePostTunaFallbackV6OutboundContext(transferId,
            remoteNextExpectedChunkIndex: 0, chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0, chunkCount: 1);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(SessionFileTransferService);
        type.GetField("outboundTransfer", flags)!.SetValue(service, context);
        var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        Assert.True(leg.CanSendData);
        await (Task)type.GetMethod("ApplyOutboundV6ReceiverState", flags)!.Invoke(service,
        [context, new FileTransferReceiverStateFrameV6
        {
            SessionId = $"session_{transferId}", TransferId = transferId, Epoch = 1,
            ContiguousCommittedChunkIndex = 0, DurableReceivedHighestChunkIndex = -1,
            CreditUntilChunkIndexExclusive = 1,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
            TransportEpoch = leg.TransportEpochId,
        }, FileTransferTransportKind.RegularNkn])!;
        object?[] args = [context, true, null, null];
        Assert.True((bool)type.GetMethod("TryDequeueOutboundV6ChunkRunLocked", flags)!.Invoke(service, args)!);
        using var stream = new MemoryStream(new byte[21 * 1024], writable: false);
        var preparing = (Task)type.GetMethod("PrepareChunkBatchV6Async", flags)!.Invoke(service, [context, stream, args[2], args[3]])!;
        await preparing;
        var prepared = preparing.GetType().GetProperty("Result")!.GetValue(preparing);
        Assert.NotNull(prepared);
        var shouldDrop = type.GetMethod("ShouldDropPreparedV6ChunkBatchBeforeSend", flags)!;
        Assert.False((bool)shouldDrop.Invoke(service, [context, prepared])!);
        type.GetMethod("StartOutboundPostTunaRecoveryLocked", flags)!.Invoke(service, [context, "post_tuna_fallback_v6_route"]);
        Assert.True((bool)shouldDrop.Invoke(service, [context, prepared])!);
        Assert.False(GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg").CanSendData);
    }

    [Fact]
    [Trait("FileTransferRuntimeSuite", "FallbackStartupCheckpoint")]
    public async Task FallbackV6Startup_ClosedLegWithoutRequestCannotDequeueOrdinaryPayload()
    {
        using var service = new SessionFileTransferService();
        const string transferId = "fallback_startup_closed_without_request";
        var context = CreatePostTunaFallbackV6OutboundContext(transferId,
            remoteNextExpectedChunkIndex: 0, chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0, chunkCount: 1);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(SessionFileTransferService);
        type.GetField("outboundTransfer", flags)!.SetValue(service, context);
        type.GetMethod("StartOutboundPostTunaRecoveryLocked", flags)!
            .Invoke(service, [context, "post_tuna_fallback_v6_route"]);
        var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
        Assert.Equal(FileTransferLegState.CheckpointPending, leg.State);
        Assert.False(leg.CanSendData);
        Assert.Null(leg.CheckpointRequestId);
        Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackCheckpointLease"));

        await (Task)type.GetMethod("ApplyOutboundV6ReceiverState", flags)!.Invoke(service,
        [context, new FileTransferReceiverStateFrameV6
        {
            SessionId = $"session_{transferId}", TransferId = transferId, Epoch = 1,
            ContiguousCommittedChunkIndex = 0, DurableReceivedHighestChunkIndex = -1,
            CreditUntilChunkIndexExclusive = 1,
            MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
            TransportEpoch = leg.TransportEpochId,
        }, FileTransferTransportKind.RegularNkn])!;

        object?[] args = [context, true, null, null];
        var dequeued = (bool)type.GetMethod("TryDequeueOutboundV6ChunkRunLocked", flags)!.Invoke(service, args)!;
        Assert.False(dequeued);
        Assert.Empty(Assert.IsType<List<int>>(args[2]));
        Assert.False(leg.CanSendData);
        Assert.Null(GetPrivatePropertyValue(context, "CurrentRepairReplayDecision"));
    }

    [Theory]
    [Trait("FileTransferRuntimeSuite", "FallbackStartupCheckpoint")]
    [InlineData("missing_id")]
    [InlineData("wrong_id")]
    [InlineData("stale_epoch")]
    [InlineData("duplicate")]
    public async Task FallbackV6Startup_RequiresMatchingCheckpointResponse(string variant)
    {
        var transferId = $"startup_proof_{Guid.NewGuid():N}";
        var sessionId = $"session_{transferId}";
        using var senderTransport = new LoopbackFileTransferTransport(sessionId) { IsPostTunaFileFallbackActiveForRouteSelection = true };
        using var receiverTransport = new LoopbackFileTransferTransport(sessionId) { IsPostTunaFileFallbackActiveForRouteSelection = true };
        senderTransport.Connect(receiverTransport);
        using var sender = new SessionFileTransferService();
        using var receiver = new SessionFileTransferService();
        sender.AttachTransport(senderTransport);
        receiver.AttachTransport(receiverTransport);
        var responseHeld = new TaskCompletionSource<FileTransferReceiverStateFrameV6>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        senderTransport.OutboundDataFrameDeliveryOverrideAsync = (_, frame, _) =>
        {
            if (frame is FileTransferFrontierRequestFrameV6 { RecoveryMode: "regular_nkn_state_refresh" })
            {
                var context = typeof(SessionFileTransferService).GetField("outboundTransfer",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(sender)!;
                Assert.Equal(GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner"),
                    GetPrivateProperty<RuntimeOwnerToken>(context, "V6StartupCheckpointOwner"));
            }
            return Task.FromResult(false);
        };
        receiverTransport.OutboundDataFrameDeliveryOverrideAsync = async (target, frame, ct) =>
        {
            if (frame is FileTransferReceiverStateFrameV6 state && state.RecoveryMode == "regular_nkn_state_refresh")
            {
                responseHeld.TrySetResult(state);
                await release.Task.WaitAsync(ct);
                if (variant == "duplicate") { target.ReceiveDeliveredDataFrame(state); }
            }
            return false;
        };
        try
        {
            await sender.TryStartSendAsync(new FileTransferSendDescriptor("proof.bin", 2048, transferId, ChunkSizeBytes: 2048),
                _ => Task.FromResult<Stream>(new MemoryStream(new byte[2048], writable: false)), CancellationToken.None);
            await WaitUntilAsync(() => receiver.Snapshot.InboundState == FileTransferTransferState.PendingDecision);
            await receiver.AcceptIncomingTransferAsync(transferId,
                (_, _) => Task.FromResult(new FileTransferReceiveDestination(new MemoryStream())), CancellationToken.None);
            var proof = await responseHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var context = typeof(SessionFileTransferService).GetField("outboundTransfer", flags)!.GetValue(sender)!;
            if (variant != "duplicate")
            {
                var invalid = proof with
                {
                    Epoch = proof.Epoch + 1,
                    RepairRequestId = variant == "missing_id" ? null : variant == "wrong_id" ? "wrong-checkpoint" : proof.RepairRequestId,
                    RecoveryMode = variant == "missing_id" ? null : proof.RecoveryMode,
                    TransportEpoch = variant == "stale_epoch" ? proof.TransportEpoch - 1 : proof.TransportEpoch,
                    ContiguousCommittedChunkIndex = 1, DurableReceivedHighestChunkIndex = 0,
                    BytesCommitted = 2048, MissingRanges = [], TerminalReady = true,
                };
                var beforeEpoch = GetPrivateProperty<int>(context, "V6LastReceiverStateEpoch");
                var applying = (Task<bool>)typeof(SessionFileTransferService).GetMethod("ApplyOutboundV6ReceiverState", flags)!
                    .Invoke(sender, [context, invalid, FileTransferTransportKind.RegularNkn])!;
                Assert.False(await applying);
                Assert.Equal(beforeEpoch, GetPrivateProperty<int>(context, "V6LastReceiverStateEpoch"));
                Assert.Equal(0L, GetPrivateProperty<long>(context, "BytesTransferred"));
                Assert.NotEqual(FileTransferTransferState.Completed, sender.Snapshot.OutboundState);
            }
            var leg = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg");
            Assert.False(leg.CanSendData);
            Assert.Equal(FileTransferLegState.CheckpointPending, leg.State);
            Assert.Equal(proof.RepairRequestId, leg.CheckpointRequestId);
            Assert.NotNull(GetPrivatePropertyValue(context, "V6StartupCheckpointOwner"));
            Assert.Empty(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>());
            release.TrySetResult();
            try
            {
                await WaitUntilAsync(() => sender.Snapshot.OutboundState == FileTransferTransferState.Completed &&
                    receiver.Snapshot.InboundState == FileTransferTransferState.Completed, timeoutMs: 8000);
            }
            catch (TaskCanceledException)
            {
                throw new Xunit.Sdk.XunitException(System.Text.Json.JsonSerializer.Serialize(new
                {
                    sender = sender.Snapshot.Outbound, receiver = receiver.Snapshot.Inbound,
                    leg = GetPrivatePropertyValue(context, "CurrentTransferLeg"),
                    chunks = senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>().Count(),
                }));
            }
            Assert.Single(senderTransport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>());
            Assert.Null(GetPrivatePropertyValue(context, "V6StartupCheckpointOwner"));
        }
        finally { release.TrySetResult(); }
    }

    [Theory]
    [Trait("FileTransferRuntimeSuite", "FallbackStartupCheckpoint")]
    [InlineData("manifest", "session")]
    [InlineData("manifest", "runtime")]
    [InlineData("receiver_state", "session")]
    [InlineData("receiver_state", "runtime")]
    public async Task FallbackV6Startup_LateBoundaryCannotRegisterCheckpointForReplacement(string boundary, string invalidation)
    {
        var transferId = $"startup_stale_{Guid.NewGuid():N}";
        var sessionId = $"session_{transferId}";
        using var transport = new LoopbackFileTransferTransport(sessionId) { IsPostTunaFileFallbackActiveForRouteSelection = true };
        using var peer = new LoopbackFileTransferTransport(sessionId);
        transport.Connect(peer);
        using var service = new SessionFileTransferService();
        service.AttachTransport(transport);
        var context = CreatePostTunaFallbackV6OutboundContext(transferId,
            remoteNextExpectedChunkIndex: 0, chunksAcceptedForTransport: 0,
            remoteGrantedUntilExclusive: 0, chunkCount: 1);
        SetPrivateProperty(context, "Sha256Base64", Convert.ToBase64String(new byte[32]));
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(SessionFileTransferService);
        type.GetField("outboundTransfer", flags)!.SetValue(service, context);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.OutboundDataFrameDeliveryOverrideAsync = async (_, frame, ct) =>
        {
            if (frame is FileTransferManifestFrameV6)
            {
                entered.TrySetResult();
                if (boundary == "manifest") { await release.Task.WaitAsync(ct); }
            }
            return false;
        };
        var running = (Task)type.GetMethod("RunOutboundV6SenderAsync", flags)!.Invoke(service, [context])!;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            if (boundary == "receiver_state")
            {
                await WaitUntilAsync(() => service.Snapshot.OutboundState == FileTransferTransferState.Sending);
            }
            lock (type.GetField("gate", flags)!.GetValue(service)!)
            {
                if (invalidation == "session")
                {
                    SetPrivateProperty(context, "DataSessionGeneration", GetPrivateProperty<long>(context, "DataSessionGeneration") + 1);
                }
                else
                {
                    var owner = GetPrivateProperty<RuntimeOwnerToken>(context, "CurrentRuntimeOwner");
                    SetPrivateProperty(context, "CurrentRuntimeOwner", owner with { RuntimeGeneration = owner.RuntimeGeneration + 1 });
                    SetPrivateProperty(context, "LastRuntimeOwnerGeneration", owner.RuntimeGeneration + 1);
                }
            }
            release.TrySetResult();
            if (boundary == "receiver_state")
            {
                transport.ReceiveDeliveredDataFrame(new FileTransferReceiverStateFrameV6
                {
                    SessionId = sessionId, TransferId = transferId, Epoch = 1,
                    TransportEpoch = GetPrivateProperty<FileTransferLeg>(context, "CurrentTransferLeg").TransportEpochId,
                    MissingRanges = [new FileTransferRangeV4 { StartChunkIndex = 0, ChunkCount = 1 }],
                });
            }
            await running.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Empty(transport.SentDataFrames.OfType<FileTransferFrontierRequestFrameV6>());
            Assert.Empty(transport.SentDataFrames.OfType<FileTransferChunkBatchFrameV6>());
            Assert.Null(GetPrivatePropertyValue(context, "CurrentFallbackCheckpointLease"));
            Assert.Equal(-1, GetPrivateProperty<int>(context, "V6LastReceiverStateEpoch"));
        }
        finally { release.TrySetResult(); GetPrivateProperty<CancellationTokenSource>(context, "LifetimeCts").Cancel(); }
    }
}
