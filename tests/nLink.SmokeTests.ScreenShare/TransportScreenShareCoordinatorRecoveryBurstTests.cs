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

[Collection(AvaloniaHeadlessUiCollection.Name)]
[Trait("Area", "ScreenShare")]
public sealed class TransportScreenShareCoordinatorRecoveryBurstTests : ScreenShareCoordinatorTestBase, IClassFixture<ScreenShareCoordinatorFixture>
{
    public TransportScreenShareCoordinatorRecoveryBurstTests(ScreenShareCoordinatorFixture fixture) : base(fixture) { }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ContinuityLossKeyframeRequest_StartsRecoveryBurstAndRecordsGapToRequestLatency()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-start", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 5, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0);
        clock.Advance(TimeSpan.FromMilliseconds(120));
        var expectedTransportTuningLevel = GetPrivateFieldValue<ScreenShareTransportTuningLevel>(coordinator, "transportTuningLevel");
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch") > 5L);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryGapCount"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryGapToKeyframeRequestMs") >= 120L);
        Assert.Contains(ScreenSharePressureProtocol.PressureReasonContinuityLoss, fakeSource.KeyFrameRequestReasons);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurstStart_FlushesTransportQueue_AndPurgesPendingRawFrames()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var flushedTransportQueueCount = 0;
        string? flushedTransportQueueReason = null;
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 0, 30, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock, flushTransportQueue: reason =>
        {
            flushedTransportQueueCount++;
            flushedTransportQueueReason = reason;
        });
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-start-flush", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst start flush");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 55, PendingRawFrameCount: 2, OldestPendingRawFrameAgeMs: 180, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(1, flushedTransportQueueCount);
        Assert.Equal("recovery_burst_start", flushedTransportQueueReason);
        Assert.Equal(1, fakeSource.PurgePendingRawFramesCallCount);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_FrameGapReassemblerKeyframeRequest_StartsRecoveryBurst()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 1, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-frame-gap", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst frame-gap start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 6, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0);
        coordinator.RequestKeyFrame("frame_gap_reassembler");
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch") > 6L);
        Assert.Contains("frame_gap_reassembler", fakeSource.KeyFrameRequestReasons);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_DoesNotCompleteOnStableHeadAlone()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-complete", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst complete start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 7, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpochForStableHeadOnly = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(new ScreenCaptureFrameEventArgs(640, 360, new byte[] { 0x01, 0x02, 0x03 }, "h264", capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: true, streamEpoch: recoveryEpochForStableHeadOnly));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromMilliseconds(140));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonHealthy, observedFrameAgeMs: 0, recentStaleDrops: 0, appliedHeadFrameId: 1, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: 1, framesAppliedSinceLastGap: 4);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("OwnerEmittedAwaitingHelperAck", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldStartedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedCount"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckFrameId"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerEmitToFirstVisibleApplyMs"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_DoesNotCompleteOnAppliedHeadAlone()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 15, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-applied-ack", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst applied ack start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 70, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpochForAppliedOnly = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x01, 0x02, 0x03 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryEpochForAppliedOnly, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(140));
        coordinator.SetRemotePressureState(mode: ScreenShareRemotePressureMode.None, reason: ScreenSharePressureProtocol.PressureReasonHealthy, observedFrameAgeMs: 0, recentStaleDrops: 0, lastVisibleApplyFrameId: null, appliedHeadFrameId: recoveryOwnerFrameId, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: recoveryOwnerFrameId, framesAppliedSinceLastGap: 4);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("OwnerEmittedAwaitingHelperAck", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldStartedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByAppliedHeadAckCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByVisibleRecoveryFloorCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByVisibleApplyFallbackCount"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckFrameId"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_CompletesOnVisibleApplyFallbackAfterRecoveryKeyframeApply()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 20, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-last-visible-ack", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst last visible ack start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 701, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpochForVisibleFallback = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x01, 0x02, 0x03 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryEpochForVisibleFallback, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(140));
        coordinator.SetRemotePressureState(mode: ScreenShareRemotePressureMode.None, reason: ScreenSharePressureProtocol.PressureReasonHealthy, observedFrameAgeMs: 0, recentStaleDrops: 0, lastVisibleApplyFrameId: recoveryOwnerFrameId, appliedHeadFrameId: recoveryOwnerFrameId > 0 ? recoveryOwnerFrameId - 1 : null, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: recoveryOwnerFrameId, framesAppliedSinceLastGap: 4, currentEpochRecoveryKeyframeApplyCount: 1);
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldStartedCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByVisibleApplyFallbackCount"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckFrameId"));
        Assert.Equal("visible_apply_fallback", GetPrivateFieldValue<string>(coordinator, "recoveryAckSource"));
        Assert.Equal(recoveryEpochForVisibleFallback, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryEpoch"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryOwnerFrameId"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryAckFrameId"));
        Assert.Equal("visible_apply_fallback", GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryAckSource"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_CompletesOnVisibleRecoveryFloorWithoutReceipt()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 24, 10, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-visible-floor", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst visible floor start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 708, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x31, 0x32, 0x33 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(140));
        coordinator.SetRemotePressureState(mode: ScreenShareRemotePressureMode.None, reason: ScreenSharePressureProtocol.PressureReasonHealthy, observedFrameAgeMs: 0, recentStaleDrops: 0, lastVisibleApplyFrameId: recoveryOwnerFrameId, visibleHeadFrameId: recoveryOwnerFrameId, appliedHeadFrameId: recoveryOwnerFrameId, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: recoveryOwnerFrameId, framesAppliedSinceLastGap: 1, visibleRecoveryFloorFrameId: recoveryOwnerFrameId);
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByVisibleRecoveryFloorCount"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckFrameId"));
        Assert.Equal("visible_recovery_floor", GetPrivateFieldValue<string>(coordinator, "recoveryAckSource"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "satisfiedRecoveryFloorFrameId"));
        Assert.Equal("helper_ack_frame", GetPrivateFieldValue<string>(coordinator, "satisfiedRecoveryFloorSource"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_DoesNotCompleteOnVisibleApplyFallbackWhileRemotePressureStillHighFrameAge()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 22, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-applied-ack-high-age", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst applied ack high-age start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 705, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpochForHighAge = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x01, 0x02, 0x03 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryEpochForHighAge, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(140));
        coordinator.SetRemotePressureState(mode: ScreenShareRemotePressureMode.ReduceFps, reason: ScreenSharePressureProtocol.PressureReasonHighFrameAge, observedFrameAgeMs: 1400, recentStaleDrops: 0, lastVisibleApplyFrameId: recoveryOwnerFrameId, appliedHeadFrameId: recoveryOwnerFrameId, steadyVisibleProgressActive: false, stableVisibleHeadFrameId: null, framesAppliedSinceLastGap: 0, currentEpochRecoveryKeyframeApplyCount: 1);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("OwnerEmittedAwaitingHelperAck", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldStartedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByVisibleApplyFallbackCount"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckFrameId"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "recoveryAckSource"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerEmitToAckMs"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_DoesNotCompleteOnVisibleApplyFallbackWhileRemotePressureStillContinuityLoss()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 23, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-applied-ack-continuity", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst applied ack continuity start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 706, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpochForContinuityLoss = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x21, 0x22, 0x23 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryEpochForContinuityLoss, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(140));
        coordinator.SetRemotePressureState(mode: ScreenShareRemotePressureMode.None, reason: ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0, lastVisibleApplyFrameId: recoveryOwnerFrameId, appliedHeadFrameId: recoveryOwnerFrameId, steadyVisibleProgressActive: false, stableVisibleHeadFrameId: recoveryOwnerFrameId, framesAppliedSinceLastGap: 0, currentEpochRecoveryKeyframeApplyCount: 1);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("OwnerEmittedAwaitingHelperAck", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldStartedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByVisibleApplyFallbackCount"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckFrameId"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "recoveryAckSource"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_LastCompletedHelperAckPersistsAcrossLaterRestart()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 25, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-last-completed-persist", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst last completed persist start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 702, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpochForPersistedAck = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x01, 0x02, 0x03 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryEpochForPersistedAck, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(140));
        DeliverMatchingHelperVisibleReceipt(coordinator, "session-recovery-burst-last-completed-persist", recoveryEpochForPersistedAck, recoveryOwnerFrameId);
        Assert.Equal(recoveryEpochForPersistedAck, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryEpoch"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryOwnerFrameId"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryAckFrameId"));
        Assert.Equal("helper_visible_receipt", GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryAckSource"));
        Assert.Equal("helper_ack", GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryCompletionKind"));
        clock.Advance(TimeSpan.FromMilliseconds(450));
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod("OnAutoTuneTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        coordinator.RequestKeyFrame("frame_gap_reassembler");
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch") > recoveryEpochForPersistedAck);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryEpoch"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryOwnerFrameId"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "lastCompletedRecoveryAckFrameId"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryAckSource"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryCompletionKind"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckFrameId"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerEmitToAckMs"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckWindowMs"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerEmitToFirstVisibleApplyMs"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "recoveryAckSource"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "helperAckAfterFactSendMs"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_SameEpochFrameGapAfterHelperAck_IsSuppressed()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 30, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-stale-suppress", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst stale suppress start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 71, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x01, 0x02, 0x03 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(140));
        DeliverMatchingHelperVisibleReceipt(coordinator, "session-recovery-burst-stale-suppress", recoveryBurstStreamEpoch, recoveryOwnerFrameId);
        coordinator.RequestKeyFrame("frame_gap_reassembler");
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("PostAckHold", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(1, fakeSource.KeyFrameRequestReasons.Count);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstStaleRequestSuppressedCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstRequestSuppressedDueToHelperAckCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldSuppressedReopenCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_SameEpochFrameGapAfterHelperAckAndContinuitySignal_IsStillSuppressed()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 35, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-stale-suppress-continuity", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst stale suppress continuity start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 710, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x11, 0x12, 0x13 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(140));
        DeliverMatchingHelperVisibleReceipt(coordinator, "session-recovery-burst-stale-suppress-continuity", recoveryBurstStreamEpoch, recoveryOwnerFrameId);
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0);
        coordinator.RequestKeyFrame("frame_gap_reassembler");
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("PostAckHold", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(1, fakeSource.KeyFrameRequestReasons.Count);
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "helperVisibleHeadFrameId"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "helperLastVisibleApplyFrameId"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstStaleRequestSuppressedCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstRequestSuppressedDueToHelperAckCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldSuppressedReopenCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_StreamConfigMissing_RequestsKeyframeWithoutStartingRecoveryBurst()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 33, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-stream-config-missing-no-burst", CancellationToken.None), TimeSpan.FromSeconds(2), "stream config missing no burst start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 7021, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame("stream_config_missing");
        Assert.Single(fakeSource.KeyFrameRequestReasons);
        Assert.Equal("stream_config_missing", fakeSource.KeyFrameRequestReasons[0]);
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_PreOwnerSameEpochNonKeyFrames_AreHeldUntilOwnerExists()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var sentPayloadCount = 0;
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 35, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) =>
        {
            Interlocked.Increment(ref sentPayloadCount);
            return Task.CompletedTask;
        }, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-pre-owner-hold", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst pre-owner hold start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 703, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x11, 0x12, 0x13 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: false));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x21, 0x22, 0x23 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: false));
        await Task.Delay(100);
        Assert.Equal(0, Volatile.Read(ref sentPayloadCount));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerPendingNonKeyHeldCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerPendingNonKeyReplacedCount"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId"));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x31, 0x32, 0x33 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => Volatile.Read(ref sentPayloadCount) > 0, TimeSpan.FromSeconds(2));
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryOwnerPendingNonKeyHeldActive"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_PostOwnerSameEpochNonKeyFrames_BeyondFirstTwo_AreHeldUntilAck()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var sentPayloadCount = 0;
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 40, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) =>
        {
            Interlocked.Increment(ref sentPayloadCount);
            return Task.CompletedTask;
        }, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-post-owner-hold", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst post-owner hold start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 704, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x41, 0x42, 0x43 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        for (var i = 0; i < 4; i++)
        {
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { (byte)(0x50 + i), (byte)(0x60 + i), (byte)(0x70 + i) }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: false));
        }

        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerUnackedNonKeyHeldCount") >= 1 && GetPrivateFieldValue<long>(coordinator, "recoveryOwnerUnackedNonKeyReplacedCount") >= 1, TimeSpan.FromSeconds(2));
        await Task.Delay(100);
        Assert.InRange(Volatile.Read(ref sentPayloadCount), 1, 3);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerUnackedNonKeyHeldCount"));
        Assert.Equal(3L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerUnackedNonKeyReplacedCount"));
        Assert.InRange(GetPrivateFieldValue<int>(coordinator, "recoveryProtectedFollowerCount"), 0, 2);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_SettleTimeoutClearsHoldAndLease()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 19, 8, 30, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod("OnAutoTuneTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-settle-timeout", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst settle timeout start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 7061, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x01, 0x02, 0x03 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        DeliverMatchingHelperVisibleReceipt(coordinator, "session-recovery-burst-settle-timeout", recoveryBurstStreamEpoch, recoveryOwnerFrameId);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldStartedCount"));
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        clock.Advance(TimeSpan.FromMilliseconds(450));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryPostAckHoldExpiredCount"));
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_HighFrameAgeDuringOwnerAckWithFreshProgress_IsSuppressed()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var sentPayloadCount = 0;
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 6, 25, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) =>
        {
            Interlocked.Increment(ref sentPayloadCount);
            return Task.CompletedTask;
        }, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-owner-ack-age", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst owner ack age start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 707, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(180));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { (byte)(0x10 + i), (byte)(0x20 + i), (byte)(0x30 + i) }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: 707, isKeyFrame: false));
            await Task.Delay(75);
        }

        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        clock.Advance(TimeSpan.FromMilliseconds(180));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0xF1, 0xF2, 0xF3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonHealthy, observedFrameAgeMs: 0, recentStaleDrops: 0, lastVisibleApplyFrameId: recoveryOwnerFrameId, visibleHeadFrameId: recoveryOwnerFrameId, appliedHeadFrameId: recoveryOwnerFrameId, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: recoveryOwnerFrameId, framesAppliedSinceLastGap: 4);
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.ReduceFps, ScreenSharePressureProtocol.PressureReasonHighFrameAge, observedFrameAgeMs: 900, recentStaleDrops: 0, lastVisibleApplyFrameId: recoveryOwnerFrameId, visibleHeadFrameId: recoveryOwnerFrameId, appliedHeadFrameId: recoveryOwnerFrameId, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: recoveryOwnerFrameId, framesAppliedSinceLastGap: 4);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(ScreenShareRemotePressureMode.None, GetPrivateFieldValue<ScreenShareRemotePressureMode>(coordinator, "remotePressureMode"));
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonHealthy, GetPrivateFieldValue<string>(coordinator, "remotePressureReason"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "highFrameAgeSuppressedDuringOwnerAckCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_VisibleProofCompletion_SuppressesImmediatePostRecoveryHighFrameAge()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 24, 10, 15, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-visible-proof-post-recovery-grace", CancellationToken.None), TimeSpan.FromSeconds(2), "visible proof post recovery grace start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 709, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x41, 0x42, 0x43 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(120));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonHealthy, observedFrameAgeMs: 0, recentStaleDrops: 0, lastVisibleApplyFrameId: recoveryOwnerFrameId, visibleHeadFrameId: recoveryOwnerFrameId, appliedHeadFrameId: recoveryOwnerFrameId, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: recoveryOwnerFrameId, framesAppliedSinceLastGap: 4, visibleRecoveryFloorFrameId: recoveryOwnerFrameId);
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        clock.Advance(TimeSpan.FromMilliseconds(120));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.ReduceFps, ScreenSharePressureProtocol.PressureReasonHighFrameAge, observedFrameAgeMs: 900, recentStaleDrops: 0, lastVisibleApplyFrameId: recoveryOwnerFrameId, visibleHeadFrameId: recoveryOwnerFrameId, appliedHeadFrameId: recoveryOwnerFrameId, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: recoveryOwnerFrameId, framesAppliedSinceLastGap: 5, visibleRecoveryFloorFrameId: recoveryOwnerFrameId);
        Assert.Equal(ScreenShareRemotePressureMode.None, GetPrivateFieldValue<ScreenShareRemotePressureMode>(coordinator, "remotePressureMode"));
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonHealthy, GetPrivateFieldValue<string>(coordinator, "remotePressureReason"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "postRecoveryAgeGraceSuppressedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "highFrameAgeSuppressedDuringOwnerAckCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_StaleHelperProofAllowsSameEpochRestart()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 45, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-stale-expiry", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst stale expiry start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 72, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x01, 0x02, 0x03 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(140));
        DeliverMatchingHelperVisibleReceipt(coordinator, "session-recovery-burst-stale-expiry", recoveryBurstStreamEpoch, recoveryOwnerFrameId);
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod("OnAutoTuneTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);
        clock.Advance(TimeSpan.FromMilliseconds(450));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        coordinator.RequestKeyFrame("frame_gap_reassembler");
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(2, fakeSource.KeyFrameRequestReasons.Count);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstStaleRequestSuppressedCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_DuplicateRequestsRemainSingleOwner()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 6, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-single-owner", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst single owner start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 8, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        coordinator.RequestKeyFrame("frame_gap_reassembler");
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(1, fakeSource.KeyFrameRequestReasons.Count);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstRestartSuppressedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstEncoderRerequestCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_HigherEpochTakeoverStillAllowedBeforeOwnerEmit()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 18, 55, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-pre-owner-takeover", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst pre-owner takeover start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 30, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var initialBurstEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        Assert.True(initialBurstEpoch >= 30L);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: initialBurstEpoch + 1, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame("frame_gap_reassembler");
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch") > initialBurstEpoch);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstProfileTransitionTakeoverCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryEpochTakeoverSuppressedAfterOwnerEmitCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerReplacedBeforeAckCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_HelperEpochResetAfterOwnerEmit_DoesNotClearBurst()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 18, 57, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        var refreshHelperCurrentEpochState = typeof(TransportScreenShareCoordinator).GetMethod("RefreshHelperCurrentEpochState", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(refreshHelperCurrentEpochState);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-helper-epoch-reset", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst helper epoch reset start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 50, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var originalBurstEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x21, 0x22, 0x23 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: originalBurstEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        refreshHelperCurrentEpochState!.Invoke(coordinator, new object? [] { originalBurstEpoch + 1 });
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(originalBurstEpoch, GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch"));
        Assert.Equal(recoveryOwnerFrameId, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryEpochTakeoverSuppressedAfterOwnerEmitCount"));
        Assert.Equal(originalBurstEpoch, GetPrivateFieldValue<long>(coordinator, "lastRecoveryEpochTakeoverSuppressedFromEpoch"));
        Assert.Equal(originalBurstEpoch + 1, GetPrivateFieldValue<long>(coordinator, "lastRecoveryEpochTakeoverSuppressedToEpoch"));
        Assert.Equal("owner_emitted_awaiting_helper_ack", GetPrivateFieldValue<string>(coordinator, "lastRecoveryEpochTakeoverSuppressedPhase"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerReplacedBeforeAckCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_PendingOwnerForcesResetOnlyOnce()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 7, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        var ownerPendingTick = typeof(TransportScreenShareCoordinator).GetMethod("OnRecoveryOwnerPendingTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(ownerPendingTick);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-forced-reset", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst forced reset start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 9, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        clock.Advance(TimeSpan.FromMilliseconds(260));
        ownerPendingTick!.Invoke(coordinator, Array.Empty<object>());
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.True(recoveryBurstStreamEpoch > 9);
        Assert.Single(fakeSource.KeyFrameRequestReasons);
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonContinuityLoss, fakeSource.KeyFrameRequestReasons[0]);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerPendingForcedResetCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstEncoderRerequestCount"));
        Assert.Equal(GetPrivateFieldValue<ScreenShareTransportTuningLevel>(coordinator, "transportTuningLevel"), fakeSource.LastTransportTuningLevel);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_ForcedResetOwnerAckDoesNotTimeout()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 7, 30, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        var ownerPendingTick = typeof(TransportScreenShareCoordinator).GetMethod("OnRecoveryOwnerPendingTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod("OnAutoTuneTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(ownerPendingTick);
        Assert.NotNull(autoTuneTick);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-forced-reset-ack", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst forced reset ack start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 12, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        Assert.True(recoveryBurstStreamEpoch > 12);
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 1, 2, 3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        DeliverMatchingHelperVisibleReceipt(coordinator, "session-recovery-burst-forced-reset-ack", recoveryBurstStreamEpoch, recoveryOwnerFrameId);
        clock.Advance(TimeSpan.FromMilliseconds(260));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerPendingForcedResetCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryKeyframeEmittedAfterForcedResetCount"));
        clock.Advance(TimeSpan.FromMilliseconds(850));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByHelperAckCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByTimeoutCount"));
        Assert.Equal("helper_ack", GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryCompletionKind"));
        Assert.Equal("helper_visible_receipt", GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryAckSource"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_TimesOutWithoutHelperAdvance()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 9, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod("OnAutoTuneTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-timeout", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst timeout start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 11, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 1, 2, 3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: 11, isKeyFrame: true));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 4, 5, 6 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: 11, isKeyFrame: false));
        await WaitUntilAsync(() => GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive") && GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch") > 0, TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromMilliseconds(850));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByTimeoutCount"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "satisfiedRecoveryFloorFrameId"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "satisfiedRecoveryFloorSource"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryBurst_TimeoutWithAppliedOnlyProgress_RemainsTimeout()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 10, 30, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod("OnAutoTuneTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-timeout-helper-progress", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst timeout helper progress start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 13, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpochForTimeout = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 1, 2, 3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryEpochForTimeout, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") == 0, TimeSpan.FromSeconds(2));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.ReduceFps, ScreenSharePressureProtocol.PressureReasonHighFrameAge, observedFrameAgeMs: 650, recentStaleDrops: 0, sentAtUtcMs: 0, currentEpochWarmupActive: false, currentEpochApplyCount: 2, currentEpochNeedMoreInputCount: 0, lastVisibleApplyFrameId: null, appliedHeadFrameId: 0, steadyVisibleProgressActive: null, stableVisibleHeadFrameId: 0, framesAppliedSinceLastGap: null);
        clock.Advance(TimeSpan.FromMilliseconds(850));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByHelperAckCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByTimeoutCount"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryAckSource"));
        Assert.Equal("timeout", GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryCompletionKind"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByVisibleRecoveryFloorCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByVisibleApplyFallbackCount"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckFrameId"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerEmitToAckMs"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerAckWindowMs"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerEmitToFirstVisibleApplyMs"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "recoveryAckSource"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "helperAckAfterFactSendMs"));
        var maybeLogFreshnessSummary = typeof(TransportScreenShareCoordinator).GetMethod("MaybeLogFreshnessSummary", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(maybeLogFreshnessSummary);
        clock.Advance(TimeSpan.FromSeconds(2));
        maybeLogFreshnessSummary!.Invoke(coordinator, new object[] { "session-recovery-burst-timeout-helper-progress", coordinator.GetMetricsSnapshot(), fakeSource.GetFreshnessMetricsSnapshot(), 0, 0, 0L, 0L, "none", 0L, 250, 70, 100 });
        Assert.Equal("timeout", GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryCompletionKind"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryAckSource"));
        var logText = LocalOperationalLog.GetRecentLogText();
        Assert.Contains("event=screenshare_freshness_summary", logText, StringComparison.Ordinal);
        Assert.Contains("recovery_completion_accounting_mismatch=0", logText, StringComparison.Ordinal);
    }

}
