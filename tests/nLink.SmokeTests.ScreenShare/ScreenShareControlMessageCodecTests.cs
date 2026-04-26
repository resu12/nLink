using System.Reflection;
using NLink.App.Services;
using NLink.App.Services.ScreenCapture;
using NLink.Core;
using NLink.Core.RemoteControl;
using NLink.Core.ScreenShare;
using NLink.Core.SessionConnect;
using NLink.Core.SessionSecurity;
using NLink.Infra.Nkn;

namespace NLink.SmokeTests;

[Trait("Area", "ScreenShare")]
[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareControlMessageCodecTests : ScreenShareTransportBoundaryTestBase
{
[Trait("Category", "Smoke")]
    [Fact]
    public void ScreenSharePressureStateCodec_RoundTrips_VisibleHeadFrameId()
    {
        var payload = ScreenSharePressureStateCodec.Serialize(
            new ScreenSharePressureStateV1
            {
                SessionId = "codec-visible-head",
                Mode = ScreenSharePressureMode.Normal,
                Reason = ScreenSharePressureProtocol.PressureReasonHealthy,
                ObservedFrameAgeMs = 12,
                RecentStaleFrameDrops = 0,
                SentAtUtcMs = 1234,
                LastVisibleApplyFrameId = 19,
                VisibleHeadFrameId = 17,
                VisibleRecoveryFloorFrameId = 13,
                AppliedHeadFrameId = 22,
                SteadyVisibleProgressActive = true,
                StableVisibleHeadFrameId = 22,
                FramesAppliedSinceLastGap = 5,
                CurrentEpochRecoveryKeyframeApplyCount = 2,
            });

        Assert.True(ScreenSharePressureStateCodec.TryDeserialize(payload, out var parsed));
        Assert.Equal(17L, parsed.VisibleHeadFrameId);
        Assert.Equal(19L, parsed.LastVisibleApplyFrameId);
        Assert.Equal(13L, parsed.VisibleRecoveryFloorFrameId);
        Assert.Equal(2L, parsed.CurrentEpochRecoveryKeyframeApplyCount);
    }

[Trait("Category", "Smoke")]
    [Fact]
    public void ScreenShareRecoveryReceiptCodec_RoundTrips_AndRejectsInvalidPayloads()
    {
        var payload = ScreenShareRecoveryReceiptCodec.Serialize(
            new ScreenShareRecoveryReceiptV1
            {
                SessionId = " receipt-session ",
                StreamEpoch = 7,
                OwnerFrameId = 11,
                VisibleRecoveryFrameId = 13,
                VisibleHeadFrameId = 17,
                ReceiptKind = ScreenShareRecoveryReceiptCodec.VisibleProgressAfterRecoveryKeyframeReceiptKind,
            });

        Assert.True(ScreenShareRecoveryReceiptCodec.TryDeserialize(payload, out var parsed));
        Assert.Equal("receipt-session", parsed.SessionId);
        Assert.Equal(17L, parsed.VisibleHeadFrameId);
        Assert.Equal(ScreenShareRecoveryReceiptCodec.VisibleProgressAfterRecoveryKeyframeReceiptKind, parsed.ReceiptKind);

        Assert.False(
            ScreenShareRecoveryReceiptCodec.TryDeserialize(
                """
                {"Kind":"screenshare","Type":"screenshare.recovery_receipt.v1","SessionId":"x","StreamEpoch":1,"OwnerFrameId":1,"VisibleRecoveryFrameId":9,"VisibleHeadFrameId":8,"ReceiptKind":"recovery_keyframe_visible"}
                """u8.ToArray(),
                out _));
        Assert.False(
            ScreenShareRecoveryReceiptCodec.TryDeserialize(
                """
                {"Kind":"screenshare","Type":"screenshare.recovery_receipt.v1","SessionId":"x","StreamEpoch":0,"OwnerFrameId":1,"VisibleRecoveryFrameId":1,"VisibleHeadFrameId":1,"ReceiptKind":"recovery_keyframe_visible"}
                """u8.ToArray(),
                out _));
        Assert.False(
            ScreenShareRecoveryReceiptCodec.TryDeserialize(
                """
                {"Kind":"screenshare","Type":"screenshare.recovery_receipt.v1","SessionId":"x","StreamEpoch":1,"OwnerFrameId":1,"VisibleRecoveryFrameId":1,"VisibleHeadFrameId":1,"ReceiptKind":"not_allowed"}
                """u8.ToArray(),
                out _));
    }

[Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_SendScreenShareRecoveryReceiptAsync_UsesRecoveryReceiptControlMessageType()
    {
        var client = new BridgePolicyCapabilityClient();
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("recovery-receipt-send-test", "recovery.receipt.send.addr");
        using var transport = new NknSignalingTransport(client, options, identity);

        var securityState = CreateApprovedSecurityState(
            new PeerAddress("receipt.send.helpee"),
            new PeerAddress("receipt.send.helper"),
            CapabilityGrant.ScreenShare);
        var controlKey = CreateControlSharedKey();
        ConfigureNknTransportForScreenShareControlTests(transport, securityState, controlKey);

        await transport.SendScreenShareRecoveryReceiptAsync(
            new ScreenShareRecoveryReceiptV1
            {
                SessionId = string.Empty,
                StreamEpoch = 3,
                OwnerFrameId = 5,
                VisibleRecoveryFrameId = 8,
                VisibleHeadFrameId = 10,
                ReceiptKind = ScreenShareRecoveryReceiptCodec.RecoveryKeyframeVisibleReceiptKind,
            },
            CancellationToken.None);

        var sent = Assert.Single(client.SentMessages);
        Assert.True(EnvelopeCodec.TryDeserialize(sent.Payload, out var envelope));
        Assert.Equal(MsgType.ScreenShareRecoveryReceipt, envelope.Type);

        var securePayload = SessionSecureEnvelopeCodec.Decrypt(
            controlKey,
            envelope.Payload,
            new SessionSecureEnvelopeExpectation(
                Family: SessionSecureMessageFamily.RemoteControl,
                MessageType: "screenshare_recovery_receipt",
                SessionId: securityState.SessionId,
                SenderIdentity: new PeerAddress(transport.LocalPeerAddress)));
        Assert.True(ScreenShareRecoveryReceiptCodec.TryDeserialize(securePayload.Plaintext, out var parsed));
        Assert.Equal(securityState.SessionId!.Value.Value, parsed.SessionId);
        Assert.Equal(8L, parsed.VisibleRecoveryFrameId);
    }

[Trait("Category", "Smoke")]
    [Fact]
    public void NknTransport_RouteControlEnvelope_ScreenShareRecoveryReceipt_RaisesAndRejectsInvalidMessages()
    {
        var client = new BridgePolicyCapabilityClient();
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("recovery-receipt-receive-test", "recovery.receipt.receive.addr");
        using var transport = new NknSignalingTransport(client, options, identity);

        var securityState = CreateApprovedSecurityState(
            new PeerAddress("receipt.receive.helpee"),
            new PeerAddress("receipt.receive.helper"),
            CapabilityGrant.ScreenShare);
        var controlKey = CreateControlSharedKey();
        ConfigureNknTransportForScreenShareControlTests(transport, securityState, controlKey);

        ScreenShareRecoveryReceiptV1? received = null;
        transport.ScreenShareRecoveryReceiptReceived += (_, e) => received = e.Message;

        var validEnvelope = BuildSecureScreenShareRecoveryReceiptEnvelope(
            transport,
            securityState,
            controlKey,
            new ScreenShareRecoveryReceiptV1
            {
                SessionId = securityState.SessionId!.Value.Value,
                StreamEpoch = 2,
                OwnerFrameId = 9,
                VisibleRecoveryFrameId = 12,
                VisibleHeadFrameId = 13,
                ReceiptKind = ScreenShareRecoveryReceiptCodec.RecoveryKeyframeVisibleReceiptKind,
            },
            sequence: 1);
        InvokePrivateMethod(transport, "RouteControlEnvelope", securityState.HelperAddress!.Value.Value, validEnvelope);
        Assert.NotNull(received);
        Assert.Equal(12L, received!.VisibleRecoveryFrameId);

        received = null;
        var invalidEnvelope = BuildSecureScreenShareRecoveryReceiptEnvelope(
            transport,
            securityState,
            controlKey,
            """
            {"Kind":"screenshare","Type":"screenshare.recovery_receipt.v1","SessionId":"bad","StreamEpoch":1,"OwnerFrameId":1,"VisibleRecoveryFrameId":5,"VisibleHeadFrameId":4,"ReceiptKind":"recovery_keyframe_visible"}
            """u8.ToArray(),
            sequence: 2);
        InvokePrivateMethod(transport, "RouteControlEnvelope", securityState.HelperAddress!.Value.Value, invalidEnvelope);
        Assert.Null(received);

        var wrongSessionEnvelope = BuildSecureScreenShareRecoveryReceiptEnvelope(
            transport,
            securityState,
            controlKey,
            new ScreenShareRecoveryReceiptV1
            {
                SessionId = "wrong-session",
                StreamEpoch = 2,
                OwnerFrameId = 9,
                VisibleRecoveryFrameId = 12,
                VisibleHeadFrameId = 13,
                ReceiptKind = ScreenShareRecoveryReceiptCodec.RecoveryKeyframeVisibleReceiptKind,
            },
            sequence: 3);
        InvokePrivateMethod(transport, "RouteControlEnvelope", securityState.HelperAddress!.Value.Value, wrongSessionEnvelope);
        Assert.Null(received);
    }

[Trait("Category", "Smoke")]
    [Fact]
    public void NknEnvelopeRouter_ScreenShareRecoveryReceipt_RoutesToControlChannel()
    {
        var client = new BridgePolicyCapabilityClient();
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("recovery-receipt-router-test", "recovery.receipt.router.addr");
        using var transport = new NknSignalingTransport(client, options, identity);

        var securityState = CreateApprovedSecurityState(
            new PeerAddress("receipt.router.helpee"),
            new PeerAddress("receipt.router.helper"),
            CapabilityGrant.ScreenShare);
        var controlKey = CreateControlSharedKey();
        ConfigureNknTransportForScreenShareControlTests(transport, securityState, controlKey);

        ScreenShareRecoveryReceiptV1? received = null;
        transport.ScreenShareRecoveryReceiptReceived += (_, e) => received = e.Message;

        var envelope = BuildSecureScreenShareRecoveryReceiptEnvelope(
            transport,
            securityState,
            controlKey,
            new ScreenShareRecoveryReceiptV1
            {
                SessionId = securityState.SessionId!.Value.Value,
                StreamEpoch = 5,
                OwnerFrameId = 11,
                VisibleRecoveryFrameId = 11,
                VisibleHeadFrameId = 12,
                ReceiptKind = ScreenShareRecoveryReceiptCodec.RecoveryKeyframeVisibleReceiptKind,
            },
            sequence: 1);

        var envelopeRouter = GetPrivateField(transport, "envelopeRouter");
        InvokePublicMethod(
            envelopeRouter,
            "RouteInboundMessage",
            new NknInboundEnvelopeContext(
                securityState.HelperAddress!.Value.Value,
                NknBridgeChannel.Control,
                envelope,
                BridgeIngressObservedUtcMs: 0,
                EnvelopeParsedUtcMs: 0,
                BridgeMessageObservedUtcMs: 0,
                BinaryFrameDecodedUtcMs: 0,
                SocketDataEventEmittedUtcMs: 0,
                WsReceiverWriteEnteredUtcMs: 0,
                WsMessageEmittedUtcMs: 0,
                SdkHandleMsgEnteredUtcMs: 0,
                ClientMessageDispatchUtcMs: 0,
                MultiClientMessageDispatchUtcMs: 0));

        Assert.NotNull(received);
        Assert.Equal(11L, received!.VisibleRecoveryFrameId);
        Assert.Equal(12L, received.VisibleHeadFrameId);
    }

}
