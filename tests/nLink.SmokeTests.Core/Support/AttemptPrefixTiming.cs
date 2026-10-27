using System.Diagnostics;
using System.Text.Json;

namespace NLink.Tests;

// Cumulative fixture timings only. Intervals may nest; no app objects are retained.
internal sealed class AttemptPrefixTiming
{
    internal enum Stage { PrefixWait, PollCheck, Ready, ScopeGateWait, ReadyGateWait, PeerCheck, StateCapture, ScopeValidation, ReadyShape }
    private readonly long[] counts = new long[9], totals = new long[9], maxima = new long[9];
    private int written;

    internal long Start() => Stopwatch.GetTimestamp();

    internal void Record(Stage stage, long started)
    {
        var elapsed = Stopwatch.GetTimestamp() - started;
        var index = (int)stage;
        Interlocked.Increment(ref counts[index]);
        Interlocked.Add(ref totals[index], elapsed);
        var previous = Interlocked.Read(ref maxima[index]);
        while (elapsed > previous)
        {
            var observed = Interlocked.CompareExchange(ref maxima[index], elapsed, previous);
            if (observed == previous) break;
            previous = observed;
        }
    }

    internal void Write(string role)
    {
        if (Interlocked.Exchange(ref written, 1) != 0) return;
        try
        {
            var rows = Enum.GetValues<Stage>().Select(stage => new
            {
                Stage = stage.ToString(),
                CompletedCount = Interlocked.Read(ref counts[(int)stage]),
                TotalTicks = Interlocked.Read(ref totals[(int)stage]),
                MaxTicks = Interlocked.Read(ref maxima[(int)stage])
            }).ToArray();
            Console.WriteLine("carrier-canonical-prefix-timing=" + JsonSerializer.Serialize(new
            {
                Role = role, Frequency = Stopwatch.Frequency, IntervalsMayNest = true, Rows = rows
            }));
        }
        catch (Exception) { } // Diagnostics cannot replace the original outcome.
    }
}
