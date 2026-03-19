using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NLink.App.Services;
using NLink.App.Configuration;
using NLink.Core.Logging;
using NLink.Core.RemoteControl;
using NLink.Core.ScreenShare;
#if DEBUG
using NLink.Core.Diagnostics;
#endif

namespace NLink.App.Services.ScreenCapture;

internal sealed class ScreenShareSenderDegradedModeChangedEventArgs : EventArgs
{
    public ScreenShareSenderDegradedModeChangedEventArgs(bool isActive)
    {
        IsActive = isActive;
    }

    public bool IsActive { get; }
}

internal sealed class TransportScreenShareCoordinator : IAsyncDisposable
{
    private const int MinAutoTuneFramesPerSecond = 2;
    private const int DegradedSenderFramesPerSecond = 2;
    private const int HighCaptureToSendAgeMs = 450;
    private const int LowCaptureToSendAgeMs = 220;
    private const int StableLowAgeTicksForIncrease = 3;
    private static readonly TimeSpan SenderDegradedExitHoldDuration = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DisplayInfoMappingChangeDebounce = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan AutoTuneInterval = TimeSpan.FromSeconds(1);
#if DEBUG
    private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(10);
#endif

    private readonly Func<IScreenCaptureSource> captureSourceFactory;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendPayloadAsync;
    private readonly Func<ReadOnlyMemory<byte>, long>? estimateBridgeBytes;
    private readonly Func<string, ControlDisplayInfoMessageV1, CancellationToken, Task>? sendDisplayInfoAsync;
    private readonly ScreenShareDisplayInfoProvider displayInfoProvider;
    private readonly IScreenShareClock clock;
    private readonly object gate = new();
    private readonly object diagnosticRateLimitGate = new();
    private static readonly TimeSpan InFlightEnqueueDrainTimeout = TimeSpan.FromSeconds(2);

    private IScreenCaptureSource? captureSource;
    private ScreenShareFrameSendPipeline? sendPipeline;
    private string sessionId = string.Empty;
    private string lastActiveSessionId = string.Empty;
    private ScreenShareDisplayInfoSnapshot? lastSentDisplayInfo;
    private DisplayInfoMappingKey? lastSentDisplayInfoMapping;
    private long lastSentDisplayInfoRevision;
    private ScreenShareDisplayInfoSnapshot? pendingDisplayInfo;
    private DisplayInfoMappingKey? pendingDisplayInfoMapping;
    private DateTimeOffset pendingDisplayInfoNotBeforeUtc;
    private string lastDisplayInfoIssue = string.Empty;
    private long lifecycleGeneration;
    private long lastDisplayInfoSuppressedLogTick;
    private int inFlightEnqueues;
    private TaskCompletionSource<bool>? inFlightDrainedTcs;
    private Timer? autoTuneTimer;
    private int autoTuneTickInFlight;
    private int captureFpsHint;
    private int lowAgeStableTicks;
    private int preferFreshestPendingFrameOnly;
    private bool transportPressureHintActive;
    private bool fileTransferDegradedHintActive;
    private bool fileTransferCatchUpOnlyHintActive;
    private bool senderDegradedModeActive;
    private long lastAutoTuneRateGateDrops;
    private long lastAutoTuneQueueEvictDrops;
    private long displayInfoSendCount;
    private long serializedChunkBytesSent;
    private long bridgeBytesSent;
    private DateTimeOffset? lastSenderDegradedPressureUtc;
    private ScreenShareMetrics lastMetricsSnapshot = new();
    private bool disposed;
#if DEBUG
    private Timer? snapshotTimer;
    private int snapshotTickInFlight;
#endif

