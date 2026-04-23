using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NLink.App.Threading;
using NLink.Core.Logging;
#if DEBUG
using NLink.Core.Diagnostics;
#endif

namespace NLink.App.Services.ScreenCapture;

// Owns only the local preview lifecycle. Remote-view rendering lives in
// ScreenShareViewerViewModel; both share the same LatestEncodedFrameDecodeWorker.
internal sealed class HelpeeScreenShareCoordinator
{
    private const string PreviewLogRole = "helpee_preview";
    private static readonly TimeSpan RenderStatsLogInterval = TimeSpan.FromSeconds(2);
    private readonly Func<bool> isDisposed;
    private readonly Func<bool> canShowScreenShareAction;
    private readonly Func<bool> isPreviewActive;
    private readonly IScreenCaptureSourceFactory captureSourceFactory;
    private readonly Action<bool> setPreviewActive;
    private readonly Action<ScreenShareStatus> setStatus;
    private readonly Func<Bitmap?> getPreviewFrame;
    private readonly Action<Bitmap?> setPreviewFrame;
    private readonly Func<byte[], Bitmap> decodeJpegFrame;
    private readonly EncodedFrameBitmapDecoder encodedFrameDecoder;
    private readonly H264DecodeStreamState h264StreamState;
    private readonly LatestEncodedFrameDecodeWorker decodeWorker;

    private IScreenCaptureSource? screenSharePreviewCaptureSource;
    private CancellationTokenSource? screenSharePreviewCts;
    private int screenSharePreviewGeneration;
    private int screenSharePreviewToggleInFlight;
    private Task? screenSharePreviewToggleTask;
#if DEBUG
    private readonly DebugLatencyWindow previewDecodeDurationLatency = new();
    private readonly DebugLatencyWindow previewEndToEndLatency = new();
#endif
    private long framesReceived;
    private long framesDecoded;
    private long lastRenderStatsLogTick;
    private long lastRenderedUtcMs;
    private long renderIntervalsObserved;
    private long totalRenderIntervalMs;
    private long captureToRenderObserved;
    private long totalCaptureToRenderMs;
    private long lastLoggedPreparedEpoch = long.MinValue;
    private long lastLoggedDroppedEpoch = long.MinValue;
    private long lastLoggedDecodeSuccessEpoch = long.MinValue;
    private long lastLoggedDecodeFailureEpoch = long.MinValue;

