using System.Diagnostics;

namespace NLink.App.Services;

internal readonly struct TimingSpan
{
    public TimingSpan(long startTicks)
    {
        StartTicks = startTicks;
    }

    public long StartTicks { get; }

    public bool IsStarted => StartTicks > 0;

    public static TimingSpan StartNew() => new(Stopwatch.GetTimestamp());

    public double ElapsedMilliseconds()
    {
        if (StartTicks <= 0)
        {
            return 0;
        }

        var elapsedTicks = Stopwatch.GetTimestamp() - StartTicks;
        if (elapsedTicks < 0)
        {
            elapsedTicks = 0;
        }

        return (elapsedTicks * 1000d) / Stopwatch.Frequency;
    }
}
