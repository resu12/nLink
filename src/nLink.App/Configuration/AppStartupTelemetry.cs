using System;
using System.Diagnostics;
using NLink.Core.Logging;

namespace NLink.App.Configuration;

internal static class AppStartupTelemetry
{
    private static readonly DateTimeOffset ProcessStartUtc = ResolveProcessStartUtc();
    private static readonly Stopwatch FallbackStopwatch = Stopwatch.StartNew();

    public static void Mark(string eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return;
        }

        LocalOperationalLog.Info(
            "AppStartup",
            $"event={eventName}; elapsed_ms={GetElapsedMilliseconds()}");
    }

    private static long GetElapsedMilliseconds()
    {
        try
        {
            var elapsed = DateTimeOffset.UtcNow - ProcessStartUtc;
            return elapsed <= TimeSpan.Zero ? 0 : (long)Math.Round(elapsed.TotalMilliseconds);
        }
        catch
        {
            return FallbackStopwatch.ElapsedMilliseconds;
        }
    }

    private static DateTimeOffset ResolveProcessStartUtc()
    {
        try
        {
            return new DateTimeOffset(Process.GetCurrentProcess().StartTime.ToUniversalTime());
        }
        catch
        {
            return DateTimeOffset.UtcNow;
        }
    }
}
