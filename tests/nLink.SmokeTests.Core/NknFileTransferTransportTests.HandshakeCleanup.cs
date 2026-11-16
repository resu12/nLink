using System.Reflection;
using NLink.Core;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

public sealed partial class NknFileTransferTransportTests
{
    [Fact]
    [Trait("FileTransferInfraSuite", "HandshakeCleanup")]
    public async Task HandshakeCleanup_ReceivedResponseRetiresTimerBeforePublishingVerification()
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("timer-cleanup.host.address");
            var helperClient = new FakeNknClient("timer-cleanup.helper.address");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("timer-host", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("timer-helper", helperClient.Address));
            object? pending = null;
            long generation = 0;
            var timerDispatched = 0;
            var failures = 0;
            helperClient.BeforeSendAsync = (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var envelope) && envelope.Type == MsgType.SessionHandshakeResponse)
                {
                    pending = GetPrivateField(host, "pendingInboundHandshake");
                    generation = (long)GetPrivateField(host, "pendingInboundHandshakeTimeoutGeneration")!;
                }
                return Task.CompletedTask;
            };
            hostClient.BeforeSendAsync = (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var envelope) && envelope.Type == MsgType.SessionHandshakeResult &&
                    SessionHandshakeProtocol.TryDeserializeResult(envelope.Payload, out var result))
                {
                    if (!result.Verified) Interlocked.Increment(ref failures);
                    else
                    {
                        Assert.NotNull(pending);
                        Assert.Same(pending, GetPrivateField(host, "pendingInboundHandshake"));
                        Assert.True((bool)pending.GetType().GetProperty("ResponseReceived")!.GetValue(pending)!);
                        // Expiry races with publishing a response already authenticated
                        // before its deadline; no sleeps or real timer scheduling.
                        pending.GetType().GetProperty("ChallengeExpiresAtUtc")!.SetValue(pending, DateTimeOffset.UtcNow.AddSeconds(-1));
                        InvokePrivateMethod(host, "OnPendingInboundHandshakeTimeout", generation);
                        Interlocked.Increment(ref timerDispatched);
                    }
                }
                return Task.CompletedTask;
            };
            await ApproveNknSessionAsync(host, helper, lifetime.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
            Assert.Equal(1, timerDispatched);
            Assert.Equal(0, failures);
            Assert.True(host.CurrentSessionSecurityState.ApprovalGranted);
            Assert.True(helper.CurrentSessionSecurityState.ApprovalGranted);
            foreach (var name in new[] { "currentEnvelopeCode", "controlSessionSharedKey", "fileTransferSessionSharedKey" })
                Assert.NotNull(GetPrivateField(host, name));
            Assert.Null(GetPrivateField(host, "pendingInboundHandshake"));
        }
        finally { FakeNknClient.ResetNetwork(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("FileTransferInfraSuite", "HandshakeCleanup")]
    public async Task HandshakeCleanup_OnlyCurrentExpiryCanReject(bool retired)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("expiry.host.address");
            var helperClient = new FakeNknClient("expiry.helper.address");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("expiry-host", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("expiry-helper", helperClient.Address));
            await ApproveNknSessionAsync(host, helper, lifetime.Token, suppressAutomaticAccelerationNegotiation: true);
            var security = host.CurrentSessionSecurityState;
            var key = GetPrivateField(host, "controlSessionSharedKey");
            var pending = CreateCleanupPending(host, helper, "expiring-join", security.SessionId!.Value);
            pending.GetType().GetProperty("ChallengeExpiresAtUtc")!.SetValue(pending, DateTimeOffset.UtcNow.AddSeconds(-1));
            SetPrivateField(host, "pendingInboundHandshake", pending);
            var generation = (long)GetPrivateField(host, "pendingInboundHandshakeTimeoutGeneration")!;
            hostClient.ShouldDeliverSendAsync = (_, _, _) => Task.FromResult(false);
            InvokePrivateMethod(host, "OnPendingInboundHandshakeTimeout", retired ? generation - 1 : generation);
            if (retired)
            {
                Assert.Same(security, host.CurrentSessionSecurityState);
                Assert.Same(key, GetPrivateField(host, "controlSessionSharedKey"));
                Assert.Same(pending, GetPrivateField(host, "pendingInboundHandshake"));
            }
            else
            {
                Assert.Equal(SessionHandshakeState.Expired, host.CurrentSessionSecurityState.HandshakeState);
                Assert.Equal("handshake_response_expired", host.CurrentSessionSecurityState.HandshakeFailureReason);
                Assert.False(host.CurrentSessionSecurityState.ApprovalGranted);
                Assert.Null(GetPrivateField(host, "controlSessionSharedKey"));
                Assert.Null(GetPrivateField(host, "pendingInboundHandshake"));
            }
        }
        finally { FakeNknClient.ResetNetwork(); }
    }

    [Theory]
    [InlineData("handshake_start_timeout", false, false, true)]
    [InlineData("invite_revoked", false, false, true)]
    [InlineData("invite_binding_mismatch", false, false, true)]
    [InlineData("invite_helper_required", false, false, true)]
    [InlineData("invite_helper_mismatch", false, false, true)]
    [InlineData("handshake_response_mac_invalid", true, false, true)]
    [InlineData("handshake_start_timeout", true, true, true)]
    [InlineData("handshake_response_mac_invalid", false, false, false)]
    [Trait("FileTransferInfraSuite", "HandshakeCleanup")]
    public async Task HandshakeCleanup_InboundFailureHonorsRetainedSession(
        string reason, bool staleSession, bool newerPending, bool retained)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("cleanup.host.address");
            var helperClient = new FakeNknClient("cleanup.helper.address");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("cleanup-host", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("cleanup-helper", helperClient.Address));
            await ApproveNknSessionAsync(host, helper, lifetime.Token,
                InviteCapabilities.Chat | InviteCapabilities.FileTransfer, suppressAutomaticAccelerationNegotiation: true);
            var security = host.CurrentSessionSecurityState;
            var fields = new[] { "currentEnvelopeCode", "remoteEndpoint", "remoteMediaEndpoint", "remoteBulkEndpoint",
                "controlSessionSharedKey", "fileTransferSessionSharedKey" };
            var before = fields.Select(name => GetPrivateField(host, name)).ToArray();
            Assert.NotNull(before[0]);
            Assert.NotNull(before[4]);
            Assert.NotNull(before[5]);
            var pending = CreateCleanupPending(host, helper, "late-join",
                staleSession ? SessionHandshakeProtocol.CreateSessionId() : security.SessionId!.Value);
            var currentPending = newerPending
                ? CreateCleanupPending(host, helper, "newer-join", security.SessionId!.Value) : pending;
            SetPrivateField(host, "pendingInboundHandshake", currentPending);
            var failureSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            hostClient.BeforeSendAsync = (_, bytes, _) =>
            {
                if (EnvelopeCodec.TryDeserialize(bytes, out var envelope) && envelope.Type == MsgType.SessionHandshakeResult)
                    failureSent.TrySetResult();
                return Task.CompletedTask;
            };
            // Observe the rejection without letting it mutate the fake peer's state.
            hostClient.ShouldDeliverSendAsync = (_, _, _) => Task.FromResult(false);

            InvokePrivateMethod(host, "FailInboundHandshake", pending, reason, helperClient.Address);

            if (retained)
            {
                Assert.Same(security, host.CurrentSessionSecurityState);
                for (var i = 0; i < fields.Length; i++) Assert.Same(before[i], GetPrivateField(host, fields[i]));
                Assert.False(failureSent.Task.IsCompleted);
                if (newerPending) Assert.Same(currentPending, GetPrivateField(host, "pendingInboundHandshake"));
                else Assert.Null(GetPrivateField(host, "pendingInboundHandshake"));
            }
            else
            {
                await failureSent.Task.WaitAsync(lifetime.Token);
                Assert.Equal(SessionHandshakeState.Failed, host.CurrentSessionSecurityState.HandshakeState);
                Assert.Equal(reason, host.CurrentSessionSecurityState.HandshakeFailureReason);
                Assert.False(host.CurrentSessionSecurityState.ApprovalGranted);
                foreach (var name in fields) Assert.Null(GetPrivateField(host, name));
                Assert.Null(GetPrivateField(host, "pendingInboundHandshake"));
            }
        }
        finally { FakeNknClient.ResetNetwork(); }
    }

    [Theory]
    [InlineData("handshake_start_timeout", true)]
    [InlineData("handshake_response_mac_invalid", false)]
    [Trait("FileTransferInfraSuite", "HandshakeCleanup")]
    public async Task HandshakeCleanup_OutboundFailureHonorsRetainedSession(string reason, bool retained)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("outbound-cleanup.host.address");
            var helperClient = new FakeNknClient("outbound-cleanup.helper.address");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("cleanup-host", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("cleanup-helper", helperClient.Address));
            await ApproveNknSessionAsync(host, helper, lifetime.Token, suppressAutomaticAccelerationNegotiation: true);
            var security = helper.CurrentSessionSecurityState;
            var envelope = GetPrivateField(helper, "currentEnvelopeCode");
            var key = GetPrivateField(helper, "controlSessionSharedKey");
            var disconnected = 0;
            helper.Disconnected += (_, _) => disconnected++;

            InvokePrivateMethod(helper, "AbortOutboundHandshake", reason);

            Assert.Equal(retained ? 0 : 1, disconnected);
            if (retained)
            {
                Assert.Same(security, helper.CurrentSessionSecurityState);
                Assert.Same(envelope, GetPrivateField(helper, "currentEnvelopeCode"));
                Assert.Same(key, GetPrivateField(helper, "controlSessionSharedKey"));
            }
            else
            {
                Assert.Equal(SessionHandshakeState.Failed, helper.CurrentSessionSecurityState.HandshakeState);
                Assert.Null(GetPrivateField(helper, "currentEnvelopeCode"));
                Assert.Null(GetPrivateField(helper, "controlSessionSharedKey"));
            }
        }
        finally { FakeNknClient.ResetNetwork(); }
    }

    [Theory]
    [InlineData("handshake_response_mac_invalid")]
    [InlineData("handshake_response_expired")]
    [Trait("FileTransferInfraSuite", "HandshakeCleanup")]
    public async Task HandshakeCleanup_ResponseClaimDoesNotBypassAuthentication(string reason)
    {
        FakeNknClient.ResetNetwork();
        try
        {
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var options = NknTransportOptions.Load();
            var hostClient = new FakeNknClient("claim.host.address");
            var helperClient = new FakeNknClient("claim.helper.address");
            using var host = new NknSignalingTransport(hostClient, options, new NknIdentity("claim-host", hostClient.Address));
            using var helper = new NknSignalingTransport(helperClient, options, new NknIdentity("claim-helper", helperClient.Address));
            await ApproveNknSessionAsync(host, helper, lifetime.Token, suppressAutomaticAccelerationNegotiation: true);
            var session = host.CurrentSessionSecurityState.SessionId!.Value;
            var pending = CreateCleanupPending(host, helper, "claimed-join", session);
            if (reason == "handshake_response_expired")
                pending.GetType().GetProperty("ChallengeExpiresAtUtc")!.SetValue(pending, DateTimeOffset.UtcNow.AddSeconds(-1));
            SetPrivateField(host, "pendingInboundHandshake", pending);
            hostClient.ShouldDeliverSendAsync = (_, _, _) => Task.FromResult(false);
            var response = new SessionHandshakeResponse(session, new PeerAddress(helper.LocalPeerAddress), "synthetic-challenge", "invalid-base64");
            var envelope = new Envelope(1, (string)GetPrivateField(host, "currentEnvelopeCode")!, "response",
                MsgType.SessionHandshakeResponse, SessionHandshakeProtocol.Serialize(response), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);

            InvokePrivateMethod(host, "HandleSessionHandshakeResponse", helper.LocalPeerAddress, envelope);

            Assert.True((bool)pending.GetType().GetProperty("ResponseReceived")!.GetValue(pending)!);
            Assert.Equal(reason, host.CurrentSessionSecurityState.HandshakeFailureReason);
            Assert.False(host.CurrentSessionSecurityState.ApprovalGranted);
            Assert.Null(GetPrivateField(host, "pendingInboundHandshake"));
            Assert.Null(GetPrivateField(host, "currentEnvelopeCode"));
            Assert.Null(GetPrivateField(host, "controlSessionSharedKey"));
        }
        finally { FakeNknClient.ResetNetwork(); }
    }

    private static object CreateCleanupPending(NknSignalingTransport host, NknSignalingTransport helper,
        string joinId, SessionId sessionId)
    {
        var type = typeof(NknSignalingTransport).GetNestedType("PendingInboundHandshakeState", BindingFlags.NonPublic)!;
        var pending = Activator.CreateInstance(type, joinId, helper.LocalPeerAddress,
            new PeerAddress(helper.LocalPeerAddress), new byte[32], GetPrivateField(host, "currentEnvelopeCode"))!;
        return type.GetMethod("WithChallenge")!.Invoke(pending,
            [sessionId, true, CapabilityGrant.Chat, "synthetic-challenge", DateTimeOffset.UtcNow.AddSeconds(30),
                new PeerAddress(host.LocalPeerAddress)])!;
    }
}
