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
public sealed class TransportScreenShareCoordinatorRecoveryLockTests : ScreenShareCoordinatorTestBase, IClassFixture<ScreenShareCoordinatorFixture>
{
    public TransportScreenShareCoordinatorRecoveryLockTests(ScreenShareCoordinatorFixture fixture) : base(fixture) { }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteHighFrameAgeWithRecoveryLock_AllowsCatchUpWithinReducedBand()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 22, 9, 20, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod("OnAutoTuneTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-lock-catchup-allowed", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery lock catch-up allowed start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 26, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        SetPrivateFieldValue(coordinator, "recoveryLockActive", true);
        SetPrivateFieldValue(coordinator, "recoveryLockStreamEpoch", 26L);
        SetPrivateFieldValue(coordinator, "recoveryLockStartedUtc", clock.UtcNow);
        SetPrivateFieldValue(coordinator, "recoveryLockReason", ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.ReduceFps, ScreenSharePressureProtocol.PressureReasonHighFrameAge, observedFrameAgeMs: 450, recentStaleDrops: 0, sentAtUtcMs: 0, currentEpochWarmupActive: false, currentEpochApplyCount: 5, currentEpochNeedMoreInputCount: 0, lastVisibleApplyFrameId: 4, visibleHeadFrameId: 4, appliedHeadFrameId: 4, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: 4, framesAppliedSinceLastGap: 5);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        if (!string.Equals(coordinator.GetMetricsSnapshot().FreshnessMode, "catch_up", StringComparison.Ordinal))
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            autoTuneTick.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "senderCatchUpEnteredDueToRemoteHighFrameAgeCount"));
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryLockAllowedSameTuningModeChangeCount"));
        Assert.Equal("reduced->catch_up", GetPrivateFieldValue<string>(coordinator, "lastRecoveryLockAllowedSameTuningModeChange"));
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        var logText = File.ReadAllText(LocalOperationalLog.LogFilePath);
        Assert.Contains("event=screenshare_recovery_lock_allowed_same_tuning_mode_change", logText, StringComparison.Ordinal);
        Assert.Contains("session_id=session-recovery-lock-catchup-allowed", logText, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_CatchUpRecoveryWithRecoveryLock_AllowsReturnToReducedWithinReducedBand()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 22, 9, 25, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod("OnAutoTuneTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-lock-catchup-exit", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery lock catch-up exit start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        SetPrivateFieldValue(coordinator, "senderFreshnessMode", ScreenShareSenderFreshnessMode.CatchUp);
        SetPrivateFieldValue(coordinator, "captureFpsHint", 3);
        SetPrivateFieldValue(coordinator, "transportTuningLevel", ScreenShareTransportTuningLevel.BandwidthReduced);
        SetPrivateFieldValue(coordinator, "recoveryLockActive", true);
        SetPrivateFieldValue(coordinator, "recoveryLockStreamEpoch", 27L);
        SetPrivateFieldValue(coordinator, "recoveryLockStartedUtc", clock.UtcNow);
        SetPrivateFieldValue(coordinator, "recoveryLockReason", ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        fakeSource.SetCaptureFrameRateHint(3);
        if (typeof(TransportScreenShareCoordinator).GetField("sendPipeline", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(coordinator)is ScreenShareFrameSendPipeline sendPipeline)
        {
            sendPipeline.SetMaxFramesPerSecond(3);
        }

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 27, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        for (var i = 0; i < 3; i++)
        {
            SendHealthyRemotePressure(coordinator, observedFrameAgeMs: 150, recentStaleDrops: 0, currentEpochWarmupActive: false, currentEpochApplyCount: 6 + i, currentEpochNeedMoreInputCount: 0, lastVisibleApplyFrameId: 6 + i, visibleHeadFrameId: 6 + i, appliedHeadFrameId: 6 + i, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: 6 + i, framesAppliedSinceLastGap: 6 + i);
            clock.Advance(TimeSpan.FromMilliseconds(250));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new[] { (byte)(60 + i) }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: 27));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "recoveryLockAllowedSameTuningModeChangeCount"));
        Assert.Equal("catch_up->reduced", GetPrivateFieldValue<string>(coordinator, "lastRecoveryLockAllowedSameTuningModeChange"));
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryLock_StillBlocksReducedToNormalProfileChange()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 22, 9, 30, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod("OnAutoTuneTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-lock-still-blocks-normal", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery lock still blocks normal start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 28, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 60));
        SetPrivateFieldValue(coordinator, "recoveryLockActive", true);
        SetPrivateFieldValue(coordinator, "recoveryLockStreamEpoch", 28L);
        SetPrivateFieldValue(coordinator, "recoveryLockStartedUtc", clock.UtcNow);
        SetPrivateFieldValue(coordinator, "recoveryLockReason", ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        for (var i = 0; i < 3; i++)
        {
            SendHealthyRemotePressure(coordinator, observedFrameAgeMs: 0, recentStaleDrops: 0, currentEpochWarmupActive: false, currentEpochApplyCount: 3 + i, currentEpochNeedMoreInputCount: 0, lastVisibleApplyFrameId: 3 + i, visibleHeadFrameId: 3 + i, appliedHeadFrameId: 3 + i, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: 3 + i, framesAppliedSinceLastGap: 3 + i);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(1280, 720, new byte[] { (byte)(40 + i), 2, 3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: 28));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryLockAllowedSameTuningModeChangeCount"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ContinuityRecoveryLock_BlocksNormalToReducedTransition()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 8, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod("OnAutoTuneTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-lock-block", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery lock block start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.Normal);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 7, LastEncodeDurationMs: 22, LastEncodeTotalDurationMs: 140));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        clock.Advance(TimeSpan.FromMilliseconds(500));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(ScreenShareTransportTuningLevel.Normal, fakeSource.LastTransportTuningLevel);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryLock_IgnoresOutOfOrderNonRecoveryPressure()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 8, 2, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-lock-out-of-order", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery lock out-of-order start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.Normal);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 9, LastEncodeDurationMs: 20, LastEncodeTotalDurationMs: 36));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0, sentAtUtcMs: 2_000);
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.CatchUpOnly, ScreenSharePressureProtocol.PressureReasonSlowApplyCadence, observedFrameAgeMs: 280, recentStaleDrops: 0, sentAtUtcMs: 1_500);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.Equal(ScreenShareRemotePressureMode.None, GetPrivateFieldValue<ScreenShareRemotePressureMode>(coordinator, "remotePressureMode"));
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonContinuityLoss, GetPrivateFieldValue<string>(coordinator, "remotePressureReason"));
        Assert.DoesNotContain("remote_catch_up_only", fakeSource.KeyFrameRequestReasons);
        Assert.Contains("ignore_reason=stale_recovery_message", LocalOperationalLog.GetRecentLogText(), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryLock_ClearsForNewerNonRecoveryPressure()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 8, 3, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-lock-clear", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery lock clear start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.Normal);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 10, LastEncodeDurationMs: 20, LastEncodeTotalDurationMs: 36));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0, sentAtUtcMs: 2_000);
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.ReduceFps, ScreenSharePressureProtocol.PressureReasonSlowApplyCadence, observedFrameAgeMs: 260, recentStaleDrops: 0, sentAtUtcMs: 2_500);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.Equal(ScreenShareRemotePressureMode.ReduceFps, GetPrivateFieldValue<ScreenShareRemotePressureMode>(coordinator, "remotePressureMode"));
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonSlowApplyCadence, GetPrivateFieldValue<string>(coordinator, "remotePressureReason"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_AcknowledgedHelperProof_DoesNotClearRecoveryLockWithoutReceipt()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 9, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-lock-acknowledged-proof", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery lock acknowledged proof start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 40, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var recoveryEpoch = GetPrivateFieldValue<long>(coordinator, "recoveryBurstStreamEpoch");
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0, currentEpochWarmupActive: false, currentEpochApplyCount: 0, currentEpochNeedMoreInputCount: 1);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.Equal(recoveryEpoch, GetPrivateFieldValue<long>(coordinator, "recoveryLockStreamEpoch"));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 0x01, 0x02, 0x03 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: recoveryEpoch, isKeyFrame: true));
        await WaitUntilAsync(() => GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId") >= 0, TimeSpan.FromSeconds(2));
        var recoveryOwnerFrameId = GetPrivateFieldValue<long>(coordinator, "recoveryOwnerFrameId");
        clock.Advance(TimeSpan.FromMilliseconds(140));
        var acknowledgedReleaseFloorFrameId = recoveryOwnerFrameId + 98;
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0, currentEpochWarmupActive: false, currentEpochApplyCount: 1, currentEpochNeedMoreInputCount: 0, lastVisibleApplyFrameId: acknowledgedReleaseFloorFrameId, visibleHeadFrameId: recoveryOwnerFrameId + 3, appliedHeadFrameId: acknowledgedReleaseFloorFrameId, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: acknowledgedReleaseFloorFrameId, framesAppliedSinceLastGap: 1);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryLockClearedByVisibleProofCount"));
        Assert.Equal(recoveryOwnerFrameId + 3, GetPrivateFieldValue<long>(coordinator, "acknowledgedVisibleHelperHeadFrameId"));
        Assert.Equal(-1L, GetPrivateFieldValue<long>(coordinator, "satisfiedRecoveryFloorFrameId"));
        Assert.Equal(string.Empty, GetPrivateFieldValue<string>(coordinator, "satisfiedRecoveryFloorSource"));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0, currentEpochWarmupActive: false, currentEpochApplyCount: 1, currentEpochNeedMoreInputCount: 0, lastVisibleApplyFrameId: acknowledgedReleaseFloorFrameId, visibleHeadFrameId: acknowledgedReleaseFloorFrameId, appliedHeadFrameId: acknowledgedReleaseFloorFrameId, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: acknowledgedReleaseFloorFrameId, framesAppliedSinceLastGap: 1, visibleRecoveryFloorFrameId: acknowledgedReleaseFloorFrameId, currentEpochRecoveryKeyframeApplyCount: 1);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryLockClearedByAcknowledgedProofCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "recoveryLockClearedByVisibleProofCount"));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0, currentEpochWarmupActive: false, currentEpochApplyCount: 1, currentEpochNeedMoreInputCount: 0, lastVisibleApplyFrameId: acknowledgedReleaseFloorFrameId, visibleHeadFrameId: acknowledgedReleaseFloorFrameId, appliedHeadFrameId: acknowledgedReleaseFloorFrameId, steadyVisibleProgressActive: true, stableVisibleHeadFrameId: acknowledgedReleaseFloorFrameId, framesAppliedSinceLastGap: 1, visibleRecoveryFloorFrameId: acknowledgedReleaseFloorFrameId, currentEpochRecoveryKeyframeApplyCount: 1);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ActiveRecoveryBurst_DefersProfileTransition()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 10, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-burst-profile-defer", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery burst profile defer start");
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 12, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        var applyModeMethod = typeof(TransportScreenShareCoordinator).GetMethod("ApplySenderFreshnessMode", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyModeMethod);
        var sendPipelineField = typeof(TransportScreenShareCoordinator).GetField("sendPipeline", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(sendPipelineField);
        var currentPipeline = sendPipelineField!.GetValue(coordinator);
        applyModeMethod!.Invoke(coordinator, new object? [] { currentPipeline, fakeSource, "session-recovery-burst-profile-defer", ScreenShareSenderFreshnessMode.Normal, "test_profile_transition", 8, false, });
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryBurstActive"));
        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(0, fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "sender_profile_transition", StringComparison.Ordinal)));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryBurstProfileTransitionDeferredCount") >= 1);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RecoveryLock_DefersProfileTransitionBeforeEpochTakeover()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 16, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-lock-profile-defer", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery lock profile defer start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.Normal);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 18, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 32));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0);
        var applyModeMethod = typeof(TransportScreenShareCoordinator).GetMethod("ApplySenderFreshnessMode", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyModeMethod);
        var sendPipelineField = typeof(TransportScreenShareCoordinator).GetField("sendPipeline", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(sendPipelineField);
        var currentPipeline = sendPipelineField!.GetValue(coordinator);
        applyModeMethod!.Invoke(coordinator, new object? [] { currentPipeline, fakeSource, "session-recovery-lock-profile-defer", ScreenShareSenderFreshnessMode.Reduced, "test_profile_transition", 8, false, });
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(ScreenShareTransportTuningLevel.Normal, fakeSource.LastTransportTuningLevel);
        Assert.Equal(0, fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "sender_profile_transition", StringComparison.Ordinal)));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryBurstProfileTransitionDeferredCount") >= 1);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_HelperNeedMoreInputWithFreshProgress_DefersProfileTransition()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 22, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock);
        await AwaitCompletesAsync(coordinator.StartAsync("session-helper-progress-profile-defer", CancellationToken.None), TimeSpan.FromSeconds(2), "helper progress profile defer start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.Normal);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 19, LastEncodeDurationMs: 18, LastEncodeTotalDurationMs: 34));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.ReduceFps, ScreenSharePressureProtocol.PressureReasonHighFrameAge, observedFrameAgeMs: 720, recentStaleDrops: 0, sentAtUtcMs: 0, currentEpochWarmupActive: false, currentEpochApplyCount: 1, currentEpochNeedMoreInputCount: 2, lastVisibleApplyFrameId: 34, appliedHeadFrameId: 34, steadyVisibleProgressActive: false, stableVisibleHeadFrameId: null, framesAppliedSinceLastGap: 1);
        var applyModeMethod = typeof(TransportScreenShareCoordinator).GetMethod("ApplySenderFreshnessMode", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(applyModeMethod);
        var sendPipelineField = typeof(TransportScreenShareCoordinator).GetField("sendPipeline", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(sendPipelineField);
        var currentPipeline = sendPipelineField!.GetValue(coordinator);
        applyModeMethod!.Invoke(coordinator, new object? [] { currentPipeline, fakeSource, "session-helper-progress-profile-defer", ScreenShareSenderFreshnessMode.Reduced, "test_profile_transition", 8, false, });
        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(ScreenShareTransportTuningLevel.Normal, fakeSource.LastTransportTuningLevel);
        Assert.Equal(0, fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "sender_profile_transition", StringComparison.Ordinal)));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "recoveryBurstProfileTransitionDeferredCount") >= 1);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_SevereQueueCongestion_CanBreakRecoveryLock()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var backpressureProbe = new FakeScreenShareBackpressureProbe
        {
            IsSeverelyCongested = true,
            QueueDepth = 2,
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 8, 10, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(captureSourceFactory: () => fakeSource, sendPayloadAsync: (_, _) => Task.CompletedTask, clock: clock, transportBackpressureProbeResolver: () => backpressureProbe);
        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod("OnAutoTuneTimerTick", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);
        await AwaitCompletesAsync(coordinator.StartAsync("session-recovery-lock-override", CancellationToken.None), TimeSpan.FromSeconds(2), "recovery lock override start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.Normal);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(CurrentStreamEpoch: 13, LastEncodeDurationMs: 24, LastEncodeTotalDurationMs: 150));
        coordinator.SetRemotePressureState(ScreenShareRemotePressureMode.None, ScreenSharePressureProtocol.PressureReasonContinuityLoss, observedFrameAgeMs: 0, recentStaleDrops: 0);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "recoveryLockActive"));
        Assert.NotEqual("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
    }

}
