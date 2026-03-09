using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
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

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsScreenCaptureSource_DefaultStableScale_PreservesMaxWidthCapBeforeAdaptivePressure()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var source = new WindowsScreenCaptureSource(
            getTimestamp: Stopwatch.GetTimestamp,
            encodeBitmap: (_, _) => new byte[] { 1, 2, 3 },
            configuredScale: 1d);

        using var bitmap = CreatePatternBitmap(width: 1920, height: 1080);

        var frame = source.EncodeFrameForTesting(bitmap);

        Assert.Equal(1280, frame.Width);
        Assert.Equal(720, frame.Height);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsScreenCaptureSource_TransportPressure_ReducesScaleAndJpegQuality_AndRecovers()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var encodedWidths = new List<int>();
        var encodedQualities = new List<long>();

        await using var source = new WindowsScreenCaptureSource(
            getTimestamp: Stopwatch.GetTimestamp,
            encodeBitmap: (bitmap, quality) =>
            {
                encodedWidths.Add(bitmap.Width);
                encodedQualities.Add(quality);
                return new byte[] { 1, 2, 3 };
            },
            configuredScale: 1d,
            jpegQuality: 75);

        using var bitmap = CreatePatternBitmap(width: 1000, height: 500);

        _ = source.EncodeFrameForTesting(bitmap);
        source.SetTransportPressureHint(true);
        _ = source.EncodeFrameForTesting(bitmap);
        source.SetTransportPressureHint(false);
        _ = source.EncodeFrameForTesting(bitmap);

        Assert.Equal(new[] { 1000, 750, 1000 }, encodedWidths);
        Assert.Equal(new long[] { 75, 65, 75 }, encodedQualities);

        var snapshot = source.GetEncodeMetricsSnapshot();
        Assert.Equal(1d, snapshot.AdaptiveScaleFactor);
        Assert.Equal(1d, snapshot.TransportPressureScaleFactor);
        Assert.Equal(75, snapshot.JpegQuality);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsScreenCaptureSource_Downscale_UsesSharperInterpolationForUiLikePattern()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Bitmap? actualResized = null;

        await using var source = new WindowsScreenCaptureSource(
            getTimestamp: Stopwatch.GetTimestamp,
            encodeBitmap: (bitmap, _) =>
            {
                actualResized?.Dispose();
                actualResized = new Bitmap(bitmap);
                return new byte[] { 1, 2, 3 };
            },
            configuredScale: 1d);

        using var sourceBitmap = CreateUiLikeBitmap(width: 1920, height: 1080);
        using var bilinearBaseline = ResizeBitmapForTesting(
            sourceBitmap,
            width: 1280,
            height: 720,
            InterpolationMode.HighQualityBilinear);

        var frame = source.EncodeFrameForTesting(sourceBitmap);

        Assert.NotNull(actualResized);
        Assert.Equal(1280, frame.Width);
        Assert.Equal(720, frame.Height);

        var actualSharpness = MeasureEdgeEnergy(actualResized!);
        var baselineSharpness = MeasureEdgeEnergy(bilinearBaseline);

        Assert.True(
            actualSharpness >= baselineSharpness,
            $"Expected sharper or equal edge energy after resize, but actual={actualSharpness:F1} and bilinear_baseline={baselineSharpness:F1}.");

        actualResized.Dispose();
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

    private static Bitmap CreateUiLikeBitmap(int width, int height)
    {
        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.White);

        for (var row = 0; row < 18; row++)
        {
            for (var col = 0; col < 10; col++)
            {
                var x = 40 + (col * 180);
                var y = 30 + (row * 55);
                graphics.FillRectangle(Brushes.LightGray, x, y, 130, 28);
                for (var stroke = 0; stroke < 8; stroke++)
                {
                    graphics.FillRectangle(Brushes.Black, x + 8 + (stroke * 14), y + 6, 8, 16);
                }

                graphics.FillRectangle(Brushes.Black, x, y + 32, 130, 2);
            }
        }

        for (var x = 0; x < width; x += 12)
        {
            graphics.FillRectangle(Brushes.Black, x, 0, 1, height);
        }

        return bitmap;
    }

    private static Bitmap ResizeBitmapForTesting(Bitmap source, int width, int height, InterpolationMode interpolationMode)
    {
        var target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(target);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.InterpolationMode = interpolationMode;
        graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.DrawImage(source, 0, 0, width, height);
        return target;
    }

    private static double MeasureEdgeEnergy(Bitmap bitmap)
    {
        double sum = 0;
        for (var y = 0; y < bitmap.Height - 1; y += 2)
        {
            for (var x = 0; x < bitmap.Width - 1; x += 2)
            {
                var current = ComputeLuminance(bitmap.GetPixel(x, y));
                var right = ComputeLuminance(bitmap.GetPixel(x + 1, y));
                var down = ComputeLuminance(bitmap.GetPixel(x, y + 1));
                sum += Math.Abs(current - right) + Math.Abs(current - down);
            }
        }

        return sum;
    }

    private static double ComputeLuminance(Color color)
        => ((color.R * 299d) + (color.G * 587d) + (color.B * 114d)) / 1000d;
}
