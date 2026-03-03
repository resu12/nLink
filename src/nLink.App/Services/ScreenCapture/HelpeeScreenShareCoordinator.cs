using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NLink.App.Threading;

namespace NLink.App.Services.ScreenCapture;

internal sealed class HelpeeScreenShareCoordinator
{
    private readonly Func<bool> isDisposed;
    private readonly Func<bool> canShowScreenShareAction;
    private readonly Func<bool> isPreviewActive;
    private readonly Func<IScreenCaptureSource> captureSourceFactory;
    private readonly Action<bool> setPreviewActive;
    private readonly Func<Bitmap?> getPreviewFrame;
    private readonly Action<Bitmap?> setPreviewFrame;
    private readonly Func<byte[], Bitmap> decodeFrame;

    private IScreenCaptureSource? screenSharePreviewCaptureSource;
    private CancellationTokenSource? screenSharePreviewCts;
    private int screenSharePreviewDecodeInFlight;
    private int screenSharePreviewGeneration;
    private int screenSharePreviewToggleInFlight;
    private Task? screenSharePreviewDecodeTask;
    private Task? screenSharePreviewToggleTask;

    public HelpeeScreenShareCoordinator(
        Func<bool> isDisposed,
        Func<bool> canShowScreenShareAction,
        Func<bool> isPreviewActive,
        Func<IScreenCaptureSource> captureSourceFactory,
        Action<bool> setPreviewActive,
        Func<Bitmap?> getPreviewFrame,
        Action<Bitmap?> setPreviewFrame,
        Func<byte[], Bitmap>? decodeFrame = null)
    {
        this.isDisposed = isDisposed ?? throw new ArgumentNullException(nameof(isDisposed));
        this.canShowScreenShareAction = canShowScreenShareAction ?? throw new ArgumentNullException(nameof(canShowScreenShareAction));
        this.isPreviewActive = isPreviewActive ?? throw new ArgumentNullException(nameof(isPreviewActive));
        this.captureSourceFactory = captureSourceFactory ?? throw new ArgumentNullException(nameof(captureSourceFactory));
        this.setPreviewActive = setPreviewActive ?? throw new ArgumentNullException(nameof(setPreviewActive));
        this.getPreviewFrame = getPreviewFrame ?? throw new ArgumentNullException(nameof(getPreviewFrame));
        this.setPreviewFrame = setPreviewFrame ?? throw new ArgumentNullException(nameof(setPreviewFrame));
        this.decodeFrame = decodeFrame ?? DecodeFrame;
    }

    public void Toggle()
    {
        if (Interlocked.Exchange(ref screenSharePreviewToggleInFlight, 1) == 1)
        {
            return;
        }

        var toggleTask = Task.Run(ToggleAsync);
        screenSharePreviewToggleTask = toggleTask;
    }

    public async Task StopAsync()
    {
        await StopAsyncCore(awaitToggleCompletion: true).ConfigureAwait(false);
    }

