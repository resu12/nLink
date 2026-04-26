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
public sealed class TransportScreenShareCoordinatorAutoTuneTests : ScreenShareCoordinatorTestBase, IClassFixture<ScreenShareCoordinatorFixture>
{
    public TransportScreenShareCoordinatorAutoTuneTests(ScreenShareCoordinatorFixture fixture) : base(fixture)
    {
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_AutoTuneHint_ResetsAcrossStopRestart()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new AdaptiveFakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.0),
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 6, 10, 0, 0, TimeSpan.Zero));
        var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        try
        {
            await AwaitCompletesAsync(
                coordinator.StartAsync("session-auto-1", CancellationToken.None),
                TimeSpan.FromSeconds(2),
                "auto-tune cycle 1 start");

            Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);

            DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource);

            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { 1, 2, 3 },
                    capturedTsUtcMs: clock.UtcNow.AddMilliseconds(-1000).ToUnixTimeMilliseconds()));

            await Task.Delay(50);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

            Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
            Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
            Assert.Contains(FeatureFlags.ScreenShareTransportMaxFps, fakeSource.CaptureFrameRateHints);

            await AwaitCompletesAsync(
                coordinator.StopAsync(sendStopMessage: false, reason: "restart", CancellationToken.None),
                TimeSpan.FromSeconds(2),
                "auto-tune cycle 1 stop");

            await AwaitCompletesAsync(
                coordinator.StartAsync("session-auto-2", CancellationToken.None),
                TimeSpan.FromSeconds(2),
                "auto-tune cycle 2 start");

            Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);

            DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource);

            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { 4, 5, 6 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));

            await Task.Delay(50);
            autoTuneTick.Invoke(coordinator, Array.Empty<object>());

            Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        }
        finally
        {
            await AwaitCompletesAsync(
                coordinator.DisposeAsync().AsTask(),
                TimeSpan.FromSeconds(2),
                "auto-tune final dispose");
        }
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_AutoTuneHint_CoalescesWithinSendSlot_WithoutRateGateDrops()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new AdaptiveFakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.0),
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 11, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-rate-pressure", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "rate pressure start");

        Assert.InRange(fakeSource.LastCaptureFrameRateHint, 5, 8);
        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource);

        for (var i = 0; i < 5; i++)
        {
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(i + 1), 7, 9 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
            clock.Advance(TimeSpan.FromMilliseconds(40));
        }

        await WaitUntilAsync(
            () => coordinator.GetMetricsSnapshot().FramesCaptured >= 1,
            TimeSpan.FromSeconds(2));
        var burstMetrics = coordinator.GetMetricsSnapshot();
        Assert.Equal(0, burstMetrics.FramesDroppedByRateGate);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.InRange(fakeSource.LastCaptureFrameRateHint, 5, 8);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_AutoTuneHint_ReducesOnQueuePressure_BeforeAnySuccessfulSend()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new AdaptiveFakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.0),
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 11, 30, 0, TimeSpan.Zero));
        var sendEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: async (_, ct) =>
            {
                sendEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            },
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-queue-pressure", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "queue pressure start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource);

        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { 1, 2, 3 },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
        await AwaitCompletesAsync(
            sendEntered.Task,
            TimeSpan.FromSeconds(2),
            "queue pressure blocked send entered");

        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(10 + i), 2, 3 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
        }

        await Task.Delay(50);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.InRange(fakeSource.LastCaptureFrameRateHint, 5, 8);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_AutoTuneHint_MildQueuePressure_EntersReducedModeBeforeCatchUp()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new AdaptiveFakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.0),
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 11, 45, 0, TimeSpan.Zero));
        var sendEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: async (_, ct) =>
            {
                sendEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            },
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-quality-protected", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "quality protected start");

        DisableStartupWarmupForAutoTuneTests(
            coordinator,
            fakeSource,
            initialFpsHint: TransportClarityFpsFloorForTesting);

        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { 1, 2, 3 },
                capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
        await AwaitCompletesAsync(
            sendEntered.Task,
            TimeSpan.FromSeconds(2),
            "quality protected blocked send entered");

        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(20 + i), 4, 5 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.InRange(fakeSource.LastCaptureFrameRateHint, 5, 8);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_AutoTuneHint_EscalatesToCatchUpAfterSustainedSeverePressure()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new AdaptiveFakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.0),
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 12, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-bandwidth-reduced", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "bandwidth reduced start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeTotalDurationMs: 30));
        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { 1, 2, 3 },
                capturedTsUtcMs: clock.UtcNow.AddMilliseconds(-700).ToUnixTimeMilliseconds()));
        await Task.Delay(20);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);

        clock.Advance(TimeSpan.FromSeconds(1));
        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                1280,
                720,
                new byte[] { 4, 5, 6 },
                capturedTsUtcMs: clock.UtcNow.AddMilliseconds(-720).ToUnixTimeMilliseconds()));
        await Task.Delay(20);
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(0, fakeSource.PurgePendingRawFramesCallCount);
        Assert.Contains("catch_up_mode_enter", fakeSource.KeyFrameRequestReasons);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_StartsInReducedProfile_AndPromotesAfterHealthyTicks()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new AdaptiveFakeScreenCaptureSource
        {
            CaptureMetadata = new ScreenCaptureMetadata(
                DisplayId: "primary",
                CaptureRegionPx: new ScreenCapturePixelRect(0, 0, 1920, 1080),
                DpiScale: 1.0),
        };
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 12, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-startup-warmup", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced start");

        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);

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
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 30,
            SupersededPendingRawFrameCount: 9));

        await Task.Delay(50);
        for (var i = 0; i < 5; i++)
        {
            SendHealthyRemotePressure(coordinator);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.Normal, fakeSource.LastTransportTuningLevel);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_NormalMode_RequiresTwoConsecutivePressureTicksToDemoteToReduced()
    {
        using var autoTuneFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_TRANSPORT_AUTOTUNE", "1");
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 12, 7, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-normal-demotion", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "normal demotion start");

        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 30));

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 20,
            LastEncodeTotalDurationMs: 105));

        clock.Advance(TimeSpan.FromMilliseconds(500));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 90 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
        await Task.Delay(20);
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);

        clock.Advance(TimeSpan.FromMilliseconds(500));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(640, 360, new byte[] { 91 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds()));
        await Task.Delay(20);
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_UsesProfileRelativeEncodeBudgetForPromotion()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 21, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-promotion-budget", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced promotion budget start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 3; i++)
        {
            SendHealthyRemotePressure(coordinator);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(40 + i), 2, 3 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_UsesReportedHelperApplyCountForPromotion()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 13, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-promotion-helper-apply-count", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced promotion helper apply count start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        SendHealthyRemotePressure(
            coordinator,
            currentEpochWarmupActive: false,
            currentEpochApplyCount: 3,
            currentEpochNeedMoreInputCount: 0);

        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(48 + i), 2, 3 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_UsesStableVisibleProgressProofForPromotion()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-promotion-stable-progress", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced promotion stable-progress start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 6; i++)
        {
            SendHealthyRemotePressure(
                coordinator,
                currentEpochWarmupActive: false,
                currentEpochApplyCount: 2,
                currentEpochNeedMoreInputCount: 0,
                steadyVisibleProgressActive: true,
                stableVisibleHeadFrameId: 12 + i,
                framesAppliedSinceLastGap: 8 + i);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(80 + i), 2, 3 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_OverBudgetEncode_DoesNotPromote()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 21, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-promotion-over-budget", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced promotion over-budget start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 22,
            LastEncodeTotalDurationMs: 80,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 4; i++)
        {
            SendHealthyRemotePressure(coordinator);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(60 + i), 4, 5 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        var overBudgetMetrics = coordinator.GetMetricsSnapshot();
        Assert.True(overBudgetMetrics.PromotionBlockerEncodeBudgetTicks >= 1);
        Assert.True(GetPrivateFieldValue<long>(coordinator, "promotionEncodeSoftSpikeCount") >= 1);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_StableVisibleProgressProof_DoesNotBypassEncodeBudget()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 9, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-promotion-stable-progress-over-budget", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced promotion stable-progress over-budget start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 22,
            LastEncodeTotalDurationMs: 80,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 4; i++)
        {
            SendHealthyRemotePressure(
                coordinator,
                currentEpochWarmupActive: false,
                currentEpochApplyCount: 2,
                currentEpochNeedMoreInputCount: 0,
                steadyVisibleProgressActive: true,
                stableVisibleHeadFrameId: 20 + i,
                framesAppliedSinceLastGap: 8 + i);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(96 + i), 4, 5 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
        Assert.True(coordinator.GetMetricsSnapshot().PromotionBlockerEncodeBudgetTicks >= 1);
        Assert.True(GetPrivateFieldValue<long>(coordinator, "promotionBlockedByEncodeBudgetAloneCount") >= 1);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_SingleModerateEncodeSpike_DoesNotResetHealthyTicks()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 18, 10, 30, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-promotion-soft-encode-spike", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced promotion soft encode-spike start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);

        var encodeTotals = new[] { 80, 60, 60 };
        for (var i = 0; i < encodeTotals.Length; i++)
        {
            fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
                LastEncodeDurationMs: 18,
                LastEncodeTotalDurationMs: encodeTotals[i],
                CurrentStreamEpoch: 1));
            SendHealthyRemotePressure(
                coordinator,
                currentEpochWarmupActive: false,
                currentEpochApplyCount: 2,
                currentEpochNeedMoreInputCount: 0,
                steadyVisibleProgressActive: true,
                stableVisibleHeadFrameId: 20 + i,
                framesAppliedSinceLastGap: 8 + i);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(112 + i), 7, 8 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
        Assert.True(GetPrivateFieldValue<long>(coordinator, "promotionEncodeSoftSpikeCount") >= 1);
        Assert.True(GetPrivateFieldValue<long>(coordinator, "promotionEncodeSoftSpikeResetSuppressedCount") >= 1);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_PacedCaptureAgeBudget_AllowsPromotionAt180Ms()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 18, 0, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-promotion-capture-age-allow", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced promotion capture-age allow start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 3; i++)
        {
            SetLastCaptureToSendAgeMs(coordinator, 180);
            SendHealthyRemotePressure(coordinator);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_PacedCaptureAgeBudget_BlocksPromotionAt260Ms()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 18, 5, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-promotion-capture-age-block", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced promotion capture-age block start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 4; i++)
        {
            SetLastCaptureToSendAgeMs(coordinator, 260);
            SendHealthyRemotePressure(coordinator);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_RawRemoteAgeSpike_DoesNotResetHealthyPromotion()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 21, 10, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-raw-age-spike", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced raw-age spike start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        SendHealthyRemotePressure(coordinator);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(1280, 720, new byte[] { 70, 2, 3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: 1));
        await Task.Delay(20);
        autoTuneTick!.Invoke(coordinator, Array.Empty<object>());

        SendHealthyRemotePressure(coordinator, observedFrameAgeMs: 700);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(1280, 720, new byte[] { 71, 2, 3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: 1));
        await Task.Delay(20);
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        SendHealthyRemotePressure(coordinator);
        clock.Advance(TimeSpan.FromMilliseconds(500));
        fakeSource.RaiseFrame(CreateTransportFrameEventArgs(1280, 720, new byte[] { 72, 2, 3 }, capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(), streamEpoch: 1));
        await Task.Delay(20);
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_BlocksPromotionUntilHelperWarmupCompletes()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 21, 15, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-helper-warmup-block", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "helper warmup promotion block start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 4; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(80 + i), 6, 7 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Contains("helper_warmup", LocalOperationalLog.GetRecentLogText(), StringComparison.Ordinal);
        var helperWarmupMetrics = coordinator.GetMetricsSnapshot();
        Assert.True(helperWarmupMetrics.PromotionBlockerHelperWarmupTicks >= 1);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedMode_SlowApplyCadencePressure_DoesNotPromote()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 14, 12, 10, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-reduced-slow-cadence-block", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "reduced slow cadence block start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 60,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 4; i++)
        {
            coordinator.SetRemotePressureState(
                ScreenShareRemotePressureMode.None,
                ScreenSharePressureProtocol.PressureReasonSlowApplyCadence,
                observedFrameAgeMs: 180,
                recentStaleDrops: 0);
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(90 + i), 2, 3 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Contains("helper_pressure", LocalOperationalLog.GetRecentLogText(), StringComparison.Ordinal);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_NormalMode_UsesProfileRelativeEncodeBudgetForDemotion()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 21, 20, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-normal-demotion-budget", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "normal demotion budget start");

        DisableStartupWarmupForCoordinatorOnly(coordinator);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 20,
            LastEncodeTotalDurationMs: 95,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 2; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(90 + i), 8, 9 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);

        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 24,
            LastEncodeTotalDurationMs: 105,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 2; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(100 + i), 10, 11 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: 1));
            await Task.Delay(20);
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("reduced", coordinator.GetMetricsSnapshot().FreshnessMode);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ReducedToNormalProfileTransition_StartsGrace_PurgesBacklog_AndRequestsSingleKeyframe()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 20, 15, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-transition-grace-start", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "transition grace start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            PendingRawFrameCount: 2,
            OldestPendingRawFrameAgeMs: 25,
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 30,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 3; i++)
        {
            SendHealthyRemotePressure(coordinator);
            var chunksSentBefore = coordinator.GetMetricsSnapshot().ChunksSent;
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(150 + i), 4, 5 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: fakeSource.GetFreshnessMetricsSnapshot().CurrentStreamEpoch));
            await WaitUntilAsync(
                () =>
                {
                    var metrics = coordinator.GetMetricsSnapshot();
                    return metrics.ChunksSent > chunksSentBefore && metrics.LastCaptureToSendAgeMs >= 0;
                },
                TimeSpan.FromSeconds(2));
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.Normal, fakeSource.LastTransportTuningLevel);
        Assert.Equal(1, fakeSource.PurgePendingRawFramesCallCount);
        Assert.Equal(
            1,
            fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "sender_profile_transition", StringComparison.Ordinal)));
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "transitionActive"));
        Assert.True(GetPrivateFieldValue<long>(coordinator, "transitionStreamEpoch") > 0);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ProfileTransitionGrace_BlocksRemoteAgeOnlyDemotion()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 20, 25, 0, TimeSpan.Zero));
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: clock);

        var autoTuneTick = typeof(TransportScreenShareCoordinator).GetMethod(
            "OnAutoTuneTimerTick",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(autoTuneTick);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-transition-grace-block", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "transition grace block start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 30,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 3; i++)
        {
            SendHealthyRemotePressure(coordinator);
            var chunksSentBefore = coordinator.GetMetricsSnapshot().ChunksSent;
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(170 + i), 2, 3 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: fakeSource.GetFreshnessMetricsSnapshot().CurrentStreamEpoch));
            await WaitUntilAsync(
                () =>
                {
                    var metrics = coordinator.GetMetricsSnapshot();
                    return metrics.ChunksSent > chunksSentBefore && metrics.LastCaptureToSendAgeMs >= 0;
                },
                TimeSpan.FromSeconds(2));
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "transitionActive"));

        for (var i = 0; i < 2; i++)
        {
            SendHealthyRemotePressure(coordinator, observedFrameAgeMs: 700);
            autoTuneTick.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(8, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.Normal, fakeSource.LastTransportTuningLevel);
        Assert.Equal(
            1,
            fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "sender_profile_transition", StringComparison.Ordinal)));
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_ProfileTransitionGrace_AllowsSevereCongestionDemotion()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        var backpressureProbe = new FakeScreenShareBackpressureProbe();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 4, 13, 20, 35, 0, TimeSpan.Zero));
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
            coordinator.StartAsync("session-transition-grace-severe", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "transition grace severe start");

        DisableStartupWarmupForAutoTuneTests(coordinator, fakeSource, initialFpsHint: 5);
        fakeSource.SetFreshnessMetrics(new ScreenCaptureFreshnessMetrics(
            LastEncodeDurationMs: 18,
            LastEncodeTotalDurationMs: 30,
            CurrentStreamEpoch: 1));

        for (var i = 0; i < 3; i++)
        {
            SendHealthyRemotePressure(coordinator);
            var chunksSentBefore = coordinator.GetMetricsSnapshot().ChunksSent;
            clock.Advance(TimeSpan.FromMilliseconds(500));
            fakeSource.RaiseFrame(
                CreateTransportFrameEventArgs(
                    1280,
                    720,
                    new byte[] { (byte)(190 + i), 8, 9 },
                    capturedTsUtcMs: clock.UtcNow.ToUnixTimeMilliseconds(),
                    streamEpoch: fakeSource.GetFreshnessMetricsSnapshot().CurrentStreamEpoch));
            await WaitUntilAsync(
                () =>
                {
                    var metrics = coordinator.GetMetricsSnapshot();
                    return metrics.ChunksSent > chunksSentBefore && metrics.LastCaptureToSendAgeMs >= 0;
                },
                TimeSpan.FromSeconds(2));
            autoTuneTick!.Invoke(coordinator, Array.Empty<object>());
        }

        Assert.Equal("normal", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.True(GetPrivateFieldValue<bool>(coordinator, "transitionActive"));

        backpressureProbe.IsSeverelyCongested = true;
        backpressureProbe.QueueDepth = 18;
        backpressureProbe.QueuedBytes = 256 * 1024;
        backpressureProbe.RecentDropCount = 2;
        autoTuneTick.Invoke(coordinator, Array.Empty<object>());

        Assert.Equal("catch_up", coordinator.GetMetricsSnapshot().FreshnessMode);
        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);
        Assert.Equal(
            2,
            fakeSource.KeyFrameRequestReasons.Count(static reason => string.Equals(reason, "sender_profile_transition", StringComparison.Ordinal)));
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_RemotePressureState_OverridesWarmupAndRestoresOnHealthyState()
    {
        var fakeSource = new AdaptiveFakeScreenCaptureSource();
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: (_, _) => Task.CompletedTask,
            clock: new FakeScreenShareClock(new DateTimeOffset(2026, 3, 8, 12, 10, 0, TimeSpan.Zero)));

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-remote-pressure", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            "remote pressure start");

        Assert.Equal(5, fakeSource.LastCaptureFrameRateHint);

        coordinator.SetRemotePressureState(
            ScreenShareRemotePressureMode.CatchUpOnly,
            "high_frame_age",
            observedFrameAgeMs: 1800,
            recentStaleDrops: 4);

        Assert.Equal(3, fakeSource.LastCaptureFrameRateHint);
        Assert.Equal(ScreenShareTransportTuningLevel.BandwidthReduced, fakeSource.LastTransportTuningLevel);

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

}
