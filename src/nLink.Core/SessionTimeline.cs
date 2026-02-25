using System.Collections.Generic;

namespace NLink.Core;

public static class SessionTimeline
{
    private const int Capacity = 30;
    private static readonly object Gate = new();
    private static readonly Queue<SessionTimelineEntry> Entries = new(Capacity);

    public static void Record(string eventName, string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return;
        }

        var entry = new SessionTimelineEntry(
            DateTimeOffset.UtcNow,
            Sanitize(eventName, 48),
            Sanitize(reason, 120));

        lock (Gate)
        {
            Entries.Enqueue(entry);
            while (Entries.Count > Capacity)
            {
                Entries.Dequeue();
            }
        }
    }

    public static IReadOnlyList<SessionTimelineEntry> SnapshotRecent(int maxCount = Capacity)
    {
        lock (Gate)
        {
            var count = Math.Max(0, Math.Min(maxCount, Entries.Count));
            if (count == 0)
            {
                return Array.Empty<SessionTimelineEntry>();
            }

            return Entries.ToArray()[^count..];
        }
    }

    public static void Clear()
    {
        lock (Gate)
        {
            Entries.Clear();
        }
    }

    private static string Sanitize(string? value, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var text = value.Trim()
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);

        return text.Length <= maxLen ? text : text[..maxLen];
    }
}

public readonly record struct SessionTimelineEntry(DateTimeOffset TimestampUtc, string EventName, string Reason);

