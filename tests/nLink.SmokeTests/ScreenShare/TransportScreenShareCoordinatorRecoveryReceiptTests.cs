using System.Runtime.InteropServices;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NLink.App.Configuration;
using NLink.App.Services.ScreenCapture;
using NLink.App.Views;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;
using NLink.Infra.Nkn;
using NLink.SmokeTests.Fakes;
using System.Collections.Concurrent;

namespace NLink.SmokeTests;

[Collection(FakeNknNetworkCollection.Name)]
[Trait("Area", "ScreenShare")]
public sealed class TransportScreenShareCoordinatorRecoveryReceiptTests : ScreenShareCoordinatorTestBase, IClassFixture<ScreenShareCoordinatorFixture>
{
    public TransportScreenShareCoordinatorRecoveryReceiptTests(ScreenShareCoordinatorFixture fixture) : base(fixture) { }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_SimplifiedRecovery_HelperVisibleReceipt_CompletesBurstAndStartsPostAckHold()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 20, 9, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-simplified-recovery-ack", CancellationToken.None), TimeSpan.FromSeconds(2), "simplified recovery ack start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 70, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        Assert.Equal(71L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch"));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x01, 0x02, 0x03 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: 71, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(140));
        coordinator.SetRemoteRecoveryReceipt(new ScreenShareRecoveryReceiptV1 { SessionId = "session-simplified-recovery-ack", StreamEpoch = 71, OwnerFrameId = recoveryOwnerFrameId, VisibleRecoveryFrameId = recoveryOwnerFrameId, VisibleHeadFrameId = recoveryOwnerFrameId, ReceiptKind = ScreenShareRecoveryReceiptCodec.RecoveryKeyframeVisibleReceiptKind, });
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("PostAckHold", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal("helper_ack", GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryCompletionKind"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryOwnerFrameId"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryAckFrameId"));
        Assert.Equal("helper_visible_receipt", GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryAckSource"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldStartedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldExpiredCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByHelperAckCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByHelperVisibleReceiptCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByTimeoutCount"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "lastRemoteRecoveryReceiptOwnerFrameId"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "lastRemoteRecoveryReceiptVisibleRecoveryFrameId"));
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_HelperVisibleReceipt_WrongOwner_IsRejectedWithDiagnostics()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 20, 9, 5, 20, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-receipt-wrong-owner", CancellationToken.None), TimeSpan.FromSeconds(2), "receipt wrong owner start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 80, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        Assert.Equal(81L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch"));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x01, 0x02, 0x03 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: 81, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        coordinator.SetRemoteRecoveryReceipt(new ScreenShareRecoveryReceiptV1 { SessionId = "session-receipt-wrong-owner", StreamEpoch = 81, OwnerFrameId = recoveryOwnerFrameId + 1, VisibleRecoveryFrameId = recoveryOwnerFrameId + 1, VisibleHeadFrameId = recoveryOwnerFrameId + 1, ReceiptKind = ScreenShareRecoveryReceiptCodec.RecoveryKeyframeVisibleReceiptKind, });
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("OwnerEmittedAwaitingHelperAck", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByHelperVisibleReceiptCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "remoteRecoveryReceiptRejectedCount"));
        Assert.Equal("wrong_owner_frame", GetPrivateFieldValue<string>(coordinator, "lastRemoteRecoveryReceiptRejectReason"));
        Assert.Equal(81L, GetPrivateFieldValue<long>(coordinator, "lastRemoteRecoveryReceiptRejectActiveStreamEpoch"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "lastRemoteRecoveryReceiptRejectActiveOwnerFrameId"));
        Assert.Equal("owner_emitted_awaiting_helper_ack", GetPrivateFieldValue<string>(coordinator, "lastRemoteRecoveryReceiptRejectActivePhase"));
        Assert.Equal(recoveryOwnerFrameId + 1, GetPrivateFieldValue<long>(coordinator, "lastRemoteRecoveryReceiptOwnerFrameId"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "recoveryAckSource"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_HelperAckStartsSettleHold_AndFollowerProgressResumesFreshFrames()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var sentPayloadCount = 0;
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 6, 10, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) =>
        {
            Interlocked.Increment(ref sentPayloadCount);
            return Task.CompletedTask;
        }, clock: clock);
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod("OnAutoTuneTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-post-owner-resume", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst post-owner resume start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 706, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0xA1, 0xA2, 0xA3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        for (var i = 0; i < 4; i++)
        {
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { (byte)(0xB0 + i), (byte)(0xC0 + i), (byte)(0xD0 + i) }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: false));
        }

        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerUnackedNonKeyHeldCount") >= 1, TimeSpan.FromSeconds(2));
        DeliverMatchingHelperVisibleReceipt(coordinator, "session-recovery-burst-post-owner-resume", recoveryBurstStreamEpoch, recoveryOwnerFrameId);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("PostAckHold", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldStartedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldExpiredCount"));
        var sentPayloadCountBeforeResume = Volatile.Read(ref sentPayloadCount);
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0xE1, 0xE2, 0xE3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: 706, isKeyFrame: false));
        await Task.Delay(100);
        Assert.Equal(sentPayloadCountBeforeResume, Volatile.Read(ref sentPayloadCount));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonHealthy, observedFrameAgeMs: 0, recentStaleDrops: 0, lastVisibleApplyFrameId: recoveryOwnerFrameId + 1, visibleHeadFrameId: recoveryOwnerFrameId + 1, appliedHeadFrameId: recoveryOwnerFrameId + 1, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: recoveryOwnerFrameId + 1, framesAppliedSinceLastGap: 5);
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0xF1, 0xF2, 0xF3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: 706, isKeyFrame: false));
        await Task.Delay(100);
        Assert.True(Volatile.Read(ref sentPayloadCount) <= sentPayloadCountBeforeResume + 1);
        clock.Advance(TimeSpan.FromMilliseconds(450));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        await WaitUntilAsync(() => Volatile.Read(ref sentPayloadCount) > sentPayloadCountBeforeResume, TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.True(Volatile.Read(ref sentPayloadCount) > sentPayloadCountBeforeResume);
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryOwnerUnackedNonKeyHeldActive"));
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldExpiredCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_HigherEpochRequestAfterOwnerEmit_DoesNotReplaceBurst_AndReceiptCompletesOriginal()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 18, 56, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod("OnAutoTuneTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-freeze-after-owner-emit", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst freeze after owner emit start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 40, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var originalBurstEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x11, 0x12, 0x13 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: originalBurstEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: originalBurstEpoch + 1, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame("frame_gap_reassembler");
        Assert.Equal(originalBurstEpoch, GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryEpochTakeoverSuppressedAfterOwnerEmitCount"));
        Assert.Equal(originalBurstEpoch, GetPrivateFieldValue<long>(coordinator, "lastRecoveryEpochTakeoverSuppressedFromEpoch"));
        Assert.Equal(originalBurstEpoch + 1, GetPrivateFieldValue<long>(coordinator, "lastRecoveryEpochTakeoverSuppressedToEpoch"));
        Assert.Equal("owner_emitted_awaiting_helper_ack", GetPrivateFieldValue<string>(coordinator, "lastRecoveryEpochTakeoverSuppressedPhase"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstProfileTransitionTakeoverCount"));
        DeliverMatchingHelperVisibleReceipt(coordinator, "session-recovery-burst-freeze-after-owner-emit", originalBurstEpoch, recoveryOwnerFrameId);
        await WaitUntilAsync(() => string.Equals(GetPrivateFieldValue<string>(coordinator, "recoveryAckSource"), "helper_visible_receipt", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
        Assert.Equal("PostAckHold", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        clock.Advance(TimeSpan.FromMilliseconds(450));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByHelperVisibleReceiptCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByTimeoutCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "remoteRecoveryReceiptRejectedCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_ProtectedFramesDoNotCompleteBurst()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 8, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-protected-frames", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst protected frames start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 10, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 1, 2, 3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: true));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 4, 5, 6 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: false));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 7, 8, 9 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: false));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByProtectedFramesCount"));
        Assert.InRange(GetPrivateFieldValue<int>(coordinator, "recoveryProtectedFollowerCount"), 0, 2);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_ProtectedFollowersArePreservedBeforeHelperAck()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 19, 8, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-corridor-ack", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst corridor ack start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 721, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x11, 0x12, 0x13 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x21, 0x22, 0x23 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: false));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x31, 0x32, 0x33 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: false));
        await Task.Delay(100);
        Assert.InRange(GetPrivateFieldValue<int>(coordinator, "recoveryProtectedFollowerCount"), 0, 2);
        DeliverMatchingHelperVisibleReceipt(coordinator, "session-recovery-burst-corridor-ack", recoveryBurstStreamEpoch, recoveryOwnerFrameId, visibleRecoveryFrameId: recoveryOwnerFrameId + 1, visibleHeadFrameId: recoveryOwnerFrameId + 1);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("PostAckHold", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByHelperAckCount"));
        Assert.Equal(recoveryOwnerFrameId + 1, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckFrameId"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryOwnerKeyframe_StillBatchesWhenFit()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 23, 10, 30, 0, TimeSpan.Zero));
        var sentPayloads = new ConcurrentQueue<(byte[] Payload, string? Role, long BurstToken)>();
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock, estimateBridgeBytes: payload => NknBridgePayloadAccounting.MeasureSendFrameBytes(destination: "peer.test", payload.Span), sendPayloadWithRecoveryMetadataAsync: (payload, recoverySendRole, recoveryBurstTransportFallbackToken, _) =>
        {
            sentPayloads.Enqueue((payload.ToArray(), recoverySendRole, recoveryBurstTransportFallbackToken));
            return Task.CompletedTask;
        });
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-owner-batch", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery owner batch start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 12, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        var frameBytes = Enumerable.Range(0, ScreenShareVideoFragmenter.MaxFragmentRawBytes + 137).Select(i => (byte)(i % 251)).ToArray();
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(1280, 720, frameBytes, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => sentPayloads.Any(payload => string.Equals(payload.Role, "owner", StringComparison.Ordinal)), TimeSpan.FromSeconds(2));
        var sentPayloadSnapshot = sentPayloads.ToArray();
        var metrics = coordinator.GetMetricsSnapshot();
        Assert.Contains(sentPayloadSnapshot, payload => string.Equals(payload.Role, "owner", StringComparison.Ordinal));
        Assert.Contains(sentPayloadSnapshot, payload => ExpandFragmentsFromPayload(payload.Payload).Length > 1);
        Assert.True(metrics.BatchedPayloadsSent > 0);
        Assert.True(metrics.KeyframeOrRecoveryBatchedPayloadsSent > 0);
        Assert.Equal(0, metrics.OrdinaryNonKeyBatchedPayloadsSent);
        Assert.Equal(0, metrics.OrdinaryNonKeyLegacyPayloadsSent);
    }

}
