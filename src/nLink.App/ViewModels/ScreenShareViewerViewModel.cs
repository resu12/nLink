using System;
using System.Buffers;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;
using System.Diagnostics;
#if DEBUG
using NLink.Core.Diagnostics;
#endif

namespace NLink.App.ViewModels;

public sealed class ScreenShareViewerViewModel : ViewModelBase, IDisposable
{
    private static readonly TimeSpan RenderStatsLogInterval = TimeSpan.FromSeconds(2);
    private const long StaleFrameThresholdMs = 750;
#if DEBUG
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(10);
#endif

    private readonly Func<ReadOnlyMemory<byte>, Bitmap> decodeFrame;
    private readonly Func<Action, Task> postFrameToUiAsync;
    private readonly Func<Action, Task> postStatusToUiAsync;
    private readonly object gate = new();

    private Bitmap? currentFrame;
    private bool isActive;
    private string statusText = string.Empty;
    private long lastRenderedFrameAgeMs = -1;
    private PendingJpegFrame? pendingJpegFrame;
    private int decodeInFlight;
    private int generation;
    private long framesReceived;
    private long framesDecoded;
    private long decodeErrors;
    private long framesCoalesced;
    private long chunksDroppedOlderFrame;
    private long assembliesExpired;
    private long lastRenderStatsLogTick;
    private long lastRenderedUtcMs;
    private long staleFrameRenders;
    private long renderIntervalsObserved;
    private long totalRenderIntervalMs;
    private long captureToRenderObserved;
    private long totalCaptureToRenderMs;
    private bool disposed;
#if DEBUG
    private readonly DebugLatencyWindow decodeDurationLatency = new();
    private readonly DebugLatencyWindow endToEndLatency = new();
    private Timer? snapshotTimer;
    private int snapshotTickInFlight;
#endif

    public ScreenShareViewerViewModel(
        Func<ReadOnlyMemory<byte>, Bitmap>? decodeFrame = null,
        Func<Action, Task>? postToUiAsync = null)
    {
        this.decodeFrame = decodeFrame ?? DecodeFrame;
        postFrameToUiAsync = postToUiAsync ?? PostFrameApplyToUiAsync;
        postStatusToUiAsync = postToUiAsync ?? PostStatusToUiAsync;
    }

    public IImage? CurrentFrame
    {
        get => currentFrame;
        private set => SetProperty(ref currentFrame, value as Bitmap);
    }

    public bool IsActive
    {
        get => isActive;
        private set => SetProperty(ref isActive, value);
    }

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public long LastRenderedFrameAgeMs
    {
        get => lastRenderedFrameAgeMs;
        private set => SetProperty(ref lastRenderedFrameAgeMs, value);
    }

    internal bool IsIdleForDiagnostics
    {
        get
        {
            lock (gate)
            {
                return Volatile.Read(ref decodeInFlight) == 0 && pendingJpegFrame is null;
            }
        }
    }

    public ScreenShareMetrics GetMetricsSnapshot()
    {
        return new ScreenShareMetrics(
            FramesDecoded: Interlocked.Read(ref framesDecoded),
            DecodeErrors: Interlocked.Read(ref decodeErrors),
            FramesCoalesced: Interlocked.Read(ref framesCoalesced),
            StaleFrameRenders: Interlocked.Read(ref staleFrameRenders),
            AverageRenderIntervalMs: ComputeAverage(
                Interlocked.Read(ref totalRenderIntervalMs),
                Interlocked.Read(ref renderIntervalsObserved)),
            AverageCaptureToRenderMs: ComputeAverage(
                Interlocked.Read(ref totalCaptureToRenderMs),
                Interlocked.Read(ref captureToRenderObserved)));
    }

    public void OnJpegFrame(
        byte[] jpegBytes,
        long capturedTsUtcMs = 0,
        long chunksDroppedOlderFrame = 0,
        long assembliesExpired = 0)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentNullException.ThrowIfNull(jpegBytes);
        if (jpegBytes.Length == 0)
        {
            throw new ArgumentException("JPEG bytes must not be empty.", nameof(jpegBytes));
        }

        PendingJpegFrame? pendingFrame = null;
        try
        {
            pendingFrame = PendingJpegFrame.CopyFrom(jpegBytes, capturedTsUtcMs);
            ReplacePendingFrame(pendingFrame);
            pendingFrame = null;
        }
        finally
        {
            pendingFrame?.Dispose();
        }

        Interlocked.Increment(ref framesReceived);
        Interlocked.Exchange(ref this.chunksDroppedOlderFrame, Math.Max(0, chunksDroppedOlderFrame));
        Interlocked.Exchange(ref this.assembliesExpired, Math.Max(0, assembliesExpired));

        IsActive = true;
        StatusText = "Live";
#if DEBUG
        StartSnapshotTimer();
#endif

