using System;
using System.Collections.Generic;

namespace NLink.Infra.Nkn;

internal sealed class LruMessageIdCache
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private readonly int capacity;
    private readonly Dictionary<string, LinkedListNode<Entry>> map = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> list = new();
    private readonly object gate = new();

    public LruMessageIdCache(int capacity = 500)
    {
        this.capacity = Math.Max(16, capacity);
    }

    public bool TryAdd(string messageId, long unixTimeMs)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            return false;
        }

        lock (gate)
        {
            EvictExpired(unixTimeMs);

            if (map.ContainsKey(messageId))
            {
                return false;
            }

            var node = list.AddFirst(new Entry(messageId, unixTimeMs));
            map[messageId] = node;

            while (map.Count > capacity)
            {
                var last = list.Last;
                if (last is null)
                {
                    break;
                }

                map.Remove(last.Value.MessageId);
                list.RemoveLast();
            }

            return true;
        }
    }

    public void Clear()
    {
        lock (gate)
        {
            map.Clear();
            list.Clear();
        }
    }

    private void EvictExpired(long nowUnixMs)
    {
        var minAllowed = nowUnixMs - (long)MaxAge.TotalMilliseconds;

        while (true)
        {
            var last = list.Last;
            if (last is null)
            {
                return;
            }

            if (last.Value.UnixTimeMs >= minAllowed)
            {
                return;
            }

            map.Remove(last.Value.MessageId);
            list.RemoveLast();
        }
    }

    private readonly record struct Entry(string MessageId, long UnixTimeMs);
}
