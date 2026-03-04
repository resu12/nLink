#if DEBUG
using System.Diagnostics;

namespace NLink.Core.Diagnostics;

internal readonly record struct DebugLatencySummary(
    int Count,
    double AverageMilliseconds,
    double P50Milliseconds,
    double P95Milliseconds)
{
    public bool HasSamples => Count > 0;
}

internal sealed class DebugLatencyWindow
{
    private const int Capacity = 256;

    private readonly object gate = new();
    private readonly long[] samples = new long[Capacity];
    private readonly long[] scratch = new long[Capacity];
    private int nextIndex;
    private int count;
    private long sumTimeSpanTicks;

    public void RecordTimeSpanTicks(long timeSpanTicks)
    {
        if (timeSpanTicks < 0)
        {
            return;
        }

        lock (gate)
        {
            if (count == Capacity)
            {
                sumTimeSpanTicks -= samples[nextIndex];
            }
            else
            {
                count++;
            }

            samples[nextIndex] = timeSpanTicks;
            sumTimeSpanTicks += timeSpanTicks;
            nextIndex = (nextIndex + 1) % Capacity;
        }
    }

    public DebugLatencySummary SnapshotAndReset()
    {
        lock (gate)
        {
            if (count == 0)
            {
                return default;
            }

            if (count < Capacity)
            {
                Array.Copy(samples, 0, scratch, 0, count);
            }
            else
            {
                var tailLength = Capacity - nextIndex;
                Array.Copy(samples, nextIndex, scratch, 0, tailLength);
                Array.Copy(samples, 0, scratch, tailLength, nextIndex);
            }

            Array.Sort(scratch, 0, count);

            var localCount = count;
            var summary = new DebugLatencySummary(
                localCount,
                TimeSpan.FromTicks(sumTimeSpanTicks / localCount).TotalMilliseconds,
                TimeSpan.FromTicks(scratch[GetPercentileIndex(localCount, 50)]).TotalMilliseconds,
                TimeSpan.FromTicks(scratch[GetPercentileIndex(localCount, 95)]).TotalMilliseconds);

            nextIndex = 0;
            count = 0;
            sumTimeSpanTicks = 0;

            return summary;
        }
    }

    public static long StopwatchElapsedTimeSpanTicks(long startTimestamp, long endTimestamp)
    {
        if (endTimestamp <= startTimestamp)
        {
            return 0;
        }

        return (long)(((endTimestamp - startTimestamp) * (double)TimeSpan.TicksPerSecond) / Stopwatch.Frequency);
    }

    private static int GetPercentileIndex(int sampleCount, int percentile)
    {
        if (sampleCount <= 1)
        {
            return 0;
        }

        var index = (int)Math.Ceiling((sampleCount * percentile) / 100d) - 1;
        return Math.Clamp(index, 0, sampleCount - 1);
    }
}
#endif
