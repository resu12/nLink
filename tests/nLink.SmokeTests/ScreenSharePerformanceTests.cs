using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NLink.App.ViewModels;
using NLink.Core.ScreenShare;
using NLink.SmokeTests.Fakes;
using Xunit.Abstractions;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenSharePerformanceTests : IClassFixture<ScreenShareCoordinatorFixture>
{
    private readonly ScreenShareCoordinatorFixture fixture;
    private readonly ITestOutputHelper output;

    public ScreenSharePerformanceTests(ScreenShareCoordinatorFixture fixture, ITestOutputHelper output)
    {
        this.fixture = fixture;
        this.output = output;
    }

    [Fact]
    [Trait("Category", "Performance")]
    public async Task ScreenSharePipeline_ShortStreamingPressure_StaysBounded_AndProcessesFrames()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var memoryBeforeBytes = GC.GetTotalMemory(forceFullCollection: true);
            var fakeCapture = new FakeScreenCaptureSource();
            var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 12, 0, 0, TimeSpan.Zero));
            var reassembler = new ScreenShareFrameReassembler();
            var firstChunkStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstChunk = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstFrameCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var firstFrameDecoded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var sentChunkCount = 0;

            await using var pipeline = new ScreenShareFrameSendPipeline(
                sendChunkAsync: async (chunk, _) =>
                {
                    if (Interlocked.Increment(ref sentChunkCount) == 1)
                    {
                        firstChunkStarted.TrySetResult(true);
                        await releaseFirstChunk.Task;
                    }

                    reassembler.OnChunk(chunk);
                },
                capacity: ScreenShareFrameSendPipeline.MaxBufferedFrames,
                clock: clock);

            using var viewer = new ScreenShareViewerViewModel(
                decodeFrame: _ =>
                {
                    firstFrameDecoded.TrySetResult(true);
                    return CreateBitmap(2, 1);
                },
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                });

            reassembler.FrameReady += (_, frame) =>
            {
                firstFrameCompleted.TrySetResult(true);
                viewer.OnJpegFrame(frame.EncodedFrameBytes);
            };

            EventHandler<NLink.App.Services.ScreenCapture.ScreenCaptureFrameEventArgs>? onFrameArrived = null;
            onFrameArrived = (_, frame) =>
            {
                _ = pipeline.EnqueueFrameAsync(
                    sessionId: "perf-stream",
                    width: frame.Width,
                    height: frame.Height,
                    encoding: frame.Encoding,
                    encodedFrameBytes: frame.EncodedFrameData,
                    timestampUnixMilliseconds: clock.UtcNow.ToUnixTimeMilliseconds(),
                    cancellationToken: CancellationToken.None);
            };

            fakeCapture.FrameArrived += onFrameArrived;
            await fakeCapture.StartAsync(CancellationToken.None);

            fakeCapture.RaiseFrame(640, 360, new byte[] { 1, 2, 3 }, "jpeg");
            await firstChunkStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            for (var i = 1; i < 24; i++)
            {
                clock.Advance(TimeSpan.FromMilliseconds(125));
                fakeCapture.RaiseFrame(640 + (i % 2), 360 + (i % 2), new byte[] { (byte)i, (byte)(i + 1), (byte)(i + 2) }, "jpeg");
            }

            releaseFirstChunk.TrySetResult(true);

            await WaitForSignalAsync(
                Task.WhenAll(firstFrameCompleted.Task, firstFrameDecoded.Task),
                TimeSpan.FromSeconds(2),
                () =>
                {
                    var sender = pipeline.GetMetricsSnapshot();
                    var receiver = reassembler.GetMetricsSnapshot();
                    var viewerMetrics = viewer.GetMetricsSnapshot();
                    return $"Expected screenshare progress under pressure. sender={sender}; receiver={receiver}; viewer={viewerMetrics}";
                });

            fakeCapture.FrameArrived -= onFrameArrived;
            await fakeCapture.StopAsync();
            viewer.Clear();
            await WaitForSignalAsync(
                WaitUntilAsync(() => viewer.IsIdleForDiagnostics, TimeSpan.FromSeconds(2)),
                TimeSpan.FromSeconds(2),
                () => "Viewer did not become idle after pressure run.");

            var senderMetrics = pipeline.GetMetricsSnapshot();
            var receiverMetrics = reassembler.GetMetricsSnapshot();
            var viewerStats = viewer.GetMetricsSnapshot();

            Assert.Equal(24, senderMetrics.FramesCaptured);
            Assert.True(senderMetrics.FramesDropped > 0);
            Assert.True(senderMetrics.FramesQueued >= 1);
            Assert.True(senderMetrics.ChunksSent >= 1);
            Assert.True(receiverMetrics.FramesCompleted >= 1);
            Assert.Equal(0, receiverMetrics.FramesRejectedOversize);
            Assert.Equal(0, viewerStats.DecodeErrors);

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var memoryAfterBytes = GC.GetTotalMemory(forceFullCollection: true);
            var memoryDeltaBytes = memoryAfterBytes - memoryBeforeBytes;
            Assert.True(
                memoryDeltaBytes < 8 * 1024 * 1024,
                $"Expected bounded memory growth during short pressure run. DeltaBytes={memoryDeltaBytes}; sender={senderMetrics}; receiver={receiverMetrics}; viewer={viewerStats}");

            return true;
        }, default);
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

    [Fact]
    [Trait("Category", "Performance")]
    public async Task ScreenSharePipeline_SimulatedTwoMinuteShare_NoHang_NoRunawayMemory_AndContinuousDecode()
    {
        await fixture.Session.Dispatch(async () =>
        {
            const int totalFrames = 240; // 2 minutes simulated at 2 FPS
            const int sampleStride = 24;
            const int minimumCompletedFrames = totalFrames / 2;
            const int minimumDecodedFrames = 1;
            const long maxTotalMemoryDeltaBytes = 12 * 1024 * 1024;
            const long maxMonotonicGrowthDeltaBytes = 8 * 1024 * 1024;

            var fakeCapture = new FakeScreenCaptureSource();
            var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 4, 10, 0, 0, TimeSpan.Zero));
            var reassembler = new ScreenShareFrameReassembler();
            var decodeProgress = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var appliedFrames = 0;
            var memorySamples = new List<long>();

            await using var pipeline = ScreenShareFrameSendPipeline.CreateForTesting(
                sendChunkAsync: (chunk, _) =>
                {
                    reassembler.OnChunk(chunk);
                    return Task.CompletedTask;
                },
                capacity: ScreenShareFrameSendPipeline.MaxBufferedFrames,
                clock: clock,
                maxFramesPerSecond: ScreenShareFrameSendPipeline.MaxFramesPerSecond,
                delayAsync: CreateAdvancingDelay(clock));

            using var viewer = new ScreenShareViewerViewModel(
                decodeFrame: _ =>
                {
                    Interlocked.Increment(ref appliedFrames);
                    decodeProgress.TrySetResult(true);
                    return CreateBitmap(2, 1);
                },
                postToUiAsync: action =>
                {
                    action();
                    return Task.CompletedTask;
                });

            reassembler.FrameReady += (_, frame) => viewer.OnJpegFrame(frame.EncodedFrameBytes);

            EventHandler<NLink.App.Services.ScreenCapture.ScreenCaptureFrameEventArgs>? onFrameArrived = null;
            onFrameArrived = (_, frame) =>
            {
                _ = pipeline.EnqueueFrameAsync(
                    sessionId: "perf-two-minute",
                    width: frame.Width,
                    height: frame.Height,
                    encoding: frame.Encoding,
                    encodedFrameBytes: frame.EncodedFrameData,
                    timestampUnixMilliseconds: clock.UtcNow.ToUnixTimeMilliseconds(),
                    cancellationToken: CancellationToken.None);
            };

            fakeCapture.FrameArrived += onFrameArrived;
            await fakeCapture.StartAsync(CancellationToken.None);

            memorySamples.Add(GC.GetTotalMemory(forceFullCollection: true));

            for (var frameIndex = 0; frameIndex < totalFrames; frameIndex++)
            {
                fakeCapture.RaiseFrame(
                    640 + (frameIndex % 2),
                    360 + (frameIndex % 2),
                    new byte[] { (byte)(frameIndex % 251), 1, 2, 3 },
                    "jpeg");

                clock.Advance(TimeSpan.FromMilliseconds(500));
                var expectedChunksSent = frameIndex + 1;
                await WaitUntilAsync(
                    () => pipeline.GetMetricsSnapshot().ChunksSent >= expectedChunksSent,
                    TimeSpan.FromSeconds(2));

                if ((frameIndex + 1) % sampleStride == 0)
                {
                    memorySamples.Add(GC.GetTotalMemory(forceFullCollection: true));
                }
            }

            await WaitForSignalAsync(
                decodeProgress.Task,
                TimeSpan.FromSeconds(2),
                () => "Expected at least one decoded frame during two-minute simulation.");

            await WaitUntilAsync(
                () =>
                {
                    var receiverMetrics = reassembler.GetMetricsSnapshot();
                    var viewerProgress = viewer.GetMetricsSnapshot();
                    return receiverMetrics.FramesCompleted >= minimumCompletedFrames &&
                        viewerProgress.FramesDecoded >= minimumDecodedFrames;
                },
                TimeSpan.FromSeconds(2));

            fakeCapture.FrameArrived -= onFrameArrived;
            await fakeCapture.StopAsync();
            viewer.Clear();
            await WaitForSignalAsync(
                WaitUntilAsync(() => viewer.IsIdleForDiagnostics, TimeSpan.FromSeconds(2)),
                TimeSpan.FromSeconds(2),
                () => "Viewer did not become idle after simulated two-minute run.");

            var sender = pipeline.GetMetricsSnapshot();
            var receiver = reassembler.GetMetricsSnapshot();
            var viewerMetrics = viewer.GetMetricsSnapshot();
            var memoryMin = memorySamples.Min();
            var memoryMax = memorySamples.Max();
            var memoryDelta = memorySamples[^1] - memorySamples[0];
            var monotonicGrowth = IsStrictlyMonotonicIncrease(memorySamples);

            Assert.Equal(totalFrames, sender.FramesCaptured);
            Assert.True(sender.FramesQueued >= minimumCompletedFrames);
            Assert.True(sender.FramesDropped <= totalFrames - minimumCompletedFrames);
            Assert.True(sender.ChunksSent >= minimumCompletedFrames);
            Assert.True(receiver.FramesCompleted >= minimumCompletedFrames);
            Assert.Equal(0, receiver.FramesRejectedOversize);
            Assert.True(viewerMetrics.FramesDecoded >= minimumDecodedFrames);
            Assert.Equal(0, viewerMetrics.DecodeErrors);
            Assert.True(
                memoryMax - memoryMin <= maxTotalMemoryDeltaBytes,
                $"Expected bounded memory spread. Min={memoryMin}, Max={memoryMax}, Spread={memoryMax - memoryMin}.");
            Assert.False(
                monotonicGrowth && memoryDelta > maxMonotonicGrowthDeltaBytes,
                $"Expected no runaway monotonic memory growth. Samples={string.Join(", ", memorySamples)}");

            output.WriteLine(
                $"Sim2m sender={sender}; receiver={receiver}; viewer={viewerMetrics}; memDelta={memoryDelta}; memSpread={memoryMax - memoryMin}; samples={string.Join(',', memorySamples)}");

            return true;
        }, default);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Yield();
        }

        Assert.True(predicate(), $"Condition not met within {timeout.TotalSeconds:N1}s.");
    }

    private static bool IsStrictlyMonotonicIncrease(IReadOnlyList<long> samples)
    {
        for (var i = 1; i < samples.Count; i++)
        {
            if (samples[i] <= samples[i - 1])
            {
                return false;
            }
        }

        return true;
    }

    private static Bitmap CreateBitmap(int width, int height)
    {
        var writeable = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var locked = writeable.Lock())
        {
            var totalBytes = width * height * 4;
            var pixels = new byte[totalBytes];
            Marshal.Copy(pixels, 0, locked.Address, totalBytes);
        }

        return writeable;
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
        return (delay, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (delay > TimeSpan.Zero)
            {
                clock.Advance(delay);
            }

            return Task.CompletedTask;
        };
    }
}
