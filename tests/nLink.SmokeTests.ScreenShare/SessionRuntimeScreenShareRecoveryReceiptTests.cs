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
public sealed class SessionRuntimeScreenShareRecoveryReceiptTests : ScreenShareTransportBoundaryTestBase
{
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

}
