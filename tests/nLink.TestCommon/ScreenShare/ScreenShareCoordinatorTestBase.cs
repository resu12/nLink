using System.Runtime.InteropServices;
using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NLink.App.Configuration;
using NLink.App.Services.ScreenCapture;
using NLink.App.Views;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;
using NLink.Infra.Nkn;
using NLink.SmokeTests.Fakes;
using System.Collections.Concurrent;

namespace NLink.SmokeTests;

public abstract class ScreenShareCoordinatorTestBase : IClassFixture<ScreenShareCoordinatorFixture>
{
internal const int TransportClarityFpsFloorForTesting = 5;

internal readonly ScreenShareCoordinatorFixture fixture;

protected ScreenShareCoordinatorTestBase(ScreenShareCoordinatorFixture fixture)
    {
        this.fixture = fixture;
    }

internal static async Task RunRapidFramesThrottledScenarioAsync(string iterationLabel)
    {
        var fakeSource = new FakeScreenCaptureSource();
        var probe = new ScreenShareSendProbe(recentPayloadCapacity: 4);
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 3, 18, 0, 0, TimeSpan.Zero));

        var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: probe.SendReadOnlyPayloadAsync,
            clock: clock);

        try
        {
            await AwaitCompletesAsync(
                coordinator.StartAsync("session-live", CancellationToken.None),
                TimeSpan.FromSeconds(2),
                $"{iterationLabel}: transport start");
            DisableStartupWarmupForCoordinatorOnly(coordinator);

            for (var i = 0; i < 5; i++)
            {
                RaiseTransportFrame(fakeSource, 1, 1, new byte[] { (byte)(i + 1) });
                // Keep frame cadence well above the transport min interval (8 FPS -> 125 ms)
                // so this scenario remains deterministic even if the default transport cap changes.
                clock.Advance(TimeSpan.FromMilliseconds(40));
            }

            await AwaitCompletesAsync(
                probe.WaitForPayloadCountAsync(1, TimeSpan.FromSeconds(2)),
                TimeSpan.FromSeconds(2),
                $"{iterationLabel}: first throttled payload send");
            await Task.Delay(350);
            await AwaitCompletesAsync(
                coordinator.StopAsync(sendStopMessage: false, reason: null, CancellationToken.None),
                TimeSpan.FromSeconds(2),
                $"{iterationLabel}: throttled stop");

            var sentPayloads = probe.GetRecentPayloadsSnapshot();
            Assert.InRange(probe.PayloadsSent, 1, 2);
            Assert.InRange(sentPayloads.Length, 1, 2);
            var firstChunk = Assert.Single(ExpandFragmentsFromPayload(sentPayloads[0]));
            Assert.Equal("session-live", firstChunk.SessionId);
            Assert.Equal(0, firstChunk.FrameId);
            if (sentPayloads.Length > 1)
            {
                var secondChunk = Assert.Single(ExpandFragmentsFromPayload(sentPayloads[1]));
                Assert.Equal("session-live", secondChunk.SessionId);
                Assert.Equal(1, secondChunk.FrameId);
            }
        }
        finally
        {
            await AwaitCompletesAsync(
                coordinator.DisposeAsync().AsTask(),
                TimeSpan.FromSeconds(2),
                $"{iterationLabel}: transport dispose");
        }
    }

internal static void DriveTransportFrames(
        FakeScreenCaptureSource fakeSource,
        FakeScreenShareClock clock,
        int count,
        TimeSpan advancePerFrame)
    {
        for (var i = 0; i < count; i++)
        {
            RaiseTransportFrame(fakeSource, 1, 1, new byte[] { (byte)((i % 250) + 1) });
            clock.Advance(advancePerFrame);
        }
    }

