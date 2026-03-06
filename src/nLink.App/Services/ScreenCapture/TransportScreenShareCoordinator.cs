using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Configuration;
using NLink.Core.RemoteControl;
using NLink.Core.ScreenShare;
#if DEBUG
using NLink.Core.Diagnostics;
#endif

namespace NLink.App.Services.ScreenCapture;

internal sealed class TransportScreenShareCoordinator : IAsyncDisposable
{
    private const int MinAutoTuneFramesPerSecond = 2;
    private const int HighCaptureToSendAgeMs = 450;
    private const int LowCaptureToSendAgeMs = 220;
    private const int StableLowAgeTicksForIncrease = 3;
    private static readonly TimeSpan DisplayInfoMappingChangeDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AutoTuneInterval = TimeSpan.FromSeconds(1);
#if DEBUG
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(10);
#endif

    private readonly Func<IScreenCaptureSource> captureSourceFactory;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendPayloadAsync;
    private readonly Func<ControlDisplayInfoMessageV1, CancellationToken, Task>? sendDisplayInfoAsync;
    private readonly ScreenShareDisplayInfoProvider displayInfoProvider;
    private readonly IScreenShareClock clock;
    private readonly object gate = new();
    private static readonly TimeSpan InFlightEnqueueDrainTimeout = TimeSpan.FromSeconds(2);

    private IScreenCaptureSource? captureSource;
    private ScreenShareFrameSendPipeline? sendPipeline;
    private string sessionId = string.Empty;
    private ScreenShareDisplayInfoSnapshot? lastSentDisplayInfo;
    private DisplayInfoMappingKey? lastSentDisplayInfoMapping;
    private long lastSentDisplayInfoRevision;
    private ScreenShareDisplayInfoSnapshot? pendingDisplayInfo;
    private DisplayInfoMappingKey? pendingDisplayInfoMapping;
    private DateTimeOffset pendingDisplayInfoNotBeforeUtc;
    private string lastDisplayInfoIssue = string.Empty;
    private int inFlightEnqueues;
    private TaskCompletionSource<bool>? inFlightDrainedTcs;
    private Timer? autoTuneTimer;
    private int autoTuneTickInFlight;
    private int captureFpsHint;
    private int lowAgeStableTicks;
    private bool disposed;
#if DEBUG
    private Timer? snapshotTimer;
    private int snapshotTickInFlight;
#endif

    public TransportScreenShareCoordinator(
        Func<IScreenCaptureSource> captureSourceFactory,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendPayloadAsync,
        IScreenShareClock? clock = null,
        Func<ControlDisplayInfoMessageV1, CancellationToken, Task>? sendDisplayInfoAsync = null,
        ScreenShareDisplayInfoProvider? displayInfoProvider = null)
    {
        this.captureSourceFactory = captureSourceFactory ?? throw new ArgumentNullException(nameof(captureSourceFactory));
        this.sendPayloadAsync = sendPayloadAsync ?? throw new ArgumentNullException(nameof(sendPayloadAsync));
        this.sendDisplayInfoAsync = sendDisplayInfoAsync;
        this.displayInfoProvider = displayInfoProvider ?? new ScreenShareDisplayInfoProvider();
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
            maxFramesPerSecond: FeatureFlags.ScreenShareTransportMaxFps);

        lock (gate)
        {
            captureSource = nextCaptureSource;
            sendPipeline = nextPipeline;
            sessionId = normalizedSessionId;
            lastSentDisplayInfo = null;
            lastSentDisplayInfoMapping = null;
            lastSentDisplayInfoRevision = 0;
            pendingDisplayInfo = null;
            pendingDisplayInfoMapping = null;
            pendingDisplayInfoNotBeforeUtc = default;
            lastDisplayInfoIssue = string.Empty;
            var minAutoTuneFps = Math.Min(MinAutoTuneFramesPerSecond, FeatureFlags.ScreenShareTransportMaxFps);
            captureFpsHint = Math.Clamp(
                Math.Min(FeatureFlags.ScreenShareMaxFps, FeatureFlags.ScreenShareTransportMaxFps),
                minAutoTuneFps,
                FeatureFlags.ScreenShareTransportMaxFps);
            lowAgeStableTicks = 0;
            nextCaptureSource.FrameArrived += OnFrameArrived;
            if (nextCaptureSource is IScreenCaptureAdaptiveTuning tunableCaptureSource)
            {
                tunableCaptureSource.SetCaptureFrameRateHint(captureFpsHint);
            }
        }

