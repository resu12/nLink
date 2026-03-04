using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

internal sealed class ScreenShareSendProbe
{
    private readonly object gate = new();
    private readonly SemaphoreSlim? inFlightSemaphore;
    private readonly byte[][] recentPayloads;
    private readonly int[] recentPayloadSizes;
    private readonly bool respectCancellation;
    private readonly TaskCompletionSource<bool> firstSendStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<PayloadCountWaiter> payloadCountWaiters = [];
    private TaskCompletionSource<bool> releaseGate;
    private int nextPayloadIndex;
    private int payloadHistoryCount;
    private long payloadsSent;
    private long chunksSent;
    private long bytesSent;
    private long canceledSendCount;
    private int currentInFlight;
    private int maxObservedInFlight;

    public ScreenShareSendProbe(
        int recentPayloadCapacity = 8,
        int maxInFlight = int.MaxValue,
        bool startBlocked = false,
        bool respectCancellation = true)
    {
        if (recentPayloadCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(recentPayloadCapacity));
        }

        if (maxInFlight <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxInFlight));
        }

        recentPayloads = new byte[recentPayloadCapacity][];
        recentPayloadSizes = new int[recentPayloadCapacity];
        this.respectCancellation = respectCancellation;
        releaseGate = CreateGate(startBlocked);
        if (maxInFlight != int.MaxValue)
        {
            inFlightSemaphore = new SemaphoreSlim(maxInFlight, maxInFlight);
        }
    }

    public long PayloadsSent => Interlocked.Read(ref payloadsSent);

    public long ChunksSent => Interlocked.Read(ref chunksSent);

    public long BytesSent => Interlocked.Read(ref bytesSent);

    public long CanceledSendCount => Interlocked.Read(ref canceledSendCount);

    public int CurrentInFlight => Volatile.Read(ref currentInFlight);

    public int MaxObservedInFlight => Volatile.Read(ref maxObservedInFlight);

    public Task FirstSendStarted => firstSendStarted.Task;

    public async Task SendPayloadAsync(byte[] payload, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(payload);
        await SendPayloadCoreAsync(payload, countChunk: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendReadOnlyPayloadAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await SendPayloadCoreAsync(payload.ToArray(), countChunk: false, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendChunkAsync(ScreenShareFrameChunkV1 chunk, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        await SendPayloadCoreAsync(ScreenSharePayloadCodec.Serialize(chunk), countChunk: true, cancellationToken).ConfigureAwait(false);
    }

    public void BlockSends()
    {
        lock (gate)
        {
            if (releaseGate.Task.IsCompleted)
            {
                releaseGate = CreateGate(startBlocked: true);
            }
        }
    }

    public void ReleaseBlockedSends()
    {
        TaskCompletionSource<bool> currentGate;
        lock (gate)
        {
            currentGate = releaseGate;
        }

        currentGate.TrySetResult(true);
    }

    public Task WaitForPayloadCountAsync(long expectedCount, TimeSpan timeout)
    {
        lock (gate)
        {
            if (Interlocked.Read(ref payloadsSent) >= expectedCount)
            {
                return Task.CompletedTask;
            }

            var waiter = new PayloadCountWaiter(
                expectedCount,
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
            payloadCountWaiters.Add(waiter);
            return waiter.TaskSource.Task.WaitAsync(timeout);
        }
    }

    public byte[][] GetRecentPayloadsSnapshot()
    {
        lock (gate)
        {
            var snapshot = new byte[payloadHistoryCount][];
            for (var i = 0; i < payloadHistoryCount; i++)
            {
                var index = GetHistoryIndex(i);
                snapshot[i] = recentPayloads[index].ToArray();
            }

            return snapshot;
        }
    }

    public int[] GetRecentPayloadSizesSnapshot()
    {
        lock (gate)
        {
            var snapshot = new int[payloadHistoryCount];
            for (var i = 0; i < payloadHistoryCount; i++)
            {
                snapshot[i] = recentPayloadSizes[GetHistoryIndex(i)];
            }

            return snapshot;
        }
    }

    private async Task SendPayloadCoreAsync(byte[] payload, bool countChunk, CancellationToken cancellationToken)
    {
        firstSendStarted.TrySetResult(true);

        if (inFlightSemaphore is not null)
        {
            if (respectCancellation)
            {
                await inFlightSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await inFlightSemaphore.WaitAsync().ConfigureAwait(false);
            }
        }

        var inFlight = Interlocked.Increment(ref currentInFlight);
        UpdateMaxObservedInFlight(inFlight);

        try
        {
            Task releaseTask;
            lock (gate)
            {
                releaseTask = releaseGate.Task;
            }

            try
            {
                if (respectCancellation)
                {
                    await releaseTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await releaseTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref canceledSendCount);
                throw;
            }

            var payloadCount = Interlocked.Increment(ref payloadsSent);
            Interlocked.Add(ref bytesSent, payload.Length);
            if (countChunk)
            {
                Interlocked.Increment(ref chunksSent);
            }

            RecordPayload(payload);
            CompletePayloadWaiters(payloadCount);
        }
        finally
        {
            Interlocked.Decrement(ref currentInFlight);
            inFlightSemaphore?.Release();
        }
    }

    private void RecordPayload(byte[] payload)
    {
        lock (gate)
        {
            recentPayloads[nextPayloadIndex] = payload.ToArray();
            recentPayloadSizes[nextPayloadIndex] = payload.Length;
            nextPayloadIndex = (nextPayloadIndex + 1) % recentPayloads.Length;
            if (payloadHistoryCount < recentPayloads.Length)
            {
                payloadHistoryCount++;
            }
        }
    }

    private void CompletePayloadWaiters(long payloadCount)
    {
        List<TaskCompletionSource<bool>>? completed = null;

        lock (gate)
        {
            for (var i = payloadCountWaiters.Count - 1; i >= 0; i--)
            {
                if (payloadCountWaiters[i].ExpectedCount <= payloadCount)
                {
                    completed ??= [];
                    completed.Add(payloadCountWaiters[i].TaskSource);
                    payloadCountWaiters.RemoveAt(i);
                }
            }
        }

        if (completed is null)
        {
            return;
        }

        foreach (var waiter in completed)
        {
            waiter.TrySetResult(true);
        }
    }

    private int GetHistoryIndex(int snapshotIndex)
    {
        var start = payloadHistoryCount == recentPayloads.Length ? nextPayloadIndex : 0;
        return (start + snapshotIndex) % recentPayloads.Length;
    }

    private void UpdateMaxObservedInFlight(int candidate)
    {
        while (true)
        {
            var current = Volatile.Read(ref maxObservedInFlight);
            if (candidate <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref maxObservedInFlight, candidate, current) == current)
            {
                return;
            }
        }
    }

    private static TaskCompletionSource<bool> CreateGate(bool startBlocked)
    {
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!startBlocked)
        {
            gate.TrySetResult(true);
        }

        return gate;
    }

    private sealed record PayloadCountWaiter(long ExpectedCount, TaskCompletionSource<bool> TaskSource);
}
