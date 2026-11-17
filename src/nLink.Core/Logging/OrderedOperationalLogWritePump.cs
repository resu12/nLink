using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace NLink.Core.Logging;

internal sealed class OrderedOperationalLogWritePump
{
    internal const int DefaultMaximumQueuedBatches = 16_384;
    internal const long DefaultMaximumQueuedBytes = 16L * 1024L * 1024L;
    internal const int MaximumExactBatchReceipts = 64;
    private const long MaximumDrainWriteBytes = 512L * 1024L;

    private readonly object gate = new();
    private readonly RollingFileLogger logger;
    private readonly Action<OperationalLogWriteMarker> markerSink;
    private readonly Queue<QueuedBatch> pending = new();
    private readonly Dictionary<ExactBatchReceipt, ExactBatchPersistence> exactBatchReceipts =
        new(ReferenceEqualityComparer.Instance);
    private readonly int maximumQueuedBatches;
    private readonly long maximumQueuedBytes;
    private long queuedBytes;
    private long lastPersistedSequence;
    private bool workerRunning;
    private bool completionRequested;
    private bool abandonRequested;
    private bool exactBatchReceiptsRevoked;

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
        => Submit(batch, observePersistence: false, out _);

    // A receipt identifies this submission, not the copyable batch metadata. Missing
    // diagnostic capacity must not reject or delay the operational log write.
    internal bool SubmitWithReceipt(OperationalLogWriteBatch batch, out ExactBatchReceipt? receipt)
        => Submit(batch, observePersistence: true, out receipt);

    internal bool SubmitWithSourceReceipt(OperationalLogWriteBatch batch, out ExactBatchReceipt? receipt)
        => Submit(batch, observePersistence: true, out receipt, captureSource: true);

    internal ExactBatchPersistence GetPersistence(ExactBatchReceipt? receipt)
    {
        lock (gate)
        {
            if (receipt is null || !exactBatchReceipts.TryGetValue(receipt, out var persistence))
            {
                return ExactBatchPersistence.Unowned;
            }

            return exactBatchReceiptsRevoked ? ExactBatchPersistence.Revoked : persistence;
        }
    }

    internal void RevokeExactBatchReceipts()
    {
        lock (gate)
        {
            exactBatchReceiptsRevoked = true;
            foreach (var receipt in exactBatchReceipts.Keys) { receipt.Source?.Dispose(); receipt.Provisional?.Dispose(); }
        }
    }

    internal void ReleaseSource(ExactBatchReceipt? receipt)
    {
        lock (gate)
        {
            if (receipt is not null && exactBatchReceipts.ContainsKey(receipt))
            { receipt.SourceAttempted = true; receipt.Source?.Dispose(); receipt.Provisional?.Dispose(); }
        }
    }

    internal OperationalLogNativeSource? TryAcquireSource(ExactBatchReceipt drain, IReadOnlyList<ExactBatchReceipt> receipts)
    {
        OperationalLogNativeSource source;
        OperationalLogNativeSpan[] spans;
        lock (gate)
        {
            if (GetPersistence(drain) != ExactBatchPersistence.Persisted || !drain.FreezeSource || drain.SourceAttempted)
            { return null; }
            drain.SourceAttempted = true;
            if (drain.Source is not { } captured) { return null; }
            source = captured;
            if (receipts.Count is < 1 or > 33 || !receipts.Contains(drain) || receipts.Distinct().Count() != receipts.Count ||
                receipts.Any(r => GetPersistence(r) != ExactBatchPersistence.Persisted || r.Span is null))
            { source.Dispose(); return null; }
            spans = receipts.Select(r => r.Span!).ToArray();
        }
        // No writer gate is held during the bounded file traversal.
        if (!source.TryVerify(spans)) { return null; }
        lock (gate)
        {
            if (GetPersistence(drain) == ExactBatchPersistence.Persisted && source.IsVerified) { return source; }
            source.Dispose(); return null;
        }
    }

