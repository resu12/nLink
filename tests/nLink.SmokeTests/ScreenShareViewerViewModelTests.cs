using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NLink.App.ViewModels;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenShareViewerViewModelTests : IClassFixture<ScreenShareCoordinatorFixture>
{
    private readonly ScreenShareCoordinatorFixture fixture;

    public ScreenShareViewerViewModelTests(ScreenShareCoordinatorFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_DecodeFailure_DoesNotFreezeFutureFrames()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var decodeCalls = 0;

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ =>
                {
                    var next = Interlocked.Increment(ref decodeCalls);
                    if (next == 1)
                    {
                        throw new InvalidDataException("invalid jpeg");
                    }

                    return CreateTinyBitmap();
                },
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                });

            vm.OnJpegFrame(new byte[] { 1 });
            await WaitUntilAsync(
                () => string.Equals(vm.StatusText, "Invalid frame received", StringComparison.Ordinal) && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            vm.OnJpegFrame(new byte[] { 2 });
            await WaitUntilAsync(
                () => vm.CurrentFrame is not null && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            Assert.NotNull(vm.CurrentFrame);
            Assert.Equal("Live", vm.StatusText);
            Assert.Equal(2, decodeCalls);
            Assert.True(vm.IsIdleForDiagnostics, "Expected viewer decode loop to return to idle after recovery frame.");

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewerViewModel_RapidFrames_CoalescesDecodeAndPublishesFrame()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var decodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDecode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var decodeCallCount = 0;

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: bytes =>
                {
                    var call = Interlocked.Increment(ref decodeCallCount);
                    if (call == 1)
                    {
                        decodeStarted.TrySetResult(true);
                        WaitForSignal(releaseDecode.Task, TimeSpan.FromSeconds(2));
                    }

                    return CreateTinyBitmap();
                });

            try
            {
                vm.OnJpegFrame(CreateTinyJpegBytes());
                await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

                vm.OnJpegFrame(CreateTinyJpegBytes());
                vm.OnJpegFrame(CreateTinyJpegBytes());

                releaseDecode.TrySetResult(true);

                await WaitUntilAsync(() => vm.CurrentFrame is not null, TimeSpan.FromSeconds(2));

                var metrics = vm.GetMetricsSnapshot();
                Assert.True(vm.IsActive);
                Assert.Equal("Live", vm.StatusText);
                Assert.NotNull(vm.CurrentFrame);
                Assert.InRange(decodeCallCount, 1, 3);
                Assert.True(metrics.FramesDecoded >= 1);
                Assert.True(metrics.FramesCoalesced >= 1);
            }
            finally
            {
            }

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewerViewModel_WhenFlooded_OnlyDecodesLatestFrames()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var firstDecodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstDecode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var decodeCallCount = 0;

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: bytes =>
                {
                    var call = Interlocked.Increment(ref decodeCallCount);
                    if (call == 1)
                    {
                        firstDecodeStarted.TrySetResult(true);
                        WaitForSignal(releaseFirstDecode.Task, TimeSpan.FromSeconds(2));
                    }

                    return CreateBitmap(bytes.Span[0], 1);
                });

            vm.OnJpegFrame(new byte[] { 1 });
            await firstDecodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            for (byte i = 2; i <= 20; i++)
            {
                vm.OnJpegFrame(new byte[] { i });
            }

            releaseFirstDecode.TrySetResult(true);

            await WaitUntilAsync(() => vm.CurrentFrame is Bitmap, TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap latest && latest.PixelSize.Width == 20,
                TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.NotNull(vm.CurrentFrame);
            Assert.True(vm.IsActive);
            Assert.Equal("Live", vm.StatusText);
            Assert.InRange(decodeCallCount, 2, 3);
            Assert.InRange(metrics.FramesDecoded, 2, 3);
            Assert.True(metrics.FramesCoalesced >= 18);

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_SlowDecode_AppliesLatestFrame()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var decodeGate = new SemaphoreSlim(0, 1);
            var firstDecodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var decodeCallCount = 0;

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: bytes =>
                {
                    var call = Interlocked.Increment(ref decodeCallCount);
                    if (call == 1)
                    {
                        firstDecodeStarted.TrySetResult(true);
                        Assert.True(
                            decodeGate.Wait(TimeSpan.FromSeconds(2)),
                            "Timed out waiting to release the first viewer decode.");
                    }

                    return CreateBitmap(bytes.Span[0], 1);
                },
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                });

            vm.OnJpegFrame(new byte[] { 1 });
            await WaitUntilAsync(
                () => firstDecodeStarted.Task.IsCompleted,
                TimeSpan.FromSeconds(2));

            vm.OnJpegFrame(new byte[] { 2 });
            vm.OnJpegFrame(new byte[] { 5 });

            decodeGate.Release();

            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap latest && latest.PixelSize.Width == 5 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            Assert.NotNull(vm.CurrentFrame);
            Assert.Equal("Live", vm.StatusText);
            Assert.InRange(decodeCallCount, 2, 3);
            Assert.True(vm.IsIdleForDiagnostics, "Expected viewer decode loop to return to idle after applying the latest frame.");

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_CurrentFrame_Progresses_UnderRapidFrames_AndSlowDecode()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var decodeGate = new SemaphoreSlim(0, 6);
            var appliedFrames = 0;

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: bytes =>
                {
                    Assert.True(
                        decodeGate.Wait(TimeSpan.FromSeconds(2)),
                        "Timed out waiting to release viewer decode.");
                    return CreateBitmap(bytes.Span[0], 1);
                },
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                });

            vm.PropertyChanged += (_, e) =>
            {
                if (!string.Equals(e.PropertyName, nameof(ScreenShareViewerViewModel.CurrentFrame), StringComparison.Ordinal))
                {
                    return;
                }

                if (vm.CurrentFrame is null)
                {
                    return;
                }
                Interlocked.Increment(ref appliedFrames);
            };

            for (byte i = 1; i <= 20; i++)
            {
                vm.OnJpegFrame(new byte[] { i });
            }

            decodeGate.Release(2);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 20 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            for (byte i = 21; i <= 35; i++)
            {
                vm.OnJpegFrame(new byte[] { i });
            }

            decodeGate.Release(2);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap second && second.PixelSize.Width == 35 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            for (byte i = 36; i <= 50; i++)
            {
                vm.OnJpegFrame(new byte[] { i });
            }

            decodeGate.Release(2);
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap third && third.PixelSize.Width == 50 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));
            await WaitUntilAsync(
                () => vm.IsActive && string.Equals(vm.StatusText, "Live", StringComparison.Ordinal),
                TimeSpan.FromSeconds(2));

            Assert.True(appliedFrames >= 3, $"Expected at least 3 applied frames, but saw {appliedFrames}.");
            Assert.True(vm.IsActive);
            Assert.Equal("Live", vm.StatusText);

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_CurrentFrame_AppliesLatestFrame_WhenDecodeSlowerThanArrival()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var decodeGate = new SemaphoreSlim(0, 2);
            var firstDecodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var decodeCalls = 0;
            var lastDecodedMarker = 0;

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: bytes =>
                {
                    var call = Interlocked.Increment(ref decodeCalls);
                    Volatile.Write(ref lastDecodedMarker, bytes.Span[0]);
                    if (call == 1)
                    {
                        firstDecodeStarted.TrySetResult(true);
                    }

                    Assert.True(
                        decodeGate.Wait(TimeSpan.FromSeconds(2)),
                        $"Timed out waiting to release viewer decode {call}.");
                    return CreateBitmap(bytes.Span[0], 1);
                },
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                });

            vm.OnJpegFrame(new byte[] { 1 });
            await firstDecodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            for (byte i = 2; i <= 20; i++)
            {
                vm.OnJpegFrame(new byte[] { i });
            }

            decodeGate.Release(2);

            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap latest && latest.PixelSize.Width == 20 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            Assert.Equal(20, Volatile.Read(ref lastDecodedMarker));
            Assert.InRange(decodeCalls, 2, 3);
            Assert.NotNull(vm.CurrentFrame);
            Assert.Equal("Live", vm.StatusText);
            Assert.True(vm.IsIdleForDiagnostics, "Expected viewer decode loop to return to idle after applying the latest frame.");

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_OnJpegFrame_CopiesInputBeforeAsyncDecode()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var decodeGate = new SemaphoreSlim(0, 1);
            var decodedMarker = 0;

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: bytes =>
                {
                    Assert.True(
                        decodeGate.Wait(TimeSpan.FromSeconds(2)),
                        "Timed out waiting to release viewer decode.");
                    decodedMarker = bytes.Span[0];
                    return CreateBitmap(bytes.Span[0], 1);
                },
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                });

            var source = new byte[] { 7 };
            vm.OnJpegFrame(source);
            source[0] = 9;
            decodeGate.Release();

            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap current && current.PixelSize.Width == 7 && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            Assert.Equal(7, decodedMarker);
            Assert.Equal("Live", vm.StatusText);

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewer_DefaultDispatcherPath_RendersFrame_FromBackgroundCaller()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => CreateTinyBitmap());

            await Task.Run(() => vm.OnJpegFrame(CreateTinyJpegBytes()));

            await WaitUntilAsync(
                () => vm.CurrentFrame is not null && vm.IsIdleForDiagnostics,
                TimeSpan.FromSeconds(2));

            Assert.NotNull(vm.CurrentFrame);
            Assert.Equal("Live", vm.StatusText);

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewerViewModel_ClearAndDispose_AreIdempotent()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => CreateTinyBitmap());

            vm.OnJpegFrame(CreateTinyJpegBytes());
            await WaitUntilAsync(() => vm.CurrentFrame is not null, TimeSpan.FromSeconds(2));

            vm.Clear();
            vm.Clear();

            Assert.False(vm.IsActive);
            Assert.Null(vm.CurrentFrame);

            vm.Dispose();
            vm.Dispose();

            Assert.False(vm.IsActive);
            Assert.Null(vm.CurrentFrame);

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Viewer_Dispose_PreventsFurtherFrameApply()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var firstApplyObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var applyCount = 0;

            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ => CreateTinyBitmap(),
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                });

            vm.PropertyChanged += (_, e) =>
            {
                if (!string.Equals(e.PropertyName, nameof(ScreenShareViewerViewModel.CurrentFrame), StringComparison.Ordinal))
                {
                    return;
                }

                if (vm.CurrentFrame is null)
                {
                    return;
                }

                if (Interlocked.Increment(ref applyCount) == 1)
                {
                    firstApplyObserved.TrySetResult(true);
                }
            };

            vm.OnJpegFrame(CreateTinyJpegBytes());
            await firstApplyObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => vm.IsIdleForDiagnostics, TimeSpan.FromSeconds(2));

            vm.Dispose();

            var applyCountAfterDispose = Volatile.Read(ref applyCount);
            var exception = Assert.Throws<ObjectDisposedException>(() => vm.OnJpegFrame(CreateTinyJpegBytes()));

            Assert.Contains(nameof(ScreenShareViewerViewModel), exception.ObjectName ?? string.Empty, StringComparison.Ordinal);
            Assert.Equal(applyCountAfterDispose, Volatile.Read(ref applyCount));
            Assert.True(vm.IsIdleForDiagnostics, "Expected viewer decode loop to remain idle after dispose.");

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewerViewModel_AlternatingFrameSizes_UpdatesCurrentFrame()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: bytes => CreateBitmap(bytes.Span[0] == 1 ? 1 : 2, 1));

            vm.OnJpegFrame(new byte[] { 1 });
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap first && first.PixelSize.Width == 1 && first.PixelSize.Height == 1,
                TimeSpan.FromSeconds(2));

            vm.OnJpegFrame(new byte[] { 2 });
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap second && second.PixelSize.Width == 2 && second.PixelSize.Height == 1,
                TimeSpan.FromSeconds(2));

            vm.OnJpegFrame(new byte[] { 1 });
            await WaitUntilAsync(
                () => vm.CurrentFrame is Bitmap third && third.PixelSize.Width == 1 && third.PixelSize.Height == 1,
                TimeSpan.FromSeconds(2));

            Assert.True(vm.IsActive);
            Assert.Equal("Live", vm.StatusText);

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewerViewModel_InvalidFrame_DoesNotThrow_AndSubsequentValidFrameRenders()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var decodeCallCount = 0;
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: _ =>
                {
                    var next = Interlocked.Increment(ref decodeCallCount);
                    if (next == 1)
                    {
                        throw new InvalidDataException("invalid jpeg");
                    }

                    return CreateTinyBitmap();
                });

            vm.OnJpegFrame(new byte[] { 1 });
            await WaitUntilAsync(() => string.Equals(vm.StatusText, "Invalid frame received", StringComparison.Ordinal), TimeSpan.FromSeconds(2));
            Assert.Null(vm.CurrentFrame);
            Assert.True(vm.IsActive);

            vm.OnJpegFrame(new byte[] { 2 });
            await WaitUntilAsync(() => vm.CurrentFrame is not null, TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();
            Assert.Equal("Live", vm.StatusText);
            Assert.NotNull(vm.CurrentFrame);
            Assert.Equal(2, decodeCallCount);
            Assert.Equal(1, metrics.DecodeErrors);
            Assert.Equal(1, metrics.FramesDecoded);

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareViewerViewModel_Metrics_TrackRenderInterval_CaptureToRender_AndStaleFrames()
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

            vm.OnJpegFrame(CreateTinyJpegBytes(), capturedTsUtcMs: DateTimeOffset.UtcNow.AddMilliseconds(-1500).ToUnixTimeMilliseconds());
            await WaitUntilAsync(() => vm.GetMetricsSnapshot().FramesDecoded >= 1, TimeSpan.FromSeconds(2));

            await Task.Delay(40);

            vm.OnJpegFrame(CreateTinyJpegBytes(), capturedTsUtcMs: DateTimeOffset.UtcNow.AddMilliseconds(-100).ToUnixTimeMilliseconds());
            await WaitUntilAsync(() => vm.GetMetricsSnapshot().FramesDecoded >= 2, TimeSpan.FromSeconds(2));

            var metrics = vm.GetMetricsSnapshot();

            Assert.Equal(2, metrics.FramesDecoded);
            Assert.True(metrics.AverageRenderIntervalMs > 0, $"Expected render interval metric to be recorded, got {metrics.AverageRenderIntervalMs}.");
            Assert.True(metrics.AverageCaptureToRenderMs > 0, $"Expected capture-to-render metric to be recorded, got {metrics.AverageCaptureToRenderMs}.");
            Assert.Equal(1, metrics.StaleFrameRenders);
            Assert.InRange(vm.LastRenderedFrameAgeMs, 0, 750);

            return true;
        }, default);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Yield();
        }

        Assert.True(predicate(), $"Condition not met within {timeout.TotalSeconds:N1}s.");
    }

    private static void WaitForSignal(Task signal, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!signal.IsCompleted && DateTime.UtcNow < deadline)
        {
            Thread.Yield();
        }

        Assert.True(signal.IsCompleted, $"Signal was not completed within {timeout.TotalSeconds:N1}s.");
        Assert.False(signal.IsCanceled, "Signal was canceled unexpectedly.");
        Assert.False(signal.IsFaulted, $"Signal faulted unexpectedly: {signal.Exception}");
    }

    private static Bitmap CreateTinyBitmap()
    {
        using var stream = new MemoryStream(CreateTinyPngBytes(), writable: false);
        return new Bitmap(stream);
    }

    private static Bitmap CreateBitmap(int width, int height)
    {
        var writeable = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var locked = writeable.Lock())
        {
            var totalBytes = width * height * 4;
            var pixels = new byte[totalBytes];
            Marshal.Copy(pixels, 0, locked.Address, totalBytes);
        }

        return writeable;
    }

    private static byte[] CreateTinyPngBytes()
    {
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a5kAAAAASUVORK5CYII=");
    }

    private static byte[] CreateTinyJpegBytes()
    {
        return Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wCEAAkGBxAQEBUQEBAVFRUVFRUVFRUVFRUVFRUVFRUWFhUVFRUYHSggGBolHRUVITEhJSkrLi4uFx8zODMsNygtLisBCgoKDg0OGxAQGi0fHyUtLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLf/AABEIAAEAAQMBIgACEQEDEQH/xAAXAAEBAQEAAAAAAAAAAAAAAAAAAQID/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAwDAQACEAMQAAAB6AAAAP/EABQQAQAAAAAAAAAAAAAAAAAAACD/2gAIAQEAAT8Af//EABQRAQAAAAAAAAAAAAAAAAAAACD/2gAIAQIBAT8Af//EABQRAQAAAAAAAAAAAAAAAAAAACD/2gAIAQMBAT8Af//Z");
    }
}
