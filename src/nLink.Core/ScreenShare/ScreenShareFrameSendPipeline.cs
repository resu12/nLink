using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;

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

    private readonly Func<ScreenShareFrameChunkV1, CancellationToken, Task> sendChunkAsync;
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
    private long framesCaptured;
    private long framesQueued;
    private long framesDropped;
    private long chunksSent;
    private bool disposed;

    public ScreenShareFrameSendPipeline(
        Func<ScreenShareFrameChunkV1, CancellationToken, Task> sendChunkAsync,
        int capacity = 2,
        IScreenShareClock? clock = null,
        int maxFramesPerSecond = MaxFramesPerSecond)
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
        minimumFrameInterval = TimeSpan.FromMilliseconds(1000d / maxFramesPerSecond);
        pendingSignals = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        sendLoopTask = Task.Run(ProcessLoopAsync, CancellationToken.None);
    }

    public ScreenShareMetrics GetMetricsSnapshot()
    {
        return new ScreenShareMetrics(
            FramesCaptured: Interlocked.Read(ref framesCaptured),
            FramesQueued: Interlocked.Read(ref framesQueued),
            FramesDropped: Interlocked.Read(ref framesDropped),
            ChunksSent: Interlocked.Read(ref chunksSent));
    }

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
                Interlocked.Increment(ref framesDropped);
            }

            pendingFrames.Enqueue(frame);
            AssertBufferBounds();
            lastQueuedFrameAtUtc = now;
            Interlocked.Increment(ref framesQueued);
            pendingSignals.Writer.TryWrite(true);
        }

        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        pendingSignals.Writer.TryComplete();
        disposeCts.Cancel();

        try
        {
            await sendLoopTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (disposeCts.IsCancellationRequested)
        {
        }

        disposeCts.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task ProcessLoopAsync()
    {
        await foreach (var _ in pendingSignals.Reader.ReadAllAsync(disposeCts.Token).ConfigureAwait(false))
        {
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

                var chunks = ScreenShareFrameChunker.ChunkFrame(
                    frame.SessionId,
                    frame.FrameId,
                    frame.Width,
                    frame.Height,
                    frame.Encoding,
                    frame.TimestampUnixMilliseconds,
                    frame.EncodedFrameBytes);

                foreach (var chunk in chunks)
                {
                    disposeCts.Token.ThrowIfCancellationRequested();
                    await sendChunkAsync(chunk, disposeCts.Token).ConfigureAwait(false);
                    Interlocked.Increment(ref chunksSent);
                }
            }
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

    [Conditional("DEBUG")]
    private void AssertBufferBounds()
    {
        if (pendingFrames.Count > MaxBufferedFrames)
        {
            throw new InvalidOperationException($"Screenshare sender buffer exceeded max of {MaxBufferedFrames} frames.");
        }
    }
}
