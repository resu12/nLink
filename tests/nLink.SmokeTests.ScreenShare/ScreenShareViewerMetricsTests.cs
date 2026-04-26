using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.App.Views;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
[Trait("Area", "ScreenShare")]
public sealed class ScreenShareViewerMetricsTests : ScreenShareViewerViewModelTestBase, IClassFixture<ScreenShareCoordinatorFixture>
{
    public ScreenShareViewerMetricsTests(ScreenShareCoordinatorFixture fixture) : base(fixture)
    {
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_Metrics_TrackRenderInterval_CaptureToRender_AndStaleFrames()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => CreateTinyBitmap(),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                });

            vm.OnEncodedFrame("jpeg", CreateTinyJpegBytes(), capturedTsUtcMs: DateTimeOffset.UtcNow.AddMilliseconds(-1500).ToUnixTimeMilliseconds());
            await WaitUntilAsync(() => vm.GetMetricsSnapshot().FramesDecoded >= 1, TimeSpan.FromSeconds(2));

            await Task.Delay(40);

            vm.OnEncodedFrame("jpeg", CreateTinyJpegBytes(), capturedTsUtcMs: DateTimeOffset.UtcNow.AddMilliseconds(-100).ToUnixTimeMilliseconds());
            await WaitUntilAsync(() => vm.GetMetricsSnapshot().FramesDecoded >= 2, TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(2, metrics.FramesDecoded);
            Assert.True(metrics.AverageRenderIntervalMs > 0);
            Assert.True(metrics.AverageCaptureToRenderMs > 0);
            Assert.Equal(1, metrics.StaleFrameRenders);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_CursorOverlay_AppliesTelemetryAndFallsBackWhenCaptureCursorRemainsEnabled()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => CreateTinyBitmap(),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                });

            vm.OnEncodedFrame(
                "jpeg",
                CreateTinyJpegBytes(),
                capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await WaitUntilAsync(() => vm.IsActive && vm.GetMetricsSnapshot().FramesDecoded >= 1, TimeSpan.FromSeconds(2));

            vm.OnCursorState(new ScreenShareCursorStateV1
            {
                SessionId = "session",
                Seq = 1,
                TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DisplayId = "display",
                DisplayInfoRevision = 1,
                Nx = 0.25,
                Ny = 0.75,
                Visible = true,
                Status = "captured_cursor_disabled",
                CapturedCursorEnabled = false,
                CursorCaptureControlSupported = true,
            });

            Assert.True(vm.CursorOverlayVisible);
            Assert.Equal(0.25, vm.CursorOverlayNx, precision: 3);
            Assert.Equal(0.75, vm.CursorOverlayNy, precision: 3);
            Assert.Equal("helper_overlay", vm.CursorDeliveryMode);

            var overlayMetrics = vm.GetMetricsSnapshot();
            Assert.True(overlayMetrics.CursorOverlayVisible);
            Assert.Equal(1, overlayMetrics.CursorOverlayUpdatesReceivedCount);
            Assert.Equal(1, overlayMetrics.CursorOverlayUpdatesAppliedCount);
            Assert.Equal("captured_cursor_disabled", overlayMetrics.CursorOverlayLastStatus);

            vm.OnCursorState(new ScreenShareCursorStateV1
            {
                SessionId = "session",
                Seq = 2,
                TsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                DisplayId = "display",
                DisplayInfoRevision = 1,
                Nx = 0.5,
                Ny = 0.5,
                Visible = true,
                Status = "fallback_captured",
                CapturedCursorEnabled = true,
                CursorCaptureControlSupported = true,
            });

            Assert.False(vm.CursorOverlayVisible);
            Assert.Equal("fallback_captured", vm.CursorDeliveryMode);
            Assert.Equal(2, vm.GetMetricsSnapshot().CursorOverlayUpdatesReceivedCount);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareSurfaceView_CursorOverlayMapping_UsesUniformPresentationTransform()
    {
        Assert.True(ScreenShareSurfaceView.TryMapCursorOverlayToSurface(
            nx: 0.5,
            ny: 0.5,
            frameWidth: 1440,
            frameHeight: 810,
            viewportWidth: 1600,
            viewportHeight: 900,
            out var centeredPoint));
        Assert.Equal(800, centeredPoint.X, precision: 1);
        Assert.Equal(450, centeredPoint.Y, precision: 1);

        Assert.True(ScreenShareSurfaceView.TryMapCursorOverlayToSurface(
            nx: 1,
            ny: 0.5,
            frameWidth: 1440,
            frameHeight: 810,
            viewportWidth: 1600,
            viewportHeight: 1000,
            out var letterboxedPoint));
        Assert.Equal(1600, letterboxedPoint.X, precision: 1);
        Assert.Equal(500, letterboxedPoint.Y, precision: 1);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public void ScreenShareSurfaceView_CursorOverlayPointer_UsesBalancedStandardSize()
    {
        Assert.Equal(10d, ScreenShareSurfaceView.CursorOverlayPointerWidthDip);
        Assert.Equal(14d, ScreenShareSurfaceView.CursorOverlayPointerHeightDip);
        Assert.Equal(0.8d, ScreenShareSurfaceView.CursorOverlayPointerStrokeThicknessDip);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_MetricsSerializeAuthoritativeSessionSnapshot()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper-sequential",
                StreamEpoch = 17,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame(
                "h264",
                new byte[] { 30 },
                capturedTsUtcMs: 0,
                isKeyFrame: true,
                streamEpoch: 17,
                streamConfig: config,
                frameId: 30,
                sessionId: "helper-sequential");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 30 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame(
                "h264",
                new byte[] { 31 },
                capturedTsUtcMs: 0,
                isKeyFrame: false,
                streamEpoch: 17,
                frameId: 31,
                sessionId: "helper-sequential");
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap second && second.PixelSize.Width == 31 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnOwnedEncodedFrame(
                "h264",
                new byte[] { 32 },
                capturedTsUtcMs: 0,
                isKeyFrame: false,
                streamEpoch: 17,
                frameId: 32,
                sessionId: "helper-sequential");

            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap current && current.PixelSize.Width == 32 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            var helperSessionSnapshot = vm.GetHelperRemoteSessionSnapshot();
            var metrics = vm.GetMetricsSnapshot();

            Assert.Equal(ScreenShareConceptualModelFormatter.FormatHelperSessionPhase(helperSessionSnapshot.Phase), metrics.HelperSessionPhase);
            Assert.Equal(ScreenShareConceptualModelFormatter.FormatHelperRecoveryMechanism(helperSessionSnapshot.RecoveryMechanism), metrics.HelperRecoveryMechanism);
            Assert.Equal(helperSessionSnapshot.BaselineEstablished, metrics.BaselineEstablished);
            Assert.Equal(helperSessionSnapshot.SteadyVisibleProgressActive, metrics.SteadyVisibleProgressActive);
            Assert.Equal(helperSessionSnapshot.VisibleHeadFrameId, metrics.VisibleHeadFrameId);
            Assert.Equal(helperSessionSnapshot.StableVisibleHeadFrameId, metrics.StableVisibleHeadFrameId);
            Assert.Equal(helperSessionSnapshot.AppliedHeadFrameId, metrics.AppliedHeadFrameId);
            Assert.Equal(helperSessionSnapshot.VisibleRecoveryFloorFrameId, metrics.VisibleRecoveryFloorFrameId);
            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_StableVisibleProgress_BypassesDecodeAgeBudget()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var decodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDecode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: null,
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FrameBlockingH264BitmapDecoder(
                    blockedFrameId: 12,
                    decodeStarted,
                    releaseDecode),
                logRole: "helper_remote");

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 41,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            vm.OnOwnedEncodedFrame("h264", new byte[] { 10 }, capturedTsUtcMs: nowUtcMs, isKeyFrame: true, streamEpoch: 41, streamConfig: config, frameId: 10);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap keyframe && keyframe.PixelSize.Width == 10 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(5));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 11 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: false, streamEpoch: 41, frameId: 11);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap follower && follower.PixelSize.Width == 11 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(5));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 12 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: false, streamEpoch: 41, frameId: 12);
            await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var staleCapturedTsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 5000;
            vm.OnOwnedEncodedFrame("h264", new byte[] { 13 }, capturedTsUtcMs: staleCapturedTsUtcMs, isKeyFrame: false, streamEpoch: 41, frameId: 13);
            vm.OnOwnedEncodedFrame("h264", new byte[] { 14 }, capturedTsUtcMs: staleCapturedTsUtcMs, isKeyFrame: false, streamEpoch: 41, frameId: 14);

            releaseDecode.TrySetResult(true);

            await WaitUntilAsync(
                () => vm.IsIdleForDiagnostics &&
                      vm.GetMetricsSnapshot().OrdinaryNonKeyAgeBudgetBypassCount >= 1 &&
                      vm.CurrentFrame is Bitmap latest && latest.PixelSize.Width >= 12,
                TimeSpan.FromSeconds(5));

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal(0, metrics.DecodeAgeBudgetCount);
            Assert.Equal(0, metrics.FramesDroppedBeforeDecode);
            Assert.True(metrics.OrdinaryNonKeyAgeBudgetBypassCount >= 1);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_HelperRemote_VisibleStableOrdinaryStaleFrame_UsesVisibleStableFreshnessDrop()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                },
                h264Decoder: new FakeH264BitmapDecoder(),
                logRole: "helper_remote");
            var staleDropCount = 0;
            ScreenShareViewerStaleFrameDroppedEventArgs? staleDrop = null;
            vm.StaleFrameDropped += (_, e) =>
            {
                staleDropCount++;
                staleDrop = e;
            };

            var config = new ScreenShareVideoStreamConfigV1
            {
                SessionId = "helper",
                StreamEpoch = 51,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            };

            vm.OnOwnedEncodedFrame("h264", new byte[] { 50 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: true, streamEpoch: 51, streamConfig: config, frameId: 50);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 50 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(5));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 51 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), isKeyFrame: false, streamEpoch: 51, frameId: 51);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap follower && follower.PixelSize.Width == 51 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(5));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 52 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 900, isKeyFrame: false, streamEpoch: 51, frameId: 52);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap stale &&
                      stale.PixelSize.Width == 51 &&
                      vm.GetMetricsSnapshot().StaleFrameDropVisibleStableCount >= 1 &&
                      vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(5));

            var current = Assert.IsAssignableFrom<Bitmap>(vm.CurrentFrame);
            Assert.Equal(51, current.PixelSize.Width);

            var metrics = vm.GetMetricsSnapshot();
            Assert.True(metrics.StaleFrameDropVisibleStableCount >= 1);
            Assert.True(metrics.StaleFrameDropVisibleStableLastAgeMs >= 700);
            Assert.False(metrics.H264ReferenceTaintActive);
            Assert.Equal(0, metrics.H264ReferenceTaintStaleVisibleStableEnterCount);
            Assert.True(metrics.StaleNormalNonKeyVisibleSuppressCount >= 1);
            Assert.True(metrics.DecodedStaleVisibleSuppressCount >= 1);
            Assert.NotNull(staleDrop);
            Assert.True(staleDrop!.ReferenceContinuityPreserved);
            Assert.Equal("none", metrics.H264ReferenceTaintLastReason);

            var frameLossSnapshot = vm.GetFrameLossSnapshotForDiagnostics();
            Assert.Contains(
                frameLossSnapshot.RecentLosses,
                static loss => loss.FrameId == 52 && string.Equals(loss.Reason, "stale_frame_drop_visible_stable", StringComparison.Ordinal));

            vm.OnOwnedEncodedFrame("h264", new byte[] { 53 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 200, isKeyFrame: false, streamEpoch: 51, frameId: 53);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap next && next.PixelSize.Width == 53 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(5));

            var finalMetrics = vm.GetMetricsSnapshot();
            Assert.Equal(0, finalMetrics.H264ReferenceTaintDroppedNonKeyCount);
            Assert.False(finalMetrics.H264ReferenceTaintActive);
            return true;
        }, default);
    }

    private static bool CurrentFrameWidthEquals(ScreenShareViewerViewModel vm, int expectedWidth)
    {
        if (vm.CurrentFrame is not Bitmap bitmap)
        {
            return false;
        }

        try
        {
            return bitmap.PixelSize.Width == expectedWidth;
        }
        catch (NullReferenceException)
        {
            return false;
        }
    }
}
