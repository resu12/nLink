using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NLink.App.ViewModels;
using NLink.Core.ScreenShare;
using NLink.SmokeTests.Fakes;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
public sealed class ScreenSharePerformanceTests : IClassFixture<ScreenShareCoordinatorFixture>
{
    private readonly ScreenShareCoordinatorFixture fixture;

    public ScreenSharePerformanceTests(ScreenShareCoordinatorFixture fixture)
    {
        this.fixture = fixture;
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
}
