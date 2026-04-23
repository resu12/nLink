namespace NLink.Core.SessionSecurity;

public enum SessionReplaySequenceResult
{
    Accepted = 0,
    Duplicate = 1,
    Stale = 2,
    TooFarAhead = 3,
    Invalid = 4,
}

public sealed class SessionReplayWindow
{
    private readonly int windowSize;
    private readonly long maxForwardAdvance;
    private readonly long[] trackedSequences;
    private long highestAcceptedSequence;
    private int trackedSequenceCount;
    private bool hasHighestSequence;

    public SessionReplayWindow(int windowSize = 128, long maxForwardAdvance = 4096)
    {
        if (windowSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(windowSize), "Replay window size must be positive.");
        }

        if (maxForwardAdvance <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxForwardAdvance), "Max forward advance must be positive.");
        }

        this.windowSize = windowSize;
        this.maxForwardAdvance = maxForwardAdvance;
        trackedSequences = new long[windowSize];
    }

    public int WindowSize => windowSize;

    public long MaxForwardAdvance => maxForwardAdvance;

    public bool HasHighestSequence => hasHighestSequence;

    public long HighestAcceptedSequence => highestAcceptedSequence;

    public long LowestAcceptedSequence => !hasHighestSequence
        ? 0
        : Math.Max(1, highestAcceptedSequence - windowSize + 1);

    public int TrackedSequenceCount => trackedSequenceCount;

    public SessionReplaySequenceResult EvaluateAndTrack(long sequence)
    {
        if (sequence <= 0)
        {
            return SessionReplaySequenceResult.Invalid;
        }

        if (!hasHighestSequence)
        {
            hasHighestSequence = true;
            highestAcceptedSequence = sequence;
            TrackAcceptedSequence(sequence);
            return SessionReplaySequenceResult.Accepted;
        }

        if (sequence > highestAcceptedSequence)
        {
            if (sequence - highestAcceptedSequence > maxForwardAdvance)
            {
                return SessionReplaySequenceResult.TooFarAhead;
            }

            AdvanceWindow(sequence);
            highestAcceptedSequence = sequence;
            TrackAcceptedSequence(sequence);
            return SessionReplaySequenceResult.Accepted;
        }

        var minimumAcceptedSequence = LowestAcceptedSequence;
        if (sequence < minimumAcceptedSequence)
        {
            return SessionReplaySequenceResult.Stale;
        }

        if (IsTracked(sequence))
        {
            return SessionReplaySequenceResult.Duplicate;
        }

        TrackAcceptedSequence(sequence);
        return SessionReplaySequenceResult.Accepted;
    }

    public void Reset()
    {
        Array.Clear(trackedSequences);
        highestAcceptedSequence = 0;
        trackedSequenceCount = 0;
        hasHighestSequence = false;
    }

    private void AdvanceWindow(long nextHighestSequence)
    {
        var delta = nextHighestSequence - highestAcceptedSequence;
        if (delta >= windowSize)
        {
            Array.Clear(trackedSequences);
            trackedSequenceCount = 0;
            return;
        }

        for (var sequence = highestAcceptedSequence + 1; sequence <= nextHighestSequence; sequence++)
        {
            ClearTrackedSlot(sequence);
        }
    }

    private bool IsTracked(long sequence)
    {
        return trackedSequences[GetSlotIndex(sequence)] == sequence;
    }

    private void TrackAcceptedSequence(long sequence)
    {
        var index = GetSlotIndex(sequence);
        if (trackedSequences[index] == sequence)
        {
            return;
        }

        if (trackedSequences[index] == 0)
        {
            trackedSequenceCount++;
        }

        trackedSequences[index] = sequence;
    }

    private void ClearTrackedSlot(long sequence)
    {
        var index = GetSlotIndex(sequence);
        if (trackedSequences[index] != 0)
        {
            trackedSequences[index] = 0;
            trackedSequenceCount = Math.Max(0, trackedSequenceCount - 1);
        }
    }

    private int GetSlotIndex(long sequence)
    {
        var index = sequence % windowSize;
        if (index < 0)
        {
            index += windowSize;
        }

        return (int)index;
    }
}

public enum SessionReplayDedupeResult
{
    Accepted = 0,
    Duplicate = 1,
    Invalid = 2,
}

public sealed class SessionReplayDedupeCache
{
    private readonly int capacity;
    private readonly Dictionary<string, LinkedListNode<string>> map = new(StringComparer.Ordinal);
    private readonly LinkedList<string> lru = [];

    public SessionReplayDedupeCache(int capacity = 256)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Replay dedupe capacity must be positive.");
        }

        this.capacity = capacity;
    }

    public int Capacity => capacity;

    public int Count => map.Count;

    public SessionReplayDedupeResult EvaluateAndTrack(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return SessionReplayDedupeResult.Invalid;
        }

        var normalized = id.Trim();
        if (map.TryGetValue(normalized, out var existing))
        {
            lru.Remove(existing);
            lru.AddFirst(existing);
            return SessionReplayDedupeResult.Duplicate;
        }

        var node = new LinkedListNode<string>(normalized);
        lru.AddFirst(node);
        map[normalized] = node;

        if (map.Count > capacity && lru.Last is LinkedListNode<string> tail)
        {
            lru.RemoveLast();
            map.Remove(tail.Value);
        }

        return SessionReplayDedupeResult.Accepted;
    }

    public void Clear()
    {
        map.Clear();
        lru.Clear();
    }
}
