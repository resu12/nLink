using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
#if DEBUG
using NLink.Core.Diagnostics;
#endif

namespace NLink.Core.ScreenShare;

/// <summary>
/// Bounded frame sender that drops the oldest queued frame when capacity is exceeded.
/// Frames already being sent are not interrupted.
/// </summary>
// 0.3.0 RC: protocol freeze - additive changes only.
public sealed class ScreenShareFrameSendPipeline : IAsyncDisposable
{
    public const int MaxFramesPerSecond = 8;
    public const int MaxBufferedFrames = 2;
#if DEBUG
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(10);
#endif

    private readonly Func<ScreenShareFrameChunkV1, CancellationToken, Task> sendChunkAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly IScreenShareClock clock;
    private readonly ConcurrentDictionary<string, long> nextFrameIds = new(StringComparer.Ordinal);
    private readonly Queue<PendingFrame> pendingFrames = new();
    private readonly Channel<bool> pendingSignals;
    private readonly CancellationTokenSource disposeCts = new();
    private readonly object gate = new();
    private readonly Task sendLoopTask;
    private readonly int capacity;
    private long minimumFrameIntervalTicks;
    private DateTimeOffset lastQueuedFrameAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastSendStartedAtUtc = DateTimeOffset.MinValue;
    private long framesCaptured;
    private long framesQueued;
    private long framesDropped;
    private long framesDroppedByRateGate;
    private long framesDroppedByQueueEvict;
    private long chunksSent;
    private long rawFrameBytesSent;
    private long lastCaptureToSendAgeMs = -1;
    private long captureToEnqueueSampleCount;
    private long captureToEnqueueTotalTimeSpanTicks;
    private long enqueueToSendSampleCount;
    private long enqueueToSendTotalTimeSpanTicks;
    private long captureToSendSampleCount;
    private long captureToSendTotalMilliseconds;
    private long signalWriteAttempts;
    private long signalReadCount;
    private bool disposed;
#if DEBUG
    private readonly DebugLatencyWindow captureToEnqueueLatency = new();
    private readonly DebugLatencyWindow enqueueToSendLatency = new();
    private readonly DebugLatencyWindow sendDurationLatency = new();
    private readonly DebugLatencyWindow endToEndLatency = new();
    private Timer? snapshotTimer;
    private int snapshotTickInFlight;
#endif

    public ScreenShareFrameSendPipeline(
        Func<ScreenShareFrameChunkV1, CancellationToken, Task> sendChunkAsync,
        int capacity = 2,
        IScreenShareClock? clock = null,
        int maxFramesPerSecond = MaxFramesPerSecond)
        : this(sendChunkAsync, capacity, clock, maxFramesPerSecond, delayAsync: null)
    {
    }

    internal static ScreenShareFrameSendPipeline CreateForTesting(
        Func<ScreenShareFrameChunkV1, CancellationToken, Task> sendChunkAsync,
        int capacity = 2,
        IScreenShareClock? clock = null,
        int maxFramesPerSecond = MaxFramesPerSecond,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        return new ScreenShareFrameSendPipeline(sendChunkAsync, capacity, clock, maxFramesPerSecond, delayAsync);
    }

    private ScreenShareFrameSendPipeline(
        Func<ScreenShareFrameChunkV1, CancellationToken, Task> sendChunkAsync,
        int capacity = 2,
        IScreenShareClock? clock = null,
        int maxFramesPerSecond = MaxFramesPerSecond,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        this.sendChunkAsync = sendChunkAsync ?? throw new ArgumentNullException(nameof(sendChunkAsync));
        if (capacity <= 0 || capacity > MaxBufferedFrames)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        if (maxFramesPerSecond <= 0 || maxFramesPerSecond > MaxFramesPerSecond)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFramesPerSecond));
        }

        this.capacity = capacity;
        this.clock = clock ?? SystemScreenShareClock.Instance;
        this.delayAsync = delayAsync ?? Task.Delay;
        minimumFrameIntervalTicks = TimeSpan.FromMilliseconds(1000d / maxFramesPerSecond).Ticks;
        pendingSignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
        });

        sendLoopTask = Task.Run(ProcessLoopAsync, CancellationToken.None);
