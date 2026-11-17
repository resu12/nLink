using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace NLink.Tests;

// Test-only scheduling diagnostics. No event text, app object, or authority is retained.
internal sealed class AttemptDeferredWriteTiming
{
    internal enum Boundary { CursorStart, CursorEnd, ReleaseStart, ReleaseEnd, RecheckRequested, RecheckGateEntered }
    internal readonly record struct Point(long Counter, long UtcTicks);
    private readonly Func<Point> clock;
    private readonly Point?[] boundaries = new Point?[6];
    private Point? firstReadStart, lastReadStart, lastReadEnd, lastPollEnd, matchedAt;
    private long readCount, completedReads, totalReadTicks, maxReadTicks;
    private int? lastTailCharacters;
    private long? matchedWriterUtcTicks, matchedWriterSequence;
    private long? beforeReleaseNowUtcTicks, beforeReleaseDueUtcTicks, recheckNowUtcTicks, recheckDueUtcTicks;
    private bool written;

    internal AttemptDeferredWriteTiming(Func<Point>? clock = null) =>
        this.clock = clock ?? (() => new(Stopwatch.GetTimestamp(), DateTimeOffset.UtcNow.UtcTicks));

    internal void Capture(Boundary boundary)
    {
        try { if (!written) boundaries[(int)boundary] ??= clock(); }
        catch (Exception) { /* Diagnostics never replace the original failure. */ }
    }

    internal void Grace(bool recheck, DateTimeOffset now, DateTimeOffset due)
    {
        if (written) return;
        if (recheck) { recheckNowUtcTicks ??= now.UtcTicks; recheckDueUtcTicks ??= due.UtcTicks; }
        else { beforeReleaseNowUtcTicks ??= now.UtcTicks; beforeReleaseDueUtcTicks ??= due.UtcTicks; }
    }

    internal void BeginRead()
    {
        try
        {
            if (written) return;
            readCount++;
            lastReadStart = null; lastReadEnd = null; lastPollEnd = null; lastTailCharacters = null;
            lastReadStart = clock(); firstReadStart ??= lastReadStart;
        }
        catch (Exception) { }
    }

    internal void ReadCompleted(int characters)
    {
        try
        {
            if (written) return;
            completedReads++; lastTailCharacters = characters;
            lastReadEnd = clock();
            if (lastReadStart is { } start)
            {
                var ticks = lastReadEnd.Value.Counter - start.Counter;
                totalReadTicks += ticks; maxReadTicks = Math.Max(maxReadTicks, ticks);
            }
        }
        catch (Exception) { }
    }

    internal void Match(string line)
    {
        try
        {
            if (written || matchedAt is not null) return;
            matchedAt = clock();
            matchedWriterUtcTicks = Scalar(line, "; log_writer_attempt_utc_ticks=");
            matchedWriterSequence = Scalar(line, "; log_writer_sequence=");
        }
        catch (Exception) { }
    }

    internal void EndPoll()
    {
        try { if (!written) lastPollEnd = clock(); }
        catch (Exception) { }
    }

    private static long? Scalar(string line, string marker)
    {
        var start = line.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0 || start != line.LastIndexOf(marker, StringComparison.Ordinal)) return null;
        var value = line.AsSpan(start + marker.Length);
        var end = value.IndexOf(';');
        if (end >= 0) value = value[..end];
        value = value.Trim();
        return value.Length is > 0 and <= 19 &&
            long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 0 ? number : null;
    }

    internal void Write(TextWriter output)
    {
        if (written) return;
        written = true;
        try
        {
            // One bounded record, formatted only after the carrier's cleanup, outside its gates.
            output.WriteLine("carrier-canonical-deferred-write-timing=" + JsonSerializer.Serialize(new
            {
                Frequency = Stopwatch.Frequency,
                CursorStart = boundaries[0], CursorEnd = boundaries[1],
                ReleaseStart = boundaries[2], ReleaseEnd = boundaries[3],
                RecheckRequested = boundaries[4], RecheckGateEntered = boundaries[5],
                FirstReadStart = firstReadStart, LastReadStart = lastReadStart, LastReadEnd = lastReadEnd,
                LastPollEnd = lastPollEnd, ReadCount = readCount, CompletedReads = completedReads,
                TotalReadTicks = totalReadTicks, MaxReadTicks = maxReadTicks, LastTailCharacters = lastTailCharacters,
                MatchedAt = matchedAt, MatchedWriterUtcTicks = matchedWriterUtcTicks, MatchedWriterSequence = matchedWriterSequence,
                BeforeReleaseNowUtcTicks = beforeReleaseNowUtcTicks, BeforeReleaseDueUtcTicks = beforeReleaseDueUtcTicks,
                RecheckNowUtcTicks = recheckNowUtcTicks, RecheckDueUtcTicks = recheckDueUtcTicks
            }));
        }
        catch (Exception) { /* Output is one-shot and must not mask the original exception. */ }
    }
}
