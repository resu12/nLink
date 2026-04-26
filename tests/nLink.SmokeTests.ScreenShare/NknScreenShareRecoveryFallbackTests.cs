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

[Collection(AvaloniaHeadlessUiCollection.Name)]
[Trait("Area", "ScreenShare")]
public sealed class NknScreenShareRecoveryFallbackTests : ScreenCaptureAbstractionTestBase
{
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
            var gate = new object ();
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
                if (!e.IsTopic && EnvelopeCodec.TryDeserialize(e.Payload, out var env) && env.Type == MsgType.ScreenShareFrame)
                {
                    lock (gate)
                    {
                        deliveredChannels.Add(e.Channel);
                        deliveredSources.Add(e.Source);
                    }
                }
            };
            await host.HostByAddressAsync(cts.Token);
            var(rawToken, invite) = InviteTestFactory.CreateValidatedInvite(new PeerAddress(host.LocalPeerAddress), InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);
            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var authorizedSessionId = Assert.IsType<SessionId>(helper.CurrentSessionSecurityState.SessionId).Value;
            const long recoveryBurstToken = 41;
            helper.ArmRecoveryBurstControlFallback(authorizedSessionId, streamEpoch: 1, burstToken: recoveryBurstToken, ownerFrameId: 0);
            helperClient.ShouldDeliverSendAsync = (destination, _, _) => Task.FromResult(!string.Equals(destination, hostClient.ConnectedMediaAddress, StringComparison.Ordinal));
            await helper.SendScreenShareVideoStreamConfigAsync(CreateVideoStreamConfig(authorizedSessionId, streamEpoch: 1), cts.Token);
            await streamConfigReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 0, width: 640, height: 360, new byte[] { 0x01 }, streamEpoch: 1, capturedTsUtcMs: 1000, isKeyFrame: true), recoverySendRole: "owner", recoveryBurstToken: recoveryBurstToken, cts.Token).WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 1, width: 640, height: 360, new byte[] { 0x02 }, streamEpoch: 1, capturedTsUtcMs: 1010, isKeyFrame: false), recoverySendRole: "protected_follower", recoveryBurstToken: recoveryBurstToken, cts.Token).WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 2, width: 640, height: 360, new byte[] { 0x03 }, streamEpoch: 1, capturedTsUtcMs: 1020, isKeyFrame: false), recoverySendRole: "protected_follower", recoveryBurstToken: recoveryBurstToken, cts.Token).WaitAsync(TimeSpan.FromSeconds(2), cts.Token);
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 3, width: 640, height: 360, new byte[] { 0x04 }, streamEpoch: 1, capturedTsUtcMs: 1030, isKeyFrame: false), recoverySendRole: null, recoveryBurstToken: 0, cts.Token);
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
            var gate = new object ();
            var deliveredFrameIds = new List<long>();
            var deliveredChannels = new List<NknBridgeChannel>();
            var droppedBootstrapKeyframe = 0;
            byte[]? decryptKey = null;
            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ScreenShareVideoStreamConfigReceived += (_, e) => streamConfigReceived.TrySetResult(e.Message);
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic && EnvelopeCodec.TryDeserialize(e.Payload, out var env) && env.Type == MsgType.ScreenShareFrame && decryptKey is not null)
                {
                    var securePayload = SessionSecureEnvelopeCodec.Decrypt(decryptKey, env.Payload, new SessionSecureEnvelopeExpectation(Family: SessionSecureMessageFamily.ScreenShare, MessageType: "screenshare_frame"));
                    if (!ScreenShareVideoPayloadCodec.TryDeserializeFragmentEnvelope(securePayload.Plaintext, out var fragments, out var isBatch) || isBatch || fragments.Length == 0)
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
            var(rawToken, invite) = InviteTestFactory.CreateValidatedInvite(new PeerAddress(host.LocalPeerAddress), InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);
            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var authorizedSessionId = Assert.IsType<SessionId>(helper.CurrentSessionSecurityState.SessionId).Value;
            decryptKey = Assert.IsType<byte[]>(GetPrivateField(host, "controlSessionSharedKey"));
            const long recoveryBurstToken = 99;
            helper.ArmRecoveryBurstControlFallback(authorizedSessionId, streamEpoch: 1, burstToken: recoveryBurstToken, ownerFrameId: 0);
            helperClient.ShouldDeliverSendAsync = (destination, payload, _) =>
            {
                if (string.Equals(destination, hostClient.ConnectedMediaAddress, StringComparison.Ordinal))
                {
                    return Task.FromResult(false);
                }

                if (!string.Equals(destination, hostClient.ConnectedAddress, StringComparison.Ordinal) || !EnvelopeCodec.TryDeserialize(payload, out var env) || env.Type != MsgType.ScreenShareFrame)
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
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 0, width: 640, height: 360, new byte[] { 0x01 }, streamEpoch: 1, capturedTsUtcMs: 1000, isKeyFrame: true), recoverySendRole: "owner", recoveryBurstToken: recoveryBurstToken, cts.Token);
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 1, width: 640, height: 360, new byte[] { 0x02 }, streamEpoch: 1, capturedTsUtcMs: 1010, isKeyFrame: false), recoverySendRole: "protected_follower", recoveryBurstToken: recoveryBurstToken, cts.Token);
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 2, width: 640, height: 360, new byte[] { 0x03 }, streamEpoch: 1, capturedTsUtcMs: 1020, isKeyFrame: false), recoverySendRole: "protected_follower", recoveryBurstToken: recoveryBurstToken, cts.Token);
            await threeFramesReceived.Task.WaitAsync(TimeSpan.FromSeconds(4), cts.Token);
            lock (gate)
            {
                Assert.Equal(new long[] { 0, 1, 2 }, deliveredFrameIds.Distinct().OrderBy(frameId => frameId).ToArray());
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
            var gate = new object ();
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
                if (!e.IsTopic && EnvelopeCodec.TryDeserialize(e.Payload, out var env) && env.Type == MsgType.ScreenShareFrame && decryptKey is not null)
                {
                    var securePayload = SessionSecureEnvelopeCodec.Decrypt(decryptKey, env.Payload, new SessionSecureEnvelopeExpectation(Family: SessionSecureMessageFamily.ScreenShare, MessageType: "screenshare_frame"));
                    if (!ScreenShareVideoPayloadCodec.TryDeserializeFragmentEnvelope(securePayload.Plaintext, out var fragments, out var isBatch) || isBatch || fragments.Length == 0)
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
            var(rawToken, invite) = InviteTestFactory.CreateValidatedInvite(new PeerAddress(host.LocalPeerAddress), InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
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

                if (!string.Equals(destination, hostClient.ConnectedAddress, StringComparison.Ordinal) || !EnvelopeCodec.TryDeserialize(payload, out var env) || env.Type != MsgType.ScreenShareFrame)
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
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 10, width: 640, height: 360, new byte[] { 0x01 }, streamEpoch: 1, capturedTsUtcMs: 3000, isKeyFrame: true), recoverySendRole: "owner", recoveryBurstToken: 123, ct: cts.Token);
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 11, width: 640, height: 360, new byte[] { 0x02 }, streamEpoch: 1, capturedTsUtcMs: 3010, isKeyFrame: false), recoverySendRole: "protected_follower", recoveryBurstToken: 123, ct: cts.Token);
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 12, width: 640, height: 360, new byte[] { 0x03 }, streamEpoch: 1, capturedTsUtcMs: 3020, isKeyFrame: false), recoverySendRole: "protected_follower", recoveryBurstToken: 123, ct: cts.Token);
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
            var(rawToken, invite) = InviteTestFactory.CreateValidatedInvite(new PeerAddress(host.LocalPeerAddress), InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);
            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var authorizedSessionId = Assert.IsType<SessionId>(helper.CurrentSessionSecurityState.SessionId).Value;
            helperClient.ShouldDeliverSendAsync = (destination, _, _) => Task.FromResult(!string.Equals(destination, hostClient.ConnectedMediaAddress, StringComparison.Ordinal));
            await helper.SendScreenShareVideoStreamConfigAsync(CreateVideoStreamConfig(authorizedSessionId, streamEpoch: 1), cts.Token);
            await streamConfigReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 8, width: 640, height: 360, new byte[] { 0x01 }, streamEpoch: 1, capturedTsUtcMs: 4000, isKeyFrame: true), cts.Token);
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 9, width: 640, height: 360, new byte[] { 0x02 }, streamEpoch: 1, capturedTsUtcMs: 4010, isKeyFrame: false), cts.Token);
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 10, width: 640, height: 360, new byte[] { 0x03 }, streamEpoch: 1, capturedTsUtcMs: 4020, isKeyFrame: false), cts.Token);
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
            var gate = new object ();
            var deliveredFrameIds = new List<long>();
            var deliveredChannels = new List<NknBridgeChannel>();
            byte[]? decryptKey = null;
            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ScreenShareVideoStreamConfigReceived += (_, e) => streamConfigReceived.TrySetResult(e.Message);
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic && EnvelopeCodec.TryDeserialize(e.Payload, out var env) && env.Type == MsgType.ScreenShareFrame && decryptKey is not null)
                {
                    var securePayload = SessionSecureEnvelopeCodec.Decrypt(decryptKey, env.Payload, new SessionSecureEnvelopeExpectation(Family: SessionSecureMessageFamily.ScreenShare, MessageType: "screenshare_frame"));
                    if (!ScreenShareVideoPayloadCodec.TryDeserializeFragmentEnvelope(securePayload.Plaintext, out var fragments, out var isBatch) || isBatch || fragments.Length == 0)
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
            var(rawToken, invite) = InviteTestFactory.CreateValidatedInvite(new PeerAddress(host.LocalPeerAddress), InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
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

                if (!string.Equals(destination, hostClient.ConnectedAddress, StringComparison.Ordinal) || !EnvelopeCodec.TryDeserialize(payload, out var env) || env.Type != MsgType.ScreenShareFrame)
                {
                    return Task.FromResult(true);
                }

                return Task.FromResult(true);
            };
            await helper.SendScreenShareVideoStreamConfigAsync(CreateVideoStreamConfig(authorizedSessionId, streamEpoch: 1), cts.Token);
            await streamConfigReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            helper.ArmRecoveryBurstControlFallback(authorizedSessionId, 1, 123, 10);
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 8, width: 640, height: 360, new byte[] { 0x01 }, streamEpoch: 1, capturedTsUtcMs: 5000, isKeyFrame: true), cts.Token);
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 9, width: 640, height: 360, new byte[] { 0x02 }, streamEpoch: 1, capturedTsUtcMs: 5010, isKeyFrame: false), cts.Token);
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 10, width: 640, height: 360, new byte[] { 0x03 }, streamEpoch: 1, capturedTsUtcMs: 5020, isKeyFrame: true), recoverySendRole: "owner", recoveryBurstToken: 123, ct: cts.Token);
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 11, width: 640, height: 360, new byte[] { 0x04 }, streamEpoch: 1, capturedTsUtcMs: 5030, isKeyFrame: false), recoverySendRole: "protected_follower", recoveryBurstToken: 123, ct: cts.Token);
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 12, width: 640, height: 360, new byte[] { 0x05 }, streamEpoch: 1, capturedTsUtcMs: 5040, isKeyFrame: false), recoverySendRole: "protected_follower", recoveryBurstToken: 123, ct: cts.Token);
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
            var(rawToken, invite) = InviteTestFactory.CreateValidatedInvite(new PeerAddress(host.LocalPeerAddress), InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);
            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var authorizedSessionId = Assert.IsType<SessionId>(helper.CurrentSessionSecurityState.SessionId).Value;
            helperClient.ShouldDeliverSendAsync = (destination, payload, _) =>
            {
                if (!EnvelopeCodec.TryDeserialize(payload, out var env) || env.Type != MsgType.ScreenShareFrame)
                {
                    return Task.FromResult(true);
                }

                if (string.Equals(destination, hostClient.ConnectedAddress, StringComparison.Ordinal) || string.Equals(destination, hostClient.ConnectedMediaAddress, StringComparison.Ordinal))
                {
                    return Task.FromResult(false);
                }

                return Task.FromResult(true);
            };
            await helper.SendScreenShareVideoStreamConfigAsync(CreateVideoStreamConfig(authorizedSessionId, streamEpoch: 1), cts.Token);
            await streamConfigReceived.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            helper.ArmRecoveryBurstControlFallback(authorizedSessionId, 1, 123, 10);
            await helper.SendScreenSharePayloadAsync(CreateVideoFragmentPayload(authorizedSessionId, frameId: 10, width: 640, height: 360, new byte[] { 0x01 }, streamEpoch: 1, capturedTsUtcMs: 3000, isKeyFrame: true), recoverySendRole: "owner", recoveryBurstToken: 123, ct: cts.Token);
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
    public async Task NknSignalingTransport_OrdinaryFollowerFragments_DoNotFallBackToControlWithoutRecoveryBurst()
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
            var gate = new object ();
            var deliveredFrameIds = new List<long>();
            var deliveredChannels = new List<NknBridgeChannel>();
            host.IncomingJoinRequest += (_, e) => joinRequestRaised.TrySetResult(e);
            host.Approved += (_, _) => hostApproved.TrySetResult();
            helper.Approved += (_, _) => helperApproved.TrySetResult();
            host.ScreenShareVideoStreamConfigReceived += (_, e) => streamConfigReceived.TrySetResult(e.Message);
            host.ScreenShareFrameCompleted += (_, e) =>
            {
                lock (gate)
                {
                    deliveredFrameIds.Add(e.FrameId);
                }
            };
            hostClient.MessageReceived += (_, e) =>
            {
                if (!e.IsTopic && EnvelopeCodec.TryDeserialize(e.Payload, out var env) && env.Type == MsgType.ScreenShareFrame)
                {
                    lock (gate)
                    {
                        deliveredChannels.Add(e.Channel);
                    }
                }
            };
            await host.HostByAddressAsync(cts.Token);
            var(rawToken, invite) = InviteTestFactory.CreateValidatedInvite(new PeerAddress(host.LocalPeerAddress), InviteCapabilities.Chat | InviteCapabilities.ScreenShare);
            await helper.JoinByInviteAsync(rawToken, invite, cts.Token);
            var pendingJoin = await joinRequestRaised.Task.WaitAsync(TimeSpan.FromSeconds(6), cts.Token);
            await pendingJoin.ApproveAsync(pendingJoin.CreateApprovalDecision(), cts.Token);
            await hostApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            await helperApproved.Task.WaitAsync(TimeSpan.FromSeconds(3), cts.Token);
            var authorizedSessionId = Assert.IsType<SessionId>(helper.CurrentSessionSecurityState.SessionId).Value;
            helperClient.ShouldDeliverSendAsync = (destination, _, _) => Task.FromResult(!string.Equals(destination, hostClient.ConnectedMediaAddress, StringComparison.Ordinal));
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

            await Task.Delay(500, cts.Token);
            lock (gate)
            {
                Assert.Empty(deliveredFrameIds);
                Assert.Empty(deliveredChannels);
            }
        }
        finally
        {
            FakeNknClient.ResetNetwork();
        }
    }

}
