using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Configuration;

namespace NLink.App.Services.ScreenCapture;

/// <summary>
/// Windows screen capture source for the primary display.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsScreenCaptureSource : IScreenCaptureSource, IScreenCaptureMetadataSource, IScreenCaptureAdaptiveTuning, IAsyncDisposable
{
    private const int MaxFrameWidth = 1280;
    private const int DefaultMaxFramesPerSecond = 15;
    private const int MaxConfiguredFramesPerSecond = 30;
    private const long DefaultJpegQuality = 60L;
    private const long MinJpegQuality = 30L;
    private const long MaxJpegQuality = 80L;
    private const double DefaultConfiguredScale = 0.75d;
    private const double MinConfiguredScale = 0.25d;
    private const double MaxConfiguredScale = 1d;
    private const double ScaleFull = 1d;
    private const double ScaleReduced = 0.75d;
    private const double ScaleMinimum = 0.5d;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const int CursorShowing = 0x00000001;
    private const int CursorMarkerRadius = 5;
    private const int CursorMarkerCrossHalfSize = 7;

    private static readonly long EncodeScaleDownThresholdTimestampTicks = TimeSpanToStopwatchTicks(TimeSpan.FromMilliseconds(20));
    private static readonly long EncodeScaleUpThresholdTimestampTicks = TimeSpanToStopwatchTicks(TimeSpan.FromMilliseconds(12));
    private static readonly ImageCodecInfo? JpegCodec = FindJpegCodec();
    private static readonly Brush CursorMarkerFillBrush = Brushes.Gold;
    private static readonly Pen CursorMarkerOutlinePen = Pens.Black;
    private static readonly Pen CursorMarkerCrossPen = Pens.DarkSlateGray;

    private readonly object sync = new();
    private readonly Func<long> getTimestamp;
    private readonly Func<Bitmap, long, byte[]> encodeBitmap;
    private readonly double configuredScale;
    private readonly double? configuredDpiScale;
    private readonly long jpegQuality;
    private readonly int configuredMaxFramesPerSecond;

    private CancellationTokenSource? captureCts;
    private Task? captureLoopTask;
    private bool isStarted;
    private bool disposed;
    private int adaptiveScaleIndex;
    private long averageEncodeDurationTicks;
    private long totalEncodeDurationTicks;
    private long totalEncodedBytes;
    private int encodedFrameCount;
    private long lastCaptureTimestampTick;
    private long effectiveMinFrameIntervalTimestampTicks;

    public WindowsScreenCaptureSource()
        : this(
            getTimestamp: null,
            encodeBitmap: null,
            maxFramesPerSecond: Math.Min(FeatureFlags.ScreenShareMaxFps, FeatureFlags.ScreenShareTransportMaxFps),
            configuredScale: FeatureFlags.ScreenShareScale,
            jpegQuality: FeatureFlags.ScreenShareJpegQuality)
    {
    }

    internal WindowsScreenCaptureSource(
        Func<long>? getTimestamp = null,
        Func<Bitmap, long, byte[]>? encodeBitmap = null,
        int maxFramesPerSecond = DefaultMaxFramesPerSecond,
        double configuredScale = ScaleFull,
        long jpegQuality = DefaultJpegQuality)
    {
        this.getTimestamp = getTimestamp ?? Stopwatch.GetTimestamp;
        this.encodeBitmap = encodeBitmap ?? EncodeBitmapToJpegBytes;
        configuredMaxFramesPerSecond = Math.Clamp(maxFramesPerSecond, 1, MaxConfiguredFramesPerSecond);
        this.configuredScale = ClampConfiguredScale(configuredScale);
        configuredDpiScale = TryGetSystemDpiScale();
        this.jpegQuality = Math.Clamp(jpegQuality, MinJpegQuality, MaxJpegQuality);
        effectiveMinFrameIntervalTimestampTicks = TimeSpanToStopwatchTicks(TimeSpan.FromMilliseconds(1000d / configuredMaxFramesPerSecond));
    }

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public event EventHandler<ScreenCaptureFrameEventArgs>? FrameArrived;

    public bool TryGetCaptureMetadata(out ScreenCaptureMetadata metadata)
    {
        metadata = default;
        var width = GetSystemMetrics(SmCxScreen);
        var height = GetSystemMetrics(SmCyScreen);
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        metadata = new ScreenCaptureMetadata(
            // TODO(v0.5.0-P7): plumb selected monitor/region identity once monitor-selection
            // UI exists; this must match the active capture target used for streaming.
            DisplayId: ScreenCaptureDisplayIds.Primary,
            CaptureRegionPx: new ScreenCapturePixelRect(0, 0, width, height),
            // TODO(v0.5.0-P7): resolve per-monitor DPI for the selected target in mixed-DPI setups.
            DpiScale: configuredDpiScale);
        return true;
    }

    public void SetCaptureFrameRateHint(int maxFramesPerSecond)
    {
        var clamped = Math.Clamp(maxFramesPerSecond, 1, configuredMaxFramesPerSecond);
        Volatile.Write(
            ref effectiveMinFrameIntervalTimestampTicks,
            TimeSpanToStopwatchTicks(TimeSpan.FromMilliseconds(1000d / clamped)));
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();

        lock (sync)
        {
            if (isStarted)
            {
                LogDebug("StartAsync ignored because capture is already running.");
                return Task.CompletedTask;
            }

            captureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lastCaptureTimestampTick = getTimestamp();
            captureLoopTask = Task.Run(() => CaptureLoopAsync(captureCts.Token), CancellationToken.None);
            isStarted = true;
            LogDebug("Capture loop started.");
        }

        // A transient failure on the first capture should not fail startup if the loop can recover.
        CaptureAndRaiseFrame(swallowFailures: true);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        Task? loopTask;

        lock (sync)
        {
            if (!isStarted)
            {
                LogDebug("StopAsync ignored because capture is already stopped.");
                return;
            }

            isStarted = false;
            captureCts?.Cancel();
            loopTask = captureLoopTask;
            captureLoopTask = null;
            LogDebug("Stopping capture loop.");
        }

        if (loopTask is not null)
        {
            try
            {
                await loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        lock (sync)
        {
            captureCts?.Dispose();
            captureCts = null;
            LogDebug("Capture loop stopped and resources released.");
        }
    }

    /// <summary>
    /// Disposes the capture source and waits for the background capture loop to stop.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task CaptureLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var nowTimestampTick = getTimestamp();
            var previousCaptureTimestampTick = Volatile.Read(ref lastCaptureTimestampTick);
            var minIntervalTicks = Volatile.Read(ref effectiveMinFrameIntervalTimestampTicks);
            var remainingTicks = minIntervalTicks - (nowTimestampTick - previousCaptureTimestampTick);
            if (previousCaptureTimestampTick > 0 && remainingTicks > 0)
            {
                await Task.Delay(StopwatchTicksToTimeSpan(remainingTicks), cancellationToken).ConfigureAwait(false);
                continue;
            }

            Volatile.Write(ref lastCaptureTimestampTick, nowTimestampTick);
            CaptureAndRaiseFrame(swallowFailures: true);
        }

        LogDebug("Capture loop exited.");
    }

    private void CaptureAndRaiseFrame(bool swallowFailures)
    {
        try
        {
            var frame = CaptureFrame();
            LogDebug($"Captured frame {frame.Width}x{frame.Height}, bytes={frame.EncodedFrameData.Length}.");
            FrameArrived?.Invoke(this, frame);
            LogDebug("FrameArrived invoked.");
        }
        catch (OperationCanceledException)
        {
            LogDebug("Capture loop canceled.");
            if (!swallowFailures)
            {
                throw;
            }
        }
        catch (Exception ex)
        {
            LogDebug($"Capture loop frame failed: {ex.GetType().Name}: {ex.Message}");
            if (!swallowFailures)
            {
                throw;
            }
        }
    }

    private ScreenCaptureFrameEventArgs CaptureFrame()
    {
        var width = GetSystemMetrics(SmCxScreen);
        var height = GetSystemMetrics(SmCyScreen);
        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException("Primary display dimensions could not be resolved.");
        }

        using var sourceBitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(sourceBitmap))
        {
            graphics.CopyFromScreen(0, 0, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);
            TryDrawCursorOverlay(graphics);
        }

        return EncodeFrame(sourceBitmap, width, height);
    }

    private static void TryDrawCursorOverlay(Graphics graphics)
    {
        if (graphics is null)
        {
            return;
        }

        var cursorInfo = new CursorInfo
        {
            CbSize = Marshal.SizeOf<CursorInfo>(),
        };

        if (!GetCursorInfo(ref cursorInfo) ||
            (cursorInfo.Flags & CursorShowing) == 0)
        {
            return;
        }

        // Lightweight marker avoids expensive icon duplication/composition on every frame.
        var x = cursorInfo.ScreenPosition.X;
        var y = cursorInfo.ScreenPosition.Y;
        graphics.FillEllipse(CursorMarkerFillBrush, x - CursorMarkerRadius, y - CursorMarkerRadius, CursorMarkerRadius * 2, CursorMarkerRadius * 2);
        graphics.DrawEllipse(CursorMarkerOutlinePen, x - CursorMarkerRadius, y - CursorMarkerRadius, CursorMarkerRadius * 2, CursorMarkerRadius * 2);
        graphics.DrawLine(CursorMarkerCrossPen, x - CursorMarkerCrossHalfSize, y, x + CursorMarkerCrossHalfSize, y);
        graphics.DrawLine(CursorMarkerCrossPen, x, y - CursorMarkerCrossHalfSize, x, y + CursorMarkerCrossHalfSize);
    }

    internal ScreenCaptureFrameEventArgs EncodeFrameForTesting(Bitmap sourceBitmap)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(sourceBitmap);
        return EncodeFrame(sourceBitmap, sourceBitmap.Width, sourceBitmap.Height);
    }

    internal WindowsScreenCaptureEncodeMetricsSnapshot GetEncodeMetricsSnapshot()
    {
        lock (sync)
        {
            var averageEncodeDuration = encodedFrameCount == 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds((double)totalEncodeDurationTicks / Stopwatch.Frequency / encodedFrameCount);
            var ewmaEncodeDuration = averageEncodeDurationTicks == 0
                ? TimeSpan.Zero
                : TimeSpan.FromSeconds((double)averageEncodeDurationTicks / Stopwatch.Frequency);
            var averageEncodedBytes = encodedFrameCount == 0
                ? 0
                : totalEncodedBytes / encodedFrameCount;

            return new WindowsScreenCaptureEncodeMetricsSnapshot(
                encodedFrameCount,
                averageEncodedBytes,
                averageEncodeDuration,
                ewmaEncodeDuration,
                GetAdaptiveScaleFactor(adaptiveScaleIndex),
                jpegQuality);
        }
    }

    internal static byte[] EncodeBitmapToJpegBytesForTesting(Bitmap bitmap, long quality)
    {
        ArgumentNullException.ThrowIfNull(bitmap);
        return EncodeBitmapToJpegBytes(bitmap, quality);
    }

    internal static long DefaultJpegQualityForTesting => DefaultJpegQuality;

    private ScreenCaptureFrameEventArgs EncodeFrame(Bitmap sourceBitmap, int width, int height)
    {
        var scale = Math.Clamp(
            GetBaseScale(width) * configuredScale * GetAdaptiveScaleFactorSnapshot(),
            MinConfiguredScale,
            MaxConfiguredScale);
        var scaledWidth = Math.Max(1, (int)Math.Round(width * scale));
        var scaledHeight = Math.Max(1, (int)Math.Round(height * scale));

        using var encodedBitmap = scale < 1d
            ? ResizeBitmap(sourceBitmap, scaledWidth, scaledHeight)
            : new Bitmap(sourceBitmap);
        var encodeStartedAt = getTimestamp();
        var encodedBytes = encodeBitmap(encodedBitmap, jpegQuality);
        var encodeCompletedAt = getTimestamp();
        RecordEncodeMetrics(
            elapsedTimestampTicks: encodeCompletedAt - encodeStartedAt,
            encodedBytesLength: encodedBytes.Length);
        return new ScreenCaptureFrameEventArgs(
            scaledWidth,
            scaledHeight,
            encodedBytes,
            "jpeg",
            capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private static Bitmap ResizeBitmap(Bitmap source, int width, int height)
    {
        var target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(target);
        graphics.CompositingMode = CompositingMode.SourceCopy;
        graphics.CompositingQuality = CompositingQuality.HighSpeed;
        graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
        graphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
        graphics.SmoothingMode = SmoothingMode.None;
        graphics.DrawImage(source, 0, 0, width, height);
        return target;
    }

    private static void SaveJpeg(Bitmap bitmap, Stream output, long quality)
    {
        if (JpegCodec is null)
        {
            bitmap.Save(output, ImageFormat.Jpeg);
            return;
        }

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        bitmap.Save(output, JpegCodec, parameters);
    }

    private static byte[] EncodeBitmapToJpegBytes(Bitmap bitmap, long quality)
    {
        using var stream = new MemoryStream();
        SaveJpeg(bitmap, stream, quality);
        return stream.ToArray();
    }

    private static double? TryGetSystemDpiScale()
    {
        try
        {
            using var graphics = Graphics.FromHwnd(IntPtr.Zero);
            var scale = graphics.DpiX / 96d;
            if (scale > 0d && !double.IsNaN(scale) && !double.IsInfinity(scale))
            {
                return scale;
            }
        }
        catch
        {
            // Best-effort only.
        }

        return null;
    }

    private static double GetBaseScale(int width)
        => width > MaxFrameWidth
            ? (double)MaxFrameWidth / width
            : 1d;

    private double GetAdaptiveScaleFactorSnapshot()
    {
        lock (sync)
        {
            return GetAdaptiveScaleFactor(adaptiveScaleIndex);
        }
    }

    private static double GetAdaptiveScaleFactor(int scaleIndex)
        => scaleIndex switch
        {
            0 => ScaleFull,
            1 => ScaleReduced,
            _ => ScaleMinimum,
        };

    private void RecordEncodeMetrics(long elapsedTimestampTicks, int encodedBytesLength)
    {
        lock (sync)
        {
            encodedFrameCount++;
            totalEncodedBytes += encodedBytesLength;
            totalEncodeDurationTicks += elapsedTimestampTicks;

            averageEncodeDurationTicks = averageEncodeDurationTicks == 0
                ? elapsedTimestampTicks
                : ((averageEncodeDurationTicks * 3) + elapsedTimestampTicks) / 4;

            if (averageEncodeDurationTicks > EncodeScaleDownThresholdTimestampTicks && adaptiveScaleIndex < 2)
            {
                adaptiveScaleIndex++;
            }
            else if (averageEncodeDurationTicks < EncodeScaleUpThresholdTimestampTicks && adaptiveScaleIndex > 0)
            {
                adaptiveScaleIndex--;
            }
        }
    }

    private static long TimeSpanToStopwatchTicks(TimeSpan value)
        => (long)Math.Ceiling(value.TotalSeconds * Stopwatch.Frequency);

    private static TimeSpan StopwatchTicksToTimeSpan(long value)
        => value <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(value / (double)Stopwatch.Frequency);

    private static double ClampConfiguredScale(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return DefaultConfiguredScale;
        }

        return Math.Clamp(value, MinConfiguredScale, MaxConfiguredScale);
    }

    private static ImageCodecInfo? FindJpegCodec()
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (string.Equals(codec.MimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase))
            {
                return codec;
            }
        }

        return null;
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorInfo(ref CursorInfo pci);

    private void ThrowIfDisposed()
    {
        if (disposed)
        {
            throw new ObjectDisposedException(nameof(WindowsScreenCaptureSource));
        }
    }

    [Conditional("DEBUG")]
    private static void LogDebug(string message)
    {
        Trace.WriteLine($"[ScreenCapture] {message}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CursorInfo
    {
        public int CbSize;
        public int Flags;
        public IntPtr HCursor;
        public PointStruct ScreenPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PointStruct
    {
        public int X;
        public int Y;
    }

}

internal readonly record struct WindowsScreenCaptureEncodeMetricsSnapshot(
    int EncodedFrames,
    long AverageEncodedBytes,
    TimeSpan AverageEncodeDuration,
    TimeSpan CurrentAverageEncodeDuration,
    double AdaptiveScaleFactor,
    long JpegQuality);
