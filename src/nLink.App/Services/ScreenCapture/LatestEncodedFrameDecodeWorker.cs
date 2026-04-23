using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

internal readonly record struct LatestEncodedDecodedFrame(
    Bitmap Bitmap,
    EncodedFrameDecodeRequest Request,
    long CapturedTsUtcMs,
    long ReceivedUtcTicks,
    int Generation,
    long DecodeDurationTimeSpanTicks,
    long DecodeCompletedUtcMs);

internal readonly record struct LatestEncodedDecodeFailure(
    Exception Exception,
    EncodedFrameDecodeRequest Request,
    int Generation,
    long DecodeDurationTimeSpanTicks);

internal readonly record struct LatestEncodedFrameDecodeWorkerOptions(
    int MaxPendingEncodedFrames = 1,
    long MaxPendingEncodedFrameAgeMs = 0,
    bool DecoupleApplyFromDecode = false,
    int MaxPendingDecodedFrames = 0,
    Func<long>? GetNowUtcMs = null)
{
    public int EffectiveMaxPendingEncodedFrames => Math.Max(1, MaxPendingEncodedFrames);

    public int EffectiveMaxPendingDecodedFrames => DecoupleApplyFromDecode
        ? Math.Max(1, MaxPendingDecodedFrames)
        : 0;

    public long NowUtcMs() => (GetNowUtcMs ?? DefaultNowUtcMs)();

    private static long DefaultNowUtcMs()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
}

internal readonly record struct LatestEncodedFrameEnqueueResult(
    bool DroppedPendingFrame = false,
    bool DroppedByQueueOverflow = false,
    bool DroppedByAgeBudget = false);

internal readonly record struct LatestEncodedFrameDecodeWorkerMetrics(
    long FramesEnqueuedForDecode = 0,
    long FramesDroppedBeforeDecode = 0,
    long FramesDroppedAfterDecode = 0,
    long FramesDecoded = 0,
    long FramesApplyCallbacksCompleted = 0,
    double AverageReceiveIntervalMs = 0,
    double AverageDecodeDurationMs = 0,
    double AverageDecodeToApplyWaitMs = 0,
    double AverageEnqueueToDecodeStartMs = 0,
    double AverageEnqueueToDropMs = 0,
    double AverageApplyDurationMs = 0,
    double AverageApplyIntervalMs = 0,
    long MaxPendingEncodedDepth = 0,
    long MaxPendingDecodedDepth = 0,
    long DecodeWorkerDropQueueOverflowCount = 0,
    long DecodeWorkerDropAgeBudgetCount = 0,
    long DecodeWorkerDropGenerationCount = 0,
    long DecodeWorkerDropStoppedCount = 0);

internal sealed class LatestEncodedFrameDecodeWorker : IDisposable
{
    private readonly Func<EncodedFrameDecodeRequest, Bitmap> decodeFrame;
    private readonly Func<LatestEncodedDecodedFrame, Task> onFrameDecodedAsync;
    private readonly Func<LatestEncodedDecodeFailure, Task>? onDecodeFailedAsync;
    private readonly Action<EncodedFrameDecodeRequest>? onFrameEnqueued;
    private readonly Action<EncodedFrameDecodeRequest>? onFrameDecodeStarted;
    private readonly Action<EncodedFrameDecodeRequest, string>? onFrameDroppedBeforeDecode;
    private readonly Action<EncodedFrameDecodeRequest, string>? onFrameDroppedAfterDecode;
    private readonly Func<bool> shouldStop;
    private readonly Func<int> getGeneration;
    private readonly LatestEncodedFrameDecodeWorkerOptions options;
    private readonly object gate = new();

    private readonly Queue<PendingEncodedFrame> pendingFrames = new();
    private readonly Queue<PendingDecodedFrame> pendingDecodedFrames = new();
    private Task? decodeTask;
    private Task? applyTask;
    private int decodeInFlight;
    private int applyInFlight;
    private int maxDecodeTasksActive;
    private long framesEnqueuedForDecode;
    private long framesDroppedBeforeDecode;
    private long framesDroppedAfterDecode;
    private long framesDecoded;
    private long framesApplyCallbacksCompleted;
    private long lastReceivedUtcMs;
    private long receiveIntervalsObserved;
    private long totalReceiveIntervalMs;
    private long decodeDurationObserved;
    private long totalDecodeDurationMs;
    private long decodeToApplyWaitObserved;
    private long totalDecodeToApplyWaitMs;
    private long enqueueToDecodeStartObserved;
    private long totalEnqueueToDecodeStartMs;
    private long enqueueToDropObserved;
    private long totalEnqueueToDropMs;
    private long applyDurationObserved;
    private long totalApplyDurationMs;
    private long lastApplyCompletedUtcMs;
    private long applyIntervalsObserved;
    private long totalApplyIntervalMs;
    private long maxPendingEncodedDepth;
    private long maxPendingDecodedDepth;
    private long decodeWorkerDropQueueOverflowCount;
    private long decodeWorkerDropAgeBudgetCount;
    private long decodeWorkerDropGenerationCount;
    private long decodeWorkerDropStoppedCount;
    private bool disposed;

