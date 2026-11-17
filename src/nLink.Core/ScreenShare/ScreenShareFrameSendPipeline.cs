using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
#if DEBUG
using NLink.Core.Diagnostics;
#endif

namespace NLink.Core.ScreenShare;

/// <summary>
/// Bounded frame sender where ordinary freshness loss happens before transport send.
/// Frames already being sent are not interrupted.
/// </summary>
// 0.3.0 RC: protocol freeze - additive changes only.
public sealed class ScreenShareFrameSendPipeline : IAsyncDisposable
{
    public const int MaxFramesPerSecond = 15;
    public const int MaxBufferedFrames = 1;
    private const int MaxBufferedFramesWithRecoveryReserve = 4;
#if DEBUG
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(10);
#endif

    private readonly Func<ScreenShareEncodedFramePacket, CancellationToken, Task<int>> sendFrameAsync;
    private readonly Func<TimeSpan, CancellationToken, Task> delayAsync;
    private readonly IScreenShareClock clock;
    private readonly ConcurrentDictionary<FrameSequenceKey, long> nextFrameIds = new();
    private readonly Queue<PendingFrame> protectedRecoveryFrames = new();
    private readonly Channel<bool> pendingSignals;
    private readonly CancellationTokenSource disposeCts = new();
    private readonly object gate = new();
    private readonly Task sendLoopTask;
    private readonly int capacity;
    private long minimumFrameIntervalTicks;
    private long lastSendStartedAtUtcTicks;
    private long framesCaptured;
    private long framesQueued;
    private long framesDropped;
    private long framesDroppedByRateGate;
    private long framesDroppedByQueueEvict;
    private long framesDeferredToSendSlot;
    private long framesReplacedBeforeSendSlot;
    private long protectedRecoveryFramesDispatched;
    private long recoveryProtectedFrameBlockedByOrdinary;
    private long sendSlotEmptyCount;
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
    private PendingFrame? ordinaryPendingFrame;
#if DEBUG
    private readonly DebugLatencyWindow captureToEnqueueLatency = new();
    private readonly DebugLatencyWindow enqueueToSendLatency = new();
    private readonly DebugLatencyWindow sendDurationLatency = new();
    private readonly DebugLatencyWindow endToEndLatency = new();
    private Timer? snapshotTimer;
    private int snapshotTickInFlight;
#endif

    public ScreenShareFrameSendPipeline(
        Func<ScreenShareEncodedFramePacket, CancellationToken, Task<int>> sendFrameAsync,
        int capacity = MaxBufferedFrames,
        IScreenShareClock? clock = null,
        int maxFramesPerSecond = MaxFramesPerSecond)
        : this(sendFrameAsync, capacity, clock, maxFramesPerSecond, delayAsync: null)
    {
    }

