using System.Diagnostics;
using System.Reflection;
using NLink.App.Services.ScreenCapture;

namespace NLink.SmokeTests;

public sealed class WindowsScreenCaptureSourceLifecycleTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsScreenCaptureSource_StartStopDispose_IsIdempotent_AndStopsLogging()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var source = new WindowsScreenCaptureSource();
        using var writer = new StringWriter();
        using var listener = new TextWriterTraceListener(writer);
        Trace.Listeners.Add(listener);

        try
        {
            await source.StartAsync(CancellationToken.None);
            if (ShouldExpectScreenCaptureTraceLogs())
            {
                await WaitUntilAsync(
                    condition: () => GetCurrentScreenCaptureLogLines(writer, listener).Count > 0,
                    timeout: TimeSpan.FromSeconds(2),
                    pollInterval: TimeSpan.FromMilliseconds(50),
                    failureMessage: () => $"Expected capture logs after StartAsync. State: {DescribeLogState(writer, listener)}");
            }

            await source.StopAsync();
            if (ShouldExpectScreenCaptureTraceLogs())
            {
                await WaitUntilAsync(
                    condition: () => GetCurrentScreenCaptureLogLines(writer, listener)
                        .Any(line => line.Contains("Capture loop stopped and resources released.", StringComparison.Ordinal)),
                    timeout: TimeSpan.FromSeconds(2),
                    pollInterval: TimeSpan.FromMilliseconds(50),
                    failureMessage: () => $"Expected stop marker after first StopAsync. State: {DescribeLogState(writer, listener)}");
            }

            await WaitForStableLogCountAsync(
                getCount: () => GetCurrentScreenCaptureLogLines(writer, listener).Count,
                timeout: TimeSpan.FromSeconds(2),
                pollInterval: TimeSpan.FromMilliseconds(50),
                stablePolls: 5,
                failureMessage: "Timed out waiting for screen-capture logs to stabilize after first StopAsync.");

            await source.StopAsync();
            var logAfterSecondStop = GetCurrentScreenCaptureLogLines(writer, listener);

            await WaitForStableLogCountAsync(
                getCount: () => GetCurrentScreenCaptureLogLines(writer, listener).Count,
                timeout: TimeSpan.FromSeconds(2),
                pollInterval: TimeSpan.FromMilliseconds(50),
                stablePolls: 5,
                failureMessage: "Timed out waiting for screen-capture logs to stabilize after second StopAsync.");

            var logAfterWait = GetCurrentScreenCaptureLogLines(writer, listener);

            await source.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => source.StartAsync(CancellationToken.None));

            Assert.Equal(logAfterSecondStop, logAfterWait);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    private static IReadOnlyList<string> GetCurrentScreenCaptureLogLines(StringWriter writer, TextWriterTraceListener listener)
    {
        listener.Flush();
        return GetScreenCaptureLogLines(writer.ToString());
    }

    private static IReadOnlyList<string> GetScreenCaptureLogLines(string rawLogs)
    {
        return rawLogs
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.Contains("[ScreenCapture]", StringComparison.Ordinal))
            .ToArray();
    }

    private static bool ShouldExpectScreenCaptureTraceLogs()
    {
        var configuration = typeof(WindowsScreenCaptureSource).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()
            ?.Configuration;

        return string.Equals(configuration, "Debug", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan pollInterval,
        Func<string> failureMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(pollInterval);
        }

        Assert.True(condition(), failureMessage());
    }

    private static async Task WaitForStableLogCountAsync(
        Func<int> getCount,
        TimeSpan timeout,
        TimeSpan pollInterval,
        int stablePolls,
        string failureMessage)
    {
        var deadline = DateTime.UtcNow + timeout;
        var samples = new List<int>();
        int? previous = null;
        var stableCount = 0;

        while (DateTime.UtcNow < deadline)
        {
            var current = getCount();
            samples.Add(current);

            if (previous.HasValue && previous.Value == current)
            {
                stableCount++;
            }
            else
            {
                stableCount = 1;
            }

            if (stableCount >= stablePolls)
            {
                return;
            }

            previous = current;
            await Task.Delay(pollInterval);
        }

        Assert.Fail($"{failureMessage} Samples: {string.Join(", ", samples.TakeLast(10))}");
    }

    private static string DescribeLogState(StringWriter writer, TextWriterTraceListener listener)
    {
        var lines = GetCurrentScreenCaptureLogLines(writer, listener);
        return $"count={lines.Count}; lastLines={string.Join(" | ", lines.TakeLast(5))}";
    }
}
