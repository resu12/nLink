using NLink.App.Services.ScreenCapture;
using NLink.Core;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.DevLocal;
using NLink.Infra.Nkn;
using System.Collections.Concurrent;
using System.Reflection;

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
            if (WindowsH264ScreenCaptureSource.IsPreviewRuntimeSupported())
            {
                Assert.Equal("WindowsH264ScreenCaptureSource", source.GetType().Name);
                Assert.True(source.IsSupported);
                return;
            }
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
        var streamConfig = CreateVideoStreamConfig(authorizedSessionId, streamEpoch: 1);
        var framePayload = CreateVideoFragmentPayload(authorizedSessionId, frameId: 17, width: 800, height: 600, frameBytes, streamEpoch: 1);

        await helper.SendScreenShareVideoStreamConfigAsync(streamConfig, cts.Token);
        await helper.SendScreenSharePayloadAsync(framePayload, cts.Token);
        var frame = await frameReceived.Task.WaitAsync(cts.Token);

        Assert.Equal(17, frame.FrameId);
        Assert.Equal(800, frame.Width);
        Assert.Equal(600, frame.Height);
        Assert.Equal("h264", frame.Encoding);
        Assert.Equal(frameBytes, frame.EncodedFrameBytes);
        Assert.Equal(authorizedSessionId, frame.SessionId);
        Assert.Equal(1, frame.StreamEpoch);

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
    public void RealNknClientAdapter_BridgeMessage_WithUnknownPayload_RemainsRawMessage()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-unknown", "screenshare-unknown.fake");
        using var adapter = new RealNknClientAdapter(identity, options);

        NknIncomingMessage? receivedRawMessage = null;
        adapter.MessageReceived += (_, message) => receivedRawMessage = message;

        var payloadJson = "{\"kind\":\"other\",\"type\":\"other.frame.v1\",\"value\":1}";
        var payloadBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payloadJson));
        var line = $"{{\"event\":\"message\",\"source\":\"peer.test\",\"payloadBase64\":\"{payloadBase64}\",\"isTopic\":false,\"ts\":1731000000000}}";

        adapter.HandleStdoutJsonLineForTests(line);

        Assert.NotNull(receivedRawMessage);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RealNknClientAdapter_MediaChannelUnknownPayload_RemainsClassifiedAsMedia()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-media-unknown", "screenshare-media-unknown.fake");
        using var adapter = new RealNknClientAdapter(identity, options);

        NknIncomingMessage? receivedRawMessage = null;
        adapter.MessageReceived += (_, message) => receivedRawMessage = message;

        var payloadJson = "{\"kind\":\"other\",\"type\":\"other.frame.v1\",\"value\":1}";
        var payloadBase64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payloadJson));
        var line = $"{{\"event\":\"message\",\"source\":\"peer.test\",\"channel\":\"media\",\"payloadBase64\":\"{payloadBase64}\",\"isTopic\":false,\"ts\":1731000000000}}";

        adapter.HandleStdoutJsonLineForTests(line);

        Assert.NotNull(receivedRawMessage);
        Assert.Equal(NknBridgeChannel.Media, receivedRawMessage!.Channel);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void RealNknClientAdapter_ScreenShareQueueStateEvent_UpdatesBridgeQueueSnapshot()
    {
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("screenshare-queue-state", "screenshare-queue-state.fake");
        using var adapter = new RealNknClientAdapter(identity, options);

        var capability = Assert.IsAssignableFrom<IBridgeScreenShareQueueCapability>(adapter);
        BridgeScreenShareQueueStateChangedEventArgs? received = null;
        capability.ScreenShareQueueStateChanged += (_, e) => received = e;

        adapter.HandleStdoutJsonLineForTests(
            "{\"event\":\"screen_share_queue_state\",\"queueDepth\":9,\"queuedBytes\":131072,\"oldestQueuedAgeMs\":275,\"inFlight\":true,\"droppedSinceLast\":2,\"congested\":true,\"severe\":false,\"mode\":\"catch_up_only\"}");

        Assert.NotNull(received);
        Assert.Equal(9, received!.State.QueueDepth);
        Assert.Equal(131072, received.State.QueuedBytes);
        Assert.Equal(275, received.State.OldestQueuedAgeMs);
        Assert.True(received.State.InFlight);
        Assert.Equal(2, received.State.DroppedSinceLast);
        Assert.True(received.State.IsCongested);
        Assert.False(received.State.IsSevere);
        Assert.Equal(BridgeScreenShareQueueMode.CatchUpOnly, received.State.Mode);
        Assert.Equal(received.State, capability.CurrentScreenShareQueueState);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_SendScreenSharePayloadAsync_UsesMediaChannel()
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
            var streamConfigReceived = new TaskCompletionSource<ScreenShareVideoStreamConfigV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ScreenShareVideoStreamConfigReceived += (_, e) => streamConfigReceived.TrySetResult(e.Message);

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
            var streamConfig = CreateVideoStreamConfig(authorizedSessionId, streamEpoch: 1);
            var rawPayloadReceived = new TaskCompletionSource<NknIncomingMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var frameReceived = new TaskCompletionSource<ScreenShareFrameCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var payload = CreateVideoFragmentPayload(authorizedSessionId, frameId: 0, width: 640, height: 360, frameBytes, streamEpoch: 1, capturedTsUtcMs: 999);
            var controlFrameCount = 0;
            host.ScreenShareFrameCompleted += (_, e) => frameReceived.TrySetResult(e);
            hostClient.MessageReceived += (_, e) =>
            {
                if (EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.ScreenShareFrame)
                {
                    if (e.Channel == NknBridgeChannel.Control)
                    {
                        Interlocked.Increment(ref controlFrameCount);
                    }

                    rawPayloadReceived.TrySetResult(e);
                }
            };
            var diagnosticsBeforeSend = NknRuntimeDiagnostics.Snapshot();

            await helper.SendScreenShareVideoStreamConfigAsync(streamConfig, cts.Token);
            var receivedConfig = await streamConfigReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helper.SendScreenSharePayloadAsync(payload, cts.Token);
            var received = await rawPayloadReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var deliveredFrame = await frameReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await Task.Delay(100, cts.Token);
            var diagnosticsAfterSend = NknRuntimeDiagnostics.Snapshot();

            Assert.Equal(authorizedSessionId, receivedConfig.SessionId);
            Assert.Equal(1, receivedConfig.StreamEpoch);
            Assert.Equal(helperClient.ConnectedMediaAddress, received.Source);
            Assert.False(received.IsTopic);
            Assert.Null(received.Topic);
            Assert.Equal(NknBridgeChannel.Media, received.Channel);
            Assert.True(EnvelopeCodec.TryDeserialize(received.Payload, out var env));
            Assert.Equal(MsgType.ScreenShareFrame, env.Type);
            Assert.Equal(frameBytes, deliveredFrame.EncodedFrameBytes);
            Assert.Equal(0, deliveredFrame.FrameId);
            Assert.Equal(640, deliveredFrame.Width);
            Assert.Equal(360, deliveredFrame.Height);
            Assert.Equal("h264", deliveredFrame.Encoding);
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
            Assert.Equal(0, Volatile.Read(ref controlFrameCount));
            Assert.Equal(
                diagnosticsBeforeSend.MediaPlane.FramesSent + 1,
                diagnosticsAfterSend.MediaPlane.FramesSent);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_RecoveryOwnerAndFollowers_FallBackToControlWhenMediaDeliveryIsDropped()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.screenshare-control-fallback.address");
            var helperClient = new FakeNknClient("helper.screenshare-control-fallback.address");
            var hostIdentity = new NknIdentity("host-screenshare-control-fallback-id", "host.screenshare-control-fallback.address");
            var helperIdentity = new NknIdentity("helper-screenshare-control-fallback-id", "helper.screenshare-control-fallback.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamConfigReceived = new TaskCompletionSource<ScreenShareVideoStreamConfigV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            var threeFramesReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deliveredFrameIds = new List<long>();
            var deliveredChannels = new List<NknBridgeChannel>();
            var deliveredSources = new List<string>();
            var gate = new object();

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ScreenShareVideoStreamConfigReceived += (_, e) => streamConfigReceived.TrySetResult(e.Message);
            host.ScreenShareFrameCompleted += (_, e) =>
            {
                lock (gate)
                {
                    deliveredFrameIds.Add(e.FrameId);
                    if (deliveredFrameIds.Count >= 3)
                    {
                        threeFramesReceived.TrySetResult();
                    }
                }
            };

            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.ScreenShareFrame)
                {
                    lock (gate)
                    {
                        deliveredChannels.Add(e.Channel);
                        deliveredSources.Add(e.Source);
                    }
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
            const long recoveryBurstToken = 41;

            helper.ArmRecoveryBurstControlFallback(authorizedSessionId, streamEpoch: 1, burstToken: recoveryBurstToken, ownerFrameId: 0);

            helperClient.ShouldDeliverSendAsync = (destination, _, _) =>
                Task.FromResult(!string.Equals(destination, hostClient.ConnectedMediaAddress, StringComparison.Ordinal));

            await helper.SendScreenShareVideoStreamConfigAsync(CreateVideoStreamConfig(authorizedSessionId, streamEpoch: 1), cts.Token);
            await streamConfigReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            await helper.SendScreenSharePayloadAsync(
                    CreateVideoFragmentPayload(authorizedSessionId, frameId: 0, width: 640, height: 360, new byte[] { 0x01 }, streamEpoch: 1, capturedTsUtcMs: 1000, isKeyFrame: true),
                    recoverySendRole: "owner",
                    recoveryBurstToken: recoveryBurstToken,
                    cts.Token)
                .WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            await helper.SendScreenSharePayloadAsync(
                    CreateVideoFragmentPayload(authorizedSessionId, frameId: 1, width: 640, height: 360, new byte[] { 0x02 }, streamEpoch: 1, capturedTsUtcMs: 1010, isKeyFrame: false),
                    recoverySendRole: "protected_follower",
                    recoveryBurstToken: recoveryBurstToken,
                    cts.Token)
                .WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            await helper.SendScreenSharePayloadAsync(
                    CreateVideoFragmentPayload(authorizedSessionId, frameId: 2, width: 640, height: 360, new byte[] { 0x03 }, streamEpoch: 1, capturedTsUtcMs: 1020, isKeyFrame: false),
                    recoverySendRole: "protected_follower",
                    recoveryBurstToken: recoveryBurstToken,
                    cts.Token)
                .WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 3, width: 640, height: 360, new byte[] { 0x04 }, streamEpoch: 1, capturedTsUtcMs: 1030, isKeyFrame: false),
                recoverySendRole: null,
                recoveryBurstToken: 0,
                cts.Token);

            await threeFramesReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await Task.Delay(300, cts.Token);

            lock (gate)
            {
                Assert.Equal(new long[] { 0, 1, 2 }, deliveredFrameIds);
                Assert.All(deliveredChannels, channel => Assert.Equal(NknBridgeChannel.Control, channel));
                Assert.All(deliveredSources, source => Assert.Equal(helperClient.ConnectedAddress, source));
                Assert.DoesNotContain(3L, deliveredFrameIds);
            }
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_RecoveryOwnerControlFallback_RetriesDroppedOwnerKeyframe()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.screenshare-control-bootstrap.address");
            var helperClient = new FakeNknClient("helper.screenshare-control-bootstrap.address");
            var hostIdentity = new NknIdentity("host-screenshare-control-bootstrap-id", "host.screenshare-control-bootstrap.address");
            var helperIdentity = new NknIdentity("helper-screenshare-control-bootstrap-id", "helper.screenshare-control-bootstrap.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamConfigReceived = new TaskCompletionSource<ScreenShareVideoStreamConfigV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            var threeFramesReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new object();
            var deliveredFrameIds = new List<long>();
            var deliveredChannels = new List<NknBridgeChannel>();
            var droppedBootstrapKeyframe = 0;

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ScreenShareVideoStreamConfigReceived += (_, e) => streamConfigReceived.TrySetResult(e.Message);
            host.ScreenShareFrameCompleted += (_, e) =>
            {
                lock (gate)
                {
                    deliveredFrameIds.Add(e.FrameId);
                    if (deliveredFrameIds.Count >= 3)
                    {
                        threeFramesReceived.TrySetResult();
                    }
                }
            };

            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.ScreenShareFrame)
                {
                    lock (gate)
                    {
                        deliveredChannels.Add(e.Channel);
                    }
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
            const long recoveryBurstToken = 99;

            helper.ArmRecoveryBurstControlFallback(authorizedSessionId, streamEpoch: 1, burstToken: recoveryBurstToken, ownerFrameId: 0);

            helperClient.ShouldDeliverSendAsync = (destination, payload, _) =>
            {
                if (string.Equals(destination, hostClient.ConnectedMediaAddress, StringComparison.Ordinal))
                {
                    return Task.FromResult(false);
                }

                if (!string.Equals(destination, hostClient.ConnectedAddress, StringComparison.Ordinal) ||
                    !EnvelopeCodec.TryDeserialize(payload, out var env) ||
                    env.Type != MsgType.ScreenShareFrame)
                {
                    return Task.FromResult(true);
                }

                if (Interlocked.Exchange(ref droppedBootstrapKeyframe, 1) == 0)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };

            await helper.SendScreenShareVideoStreamConfigAsync(CreateVideoStreamConfig(authorizedSessionId, streamEpoch: 1), cts.Token);
            await streamConfigReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 0, width: 640, height: 360, new byte[] { 0x01 }, streamEpoch: 1, capturedTsUtcMs: 1000, isKeyFrame: true),
                recoverySendRole: "owner",
                recoveryBurstToken: recoveryBurstToken,
                cts.Token);
            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 1, width: 640, height: 360, new byte[] { 0x02 }, streamEpoch: 1, capturedTsUtcMs: 1010, isKeyFrame: false),
                recoverySendRole: "protected_follower",
                recoveryBurstToken: recoveryBurstToken,
                cts.Token);
            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 2, width: 640, height: 360, new byte[] { 0x03 }, streamEpoch: 1, capturedTsUtcMs: 1020, isKeyFrame: false),
                recoverySendRole: "protected_follower",
                recoveryBurstToken: recoveryBurstToken,
                cts.Token);

            await threeFramesReceived.Task.WaitAsync(TimeSpan.FromSeconds(4), cts.Token);

            lock (gate)
            {
                Assert.Equal(new long[] { 0, 1, 2 }, deliveredFrameIds);
                Assert.All(deliveredChannels, channel => Assert.Equal(NknBridgeChannel.Control, channel));
            }

            Assert.Equal(1, Volatile.Read(ref droppedBootstrapKeyframe));
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_ScreenShareRecoveryControlFallback_RetriesDroppedRecoveryKeyframeBeyondStartupWindow()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.screenshare-control-recovery.address");
            var helperClient = new FakeNknClient("helper.screenshare-control-recovery.address");
            var hostIdentity = new NknIdentity("host-screenshare-control-recovery-id", "host.screenshare-control-recovery.address");
            var helperIdentity = new NknIdentity("helper-screenshare-control-recovery-id", "helper.screenshare-control-recovery.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamConfigReceived = new TaskCompletionSource<ScreenShareVideoStreamConfigV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            var threeFramesReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new object();
            var deliveredFrameIds = new List<long>();
            var deliveredChannels = new List<NknBridgeChannel>();
            var droppedRecoveryKeyframe = 0;
            byte[]? decryptKey = null;

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ScreenShareVideoStreamConfigReceived += (_, e) => streamConfigReceived.TrySetResult(e.Message);

            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.ScreenShareFrame &&
                    decryptKey is not null)
                {
                    var securePayload = SessionSecureEnvelopeCodec.Decrypt(
                        decryptKey,
                        env.Payload,
                        new SessionSecureEnvelopeExpectation(
                            Family: SessionSecureMessageFamily.ScreenShare,
                            MessageType: "screenshare_frame"));
                    if (!ScreenShareVideoPayloadCodec.TryDeserializeFragmentEnvelope(securePayload.Plaintext, out var fragments, out var isBatch) ||
                        isBatch ||
                        fragments.Length == 0)
                    {
                        return;
                    }

                    lock (gate)
                    {
                        foreach (var fragment in fragments)
                        {
                            deliveredFrameIds.Add(fragment.FrameId);
                            deliveredChannels.Add(e.Channel);
                        }

                        if (deliveredFrameIds.Distinct().Count() >= 3)
                        {
                            threeFramesReceived.TrySetResult();
                        }
                    }
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
            decryptKey = Assert.IsType<byte[]>(GetPrivateField(host, "controlSessionSharedKey"));

            helperClient.ShouldDeliverSendAsync = (destination, payload, _) =>
            {
                if (string.Equals(destination, hostClient.ConnectedMediaAddress, StringComparison.Ordinal))
                {
                    return Task.FromResult(false);
                }

                if (!string.Equals(destination, hostClient.ConnectedAddress, StringComparison.Ordinal) ||
                    !EnvelopeCodec.TryDeserialize(payload, out var env) ||
                    env.Type != MsgType.ScreenShareFrame)
                {
                    return Task.FromResult(true);
                }

                if (Interlocked.Exchange(ref droppedRecoveryKeyframe, 1) == 0)
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };

            await helper.SendScreenShareVideoStreamConfigAsync(CreateVideoStreamConfig(authorizedSessionId, streamEpoch: 1), cts.Token);
            await streamConfigReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            helper.ArmRecoveryBurstControlFallback(authorizedSessionId, 1, 123, 10);
            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 10, width: 640, height: 360, new byte[] { 0x01 }, streamEpoch: 1, capturedTsUtcMs: 3000, isKeyFrame: true),
                recoverySendRole: "owner",
                recoveryBurstToken: 123,
                ct: cts.Token);
            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 11, width: 640, height: 360, new byte[] { 0x02 }, streamEpoch: 1, capturedTsUtcMs: 3010, isKeyFrame: false),
                recoverySendRole: "protected_follower",
                recoveryBurstToken: 123,
                ct: cts.Token);
            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 12, width: 640, height: 360, new byte[] { 0x03 }, streamEpoch: 1, capturedTsUtcMs: 3020, isKeyFrame: false),
                recoverySendRole: "protected_follower",
                recoveryBurstToken: 123,
                ct: cts.Token);

            await threeFramesReceived.Task.WaitAsync(TimeSpan.FromSeconds(4), cts.Token);

            lock (gate)
            {
                Assert.Equal(new long[] { 10, 11, 12 }, deliveredFrameIds.Distinct().OrderBy(frameId => frameId).ToArray());
                Assert.All(deliveredChannels, channel => Assert.Equal(NknBridgeChannel.Control, channel));
            }

            Assert.Equal(1, Volatile.Read(ref droppedRecoveryKeyframe));
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_ScreenShareGenericControlFallback_DoesNotArmBeyondStartupWindow()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.screenshare-control-startup-window.address");
            var helperClient = new FakeNknClient("helper.screenshare-control-startup-window.address");
            var hostIdentity = new NknIdentity("host-screenshare-control-startup-window-id", "host.screenshare-control-startup-window.address");
            var helperIdentity = new NknIdentity("helper-screenshare-control-startup-window-id", "helper.screenshare-control-startup-window.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamConfigReceived = new TaskCompletionSource<ScreenShareVideoStreamConfigV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            var deliveredFrameIds = new ConcurrentQueue<long>();

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ScreenShareVideoStreamConfigReceived += (_, e) => streamConfigReceived.TrySetResult(e.Message);
            host.ScreenShareFrameCompleted += (_, e) => deliveredFrameIds.Enqueue(e.FrameId);

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

            helperClient.ShouldDeliverSendAsync = (destination, _, _) =>
                Task.FromResult(!string.Equals(destination, hostClient.ConnectedMediaAddress, StringComparison.Ordinal));

            await helper.SendScreenShareVideoStreamConfigAsync(CreateVideoStreamConfig(authorizedSessionId, streamEpoch: 1), cts.Token);
            await streamConfigReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 8, width: 640, height: 360, new byte[] { 0x01 }, streamEpoch: 1, capturedTsUtcMs: 4000, isKeyFrame: true),
                cts.Token);
            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 9, width: 640, height: 360, new byte[] { 0x02 }, streamEpoch: 1, capturedTsUtcMs: 4010, isKeyFrame: false),
                cts.Token);
            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 10, width: 640, height: 360, new byte[] { 0x03 }, streamEpoch: 1, capturedTsUtcMs: 4020, isKeyFrame: false),
                cts.Token);

            await Task.Delay(400, cts.Token);

            Assert.Empty(deliveredFrameIds);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_ActiveRecoveryBurst_SuppressesOrdinaryControlFallbackForSameEpochFrames()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.screenshare-control-burst-owner.address");
            var helperClient = new FakeNknClient("helper.screenshare-control-burst-owner.address");
            var hostIdentity = new NknIdentity("host-screenshare-control-burst-owner-id", "host.screenshare-control-burst-owner.address");
            var helperIdentity = new NknIdentity("helper-screenshare-control-burst-owner-id", "helper.screenshare-control-burst-owner.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamConfigReceived = new TaskCompletionSource<ScreenShareVideoStreamConfigV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            var threeFramesReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new object();
            var deliveredFrameIds = new List<long>();
            var deliveredChannels = new List<NknBridgeChannel>();
            byte[]? decryptKey = null;

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ScreenShareVideoStreamConfigReceived += (_, e) => streamConfigReceived.TrySetResult(e.Message);

            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.ScreenShareFrame &&
                    decryptKey is not null)
                {
                    var securePayload = SessionSecureEnvelopeCodec.Decrypt(
                        decryptKey,
                        env.Payload,
                        new SessionSecureEnvelopeExpectation(
                            Family: SessionSecureMessageFamily.ScreenShare,
                            MessageType: "screenshare_frame"));
                    if (!ScreenShareVideoPayloadCodec.TryDeserializeFragmentEnvelope(securePayload.Plaintext, out var fragments, out var isBatch) ||
                        isBatch ||
                        fragments.Length == 0)
                    {
                        return;
                    }

                    lock (gate)
                    {
                        foreach (var fragment in fragments)
                        {
                            deliveredFrameIds.Add(fragment.FrameId);
                            deliveredChannels.Add(e.Channel);
                        }

                        if (deliveredFrameIds.Distinct().Count() >= 3)
                        {
                            threeFramesReceived.TrySetResult();
                        }
                    }
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
            decryptKey = Assert.IsType<byte[]>(GetPrivateField(host, "controlSessionSharedKey"));

            helperClient.ShouldDeliverSendAsync = (destination, payload, _) =>
            {
                if (string.Equals(destination, hostClient.ConnectedMediaAddress, StringComparison.Ordinal))
                {
                    return Task.FromResult(false);
                }

                if (!string.Equals(destination, hostClient.ConnectedAddress, StringComparison.Ordinal) ||
                    !EnvelopeCodec.TryDeserialize(payload, out var env) ||
                    env.Type != MsgType.ScreenShareFrame)
                {
                    return Task.FromResult(true);
                }

                return Task.FromResult(true);
            };

            await helper.SendScreenShareVideoStreamConfigAsync(CreateVideoStreamConfig(authorizedSessionId, streamEpoch: 1), cts.Token);
            await streamConfigReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            helper.ArmRecoveryBurstControlFallback(authorizedSessionId, 1, 123, 10);
            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 8, width: 640, height: 360, new byte[] { 0x01 }, streamEpoch: 1, capturedTsUtcMs: 5000, isKeyFrame: true),
                cts.Token);
            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 9, width: 640, height: 360, new byte[] { 0x02 }, streamEpoch: 1, capturedTsUtcMs: 5010, isKeyFrame: false),
                cts.Token);
            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 10, width: 640, height: 360, new byte[] { 0x03 }, streamEpoch: 1, capturedTsUtcMs: 5020, isKeyFrame: true),
                recoverySendRole: "owner",
                recoveryBurstToken: 123,
                ct: cts.Token);
            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 11, width: 640, height: 360, new byte[] { 0x04 }, streamEpoch: 1, capturedTsUtcMs: 5030, isKeyFrame: false),
                recoverySendRole: "protected_follower",
                recoveryBurstToken: 123,
                ct: cts.Token);
            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 12, width: 640, height: 360, new byte[] { 0x05 }, streamEpoch: 1, capturedTsUtcMs: 5040, isKeyFrame: false),
                recoverySendRole: "protected_follower",
                recoveryBurstToken: 123,
                ct: cts.Token);

            await threeFramesReceived.Task.WaitAsync(TimeSpan.FromSeconds(4), cts.Token);

            lock (gate)
            {
                Assert.Equal(new long[] { 10, 11, 12 }, deliveredFrameIds.Distinct().OrderBy(frameId => frameId).ToArray());
                Assert.All(deliveredChannels, channel => Assert.Equal(NknBridgeChannel.Control, channel));
            }
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_RecoveryBurstControlRetry_SkipsAfterBurstResolution()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.screenshare-control-recovery-resolve.address");
            var helperClient = new FakeNknClient("helper.screenshare-control-recovery-resolve.address");
            var hostIdentity = new NknIdentity("host-screenshare-control-recovery-resolve-id", "host.screenshare-control-recovery-resolve.address");
            var helperIdentity = new NknIdentity("helper-screenshare-control-recovery-resolve-id", "helper.screenshare-control-recovery-resolve.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamConfigReceived = new TaskCompletionSource<ScreenShareVideoStreamConfigV1>(TaskCreationOptions.RunContinuationsAsynchronously);

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ScreenShareVideoStreamConfigReceived += (_, e) => streamConfigReceived.TrySetResult(e.Message);

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

            helperClient.ShouldDeliverSendAsync = (destination, payload, _) =>
            {
                if (!EnvelopeCodec.TryDeserialize(payload, out var env) ||
                    env.Type != MsgType.ScreenShareFrame)
                {
                    return Task.FromResult(true);
                }

                if (string.Equals(destination, hostClient.ConnectedAddress, StringComparison.Ordinal) ||
                    string.Equals(destination, hostClient.ConnectedMediaAddress, StringComparison.Ordinal))
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };

            await helper.SendScreenShareVideoStreamConfigAsync(CreateVideoStreamConfig(authorizedSessionId, streamEpoch: 1), cts.Token);
            await streamConfigReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            helper.ArmRecoveryBurstControlFallback(authorizedSessionId, 1, 123, 10);
            await helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 10, width: 640, height: 360, new byte[] { 0x01 }, streamEpoch: 1, capturedTsUtcMs: 3000, isKeyFrame: true),
                recoverySendRole: "owner",
                recoveryBurstToken: 123,
                ct: cts.Token);
            helper.ResolveRecoveryBurstControlFallback(123);

            await Task.Delay(300, cts.Token);

            Assert.Equal(1L, Assert.IsType<long>(GetPrivateField(helper, "screenShareRecoveryControlBootstrapRetrySkippedDueToBurstResolvedCount")));
            Assert.Equal(0L, Assert.IsType<long>(GetPrivateField(helper, "screenShareRecoveryControlBootstrapRetryQueuedAfterBurstResolutionCount")));
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_ScreenShareFollowerFragments_FallBackToControlForAllFollowerPayloads()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.screenshare-fallback-fragments.address");
            var helperClient = new FakeNknClient("helper.screenshare-fallback-fragments.address");
            var hostIdentity = new NknIdentity("host-screenshare-fallback-fragments-id", "host.screenshare-fallback-fragments.address");
            var helperIdentity = new NknIdentity("helper-screenshare-fallback-fragments-id", "helper.screenshare-fallback-fragments.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamConfigReceived = new TaskCompletionSource<ScreenShareVideoStreamConfigV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            var threeFramesReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var gate = new object();
            var deliveredFrameIds = new List<long>();

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ScreenShareVideoStreamConfigReceived += (_, e) => streamConfigReceived.TrySetResult(e.Message);
            host.ScreenShareFrameCompleted += (_, e) =>
            {
                lock (gate)
                {
                    deliveredFrameIds.Add(e.FrameId);
                    if (deliveredFrameIds.Count >= 3)
                    {
                        threeFramesReceived.TrySetResult();
                    }
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

            helperClient.ShouldDeliverSendAsync = (destination, _, _) =>
                Task.FromResult(!string.Equals(destination, hostClient.ConnectedMediaAddress, StringComparison.Ordinal));

            await helper.SendScreenShareVideoStreamConfigAsync(CreateVideoStreamConfig(authorizedSessionId, streamEpoch: 1), cts.Token);
            await streamConfigReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            var payloads = new[]
            {
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 0, width: 640, height: 360, new byte[] { 0x01 }, streamEpoch: 1, capturedTsUtcMs: 2000, isKeyFrame: true),
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 1, width: 640, height: 360, new byte[] { 0x02 }, streamEpoch: 1, capturedTsUtcMs: 2010, isKeyFrame: false, fragmentIndex: 0, fragmentCount: 2),
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 1, width: 640, height: 360, new byte[] { 0x03 }, streamEpoch: 1, capturedTsUtcMs: 2010, isKeyFrame: false, fragmentIndex: 1, fragmentCount: 2),
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 2, width: 640, height: 360, new byte[] { 0x04 }, streamEpoch: 1, capturedTsUtcMs: 2020, isKeyFrame: false, fragmentIndex: 0, fragmentCount: 2),
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 2, width: 640, height: 360, new byte[] { 0x05 }, streamEpoch: 1, capturedTsUtcMs: 2020, isKeyFrame: false, fragmentIndex: 1, fragmentCount: 2),
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 3, width: 640, height: 360, new byte[] { 0x06 }, streamEpoch: 1, capturedTsUtcMs: 2030, isKeyFrame: false),
            };

            foreach (var payload in payloads)
            {
                await helper.SendScreenSharePayloadAsync(payload, cts.Token);
            }

            await threeFramesReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await Task.Delay(300, cts.Token);

            lock (gate)
            {
                Assert.Equal(new long[] { 0, 1, 2 }, deliveredFrameIds);
                Assert.DoesNotContain(3L, deliveredFrameIds);
            }
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_ChatSend_AndScreenShareFrame_UseSeparateChannels()
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
            var streamConfigReceived = new TaskCompletionSource<ScreenShareVideoStreamConfigV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sendInvocationCount = 0;
            var rawScreenPayloadReceived = 0;
            var chatPayload = System.Text.Encoding.UTF8.GetBytes("chat-priority-payload");
            byte[] screenPayload = Array.Empty<byte>();
            var screenFrameReceived = new TaskCompletionSource<ScreenShareFrameCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var screenEnvelopeReceived = new TaskCompletionSource<NknIncomingMessage>(TaskCreationOptions.RunContinuationsAsynchronously);

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ChatMessageReceived += (_, e) => hostChatReceived.TrySetResult(e.Payload);
            host.ScreenShareVideoStreamConfigReceived += (_, e) => streamConfigReceived.TrySetResult(e.Message);
            host.ScreenShareFrameCompleted += (_, e) => screenFrameReceived.TrySetResult(e);
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.ScreenShareFrame)
                {
                    Interlocked.Increment(ref rawScreenPayloadReceived);
                    screenEnvelopeReceived.TrySetResult(e);
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
            var streamConfig = CreateVideoStreamConfig(authorizedSessionId, streamEpoch: 1);
            screenPayload = CreateVideoFragmentPayload(authorizedSessionId, frameId: 1, width: 640, height: 360, new byte[] { 1, 2, 3, 4 }, streamEpoch: 1, capturedTsUtcMs: 1234);

            await helper.SendScreenShareVideoStreamConfigAsync(streamConfig, cts.Token);
            await streamConfigReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

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
            var receivedEnvelope = await screenEnvelopeReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var receivedFrame = await screenFrameReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            Assert.Equal(1, Volatile.Read(ref rawScreenPayloadReceived));
            Assert.Equal(NknBridgeChannel.Media, receivedEnvelope.Channel);
            Assert.Equal(helperClient.ConnectedMediaAddress, receivedEnvelope.Source);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, receivedFrame.EncodedFrameBytes);
            Assert.False(chatTask.IsCompleted);

            releaseFirstSend.TrySetResult();

            await chatTask;
            await screenSendTask.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var receivedChat = await hostChatReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var diagnosticsAfterSend = NknRuntimeDiagnostics.Snapshot();

            Assert.Equal(chatPayload, receivedChat);
            Assert.Equal(1, Volatile.Read(ref rawScreenPayloadReceived));
            Assert.Equal(0, helper.ScreenShareOutboundBusyDrops);
            Assert.Equal(1, helper.ScreenShareMessagesSent);
            Assert.Equal(receivedEnvelope.Payload.Length, helper.ScreenSharePayloadBytesSent);
            Assert.Equal(
                diagnosticsBeforeSend.ScreenShareOutboundBusyDrops,
                diagnosticsAfterSend.ScreenShareOutboundBusyDrops);
            Assert.Equal(
                diagnosticsBeforeSend.ScreenShareMessagesSent + 1,
                diagnosticsAfterSend.ScreenShareMessagesSent);
            Assert.Equal(
                diagnosticsBeforeSend.ScreenSharePayloadBytesSent + receivedEnvelope.Payload.Length,
                diagnosticsAfterSend.ScreenSharePayloadBytesSent);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_ScreenSharePayloadAsync_DoesNotRetainLocalMediaQueue_WhenSendIsBlocked()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.screenshare-freshness.address");
            var helperClient = new FakeNknClient("helper.screenshare-freshness.address");
            var hostIdentity = new NknIdentity("host-screenshare-freshness-id", "host.screenshare-freshness.address");
            var helperIdentity = new NknIdentity("helper-screenshare-freshness-id", "helper.screenshare-freshness.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var streamConfigReceived = new TaskCompletionSource<ScreenShareVideoStreamConfigV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var frameIds = new List<long>();
            var sendInvocationCount = 0;

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ScreenShareVideoStreamConfigReceived += (_, e) => streamConfigReceived.TrySetResult(e.Message);
            host.ScreenShareFrameCompleted += (_, e) =>
            {
                lock (frameIds)
                {
                    frameIds.Add(e.FrameId);
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
            var streamConfig = CreateVideoStreamConfig(authorizedSessionId, streamEpoch: 1);
            await helper.SendScreenShareVideoStreamConfigAsync(streamConfig, cts.Token);
            await streamConfigReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            helperClient.BeforeSendAsync = async (_, payload, sendCt) =>
            {
                if (EnvelopeCodec.TryDeserialize(payload, out var env) &&
                    env.Type == MsgType.ScreenShareFrame &&
                    Interlocked.Increment(ref sendInvocationCount) == 1)
                {
                    firstSendStarted.TrySetResult();
                    await releaseFirstSend.Task.WaitAsync(sendCt);
                }
            };

            var firstSendTask = helper.SendScreenSharePayloadAsync(
                CreateVideoFragmentPayload(authorizedSessionId, frameId: 0, width: 640, height: 360, new byte[] { 1 }, streamEpoch: 1, isKeyFrame: true),
                cts.Token);
            await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            var secondSendTask = Task.Run(
                async () =>
                {
                    secondSendStarted.TrySetResult();
                    await helper.SendScreenSharePayloadAsync(
                        CreateVideoFragmentPayload(authorizedSessionId, frameId: 1, width: 640, height: 360, new byte[] { 2 }, streamEpoch: 1),
                        cts.Token);
                },
                cts.Token);

            await secondSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            await Task.Delay(100, cts.Token);

            Assert.Equal(0, helper.ScreenShareTransportQueueDepth);
            Assert.Equal(0, helper.ScreenShareTransportQueuedBytes);

            releaseFirstSend.TrySetResult();
            await firstSendTask.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await secondSendTask.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (DateTime.UtcNow < deadline)
            {
                lock (frameIds)
                {
                    if (frameIds.Count >= 2)
                    {
                        break;
                    }
                }

                await Task.Delay(25, cts.Token);
            }

            List<long> deliveredFrameIds;
            lock (frameIds)
            {
                deliveredFrameIds = frameIds.ToList();
            }

            Assert.Contains(0L, deliveredFrameIds);
            Assert.Contains(1L, deliveredFrameIds);
            Assert.Equal(2, deliveredFrameIds.Count);
            Assert.Equal(0, helper.ScreenShareTransportRecentDropCount);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_ScreenSharePayload_BypassesBusyControlGate()
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
            var streamConfigReceived = new TaskCompletionSource<ScreenShareVideoStreamConfigV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var screenPayloadDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var sendInvocationCount = 0;
            var rawScreenPayloadReceived = 0;
            var chatPayload = System.Text.Encoding.UTF8.GetBytes("chat-budget-drop-payload");
            byte[] screenPayload = Array.Empty<byte>();

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ChatMessageReceived += (_, e) => hostChatReceived.TrySetResult(e.Payload);
            host.ScreenShareVideoStreamConfigReceived += (_, e) => streamConfigReceived.TrySetResult(e.Message);
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic &&
                    EnvelopeCodec.TryDeserialize(e.Payload, out var env) &&
                    env.Type == MsgType.ScreenShareFrame)
                {
                    Interlocked.Increment(ref rawScreenPayloadReceived);
                    screenPayloadDelivered.TrySetResult();
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
            var streamConfig = CreateVideoStreamConfig(authorizedSessionId, streamEpoch: 1);
            screenPayload = CreateVideoFragmentPayload(authorizedSessionId, frameId: 1, width: 640, height: 360, new byte[] { 1, 2, 3, 4 }, streamEpoch: 1, capturedTsUtcMs: 1234);

            await helper.SendScreenShareVideoStreamConfigAsync(streamConfig, cts.Token);
            await streamConfigReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            helperClient.BeforeSendAsync = async (_, payloadBytes, sendCt) =>
            {
                if (EnvelopeCodec.TryDeserialize(payloadBytes, out var env) &&
                    env.Type != MsgType.ScreenShareFrame &&
                    Interlocked.Increment(ref sendInvocationCount) == 1)
                {
                    firstSendStarted.TrySetResult();
                    await releaseFirstSend.Task.WaitAsync(sendCt);
                }
            };

            var chatTask = helper.SendChatMessageAsync(chatPayload, cts.Token);
            await firstSendStarted.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            var diagnosticsBeforeDrop = NknRuntimeDiagnostics.Snapshot();

            var screenSendTask = helper.SendScreenSharePayloadAsync(screenPayload, cts.Token);
            await screenPayloadDelivered.Task.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);

            var diagnosticsAfterSend = NknRuntimeDiagnostics.Snapshot();
            Assert.Equal(1, Volatile.Read(ref rawScreenPayloadReceived));
            Assert.Equal(0, helper.ScreenShareOutboundBusyDrops);
            Assert.Equal(1, helper.ScreenShareMessagesSent);
            Assert.True(helper.ScreenSharePayloadBytesSent > 0);
            Assert.Equal(
                diagnosticsBeforeDrop.ScreenShareOutboundBusyDrops,
                diagnosticsAfterSend.ScreenShareOutboundBusyDrops);
            Assert.Equal(
                diagnosticsBeforeDrop.ScreenShareMessagesSent + 1,
                diagnosticsAfterSend.ScreenShareMessagesSent);
            Assert.True(
                diagnosticsAfterSend.ScreenSharePayloadBytesSent >
                diagnosticsBeforeDrop.ScreenSharePayloadBytesSent);

            releaseFirstSend.TrySetResult();
            await screenSendTask.WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
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
    public async Task NknSignalingTransport_ScreenShareStop_UsesControlChannel_WhileFrameSendIsBlockedOnMediaChannel()
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
            var streamConfigReceived = new TaskCompletionSource<ScreenShareVideoStreamConfigV1>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstSendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstSend = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            byte[] stopPayload = Array.Empty<byte>();

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ScreenShareVideoStreamConfigReceived += (_, e) => streamConfigReceived.TrySetResult(e.Message);

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

            var framePayload = CreateVideoFragmentPayload(authorizedSessionId, frameId: 1, width: 640, height: 360, new byte[] { 1, 2, 3, 4 }, streamEpoch: 1, capturedTsUtcMs: 1234);
            var streamConfig = CreateVideoStreamConfig(authorizedSessionId, streamEpoch: 1);

            stopPayload = ScreenSharePayloadCodec.SerializeStop(new ScreenShareStopMessageV1
            {
                SessionId = authorizedSessionId,
                Reason = "preview_stopped",
            });

            var stopPayloadReceived = new TaskCompletionSource<NknIncomingMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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

            await helper.SendScreenShareVideoStreamConfigAsync(streamConfig, cts.Token);
            await streamConfigReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

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
            var receivedStop = await stopPayloadReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await stopReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await stopSendTask.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            releaseFirstSend.TrySetResult();

            await frameSendTask.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);

            Assert.Equal(helperIdentity.Address, receivedStop.Source);
            Assert.Equal(NknBridgeChannel.Control, receivedStop.Channel);
            Assert.True(EnvelopeCodec.TryDeserialize(receivedStop.Payload, out var env));
            Assert.Equal(MsgType.ScreenShareStop, env.Type);
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
            Assert.Equal(NknBridgeChannel.Control, receivedStop.Channel);
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

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_RawScreenShareFramePayload_OnMediaChannel_DoesNotDispatchFrame()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.raw-screenshare-frame.address");
            var helperClient = new FakeNknClient("helper.raw-screenshare-frame.address");
            var hostIdentity = new NknIdentity("host-raw-screenshare-frame-id", "host.raw-screenshare-frame.address");
            var helperIdentity = new NknIdentity("helper-raw-screenshare-frame-id", "helper.raw-screenshare-frame.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var frameCount = 0;

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ScreenShareFrameCompleted += (_, _) => Interlocked.Increment(ref frameCount);

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

            var rawFramePayload = CreateVideoFragmentPayload(authorizedSessionId, frameId: 1, width: 640, height: 360, new byte[] { 1, 2, 3, 4 }, streamEpoch: 1, capturedTsUtcMs: 1234);

            NknRuntimeDiagnostics.SetLastEnvelopeDropReason(null);
            await helperClient.SendMediaAsync(hostClient.ConnectedMediaAddress, rawFramePayload, cts.Token);
            await Task.Delay(300, cts.Token);

            Assert.Equal(0, Volatile.Read(ref frameCount));
            Assert.Equal("parse_failed", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task NknSignalingTransport_RawScreenShareStopPayload_OnMediaChannel_DoesNotDispatchStop()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));

            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("host.raw-screenshare-stop.address");
            var helperClient = new FakeNknClient("helper.raw-screenshare-stop.address");
            var hostIdentity = new NknIdentity("host-raw-screenshare-stop-id", "host.raw-screenshare-stop.address");
            var helperIdentity = new NknIdentity("helper-raw-screenshare-stop-id", "helper.raw-screenshare-stop.address");

            using var host = new NknSignalingTransport(hostClient, options, hostIdentity);
            using var helper = new NknSignalingTransport(helperClient, options, helperIdentity);
            var joinRequestRaised = new TaskCompletionSource<IncomingJoinRequestEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
            var hostApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var helperApproved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopCount = 0;

            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ScreenShareStopped += (_, _) => Interlocked.Increment(ref stopCount);

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

            var rawStopPayload = ScreenSharePayloadCodec.SerializeStop(new ScreenShareStopMessageV1
            {
                SessionId = authorizedSessionId,
                Reason = "preview_stopped",
            });

            NknRuntimeDiagnostics.SetLastEnvelopeDropReason(null);
            await helperClient.SendMediaAsync(hostClient.ConnectedMediaAddress, rawStopPayload, cts.Token);
            await Task.Delay(300, cts.Token);

            Assert.Equal(0, Volatile.Read(ref stopCount));
            Assert.Equal("parse_failed", NknRuntimeDiagnostics.Snapshot().LastEnvelopeDropReason);
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

    private static ScreenShareVideoStreamConfigV1 CreateVideoStreamConfig(string sessionId, long streamEpoch)
    {
        return new ScreenShareVideoStreamConfigV1
        {
            SessionId = sessionId,
            StreamEpoch = streamEpoch,
            Encoding = "h264",
            CodecProfile = "baseline",
            DecoderConfigData = new byte[] { 1, 2, 3 },
        };
    }

    private static byte[] CreateVideoFragmentPayload(
        string sessionId,
        long frameId,
        int width,
        int height,
        byte[] frameBytes,
        long streamEpoch,
        long capturedTsUtcMs = 0,
        bool isKeyFrame = true,
        int fragmentIndex = 0,
        int fragmentCount = 1)
    {
        return ScreenShareVideoPayloadCodec.SerializeFragment(
            new ScreenShareVideoFragmentV1
            {
                SessionId = sessionId,
                StreamEpoch = streamEpoch,
                FrameId = frameId,
                Width = width,
                Height = height,
                CapturedTsUtcMs = capturedTsUtcMs <= 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : capturedTsUtcMs,
                Encoding = "h264",
                IsKeyFrame = isKeyFrame,
                FragmentIndex = fragmentIndex,
                FragmentCount = fragmentCount,
                Data = frameBytes,
            });
    }

    private static object? GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return field!.GetValue(target);
    }
}
