using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

internal sealed class TransportScreenShareCoordinator : IAsyncDisposable
{
    internal const int MaxTransportFramesPerSecond = 2;
#if DEBUG
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(10);
#endif

    private readonly Func<IScreenCaptureSource> captureSourceFactory;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendPayloadAsync;
    private readonly IScreenShareClock clock;
    private readonly object gate = new();
    private static readonly TimeSpan InFlightEnqueueDrainTimeout = TimeSpan.FromSeconds(2);

    private IScreenCaptureSource? captureSource;
    private ScreenShareFrameSendPipeline? sendPipeline;
    private string sessionId = string.Empty;
    private int inFlightEnqueues;
    private TaskCompletionSource<bool>? inFlightDrainedTcs;
    private bool disposed;
#if DEBUG
    private Timer? snapshotTimer;
    private int snapshotTickInFlight;
#endif

    public TransportScreenShareCoordinator(
        Func<IScreenCaptureSource> captureSourceFactory,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendPayloadAsync,
        IScreenShareClock? clock = null)
    {
        this.captureSourceFactory = captureSourceFactory ?? throw new ArgumentNullException(nameof(captureSourceFactory));
        this.sendPayloadAsync = sendPayloadAsync ?? throw new ArgumentNullException(nameof(sendPayloadAsync));
        this.clock = clock ?? SystemScreenShareClock.Instance;
    }

    public bool IsActive
    {
        get
        {
            lock (gate)
            {
                return captureSource is not null && sendPipeline is not null;
            }
        }
    }