#if DEBUG
        StartSnapshotTimer();
#endif
    }

    public ScreenShareMetrics GetMetricsSnapshot()
    {
        return new ScreenShareMetrics(
            FramesCaptured: Interlocked.Read(ref framesCaptured),
            FramesQueued: Interlocked.Read(ref framesQueued),
            FramesDropped: Interlocked.Read(ref framesDropped),
            FramesDroppedByRateGate: Interlocked.Read(ref framesDroppedByRateGate),
            FramesDroppedByQueueEvict: Interlocked.Read(ref framesDroppedByQueueEvict),
            ChunksSent: Interlocked.Read(ref chunksSent),
            RawFrameBytesSent: Interlocked.Read(ref rawFrameBytesSent),
            LastCaptureToSendAgeMs: Interlocked.Read(ref lastCaptureToSendAgeMs),
            AverageCaptureToEnqueueMs: ComputeAverageMillisecondsFromTimeSpanTicks(
                Interlocked.Read(ref captureToEnqueueTotalTimeSpanTicks),
                Interlocked.Read(ref captureToEnqueueSampleCount)),
            AverageEnqueueToSendMs: ComputeAverageMillisecondsFromTimeSpanTicks(
                Interlocked.Read(ref enqueueToSendTotalTimeSpanTicks),
                Interlocked.Read(ref enqueueToSendSampleCount)),
            AverageCaptureToSendMs: ComputeAverageMillisecondsFromMilliseconds(
                Interlocked.Read(ref captureToSendTotalMilliseconds),
                Interlocked.Read(ref captureToSendSampleCount)));
    }

    internal int PendingSignalCount => pendingSignals.Reader.CanCount ? pendingSignals.Reader.Count : 0;

    internal long SignalWriteAttempts => Interlocked.Read(ref signalWriteAttempts);

    internal long SignalReadCount => Interlocked.Read(ref signalReadCount);

    internal long LastCaptureToSendAgeMs => Interlocked.Read(ref lastCaptureToSendAgeMs);

    internal long WakeSignalsWritten => Interlocked.Read(ref signalWriteAttempts);

    internal long WakeSignalsRead => Interlocked.Read(ref signalReadCount);

    internal bool IsSendLoopCompleted => sendLoopTask.IsCompleted;

    internal int PendingFrameCount
    {
        get
        {
            lock (gate)
            {
                return pendingFrames.Count;
            }
        }
    }

    internal int FlushPendingFrames()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            var droppedCount = pendingFrames.Count;
            if (droppedCount <= 0)
            {
                return 0;
            }

            pendingFrames.Clear();
            Interlocked.Add(ref framesDropped, droppedCount);
            return droppedCount;
        }
    }

    internal int KeepOnlyNewestPendingFrame()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (pendingFrames.Count <= 1)
            {
                return 0;
            }

            PendingFrame newestFrame = default!;
            foreach (var frame in pendingFrames)
            {
                newestFrame = frame;
            }

            var droppedCount = pendingFrames.Count - 1;
            pendingFrames.Clear();
            pendingFrames.Enqueue(newestFrame);
            Interlocked.Add(ref framesDropped, droppedCount);
            return droppedCount;
        }
    }

    internal void SetMaxFramesPerSecond(int maxFramesPerSecond)
    {
        if (maxFramesPerSecond <= 0 || maxFramesPerSecond > MaxFramesPerSecond)
        {
            throw new ArgumentOutOfRangeException(nameof(maxFramesPerSecond));
        }

        Interlocked.Exchange(
            ref minimumFrameIntervalTicks,
            TimeSpan.FromMilliseconds(1000d / maxFramesPerSecond).Ticks);
    }

    internal void ResetPacingWindow()
    {
        lock (gate)
        {
            lastQueuedFrameAtUtc = DateTimeOffset.MinValue;
            lastSendStartedAtUtc = DateTimeOffset.MinValue;
        }
    }

