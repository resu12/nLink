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

public sealed class ScreenShareTransportBoundaryTests
{
    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_WiresScreenShareEventsThroughResolvedScreenShareTransport()
    {
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(() => transport);
        var frameCount = 0;
        var stopCount = 0;

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("media-only.helpee"),
                new PeerAddress("media-only.helper"),
                CapabilityGrant.ScreenShare));

        runtime.ScreenShareFrameCompleted += (_, _) => frameCount++;
        runtime.ScreenShareStopped += (_, _) => stopCount++;

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        transport.RaiseScreenShareFrameCompleted(
            new ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", new byte[] { 0x01 }, SessionId: sessionId));
        transport.RaiseScreenShareStopped();

        Assert.Equal(1, frameCount);
        Assert.Equal(1, stopCount);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_SendScreenSharePayloadAsync_PreservesCapabilityAndSessionValidation()
    {
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(() => transport);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("screen-send.helpee"),
                new PeerAddress("screen-send.helper"),
                CapabilityGrant.ScreenShare));

        await InvokePrivateAsync(
            runtime,
            "SendScreenSharePayloadCoreAsync",
            new ReadOnlyMemory<byte>(CreateFramePayload("other_session")),
            CancellationToken.None);
        Assert.Empty(transport.SentScreenSharePayloads);

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        await InvokePrivateAsync(
            runtime,
            "SendScreenSharePayloadCoreAsync",
            new ReadOnlyMemory<byte>(CreateFramePayload(sessionId)),
            CancellationToken.None);

        Assert.Single(transport.SentScreenSharePayloads);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_SendsPressureStateThroughResolvedTransport()
    {
        var now = new DateTimeOffset(2026, 4, 17, 10, 0, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(sessionId, 1, 18, true);
        ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(sessionId, 1, "recovery_keyframe_applied", 18, -1);
        ScreenShareFrameLossAttributionRegistry.ObserveVisibleRecoveryFloor(sessionId, 1, 18);

        SetPrivateField(runtime, "helperRemoteCurrentPressureEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount", 8);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 18L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteSteadyVisibleProgressActive", true);
        SetPrivateField(runtime, "helperRemoteSteadyProgressVisibleHeadFrameId", 18L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 18L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 8L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 1200d);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameCount", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameIndex", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameAgesMs", new long[] { 300L, 0L, 0L });
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameAgeMs", 300L);
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-100));
        SetPrivateField(runtime, "helperRemoteLastApplyCadenceMs", 180L);
        SetPrivateField(runtime, "helperRemoteApplyCadenceObserved", 8L);
        SetPrivateField(runtime, "helperRemoteTotalApplyCadenceMs", 8L * 180L);
        SetPrivateField(runtime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.ReduceFps);
        SetPrivateField(runtime, "lastSentScreenSharePressureReason", ScreenSharePressureProtocol.PressureReasonHighFrameAge);
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5));
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "healthyScreenSharePressureIntervals", 3);

        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");

        var sent = WaitForSinglePressureState(transport.SentPressureStates);
        Assert.Equal(sessionId, sent.SessionId);
        Assert.Equal(18L, sent.VisibleHeadFrameId);
        Assert.Equal(18L, sent.VisibleRecoveryFloorFrameId);
        Assert.True(sent.CurrentEpochRecoveryKeyframeApplyCount >= 1);
    }

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
            securityState.HelperAddress!.Value.Value,
            NknBridgeChannel.Control,
            envelope);

        Assert.NotNull(received);
        Assert.Equal(11L, received!.VisibleRecoveryFrameId);
        Assert.Equal(12L, received.VisibleHeadFrameId);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_ReceivesScreenShareRecoveryReceipt_WithoutMutatingPressureOrRecoveryBehavior()
    {
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(() => transport);

        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("receipt.runtime.helpee"),
                new PeerAddress("receipt.runtime.helper"),
                CapabilityGrant.ScreenShare));

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        transport.RaiseScreenShareRecoveryReceiptReceived(
            new ScreenShareRecoveryReceiptV1
            {
                SessionId = sessionId,
                StreamEpoch = 4,
                OwnerFrameId = 14,
                VisibleRecoveryFrameId = 18,
                VisibleHeadFrameId = 19,
                ReceiptKind = ScreenShareRecoveryReceiptCodec.RecoveryKeyframeVisibleReceiptKind,
            });

        Assert.Equal(1L, GetScreenShareControlHostLongField(runtime, "remoteScreenShareRecoveryReceiptReceivedCount"));
        Assert.Equal(4L, GetScreenShareControlHostLongField(runtime, "remoteScreenShareLastRecoveryReceiptStreamEpoch"));
        Assert.Equal(14L, GetScreenShareControlHostLongField(runtime, "remoteScreenShareLastRecoveryReceiptOwnerFrameId"));
        Assert.Equal(18L, GetScreenShareControlHostLongField(runtime, "remoteScreenShareLastRecoveryReceiptVisibleRecoveryFrameId"));
        Assert.Equal(19L, GetScreenShareControlHostLongField(runtime, "remoteScreenShareLastRecoveryReceiptVisibleHeadFrameId"));
        Assert.Equal(ScreenShareRecoveryReceiptCodec.RecoveryKeyframeVisibleReceiptKind, Assert.IsType<string>(GetScreenShareControlHostField(runtime, "remoteScreenShareLastRecoveryReceiptKind")));
        Assert.Empty(transport.SentPressureStates);
        Assert.Empty(transport.SentVideoKeyframeRequests);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperPublishesRecoveryReceipt_WhenVisibleRecoveryFloorEqualsOwner()
    {
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(() => transport);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("receipt.publish.eq.helpee"),
                new PeerAddress("receipt.publish.eq.helper"),
                CapabilityGrant.ScreenShare));

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 10, 10, 0, "started");
        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(sessionId, 1, 10, true);
        ScreenShareFrameLossAttributionRegistry.ObserveVisibleRecoveryFloor(sessionId, 1, 10);

        ReportHelperRemoteFrameApplied(runtime, 180, 1, 10, 10, 10, 1);

        var sent = WaitForSingleRecoveryReceipt(transport.SentRecoveryReceipts);
        Assert.Equal(sessionId, sent.SessionId);
        Assert.Equal(1L, sent.StreamEpoch);
        Assert.Equal(10L, sent.OwnerFrameId);
        Assert.Equal(10L, sent.VisibleRecoveryFrameId);
        Assert.Equal(10L, sent.VisibleHeadFrameId);
        Assert.Equal(ScreenShareRecoveryReceiptCodec.RecoveryKeyframeVisibleReceiptKind, sent.ReceiptKind);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperPublishesVisibleProgressRecoveryReceipt_WhenVisibleRecoveryFloorAdvancesBeyondOwner()
    {
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(() => transport);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("receipt.publish.progress.helpee"),
                new PeerAddress("receipt.publish.progress.helper"),
                CapabilityGrant.ScreenShare));

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 10, 10, 0, "started");
        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(sessionId, 1, 13, true);
        ScreenShareFrameLossAttributionRegistry.ObserveVisibleRecoveryFloor(sessionId, 1, 13);

        ReportHelperRemoteFrameApplied(runtime, 180, 1, 13, 13, 13, 4);

        var sent = WaitForSingleRecoveryReceipt(transport.SentRecoveryReceipts);
        Assert.Equal(10L, sent.OwnerFrameId);
        Assert.Equal(13L, sent.VisibleRecoveryFrameId);
        Assert.Equal(13L, sent.VisibleHeadFrameId);
        Assert.Equal(ScreenShareRecoveryReceiptCodec.VisibleProgressAfterRecoveryKeyframeReceiptKind, sent.ReceiptKind);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperDoesNotPublishRecoveryReceipt_WithoutVisibleRecoveryFloor()
    {
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(() => transport);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("receipt.publish.none.helpee"),
                new PeerAddress("receipt.publish.none.helper"),
                CapabilityGrant.ScreenShare));

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 10, 10, 0, "started");
        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(sessionId, 1, 10, true);

        ReportHelperRemoteFrameApplied(runtime, 180, 1, 10, 10, 10, 1);

        Thread.Sleep(100);
        Assert.Empty(transport.SentRecoveryReceipts);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperDoesNotImmediatelyDuplicateRecoveryReceipt_ForSameReceiptKey()
    {
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(() => transport);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("receipt.publish.nodup.helpee"),
                new PeerAddress("receipt.publish.nodup.helper"),
                CapabilityGrant.ScreenShare));

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 10, 10, 0, "started");
        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(sessionId, 1, 10, true);
        ScreenShareFrameLossAttributionRegistry.ObserveVisibleRecoveryFloor(sessionId, 1, 10);

        ReportHelperRemoteFrameApplied(runtime, 180, 1, 10, 10, 10, 1);
        _ = WaitForSingleRecoveryReceipt(transport.SentRecoveryReceipts);
        transport.SentRecoveryReceipts.Clear();

        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(sessionId, 1, 11, true);
        ReportHelperRemoteFrameApplied(runtime, 170, 1, 11, 11, 11, 2);

        Thread.Sleep(100);
        Assert.Empty(transport.SentRecoveryReceipts);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperRecoveryReceipt_RetriesOnce_WhenSameReceiptRemainsCurrent()
    {
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(() => transport);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("receipt.publish.retry.helpee"),
                new PeerAddress("receipt.publish.retry.helper"),
                CapabilityGrant.ScreenShare));

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 10, 10, 0, "started");
        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(sessionId, 1, 10, true);
        ScreenShareFrameLossAttributionRegistry.ObserveVisibleRecoveryFloor(sessionId, 1, 10);

        ReportHelperRemoteFrameApplied(runtime, 180, 1, 10, 10, 10, 1);
        _ = WaitForSingleRecoveryReceipt(transport.SentRecoveryReceipts);
        transport.SentRecoveryReceipts.Clear();

        WaitUntil(() => transport.SentRecoveryReceipts.Count == 1);
        var retry = Assert.Single(transport.SentRecoveryReceipts);
        Assert.Equal(10L, retry.VisibleRecoveryFrameId);
        transport.SentRecoveryReceipts.Clear();

        Thread.Sleep(350);
        Assert.Empty(transport.SentRecoveryReceipts);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperRecoveryReceipt_RetryIsSuppressed_WhenRecoveryOwnerChanges()
    {
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(() => transport);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("receipt.publish.retrysuppress.helpee"),
                new PeerAddress("receipt.publish.retrysuppress.helper"),
                CapabilityGrant.ScreenShare));

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 10, 10, 0, "started");
        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(sessionId, 1, 10, true);
        ScreenShareFrameLossAttributionRegistry.ObserveVisibleRecoveryFloor(sessionId, 1, 10);

        ReportHelperRemoteFrameApplied(runtime, 180, 1, 10, 10, 10, 1);
        _ = WaitForSingleRecoveryReceipt(transport.SentRecoveryReceipts);
        transport.SentRecoveryReceipts.Clear();

        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 20, 20, 0, "started");

        Thread.Sleep(350);
        Assert.Empty(transport.SentRecoveryReceipts);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_HoldsCatchUpOnlyBeforeRecovering()
    {
        var now = new DateTimeOffset(2026, 4, 2, 8, 0, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        SetPrivateField(runtime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.CatchUpOnly);
        SetPrivateField(runtime, "lastSentScreenSharePressureReason", ScreenSharePressureProtocol.PressureReasonHighFrameAge);
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-1));
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "healthyScreenSharePressureIntervals", 3);

        ReportHelperRemoteFrameApplied(runtime, 120, 1);

        Thread.Sleep(100);
        Assert.Empty(transport.SentPressureStates);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_AllowsHealthyRecoveryWhileTransportHealthIsOnlyAdvisory()
    {
        var now = new DateTimeOffset(2026, 4, 2, 8, 10, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble
        {
            RecentHealthIssueCount = 2,
        };
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        SetPrivateField(runtime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.ReduceFps);
        SetPrivateField(runtime, "lastSentScreenSharePressureReason", ScreenSharePressureProtocol.PressureReasonHighFrameAge);
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5));
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "healthyScreenSharePressureIntervals", 3);

        ReportHelperRemoteFrameApplied(runtime, 120, 1);

        var sent = WaitForSinglePressureState(transport.SentPressureStates);
        Assert.Equal(ScreenSharePressureMode.Normal, sent.Mode);
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonHealthy, sent.Reason);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_BridgeHealthQuarantine_SuppressesActionablePressure()
    {
        var now = new DateTimeOffset(2026, 4, 18, 19, 0, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble
        {
            RecentHealthIssueCount = 2,
            IsCongested = true,
            QueueDepth = 2,
        };
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        SetPrivateField(runtime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.ReduceFps);
        SetPrivateField(runtime, "lastSentScreenSharePressureReason", ScreenSharePressureProtocol.PressureReasonBridgeHealth);
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddMilliseconds(-500));
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "healthyScreenSharePressureIntervals", 3);

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        InvokePrivateMethod(
            runtime,
            "TrackHelperRemoteScreenShareAcceptedFrame",
            new ScreenShareFrameCompletedEventArgs(
                FrameId: 1,
                Width: 1280,
                Height: 720,
                Encoding: "h264",
                EncodedFrameBytes: new byte[] { 0x01 },
                SessionId: sessionId,
                StreamEpoch: 1));

        ReportHelperRemoteFrameApplied(runtime, 120, 1, 1, 1, 1, 4);
        Thread.Sleep(100);

        Assert.DoesNotContain(
            transport.SentPressureStates,
            sent => sent.Mode == ScreenSharePressureMode.ReduceFps &&
                    string.Equals(sent.Reason, ScreenSharePressureProtocol.PressureReasonBridgeHealth, StringComparison.Ordinal));
        Assert.DoesNotContain(
            transport.SentPressureStates,
            sent => sent.Mode == ScreenSharePressureMode.CatchUpOnly &&
                    string.Equals(sent.Reason, ScreenSharePressureProtocol.PressureReasonBridgeHealth, StringComparison.Ordinal));

        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.True(diagnostics.BridgeHealthAdvisoryCount >= 1);
        Assert.True(diagnostics.BridgeHealthQuarantineSuppressedCount >= 1);
        Assert.Equal(0L, diagnostics.BridgeHealthTicks);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_PostQuarantineBridgeHealth_RequiresTwoCorrelatedEvaluations()
    {
        var now = new DateTimeOffset(2026, 4, 18, 19, 5, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble
        {
            RecentHealthIssueCount = 2,
            IsCongested = true,
            QueueDepth = 2,
        };
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        InvokePrivateMethod(
            runtime,
            "TrackHelperRemoteScreenShareAcceptedFrame",
            new ScreenShareFrameCompletedEventArgs(
                FrameId: 1,
                Width: 1280,
                Height: 720,
                Encoding: "h264",
                EncodedFrameBytes: new byte[] { 0x01 },
                SessionId: sessionId,
                StreamEpoch: 1));

        ReportHelperRemoteFrameApplied(runtime, 120, 1, 1, 1, 1, 4);
        Thread.Sleep(100);
        transport.SentPressureStates.Clear();

        now = now.AddMilliseconds(1700);
        SetPrivateField(runtime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
        SetPrivateField(runtime, "lastSentScreenSharePressureReason", ScreenSharePressureProtocol.PressureReasonHealthy);
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5));
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "healthyScreenSharePressureIntervals", 4);

        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");
        Assert.DoesNotContain(
            transport.SentPressureStates,
            sent => sent.Mode == ScreenSharePressureMode.ReduceFps &&
                    string.Equals(sent.Reason, ScreenSharePressureProtocol.PressureReasonBridgeHealth, StringComparison.Ordinal));
        transport.SentPressureStates.Clear();

        ScreenSharePressureStateV1? actionableBridgeHealth = null;
        for (var attempt = 0; attempt < 3 && actionableBridgeHealth is null; attempt++)
        {
            now = now.AddMilliseconds(1100);
            InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");
            actionableBridgeHealth = transport.SentPressureStates.FirstOrDefault(
                sent =>
                    sent.Mode == ScreenSharePressureMode.ReduceFps &&
                    string.Equals(sent.Reason, ScreenSharePressureProtocol.PressureReasonBridgeHealth, StringComparison.Ordinal));
            if (actionableBridgeHealth is null)
            {
                transport.SentPressureStates.Clear();
            }
        }

        Assert.NotNull(actionableBridgeHealth);

        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.True(diagnostics.BridgeHealthActionableCount >= 1);
        Assert.Equal(0L, diagnostics.BridgeHealthActionableWithoutQueueOrDropCount);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_KeepsCatchUpOnlyDuringRepeatedStaleDrops()
    {
        var now = new DateTimeOffset(2026, 4, 2, 8, 20, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        SetPrivateField(runtime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.CatchUpOnly);
        SetPrivateField(runtime, "lastSentScreenSharePressureReason", ScreenSharePressureProtocol.PressureReasonRepeatedStaleDrops);
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount", 3);
        ReportHelperRemoteStaleDrop(runtime, 1510, 1);
        Thread.Sleep(50);
        ReportHelperRemoteStaleDrop(runtime, 1550, 1);

        var sent = WaitForSinglePressureState(transport.SentPressureStates);
        Assert.Equal(ScreenSharePressureMode.CatchUpOnly, sent.Mode);
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonRepeatedStaleDrops, sent.Reason);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_SingleStaleDropAfterHealthyApply_DoesNotEscalateToCatchUpOnly()
    {
        var now = new DateTimeOffset(2026, 4, 2, 8, 25, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        ReportHelperRemoteFrameApplied(runtime, 180, 1);
        Thread.Sleep(50);
        transport.SentPressureStates.Clear();

        ReportHelperRemoteStaleDrop(runtime, 1510, 1);

        Thread.Sleep(100);
        Assert.Empty(transport.SentPressureStates);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_StartupWarmupSuppressesEarlyHighFrameAgePressure()
    {
        var now = new DateTimeOffset(2026, 4, 2, 8, 30, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        ReportHelperRemoteFrameApplied(runtime, 1250, 1);
        ReportHelperRemoteFrameApplied(runtime, 1320, 1);

        Thread.Sleep(100);
        Assert.Empty(transport.SentPressureStates);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_ThreeConsecutiveHighAppliedFrames_WithoutBaseline_DoNotSendCatchUpOnly()
    {
        var now = new DateTimeOffset(2026, 4, 2, 8, 31, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        ReportHelperRemoteFrameApplied(runtime, 1250, 1);
        ReportHelperRemoteFrameApplied(runtime, 1320, 1);
        Thread.Sleep(50);
        transport.SentPressureStates.Clear();

        ReportHelperRemoteFrameApplied(runtime, 1290, 1);

        Thread.Sleep(100);
        Assert.Empty(transport.SentPressureStates);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_StartupWarmupSuppressesStaleDropPressure()
    {
        var now = new DateTimeOffset(2026, 4, 2, 8, 32, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        ReportHelperRemoteFrameApplied(runtime, 2600, 1);
        Thread.Sleep(50);
        transport.SentPressureStates.Clear();

        ReportHelperRemoteStaleDrop(runtime, 2400, 1);
        Thread.Sleep(50);
        ReportHelperRemoteStaleDrop(runtime, 2300, 1);

        Thread.Sleep(100);
        Assert.Empty(transport.SentPressureStates);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_IsolatedLateSamplesWithOngoingProgress_DoNotSendReduceFps()
    {
        var now = new DateTimeOffset(2026, 4, 14, 12, 0, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        for (var i = 0; i < 8; i++)
        {
            ReportHelperRemoteFrameApplied(runtime, 180, 1, i);
            now = now.AddMilliseconds(120);
        }

        Thread.Sleep(100);
        transport.SentPressureStates.Clear();

        now = now.AddMilliseconds(620);
        ReportHelperRemoteFrameApplied(
            runtime,
            210,
            1,
            9,
            9,
            9,
            10,
            CreateHelperSessionSnapshot(1, 9, 9, 9, 10));
        now = now.AddMilliseconds(610);
        ReportHelperRemoteFrameApplied(
            runtime,
            230,
            1,
            10,
            10,
            10,
            11,
            CreateHelperSessionSnapshot(1, 10, 10, 10, 11));
        now = now.AddMilliseconds(640);
        ReportHelperRemoteFrameApplied(
            runtime,
            250,
            1,
            11,
            11,
            11,
            12,
            CreateHelperSessionSnapshot(1, 11, 11, 11, 12));

        Thread.Sleep(100);
        Assert.DoesNotContain(
            transport.SentPressureStates,
            sent => sent.Mode != ScreenSharePressureMode.Normal ||
                    !string.Equals(sent.Reason, ScreenSharePressureProtocol.PressureReasonHealthy, StringComparison.Ordinal));

        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.True(diagnostics.SteadyVisibleProgressActive);
        Assert.True(diagnostics.CurrentEpochProgressProven);
        Assert.True(diagnostics.AppliedHeadAdvancedSinceLastEvaluation || diagnostics.StableVisibleHeadAdvancedSinceLastEvaluation);
        Assert.NotEqual("none", diagnostics.HelperHealthyStateEstablishedBy);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_SustainedVisibleProgressStall_SendsReduceFps()
    {
        var now = new DateTimeOffset(2026, 4, 17, 8, 0, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        for (var i = 0; i < 8; i++)
        {
            ReportHelperRemoteFrameApplied(runtime, 180, 1, i);
            now = now.AddMilliseconds(120);
        }

        Thread.Sleep(100);
        transport.SentPressureStates.Clear();

        now = now.AddMilliseconds(450);
        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");
        Assert.Empty(transport.SentPressureStates);

        var firstDiagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.Equal(1L, firstDiagnostics.BaselineFrozenDueToStallCount);
        Assert.Equal(1L, firstDiagnostics.CadenceStallWindowCount);
        Assert.Equal(0L, firstDiagnostics.CadenceStallTriggerCount);

        now = now.AddMilliseconds(300);
        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");
        Assert.Empty(transport.SentPressureStates);

        now = now.AddMilliseconds(100);
        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");

        var sent = WaitForSinglePressureState(transport.SentPressureStates);
        Assert.Equal(ScreenSharePressureMode.ReduceFps, sent.Mode);
        Assert.Contains(
            sent.Reason,
            new[]
            {
                ScreenSharePressureProtocol.PressureReasonSlowApplyCadence,
                ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            });

        var secondDiagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.Equal(1L, secondDiagnostics.BaselineFrozenDueToStallCount);
        Assert.Equal(1L, secondDiagnostics.CadenceStallWindowCount);
        Assert.False(secondDiagnostics.SteadyVisibleProgressActive);
        Assert.False(secondDiagnostics.CurrentEpochProgressProven);
        Assert.Equal(0L, secondDiagnostics.NonHealthyClearSuppressedDueToProgressCount);
        if (string.Equals(sent.Reason, ScreenSharePressureProtocol.PressureReasonSlowApplyCadence, StringComparison.Ordinal))
        {
            Assert.Equal(1L, secondDiagnostics.CadenceStallTriggerCount);
        }
        else
        {
            Assert.Equal(0L, secondDiagnostics.CadenceStallTriggerCount);
        }

        now = now.AddMilliseconds(50);
        ReportHelperRemoteFrameApplied(runtime, 220, 1, 12);

        Assert.False(Assert.IsType<bool>(GetPrivateField(runtime, "helperRemoteCurrentPressureEpochCadenceStallTriggered")));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_SingleSevereAgeSpike_DoesNotEscalateToCatchUpOnly()
    {
        var now = new DateTimeOffset(2026, 4, 17, 8, 5, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        for (var i = 0; i < 8; i++)
        {
            ReportHelperRemoteFrameApplied(runtime, 180, 1, i);
            now = now.AddMilliseconds(120);
        }

        Thread.Sleep(100);
        transport.SentPressureStates.Clear();

        now = now.AddMilliseconds(850);
        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");

        Assert.DoesNotContain(
            transport.SentPressureStates,
            sent => sent.Mode == ScreenSharePressureMode.CatchUpOnly &&
                    string.Equals(sent.Reason, ScreenSharePressureProtocol.PressureReasonHighFrameAge, StringComparison.Ordinal));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_NewEpochNeedMoreInputBeforeFirstApply_DoesNotDemote()
    {
        var now = new DateTimeOffset(2026, 4, 13, 19, 0, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        InvokePrivateMethod(
            runtime,
            "TrackHelperRemoteScreenShareAcceptedFrame",
            new ScreenShareFrameCompletedEventArgs(
                FrameId: 2,
                Width: 1280,
                Height: 720,
                Encoding: "h264",
                EncodedFrameBytes: new byte[] { 0x01 },
                SessionId: sessionId,
                StreamEpoch: 2));

        runtime.ReportHelperRemoteScreenShareDecodeNeedsMoreInput(2);

        Thread.Sleep(100);
        Assert.Empty(transport.SentPressureStates);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_NewEpochResetsTailMetricsBeforeHealthyApply()
    {
        var now = new DateTimeOffset(2026, 4, 13, 19, 5, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        for (var i = 0; i < 8; i++)
        {
            ReportHelperRemoteFrameApplied(runtime, 180, 1, i);
            now = now.AddMilliseconds(120);
        }

        Thread.Sleep(100);
        transport.SentPressureStates.Clear();

        now = now.AddMilliseconds(620);
        ReportHelperRemoteFrameApplied(runtime, 220, 1, 8);
        now = now.AddMilliseconds(620);
        ReportHelperRemoteFrameApplied(runtime, 230, 1, 9);
        now = now.AddMilliseconds(620);
        ReportHelperRemoteFrameApplied(runtime, 240, 1, 10);

        Thread.Sleep(100);
        Assert.Empty(transport.SentPressureStates);
        transport.SentPressureStates.Clear();

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        InvokePrivateMethod(
            runtime,
            "TrackHelperRemoteScreenShareAcceptedFrame",
            new ScreenShareFrameCompletedEventArgs(
                FrameId: 3,
                Width: 1280,
                Height: 720,
                Encoding: "h264",
                EncodedFrameBytes: new byte[] { 0x02 },
                SessionId: sessionId,
                StreamEpoch: 2));

        ReportHelperRemoteFrameApplied(runtime, 180, 2, 3);

        Thread.Sleep(100);
        Assert.Empty(transport.SentPressureStates);
        Assert.False(Assert.IsType<bool>(GetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineEstablished")));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_RecoveryKeyframeResetsTailMetricsAndSuppressesImmediateRepressure()
    {
        var now = new DateTimeOffset(2026, 4, 14, 15, 0, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        for (var i = 0; i < 8; i++)
        {
            ReportHelperRemoteFrameApplied(runtime, 180, 1, i);
            now = now.AddMilliseconds(120);
        }

        Thread.Sleep(100);
        transport.SentPressureStates.Clear();

        now = now.AddMilliseconds(620);
        ReportHelperRemoteFrameApplied(runtime, 220, 1, 8);
        now = now.AddMilliseconds(620);
        ReportHelperRemoteFrameApplied(runtime, 230, 1, 9);
        now = now.AddMilliseconds(620);
        ReportHelperRemoteFrameApplied(runtime, 240, 1, 10);

        Thread.Sleep(100);
        Assert.Empty(transport.SentPressureStates);
        transport.SentPressureStates.Clear();

        InvokePrivateMethod(
            runtime,
            "ReportHelperRemoteScreenShareContinuityLost",
            1L,
            "need_more_input_burst",
            true,
            4L,
            -1L,
            -1L,
            12L);
        Thread.Sleep(100);
        transport.SentPressureStates.Clear();
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5));

        now = now.AddMilliseconds(50);
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 20, 20, 0, "started");
        InvokePrivateMethod(runtime, "ReportHelperRemoteScreenShareRecoveryKeyframeApplied", 480L, 1L);

        transport.SentPressureStates.Clear();
        Assert.Equal(0L, GetPrivateLongField(runtime, "helperRemoteCurrentPressureEpochNeedMoreInputCount"));
        Assert.Equal(0L, GetPrivateLongField(runtime, "helperRemoteCurrentPressureEpochStaleDropCount"));
        Assert.Equal(0, Assert.IsType<int>(GetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount")));
        Assert.False(Assert.IsType<bool>(GetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstApplySeen")));
        Assert.Equal(-1L, GetPrivateLongField(runtime, "helperRemoteLastApplyCadenceMs"));
        Assert.False(Assert.IsType<bool>(GetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineEstablished")));

        now = now.AddMilliseconds(50);
        ReportHelperRemoteFrameApplied(runtime, 320, 1, 20);
        Assert.Equal(1, Assert.IsType<int>(GetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount")));
        Assert.True(Assert.IsType<bool>(GetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstApplySeen")));

        now = now.AddMilliseconds(850);
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 20, 21, 1, "follower_applied");
        ReportHelperRemoteFrameApplied(runtime, 340, 1, 21);

        Thread.Sleep(100);
        Assert.Empty(transport.SentPressureStates);
        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.False(diagnostics.RecoveryWindowProgressed);
        Assert.False(diagnostics.RecoveryWindowSucceeded);
        Assert.Equal(0L, diagnostics.RecoveryWindowProgressedCount);
        Assert.Equal(0L, diagnostics.RecoveryWindowSuccessCount);
        Assert.Equal(0, Assert.IsType<int>(GetPrivateField(runtime, "helperRemoteCurrentPressureEpochAgePressureConsecutiveCount")));
        Assert.Equal(0, Assert.IsType<int>(GetPrivateField(runtime, "helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount")));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_PostRecoveryStabilization_SuppressesImmediateHighFrameAgeRepressure()
    {
        var now = new DateTimeOffset(2026, 4, 14, 15, 10, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        InvokePrivateMethod(
            runtime,
            "ReportHelperRemoteScreenShareContinuityLost",
            1L,
            "need_more_input_burst",
            true,
            6L,
            -1L,
            -1L,
            40L);
        Thread.Sleep(100);
        transport.SentPressureStates.Clear();

        now = now.AddMilliseconds(50);
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 40, 40, 0, "started");
        InvokePrivateMethod(runtime, "ReportHelperRemoteScreenShareRecoveryKeyframeApplied", 520L, 1L);
        ReportHelperRemoteFrameApplied(runtime, 520, 1, 40);
        Thread.Sleep(100);
        transport.SentPressureStates.Clear();

        now = now.AddMilliseconds(150);
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 40, 41, 1, "follower_applied");
        ReportHelperRemoteFrameApplied(runtime, 760, 1, 41);
        now = now.AddMilliseconds(150);
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 40, 42, 2, "follower_applied");
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 40, 42, 2, "succeeded");
        ReportHelperRemoteFrameApplied(runtime, 780, 1, 42);
        now = now.AddMilliseconds(150);
        ReportHelperRemoteFrameApplied(runtime, 790, 1, 43);

        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.DoesNotContain(
            transport.SentPressureStates,
            sent => sent.Mode == ScreenSharePressureMode.CatchUpOnly);
        Assert.DoesNotContain(
            transport.SentPressureStates,
            sent => string.Equals(sent.Reason, ScreenSharePressureProtocol.PressureReasonHighFrameAge, StringComparison.Ordinal) ||
                    string.Equals(sent.Reason, ScreenSharePressureProtocol.PressureReasonSlowApplyCadence, StringComparison.Ordinal));
        foreach (var sent in transport.SentPressureStates)
        {
            Assert.Equal(ScreenSharePressureMode.Normal, sent.Mode);
            Assert.Equal(ScreenSharePressureProtocol.PressureReasonContinuityLoss, sent.Reason);
        }
        Assert.Equal(0L, diagnostics.VisibleAppliesDuringSettleCount);
        Assert.Equal(0L, diagnostics.PostRecoverySettleWindowCount);
        Assert.Equal(0L, diagnostics.PostRecoverySettleWindowSuccessCount);
        Assert.Equal(0L, diagnostics.PostRecoverySettleWindowTimeoutCount);
        Assert.False(diagnostics.RecoveryWindowProgressed);
        Assert.False(diagnostics.RecoveryWindowSucceeded);
        Assert.Equal(0L, diagnostics.RecoveryWindowProgressedCount);
        Assert.Equal(0L, diagnostics.RecoveryWindowSuccessCount);
        Assert.False(diagnostics.BaselineReseedInProgress);
        Assert.Equal(0L, diagnostics.BaselineReseedAfterRecoveryCount);
        Assert.False(diagnostics.BaselineEstablished);
        Assert.Equal(-1L, diagnostics.VisibleAppliesBeforePressureReenabled);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_RecoverySuccess_ReseedsBaselineAndSuppressesCadenceDuringReseedWindow()
    {
        var now = new DateTimeOffset(2026, 4, 18, 8, 30, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        InvokePrivateMethod(
            runtime,
            "ReportHelperRemoteScreenShareContinuityLost",
            1L,
            "frame_gap",
            true,
            6L,
            41L,
            42L,
            40L);
        Thread.Sleep(100);
        transport.SentPressureStates.Clear();

        now = now.AddMilliseconds(50);
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 40, 40, 0, "started");
        InvokePrivateMethod(runtime, "ReportHelperRemoteScreenShareRecoveryKeyframeApplied", 520L, 1L);
        ReportHelperRemoteFrameApplied(runtime, 520, 1, 40);

        now = now.AddMilliseconds(120);
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 40, 41, 1, "follower_applied");
        ReportHelperRemoteFrameApplied(runtime, 540, 1, 41);

        now = now.AddMilliseconds(120);
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 40, 42, 2, "follower_applied");
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 40, 42, 2, "succeeded");
        ReportHelperRemoteFrameApplied(runtime, 560, 1, 42);

        var reseedStartedDiagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.False(reseedStartedDiagnostics.BaselineReseedInProgress);
        Assert.Equal(0L, reseedStartedDiagnostics.BaselineReseedAfterRecoveryCount);

        transport.SentPressureStates.Clear();
        now = now.AddMilliseconds(120);
        ReportHelperRemoteFrameApplied(runtime, 980, 1, 43);
        var reseedStillPendingDiagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.False(reseedStillPendingDiagnostics.BaselineReseedInProgress);
        Assert.False(reseedStillPendingDiagnostics.BaselineEstablished);

        transport.SentPressureStates.Clear();
        now = now.AddMilliseconds(900);
        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");
        Assert.Empty(transport.SentPressureStates);

        now = now.AddMilliseconds(120);
        ReportHelperRemoteFrameApplied(runtime, 570, 1, 44);
        now = now.AddMilliseconds(120);
        ReportHelperRemoteFrameApplied(runtime, 580, 1, 45);
        now = now.AddMilliseconds(120);
        ReportHelperRemoteFrameApplied(runtime, 590, 1, 46);

        var reseedCompletedDiagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.False(reseedCompletedDiagnostics.BaselineEstablished);
        Assert.True(reseedCompletedDiagnostics.BaselineCaptureToRenderMs <= 0);
        Assert.Equal(0L, reseedCompletedDiagnostics.BaselineReseedAfterRecoveryCount);
        Assert.Equal(0, Assert.IsType<int>(GetPrivateField(runtime, "helperRemoteCurrentPressureEpochAgePressureConsecutiveCount")));
        Assert.Equal(0, Assert.IsType<int>(GetPrivateField(runtime, "helperRemoteCurrentPressureEpochCadencePressureConsecutiveCount")));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_SteadyVisibleProgressProof_StaysStickyAcrossLaterHealthyTicks()
    {
        var now = new DateTimeOffset(2026, 4, 18, 9, 20, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        SetPrivateField(runtime, "helperRemoteCurrentPressureEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount", 4);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9));

        ReportHelperRemoteFrameApplied(runtime, 210, 1, 12, 12, 12, 8);

        var firstDiagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.True(firstDiagnostics.SteadyVisibleProgressActive);
        Assert.Equal(12L, firstDiagnostics.StableVisibleHeadFrameId);
        Assert.Equal(8L, firstDiagnostics.FramesAppliedSinceLastGap);
        Assert.True(firstDiagnostics.DerivedPostRecoveryHealthyActive);
        Assert.Equal("stable_visible_plus_applies", firstDiagnostics.DerivedPostRecoveryHealthySource);
        Assert.Equal(12L, firstDiagnostics.DerivedPostRecoveryProofFrameId);

        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount", 2);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 2L);

        var secondDiagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.True(secondDiagnostics.SteadyVisibleProgressActive);
        Assert.Equal(12L, secondDiagnostics.StableVisibleHeadFrameId);
        Assert.Equal(12L, secondDiagnostics.LastVisibleApplyFrameId);
        Assert.True(secondDiagnostics.FramesAppliedSinceLastGap >= 8);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_PostRecoveryHealthyLatch_ActivatesFromRecoveryFloorAndLaterApply()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 21, 8, 0, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        SetPrivateField(runtime, "sessionId", "pressure-latch-session");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        ScreenShareFrameLossAttributionRegistry.ObserveVisibleRecoveryFloor(sessionId, 1, 10);

        ReportHelperRemoteFrameApplied(runtime, 180, 1, 10, 10, 10, 1);
        var beforeLatch = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.False(beforeLatch.SteadyVisibleProgressActive);
        Assert.False(beforeLatch.BaselineEstablished);

        now = now.AddMilliseconds(100);
        ReportHelperRemoteFrameApplied(runtime, 190, 1, 11, 11, 11, 2);

        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.True(diagnostics.SteadyVisibleProgressActive);
        Assert.True(diagnostics.BaselineEstablished);
        Assert.True(diagnostics.FramesAppliedSinceLastGap >= 2);
        Assert.True(diagnostics.DerivedPostRecoveryHealthyActive);
        Assert.Equal("recovery_floor_plus_head", diagnostics.DerivedPostRecoveryHealthySource);
        Assert.Equal(11L, diagnostics.DerivedPostRecoveryProofFrameId);
        Assert.Equal(1L, diagnostics.PostRecoveryHealthyLatchCount);
        Assert.Equal(0L, diagnostics.PostRecoveryHealthyLatchClearCount);
        Assert.Equal("none", diagnostics.DominantPressureBlocker);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_DerivedHealthyState_UsesEpochFactsEvenWhenLatchStateIsCleared()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 21, 8, 5, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        ScreenShareFrameLossAttributionRegistry.ObserveVisibleRecoveryFloor(sessionId, 1, 33);
        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(sessionId, 1, 80, false);

        ReportHelperRemoteFrameApplied(runtime, 180, 1, 80, 80, 80, 48);

        SetPrivateField(runtime, "helperRemoteSteadyProgressEpoch", 0L);
        SetPrivateField(runtime, "helperRemoteSteadyVisibleProgressActive", false);
        SetPrivateField(runtime, "helperRemoteSteadyProgressActivationFrameId", -1L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressVisibleHeadFrameId", -1L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", -1L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 0L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochContinuityLossTicks", 7L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupTicks", 5L);

        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.True(diagnostics.DerivedPostRecoveryHealthyActive);
        Assert.Equal("recovery_floor_plus_head", diagnostics.DerivedPostRecoveryHealthySource);
        Assert.Equal(80L, diagnostics.DerivedPostRecoveryProofFrameId);
        Assert.True(diagnostics.SteadyVisibleProgressActive);
        Assert.Equal(48L, diagnostics.FramesAppliedSinceLastGap);
        Assert.Equal("none", diagnostics.DominantPressureBlocker);
        Assert.Equal(7L, diagnostics.ContinuityLossTicks);
        Assert.Equal(5L, diagnostics.WarmupTicks);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_PostRecoveryHealthyLatch_PersistsAcrossNonHealthyPressure()
    {
        var now = new DateTimeOffset(2026, 4, 21, 8, 10, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        SetPrivateField(runtime, "helperRemoteCurrentPressureEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount", 12);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 500d);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 2L);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameCount", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameIndex", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameAgesMs", new long[] { 1400L, 0L, 0L });
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameAgeMs", 1400L);
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-100));
        SetPrivateField(runtime, "helperRemoteLastApplyCadenceMs", 120L);
        SetPrivateField(runtime, "helperRemoteApplyCadenceObserved", 12L);
        SetPrivateField(runtime, "helperRemoteTotalApplyCadenceMs", 12L * 120L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteSteadyVisibleProgressActive", true);
        SetPrivateField(runtime, "helperRemoteSteadyProgressActivationFrameId", 10L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressVisibleHeadFrameId", 15L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 15L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 6L);
        SetPrivateField(runtime, "helperRemotePostRecoveryHealthyLatchCount", 1L);
        SetPrivateField(runtime, "helperRemotePostRecoveryHealthyLastHeadAdvanceUtc", now.AddMilliseconds(-100));
        SetPrivateField(runtime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
        SetPrivateField(runtime, "lastSentScreenSharePressureReason", ScreenSharePressureProtocol.PressureReasonHealthy);
        SetPrivateField(runtime, "lastSentScreenSharePressureAgeMs", 700L);
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5));
        SetPrivateField(runtime, "healthyScreenSharePressureIntervals", 4);

        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");

        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.True(diagnostics.SteadyVisibleProgressActive);
        Assert.True(diagnostics.BaselineEstablished);
        Assert.True(diagnostics.FramesAppliedSinceLastGap > 0);
        Assert.Equal(0L, diagnostics.PostRecoveryHealthyLatchClearCount);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_PostRecoveryHealthyLatch_ClearsOnlyOnRealStall()
    {
        var now = new DateTimeOffset(2026, 4, 21, 8, 20, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        SetPrivateField(runtime, "helperRemoteCurrentPressureEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount", 10);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 220d);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 2L);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameCount", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameIndex", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameAgesMs", new long[] { 220L, 0L, 0L });
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameAgeMs", 220L);
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-800));
        SetPrivateField(runtime, "helperRemoteLastApplyCadenceMs", 120L);
        SetPrivateField(runtime, "helperRemoteApplyCadenceObserved", 10L);
        SetPrivateField(runtime, "helperRemoteTotalApplyCadenceMs", 10L * 120L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteSteadyVisibleProgressActive", true);
        SetPrivateField(runtime, "helperRemoteSteadyProgressActivationFrameId", 10L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressVisibleHeadFrameId", 15L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 15L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 6L);
        SetPrivateField(runtime, "helperRemotePostRecoveryHealthyLatchCount", 1L);
        SetPrivateField(runtime, "helperRemotePostRecoveryHealthyLastHeadAdvanceUtc", now.AddMilliseconds(-800));

        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");

        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.False(diagnostics.SteadyVisibleProgressActive);
        Assert.Equal(1L, diagnostics.PostRecoveryHealthyLatchClearCount);
        Assert.Equal("post_recovery_stall", diagnostics.PostRecoveryHealthyLatchClearReason);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_PostRecoveryStallRelatch_ReseedsBaselineInsteadOfAnchoringToSingleHighAgeFrame()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 23, 7, 10, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        SetPrivateField(runtime, "helperRemoteCurrentPressureEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount", 10);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstVisibleApplyUtc", now.AddSeconds(-9));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 15L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 220d);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 2L);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameCount", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameIndex", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameAgesMs", new long[] { 220L, 0L, 0L });
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameAgeMs", 220L);
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-800));
        SetPrivateField(runtime, "helperRemoteLastApplyCadenceMs", 120L);
        SetPrivateField(runtime, "helperRemoteApplyCadenceObserved", 10L);
        SetPrivateField(runtime, "helperRemoteTotalApplyCadenceMs", 10L * 120L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteSteadyVisibleProgressActive", true);
        SetPrivateField(runtime, "helperRemoteSteadyProgressActivationFrameId", 10L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressVisibleHeadFrameId", 15L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 15L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 6L);
        SetPrivateField(runtime, "helperRemotePostRecoveryHealthyLatchCount", 1L);
        SetPrivateField(runtime, "helperRemotePostRecoveryHealthyLastHeadAdvanceUtc", now.AddMilliseconds(-800));

        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");

        var afterStallDiagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.False(afterStallDiagnostics.SteadyVisibleProgressActive);
        Assert.Equal("post_recovery_stall", afterStallDiagnostics.PostRecoveryHealthyLatchClearReason);
        Assert.Equal(1L, afterStallDiagnostics.BaselineFrozenDueToStallCount);
        Assert.False(afterStallDiagnostics.BaselineReseedInProgress);

        now = now.AddMilliseconds(100);
        ReportHelperRemoteFrameApplied(runtime, 419, 1, 16, 16, 16, 17);

        var reseedStartedDiagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.True(reseedStartedDiagnostics.SteadyVisibleProgressActive);
        Assert.True(reseedStartedDiagnostics.BaselineReseedInProgress);
        Assert.False(reseedStartedDiagnostics.BaselineEstablished);
        Assert.True(reseedStartedDiagnostics.BaselineCaptureToRenderMs <= 0);

        now = now.AddMilliseconds(120);
        ReportHelperRemoteFrameApplied(runtime, 200, 1, 17, 17, 17, 18);
        now = now.AddMilliseconds(120);
        ReportHelperRemoteFrameApplied(runtime, 210, 1, 18, 18, 18, 19);
        now = now.AddMilliseconds(120);
        ReportHelperRemoteFrameApplied(runtime, 220, 1, 19, 19, 19, 20);

        var reseedCompletedDiagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.True(reseedCompletedDiagnostics.BaselineEstablished);
        Assert.False(reseedCompletedDiagnostics.BaselineReseedInProgress);
        Assert.InRange(reseedCompletedDiagnostics.BaselineCaptureToRenderMs, 200L, 220L);
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_HealthyPressureResendsWhenStableHeadAdvances()
    {
        var now = new DateTimeOffset(2026, 4, 18, 9, 30, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        SetPrivateField(runtime, "helperRemoteCurrentPressureEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount", 8);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 500d);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameCount", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameIndex", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameAgesMs", new long[] { 220L, 0L, 0L });
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameAgeMs", 220L);
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-100));
        SetPrivateField(runtime, "helperRemoteLastApplyCadenceMs", 120L);
        SetPrivateField(runtime, "helperRemoteApplyCadenceObserved", 8L);
        SetPrivateField(runtime, "helperRemoteTotalApplyCadenceMs", 8L * 120L);
        SetPrivateField(runtime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
        SetPrivateField(runtime, "lastSentScreenSharePressureReason", ScreenSharePressureProtocol.PressureReasonHealthy);
        SetPrivateField(runtime, "lastSentScreenSharePressureAgeMs", 220L);
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5));
        SetPrivateField(runtime, "healthyScreenSharePressureIntervals", 4);
        SetPrivateField(runtime, "helperRemoteLastSentSteadyProgressEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteLastSentSteadyVisibleProgressActive", true);
        SetPrivateField(runtime, "helperRemoteLastSentVisibleHeadFrameId", 12L);
        SetPrivateField(runtime, "helperRemoteLastSentStableVisibleHeadFrameId", 12L);
        SetPrivateField(runtime, "helperRemoteLastSentFramesAppliedSinceLastGap", 8L);
        SetPrivateField(runtime, "helperRemoteLastSentVisibleApplyFrameId", 12L);
        SetPrivateField(runtime, "helperRemoteLastSentAppliedHeadFrameId", 12L);

        ReportHelperRemoteFrameApplied(runtime, 230, 1, 16, 16, 16, 12);
        transport.SentPressureStates.Clear();
        SetPrivateField(runtime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
        SetPrivateField(runtime, "lastSentScreenSharePressureReason", ScreenSharePressureProtocol.PressureReasonHealthy);
        SetPrivateField(runtime, "lastSentScreenSharePressureAgeMs", 220L);
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5));
        SetPrivateField(runtime, "healthyScreenSharePressureIntervals", 4);
        SetPrivateField(runtime, "helperRemoteLastSentSteadyProgressEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteLastSentSteadyVisibleProgressActive", true);
        SetPrivateField(runtime, "helperRemoteLastSentVisibleHeadFrameId", 12L);
        SetPrivateField(runtime, "helperRemoteLastSentStableVisibleHeadFrameId", 12L);
        SetPrivateField(runtime, "helperRemoteLastSentFramesAppliedSinceLastGap", 8L);
        SetPrivateField(runtime, "helperRemoteLastSentVisibleApplyFrameId", 12L);
        SetPrivateField(runtime, "helperRemoteLastSentAppliedHeadFrameId", 12L);
        SetPrivateField(runtime, "helperRemoteProofKeepaliveSendCount", 0L);
        SetPrivateField(runtime, "helperRemoteLastProofKeepaliveHeadFrameId", -1L);
        SetPrivateField(runtime, "helperRemoteLastProofKeepaliveSentUtc", default(DateTimeOffset));
        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");

        var sentMessages = transport.SentPressureStates.ToArray();
        var message = Assert.Single(sentMessages);
        Assert.Equal(ScreenSharePressureMode.Normal, message.Mode);
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonHealthy, message.Reason);
        Assert.Equal(16L, message.AppliedHeadFrameId);
        Assert.Equal(16L, message.StableVisibleHeadFrameId);
        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.Equal(1L, diagnostics.ProofKeepaliveSendCount);
        Assert.Equal(16L, diagnostics.ProofKeepaliveLastHeadFrameId);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_HealthyPressure_ResendsBoundedProofKeepaliveWithoutHeadAdvance()
    {
        var now = new DateTimeOffset(2026, 4, 21, 8, 30, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        SetPrivateField(runtime, "helperRemoteCurrentPressureEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstAcceptedFrameUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount", 8);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 16L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 500d);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
        ReportHelperRemoteFrameApplied(runtime, 230, 1, 16, 16, 16, 8);

        transport.SentPressureStates.Clear();
        SetPrivateField(runtime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
        SetPrivateField(runtime, "lastSentScreenSharePressureReason", ScreenSharePressureProtocol.PressureReasonHealthy);
        SetPrivateField(runtime, "lastSentScreenSharePressureAgeMs", 220L);
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddMilliseconds(-450));
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5));
        SetPrivateField(runtime, "healthyScreenSharePressureIntervals", 4);
        SetPrivateField(runtime, "helperRemoteLastSentSteadyProgressEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteLastSentSteadyVisibleProgressActive", true);
        SetPrivateField(runtime, "helperRemoteLastSentVisibleHeadFrameId", 16L);
        SetPrivateField(runtime, "helperRemoteLastSentStableVisibleHeadFrameId", 16L);
        SetPrivateField(runtime, "helperRemoteLastSentFramesAppliedSinceLastGap", 8L);
        SetPrivateField(runtime, "helperRemoteLastSentVisibleApplyFrameId", 16L);
        SetPrivateField(runtime, "helperRemoteLastSentAppliedHeadFrameId", 16L);
        SetPrivateField(runtime, "helperRemoteProofKeepaliveSendCount", 0L);
        SetPrivateField(runtime, "helperRemoteLastProofKeepaliveHeadFrameId", -1L);
        SetPrivateField(runtime, "helperRemoteLastProofKeepaliveSentUtc", default(DateTimeOffset));

        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");

        var sentMessages = transport.SentPressureStates.ToArray();
        var message = Assert.Single(sentMessages);
        Assert.Equal(ScreenSharePressureMode.Normal, message.Mode);
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonHealthy, message.Reason);
        Assert.Equal(16L, message.AppliedHeadFrameId);
        Assert.Equal(16L, message.StableVisibleHeadFrameId);
        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.Equal(1L, diagnostics.ProofKeepaliveSendCount);
        Assert.Equal(16L, diagnostics.ProofKeepaliveLastHeadFrameId);
        Assert.Equal(0L, diagnostics.ProofKeepaliveLastSendAgeMs);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_TimerDrivenHealthyProofKeepalive_RefreshesWithoutFrameCallbacks()
    {
        var now = new DateTimeOffset(2026, 4, 21, 8, 35, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        SetPrivateField(GetScreenShareControlHost(runtime), "remoteScreenShareActive", true);
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        SetPrivateField(runtime, "helperRemoteCurrentPressureEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstAcceptedFrameUtc", now.AddSeconds(-5));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount", 8);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 16L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 200d);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameCount", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameIndex", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameAgesMs", new long[] { 0L, 0L, 0L });
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameAgeMs", 0L);
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-300));
        SetPrivateField(runtime, "helperRemoteLastApplyCadenceMs", 120L);
        SetPrivateField(runtime, "helperRemoteApplyCadenceObserved", 8L);
        SetPrivateField(runtime, "helperRemoteTotalApplyCadenceMs", 8L * 120L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteSteadyVisibleProgressActive", true);
        SetPrivateField(runtime, "helperRemoteSteadyProgressActivationFrameId", 12L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 16L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressVisibleHeadFrameId", 16L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 8L);
        SetPrivateField(runtime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
        SetPrivateField(runtime, "lastSentScreenSharePressureReason", ScreenSharePressureProtocol.PressureReasonHealthy);
        SetPrivateField(runtime, "lastSentScreenSharePressureAgeMs", 0L);
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddMilliseconds(-450));
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5));
        SetPrivateField(runtime, "healthyScreenSharePressureIntervals", 4);
        SetPrivateField(runtime, "helperRemoteLastSentSteadyProgressEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteLastSentSteadyVisibleProgressActive", true);
        SetPrivateField(runtime, "helperRemoteLastSentVisibleHeadFrameId", 16L);
        SetPrivateField(runtime, "helperRemoteLastSentStableVisibleHeadFrameId", 16L);
        SetPrivateField(runtime, "helperRemoteLastSentFramesAppliedSinceLastGap", 8L);
        SetPrivateField(runtime, "helperRemoteLastSentVisibleApplyFrameId", 16L);
        SetPrivateField(runtime, "helperRemoteLastSentAppliedHeadFrameId", 16L);
        SetPrivateField(runtime, "helperRemoteProofKeepaliveSendCount", 0L);
        SetPrivateField(runtime, "helperRemoteProofKeepaliveTimerDrivenSendCount", 0L);
        SetPrivateField(runtime, "helperRemoteLastProofKeepaliveHeadFrameId", -1L);
        SetPrivateField(runtime, "helperRemoteLastProofKeepaliveSentUtc", default(DateTimeOffset));

        InvokePrivateMethod(runtime, "OnHelperRemoteScreenSharePressureTimerTick");

        var message = WaitForSinglePressureState(transport.SentPressureStates);
        Assert.Equal(ScreenSharePressureMode.Normal, message.Mode);
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonHealthy, message.Reason);
        Assert.Equal(16L, message.AppliedHeadFrameId);
        Assert.Equal(16L, message.StableVisibleHeadFrameId);
        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.Equal(1L, diagnostics.ProofKeepaliveSendCount);
        Assert.Equal(1L, diagnostics.ProofKeepaliveTimerDrivenSendCount);
        Assert.Equal(16L, diagnostics.ProofKeepaliveLastHeadFrameId);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_SteadyVisibleProgress_SuppressesStandaloneHighFrameAge()
    {
        var now = new DateTimeOffset(2026, 4, 18, 11, 0, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        SetPrivateField(runtime, "helperRemoteCurrentPressureEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount", 10);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 17L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 500d);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochAgePressureConsecutiveCount", 0);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameCount", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameIndex", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameAgesMs", new long[] { 800L, 0L, 0L });
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameAgeMs", 800L);
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-100));
        SetPrivateField(runtime, "helperRemoteLastApplyCadenceMs", 120L);
        SetPrivateField(runtime, "helperRemoteApplyCadenceObserved", 10L);
        SetPrivateField(runtime, "helperRemoteTotalApplyCadenceMs", 10L * 120L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteSteadyVisibleProgressActive", true);
        SetPrivateField(runtime, "helperRemoteSteadyProgressActivationFrameId", 15L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 17L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressVisibleHeadFrameId", 17L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 10L);
        SetPrivateField(runtime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
        SetPrivateField(runtime, "lastSentScreenSharePressureReason", ScreenSharePressureProtocol.PressureReasonHealthy);
        SetPrivateField(runtime, "lastSentScreenSharePressureAgeMs", 220L);
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5));
        SetPrivateField(runtime, "healthyScreenSharePressureIntervals", 4);
        runtime.ReportHelperRemoteScreenShareSessionSnapshot(CreateHelperSessionSnapshot(1, 21, 21, 21, 12));

        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");

        Assert.DoesNotContain(
            transport.SentPressureStates,
            sent => sent.Mode == ScreenSharePressureMode.ReduceFps &&
                    string.Equals(sent.Reason, ScreenSharePressureProtocol.PressureReasonHighFrameAge, StringComparison.Ordinal));
        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.Equal(21L, diagnostics.AppliedHeadFrameId);
        Assert.True(diagnostics.SteadyVisibleProgressActive);
        Assert.True(diagnostics.HighFrameAgeSuppressedDueToVisibleProgressCount >= 1);
        Assert.True(diagnostics.HighFrameAgeSuppressedDueToHeadAdvanceCount >= 1);
        Assert.Equal(0L, diagnostics.NonHealthyClearSuppressedDueToProgressCount);
        Assert.Equal(0, diagnostics.ActionableHighFrameAgeCount);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_PostRecoveryAgeGrace_SuppressesStandaloneHighFrameAge()
    {
        var now = new DateTimeOffset(2026, 4, 18, 11, 2, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        SetPrivateField(runtime, "helperRemoteCurrentPressureEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount", 10);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 18L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 500d);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochAgePressureConsecutiveCount", 2);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastEvaluatedAppliedHeadFrameId", 18L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastEvaluatedStableVisibleHeadFrameId", 18L);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameCount", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameIndex", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameAgesMs", new long[] { 920L, 0L, 0L });
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameAgeMs", 920L);
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-100));
        SetPrivateField(runtime, "helperRemoteLastApplyCadenceMs", 120L);
        SetPrivateField(runtime, "helperRemoteApplyCadenceObserved", 10L);
        SetPrivateField(runtime, "helperRemoteTotalApplyCadenceMs", 10L * 120L);
        SetPrivateField(runtime, "helperRemotePostRecoveryAgeGraceEpoch", 1L);
        SetPrivateField(runtime, "helperRemotePostRecoveryAgeGraceUntilUtc", now.AddMilliseconds(900));
        SetPrivateField(runtime, "helperRemoteSteadyProgressEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteSteadyVisibleProgressActive", true);
        SetPrivateField(runtime, "helperRemoteSteadyProgressActivationFrameId", 15L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 18L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressVisibleHeadFrameId", 18L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 10L);
        SetPrivateField(runtime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
        SetPrivateField(runtime, "lastSentScreenSharePressureReason", ScreenSharePressureProtocol.PressureReasonHealthy);
        SetPrivateField(runtime, "lastSentScreenSharePressureAgeMs", 220L);
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5));
        SetPrivateField(runtime, "healthyScreenSharePressureIntervals", 4);

        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");

        Assert.DoesNotContain(
            transport.SentPressureStates,
            sent => sent.Mode == ScreenSharePressureMode.ReduceFps &&
                    string.Equals(sent.Reason, ScreenSharePressureProtocol.PressureReasonHighFrameAge, StringComparison.Ordinal));
        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.True(diagnostics.PostRecoveryAgeGraceActive);
        Assert.True(diagnostics.PostRecoveryAgeGraceSuppressedCount >= 1);
        Assert.Equal(0, diagnostics.ActionableHighFrameAgeCount);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_SteadyVisibleProgress_AllowsSustainedHighFrameAgeAfterHeadAdvances()
    {
        var now = new DateTimeOffset(2026, 4, 18, 11, 5, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        SetPrivateField(runtime, "helperRemoteCurrentPressureEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount", 10);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 20L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 500d);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochAgePressureConsecutiveCount", 1);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastEvaluatedAppliedHeadFrameId", 20L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastEvaluatedStableVisibleHeadFrameId", 20L);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameCount", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameIndex", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameAgesMs", new long[] { 1500L, 0L, 0L });
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameAgeMs", 1500L);
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-100));
        SetPrivateField(runtime, "helperRemoteLastApplyCadenceMs", 120L);
        SetPrivateField(runtime, "helperRemoteApplyCadenceObserved", 10L);
        SetPrivateField(runtime, "helperRemoteTotalApplyCadenceMs", 10L * 120L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteSteadyVisibleProgressActive", true);
        SetPrivateField(runtime, "helperRemoteSteadyProgressActivationFrameId", 15L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 20L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressVisibleHeadFrameId", 20L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 12L);
        SetPrivateField(runtime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
        SetPrivateField(runtime, "lastSentScreenSharePressureReason", ScreenSharePressureProtocol.PressureReasonHealthy);
        SetPrivateField(runtime, "lastSentScreenSharePressureAgeMs", 220L);
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5));
        SetPrivateField(runtime, "healthyScreenSharePressureIntervals", 4);

        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");

        var sent = WaitForSinglePressureState(transport.SentPressureStates);
        Assert.Equal(ScreenSharePressureMode.ReduceFps, sent.Mode);
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonHighFrameAge, sent.Reason);
        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.True(diagnostics.ActionableHighFrameAgeCount >= 1);
        Assert.False(diagnostics.SteadyVisibleProgressActive);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_AgeOnlySampleWithHeadAdvance_DoesNotClearSteadyProgress()
    {
        var now = new DateTimeOffset(2026, 4, 18, 11, 10, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        SetPrivateField(runtime, "helperRemoteCurrentPressureEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount", 10);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 21L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 500d);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochAgePressureConsecutiveCount", 0);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastEvaluatedAppliedHeadFrameId", 18L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastEvaluatedStableVisibleHeadFrameId", 18L);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameCount", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameIndex", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameAgesMs", new long[] { 820L, 0L, 0L });
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameAgeMs", 820L);
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-100));
        SetPrivateField(runtime, "helperRemoteLastApplyCadenceMs", 120L);
        SetPrivateField(runtime, "helperRemoteApplyCadenceObserved", 10L);
        SetPrivateField(runtime, "helperRemoteTotalApplyCadenceMs", 10L * 120L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteSteadyVisibleProgressActive", true);
        SetPrivateField(runtime, "helperRemoteSteadyProgressActivationFrameId", 15L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressStableVisibleHeadFrameId", 21L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressVisibleHeadFrameId", 21L);
        SetPrivateField(runtime, "helperRemoteSteadyProgressFramesAppliedSinceLastGap", 12L);
        SetPrivateField(runtime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
        SetPrivateField(runtime, "lastSentScreenSharePressureReason", ScreenSharePressureProtocol.PressureReasonHealthy);
        SetPrivateField(runtime, "lastSentScreenSharePressureAgeMs", 220L);
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5));
        SetPrivateField(runtime, "healthyScreenSharePressureIntervals", 4);

        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");

        Assert.DoesNotContain(
            transport.SentPressureStates,
            sent => sent.Mode == ScreenSharePressureMode.ReduceFps &&
                    string.Equals(sent.Reason, ScreenSharePressureProtocol.PressureReasonHighFrameAge, StringComparison.Ordinal));
        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.True(diagnostics.SteadyVisibleProgressActive);
        Assert.True(diagnostics.HighFrameAgeSuppressedDueToHeadAdvanceCount >= 1);
        Assert.Equal(0, diagnostics.ActionableHighFrameAgeCount);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_PostRecoverySettleTimeout_IsNotUsedByPressureSendPath()
    {
        var now = new DateTimeOffset(2026, 4, 17, 9, 0, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        InvokePrivateMethod(
            runtime,
            "ReportHelperRemoteScreenShareContinuityLost",
            1L,
            "frame_gap",
            true,
            5L,
            11L,
            12L,
            10L);
        Thread.Sleep(100);
        transport.SentPressureStates.Clear();
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-5));

        now = now.AddMilliseconds(50);
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 10, 10, 0, "started");
        InvokePrivateMethod(runtime, "ReportHelperRemoteScreenShareRecoveryKeyframeApplied", 220L, 1L);
        ReportHelperRemoteFrameApplied(runtime, 220, 1, 10);
        transport.SentPressureStates.Clear();

        now = now.AddMilliseconds(450);
        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");

        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.Equal(0L, diagnostics.PostRecoverySettleWindowCount);
        Assert.Equal(0L, diagnostics.PostRecoverySettleWindowSuccessCount);
        Assert.Equal(0L, diagnostics.PostRecoverySettleWindowTimeoutCount);
        Assert.Equal(0L, diagnostics.VisibleAppliesDuringSettleCount);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_FirstVisibleRecoveryFrame_UsesNormalPressureSendPath()
    {
        var now = new DateTimeOffset(2026, 4, 17, 9, 30, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        InvokePrivateMethod(
            runtime,
            "ReportHelperRemoteScreenShareContinuityLost",
            1L,
            "frame_gap",
            true,
            5L,
            11L,
            12L,
            10L);
        Thread.Sleep(100);
        transport.SentPressureStates.Clear();

        now = now.AddMilliseconds(50);
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 10, 10, 0, "started");
        InvokePrivateMethod(runtime, "ReportHelperRemoteScreenShareRecoveryKeyframeApplied", 220L, 1L);
        transport.SentPressureStates.Clear();

        now = now.AddMilliseconds(50);
        ReportHelperRemoteFrameApplied(runtime, 230, 1, 10);
        Thread.Sleep(100);
        Assert.Empty(transport.SentPressureStates);
        Assert.Equal(1, Assert.IsType<int>(GetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount")));

        now = now.AddMilliseconds(50);
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 10, 11, 1, "follower_applied");
        ReportHelperRemoteFrameApplied(runtime, 240, 1, 11);

        for (var frameId = 12L; frameId <= 15L && transport.SentPressureStates.Count == 0; frameId++)
        {
            now = now.AddMilliseconds(200);
            ReportHelperRemoteFrameApplied(runtime, 240 + ((int)(frameId - 11L) * 10), 1, frameId);
            InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");
        }

        var sentMessages = transport.SentPressureStates.ToArray();
        Assert.Empty(sentMessages);
        Assert.True(Assert.IsType<int>(GetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount")) >= 2);
        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.False(diagnostics.RecoveryWindowProgressed);
        Assert.False(diagnostics.RecoveryWindowSucceeded);
        Assert.Equal(0L, diagnostics.RecoveryWindowProgressedCount);
        Assert.Equal(0L, diagnostics.RecoveryWindowSuccessCount);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_AppliedHeadAdvanceDuringRecovery_BypassesPressureSend()
    {
        var now = new DateTimeOffset(2026, 4, 18, 12, 0, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        SetPrivateField(runtime, "helperRemoteCurrentPressureEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstAcceptedFrameUtc", now.AddSeconds(-2));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochFirstApplySeen", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount", 15);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastVisibleApplyFrameId", 19L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupStartedUtc", now.AddSeconds(-10));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochWarmupEndedUtc", now.AddSeconds(-9));
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineEstablished", true);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineCaptureToRenderMs", 500d);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochBaselineSampleCount", 4L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochAgePressureConsecutiveCount", 2);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastEvaluatedAppliedHeadFrameId", 15L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochLastEvaluatedStableVisibleHeadFrameId", -1L);
        SetPrivateField(runtime, "helperRemoteCurrentPressureEpochVisibleAppliesDuringSettleCount", 1L);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameCount", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameIndex", 1);
        SetPrivateField(runtime, "helperRemoteRecentAppliedFrameAgesMs", new long[] { 1500L, 0L, 0L });
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameAgeMs", 1500L);
        SetPrivateField(runtime, "helperRemoteLastAppliedFrameUtc", now.AddMilliseconds(-100));
        SetPrivateField(runtime, "helperRemoteLastApplyCadenceMs", 120L);
        SetPrivateField(runtime, "helperRemoteApplyCadenceObserved", 16L);
        SetPrivateField(runtime, "helperRemoteTotalApplyCadenceMs", 16L * 120L);
        SetPrivateField(runtime, "helperRemoteRecoveryWindowActive", true);
        SetPrivateField(runtime, "helperRemoteRecoveryWindowEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteRecoveryWindowRecoveryFrameId", 19L);
        SetPrivateField(runtime, "helperRemoteRecoveryWindowLastContiguousFrameId", 19L);
        SetPrivateField(runtime, "helperRemoteRecoveryWindowContiguousFollowerApplyCount", 0);
        SetPrivateField(runtime, "helperRemoteContinuityRecoveryActive", true);
        SetPrivateField(runtime, "helperRemoteContinuityRecoveryEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteContinuityRecoveryStartedUtc", now.AddMilliseconds(-500));
        SetPrivateField(runtime, "helperRemoteLastSentSteadyProgressEpoch", 1L);
        SetPrivateField(runtime, "helperRemoteLastSentSteadyVisibleProgressActive", false);
        SetPrivateField(runtime, "helperRemoteLastSentVisibleHeadFrameId", -1L);
        SetPrivateField(runtime, "helperRemoteLastSentStableVisibleHeadFrameId", -1L);
        SetPrivateField(runtime, "helperRemoteLastSentFramesAppliedSinceLastGap", 0L);
        SetPrivateField(runtime, "helperRemoteLastSentVisibleApplyFrameId", 15L);
        SetPrivateField(runtime, "helperRemoteLastSentAppliedHeadFrameId", 15L);
        SetPrivateField(runtime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.ReduceFps);
        SetPrivateField(runtime, "lastSentScreenSharePressureReason", ScreenSharePressureProtocol.PressureReasonHighFrameAge);
        SetPrivateField(runtime, "lastSentScreenSharePressureAgeMs", 1600L);
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now.AddMilliseconds(-100));
        SetPrivateField(runtime, "lastSentScreenSharePressureModeEnteredUtc", now.AddSeconds(-1));

        transport.SentPressureStates.Clear();
        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");
        Thread.Sleep(100);

        var message = Assert.Single(transport.SentPressureStates);
        Assert.Equal(ScreenSharePressureMode.ReduceFps, message.Mode);
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonHighFrameAge, message.Reason);
        Assert.Equal(19L, message.LastVisibleApplyFrameId);
        Assert.Equal(19L, message.AppliedHeadFrameId);
        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.True(diagnostics.PressureSendBypassedForVisibleProgressCount > 0);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_DuplicateVisibleApplyForSameFrame_DoesNotDoubleCountProgress()
    {
        var now = new DateTimeOffset(2026, 4, 17, 9, 45, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        InvokePrivateMethod(
            runtime,
            "ReportHelperRemoteScreenShareContinuityLost",
            1L,
            "frame_gap",
            true,
            5L,
            11L,
            12L,
            10L);
        Thread.Sleep(100);

        now = now.AddMilliseconds(50);
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 10, 10, 0, "started");
        InvokePrivateMethod(runtime, "ReportHelperRemoteScreenShareRecoveryKeyframeApplied", 220L, 1L);
        transport.SentPressureStates.Clear();

        now = now.AddMilliseconds(50);
        ReportHelperRemoteFrameApplied(runtime, 230, 1, 10);
        transport.SentPressureStates.Clear();

        now = now.AddMilliseconds(50);
        ReportHelperRemoteFrameApplied(runtime, 240, 1, 10);
        Thread.Sleep(100);

        Assert.Empty(transport.SentPressureStates);
        Assert.Equal(1, Assert.IsType<int>(GetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount")));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureFeedback_FirstVisibleApplyDuringContinuityLoss_BypassesThrottle()
    {
        var now = new DateTimeOffset(2026, 4, 20, 10, 0, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        InvokePrivateMethod(
            runtime,
            "ReportHelperRemoteScreenShareContinuityLost",
            1L,
            "frame_gap",
            true,
            5L,
            11L,
            12L,
            10L);
        transport.SentPressureStates.Clear();

        SetPrivateField(runtime, "lastSentScreenSharePressureMode", ScreenSharePressureMode.Normal);
        SetPrivateField(runtime, "lastSentScreenSharePressureReason", ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        SetPrivateField(runtime, "lastSentScreenSharePressureUtc", now);

        now = now.AddMilliseconds(50);
        ReportHelperRemoteFrameApplied(runtime, 230, 1, 10);
        Thread.Sleep(100);

        var message = Assert.Single(transport.SentPressureStates);
        Assert.Equal(ScreenSharePressureMode.Normal, message.Mode);
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonContinuityLoss, message.Reason);
        Assert.Equal(10L, message.LastVisibleApplyFrameId);

        transport.SentPressureStates.Clear();
        InvokePrivateMethod(runtime, "MaybeSendScreenSharePressureState");
        Assert.Empty(transport.SentPressureStates);

        var diagnostics = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.True(diagnostics.PressureSendBypassedForVisibleProgressCount > 0);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_HelperScreenSharePressureDiagnosticsSnapshot_CorrelatesVisibleProgressWithRecoveryState()
    {
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
        var now = new DateTimeOffset(2026, 4, 14, 15, 20, 0, TimeSpan.Zero);
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(
            () => transport,
            watchdogOptions: SessionRuntimeWatchdogOptions.Default,
            nowProvider: () => now);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("pressure.helpee"),
                new PeerAddress("pressure.helper"),
                CapabilityGrant.ScreenShare));

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(sessionId, 1, "gap_detected", 4, 6);

        InvokePrivateMethod(
            runtime,
            "ReportHelperRemoteScreenShareContinuityLost",
            1L,
            "frame_gap",
            true,
            4L,
            6L,
            -1L,
            12L);

        Thread.Sleep(100);
        var beforeVisible = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.Equal(1L, beforeVisible.StreamEpoch);
        Assert.True(beforeVisible.ContinuityLossTicks > 0);
        Assert.True(beforeVisible.WarmupTicks > 0);
        Assert.True(beforeVisible.BeforeFirstVisibleApplyTicks > 0);
        Assert.Equal("continuity_loss", beforeVisible.DominantPressureBlocker);

        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(sessionId, 1, 10, true);
        ScreenShareFrameLossAttributionRegistry.ObserveEpochContinuityEvent(sessionId, 1, "recovery_keyframe_applied", 10, -1);
        ScreenShareFrameLossAttributionRegistry.ObserveRecoveryKeyframeResync(sessionId, 1, 10);
        ScreenShareFrameLossAttributionRegistry.ObserveVisibleRecoveryFloor(sessionId, 1, 10);

        now = now.AddMilliseconds(50);
        ReportHelperRemoteRecoveryWindowStateChanged(runtime, 1, 10, 10, 0, "started");
        InvokePrivateMethod(runtime, "ReportHelperRemoteScreenShareRecoveryKeyframeApplied", 180L, 1L);
        ReportHelperRemoteFrameApplied(runtime, 180, 1, 10);

        now = now.AddMilliseconds(50);
        ScreenShareFrameLossAttributionRegistry.ObserveFrameApplied(sessionId, 1, 11, false);
        ReportHelperRemoteFrameApplied(runtime, 175, 1, 11);

        var afterVisible = runtime.GetHelperRemoteScreenSharePressureDiagnosticsSnapshotForTests();
        Assert.Equal(11L, afterVisible.LastVisibleApplyFrameId);
        Assert.True(afterVisible.FramesAppliedSinceLastGap >= 2);
        Assert.True(afterVisible.DerivedPostRecoveryHealthyActive);
        Assert.Equal("recovery_floor_plus_head", afterVisible.DerivedPostRecoveryHealthySource);
        Assert.True(afterVisible.CurrentEpochGapCount >= 1);
        Assert.True(afterVisible.CurrentEpochRecoveryKeyframeApplyCount >= 1);
        Assert.True(afterVisible.CurrentEpochResyncCount >= 1);
        Assert.Equal(0L, afterVisible.PostRecoverySettleWindowCount);
        Assert.Equal(0L, afterVisible.PostRecoverySettleWindowSuccessCount);
        Assert.False(afterVisible.RecoveryWindowProgressed);
        Assert.False(afterVisible.RecoveryWindowSucceeded);
        Assert.Equal(0L, afterVisible.RecoveryWindowProgressedCount);
        Assert.Equal(0L, afterVisible.RecoveryWindowSuccessCount);
        Assert.Equal(HelperRemoteSessionPhase.VisibleStable, afterVisible.HelperSessionPhase);
        Assert.Equal(HelperRemoteRecoveryMechanism.None, afterVisible.HelperRecoveryMechanism);
        Assert.True(afterVisible.CurrentEpochProgressProven);
        Assert.Equal("none", afterVisible.DominantPressureBlocker);
        Assert.Equal(-1L, afterVisible.VisibleAppliesBeforePressureReenabled);
        Assert.Equal(
            Assert.IsType<int>(GetPrivateField(runtime, "helperRemoteCurrentPressureEpochApplyCount")),
            (int)afterVisible.FramesAppliedSinceLastGap);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task SessionRuntime_SendRemoteControlDisplayInfoAsync_StaysOnControlPlane()
    {
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(() => transport);

        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("display-info.helpee"),
                new PeerAddress("display-info.helper"),
                CapabilityGrant.ScreenShare | CapabilityGrant.RemoteControl));

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        await InvokePrivateAsync(
            runtime,
            "SendRemoteControlDisplayInfoAsync",
            sessionId,
            new ControlDisplayInfoMessageV1
            {
                SessionId = sessionId,
                DisplayId = "display-1",
                VirtualDesktopWidth = 1920,
                VirtualDesktopHeight = 1080,
                CaptureRegionWidth = 1920,
                CaptureRegionHeight = 1080,
                FrameWidth = 1920,
                FrameHeight = 1080,
                Revision = 1,
                TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            },
            CancellationToken.None);

        Assert.Single(transport.SentDisplayInfoMessages);
        Assert.Empty(transport.SentScreenSharePayloads);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_SenderFiltering_AcceptsCurrentScreenShareTransportAndRejectsStaleScreenShareTransport()
    {
        using var currentTransport = new ScreenShareSignalingTransportDouble();
        using var runtime = new SessionRuntime(() => currentTransport);
        var frameCount = 0;

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", currentTransport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", currentTransport);
        currentTransport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("adapter-filter.helpee"),
                new PeerAddress("adapter-filter.helper"),
                CapabilityGrant.ScreenShare));

        runtime.ScreenShareFrameCompleted += (_, _) => frameCount++;
        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        InvokePrivateMethod(
            runtime,
            "OnTransportScreenShareFrameCompleted",
            currentTransport,
            new ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", new byte[] { 0x01 }, SessionId: sessionId));

        using var staleTransport = new ScreenShareSignalingTransportDouble();
        InvokePrivateMethod(
            runtime,
            "OnTransportScreenShareFrameCompleted",
            staleTransport,
            new ScreenShareFrameCompletedEventArgs(2, 1, 1, "jpeg", new byte[] { 0x02 }, SessionId: sessionId));

        Assert.Equal(1, frameCount);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_RemoteScreenShareActivity_SwitchesFileTransferFlowControlMode()
    {
        using var transport = new ScreenShareAwareSignalingTransportDouble();
        using var runtime = new SessionRuntime(() => transport);

        runtime.SetRoleForTests(SessionRuntimeRole.Helper);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("flow-mode.helpee"),
                new PeerAddress("flow-mode.helper"),
                CapabilityGrant.ScreenShare));

        Assert.Equal("Background", GetFileTransferFlowControlMode(runtime));

        var sessionId = runtime.SecurityState.SessionId!.Value.Value;
        transport.RaiseScreenShareFrameCompleted(
            new ScreenShareFrameCompletedEventArgs(1, 1, 1, "jpeg", new byte[] { 0x01 }, SessionId: sessionId));

        Assert.Equal("Interactive", GetFileTransferFlowControlMode(runtime));

        transport.RaiseScreenShareStopped();

        Assert.Equal("Background", GetFileTransferFlowControlMode(runtime));
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public void SessionRuntime_SenderDegradedMode_UpdatesTransportCatchUpPolicy()
    {
        using var transport = new ScreenSharePolicyAwareTransportDouble();
        using var runtime = new SessionRuntime(() => transport);

        runtime.SetRoleForTests(SessionRuntimeRole.Helpee);
        SetPrivateField(runtime, "transport", transport);
        SetPrivateField(runtime, "state", SessionRuntimeState.Connected);
        SetPrivateField(runtime, "statusText", "Connected");
        SetPrivateField(GetScreenShareControlHost(runtime), "remoteScreenShareActive", true);
        InvokePrivateMethod(runtime, "WireTransport", transport);
        transport.SetSessionSecurityStateForTests(
            CreateApprovedSecurityState(
                new PeerAddress("policy.helpee"),
                new PeerAddress("policy.helper"),
                CapabilityGrant.ScreenShare));

        var handler = runtime.GetType().GetMethod("OnScreenShareSenderDegradedModeChanged", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(handler);
        var degradedArgsType = runtime.GetType().Assembly.GetType("NLink.App.Services.ScreenCapture.ScreenShareSenderDegradedModeChangedEventArgs");
        Assert.NotNull(degradedArgsType);
        var degradedEnteredArgs = Activator.CreateInstance(
            degradedArgsType!,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object?[] { true },
            culture: null);
        Assert.NotNull(degradedEnteredArgs);

        handler!.Invoke(runtime, new[] { null, degradedEnteredArgs });
        WaitUntil(() => transport.PolicyUpdates.Count == 1);
        Assert.True(transport.PolicyUpdates[^1]);

        var degradedExitedArgs = Activator.CreateInstance(
            degradedArgsType!,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object?[] { false },
            culture: null);
        Assert.NotNull(degradedExitedArgs);

        handler.Invoke(runtime, new[] { null, degradedExitedArgs });
        WaitUntil(() => transport.PolicyUpdates.Count == 2);
        Assert.False(transport.PolicyUpdates[^1]);
    }

    [Trait("Category", "Smoke")]
    [Fact]
    public async Task NknTransport_ScreenShareBridgePolicy_StartRetry_WaitsForRunningBridge()
    {
        var client = new BridgePolicyCapabilityClient();
        var options = NknTransportOptions.Load();
        var identity = new NknIdentity("bridge-policy-test", "bridge.policy.test.addr");

        using var transport = new NknSignalingTransport(client, options, identity);

        await InvokePrivateAsync(
            transport,
            "EnsureScreenShareBridgeSessionStartedAsync",
            CancellationToken.None);

        Assert.Empty(client.PolicyApplications);
        Assert.Equal(0L, GetPrivateLongField(transport, "screenShareBridgePolicyGeneration"));

        client.IsBridgeProcessRunning = true;

        await InvokePrivateAsync(
            transport,
            "EnsureScreenShareBridgeSessionStartedAsync",
            CancellationToken.None);

        var application = Assert.Single(client.PolicyApplications);
        Assert.Equal(BridgeScreenShareQueueMode.Normal, application.Mode);
        Assert.False(application.FlushQueued);
        Assert.Equal(1L, application.Generation);
        Assert.Equal(1L, GetPrivateLongField(transport, "screenShareBridgePolicyGeneration"));
    }

    private static SessionSecurityState CreateApprovedSecurityState(
        PeerAddress helpeeAddress,
        PeerAddress helperAddress,
        CapabilityGrant capabilities)
    {
        var sessionId = new SessionId($"screenshare_boundary_{Guid.NewGuid():N}");
        return (SessionSecurityState.Empty with
        {
            SessionId = sessionId,
            HelpeeAddress = helpeeAddress,
            HelperAddress = helperAddress,
            InviteValidated = true,
        }).WithHandshakeVerified(helperAddress)
          .WithApproval(new SessionGrant(helperAddress, capabilities, sessionId, DateTimeOffset.UtcNow.Add(SessionSecurityDefaults.GrantLifetime)));
    }

    private static byte[] CreateFramePayload(string sessionId)
    {
        return ScreenShareVideoPayloadCodec.SerializeFragment(
            new ScreenShareVideoFragmentV1
            {
                Type = ScreenShareVideoPayloadCodec.ScreenShareVideoFragmentTypeV1,
                SessionId = sessionId,
                StreamEpoch = 1,
                FrameId = 1,
                Width = 1,
                Height = 1,
                CapturedTsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Encoding = "h264",
                IsKeyFrame = true,
                FragmentIndex = 0,
                FragmentCount = 1,
                Data = new byte[] { 0x01 },
            });
    }

    private static void ConfigureNknTransportForScreenShareControlTests(
        NknSignalingTransport transport,
        SessionSecurityState securityState,
        byte[] controlKey)
    {
        SetPrivateField(transport, "currentSessionSecurityState", securityState);
        SetPrivateField(transport, "remoteEndpoint", securityState.HelperAddress!.Value.Value);
        SetPrivateField(transport, "currentEnvelopeCode", "screenshare-recovery-receipt-envelope");
        SetPrivateField(transport, "controlSessionSharedKey", controlKey);
    }

    private static byte[] CreateControlSharedKey()
    {
        var key = new byte[32];
        for (var i = 0; i < key.Length; i++)
        {
            key[i] = (byte)(i + 1);
        }

        return key;
    }

    private static Envelope BuildSecureScreenShareRecoveryReceiptEnvelope(
        NknSignalingTransport senderTransport,
        SessionSecurityState securityState,
        byte[] controlKey,
        ScreenShareRecoveryReceiptV1 message,
        long sequence)
    {
        return BuildSecureScreenShareRecoveryReceiptEnvelope(
            senderTransport,
            securityState,
            controlKey,
            ScreenShareRecoveryReceiptCodec.Serialize(message),
            sequence);
    }

    private static Envelope BuildSecureScreenShareRecoveryReceiptEnvelope(
        NknSignalingTransport senderTransport,
        SessionSecurityState securityState,
        byte[] controlKey,
        byte[] plaintext,
        long sequence)
    {
        var envelopeCode = Assert.IsType<string>(GetPrivateField(senderTransport, "currentEnvelopeCode"));
        var senderIdentity = securityState.HelperAddress ?? new PeerAddress("receipt.helper");
        var securePayload = SessionSecureEnvelopeCodec.Encrypt(
            controlKey,
            new SessionSecureEnvelopeMetadata(
                Family: SessionSecureMessageFamily.RemoteControl,
                MessageType: "screenshare_recovery_receipt",
                SessionId: Assert.IsType<SessionId>(securityState.SessionId),
                SenderIdentity: senderIdentity,
                Sequence: sequence,
                RequestId: null),
            plaintext);

        return new Envelope(
            Version: 1,
            Code: envelopeCode,
            MessageId: Guid.NewGuid().ToString("N"),
            Type: MsgType.ScreenShareRecoveryReceipt,
            Payload: securePayload,
            UnixTimeMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ReplyTo: null);
    }

    private static void ReportHelperRemoteFrameApplied(SessionRuntime runtime, long ageMs, long streamEpoch)
    {
        InvokePrivateMethod(runtime, "ReportHelperRemoteScreenShareFrameApplied", ageMs, streamEpoch);
    }

    private static void ReportHelperRemoteFrameApplied(SessionRuntime runtime, long ageMs, long streamEpoch, long frameId)
    {
        InvokePrivateMethod(runtime, "ReportHelperRemoteScreenShareFrameApplied", ageMs, streamEpoch, frameId);
    }

    private static void ReportHelperRemoteFrameApplied(
        SessionRuntime runtime,
        long ageMs,
        long streamEpoch,
        long frameId,
        long visibleHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap)
    {
        InvokePrivateMethod(
            runtime,
            "ReportHelperRemoteScreenShareFrameApplied",
            ageMs,
            streamEpoch,
            frameId,
            visibleHeadFrameId,
            stableVisibleHeadFrameId,
            framesAppliedSinceLastGap);
    }

    private static void ReportHelperRemoteFrameApplied(
        SessionRuntime runtime,
        long ageMs,
        long streamEpoch,
        long frameId,
        long visibleHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap,
        HelperRemoteSessionSnapshot sessionSnapshot)
    {
        InvokePrivateMethod(
            runtime,
            "ReportHelperRemoteScreenShareFrameApplied",
            ageMs,
            streamEpoch,
            frameId,
            visibleHeadFrameId,
            stableVisibleHeadFrameId,
            framesAppliedSinceLastGap,
            sessionSnapshot);
    }

    private static HelperRemoteSessionSnapshot CreateHelperSessionSnapshot(
        long currentEpoch,
        long visibleHeadFrameId,
        long appliedHeadFrameId,
        long stableVisibleHeadFrameId,
        long framesAppliedSinceLastGap,
        long visibleRecoveryFloorFrameId = -1,
        HelperRemoteSessionPhase phase = HelperRemoteSessionPhase.VisibleStable,
        HelperRemoteRecoveryMechanism recoveryMechanism = HelperRemoteRecoveryMechanism.None)
    {
        var provenHeadFrameId = Math.Max(
            Math.Max(visibleHeadFrameId, appliedHeadFrameId),
            Math.Max(stableVisibleHeadFrameId, visibleRecoveryFloorFrameId));
        return new HelperRemoteSessionSnapshot(
            CurrentEpoch: currentEpoch,
            Phase: phase,
            RecoveryMechanism: recoveryMechanism,
            BaselineEstablished: provenHeadFrameId >= 0,
            SteadyVisibleProgressActive:
                phase == HelperRemoteSessionPhase.VisibleStable &&
                recoveryMechanism == HelperRemoteRecoveryMechanism.None &&
                provenHeadFrameId >= 0,
            VisibleHeadFrameId: visibleHeadFrameId,
            AppliedHeadFrameId: appliedHeadFrameId,
            StableVisibleHeadFrameId: stableVisibleHeadFrameId,
            VisibleRecoveryFloorFrameId: visibleRecoveryFloorFrameId,
            ProvenHeadFrameId: provenHeadFrameId,
            FramesAppliedSinceLastGap: framesAppliedSinceLastGap,
            CurrentEpochProgressProven: provenHeadFrameId >= 0,
            CurrentEpochProgressProofSource:
                visibleRecoveryFloorFrameId >= 0 && provenHeadFrameId >= visibleRecoveryFloorFrameId
                    ? "recovery_floor_plus_head"
                    : stableVisibleHeadFrameId >= 0
                        ? "stable_visible_head"
                        : appliedHeadFrameId >= 0
                            ? "applied_head"
                            : visibleHeadFrameId >= 0
                                ? "visible_head"
                                : "none",
            RecoveryActive: recoveryMechanism == HelperRemoteRecoveryMechanism.WaitingForRecoveryKeyframe,
            RecoveryCorridorActive: recoveryMechanism == HelperRemoteRecoveryMechanism.RecoveryCorridor,
            RunwayCleanupActive: recoveryMechanism == HelperRemoteRecoveryMechanism.RunwayCleanup,
            PostRecoveryStabilizationActive: recoveryMechanism == HelperRemoteRecoveryMechanism.FollowerWindow);
    }

    private static void ReportHelperRemoteRecoveryWindowStateChanged(
        SessionRuntime runtime,
        long streamEpoch,
        long recoveryFrameId,
        long lastContiguousFrameId,
        int contiguousFollowerApplyCount,
        string status,
        string? abortReason = null)
    {
        InvokePrivateMethod(
            runtime,
            "ReportHelperRemoteScreenShareRecoveryWindowStateChanged",
            streamEpoch,
            recoveryFrameId,
            lastContiguousFrameId,
            contiguousFollowerApplyCount,
            status,
            abortReason);
    }

    private static void ReportHelperRemoteStaleDrop(SessionRuntime runtime, long renderedAgeMs, long streamEpoch)
    {
        InvokePrivateMethod(runtime, "ReportHelperRemoteScreenShareStaleFrameDropped", renderedAgeMs, streamEpoch);
    }

    private static ScreenSharePressureStateV1 WaitForSinglePressureState(List<ScreenSharePressureStateV1> sentMessages)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (sentMessages.Count == 1)
            {
                return sentMessages[0];
            }

            Thread.Sleep(25);
        }

        return Assert.Single(sentMessages);
    }

    private static ScreenShareRecoveryReceiptV1 WaitForSingleRecoveryReceipt(List<ScreenShareRecoveryReceiptV1> sentMessages)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (sentMessages.Count == 1)
            {
                return sentMessages[0];
            }

            Thread.Sleep(25);
        }

        return Assert.Single(sentMessages);
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(target, value);
    }

    private static object GetPrivateField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsAssignableFrom<object>(field!.GetValue(target));
    }

    private static long GetPrivateLongField(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<long>(field!.GetValue(target));
    }

    private static object GetScreenShareControlHost(SessionRuntime runtime)
    {
        return GetPrivateField(runtime, "screenShareControlHost");
    }

    private static object GetScreenShareControlHostField(SessionRuntime runtime, string fieldName)
    {
        return GetPrivateField(GetScreenShareControlHost(runtime), fieldName);
    }

    private static long GetScreenShareControlHostLongField(SessionRuntime runtime, string fieldName)
    {
        return GetPrivateLongField(GetScreenShareControlHost(runtime), fieldName);
    }

    private static string GetFileTransferFlowControlMode(SessionRuntime runtime)
    {
        var fileTransferService = GetPrivateField(runtime, "fileTransferService");
        var policyField = fileTransferService.GetType().GetField("flowControlPolicy", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(policyField);
        var policy = Assert.IsAssignableFrom<object>(policyField!.GetValue(fileTransferService));
        var modeProperty = policy.GetType().GetProperty("Mode", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(modeProperty);
        return Assert.IsAssignableFrom<object>(modeProperty!.GetValue(policy)).ToString()!;
    }

    private static void WaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            Thread.Sleep(20);
        }

        Assert.True(predicate(), "Condition not met before timeout.");
    }

    private static object? InvokePrivateMethod(object target, string methodName, params object?[] args)
    {
        var methods = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(methods);
        var method = methods.FirstOrDefault(m => m.GetParameters().Length == args.Length) ?? methods[0];
        return method.Invoke(target, args);
    }

    private static object? InvokePublicMethod(object target, string methodName, params object?[] args)
    {
        var methods = target.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(methods);
        var method = methods.FirstOrDefault(m => m.GetParameters().Length == args.Length) ?? methods[0];
        return method.Invoke(target, args);
    }

    private static async Task InvokePrivateAsync(object target, string methodName, params object?[] args)
    {
        var task = Assert.IsAssignableFrom<Task>(InvokePrivateMethod(target, methodName, args));
        await task.ConfigureAwait(false);
    }

#pragma warning disable CS0067
    private sealed class ScreenShareSignalingTransportDouble : ISignalingTransport, IScreenShareSignalingTransport, ISessionSecuritySignalingTransport
    {
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;

        public List<byte[]> SentPayloads { get; } = new();
        public List<ScreenSharePressureStateV1> SentPressureStates { get; } = new();
        public List<ScreenShareRecoveryReceiptV1> SentRecoveryReceipts { get; } = new();
        public List<ScreenShareVideoStreamConfigV1> SentVideoStreamConfigs { get; } = new();
        public List<ScreenShareVideoKeyframeRequestV1> SentVideoKeyframeRequests { get; } = new();

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;
        public event EventHandler<ScreenShareFrameCompletedEventArgs>? ScreenShareFrameCompleted;
        public event EventHandler? ScreenShareStopped;
        public event EventHandler<ScreenSharePressureStateReceivedEventArgs>? ScreenSharePressureStateReceived;
        public event EventHandler<ScreenShareRecoveryReceiptReceivedEventArgs>? ScreenShareRecoveryReceiptReceived;
        public event EventHandler<ScreenShareVideoStreamConfigReceivedEventArgs>? ScreenShareVideoStreamConfigReceived;
        public event EventHandler<ScreenShareVideoKeyframeRequestReceivedEventArgs>? ScreenShareVideoKeyframeRequestReceived;

        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;

        public void Dispose()
        {
        }

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;

        public Task SendScreenSharePayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            SentPayloads.Add(payload.ToArray());
            return Task.CompletedTask;
        }

        public Task SendScreenSharePressureStateAsync(ScreenSharePressureStateV1 message, CancellationToken ct)
        {
            SentPressureStates.Add(message);
            return Task.CompletedTask;
        }

        public Task SendScreenShareRecoveryReceiptAsync(ScreenShareRecoveryReceiptV1 message, CancellationToken ct)
        {
            SentRecoveryReceipts.Add(message);
            return Task.CompletedTask;
        }

        public Task SendScreenShareVideoStreamConfigAsync(ScreenShareVideoStreamConfigV1 message, CancellationToken ct)
        {
            SentVideoStreamConfigs.Add(message);
            return Task.CompletedTask;
        }

        public Task SendScreenShareVideoKeyframeRequestAsync(ScreenShareVideoKeyframeRequestV1 message, CancellationToken ct)
        {
            SentVideoKeyframeRequests.Add(message);
            return Task.CompletedTask;
        }

        public void RaiseScreenShareFrameCompleted(ScreenShareFrameCompletedEventArgs e)
        {
            ScreenShareFrameCompleted?.Invoke(this, e);
        }

        public void RaiseScreenShareStopped()
        {
            ScreenShareStopped?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseScreenShareRecoveryReceiptReceived(ScreenShareRecoveryReceiptV1 message)
        {
            ScreenShareRecoveryReceiptReceived?.Invoke(this, new ScreenShareRecoveryReceiptReceivedEventArgs(message, peerId: "screenshare-double-peer"));
        }

        public void SetSessionSecurityStateForTests(SessionSecurityState nextState)
        {
            currentSessionSecurityState = nextState;
            SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
        }
    }

    private class ScreenShareAwareSignalingTransportDouble : ISignalingTransport, IRemoteControlSignalingTransport, IScreenShareSignalingTransport, ISessionSecuritySignalingTransport, IScreenShareTransportBackpressureProbe
    {
        private SessionSecurityState currentSessionSecurityState = SessionSecurityState.Empty;

        public List<byte[]> SentScreenSharePayloads { get; } = new();
        public List<ControlDisplayInfoMessageV1> SentDisplayInfoMessages { get; } = new();
        public List<ScreenSharePressureStateV1> SentPressureStates { get; } = new();
        public List<ScreenShareRecoveryReceiptV1> SentRecoveryReceipts { get; } = new();
        public List<ScreenShareVideoStreamConfigV1> SentVideoStreamConfigs { get; } = new();
        public List<ScreenShareVideoKeyframeRequestV1> SentVideoKeyframeRequests { get; } = new();
        public long RecentHealthIssueCount { get; set; }
        public bool IsHealthSeverelyDegraded { get; set; }
        public bool IsCongested { get; set; }
        public bool IsSeverelyCongested { get; set; }
        public int QueueDepth { get; set; }
        public int QueuedBytes { get; set; }
        public long OldestQueuedAgeMs { get; set; }
        public long RecentDropCount { get; set; }

        public event EventHandler<IncomingJoinRequestEventArgs>? IncomingJoinRequest;
        public event EventHandler<TransportSessionKeyReadyEventArgs>? SessionKeyReady;
        public event EventHandler<TransportChatMessageEventArgs>? ChatMessageReceived;
        public event EventHandler? Approved;
        public event EventHandler? Rejected;
        public event EventHandler? Disconnected;
        public event EventHandler<TransportSessionSecurityStateChangedEventArgs>? SessionSecurityStateChanged;
        public event EventHandler<RemoteControlRequestReceivedEventArgs>? RemoteControlRequestReceived;
        public event EventHandler<RemoteControlResponseReceivedEventArgs>? RemoteControlResponseReceived;
        public event EventHandler<RemoteControlStartReceivedEventArgs>? RemoteControlStartReceived;
        public event EventHandler<RemoteControlStopReceivedEventArgs>? RemoteControlStopReceived;
        public event EventHandler<RemoteControlInputReceivedEventArgs>? RemoteControlInputReceived;
        public event EventHandler<RemoteControlAckReceivedEventArgs>? RemoteControlAckReceived;
        public event EventHandler<RemoteControlStateSnapshotReceivedEventArgs>? RemoteControlStateSnapshotReceived;
        public event EventHandler<RemoteControlDisplayInfoReceivedEventArgs>? RemoteControlDisplayInfoReceived;
        public event EventHandler<ScreenShareFrameCompletedEventArgs>? ScreenShareFrameCompleted;
        public event EventHandler? ScreenShareStopped;
        public event EventHandler<ScreenSharePressureStateReceivedEventArgs>? ScreenSharePressureStateReceived;
        public event EventHandler<ScreenShareRecoveryReceiptReceivedEventArgs>? ScreenShareRecoveryReceiptReceived;
        public event EventHandler<ScreenShareVideoStreamConfigReceivedEventArgs>? ScreenShareVideoStreamConfigReceived;
        public event EventHandler<ScreenShareVideoKeyframeRequestReceivedEventArgs>? ScreenShareVideoKeyframeRequestReceived;

        public SessionSecurityState CurrentSessionSecurityState => currentSessionSecurityState;
        public bool IsScreenShareTransportCongested => IsCongested;
        public bool IsScreenShareTransportSeverelyCongested => IsSeverelyCongested;
        public int ScreenShareTransportQueueDepth => QueueDepth;
        public int ScreenShareTransportQueuedBytes => QueuedBytes;
        public long ScreenShareTransportOldestQueuedAgeMs => OldestQueuedAgeMs;
        public long ScreenShareTransportRecentDropCount => RecentDropCount;
        public long ScreenShareTransportRecentHealthIssueCount => RecentHealthIssueCount;
        public bool IsScreenShareTransportHealthSeverelyDegraded => IsHealthSeverelyDegraded;

        public void Dispose()
        {
        }

        public Task SendChatMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken ct) => Task.CompletedTask;

        public Task SendScreenSharePayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken ct)
        {
            SentScreenSharePayloads.Add(payload.ToArray());
            return Task.CompletedTask;
        }

        public Task SendScreenSharePressureStateAsync(ScreenSharePressureStateV1 message, CancellationToken ct)
        {
            SentPressureStates.Add(message);
            return Task.CompletedTask;
        }

        public Task SendScreenShareRecoveryReceiptAsync(ScreenShareRecoveryReceiptV1 message, CancellationToken ct)
        {
            SentRecoveryReceipts.Add(message);
            return Task.CompletedTask;
        }

        public Task SendScreenShareVideoStreamConfigAsync(ScreenShareVideoStreamConfigV1 message, CancellationToken ct)
        {
            SentVideoStreamConfigs.Add(message);
            return Task.CompletedTask;
        }

        public Task SendScreenShareVideoKeyframeRequestAsync(ScreenShareVideoKeyframeRequestV1 message, CancellationToken ct)
        {
            SentVideoKeyframeRequests.Add(message);
            return Task.CompletedTask;
        }

        public Task SendControlRequestAsync(ControlRequestMessageV1 message, CancellationToken ct) => Task.CompletedTask;
        public Task SendControlResponseAsync(ControlResponseMessageV1 message, CancellationToken ct) => Task.CompletedTask;
        public Task SendControlStartAsync(ControlStartMessageV1 message, CancellationToken ct) => Task.CompletedTask;
        public Task SendControlStopAsync(ControlStopMessageV1 message, CancellationToken ct) => Task.CompletedTask;
        public Task SendControlInputAsync(ControlInputMessageV1 message, CancellationToken ct) => Task.CompletedTask;
        public Task SendControlAckAsync(ControlInputAckV1 message, CancellationToken ct) => Task.CompletedTask;
        public Task SendControlStateSnapshotAsync(ControlStateSnapshotV1 message, CancellationToken ct) => Task.CompletedTask;

        public Task SendControlDisplayInfoAsync(ControlDisplayInfoMessageV1 message, CancellationToken ct)
        {
            SentDisplayInfoMessages.Add(message);
            return Task.CompletedTask;
        }

        public void RaiseScreenShareFrameCompleted(ScreenShareFrameCompletedEventArgs e)
        {
            ScreenShareFrameCompleted?.Invoke(this, e);
        }

        public void RaiseScreenShareStopped()
        {
            ScreenShareStopped?.Invoke(this, EventArgs.Empty);
        }

        public void RaiseScreenShareRecoveryReceiptReceived(ScreenShareRecoveryReceiptV1 message)
        {
            ScreenShareRecoveryReceiptReceived?.Invoke(this, new ScreenShareRecoveryReceiptReceivedEventArgs(message, peerId: "screenshare-aware-double-peer"));
        }

        public void SetSessionSecurityStateForTests(SessionSecurityState nextState)
        {
            currentSessionSecurityState = nextState;
            SessionSecurityStateChanged?.Invoke(this, new TransportSessionSecurityStateChangedEventArgs(nextState));
        }
    }

    private sealed class BridgePolicyCapabilityClient : INknClient, IAuthoritativeConnectedAddressSource, IBridgeScreenShareQueueCapability
    {
        public List<(BridgeScreenShareQueueMode Mode, long Generation, bool FlushQueued)> PolicyApplications { get; } = new();
        public List<(string Destination, byte[] Payload)> SentMessages { get; } = new();

        public bool IsBridgeProcessRunning { get; set; }

        public string Address => "bridge.policy.control";

        public string MediaAddress => "bridge.policy.media";

        public string BulkAddress => "bridge.policy.bulk";

        public BridgeScreenShareQueueState CurrentScreenShareQueueState =>
            new(
                QueueDepth: 0,
                QueuedBytes: 0,
                OldestQueuedAgeMs: 0,
                InFlight: false,
                DroppedSinceLast: 0,
                IsCongested: false,
                IsSevere: false,
                Mode: BridgeScreenShareQueueMode.Normal);

        public BridgeScreenShareHealthState CurrentScreenShareHealthState =>
            new(
                RecentIssueCount: 0,
                IsSevere: false,
                OldestIssueAgeMs: 0);

        bool IAuthoritativeConnectedAddressSource.HasAuthoritativeConnectedAddress => true;

        public event EventHandler<NknIncomingMessage>? MessageReceived;
        public event EventHandler? Disconnected;
        public event EventHandler<BridgeScreenShareQueueStateChangedEventArgs>? ScreenShareQueueStateChanged;

        public void Dispose()
        {
        }

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;

        public Task DisconnectAsync() => Task.CompletedTask;

        public Task SubscribeAsync(string topic, CancellationToken ct) => Task.CompletedTask;

        public Task UnsubscribeAsync(string topic) => Task.CompletedTask;

        public Task PublishAsync(string topic, byte[] payload, CancellationToken ct) => Task.CompletedTask;

        public Task SendAsync(string destination, byte[] payload, CancellationToken ct)
        {
            SentMessages.Add((destination, payload));
            return Task.CompletedTask;
        }

        public Task SendMediaAsync(string destination, byte[] payload, CancellationToken ct) => Task.CompletedTask;

        public Task SendBulkAsync(string destination, byte[] payload, CancellationToken ct) => Task.CompletedTask;

        public Task SetScreenSharePolicyAsync(BridgeScreenShareQueueMode mode, long generation, bool flushQueued, CancellationToken ct)
        {
            PolicyApplications.Add((mode, generation, flushQueued));
            return Task.CompletedTask;
        }
    }

    private sealed class ScreenSharePolicyAwareTransportDouble :
        ScreenShareAwareSignalingTransportDouble,
        IScreenShareTransportPolicyController
    {
        public List<bool> PolicyUpdates { get; } = new();
        public List<string> QueueFlushReasons { get; } = new();

        public Task SetScreenShareTransportCatchUpOnlyAsync(bool active, CancellationToken ct)
        {
            PolicyUpdates.Add(active);
            return Task.CompletedTask;
        }

        public void FlushScreenShareTransportQueue(string reason)
        {
            QueueFlushReasons.Add(reason);
        }
    }
#pragma warning restore CS0067
}