    private ScreenShareFrameSendPipeline(
        Func<ScreenShareEncodedFramePacket, CancellationToken, Task<int>> sendFrameAsync,
        int capacity = MaxBufferedFrames,
        IScreenShareClock? clock = null,
        int maxFramesPerSecond = MaxFramesPerSecond,
        Func<TimeSpan, CancellationToken, Task>? delayAsync = null)
    {
        this.sendFrameAsync = sendFrameAsync ?? throw new ArgumentNullException(nameof(sendFrameAsync));
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
            FramesDeferredToSendSlot: Interlocked.Read(ref framesDeferredToSendSlot),
            FramesReplacedBeforeSendSlot: Interlocked.Read(ref framesReplacedBeforeSendSlot),
            ProtectedRecoveryFramesDispatched: Interlocked.Read(ref protectedRecoveryFramesDispatched),
            RecoveryProtectedFrameBlockedByOrdinaryCount: Interlocked.Read(ref recoveryProtectedFrameBlockedByOrdinary),
            SendSlotEmptyCount: Interlocked.Read(ref sendSlotEmptyCount),
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
                Interlocked.Read(ref captureToSendSampleCount)),
            SlotCoalescingActive: true);
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
                return protectedRecoveryFrames.Count + (ordinaryPendingFrame is null ? 0 : 1);
            }
        }
    }

    internal int FrameSequenceKeyCount => nextFrameIds.Count;

    internal int FlushPendingFrames()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            var droppedCount = protectedRecoveryFrames.Count + (ordinaryPendingFrame is null ? 0 : 1);
            if (droppedCount <= 0)
            {
                return 0;
            }

            protectedRecoveryFrames.Clear();
            ordinaryPendingFrame = null;
            Interlocked.Add(ref framesDropped, droppedCount);
            return droppedCount;
        }
    }

    internal int KeepOnlyNewestPendingFrame()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            var pendingCount = protectedRecoveryFrames.Count + (ordinaryPendingFrame is null ? 0 : 1);
            if (pendingCount <= 1)
            {
                return 0;
            }

            if (protectedRecoveryFrames.Count > 0)
            {
                var droppedCount = ordinaryPendingFrame is null ? 0 : 1;
                ordinaryPendingFrame = null;
                if (droppedCount > 0)
                {
                    Interlocked.Add(ref framesDropped, droppedCount);
                }

                return droppedCount;
            }

            return 0;
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
        Volatile.Write(ref lastSendStartedAtUtcTicks, DateTimeOffset.MinValue.UtcTicks);
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
        => EnqueueFrameAsync(
            sessionId,
            width,
            height,
            encoding,
            encodedFrameBytes,
            timestampUnixMilliseconds,
            isKeyFrame: false,
            streamEpoch: 0,
            streamConfig: null,
            cancellationToken,
            preserveOrdering: false);

    public Task EnqueueFrameAsync(
        string sessionId,
        int width,
        int height,
        string encoding,
        byte[] encodedFrameBytes,
        long timestampUnixMilliseconds,
        bool isKeyFrame,
        long streamEpoch,
        ScreenShareVideoStreamConfigV1? streamConfig,
        CancellationToken cancellationToken,
        bool preserveOrdering = false)
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
            var frame = new PendingFrame(
                normalizedSessionId,
                width,
                height,
                encoding.Trim(),
                timestampUnixMilliseconds,
                encodedFrameBytes,
                now.UtcTicks,
                isKeyFrame,
                streamEpoch,
                streamConfig,
                preserveOrdering);

            if (ShouldDeferToNextSendSlot_NoLock(now))
            {
                if (TryAppendProtectedRecoveryFrame_NoLock(frame))
                {
                    Interlocked.Increment(ref framesDeferredToSendSlot);
                    Interlocked.Increment(ref framesQueued);
                    Interlocked.Increment(ref signalWriteAttempts);
                    pendingSignals.Writer.TryWrite(true);
                    return Task.CompletedTask;
                }

                if (ShouldDropIncomingToProtectRecoverySequence_NoLock(frame))
                {
                    Interlocked.Increment(ref framesDropped);
                    return Task.CompletedTask;
                }

                if (TryCoalescePendingSendSlotCandidate_NoLock(frame))
                {
                    Interlocked.Increment(ref framesDeferredToSendSlot);
                    Interlocked.Increment(ref framesQueued);
                    Interlocked.Increment(ref signalWriteAttempts);
                    pendingSignals.Writer.TryWrite(true);
                    return Task.CompletedTask;
                }

                Interlocked.Increment(ref framesDropped);
                return Task.CompletedTask;
            }

            if (TryAppendProtectedRecoveryFrame_NoLock(frame))
            {
                Interlocked.Increment(ref framesQueued);
                Interlocked.Increment(ref signalWriteAttempts);
                pendingSignals.Writer.TryWrite(true);
                return Task.CompletedTask;
            }

            if (ShouldDropIncomingToProtectRecoverySequence_NoLock(frame))
            {
                Interlocked.Increment(ref framesDropped);
                return Task.CompletedTask;
            }

            if (!TryCoalescePendingSendSlotCandidate_NoLock(frame))
            {
                Interlocked.Increment(ref framesDropped);
                return Task.CompletedTask;
            }

            AssertBufferBounds();
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
                    await WaitForNextSendSlotAsync().ConfigureAwait(false);

                    PendingFrame? frame = null;
                    lock (gate)
                    {
                        if (protectedRecoveryFrames.Count > 0)
                        {
                            frame = protectedRecoveryFrames.Dequeue();
                            Interlocked.Increment(ref protectedRecoveryFramesDispatched);
                            AssertBufferBounds();
                        }
                        else if (ordinaryPendingFrame is { } pendingOrdinaryFrame)
                        {
                            frame = pendingOrdinaryFrame;
                            ordinaryPendingFrame = null;
                            AssertBufferBounds();
                        }
                    }

                    if (frame is null)
                    {
                        Interlocked.Increment(ref sendSlotEmptyCount);
                        break;
                    }

                    PruneFrameIdsForOlderEpochs(frame.SessionId, frame.StreamEpoch);
                    var frameId = nextFrameIds.AddOrUpdate(
                        new FrameSequenceKey(frame.SessionId, frame.StreamEpoch),
                        addValueFactory: static _ => 0,
                        updateValueFactory: static (_, current) => checked(current + 1));

