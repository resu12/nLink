using NLink.App.Services.ScreenCapture;
using NLink.Core;
using NLink.Core.ScreenShare;
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
    public void RealNknClientAdapter_BridgeMessage_WithScreenShareChunk_RoutesToAssembler()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-route", "screenshare-route.fake");
        using var adapter = new RealNknClientAdapter(identity, options);

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

            var code = new SessionCode("654321");
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();

            await host.HostAsync(code, cts.Token);
            await helper.JoinAsync(code, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            await pendingJoin.ApproveAsync(cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            var frameBytes = new byte[] { 21, 22, 23, 24 };
            var chunk = new ScreenShareFrameChunkV1
            {
                SessionId = "session-wire",
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

            var code = new SessionCode("654322");
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostChatReceived = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sendInvocationCount = 0;
            var rawScreenPayloadReceived = 0;
            var chatPayload = System.Text.Encoding.UTF8.GetBytes("chat-priority-payload");
            var screenPayload = ScreenSharePayloadCodec.Serialize(new ScreenShareFrameChunkV1
            {
                SessionId = "session-chat-priority",
                FrameId = 1,
                Width = 640,
                Height = 360,
                TimestampUnixMilliseconds = 1234,
                Encoding = "jpeg",
                ChunkIndex = 0,
                ChunkCount = 1,
                DataBase64 = Convert.ToBase64String(new byte[] { 1, 2, 3, 4 }),
            });

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

            await host.HostAsync(code, cts.Token);
            await helper.JoinAsync(code, cts.Token);

            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            await pendingJoin.ApproveAsync(cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

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

    private static string BuildBridgeMessageLine(ScreenShareFrameChunkV1 chunk)
    {
        var payloadBase64 = Convert.ToBase64String(ScreenSharePayloadCodec.Serialize(chunk));
        return $"{{\"event\":\"message\",\"source\":\"peer.test\",\"payloadBase64\":\"{payloadBase64}\",\"isTopic\":false,\"ts\":1}}";
    }
}
