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

namespace NLink.App.Services.ScreenCapture;

/// <summary>
/// Windows screen capture source for the primary display.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsScreenCaptureSource : IScreenCaptureSource, IAsyncDisposable
{
    private const int MaxFrameWidth = 1280;
    private const int MaxFramesPerSecond = 8;
    private const long JpegQuality = 75L;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(1000d / MaxFramesPerSecond);
    private static readonly ImageCodecInfo? JpegCodec = FindJpegCodec();

    private readonly object sync = new();

    private CancellationTokenSource? captureCts;
    private Task? captureLoopTask;
    private bool isStarted;
    private bool disposed;

    /// <inheritdoc />
    public bool IsSupported => true;

    /// <inheritdoc />
    public event EventHandler<ScreenCaptureFrameEventArgs>? FrameArrived;

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
            var frameStartedAt = DateTime.UtcNow;
            CaptureAndRaiseFrame(swallowFailures: true);

            var remaining = FrameInterval - (DateTime.UtcNow - frameStartedAt);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
            }
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

    private static ScreenCaptureFrameEventArgs CaptureFrame()
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
        }

        var scale = width > MaxFrameWidth
            ? (double)MaxFrameWidth / width
            : 1d;
        var scaledWidth = Math.Max(1, (int)Math.Round(width * scale));
        var scaledHeight = Math.Max(1, (int)Math.Round(height * scale));

        using var encodedBitmap = scale < 1d
            ? ResizeBitmap(sourceBitmap, scaledWidth, scaledHeight)
            : new Bitmap(sourceBitmap);

        using var stream = new MemoryStream();
        SaveJpeg(encodedBitmap, stream);
        return new ScreenCaptureFrameEventArgs(scaledWidth, scaledHeight, stream.ToArray(), "jpeg");
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

    private static void SaveJpeg(Bitmap bitmap, Stream output)
    {
        if (JpegCodec is null)
        {
            bitmap.Save(output, ImageFormat.Jpeg);
            return;
        }

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
        bitmap.Save(output, JpegCodec, parameters);
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
}
