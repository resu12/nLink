using NLink.App.Services.ScreenCapture;
using NLink.Core;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
public sealed class ScreenCaptureAbstractionTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenCaptureFactory_CreateDefault_ReturnsNonNull()
    {
        var source = ScreenCaptureFactory.CreateDefault();

        Assert.NotNull(source);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenCaptureFactory_CreateDefault_ReturnsExpectedPlatformSource()
    {
        var source = ScreenCaptureFactory.CreateDefault();

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("WindowsScreenCaptureSource", source.GetType().Name);
            Assert.True(source.IsSupported);
            return;
        }

        Assert.False(source.IsSupported);
        Assert.IsType<NotSupportedCaptureSource>(source);

        try
        {
            await source.StartAsync(CancellationToken.None);
        }
        catch (NotSupportedException)
        {
            return;
        }

        await source.StopAsync();
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task DevLocalTransport_ScreenShareFrameAndStop_RoundTrip()
    {
        var hostAddress = $"devlocal.screenshare.{Guid.NewGuid():N}";
        using var host = new DevLocalTransport(hostAddress);
        using var helper = new DevLocalTransport();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        IncomingJoinRequestEventArgs? pendingJoin = null;
        var joinRaised = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var frameReceived = new TaskCompletionSource<ScreenShareFrameCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        host.IncomingJoinRequest += (_, e) =>
        {
            pendingJoin = e;
            joinRaised.TrySetResult();
        };
        host.Approved += (_, _) => hostApproved.TrySetResult();
        helper.Approved += (_, _) => helperApproved.TrySetResult();
        host.ScreenShareFrameCompleted += (_, e) => frameReceived.TrySetResult(e);
        host.ScreenShareStopped += (_, _) => stopReceived.TrySetResult();

        _ = host.HostByAddressAsync(cts.Token);
        await host.WaitUntilHostReadyAsync(cts.Token);

        var (rawToken, invite) = InviteTestFactory.CreateValidatedInvite(
            new PeerAddress(hostAddress),
            InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
        await helper.JoinByInviteAsync(rawToken, invite, cts.Token).WaitAsync(TimeSpan.FromSeconds(3));

        await joinRaised.Task.WaitAsync(cts.Token);
        await pendingJoin!.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
        await hostApproved.Task.WaitAsync(cts.Token);
        await helperApproved.Task.WaitAsync(cts.Token);

        var authorizedSessionId = Assert.IsType<SessionId>(helper.CurrentSessionSecurityState.SessionId).Value;
        var frameBytes = new byte[] { 5, 4, 3, 2, 1 };
        var framePayload = ScreenSharePayloadCodec.Serialize(new ScreenShareFrameChunkV1
        {
            SessionId = authorizedSessionId,
            FrameId = 17,
            Width = 800,
            Height = 600,
            Encoding = "jpeg",
            ChunkIndex = 0,
            ChunkCount = 1,
            DataBase64 = Convert.ToBase64String(frameBytes),
        });

        await helper.SendScreenSharePayloadAsync(framePayload, cts.Token);
        var frame = await frameReceived.Task.WaitAsync(cts.Token);

        Assert.Equal(17, frame.FrameId);
        Assert.Equal(800, frame.Width);
        Assert.Equal(600, frame.Height);
        Assert.Equal("jpeg", frame.Encoding);
        Assert.Equal(frameBytes, frame.EncodedFrameBytes);
        Assert.Equal(authorizedSessionId, frame.SessionId);

        var stopPayload = ScreenSharePayloadCodec.SerializeStop(new ScreenShareStopMessageV1
        {
            SessionId = authorizedSessionId,
            Reason = "preview_stopped",
        });

        await helper.SendScreenSharePayloadAsync(stopPayload, cts.Token);
        await stopReceived.Task.WaitAsync(cts.Token);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RealNknClientAdapter_BridgeMessage_WithScreenShareChunkV2_RoutesToAssembler()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-route", "screenshare-route.fake");
        using var adapter = new RealNknClientAdapter(identity, options);
        EnableInboundScreenShare(adapter, "session-1", "peer.test");

        ScreenShareFrameChunkV1? receivedChunk = null;
        NknIncomingMessage? receivedRawMessage = null;
        adapter.ScreenShareFrameChunkReceived += (_, chunk) => receivedChunk = chunk;
        adapter.MessageReceived += (_, message) => receivedRawMessage = message;

        var chunk = new ScreenShareFrameChunkV1
        {
            SessionId = "session-1",
            FrameId = 42,
            Width = 1280,
            Height = 720,
            Encoding = "jpeg",
            ChunkIndex = 0,
            ChunkCount = 1,
            DataBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
        };

        adapter.HandleStdoutJsonLineForTests(BuildBridgeMessageLine(chunk));

        Assert.NotNull(receivedChunk);
        Assert.Null(receivedRawMessage);
        Assert.Equal(chunk.SessionId, receivedChunk!.SessionId);
        Assert.Equal(chunk.FrameId, receivedChunk.FrameId);
        Assert.Equal(chunk.Width, receivedChunk.Width);
        Assert.Equal(chunk.Height, receivedChunk.Height);
        Assert.Equal(chunk.Encoding, receivedChunk.Encoding);
        Assert.Equal(chunk.ChunkIndex, receivedChunk.ChunkIndex);
        Assert.Equal(chunk.ChunkCount, receivedChunk.ChunkCount);
        Assert.Equal(chunk.DataBase64, receivedChunk.DataBase64);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RealNknClientAdapter_BridgeMessage_WithUnknownKind_DoesNotRouteToScreenShare()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-unknown", "screenshare-unknown.fake");
        using var adapter = new RealNknClientAdapter(identity, options);

        ScreenShareFrameChunkV1? receivedChunk = null;
        ScreenShareFrameCompletedEventArgs? completed = null;
        NknIncomingMessage? receivedRawMessage = null;
        adapter.ScreenShareFrameChunkReceived += (_, chunk) => receivedChunk = chunk;
        adapter.ScreenShareFrameCompleted += (_, frame) => completed = frame;
        adapter.MessageReceived += (_, message) => receivedRawMessage = message;

        var payloadJson = "{\"kind\":\"other\",\"type\":\"other.frame.v1\",\"value\":1}";
        var payloadBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payloadJson));
        var line = $"{{\"event\":\"message\",\"channel\":\"media\",\"source\":\"peer.test\",\"payloadBase64\":\"{payloadBase64}\",\"isTopic\":false,\"ts\":1731000000000}}";

        adapter.HandleStdoutJsonLineForTests(line);

        Assert.Null(receivedChunk);
        Assert.Null(completed);
        Assert.NotNull(receivedRawMessage);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RealNknClientAdapter_BridgeMessages_WithCompleteScreenShareFrameV2_RaisesCompletedFrame()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-complete", "screenshare-complete.fake");
        using var adapter = new RealNknClientAdapter(identity, options);
        EnableInboundScreenShare(adapter, "session-2", "peer.test");

        ScreenShareFrameCompletedEventArgs? completed = null;
        adapter.ScreenShareFrameCompleted += (_, frame) => completed = frame;

        var frameBytes = new byte[] { 11, 22, 33, 44, 55, 66 };
        var firstChunk = new ScreenShareFrameChunkV1
        {
            SessionId = "session-2",
            FrameId = 77,
            Width = 1024,
            Height = 768,
            Encoding = "jpeg",
            ChunkIndex = 0,
            ChunkCount = 2,
            DataBase64 = Convert.ToBase64String(frameBytes[..3]),
        };

        var secondChunk = firstChunk with
        {
            ChunkIndex = 1,
            DataBase64 = Convert.ToBase64String(frameBytes[3..]),
        };

        adapter.HandleStdoutJsonLineForTests(BuildBridgeMessageLine(firstChunk));
        Assert.Null(completed);

        adapter.HandleStdoutJsonLineForTests(BuildBridgeMessageLine(secondChunk));

        Assert.NotNull(completed);
        Assert.Equal(77, completed!.FrameId);
        Assert.Equal(1024, completed.Width);
        Assert.Equal(768, completed.Height);
        Assert.Equal("jpeg", completed.Encoding);
        Assert.Equal(frameBytes, completed.EncodedFrameBytes);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RealNknClientAdapter_BridgeMessage_ChannelDiagnostics_SplitControlAndMedia()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-channel-diagnostics", "screenshare-channel-diagnostics.fake");
        using var adapter = new RealNknClientAdapter(identity, options);
        EnableInboundScreenShare(adapter, "session-channel-diagnostics", "peer.test");

        var before = NknRuntimeDiagnostics.Snapshot();
        var controlPayloadJson = "{\"kind\":\"other\",\"type\":\"other.frame.v1\",\"value\":1}";
        var controlPayloadBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(controlPayloadJson));
        var controlLine = $"{{\"event\":\"message\",\"channel\":\"control\",\"source\":\"peer.test\",\"payloadBase64\":\"{controlPayloadBase64}\",\"isTopic\":false,\"ts\":1731000000000}}";

        adapter.HandleStdoutJsonLineForTests(controlLine);
        adapter.HandleStdoutJsonLineForTests(BuildBridgeMessageLine(new ScreenShareFrameChunkV1
        {
            SessionId = "session-channel-diagnostics",
            FrameId = 1,
            Width = 320,
            Height = 240,
            Encoding = "jpeg",
            ChunkIndex = 0,
            ChunkCount = 1,
            DataBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
        }));

        var after = NknRuntimeDiagnostics.Snapshot();
        Assert.True(after.BridgeControlMessagesReceived >= before.BridgeControlMessagesReceived + 1);
        Assert.True(after.BridgeMediaMessagesReceived >= before.BridgeMediaMessagesReceived + 1);
        Assert.True(after.BridgeControlBytesReceived > before.BridgeControlBytesReceived);
        Assert.True(after.BridgeMediaBytesReceived > before.BridgeMediaBytesReceived);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RealNknClientAdapter_ScreenShareSubscriberThrow_DoesNotBreakLaterFrames()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-throwing-subscriber", "screenshare-throwing-subscriber.fake");
        using var adapter = new RealNknClientAdapter(identity, options);
        EnableInboundScreenShare(adapter, "session-throwing", "peer.test");

        var throwingCalls = 0;
        ScreenShareFrameCompletedEventArgs? completed = null;
        adapter.ScreenShareFrameCompleted += (_, _) =>
        {
            if (Interlocked.Increment(ref throwingCalls) == 1)
            {
                throw new InvalidOperationException("test screenshare subscriber failure");
            }
        };
        adapter.ScreenShareFrameCompleted += (_, frame) => completed = frame;

        var firstFrameBytes = new byte[] { 11, 22, 33, 44 };
        var secondFrameBytes = new byte[] { 55, 66, 77, 88 };

        adapter.HandleStdoutJsonLineForTests(BuildBridgeMessageLine(new ScreenShareFrameChunkV1
        {
            SessionId = "session-throwing",
            FrameId = 100,
            Width = 320,
            Height = 240,
            Encoding = "jpeg",
            ChunkIndex = 0,
            ChunkCount = 1,
            DataBase64 = Convert.ToBase64String(firstFrameBytes),
        }));

        adapter.HandleStdoutJsonLineForTests(BuildBridgeMessageLine(new ScreenShareFrameChunkV1
        {
            SessionId = "session-throwing",
            FrameId = 101,
            Width = 640,
            Height = 360,
            Encoding = "jpeg",
            ChunkIndex = 0,
            ChunkCount = 1,
            DataBase64 = Convert.ToBase64String(secondFrameBytes),
        }));

        Assert.Equal(2, throwingCalls);
        Assert.NotNull(completed);
        Assert.Equal(101, completed!.FrameId);
        Assert.Equal(640, completed.Width);
        Assert.Equal(360, completed.Height);
        Assert.Equal(secondFrameBytes, completed.EncodedFrameBytes);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RealNknClientAdapter_Disconnected_ClearsIncompleteScreenShareAssemblies()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-disconnect", "screenshare-disconnect.fake");
        using var adapter = new RealNknClientAdapter(identity, options);
        EnableInboundScreenShare(adapter, "session-disconnect", "peer.test");

        ScreenShareFrameCompletedEventArgs? completed = null;
        adapter.ScreenShareFrameCompleted += (_, frame) => completed = frame;

        var frameBytes = new byte[] { 11, 22, 33, 44, 55, 66 };
        var firstChunk = new ScreenShareFrameChunkV1
        {
            SessionId = "session-disconnect",
            FrameId = 88,
            Width = 800,
            Height = 600,
            Encoding = "jpeg",
            ChunkIndex = 0,
            ChunkCount = 2,
            DataBase64 = Convert.ToBase64String(frameBytes[..3]),
        };

        var secondChunk = firstChunk with
        {
            ChunkIndex = 1,
            DataBase64 = Convert.ToBase64String(frameBytes[3..]),
        };

        adapter.HandleStdoutJsonLineForTests(BuildBridgeMessageLine(firstChunk));
        adapter.HandleStdoutJsonLineForTests("{\"event\":\"disconnected\",\"reason\":\"test\"}");
        adapter.HandleStdoutJsonLineForTests(BuildBridgeMessageLine(secondChunk));

        Assert.Null(completed);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RealNknClientAdapter_ScreenShareStop_RaisesStoppedEvent()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-stop", "screenshare-stop.fake");
        using var adapter = new RealNknClientAdapter(identity, options);
        EnableInboundScreenShare(adapter, "session-stop", "peer.test");

        var stoppedSessionId = string.Empty;
        adapter.ScreenShareStopped += (_, sessionId) => stoppedSessionId = sessionId;

        var stop = new ScreenShareStopMessageV1
        {
            SessionId = "session-stop",
            Reason = "user_stop",
        };

        var payloadBase64 = Convert.ToBase64String(ScreenSharePayloadCodec.SerializeStop(stop));
        var line = $"{{\"event\":\"message\",\"channel\":\"media\",\"source\":\"peer.test\",\"payloadBase64\":\"{payloadBase64}\",\"isTopic\":false,\"ts\":1731000000000}}";

        adapter.HandleStdoutJsonLineForTests(line);

        Assert.Equal("session-stop", stoppedSessionId);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RealNknClientAdapter_BridgeMessage_WithUnauthorizedScreenShareChunk_DropsBeforeRouting()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-unauthorized", "screenshare-unauthorized.fake");
        using var adapter = new RealNknClientAdapter(identity, options);
        EnableInboundScreenShare(adapter, "authorized-session", "peer.allowed");
        var diagnosticsBefore = NknRuntimeDiagnostics.Snapshot();

        ScreenShareFrameChunkV1? receivedChunk = null;
        ScreenShareFrameCompletedEventArgs? completed = null;
        NknIncomingMessage? receivedRawMessage = null;
        adapter.ScreenShareFrameChunkReceived += (_, chunk) => receivedChunk = chunk;
        adapter.ScreenShareFrameCompleted += (_, frame) => completed = frame;
        adapter.MessageReceived += (_, message) => receivedRawMessage = message;

        adapter.HandleStdoutJsonLineForTests(BuildBridgeMessageLine(new ScreenShareFrameChunkV1
        {
            SessionId = "stale-session",
            FrameId = 9,
            Width = 640,
            Height = 360,
            Encoding = "jpeg",
            ChunkIndex = 0,
            ChunkCount = 1,
            DataBase64 = Convert.ToBase64String(new byte[] { 9, 9, 9, 9 }),
        }));

        Assert.Null(receivedChunk);
        Assert.Null(completed);
        Assert.Null(receivedRawMessage);
        var diagnosticsAfter = NknRuntimeDiagnostics.Snapshot();
        Assert.True(diagnosticsAfter.MediaPlane.SessionMismatchRejectCount > diagnosticsBefore.MediaPlane.SessionMismatchRejectCount);
        Assert.Equal("session_id_mismatch", diagnosticsAfter.MediaPlane.LastRejectReason);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RealNknClientAdapter_BridgeMessage_WithReplayedScreenShareChunk_IncrementsReplayRejectDiagnostics()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-replay", "screenshare-replay.fake");
        using var adapter = new RealNknClientAdapter(identity, options);
        EnableInboundScreenShare(adapter, "session-replay", "peer.test");
        var diagnosticsBefore = NknRuntimeDiagnostics.Snapshot();

        var chunk = new ScreenShareFrameChunkV1
        {
            SessionId = "session-replay",
            FrameId = 41,
            Width = 640,
            Height = 360,
            Encoding = "jpeg",
            ChunkIndex = 0,
            ChunkCount = 1,
            DataBase64 = Convert.ToBase64String(new byte[] { 0x04, 0x05, 0x06 }),
        };

        adapter.HandleStdoutJsonLineForTests(BuildBridgeMessageLine(chunk));
        adapter.HandleStdoutJsonLineForTests(BuildBridgeMessageLine(chunk));

        var diagnosticsAfter = NknRuntimeDiagnostics.Snapshot();
        Assert.True(diagnosticsAfter.MediaPlane.ReplayRejectCount > diagnosticsBefore.MediaPlane.ReplayRejectCount);
        Assert.Equal("replay_duplicate", diagnosticsAfter.MediaPlane.LastRejectReason);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RealNknClientAdapter_BridgeMessage_WithReplayedChunk_DoesNotResetActiveFrameAssembly()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-replay-assembly", "screenshare-replay-assembly.fake");
        using var adapter = new RealNknClientAdapter(identity, options);
        EnableInboundScreenShare(adapter, "session-replay-assembly", "peer.test");

        ScreenShareFrameCompletedEventArgs? completed = null;
        adapter.ScreenShareFrameCompleted += (_, frame) => completed = frame;

        var firstChunk = new ScreenShareFrameChunkV1
        {
            SessionId = "session-replay-assembly",
            FrameId = 52,
            Width = 800,
            Height = 600,
            Encoding = "jpeg",
            ChunkIndex = 0,
            ChunkCount = 2,
            DataBase64 = Convert.ToBase64String(new byte[] { 0x10, 0x11, 0x12 }),
        };

        var secondChunk = firstChunk with
        {
            ChunkIndex = 1,
            DataBase64 = Convert.ToBase64String(new byte[] { 0x13, 0x14, 0x15 }),
        };

        adapter.HandleStdoutJsonLineForTests(BuildBridgeMessageLine(firstChunk));
        adapter.HandleStdoutJsonLineForTests(BuildBridgeMessageLine(firstChunk));
        adapter.HandleStdoutJsonLineForTests(BuildBridgeMessageLine(secondChunk));

        Assert.NotNull(completed);
        Assert.Equal(52, completed!.FrameId);
        Assert.Equal(new byte[] { 0x10, 0x11, 0x12, 0x13, 0x14, 0x15 }, completed.EncodedFrameBytes);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_SendScreenSharePayloadAsync_UsesRawMediaMessagePath()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.screenshare.address");
            var helperClient = new FakeNknClient("helper.screenshare.address");
            var hostIdentity = new NknIdentity("host-screenshare-id", "host.screenshare.address");
            var helperIdentity = new NknIdentity("helper-screenshare-id", "helper.screenshare.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();

            await host.HostByAddressAsync(cts.Token);
            var (rawToken, invite) = InviteTestFactory.CreateValidatedInvite(
                new PeerAddress(host.LocalPeerAddress),
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var authorizedSessionId = Assert.IsType<SessionId>(helper.CurrentSessionSecurityState.SessionId).Value;

            var frameBytes = new byte[] { 21, 22, 23, 24 };
            var chunk = new ScreenShareFrameChunkV1
            {
                SessionId = authorizedSessionId,
                FrameId = 12,
                Width = 640,
                Height = 360,
                TimestampUnixMilliseconds = 999,
                Encoding = "jpeg",
                ChunkIndex = 0,
                ChunkCount = 1,
                DataBase64 = Convert.ToBase64String(frameBytes),
            };

            var rawPayloadReceived = new TaskCompletionSource<NknIncomingMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var frameReceived = new TaskCompletionSource<ScreenShareFrameCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var payload = ScreenSharePayloadCodec.Serialize(chunk);
            host.ScreenShareFrameCompleted += (_, e) => frameReceived.TrySetResult(e);
            hostClient.MessageReceived += (_, e) =>
            {
                if (TryParseRawScreenShareFrame(e.Payload, out var _))
                {
                    rawPayloadReceived.TrySetResult(e);
                }
            };
            var diagnosticsBeforeSend = NknRuntimeDiagnostics.Snapshot();

            await helper.SendScreenSharePayloadAsync(payload, cts.Token);
            var received = await rawPayloadReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var deliveredFrame = await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var diagnosticsAfterSend = NknRuntimeDiagnostics.Snapshot();

            Assert.Equal(helperIdentity.Address, received.Source);
            Assert.False(received.IsTopic);
            Assert.Null(received.Topic);
            Assert.True(TryParseRawScreenShareFrame(received.Payload, out var rawFrame));
            Assert.Equal(authorizedSessionId, rawFrame!.SessionId);
            Assert.Equal(frameBytes, deliveredFrame.EncodedFrameBytes);
            Assert.Equal(chunk.FrameId, deliveredFrame.FrameId);
            Assert.Equal(chunk.Width, deliveredFrame.Width);
            Assert.Equal(chunk.Height, deliveredFrame.Height);
            Assert.Equal(0, helper.ScreenShareOutboundBusyDrops);
            Assert.Equal(1, helper.ScreenShareMessagesSent);
            Assert.Equal(received.Payload.Length, helper.ScreenSharePayloadBytesSent);
            Assert.Equal(
                diagnosticsBeforeSend.ScreenShareMessagesSent + 1,
                diagnosticsAfterSend.ScreenShareMessagesSent);
            Assert.Equal(
                diagnosticsBeforeSend.ScreenSharePayloadBytesSent + received.Payload.Length,
                diagnosticsAfterSend.ScreenSharePayloadBytesSent);
            Assert.Equal(
                diagnosticsBeforeSend.ScreenShareOutboundBusyDrops,
                diagnosticsAfterSend.ScreenShareOutboundBusyDrops);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_ScreenShareFrame_RawMediaPath_BypassesBusyControlSend()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.chat-priority.address");
            var helperClient = new FakeNknClient("helper.chat-priority.address");
            var hostIdentity = new NknIdentity("host-chat-priority-id", "host.chat-priority.address");
            var helperIdentity = new NknIdentity("helper-chat-priority-id", "helper.chat-priority.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostChatReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sendInvocationCount = 0;
            var rawScreenPayloadReceived = 0;
            var chatPayload = System.Text.Encoding.UTF8.GetBytes("chat-priority-payload");
            byte[] screenPayload = Array.Empty<byte>();
            var screenFrameReceived = new TaskCompletionSource<ScreenShareFrameCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var screenPayloadReceived = new TaskCompletionSource<NknIncomingMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ChatMessageReceived += (_, e) => hostChatReceived.TrySetResult(e.Payload);
            host.ScreenShareFrameCompleted += (_, e) => screenFrameReceived.TrySetResult(e);
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    TryParseRawScreenShareFrame(e.Payload, out var _))
                {
                    Interlocked.Increment(ref rawScreenPayloadReceived);
                    screenPayloadReceived.TrySetResult(e);
                }
            };

            await host.HostByAddressAsync(cts.Token);
            var (rawToken, invite) = InviteTestFactory.CreateValidatedInvite(
                new PeerAddress(host.LocalPeerAddress),
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var authorizedSessionId = Assert.IsType<SessionId>(helper.CurrentSessionSecurityState.SessionId).Value;
            screenPayload = ScreenSharePayloadCodec.Serialize(new ScreenShareFrameChunkV1
            {
                SessionId = authorizedSessionId,
                FrameId = 1,
                Width = 640,
                Height = 360,
                TimestampUnixMilliseconds = 1234,
                Encoding = "jpeg",
                ChunkIndex = 0,
                ChunkCount = 1,
                DataBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
            });

            helperClient.BeforeSendAsync = async (_, _, sendCt) =>
            {
                if (Interlocked.Increment(ref sendInvocationCount) == 1)
                {
                    firstSendStarted.TrySetResult();
                    await releaseFirstSend.Task.WaitAsync(sendCt);
                }
            };

            var chatTask = helper.SendChatMessageAsync(chatPayload, cts.Token);
            await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            var diagnosticsBeforeSend = NknRuntimeDiagnostics.Snapshot();

            var screenSendTask = helper.SendScreenSharePayloadAsync(screenPayload, cts.Token);
            await screenSendTask.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            Assert.Equal(1, Volatile.Read(ref rawScreenPayloadReceived));

            releaseFirstSend.TrySetResult();

            await chatTask;
            var receivedChat = await hostChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var receivedFrame = await screenFrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var receivedPayload = await screenPayloadReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var diagnosticsAfterSend = NknRuntimeDiagnostics.Snapshot();

            Assert.Equal(chatPayload, receivedChat);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, receivedFrame.EncodedFrameBytes);
            Assert.Equal(1, Volatile.Read(ref rawScreenPayloadReceived));
            Assert.Equal(0, helper.ScreenShareOutboundBusyDrops);
            Assert.Equal(1, helper.ScreenShareMessagesSent);
            Assert.Equal(receivedPayload.Payload.Length, helper.ScreenSharePayloadBytesSent);
            Assert.Equal(
                diagnosticsBeforeSend.ScreenShareOutboundBusyDrops,
                diagnosticsAfterSend.ScreenShareOutboundBusyDrops);
            Assert.Equal(
                diagnosticsBeforeSend.ScreenShareMessagesSent + 1,
                diagnosticsAfterSend.ScreenShareMessagesSent);
            Assert.Equal(
                diagnosticsBeforeSend.ScreenSharePayloadBytesSent + receivedPayload.Payload.Length,
                diagnosticsAfterSend.ScreenSharePayloadBytesSent);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_ScreenSharePayload_BypassesBusyOutboundGate_AndStillDelivers()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.chat-budget-drop.address");
            var helperClient = new FakeNknClient("helper.chat-budget-drop.address");
            var hostIdentity = new NknIdentity("host-chat-budget-drop-id", "host.chat-budget-drop.address");
            var helperIdentity = new NknIdentity("helper-chat-budget-drop-id", "helper.chat-budget-drop.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostChatReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sendInvocationCount = 0;
            var rawScreenPayloadReceived = 0;
            var chatPayload = System.Text.Encoding.UTF8.GetBytes("chat-budget-drop-payload");
            byte[] screenPayload = Array.Empty<byte>();
            var screenFrameReceived = new TaskCompletionSource<ScreenShareFrameCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ChatMessageReceived += (_, e) => hostChatReceived.TrySetResult(e.Payload);
            host.ScreenShareFrameCompleted += (_, e) => screenFrameReceived.TrySetResult(e);
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic && TryParseRawScreenShareFrame(e.Payload, out var _))
                {
                    Interlocked.Increment(ref rawScreenPayloadReceived);
                }
            };

            await host.HostByAddressAsync(cts.Token);
            var (rawToken, invite) = InviteTestFactory.CreateValidatedInvite(
                new PeerAddress(host.LocalPeerAddress),
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var authorizedSessionId = Assert.IsType<SessionId>(helper.CurrentSessionSecurityState.SessionId).Value;
            screenPayload = ScreenSharePayloadCodec.Serialize(new ScreenShareFrameChunkV1
            {
                SessionId = authorizedSessionId,
                FrameId = 1,
                Width = 640,
                Height = 360,
                TimestampUnixMilliseconds = 1234,
                Encoding = "jpeg",
                ChunkIndex = 0,
                ChunkCount = 1,
                DataBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
            });

            helperClient.BeforeSendAsync = async (_, _, sendCt) =>
            {
                if (Interlocked.Increment(ref sendInvocationCount) == 1)
                {
                    firstSendStarted.TrySetResult();
                    await releaseFirstSend.Task.WaitAsync(sendCt);
                }
            };

            var chatTask = helper.SendChatMessageAsync(chatPayload, cts.Token);
            await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            var diagnosticsBeforeSend = NknRuntimeDiagnostics.Snapshot();

            await helper.SendScreenSharePayloadAsync(screenPayload, cts.Token).WaitAsync(TimeSpan.FromMilliseconds(300), cts.Token);
            var receivedFrame = await screenFrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            var diagnosticsAfterSend = NknRuntimeDiagnostics.Snapshot();
            Assert.Equal(1, Volatile.Read(ref rawScreenPayloadReceived));
            Assert.Equal(0, helper.ScreenShareOutboundBusyDrops);
            Assert.Equal(1, helper.ScreenShareMessagesSent);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, receivedFrame.EncodedFrameBytes);
            Assert.Equal(
                diagnosticsBeforeSend.ScreenShareOutboundBusyDrops,
                diagnosticsAfterSend.ScreenShareOutboundBusyDrops);
            Assert.Equal(
                diagnosticsBeforeSend.ScreenShareMessagesSent + 1,
                diagnosticsAfterSend.ScreenShareMessagesSent);

            releaseFirstSend.TrySetResult();
            await chatTask;
            var receivedChat = await hostChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            Assert.Equal(chatPayload, receivedChat);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_ScreenShareStop_WaitsForBusyOutboundGate_AndStillDelivers()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.screenshare-stop.address");
            var helperClient = new FakeNknClient("helper.screenshare-stop.address");
            var hostIdentity = new NknIdentity("host-screenshare-stop-id", "host.screenshare-stop.address");
            var helperIdentity = new NknIdentity("helper-screenshare-stop-id", "helper.screenshare-stop.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] stopPayload = Array.Empty<byte>();

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();

            await host.HostByAddressAsync(cts.Token);
            var (rawToken, invite) = InviteTestFactory.CreateValidatedInvite(
                new PeerAddress(host.LocalPeerAddress),
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var authorizedSessionId = Assert.IsType<SessionId>(helper.CurrentSessionSecurityState.SessionId).Value;

            var framePayload = ScreenSharePayloadCodec.Serialize(new ScreenShareFrameChunkV1
            {
                SessionId = authorizedSessionId,
                FrameId = 1,
                Width = 640,
                Height = 360,
                TimestampUnixMilliseconds = 1234,
                Encoding = "jpeg",
                ChunkIndex = 0,
                ChunkCount = 1,
                DataBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
            });

            stopPayload = ScreenSharePayloadCodec.SerializeStop(new ScreenShareStopMessageV1
            {
                SessionId = authorizedSessionId,
                Reason = "preview_stopped",
            });

            var stopPayloadReceived = new TaskCompletionSource<NknIncomingMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var diagnosticsBeforeSend = NknRuntimeDiagnostics.Snapshot();
            host.ScreenShareStopped += (_, _) => stopReceived.TrySetResult();
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.ScreenShareStop)
                {
                    stopPayloadReceived.TrySetResult(e);
                }
            };

            var sendInvocationCount = 0;
            helperClient.BeforeSendAsync = async (_, _, sendCt) =>
            {
                if (Interlocked.Increment(ref sendInvocationCount) == 1)
                {
                    firstSendStarted.TrySetResult();
                    await releaseFirstSend.Task.WaitAsync(sendCt);
                }
            };

            var frameSendTask = helper.SendScreenSharePayloadAsync(framePayload, cts.Token);
            await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);

            var stopSendTask = helper.SendScreenSharePayloadAsync(stopPayload, cts.Token);
            Assert.False(stopSendTask.IsCompleted);

            releaseFirstSend.TrySetResult();

            await frameSendTask;
            await stopSendTask;
            var receivedStop = await stopPayloadReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await stopReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var diagnosticsAfterSend = NknRuntimeDiagnostics.Snapshot();

            Assert.Equal(helperIdentity.Address, receivedStop.Source);
            Assert.True(EnvelopeCodec.TryDeserialize(receivedStop.Payload, out var env));
            Assert.Equal(MsgType.ScreenShareStop, env.Type);
            Assert.True(diagnosticsAfterSend.ControlPlane.LastStopDispatchLatencyMs > -1d);
            Assert.True(diagnosticsAfterSend.ControlLane.MessagesSent >= diagnosticsBeforeSend.ControlLane.MessagesSent);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_ScreenShareStop_RetriesWhenFirstStopPacketIsDropped()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.screenshare-stop-retry.address");
            var helperClient = new FakeNknClient("helper.screenshare-stop-retry.address");
            var hostIdentity = new NknIdentity("host-screenshare-stop-retry-id", "host.screenshare-stop-retry.address");
            var helperIdentity = new NknIdentity("helper-screenshare-stop-retry-id", "helper.screenshare-stop-retry.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();

            await host.HostByAddressAsync(cts.Token);
            var (rawToken, invite) = InviteTestFactory.CreateValidatedInvite(
                new PeerAddress(host.LocalPeerAddress),
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var authorizedSessionId = Assert.IsType<SessionId>(helper.CurrentSessionSecurityState.SessionId).Value;

            var stopPayload = ScreenSharePayloadCodec.SerializeStop(new ScreenShareStopMessageV1
            {
                SessionId = authorizedSessionId,
                Reason = "preview_stopped",
            });

            var stopPayloadReceived = new TaskCompletionSource<NknIncomingMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deliveredStopCount = 0;
            host.ScreenShareStopped += (_, _) => stopReceived.TrySetResult();
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.ScreenShareStop)
                {
                    Interlocked.Increment(ref deliveredStopCount);
                    stopPayloadReceived.TrySetResult(e);
                }
            };

            var droppedFirstStop = 0;
            helperClient.ShouldDeliverSendAsync = (_, payload, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var env) &&
                    env.Type == MsgType.ScreenShareStop &&
                    Interlocked.Exchange(ref droppedFirstStop, 1) == 0)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };

            await helper.SendScreenSharePayloadAsync(stopPayload, cts.Token);
            var receivedStop = await stopPayloadReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await stopReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            Assert.Equal(helperIdentity.Address, receivedStop.Source);
            Assert.True(Volatile.Read(ref droppedFirstStop) == 1);
            Assert.True(EnvelopeCodec.TryDeserialize(receivedStop.Payload, out var env));
            Assert.Equal(MsgType.ScreenShareStop, env.Type);
            Assert.True(Volatile.Read(ref deliveredStopCount) >= 1);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_ScreenShareStop_DeliversViaEnvelope_WhenRawStopPayloadsAreDropped()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.screenshare-stop-envelope.address");
            var helperClient = new FakeNknClient("helper.screenshare-stop-envelope.address");
            var hostIdentity = new NknIdentity("host-screenshare-stop-envelope-id", "host.screenshare-stop-envelope.address");
            var helperIdentity = new NknIdentity("helper-screenshare-stop-envelope-id", "helper.screenshare-stop-envelope.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ScreenShareStopped += (_, _) => stopReceived.TrySetResult();

            await host.HostByAddressAsync(cts.Token);
            var (rawToken, invite) = InviteTestFactory.CreateValidatedInvite(
                new PeerAddress(host.LocalPeerAddress),
                InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var authorizedSessionId = Assert.IsType<SessionId>(helper.CurrentSessionSecurityState.SessionId).Value;

            var stopPayload = ScreenSharePayloadCodec.SerializeStop(new ScreenShareStopMessageV1
            {
                SessionId = authorizedSessionId,
                Reason = "preview_stopped",
            });

            helperClient.ShouldDeliverSendAsync = (_, payload, _) =>
            {
                if (payload.SequenceEqual(stopPayload))
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };

            await helper.SendScreenSharePayloadAsync(stopPayload, cts.Token);
            await stopReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    private static string BuildBridgeMessageLine(ScreenShareFrameChunkV1 chunk)
    {
        var mediaChunk = chunk with
        {
            Kind = "screenshare",
            Type = ScreenShareMediaPacketCodec.ScreenShareFrameTypeV2,
        };
        var payloadBase64 = Convert.ToBase64String(
            ScreenShareMediaPacketCodec.EncryptFrame(
                CreateInboundScreenShareMediaKey(),
                mediaChunk.SessionId,
                sequence: Math.Max(1L, mediaChunk.FrameId * 16L + mediaChunk.ChunkIndex + 1L),
                senderIdentity: "peer.test",
                mediaChunk));
        return $"{{\"event\":\"message\",\"channel\":\"media\",\"source\":\"peer.test\",\"payloadBase64\":\"{payloadBase64}\",\"isTopic\":false,\"ts\":1}}";
    }

    private static void EnableInboundScreenShare(RealNknClientAdapter adapter, string sessionId, string sourceAddress)
    {
        adapter.UpdateInboundScreenSharePolicyAsync(
                enabled: true,
                sessionId: sessionId,
                sourceAddress: sourceAddress,
                expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5),
                ct: CancellationToken.None,
                mediaKey: CreateInboundScreenShareMediaKey(),
                expectedSenderIdentity: sourceAddress)
            .GetAwaiter()
            .GetResult();
    }

    private static bool TryParseRawScreenShareFrame(byte[] payload, out ScreenShareMediaFrameMetadataV2 metadata)
    {
        var ok = ScreenShareMediaPacketCodec.TryDeserializeFrame(payload, out var parsed);
        metadata = ok ? parsed : default!;
        return ok;
    }

    private static byte[] CreateInboundScreenShareMediaKey()
    {
        return NLink.Core.SessionSecurity.SessionKeyDerivation.DeriveScreenShareMediaKey(
            Enumerable.Repeat((byte)0x41, 32).ToArray());
    }
}
