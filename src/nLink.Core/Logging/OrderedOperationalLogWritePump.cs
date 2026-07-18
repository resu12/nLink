using System.Diagnostics;

namespace NLink.Core.Logging;

internal sealed class OrderedOperationalLogWritePump
{
    internal const int DefaultMaximumQueuedBatches = 16_384;
    internal const long DefaultMaximumQueuedBytes = 16L * 1024L * 1024L;
    private const long MaximumDrainWriteBytes = 512L * 1024L;

    private readonly object gate = new();
    private readonly RollingFileLogger logger;
    private readonly Action<OperationalLogWriteMarker> markerSink;
    private readonly Queue<QueuedBatch> pending = new();
    private readonly int maximumQueuedBatches;
    private readonly long maximumQueuedBytes;
    private long queuedBytes;
    private long lastPersistedSequence;
    private bool workerRunning;
    private bool completionRequested;
    private bool abandonRequested;

    public OrderedOperationalLogWritePump(
        RollingFileLogger logger,
        Action<OperationalLogWriteMarker> markerSink,
        int maximumQueuedBatches = DefaultMaximumQueuedBatches,
        long maximumQueuedBytes = DefaultMaximumQueuedBytes)
    {
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.markerSink = markerSink ?? throw new ArgumentNullException(nameof(markerSink));
        this.maximumQueuedBatches = maximumQueuedBatches > 0
            ? maximumQueuedBatches
            : throw new ArgumentOutOfRangeException(nameof(maximumQueuedBatches));
        this.maximumQueuedBytes = maximumQueuedBytes > 0
            ? maximumQueuedBytes
            : throw new ArgumentOutOfRangeException(nameof(maximumQueuedBytes));
    }

    internal bool Submit(OperationalLogWriteBatch batch)
    {
        OperationalLogWriteMarker? marker = null;
        lock (gate)
        {
            if (completionRequested && !batch.IsDrain)
            {
                marker = OperationalLogWriteMarker.Failed(batch, "writer_completed", 0);
            }
            else if (pending.Count > 0 || workerRunning)
            {
                marker = EnqueueLocked(batch, "ordered_backlog");
            }
            else
            {
                var result = logger.TryWriteLine(batch.PersistedText);
                if (result.Success)
                {
                    lastPersistedSequence = Math.Max(lastPersistedSequence, batch.LastSequence);
                    Monitor.PulseAll(gate);
                    return true;
                }

                if (string.Equals(result.FailureCode, "mutex_timeout", StringComparison.Ordinal))
                {
                    marker = EnqueueLocked(batch, result.FailureCode);
                }
                else
                {
                    marker = OperationalLogWriteMarker.Failed(batch, result.FailureCode, 1);
                }
            }
        }

        if (marker.HasValue)
        {
            EmitMarker(marker.Value);
        }

        return marker is null || marker.Value.Kind != OperationalLogWriteMarkerKind.Failed;
    }

    internal bool CompleteAndFlush(OperationalLogWriteBatch drainBatch, TimeSpan timeout)
    {
        lock (gate)
        {
            if (completionRequested)
            {
                return WaitForSequenceLocked(drainBatch.LastSequence, timeout);
            }

            completionRequested = true;
        }

        _ = Submit(drainBatch);
        lock (gate)
        {
            if (WaitForSequenceLocked(drainBatch.LastSequence, timeout))
            {
                return true;
            }

            abandonRequested = true;
            var abandoned = pending.ToArray();
            pending.Clear();
            queuedBytes = 0;
            Monitor.PulseAll(gate);
            foreach (var queued in abandoned)
            {
                EmitMarker(OperationalLogWriteMarker.Failed(
                    queued.Batch,
                    "shutdown_drain_timeout",
                    queued.AttemptCount));
            }

            return false;
        }
    }

    internal bool FlushThroughSequence(long sequence, TimeSpan timeout)
    {
        lock (gate)
        {
            return WaitForSequenceLocked(sequence, timeout);
        }
    }