#if DEBUG
    internal (
        DebugLatencySummary CaptureToEnqueue,
        DebugLatencySummary EnqueueToSend,
        DebugLatencySummary SendDuration,
        DebugLatencySummary EndToEnd) GetDebugLatencySnapshotAndReset()
    {
        return (
            captureToEnqueueLatency.SnapshotAndReset(),
            enqueueToSendLatency.SnapshotAndReset(),
            sendDurationLatency.SnapshotAndReset(),
            endToEndLatency.SnapshotAndReset());
    }
#endif

    public Task EnqueueFrameAsync(
        string sessionId,
        int width,
        int height,
        string encoding,
        byte[] encodedFrameBytes,
        long timestampUnixMilliseconds,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(encoding);
        ArgumentNullException.ThrowIfNull(encodedFrameBytes);

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width));
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height));
        }

        if (timestampUnixMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timestampUnixMilliseconds));
        }

        if (encodedFrameBytes.Length == 0)
        {
            throw new ArgumentException("Encoded frame bytes must not be empty.", nameof(encodedFrameBytes));
        }

        Interlocked.Increment(ref framesCaptured);

        var normalizedSessionId = sessionId.Trim();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            var now = clock.UtcNow;
            if (lastQueuedFrameAtUtc != DateTimeOffset.MinValue &&
                now - lastQueuedFrameAtUtc < GetMinimumFrameInterval())
            {
                Interlocked.Increment(ref framesDropped);
                Interlocked.Increment(ref framesDroppedByRateGate);
                return Task.CompletedTask;
            }

            var frameId = nextFrameIds.AddOrUpdate(
                normalizedSessionId,
                addValueFactory: static _ => 0,
                updateValueFactory: static (_, current) => checked(current + 1));

            var frame = new PendingFrame(
                normalizedSessionId,
                frameId,
                width,
                height,
                encoding.Trim(),
                timestampUnixMilliseconds,
                encodedFrameBytes,
                now.UtcTicks);

            if (pendingFrames.Count >= capacity)
            {
                pendingFrames.Dequeue();
                Interlocked.Increment(ref framesDropped);
                Interlocked.Increment(ref framesDroppedByQueueEvict);
            }

            pendingFrames.Enqueue(frame);
            RecordCaptureToEnqueue(now.UtcTicks, timestampUnixMilliseconds);
            AssertBufferBounds();
            lastQueuedFrameAtUtc = now;
            Interlocked.Increment(ref framesQueued);
            Interlocked.Increment(ref signalWriteAttempts);
            pendingSignals.Writer.TryWrite(true);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
        }

        disposeCts.Cancel();
        pendingSignals.Writer.TryComplete();
#if DEBUG
        StopSnapshotTimer();