    private async Task StopAsyncCore(bool awaitToggleCompletion)
    {
        var captureSource = screenSharePreviewCaptureSource;
        var cts = screenSharePreviewCts;
        var toggleTask = awaitToggleCompletion ? screenSharePreviewToggleTask : null;

        if (captureSource is null && cts is null && !isPreviewActive() && getPreviewFrame() is null)
        {
            if (toggleTask is not null &&
                !(Application.Current is not null && Dispatcher.UIThread.CheckAccess()))
            {
                try
                {
                    await toggleTask.ConfigureAwait(false);
                }
                catch
                {
                }
            }
            return;
        }

        Interlocked.Increment(ref screenSharePreviewGeneration);
        screenSharePreviewCaptureSource = null;
        screenSharePreviewCts = null;

        if (captureSource is not null)
        {
            captureSource.FrameArrived -= OnScreenSharePreviewFrameArrived;
        }

        cts?.Cancel();

        try
        {
            if (captureSource is not null)
            {
                await captureSource.StopAsync();
            }
        }
        catch
        {
        }
        finally
        {
            cts?.Dispose();
            if (captureSource is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
        }

        await ClearScreenSharePreviewFrameAsync();
        setPreviewActive(false);

        var decodeTask = screenSharePreviewDecodeTask;
        if (decodeTask is not null &&
            !(Application.Current is not null && Dispatcher.UIThread.CheckAccess()))
        {
            try
            {
                await decodeTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }

        if (toggleTask is not null &&
            !(Application.Current is not null && Dispatcher.UIThread.CheckAccess()))
        {
            try
            {
                await toggleTask.ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private async Task ToggleAsync()
    {
        try
        {
            if (isPreviewActive())
            {
                await StopAsyncCore(awaitToggleCompletion: false);
                return;
            }

            await StartAsync();
        }
        catch
        {
            await StopAsyncCore(awaitToggleCompletion: false);
        }
        finally
        {
            screenSharePreviewToggleTask = null;
            Interlocked.Exchange(ref screenSharePreviewToggleInFlight, 0);
        }
    }

    private async Task StartAsync()
    {
        if (isDisposed() || isPreviewActive() || !canShowScreenShareAction())
        {
            return;
        }

        await StopAsyncCore(awaitToggleCompletion: false);

        var captureSource = captureSourceFactory();
        if (!captureSource.IsSupported)
        {
            if (captureSource is IAsyncDisposable unsupportedAsyncDisposable)
            {
                await unsupportedAsyncDisposable.DisposeAsync();
            }

            return;
        }

        var cts = new CancellationTokenSource();
        Interlocked.Increment(ref screenSharePreviewGeneration);

        screenSharePreviewCaptureSource = captureSource;
        screenSharePreviewCts = cts;
        captureSource.FrameArrived += OnScreenSharePreviewFrameArrived;
        LogDebug("Preview capture subscribed to FrameArrived.");

        try
        {
            await captureSource.StartAsync(cts.Token);
            setPreviewActive(true);
            LogDebug("Preview capture started.");
        }
        catch
        {
            captureSource.FrameArrived -= OnScreenSharePreviewFrameArrived;
            screenSharePreviewCaptureSource = null;
            screenSharePreviewCts = null;
            cts.Dispose();
            Interlocked.Increment(ref screenSharePreviewGeneration);
            throw;
        }
    }

    private void OnScreenSharePreviewFrameArrived(object? sender, ScreenCaptureFrameEventArgs e)
    {
        LogDebug($"Preview frame arrived encoding={e.Encoding} bytes={e.EncodedFrameData.Length} size={e.Width}x{e.Height}.");
        // Only one preview decode may run at a time; dropped frames are safe because the
        // generation check disposes stale bitmaps before they can be applied.
        if (Interlocked.Exchange(ref screenSharePreviewDecodeInFlight, 1) == 1)
        {
            LogDebug("Preview frame dropped because decode is already in flight.");
            return;
        }

        var generation = Volatile.Read(ref screenSharePreviewGeneration);
        var encodedFrameData = e.EncodedFrameData;

        Task? decodeTask = null;
        decodeTask = Task.Run(async () =>
        {
            Bitmap? bitmap = null;

            try
            {
                bitmap = decodeFrame(encodedFrameData);
                LogDebug("Preview frame decoded to Avalonia bitmap.");

                if (isDisposed() ||
                    screenSharePreviewCts?.IsCancellationRequested == true ||
                    generation != Volatile.Read(ref screenSharePreviewGeneration))
                {
                    bitmap.Dispose();
                    bitmap = null;
                    return;
                }

                await SetScreenSharePreviewFrameAsync(bitmap, generation);
                LogDebug("Preview frame applied.");
                bitmap = null;
            }
            catch (Exception ex)
            {
                LogDebug($"Preview frame decode/apply failed: {ex.GetType().Name}: {ex.Message}");
                bitmap?.Dispose();
            }
            finally
            {
                Interlocked.Exchange(ref screenSharePreviewDecodeInFlight, 0);
                if (ReferenceEquals(screenSharePreviewDecodeTask, decodeTask))
                {
                    screenSharePreviewDecodeTask = null;
                }
            }
        });
        screenSharePreviewDecodeTask = decodeTask;
    }

    private Task SetScreenSharePreviewFrameAsync(Bitmap bitmap, int generation)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            SetScreenSharePreviewFrameCore(bitmap, generation);
            return Task.CompletedTask;
        }

        return UiThreadDispatch.RunAsync(() => SetScreenSharePreviewFrameCore(bitmap, generation));
    }

    private Task ClearScreenSharePreviewFrameAsync()
    {
        var generation = Volatile.Read(ref screenSharePreviewGeneration);
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            SetScreenSharePreviewFrameCore(null, generation);
            return Task.CompletedTask;
        }

        return UiThreadDispatch.RunAsync(() => SetScreenSharePreviewFrameCore(null, generation));
    }

    private void SetScreenSharePreviewFrameCore(Bitmap? nextFrame, int generation)
    {
        if (generation != Volatile.Read(ref screenSharePreviewGeneration))
        {
            nextFrame?.Dispose();
            return;
        }

        var previousFrame = getPreviewFrame();
        setPreviewFrame(nextFrame);
        previousFrame?.Dispose();
    }

    [Conditional("DEBUG")]
    private static void LogDebug(string message)
    {
        Trace.WriteLine($"[ScreenSharePreview] {message}");
    }

    private static Bitmap DecodeFrame(byte[] encodedFrameData)
    {
        using var stream = new MemoryStream(encodedFrameData, writable: false);
        return new Bitmap(stream);
    }
}