    // Original pump receipt custody, not immutable-source verification. The GUI
    // must verify these raw ranges only after every writer has finished.
    internal OperationalLogNativeAnchor? TryAcquireProvisionalSource(ExactBatchReceipt drain,
        IReadOnlyList<ExactBatchReceipt> receipts)
    {
        lock (gate)
        {
            if (GetPersistence(drain) != ExactBatchPersistence.Persisted || !drain.RetainProvisional || drain.SourceAttempted)
            { return null; }
            drain.SourceAttempted = true;
            if (drain.Provisional is not { } source) { return null; }
            if (receipts.Count is < 1 or > 33 || !receipts.Contains(drain) || receipts.Distinct().Count() != receipts.Count ||
                receipts.Any(r => GetPersistence(r) != ExactBatchPersistence.Persisted || r.Span is null) || !source.IsRetained)
            { source.Dispose(); return null; }
            var spans = receipts.Select(r => r.Span!).OrderBy(s => s.Offset).ToArray();
            if (spans.Any(s => s.FileIdentity != source.FileIdentity || s.Offset < 0 || s.ByteCount <= 0 ||
                    s.ByteCount > source.CapturedByteCount || s.Offset > source.CapturedByteCount - s.ByteCount) ||
                spans.Skip(1).Where((s, i) => s.Offset < spans[i].Offset + spans[i].ByteCount).Any())
            { source.Dispose(); return null; }
            return source;
        }
    }

    private bool Submit(
        OperationalLogWriteBatch batch,
        bool observePersistence,
        out ExactBatchReceipt? receipt,
        bool captureSource = false,
        bool freezeSource = false,
        bool retainProvisional = false)
    {
        OperationalLogWriteMarker? marker = null;
        lock (gate)
        {
            receipt = null;
            if (observePersistence && !exactBatchReceiptsRevoked && !abandonRequested &&
                exactBatchReceipts.Count < MaximumExactBatchReceipts)
            {
                receipt = new ExactBatchReceipt { CaptureSource = captureSource, FreezeSource = freezeSource, RetainProvisional = retainProvisional };
                exactBatchReceipts.Add(receipt, ExactBatchPersistence.Pending);
            }

            if (completionRequested && !batch.IsDrain)
            {
                marker = OperationalLogWriteMarker.Failed(batch, "writer_completed", 0);
            }
            else if (pending.Count > 0 || workerRunning)
            {
                marker = EnqueueLocked(batch, "ordered_backlog", receipt);
            }
            else
            {
                var result = logger.TryWriteLine(batch.PersistedText, receipt?.CaptureSource == true,
                    receipt?.FreezeSource == true, out var append, receipt?.RetainProvisional == true);
                if (result.Success)
                {
                    if (append is not null) { CaptureSourceLocked([new QueuedBatch(batch, receipt)], append); }
                    CompleteReceiptLocked(receipt, ExactBatchPersistence.Persisted);
                    lastPersistedSequence = Math.Max(lastPersistedSequence, batch.LastSequence);
                    Monitor.PulseAll(gate);
                    return true;
                }

                if (string.Equals(result.FailureCode, "mutex_timeout", StringComparison.Ordinal))
                {
                    marker = EnqueueLocked(batch, result.FailureCode, receipt);
                }
                else
                {
                    marker = OperationalLogWriteMarker.Failed(batch, result.FailureCode, 1);
                }
            }

            if (marker?.Kind == OperationalLogWriteMarkerKind.Failed)
            {
                CompleteReceiptLocked(receipt, ExactBatchPersistence.Failed);
            }
        }

        if (marker.HasValue)
        {
            EmitMarker(marker.Value);
        }

        return marker is null || marker.Value.Kind != OperationalLogWriteMarkerKind.Failed;
    }

    internal bool CompleteAndFlush(OperationalLogWriteBatch drainBatch, TimeSpan timeout)
        => CompleteAndFlush(drainBatch, timeout, observePersistence: false, out _);

    internal bool CompleteAndFlushWithReceipt(OperationalLogWriteBatch drainBatch, TimeSpan timeout, out ExactBatchReceipt? receipt)
        => CompleteAndFlush(drainBatch, timeout, observePersistence: true, out receipt);

    internal bool CompleteAndFlushWithSourceReceipt(OperationalLogWriteBatch drainBatch, TimeSpan timeout, out ExactBatchReceipt? receipt)
        => CompleteAndFlush(drainBatch, timeout, observePersistence: true, out receipt, captureSource: true);

    internal bool CompleteAndFlushWithProvisionalSourceReceipt(OperationalLogWriteBatch drainBatch, TimeSpan timeout, out ExactBatchReceipt? receipt)
        => CompleteAndFlush(drainBatch, timeout, observePersistence: true, out receipt, captureSource: true, retainProvisional: true);