    public LatestEncodedFrameDecodeWorker(
        Func<EncodedFrameDecodeRequest, Bitmap> decodeFrame,
        Func<LatestEncodedDecodedFrame, Task> onFrameDecodedAsync,
        Func<LatestEncodedDecodeFailure, Task>? onDecodeFailedAsync,
        Func<bool> shouldStop,
        Func<int> getGeneration,
        LatestEncodedFrameDecodeWorkerOptions? options = null,
        Action<EncodedFrameDecodeRequest>? onFrameEnqueued = null,
        Action<EncodedFrameDecodeRequest>? onFrameDecodeStarted = null,
        Action<EncodedFrameDecodeRequest, string>? onFrameDroppedBeforeDecode = null,
        Action<EncodedFrameDecodeRequest, string>? onFrameDroppedAfterDecode = null)
    {
        this.decodeFrame = decodeFrame ?? throw new ArgumentNullException(nameof(decodeFrame));
        this.onFrameDecodedAsync = onFrameDecodedAsync ?? throw new ArgumentNullException(nameof(onFrameDecodedAsync));
        this.onDecodeFailedAsync = onDecodeFailedAsync;
        this.onFrameEnqueued = onFrameEnqueued;
        this.onFrameDecodeStarted = onFrameDecodeStarted;
        this.onFrameDroppedBeforeDecode = onFrameDroppedBeforeDecode;
        this.onFrameDroppedAfterDecode = onFrameDroppedAfterDecode;
        this.shouldStop = shouldStop ?? throw new ArgumentNullException(nameof(shouldStop));
        this.getGeneration = getGeneration ?? throw new ArgumentNullException(nameof(getGeneration));
        this.options = options ?? new LatestEncodedFrameDecodeWorkerOptions();
    }

    public int DecodeTasksActive => Volatile.Read(ref decodeInFlight);

    public int MaxDecodeTasksActive => Volatile.Read(ref maxDecodeTasksActive);

    public long FramesDecoded => Interlocked.Read(ref framesDecoded);

    public bool IsIdle
    {
        get
        {
            lock (gate)
            {
                return Volatile.Read(ref decodeInFlight) == 0 &&
                       Volatile.Read(ref applyInFlight) == 0 &&
                       pendingFrames.Count == 0 &&
                       pendingDecodedFrames.Count == 0;
            }
        }
    }

    public LatestEncodedFrameDecodeWorkerMetrics GetMetricsSnapshot()
    {
        return new LatestEncodedFrameDecodeWorkerMetrics(
            FramesEnqueuedForDecode: Interlocked.Read(ref framesEnqueuedForDecode),
            FramesDroppedBeforeDecode: Interlocked.Read(ref framesDroppedBeforeDecode),
            FramesDroppedAfterDecode: Interlocked.Read(ref framesDroppedAfterDecode),
            FramesDecoded: Interlocked.Read(ref framesDecoded),
            FramesApplyCallbacksCompleted: Interlocked.Read(ref framesApplyCallbacksCompleted),
            AverageReceiveIntervalMs: ComputeAverage(
                Interlocked.Read(ref totalReceiveIntervalMs),
                Interlocked.Read(ref receiveIntervalsObserved)),
            AverageDecodeDurationMs: ComputeAverage(
                Interlocked.Read(ref totalDecodeDurationMs),
                Interlocked.Read(ref decodeDurationObserved)),
            AverageDecodeToApplyWaitMs: ComputeAverage(
                Interlocked.Read(ref totalDecodeToApplyWaitMs),
                Interlocked.Read(ref decodeToApplyWaitObserved)),
            AverageEnqueueToDecodeStartMs: ComputeAverage(
                Interlocked.Read(ref totalEnqueueToDecodeStartMs),
                Interlocked.Read(ref enqueueToDecodeStartObserved)),
            AverageEnqueueToDropMs: ComputeAverage(
                Interlocked.Read(ref totalEnqueueToDropMs),
                Interlocked.Read(ref enqueueToDropObserved)),
            AverageApplyDurationMs: ComputeAverage(
                Interlocked.Read(ref totalApplyDurationMs),
                Interlocked.Read(ref applyDurationObserved)),
            AverageApplyIntervalMs: ComputeAverage(
                Interlocked.Read(ref totalApplyIntervalMs),
                Interlocked.Read(ref applyIntervalsObserved)),
            MaxPendingEncodedDepth: Interlocked.Read(ref maxPendingEncodedDepth),
            MaxPendingDecodedDepth: Interlocked.Read(ref maxPendingDecodedDepth),
            DecodeWorkerDropQueueOverflowCount: Interlocked.Read(ref decodeWorkerDropQueueOverflowCount),
            DecodeWorkerDropAgeBudgetCount: Interlocked.Read(ref decodeWorkerDropAgeBudgetCount),
            DecodeWorkerDropGenerationCount: Interlocked.Read(ref decodeWorkerDropGenerationCount),
            DecodeWorkerDropStoppedCount: Interlocked.Read(ref decodeWorkerDropStoppedCount));
    }