    public async Task StartAsync(string nextSessionId, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(nextSessionId);
        ct.ThrowIfCancellationRequested();

        var normalizedSessionId = nextSessionId.Trim();
        lock (gate)
        {
            if (captureSource is not null &&
                sendPipeline is not null &&
                string.Equals(sessionId, normalizedSessionId, StringComparison.Ordinal))
            {
                LogDebug("StartAsync ignored because screenshare is already active for the current session.");
                return;
            }
        }

        await StopAsync(sendStopMessage: false, reason: null, CancellationToken.None).ConfigureAwait(false);

        var nextCaptureSource = captureSourceFactory();
        if (!nextCaptureSource.IsSupported)
        {
            if (nextCaptureSource is IAsyncDisposable unsupportedAsyncDisposable)
            {
                await unsupportedAsyncDisposable.DisposeAsync().ConfigureAwait(false);
            }

            return;
        }

        var nextPipeline = new ScreenShareFrameSendPipeline(
            sendChunkAsync: async (chunk, sendCt) =>
            {
                var payload = ScreenSharePayloadCodec.Serialize(chunk);
                await sendPayloadAsync(payload, sendCt).ConfigureAwait(false);
            },
            clock: clock,
            maxFramesPerSecond: MaxTransportFramesPerSecond);

        lock (gate)
        {
            captureSource = nextCaptureSource;
            sendPipeline = nextPipeline;
            sessionId = normalizedSessionId;
            nextCaptureSource.FrameArrived += OnFrameArrived;
        }

        try
        {
            await nextCaptureSource.StartAsync(ct).ConfigureAwait(false);
#if DEBUG
            StartSnapshotTimer();
#endif
        }
        catch (Exception ex)
        {
            LogDebug($"Capture source start failed during screenshare startup: {ex.GetType().Name}: {ex.Message}");
            lock (gate)
            {
                if (ReferenceEquals(captureSource, nextCaptureSource))
                {
                    captureSource = null;
                }

                if (ReferenceEquals(sendPipeline, nextPipeline))
                {
                    sendPipeline = null;
                }

                if (string.Equals(sessionId, normalizedSessionId, StringComparison.Ordinal))
                {
                    sessionId = string.Empty;
                }

                nextCaptureSource.FrameArrived -= OnFrameArrived;
            }

            await nextPipeline.DisposeAsync().ConfigureAwait(false);
            if (nextCaptureSource is IAsyncDisposable failedAsyncDisposable)
            {
                await failedAsyncDisposable.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
    }

    public Task HandleDisconnectedAsync()
    {
        return StopAsync(sendStopMessage: false, reason: "disconnected", CancellationToken.None);
    }

    public async Task StopAsync(bool sendStopMessage, string? reason, CancellationToken ct)
    {
        IScreenCaptureSource? oldCaptureSource;
        ScreenShareFrameSendPipeline? oldPipeline;
        string oldSessionId;
        Task? drainTask = null;
        TaskCompletionSource<bool>? drainCompletion = null;

        lock (gate)
        {
            oldCaptureSource = captureSource;
            oldPipeline = sendPipeline;
            oldSessionId = sessionId;
            captureSource = null;
            sendPipeline = null;
            sessionId = string.Empty;

            if (oldCaptureSource is not null)
            {
                oldCaptureSource.FrameArrived -= OnFrameArrived;
            }

            if (inFlightEnqueues != 0)
            {
                inFlightDrainedTcs ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                drainCompletion = inFlightDrainedTcs;
                drainTask = drainCompletion.Task;
            }
        }

#if DEBUG
        StopSnapshotTimer();
#endif

        if (oldCaptureSource is null &&
            oldPipeline is null &&
            string.IsNullOrWhiteSpace(oldSessionId) &&
            drainTask is null)
        {
            LogDebug("StopAsync ignored because screenshare is already inactive.");
            return;
        }

        if (drainTask is not null)
        {
            try
            {
                await drainTask.WaitAsync(InFlightEnqueueDrainTimeout, CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                LogDebug("StopAsync timed out waiting for in-flight frame enqueues to drain.");
            }
            finally
            {
                lock (gate)
                {
                    if (ReferenceEquals(inFlightDrainedTcs, drainCompletion))
                    {
                        inFlightDrainedTcs = null;
                    }
                }
            }
        }

        if (oldCaptureSource is not null)
        {
            try
            {
                await oldCaptureSource.StopAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogDebug($"Capture source stop failed during screenshare shutdown: {ex.GetType().Name}: {ex.Message}");
            }

            if (oldCaptureSource is IAsyncDisposable asyncDisposable)
            {
                try
                {
                    await asyncDisposable.DisposeAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogDebug($"Capture source dispose failed during screenshare shutdown: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        if (oldPipeline is not null)
        {
            await oldPipeline.DisposeAsync().ConfigureAwait(false);
        }

        if (sendStopMessage && !string.IsNullOrWhiteSpace(oldSessionId))
        {
            var stop = new ScreenShareStopMessageV1
            {
                SessionId = oldSessionId,
                Reason = reason,
            };

            await sendPayloadAsync(ScreenSharePayloadCodec.SerializeStop(stop), ct).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await StopAsync(sendStopMessage: false, reason: null, CancellationToken.None).ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private void OnFrameArrived(object? sender, ScreenCaptureFrameEventArgs e)
    {
        ScreenShareFrameSendPipeline? currentPipeline;
        string currentSessionId;
        Task enqueueTask;

        lock (gate)
        {
            currentPipeline = sendPipeline;
            currentSessionId = sessionId;

            if (currentPipeline is null || string.IsNullOrWhiteSpace(currentSessionId))
            {
                return;
            }

            inFlightEnqueues++;
        }

        enqueueTask = TryEnqueueFrameAsync(currentPipeline, currentSessionId, e);
        _ = enqueueTask.ContinueWith(
            static (_, state) => ((TransportScreenShareCoordinator)state!).OnEnqueueCompleted(),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private async Task TryEnqueueFrameAsync(
        ScreenShareFrameSendPipeline currentPipeline,
        string currentSessionId,
        ScreenCaptureFrameEventArgs e)
    {
        try
        {
            await currentPipeline.EnqueueFrameAsync(
                currentSessionId,
                e.Width,
                e.Height,
                e.Encoding,
                e.EncodedFrameData,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            LogDebug("Frame enqueue ignored because sender pipeline was already disposed.");
        }
        catch (InvalidOperationException)
        {
            LogDebug("Frame enqueue ignored because sender pipeline was already completed.");
        }
        catch (OperationCanceledException)
        {
            LogDebug("Frame enqueue canceled during shutdown.");
        }
        catch (Exception ex)
        {
            LogDebug($"Frame enqueue failed unexpectedly: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void OnEnqueueCompleted()
    {
        TaskCompletionSource<bool>? drained = null;

        lock (gate)
        {
            if (inFlightEnqueues > 0)
            {
                inFlightEnqueues--;
            }

            if (inFlightEnqueues == 0 && inFlightDrainedTcs is not null)
            {
                drained = inFlightDrainedTcs;
                inFlightDrainedTcs = null;
            }
        }

        drained?.TrySetResult(true);
    }

#if DEBUG
    private void StartSnapshotTimer()
    {
        if (snapshotTimer is not null)
        {
            return;
        }

        snapshotTimer = new Timer(
            static state => ((TransportScreenShareCoordinator)state!).OnSnapshotTimerTick(),
            this,
            SnapshotInterval,
            SnapshotInterval);
    }

    private void StopSnapshotTimer()
    {
        Interlocked.Exchange(ref snapshotTickInFlight, 0);
        var timer = Interlocked.Exchange(ref snapshotTimer, null);
        timer?.Dispose();
    }

    private void OnSnapshotTimerTick()
    {
        if (Interlocked.Exchange(ref snapshotTickInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            ScreenShareFrameSendPipeline? currentPipeline;
            lock (gate)
            {
                currentPipeline = sendPipeline;
                if (captureSource is null || currentPipeline is null)
                {
                    return;
                }
            }

            var metrics = currentPipeline.GetMetricsSnapshot();
            var heapBytes = GC.GetTotalMemory(false);
            using var process = Process.GetCurrentProcess();
            LogDebug(
                $"Snapshot heap={heapBytes} ws={process.WorkingSet64} queued={metrics.FramesQueued} dropped={metrics.FramesDropped} sent={metrics.ChunksSent}.");
        }
        catch (Exception ex)
        {
            LogDebug($"Transport snapshot failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref snapshotTickInFlight, 0);
        }
    }
#endif

    [Conditional("DEBUG")]
    private static void LogDebug(string message)
    {
        Trace.WriteLine($"[ScreenShareTransport] {message}");
    }
}
