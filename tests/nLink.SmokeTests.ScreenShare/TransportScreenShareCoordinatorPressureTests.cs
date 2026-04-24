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
public sealed class TransportScreenShareCoordinatorPressureTests : ScreenShareCoordinatorTestBase, IClassFixture<ScreenShareCoordinatorFixture>
{
    public TransportScreenShareCoordinatorPressureTests(ScreenShareCoordinatorFixture fixture) : base(fixture)
    {
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_AdvisoryBridgeHealth_DoesNotBlockPromotionToNormal()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new AdaptiveFakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.0),
        };
        var backpressureProbe = new FakeScreenShareBackpressureProbe
        {
            RecentHealthIssueCount = 2,
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 20, 15, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            transportBackpressureProbeResolver: () => backpressureProbe);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-advisory-bridge-health", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "advisory bridge health start");

        clock.Advance(TimeSpan.FromSeconds(4));
        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { 1, 2, 3 },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            PendingRawFrameCount: 0,
            OldestPendingRawFrameAgeMs: 0,
            LastCaptureToEncodeStartAgeMs: 0,
            LastEncodeDurationMs: 16,
            LastEncodeTotalDurationMs: 28,
            SupersededPendingRawFrameCount: 12));

        await Task.Delay(50);
        for (var i = 0; i < 5; i++)
        {
            SendHealthyRemotePressure(coordinator);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
        Assert.Contains("bridge_health_kind=advisory", LocalOperationalLog.GetRecentLogText(), StringComparison.Ordinal);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_NonHealthyPressure_WithProvenFacts_DoesNotRetainLegacySenderSideHelperProof()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 15, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-sticky-proof-reset", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "sticky proof reset start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 2,
            currentEpochNeedMoreInputCount: 0,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 15,
            framesAppliedSinceLastGap: 9);

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 650,
            recentStaleDrops: 0,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 2,
            currentEpochNeedMoreInputCount: 0,
            steadyVisibleProgressActive: null,
            stableVisibleHeadFrameId: null,
            framesAppliedSinceLastGap: null);

        Assert.Equal(ScreenShareRemotePressureMode.ReduceFps, GetPrivateFieldValue<ScreenShareRemotePressureMode>(coordinator, "remotePressureMode"));
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonHighFrameAge, GetPrivateFieldValue<string>(coordinator, "remotePressureReason"));
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "remoteHelperFactHealthyActive"));
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteHelperFactHealth_ClearsOnNewEpoch()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 8, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-helper-fact-epoch-reset", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote helper fact epoch reset start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 2,
            currentEpochNeedMoreInputCount: 0,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 15,
            framesAppliedSinceLastGap: 9);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 2));

        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: true,
            currentEpochApplyCount: 0,
            currentEpochNeedMoreInputCount: 0);

        Assert.False(GetPrivateFieldValue<bool>(coordinator, "remoteHelperFactHealthyActive"));
        Assert.Equal("new_stream_epoch", GetPrivateFieldValue<string>(coordinator, "remoteHelperFactHealthyClearReason"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "remoteHelperFactHealthyClearCount") >= 0);
        Assert.False(GetPrivateFieldValue<bool>(coordinator, "helperSteadyVisibleProgressActive"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "helperFramesAppliedSinceLastGap"));
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteHelperFactHealth_RefreshMessagesRemainAdvisoryOnly()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 21, 8, 10, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-helper-fact-refresh-hold", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote helper fact refresh hold start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 2,
            currentEpochNeedMoreInputCount: 0,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 15,
            framesAppliedSinceLastGap: 9);

        clock.Advance(TimeSpan.FromMilliseconds(900));
        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 2,
            currentEpochNeedMoreInputCount: 0,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 15,
            framesAppliedSinceLastGap: 9);

        Assert.False(GetPrivateFieldValue<bool>(coordinator, "remoteHelperFactHealthyActive"));

        clock.Advance(TimeSpan.FromMilliseconds(900));
        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 2,
            currentEpochNeedMoreInputCount: 0,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 15,
            framesAppliedSinceLastGap: 9);

        Assert.False(GetPrivateFieldValue<bool>(coordinator, "remoteHelperFactHealthyActive"));
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_HealthyPressureWithoutExplicitApplyCount_DoesNotAdvanceHelperApplyCount()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 17, 10, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-helper-apply-count-authoritative", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "helper apply count authoritative start");

        DisableStartupWarmupForCoordinatorOnly(coordinator);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochApplyCount", 1);

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            ScreenSharePressureProtocol.PressureReasonHealthy,
            observedFrameAgeMs: 220,
            recentStaleDrops: 0,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: true,
            currentEpochApplyCount: null,
            currentEpochNeedMoreInputCount: 0);

        Assert.Equal(1, GetPrivateFieldValue<int>(coordinator, "helperCurrentEpochApplyCount"));
        Assert.Equal(1, GetPrivateFieldValue<int>(coordinator, "helperCurrentEpochHealthySignalCount"));
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_LocalAdvisoryBridgeHealth_DoesNotIgnoreHealthyRemotePressureState()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var backpressureProbe = new FakeScreenShareBackpressureProbe
        {
            RecentHealthIssueCount = 2,
        };
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 20, 20, 0, TimeSpan.Zero)),
            transportBackpressureProbeResolver: () => backpressureProbe);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-advisory-health-remote-none", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "advisory bridge health remote-none start");

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.CatchUpOnly,
            "high_frame_age",
            observedFrameAgeMs: 1800,
            recentStaleDrops: 4);

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.None,
            "healthy",
            observedFrameAgeMs: 0,
            recentStaleDrops: 0);

        var remotePressureMode = (ScreenShareRemotePressureMode)typeof(TransportScreenShareCoordinator)
            .GetField("remotePressureMode", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator)!;
        Assert.Equal(ScreenShareRemotePressureMode.None, remotePressureMode);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_LocalLaneCongestion_ReducesFpsBeforeRemotePressure()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var backpressureProbe = new FakeScreenShareBackpressureProbe
        {
            IsCongested = true,
            QueueDepth = 14,
            QueuedBytes = 320 * 1024,
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 12, 20, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            transportBackpressureProbeResolver: () => backpressureProbe);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-local-lane-congestion", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "local lane congestion start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);

        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { 1, 2, 3 },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));

        await Task.Delay(50);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_SevereLocalLaneCongestion_EntersDegradedModeImmediately()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var backpressureProbe = new FakeScreenShareBackpressureProbe
        {
            IsCongested = true,
            IsSeverelyCongested = true,
            QueueDepth = 22,
            QueuedBytes = 512 * 1024,
            RecentDropCount = 2,
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 12, 25, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            transportBackpressureProbeResolver: () => backpressureProbe);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-severe-local-lane-congestion", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "severe local lane congestion start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);

        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { 1, 2, 3 },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));

        await Task.Delay(50);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteReduceFpsWithRepeatedStaleDrops_StaysInDegradedMode()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 2, 12, 40, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-stale-burst", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote stale burst start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonRepeatedStaleDrops,
            observedFrameAgeMs: 320,
            recentStaleDrops: 12);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.InRange(fakeSource.LastCaptureFrameRateHint, 5, 8);
        Assert.Equal(ScreenShareTransportTuningLevel.Normal, fakeSource.LastTransportTuningLevel);
        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteReduceFpsHighFrameAgeWithHelperEvidence_EntersCatchUpAfterTwoTicks()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 22, 9, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-high-frame-age-catchup", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote high frame age catch-up start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 8);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 21,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 450,
            recentStaleDrops: 0,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 5,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: 4,
            visibleHeadFrameId: 4,
            appliedHeadFrameId: 4,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 4,
            framesAppliedSinceLastGap: 5);

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        typeof(TransportScreenShareCoordinator)
            .GetField("remoteHighFrameAgeCatchUpEntryConsecutiveTicks", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(coordinator, 0);

        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "senderCatchUpEnteredDueToRemoteHighFrameAgeCount"));
        Assert.Equal(1, GetPrivateFieldValue<int>(coordinator, "remoteHighFrameAgeCatchUpEntryConsecutiveTicks"));

        clock.Advance(TimeSpan.FromSeconds(1));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "senderCatchUpEnteredDueToRemoteHighFrameAgeCount"));
        Assert.InRange(GetPrivateFieldValue<int>(coordinator, "remoteHighFrameAgeCatchUpEntryConsecutiveTicks"), 2, int.MaxValue);
        Assert.DoesNotContain("catch_up_mode_enter", fakeSource.KeyFrameRequestReasons);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteReduceFpsHighFrameAgeWithoutHelperEvidence_DoesNotEnterCatchUp()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 22, 9, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-high-frame-age-no-proof", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote high frame age without helper evidence start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 8);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 22,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 450,
            recentStaleDrops: 0,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 0,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: null,
            visibleHeadFrameId: null,
            appliedHeadFrameId: null,
            steadyVisibleProgressActive: false,
            stableVisibleHeadFrameId: null,
            framesAppliedSinceLastGap: 0);

        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        clock.Advance(TimeSpan.FromSeconds(1));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "senderCatchUpEnteredDueToRemoteHighFrameAgeCount"));
        Assert.Equal(0, GetPrivateFieldValue<int>(coordinator, "remoteHighFrameAgeCatchUpEntryConsecutiveTicks"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "remoteHighFrameAgeCatchUpSuppressedDueToMissingHelperEvidenceCount") > 0);
        Assert.Equal("missing_helper_evidence", GetPrivateFieldValue<string>(coordinator, "lastRemoteHighFrameAgeCatchUpSuppressionReason"));

        var logText = File.ReadAllText(LocalOperationalLog.LogFilePath);
        Assert.Contains("event=screenshare_sender_auto_tune_decision", logText, StringComparison.Ordinal);
        Assert.Contains("session_id=session-remote-high-frame-age-no-proof", logText, StringComparison.Ordinal);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteHighFrameAgeCatchUp_ExitsViaExistingLowPressurePath()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 22, 9, 15, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-high-frame-age-recover", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote high frame age exit start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 8);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 25,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 450,
            recentStaleDrops: 0,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 5,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: 4,
            visibleHeadFrameId: 4,
            appliedHeadFrameId: 4,
            steadyVisibleProgressActive: true,
            stableVisibleHeadFrameId: 4,
            framesAppliedSinceLastGap: 5);

        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        clock.Advance(TimeSpan.FromSeconds(1));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);

        for (var i = 0; i < 3; i++)
        {
            coordinator.SetRemotePressureState(
                ScreenShareRemotePressureMode.ReduceFps,
                ScreenSharePressureProtocol.PressureReasonHighFrameAge,
                observedFrameAgeMs: 300 - (i * 10),
                recentStaleDrops: 0,
                sentAtUtcMs: 0,
                currentEpochWarmupActive: false,
                currentEpochApplyCount: 6 + i,
                currentEpochNeedMoreInputCount: 0,
                lastVisibleApplyFrameId: 6 + i,
                visibleHeadFrameId: 6 + i,
                appliedHeadFrameId: 6 + i,
                steadyVisibleProgressActive: true,
                stableVisibleHeadFrameId: 6 + i,
                framesAppliedSinceLastGap: 6 + i);
            fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
                CurrentStreamEpoch: 25,
                LastEncodeDurationMs: 18,
                LastEncodeTotalDurationMs: 32));
            clock.Advance(TimeSpan.FromMilliseconds(250));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
                640,
                360,
                new[] { (byte)(30 + i) },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
            await Task.Delay(20);
            autoTuneTick.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.True(GetPrivateFieldValue<long>(coordinator, "catchUpRecoverySuppressedDueToRemoteHighFrameAgeCount") > 0);
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "catchUpExitWhileRemoteHighFrameAgePressureCount"));

        for (var i = 0; i < 3; i++)
        {
            SendHealthyRemotePressure(
                coordinator,
                observedFrameAgeMs: 150,
                recentStaleDrops: 0,
                currentEpochWarmupActive: false,
                currentEpochApplyCount: 9 + i,
                currentEpochNeedMoreInputCount: 0,
                lastVisibleApplyFrameId: 9 + i,
                visibleHeadFrameId: 9 + i,
                appliedHeadFrameId: 9 + i,
                steadyVisibleProgressActive: true,
                stableVisibleHeadFrameId: 9 + i,
                framesAppliedSinceLastGap: 9 + i);
            fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
                CurrentStreamEpoch: 25,
                LastEncodeDurationMs: 18,
                LastEncodeTotalDurationMs: 32));
            clock.Advance(TimeSpan.FromMilliseconds(250));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(
                640,
                360,
                new[] { (byte)(40 + i) },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
            await Task.Delay(20);
            autoTuneTick.Invoke(coordinator, Array.Empty<object>());

            if (string.Equals(coordinator.GetMetricsSnapshot().FreshnessMode, "reduced", StringComparison.Ordinal))
            {
                break;
            }
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(1L, GetPrivateFieldValue<long>(coordinator, "senderCatchUpEnteredDueToRemoteHighFrameAgeCount"));
        Assert.Equal(0L, GetPrivateFieldValue<long>(coordinator, "catchUpExitWhileRemoteHighFrameAgePressureCount"));
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemoteCatchUpOnly_RequestsKeyframeAndPurgesQueuedBacklog()
    {
        var fakeSource = new FakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 12, 12, 0, 0, TimeSpan.Zero));
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 4, maxInFlight: 1, startBlocked: true);
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-catchup", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote catch-up start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);

        RaiseTransportFrame(fakeSource, 640, 360, new byte[] { 1 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds());
        await AwaitCompletesAsync(
            probe.FirstSendStarted,
            TimeSpan.FromSeconds(2),
            "remote catch-up blocked first send");

        for (byte marker = 2; marker <= 5; marker++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(300));
            RaiseTransportFrame(fakeSource, 640, 360, new[] { marker }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds());
        }

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.CatchUpOnly,
            ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 1600,
            recentStaleDrops: 3);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        clock.Advance(TimeSpan.FromMilliseconds(600));
        RaiseTransportFrame(fakeSource, 640, 360, new byte[] { 6 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds());

        probe.ReleaseBlockedSends();
        await AwaitCompletesAsync(
            probe.WaitForPayloadCountAsync(2, TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(2),
            "remote catch-up freshest payloads");

        var payloads = probe.GetRecentPayloadsSnapshot();
        Assert.Equal(2, payloads.Length);
        var firstFragment = Assert.Single(ExpandFragmentsFromPayload(payloads[0]));
        var secondFragment = Assert.Single(ExpandFragmentsFromPayload(payloads[1]));
        Assert.Equal(new byte[] { 1 }, firstFragment.Data);
        Assert.Equal(new byte[] { 6 }, secondFragment.Data);
        Assert.Contains("remote_catch_up_only", fakeSource.KeyFrameRequestReasons);
        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.True(coordinator.GetMetricsSnapshot().FramesDropped >= 3);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_BootstrapGrace_SuppressesAgeOnlyCatchUpBeforeFirstVisibleApply()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 20, 8, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-bootstrap-grace", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "bootstrap grace start");

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            CurrentStreamEpoch: 81,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 32));

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.ReduceFps,
            ScreenSharePressureProtocol.PressureReasonHighFrameAge,
            observedFrameAgeMs: 1500,
            recentStaleDrops: 0,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 0,
            currentEpochNeedMoreInputCount: 0,
            lastVisibleApplyFrameId: null,
            appliedHeadFrameId: null,
            steadyVisibleProgressActive: false,
            stableVisibleHeadFrameId: null,
            framesAppliedSinceLastGap: 0);

        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.True(GetPrivateFieldValue<long>(coordinator, "bootstrapGraceSuppressedCatchUpCount") > 0);
        Assert.Equal("bootstrap_grace", GetPrivateFieldValue<string>(coordinator, "lastRemoteHighFrameAgeCatchUpSuppressionReason"));
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_SenderLocalFreshnessPressure_EntersReducedModeEvenWhenTransportQueueIsEmpty()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            PendingRawFrameCount: 1,
            OldestPendingRawFrameAgeMs: 1350,
            LastCaptureToEncodeStartAgeMs: 1125,
            LastEncodeDurationMs: 320,
            SupersededPendingRawFrameCount: 3));

        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 18, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-source-freshness", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "sender freshness start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource);

        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
        Assert.Equal(0, fakeSource.PurgePendingRawFramesCallCount);
        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);

        var logText = LocalOperationalLog.GetRecentLogText();
        Assert.Contains("event=screenshare_freshness_summary", logText, StringComparison.Ordinal);
        Assert.Contains("source_pending_raw_frames=", logText, StringComparison.Ordinal);
        Assert.Contains("capture_to_encode_start_age_ms=", logText, StringComparison.Ordinal);
        Assert.Contains("last_preprocess_duration_ms=", logText, StringComparison.Ordinal);
        Assert.Contains("last_transform_encode_duration_ms=", logText, StringComparison.Ordinal);
        Assert.Contains("last_encode_total_duration_ms=", logText, StringComparison.Ordinal);
        Assert.Contains("encoder_path=", logText, StringComparison.Ordinal);
        Assert.Contains("encoder_profile=", logText, StringComparison.Ordinal);
        Assert.Contains("source_superseded_pending_frames=", logText, StringComparison.Ordinal);
        Assert.Contains("raw_frames_deferred_to_encode_slot=", logText, StringComparison.Ordinal);
        Assert.Contains("raw_frames_replaced_before_encode_slot=", logText, StringComparison.Ordinal);
        Assert.Contains("raw_encode_slot_empty_count=", logText, StringComparison.Ordinal);
        Assert.Contains("raw_slot_coalescing_active=", logText, StringComparison.Ordinal);
        Assert.Contains("promotion_capture_to_send_budget_ms=", logText, StringComparison.Ordinal);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RepeatedSevereSenderPressure_DoesNotSpamKeyframesAfterCatchUpEntry()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 18, 10, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-sender-rate-limit", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "sender rate-limit start");
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            PendingRawFrameCount: 1,
            OldestPendingRawFrameAgeMs: 1500,
            LastCaptureToEncodeStartAgeMs: 1400,
            LastEncodeDurationMs: 340,
            SupersededPendingRawFrameCount: 2));
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            PendingRawFrameCount: 1,
            OldestPendingRawFrameAgeMs: 1600,
            LastCaptureToEncodeStartAgeMs: 1450,
            LastEncodeDurationMs: 360,
            SupersededPendingRawFrameCount: 3));
        clock.Advance(TimeSpan.FromSeconds(1));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            PendingRawFrameCount: 1,
            OldestPendingRawFrameAgeMs: 1700,
            LastCaptureToEncodeStartAgeMs: 1500,
            LastEncodeDurationMs: 370,
            SupersededPendingRawFrameCount: 4));
        clock.Advance(TimeSpan.FromSeconds(1));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            PendingRawFrameCount: 1,
            OldestPendingRawFrameAgeMs: 1800,
            LastCaptureToEncodeStartAgeMs: 1550,
            LastEncodeDurationMs: 380,
            SupersededPendingRawFrameCount: 5));
        clock.Advance(TimeSpan.FromMilliseconds(2100));
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal(
            0,
            fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "sender_profile_changed", StringComparison.Ordinal)));
        Assert.Equal(
            1,
            fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "catch_up_mode_enter", StringComparison.Ordinal)));
        Assert.Equal(1, fakeSource.PurgePendingRawFramesCallCount);
        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_SevereBridgeHealthDegradation_EntersDegradedModeImmediately()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var backpressureProbe = new FakeScreenShareBackpressureProbe
        {
            RecentHealthIssueCount = 3,
            IsHealthSeverelyDegraded = true,
            IsCongested = true,
            QueueDepth = 10,
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 2, 12, 30, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock,
            transportBackpressureProbeResolver: () => backpressureProbe);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-severe-bridge-health", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "severe bridge health start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);

        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { 1, 2, 3 },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));

        await Task.Delay(50);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_UnstablePressure_DropsQueuedFrames_ToKeepFreshestOnly()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new FakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 12, 45, 0, TimeSpan.Zero));
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 4, maxInFlight: 1, startBlocked: true);
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-freshest-only", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "freshest-only start");

        RaiseTransportFrame(fakeSource, 640, 360, new byte[] { 1 });
        await AwaitCompletesAsync(
            probe.FirstSendStarted,
            TimeSpan.FromSeconds(2),
            "freshest-only blocked first send");

        for (byte marker = 2; marker <= 4; marker++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            RaiseTransportFrame(fakeSource, 640, 360, new[] { marker });
        }

        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        clock.Advance(TimeSpan.FromMilliseconds(500));
        RaiseTransportFrame(fakeSource, 640, 360, new byte[] { 5 });

        probe.ReleaseBlockedSends();
        await AwaitCompletesAsync(
            probe.WaitForPayloadCountAsync(2, TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(2),
            "freshest-only final payloads");
        await Task.Delay(100);

        var payloads = probe.GetRecentPayloadsSnapshot();
        Assert.Equal(2, probe.PayloadsSent);
        Assert.Equal(2, payloads.Length);

        var firstFragment = Assert.Single(ExpandFragmentsFromPayload(payloads[0]));
        var secondFragment = Assert.Single(ExpandFragmentsFromPayload(payloads[1]));
        Assert.Equal(new byte[] { 1 }, firstFragment.Data);
        Assert.Equal(new byte[] { 5 }, secondFragment.Data);

        var senderMetrics = coordinator.GetMetricsSnapshot();
        Assert.True(senderMetrics.FramesDropped >= 3, $"Expected unstable freshest-only mode to drop stale queued frames. Metrics={senderMetrics}");
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_FileTransferDegradedHint_DropsBacklogAndReducesSenderFps()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 13, 12, 0, 0, TimeSpan.Zero));
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 4, maxInFlight: 1, startBlocked: true);
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-filetransfer-pressure", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "file-transfer degraded screenshare start");

        coordinator.SetFileTransferDegradedHint(true);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);

        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 1 }));
        await AwaitCompletesAsync(
            probe.FirstSendStarted,
            TimeSpan.FromSeconds(2),
            "degraded sender blocked first send");

        for (byte marker = 2; marker <= 5; marker++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new[] { marker }));
        }

        probe.ReleaseBlockedSends();
        await AwaitCompletesAsync(
            probe.WaitForPayloadCountAsync(2, TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(2),
            "degraded sender freshest payloads");

        var payloads = probe.GetRecentPayloadsSnapshot();
        Assert.Equal(2, probe.PayloadsSent);
        Assert.Equal(2, payloads.Length);
        var firstFragment = Assert.Single(ExpandFragmentsFromPayload(payloads[0]));
        var secondFragment = Assert.Single(ExpandFragmentsFromPayload(payloads[1]));
        Assert.Equal(new byte[] { 1 }, firstFragment.Data);
        Assert.Equal(new byte[] { 5 }, secondFragment.Data);

        var senderMetrics = coordinator.GetMetricsSnapshot();
        Assert.Equal("reduced", senderMetrics.FreshnessMode);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_FileTransferDegradedHint_StaysStickyBeforeExiting()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: static (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-sticky-degraded", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "sticky degraded start");

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        coordinator.SetFileTransferDegradedHint(true);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);

        coordinator.SetFileTransferDegradedHint(false);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);

        DisableStartupWarmupForCoordinatorOnly(coordinator, targetFps: 5);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 30));
        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new[] { (byte)(10 + i) }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
            await WaitUntilAsync(
                () =>
                {
                    var metrics = coordinator.GetMetricsSnapshot();
                    return metrics.ChunksSent >= i + 1 && metrics.LastCaptureToSendAgeMs >= 0;
                },
                TimeSpan.FromSeconds(2));
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_FileTransferCatchUpOnlyHint_KeepsSenderDegradedUntilReleased()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 15, 11, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: static (_, _) => Task.CompletedTask,
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-catchup-pressure", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "catch-up pressure start");

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        coordinator.SetFileTransferCatchUpOnlyHint(true);
        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);

        clock.Advance(TimeSpan.FromSeconds(3));
        coordinator.SetFileTransferCatchUpOnlyHint(true);
        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);

        coordinator.SetFileTransferCatchUpOnlyHint(false);
        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 30));

        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new[] { (byte)(20 + i) }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);

        for (var i = 0; i < 5; i++)
        {
            SendHealthyRemotePressure(coordinator);
            clock.Advance(TimeSpan.FromMilliseconds(250));
            fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new[] { (byte)(30 + i) }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
            await Task.Delay(20);
            autoTuneTick.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
    }

}
