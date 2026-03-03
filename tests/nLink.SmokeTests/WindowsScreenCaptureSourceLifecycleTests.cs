using System.Diagnostics;
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
            await Task.Delay(600);

            await source.StopAsync();
            await Task.Delay(250);

            await source.StopAsync();
            await Task.Delay(250);
            listener.Flush();
            var logAfterSecondStop = GetScreenCaptureLogs(writer.ToString());

            await Task.Delay(400);
            listener.Flush();
            var logAfterWait = GetScreenCaptureLogs(writer.ToString());

            await source.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(() => source.StartAsync(CancellationToken.None));

            Assert.Equal(logAfterSecondStop, logAfterWait);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    private static string GetScreenCaptureLogs(string rawLogs)
    {
        return string.Join(
            Environment.NewLine,
            rawLogs
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => line.Contains("[ScreenCapture]")));
    }
}