internal static async Task RunStopOrDisconnectUnderLoadScenarioAsync(
        string scenarioLabel,
        Func<TransportScreenShareCoordinator, Task> stopAsync)
    {
        using var unobserved = new UnobservedTaskExceptionRecorder();
        var fakeSource = new FakeScreenCaptureSource();
        var clock = new FakeScreenShareClock(new DateTimeOffset(2026, 3, 4, 12, 0, 0, TimeSpan.Zero));
        var sendEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: () => fakeSource,
            sendPayloadAsync: async (_, ct) =>
            {
                sendEntered.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
            },
            clock: clock);

        await AwaitCompletesAsync(
            coordinator.StartAsync("session-load", CancellationToken.None),
            TimeSpan.FromSeconds(2),
            $"{scenarioLabel}: start");
        DisableStartupWarmupForCoordinatorOnly(coordinator);

        RaiseTransportFrame(fakeSource, 640, 360, new byte[] { 0, 1, 2 });
        await AwaitCompletesAsync(
            sendEntered.Task,
            TimeSpan.FromSeconds(2),
            $"{scenarioLabel}: blocked send entry");

        for (var frameIndex = 1; frameIndex <= 24; frameIndex++)
        {
            RaiseTransportFrame(fakeSource, 640, 360, new byte[] { (byte)frameIndex, 7, 9 });
            clock.Advance(TimeSpan.FromMilliseconds(500));
        }

        await AwaitCompletesAsync(
            stopAsync(coordinator),
            TimeSpan.FromSeconds(2),
            $"{scenarioLabel}: stop/disconnect");

        Assert.False(coordinator.IsActive);
        Assert.False(fakeSource.IsStarted);

        ForceFullCollection();
        Assert.Empty(unobserved.Exceptions);
    }

internal static void RaiseTransportFrame(
        FakeScreenCaptureSource fakeSource,
        int width,
        int height,
        byte[] encodedFrameBytes,
        long capturedTsUtcMs = 0,
        long streamEpoch = 1,
        bool isKeyFrame = true)
    {
        fakeSource.RaiseFrame(
            CreateTransportFrameEventArgs(
                width,
                height,
                encodedFrameBytes,
                capturedTsUtcMs,
                streamEpoch,
                isKeyFrame));
    }

internal static ScreenCaptureFrameEventArgs CreateTransportFrameEventArgs(
        int width,
        int height,
        byte[] encodedFrameBytes,
        long capturedTsUtcMs = 0,
        long streamEpoch = 1,
        bool isKeyFrame = true)
    {
        return new ScreenCaptureFrameEventArgs(
            width,
            height,
            encodedFrameBytes,
            "h264",
            capturedTsUtcMs <= 0 ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : capturedTsUtcMs,
            isKeyFrame,
            streamEpoch,
            new ScreenShareVideoStreamConfigV1
            {
                SessionId = "session-live",
                StreamEpoch = streamEpoch,
                Encoding = "h264",
                CodecProfile = "baseline",
                DecoderConfigData = new byte[] { 1, 2, 3 },
            });
    }

internal static Bitmap CreateTinyBitmap()
    {
        var bytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/a5kAAAAASUVORK5CYII=");
        using var stream = new MemoryStream(bytes, writable: false);
        return new Bitmap(stream);
    }

internal static ScreenShareVideoFragmentV1[] ExpandFragmentsFromPayload(byte[] payload)
    {
        Assert.True(
            ScreenShareVideoPayloadCodec.TryDeserializeFragmentEnvelope(payload, out var fragments, out _),
            "Expected payload to deserialize as either a legacy fragment or a fragment batch.");
        return fragments;
    }

internal static Bitmap CreateBitmap(int width, int height)
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

internal static void DeliverMatchingHelperVisibleReceipt(
        TransportScreenShareCoordinator coordinator,
        string sessionId,
        long streamEpoch,
        long ownerFrameId,
        long? visibleRecoveryFrameId = null,
        long? visibleHeadFrameId = null)
    {
        var effectiveVisibleRecoveryFrameId = visibleRecoveryFrameId ?? ownerFrameId;
        var effectiveVisibleHeadFrameId = Math.Max(
            effectiveVisibleRecoveryFrameId,
            visibleHeadFrameId ?? effectiveVisibleRecoveryFrameId);
        coordinator.SetRemoteRecoveryReceipt(
            new ScreenShareRecoveryReceiptV1
            {
                SessionId = sessionId,
                StreamEpoch = streamEpoch,
                OwnerFrameId = ownerFrameId,
                VisibleRecoveryFrameId = effectiveVisibleRecoveryFrameId,
                VisibleHeadFrameId = effectiveVisibleHeadFrameId,
                ReceiptKind = effectiveVisibleRecoveryFrameId == ownerFrameId
                    ? ScreenShareRecoveryReceiptCodec.RecoveryKeyframeVisibleReceiptKind
                    : ScreenShareRecoveryReceiptCodec.VisibleProgressAfterRecoveryKeyframeReceiptKind,
            });
    }