    public HelpeeScreenShareCoordinator(
        Func<bool> isDisposed,
        Func<bool> canShowScreenShareAction,
        Func<bool> isPreviewActive,
        IScreenCaptureSourceFactory captureSourceFactory,
        Action<bool> setPreviewActive,
        Action<ScreenShareStatus> setStatus,
        Func<Bitmap?> getPreviewFrame,
        Action<Bitmap?> setPreviewFrame,
        Func<byte[], Bitmap>? decodeFrame = null,
        IWindowsH264BitmapDecoder? h264Decoder = null)
    {
        this.isDisposed = isDisposed ?? throw new ArgumentNullException(nameof(isDisposed));
        this.canShowScreenShareAction = canShowScreenShareAction ?? throw new ArgumentNullException(nameof(canShowScreenShareAction));
        this.isPreviewActive = isPreviewActive ?? throw new ArgumentNullException(nameof(isPreviewActive));
        this.captureSourceFactory = captureSourceFactory ?? throw new ArgumentNullException(nameof(captureSourceFactory));
        this.setPreviewActive = setPreviewActive ?? throw new ArgumentNullException(nameof(setPreviewActive));
        this.setStatus = setStatus ?? throw new ArgumentNullException(nameof(setStatus));
        this.getPreviewFrame = getPreviewFrame ?? throw new ArgumentNullException(nameof(getPreviewFrame));
        this.setPreviewFrame = setPreviewFrame ?? throw new ArgumentNullException(nameof(setPreviewFrame));
        decodeJpegFrame = decodeFrame ?? DecodeFrame;
        var resolvedH264Decoder = h264Decoder ?? (OperatingSystem.IsWindows()
            ? WindowsH264BitmapDecoderFactory.TryCreate("helpee_preview")
            : null);
        encodedFrameDecoder = new EncodedFrameBitmapDecoder(DecodePreviewFrameJpeg, resolvedH264Decoder);
        h264StreamState = new H264DecodeStreamState(encodedFrameDecoder);
        decodeWorker = new LatestEncodedFrameDecodeWorker(
            decodeFrame: encodedFrameDecoder.Decode,
            onFrameDecodedAsync: OnFrameDecodedAsync,
            onDecodeFailedAsync: OnDecodeFailedAsync,
            shouldStop: () => isDisposed() || screenSharePreviewCts?.IsCancellationRequested == true,
            getGeneration: () => Volatile.Read(ref screenSharePreviewGeneration));
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

    internal long FramesDecoded => decodeWorker.FramesDecoded;

    internal int DecodeTasksActive => decodeWorker.DecodeTasksActive;

    internal int MaxDecodeTasksActive => decodeWorker.MaxDecodeTasksActive;

#if DEBUG
    internal (DebugLatencySummary EndToEnd, DebugLatencySummary DecodeDuration) GetDebugLatencySnapshotAndReset()
    {
        return (
            previewEndToEndLatency.SnapshotAndReset(),
            previewDecodeDurationLatency.SnapshotAndReset());
    }
#endif

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
        decodeWorker.ClearPending();
        h264StreamState.Reset();
        ResetLifecycleLoggingState();

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

        await decodeWorker.AwaitIdleAsync().ConfigureAwait(false);

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
        if (!TryPreparePreviewDecoder(e))
        {
            return;
        }

        Interlocked.Increment(ref framesReceived);
        LogDebug($"Preview frame arrived encoding={e.Encoding} bytes={e.EncodedFrameData.Length} size={e.Width}x{e.Height}.");
        decodeWorker.EnqueueOwned(e.Encoding, e.EncodedFrameData, e.CapturedTsUtcMs, e.IsKeyFrame, e.StreamEpoch);
    }

