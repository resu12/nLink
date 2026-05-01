using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

public abstract class ScreenShareViewerViewModelTestBase : IClassFixture<ScreenShareCoordinatorFixture>
{
internal readonly ScreenShareCoordinatorFixture fixture;

protected ScreenShareViewerViewModelTestBase(ScreenShareCoordinatorFixture fixture)
    {
        this.fixture = fixture;
        ScreenShareFrameLossAttributionRegistry.ResetAllForTests();
    }

internal static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
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

internal static bool CurrentFrameWidthEqualsSafely(ScreenShareViewerViewModel vm, int expectedWidth)
    {
        return TryGetFrameWidth(vm.CurrentFrame, out var width) && width == expectedWidth;
    }

internal static void AssertCurrentFrameWidthSafely(ScreenShareViewerViewModel vm, int expectedWidth)
    {
        Assert.True(TryGetFrameWidth(vm.CurrentFrame, out var width), "Expected the current frame to be a readable bitmap.");
        Assert.Equal(expectedWidth, width);
    }

private static bool TryGetFrameWidth(IImage? frame, out int width)
    {
        width = 0;
        if (frame is not Bitmap bitmap)
        {
            return false;
        }

        try
        {
            width = bitmap.PixelSize.Width;
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (NullReferenceException)
        {
            return false;
        }
    }

internal static Bitmap CreateTinyBitmap()
    {
        using var stream = new MemoryStream(CreateTinyPngBytes(), writable: false);
        return new Bitmap(stream);
    }

internal static Bitmap CreateBitmap(int width, int height)
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

internal static byte[] CreateTinyPngBytes()
    {
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a5kAAAAASUVORK5CYII=");
    }

internal static byte[] CreateTinyJpegBytes()
    {
        return Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wCEAAkGBxAQEBUQEBAVFRUVFRUVFRUVFRUVFRUVFRUWFhUVFRUYHSggGBolHRUVITEhJSkrLi4uFx8zODMsNygtLisBCgoKDg0OGxAQGi0fHyUtLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLS0tLf/AABEIAAEAAQMBIgACEQEDEQH/xAAXAAEBAQEAAAAAAAAAAAAAAAAAAQID/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAwDAQACEAMQAAAB6AAAAP/EABQQAQAAAAAAAAAAAAAAAAAAACD/2gAIAQEAAT8Af//EABQRAQAAAAAAAAAAAAAAAAAAACD/2gAIAQIBAT8Af//EABQRAQAAAAAAAAAAAAAAAAAAACD/2gAIAQMBAT8Af//Z");
    }

internal static ScreenShareVideoFragmentV1 CreatePartialFragment(string sessionId, long streamEpoch, long frameId, int fragmentIndex, bool? isKeyFrame = null)
    {
        return new ScreenShareVideoFragmentV1
        {
            Type = ScreenShareVideoPayloadCodec.ScreenShareVideoFragmentTypeV1,
            SessionId = sessionId,
            StreamEpoch = streamEpoch,
            FrameId = frameId,
            Width = 640,
            Height = 360,
            CapturedTsUtcMs = (streamEpoch * 1000) + frameId,
            Encoding = "h264",
            IsKeyFrame = isKeyFrame ?? frameId == 10,
            FragmentIndex = fragmentIndex,
            FragmentCount = 2,
            Data = new byte[] { (byte)frameId, (byte)fragmentIndex },
        };
    }

internal static void CompleteFrame(
        ScreenShareVideoFrameReassembler reassembler,
        string sessionId,
        long streamEpoch,
        long frameId,
        bool? isKeyFrame = null)
    {
        reassembler.OnFragment(CreateFragment(sessionId, streamEpoch, frameId, fragmentIndex: 0, isKeyFrame));
        reassembler.OnFragment(CreateFragment(sessionId, streamEpoch, frameId, fragmentIndex: 1, isKeyFrame));
    }

internal static ScreenShareVideoFragmentV1 CreateFragment(
        string sessionId,
        long streamEpoch,
        long frameId,
        int fragmentIndex,
        bool? isKeyFrame = null)
    {
        return new ScreenShareVideoFragmentV1
        {
            Type = ScreenShareVideoPayloadCodec.ScreenShareVideoFragmentTypeV1,
            SessionId = sessionId,
            StreamEpoch = streamEpoch,
            FrameId = frameId,
            Width = 640,
            Height = 360,
            CapturedTsUtcMs = (streamEpoch * 1000) + frameId,
            Encoding = "h264",
            IsKeyFrame = isKeyFrame ?? frameId == 10,
            FragmentIndex = fragmentIndex,
            FragmentCount = 2,
            Data = new byte[] { (byte)frameId, (byte)fragmentIndex },
        };
    }

internal sealed class FakeH264BitmapDecoder : IWindowsH264BitmapDecoder
    {
        public bool IsSupported => true;

        public int ConfigureCallCount { get; private set; }

        public int DecodeCallCount { get; private set; }

        public long LastConfiguredEpoch { get; private set; }

        public void ConfigureStream(ScreenShareVideoStreamConfigV1 config)
        {
            ConfigureCallCount++;
            LastConfiguredEpoch = config.StreamEpoch;
        }

        public void Reset()
        {
        }

        public Bitmap Decode(EncodedFrameDecodeRequest request)
        {
            DecodeCallCount++;
            return CreateBitmap(request.EncodedFrameBytes.Span[0], 1);
        }

        public void Dispose()
        {
        }
    }

internal sealed class NeedMoreInputH264BitmapDecoder : IWindowsH264BitmapDecoder
    {
        public bool IsSupported => true;

        public void ConfigureStream(ScreenShareVideoStreamConfigV1 config)
        {
        }

        public void Reset()
        {
        }

        public Bitmap Decode(EncodedFrameDecodeRequest request)
        {
            throw new H264DecoderNeedsMoreInputException("more input required");
        }

        public void Dispose()
        {
        }
    }

internal sealed class BlockingH264BitmapDecoder : IWindowsH264BitmapDecoder
    {
        private readonly TaskCompletionSource<bool> decodeStarted;
        private readonly TaskCompletionSource<bool> releaseDecode;
        private int decodeCalls;

        public BlockingH264BitmapDecoder(
            TaskCompletionSource<bool> decodeStarted,
            TaskCompletionSource<bool> releaseDecode)
        {
            this.decodeStarted = decodeStarted;
            this.releaseDecode = releaseDecode;
        }

        public bool IsSupported => true;

        public void ConfigureStream(ScreenShareVideoStreamConfigV1 config)
        {
        }

        public void Reset()
        {
        }

        public Bitmap Decode(EncodedFrameDecodeRequest request)
        {
            if (Interlocked.Increment(ref decodeCalls) == 1)
            {
                decodeStarted.TrySetResult(true);
                Assert.True(releaseDecode.Task.Wait(TimeSpan.FromSeconds(2)));
            }

            return CreateBitmap(request.EncodedFrameBytes.Span[0], 1);
        }

        public void Dispose()
        {
        }
    }

internal sealed class FrameBlockingH264BitmapDecoder : IWindowsH264BitmapDecoder
    {
        private readonly long blockedFrameId;
        private readonly TaskCompletionSource<bool> decodeStarted;
        private readonly TaskCompletionSource<bool> releaseDecode;

        public FrameBlockingH264BitmapDecoder(
            long blockedFrameId,
            TaskCompletionSource<bool> decodeStarted,
            TaskCompletionSource<bool> releaseDecode)
        {
            this.blockedFrameId = blockedFrameId;
            this.decodeStarted = decodeStarted;
            this.releaseDecode = releaseDecode;
        }

        public bool IsSupported => true;

        public void ConfigureStream(ScreenShareVideoStreamConfigV1 config)
        {
        }

        public void Reset()
        {
        }

        public Bitmap Decode(EncodedFrameDecodeRequest request)
        {
            if (request.FrameId == blockedFrameId)
            {
                decodeStarted.TrySetResult(true);
                Assert.True(releaseDecode.Task.Wait(TimeSpan.FromSeconds(2)));
            }

            return CreateBitmap(request.EncodedFrameBytes.Span[0], 1);
        }

        public void Dispose()
        {
        }
    }

}


