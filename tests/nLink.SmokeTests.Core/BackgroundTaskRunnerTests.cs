using NLink.App.Services;
using NLink.Core.Logging;
using NLink.Core.Resources;

namespace NLink.SmokeTests;

[Trait("Area", "Core")]
public sealed class BackgroundTaskRunnerTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task BackgroundTaskRunner_Fault_LogsFailure_AndRunsFinallyCleanup()
    {
        const string operationName = "background_task_runner_fault_unit_test";
        var cleanupRan = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        ActiveRuntimeCounters.ResetForTests();
        ActiveRuntimeCounters.IncTransportTasks();

        var task = BackgroundTaskRunner.Run(
            async () =>
            {
                await Task.Yield();
                throw new InvalidOperationException("boom");
            },
            source: "BackgroundTaskRunnerTests",
            operationName: operationName,
            onFinally: () =>
            {
                ActiveRuntimeCounters.DecTransportTasks();
                cleanupRan.TrySetResult(true);
            },
            contextProvider: () => "scope=test");

        await task.WaitAsync(TimeSpan.FromSeconds(2));
        await cleanupRan.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var counters = ActiveRuntimeCounters.Snapshot();
        Assert.Equal(0, counters.ActiveTransportTasks);

        var logText = await ReadOperationalLogWithRetryAsync(LocalOperationalLog.LogFilePath);
        var matchingLine = logText
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line =>
                line.Contains("[BackgroundTaskRunnerTests]", StringComparison.Ordinal) &&
                line.Contains("event=background_task_failed", StringComparison.Ordinal) &&
                line.Contains("scope=test", StringComparison.Ordinal));

        Assert.False(string.IsNullOrWhiteSpace(matchingLine));
        Assert.Contains("event=background_task_failed", matchingLine!, StringComparison.Ordinal);
        Assert.Contains("ex=InvalidOperationException", matchingLine!, StringComparison.Ordinal);
        Assert.Contains("scope=test", matchingLine!, StringComparison.Ordinal);
    }

    private static async Task<string> ReadOperationalLogWithRetryAsync(string path)
    {
        IOException? lastError = null;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return await reader.ReadToEndAsync();
            }
            catch (IOException ex)
            {
                lastError = ex;
                await Task.Delay(50);
            }
        }

        throw lastError ?? new IOException($"Could not read operational log: {path}");
    }
}
