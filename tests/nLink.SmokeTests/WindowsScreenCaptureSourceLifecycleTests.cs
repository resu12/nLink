using System.Diagnostics;
using System.Threading;
using NLink.App.Services.ScreenCapture;

namespace NLink.SmokeTests;

public sealed class WindowsScreenCaptureSourceLifecycleTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsScreenCaptureSource_StartStopDispose_IsIdempotent_AndStopsFrames()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var source = new WindowsScreenCaptureSource();
        var firstFrameArrived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var frameCount = 0;

        source.FrameArrived += (_, _) =>
        {
            Interlocked.Increment(ref frameCount);
            firstFrameArrived.TrySetResult(true);
        };

        await source.StartAsync(CancellationToken.None);
        await WaitForFirstFrameAsync(firstFrameArrived.Task, () => Volatile.Read(ref frameCount));

        await source.StopAsync();
        var countAfterFirstStop = Volatile.Read(ref frameCount);
        var stableAfterFirstStop = await WaitForStableValueAsync(
            getValue: () => Volatile.Read(ref frameCount),
            timeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(50),
            stableSamples: 5,
            failureMessage: "Timed out waiting for frame count to stabilize after first StopAsync.");
        Assert.Equal(countAfterFirstStop, stableAfterFirstStop);

        await source.StopAsync();
        var countAfterSecondStop = Volatile.Read(ref frameCount);
        var stableAfterSecondStop = await WaitForStableValueAsync(
            getValue: () => Volatile.Read(ref frameCount),
            timeout: TimeSpan.FromSeconds(1),
            pollInterval: TimeSpan.FromMilliseconds(50),
            stableSamples: 5,
            failureMessage: "Timed out waiting for frame count to stabilize after second StopAsync.");
        Assert.Equal(countAfterSecondStop, stableAfterSecondStop);

        await source.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => source.StartAsync(CancellationToken.None));
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task WindowsScreenCaptureSource_StartStop_25Cycles_RemainsStable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        await using var source = new WindowsScreenCaptureSource();
        var frameCount = 0;
        var firstFrameArrived = CreateFirstFrameSignal();

        source.FrameArrived += (_, _) =>
        {
            Interlocked.Increment(ref frameCount);
            firstFrameArrived.TrySetResult(true);
        };

        for (var cycle = 1; cycle <= 25; cycle++)
        {
            var countBeforeStart = Volatile.Read(ref frameCount);
            if (firstFrameArrived.Task.IsCompleted)
            {
                firstFrameArrived = CreateFirstFrameSignal();
            }

            await source.StartAsync(CancellationToken.None);
            await WaitForFirstFrameAsync(
                firstFrameArrived.Task,
                () => Volatile.Read(ref frameCount),
                cycle,
                TimeSpan.FromSeconds(cycle == 1 ? 2 : 1));

            Assert.True(
                Volatile.Read(ref frameCount) > countBeforeStart,
                $"Expected at least one new frame during cycle {cycle}. Count before start={countBeforeStart}, current={Volatile.Read(ref frameCount)}.");

            await source.StopAsync();
            var countAfterStop = Volatile.Read(ref frameCount);
            var stableAfterStop = await WaitForStableValueAsync(
                getValue: () => Volatile.Read(ref frameCount),
                timeout: TimeSpan.FromSeconds(1),
                pollInterval: TimeSpan.FromMilliseconds(50),
                stableSamples: 5,
                failureMessage: $"Timed out waiting for frame count to stabilize after StopAsync in cycle {cycle}.");
            Assert.Equal(countAfterStop, stableAfterStop);
        }
    }

    private static TaskCompletionSource<bool> CreateFirstFrameSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task WaitForFirstFrameAsync(
        Task firstFrameTask,
        Func<int> getCurrentCount,
        int? cycle = null,
        TimeSpan? timeout = null)
    {
        try
        {
            await firstFrameTask.WaitAsync(timeout ?? TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException)
        {
            var cycleSuffix = cycle.HasValue ? $" in cycle {cycle.Value}" : string.Empty;
            Assert.Fail($"Expected FrameArrived within {(timeout ?? TimeSpan.FromSeconds(2)).TotalSeconds:N0} seconds{cycleSuffix}. Last observed frame count={getCurrentCount()}.");
        }
    }

    private static async Task<int> WaitForStableValueAsync(
        Func<int> getValue,
        TimeSpan timeout,
        TimeSpan pollInterval,
        int stableSamples,
        string failureMessage)
    {
        var deadline = Stopwatch.StartNew();
        var samples = new List<int>();
        int? previous = null;
        var consecutiveStableSamples = 0;

        while (deadline.Elapsed < timeout)
        {
            var current = getValue();
            samples.Add(current);

            if (previous == current)
            {
                consecutiveStableSamples++;
            }
            else
            {
                consecutiveStableSamples = 1;
            }

            if (consecutiveStableSamples >= stableSamples)
            {
                return current;
            }

            previous = current;
            await Task.Delay(pollInterval);
        }

        Assert.Fail($"{failureMessage} Last observed value={samples.LastOrDefault()}; samples={string.Join(", ", samples.TakeLast(10))}");
        return samples.LastOrDefault();
    }
}
