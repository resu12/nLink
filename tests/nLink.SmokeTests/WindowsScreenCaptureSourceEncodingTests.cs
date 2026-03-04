using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.Versioning;
using NLink.App.Services.ScreenCapture;

namespace NLink.SmokeTests;

[SupportedOSPlatform("windows")]
public sealed class WindowsScreenCaptureSourceEncodingTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public void WindowsScreenCaptureSource_LowerDefaultJpegQuality_ProducesSmallerPayload()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var bitmap = CreatePatternBitmap(width: 640, height: 360);

        var previousPayload = WindowsScreenCaptureSource.EncodeBitmapToJpegBytesForTesting(bitmap, quality: 75);
        var currentPayload = WindowsScreenCaptureSource.EncodeBitmapToJpegBytesForTesting(
            bitmap,
            WindowsScreenCaptureSource.DefaultJpegQualityForTesting);

        Assert.True(
            currentPayload.Length < previousPayload.Length,
            $"Expected lower default JPEG quality to reduce payload size, but previous={previousPayload.Length} and current={currentPayload.Length}.");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsScreenCaptureSource_SlowEncode_ReducesAdaptiveScale()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fakeTimestamp = 0L;
        var encodedWidths = new List<int>();

        await using var source = new WindowsScreenCaptureSource(
            getTimestamp: () => fakeTimestamp,
            encodeBitmap: (bitmap, _) =>
            {
                encodedWidths.Add(bitmap.Width);
                fakeTimestamp += Stopwatch.Frequency / 40; // 25 ms
                return new byte[] { 1, 2, 3 };
            });

        using var bitmap = CreatePatternBitmap(width: 1000, height: 500);

        _ = source.EncodeFrameForTesting(bitmap);
        _ = source.EncodeFrameForTesting(bitmap);
        _ = source.EncodeFrameForTesting(bitmap);

        Assert.Equal(new[] { 1000, 750, 500 }, encodedWidths);

        var snapshot = source.GetEncodeMetricsSnapshot();
        Assert.Equal(3, snapshot.EncodedFrames);
        Assert.Equal(0.5d, snapshot.AdaptiveScaleFactor);
        Assert.True(
            snapshot.CurrentAverageEncodeDuration > TimeSpan.FromMilliseconds(20),
            $"Expected slow encode average above 20 ms, but was {snapshot.CurrentAverageEncodeDuration.TotalMilliseconds:F1} ms.");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsScreenCaptureSource_FastEncode_RestoresAdaptiveScaleGradually()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fakeTimestamp = 0L;
        var encodedWidths = new List<int>();
        var encodeDurations = new Queue<long>(
        [
            Stopwatch.Frequency / 40,
            Stopwatch.Frequency / 40,
            Stopwatch.Frequency / 40,
            Stopwatch.Frequency / 200,
            Stopwatch.Frequency / 200,
            Stopwatch.Frequency / 200,
            Stopwatch.Frequency / 200,
            Stopwatch.Frequency / 200,
            Stopwatch.Frequency / 200,
        ]);

        await using var source = new WindowsScreenCaptureSource(
            getTimestamp: () => fakeTimestamp,
            encodeBitmap: (bitmap, _) =>
            {
                encodedWidths.Add(bitmap.Width);
                fakeTimestamp += encodeDurations.Dequeue();
                return new byte[] { 1, 2, 3 };
            });

        using var bitmap = CreatePatternBitmap(width: 1000, height: 500);

        for (var i = 0; i < 9; i++)
        {
            _ = source.EncodeFrameForTesting(bitmap);
        }

        Assert.Equal(1000, encodedWidths[0]);
        Assert.Contains(750, encodedWidths);
        Assert.Contains(500, encodedWidths);
        Assert.Equal(1000, encodedWidths[^1]);

        var snapshot = source.GetEncodeMetricsSnapshot();
        Assert.Equal(9, snapshot.EncodedFrames);
        Assert.Equal(1d, snapshot.AdaptiveScaleFactor);
        Assert.True(
            snapshot.CurrentAverageEncodeDuration < TimeSpan.FromMilliseconds(12),
            $"Expected recovered encode average below 12 ms, but was {snapshot.CurrentAverageEncodeDuration.TotalMilliseconds:F1} ms.");
    }

    private static Bitmap CreatePatternBitmap(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var r = (x * 17 + y * 3) % 256;
                var g = (x * 7 + y * 11) % 256;
                var b = (x * 13 + y * 5) % 256;
                bitmap.SetPixel(x, y, Color.FromArgb(r, g, b));
            }
        }

        return bitmap;
    }
}
