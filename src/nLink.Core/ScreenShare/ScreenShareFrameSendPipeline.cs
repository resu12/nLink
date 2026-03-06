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
    private readonly TimeSpan minimumFrameInterval;
    private DateTimeOffset lastQueuedFrameAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset lastSendStartedAtUtc = DateTimeOffset.MinValue;
    private long framesCaptured;
    private long framesQueued;
    private long framesDropped;
    private long chunksSent;
    private long lastCaptureToSendAgeMs = -1;
    private long signalWriteAttempts;
    private long signalReadCount;
    private bool disposed;
#if DEBUG
    private readonly Queue<long> pendingEnqueueUtcTicks = new();
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
        minimumFrameInterval = TimeSpan.FromMilliseconds(1000d / maxFramesPerSecond);
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
            ChunksSent: Interlocked.Read(ref chunksSent));
    }

    internal int PendingSignalCount => pendingSignals.Reader.CanCount ? pendingSignals.Reader.Count : 0;

    internal long SignalWriteAttempts => Interlocked.Read(ref signalWriteAttempts);

    internal long SignalReadCount => Interlocked.Read(ref signalReadCount);

    internal long LastCaptureToSendAgeMs => Interlocked.Read(ref lastCaptureToSendAgeMs);

    internal long WakeSignalsWritten => Interlocked.Read(ref signalWriteAttempts);

    internal long WakeSignalsRead => Interlocked.Read(ref signalReadCount);

    internal bool IsSendLoopCompleted => sendLoopTask.IsCompleted;

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
                now - lastQueuedFrameAtUtc < minimumFrameInterval)
            {
                Interlocked.Increment(ref framesDropped);
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
                encodedFrameBytes);

            if (pendingFrames.Count >= capacity)
            {
                pendingFrames.Dequeue();
#if DEBUG
                pendingEnqueueUtcTicks.Dequeue();
#endif
                Interlocked.Increment(ref framesDropped);
            }

            pendingFrames.Enqueue(frame);
#if DEBUG
            pendingEnqueueUtcTicks.Enqueue(now.UtcTicks);
            captureToEnqueueLatency.RecordTimeSpanTicks(
                now.UtcTicks - (timestampUnixMilliseconds * TimeSpan.TicksPerMillisecond));
#endif
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
#if DEBUG
                    var enqueueTimestampUtcTicks = 0L;
#endif
                    lock (gate)
                    {
                        if (pendingFrames.Count > 0)
                        {
                            frame = pendingFrames.Dequeue();
#if DEBUG
                            enqueueTimestampUtcTicks = pendingEnqueueUtcTicks.Dequeue();
#endif
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
                    if (enqueueTimestampUtcTicks != 0)
                    {
                        enqueueToSendLatency.RecordTimeSpanTicks(sendStartUtcTicks - enqueueTimestampUtcTicks);
                    }

                    var sendStartTimestamp = Stopwatch.GetTimestamp();
#endif
                    foreach (var chunk in chunks)
                    {
                        disposeCts.Token.ThrowIfCancellationRequested();
                        await sendChunkAsync(chunk, disposeCts.Token).ConfigureAwait(false);
                        Interlocked.Increment(ref chunksSent);
                    }
                    if (frame.TimestampUnixMilliseconds > 0)
                    {
                        var captureToSendAgeMs = Math.Max(0, clock.UtcNow.ToUnixTimeMilliseconds() - frame.TimestampUnixMilliseconds);
                        Interlocked.Exchange(ref lastCaptureToSendAgeMs, captureToSendAgeMs);
                    }
#if DEBUG
                    var sendEndTimestamp = Stopwatch.GetTimestamp();
                    var sendEndUtcTicks = clock.UtcNow.UtcTicks;
                    sendDurationLatency.RecordTimeSpanTicks(
                        DebugLatencyWindow.StopwatchElapsedTimeSpanTicks(sendStartTimestamp, sendEndTimestamp));
                    endToEndLatency.RecordTimeSpanTicks(
                        sendEndUtcTicks - (frame.TimestampUnixMilliseconds * TimeSpan.TicksPerMillisecond));
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
        byte[] EncodedFrameBytes);

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

            var scheduledSendAtUtc = lastSendStartedAtUtc + minimumFrameInterval;
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
                $"Latency queued={metrics.FramesQueued} dropped={metrics.FramesDropped} sent={metrics.ChunksSent} " +
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
