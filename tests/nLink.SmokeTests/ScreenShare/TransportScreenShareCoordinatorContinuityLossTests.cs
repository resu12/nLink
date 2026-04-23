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
public sealed class TransportScreenShareCoordinatorContinuityLossTests : ScreenShareCoordinatorTestBase, IClassFixture<ScreenShareCoordinatorFixture>
{
    public TransportScreenShareCoordinatorContinuityLossTests(ScreenShareCoordinatorFixture fixture) : base(fixture) { }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_PostReceiptContinuityLossStaleBlockers_DoNotBlockPromotion()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 19, 45, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod("OnAutoTuneTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);
        await AwaitCompletesAsync(coordinator.StartAsync("session-post-receipt-stale-blockers", CancellationToken.None), TimeSpan.FromSeconds(2), "post receipt stale blockers start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        typeof(TransportScreenShareCoordinator).GetField("senderFreshnessMode", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, ScreenShareSenderFreshnessMode.Reduced);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 600, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x51, 0x52, 0x53 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        DeliverMatchingHelperVisibleReceipt(coordinator, "session-post-receipt-stale-blockers", recoveryBurstStreamEpoch, recoveryOwnerFrameId);
        SetPrivateFieldValue(coordinator, "recoveryLockActive", true);
        SetPrivateFieldValue(coordinator, "recoveryLockStreamEpoch", recoveryBurstStreamEpoch);
        SetPrivateFieldValue(coordinator, "recoveryLockStartedUtc", clock.UtcNow - TimeSpan.FromMilliseconds(100));
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: recoveryBurstStreamEpoch, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0, sentAtUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), currentEpochWarmupActive: false, currentEpochApplyCount: 3, currentEpochNeedMoreInputCount: 0, lastVisibleApplyFrameId: recoveryOwnerFrameId, visibleHeadFrameId: recoveryOwnerFrameId, appliedHeadFrameId: recoveryOwnerFrameId, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: recoveryOwnerFrameId, framesAppliedSinceLastGap: 3, visibleRecoveryFloorFrameId: recoveryOwnerFrameId, currentEpochRecoveryKeyframeApplyCount: 1);
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(1280, 720, new byte[] { (byte)(96 + i), 2, 3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.True(GetPrivateFieldValue<long>(coordinator, "postReceiptBlockerSuppressedCount") > 0);
        Assert.Contains("helper_pressure", GetPrivateFieldValue<string>(coordinator, "lastPostReceiptBlockerSuppressedSet"), StringComparison.Ordinal);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "promotionBlockerHelperPressureTicks"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "promotionBlockerHelperApplyCountTicks"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "promotionBlockerRecoveryLockTicks"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ContinuityLossPressure_WithFramesAppliedProof_ActivatesSenderSideHelperFactHealth()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 7, 55, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-continuity-fact-health", CancellationToken.None), TimeSpan.FromSeconds(2), "continuity fact health start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 60, CurrentStreamEpoch: 1));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0, sentAtUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), currentEpochWarmupActive: true, currentEpochApplyCount: 4, currentEpochNeedMoreInputCount: 0, lastVisibleApplyFrameId: 7, appliedHeadFrameId: 7, steadyVisibleProgressActive: false, stableVisibleHeadFrameId: 7, framesAppliedSinceLastGap: 4);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "helperSteadyVisibleProgressActive"));
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "helperCurrentEpochWarmupActive"));
        Assert.Equal(4L, GetPrivateFieldValue<long>(coordinator, "helperFramesAppliedSinceLastGap"));
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "remoteHelperFactHealthyActive"));
        Assert.Equal("frames_applied_since_last_gap", GetPrivateFieldValue<string>(coordinator, "remoteHelperFactHealthySource"));
        Assert.Equal(7L, GetPrivateFieldValue<long>(coordinator, "remoteHelperFactProofFrameId"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteHelperFactHealth_ClearsAfterNoProgressStall()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 8, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-remote-helper-fact-stall-clear", CancellationToken.None), TimeSpan.FromSeconds(2), "remote helper fact stall clear start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 60, CurrentStreamEpoch: 1));
        SendHealthyRemotePressure(coordinator, currentEpochWarmupActive: false, currentEpochApplyCount: 2, currentEpochNeedMoreInputCount: 0, lastVisibleApplyFrameId: 15, appliedHeadFrameId: 15, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: 15, framesAppliedSinceLastGap: 9);
        clock.Advance(TimeSpan.FromMilliseconds(1600));
        SendHealthyRemotePressure(coordinator, currentEpochWarmupActive: false, currentEpochApplyCount: 2, currentEpochNeedMoreInputCount: 0);
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "remoteHelperFactHealthyActive"));
        Assert.Equal("no_progress_stall", GetPrivateFieldValue<string>(coordinator, "remoteHelperFactHealthyClearReason"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "remoteHelperFactHealthyClearCount"));
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "helperSteadyVisibleProgressActive"));
        Assert.Equal(9L, GetPrivateFieldValue<long>(coordinator, "helperFramesAppliedSinceLastGap"));
        Assert.Equal(15L, GetPrivateFieldValue<long>(coordinator, "acknowledgedHelperHeadFrameId"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "satisfiedRecoveryFloorFrameId"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteReduceFpsHighFrameAgeDuringActiveRecovery_DoesNotEnterCatchUp()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 22, 9, 10, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod("OnAutoTuneTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);
        await AwaitCompletesAsync(coordinator.StartAsync("session-remote-high-frame-age-active-recovery", CancellationToken.None), TimeSpan.FromSeconds(2), "remote high frame age during active recovery start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 8);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 24, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 1, 2, 3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.ReduceFps, ScreenSharePressureProtocol.PressureReasonHighFrameAge, observedFrameAgeMs: 450, recentStaleDrops: 0, sentAtUtcMs: 0, currentEpochWarmupActive: false, currentEpochApplyCount: 5, currentEpochNeedMoreInputCount: 0, lastVisibleApplyFrameId: 4, visibleHeadFrameId: 4, appliedHeadFrameId: 4, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: 4, framesAppliedSinceLastGap: 5);
        SetPrivateFieldValue(coordinator, "remoteHighFrameAgeCatchUpEntryConsecutiveTicks", 0);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        clock.Advance(TimeSpan.FromSeconds(1));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());
        Assert.NotEqual("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "senderCatchUpEnteredDueToRemoteHighFrameAgeCount"));
        Assert.Equal(0, GetPrivateFieldValue<int>(coordinator, "remoteHighFrameAgeCatchUpEntryConsecutiveTicks"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_NoProgressStall_DoesNotRevokeSatisfiedRecoveryFloor()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 9, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-satisfied-floor-stall", CancellationToken.None), TimeSpan.FromSeconds(2), "satisfied floor stall start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 50, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x01, 0x02, 0x03 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(140));
        SendHealthyRemotePressure(coordinator, currentEpochWarmupActive: false, currentEpochApplyCount: 1, currentEpochNeedMoreInputCount: 0, lastVisibleApplyFrameId: recoveryOwnerFrameId, appliedHeadFrameId: recoveryOwnerFrameId, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: recoveryOwnerFrameId, framesAppliedSinceLastGap: 1);
        Assert.InRange(GetPrivateFieldValue<long>(coordinator, "satisfiedRecoveryFloorFrameId"), -1L, recoveryOwnerFrameId);
        Assert.InRange(GetPrivateFieldValue<long>(coordinator, "acknowledgedHelperHeadFrameId"), -1L, recoveryOwnerFrameId);
        clock.Advance(TimeSpan.FromMilliseconds(1600));
        SendHealthyRemotePressure(coordinator, currentEpochWarmupActive: false, currentEpochApplyCount: 1, currentEpochNeedMoreInputCount: 0);
        Assert.InRange(GetPrivateFieldValue<long>(coordinator, "acknowledgedHelperHeadFrameId"), -1L, recoveryOwnerFrameId);
        Assert.InRange(GetPrivateFieldValue<long>(coordinator, "satisfiedRecoveryFloorFrameId"), -1L, recoveryOwnerFrameId);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ContinuityRecoveryTimeout_TriggersExactlyOneHardReset()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var flushedTransportQueueCount = 0;
        string? flushedTransportQueueReason = null;
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 8, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock, flushTransportQueue: reason =>
        {
            flushedTransportQueueCount++;
            flushedTransportQueueReason = reason;
        });
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-timeout-reset", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery timeout reset start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.Normal);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 11, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0);
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.CatchUpOnly, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0);
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.CatchUpOnly, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.Equal(0, GetPrivateFieldValue<int>(coordinator, "recoveryTimeoutResetCount"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryLockStreamEpoch") >= 11);
        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(ScreenShareTransportTuningLevel.Normal, fakeSource.LastTransportTuningLevel);
        Assert.Equal(0, flushedTransportQueueCount);
        Assert.True(string.IsNullOrEmpty(flushedTransportQueueReason));
        Assert.Equal(0, fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "recovery_timeout_reset", StringComparison.Ordinal)));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_SimplifiedRecovery_StartsOnResetEpochImmediately()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 20, 9, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-simplified-recovery-start", CancellationToken.None), TimeSpan.FromSeconds(2), "simplified recovery start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 5, PendingRawFrameCount: 1, OldestPendingRawFrameAgeMs: 180, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0);
        clock.Advance(TimeSpan.FromMilliseconds(120));
        var expectedTransportTuningLevel = GetPrivateFieldValue<ScreenShareTransportTuningLevel>(coordinator, "transportTuningLevel");
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal(6L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch"));
        Assert.Equal("OwnerPending", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.Equal(6L, fakeSource.GetFreshnessMetricsSnapshot().CurrentStreamEpoch);
        Assert.Equal(expectedTransportTuningLevel, fakeSource.LastTransportTuningLevel);
        Assert.Equal(1, fakeSource.PurgePendingRawFramesCallCount);
        Assert.Contains(ScreenSharePressureProtocol.PressureReasonContinuityLoss, fakeSource.KeyFrameRequestReasons);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryOwnerPendingForcedResetCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ContinuityLossPressure_WithHelperHeadAdvance_DoesNotCompleteBurstButClearsWarmup()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 20, 9, 30, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-helper-progress-continuity-loss", CancellationToken.None), TimeSpan.FromSeconds(2), "helper progress continuity loss start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 912, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryBurstStreamEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x41, 0x42, 0x43 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryBurstStreamEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0, sentAtUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), currentEpochWarmupActive: true, currentEpochApplyCount: 1, currentEpochNeedMoreInputCount: 0, lastVisibleApplyFrameId: recoveryOwnerFrameId, appliedHeadFrameId: recoveryOwnerFrameId, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: recoveryOwnerFrameId, framesAppliedSinceLastGap: 1);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("OwnerEmittedAwaitingHelperAck", GetPrivateFieldValue<object>(coordinator, "recoveryBurstPhase")?.ToString());
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "helperCurrentEpochWarmupActive"));
        Assert.Equal(1, GetPrivateFieldValue<int>(coordinator, "helperCurrentEpochApplyCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByHelperAckCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryBurstCompletedByTimeoutCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "senderReceivedHelperProgressDuringContinuityLossCount"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "lastCompletedRecoveryCompletionKind"));
    }

}