#endif

        try
        {
            await sendLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (disposeCts.IsCancellationRequested)
        {
            LogDebug($"Send loop canceled during dispose: {ex.GetType().Name}: {ex.Message}");
        }
        catch (ChannelClosedException ex)
        {
            LogDebug($"Send loop completed with closed channel during dispose: {ex.GetType().Name}: {ex.Message}");
        }

        disposeCts.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task ProcessLoopAsync()
    {
        try
        {
            await foreach (var signal in pendingSignals.Reader.ReadAllAsync(disposeCts.Token).ConfigureAwait(false))
            {
                _ = signal;
                Interlocked.Increment(ref signalReadCount);
                while (pendingSignals.Reader.TryRead(out _))
                {
                    Interlocked.Increment(ref signalReadCount);
                }

                while (true)
                {
                    PendingFrame? frame = null;
                    lock (gate)
                    {
                        if (pendingFrames.Count > 0)
                        {
                            frame = pendingFrames.Dequeue();
                            AssertBufferBounds();
                        }
                    }

                    if (frame is null)
                    {
                        break;
                    }

                    await WaitForNextSendSlotAsync().ConfigureAwait(false);

                    var chunks = ScreenShareFrameChunker.ChunkFrame(
                        frame.SessionId,
                        frame.FrameId,
                        frame.Width,
                        frame.Height,
                        frame.Encoding,
                        frame.TimestampUnixMilliseconds,
                        frame.EncodedFrameBytes);

#if DEBUG
                    var sendStartUtcTicks = clock.UtcNow.UtcTicks;
                    var sendStartTimestamp = Stopwatch.GetTimestamp();
#endif
                    RecordEnqueueToSend(frame.EnqueuedAtUtcTicks);
                    foreach (var chunk in chunks)
                    {
                        disposeCts.Token.ThrowIfCancellationRequested();
                        await sendChunkAsync(chunk, disposeCts.Token).ConfigureAwait(false);
                        Interlocked.Increment(ref chunksSent);
                    }
                    Interlocked.Add(ref rawFrameBytesSent, frame.EncodedFrameBytes.Length);
                    if (frame.TimestampUnixMilliseconds > 0)
                    {
                        var captureToSendAgeMs = Math.Max(0, clock.UtcNow.ToUnixTimeMilliseconds() - frame.TimestampUnixMilliseconds);
                        Interlocked.Exchange(ref lastCaptureToSendAgeMs, captureToSendAgeMs);
                        Interlocked.Increment(ref captureToSendSampleCount);
                        Interlocked.Add(ref captureToSendTotalMilliseconds, captureToSendAgeMs);
                    }
#if DEBUG
                    var sendEndTimestamp = Stopwatch.GetTimestamp();
                    var sendEndUtcTicks = clock.UtcNow.UtcTicks;
                    if (frame.EnqueuedAtUtcTicks > 0)
                    {
                        enqueueToSendLatency.RecordTimeSpanTicks(sendStartUtcTicks - frame.EnqueuedAtUtcTicks);
                    }
                    sendDurationLatency.RecordTimeSpanTicks(
                        DebugLatencyWindow.StopwatchElapsedTimeSpanTicks(sendStartTimestamp, sendEndTimestamp));
                    endToEndLatency.RecordTimeSpanTicks(
                        sendEndUtcTicks - ConvertUnixMillisecondsToUtcTicks(frame.TimestampUnixMilliseconds));
#endif
                }
            }
        }
        catch (OperationCanceledException ex) when (disposeCts.IsCancellationRequested)
        {
            LogDebug($"Send loop canceled: {ex.GetType().Name}: {ex.Message}");
        }
        catch (ChannelClosedException ex)
        {
            LogDebug($"Send loop stopped because the signal channel closed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed record PendingFrame(
        string SessionId,
        long FrameId,
        int Width,
        int Height,
        string Encoding,
        long TimestampUnixMilliseconds,
        byte[] EncodedFrameBytes,
        long EnqueuedAtUtcTicks);

    private void RecordCaptureToEnqueue(long enqueueUtcTicks, long timestampUnixMilliseconds)
    {
        if (enqueueUtcTicks <= 0 || timestampUnixMilliseconds <= 0)
        {
            return;
        }

        var captureUtcTicks = ConvertUnixMillisecondsToUtcTicks(timestampUnixMilliseconds);
        var elapsedTicks = Math.Max(0, enqueueUtcTicks - captureUtcTicks);
        Interlocked.Increment(ref captureToEnqueueSampleCount);
        Interlocked.Add(ref captureToEnqueueTotalTimeSpanTicks, elapsedTicks);
#if DEBUG
        captureToEnqueueLatency.RecordTimeSpanTicks(elapsedTicks);
#endif
    }

    private void RecordEnqueueToSend(long enqueueUtcTicks)
    {
        if (enqueueUtcTicks <= 0)
        {
            return;
        }

        var sendStartUtcTicks = clock.UtcNow.UtcTicks;
        var elapsedTicks = Math.Max(0, sendStartUtcTicks - enqueueUtcTicks);
        Interlocked.Increment(ref enqueueToSendSampleCount);
        Interlocked.Add(ref enqueueToSendTotalTimeSpanTicks, elapsedTicks);
    }

    private static double ComputeAverageMillisecondsFromTimeSpanTicks(long totalTimeSpanTicks, long sampleCount)
    {
        if (sampleCount <= 0 || totalTimeSpanTicks <= 0)
        {
            return 0;
        }

        return TimeSpan.FromTicks(totalTimeSpanTicks / sampleCount).TotalMilliseconds;
    }

    private static double ComputeAverageMillisecondsFromMilliseconds(long totalMilliseconds, long sampleCount)
    {
        if (sampleCount <= 0 || totalMilliseconds <= 0)
        {
            return 0;
        }

        return totalMilliseconds / (double)sampleCount;
    }

    private static long ConvertUnixMillisecondsToUtcTicks(long timestampUnixMilliseconds)
        => DateTimeOffset.FromUnixTimeMilliseconds(timestampUnixMilliseconds).UtcTicks;

    private async Task WaitForNextSendSlotAsync()
    {
        while (true)
        {
            var now = clock.UtcNow;
            if (lastSendStartedAtUtc == DateTimeOffset.MinValue)
            {
                lastSendStartedAtUtc = now;
                return;
            }

            var scheduledSendAtUtc = lastSendStartedAtUtc + GetMinimumFrameInterval();
            var remaining = scheduledSendAtUtc - now;
            if (remaining <= TimeSpan.Zero)
            {
                lastSendStartedAtUtc = now;
                return;
            }

            await delayAsync(remaining, disposeCts.Token).ConfigureAwait(false);
            var resumedAtUtc = clock.UtcNow;
            lastSendStartedAtUtc = resumedAtUtc >= scheduledSendAtUtc
                ? resumedAtUtc
                : scheduledSendAtUtc;
            return;
        }
    }

    private TimeSpan GetMinimumFrameInterval()
        => TimeSpan.FromTicks(Math.Max(1, Interlocked.Read(ref minimumFrameIntervalTicks)));

    [Conditional("DEBUG")]
    private void AssertBufferBounds()
    {
        if (pendingFrames.Count > MaxBufferedFrames)
        {
            throw new InvalidOperationException($"Screenshare sender buffer exceeded max of {MaxBufferedFrames} frames.");
        }
    }

    [Conditional("DEBUG")]
    private static void LogDebug(string message)
    {
        Trace.WriteLine($"[ScreenShareSendPipeline] {message}");
    }

#if DEBUG
    private void StartSnapshotTimer()
    {
        if (snapshotTimer is not null)
        {
            return;
        }

        snapshotTimer = new Timer(
            static state => ((ScreenShareFrameSendPipeline)state!).OnSnapshotTimerTick(),
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
            var metrics = GetMetricsSnapshot();
            var latency = GetDebugLatencySnapshotAndReset();
            if (!latency.CaptureToEnqueue.HasSamples &&
                !latency.EnqueueToSend.HasSamples &&
                !latency.SendDuration.HasSamples &&
                !latency.EndToEnd.HasSamples &&
                metrics.FramesQueued == 0 &&
                metrics.ChunksSent == 0)
            {
                return;
            }

            LogDebug(
                $"Latency queued={metrics.FramesQueued} dropped={metrics.FramesDropped} " +
                $"drop_rate={metrics.FramesDroppedByRateGate} drop_evict={metrics.FramesDroppedByQueueEvict} " +
                $"sent={metrics.ChunksSent} raw_bytes={metrics.RawFrameBytesSent} avg_c2e={metrics.AverageCaptureToEnqueueMs:F1}ms " +
                $"avg_q2s={metrics.AverageEnqueueToSendMs:F1}ms avg_c2s={metrics.AverageCaptureToSendMs:F1}ms " +
                $"c2e={FormatLatency(latency.CaptureToEnqueue)} q2s={FormatLatency(latency.EnqueueToSend)} " +
                $"send={FormatLatency(latency.SendDuration)} e2e={FormatLatency(latency.EndToEnd)}.");
        }
        catch (Exception ex)
        {
            LogDebug($"Latency snapshot failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref snapshotTickInFlight, 0);
        }
    }

    private static string FormatLatency(DebugLatencySummary summary)
    {
        return !summary.HasSamples
            ? "na"
            : $"avg={summary.AverageMilliseconds:F1}ms p50={summary.P50Milliseconds:F1}ms p95={summary.P95Milliseconds:F1}ms n={summary.Count}";
    }
#endif
}
