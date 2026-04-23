using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Logging;
using Avalonia.Threading;
using NLink.App.Services.ScreenCapture;
using NLink.App.ViewModels;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;
using NLink.Infra.Nkn;

namespace NLink.App;

internal static class ScreenShareSoakRunner
{
    private static readonly TimeSpan DefaultDuration = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DefaultSampleInterval = TimeSpan.FromSeconds(5);

    internal sealed record ScreenShareSoakRunnerOptions(
        TimeSpan Duration,
        TimeSpan SampleInterval);

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            await error.WriteLineAsync("FAIL: --screenshare-soak is only supported on Windows.");
            return 1;
        }

        if (!TryParseOptions(args, out var options, out var parseError))
        {
            await error.WriteLineAsync($"FAIL: {parseError}");
            return 1;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linkedCts.CancelAfter(options!.Duration + TimeSpan.FromSeconds(30));
        EnsureViewerDecodePlatformInitialized();

        var reassembler = new ScreenShareVideoFrameReassembler();
        long framesSent = 0;
        long enqueueFailures = 0;
        await using var coordinator = new TransportScreenShareCoordinator(
            captureSourceFactory: ScreenCaptureFactory.CreateForTransport,
            sendPayloadAsync: (payload, sendCt) =>
            {
                _ = sendCt;
                if (ScreenShareVideoPayloadCodec.TryDeserializeFragmentEnvelope(payload.Span, out var fragments, out var isBatch))
                {
                    _ = isBatch;
                    foreach (var fragment in fragments)
                    {
                        reassembler.OnFragment(fragment);
                        if (fragment.FragmentIndex == fragment.FragmentCount - 1)
                        {
                            Interlocked.Increment(ref framesSent);
                        }
                    }
                }

                return Task.CompletedTask;
            },
            sendVideoStreamConfigAsync: (config, _) =>
            {
                reassembler.OnStreamConfig(config);
                return Task.CompletedTask;
            },
            sendDisplayInfoAsync: (_, _, _) => Task.CompletedTask,
            estimateBridgeBytes: payload => NknBridgePayloadAccounting.MeasureSendFrameBytes(
                destination: "screenshare-soak",
                payload.Span));
        using var viewer = new ScreenShareViewerViewModel(
            postToUiAsync: action =>
            {
                action();
                return Task.CompletedTask;
            });

        reassembler.FrameReady += (_, frame) => viewer.OnOwnedEncodedFrame(
            frame.Encoding,
            frame.EncodedFrameBytes,
            frame.CapturedTsUtcMs,
            frame.IsKeyFrame,
            frame.StreamEpoch,
            frame.StreamConfig,
            frameId: frame.FrameId,
            sessionId: frame.SessionId,
            recoveryDeliveryClass: frame.RecoveryDeliveryClass,
            frameReadyObservedUtcMs: frame.FrameReadyObservedUtcMs);

        try
        {
            await output.WriteLineAsync("ScreenShare soak runner");
            await output.WriteLineAsync($"  Duration: {options.Duration}");
            await output.WriteLineAsync($"  Sample interval: {options.SampleInterval}");

            await coordinator.StartAsync("screenshare-soak", linkedCts.Token).ConfigureAwait(false);
            var startedAt = DateTimeOffset.UtcNow;
            var nextSampleAt = startedAt;

            while (DateTimeOffset.UtcNow - startedAt < options.Duration)
            {
                linkedCts.Token.ThrowIfCancellationRequested();
                await TryFlushUiAsync(linkedCts.Token).ConfigureAwait(false);

                var now = DateTimeOffset.UtcNow;
                if (now >= nextSampleAt)
                {
                    var senderMetrics = coordinator.GetMetricsSnapshot();
                    var viewerMetrics = viewer.GetMetricsSnapshot();
                    var healthSnapshot = BuildHealthSnapshot(senderMetrics, viewerMetrics);
                    LocalOperationalLog.Info("ScreenShare", healthSnapshot.ToLogMessage());
                    await output.WriteLineAsync(BuildMetricsLine(
                        elapsed: now - startedAt,
                        framesSent: Interlocked.Read(ref framesSent),
                        senderMetrics: senderMetrics,
                        receiverMetrics: reassembler.GetMetricsSnapshot(),
                        viewerMetrics: viewerMetrics,
                        enqueueFailures: Interlocked.Read(ref enqueueFailures)));
                    nextSampleAt = now + options.SampleInterval;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(250), linkedCts.Token).ConfigureAwait(false);
            }

            await coordinator.StopAsync(sendStopMessage: false, reason: "soak_complete", linkedCts.Token).ConfigureAwait(false);
            await TryFlushUiAsync(linkedCts.Token).ConfigureAwait(false);
            await WaitUntilAsync(
                condition: () => viewer.IsIdleForDiagnostics,
                timeout: TimeSpan.FromSeconds(5),
                pollInterval: TimeSpan.FromMilliseconds(50),
                failureMessage: "Viewer did not become idle after screenshare stop.",
                ct: linkedCts.Token).ConfigureAwait(false);

            var stableSnapshot = await WaitForStableMetricsAsync(
                getSnapshot: () => CreateStopSnapshot(coordinator, reassembler, viewer, framesSent, enqueueFailures),
                timeout: TimeSpan.FromSeconds(5),
                pollInterval: TimeSpan.FromMilliseconds(50),
                stablePolls: 5,
                ct: linkedCts.Token).ConfigureAwait(false);

            viewer.Clear();

            await output.WriteLineAsync("Final metrics");
            LocalOperationalLog.Info("ScreenShare", BuildHealthSnapshot(stableSnapshot.SenderMetrics, stableSnapshot.ViewerMetrics).ToLogMessage());
            await output.WriteLineAsync(BuildMetricsLine(
                elapsed: options.Duration,
                framesSent: stableSnapshot.FramesSent,
                senderMetrics: stableSnapshot.SenderMetrics,
                receiverMetrics: stableSnapshot.ReceiverMetrics,
                viewerMetrics: stableSnapshot.ViewerMetrics,
                enqueueFailures: stableSnapshot.EnqueueFailures));
            await output.WriteLineAsync(BuildFrameLossReport("screenshare-soak"));
            await output.WriteLineAsync("Screenshare soak completed cleanly.");
            return 0;
        }
        catch (OperationCanceledException) when (linkedCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            await error.WriteLineAsync("FAIL: screenshare soak timed out.");
            return 1;
        }
        catch (Exception ex)
        {
            await error.WriteLineAsync($"FAIL: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
        finally
        {
            await coordinator.StopAsync(sendStopMessage: false, reason: "soak_finalize", CancellationToken.None).ConfigureAwait(false);
        }
    }

    internal static bool TryParseOptionsForTests(string[] args, out ScreenShareSoakRunnerOptions? options, out string error)
        => TryParseOptions(args, out options, out error);

    private static void EnsureViewerDecodePlatformInitialized()
    {
        if (Application.Current is not null)
        {
            return;
        }

        AppBuilder.Configure<ScreenShareSoakApplication>()
            .UsePlatformDetect()
            .LogToTrace(LogEventLevel.Warning)
            .SetupWithoutStarting();
    }

    private static bool TryParseOptions(string[] args, out ScreenShareSoakRunnerOptions? options, out string error)
    {
        options = null;
        error = string.Empty;

        var duration = DefaultDuration;
        var sampleInterval = DefaultSampleInterval;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (!arg.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            string key;
            string? value = null;
            var eq = arg.IndexOf('=');
            if (eq > 0)
            {
                key = arg[..eq];
                value = arg[(eq + 1)..];
            }
            else
            {
                key = arg;
                if (key is "--screenshare-soak")
                {
                    continue;
                }

                if (i + 1 < args.Length)
                {
                    value = args[++i];
                }
            }

            switch (key.ToLowerInvariant())
            {
                case "--screenshare-soak":
                    break;
                case "--seconds":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSeconds) || parsedSeconds <= 0)
                    {
                        error = "Invalid --seconds value.";
                        return false;
                    }

                    duration = TimeSpan.FromSeconds(parsedSeconds);
                    break;
                case "--sample-interval-seconds":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedIntervalSeconds) || parsedIntervalSeconds <= 0)
                    {
                        error = "Invalid --sample-interval-seconds value.";
                        return false;
                    }

                    sampleInterval = TimeSpan.FromSeconds(parsedIntervalSeconds);
                    break;
            }
        }

        if (sampleInterval > duration)
        {
            sampleInterval = duration;
        }

        options = new ScreenShareSoakRunnerOptions(duration, sampleInterval);
        return true;
    }

    private static string BuildMetricsLine(
        TimeSpan elapsed,
        long framesSent,
        ScreenShareMetrics senderMetrics,
        ScreenShareMetrics receiverMetrics,
        ScreenShareMetrics viewerMetrics,
        long enqueueFailures)
    {
        var healthSnapshot = BuildHealthSnapshot(senderMetrics, viewerMetrics);
        return string.Format(
            CultureInfo.InvariantCulture,
            "[{0:mm\\:ss}] FramesCaptured={1} FramesSent={2} FramesDropped={3} FramesDroppedByRateGate={4} " +
            "FramesDroppedByQueueEvict={5} FramesDeferredToSendSlot={6} FramesReplacedBeforeSendSlot={7} SendSlotEmptyCount={8} SlotCoalescingActive={9} FramesCompleted={10} FramesSuperseded={11} FramesEnqueuedForDecode={12} FramesDroppedBeforeDecode={13} FramesDecoded={14} FramesDroppedAfterDecode={15} FramesApplied={16} DecodeErrors={17} EnqueueFailures={18} " +
            "NeedMoreInputCount={19} CompletedWithoutPictureCount={20} EmittedDisplayableFrames={21} EmittedNonDisplayableUnits={22} DisplayableFrameRatio={23:F2} IdrFrameRatio={24:F2} AvgEncodedFrameBytes={25:F1} TransportIpOnlyMode={26} LastAccessUnitKind={27} LowDelayConfigApplied={28} " +
            "EmittedPFrames={29} DroppedBFrames={30} DroppedMultiPictureUnits={31} DisplayInfoSends={32} AvgCaptureToEnqueueMs={33:F1} AvgEnqueueToSendMs={34:F1} AvgCaptureToSendMs={35:F1} LastCaptureToSendAgeMs={36} " +
            "AvgFragmentsPerFrame={37:F2} AvgTransportPayloadsPerFrame={38:F2} BatchedPayloads={39} LegacyFragmentPayloads={40} " +
            "AvgRawFrameBytes={41:F1} AvgSerializedChunkBytes={42:F1} AvgBridgeBytes={43:F1} " +
            "AvgReceiveIntervalMs={44:F1} AvgDecodeDurationMs={45:F1} AvgDecodeToApplyWaitMs={46:F1} AvgApplyDurationMs={47:F1} AvgApplyIntervalMs={48:F1} " +
            "AvgDecodeIntervalMs={49:F1} AvgRenderIntervalMs={50:F1} AvgCaptureToRenderMs={51:F1} StaleFrameRenders={52} " +
            "ReassemblerLossCount={53} EnqueueRejectCount={54} DecodeWorkerDropCount={55} PostDecodeDropCount={56} UnattributedLossCount={57} " +
            "SenderOperatingState={58} SenderGuardState={59} HelperSessionPhase={60} HelperRecoveryMechanism={61} DominantLossClass={62} DominantPressureBlocker={63} DominantTroubleDomain={64}",
            elapsed,
            senderMetrics.FramesCaptured,
            framesSent,
            senderMetrics.FramesDropped,
            senderMetrics.FramesDroppedByRateGate,
            senderMetrics.FramesDroppedByQueueEvict,
            senderMetrics.FramesDeferredToSendSlot,
            senderMetrics.FramesReplacedBeforeSendSlot,
            senderMetrics.SendSlotEmptyCount,
            senderMetrics.SlotCoalescingActive ? 1 : 0,
            receiverMetrics.FramesCompleted,
            receiverMetrics.FramesSuperseded,
            viewerMetrics.FramesEnqueuedForDecode,
            viewerMetrics.FramesDroppedBeforeDecode,
            viewerMetrics.FramesDecoded,
            viewerMetrics.FramesDroppedAfterDecode,
            viewerMetrics.FramesApplied,
            viewerMetrics.DecodeErrors,
            enqueueFailures,
            viewerMetrics.NeedMoreInputCount,
            viewerMetrics.CompletedWithoutPictureCount,
            senderMetrics.EmittedDisplayableFrames,
            senderMetrics.EmittedNonDisplayableUnits,
            senderMetrics.DisplayableFrameRatio,
            senderMetrics.IdrFrameRatio,
            senderMetrics.AverageEncodedFrameBytes,
            senderMetrics.TransportIpOnlyMode ? 1 : 0,
            senderMetrics.LastAccessUnitKind,
            senderMetrics.LowDelayConfigApplied,
            senderMetrics.PFramesEmitted,
            senderMetrics.DroppedBFrames,
            senderMetrics.DroppedMultiPictureUnits,
            senderMetrics.DisplayInfoSendCount,
            senderMetrics.AverageCaptureToEnqueueMs,
            senderMetrics.AverageEnqueueToSendMs,
            senderMetrics.AverageCaptureToSendMs,
            senderMetrics.LastCaptureToSendAgeMs,
            senderMetrics.AverageFragmentsPerFrame,
            senderMetrics.AverageTransportPayloadsPerFrame,
            senderMetrics.BatchedPayloadsSent,
            senderMetrics.LegacyFragmentPayloadsSent,
            framesSent > 0 ? senderMetrics.RawFrameBytesSent / (double)framesSent : 0d,
            framesSent > 0 ? senderMetrics.SerializedChunkBytesSent / (double)framesSent : 0d,
            framesSent > 0 ? senderMetrics.BridgeBytesSent / (double)framesSent : 0d,
            viewerMetrics.AverageReceiveIntervalMs,
            viewerMetrics.AverageDecodeDurationMs,
            viewerMetrics.AverageDecodeToApplyWaitMs,
            viewerMetrics.AverageApplyDurationMs,
            viewerMetrics.AverageApplyIntervalMs,
            viewerMetrics.AverageDecodeIntervalMs,
            viewerMetrics.AverageRenderIntervalMs,
            viewerMetrics.AverageCaptureToRenderMs,
            viewerMetrics.StaleFrameRenders,
            viewerMetrics.ReassemblerLossCount,
            viewerMetrics.EnqueueRejectCount,
            viewerMetrics.DecodeWorkerDropCount,
            viewerMetrics.PostDecodeDropCount,
            viewerMetrics.UnattributedLossCount,
            ScreenShareConceptualModelFormatter.FormatSenderOperatingState(healthSnapshot.SenderOperatingState),
            ScreenShareConceptualModelFormatter.FormatSenderGuardState(healthSnapshot.SenderGuardState),
            ScreenShareConceptualModelFormatter.FormatHelperSessionPhase(healthSnapshot.HelperSessionPhase),
            ScreenShareConceptualModelFormatter.FormatHelperRecoveryMechanism(healthSnapshot.HelperRecoveryMechanism),
            ScreenShareConceptualModelFormatter.FormatLossClass(healthSnapshot.DominantLossClass),
            healthSnapshot.DominantPressureBlocker,
            ScreenShareConceptualModelFormatter.FormatTroubleDomain(healthSnapshot.DominantTroubleDomain));
    }

    private static ScreenShareOperationalHealthSnapshot BuildHealthSnapshot(
        ScreenShareMetrics senderMetrics,
        ScreenShareMetrics viewerMetrics)
    {
        return ScreenShareOperationalHealthSnapshotBuilder.Build(senderMetrics, viewerMetrics);
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        TimeSpan pollInterval,
        string failureMessage,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await TryFlushUiAsync(ct).ConfigureAwait(false);
            if (condition())
            {
                return;
            }

            await Task.Delay(pollInterval, ct).ConfigureAwait(false);
        }

        await TryFlushUiAsync(ct).ConfigureAwait(false);
        if (!condition())
        {
            throw new TimeoutException(failureMessage);
        }
    }

    private static async Task<StopSnapshot> WaitForStableMetricsAsync(
        Func<StopSnapshot> getSnapshot,
        TimeSpan timeout,
        TimeSpan pollInterval,
        int stablePolls,
        CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        var stableCount = 0;
        StopSnapshot? previous = null;

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            await TryFlushUiAsync(ct).ConfigureAwait(false);
            var current = getSnapshot();
            if (previous is not null && current.Equals(previous))
            {
                stableCount++;
                if (stableCount >= stablePolls)
                {
                    return current;
                }
            }
            else
            {
                stableCount = 1;
            }

            previous = current;
            await Task.Delay(pollInterval, ct).ConfigureAwait(false);
        }

        throw new TimeoutException("Screenshare metrics did not stabilize after stop.");
    }

    private static Task TryFlushUiAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Loaded);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Best-effort only. The soak runner should keep polling even if the dispatcher is unavailable.
        }

        return Task.CompletedTask;
    }

    private static string BuildFrameLossReport(string sessionId)
    {
        var snapshot = ScreenShareFrameLossAttributionRegistry.GetSnapshot(sessionId);
        return string.Format(
            CultureInfo.InvariantCulture,
            "HelperFrameLossReport SessionId={0} FragmentSeen={1} Assembled={2} Ready={3} Emitted={4} ViewerAccepted={5} DecodeEnqueued={6} Decoded={7} Applied={8} ReassemblerLossCount={9} EnqueueRejectCount={10} DecodeWorkerDropCount={11} PostDecodeDropCount={12} GapNonKeyPrunedCount={13} RecoveryKeyframeResyncCount={14} GapActive={15} GapExpectedFrameId={16} BufferedRecoveryKeyframeFrameId={17} FutureNonKeyBufferedCount={18} UnattributedLossCount={19} RecentLosses={20}",
            string.IsNullOrWhiteSpace(snapshot.SessionId) ? "(none)" : snapshot.SessionId,
            snapshot.FragmentSeenFrames,
            snapshot.FramesAssembled,
            snapshot.FramesReady,
            snapshot.FramesEmitted,
            snapshot.ViewerAcceptedFrames,
            snapshot.DecodeEnqueuedFrames,
            snapshot.FramesDecoded,
            snapshot.FramesApplied,
            snapshot.ReassemblerLossCount,
            snapshot.EnqueueRejectCount,
            snapshot.DecodeWorkerDropCount,
            snapshot.PostDecodeDropCount,
            snapshot.GapNonKeyPrunedCount,
            snapshot.RecoveryKeyframeResyncCount,
            snapshot.GapActive ? 1 : 0,
            snapshot.GapExpectedFrameId,
            snapshot.BufferedRecoveryKeyframeFrameId,
            snapshot.FutureNonKeyBufferedCount,
            snapshot.UnattributedLossCount,
            ScreenShareFrameLossAttributionRegistry.FormatRecentLosses(snapshot.RecentLosses));
    }

    private static StopSnapshot CreateStopSnapshot(
        TransportScreenShareCoordinator coordinator,
        ScreenShareVideoFrameReassembler reassembler,
        ScreenShareViewerViewModel viewer,
        long framesSent,
        long enqueueFailures)
    {
        return new StopSnapshot(
            SenderMetrics: coordinator.GetMetricsSnapshot(),
            ReceiverMetrics: reassembler.GetMetricsSnapshot(),
            ViewerMetrics: viewer.GetMetricsSnapshot(),
            FramesSent: Interlocked.Read(ref framesSent),
            EnqueueFailures: Interlocked.Read(ref enqueueFailures));
    }

    private sealed record StopSnapshot(
        ScreenShareMetrics SenderMetrics,
        ScreenShareMetrics ReceiverMetrics,
        ScreenShareMetrics ViewerMetrics,
        long FramesSent,
        long EnqueueFailures);

    private sealed class ScreenShareSoakApplication : Application
    {
        public override void Initialize()
        {
        }
    }
}