    public LatestEncodedFrameEnqueueResult EnqueueCopied(string encoding, byte[] encodedFrameBytes, long capturedTsUtcMs = 0, bool isKeyFrame = false, long streamEpoch = 0, long frameId = -1, string sessionId = "", bool requiresReservedApply = false, bool bypassesAgeBudget = false, ScreenShareRecoveryDeliveryClass recoveryDeliveryClass = ScreenShareRecoveryDeliveryClass.Normal, long frameReadyObservedUtcMs = 0, long viewerAcceptedUtcMs = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoding);
        ArgumentNullException.ThrowIfNull(encodedFrameBytes);
        ObjectDisposedException.ThrowIf(disposed, this);

        PendingEncodedFrame? nextPendingFrame = null;
        try
        {
            nextPendingFrame = PendingEncodedFrame.CopyFrom(encoding, encodedFrameBytes, capturedTsUtcMs, isKeyFrame, streamEpoch, frameId, sessionId, requiresReservedApply, bypassesAgeBudget, recoveryDeliveryClass, frameReadyObservedUtcMs, viewerAcceptedUtcMs);
            var enqueueResult = EnqueuePendingFrame(nextPendingFrame);
            nextPendingFrame = null;
            return enqueueResult;
        }
        finally
        {
            nextPendingFrame?.Dispose();
        }
    }

    public LatestEncodedFrameEnqueueResult EnqueueOwned(string encoding, byte[] encodedFrameBytes, long capturedTsUtcMs = 0, bool isKeyFrame = false, long streamEpoch = 0, long frameId = -1, string sessionId = "", bool requiresReservedApply = false, bool bypassesAgeBudget = false, ScreenShareRecoveryDeliveryClass recoveryDeliveryClass = ScreenShareRecoveryDeliveryClass.Normal, long frameReadyObservedUtcMs = 0, long viewerAcceptedUtcMs = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(encoding);
        ArgumentNullException.ThrowIfNull(encodedFrameBytes);
        ObjectDisposedException.ThrowIf(disposed, this);

        PendingEncodedFrame? nextPendingFrame = null;
        try
        {
            nextPendingFrame = PendingEncodedFrame.FromOwnedBuffer(encoding, encodedFrameBytes, capturedTsUtcMs, isKeyFrame, streamEpoch, frameId, sessionId, requiresReservedApply, bypassesAgeBudget, recoveryDeliveryClass, frameReadyObservedUtcMs, viewerAcceptedUtcMs);
            var enqueueResult = EnqueuePendingFrame(nextPendingFrame);
            nextPendingFrame = null;
            return enqueueResult;
        }
        finally
        {
            nextPendingFrame?.Dispose();
        }
    }

    public void ClearPending()
    {
        List<(EncodedFrameDecodeRequest Request, string Reason)>? droppedBeforeDecode = null;
        List<(EncodedFrameDecodeRequest Request, string Reason)>? droppedAfterDecode = null;
        var nowUtcMs = options.NowUtcMs();
        lock (gate)
        {
            while (pendingFrames.Count > 0)
            {
                RecordDroppedFrame(
                    ref droppedBeforeDecode,
                    DropPendingEncodedFrameUnsafe(
                        pendingFrames.Dequeue(),
                        "stopped_or_disposed",
                        nowUtcMs),
                    "stopped_or_disposed");
            }

            while (pendingDecodedFrames.Count > 0)
            {
                var dropped = pendingDecodedFrames.Dequeue();
                Interlocked.Increment(ref framesDroppedAfterDecode);
                RecordDropReason("stopped_or_disposed");
                droppedAfterDecode ??= new List<(EncodedFrameDecodeRequest Request, string Reason)>();
                droppedAfterDecode.Add((dropped.Request, "stopped_or_disposed"));
                dropped.DisposeIfOwnedByQueue();
            }
        }

        if (droppedBeforeDecode is not null)
        {
            foreach (var dropped in droppedBeforeDecode)
            {
                onFrameDroppedBeforeDecode?.Invoke(dropped.Request, dropped.Reason);
            }
        }

        if (droppedAfterDecode is not null)
        {
            foreach (var dropped in droppedAfterDecode)
            {
                onFrameDroppedAfterDecode?.Invoke(dropped.Request, dropped.Reason);
            }
        }
    }

    public async Task AwaitIdleAsync()
    {
        while (true)
        {
            Task[] tasksToAwait;
            lock (gate)
            {
                if (Volatile.Read(ref decodeInFlight) == 0 &&
                    Volatile.Read(ref applyInFlight) == 0 &&
                    pendingFrames.Count == 0 &&
                    pendingDecodedFrames.Count == 0)
                {
                    return;
                }

                var tasks = new List<Task>(2);
                if (decodeTask is not null)
                {
                    tasks.Add(decodeTask);
                }

                if (applyTask is not null)
                {
                    tasks.Add(applyTask);
                }

                tasksToAwait = tasks.ToArray();
            }

            if (tasksToAwait.Length == 0)
            {
                await Task.Delay(10).ConfigureAwait(false);
                continue;
            }

            try
            {
                await Task.WhenAll(tasksToAwait).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ClearPending();
        GC.SuppressFinalize(this);
    }

    private LatestEncodedFrameEnqueueResult EnqueuePendingFrame(PendingEncodedFrame nextPendingFrame)
    {
        var nowUtcMs = options.NowUtcMs();
        RecordReceiveInterval(nowUtcMs);

        List<(EncodedFrameDecodeRequest Request, string Reason)>? droppedFrames = null;
        LatestEncodedFrameEnqueueResult enqueueResult;
        lock (gate)
        {
            nextPendingFrame.MarkEnqueued(nowUtcMs);
            pendingFrames.Enqueue(nextPendingFrame);
            RecordQueueDepth(ref maxPendingEncodedDepth, pendingFrames.Count);
            Interlocked.Increment(ref framesEnqueuedForDecode);
            enqueueResult = TrimPendingEncodedFramesUnsafe(nowUtcMs, ref droppedFrames);
        }

        onFrameEnqueued?.Invoke(nextPendingFrame.Request);
        if (droppedFrames is not null)
        {
            foreach (var droppedFrame in droppedFrames)
            {
                onFrameDroppedBeforeDecode?.Invoke(droppedFrame.Request, droppedFrame.Reason);
            }
        }

        EnsureDecodeLoopStarted();
        return enqueueResult;
    }

    private LatestEncodedFrameEnqueueResult TrimPendingEncodedFramesUnsafe(long nowUtcMs, ref List<(EncodedFrameDecodeRequest Request, string Reason)>? droppedFrames)
    {
        var dropped = false;
        var droppedByQueueOverflow = false;
        var droppedByAgeBudget = false;
        while (pendingFrames.Count > options.EffectiveMaxPendingEncodedFrames)
        {
            RecordDroppedFrame(
                ref droppedFrames,
                DropPendingEncodedFrameUnsafe(
                    pendingFrames.Dequeue(),
                    "queue_overflow",
                    nowUtcMs),
                "queue_overflow");
            dropped = true;
            droppedByQueueOverflow = true;
        }

        if (options.MaxPendingEncodedFrameAgeMs > 0)
        {
            while (pendingFrames.Count > 1)
            {
                var oldest = pendingFrames.Peek();
                if (oldest.CapturedTsUtcMs <= 0 || oldest.Request.BypassesAgeBudget)
                {
                    break;
                }

                var ageMs = Math.Max(0, nowUtcMs - oldest.CapturedTsUtcMs);
                if (ageMs <= options.MaxPendingEncodedFrameAgeMs)
                {
                    break;
                }

                RecordDroppedFrame(
                    ref droppedFrames,
                    DropPendingEncodedFrameUnsafe(
                        pendingFrames.Dequeue(),
                        "age_budget",
                        nowUtcMs),
                    "age_budget");
                dropped = true;
                droppedByAgeBudget = true;
            }
        }

        return new LatestEncodedFrameEnqueueResult(
            DroppedPendingFrame: dropped,
            DroppedByQueueOverflow: droppedByQueueOverflow,
            DroppedByAgeBudget: droppedByAgeBudget);
    }

    private EncodedFrameDecodeRequest DropPendingEncodedFrameUnsafe(PendingEncodedFrame frame, string reason, long nowUtcMs)
    {
        Interlocked.Increment(ref framesDroppedBeforeDecode);
        RecordDropReason(reason);
        RecordEnqueueToDrop(nowUtcMs, frame.EnqueueUtcMs);
        var request = frame.Request;
        frame.Dispose();
        return request;
    }

    private PendingEncodedFrame? TakePendingFrame()
    {
        lock (gate)
        {
            return pendingFrames.Count > 0 ? pendingFrames.Dequeue() : null;
        }
    }

    private PendingDecodedFrame? TakePendingDecodedFrame()
    {
        lock (gate)
        {
            return pendingDecodedFrames.Count > 0 ? pendingDecodedFrames.Dequeue() : null;
        }
    }

    private void EnsureDecodeLoopStarted()
    {
        if (Interlocked.Exchange(ref decodeInFlight, 1) != 0)
        {
            return;
        }

        RecordDecodeTaskActivated();
        StartDecodeLoopCore();
    }

    private void EnsureApplyLoopStarted()
    {
        if (!options.DecoupleApplyFromDecode || Interlocked.Exchange(ref applyInFlight, 1) != 0)
        {
            return;
        }

        StartApplyLoopCore();
    }

    private void StartDecodeLoopCore()
    {
        Task? nextDecodeTask = null;
        nextDecodeTask = Task.Run(async () =>
        {
            try
            {
                await ProcessPendingDecodeLoopAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref decodeInFlight, 0);
                if (ReferenceEquals(decodeTask, nextDecodeTask))
                {
                    decodeTask = null;
                }

                var restart = false;
                lock (gate)
                {
                    if (!disposed &&
                        !shouldStop() &&
                        pendingFrames.Count > 0 &&
                        Interlocked.Exchange(ref decodeInFlight, 1) == 0)
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

        decodeTask = nextDecodeTask;
    }

    private void StartApplyLoopCore()
    {
        Task? nextApplyTask = null;
        nextApplyTask = Task.Run(async () =>
        {
            try
            {
                await ProcessPendingApplyLoopAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref applyInFlight, 0);
                if (ReferenceEquals(applyTask, nextApplyTask))
                {
                    applyTask = null;
                }

                var restart = false;
                lock (gate)
                {
                    if (!disposed &&
                        !shouldStop() &&
                        pendingDecodedFrames.Count > 0 &&
                        Interlocked.Exchange(ref applyInFlight, 1) == 0)
                    {
                        restart = true;
                    }
                }

                if (restart)
                {
                    StartApplyLoopCore();
                }
            }
        });

        applyTask = nextApplyTask;
    }

    private async Task ProcessPendingDecodeLoopAsync()
    {
        while (true)
        {
            var generationSnapshot = getGeneration();
            PendingEncodedFrame? frame = null;
            try
            {
                frame = TakePendingFrame();
                if (frame is null || disposed || shouldStop())
                {
                    return;
                }

                var decodeStartUtcMs = options.NowUtcMs();
                frame.MarkDecodeStarted(decodeStartUtcMs);
                onFrameDecodeStarted?.Invoke(frame.Request);
                RecordEnqueueToDecodeStart(decodeStartUtcMs, frame.EnqueueUtcMs);
                Bitmap? bitmap = null;
                var decodeStartTimestamp = Stopwatch.GetTimestamp();
                try
                {
                    bitmap = decodeFrame(frame.Request);
                    var decodeDurationTicks = Stopwatch.GetElapsedTime(decodeStartTimestamp, Stopwatch.GetTimestamp()).Ticks;
                    RecordDecodeDuration(decodeDurationTicks);
                    Interlocked.Increment(ref framesDecoded);

                    if (disposed || shouldStop())
                    {
                        bitmap.Dispose();
                        Interlocked.Increment(ref framesDroppedAfterDecode);
                        RecordDropReason("stopped_or_disposed");
                        onFrameDroppedAfterDecode?.Invoke(frame.Request, "stopped_or_disposed");
                        return;
                    }

                    if (generationSnapshot != getGeneration())
                    {
                        bitmap.Dispose();
                        Interlocked.Increment(ref framesDroppedAfterDecode);
                        RecordDropReason("generation_changed");
                        onFrameDroppedAfterDecode?.Invoke(frame.Request, "generation_changed");
                        return;
                    }

                    var decodedFrame = new LatestEncodedDecodedFrame(
                        bitmap,
                        frame.Request,
                        frame.CapturedTsUtcMs,
                        frame.ReceivedUtcTicks,
                        generationSnapshot,
                        decodeDurationTicks,
                        options.NowUtcMs());
                    bitmap = null;

                    if (options.DecoupleApplyFromDecode)
                    {
                        EnqueueDecodedFrame(decodedFrame);
                    }
                    else
                    {
                        await ApplyDecodedFrameAsync(decodedFrame, decodeCompletedUtcMs: options.NowUtcMs()).ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    bitmap?.Dispose();
                    if (onDecodeFailedAsync is not null)
                    {
                        try
                        {
                            var decodeDurationTicks = Stopwatch.GetElapsedTime(decodeStartTimestamp, Stopwatch.GetTimestamp()).Ticks;
                            RecordDecodeDuration(decodeDurationTicks);
                            await onDecodeFailedAsync(
                                    new LatestEncodedDecodeFailure(
                                        ex,
                                        frame.Request,
                                        generationSnapshot,
                                        decodeDurationTicks))
                                .ConfigureAwait(false);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            finally
            {
                frame?.Dispose();
            }
        }
    }

    private async Task ProcessPendingApplyLoopAsync()
    {
        while (true)
        {
            PendingDecodedFrame? pending = null;
            try
            {
                pending = TakePendingDecodedFrame();
                if (pending is null || disposed || shouldStop())
                {
                    return;
                }

                await ApplyDecodedFrameAsync(pending.Frame, pending.DecodeCompletedUtcMs).ConfigureAwait(false);
                pending.MarkDelivered();
            }
            finally
            {
                pending?.DisposeIfOwnedByQueue();
            }
        }
    }

    private async Task ApplyDecodedFrameAsync(LatestEncodedDecodedFrame decodedFrame, long decodeCompletedUtcMs)
    {
        var applyWaitMs = Math.Max(0, options.NowUtcMs() - decodeCompletedUtcMs);
        RecordDecodeToApplyWait(applyWaitMs);

        var applyStartTimestamp = Stopwatch.GetTimestamp();
        try
        {
            await onFrameDecodedAsync(decodedFrame).ConfigureAwait(false);
            var applyDurationMs = (long)Stopwatch.GetElapsedTime(applyStartTimestamp).TotalMilliseconds;
            RecordApplyCompletion(applyDurationMs, options.NowUtcMs());
            Interlocked.Increment(ref framesApplyCallbacksCompleted);
        }
        catch (Exception ex)
        {
            TryDisposeBitmap(decodedFrame.Bitmap);
            if (onDecodeFailedAsync is not null)
            {
                try
                {
                    await onDecodeFailedAsync(
                            new LatestEncodedDecodeFailure(
                                ex,
                                decodedFrame.Request,
                                decodedFrame.Generation,
                                decodedFrame.DecodeDurationTimeSpanTicks))
                        .ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private void EnqueueDecodedFrame(LatestEncodedDecodedFrame decodedFrame)
    {
        var decodeCompletedUtcMs = options.NowUtcMs();
        var pendingDecodedFrame = new PendingDecodedFrame(decodedFrame, decodeCompletedUtcMs);
        List<(EncodedFrameDecodeRequest Request, string Reason)>? droppedFrames = null;
        PendingDecodedFrame? incomingDropped = null;
        string? incomingDropReason = null;
        lock (gate)
        {
            if (pendingDecodedFrames.Count >= options.EffectiveMaxPendingDecodedFrames)
            {
                incomingDropped = pendingDecodedFrame;
                incomingDropReason = "decoded_apply_queue_overflow";
            }
            else
            {
                pendingDecodedFrames.Enqueue(pendingDecodedFrame);
                RecordQueueDepth(ref maxPendingDecodedDepth, pendingDecodedFrames.Count);
            }
        }

        if (droppedFrames is not null)
        {
            foreach (var droppedFrame in droppedFrames)
            {
                onFrameDroppedAfterDecode?.Invoke(droppedFrame.Request, droppedFrame.Reason);
            }
        }

        if (incomingDropped is not null)
        {
            Interlocked.Increment(ref framesDroppedAfterDecode);
            onFrameDroppedAfterDecode?.Invoke(incomingDropped.Request, incomingDropReason ?? "decoded_frame_replaced_before_apply");
            incomingDropped.DisposeIfOwnedByQueue();
            return;
        }

        EnsureApplyLoopStarted();
    }

    private static void RecordDroppedFrame(
        ref List<(EncodedFrameDecodeRequest Request, string Reason)>? droppedFrames,
        EncodedFrameDecodeRequest request,
        string reason)
    {
        droppedFrames ??= new List<(EncodedFrameDecodeRequest Request, string Reason)>();
        droppedFrames.Add((request, reason));
    }

    private void RecordDecodeTaskActivated()
    {
        while (true)
        {
            var currentMax = Volatile.Read(ref maxDecodeTasksActive);
            if (currentMax >= 1)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref maxDecodeTasksActive, 1, currentMax) == currentMax)
            {
                return;
            }
        }
    }

    private void RecordReceiveInterval(long nowUtcMs)
    {
        var previousReceivedUtcMs = Interlocked.Exchange(ref lastReceivedUtcMs, nowUtcMs);
        if (previousReceivedUtcMs <= 0 || nowUtcMs < previousReceivedUtcMs)
        {
            return;
        }

        Interlocked.Increment(ref receiveIntervalsObserved);
        Interlocked.Add(ref totalReceiveIntervalMs, nowUtcMs - previousReceivedUtcMs);
    }

    private void RecordDecodeDuration(long decodeDurationTicks)
    {
        var decodeDurationMs = (long)TimeSpan.FromTicks(decodeDurationTicks).TotalMilliseconds;
        Interlocked.Increment(ref decodeDurationObserved);
        Interlocked.Add(ref totalDecodeDurationMs, Math.Max(0, decodeDurationMs));
    }

    private void RecordDecodeToApplyWait(long waitMs)
    {
        Interlocked.Increment(ref decodeToApplyWaitObserved);
        Interlocked.Add(ref totalDecodeToApplyWaitMs, Math.Max(0, waitMs));
    }

    private void RecordEnqueueToDecodeStart(long nowUtcMs, long enqueuedUtcMs)
    {
        if (enqueuedUtcMs <= 0 || nowUtcMs < enqueuedUtcMs)
        {
            return;
        }

        Interlocked.Increment(ref enqueueToDecodeStartObserved);
        Interlocked.Add(ref totalEnqueueToDecodeStartMs, nowUtcMs - enqueuedUtcMs);
    }

    private void RecordEnqueueToDrop(long nowUtcMs, long enqueuedUtcMs)
    {
        if (enqueuedUtcMs <= 0 || nowUtcMs < enqueuedUtcMs)
        {
            return;
        }

        Interlocked.Increment(ref enqueueToDropObserved);
        Interlocked.Add(ref totalEnqueueToDropMs, nowUtcMs - enqueuedUtcMs);
    }

    private void RecordApplyCompletion(long applyDurationMs, long completedUtcMs)
    {
        Interlocked.Increment(ref applyDurationObserved);
        Interlocked.Add(ref totalApplyDurationMs, Math.Max(0, applyDurationMs));

        var previousApplyCompletedUtcMs = Interlocked.Exchange(ref lastApplyCompletedUtcMs, completedUtcMs);
        if (previousApplyCompletedUtcMs > 0 && completedUtcMs >= previousApplyCompletedUtcMs)
        {
            Interlocked.Increment(ref applyIntervalsObserved);
            Interlocked.Add(ref totalApplyIntervalMs, completedUtcMs - previousApplyCompletedUtcMs);
        }
    }

    private static double ComputeAverage(long total, long count)
    {
        return count > 0 ? (double)total / count : 0;
    }

    private static void RecordQueueDepth(ref long target, int count)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (count <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref target, count, current) == current)
            {
                return;
            }
        }
    }

    private void RecordDropReason(string reason)
    {
        switch (reason)
        {
            case "queue_overflow":
                Interlocked.Increment(ref decodeWorkerDropQueueOverflowCount);
                break;
            case "age_budget":
                Interlocked.Increment(ref decodeWorkerDropAgeBudgetCount);
                break;
            case "generation_changed":
                Interlocked.Increment(ref decodeWorkerDropGenerationCount);
                break;
            case "stopped_or_disposed":
                Interlocked.Increment(ref decodeWorkerDropStoppedCount);
                break;
        }
    }

    private static void TryDisposeBitmap(Bitmap bitmap)
    {
        try
        {
            bitmap.Dispose();
        }
        catch
        {
        }
    }

    private sealed class PendingEncodedFrame : IDisposable
    {
        private byte[]? buffer;
        private readonly bool returnBufferToPool;

        private PendingEncodedFrame(
            string encoding,
            byte[] buffer,
            int length,
            long capturedTsUtcMs,
            bool isKeyFrame,
            long streamEpoch,
            long frameId,
            string sessionId,
            bool requiresReservedApply,
            bool bypassesAgeBudget,
            ScreenShareRecoveryDeliveryClass recoveryDeliveryClass,
            long frameReadyObservedUtcMs,
            long viewerAcceptedUtcMs,
            bool returnBufferToPool)
        {
            Request = new EncodedFrameDecodeRequest(
                encoding,
                buffer.AsMemory(0, length),
                isKeyFrame,
                streamEpoch > 0 ? streamEpoch : 0,
                frameId >= 0 ? frameId : -1,
                string.IsNullOrWhiteSpace(sessionId) ? string.Empty : sessionId.Trim(),
                requiresReservedApply,
                bypassesAgeBudget,
                recoveryDeliveryClass,
                frameReadyObservedUtcMs > 0 ? frameReadyObservedUtcMs : 0,
                viewerAcceptedUtcMs > 0 ? viewerAcceptedUtcMs : 0);
            this.buffer = buffer;
            this.returnBufferToPool = returnBufferToPool;
            Length = length;
            CapturedTsUtcMs = capturedTsUtcMs > 0 ? capturedTsUtcMs : 0;
            ReceivedUtcTicks = DateTime.UtcNow.Ticks;
        }

        public EncodedFrameDecodeRequest Request { get; private set; }

        public int Length { get; }

        public long CapturedTsUtcMs { get; }

        public long ReceivedUtcTicks { get; }

        public long EnqueueUtcMs { get; private set; }

        public static PendingEncodedFrame CopyFrom(string encoding, byte[] source, long capturedTsUtcMs, bool isKeyFrame, long streamEpoch, long frameId, string sessionId, bool requiresReservedApply, bool bypassesAgeBudget, ScreenShareRecoveryDeliveryClass recoveryDeliveryClass, long frameReadyObservedUtcMs, long viewerAcceptedUtcMs)
        {
            var trimmedEncoding = encoding.Trim();
            var rentedBuffer = ArrayPool<byte>.Shared.Rent(source.Length);
            Buffer.BlockCopy(source, 0, rentedBuffer, 0, source.Length);
            return new PendingEncodedFrame(trimmedEncoding, rentedBuffer, source.Length, capturedTsUtcMs, isKeyFrame, streamEpoch, frameId, sessionId, requiresReservedApply, bypassesAgeBudget, recoveryDeliveryClass, frameReadyObservedUtcMs, viewerAcceptedUtcMs, returnBufferToPool: true);
        }

        public static PendingEncodedFrame FromOwnedBuffer(string encoding, byte[] source, long capturedTsUtcMs, bool isKeyFrame, long streamEpoch, long frameId, string sessionId, bool requiresReservedApply, bool bypassesAgeBudget, ScreenShareRecoveryDeliveryClass recoveryDeliveryClass, long frameReadyObservedUtcMs, long viewerAcceptedUtcMs)
        {
            return new PendingEncodedFrame(encoding.Trim(), source, source.Length, capturedTsUtcMs, isKeyFrame, streamEpoch, frameId, sessionId, requiresReservedApply, bypassesAgeBudget, recoveryDeliveryClass, frameReadyObservedUtcMs, viewerAcceptedUtcMs, returnBufferToPool: false);
        }

        public void MarkEnqueued(long enqueueUtcMs)
        {
            EnqueueUtcMs = enqueueUtcMs > 0 ? enqueueUtcMs : 0;
            Request = Request with
            {
                DecodeEnqueuedUtcMs = EnqueueUtcMs > 0 ? EnqueueUtcMs : 0,
            };
        }

        public void MarkDecodeStarted(long decodeStartedUtcMs)
        {
            Request = Request with
            {
                DecodeEnqueuedUtcMs = EnqueueUtcMs > 0 ? EnqueueUtcMs : Request.DecodeEnqueuedUtcMs,
                DecodeStartedUtcMs = decodeStartedUtcMs > 0 ? decodeStartedUtcMs : 0,
            };
        }

        public void Dispose()
        {
            var bufferToReturn = Interlocked.Exchange(ref buffer, null);
            if (bufferToReturn is not null && returnBufferToPool)
            {
                ArrayPool<byte>.Shared.Return(bufferToReturn);
            }
        }
    }

    private sealed class PendingDecodedFrame : IDisposable
    {
        private Bitmap? bitmap;
        private int delivered;

        public PendingDecodedFrame(LatestEncodedDecodedFrame frame, long decodeCompletedUtcMs)
        {
            bitmap = frame.Bitmap;
            Request = frame.Request;
            CapturedTsUtcMs = frame.CapturedTsUtcMs;
            ReceivedUtcTicks = frame.ReceivedUtcTicks;
            Generation = frame.Generation;
            DecodeDurationTimeSpanTicks = frame.DecodeDurationTimeSpanTicks;
            DecodeCompletedUtcMs = decodeCompletedUtcMs;
        }

        public EncodedFrameDecodeRequest Request { get; }

        public long CapturedTsUtcMs { get; }

        public long ReceivedUtcTicks { get; }

        public int Generation { get; }

        public long DecodeDurationTimeSpanTicks { get; }

        public LatestEncodedDecodedFrame Frame => new(
            bitmap ?? throw new ObjectDisposedException(nameof(PendingDecodedFrame)),
            Request,
            CapturedTsUtcMs,
            ReceivedUtcTicks,
            Generation,
            DecodeDurationTimeSpanTicks,
            DecodeCompletedUtcMs);

        public long DecodeCompletedUtcMs { get; }

        public void DisposeIfOwnedByQueue()
        {
            if (Volatile.Read(ref delivered) == 0)
            {
                Dispose();
            }
        }

        public void MarkDelivered()
        {
            Interlocked.Exchange(ref delivered, 1);
        }

        public void Dispose()
        {
            var currentBitmap = Interlocked.Exchange(ref bitmap, null);
            if (currentBitmap is not null)
            {
                TryDisposeBitmap(currentBitmap);
            }
        }
    }
}