    public TransportScreenShareCoordinator(
        Func<IScreenCaptureSource> captureSourceFactory,
        Func<ReadOnlyMemory<byte>, CancellationToken, Task> sendPayloadAsync,
        IScreenShareClock? clock = null,
        Func<string, ControlDisplayInfoMessageV1, CancellationToken, Task>? sendDisplayInfoAsync = null,
        ScreenShareDisplayInfoProvider? displayInfoProvider = null,
        Func<ReadOnlyMemory<byte>, long>? estimateBridgeBytes = null)
    {
        this.captureSourceFactory = captureSourceFactory ?? throw new ArgumentNullException(nameof(captureSourceFactory));
        this.sendPayloadAsync = sendPayloadAsync ?? throw new ArgumentNullException(nameof(sendPayloadAsync));
        this.sendDisplayInfoAsync = sendDisplayInfoAsync;
        this.displayInfoProvider = displayInfoProvider ?? new ScreenShareDisplayInfoProvider();
        this.clock = clock ?? SystemScreenShareClock.Instance;
        this.estimateBridgeBytes = estimateBridgeBytes;
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
                Interlocked.Add(ref serializedChunkBytesSent, payload.Length);
                if (estimateBridgeBytes is not null)
                {
                    var bridgeBytes = Math.Max(0L, estimateBridgeBytes(payload));
                    if (bridgeBytes > 0)
                    {
                        Interlocked.Add(ref bridgeBytesSent, bridgeBytes);
                    }
                }
            },
            clock: clock,
            maxFramesPerSecond: FeatureFlags.ScreenShareTransportMaxFps);

        lock (gate)
        {
            lifecycleGeneration = checked(lifecycleGeneration + 1);
            captureSource = nextCaptureSource;
            sendPipeline = nextPipeline;
            sessionId = normalizedSessionId;
            lastActiveSessionId = normalizedSessionId;
            lastSentDisplayInfo = null;
            lastSentDisplayInfoMapping = null;
            lastSentDisplayInfoRevision = 0;
            pendingDisplayInfo = null;
            pendingDisplayInfoMapping = null;
            pendingDisplayInfoNotBeforeUtc = default;
            lastDisplayInfoIssue = string.Empty;
            displayInfoSendCount = 0;
            lastMetricsSnapshot = new();
            var minAutoTuneFps = Math.Min(MinAutoTuneFramesPerSecond, FeatureFlags.ScreenShareTransportMaxFps);
            captureFpsHint = Math.Clamp(
                Math.Min(FeatureFlags.ScreenShareMaxFps, FeatureFlags.ScreenShareTransportMaxFps),
                minAutoTuneFps,
                FeatureFlags.ScreenShareTransportMaxFps);
            lowAgeStableTicks = 0;
            Volatile.Write(ref preferFreshestPendingFrameOnly, 0);
            senderDegradedModeActive = false;
            lastAutoTuneRateGateDrops = 0;
            lastAutoTuneQueueEvictDrops = 0;
            lastSenderDegradedPressureUtc = null;
            serializedChunkBytesSent = 0;
            bridgeBytesSent = 0;
            nextCaptureSource.FrameArrived += OnFrameArrived;
            if (nextCaptureSource is IScreenCaptureAdaptiveTuning tunableCaptureSource)
            {
                tunableCaptureSource.SetCaptureFrameRateHint(captureFpsHint);
                tunableCaptureSource.SetTransportPressureHint(false);
            }
        }

        try
        {
            await nextCaptureSource.StartAsync(ct).ConfigureAwait(false);
            StartAutoTuneTimer();
#if DEBUG
            StartSnapshotTimer();
#endif
            if (fileTransferDegradedHintActive)
            {
                SetFileTransferDegradedHint(true);
            }
            else if (fileTransferCatchUpOnlyHintActive)
            {
                SetFileTransferCatchUpOnlyHint(true);
            }
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
        long oldLifecycleGeneration;
        ScreenShareMetrics oldMetricsSnapshot = new(DisplayInfoSendCount: Interlocked.Read(ref displayInfoSendCount));
        Task? pipelineDisposeTask = null;
        Task? drainTask = null;
        TaskCompletionSource<bool>? drainCompletion = null;

        lock (gate)
        {
            oldCaptureSource = captureSource;
            oldPipeline = sendPipeline;
            oldSessionId = sessionId;
            oldLifecycleGeneration = lifecycleGeneration;
            if (oldPipeline is not null)
            {
                oldMetricsSnapshot = oldPipeline.GetMetricsSnapshot() with
                {
                    DisplayInfoSendCount = Interlocked.Read(ref displayInfoSendCount),
                    SerializedChunkBytesSent = Interlocked.Read(ref serializedChunkBytesSent),
                    BridgeBytesSent = Interlocked.Read(ref bridgeBytesSent),
                };
            }

            lifecycleGeneration = checked(lifecycleGeneration + 1);
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
            lastMetricsSnapshot = oldMetricsSnapshot;

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

        if (oldPipeline is not null)
        {
            // Cancel queued/in-flight frame work immediately, but do not wait for the
            // send loop to finish before notifying the remote side that screensharing stopped.
            pipelineDisposeTask = oldPipeline.DisposeAsync().AsTask();
        }

        if (sendStopMessage && !string.IsNullOrWhiteSpace(oldSessionId))
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_stop_local_requested; session_id={oldSessionId}; reason={(string.IsNullOrWhiteSpace(reason) ? "(none)" : reason)}; lifecycle_generation={oldLifecycleGeneration}");
            var stop = new ScreenShareStopMessageV1
            {
                SessionId = oldSessionId,
                Reason = reason,
            };

            await sendPayloadAsync(ScreenSharePayloadCodec.SerializeStop(stop), ct).ConfigureAwait(false);
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_stop_local_dispatched; session_id={oldSessionId}; reason={(string.IsNullOrWhiteSpace(reason) ? "(none)" : reason)}; lifecycle_generation={oldLifecycleGeneration}");
        }

        if (pipelineDisposeTask is not null)
        {
            await pipelineDisposeTask.ConfigureAwait(false);
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
            if (Volatile.Read(ref preferFreshestPendingFrameOnly) == 1 || IsSenderDegradedModeActive())
            {
                var droppedQueuedFrames = currentPipeline.FlushPendingFrames();
                if (droppedQueuedFrames > 0)
                {
                    LocalOperationalLog.Info(
                        "ScreenShareTransport",
                        $"event=screenshare_sender_frame_dropped_backlog; session_id={currentSessionId}; dropped_count={droppedQueuedFrames}");
                }
            }

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

    internal ScreenShareMetrics GetMetricsSnapshot()
    {
        lock (gate)
        {
            if (sendPipeline is not null)
            {
                lastMetricsSnapshot = sendPipeline.GetMetricsSnapshot() with
                {
                    DisplayInfoSendCount = Interlocked.Read(ref displayInfoSendCount),
                    SerializedChunkBytesSent = Interlocked.Read(ref serializedChunkBytesSent),
                    BridgeBytesSent = Interlocked.Read(ref bridgeBytesSent),
                    FreshnessMode = senderDegradedModeActive ? "degraded" : "normal",
                };
            }
            else
            {
                lastMetricsSnapshot = lastMetricsSnapshot with
                {
                    DisplayInfoSendCount = Interlocked.Read(ref displayInfoSendCount),
                    SerializedChunkBytesSent = Interlocked.Read(ref serializedChunkBytesSent),
                    BridgeBytesSent = Interlocked.Read(ref bridgeBytesSent),
                    FreshnessMode = senderDegradedModeActive ? "degraded" : "normal",
                };
            }

            return lastMetricsSnapshot;
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
        Volatile.Write(ref preferFreshestPendingFrameOnly, 0);
        transportPressureHintActive = false;
        senderDegradedModeActive = false;
        lastAutoTuneRateGateDrops = 0;
        lastAutoTuneQueueEvictDrops = 0;
        lastSenderDegradedPressureUtc = null;
    }

    internal void SetFileTransferDegradedHint(bool active)
    {
        ScreenShareFrameSendPipeline? currentPipeline;
        IScreenCaptureSource? currentCaptureSource;
        string currentSessionId;

        lock (gate)
        {
            fileTransferDegradedHintActive = active;
            currentPipeline = sendPipeline;
            currentCaptureSource = captureSource;
            currentSessionId = sessionId;
        }

        ApplySenderDegradedMode(
            currentPipeline,
            currentCaptureSource,
            currentSessionId,
            shouldEnable: active || fileTransferCatchUpOnlyHintActive || ComputeLocalSenderPressure(currentPipeline),
            reason: active ? "file_transfer" : "recovered");
    }

    internal void SetFileTransferCatchUpOnlyHint(bool active)
    {
        ScreenShareFrameSendPipeline? currentPipeline;
        IScreenCaptureSource? currentCaptureSource;
        string currentSessionId;

        lock (gate)
        {
            fileTransferCatchUpOnlyHintActive = active;
            currentPipeline = sendPipeline;
            currentCaptureSource = captureSource;
            currentSessionId = sessionId;
        }

        ApplySenderDegradedMode(
            currentPipeline,
            currentCaptureSource,
            currentSessionId,
            shouldEnable: active || fileTransferDegradedHintActive || ComputeLocalSenderPressure(currentPipeline),
            reason: active ? "file_transfer_pressure" : "recovered");
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
            string currentSessionId;
            bool fileTransferDegradedHint;
            lock (gate)
            {
                currentPipeline = sendPipeline;
                currentCaptureSource = captureSource;
                currentSessionId = sessionId;
                fileTransferDegradedHint = fileTransferDegradedHintActive;
            }

            if (currentPipeline is null)
            {
                return;
            }

            var metrics = currentPipeline.GetMetricsSnapshot();
            var rateGateDropDelta = ConsumeAutoTuneCounterDelta(
                metrics.FramesDroppedByRateGate,
                ref lastAutoTuneRateGateDrops);
            var queueEvictDropDelta = ConsumeAutoTuneCounterDelta(
                metrics.FramesDroppedByQueueEvict,
                ref lastAutoTuneQueueEvictDrops);

            var maxTransportFps = FeatureFlags.ScreenShareTransportMaxFps;
            var minAutoTuneFps = Math.Min(MinAutoTuneFramesPerSecond, maxTransportFps);
            var configuredCap = Math.Clamp(
                Math.Min(FeatureFlags.ScreenShareMaxFps, maxTransportFps),
                minAutoTuneFps,
                maxTransportFps);

            var currentHint = captureFpsHint <= 0 ? configuredCap : captureFpsHint;
            var captureToSendAgeMs = metrics.LastCaptureToSendAgeMs;
            var hasHighAgePressure = captureToSendAgeMs >= HighCaptureToSendAgeMs;
            var hasLowAgeHeadroom = captureToSendAgeMs >= 0 && captureToSendAgeMs <= LowCaptureToSendAgeMs;
            var hasRateGatePressure = rateGateDropDelta > 0;
            var hasQueuePressure = queueEvictDropDelta > 0;
            var shouldPreferLowerBandwidth = hasQueuePressure || hasHighAgePressure || hasRateGatePressure;
            var nextSenderDegradedMode = fileTransferCatchUpOnlyHintActive || fileTransferDegradedHint || hasQueuePressure || hasHighAgePressure;

            if (hasQueuePressure || hasHighAgePressure || fileTransferDegradedHint)
            {
                Volatile.Write(ref preferFreshestPendingFrameOnly, 1);
            }

            if (captureToSendAgeMs < 0 && !hasRateGatePressure && !hasQueuePressure)
            {
                return;
            }

            var nextHint = currentHint;
            var nextTransportPressureHint = transportPressureHintActive;
            if (shouldPreferLowerBandwidth || fileTransferDegradedHint)
            {
                nextTransportPressureHint = true;
            }

            if (nextSenderDegradedMode)
            {
                nextHint = DegradedSenderFramesPerSecond;
                lowAgeStableTicks = 0;
            }
            else if (hasQueuePressure)
            {
                nextHint = Math.Max(minAutoTuneFps, currentHint - 2);
                lowAgeStableTicks = 0;
            }
            else if (hasHighAgePressure || hasRateGatePressure)
            {
                nextHint = Math.Max(minAutoTuneFps, currentHint - 1);
                lowAgeStableTicks = 0;
            }
            else if (hasLowAgeHeadroom)
            {
                lowAgeStableTicks++;
                if (lowAgeStableTicks >= StableLowAgeTicksForIncrease)
                {
                    Volatile.Write(ref preferFreshestPendingFrameOnly, 0);
                    nextTransportPressureHint = false;
                    nextHint = Math.Min(configuredCap, currentHint + 1);
                    lowAgeStableTicks = 0;
                }
            }
            else
            {
                lowAgeStableTicks = 0;
            }

            if (currentCaptureSource is not IScreenCaptureAdaptiveTuning tunableCaptureSource)
            {
                return;
            }

            if (nextTransportPressureHint != transportPressureHintActive)
            {
                transportPressureHintActive = nextTransportPressureHint;
                tunableCaptureSource.SetTransportPressureHint(nextTransportPressureHint);
                LogDebug(
                    $"Auto-tuned transport pressure hint to {(nextTransportPressureHint ? "lower-bandwidth" : "normal")} " +
                    $"(capture_to_send_age_ms={captureToSendAgeMs}, rate_gate_delta={rateGateDropDelta}, queue_evict_delta={queueEvictDropDelta}).");
            }

            if (nextHint == currentHint)
            {
                ApplySenderDegradedMode(
                    currentPipeline,
                    currentCaptureSource,
                    currentSessionId,
                    nextSenderDegradedMode,
                    nextSenderDegradedMode
                        ? (fileTransferDegradedHint ? "file_transfer" : hasQueuePressure ? "queue_pressure" : "capture_age")
                        : "recovered");
                return;
            }

            captureFpsHint = nextHint;
            tunableCaptureSource.SetCaptureFrameRateHint(nextHint);
            currentPipeline.SetMaxFramesPerSecond(nextHint);
            LogDebug(
                $"Auto-tuned capture fps hint to {nextHint} " +
                $"(capture_to_send_age_ms={captureToSendAgeMs}, rate_gate_delta={rateGateDropDelta}, queue_evict_delta={queueEvictDropDelta}).");
            ApplySenderDegradedMode(
                currentPipeline,
                currentCaptureSource,
                currentSessionId,
                nextSenderDegradedMode,
                nextSenderDegradedMode
                    ? (fileTransferDegradedHint ? "file_transfer" : hasQueuePressure ? "queue_pressure" : "capture_age")
                    : "recovered");
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

    private static long ConsumeAutoTuneCounterDelta(long currentValue, ref long previousValue)
    {
        var delta = Math.Max(0, currentValue - previousValue);
        previousValue = currentValue;
        return delta;
    }

    private bool IsSenderDegradedModeActive()
    {
        lock (gate)
        {
            return senderDegradedModeActive;
        }
    }

    internal event EventHandler<ScreenShareSenderDegradedModeChangedEventArgs>? SenderDegradedModeChanged;

    private static bool ComputeLocalSenderPressure(ScreenShareFrameSendPipeline? pipeline)
    {
        if (pipeline is null)
        {
            return false;
        }

        var metrics = pipeline.GetMetricsSnapshot();
        return pipeline.PendingFrameCount > 1 ||
               pipeline.PendingSignalCount > 0 ||
               metrics.LastCaptureToSendAgeMs >= HighCaptureToSendAgeMs;
    }

    private void ApplySenderDegradedMode(
        ScreenShareFrameSendPipeline? currentPipeline,
        IScreenCaptureSource? currentCaptureSource,
        string currentSessionId,
        bool shouldEnable,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(currentSessionId))
        {
            lock (gate)
            {
                currentSessionId = sessionId;
                if (string.IsNullOrWhiteSpace(currentSessionId))
                {
                    currentSessionId = lastActiveSessionId;
                }
            }
        }

        var now = clock.UtcNow;
        bool entered;
        bool exited;
        string effectiveReason;
        lock (gate)
        {
            if (fileTransferCatchUpOnlyHintActive)
            {
                shouldEnable = true;
                reason = "file_transfer_pressure";
                lastSenderDegradedPressureUtc = now;
            }
            else if (shouldEnable)
            {
                lastSenderDegradedPressureUtc = now;
            }
            else if (senderDegradedModeActive &&
                     lastSenderDegradedPressureUtc is DateTimeOffset lastPressureUtc &&
                     now - lastPressureUtc < SenderDegradedExitHoldDuration)
            {
                shouldEnable = true;
                reason = "sticky_hold";
            }

            entered = shouldEnable && !senderDegradedModeActive;
            exited = !shouldEnable && senderDegradedModeActive;
            senderDegradedModeActive = shouldEnable;
            effectiveReason = reason;

            if (exited)
            {
                lastSenderDegradedPressureUtc = null;
            }
        }

        if (currentPipeline is not null)
        {
            currentPipeline.SetMaxFramesPerSecond(shouldEnable ? DegradedSenderFramesPerSecond : FeatureFlags.ScreenShareTransportMaxFps);
            if (shouldEnable)
            {
                var droppedQueuedFrames = currentPipeline.FlushPendingFrames();
                if (droppedQueuedFrames > 0)
                {
                    LocalOperationalLog.Info(
                        "ScreenShareTransport",
                        $"event=screenshare_sender_frame_dropped_backlog; session_id={currentSessionId}; dropped_count={droppedQueuedFrames}");
                }
            }
            else if (exited)
            {
                currentPipeline.FlushPendingFrames();
                currentPipeline.ResetPacingWindow();
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_sender_refresh_requested; session_id={currentSessionId}; reason={effectiveReason}");
            }
        }

        if (currentCaptureSource is IScreenCaptureAdaptiveTuning tunableCaptureSource)
        {
            var normalHint = Math.Clamp(
                Math.Min(FeatureFlags.ScreenShareMaxFps, FeatureFlags.ScreenShareTransportMaxFps),
                Math.Min(MinAutoTuneFramesPerSecond, FeatureFlags.ScreenShareTransportMaxFps),
                FeatureFlags.ScreenShareTransportMaxFps);
            tunableCaptureSource.SetCaptureFrameRateHint(shouldEnable ? DegradedSenderFramesPerSecond : (captureFpsHint <= 0 ? normalHint : captureFpsHint));
            tunableCaptureSource.SetTransportPressureHint(shouldEnable || transportPressureHintActive);
        }

        if (entered)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_degraded_entered; session_id={currentSessionId}; reason={effectiveReason}");
            SenderDegradedModeChanged?.Invoke(this, new ScreenShareSenderDegradedModeChangedEventArgs(true));
        }
        else if (exited)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_degraded_exited; session_id={currentSessionId}; reason={effectiveReason}");
            SenderDegradedModeChanged?.Invoke(this, new ScreenShareSenderDegradedModeChangedEventArgs(false));
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
        string publishSessionId;
        long publishLifecycleGeneration;
        long revision;
        ScreenShareDisplayInfoSnapshot sentSnapshot;
        DisplayInfoMappingKey sentMapping;
        var flushedQueuedFrames = 0;
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
                if (sendPipeline is not null)
                {
                    flushedQueuedFrames = sendPipeline.FlushPendingFrames();
                }
                ClearPendingDisplayInfoUnsafe();
            }

            if (string.IsNullOrWhiteSpace(sessionId) ||
                captureSource is null ||
                sendPipeline is null)
            {
                return;
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
            publishSessionId = sessionId;
            publishLifecycleGeneration = lifecycleGeneration;
        }

        if (flushedQueuedFrames > 0)
        {
            LogDebug(
                $"Dropped {flushedQueuedFrames} queued frame(s) before display info send " +
                $"(display_id={message.DisplayId}, revision={message.Revision}, frame={message.FrameWidth}x{message.FrameHeight}).");
        }

        _ = BackgroundTaskRunner.Run(
            async () =>
            {
                if (!ShouldSendDisplayInfo(
                        publishSessionId,
                        publishLifecycleGeneration,
                        sentSnapshot,
                        sentMapping,
                        revision))
                {
                    LogDisplayInfoSendSuppressed(message);
                    LogDebug($"Display info suppressed because ownership changed before send (display_id={message.DisplayId}, revision={message.Revision}).");
                    return;
                }

                try
                {
                    await sendDisplayInfoAsync(publishSessionId, message, CancellationToken.None).ConfigureAwait(false);
                    Interlocked.Increment(ref displayInfoSendCount);
                    LogDebug($"Display info sent (display_id={message.DisplayId}, revision={message.Revision}, frame={message.FrameWidth}x{message.FrameHeight}).");
                }
                catch
                {
                    ResetDisplayInfoRetryStateIfCurrent(
                        publishSessionId,
                        publishLifecycleGeneration,
                        sentSnapshot,
                        sentMapping,
                        revision);

                    throw;
                }
            },
            source: "ScreenShareTransport",
            operationName: "send_display_info",
            contextProvider: () => $"revision={revision}; frame={message.FrameWidth}x{message.FrameHeight}");
    }

    private void ClearPendingDisplayInfoUnsafe()
    {
        pendingDisplayInfo = null;
        pendingDisplayInfoMapping = null;
        pendingDisplayInfoNotBeforeUtc = default;
    }

    private bool ShouldSendDisplayInfo(
        string expectedSessionId,
        long expectedLifecycleGeneration,
        ScreenShareDisplayInfoSnapshot expectedSnapshot,
        DisplayInfoMappingKey expectedMapping,
        long expectedRevision)
    {
        lock (gate)
        {
            return captureSource is not null &&
                sendPipeline is not null &&
                string.Equals(sessionId, expectedSessionId, StringComparison.Ordinal) &&
                lifecycleGeneration == expectedLifecycleGeneration &&
                lastSentDisplayInfo.HasValue &&
                lastSentDisplayInfo.Value.Equals(expectedSnapshot) &&
                lastSentDisplayInfoMapping.HasValue &&
                lastSentDisplayInfoMapping.Value.Equals(expectedMapping) &&
                lastSentDisplayInfoRevision == expectedRevision;
        }
    }

    private void LogDisplayInfoSendSuppressed(ControlDisplayInfoMessageV1 message)
    {
        var nowTicks = Environment.TickCount64;
        var windowTicks = (long)Math.Max(1d, TimeSpan.FromSeconds(2).TotalMilliseconds);
        lock (diagnosticRateLimitGate)
        {
            if (nowTicks - lastDisplayInfoSuppressedLogTick < windowTicks)
            {
                return;
            }

            lastDisplayInfoSuppressedLogTick = nowTicks;
        }

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=display_info_send_suppressed; reason=ownership_changed; display_id={message.DisplayId}; revision={message.Revision}; frame={message.FrameWidth}x{message.FrameHeight}");
    }

    private void ResetDisplayInfoRetryStateIfCurrent(
        string expectedSessionId,
        long expectedLifecycleGeneration,
        ScreenShareDisplayInfoSnapshot expectedSnapshot,
        DisplayInfoMappingKey expectedMapping,
        long expectedRevision)
    {
        lock (gate)
        {
            if (string.Equals(sessionId, expectedSessionId, StringComparison.Ordinal) &&
                lifecycleGeneration == expectedLifecycleGeneration &&
                lastSentDisplayInfo.HasValue &&
                lastSentDisplayInfo.Value.Equals(expectedSnapshot) &&
                lastSentDisplayInfoMapping.HasValue &&
                lastSentDisplayInfoMapping.Value.Equals(expectedMapping) &&
                lastSentDisplayInfoRevision == expectedRevision)
            {
                // Retry on subsequent frames if this send failed while still current.
                lastSentDisplayInfo = null;
                lastSentDisplayInfoMapping = null;
            }
        }
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

            var metrics = GetMetricsSnapshot();
            var latency = currentPipeline.GetDebugLatencySnapshotAndReset();
            var heapBytes = GC.GetTotalMemory(false);
            using var process = Process.GetCurrentProcess();
            LogDebug(
                $"Snapshot heap={heapBytes} ws={process.WorkingSet64} queued={metrics.FramesQueued} dropped={metrics.FramesDropped} " +
                $"drop_rate={metrics.FramesDroppedByRateGate} drop_evict={metrics.FramesDroppedByQueueEvict} sent={metrics.ChunksSent} " +
                $"raw_bytes={metrics.RawFrameBytesSent} serialized_bytes={metrics.SerializedChunkBytesSent} bridge_bytes={metrics.BridgeBytesSent} " +
                $"display_info={metrics.DisplayInfoSendCount} avg_c2e={metrics.AverageCaptureToEnqueueMs:F1}ms " +
                $"avg_q2s={metrics.AverageEnqueueToSendMs:F1}ms avg_c2s={metrics.AverageCaptureToSendMs:F1}ms " +
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