        if (Interlocked.Exchange(ref decodeInFlight, 1) == 0)
        {
            _ = Task.Run(ProcessDecodeOnceAsync);
        }
    }

    public void Clear()
    {
        Interlocked.Increment(ref generation);
        lock (gate)
        {
            var pendingFrame = pendingJpegFrame;
            pendingJpegFrame = null;
            pendingFrame?.Dispose();
        }

        IsActive = false;
        StatusText = string.Empty;
        LastRenderedFrameAgeMs = -1;
#if DEBUG
        StopSnapshotTimer();
#endif
        ReplaceCurrentFrame(null);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Clear();
        GC.SuppressFinalize(this);
    }

    private async Task ProcessDecodeOnceAsync()
    {
        try
        {
            var generationSnapshot = Volatile.Read(ref generation);
            PendingJpegFrame? jpegFrame = null;
            try
            {
                jpegFrame = TakePendingFrame();

                if (jpegFrame is null || disposed)
                {
                    return;
                }

                Bitmap? bitmap = null;
                var decodeStartTimestamp = Stopwatch.GetTimestamp();
                try
                {
                    bitmap = decodeFrame(jpegFrame.Memory);
#if DEBUG
                    decodeDurationLatency.RecordTimeSpanTicks(
                        DebugLatencyWindow.StopwatchElapsedTimeSpanTicks(decodeStartTimestamp, Stopwatch.GetTimestamp()));
#endif
                    if (disposed || generationSnapshot != Volatile.Read(ref generation))
                    {
                        bitmap.Dispose();
                        bitmap = null;
                        return;
                    }

                    var nextBitmap = bitmap;
                    await postFrameToUiAsync(() =>
                    {
                        if (disposed || generationSnapshot != Volatile.Read(ref generation))
                        {
                            nextBitmap.Dispose();
                            return;
                        }

                        ReplaceCurrentFrame(nextBitmap);
                        Interlocked.Increment(ref framesDecoded);
                        var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                        RecordRenderInterval(nowUtcMs);
                        var ageMs = jpegFrame.CapturedTsUtcMs > 0
                            ? Math.Max(0, nowUtcMs - jpegFrame.CapturedTsUtcMs)
                            : -1;
                        RecordCaptureToRender(ageMs);
                        LastRenderedFrameAgeMs = ageMs;
                        MaybeLogRenderStats(ageMs);
#if DEBUG
                        endToEndLatency.RecordTimeSpanTicks(DateTime.UtcNow.Ticks - jpegFrame.ReceivedUtcTicks);
#endif
                    }).ConfigureAwait(false);
                    bitmap = null;
                }
                catch (Exception ex)
                {
#if DEBUG
                    decodeDurationLatency.RecordTimeSpanTicks(
                        DebugLatencyWindow.StopwatchElapsedTimeSpanTicks(decodeStartTimestamp, Stopwatch.GetTimestamp()));
#endif
                    bitmap?.Dispose();
                    Interlocked.Increment(ref decodeErrors);
                    LogDebug($"Viewer frame decode/apply failed: {ex.GetType().Name}: {ex.Message}");
                    await postStatusToUiAsync(() =>
                    {
                        if (!disposed && generationSnapshot == Volatile.Read(ref generation))
                        {
                            StatusText = "Invalid frame received";
                        }
                    }).ConfigureAwait(false);
                }
            }
            finally
            {
                jpegFrame?.Dispose();
            }
        }
        finally
        {
            Interlocked.Exchange(ref decodeInFlight, 0);
            lock (gate)
            {
                if (!disposed &&
                    pendingJpegFrame is not null &&
                    Interlocked.Exchange(ref decodeInFlight, 1) == 0)
                {
                    _ = Task.Run(ProcessDecodeOnceAsync);
                }
            }
        }
    }

    private void ReplaceCurrentFrame(Bitmap? nextFrame)
    {
        var previous = currentFrame;
        CurrentFrame = nextFrame;
        if (previous is not null)
        {
            try
            {
                previous.Dispose();
            }
            catch (Exception ex)
            {
                LogDebug($"Viewer previous-frame disposal failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void ReplacePendingFrame(PendingJpegFrame pendingFrame)
    {
        lock (gate)
        {
            if (pendingJpegFrame is not null)
            {
                Interlocked.Increment(ref framesCoalesced);
                pendingJpegFrame.Dispose();
            }

            pendingJpegFrame = pendingFrame;
        }
    }

    private PendingJpegFrame? TakePendingFrame()
    {
        lock (gate)
        {
            var jpegFrame = pendingJpegFrame;
            pendingJpegFrame = null;
            return jpegFrame;
        }
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

        var metrics = GetMetricsSnapshot();
        var ageText = ageMs >= 0 ? ageMs.ToString() : "(none)";
        LocalOperationalLog.Info(
            "ScreenShare",
            $"event=frameshare_render; age_ms={ageText}; frames_completed={Interlocked.Read(ref framesReceived)}; frames_decoded={metrics.FramesDecoded}; frames_coalesced={metrics.FramesCoalesced}; avg_render_interval_ms={metrics.AverageRenderIntervalMs:F1}; avg_capture_to_render_ms={metrics.AverageCaptureToRenderMs:F1}; stale_frame_renders={metrics.StaleFrameRenders}; chunks_dropped_older_frame={Interlocked.Read(ref chunksDroppedOlderFrame)}; assemblies_expired={Interlocked.Read(ref assembliesExpired)}");
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
        if (ageMs > StaleFrameThresholdMs)
        {
            Interlocked.Increment(ref staleFrameRenders);
        }
    }

    private static double ComputeAverage(long total, long count)
    {
        return count > 0 ? (double)total / count : 0;
    }

    private static Task PostFrameApplyToUiAsync(Action action)
        => PostToUiAsync(action, DispatcherPriority.Render);

    private static Task PostStatusToUiAsync(Action action)
        => PostToUiAsync(action, DispatcherPriority.Background);

    private static Task PostToUiAsync(Action action, DispatcherPriority priority)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return Task.CompletedTask;
        }

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                action();
                completion.TrySetResult();
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        }, priority);
        return completion.Task;
    }

    private static Bitmap DecodeFrame(ReadOnlyMemory<byte> jpegBytes)
    {
        if (MemoryMarshal.TryGetArray(jpegBytes, out var segment) && segment.Array is not null)
        {
            using var pooledStream = new MemoryStream(segment.Array, segment.Offset, segment.Count, writable: false, publiclyVisible: true);
            return new Bitmap(pooledStream);
        }

        using var fallbackStream = new MemoryStream(jpegBytes.ToArray(), writable: false);
        return new Bitmap(fallbackStream);
    }

