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
public sealed class HelpeeScreenShareCoordinatorDecodeTests : ScreenShareCoordinatorTestBase, IClassFixture<ScreenShareCoordinatorFixture>
{
    public HelpeeScreenShareCoordinatorDecodeTests(ScreenShareCoordinatorFixture fixture) : base(fixture)
    {
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeeScreenShareCoordinator_SlowDecode_CoalescesToLatestFrame()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var fakeSource = new FakeScreenCaptureSource();
            var decodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDecode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = false;
            Bitmap? currentFrame = null;
            var decodeCount = 0;

            var coordinator = new HelpeeScreenShareCoordinator(
                isDisposed: static () => false,
                canShowScreenShareAction: static () => true,
                isPreviewActive: () => active,
                captureSourceFactory: new FixedCaptureSourceFactory(fakeSource),
                setPreviewActive: value => active = value,
                setStatus: _ => { },
                getPreviewFrame: () => currentFrame,
                setPreviewFrame: value => currentFrame = value,
                decodeFrame: bytes =>
                {
                    var call = Interlocked.Increment(ref decodeCount);
                    if (call == 1)
                    {
                        decodeStarted.TrySetResult(true);
                        WaitForSignal(releaseDecode.Task, TimeSpan.FromSeconds(2));
                    }

                    return CreateBitmap(bytes[0], 1);
                });

            try
            {
                coordinator.Toggle();
                await WaitUntilAsync(() => active, TimeSpan.FromSeconds(2));

                fakeSource.RaiseFrame(1, 1, new byte[] { 1 }, "jpeg");
                await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

                for (byte i = 2; i <= 5; i++)
                {
                    fakeSource.RaiseFrame(1, 1, new byte[] { i }, "jpeg");
                }

                releaseDecode.TrySetResult(true);
                await WaitUntilAsync(
                    () => currentFrame is Bitmap latest && latest.PixelSize.Width == 5,
                    TimeSpan.FromSeconds(2));
                await WaitUntilAsync(
                    () => coordinator.DecodeTasksActive == 0,
                    TimeSpan.FromSeconds(2));

                Assert.True(active);
                Assert.NotNull(currentFrame);
                Assert.Equal(2, decodeCount);
                Assert.Equal(2, coordinator.FramesDecoded);
                Assert.Equal(1, coordinator.MaxDecodeTasksActive);
                Assert.Equal(0, coordinator.DecodeTasksActive);

                await coordinator.StopAsync();
                await WaitUntilAsync(() => !active, TimeSpan.FromSeconds(2));
                await WaitUntilAsync(() => currentFrame is null, TimeSpan.FromSeconds(2));
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeeScreenShareCoordinator_ToggleIsIdempotent_AndStopClearsFrame()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var fakeSource = new FakeScreenCaptureSource();
            var active = false;
            Bitmap? currentFrame = null;

            var coordinator = new HelpeeScreenShareCoordinator(
                isDisposed: static () => false,
                canShowScreenShareAction: static () => true,
                isPreviewActive: () => active,
                captureSourceFactory: new FixedCaptureSourceFactory(fakeSource),
                setPreviewActive: value => active = value,
                setStatus: _ => { },
                getPreviewFrame: () => currentFrame,
                setPreviewFrame: value => currentFrame = value,
                decodeFrame: _ => CreateTinyBitmap());

            try
            {
                coordinator.Toggle();
                coordinator.Toggle();

                await WaitUntilAsync(() => active, TimeSpan.FromSeconds(2));
                Assert.Equal(1, fakeSource.StartCallCount);

                fakeSource.RaiseFrame(1, 1, new byte[] { 1 }, "jpeg");
                await WaitUntilAsync(() => currentFrame is not null, TimeSpan.FromSeconds(2));

                await coordinator.StopAsync();

                Assert.False(active);
                Assert.False(fakeSource.IsStarted);
                Assert.Equal(1, fakeSource.StopCallCount);
                Assert.Equal(0, fakeSource.FrameSubscriberCount);
                Assert.Equal(0, coordinator.DecodeTasksActive);
                Assert.Null(currentFrame);
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeeScreenShareCoordinator_H264Preview_WaitsForStreamConfigBeforeDecode()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var fakeSource = new FakeScreenCaptureSource();
            var fakeDecoder = new FakePreviewH264BitmapDecoder();
            var active = false;
            Bitmap? currentFrame = null;

            var coordinator = new HelpeeScreenShareCoordinator(
                isDisposed: static () => false,
                canShowScreenShareAction: static () => true,
                isPreviewActive: () => active,
                captureSourceFactory: new FixedCaptureSourceFactory(fakeSource),
                setPreviewActive: value => active = value,
                setStatus: _ => { },
                getPreviewFrame: () => currentFrame,
                setPreviewFrame: value => currentFrame = value,
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                h264Decoder: fakeDecoder);

            try
            {
                coordinator.Toggle();
                await WaitUntilAsync(() => active, TimeSpan.FromSeconds(2));

                fakeSource.RaiseFrame(1, 1, new byte[] { 3 }, "h264", isKeyFrame: true, streamEpoch: 4);
                await Task.Delay(50);
                Assert.Null(currentFrame);
                Assert.Equal(0, fakeDecoder.ConfigureCallCount);
                Assert.Equal(0, fakeDecoder.DecodeCallCount);

                fakeSource.RaiseFrame(
                    1,
                    1,
                    new byte[] { 8 },
                    "h264",
                    isKeyFrame: true,
                    streamEpoch: 4,
                    streamConfig: new ScreenShareVideoStreamConfigV1
                    {
                        SessionId = "preview",
                        StreamEpoch = 4,
                        Encoding = "h264",
                        CodecProfile = "baseline",
                        DecoderConfigData = new byte[] { 1, 2, 3 },
                    });

                await WaitUntilAsync(
                    () => currentFrame is Bitmap latest && latest.PixelSize.Width == 8 && coordinator.DecodeTasksActive == 0,
                    TimeSpan.FromSeconds(2));

                Assert.Equal(1, fakeDecoder.ConfigureCallCount);
                Assert.Equal(1, fakeDecoder.DecodeCallCount);
                Assert.Equal(4, fakeDecoder.LastConfiguredEpoch);

                await coordinator.StopAsync();
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeeScreenShareCoordinator_H264Preview_NeedMoreInput_DoesNotResetStreamState_AndLaterAppliesFrame()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var fakeSource = new FakeScreenCaptureSource();
            var fakeDecoder = new FakePreviewH264BitmapDecoder
            {
                NeedMoreInputBeforeSuccessCount = 1,
            };
            var active = false;
            Bitmap? currentFrame = null;
            var status = new ScreenShareStatus(ScreenShareState.Off, null, DateTimeOffset.UtcNow);

            var coordinator = new HelpeeScreenShareCoordinator(
                isDisposed: static () => false,
                canShowScreenShareAction: static () => true,
                isPreviewActive: () => active,
                captureSourceFactory: new FixedCaptureSourceFactory(fakeSource),
                setPreviewActive: value => active = value,
                setStatus: value => status = value,
                getPreviewFrame: () => currentFrame,
                setPreviewFrame: value => currentFrame = value,
                decodeFrame: _ => throw new InvalidOperationException("jpeg should not be used"),
                h264Decoder: fakeDecoder);

            try
            {
                coordinator.Toggle();
                await WaitUntilAsync(() => active, TimeSpan.FromSeconds(2));

                var streamConfig = new ScreenShareVideoStreamConfigV1
                {
                    SessionId = "preview",
                    StreamEpoch = 7,
                    Encoding = "h264",
                    CodecProfile = "baseline",
                    DecoderConfigData = new byte[] { 1, 2, 3 },
                };

                fakeSource.RaiseFrame(1, 1, new byte[] { 21 }, "h264", isKeyFrame: true, streamEpoch: 7, streamConfig: streamConfig);
                await Task.Delay(100);
                Assert.Null(currentFrame);
                Assert.Equal(ScreenShareState.Active, status.State);
                Assert.Equal(1, fakeDecoder.ConfigureCallCount);
                Assert.Equal(1, fakeDecoder.DecodeCallCount);
                Assert.Equal(0, fakeDecoder.ResetCallCount);

                fakeSource.RaiseFrame(1, 1, new byte[] { 22 }, "h264", isKeyFrame: false, streamEpoch: 7);
                await WaitUntilAsync(
                    () => currentFrame is Bitmap latest && latest.PixelSize.Width == 22 && coordinator.DecodeTasksActive == 0,
                    TimeSpan.FromSeconds(2));

                Assert.Equal(1, fakeDecoder.ConfigureCallCount);
                Assert.Equal(2, fakeDecoder.DecodeCallCount);
                Assert.Equal(0, fakeDecoder.ResetCallCount);
                Assert.Equal(ScreenShareState.Active, status.State);

                await coordinator.StopAsync();
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task Helpee_Toggle_RapidStartStop_DoesNotHangOrStick()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var fakeSource = new FakeScreenCaptureSource();
            var active = false;
            Bitmap? currentFrame = null;
            var status = new ScreenShareStatus(ScreenShareState.Off, null, DateTimeOffset.UtcNow);
            var decodeCalls = 0;

            var coordinator = new HelpeeScreenShareCoordinator(
                isDisposed: static () => false,
                canShowScreenShareAction: static () => true,
                isPreviewActive: () => active,
                captureSourceFactory: new FixedCaptureSourceFactory(fakeSource),
                setPreviewActive: value => active = value,
                setStatus: value => status = value,
                getPreviewFrame: () => currentFrame,
                setPreviewFrame: value => currentFrame = value,
                decodeFrame: bytes =>
                {
                    Interlocked.Increment(ref decodeCalls);
                    return CreateBitmap(bytes[0], 1);
                });

            try
            {
                for (var i = 0; i < 10; i++)
                {
                    coordinator.Toggle();
                    coordinator.Toggle();
                }

                await AwaitCompletesAsync(
                    coordinator.StopAsync(),
                    TimeSpan.FromSeconds(2),
                    "helpee rapid-toggle final stop");

                await WaitUntilAsync(
                    () => !active &&
                          currentFrame is null &&
                          status.State == ScreenShareState.Off &&
                          fakeSource.FrameSubscriberCount == 0 &&
                          !fakeSource.IsStarted,
                    TimeSpan.FromSeconds(2));

                var decodeCallsBeforePostStopFrame = Volatile.Read(ref decodeCalls);
                fakeSource.RaiseFrame(1, 1, new byte[] { 9 }, "jpeg");
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                await Task.Yield();

                Assert.False(active);
                Assert.Null(currentFrame);
                Assert.Equal(ScreenShareState.Off, status.State);
                Assert.Equal(0, fakeSource.FrameSubscriberCount);
                Assert.False(fakeSource.IsStarted);
                Assert.Equal(0, coordinator.DecodeTasksActive);
                Assert.Equal(
                    decodeCallsBeforePostStopFrame,
                    Volatile.Read(ref decodeCalls));
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeeScreenShareCoordinator_StopDuringBlockedDecode_LeavesNoDecodeTaskRunning()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var fakeSource = new FakeScreenCaptureSource();
            var decodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDecode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var active = false;
            Bitmap? currentFrame = null;

            var coordinator = new HelpeeScreenShareCoordinator(
                isDisposed: static () => false,
                canShowScreenShareAction: static () => true,
                isPreviewActive: () => active,
                captureSourceFactory: new FixedCaptureSourceFactory(fakeSource),
                setPreviewActive: value => active = value,
                setStatus: _ => { },
                getPreviewFrame: () => currentFrame,
                setPreviewFrame: value => currentFrame = value,
                decodeFrame: _ =>
                {
                    decodeStarted.TrySetResult(true);
                    WaitForSignal(releaseDecode.Task, TimeSpan.FromSeconds(2));
                    return CreateTinyBitmap();
                });

            try
            {
                coordinator.Toggle();
                await WaitUntilAsync(() => active, TimeSpan.FromSeconds(2));

                fakeSource.RaiseFrame(1, 1, new byte[] { 1 }, "jpeg");
                await AwaitCompletesAsync(
                    decodeStarted.Task,
                    TimeSpan.FromSeconds(2),
                    "helpee decode start");

                var stopTask = Task.Run(coordinator.StopAsync);
                releaseDecode.TrySetResult(true);

                await AwaitCompletesAsync(
                    stopTask,
                    TimeSpan.FromSeconds(2),
                    "helpee stop during blocked decode");

                Assert.False(active);
                Assert.Equal(0, coordinator.DecodeTasksActive);
                Assert.Null(currentFrame);
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeeScreenShareCoordinator_StartFailure_SetsFailedStatus_AndCleansUp()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var failingSource = new FakeScreenCaptureSource
            {
                StartException = new InvalidOperationException("capture init failed"),
            };
            var previewActive = false;
            Bitmap? currentFrame = null;
            var status = new ScreenShareStatus(ScreenShareState.Off, null, DateTimeOffset.UtcNow);

            var coordinator = new HelpeeScreenShareCoordinator(
                isDisposed: static () => false,
                canShowScreenShareAction: static () => true,
                isPreviewActive: () => previewActive,
                captureSourceFactory: new FixedCaptureSourceFactory(failingSource),
                setPreviewActive: value => previewActive = value,
                setStatus: value => status = value,
                getPreviewFrame: () => currentFrame,
                setPreviewFrame: value => currentFrame = value,
                decodeFrame: _ => CreateTinyBitmap());

            try
            {
                coordinator.Toggle();

                await WaitUntilAsync(
                    () => status.State == ScreenShareState.Failed,
                    TimeSpan.FromSeconds(2));

                Assert.Equal("Screen sharing failed to start", status.UserMessage);
                Assert.False(previewActive);
                Assert.Null(currentFrame);
                Assert.False(failingSource.IsStarted);
                Assert.Equal(1, failingSource.StartCallCount);
                Assert.Equal(1, failingSource.DisposeCallCount);
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeePageViewModel_ScreenShareStartFailure_ShowsViewerError_AndCleansUp()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var view = new ScreenSharePlaceholderView
            {
                DataContext = new ScreenShareViewerErrorContext(),
            };
            var window = new Window { Width = 640, Height = 420, Content = view };
            window.Show();

            try
            {
                await WaitUntilAsync(
                    () => FindViewerMessageText(window) is { IsVisible: true, Text: "Screen sharing failed to start" },
                    TimeSpan.FromSeconds(2));
            }
            finally
            {
                window.Close();
            }

            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task HelpeeScreenShareCoordinator_InvalidFrame_DoesNotKillDecodeLoop_AndSubsequentFrameApplies()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var fakeSource = new FakeScreenCaptureSource();
            var active = false;
            Bitmap? currentFrame = null;
            var decodeCalls = 0;

            var coordinator = new HelpeeScreenShareCoordinator(
                isDisposed: static () => false,
                canShowScreenShareAction: static () => true,
                isPreviewActive: () => active,
                captureSourceFactory: new FixedCaptureSourceFactory(fakeSource),
                setPreviewActive: value => active = value,
                setStatus: _ => { },
                getPreviewFrame: () => currentFrame,
                setPreviewFrame: value => currentFrame = value,
                decodeFrame: bytes =>
                {
                    var next = Interlocked.Increment(ref decodeCalls);
                    if (next == 1)
                    {
                        throw new InvalidDataException("invalid preview frame");
                    }

                    return CreateBitmap(bytes[0], 1);
                });

            try
            {
                coordinator.Toggle();
                await WaitUntilAsync(() => active, TimeSpan.FromSeconds(2));

                fakeSource.RaiseFrame(1, 1, new byte[] { 1 }, "jpeg");
                await WaitUntilAsync(() => coordinator.DecodeTasksActive == 0, TimeSpan.FromSeconds(2));
                Assert.Null(currentFrame);

                fakeSource.RaiseFrame(1, 1, new byte[] { 9 }, "jpeg");
                await WaitUntilAsync(
                    () => currentFrame is Bitmap frame && frame.PixelSize.Width == 9,
                    TimeSpan.FromSeconds(2));
                await WaitUntilAsync(
                    () => coordinator.FramesDecoded == 1 && coordinator.DecodeTasksActive == 0,
                    TimeSpan.FromSeconds(2));

                Assert.Equal(2, decodeCalls);
                Assert.Equal(1, coordinator.FramesDecoded);
                Assert.Equal(0, coordinator.DecodeTasksActive);

                await coordinator.StopAsync();
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task TransportScreenShareCoordinator_InvalidFrame_DoesNotRaiseUnobservedException_AndLaterFrameStillSends()
    {
        using var unobserved = new UnobservedTaskExceptionRecorder();
        var fakeSource = new FakeScreenCaptureSource();
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 2);

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync);

        await coordinator.StartAsync("session-live", CancellationToken.None);

        RaiseTransportFrame(fakeSource, 1, 1, Array.Empty<byte>());
        await Task.Yield();
        ForceFullCollection();
        Assert.Empty(unobserved.Exceptions);

        RaiseTransportFrame(fakeSource, 1, 1, new byte[] { 1, 2, 3 });
        await AwaitCompletesAsync(
            probe.WaitForPayloadCountAsync(1, TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(2),
            "payload after invalid frame");

        Assert.Equal(1, probe.PayloadsSent);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenSharePlaceholderView_States_RenderExpectedSurfaceAndCenteredErrorOverlay()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var remoteFrame = CreateBitmap(640, 360);
            using var previewFrame = CreateBitmap(360, 640);

            var blankView = new ScreenSharePlaceholderView
            {
                DataContext = new ScreenSharePlaceholderContext(),
            };
            var blankWindow = new Window { Width = 1200, Height = 800, Content = blankView };
            blankWindow.Show();

            try
            {
                await WaitUntilAsync(
                    () => blankView.GetVisualDescendants().OfType<Grid>().Any(),
                    TimeSpan.FromSeconds(2));

                var blankImages = blankView.GetVisualDescendants().OfType<Image>().ToArray();
                Assert.Equal(2, blankImages.Length);
                Assert.All(blankImages, image => Assert.False(image.IsVisible));

                var blankRoot = blankView.GetVisualDescendants().OfType<Grid>().First();
                Assert.True(blankRoot.ClipToBounds);
            }
            finally
            {
                blankWindow.Close();
            }

            var remoteView = new ScreenSharePlaceholderView
            {
                DataContext = new ScreenSharePlaceholderContext
                {
                    ShowRemoteScreenShareFrame = true,
                    RemoteFrame = remoteFrame,
                },
            };
            var remoteWindow = new Window { Width = 1280, Height = 900, Content = remoteView };
            remoteWindow.Show();

            try
            {
                await WaitUntilAsync(
                    () => remoteView.GetVisualDescendants().OfType<Image>().Any(image => image.IsVisible),
                    TimeSpan.FromSeconds(2));

                var remoteImages = remoteView.GetVisualDescendants().OfType<Image>().ToArray();
                Assert.True(remoteImages[0].IsVisible);
                Assert.False(remoteImages[1].IsVisible);
                Assert.Same(remoteFrame, remoteImages[0].Source);
            }
            finally
            {
                remoteWindow.Close();
            }

            var previewView = new ScreenSharePlaceholderView
            {
                DataContext = new ScreenSharePlaceholderContext
                {
                    ShowScreenSharePreviewFrame = true,
                    PreviewFrame = previewFrame,
                },
            };
            var previewWindow = new Window { Width = 900, Height = 1200, Content = previewView };
            previewWindow.Show();

            try
            {
                await WaitUntilAsync(
                    () => previewView.GetVisualDescendants().OfType<Image>().Any(image => image.IsVisible),
                    TimeSpan.FromSeconds(2));

                var previewImages = previewView.GetVisualDescendants().OfType<Image>().ToArray();
                Assert.False(previewImages[0].IsVisible);
                Assert.True(previewImages[1].IsVisible);
                Assert.Same(previewFrame, previewImages[1].Source);
            }
            finally
            {
                previewWindow.Close();
            }

            var errorView = new ScreenSharePlaceholderView
            {
                DataContext = new ScreenSharePlaceholderContext
                {
                    ShowScreenShareViewerError = true,
                    ScreenShareViewerMessage = "Screen sharing failed to start",
                },
            };
            var errorWindow = new Window { Width = 1400, Height = 900, Content = errorView };
            errorWindow.Show();

            try
            {
                await WaitUntilAsync(
                    () => FindViewerMessageText(errorWindow) is { IsVisible: true },
                    TimeSpan.FromSeconds(2));

                var overlay = errorView.GetVisualDescendants()
                    .OfType<Border>()
                    .First(border => border.Child is TextBlock);
                var message = Assert.IsType<TextBlock>(overlay.Child);

                Assert.Equal(HorizontalAlignment.Center, overlay.HorizontalAlignment);
                Assert.Equal(VerticalAlignment.Center, overlay.VerticalAlignment);
                Assert.Equal(new Thickness(16), overlay.Margin);
                Assert.Equal(360d, message.MaxWidth);
                Assert.True(overlay.IsVisible);
                Assert.Equal("Screen sharing failed to start", message.Text);
            }
            finally
            {
                errorWindow.Close();
            }

            return true;
        }, default);
    }

[Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenSharePlaceholderView_WhenWindowIsLarger_SurfaceBoundsIncreaseWithoutDecodeChanges()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var remoteFrame = CreateBitmap(640, 360);

            var smallView = new ScreenSharePlaceholderView
            {
                DataContext = new ScreenSharePlaceholderContext
                {
                    ShowRemoteScreenShareFrame = true,
                    RemoteFrame = remoteFrame,
                },
            };
            var smallWindow = new Window { Width = 760, Height = 560, Content = smallView };
            smallWindow.Show();

            Rect smallBounds;
            try
            {
                await WaitUntilAsync(() => smallView.Bounds.Width > 0 && smallView.Bounds.Height > 0, TimeSpan.FromSeconds(2));
                smallBounds = smallView.Bounds;
            }
            finally
            {
                smallWindow.Close();
            }

            var largeView = new ScreenSharePlaceholderView
            {
                DataContext = new ScreenSharePlaceholderContext
                {
                    ShowRemoteScreenShareFrame = true,
                    RemoteFrame = remoteFrame,
                },
            };
            var largeWindow = new Window { Width = 1280, Height = 860, Content = largeView };
            largeWindow.Show();

            try
            {
                await WaitUntilAsync(() => largeView.Bounds.Width > 0 && largeView.Bounds.Height > 0, TimeSpan.FromSeconds(2));
                var largeBounds = largeView.Bounds;

                Assert.True(
                    largeBounds.Width > smallBounds.Width,
                    $"Expected screenshare surface width to grow with a larger window. Before={smallBounds.Width:N1}, After={largeBounds.Width:N1}.");
                Assert.True(
                    largeBounds.Height > smallBounds.Height,
                    $"Expected screenshare surface height to grow with a larger window. Before={smallBounds.Height:N1}, After={largeBounds.Height:N1}.");
            }
            finally
            {
                largeWindow.Close();
            }

            return true;
        }, default);
    }

}