        try
        {
            await nextCaptureSource.StartAsync(ct).ConfigureAwait(false);
            StartAutoTuneTimer();
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
            lastSentDisplayInfo = null;
            lastSentDisplayInfoMapping = null;
            lastSentDisplayInfoRevision = 0;
            pendingDisplayInfo = null;
            pendingDisplayInfoMapping = null;
            pendingDisplayInfoNotBeforeUtc = default;
            lastDisplayInfoIssue = string.Empty;

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
        StopAutoTuneTimer();

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
        IScreenCaptureSource? currentCaptureSource;
        string currentSessionId;
        Task enqueueTask;

        lock (gate)
        {
            currentPipeline = sendPipeline;
            currentCaptureSource = captureSource;
            currentSessionId = sessionId;

            if (currentPipeline is null || string.IsNullOrWhiteSpace(currentSessionId))
            {
                return;
            }

            inFlightEnqueues++;
        }

        if (currentCaptureSource is not null)
        {
            TryPublishDisplayInfo(currentCaptureSource, e.Width, e.Height);
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
                e.CapturedTsUtcMs > 0
                    ? e.CapturedTsUtcMs
                    : clock.UtcNow.ToUnixTimeMilliseconds(),
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

    private void StartAutoTuneTimer()
    {
        if (autoTuneTimer is not null)
        {
            return;
        }

        autoTuneTimer = new Timer(
            static state => ((TransportScreenShareCoordinator)state!).OnAutoTuneTimerTick(),
            this,
            AutoTuneInterval,
            AutoTuneInterval);
    }

    private void StopAutoTuneTimer()
    {
        Interlocked.Exchange(ref autoTuneTickInFlight, 0);
        var timer = Interlocked.Exchange(ref autoTuneTimer, null);
        timer?.Dispose();
        lowAgeStableTicks = 0;
        captureFpsHint = 0;
    }

    private void OnAutoTuneTimerTick()
    {
        if (!FeatureFlags.ScreenShareTransportAutoTuneEnabled)
        {
            return;
        }

        if (Interlocked.Exchange(ref autoTuneTickInFlight, 1) == 1)
        {
            return;
        }

        try
        {
            ScreenShareFrameSendPipeline? currentPipeline;
            IScreenCaptureSource? currentCaptureSource;
            lock (gate)
            {
                currentPipeline = sendPipeline;
                currentCaptureSource = captureSource;
            }

            if (currentPipeline is null ||
                currentCaptureSource is not IScreenCaptureAdaptiveTuning tunableCaptureSource)
            {
                return;
            }

            var maxTransportFps = FeatureFlags.ScreenShareTransportMaxFps;
            var minAutoTuneFps = Math.Min(MinAutoTuneFramesPerSecond, maxTransportFps);
            var configuredCap = Math.Clamp(
                Math.Min(FeatureFlags.ScreenShareMaxFps, maxTransportFps),
                minAutoTuneFps,
                maxTransportFps);

            var currentHint = captureFpsHint <= 0 ? configuredCap : captureFpsHint;
            var captureToSendAgeMs = currentPipeline.LastCaptureToSendAgeMs;
            if (captureToSendAgeMs < 0)
            {
                return;
            }

            var nextHint = currentHint;
            if (captureToSendAgeMs >= HighCaptureToSendAgeMs)
            {
                nextHint = Math.Max(minAutoTuneFps, currentHint - 1);
                lowAgeStableTicks = 0;
            }
            else if (captureToSendAgeMs <= LowCaptureToSendAgeMs)
            {
                lowAgeStableTicks++;
                if (lowAgeStableTicks >= StableLowAgeTicksForIncrease)
                {
                    nextHint = Math.Min(configuredCap, currentHint + 1);
                    lowAgeStableTicks = 0;
                }
            }
            else
            {
                lowAgeStableTicks = 0;
            }

            if (nextHint == currentHint)
            {
                return;
            }

            captureFpsHint = nextHint;
            tunableCaptureSource.SetCaptureFrameRateHint(nextHint);
            LogDebug($"Auto-tuned capture fps hint to {nextHint} (capture_to_send_age_ms={captureToSendAgeMs}).");
        }
        catch (Exception ex)
        {
            LogDebug($"Auto-tune tick failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref autoTuneTickInFlight, 0);
        }
    }

    private void TryPublishDisplayInfo(IScreenCaptureSource currentCaptureSource, int frameWidth, int frameHeight)
    {
        if (sendDisplayInfoAsync is null)
        {
            return;
        }

        if (!displayInfoProvider.TryGetSnapshot(currentCaptureSource, frameWidth, frameHeight, out var snapshot, out var reason))
        {
            lock (gate)
            {
                if (!string.Equals(lastDisplayInfoIssue, reason, StringComparison.Ordinal))
                {
                    lastDisplayInfoIssue = reason;
                    LogDebug($"Display info skipped ({reason}).");
                }
            }

            return;
        }

        ControlDisplayInfoMessageV1 message;
        long revision;
        ScreenShareDisplayInfoSnapshot sentSnapshot;
        DisplayInfoMappingKey sentMapping;
        lock (gate)
        {
            if (lastSentDisplayInfo.HasValue &&
                lastSentDisplayInfo.Value.Equals(snapshot))
            {
                return;
            }

            var mapping = CreateMappingKey(snapshot);
            if (lastSentDisplayInfoMapping.HasValue &&
                lastSentDisplayInfoMapping.Value.Equals(mapping))
            {
                // Frame-level changes (e.g. adaptive encoder size) are not mapping changes.
                // Keep latest diagnostics fields but do not bump revision/send updates.
                lastSentDisplayInfo = snapshot;
                ClearPendingDisplayInfoUnsafe();
                lastDisplayInfoIssue = string.Empty;
                return;
            }

            var now = clock.UtcNow;
            if (lastSentDisplayInfoMapping.HasValue)
            {
                if (!pendingDisplayInfoMapping.HasValue ||
                    !pendingDisplayInfoMapping.Value.Equals(mapping))
                {
                    pendingDisplayInfoMapping = mapping;
                    pendingDisplayInfo = snapshot;
                    pendingDisplayInfoNotBeforeUtc = now + DisplayInfoMappingChangeDebounce;
                    lastDisplayInfoIssue = string.Empty;
                    return;
                }

                pendingDisplayInfo = snapshot;
                if (now < pendingDisplayInfoNotBeforeUtc)
                {
                    return;
                }

                snapshot = pendingDisplayInfo.Value;
                mapping = pendingDisplayInfoMapping.Value;
                ClearPendingDisplayInfoUnsafe();
            }

            revision = checked(lastSentDisplayInfoRevision + 1);
            lastSentDisplayInfoRevision = revision;
            lastSentDisplayInfo = snapshot;
            lastSentDisplayInfoMapping = mapping;
            lastDisplayInfoIssue = string.Empty;
            sentSnapshot = snapshot;
            sentMapping = mapping;
            message = new ControlDisplayInfoMessageV1
            {
                DisplayId = snapshot.DisplayId,
                VirtualDesktopX = snapshot.VirtualDesktopX,
                VirtualDesktopY = snapshot.VirtualDesktopY,
                VirtualDesktopWidth = snapshot.VirtualDesktopWidth,
                VirtualDesktopHeight = snapshot.VirtualDesktopHeight,
                CaptureRegionX = snapshot.CaptureRegionX,
                CaptureRegionY = snapshot.CaptureRegionY,
                CaptureRegionWidth = snapshot.CaptureRegionWidth,
                CaptureRegionHeight = snapshot.CaptureRegionHeight,
                FrameWidth = snapshot.FrameWidth,
                FrameHeight = snapshot.FrameHeight,
                DpiScale = snapshot.DpiScale,
                Revision = revision,
                TsUtcMs = clock.UtcNow.ToUnixTimeMilliseconds(),
            };
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await sendDisplayInfoAsync(message, CancellationToken.None).ConfigureAwait(false);
                LogDebug($"Display info sent (display_id={message.DisplayId}, revision={message.Revision}, frame={message.FrameWidth}x{message.FrameHeight}).");
            }
            catch (Exception ex)
            {
                lock (gate)
                {
                    if (lastSentDisplayInfo.HasValue &&
                        lastSentDisplayInfo.Value.Equals(sentSnapshot) &&
                        lastSentDisplayInfoMapping.HasValue &&
                        lastSentDisplayInfoMapping.Value.Equals(sentMapping) &&
                        lastSentDisplayInfoRevision == revision)
                    {
                        // Retry on subsequent frames if this send failed.
                        lastSentDisplayInfo = null;
                        lastSentDisplayInfoMapping = null;
                    }
                }

                LogDebug($"Display info send failed: {ex.GetType().Name}: {ex.Message}");
            }
        });
    }

    private void ClearPendingDisplayInfoUnsafe()
    {
        pendingDisplayInfo = null;
        pendingDisplayInfoMapping = null;
        pendingDisplayInfoNotBeforeUtc = default;
    }

    private static DisplayInfoMappingKey CreateMappingKey(ScreenShareDisplayInfoSnapshot snapshot)
    {
        return new DisplayInfoMappingKey(
            DisplayId: snapshot.DisplayId,
            VirtualDesktopX: snapshot.VirtualDesktopX,
            VirtualDesktopY: snapshot.VirtualDesktopY,
            VirtualDesktopWidth: snapshot.VirtualDesktopWidth,
            VirtualDesktopHeight: snapshot.VirtualDesktopHeight,
            CaptureRegionX: snapshot.CaptureRegionX,
            CaptureRegionY: snapshot.CaptureRegionY,
            CaptureRegionWidth: snapshot.CaptureRegionWidth,
            CaptureRegionHeight: snapshot.CaptureRegionHeight);
    }

    private readonly record struct DisplayInfoMappingKey(
        string DisplayId,
        int VirtualDesktopX,
        int VirtualDesktopY,
        int VirtualDesktopWidth,
        int VirtualDesktopHeight,
        int CaptureRegionX,
        int CaptureRegionY,
        int CaptureRegionWidth,
        int CaptureRegionHeight);

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
            var latency = currentPipeline.GetDebugLatencySnapshotAndReset();
            var heapBytes = GC.GetTotalMemory(false);
            using var process = Process.GetCurrentProcess();
            LogDebug(
                $"Snapshot heap={heapBytes} ws={process.WorkingSet64} queued={metrics.FramesQueued} dropped={metrics.FramesDropped} sent={metrics.ChunksSent} " +
                $"c2e={FormatLatency(latency.CaptureToEnqueue)} q2s={FormatLatency(latency.EnqueueToSend)} " +
                $"send={FormatLatency(latency.SendDuration)} e2e={FormatLatency(latency.EndToEnd)}.");
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

#if DEBUG
    private static string FormatLatency(DebugLatencySummary summary)
    {
        return !summary.HasSamples
            ? "na"
            : $"avg={summary.AverageMilliseconds:F1}ms p50={summary.P50Milliseconds:F1}ms p95={summary.P95Milliseconds:F1}ms n={summary.Count}";
    }
#endif
}
