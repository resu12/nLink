using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using NLink.App.Services.ScreenCapture;
using NLink.Core.ScreenShare;

namespace NLink.SmokeTests;

[Collection(AvaloniaHeadlessUiCollection.Name)]
[Trait("Area", "ScreenShare")]
public sealed class LatestEncodedFrameDecodeWorkerTests : IClassFixture<ScreenShareCoordinatorFixture>
{
    private readonly ScreenShareCoordinatorFixture fixture;

    public LatestEncodedFrameDecodeWorkerTests(ScreenShareCoordinatorFixture fixture)
    {
        this.fixture = fixture;
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task LatestEncodedFrameDecodeWorker_CoalescesToLatestFrame()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var generation = 0;
            var decodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDecode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Bitmap? currentFrame = null;
            var decodeCalls = 0;

            using var worker = new LatestEncodedFrameDecodeWorker(
                decodeFrame: request =>
                {
                    Assert.Equal("jpeg", request.Encoding);
                    var call = Interlocked.Increment(ref decodeCalls);
                    if (call == 1)
                    {
                        decodeStarted.TrySetResult(true);
                        WaitForSignal(releaseDecode.Task, TimeSpan.FromSeconds(2));
                    }

                    return CreateBitmap(request.EncodedFrameBytes.Span[0], 1);
                },
                onFrameDecodedAsync: frame =>
                {
                    Assert.Equal("jpeg", frame.Request.Encoding);
                    var previous = currentFrame;
                    currentFrame = frame.Bitmap;
                    previous?.Dispose();
                    return Task.CompletedTask;
                },
                onDecodeFailedAsync: _ => Task.CompletedTask,
                shouldStop: static () => false,
                getGeneration: () => Volatile.Read(ref generation));

            try
            {
                worker.EnqueueCopied("jpeg", new byte[] { 1 });
                await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

                for (byte i = 2; i <= 5; i++)
                {
                    worker.EnqueueCopied("jpeg", new byte[] { i });
                }

                releaseDecode.TrySetResult(true);
                await WaitUntilAsync(
                    () =>
                    {
                        var latest = currentFrame;
                        if (latest is null)
                        {
                            return false;
                        }

                        try
                        {
                            return latest.PixelSize.Width == 5 && worker.IsIdle;
                        }
                        catch (ObjectDisposedException)
                        {
                            return false;
                        }
                        catch (NullReferenceException)
                        {
                            return false;
                        }
                    },
                    TimeSpan.FromSeconds(2));

                Assert.Equal(2, decodeCalls);
                Assert.Equal(2, worker.FramesDecoded);
                Assert.Equal(1, worker.MaxDecodeTasksActive);
                Assert.Equal(0, worker.DecodeTasksActive);
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task LatestEncodedFrameDecodeWorker_GenerationChange_SuppressesStaleApply()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var generation = 0;
            var decodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDecode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Bitmap? currentFrame = null;
            var droppedAfterDecode = new List<(long FrameId, string Reason)>();

            using var worker = new LatestEncodedFrameDecodeWorker(
                decodeFrame: _ =>
                {
                    decodeStarted.TrySetResult(true);
                    WaitForSignal(releaseDecode.Task, TimeSpan.FromSeconds(2));
                    return CreateTinyBitmap();
                },
                onFrameDecodedAsync: frame =>
                {
                    currentFrame = frame.Bitmap;
                    return Task.CompletedTask;
                },
                onDecodeFailedAsync: _ => Task.CompletedTask,
                shouldStop: static () => false,
                getGeneration: () => Volatile.Read(ref generation),
                onFrameDroppedAfterDecode: (request, reason) => droppedAfterDecode.Add((request.FrameId, reason)));

            worker.EnqueueCopied("jpeg", new byte[] { 1 }, frameId: 1, sessionId: "generation-change");
            await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            Interlocked.Increment(ref generation);
            worker.ClearPending();
            releaseDecode.TrySetResult(true);

            await worker.AwaitIdleAsync();
            Assert.Null(currentFrame);
            Assert.Equal(0, worker.DecodeTasksActive);
            Assert.Contains(droppedAfterDecode, static item => item.FrameId == 1 && string.Equals(item.Reason, "generation_changed", StringComparison.Ordinal));
            var metrics = worker.GetMetricsSnapshot();
            Assert.Equal(1, metrics.DecodeWorkerDropGenerationCount);
            Assert.Equal(1, metrics.FramesDroppedAfterDecode);

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task LatestEncodedFrameDecodeWorker_DecodeFailure_DoesNotKillFutureFrames()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var generation = 0;
            Bitmap? currentFrame = null;
            var decodeCalls = 0;
            var failures = 0;

            using var worker = new LatestEncodedFrameDecodeWorker(
                decodeFrame: request =>
                {
                    Assert.Equal("jpeg", request.Encoding);
                    var next = Interlocked.Increment(ref decodeCalls);
                    if (next == 1)
                    {
                        throw new InvalidDataException("invalid frame");
                    }

                    return CreateBitmap(request.EncodedFrameBytes.Span[0], 1);
                },
                onFrameDecodedAsync: frame =>
                {
                    currentFrame?.Dispose();
                    currentFrame = frame.Bitmap;
                    return Task.CompletedTask;
                },
                onDecodeFailedAsync: failure =>
                {
                    Assert.Equal("jpeg", failure.Request.Encoding);
                    Interlocked.Increment(ref failures);
                    return Task.CompletedTask;
                },
                shouldStop: static () => false,
                getGeneration: () => Volatile.Read(ref generation));

            try
            {
                worker.EnqueueCopied("jpeg", new byte[] { 1 });
                await WaitUntilAsync(() => worker.IsIdle, TimeSpan.FromSeconds(2));

                worker.EnqueueCopied("jpeg", new byte[] { 7 });
                await WaitUntilAsync(
                    () => currentFrame is Bitmap frame && frame.PixelSize.Width == 7 && worker.IsIdle,
                    TimeSpan.FromSeconds(2));

                Assert.Equal(2, decodeCalls);
                Assert.Equal(1, failures);
                Assert.Equal(1, worker.FramesDecoded);
            }
            finally
            {
                currentFrame?.Dispose();
            }

            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task LatestEncodedFrameDecodeWorker_DecoupledApply_AllowsDecodeBurstWhileApplyIsBlocked()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var generation = 0;
            var firstApplyStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstApply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var appliedFrames = new List<int>();
            var droppedAfterDecode = new List<(long FrameId, string Reason)>();

            using var worker = new LatestEncodedFrameDecodeWorker(
                decodeFrame: request => CreateBitmap(request.EncodedFrameBytes.Span[0], 1),
                onFrameDecodedAsync: async frame =>
                {
                    var width = frame.Bitmap.PixelSize.Width;
                    if (!firstApplyStarted.Task.IsCompleted)
                    {
                        firstApplyStarted.TrySetResult(true);
                        await releaseFirstApply.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    }

                    appliedFrames.Add(width);
                    frame.Bitmap.Dispose();
                },
                onDecodeFailedAsync: _ => Task.CompletedTask,
                shouldStop: static () => false,
                getGeneration: () => Volatile.Read(ref generation),
                options: new LatestEncodedFrameDecodeWorkerOptions(
                    MaxPendingEncodedFrames: 3,
                    MaxPendingEncodedFrameAgeMs: 200,
                    DecoupleApplyFromDecode: true,
                    MaxPendingDecodedFrames: 1),
                onFrameDroppedAfterDecode: (request, reason) => droppedAfterDecode.Add((request.FrameId, reason)));

            worker.EnqueueCopied("jpeg", new byte[] { 1 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            await firstApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            worker.EnqueueCopied("jpeg", new byte[] { 2 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), frameId: 2, sessionId: "decoded-fifo");
            worker.EnqueueCopied("jpeg", new byte[] { 3 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), frameId: 3, sessionId: "decoded-fifo");
            worker.EnqueueCopied("jpeg", new byte[] { 4 }, capturedTsUtcMs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), frameId: 4, sessionId: "decoded-fifo");

            await WaitUntilAsync(
                () => worker.GetMetricsSnapshot().FramesDecoded >= 2,
                TimeSpan.FromSeconds(2));

            releaseFirstApply.TrySetResult(true);
            await WaitUntilAsync(() => worker.IsIdle, TimeSpan.FromSeconds(2));

            var metrics = worker.GetMetricsSnapshot();
            Assert.True(metrics.FramesDecoded >= 2);
            Assert.True(metrics.FramesDroppedAfterDecode >= 1);
            Assert.Equal(new[] { 1, 2 }, appliedFrames.ToArray());
            Assert.Contains(droppedAfterDecode, static item => item.FrameId == 3 && string.Equals(item.Reason, "decoded_apply_queue_overflow", StringComparison.Ordinal));
            Assert.Equal(1, worker.MaxDecodeTasksActive);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task LatestEncodedFrameDecodeWorker_DecoupledApply_ReservedApplyFlag_DoesNotDisplaceOrdinaryPendingApplyFrame()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var generation = 0;
            var firstApplyStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirstApply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var appliedFrames = new List<long>();
            var droppedAfterDecode = new List<(long FrameId, string Reason)>();

            using var worker = new LatestEncodedFrameDecodeWorker(
                decodeFrame: request => CreateBitmap(request.EncodedFrameBytes.Span[0], 1),
                onFrameDecodedAsync: async frame =>
                {
                    if (!firstApplyStarted.Task.IsCompleted)
                    {
                        firstApplyStarted.TrySetResult(true);
                        await releaseFirstApply.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    }

                    appliedFrames.Add(frame.Request.FrameId);
                    frame.Bitmap.Dispose();
                },
                onDecodeFailedAsync: _ => Task.CompletedTask,
                shouldStop: static () => false,
                getGeneration: () => Volatile.Read(ref generation),
                options: new LatestEncodedFrameDecodeWorkerOptions(
                    MaxPendingEncodedFrames: 3,
                    MaxPendingEncodedFrameAgeMs: 200,
                    DecoupleApplyFromDecode: true,
                    MaxPendingDecodedFrames: 1),
                onFrameDroppedAfterDecode: (request, reason) => droppedAfterDecode.Add((request.FrameId, reason)));

            worker.EnqueueCopied("jpeg", new byte[] { 1 }, frameId: 1, sessionId: "reserved-apply");
            await firstApplyStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            worker.EnqueueCopied("jpeg", new byte[] { 2 }, frameId: 2, sessionId: "reserved-apply");
            worker.EnqueueCopied(
                "jpeg",
                new byte[] { 3 },
                frameId: 3,
                sessionId: "reserved-apply",
                requiresReservedApply: true,
                bypassesAgeBudget: true,
                recoveryDeliveryClass: ScreenShareRecoveryDeliveryClass.ProtectedFollower);

            releaseFirstApply.TrySetResult(true);
            await WaitUntilAsync(() => worker.IsIdle, TimeSpan.FromSeconds(2));

            Assert.Equal(new long[] { 1, 2 }, appliedFrames.ToArray());
            Assert.Contains(droppedAfterDecode, static item => item.FrameId == 3 && string.Equals(item.Reason, "decoded_apply_queue_overflow", StringComparison.Ordinal));
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task LatestEncodedFrameDecodeWorker_DroppedBeforeDecode_CallbackReportsExactFrameId()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var generation = 0;
            var decodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseDecode = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var droppedFrames = new List<(long FrameId, string Reason)>();

            using var worker = new LatestEncodedFrameDecodeWorker(
                decodeFrame: request =>
                {
                    if (request.FrameId == 1)
                    {
                        decodeStarted.TrySetResult(true);
                        WaitForSignal(releaseDecode.Task, TimeSpan.FromSeconds(2));
                    }

                    return CreateBitmap(request.EncodedFrameBytes.Span[0], 1);
                },
                onFrameDecodedAsync: frame =>
                {
                    frame.Bitmap.Dispose();
                    return Task.CompletedTask;
                },
                onDecodeFailedAsync: _ => Task.CompletedTask,
                shouldStop: static () => false,
                getGeneration: () => Volatile.Read(ref generation),
                options: new LatestEncodedFrameDecodeWorkerOptions(
                    MaxPendingEncodedFrames: 4,
                    MaxPendingEncodedFrameAgeMs: 300,
                    DecoupleApplyFromDecode: true,
                    MaxPendingDecodedFrames: 1),
                onFrameDroppedBeforeDecode: (request, reason) => droppedFrames.Add((request.FrameId, reason)));

            worker.EnqueueCopied("jpeg", new byte[] { 1 }, frameId: 1, sessionId: "worker-drop");
            await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

            worker.EnqueueCopied("jpeg", new byte[] { 2 }, frameId: 2, sessionId: "worker-drop");
            worker.EnqueueCopied("jpeg", new byte[] { 3 }, frameId: 3, sessionId: "worker-drop");
            worker.EnqueueCopied("jpeg", new byte[] { 4 }, frameId: 4, sessionId: "worker-drop");
            worker.EnqueueCopied("jpeg", new byte[] { 5 }, frameId: 5, sessionId: "worker-drop");
            worker.EnqueueCopied("jpeg", new byte[] { 6 }, frameId: 6, sessionId: "worker-drop");

            releaseDecode.TrySetResult(true);
            await WaitUntilAsync(() => worker.IsIdle, TimeSpan.FromSeconds(2));

            Assert.Contains(droppedFrames, static item => item.FrameId == 2 && string.Equals(item.Reason, "queue_overflow", StringComparison.Ordinal));
            var metrics = worker.GetMetricsSnapshot();
            Assert.True(metrics.FramesDroppedBeforeDecode >= 1);
            Assert.True(metrics.DecodeWorkerDropQueueOverflowCount >= 1);
            Assert.True(metrics.MaxPendingEncodedDepth >= 4);
            Assert.True(metrics.AverageEnqueueToDropMs >= 0);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task LatestEncodedFrameDecodeWorker_AgeBudgetDrop_IsAttributedSeparately()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var generation = 0;
            long nowUtcMs = 1_000;
            var droppedFrames = new List<(long FrameId, string Reason)>();

            using var worker = new LatestEncodedFrameDecodeWorker(
                decodeFrame: request => CreateBitmap(request.EncodedFrameBytes.Span[0], 1),
                onFrameDecodedAsync: frame =>
                {
                    frame.Bitmap.Dispose();
                    return Task.CompletedTask;
                },
                onDecodeFailedAsync: _ => Task.CompletedTask,
                shouldStop: static () => false,
                getGeneration: () => Volatile.Read(ref generation),
                options: new LatestEncodedFrameDecodeWorkerOptions(
                    MaxPendingEncodedFrames: 4,
                    MaxPendingEncodedFrameAgeMs: 100,
                    GetNowUtcMs: () => Volatile.Read(ref nowUtcMs)),
                onFrameDroppedBeforeDecode: (request, reason) => droppedFrames.Add((request.FrameId, reason)));

            worker.EnqueueCopied("jpeg", new byte[] { 1 }, capturedTsUtcMs: 600, frameId: 1, sessionId: "age-budget");
            worker.EnqueueCopied("jpeg", new byte[] { 2 }, capturedTsUtcMs: 1_000, frameId: 2, sessionId: "age-budget");

            await WaitUntilAsync(() => worker.IsIdle, TimeSpan.FromSeconds(2));

            Assert.Contains(droppedFrames, static item => item.FrameId == 1 && string.Equals(item.Reason, "age_budget", StringComparison.Ordinal));
            var metrics = worker.GetMetricsSnapshot();
            Assert.True(metrics.DecodeWorkerDropAgeBudgetCount >= 1);
            Assert.True(metrics.AverageEnqueueToDropMs >= 0);
            return true;
        }, default);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task LatestEncodedFrameDecodeWorker_PropagatesUpstreamTimingMetadata()
    {
        await fixture.Session.Dispatch(async () =>
        {
            var generation = 0;
            EncodedFrameDecodeRequest decodeStartedRequest = default;
            var decodeStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            using var worker = new LatestEncodedFrameDecodeWorker(
                decodeFrame: request => CreateBitmap(request.EncodedFrameBytes.Span[0], 1),
                onFrameDecodedAsync: frame =>
                {
                    frame.Bitmap.Dispose();
                    return Task.CompletedTask;
                },
                onDecodeFailedAsync: _ => Task.CompletedTask,
                shouldStop: static () => false,
                getGeneration: () => Volatile.Read(ref generation),
                onFrameDecodeStarted: request =>
                {
                    decodeStartedRequest = request;
                    decodeStarted.TrySetResult(true);
                });

            worker.EnqueueCopied(
                "jpeg",
                new byte[] { 4 },
                capturedTsUtcMs: 100,
                isKeyFrame: false,
                streamEpoch: 7,
                frameId: 11,
                sessionId: "upstream-metadata",
                frameReadyObservedUtcMs: 1234,
                viewerAcceptedUtcMs: 1245);

            await decodeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await WaitUntilAsync(() => worker.IsIdle, TimeSpan.FromSeconds(2));

            Assert.Equal(1234, decodeStartedRequest.FrameReadyObservedUtcMs);
            Assert.Equal(1245, decodeStartedRequest.ViewerAcceptedUtcMs);
            Assert.True(decodeStartedRequest.DecodeEnqueuedUtcMs > 0);
            Assert.True(decodeStartedRequest.DecodeStartedUtcMs > 0);
            Assert.True(decodeStartedRequest.DecodeStartedUtcMs >= decodeStartedRequest.DecodeEnqueuedUtcMs);
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

    private static void WaitForSignal(Task signal, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!signal.IsCompleted && DateTime.UtcNow < deadline)
        {
            Thread.Yield();
        }

        Assert.True(signal.IsCompleted, $"Signal was not completed within {timeout.TotalSeconds:N1}s.");
        Assert.False(signal.IsCanceled, "Signal was canceled unexpectedly.");
        Assert.False(signal.IsFaulted, $"Signal faulted unexpectedly: {signal.Exception}");
    }

    private static Bitmap CreateTinyBitmap()
    {
        using var stream = new MemoryStream(CreateTinyPngBytes(), writable: false);
        return new Bitmap(stream);
    }

    private static Bitmap CreateBitmap(int width, int height)
    {
        var writeable = new WriteableBitmap(
            new PixelSize(width, height),
            new Avalonia.Vector(96, 96),
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

    private static byte[] CreateTinyPngBytes()
    {
        return Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a5kAAAAASUVORK5CYII=");
    }
}