    private bool CompleteAndFlush(OperationalLogWriteBatch drainBatch, TimeSpan timeout,
        bool observePersistence, out ExactBatchReceipt? receipt, bool captureSource = false, bool retainProvisional = false)
    {
        receipt = null;
        lock (gate)
        {
            if (completionRequested)
            {
                return WaitForSequenceLocked(drainBatch.LastSequence, timeout);
            }

            completionRequested = true;
        }

        _ = Submit(drainBatch, observePersistence, out receipt, captureSource,
            freezeSource: captureSource && !retainProvisional, retainProvisional: retainProvisional);
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
                CompleteReceiptLocked(queued.Receipt, ExactBatchPersistence.Abandoned);
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

    private OperationalLogWriteMarker EnqueueLocked(
        OperationalLogWriteBatch batch,
        string reason,
        ExactBatchReceipt? receipt)
    {
        if (!batch.IsDrain &&
            (pending.Count >= maximumQueuedBatches || queuedBytes + batch.Utf8ByteCount > maximumQueuedBytes))
        {
            return OperationalLogWriteMarker.Failed(batch, "queue_capacity_exhausted", 0);
        }

        pending.Enqueue(new QueuedBatch(batch, receipt));
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
            var result = logger.TryWriteLine(persistedText, queuedBatches.Any(q => q.Receipt?.CaptureSource == true),
                queuedBatches.Any(q => q.Receipt?.FreezeSource == true), out var append,
                queuedBatches.Any(q => q.Receipt?.RetainProvisional == true));
            var markers = new List<OperationalLogWriteMarker>();
            lock (gate)
            {
                if (abandonRequested)
                {
                    append?.Source?.Dispose();
                    append?.Provisional?.Dispose();
                    workerRunning = false;
                    Monitor.PulseAll(gate);
                    return;
                }

                if (pending.Count < queuedBatches.Length ||
                    !ReferenceEquals(pending.Peek(), queuedBatches[0]))
                {
                    append?.Source?.Dispose();
                    append?.Provisional?.Dispose();
                    continue;
                }

                if (result.Success)
                {
                    CaptureSourceLocked(queuedBatches, append);
                    foreach (var queued in queuedBatches)
                    {
                        _ = pending.Dequeue();
                        queuedBytes -= queued.Batch.Utf8ByteCount;
                        CompleteReceiptLocked(queued.Receipt, ExactBatchPersistence.Persisted);
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
                        CompleteReceiptLocked(queued.Receipt, ExactBatchPersistence.Failed);
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

    private void CompleteReceiptLocked(ExactBatchReceipt? receipt, ExactBatchPersistence persistence)
    {
        if (receipt is not null && !exactBatchReceiptsRevoked &&
            exactBatchReceipts[receipt] == ExactBatchPersistence.Pending)
        {
            exactBatchReceipts[receipt] = persistence;
        }
    }

    private void CaptureSourceLocked(QueuedBatch[] batches, OperationalLogNativeAppend? append)
    {
        if (append is null) { return; }
        var retained = false;
        try
        {
            if (exactBatchReceiptsRevoked || abandonRequested) { return; }
            var offset = append.Offset;
            foreach (var queued in batches)
            {
                var byteCount = Encoding.UTF8.GetByteCount(queued.Batch.PersistedText);
                if (queued.Receipt is { CaptureSource: true } receipt &&
                    GetPersistence(receipt) == ExactBatchPersistence.Pending)
                {
                    var bytes = Encoding.UTF8.GetBytes(queued.Batch.PersistedText);
                    receipt.Span = new(append.FileIdentity, offset, byteCount,
                        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
                    if (receipt.FreezeSource && !receipt.SourceAttempted) { receipt.Source = append.Source; retained = true; }
                    if (receipt.RetainProvisional && !receipt.SourceAttempted) { receipt.Provisional = append.Provisional; retained = true; }
                }
                offset += byteCount + Encoding.UTF8.GetByteCount(Environment.NewLine);
            }
        }
        catch { /* Optional custody cannot change a successful append. */ }
        finally { if (!retained) { append.Source?.Dispose(); append.Provisional?.Dispose(); } }
    }

    // The owning dictionary, not construction or copied metadata, grants identity.
    // No batch text, payload or callback is retained by these bounded tokens.
    internal sealed class ExactBatchReceipt
    {
        internal bool CaptureSource { get; init; }
        internal bool FreezeSource { get; init; }
        internal bool RetainProvisional { get; init; }
        internal OperationalLogNativeSpan? Span;
        internal OperationalLogNativeSource? Source;
        internal OperationalLogNativeAnchor? Provisional;
        internal bool SourceAttempted;
    }

    internal enum ExactBatchPersistence
    {
        Unowned,
        Pending,
        // Successful append observed by this pump, not fsync or immutable file custody.
        Persisted,
        Failed,
        Abandoned,
        Revoked,
    }

    private sealed class QueuedBatch(OperationalLogWriteBatch batch, ExactBatchReceipt? receipt)
    {
        public OperationalLogWriteBatch Batch { get; } = batch;

        public ExactBatchReceipt? Receipt { get; } = receipt;

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
