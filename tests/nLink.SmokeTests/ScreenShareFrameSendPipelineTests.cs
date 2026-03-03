using System.Collections.Concurrent;
using System.Reflection;
using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

public sealed class ScreenShareFrameSendPipelineTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrameSendPipeline_RateGate_DropsFramesAboveMaxFramesPerSecond()
    {
        var sentFrameIds = new ConcurrentQueue<long>();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 12, 0, 0, TimeSpan.Zero));

        await using var pipeline = new ScreenShareFrameSendPipeline(
            sendChunkAsync: (chunk, _) =>
            {
                sentFrameIds.Enqueue(chunk.FrameId);
                return Task.CompletedTask;
            },
            capacity: 2,
            clock: clock);

        await pipeline.EnqueueFrameAsync("stream-rate", 800, 600, "jpeg", [1], 1000, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await pipeline.EnqueueFrameAsync("stream-rate", 800, 600, "jpeg", [2], 1001, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await pipeline.EnqueueFrameAsync("stream-rate", 800, 600, "jpeg", [3], 1002, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(25));
        await pipeline.EnqueueFrameAsync("stream-rate", 800, 600, "jpeg", [4], 1003, CancellationToken.None);

        await WaitUntilAsync(
            condition: () => sentFrameIds.Count >= 2,
            timeout: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(25),
            failureMessage: () => $"Expected two sent frames. Current ids={string.Join(", ", sentFrameIds)}");

        var metrics = pipeline.GetMetricsSnapshot();
        Assert.Equal([0L, 1L], sentFrameIds.ToArray());
        Assert.Equal(4, metrics.FramesCaptured);
        Assert.Equal(2, metrics.FramesQueued);
        Assert.Equal(2, metrics.FramesDropped);
        Assert.Equal(2, metrics.ChunksSent);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrameSendPipeline_WhenConsumerBusy_DropsOldestQueuedFrame()
    {
        var sentFrameIds = new ConcurrentQueue<long>();
        var firstChunkStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstChunk = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 12, 10, 0, TimeSpan.Zero));
        var sendCount = 0;

        await using var pipeline = new ScreenShareFrameSendPipeline(
            sendChunkAsync: async (chunk, _) =>
            {
                sentFrameIds.Enqueue(chunk.FrameId);
                if (Interlocked.Increment(ref sendCount) == 1)
                {
                    firstChunkStarted.TrySetResult(true);
                    await releaseFirstChunk.Task;
                }
            },
            capacity: 2,
            clock: clock);

        await pipeline.EnqueueFrameAsync("stream-b", 800, 600, "jpeg", [1, 2, 3], 1000, CancellationToken.None);
        await firstChunkStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        clock.Advance(TimeSpan.FromMilliseconds(125));
        await pipeline.EnqueueFrameAsync("stream-b", 800, 600, "jpeg", [4, 5, 6], 1001, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(125));
        await pipeline.EnqueueFrameAsync("stream-b", 800, 600, "jpeg", [7, 8, 9], 1002, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(125));
        await pipeline.EnqueueFrameAsync("stream-b", 800, 600, "jpeg", [10, 11, 12], 1003, CancellationToken.None);

        releaseFirstChunk.TrySetResult(true);

        await WaitUntilAsync(
            condition: () => sentFrameIds.Count >= 3,
            timeout: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(25),
            failureMessage: () => $"Expected three sent frames. Current ids={string.Join(", ", sentFrameIds)}");

        var metrics = pipeline.GetMetricsSnapshot();
        Assert.Equal([0L, 2L, 3L], sentFrameIds.ToArray());
        Assert.Equal(4, metrics.FramesCaptured);
        Assert.Equal(4, metrics.FramesQueued);
        Assert.Equal(1, metrics.FramesDropped);
        Assert.Equal(3, metrics.ChunksSent);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrameSendPipeline_WhenPushedWith100Frames_KeepsQueueBounded()
    {
        var sentFrameIds = new ConcurrentQueue<long>();
        var firstChunkStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstChunk = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 12, 30, 0, TimeSpan.Zero));
        var sendCount = 0;

        await using var pipeline = new ScreenShareFrameSendPipeline(
            sendChunkAsync: async (chunk, _) =>
            {
                sentFrameIds.Enqueue(chunk.FrameId);
                if (Interlocked.Increment(ref sendCount) == 1)
                {
                    firstChunkStarted.TrySetResult(true);
                    await releaseFirstChunk.Task;
                }
            },
            capacity: ScreenShareFrameSendPipeline.MaxBufferedFrames,
            clock: clock);

        await pipeline.EnqueueFrameAsync("stream-pressure", 800, 600, "jpeg", [1], 1000, CancellationToken.None);
        await firstChunkStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (var i = 0; i < 99; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(125));
            await pipeline.EnqueueFrameAsync("stream-pressure", 800, 600, "jpeg", [(byte)(i % 251)], 1001 + i, CancellationToken.None);
        }

        Assert.True(GetPendingFrameCount(pipeline) <= ScreenShareFrameSendPipeline.MaxBufferedFrames);

        releaseFirstChunk.TrySetResult(true);

        await WaitUntilAsync(
            condition: () => sentFrameIds.Count >= 3,
            timeout: TimeSpan.FromSeconds(2),
            pollInterval: TimeSpan.FromMilliseconds(25),
            failureMessage: () => $"Expected bounded sender to flush three frames. Current ids={string.Join(", ", sentFrameIds)}");

        var metrics = pipeline.GetMetricsSnapshot();
        Assert.Equal(100, metrics.FramesCaptured);
        Assert.Equal(100, metrics.FramesQueued);
        Assert.True(metrics.FramesDropped >= 97);
        Assert.True(GetPendingFrameCount(pipeline) <= ScreenShareFrameSendPipeline.MaxBufferedFrames);
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

    private sealed class FakeScreenShareClock : IScreenShareClock
    {
        private DateTimeOffset utcNow;

        public FakeScreenShareClock(DateTimeOffset initialUtcNow)
        {
            utcNow = initialUtcNow;
        }

        public DateTimeOffset UtcNow => utcNow;

        public void Advance(TimeSpan by)
        {
            utcNow = utcNow.Add(by);
        }
    }

    private static int GetPendingFrameCount(ScreenShareFrameSendPipeline pipeline)
    {
        var field = typeof(ScreenShareFrameSendPipeline).GetField("pendingFrames", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var queue = field!.GetValue(pipeline);
        Assert.NotNull(queue);
        var countProperty = queue!.GetType().GetProperty("Count");
        Assert.NotNull(countProperty);
        return (int)countProperty!.GetValue(queue)!;
    }
}
