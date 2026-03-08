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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

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
        await Task.Delay(75, cts.Token);

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
    public void RealNknClientAdapter_BridgeMessage_WithScreenShareChunk_RoutesToAssembler()
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

        var payloadBase64 = Convert.ToBase64String(ScreenSharePayloadCodec.Serialize(chunk));
        var line = $"{{\"event\":\"message\",\"source\":\"peer.test\",\"payloadBase64\":\"{payloadBase64}\",\"isTopic\":false,\"ts\":1731000000000}}";

        adapter.HandleStdoutJsonLineForTests(line);

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
        var line = $"{{\"event\":\"message\",\"source\":\"peer.test\",\"payloadBase64\":\"{payloadBase64}\",\"isTopic\":false,\"ts\":1731000000000}}";

        adapter.HandleStdoutJsonLineForTests(line);

        Assert.Null(receivedChunk);
        Assert.Null(completed);
        Assert.NotNull(receivedRawMessage);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RealNknClientAdapter_BridgeMessages_WithCompleteScreenShareFrame_RaisesCompletedFrame()
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
        var line = $"{{\"event\":\"message\",\"source\":\"peer.test\",\"payloadBase64\":\"{payloadBase64}\",\"isTopic\":false,\"ts\":1731000000000}}";

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
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_SendScreenSharePayloadAsync_UsesExistingMessagePath()
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
            var payload = ScreenSharePayloadCodec.Serialize(chunk);
            hostClient.MessageReceived += (_, e) =>
            {
                if (e.Payload.SequenceEqual(payload))
                {
                    rawPayloadReceived.TrySetResult(e);
                }
            };

            await helper.SendScreenSharePayloadAsync(payload, cts.Token);
            var received = await rawPayloadReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            Assert.Equal(helperIdentity.Address, received.Source);
            Assert.False(received.IsTopic);
            Assert.Null(received.Topic);
            Assert.Equal(payload, received.Payload);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_ChatSend_PrioritizesOverConcurrentScreenSharePayload()
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

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ChatMessageReceived += (_, e) => hostChatReceived.TrySetResult(e.Payload);
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic && e.Payload.SequenceEqual(screenPayload))
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

            await helper.SendScreenSharePayloadAsync(screenPayload, cts.Token).WaitAsync(TimeSpan.FromMilliseconds(300), cts.Token);
            Assert.Equal(0, Volatile.Read(ref rawScreenPayloadReceived));

            releaseFirstSend.TrySetResult();

            await chatTask;
            var receivedChat = await hostChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            Assert.Equal(chatPayload, receivedChat);
            Assert.Equal(0, Volatile.Read(ref rawScreenPayloadReceived));
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
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic && e.Payload.SequenceEqual(stopPayload))
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

            Assert.Equal(helperIdentity.Address, receivedStop.Source);
            Assert.Equal(stopPayload, receivedStop.Payload);
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
            var deliveredStopCount = 0;
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic && e.Payload.SequenceEqual(stopPayload))
                {
                    Interlocked.Increment(ref deliveredStopCount);
                    stopPayloadReceived.TrySetResult(e);
                }
            };

            var droppedFirstStop = 0;
            helperClient.ShouldDeliverSendAsync = (_, payload, _) =>
            {
                if (payload.SequenceEqual(stopPayload) && Interlocked.Exchange(ref droppedFirstStop, 1) == 0)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };

            await helper.SendScreenSharePayloadAsync(stopPayload, cts.Token);
            var receivedStop = await stopPayloadReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            Assert.Equal(helperIdentity.Address, receivedStop.Source);
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
        var payloadBase64 = Convert.ToBase64String(ScreenSharePayloadCodec.Serialize(chunk));
        return $"{{\"event\":\"message\",\"source\":\"peer.test\",\"payloadBase64\":\"{payloadBase64}\",\"isTopic\":false,\"ts\":1}}";
    }

    private static void EnableInboundScreenShare(RealNknClientAdapter adapter, string sessionId, string sourceAddress)
    {
        adapter.UpdateInboundScreenSharePolicyAsync(
                enabled: true,
                sessionId: sessionId,
                sourceAddress: sourceAddress,
                expiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(5),
                ct: CancellationToken.None)
            .GetAwaiter()
            .GetResult();
    }
}
