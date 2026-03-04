using System.Collections.Concurrent;
using System.Diagnostics;
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
        var twoFramesSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 12, 0, 0, TimeSpan.Zero));

        await using var pipeline = ScreenShareFrameSendPipeline.CreateForTesting(
            sendChunkAsync: (chunk, _) =>
            {
                sentFrameIds.Enqueue(chunk.FrameId);
                if (sentFrameIds.Count >= 2)
                {
                    twoFramesSent.TrySetResult(true);
                }

                return Task.CompletedTask;
            },
            capacity: 2,
            clock: clock,
            delayAsync: CreateAdvancingDelay(clock));

        await pipeline.EnqueueFrameAsync("stream-rate", 800, 600, "jpeg", [1], 1000, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await pipeline.EnqueueFrameAsync("stream-rate", 800, 600, "jpeg", [2], 1001, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(50));
        await pipeline.EnqueueFrameAsync("stream-rate", 800, 600, "jpeg", [3], 1002, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(25));
        await pipeline.EnqueueFrameAsync("stream-rate", 800, 600, "jpeg", [4], 1003, CancellationToken.None);

        await WaitForSignalAsync(
            twoFramesSent.Task,
            TimeSpan.FromSeconds(2),
            () => $"Expected two sent frames. Current ids={string.Join(", ", sentFrameIds)}");

        var metrics = pipeline.GetMetricsSnapshot();
        Assert.Equal([0L, 1L], sentFrameIds.ToArray());
        Assert.Equal(4, metrics.FramesCaptured);
        Assert.Equal(2, metrics.FramesQueued);
        Assert.Equal(2, metrics.FramesDropped);
        Assert.Equal(2, metrics.ChunksSent);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrameSendPipeline_DropsFrames_WhenCalledFasterThanMaxFps()
    {
        var sentFrameIds = new ConcurrentQueue<long>();
        var twoFramesSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 12, 2, 0, TimeSpan.Zero));

        await using var pipeline = ScreenShareFrameSendPipeline.CreateForTesting(
            sendChunkAsync: (chunk, _) =>
            {
                sentFrameIds.Enqueue(chunk.FrameId);
                if (sentFrameIds.Count >= 2)
                {
                    twoFramesSent.TrySetResult(true);
                }

                return Task.CompletedTask;
            },
            capacity: 2,
            clock: clock,
            maxFramesPerSecond: 5,
            delayAsync: CreateAdvancingDelay(clock));

        await pipeline.EnqueueFrameAsync("stream-fps-drop", 800, 600, "jpeg", [1], 1000, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        await pipeline.EnqueueFrameAsync("stream-fps-drop", 800, 600, "jpeg", [2], 1001, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(10));
        await pipeline.EnqueueFrameAsync("stream-fps-drop", 800, 600, "jpeg", [3], 1002, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        await pipeline.EnqueueFrameAsync("stream-fps-drop", 800, 600, "jpeg", [4], 1003, CancellationToken.None);

        await AwaitCompletesAsync(
            twoFramesSent.Task,
            TimeSpan.FromSeconds(2),
            "two allowed frames after deterministic fps drops");

        var metrics = pipeline.GetMetricsSnapshot();
        Assert.Equal([0L, 1L], sentFrameIds.ToArray());
        Assert.Equal(4, metrics.FramesCaptured);
        Assert.Equal(2, metrics.FramesQueued);
        Assert.Equal(2, metrics.FramesDropped);
        Assert.Equal(2, metrics.ChunksSent);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrameSendPipeline_AllowsFrames_WhenIntervalSatisfied()
    {
        var sentFrameIds = new ConcurrentQueue<long>();
        var sentCount = 0;
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 12, 3, 0, TimeSpan.Zero));

        await using var pipeline = ScreenShareFrameSendPipeline.CreateForTesting(
            sendChunkAsync: (chunk, _) =>
            {
                sentFrameIds.Enqueue(chunk.FrameId);
                Interlocked.Increment(ref sentCount);
                return Task.CompletedTask;
            },
            capacity: 2,
            clock: clock,
            maxFramesPerSecond: 5,
            delayAsync: CreateAdvancingDelay(clock));

        for (var i = 0; i < 5; i++)
        {
            await pipeline.EnqueueFrameAsync(
                "stream-fps-allow",
                800,
                600,
                "jpeg",
                [(byte)(i + 1)],
                2000 + i,
                CancellationToken.None);

            await AwaitConditionAsync(
                () => Volatile.Read(ref sentCount) == i + 1,
                TimeSpan.FromSeconds(2),
                $"allowed frame {i + 1} send at satisfied fps interval");

            if (i < 4)
            {
                clock.Advance(TimeSpan.FromMilliseconds(200));
            }
        }

        await AwaitConditionAsync(
            () => GetPendingFrameCount(pipeline) == 0 &&
                  pipeline.PendingSignalCount == 0 &&
                  pipeline.GetMetricsSnapshot().ChunksSent == 5,
            TimeSpan.FromSeconds(2),
            $"all allowed frames to drain. PendingFrames={GetPendingFrameCount(pipeline)}, PendingSignals={pipeline.PendingSignalCount}, SentCount={Volatile.Read(ref sentCount)}, ChunkMetrics={pipeline.GetMetricsSnapshot().ChunksSent}");

        var metrics = pipeline.GetMetricsSnapshot();
        Assert.Equal([0L, 1L, 2L, 3L, 4L], sentFrameIds.ToArray());
        Assert.Equal(5, metrics.FramesCaptured);
        Assert.Equal(5, metrics.FramesQueued);
        Assert.Equal(0, metrics.FramesDropped);
        Assert.Equal(5, metrics.ChunksSent);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrameSendPipeline_RapidInput_RespectsConfiguredFps_AndDoesNotSignalDroppedFrames()
    {
        const int maxFramesPerSecond = 5;
        const int totalFrames = 100;
        const int expectedQueuedFrames = 5;
        var sentFrameIds = new ConcurrentQueue<long>();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 12, 5, 0, TimeSpan.Zero));

        await using var sender = ScreenShareFrameSendPipeline.CreateForTesting(
            sendChunkAsync: (chunk, _) =>
            {
                sentFrameIds.Enqueue(chunk.FrameId);
                return Task.CompletedTask;
            },
            capacity: ScreenShareFrameSendPipeline.MaxBufferedFrames,
            clock: clock,
            maxFramesPerSecond: maxFramesPerSecond);

        for (var i = 0; i < totalFrames; i++)
        {
            await sender.EnqueueFrameAsync(
                "stream-throttle",
                800,
                600,
                "jpeg",
                [(byte)(i % 251)],
                2000 + i,
                CancellationToken.None);

            if (i < totalFrames - 1)
            {
                clock.Advance(TimeSpan.FromMilliseconds(10));
            }
        }

        await AwaitConditionAsync(
            () => GetPendingFrameCount(sender) == 0 && sender.PendingSignalCount == 0,
            TimeSpan.FromSeconds(2),
            $"throttled sender to drain queued work. PendingFrames={GetPendingFrameCount(sender)}, PendingSignals={sender.PendingSignalCount}, SentIds={string.Join(", ", sentFrameIds)}");

        var metrics = sender.GetMetricsSnapshot();
        Assert.Equal(totalFrames, metrics.FramesCaptured);
        Assert.Equal(expectedQueuedFrames, metrics.FramesQueued);
        Assert.InRange(metrics.FramesDropped, totalFrames - expectedQueuedFrames, totalFrames - 1);
        Assert.InRange(metrics.ChunksSent, 1, expectedQueuedFrames);
        Assert.Equal(metrics.ChunksSent, sentFrameIds.Count);
        Assert.Equal(expectedQueuedFrames, sender.WakeSignalsWritten);
        Assert.InRange(sender.WakeSignalsRead, 1, sender.WakeSignalsWritten);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrameSendPipeline_WhenConsumerBusy_DropsOldestQueuedFrame()
    {
        var sentFrameIds = new ConcurrentQueue<long>();
        var firstChunkStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstChunk = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var threeFramesSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 12, 10, 0, TimeSpan.Zero));
        var sendCount = 0;

        await using var pipeline = ScreenShareFrameSendPipeline.CreateForTesting(
            sendChunkAsync: async (chunk, _) =>
            {
                sentFrameIds.Enqueue(chunk.FrameId);
                var currentSendCount = Interlocked.Increment(ref sendCount);
                if (currentSendCount == 1)
                {
                    firstChunkStarted.TrySetResult(true);
                    await releaseFirstChunk.Task;
                }

                if (currentSendCount >= 3)
                {
                    threeFramesSent.TrySetResult(true);
                }
            },
            capacity: 2,
            clock: clock,
            delayAsync: CreateAdvancingDelay(clock));

        await pipeline.EnqueueFrameAsync("stream-b", 800, 600, "jpeg", [1, 2, 3], 1000, CancellationToken.None);
        await AwaitCompletesAsync(
            firstChunkStarted.Task,
            TimeSpan.FromSeconds(2),
            "first send start before wake backlog check");

        clock.Advance(TimeSpan.FromMilliseconds(125));
        await pipeline.EnqueueFrameAsync("stream-b", 800, 600, "jpeg", [4, 5, 6], 1001, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(125));
        await pipeline.EnqueueFrameAsync("stream-b", 800, 600, "jpeg", [7, 8, 9], 1002, CancellationToken.None);
        clock.Advance(TimeSpan.FromMilliseconds(125));
        await pipeline.EnqueueFrameAsync("stream-b", 800, 600, "jpeg", [10, 11, 12], 1003, CancellationToken.None);

        releaseFirstChunk.TrySetResult(true);

        await WaitForSignalAsync(
            threeFramesSent.Task,
            TimeSpan.FromSeconds(2),
            () => $"Expected three sent frames. Current ids={string.Join(", ", sentFrameIds)}");

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
        var threeFramesSent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 12, 30, 0, TimeSpan.Zero));
        var sendCount = 0;

        await using var pipeline = ScreenShareFrameSendPipeline.CreateForTesting(
            sendChunkAsync: async (chunk, _) =>
            {
                sentFrameIds.Enqueue(chunk.FrameId);
                var currentSendCount = Interlocked.Increment(ref sendCount);
                if (currentSendCount == 1)
                {
                    firstChunkStarted.TrySetResult(true);
                    await releaseFirstChunk.Task;
                }

                if (currentSendCount >= 3)
                {
                    threeFramesSent.TrySetResult(true);
                }
            },
            capacity: ScreenShareFrameSendPipeline.MaxBufferedFrames,
            clock: clock,
            delayAsync: CreateAdvancingDelay(clock));

        await pipeline.EnqueueFrameAsync("stream-pressure", 800, 600, "jpeg", [1], 1000, CancellationToken.None);
        await firstChunkStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        for (var i = 0; i < 99; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(125));
            await pipeline.EnqueueFrameAsync("stream-pressure", 800, 600, "jpeg", [(byte)(i % 251)], 1001 + i, CancellationToken.None);
        }

        Assert.True(GetPendingFrameCount(pipeline) <= ScreenShareFrameSendPipeline.MaxBufferedFrames);

        releaseFirstChunk.TrySetResult(true);

        await WaitForSignalAsync(
            threeFramesSent.Task,
            TimeSpan.FromSeconds(2),
            () => $"Expected bounded sender to flush three frames. Current ids={string.Join(", ", sentFrameIds)}");

        var metrics = pipeline.GetMetricsSnapshot();
        Assert.Equal(100, metrics.FramesCaptured);
        Assert.Equal(100, metrics.FramesQueued);
        Assert.True(metrics.FramesDropped >= 97);
        Assert.True(GetPendingFrameCount(pipeline) <= ScreenShareFrameSendPipeline.MaxBufferedFrames);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrameSendPipeline_Dispose_CancelsBlockedSend()
    {
        var sendEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pipeline = ScreenShareFrameSendPipeline.CreateForTesting(
            sendChunkAsync: async (_, ct) =>
            {
                sendEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });

        await pipeline.EnqueueFrameAsync(
            "stream-dispose-guard",
            800,
            600,
            "jpeg",
            [1],
            1000,
            CancellationToken.None);

        await AwaitCompletesAsync(
            sendEntered.Task,
            TimeSpan.FromSeconds(2),
            "blocked send entry before dispose cancellation");

        await AwaitCompletesAsync(
            pipeline.DisposeAsync().AsTask(),
            TimeSpan.FromSeconds(3),
            "pipeline dispose after blocked send");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrameSendPipeline_DisposeAsync_CancelsBlockedSend_AndCompletes()
    {
        using var unobserved = new UnobservedTaskExceptionRecorder();
        var firstChunkStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCanceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var pipeline = ScreenShareFrameSendPipeline.CreateForTesting(
            sendChunkAsync: async (_, ct) =>
            {
                firstChunkStarted.TrySetResult(true);
                using var registration = ct.Register(() => sendCanceled.TrySetCanceled(ct));
                await sendCanceled.Task;
            });

        await pipeline.EnqueueFrameAsync("stream-dispose", 800, 600, "jpeg", [1], 1000, CancellationToken.None);
        await AwaitCompletesAsync(
            firstChunkStarted.Task,
            TimeSpan.FromSeconds(2),
            "blocked send entry before dispose");

        await AwaitCompletesAsync(
            pipeline.DisposeAsync().AsTask(),
            TimeSpan.FromSeconds(2),
            "pipeline dispose");

        Assert.True(pipeline.IsSendLoopCompleted, "Expected send loop task to complete after pipeline dispose.");

        ForceFullCollection();
        Assert.Empty(unobserved.Exceptions);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrameSendPipeline_WhenFlooded_DoesNotAccumulateSignalBacklog()
    {
        var firstChunkStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstChunk = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var threeSendsObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 12, 40, 0, TimeSpan.Zero));
        var sendCount = 0;

        await using var pipeline = ScreenShareFrameSendPipeline.CreateForTesting(
            sendChunkAsync: async (_, ct) =>
            {
                var currentSendCount = Interlocked.Increment(ref sendCount);
                if (currentSendCount == 1)
                {
                    firstChunkStarted.TrySetResult(true);
                    await releaseFirstChunk.Task.WaitAsync(ct);
                }

                if (currentSendCount >= 3)
                {
                    threeSendsObserved.TrySetResult(true);
                }
            },
            capacity: ScreenShareFrameSendPipeline.MaxBufferedFrames,
            clock: clock,
            delayAsync: CreateAdvancingDelay(clock));

        await pipeline.EnqueueFrameAsync("stream-signals", 800, 600, "jpeg", [1], 1000, CancellationToken.None);
        await AwaitCompletesAsync(
            firstChunkStarted.Task,
            TimeSpan.FromSeconds(2),
            "first send start before bounded queue pressure");

        for (var i = 0; i < 1_000; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(125));
            await pipeline.EnqueueFrameAsync("stream-signals", 800, 600, "jpeg", [(byte)(i % 251)], 1001 + i, CancellationToken.None);
        }

        Assert.InRange(
            pipeline.PendingSignalCount,
            0,
            1);

        releaseFirstChunk.TrySetResult(true);

        await WaitForSignalAsync(
            threeSendsObserved.Task,
            TimeSpan.FromSeconds(2),
            () => $"Expected bounded sender to flush after releasing the first chunk. SendCount={sendCount}, SignalWriteAttempts={pipeline.SignalWriteAttempts}, PendingSignalCount={pipeline.PendingSignalCount}");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrameSendPipeline_WhenIdle_DoesNotConsumeSignals_OrInvokeSender()
    {
        var sendCount = 0;

        await using var pipeline = ScreenShareFrameSendPipeline.CreateForTesting(
            sendChunkAsync: (_, _) =>
            {
                Interlocked.Increment(ref sendCount);
                return Task.CompletedTask;
            });

        await Task.Yield();
        await Task.Yield();

        Assert.Equal(0, sendCount);
        Assert.Equal(0, pipeline.SignalWriteAttempts);
        Assert.Equal(0, pipeline.SignalReadCount);
        Assert.Equal(0, pipeline.PendingSignalCount);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrameSendPipeline_SlowSend_RapidFrames_DoesNotFreeze()
    {
        var semaphore = new SemaphoreSlim(0);
        var firstChunkStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var threeChunksStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 12, 50, 0, TimeSpan.Zero));
        var chunksStarted = 0;

        await using var pipeline = ScreenShareFrameSendPipeline.CreateForTesting(
            sendChunkAsync: async (_, ct) =>
            {
                var current = Interlocked.Increment(ref chunksStarted);
                if (current == 1)
                {
                    firstChunkStarted.TrySetResult(true);
                }

                if (current >= 3)
                {
                    threeChunksStarted.TrySetResult(true);
                }

                await semaphore.WaitAsync(ct);
            },
            clock: clock,
            maxFramesPerSecond: ScreenShareFrameSendPipeline.MaxFramesPerSecond,
            delayAsync: CreateAdvancingDelay(clock));

        await pipeline.EnqueueFrameAsync(
            "stream-slow-send",
            800,
            600,
            "jpeg",
            [0],
            1000,
            CancellationToken.None);

        await AwaitCompletesAsync(
            firstChunkStarted.Task,
            TimeSpan.FromSeconds(2),
            "first slow send entry");

        for (var i = 1; i < 200; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(125));
            await pipeline.EnqueueFrameAsync(
                "stream-slow-send",
                800,
                600,
                "jpeg",
                [(byte)(i % 251)],
                1000 + i,
                CancellationToken.None);
        }

        semaphore.Release(3);

        await AwaitCompletesAsync(
            threeChunksStarted.Task,
            TimeSpan.FromSeconds(2),
            "slow send progress under rapid frame load");

        await AwaitCompletesAsync(
            pipeline.DisposeAsync().AsTask(),
            TimeSpan.FromSeconds(3),
            "pipeline dispose after slow send rapid frame load");

        Assert.True(
            chunksStarted > 0,
            $"Expected at least one chunk send to start under rapid load. ChunksStarted={chunksStarted}.");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ScreenShareFrameSendPipeline_CreateDisposeCycles_CancelBlockedSend_AndRemainStable()
    {
        const int cycleCount = 100;

        ForceFullCollection();
        var memoryBeforeBytes = GC.GetTotalMemory(forceFullCollection: true);
        var totalCanceledSends = 0L;

        for (var cycle = 1; cycle <= cycleCount; cycle++)
        {
            var probe = new ScreenShareSendProbe(startBlocked: true, respectCancellation: true);
            await using var pipeline = ScreenShareFrameSendPipeline.CreateForTesting(
                sendChunkAsync: probe.SendChunkAsync);

            await pipeline.EnqueueFrameAsync(
                "stream-cycle",
                800,
                600,
                "jpeg",
                new byte[] { 1, 2, 3 },
                1000 + cycle,
                CancellationToken.None);

            await AwaitCompletesAsync(
                probe.FirstSendStarted,
                TimeSpan.FromSeconds(2),
                $"cycle {cycle}: blocked send entry");
            await AwaitCompletesAsync(
                pipeline.DisposeAsync().AsTask(),
                TimeSpan.FromSeconds(2),
                $"cycle {cycle}: pipeline dispose");

            Assert.True(
                probe.CanceledSendCount >= 1,
                $"Expected blocked send cancellation in cycle {cycle}. Canceled={probe.CanceledSendCount}, Payloads={probe.PayloadsSent}, Chunks={probe.ChunksSent}.");
            Assert.Equal(0, probe.PayloadsSent);
            totalCanceledSends += probe.CanceledSendCount;
        }

        Assert.Equal(cycleCount, totalCanceledSends);

        ForceFullCollection();
        var memoryAfterBytes = GC.GetTotalMemory(forceFullCollection: true);
        var memoryDeltaBytes = memoryAfterBytes - memoryBeforeBytes;
        Assert.True(
            memoryDeltaBytes <= 4 * 1024 * 1024,
            $"Expected bounded memory growth after pipeline cycles. DeltaBytes={memoryDeltaBytes}, Cycles={cycleCount}, CanceledSends={totalCanceledSends}.");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task FramePacing_IsStable_UnderBurstArrival()
    {
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 13, 0, 0, TimeSpan.Zero));
        var sendStartedAt = new ConcurrentQueue<DateTimeOffset>();
        var firstChunkStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstChunk = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var threeChunksStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendCount = 0;

        await using var pipeline = ScreenShareFrameSendPipeline.CreateForTesting(
            sendChunkAsync: async (_, ct) =>
            {
                sendStartedAt.Enqueue(clock.UtcNow);
                var currentSendCount = Interlocked.Increment(ref sendCount);
                if (currentSendCount == 1)
                {
                    firstChunkStarted.TrySetResult(true);
                    await releaseFirstChunk.Task.WaitAsync(ct);
                }

                if (currentSendCount >= 3)
                {
                    threeChunksStarted.TrySetResult(true);
                }
            },
            capacity: ScreenShareFrameSendPipeline.MaxBufferedFrames,
            clock: clock,
            maxFramesPerSecond: ScreenShareFrameSendPipeline.MaxFramesPerSecond,
            delayAsync: CreateAdvancingDelay(clock));

        await pipeline.EnqueueFrameAsync("stream-pacing", 800, 600, "jpeg", [1], 1000, CancellationToken.None);
        await AwaitCompletesAsync(
            firstChunkStarted.Task,
            TimeSpan.FromSeconds(2),
            "first paced send entry");

        for (var i = 1; i < 50; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(125));
            await pipeline.EnqueueFrameAsync(
                "stream-pacing",
                800,
                600,
                "jpeg",
                [(byte)(i % 251)],
                1000 + i,
                CancellationToken.None);
        }

        releaseFirstChunk.TrySetResult(true);

        await AwaitCompletesAsync(
            threeChunksStarted.Task,
            TimeSpan.FromSeconds(2),
            "paced sends after burst arrival");

        var sendTimes = sendStartedAt.ToArray();
        Assert.True(sendTimes.Length >= 3, $"Expected at least 3 sends, but saw {sendTimes.Length}.");
        var secondToThirdGap = sendTimes[2] - sendTimes[1];
        Assert.True(
            secondToThirdGap >= TimeSpan.FromMilliseconds(125),
            $"Expected second-to-third send gap >= 125ms, but was {secondToThirdGap.TotalMilliseconds:F1}ms. Sends={string.Join(", ", sendTimes)}");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task SendPipeline_Processes100Frames_UnderTimeBudget()
    {
        const int frameCount = 100;
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 13, 30, 0, TimeSpan.Zero));
        var sentCount = 0;

        await using var pipeline = ScreenShareFrameSendPipeline.CreateForTesting(
            sendChunkAsync: (_, _) =>
            {
                Interlocked.Increment(ref sentCount);
                return Task.CompletedTask;
            },
            capacity: ScreenShareFrameSendPipeline.MaxBufferedFrames,
            clock: clock,
            maxFramesPerSecond: ScreenShareFrameSendPipeline.MaxFramesPerSecond,
            delayAsync: CreateAdvancingDelay(clock));

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < frameCount; i++)
        {
            await pipeline.EnqueueFrameAsync(
                "stream-throughput",
                1280,
                720,
                "jpeg",
                [(byte)(i % 251)],
                6000 + i,
                CancellationToken.None);

            await AwaitConditionAsync(
                () => Volatile.Read(ref sentCount) >= i + 1,
                TimeSpan.FromSeconds(2),
                $"frame {i + 1} to send in throughput budget test");

            if (i < frameCount - 1)
            {
                clock.Advance(TimeSpan.FromMilliseconds(125));
            }
        }

        stopwatch.Stop();
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"Expected 100 no-op frames to process quickly under fake-clock pacing, but it took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    }

    private static async Task WaitForSignalAsync(
        Task signal,
        TimeSpan timeout,
        Func<string> failureMessage)
    {
        try
        {
            await signal.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            Assert.Fail(failureMessage());
        }
    }

    private static async Task AwaitCompletesAsync(Task operation, TimeSpan timeout, string phase)
    {
        using var timeoutCts = new CancellationTokenSource();
        var timeoutTask = Task.Delay(timeout, timeoutCts.Token);
        var completed = await Task.WhenAny(operation, timeoutTask);
        if (!ReferenceEquals(completed, operation))
        {
            Assert.Fail($"Timed out waiting for {phase} after {timeout.TotalSeconds:N1}s.");
        }

        timeoutCts.Cancel();
        await operation;
    }

    private static async Task AwaitConditionAsync(Func<bool> condition, TimeSpan timeout, string phase)
    {
        using var timeoutCts = new CancellationTokenSource(timeout);
        while (!condition())
        {
            if (timeoutCts.IsCancellationRequested)
            {
                Assert.Fail($"Timed out waiting for {phase} after {timeout.TotalSeconds:N1}s.");
            }

            await Task.Yield();
        }
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

    private static Func<TimeSpan, CancellationToken, Task> CreateAdvancingDelay(FakeScreenShareClock clock)
    {
        return (delay, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            clock.Advance(delay);
            return Task.CompletedTask;
        };
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

    private static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private sealed class UnobservedTaskExceptionRecorder : IDisposable
    {
        private readonly ConcurrentQueue<Exception> exceptions = new();

        public UnobservedTaskExceptionRecorder()
        {
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        public Exception[] Exceptions => exceptions.ToArray();

        public void Dispose()
        {
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
        }

        private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
        {
            exceptions.Enqueue(e.Exception);
            e.SetObserved();
        }
    }
}