    private OperationalLogWriteMarker EnqueueLocked(OperationalLogWriteBatch batch, string reason)
    {
        if (!batch.IsDrain &&
            (pending.Count >= maximumQueuedBatches || queuedBytes + batch.Utf8ByteCount > maximumQueuedBytes))
        {
            return OperationalLogWriteMarker.Failed(batch, "queue_capacity_exhausted", 0);
        }

        pending.Enqueue(new QueuedBatch(batch));
        queuedBytes += batch.Utf8ByteCount;
        if (!workerRunning)
        {
            workerRunning = true;
            _ = Task.Run(DrainAsync);
        }

        return OperationalLogWriteMarker.Deferred(batch, reason);
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            QueuedBatch[] queuedBatches;
            lock (gate)
            {
                if (abandonRequested || pending.Count == 0)
                {
                    workerRunning = false;
                    Monitor.PulseAll(gate);
                    return;
                }

                var captured = new List<QueuedBatch>();
                var capturedBytes = 0L;
                foreach (var queued in pending)
                {
                    var separatorBytes = captured.Count == 0 ? 0L : Environment.NewLine.Length;
                    if (captured.Count > 0 &&
                        capturedBytes + separatorBytes + queued.Batch.Utf8ByteCount > MaximumDrainWriteBytes)
                    {
                        break;
                    }

                    queued.AttemptCount++;
                    captured.Add(queued);
                    capturedBytes += separatorBytes + queued.Batch.Utf8ByteCount;
                }

                queuedBatches = captured.ToArray();
            }

            var persistedText = string.Join(
                Environment.NewLine,
                queuedBatches.Select(static queued => queued.Batch.PersistedText));
            var result = logger.TryWriteLine(persistedText);
            var markers = new List<OperationalLogWriteMarker>();
            lock (gate)
            {
                if (abandonRequested)
                {
                    workerRunning = false;
                    Monitor.PulseAll(gate);
                    return;
                }

                if (pending.Count < queuedBatches.Length ||
                    !ReferenceEquals(pending.Peek(), queuedBatches[0]))
                {
                    continue;
                }

                if (result.Success)
                {
                    foreach (var queued in queuedBatches)
                    {
                        _ = pending.Dequeue();
                        queuedBytes -= queued.Batch.Utf8ByteCount;
                        lastPersistedSequence = Math.Max(lastPersistedSequence, queued.Batch.LastSequence);
                        markers.Add(OperationalLogWriteMarker.Recovered(queued.Batch, queued.AttemptCount));
                    }
                    Monitor.PulseAll(gate);
                }
                else if (!string.Equals(result.FailureCode, "mutex_timeout", StringComparison.Ordinal))
                {
                    foreach (var queued in queuedBatches)
                    {
                        _ = pending.Dequeue();
                        queuedBytes -= queued.Batch.Utf8ByteCount;
                        markers.Add(OperationalLogWriteMarker.Failed(
                            queued.Batch,
                            result.FailureCode,
                            queued.AttemptCount));
                    }
                    Monitor.PulseAll(gate);
                }
            }

            foreach (var marker in markers)
            {
                EmitMarker(marker);
            }

            if (!result.Success && string.Equals(result.FailureCode, "mutex_timeout", StringComparison.Ordinal))
            {
                await Task.Yield();
            }
        }
    }

    private bool WaitForSequenceLocked(long sequence, TimeSpan timeout)
    {
        if (lastPersistedSequence >= sequence)
        {
            return true;
        }

        var stopwatch = Stopwatch.StartNew();
        while (lastPersistedSequence < sequence && !abandonRequested)
        {
            var remaining = timeout - stopwatch.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            Monitor.Wait(gate, remaining);
        }

        return lastPersistedSequence >= sequence;
    }

    private void EmitMarker(OperationalLogWriteMarker marker)
    {
        try
        {
            markerSink(marker);
        }
        catch
        {
            // Producer-side evidence must not affect runtime behavior.
        }
    }

    private sealed class QueuedBatch(OperationalLogWriteBatch batch)
    {
        public OperationalLogWriteBatch Batch { get; } = batch;

        public int AttemptCount { get; set; } = 1;
    }
}

internal readonly record struct OperationalLogWriteBatch(
    string AcceptanceRunId,
    string ScenarioRunId,
    string WriterId,
    long BatchGeneration,
    long FirstSequence,
    long LastSequence,
    long AttemptUtcTicks,
    string PayloadSha256,
    string PersistedText,
    long Utf8ByteCount,
    bool IsDrain);

internal enum OperationalLogWriteMarkerKind
{
    Deferred,
    Recovered,
    Failed,
}

internal readonly record struct OperationalLogWriteMarker(
    OperationalLogWriteMarkerKind Kind,
    OperationalLogWriteBatch Batch,
    string FailureCode,
    int AttemptCount)
{
    public static OperationalLogWriteMarker Deferred(OperationalLogWriteBatch batch, string reason)
        => new(OperationalLogWriteMarkerKind.Deferred, batch, reason, 1);

    public static OperationalLogWriteMarker Recovered(OperationalLogWriteBatch batch, int attemptCount)
        => new(OperationalLogWriteMarkerKind.Recovered, batch, "none", attemptCount);

    public static OperationalLogWriteMarker Failed(OperationalLogWriteBatch batch, string reason, int attemptCount)
        => new(OperationalLogWriteMarkerKind.Failed, batch, reason, attemptCount);
}