    private Task ApplyDecodedPreviewFrameAsync(Bitmap bitmap, int generation)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            ApplyDecodedPreviewFrameCore(bitmap, generation);
            return Task.CompletedTask;
        }

        return UiThreadDispatch.RunAsync(() => ApplyDecodedPreviewFrameCore(bitmap, generation));
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

    private void ApplyDecodedPreviewFrameCore(Bitmap nextFrame, int generation)
    {
        if (generation != Volatile.Read(ref screenSharePreviewGeneration) ||
            screenSharePreviewCts?.IsCancellationRequested == true ||
            isDisposed())
        {
            nextFrame.Dispose();
            return;
        }

        SetScreenSharePreviewFrameCore(nextFrame, generation);
        SetStatus(ScreenShareState.Active);
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

    private async Task OnFrameDecodedAsync(LatestEncodedDecodedFrame decodedFrame)
    {
        if (isDisposed() ||
            screenSharePreviewCts?.IsCancellationRequested == true ||
            decodedFrame.Generation != Volatile.Read(ref screenSharePreviewGeneration))
        {
            decodedFrame.Bitmap.Dispose();
            return;
        }

#if DEBUG
        previewDecodeDurationLatency.RecordTimeSpanTicks(decodedFrame.DecodeDurationTimeSpanTicks);
#endif
        var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        RecordRenderInterval(nowUtcMs);
        var ageMs = decodedFrame.CapturedTsUtcMs > 0
            ? Math.Max(0, nowUtcMs - decodedFrame.CapturedTsUtcMs)
            : -1;
        Interlocked.Increment(ref framesDecoded);
        RecordCaptureToRender(ageMs);
        if (TryMarkEpochLogged(ref lastLoggedDecodeSuccessEpoch, decodedFrame.Request.StreamEpoch))
        {
            LocalOperationalLog.Info(
                "ScreenShare",
                $"event=screenshare_viewer_decode_succeeded; role={PreviewLogRole}; encoding={decodedFrame.Request.Encoding}; stream_epoch={decodedFrame.Request.StreamEpoch}; is_keyframe={(decodedFrame.Request.IsKeyFrame ? 1 : 0)}; captured_ts_utc_ms={decodedFrame.CapturedTsUtcMs}; rendered_age_ms={ageMs}");
        }
        LogDebug("Preview frame decoded to Avalonia bitmap.");
        await ApplyDecodedPreviewFrameAsync(decodedFrame.Bitmap, decodedFrame.Generation).ConfigureAwait(false);
        LogDebug("Preview frame applied.");
        MaybeLogRenderStats(ageMs);
#if DEBUG
        previewEndToEndLatency.RecordTimeSpanTicks(DateTime.UtcNow.Ticks - decodedFrame.ReceivedUtcTicks);
#endif
    }

    private Task OnDecodeFailedAsync(LatestEncodedDecodeFailure failure)
    {
#if DEBUG
        previewDecodeDurationLatency.RecordTimeSpanTicks(failure.DecodeDurationTimeSpanTicks);
#endif
        if (failure.Exception is H264DecoderNeedsMoreInputException)
        {
            LogDebug($"Preview H.264 decoder needs more input for epoch={failure.Request.StreamEpoch} bytes={failure.Request.EncodedFrameBytes.Length}.");
            return Task.CompletedTask;
        }

        if (H264DecodeStreamState.IsH264Encoding(failure.Request.Encoding))
        {
            h264StreamState.Reset();
        }

        if (TryMarkEpochLogged(ref lastLoggedDecodeFailureEpoch, failure.Request.StreamEpoch))
        {
            LocalOperationalLog.Info(
                "ScreenShare",
                $"event=screenshare_viewer_decode_failed; role={PreviewLogRole}; encoding={failure.Request.Encoding}; stream_epoch={failure.Request.StreamEpoch}; is_keyframe={(failure.Request.IsKeyFrame ? 1 : 0)}; reason={failure.Exception.GetType().Name}; payload_bytes={failure.Request.EncodedFrameBytes.Length}");
        }
        LogDebug($"Preview frame decode/apply failed encoding={failure.Request.Encoding}: {failure.Exception.GetType().Name}: {failure.Exception.Message}");
        return SetStatusAsync(ScreenShareState.Failed, "Invalid frame received");
    }

    private bool TryPreparePreviewDecoder(ScreenCaptureFrameEventArgs frame)
    {
        if (!H264DecodeStreamState.IsH264Encoding(frame.Encoding))
        {
            return true;
        }

        var preparation = h264StreamState.Prepare(
            frame.Encoding,
            frame.StreamEpoch,
            frame.StreamConfig,
            onEpochChanged: () =>
            {
                Interlocked.Increment(ref screenSharePreviewGeneration);
                decodeWorker.ClearPending();
            });

        if (preparation.ConfigApplied &&
            TryMarkEpochLogged(ref lastLoggedPreparedEpoch, preparation.EffectiveStreamEpoch))
        {
            LocalOperationalLog.Info(
                "ScreenShare",
                $"event=screenshare_viewer_decoder_prepared; role={PreviewLogRole}; encoding={frame.Encoding}; stream_epoch={preparation.EffectiveStreamEpoch}; has_stream_config=1; decoder_config_bytes={frame.StreamConfig?.DecoderConfigData?.Length ?? 0}");
        }

        if (!preparation.ShouldDecode)
        {
            if (TryMarkEpochLogged(ref lastLoggedDroppedEpoch, frame.StreamEpoch))
            {
                LocalOperationalLog.Info(
                    "ScreenShare",
                    $"event=screenshare_viewer_frame_dropped_waiting_for_config; role={PreviewLogRole}; encoding={frame.Encoding}; stream_epoch={frame.StreamEpoch}; configured_epoch={preparation.ConfiguredStreamEpoch}; has_stream_config=0");
            }
            LogDebug($"Preview H.264 frame dropped until stream config is available for epoch={frame.StreamEpoch}.");
            return false;
        }

        return true;
    }

    private void MaybeLogRenderStats(long ageMs)
    {
        var nowTick = Stopwatch.GetTimestamp();
        while (true)
        {
            var lastTick = Interlocked.Read(ref lastRenderStatsLogTick);
            if (lastTick > 0 && Stopwatch.GetElapsedTime(lastTick, nowTick) < RenderStatsLogInterval)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref lastRenderStatsLogTick, nowTick, lastTick) == lastTick)
            {
                break;
            }
        }

        var ageText = ageMs >= 0 ? ageMs.ToString() : "(none)";
        var averageRenderIntervalMs = renderIntervalsObserved > 0
            ? (double)Interlocked.Read(ref totalRenderIntervalMs) / Interlocked.Read(ref renderIntervalsObserved)
            : 0d;
        var averageCaptureToRenderMs = captureToRenderObserved > 0
            ? (double)Interlocked.Read(ref totalCaptureToRenderMs) / Interlocked.Read(ref captureToRenderObserved)
            : 0d;
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=screenshare_viewer_frame_applied; role={PreviewLogRole}; age_ms={ageText}; frames_completed={Interlocked.Read(ref framesReceived)}; frames_decoded={Interlocked.Read(ref framesDecoded)}; avg_render_interval_ms={averageRenderIntervalMs:F1}; avg_capture_to_render_ms={averageCaptureToRenderMs:F1}; stream_epoch={h264StreamState.ConfiguredStreamEpoch}");
    }

    private void RecordRenderInterval(long nowUtcMs)
    {
        var previousRenderUtcMs = Interlocked.Exchange(ref lastRenderedUtcMs, nowUtcMs);
        if (previousRenderUtcMs <= 0 || nowUtcMs < previousRenderUtcMs)
        {
            return;
        }

        Interlocked.Increment(ref renderIntervalsObserved);
        Interlocked.Add(ref totalRenderIntervalMs, nowUtcMs - previousRenderUtcMs);
    }

    private void RecordCaptureToRender(long ageMs)
    {
        if (ageMs < 0)
        {
            return;
        }

        Interlocked.Increment(ref captureToRenderObserved);
        Interlocked.Add(ref totalCaptureToRenderMs, ageMs);
    }

    private void ResetLifecycleLoggingState()
    {
        Interlocked.Exchange(ref framesReceived, 0);
        Interlocked.Exchange(ref framesDecoded, 0);
        Interlocked.Exchange(ref lastRenderStatsLogTick, 0);
        Interlocked.Exchange(ref lastRenderedUtcMs, 0);
        Interlocked.Exchange(ref renderIntervalsObserved, 0);
        Interlocked.Exchange(ref totalRenderIntervalMs, 0);
        Interlocked.Exchange(ref captureToRenderObserved, 0);
        Interlocked.Exchange(ref totalCaptureToRenderMs, 0);
        Interlocked.Exchange(ref lastLoggedPreparedEpoch, long.MinValue);
        Interlocked.Exchange(ref lastLoggedDroppedEpoch, long.MinValue);
        Interlocked.Exchange(ref lastLoggedDecodeSuccessEpoch, long.MinValue);
        Interlocked.Exchange(ref lastLoggedDecodeFailureEpoch, long.MinValue);
    }

    private static bool TryMarkEpochLogged(ref long target, long streamEpoch)
    {
        var previous = Interlocked.Read(ref target);
        if (previous == streamEpoch)
        {
            return false;
        }

        Interlocked.Exchange(ref target, streamEpoch);
        return true;
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

    private Bitmap DecodePreviewFrameJpeg(ReadOnlyMemory<byte> encodedFrameData)
    {
        if (MemoryMarshal.TryGetArray(encodedFrameData, out var segment) && segment.Array is not null)
        {
            if (segment.Offset == 0 && segment.Count == segment.Array.Length)
            {
                return decodeJpegFrame(segment.Array);
            }

            return decodeJpegFrame(segment.Array.AsSpan(segment.Offset, segment.Count).ToArray());
        }

        return decodeJpegFrame(encodedFrameData.ToArray());
    }
}
