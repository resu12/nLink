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
    private readonly IScreenCaptureSourceFactory captureSourceFactory;
    private readonly Action<bool> setPreviewActive;
    private readonly Action<ScreenShareStatus> setStatus;
    private readonly Func<Bitmap?> getPreviewFrame;
    private readonly Action<Bitmap?> setPreviewFrame;
    private readonly Func<byte[], Bitmap> decodeFrame;
    private readonly object gate = new();

    private IScreenCaptureSource? screenSharePreviewCaptureSource;
    private CancellationTokenSource? screenSharePreviewCts;
    private int screenSharePreviewDecodeInFlight;
    private int maxScreenSharePreviewDecodeTasksActive;
    private int screenSharePreviewGeneration;
    private int screenSharePreviewToggleInFlight;
    private Task? screenSharePreviewDecodeTask;
    private Task? screenSharePreviewToggleTask;
    private byte[]? pendingPreviewFrameBytes;
    private long framesDecoded;

    public HelpeeScreenShareCoordinator(
        Func<bool> isDisposed,
        Func<bool> canShowScreenShareAction,
        Func<bool> isPreviewActive,
        IScreenCaptureSourceFactory captureSourceFactory,
        Action<bool> setPreviewActive,
        Action<ScreenShareStatus> setStatus,
        Func<Bitmap?> getPreviewFrame,
        Action<Bitmap?> setPreviewFrame,
        Func<byte[], Bitmap>? decodeFrame = null)
    {
        this.isDisposed = isDisposed ?? throw new ArgumentNullException(nameof(isDisposed));
        this.canShowScreenShareAction = canShowScreenShareAction ?? throw new ArgumentNullException(nameof(canShowScreenShareAction));
        this.isPreviewActive = isPreviewActive ?? throw new ArgumentNullException(nameof(isPreviewActive));
        this.captureSourceFactory = captureSourceFactory ?? throw new ArgumentNullException(nameof(captureSourceFactory));
        this.setPreviewActive = setPreviewActive ?? throw new ArgumentNullException(nameof(setPreviewActive));
        this.setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
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

        var toggleTask = UiThreadDispatch.RunAsync(ToggleAsync);
        screenSharePreviewToggleTask = toggleTask;
    }

    public async Task StopAsync()
    {
        await StopAsyncCore(awaitToggleCompletion: true).ConfigureAwait(false);
    }

    internal long FramesDecoded => Interlocked.Read(ref framesDecoded);

    internal int DecodeTasksActive => Volatile.Read(ref screenSharePreviewDecodeInFlight);

    internal int MaxDecodeTasksActive => Volatile.Read(ref maxScreenSharePreviewDecodeTasksActive);

    private async Task StopAsyncCore(bool awaitToggleCompletion)
    {
        var captureSource = screenSharePreviewCaptureSource;
        var cts = screenSharePreviewCts;
        var toggleTask = awaitToggleCompletion ? screenSharePreviewToggleTask : null;

        if (captureSource is null && cts is null && !isPreviewActive() && getPreviewFrame() is null)
        {
            if (toggleTask is not null)
            {
                try
                {
                    await toggleTask.ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogDebug($"Preview toggle completion failed during stop: {ex.GetType().Name}: {ex.Message}");
                }

                await StopAsyncCore(awaitToggleCompletion: false).ConfigureAwait(false);
            }
            return;
        }

        Interlocked.Increment(ref screenSharePreviewGeneration);
        screenSharePreviewCaptureSource = null;
        screenSharePreviewCts = null;
        ReplacePendingFrame(null);

        if (captureSource is not null)
        {
            captureSource.FrameArrived -= OnScreenSharePreviewFrameArrived;
        }

        cts?.Cancel();

        await ClearScreenSharePreviewFrameAsync().ConfigureAwait(false);
        await SetPreviewActiveAsync(false).ConfigureAwait(false);
        await SetStatusAsync(ScreenShareState.Off).ConfigureAwait(false);

        try
        {
            if (captureSource is not null)
            {
                await captureSource.StopAsync();
            }
        }
        catch (Exception ex)
        {
            LogDebug($"Preview capture stop failed during shutdown: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            cts?.Dispose();
            if (captureSource is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync();
            }
        }

        var decodeTask = screenSharePreviewDecodeTask;
        if (decodeTask is not null)
        {
            try
            {
                await decodeTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogDebug($"Preview decode task completion failed during stop: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (toggleTask is not null)
        {
            try
            {
                await toggleTask.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogDebug($"Preview toggle task completion failed during stop: {ex.GetType().Name}: {ex.Message}");
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
        catch (Exception ex)
        {
            LogDebug($"Preview capture start failed: {ex.GetType().Name}: {ex.Message}");
            await StopAsyncCore(awaitToggleCompletion: false);
            await SetStatusAsync(ScreenShareState.Failed, "Screen sharing failed to start").ConfigureAwait(false);
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
        SetStatus(ScreenShareState.Starting);

        var captureSource = captureSourceFactory.Create();
        if (!captureSource.IsSupported)
        {
            if (captureSource is IAsyncDisposable unsupportedAsyncDisposable)
            {
                await unsupportedAsyncDisposable.DisposeAsync();
            }

            SetStatus(ScreenShareState.Off);
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

            if (isDisposed() ||
                cts.IsCancellationRequested ||
                !ReferenceEquals(screenSharePreviewCaptureSource, captureSource) ||
                !ReferenceEquals(screenSharePreviewCts, cts))
            {
                LogDebug("Preview capture start completed after stop/reset; suppressing active state transition.");
                return;
            }

            await SetPreviewActiveAsync(true).ConfigureAwait(false);
            await SetStatusAsync(ScreenShareState.Active).ConfigureAwait(false);
            LogDebug("Preview capture started.");
        }
        catch (Exception ex)
        {
            LogDebug($"Preview capture start failed during startup: {ex.GetType().Name}: {ex.Message}");
            captureSource.FrameArrived -= OnScreenSharePreviewFrameArrived;
            screenSharePreviewCaptureSource = null;
            screenSharePreviewCts = null;
            cts.Dispose();
            if (captureSource is IAsyncDisposable failedAsyncDisposable)
            {
                await failedAsyncDisposable.DisposeAsync();
            }

            Interlocked.Increment(ref screenSharePreviewGeneration);
            throw;
        }
    }

    private void SetStatus(ScreenShareState state, string? userMessage = null)
    {
        setStatus(new ScreenShareStatus(state, userMessage, DateTimeOffset.UtcNow));
    }

    private Task SetPreviewActiveAsync(bool value)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            setPreviewActive(value);
            return Task.CompletedTask;
        }

        return UiThreadDispatch.RunAsync(() => setPreviewActive(value));
    }

    private Task SetStatusAsync(ScreenShareState state, string? userMessage = null)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            SetStatus(state, userMessage);
            return Task.CompletedTask;
        }

        return UiThreadDispatch.RunAsync(() => SetStatus(state, userMessage));
    }

    private void OnScreenSharePreviewFrameArrived(object? sender, ScreenCaptureFrameEventArgs e)
    {
        LogDebug($"Preview frame arrived encoding={e.Encoding} bytes={e.EncodedFrameData.Length} size={e.Width}x{e.Height}.");
        ReplacePendingFrame(e.EncodedFrameData);

        // Only one preview decode loop may run at a time; new frames coalesce into a latest-wins slot.
        if (Interlocked.Exchange(ref screenSharePreviewDecodeInFlight, 1) == 1)
        {
            return;
        }

        RecordDecodeTaskActivated();
        StartDecodeLoopCore();
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

    private void StartDecodeLoopCore()
    {
        Task? decodeTask = null;
        decodeTask = Task.Run(async () =>
        {
            try
            {
                await ProcessPendingDecodeLoopAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref screenSharePreviewDecodeInFlight, 0);
                if (ReferenceEquals(screenSharePreviewDecodeTask, decodeTask))
                {
                    screenSharePreviewDecodeTask = null;
                }

                var restart = false;
                lock (gate)
                {
                    if (!isDisposed() &&
                        pendingPreviewFrameBytes is not null &&
                        Interlocked.Exchange(ref screenSharePreviewDecodeInFlight, 1) == 0)
                    {
                        RecordDecodeTaskActivated();
                        restart = true;
                    }
                }

                if (restart)
                {
                    StartDecodeLoopCore();
                }
            }
        });

        screenSharePreviewDecodeTask = decodeTask;
    }

    private async Task ProcessPendingDecodeLoopAsync()
    {
        while (true)
        {
            var generation = Volatile.Read(ref screenSharePreviewGeneration);
            var encodedFrameData = TakePendingFrame();
            if (encodedFrameData is null || isDisposed())
            {
                return;
            }

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

                await SetScreenSharePreviewFrameAsync(bitmap, generation).ConfigureAwait(false);
                LogDebug("Preview frame applied.");
                Interlocked.Increment(ref framesDecoded);
                bitmap = null;
            }
            catch (Exception ex)
            {
                LogDebug($"Preview frame decode/apply failed: {ex.GetType().Name}: {ex.Message}");
                bitmap?.Dispose();
            }
        }
    }

    private void ReplacePendingFrame(byte[]? encodedFrameData)
    {
        lock (gate)
        {
            pendingPreviewFrameBytes = encodedFrameData;
        }
    }

    private byte[]? TakePendingFrame()
    {
        lock (gate)
        {
            var encodedFrameData = pendingPreviewFrameBytes;
            pendingPreviewFrameBytes = null;
            return encodedFrameData;
        }
    }

    private void RecordDecodeTaskActivated()
    {
        while (true)
        {
            var currentMax = Volatile.Read(ref maxScreenSharePreviewDecodeTasksActive);
            if (currentMax >= 1)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref maxScreenSharePreviewDecodeTasksActive, 1, currentMax) == currentMax)
            {
                return;
            }
        }
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
