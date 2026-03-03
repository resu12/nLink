using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
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
                        releaseDecode.Task.GetAwaiter().GetResult();
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
                        releaseFirstDecode.Task.GetAwaiter().GetResult();
                    }

                    return CreateBitmap(bytes[0], 1);
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
    public async Task ScreenShareViewerViewModel_AlternatingFrameSizes_UpdatesCurrentFrame()
    {
        await fixture.Session.Dispatch(async () =>
        {
            using var vm = new ScreenShareViewerViewModel(
                decodeFrame: bytes => CreateBitmap(bytes[0] == 1 ? 1 : 2, 1));

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