#if DEBUG
                    var sendStartUtcTicks = clock.UtcNow.UtcTicks;
                    var sendStartTimestamp = Stopwatch.GetTimestamp();
#endif
                    RecordEnqueueToSend(frame.EnqueuedAtUtcTicks);
                    try
                    {
                        var sentChunkCount = await sendFrameAsync(
                                new ScreenShareEncodedFramePacket(
                                    frame.SessionId,
                                    frameId,
                                    frame.Width,
                                    frame.Height,
                                    frame.Encoding,
                                    frame.TimestampUnixMilliseconds,
                                    frame.EncodedFrameBytes,
                                    frame.IsKeyFrame,
                                    frame.StreamEpoch,
                                    frame.StreamConfig),
                                disposeCts.Token)
                            .ConfigureAwait(false);
                        Interlocked.Add(ref chunksSent, Math.Max(0, sentChunkCount));
                    }
                    catch (ScreenShareSendSupersededException ex)
                    {
                        LogDebug($"Frame send superseded: {ex.Message}");
                        continue;
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

    private void PruneFrameIdsForOlderEpochs(string sessionId, long streamEpoch)
    {
        foreach (var key in nextFrameIds.Keys)
        {
            if (key.StreamEpoch < streamEpoch &&
                string.Equals(key.SessionId, sessionId, StringComparison.Ordinal))
            {
                nextFrameIds.TryRemove(key, out _);
            }
        }
    }

    private readonly record struct FrameSequenceKey(string SessionId, long StreamEpoch);

    private sealed record PendingFrame(
        string SessionId,
        int Width,
        int Height,
        string Encoding,
        long TimestampUnixMilliseconds,
        byte[] EncodedFrameBytes,
        long EnqueuedAtUtcTicks,
        bool IsKeyFrame,
        long StreamEpoch,
        ScreenShareVideoStreamConfigV1? StreamConfig,
        bool PreserveOrdering);

    private bool TryAppendProtectedRecoveryFrame_NoLock(PendingFrame incomingFrame)
    {
        if (!CanUseProtectedRecoveryReserve_NoLock(incomingFrame))
        {
            return false;
        }

        if (ordinaryPendingFrame is not null)
        {
            Interlocked.Increment(ref recoveryProtectedFrameBlockedByOrdinary);
        }

        protectedRecoveryFrames.Enqueue(incomingFrame);
        RecordCaptureToEnqueue(incomingFrame.EnqueuedAtUtcTicks, incomingFrame.TimestampUnixMilliseconds);
        AssertBufferBounds();
        return true;
    }

    private bool CanUseProtectedRecoveryReserve_NoLock(PendingFrame incomingFrame)
    {
        if (!incomingFrame.PreserveOrdering ||
            incomingFrame.StreamEpoch <= 0 ||
            protectedRecoveryFrames.Count >= MaxBufferedFramesWithRecoveryReserve)
        {
            return false;
        }

        if (protectedRecoveryFrames.Count == 0)
        {
            return true;
        }

        foreach (var queuedFrame in protectedRecoveryFrames)
        {
            if (!string.Equals(queuedFrame.SessionId, incomingFrame.SessionId, StringComparison.Ordinal) ||
                queuedFrame.StreamEpoch != incomingFrame.StreamEpoch)
            {
                return false;
            }
        }

        return true;
    }

    private bool ShouldDropIncomingToProtectRecoverySequence_NoLock(PendingFrame incomingFrame)
    {
        if (incomingFrame.StreamEpoch <= 0 ||
            incomingFrame.PreserveOrdering ||
            protectedRecoveryFrames.Count == 0)
        {
            return false;
        }

        foreach (var queuedFrame in protectedRecoveryFrames)
        {
            if (!string.Equals(queuedFrame.SessionId, incomingFrame.SessionId, StringComparison.Ordinal) ||
                queuedFrame.StreamEpoch != incomingFrame.StreamEpoch)
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private bool ShouldDeferToNextSendSlot_NoLock(DateTimeOffset now)
    {
        var lastSendStartedTicks = Volatile.Read(ref lastSendStartedAtUtcTicks);
        if (lastSendStartedTicks == DateTimeOffset.MinValue.UtcTicks)
        {
            return false;
        }

        return now.UtcTicks - lastSendStartedTicks < GetMinimumFrameInterval().Ticks;
    }

    private bool TryCoalescePendingSendSlotCandidate_NoLock(PendingFrame incomingFrame)
    {
        if (incomingFrame.PreserveOrdering)
        {
            return false;
        }

        if (ordinaryPendingFrame is null)
        {
            ordinaryPendingFrame = incomingFrame;
            RecordCaptureToEnqueue(incomingFrame.EnqueuedAtUtcTicks, incomingFrame.TimestampUnixMilliseconds);
            AssertBufferBounds();
            return true;
        }

        var existingCandidate = ordinaryPendingFrame;
        if (incomingFrame.PreserveOrdering || existingCandidate.PreserveOrdering)
        {
            return false;
        }

        if (!ShouldPreferIncomingForSendSlot(incomingFrame, existingCandidate))
        {
            return false;
        }

        ordinaryPendingFrame = incomingFrame;
        RecordCaptureToEnqueue(incomingFrame.EnqueuedAtUtcTicks, incomingFrame.TimestampUnixMilliseconds);
        Interlocked.Increment(ref framesDropped);
        Interlocked.Increment(ref framesReplacedBeforeSendSlot);
        AssertBufferBounds();
        return true;
    }

    private static bool ShouldPreferIncomingForSendSlot(PendingFrame incomingFrame, PendingFrame existingCandidate)
    {
        if (incomingFrame.StreamEpoch != existingCandidate.StreamEpoch)
        {
            return incomingFrame.StreamEpoch > existingCandidate.StreamEpoch;
        }

        var incomingHasStreamConfig = incomingFrame.StreamConfig is not null;
        var existingHasStreamConfig = existingCandidate.StreamConfig is not null;
        if (incomingHasStreamConfig != existingHasStreamConfig)
        {
            return incomingHasStreamConfig;
        }

        if (incomingFrame.IsKeyFrame != existingCandidate.IsKeyFrame)
        {
            return incomingFrame.IsKeyFrame;
        }

        if (incomingFrame.TimestampUnixMilliseconds != existingCandidate.TimestampUnixMilliseconds)
        {
            return incomingFrame.TimestampUnixMilliseconds >= existingCandidate.TimestampUnixMilliseconds;
        }

        return incomingFrame.EnqueuedAtUtcTicks >= existingCandidate.EnqueuedAtUtcTicks;
    }

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
            var nowTicks = now.UtcTicks;
            var lastSendStartedTicks = Volatile.Read(ref lastSendStartedAtUtcTicks);
            if (lastSendStartedTicks == DateTimeOffset.MinValue.UtcTicks)
            {
                Volatile.Write(ref lastSendStartedAtUtcTicks, nowTicks);
                return;
            }

            var scheduledSendAtUtcTicks = lastSendStartedTicks + GetMinimumFrameInterval().Ticks;
            var remainingTicks = scheduledSendAtUtcTicks - nowTicks;
            if (remainingTicks <= 0)
            {
                Volatile.Write(ref lastSendStartedAtUtcTicks, nowTicks);
                return;
            }

            await delayAsync(TimeSpan.FromTicks(remainingTicks), disposeCts.Token).ConfigureAwait(false);
            var resumedAtUtcTicks = clock.UtcNow.UtcTicks;
            Volatile.Write(
                ref lastSendStartedAtUtcTicks,
                resumedAtUtcTicks >= scheduledSendAtUtcTicks
                    ? resumedAtUtcTicks
                    : scheduledSendAtUtcTicks);
            return;
        }
    }

    private TimeSpan GetMinimumFrameInterval()
        => TimeSpan.FromTicks(Math.Max(1, Interlocked.Read(ref minimumFrameIntervalTicks)));

    [Conditional("DEBUG")]
    private void AssertBufferBounds()
    {
        var totalPendingFrameCount = protectedRecoveryFrames.Count + (ordinaryPendingFrame is null ? 0 : 1);
        if (totalPendingFrameCount > MaxBufferedFramesWithRecoveryReserve)
        {
            throw new InvalidOperationException($"Screenshare sender buffer exceeded max of {MaxBufferedFramesWithRecoveryReserve} frames.");
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
                $"deferred={metrics.FramesDeferredToSendSlot} replaced={metrics.FramesReplacedBeforeSendSlot} slot_empty={metrics.SendSlotEmptyCount} " +
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