#if DEBUG
    private void StartSnapshotTimer()
    {
        if (snapshotTimer is not null)
        {
            return;
        }

        snapshotTimer = new Timer(
            static state => ((ScreenShareViewerViewModel)state!).OnSnapshotTimerTick(),
            this,
            SnapshotInterval,
            SnapshotInterval);
    }

    private void StopSnapshotTimer()
    {
        Interlocked.Exchange(ref snapshotTickInFlight, 0);
        var timer = Interlocked.Exchange(ref snapshotTimer, null);
        timer?.Dispose();
    }

    private void OnSnapshotTimerTick()
    {
        if (Interlocked.Exchange(ref snapshotTickInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            if (!IsActive)
            {
                return;
            }

            var metrics = GetMetricsSnapshot();
            var decodeSummary = decodeDurationLatency.SnapshotAndReset();
            var endToEndSummary = endToEndLatency.SnapshotAndReset();
            var heapBytes = GC.GetTotalMemory(false);
            using var process = Process.GetCurrentProcess();
            LogDebug(
                $"Snapshot heap={heapBytes} ws={process.WorkingSet64} decoded={metrics.FramesDecoded} errors={metrics.DecodeErrors} inFlight={Volatile.Read(ref decodeInFlight)} " +
                $"decode={FormatLatency(decodeSummary)} e2e={FormatLatency(endToEndSummary)} age_ms={LastRenderedFrameAgeMs} avg_render_interval_ms={metrics.AverageRenderIntervalMs:F1} avg_capture_to_render_ms={metrics.AverageCaptureToRenderMs:F1} stale_frame_renders={metrics.StaleFrameRenders}.");
        }
        catch (Exception ex)
        {
            LogDebug($"Viewer snapshot failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref snapshotTickInFlight, 0);
        }
    }
#endif

    [Conditional("DEBUG")]
    private static void LogDebug(string message)
    {
        Trace.WriteLine($"[ScreenShareViewer] {message}");
    }

#if DEBUG
    private static string FormatLatency(DebugLatencySummary summary)
    {
        return !summary.HasSamples
            ? "na"
            : $"avg={summary.AverageMilliseconds:F1}ms p50={summary.P50Milliseconds:F1}ms p95={summary.P95Milliseconds:F1}ms n={summary.Count}";
    }
#endif

    private sealed class PendingJpegFrame : IDisposable
    {
        private byte[]? buffer;

        private PendingJpegFrame(byte[] buffer, int length, long capturedTsUtcMs)
        {
            this.buffer = buffer;
            Length = length;
            CapturedTsUtcMs = capturedTsUtcMs > 0 ? capturedTsUtcMs : 0;
#if DEBUG
            ReceivedUtcTicks = DateTime.UtcNow.Ticks;
#endif
        }

        public int Length { get; }

        public long CapturedTsUtcMs { get; }

#if DEBUG
        public long ReceivedUtcTicks { get; }
#endif

        public ReadOnlyMemory<byte> Memory
        {
            get
            {
                var currentBuffer = buffer ?? throw new ObjectDisposedException(nameof(PendingJpegFrame));
                return currentBuffer.AsMemory(0, Length);
            }
        }

        public static PendingJpegFrame CopyFrom(byte[] source, long capturedTsUtcMs)
        {
            var rentedBuffer = ArrayPool<byte>.Shared.Rent(source.Length);
            Buffer.BlockCopy(source, 0, rentedBuffer, 0, source.Length);
            return new PendingJpegFrame(rentedBuffer, source.Length, capturedTsUtcMs);
        }

        public void Dispose()
        {
            var bufferToReturn = Interlocked.Exchange(ref buffer, null);
            if (bufferToReturn is not null)
            {
                ArrayPool<byte>.Shared.Return(bufferToReturn);
            }
        }
    }
}
