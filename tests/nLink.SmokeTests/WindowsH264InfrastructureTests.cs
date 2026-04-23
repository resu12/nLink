using DrawingBitmap = System.Drawing.Bitmap;
using Avalonia.Media.Imaging;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using NLink.App.Configuration;
using NLink.App.Views;
using NLink.App.Services.ScreenCapture;
using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class WindowsH264InfrastructureTests : IClassFixture<ScreenShareCoordinatorFixture>
{
    private readonly ScreenShareCoordinatorFixture fixture;

    public WindowsH264InfrastructureTests(ScreenShareCoordinatorFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenCaptureFactory_Create_H264Capability_ReturnsH264SourceOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = ScreenCaptureFactory.Create(ScreenCapturePipelineKind.H264);
        Assert.Equal("WindowsH264ScreenCaptureSource", source.GetType().Name);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenCaptureFactory_CreateDefault_UsesH264Source_WhenFeatureAndRuntimeSupportEnabled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = ScreenCaptureFactory.CreateDefault(() => true);
        Assert.Equal("WindowsH264ScreenCaptureSource", source.GetType().Name);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenCaptureFactory_CreateDefault_FailsClosed_WhenPreviewRuntimeSupportIsUnavailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = ScreenCaptureFactory.CreateDefault(() => false);
        Assert.IsType<NotSupportedCaptureSource>(source);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsGraphicsCaptureRawSource_IsSupported_OnlyForDisplayTargets()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var displaySource = new WindowsGraphicsCaptureRawSource(ScreenCaptureTargetSelection.PrimaryDisplay);
        Assert.Equal(WindowsGraphicsCaptureRawSource.IsRuntimeSupported(), displaySource.IsSupported);

        var windowSource = new WindowsGraphicsCaptureRawSource(
            new ScreenCaptureTargetSelection(ScreenCaptureTargetMode.Window, null, "ABCDEF", default));
        Assert.False(windowSource.IsSupported);

        var regionSource = new WindowsGraphicsCaptureRawSource(
            new ScreenCaptureTargetSelection(ScreenCaptureTargetMode.Region, "display-1", null, new ScreenCapturePixelRect(0, 0, 100, 100)));
        Assert.False(regionSource.IsSupported);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsGraphicsCaptureRawSource_TryGetCaptureMetadata_UsesExistingCatalogResolution()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = new WindowsGraphicsCaptureRawSource(ScreenCaptureTargetSelection.PrimaryDisplay);
        var resolved = source.TryGetCaptureMetadata(out var metadata);

        Assert.True(resolved);
        Assert.True(metadata.CaptureRegionPx.IsValid);
        Assert.True(metadata.CaptureRegionPx.Width > 0);
        Assert.True(metadata.CaptureRegionPx.Height > 0);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void DesktopDuplicationRawSource_IsSupported_OnlyForDisplayTargets()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var displaySource = new DesktopDuplicationRawSource(ScreenCaptureTargetSelection.PrimaryDisplay);
        Assert.Equal(DesktopDuplicationRawSource.IsRuntimeSupported(), displaySource.IsSupported);

        var windowSource = new DesktopDuplicationRawSource(
            new ScreenCaptureTargetSelection(ScreenCaptureTargetMode.Window, null, "ABCDEF", default));
        Assert.False(windowSource.IsSupported);

        var regionSource = new DesktopDuplicationRawSource(
            new ScreenCaptureTargetSelection(ScreenCaptureTargetMode.Region, "display-1", null, new ScreenCapturePixelRect(0, 0, 100, 100)));
        Assert.False(regionSource.IsSupported);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void DesktopDuplicationRawSource_TryGetCaptureMetadata_UsesExistingCatalogResolution()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var source = new DesktopDuplicationRawSource(ScreenCaptureTargetSelection.PrimaryDisplay);
        var resolved = source.TryGetCaptureMetadata(out var metadata);

        Assert.True(resolved);
        Assert.True(metadata.CaptureRegionPx.IsValid);
        Assert.True(metadata.CaptureRegionPx.Width > 0);
        Assert.True(metadata.CaptureRegionPx.Height > 0);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsH264EncodePolicy_TransportNormalProfile_CapsTo1440x810_AndUsesIpOnlyBudget()
    {
        using var scaleOverride = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_SCALE", "1");

        var profile = WindowsH264EncodePolicy.ResolveProfile(
            sourceWidth: 1920,
            sourceHeight: 1080,
            targetFramesPerSecond: FeatureFlags.ScreenShareTransportMaxFps,
            tuningLevel: ScreenShareTransportTuningLevel.Normal,
            transportIpOnly: true);

        Assert.Equal("normal", profile.ProfileName);
        Assert.Equal(1440, profile.Width);
        Assert.Equal(810, profile.Height);
        Assert.Equal(8, profile.TargetFramesPerSecond);
        Assert.True(profile.TransportIpOnly);
        Assert.InRange(profile.TargetBitrate, 4_500_000u, 6_000_000u);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsH264EncodePolicy_TransportBandwidthReducedProfile_CapsTo1280x720_AndUsesIpOnlyBudget()
    {
        using var scaleOverride = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_SCALE", "1");

        var profile = WindowsH264EncodePolicy.ResolveProfile(
            sourceWidth: 1920,
            sourceHeight: 1080,
            targetFramesPerSecond: 5,
            tuningLevel: ScreenShareTransportTuningLevel.BandwidthReduced,
            transportIpOnly: true);

        Assert.Equal("reduced", profile.ProfileName);
        Assert.Equal(1280, profile.Width);
        Assert.Equal(720, profile.Height);
        Assert.Equal(5, profile.TargetFramesPerSecond);
        Assert.True(profile.TransportIpOnly);
        Assert.InRange(profile.TargetBitrate, 2_000_000u, 3_000_000u);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsH264EncodePolicy_TransportBandwidthReducedProfile_HonorsRequestedCatchUpFps()
    {
        using var scaleOverride = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_SCALE", "1");

        var profile = WindowsH264EncodePolicy.ResolveProfile(
            sourceWidth: 1920,
            sourceHeight: 1080,
            targetFramesPerSecond: 3,
            tuningLevel: ScreenShareTransportTuningLevel.BandwidthReduced,
            transportIpOnly: true);

        Assert.Equal("reduced", profile.ProfileName);
        Assert.Equal(1280, profile.Width);
        Assert.Equal(720, profile.Height);
        Assert.Equal(3, profile.TargetFramesPerSecond);
        Assert.True(profile.TransportIpOnly);
        Assert.InRange(profile.TargetBitrate, 2_000_000u, 3_000_000u);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsH264EncodePolicy_TransportBandwidthReducedProfile_ClampsRequestedFpsToReducedCeiling()
    {
        using var scaleOverride = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_SCALE", "1");

        var profile = WindowsH264EncodePolicy.ResolveProfile(
            sourceWidth: 1920,
            sourceHeight: 1080,
            targetFramesPerSecond: 7,
            tuningLevel: ScreenShareTransportTuningLevel.BandwidthReduced,
            transportIpOnly: true);

        Assert.Equal("reduced", profile.ProfileName);
        Assert.Equal(5, profile.TargetFramesPerSecond);
        Assert.True(profile.TransportIpOnly);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsH264EncodePolicy_ReducedProfile_LowersBitrateWhenCatchUpFpsDrops()
    {
        using var scaleOverride = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_SCALE", "1");

        var catchUpProfile = WindowsH264EncodePolicy.ResolveProfile(
            sourceWidth: 1920,
            sourceHeight: 1080,
            targetFramesPerSecond: 3,
            tuningLevel: ScreenShareTransportTuningLevel.BandwidthReduced,
            transportIpOnly: true);
        var reducedProfile = WindowsH264EncodePolicy.ResolveProfile(
            sourceWidth: 1920,
            sourceHeight: 1080,
            targetFramesPerSecond: 5,
            tuningLevel: ScreenShareTransportTuningLevel.BandwidthReduced,
            transportIpOnly: true);

        Assert.Equal(3, catchUpProfile.TargetFramesPerSecond);
        Assert.Equal(5, reducedProfile.TargetFramesPerSecond);
        Assert.True(catchUpProfile.TargetBitrate < reducedProfile.TargetBitrate);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsH264EncodePolicy_ManualScale_RemainsAnUpperBound()
    {
        using var scaleOverride = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_SCALE", "0.5");

        var profile = WindowsH264EncodePolicy.ResolveProfile(
            sourceWidth: 1920,
            sourceHeight: 1080,
            targetFramesPerSecond: 4,
            tuningLevel: ScreenShareTransportTuningLevel.BandwidthReduced,
            transportIpOnly: true);

        Assert.Equal(640, profile.Width);
        Assert.Equal(360, profile.Height);
        Assert.True(profile.TransportIpOnly);
        Assert.InRange(profile.TargetBitrate, 2_000_000u, 3_000_000u);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsH264EncodePolicy_PreviewNormalProfile_RemainsInterFrameBudget()
    {
        using var scaleOverride = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_SCALE", "1");

        var profile = WindowsH264EncodePolicy.ResolveProfile(
            sourceWidth: 1920,
            sourceHeight: 1080,
            targetFramesPerSecond: FeatureFlags.ScreenShareTransportMaxFps,
            tuningLevel: ScreenShareTransportTuningLevel.Normal,
            transportIpOnly: false);

        Assert.Equal("normal", profile.ProfileName);
        Assert.False(profile.TransportIpOnly);
        Assert.Equal(1280, profile.Width);
        Assert.Equal(720, profile.Height);
        Assert.InRange(profile.TargetBitrate, 1_800_000u, 2_400_000u);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareSurfaceView_NearNativePresentation_UsesNoBitmapSmoothing()
    {
        var interpolationMode = ScreenShareSurfaceView.ResolveInterpolationModeForPresentation(
            frameWidth: 1440,
            frameHeight: 810,
            viewportWidth: 1430,
            viewportHeight: 800,
            renderScaling: 1d);

        Assert.Equal(BitmapInterpolationMode.None, interpolationMode);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareSurfaceView_DownscaledPresentation_UsesHighQualityInterpolation()
    {
        var interpolationMode = ScreenShareSurfaceView.ResolveInterpolationModeForPresentation(
            frameWidth: 1440,
            frameHeight: 810,
            viewportWidth: 900,
            viewportHeight: 506,
            renderScaling: 1d);

        Assert.Equal(BitmapInterpolationMode.HighQuality, interpolationMode);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task EncodedFrameBitmapDecoder_H264Decoder_IsUsedWhenRegistered()
    {
        await fixture.Session.Dispatch(() =>
        {
            var h264Decoder = new FakeH264BitmapDecoder();
            var decoder = new EncodedFrameBitmapDecoder(_ => throw new InvalidOperationException("jpeg should not be used"), h264Decoder);
            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "session",
                StreamEpoch = 5,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            decoder.ConfigureH264Stream(config);
            using var bitmap = decoder.Decode(new EncodedFrameDecodeRequest("h264", new byte[] { 9, 8, 7 }, IsKeyFrame: true, StreamEpoch: 5));

            Assert.Equal(1, h264Decoder.ConfigureCallCount);
            Assert.Equal(5, h264Decoder.LastConfiguredEpoch);
            Assert.Equal(1, h264Decoder.DecodeCallCount);
            Assert.Equal(5, h264Decoder.LastDecodedEpoch);
            Assert.Equal(1, bitmap.PixelSize.Width);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task EncodedFrameBitmapDecoder_H264Reset_WaitsForInFlightDecode()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var h264Decoder = new BlockingH264BitmapDecoder();
            var decoder = new EncodedFrameBitmapDecoder(_ => throw new InvalidOperationException("jpeg should not be used"), h264Decoder);

            var decodeTask = Task.Run(() =>
            {
                using var bitmap = decoder.Decode(new EncodedFrameDecodeRequest("h264", new byte[] { 3 }, IsKeyFrame: true, StreamEpoch: 9));
                Assert.True(bitmap.PixelSize.Width > 0);
            });

            await h264Decoder.DecodeStarted.WaitAsync(TimeSpan.FromSeconds(2));

            var resetTask = Task.Run(decoder.ResetH264Stream);
            await Task.Delay(100);
            Assert.False(h264Decoder.ResetStarted.Task.IsCompleted);

            h264Decoder.ReleaseDecode();
            await decodeTask.WaitAsync(TimeSpan.FromSeconds(2));
            await resetTask.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, h264Decoder.ResetCallCount);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsH264ScreenCaptureSource_FakeRawSourceAndEncoder_RaisesH264Frame()
    {
        await using var rawSource = new FakeWindowsRawCaptureSource();
        await using var encoder = new FakeWindowsH264FrameEncoder();
        await using var source = new WindowsH264ScreenCaptureSource(
            ScreenCaptureTargetSelection.PrimaryDisplay,
            rawCaptureSourceFactory: () => rawSource,
            encoderFactory: () => encoder);

        ScreenCaptureFrameEventArgs? received = null;
        source.FrameArrived += (_, frame) => received = frame;

        await source.StartAsync(CancellationToken.None);
        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(4, 3), capturedTsUtcMs: 123));
        await Task.Delay(50);
        await source.StopAsync();

        Assert.NotNull(received);
        Assert.Equal("h264", received!.Encoding);
        Assert.True(received.IsKeyFrame);
        Assert.Equal(1, received.StreamEpoch);
        Assert.Equal(4, received.Width);
        Assert.Equal(3, received.Height);
        Assert.NotNull(received.StreamConfig);
        Assert.Equal(1, received.StreamConfig!.StreamEpoch);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsH264ScreenCaptureSource_PreviewLatestOnlyRawFrameGate_EncodesFirstAndNewestPendingFrame()
    {
        await using var rawSource = new FakeWindowsRawCaptureSource();
        await using var encoder = new BlockingWindowsH264FrameEncoder();
        await using var source = new WindowsH264ScreenCaptureSource(
            ScreenCaptureTargetSelection.PrimaryDisplay,
            rawCaptureSourceFactory: () => rawSource,
            encoderFactory: () => encoder);

        var received = new List<ScreenCaptureFrameEventArgs>();
        source.FrameArrived += (_, frame) => received.Add(frame);

        await source.StartAsync(CancellationToken.None);
        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(1, 1), capturedTsUtcMs: 100));
        await encoder.FirstEncodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(2, 1), capturedTsUtcMs: 101));
        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(3, 1), capturedTsUtcMs: 102));

        var freshnessSource = Assert.IsAssignableFrom<IScreenCaptureFreshnessMetricsSource>(source);
        await WaitUntilAsync(
            () => freshnessSource.GetFreshnessMetricsSnapshot().PendingRawFrameCount == 1 &&
                  freshnessSource.GetFreshnessMetricsSnapshot().SupersededPendingRawFrameCount == 1,
            TimeSpan.FromSeconds(2));

        encoder.ReleaseFirstEncode();
        await WaitUntilAsync(() => received.Count == 2, TimeSpan.FromSeconds(2));

        Assert.Equal(new[] { 1, 3 }, received.Select(static frame => frame.Width).ToArray());
        Assert.Equal(new[] { 1, 3 }, encoder.EncodedWidths.ToArray());
        Assert.Equal(0, freshnessSource.GetFreshnessMetricsSnapshot().PendingRawFrameCount);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsH264ScreenCaptureSource_TransportRawCapture_StartsEncodingWithoutWaitingForTransportPacingSlot()
    {
        await using var rawSource = new FakeWindowsRawCaptureSource();
        await using var encoder = new FakeWindowsH264FrameEncoder();
        await using var source = new WindowsH264ScreenCaptureSource(
            ScreenCaptureTargetSelection.PrimaryDisplay,
            rawCaptureSourceFactory: () => rawSource,
            encoderFactory: () => encoder,
            sourceRole: "transport");

        source.SetCaptureFrameRateHint(1);
        source.SetTransportTuningLevel(ScreenShareTransportTuningLevel.BandwidthReduced);

        var received = new List<ScreenCaptureFrameEventArgs>();
        source.FrameArrived += (_, frame) => received.Add(frame);

        await source.StartAsync(CancellationToken.None);
        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(1, 1), capturedTsUtcMs: 100));
        await WaitUntilAsync(() => received.Count == 1, TimeSpan.FromSeconds(2));

        var startedAt = Stopwatch.StartNew();
        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(2, 1), capturedTsUtcMs: 101));

        await WaitUntilAsync(() => received.Count == 2, TimeSpan.FromSeconds(2));
        startedAt.Stop();

        var freshnessSource = Assert.IsAssignableFrom<IScreenCaptureFreshnessMetricsSource>(source);
        var metrics = freshnessSource.GetFreshnessMetricsSnapshot();

        Assert.True(
            startedAt.Elapsed < TimeSpan.FromMilliseconds(600),
            $"Expected transport raw capture to encode immediately without a transport pacing wait, but elapsed {startedAt.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.Equal(new[] { 1, 2 }, received.Select(static frame => frame.Width).ToArray());
        Assert.Equal(new[] { 1, 2 }, encoder.EncodedWidths.ToArray());
        Assert.Equal(0, metrics.PendingRawFrameCount);
        Assert.Equal(0, metrics.SupersededPendingRawFrameCount);
        Assert.Equal(0, metrics.RawFramesDeferredToEncodeSlot);
        Assert.Equal(0, metrics.RawFramesReplacedBeforeEncodeSlot);
        Assert.False(metrics.RawSlotCoalescingActive);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsH264ScreenCaptureSource_TransportRawCapture_ReflectsCatchUpTargetFpsInRuntimeMetrics()
    {
        await using var rawSource = new FakeWindowsRawCaptureSource();
        await using var encoder = new FakeWindowsH264FrameEncoder();
        await using var source = new WindowsH264ScreenCaptureSource(
            ScreenCaptureTargetSelection.PrimaryDisplay,
            rawCaptureSourceFactory: () => rawSource,
            encoderFactory: () => encoder,
            sourceRole: "transport");

        source.SetCaptureFrameRateHint(3);
        source.SetTransportTuningLevel(ScreenShareTransportTuningLevel.BandwidthReduced);

        await source.StartAsync(CancellationToken.None);
        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(1, 1), capturedTsUtcMs: 150));
        await WaitUntilAsync(
            () => Assert.IsAssignableFrom<IScreenCaptureFreshnessMetricsSource>(source).GetFreshnessMetricsSnapshot().ActiveTargetFramesPerSecond == 3,
            TimeSpan.FromSeconds(2));

        var metrics = Assert.IsAssignableFrom<IScreenCaptureFreshnessMetricsSource>(source).GetFreshnessMetricsSnapshot();
        Assert.Equal(3, metrics.ActiveTargetFramesPerSecond);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsH264ScreenCaptureSource_TransportRawCapture_PreservesFirstPendingOrdinaryCandidateWhileEncodeIsInFlight()
    {
        await using var rawSource = new FakeWindowsRawCaptureSource();
        await using var encoder = new BlockingWindowsH264FrameEncoder();
        await using var source = new WindowsH264ScreenCaptureSource(
            ScreenCaptureTargetSelection.PrimaryDisplay,
            rawCaptureSourceFactory: () => rawSource,
            encoderFactory: () => encoder,
            sourceRole: "transport");

        source.SetCaptureFrameRateHint(5);
        source.SetTransportTuningLevel(ScreenShareTransportTuningLevel.BandwidthReduced);

        var received = new List<ScreenCaptureFrameEventArgs>();
        source.FrameArrived += (_, frame) => received.Add(frame);

        await source.StartAsync(CancellationToken.None);
        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(1, 1), capturedTsUtcMs: 200));
        await encoder.FirstEncodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(2, 1), capturedTsUtcMs: 201));
        await WaitUntilAsync(
            () => Assert.IsAssignableFrom<IScreenCaptureFreshnessMetricsSource>(source).GetFreshnessMetricsSnapshot().PendingRawFrameCount == 1,
            TimeSpan.FromSeconds(2));
        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(3, 1), capturedTsUtcMs: 202));
        encoder.ReleaseFirstEncode();
        await WaitUntilAsync(() => received.Count == 2, TimeSpan.FromSeconds(2));

        var freshnessSource = Assert.IsAssignableFrom<IScreenCaptureFreshnessMetricsSource>(source);
        var metrics = freshnessSource.GetFreshnessMetricsSnapshot();

        Assert.Equal(new[] { 1, 2 }, received.Select(static frame => frame.Width).ToArray());
        Assert.Equal(new[] { 1, 2 }, encoder.EncodedWidths.ToArray());
        Assert.True(metrics.RawFramesDeferredToEncodeSlot >= 1);
        Assert.Equal(0, metrics.RawFramesReplacedBeforeEncodeSlot);
        Assert.False(metrics.RawSlotCoalescingActive);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsH264ScreenCaptureSource_TransportRawCapture_PrefersNewerStreamEpochCandidate()
    {
        await using var rawSource = new FakeWindowsRawCaptureSource();
        await using var encoder = new BlockingWindowsH264FrameEncoder();
        await using var source = new WindowsH264ScreenCaptureSource(
            ScreenCaptureTargetSelection.PrimaryDisplay,
            rawCaptureSourceFactory: () => rawSource,
            encoderFactory: () => encoder,
            sourceRole: "transport");

        source.SetCaptureFrameRateHint(5);
        source.SetTransportTuningLevel(ScreenShareTransportTuningLevel.BandwidthReduced);

        var received = new List<ScreenCaptureFrameEventArgs>();
        source.FrameArrived += (_, frame) => received.Add(frame);

        await source.StartAsync(CancellationToken.None);
        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(1, 1), capturedTsUtcMs: 300));
        await encoder.FirstEncodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(2, 1), capturedTsUtcMs: 301));
        _ = source.ForceTransportRecoveryReset(ScreenShareTransportTuningLevel.BandwidthReduced);
        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(4, 1), capturedTsUtcMs: 302));
        encoder.ReleaseFirstEncode();

        await WaitUntilAsync(() => received.Count == 2, TimeSpan.FromSeconds(2));

        var freshnessSource = Assert.IsAssignableFrom<IScreenCaptureFreshnessMetricsSource>(source);
        var metrics = freshnessSource.GetFreshnessMetricsSnapshot();

        Assert.Equal(new[] { 1, 4 }, received.Select(static frame => frame.Width).ToArray());
        Assert.Equal(new[] { 1, 4 }, encoder.EncodedWidths.ToArray());
        Assert.Equal(new long[] { 1, 2 }, encoder.EncodedStreamEpochs.ToArray());
        Assert.Equal(2, received[1].StreamEpoch);
        Assert.True(metrics.RawFramesReplacedBeforeEncodeSlot >= 1);
        Assert.Equal(0, metrics.SupersededPendingRawFrameCount);
        Assert.False(metrics.RawSlotCoalescingActive);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsH264ScreenCaptureSource_RequestKeyFrame_RecoveryPurgesStalePendingRawCandidate()
    {
        await using var rawSource = new FakeWindowsRawCaptureSource();
        await using var encoder = new BlockingWindowsH264FrameEncoder();
        await using var source = new WindowsH264ScreenCaptureSource(
            ScreenCaptureTargetSelection.PrimaryDisplay,
            rawCaptureSourceFactory: () => rawSource,
            encoderFactory: () => encoder,
            sourceRole: "transport");

        await source.StartAsync(CancellationToken.None);

        var received = new List<ScreenCaptureFrameEventArgs>();
        source.FrameArrived += (_, frame) => received.Add(frame);

        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(1, 1), capturedTsUtcMs: 400));
        await encoder.FirstEncodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(2, 1), capturedTsUtcMs: 401));
        await WaitUntilAsync(
            () => Assert.IsAssignableFrom<IScreenCaptureFreshnessMetricsSource>(source).GetFreshnessMetricsSnapshot().PendingRawFrameCount == 1,
            TimeSpan.FromSeconds(2));

        source.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(4, 1), capturedTsUtcMs: 402));
        encoder.ReleaseFirstEncode();

        await WaitUntilAsync(() => received.Count == 2, TimeSpan.FromSeconds(2));

        var freshnessSource = Assert.IsAssignableFrom<IScreenCaptureFreshnessMetricsSource>(source);
        var metrics = freshnessSource.GetFreshnessMetricsSnapshot();

        Assert.Equal(new[] { 1, 4 }, received.Select(static frame => frame.Width).ToArray());
        Assert.Equal(new[] { 1, 4 }, encoder.EncodedWidths.ToArray());
        Assert.True(metrics.RawFramesReplacedBeforeEncodeSlot >= 1);
        Assert.True(metrics.SenderContinuityRecoveryActive);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsH264ScreenCaptureSource_RequestKeyFrame_ContinuityLoss_StartsEncoderRecoveryBurst()
    {
        await using var rawSource = new FakeWindowsRawCaptureSource();
        await using var encoder = new FakeWindowsH264FrameEncoder();
        await using var source = new WindowsH264ScreenCaptureSource(
            ScreenCaptureTargetSelection.PrimaryDisplay,
            rawCaptureSourceFactory: () => rawSource,
            encoderFactory: () => encoder,
            sourceRole: "transport");

        await source.StartAsync(CancellationToken.None);
        source.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);

        Assert.Equal(1, encoder.RecoveryBurstStartCount);
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonContinuityLoss, encoder.LastRecoveryBurstReason);
        Assert.Equal(1L, encoder.LastRecoveryBurstEpoch);

        var freshnessSource = Assert.IsAssignableFrom<IScreenCaptureFreshnessMetricsSource>(source);
        var metrics = freshnessSource.GetFreshnessMetricsSnapshot();
        Assert.True(metrics.SenderContinuityRecoveryActive);
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonContinuityLoss, metrics.LastSenderContinuityLossReason);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsH264ScreenCaptureSource_ForceTransportRecoveryReset_AndKeyFrameRequest_NextEncodeUsesResetEpochAndForceKeyFrame()
    {
        await using var rawSource = new FakeWindowsRawCaptureSource();
        await using var encoder = new FakeWindowsH264FrameEncoder();
        await using var source = new WindowsH264ScreenCaptureSource(
            ScreenCaptureTargetSelection.PrimaryDisplay,
            rawCaptureSourceFactory: () => rawSource,
            encoderFactory: () => encoder,
            sourceRole: "transport");

        var received = new List<ScreenCaptureFrameEventArgs>();
        source.FrameArrived += (_, frame) => received.Add(frame);

        await source.StartAsync(CancellationToken.None);

        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(1, 1), capturedTsUtcMs: 500));
        await WaitUntilAsync(() => received.Count == 1, TimeSpan.FromSeconds(2));

        var resetEpoch = source.ForceTransportRecoveryReset(ScreenShareTransportTuningLevel.Normal);
        source.RequestKeyFrame(ScreenSharePressureProtocol.PressureReasonContinuityLoss);
        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(2, 1), capturedTsUtcMs: 501));

        await WaitUntilAsync(() => received.Count == 2, TimeSpan.FromSeconds(2));

        Assert.Equal(2L, resetEpoch);
        Assert.Equal(new long[] { 1, resetEpoch }, encoder.EncodedStreamEpochs.ToArray());
        Assert.Equal(new[] { true, true }, encoder.ForceKeyFrameFlags.ToArray());
        Assert.Equal(resetEpoch, encoder.LastRecoveryBurstEpoch);
        Assert.Equal(resetEpoch, received[1].StreamEpoch);

        var freshnessSource = Assert.IsAssignableFrom<IScreenCaptureFreshnessMetricsSource>(source);
        var metrics = freshnessSource.GetFreshnessMetricsSnapshot();
        Assert.True(metrics.SenderContinuityRecoveryActive);
        Assert.Equal(ScreenSharePressureProtocol.PressureReasonContinuityLoss, metrics.LastSenderContinuityLossReason);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsH264ScreenCaptureSource_PurgePendingRawFrames_RemovesPendingFrameWithoutCancelingInFlightEncode()
    {
        await using var rawSource = new FakeWindowsRawCaptureSource();
        await using var encoder = new BlockingWindowsH264FrameEncoder();
        await using var source = new WindowsH264ScreenCaptureSource(
            ScreenCaptureTargetSelection.PrimaryDisplay,
            rawCaptureSourceFactory: () => rawSource,
            encoderFactory: () => encoder);

        var received = new List<ScreenCaptureFrameEventArgs>();
        source.FrameArrived += (_, frame) => received.Add(frame);

        await source.StartAsync(CancellationToken.None);
        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(1, 1), capturedTsUtcMs: 100));
        await encoder.FirstEncodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        rawSource.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(2, 1), capturedTsUtcMs: 101));

        var freshnessSource = Assert.IsAssignableFrom<IScreenCaptureFreshnessMetricsSource>(source);
        Assert.Equal(1, freshnessSource.PurgePendingRawFrames());
        Assert.Equal(0, freshnessSource.GetFreshnessMetricsSnapshot().PendingRawFrameCount);

        encoder.ReleaseFirstEncode();
        await WaitUntilAsync(() => received.Count == 1, TimeSpan.FromSeconds(2));
        await Task.Delay(100);

        Assert.Equal(new[] { 1 }, received.Select(static frame => frame.Width).ToArray());
        Assert.Equal(new[] { 1 }, encoder.EncodedWidths.ToArray());
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsH264ScreenCaptureSource_NonFatalWgcFailure_DoesNotTriggerOuterRecycleOrDesktopDuplicationFallback()
    {
        await using var initialWgc = new FakeWindowsRawCaptureSource(WindowsRawCaptureBackendKind.WindowsGraphicsCapture);
        await using var recycledWgc = new FakeWindowsRawCaptureSource(WindowsRawCaptureBackendKind.WindowsGraphicsCapture);
        await using var encoder = new FakeWindowsH264FrameEncoder();
        var ddSources = new List<FakeWindowsRawCaptureSource>();
        var received = new List<ScreenCaptureFrameEventArgs>();

        await using var source = new WindowsH264ScreenCaptureSource(
            ScreenCaptureTargetSelection.PrimaryDisplay,
            rawCaptureSourceFactory: () => initialWgc,
            encoderFactory: () => encoder,
            windowsGraphicsCaptureSourceFactory: () => recycledWgc,
            desktopDuplicationSourceFactory: () =>
            {
                var next = new FakeWindowsRawCaptureSource(WindowsRawCaptureBackendKind.DesktopDuplication);
                ddSources.Add(next);
                return next;
            });

        source.FrameArrived += (_, frame) => received.Add(frame);
        await source.StartAsync(CancellationToken.None);
        initialWgc.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(4, 3), capturedTsUtcMs: 100));
        initialWgc.RaiseFailure("copy_surface", "ObjectDisposedException", "copy surface failed");
        initialWgc.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(4, 3), capturedTsUtcMs: 101));

        await Task.Delay(150);

        Assert.Equal(0, recycledWgc.StartCallCount);
        Assert.DoesNotContain(ddSources, static candidate => candidate.StartCallCount > 0);
        Assert.InRange(received.Count, 1, 2);
        Assert.All(received, frame => Assert.Equal(1, frame.StreamEpoch));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsH264ScreenCaptureSource_FatalWgcFailure_FallsBackToDesktopDuplicationAndBumpsStreamEpoch()
    {
        await using var initialWgc = new FakeWindowsRawCaptureSource(WindowsRawCaptureBackendKind.WindowsGraphicsCapture);
        await using var encoder = new FakeWindowsH264FrameEncoder();
        var ddSources = new List<FakeWindowsRawCaptureSource>();
        var received = new List<ScreenCaptureFrameEventArgs>();

        await using var source = new WindowsH264ScreenCaptureSource(
            ScreenCaptureTargetSelection.PrimaryDisplay,
            rawCaptureSourceFactory: () => initialWgc,
            encoderFactory: () => encoder,
            windowsGraphicsCaptureSourceFactory: () => throw new InvalidOperationException("outer WGC recycle should not be used"),
            desktopDuplicationSourceFactory: () =>
            {
                var next = new FakeWindowsRawCaptureSource(WindowsRawCaptureBackendKind.DesktopDuplication);
                ddSources.Add(next);
                return next;
            });

        source.FrameArrived += (_, frame) => received.Add(frame);
        await source.StartAsync(CancellationToken.None);
        initialWgc.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(4, 3), capturedTsUtcMs: 100));
        initialWgc.RaiseFailure("copy_surface", "ObjectDisposedException", "copy surface failed", isFatal: true);

        await WaitUntilAsync(
            () => ddSources.Any(static candidate => candidate.StartCallCount > 0),
            TimeSpan.FromSeconds(2));
        var activeDesktopDuplication = ddSources.Single(candidate => candidate.StartCallCount > 0);
        activeDesktopDuplication.RaiseFrame(new WindowsRawCaptureFrame(new DrawingBitmap(4, 3), capturedTsUtcMs: 101));

        await WaitUntilAsync(() => received.Count == 2, TimeSpan.FromSeconds(2));
        Assert.Equal(1, received[0].StreamEpoch);
        Assert.Equal(2, received[1].StreamEpoch);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsH264ScreenCaptureSource_DesktopDuplicationStartupFailure_DisablesFallbackForSession()
    {
        await using var initialWgc = new FakeWindowsRawCaptureSource(WindowsRawCaptureBackendKind.WindowsGraphicsCapture);
        await using var encoder = new FakeWindowsH264FrameEncoder();
        var ddSources = new List<FakeWindowsRawCaptureSource>();

        await using var source = new WindowsH264ScreenCaptureSource(
            ScreenCaptureTargetSelection.PrimaryDisplay,
            rawCaptureSourceFactory: () => initialWgc,
            encoderFactory: () => encoder,
            windowsGraphicsCaptureSourceFactory: () => throw new InvalidOperationException("outer WGC recycle should not be used"),
            desktopDuplicationSourceFactory: () =>
            {
                var next = new FakeWindowsRawCaptureSource(WindowsRawCaptureBackendKind.DesktopDuplication)
                {
                    StartException = new InvalidOperationException("duplicate_output failed"),
                };
                ddSources.Add(next);
                return next;
            });

        await source.StartAsync(CancellationToken.None);
        initialWgc.RaiseFailure("copy_surface", "ObjectDisposedException", "copy surface failed", isFatal: true);

        await WaitUntilAsync(
            () => (bool)(typeof(WindowsH264ScreenCaptureSource)
                .GetField("desktopDuplicationDisabledForSession", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(source) ?? false),
            TimeSpan.FromSeconds(2));
        Assert.Single(ddSources);
        Assert.Equal(1, ddSources[0].StartCallCount);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsH264ScreenCaptureSource_IsSupported_ProbesAndDisposesFactoryInstances()
    {
        var rawDisposed = 0;
        var encoderDisposed = 0;
        var source = new WindowsH264ScreenCaptureSource(
            ScreenCaptureTargetSelection.PrimaryDisplay,
            rawCaptureSourceFactory: () => new ProbeRawCaptureSource(() => Interlocked.Increment(ref rawDisposed)),
            encoderFactory: () => new ProbeH264FrameEncoder(() => Interlocked.Increment(ref encoderDisposed)));

        var supported = source.IsSupported;

        Assert.True(supported);
        Assert.Equal(1, rawDisposed);
        Assert.Equal(1, encoderDisposed);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsH264ScreenCaptureSource_IsPreviewRuntimeSupported_DoesNotDependOnRemovedRolloutFlags()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var infraFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_H264_INFRA", "1");
        using var decodeFlag = new EnvironmentOverride("NLINK_FEATURE_SCREENCAP_H264_DECODE", "0");

        var supportedWithFlagsDisabled = WindowsH264ScreenCaptureSource.IsPreviewRuntimeSupported();
        var supportedWithoutOverrides = WindowsH264ScreenCaptureSource.IsPreviewRuntimeSupported();

        Assert.Equal(supportedWithoutOverrides, supportedWithFlagsDisabled);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsH264BitmapDecoderFactory_PrefersFfmpegBackend_WhenAvailable()
    {
        WindowsH264BitmapDecoderFactory.ResetDebugState();
        using var decoder = WindowsH264BitmapDecoderFactory.TryCreate(
            "helper_remote",
            _ => new FakeH264BitmapDecoder(),
            _ => throw new InvalidOperationException("media foundation fallback should not be used"));

        Assert.NotNull(decoder);
        Assert.Equal("ffmpeg_software", WindowsH264BitmapDecoderFactory.DebugLastSelectedBackend);
        Assert.Equal("(none)", WindowsH264BitmapDecoderFactory.DebugLastFallbackReason);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsH264BitmapDecoderFactory_FallsBackToMediaFoundation_WhenFfmpegIsUnavailable()
    {
        WindowsH264BitmapDecoderFactory.ResetDebugState();
        using var decoder = WindowsH264BitmapDecoderFactory.TryCreate(
            "helper_remote",
            _ => null,
            _ => new FakeH264BitmapDecoder());

        Assert.NotNull(decoder);
        Assert.Equal("media_foundation", WindowsH264BitmapDecoderFactory.DebugLastSelectedBackend);
        Assert.Equal("ffmpeg_backend_unavailable", WindowsH264BitmapDecoderFactory.DebugLastFallbackReason);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsH264BitmapDecoderFactory_FallbackDiagnostics_ExposeFfmpegInitializationDetails()
    {
        WindowsH264BitmapDecoderFactory.ResetDebugState();
        using var decoder = WindowsH264BitmapDecoderFactory.TryCreate(
            "helper_remote",
            _ => null,
            _ => new FakeH264BitmapDecoder(),
            () => new FfmpegRuntimeDiagnostics(
                InitializationSucceeded: false,
                InitializationFailure: "ffmpeg_dlls_not_found",
                SearchPaths: @"C:\temp\nLink|C:\temp\nLink\ffmpeg",
                SelectedLibraryPath: "(none)"));

        Assert.NotNull(decoder);
        Assert.Equal("media_foundation", WindowsH264BitmapDecoderFactory.DebugLastSelectedBackend);
        Assert.Equal("ffmpeg_backend_unavailable", WindowsH264BitmapDecoderFactory.DebugLastFallbackReason);
        Assert.Equal("ffmpeg_dlls_not_found", WindowsH264BitmapDecoderFactory.DebugLastFfmpegInitializationFailure);
        Assert.Equal(@"C:\temp\nLink|C:\temp\nLink\ffmpeg", WindowsH264BitmapDecoderFactory.DebugLastFfmpegSearchPaths);
        Assert.Equal("(none)", WindowsH264BitmapDecoderFactory.DebugLastFfmpegSelectedLibraryPath);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsFfmpegRuntime_DebugProbeInitialization_SucceedsWhenDllsExistUnderFfmpegSubfolder()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var decoder = FfmpegH264BitmapDecoder.TryCreate("helper_remote");
        if (decoder is null)
        {
            return;
        }

        var sourceLibrariesPath = FfmpegH264BitmapDecoder.DebugNativeLibrariesPath;
        Assert.True(Directory.Exists(sourceLibrariesPath), $"Expected FFmpeg native library directory to exist at '{sourceLibrariesPath}'.");

        var tempRoot = Path.Combine(Path.GetTempPath(), "nlink-ffmpeg-probe-" + Guid.NewGuid().ToString("N"));
        var tempFfmpegDir = Path.Combine(tempRoot, "ffmpeg");
        Directory.CreateDirectory(tempFfmpegDir);

        try
        {
            foreach (var libraryPath in Directory.GetFiles(sourceLibrariesPath, "*.dll", SearchOption.TopDirectoryOnly))
            {
                File.Copy(libraryPath, Path.Combine(tempFfmpegDir, Path.GetFileName(libraryPath)), overwrite: true);
            }

            var probe = WindowsFfmpegRuntime.DebugProbeInitialization(tempRoot);

            Assert.True(probe.InitializationSucceeded);
            Assert.Equal("none", probe.InitializationFailure);
            Assert.Equal(tempFfmpegDir, probe.SelectedLibraryPath);
            Assert.Contains(tempRoot, probe.SearchPaths, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(tempFfmpegDir, probe.SearchPaths, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsFfmpegRuntime_BuildOutput_ShipsOnlyWhitelistedDecoderDlls()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var decoder = FfmpegH264BitmapDecoder.TryCreate("helper_remote");
        if (decoder is null)
        {
            return;
        }

        var actualDlls = Directory.GetFiles(FfmpegH264BitmapDecoder.DebugNativeLibrariesPath, "*.dll", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var expectedDlls = new[]
        {
            "avcodec-61.dll",
            "avutil-59.dll",
            "swresample-5.dll",
            "swscale-8.dll",
        };

        Assert.True(
            expectedDlls.SequenceEqual(actualDlls, StringComparer.OrdinalIgnoreCase),
            $"Expected ffmpeg DLL set [{string.Join(", ", expectedDlls)}] but found [{string.Join(", ", actualDlls)}].");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FfmpegH264BitmapDecoder_WhenAvailable_HelperStyleReplay_DecodesBitmap()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await fixture.Session.Dispatch(() =>
        {
            using var decoder = FfmpegH264BitmapDecoder.TryCreate("helper_remote");
            if (decoder is null)
            {
                return true;
            }

            var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "HelperRemoteH264Decode");
            Assert.True(Directory.Exists(fixtureRoot), $"Expected helper replay fixture directory to exist at '{fixtureRoot}'.");

            var configBytes = File.ReadAllBytes(Path.Combine(fixtureRoot, "decoder-config.bin"));
            var framePaths = Directory.GetFiles(fixtureRoot, "frame-*.bin")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.True(framePaths.Length >= 1, "Expected at least one helper replay frame.");

            decoder.ConfigureStream(new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper-replay",
                StreamEpoch = 39,
                Encoding = "h264",
                CodecProfile = "main",
                DecoderConfigData = configBytes,
            });

            Bitmap? decodedBitmap = null;
            foreach (var framePath in framePaths)
            {
                var encodedBytes = File.ReadAllBytes(framePath);
                try
                {
                    decodedBitmap = decoder.Decode(new EncodedFrameDecodeRequest("h264", encodedBytes, IsKeyFrame: true, StreamEpoch: 39));
                    if (decodedBitmap is not null)
                    {
                        break;
                    }
                }
                catch (H264DecoderNeedsMoreInputException)
                {
                }
            }

            using (decodedBitmap)
            {
                Assert.NotNull(decodedBitmap);
                Assert.True(decodedBitmap!.PixelSize.Width > 0);
                Assert.True(decodedBitmap.PixelSize.Height > 0);
                Assert.NotEqual("(none)", FfmpegH264BitmapDecoder.DebugNativeLibrariesPath);
                Assert.Equal("none", FfmpegH264BitmapDecoder.DebugNativeInitializationFailure);
                Assert.NotEqual("(none)", FfmpegH264BitmapDecoder.DebugNativeSearchPaths);
            }

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FfmpegH264BitmapDecoder_WhenAvailable_HelperStyleReplay_DrainsQueuedOutputAcrossPackets()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await fixture.Session.Dispatch(() =>
        {
            using var decoder = FfmpegH264BitmapDecoder.TryCreate("helper_remote");
            if (decoder is null)
            {
                return true;
            }

            var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "Fixtures", "HelperRemoteH264Decode");
            Assert.True(Directory.Exists(fixtureRoot), $"Expected helper replay fixture directory to exist at '{fixtureRoot}'.");

            var configBytes = File.ReadAllBytes(Path.Combine(fixtureRoot, "decoder-config.bin"));
            var framePaths = Directory.GetFiles(fixtureRoot, "frame-*.bin")
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            Assert.True(framePaths.Length >= 7, "Expected at least seven helper replay frames.");

            decoder.ConfigureStream(new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper-replay",
                StreamEpoch = 41,
                Encoding = "h264",
                CodecProfile = "main",
                DecoderConfigData = configBytes,
            });

            var decodedCount = 0;
            foreach (var framePath in framePaths)
            {
                var encodedBytes = File.ReadAllBytes(framePath);
                try
                {
                    using var decodedBitmap = decoder.Decode(new EncodedFrameDecodeRequest("h264", encodedBytes, IsKeyFrame: true, StreamEpoch: 41));
                    Assert.True(decodedBitmap.PixelSize.Width > 0);
                    Assert.True(decodedBitmap.PixelSize.Height > 0);
                    decodedCount++;
                }
                catch (H264DecoderNeedsMoreInputException)
                {
                }
            }

            Assert.True(
                decodedCount >= Math.Max(3, framePaths.Length / 2),
                $"Expected FFmpeg helper replay to drain queued decoded output across packets, but only decoded {decodedCount} of {framePaths.Length} packets.");

            return true;
        }, default);
    }

    private sealed class FakeH264BitmapDecoder : IWindowsH264BitmapDecoder
    {
        public bool IsSupported => true;

        public int ConfigureCallCount { get; private set; }

        public int DecodeCallCount { get; private set; }

        public long LastConfiguredEpoch { get; private set; }

        public long LastDecodedEpoch { get; private set; }

        public void ConfigureStream(ScreenShareVideoStreamConfigV1 config)
        {
            ConfigureCallCount++;
            LastConfiguredEpoch = config.StreamEpoch;
        }

        public AvaloniaBitmap Decode(EncodedFrameDecodeRequest request)
        {
            DecodeCallCount++;
            LastDecodedEpoch = request.StreamEpoch;
            return CreateBitmap(request.EncodedFrameBytes.Span[0], 1);
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class BlockingH264BitmapDecoder : IWindowsH264BitmapDecoder
    {
        private readonly TaskCompletionSource<bool> decodeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> releaseDecode = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsSupported => true;

        public TaskCompletionSource<bool> ResetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task DecodeStarted => decodeStarted.Task;

        public int ResetCallCount { get; private set; }

        public void ConfigureStream(ScreenShareVideoStreamConfigV1 config)
        {
        }

        public AvaloniaBitmap Decode(EncodedFrameDecodeRequest request)
        {
            decodeStarted.TrySetResult(true);
            if (!releaseDecode.Task.Wait(TimeSpan.FromSeconds(2)))
            {
                throw new TimeoutException("Timed out waiting to release blocking H.264 decode.");
            }

            return CreateBitmap(request.EncodedFrameBytes.Span[0], 1);
        }

        public void Reset()
        {
            ResetCallCount++;
            ResetStarted.TrySetResult(true);
        }

        public void ReleaseDecode()
        {
            releaseDecode.TrySetResult(true);
        }

        public void Dispose()
        {
        }
    }

    private sealed class ProbeRawCaptureSource : IWindowsRawCaptureSource
    {
        private readonly Action onDispose;

        public ProbeRawCaptureSource(Action onDispose)
        {
            this.onDispose = onDispose;
        }

        public bool IsSupported => true;

        public event EventHandler<WindowsRawCaptureFrameEventArgs>? FrameArrived;
        public event EventHandler<WindowsRawCaptureFailureEventArgs>? CaptureFailed;

        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync() => Task.CompletedTask;

        public bool TryGetCaptureMetadata(out ScreenCaptureMetadata metadata)
        {
            metadata = default;
            return false;
        }

        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ProbeH264FrameEncoder : IWindowsH264FrameEncoder
    {
        private readonly Action onDispose;

        public ProbeH264FrameEncoder(Action onDispose)
        {
            this.onDispose = onDispose;
        }

        public bool IsSupported => true;

        public ValueTask<WindowsH264EncodedFrame?> EncodeAsync(
            WindowsRawCaptureFrame frame,
            WindowsH264EncodeOptions options,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ValueTask DisposeAsync()
        {
            onDispose();
            return ValueTask.CompletedTask;
        }

        public void StartRecoveryBurst(string reason, long streamEpoch)
        {
        }
    }

    private sealed class FakeWindowsRawCaptureSource : IWindowsRawCaptureSource, IWindowsRawCaptureBackendDescriptor
    {
        public FakeWindowsRawCaptureSource(WindowsRawCaptureBackendKind backendKind = WindowsRawCaptureBackendKind.Unknown)
        {
            BackendKind = backendKind;
        }

        public bool IsSupported { get; set; } = true;

        public WindowsRawCaptureBackendKind BackendKind { get; }

        public int StartCallCount { get; private set; }

        public int StopCallCount { get; private set; }

        public int DisposeCallCount { get; private set; }

        public Exception? StartException { get; set; }

        public event EventHandler<WindowsRawCaptureFrameEventArgs>? FrameArrived;
        public event EventHandler<WindowsRawCaptureFailureEventArgs>? CaptureFailed;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StartCallCount++;
            if (StartException is not null)
            {
                throw StartException;
            }

            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            StopCallCount++;
            return Task.CompletedTask;
        }

        public bool TryGetCaptureMetadata(out ScreenCaptureMetadata metadata)
        {
            metadata = default;
            return false;
        }

        public void RaiseFrame(WindowsRawCaptureFrame frame)
        {
            FrameArrived?.Invoke(this, new WindowsRawCaptureFrameEventArgs(frame));
        }

        public void RaiseFailure(string stage, string reason, string? message = null, bool isFatal = false)
        {
            CaptureFailed?.Invoke(this, new WindowsRawCaptureFailureEventArgs(stage, reason, message, isFatal));
        }

        public ValueTask DisposeAsync()
        {
            DisposeCallCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeWindowsH264FrameEncoder : IWindowsH264FrameEncoder
    {
        private readonly List<int> encodedWidths = new();
        private readonly List<long> encodedStreamEpochs = new();
        private readonly List<bool> forceKeyFrameFlags = new();

        public bool IsSupported => true;

        public IReadOnlyList<int> EncodedWidths => encodedWidths;

        public IReadOnlyList<long> EncodedStreamEpochs => encodedStreamEpochs;

        public IReadOnlyList<bool> ForceKeyFrameFlags => forceKeyFrameFlags;

        public int RecoveryBurstStartCount { get; private set; }

        public string LastRecoveryBurstReason { get; private set; } = string.Empty;

        public long LastRecoveryBurstEpoch { get; private set; }

        public ValueTask<WindowsH264EncodedFrame?> EncodeAsync(
            WindowsRawCaptureFrame frame,
            WindowsH264EncodeOptions options,
            CancellationToken cancellationToken)
        {
            encodedWidths.Add(frame.Bitmap.Width);
            encodedStreamEpochs.Add(options.StreamEpoch);
            forceKeyFrameFlags.Add(options.ForceKeyFrame);
            return ValueTask.FromResult(
                (WindowsH264EncodedFrame?)new WindowsH264EncodedFrame(
                    EncodedBytes: new byte[] { 0x01, 0x02, 0x03 },
                    Width: frame.Bitmap.Width,
                    Height: frame.Bitmap.Height,
                    CapturedTsUtcMs: frame.CapturedTsUtcMs,
                    IsKeyFrame: true,
                    StreamEpoch: options.StreamEpoch,
                    StreamConfig: new ScreenShareVideoStreamConfigV1
                    {
                        SessionId = "session",
                        StreamEpoch = options.StreamEpoch,
                        Encoding = "h264",
                        CodecProfile = "baseline",
                        DecoderConfigData = new byte[] { 1, 2, 3 },
                    }));
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public void StartRecoveryBurst(string reason, long streamEpoch)
        {
            RecoveryBurstStartCount++;
            LastRecoveryBurstReason = reason;
            LastRecoveryBurstEpoch = streamEpoch > 0 ? streamEpoch : 1;
        }
    }

    private sealed class BlockingWindowsH264FrameEncoder : IWindowsH264FrameEncoder
    {
        private readonly List<int> encodedWidths = new();
        private readonly List<long> encodedStreamEpochs = new();
        private readonly List<bool> forceKeyFrameFlags = new();
        private readonly TaskCompletionSource<bool> firstEncodeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> releaseFirstEncode = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int encodeCallCount;

        public bool IsSupported => true;

        public TaskCompletionSource<bool> FirstEncodeStarted => firstEncodeStarted;

        public IReadOnlyList<int> EncodedWidths => encodedWidths;

        public IReadOnlyList<long> EncodedStreamEpochs => encodedStreamEpochs;

        public IReadOnlyList<bool> ForceKeyFrameFlags => forceKeyFrameFlags;

        public void ReleaseFirstEncode()
        {
            releaseFirstEncode.TrySetResult(true);
        }

        public async ValueTask<WindowsH264EncodedFrame?> EncodeAsync(
            WindowsRawCaptureFrame frame,
            WindowsH264EncodeOptions options,
            CancellationToken cancellationToken)
        {
            var callNumber = Interlocked.Increment(ref encodeCallCount);
            if (callNumber == 1)
            {
                firstEncodeStarted.TrySetResult(true);
                await releaseFirstEncode.Task.WaitAsync(cancellationToken);
            }

            encodedWidths.Add(frame.Bitmap.Width);
            encodedStreamEpochs.Add(options.StreamEpoch);
            forceKeyFrameFlags.Add(options.ForceKeyFrame);
            return (WindowsH264EncodedFrame?)new WindowsH264EncodedFrame(
                EncodedBytes: new byte[] { 0x01, 0x02, 0x03 },
                Width: frame.Bitmap.Width,
                Height: frame.Bitmap.Height,
                CapturedTsUtcMs: frame.CapturedTsUtcMs,
                IsKeyFrame: true,
                StreamEpoch: options.StreamEpoch,
                StreamConfig: new ScreenShareVideoStreamConfigV1
                {
                    SessionId = "session",
                    StreamEpoch = options.StreamEpoch,
                    Encoding = "h264",
                    CodecProfile = "baseline",
                    DecoderConfigData = new byte[] { 1, 2, 3 },
                });
        }

        public ValueTask DisposeAsync()
        {
            releaseFirstEncode.TrySetCanceled();
            return ValueTask.CompletedTask;
        }

        public void StartRecoveryBurst(string reason, long streamEpoch)
        {
        }
    }

    private static AvaloniaBitmap CreateBitmap(int width, int height)
    {
        using var stream = new MemoryStream(Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a5kAAAAASUVORK5CYII="), writable: false);
        return new AvaloniaBitmap(stream);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var start = DateTime.UtcNow;
        while (!predicate())
        {
            if (DateTime.UtcNow - start > timeout)
            {
                throw new TimeoutException("Condition was not satisfied within the allotted timeout.");
            }

            await Task.Delay(25);
        }
    }
}