internal static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await TryPumpUiThreadOnceAsync().ConfigureAwait(false);
            await Task.Delay(10).ConfigureAwait(false);
        }

        Assert.True(predicate(), $"Condition not met within {timeout.TotalSeconds:N1}s.");
    }

internal static async Task TryPumpUiThreadOnceAsync()
    {
        try
        {
            var pumpTask = Dispatcher.UIThread
                .InvokeAsync(static () => { }, DispatcherPriority.Background)
                .GetTask();
            var completed = await Task.WhenAny(pumpTask, Task.Delay(25)).ConfigureAwait(false);
            if (ReferenceEquals(completed, pumpTask))
            {
                await pumpTask.ConfigureAwait(false);
            }
        }
        catch
        {
            // Best-effort UI pump for tests. If dispatcher is unavailable/stalled, continue polling.
        }
    }

internal static void WaitForSignal(Task signal, TimeSpan timeout)
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

internal static void ForceFullCollection()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

internal static async Task AwaitCompletesAsync(Task operation, TimeSpan timeout, string phase)
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

internal static void DisableStartupWarmupForAutoTuneTests(
        TransportScreenShareCoordinator coordinator,
        AdaptiveFakeScreenCaptureSource fakeSource,
        int? initialFpsHint = null)
    {
        if (typeof(TransportScreenShareCoordinator)
                .GetField("autoTuneTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(coordinator) is Timer autoTuneTimer)
        {
            autoTuneTimer.Dispose();
            typeof(TransportScreenShareCoordinator)
                .GetField("autoTuneTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(coordinator, null);
        }

        var targetFps = initialFpsHint ?? FeatureFlags.ScreenShareTransportMaxFps;
        SetPrivateFieldValue(coordinator, "startupWarmupUntilUtc", default(DateTimeOffset));
        SetPrivateFieldValue(coordinator, "captureFpsHint", targetFps);
        SetPrivateFieldValue(coordinator, "captureToSendCatchUpPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "remoteObservedCatchUpPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "normalToReducedPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "catchUpRecoveryLowPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "reducedRecoveryLowPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "remoteHighFrameAgeCatchUpEntryConsecutiveTicks", 0);
        SetPrivateFieldValue(coordinator, "senderCatchUpEnteredDueToRemoteHighFrameAgeCount", 0L);
        SetPrivateFieldValue(coordinator, "transitionActive", false);
        SetPrivateFieldValue(coordinator, "transitionStreamEpoch", 0L);
        SetPrivateFieldValue(coordinator, "transitionStartedUtc", default(DateTimeOffset));
        SetPrivateFieldValue(coordinator, "transitionFirstRemoteApplySeen", false);
        SetPrivateFieldValue(coordinator, "transitionRemoteApplyCount", 0);
        SetPrivateFieldValue(coordinator, "recoveryLockActive", false);
        SetPrivateFieldValue(coordinator, "recoveryLockStreamEpoch", 0L);
        SetPrivateFieldValue(coordinator, "recoveryLockStartedUtc", default(DateTimeOffset));
        SetPrivateFieldValue(coordinator, "recoveryLockReason", string.Empty);
        SetPrivateFieldValue(coordinator, "recoveryTimeoutResetIssued", false);
        SetPrivateFieldValue(coordinator, "recoveryTimeoutResetCount", 0);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochStateStreamEpoch", 0L);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochWarmupActive", true);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochApplyCount", 0);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochNeedMoreInputCount", 0L);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochHealthySignalCount", 0);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochStaleDrops", 0L);
        fakeSource.SetCaptureFrameRateHint(targetFps);

        var sendPipeline = typeof(TransportScreenShareCoordinator)
            .GetField("sendPipeline", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator) as ScreenShareFrameSendPipeline;
        sendPipeline?.SetMaxFramesPerSecond(targetFps);
    }

internal static void DisableStartupWarmupForCoordinatorOnly(
        TransportScreenShareCoordinator coordinator,
        int targetFps = 8)
    {
        if (typeof(TransportScreenShareCoordinator)
                .GetField("autoTuneTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(coordinator) is Timer autoTuneTimer)
        {
            autoTuneTimer.Dispose();
            typeof(TransportScreenShareCoordinator)
                .GetField("autoTuneTimer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(coordinator, null);
        }

        SetPrivateFieldValue(coordinator, "startupWarmupUntilUtc", default(DateTimeOffset));
        SetPrivateFieldValue(coordinator, "captureFpsHint", targetFps);
        SetPrivateFieldValue(coordinator, "senderFreshnessMode", ScreenShareSenderFreshnessMode.Normal);
        SetPrivateFieldValue(coordinator, "transportTuningLevel", ScreenShareTransportTuningLevel.Normal);
        SetPrivateFieldValue(coordinator, "preferFreshestPendingFrameOnly", 0);
        SetPrivateFieldValue(coordinator, "captureToSendCatchUpPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "remoteObservedCatchUpPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "normalToReducedPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "catchUpRecoveryLowPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "reducedRecoveryLowPressureTicks", 0);
        SetPrivateFieldValue(coordinator, "remoteHighFrameAgeCatchUpEntryConsecutiveTicks", 0);
        SetPrivateFieldValue(coordinator, "senderCatchUpEnteredDueToRemoteHighFrameAgeCount", 0L);
        SetPrivateFieldValue(coordinator, "transitionActive", false);
        SetPrivateFieldValue(coordinator, "transitionStreamEpoch", 0L);
        SetPrivateFieldValue(coordinator, "transitionStartedUtc", default(DateTimeOffset));
        SetPrivateFieldValue(coordinator, "transitionFirstRemoteApplySeen", false);
        SetPrivateFieldValue(coordinator, "transitionRemoteApplyCount", 0);
        SetPrivateFieldValue(coordinator, "recoveryLockActive", false);
        SetPrivateFieldValue(coordinator, "recoveryLockStreamEpoch", 0L);
        SetPrivateFieldValue(coordinator, "recoveryLockStartedUtc", default(DateTimeOffset));
        SetPrivateFieldValue(coordinator, "recoveryLockReason", string.Empty);
        SetPrivateFieldValue(coordinator, "recoveryTimeoutResetIssued", false);
        SetPrivateFieldValue(coordinator, "recoveryTimeoutResetCount", 0);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochStateStreamEpoch", 0L);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochWarmupActive", true);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochApplyCount", 0);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochNeedMoreInputCount", 0L);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochHealthySignalCount", 0);
        SetPrivateFieldValue(coordinator, "helperCurrentEpochStaleDrops", 0L);

        var sendPipeline = typeof(TransportScreenShareCoordinator)
            .GetField("sendPipeline", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator) as ScreenShareFrameSendPipeline;
        sendPipeline?.SetMaxFramesPerSecond(targetFps);

        var captureSource = typeof(TransportScreenShareCoordinator)
            .GetField("captureSource", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator);
        if (captureSource is IScreenCaptureAdaptiveTuning adaptiveCaptureSource)
        {
            adaptiveCaptureSource.SetCaptureFrameRateHint(targetFps);
            adaptiveCaptureSource.SetTransportTuningLevel(ScreenShareTransportTuningLevel.Normal);
        }
    }

internal static void SetLastCaptureToSendAgeMs(TransportScreenShareCoordinator coordinator, long captureToSendAgeMs)
    {
        var sendPipeline = typeof(TransportScreenShareCoordinator)
            .GetField("sendPipeline", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(coordinator) as ScreenShareFrameSendPipeline;
        Assert.NotNull(sendPipeline);

        typeof(ScreenShareFrameSendPipeline)
            .GetField("lastCaptureToSendAgeMs", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(sendPipeline, captureToSendAgeMs);
    }

internal static T GetPrivateFieldValue<T>(object target, string fieldName)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is not null)
        {
            var value = field.GetValue(target);
            if (typeof(T) == typeof(object))
            {
                return (T)value!;
            }

            return Assert.IsType<T>(value);
        }

        var property = target.GetType().GetProperty(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (property is not null)
        {
            var value = property.GetValue(target);
            if (typeof(T) == typeof(object))
            {
                return (T)value!;
            }

            return Assert.IsType<T>(value);
        }

        if (TryGetLegacyRecoveryFieldValue(target, fieldName, out var remappedValue))
        {
            if (typeof(T) == typeof(object))
            {
                return (T)remappedValue!;
            }

            return Assert.IsType<T>(remappedValue);
        }

        Assert.NotNull(field);
        return Assert.IsType<T>(field!.GetValue(target));
    }

internal static void SetPrivateFieldValue(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field is not null)
        {
            field.SetValue(target, value);
            return;
        }

        var property = target.GetType().GetProperty(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        if (property is not null)
        {
            property.SetValue(target, value);
            return;
        }

        Assert.NotNull(field);
    }

internal static bool TryGetLegacyRecoveryFieldValue(object target, string fieldName, out object? value)
    {
        value = null;
        if (target is not TransportScreenShareCoordinator)
        {
            return false;
        }

        var activeRecoveryBurst = GetNestedPrivateFieldValue(target, "activeRecoveryBurst");
        var lastCompletedRecovery = GetNestedPrivateFieldValue(target, "lastCompletedRecovery");
        value = fieldName switch
        {
            "recoveryBurstActive" => activeRecoveryBurst is not null,
            "recoveryBurstStreamEpoch" => GetNestedPrivateFieldValue(activeRecoveryBurst, "StreamEpoch") ?? 0L,
            "recoveryOwnerFrameId" => GetNestedPrivateFieldValue(activeRecoveryBurst, "OwnerFrameId") ?? -1L,
            "recoveryBurstPhase" => GetNestedPrivateFieldValue(activeRecoveryBurst, "Phase") ?? RecoveryBurstPhase.Idle,
            "lastCompletedRecoveryEpoch" => GetNestedPrivateFieldValue(lastCompletedRecovery, "StreamEpoch") ?? 0L,
            "lastCompletedRecoveryOwnerFrameId" => GetNestedPrivateFieldValue(lastCompletedRecovery, "OwnerFrameId") ?? -1L,
            "lastCompletedRecoveryAckFrameId" => GetNestedPrivateFieldValue(lastCompletedRecovery, "AckFrameId") ?? -1L,
            "lastCompletedRecoveryAckSource" => GetNestedPrivateFieldValue(lastCompletedRecovery, "AckSource") ?? string.Empty,
            "lastCompletedRecoveryOwnerEmitToAckMs" => GetNestedPrivateFieldValue(lastCompletedRecovery, "OwnerEmitToAckMs") ?? -1L,
            "lastCompletedRecoveryCompletionKind" => GetNestedPrivateFieldValue(lastCompletedRecovery, "CompletionKind") ?? string.Empty,
            _ => null,
        };

        return value is not null;
    }

internal static object? GetNestedPrivateFieldValue(object? target, string fieldName)
    {
        if (target is null)
        {
            return null;
        }

        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        if (field is not null)
        {
            return field.GetValue(target);
        }

        var property = target.GetType().GetProperty(fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        return property?.GetValue(target);
    }

internal static void SendHealthyRemotePressure(
        TransportScreenShareCoordinator coordinator,
        long observedFrameAgeMs = 0,
        long recentStaleDrops = 0,
        bool? currentEpochWarmupActive = false,
        int? currentEpochApplyCount = 3,
        long? currentEpochNeedMoreInputCount = 0,
        long? lastVisibleApplyFrameId = null,
        long? visibleHeadFrameId = null,
        long? appliedHeadFrameId = null,
        bool? steadyVisibleProgressActive = null,
        long? stableVisibleHeadFrameId = null,
        long? framesAppliedSinceLastGap = null,
        long? visibleRecoveryFloorFrameId = null,
        long? currentEpochRecoveryKeyframeApplyCount = null)
    {
        coordinator.SetRemotePressureState(
            mode: ScreenShareRemotePressureMode.None,
            reason: ScreenSharePressureProtocol.PressureReasonHealthy,
            observedFrameAgeMs: observedFrameAgeMs,
            recentStaleDrops: recentStaleDrops,
            sentAtUtcMs: 0,
            currentEpochWarmupActive: currentEpochWarmupActive,
            currentEpochApplyCount: currentEpochApplyCount,
            currentEpochNeedMoreInputCount: currentEpochNeedMoreInputCount,
            lastVisibleApplyFrameId: lastVisibleApplyFrameId,
            visibleHeadFrameId: visibleHeadFrameId,
            appliedHeadFrameId: appliedHeadFrameId,
            steadyVisibleProgressActive: steadyVisibleProgressActive,
            stableVisibleHeadFrameId: stableVisibleHeadFrameId,
            framesAppliedSinceLastGap: framesAppliedSinceLastGap,
            visibleRecoveryFloorFrameId: visibleRecoveryFloorFrameId,
            currentEpochRecoveryKeyframeApplyCount: currentEpochRecoveryKeyframeApplyCount);
    }

internal static TextBlock? FindViewerMessageText(Window window)
    {
        return window.GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(x =>
                string.Equals(
                    AutomationProperties.GetAutomationId(x),
                    "ScreenShare.ViewerMessage",
                    StringComparison.Ordinal));
    }

internal sealed class FixedCaptureSourceFactory : IScreenCaptureSourceFactory
    {
        private readonly IScreenCaptureSource source;

        public FixedCaptureSourceFactory(IScreenCaptureSource source)
        {
            this.source = source;
        }

        public IScreenCaptureSource Create() => source;
    }

internal sealed class SequenceCaptureSourceFactory : IScreenCaptureSourceFactory
    {
        private readonly Queue<IScreenCaptureSource> sources;

        public SequenceCaptureSourceFactory(params IScreenCaptureSource[] sources)
        {
            this.sources = new Queue<IScreenCaptureSource>(sources);
        }

        public IScreenCaptureSource Create()
        {
            if (sources.Count == 0)
            {
                throw new InvalidOperationException("No capture sources remain in the test factory.");
            }

            return sources.Dequeue();
        }
    }

internal sealed class AdaptiveFakeScreenCaptureSource :
        IScreenCaptureSource,
        IScreenCaptureMetadataSource,
        IScreenCaptureAdaptiveTuning,
        IScreenCaptureKeyFrameRequestSource,
        IScreenCaptureTransportRecoveryResetSource,
        IScreenCaptureFreshnessMetricsSource,
        IScreenCaptureCursorCaptureControl,
        IAsyncDisposable
    {
        private EventHandler<ScreenCaptureFrameEventArgs>? frameArrived;
        private readonly List<int> captureFrameRateHints = new();
        private readonly List<ScreenShareTransportTuningLevel> transportTuningLevels = new();
        private readonly List<string> keyFrameRequestReasons = new();
        private readonly List<bool> cursorCaptureEnabledRequests = new();
        private ScreenCaptureFreshnessMetrics freshnessMetrics = new();
        private bool cursorCaptureEnabled = true;

        public bool IsSupported => true;

        public bool IsStarted { get; private set; }

        public int LastCaptureFrameRateHint { get; private set; }

        public IReadOnlyList<int> CaptureFrameRateHints => captureFrameRateHints;

        public ScreenShareTransportTuningLevel LastTransportTuningLevel { get; private set; }

        public IReadOnlyList<ScreenShareTransportTuningLevel> TransportTuningLevels => transportTuningLevels;

        public int PurgePendingRawFramesCallCount { get; private set; }

        public IReadOnlyList<string> KeyFrameRequestReasons => keyFrameRequestReasons;

        public IReadOnlyList<bool> CursorCaptureEnabledRequests => cursorCaptureEnabledRequests;

        public bool IsCursorCaptureControlSupported { get; set; } = true;

        public bool IsCursorCaptureEnabled => cursorCaptureEnabled;

        public ScreenCaptureMetadata? CaptureMetadata { get; set; }

        public event EventHandler<ScreenCaptureFrameEventArgs>? FrameArrived
        {
            add => frameArrived += value;
            remove => frameArrived -= value;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsStarted = true;
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            IsStarted = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            IsStarted = false;
            frameArrived = null;
            keyFrameRequestReasons.Clear();
            cursorCaptureEnabledRequests.Clear();
            cursorCaptureEnabled = true;
            freshnessMetrics = new ScreenCaptureFreshnessMetrics();
            return ValueTask.CompletedTask;
        }

        public bool TryGetCaptureMetadata(out ScreenCaptureMetadata metadata)
        {
            if (CaptureMetadata.HasValue)
            {
                metadata = CaptureMetadata.Value;
                return true;
            }

            metadata = default;
            return false;
        }

        public void SetCaptureFrameRateHint(int maxFramesPerSecond)
        {
            LastCaptureFrameRateHint = maxFramesPerSecond;
            captureFrameRateHints.Add(maxFramesPerSecond);
        }

        public void SetTransportTuningLevel(ScreenShareTransportTuningLevel level)
        {
            if (LastTransportTuningLevel != level)
            {
                var nextEpoch = freshnessMetrics.CurrentStreamEpoch > 0
                    ? freshnessMetrics.CurrentStreamEpoch + 1
                    : 1;
                freshnessMetrics = freshnessMetrics with
                {
                    CurrentStreamEpoch = nextEpoch,
                };
            }

            LastTransportTuningLevel = level;
            transportTuningLevels.Add(level);
        }

        public void RequestKeyFrame(string reason)
        {
            keyFrameRequestReasons.Add(string.IsNullOrWhiteSpace(reason) ? "(none)" : reason.Trim());
        }

        public bool TrySetCursorCaptureEnabled(bool enabled, string reason)
        {
            cursorCaptureEnabledRequests.Add(enabled);
            if (!IsCursorCaptureControlSupported)
            {
                cursorCaptureEnabled = true;
                freshnessMetrics = freshnessMetrics with
                {
                    CursorCaptureControlSupported = false,
                    CursorCaptureEnabled = true,
                    CursorCaptureFallbackReason = "unsupported",
                };
                return false;
            }

            cursorCaptureEnabled = enabled;
            freshnessMetrics = freshnessMetrics with
            {
                CursorCaptureControlSupported = true,
                CursorCaptureEnabled = enabled,
                CursorCaptureFallbackReason = string.Empty,
            };
            return true;
        }

        public long ForceTransportRecoveryReset(ScreenShareTransportTuningLevel level)
        {
            LastTransportTuningLevel = level;
            transportTuningLevels.Add(level);
            var nextEpoch = freshnessMetrics.CurrentStreamEpoch > 0
                ? freshnessMetrics.CurrentStreamEpoch + 1
                : 1;
            freshnessMetrics = freshnessMetrics with
            {
                CurrentStreamEpoch = nextEpoch,
            };
            return nextEpoch;
        }

        public ScreenCaptureFreshnessMetrics GetFreshnessMetricsSnapshot()
        {
            return freshnessMetrics;
        }

        public int PurgePendingRawFrames()
        {
            if (freshnessMetrics.PendingRawFrameCount <= 0)
            {
                return 0;
            }

            PurgePendingRawFramesCallCount++;
            freshnessMetrics = freshnessMetrics with
            {
                PendingRawFrameCount = 0,
                OldestPendingRawFrameAgeMs = 0,
            };
            return 1;
        }

        public void SetFreshnessMetrics(ScreenCaptureFreshnessMetrics metrics)
        {
            freshnessMetrics = metrics;
        }

        public void RaiseFrame(ScreenCaptureFrameEventArgs frame)
        {
            frameArrived?.Invoke(this, frame);
        }
    }

internal sealed class ScreenShareViewerErrorContext
    {
        public bool ShowDefaultScreenSharePlaceholder => false;

        public bool ShowScreenShareViewerError => true;

        public string ScreenShareViewerMessage => "Screen sharing failed to start";

        public bool ShowRemoteScreenShareFrame => false;

        public bool ShowScreenSharePreviewFrame => false;
    }

internal sealed class ScreenSharePlaceholderContext
    {
        public bool ShowDefaultScreenSharePlaceholder => !ShowRemoteScreenShareFrame &&
                                                         !ShowScreenSharePreviewFrame &&
                                                         !ShowScreenShareViewerError;

        public bool ShowScreenShareViewerError { get; init; }

        public string ScreenShareViewerMessage { get; init; } = string.Empty;

        public bool ShowRemoteScreenShareFrame { get; init; }

        public Bitmap? RemoteFrame { get; init; }

        public ScreenShareViewerProxy ScreenShareViewer => new(RemoteFrame);

        public bool ShowScreenSharePreviewFrame { get; init; }

        public Bitmap? PreviewFrame { get; init; }

        public Bitmap? ScreenSharePreviewFrame => PreviewFrame;
    }

internal sealed class ScreenShareViewerProxy
    {
        public ScreenShareViewerProxy(Bitmap? currentFrame)
        {
            CurrentFrame = currentFrame;
        }

        public Bitmap? CurrentFrame { get; }
    }

internal sealed class FakeScreenShareClock : IScreenShareClock
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

internal sealed class FakePreviewH264BitmapDecoder : IWindowsH264BitmapDecoder
    {
        public bool IsSupported => true;

        public int NeedMoreInputBeforeSuccessCount { get; set; }

        public int ConfigureCallCount { get; private set; }

        public int DecodeCallCount { get; private set; }

        public int ResetCallCount { get; private set; }

        public long LastConfiguredEpoch { get; private set; }

        public void ConfigureStream(ScreenShareVideoStreamConfigV1 config)
        {
            ConfigureCallCount++;
            LastConfiguredEpoch = config.StreamEpoch;
        }

        public void Reset()
        {
            ResetCallCount++;
        }

        public Bitmap Decode(EncodedFrameDecodeRequest request)
        {
            DecodeCallCount++;
            if (NeedMoreInputBeforeSuccessCount > 0)
            {
                NeedMoreInputBeforeSuccessCount--;
                throw new H264DecoderNeedsMoreInputException("more input required");
            }

            return CreateBitmap(request.EncodedFrameBytes.Span[0], 1);
        }

        public void Dispose()
        {
        }
    }

internal sealed class FakeScreenShareBackpressureProbe : IScreenShareTransportBackpressureProbe
    {
        public bool IsCongested { get; set; }

        public bool IsSeverelyCongested { get; set; }

        public int QueueDepth { get; set; }

        public int QueuedBytes { get; set; }

        public long OldestQueuedAgeMs { get; set; }

        public long RecentDropCount { get; set; }

        public long RecentHealthIssueCount { get; set; }

        public bool IsHealthSeverelyDegraded { get; set; }

        public bool IsScreenShareTransportCongested => IsCongested;

        public bool IsScreenShareTransportSeverelyCongested => IsSeverelyCongested;

        public int ScreenShareTransportQueueDepth => QueueDepth;

        public int ScreenShareTransportQueuedBytes => QueuedBytes;

        public long ScreenShareTransportOldestQueuedAgeMs => OldestQueuedAgeMs;

        public long ScreenShareTransportRecentDropCount => RecentDropCount;

        public long ScreenShareTransportRecentHealthIssueCount => RecentHealthIssueCount;

        public bool IsScreenShareTransportHealthSeverelyDegraded => IsHealthSeverelyDegraded;
    }

internal sealed class UnobservedTaskExceptionRecorder : IDisposable
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


