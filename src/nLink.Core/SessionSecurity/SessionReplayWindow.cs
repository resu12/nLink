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
    private readonly HashSet<long> acceptedSequences = [];
    private long highestAcceptedSequence;
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
    }

    public int WindowSize => windowSize;

    public long MaxForwardAdvance => maxForwardAdvance;

    public bool HasHighestSequence => hasHighestSequence;

    public long HighestAcceptedSequence => highestAcceptedSequence;

    public int TrackedSequenceCount => acceptedSequences.Count;

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
            acceptedSequences.Add(sequence);
            return SessionReplaySequenceResult.Accepted;
        }

        if (sequence > highestAcceptedSequence)
        {
            if (sequence - highestAcceptedSequence > maxForwardAdvance)
            {
                return SessionReplaySequenceResult.TooFarAhead;
            }

            highestAcceptedSequence = sequence;
            PruneOldSequences();
            acceptedSequences.Add(sequence);
            return SessionReplaySequenceResult.Accepted;
        }

        var minimumAcceptedSequence = highestAcceptedSequence - windowSize + 1;
        if (sequence < minimumAcceptedSequence)
        {
            return SessionReplaySequenceResult.Stale;
        }

        if (!acceptedSequences.Add(sequence))
        {
            return SessionReplaySequenceResult.Duplicate;
        }

        return SessionReplaySequenceResult.Accepted;
    }

    public void Reset()
    {
        acceptedSequences.Clear();
        highestAcceptedSequence = 0;
        hasHighestSequence = false;
    }

    private void PruneOldSequences()
    {
        var minimumAcceptedSequence = highestAcceptedSequence - windowSize + 1;
        foreach (var acceptedSequence in acceptedSequences.ToArray())
        {
            if (acceptedSequence < minimumAcceptedSequence)
            {
                acceptedSequences.Remove(acceptedSequence);
            }
        }
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
