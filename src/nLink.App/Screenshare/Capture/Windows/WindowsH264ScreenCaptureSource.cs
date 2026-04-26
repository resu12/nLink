using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using NLink.App.Configuration;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

[SupportedOSPlatform("windows")]
internal sealed class WindowsH264ScreenCaptureSource : IScreenCaptureSource, IScreenCaptureMetadataSource, IScreenCaptureAdaptiveTuning, IScreenCaptureKeyFrameRequestSource, IScreenCaptureFreshnessMetricsSource, IScreenCaptureTransportRecoveryResetSource, IScreenCaptureCursorCaptureControl, IAsyncDisposable
{
    private static readonly TimeSpan SupersededPendingRawFrameLogInterval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EncodeCadenceWakePollInterval = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan CpuSampleMinInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan EncodedFpsSampleMinInterval = TimeSpan.FromMilliseconds(500);
    private readonly object sync = new();
    private readonly ScreenCaptureTargetSelection captureTarget;
    private readonly Func<IWindowsRawCaptureSource> rawCaptureSourceFactory;
    private readonly Func<IWindowsRawCaptureSource> windowsGraphicsCaptureSourceFactory;
    private readonly Func<IWindowsRawCaptureSource> desktopDuplicationSourceFactory;
    private readonly Func<IWindowsH264FrameEncoder> encoderFactory;
    private readonly string sourceRole;

    private IWindowsRawCaptureSource? rawCaptureSource;
    private IWindowsH264FrameEncoder? encoder;
    private CancellationTokenSource? captureCts;
    private CancellationTokenSource? encodeCts;
    private Task? startTask;
    private Task? rawEncodeLoopTask;
    private PendingRawFrame? pendingRawFrame;
    private bool started;
    private bool disposed;
    private bool rawEncodeLoopActive;
    private int captureFrameRateHint;
    private ScreenShareTransportTuningLevel tuningLevel;
    private long streamEpoch;
    private int pendingForceKeyFrame;
    private int runtimeFallbackInProgress;
    private bool loggedTerminalEncoderFailure;
    private bool wgcStartSucceeded;
    private string? wgcFirstFailureStage;
    private bool wgcFallbackAttempted;
    private bool wgcFallbackStarted;
    private bool desktopDuplicationDisabledForSession;
    private bool wgcSummaryLogged;
    private bool terminalCaptureFailureLogged;
    private long supersededPendingRawFrameCount;
    private long rawFramesDeferredToEncodeSlot;
    private long rawFramesReplacedBeforeEncodeSlot;
    private long rawCaptureEventCount;
    private long rawFramesSkippedBeforeEncode;
    private long rawEncodeSlotEmptyCount;
    private int rawSlotCoalescingActive;
    private long lastTransportEncodeStartedUtcMs;
    private long lastEncodedStreamEpoch;
    private long actualEncodedFpsSampleUtcMs;
    private long actualEncodedFpsSampleDisplayableFrames;
    private double actualEncodedDisplayableFps;
    private long lastCpuSampleTimestamp;
    private long lastCpuSampleProcessorTicks;
    private double senderProcessCpuPercent = -1;
    private long lastCaptureToEncodeStartAgeMs = -1;
    private long lastEncodeDurationMs = -1;
    private long emittedDisplayableFrames;
    private long emittedNonDisplayableUnits;
    private long idrFramesEmitted;
    private long pFramesEmitted;
    private long droppedBFrames;
    private long droppedMultiPictureUnits;
    private double displayableFrameRatio;
    private double averageEncodedFrameBytes;
    private long lastPreprocessDurationMs = -1;
    private long lastPreprocessResizeDurationMs = -1;
    private long lastPreprocessColorConvertDurationMs = -1;
    private string preprocessResizePath = string.Empty;
    private long preprocessDirectNv12Count;
    private long lastTransformEncodeDurationMs = -1;
    private long lastEncodeTotalDurationMs = -1;
    private double idrFrameRatio;
    private bool transportIpOnlyMode;
    private string lastAccessUnitKind = string.Empty;
    private string lowDelayConfigApplied = string.Empty;
    private bool senderContinuityRecoveryActive;
    private long senderContinuityLossCount;
    private long framesDroppedWaitingForRecoveryKeyframe;
    private string lastSenderContinuityLossReason = string.Empty;
    private int activeTargetWidth;
    private int activeTargetHeight;
    private long activeTargetBitrate;
    private int activeTargetFramesPerSecond;
    private string activeEncoderPath = string.Empty;
    private string activeEncoderProfile = string.Empty;
    private bool motionIntegrityGuardActive;
    private double motionIntegritySampledRatio;
    private double motionIntegrityPeakSampledRatio;
    private int motionIntegrityScrollMotionActiveBandCount;
    private double motionIntegrityScrollMotionPeakBandRatio;
    private long motionIntegrityHighMotionFrameCount;
    private long motionIntegrityScrollTriggerCount;
    private long motionIntegrityBurstEnterCount;
    private long motionIntegrityBurstExitCount;
    private long motionIntegrityForcedKeyFrameCount;
    private string motionIntegrityLastTriggerKind = string.Empty;
    private string motionIntegrityLastReason = string.Empty;
    private double motionIntegrityIdrFrameRatio;
    private long motionIntegrityForcedIdrRequestedCount;
    private long motionIntegrityForcedIdrConfirmedCount;
    private long motionIntegrityForcedIdrMissedCount;
    private long motionIntegrityForcedIdrPendingCount;
    private long motionIntegrityForcedIdrConsecutiveMissCount;
    private long motionIntegrityForcedIdrBurstMissCount;
    private double motionIntegrityActiveIdrFrameRatio;
    private string motionIntegrityForcedIdrLastMissReason = string.Empty;
    private long motionIntegrityEncoderRebuildCount;
    private long motionIntegrityEncoderRebuildSuppressedCount;
    private bool motionIntegrityEncoderRebuildPending;
    private string motionIntegrityEncoderRebuildLastReason = string.Empty;
    private DateTimeOffset lastSupersededPendingRawFrameLogUtc;
    private bool desiredCursorCaptureEnabled = true;
    private bool cursorCaptureEnabled = true;
    private bool cursorCaptureControlSupported;
    private string cursorCaptureFallbackReason = string.Empty;
    private string cursorCaptureApplyStatus = string.Empty;

    public WindowsH264ScreenCaptureSource()
        : this(ScreenCaptureTargetStore.Load(), sourceRole: "preview")
    {
    }

    internal WindowsH264ScreenCaptureSource(ScreenCaptureTargetSelection captureTarget, string sourceRole = "preview")
        : this(
            captureTarget,
            rawCaptureSourceFactory: () => CreateDefaultRawSource(captureTarget, sourceRole),
            encoderFactory: () => CreateDefaultEncoder(sourceRole),
            sourceRole: sourceRole)
    {
    }

    internal WindowsH264ScreenCaptureSource(
        ScreenCaptureTargetSelection captureTarget,
        Func<IWindowsRawCaptureSource> rawCaptureSourceFactory,
        Func<IWindowsH264FrameEncoder> encoderFactory,
        Func<IWindowsRawCaptureSource>? windowsGraphicsCaptureSourceFactory = null,
        Func<IWindowsRawCaptureSource>? desktopDuplicationSourceFactory = null,
        string sourceRole = "unknown")
    {
        this.captureTarget = captureTarget;
        this.rawCaptureSourceFactory = rawCaptureSourceFactory ?? throw new ArgumentNullException(nameof(rawCaptureSourceFactory));
        this.windowsGraphicsCaptureSourceFactory = windowsGraphicsCaptureSourceFactory ?? (() => new WindowsGraphicsCaptureRawSource(captureTarget, sourceRole));
        this.desktopDuplicationSourceFactory = desktopDuplicationSourceFactory ?? (() => new DesktopDuplicationRawSource(captureTarget));
        this.encoderFactory = encoderFactory ?? throw new ArgumentNullException(nameof(encoderFactory));
        this.sourceRole = string.IsNullOrWhiteSpace(sourceRole) ? "unknown" : sourceRole.Trim().ToLowerInvariant();
        captureFrameRateHint = Math.Min(FeatureFlags.ScreenShareMaxFps, FeatureFlags.ScreenShareTransportMaxFps);
    }

    public bool IsSupported
    {
        get
        {
            return ProbeSupport(rawCaptureSourceFactory, encoderFactory);
        }
    }

    public event EventHandler<ScreenCaptureFrameEventArgs>? FrameArrived;

    public bool IsCursorCaptureControlSupported
    {
        get
        {
            lock (sync)
            {
                return cursorCaptureControlSupported;
            }
        }
    }

    public bool IsCursorCaptureEnabled
    {
        get
        {
            lock (sync)
            {
                return cursorCaptureEnabled;
            }
        }
    }

    public static bool IsRuntimeSupported()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        return (WindowsGraphicsCaptureRawSource.IsRuntimeSupported() || DesktopDuplicationRawSource.IsRuntimeSupported()) &&
               ProbeEncoderSupport(static () => MediaFoundationH264FrameEncoder.TryCreate());
    }

    public static bool IsPreviewRuntimeSupported()
    {
        return IsRuntimeSupported() &&
               ProbeDecoderSupport(static () => WindowsH264BitmapDecoderFactory.TryCreate());
    }

    public bool TryGetCaptureMetadata(out ScreenCaptureMetadata metadata)
    {
        if (rawCaptureSource?.TryGetCaptureMetadata(out metadata) == true)
        {
            return true;
        }

        return WindowsScreenCaptureTargetCatalog.TryResolveTarget(captureTarget, fallbackDpiScale: null, out metadata, out _);
    }

    public void SetCaptureFrameRateHint(int maxFramesPerSecond)
    {
        lock (sync)
        {
            captureFrameRateHint = Math.Max(1, maxFramesPerSecond);
            ApplyRawCaptureCadence_NoLock(rawCaptureSource, "frame_rate_hint");
        }
    }

    public void SetTransportTuningLevel(ScreenShareTransportTuningLevel level)
    {
        lock (sync)
        {
            if (tuningLevel == level)
            {
                return;
            }

            tuningLevel = level;
            if (!started)
            {
                return;
            }

            streamEpoch = Math.Max(1, streamEpoch + 1);
            ApplyRawCaptureCadence_NoLock(rawCaptureSource, "tuning_level");
            ForceNextRawCapture_NoLock("tuning_level_epoch_change");
        }

        Interlocked.Exchange(ref pendingForceKeyFrame, 1);
    }

    public void RequestKeyFrame(string reason)
    {
        LogDebug($"Keyframe requested: {reason}");
        IWindowsH264FrameEncoder? currentEncoder = null;
        PendingRawFrame? purgedPendingRawFrame = null;
        long currentStreamEpoch = 0;
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "keyframe_request"
            : reason.Trim();

        lock (sync)
        {
            if (IsTransportSourceRole() &&
                ShouldStartSenderRecoveryBurst(normalizedReason))
            {
                senderContinuityRecoveryActive = true;
                lastSenderContinuityLossReason = normalizedReason;
                purgedPendingRawFrame = PurgePendingRawFrameForRecovery_NoLock();
                currentEncoder = encoder;
                currentStreamEpoch = Math.Max(0, streamEpoch);
            }

            ForceNextRawCapture_NoLock("keyframe_request");
        }

        purgedPendingRawFrame?.Dispose();
        Interlocked.Exchange(ref pendingForceKeyFrame, 1);
        currentEncoder?.StartRecoveryBurst(normalizedReason, currentStreamEpoch);
    }

    public long ForceTransportRecoveryReset(ScreenShareTransportTuningLevel level)
    {
        long nextEpoch;
        PendingRawFrame? purgedPendingRawFrame = null;
        IWindowsH264FrameEncoder? currentEncoder = null;
        lock (sync)
        {
            tuningLevel = level;
            ApplyRawCaptureCadence_NoLock(rawCaptureSource, "transport_recovery_reset");
            if (IsTransportSourceRole())
            {
                senderContinuityRecoveryActive = true;
                lastSenderContinuityLossReason = "transport_recovery_reset";
                currentEncoder = encoder;
                ForceNextRawCapture_NoLock("transport_recovery_reset");
            }

            nextEpoch = streamEpoch > 0
                ? streamEpoch + 1
                : 1;
            streamEpoch = nextEpoch;
            purgedPendingRawFrame = PurgePendingRawFrameForRecovery_NoLock();
        }

        purgedPendingRawFrame?.Dispose();
        Interlocked.Exchange(ref pendingForceKeyFrame, 1);
        currentEncoder?.StartRecoveryBurst("transport_recovery_reset", nextEpoch);
        return nextEpoch;
    }

    public ScreenCaptureFreshnessMetrics GetFreshnessMetricsSnapshot()
    {
        lock (sync)
        {
            var nowUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var emittedDisplayableFrameCount = Interlocked.Read(ref emittedDisplayableFrames);
            UpdateActualEncodedDisplayableFps_NoLock(nowUtcMs, emittedDisplayableFrameCount);
            UpdateProcessCpuPercent_NoLock();
            var pendingRawFrameCount = pendingRawFrame is null ? 0 : 1;
            long oldestPendingAgeMs = 0;
            if (pendingRawFrame is not null)
            {
                oldestPendingAgeMs = Math.Max(
                    0,
                    nowUtcMs - (pendingRawFrame.Frame.CapturedTsUtcMs > 0
                        ? pendingRawFrame.Frame.CapturedTsUtcMs
                        : pendingRawFrame.EnqueuedTsUtcMs));
            }

            var rawSourceRuntimeMetrics = rawCaptureSource is IWindowsRawCaptureCadenceControl rawCadenceControl
                ? rawCadenceControl.GetRawCaptureRuntimeMetricsSnapshot()
                : default;

            return new ScreenCaptureFreshnessMetrics(
                CurrentStreamEpoch: streamEpoch,
                PendingRawFrameCount: pendingRawFrameCount,
                OldestPendingRawFrameAgeMs: oldestPendingAgeMs,
                LastCaptureToEncodeStartAgeMs: Interlocked.Read(ref lastCaptureToEncodeStartAgeMs),
                LastEncodeDurationMs: Interlocked.Read(ref lastEncodeDurationMs),
                SupersededPendingRawFrameCount: Interlocked.Read(ref supersededPendingRawFrameCount),
                RawFramesDeferredToEncodeSlot: Interlocked.Read(ref rawFramesDeferredToEncodeSlot),
                RawFramesReplacedBeforeEncodeSlot: Interlocked.Read(ref rawFramesReplacedBeforeEncodeSlot),
                RawCaptureEventCount: Interlocked.Read(ref rawCaptureEventCount),
                RawFramesSkippedBeforeEncode: Interlocked.Read(ref rawFramesSkippedBeforeEncode),
                RawEncodeSlotEmptyCount: Interlocked.Read(ref rawEncodeSlotEmptyCount),
                RawSlotCoalescingActive: Volatile.Read(ref rawSlotCoalescingActive) == 1,
                RawSourceFrameArrivedCount: rawSourceRuntimeMetrics.FrameArrivedCount,
                RawSourceFramesSkippedBeforeReadback: rawSourceRuntimeMetrics.FramesSkippedBeforeReadback,
                RawSourceFramesReadbackCount: rawSourceRuntimeMetrics.FramesReadbackCount,
                RawSourceReadbackFps: rawSourceRuntimeMetrics.ReadbackFps,
                RawSourceLastReadbackDurationMs: rawSourceRuntimeMetrics.LastReadbackDurationMs,
                RawSourceAverageReadbackDurationMs: rawSourceRuntimeMetrics.AverageReadbackDurationMs,
                RawSourceCadenceTargetFps: rawSourceRuntimeMetrics.CadenceTargetFps,
                RawSourceUrgentBypassCount: rawSourceRuntimeMetrics.UrgentBypassCount,
                RawSourceOutputWidth: rawSourceRuntimeMetrics.OutputWidth,
                RawSourceOutputHeight: rawSourceRuntimeMetrics.OutputHeight,
                RawSourceGpuScaleEnabled: rawSourceRuntimeMetrics.GpuScaleEnabled,
                RawSourceGpuScaleFallbackReason: rawSourceRuntimeMetrics.GpuScaleFallbackReason,
                RawSourceCaptureActive: rawSourceRuntimeMetrics.CaptureActive,
                RawSourceBorderRequiredControlSupported: rawSourceRuntimeMetrics.BorderRequiredControlSupported,
                RawSourceBorderRequiredDesired: rawSourceRuntimeMetrics.BorderRequiredDesired,
                RawSourceBorderRequired: rawSourceRuntimeMetrics.BorderRequired,
                RawSourceBorderRequiredApplyStatus: rawSourceRuntimeMetrics.BorderRequiredApplyStatus,
                RawSourceBorderRequiredFallbackReason: rawSourceRuntimeMetrics.BorderRequiredFallbackReason,
                RawSourceLastStopDurationMs: rawSourceRuntimeMetrics.LastStopDurationMs,
                RawSourceLastStopReason: rawSourceRuntimeMetrics.LastStopReason,
                RawSourceActiveSessionLeaseCount: rawSourceRuntimeMetrics.ActiveSessionLeaseCount,
                RawSourceLastSessionCloseStatus: rawSourceRuntimeMetrics.LastSessionCloseStatus,
                RawSourceLastSessionCloseMethod: rawSourceRuntimeMetrics.LastSessionCloseMethod,
                RawSourceLastSessionCloseHResult: rawSourceRuntimeMetrics.LastSessionCloseHResult,
                RawSourceForceCloseCount: rawSourceRuntimeMetrics.ForceCloseCount,
                RawSourceSessionCloseAnomalyCount: rawSourceRuntimeMetrics.SessionCloseAnomalyCount,
                RawSourceSessionOwnerThreadId: rawSourceRuntimeMetrics.SessionOwnerThreadId,
                RawSourceLastSessionCloseThreadId: rawSourceRuntimeMetrics.LastSessionCloseThreadId,
                RawSourceLastSessionCloseOnOwnerThread: rawSourceRuntimeMetrics.LastSessionCloseOnOwnerThread,
                RawSourceOwnerDispatcherActive: rawSourceRuntimeMetrics.OwnerDispatcherActive,
                RawSourceOwnerThreadCloseTimeoutCount: rawSourceRuntimeMetrics.OwnerThreadCloseTimeoutCount,
                ActualEncodedDisplayableFps: Volatile.Read(ref actualEncodedDisplayableFps),
                EncodeCadenceTargetFps: Volatile.Read(ref activeTargetFramesPerSecond),
                SenderProcessCpuPercent: Volatile.Read(ref senderProcessCpuPercent),
                EmittedDisplayableFrames: emittedDisplayableFrameCount,
                EmittedNonDisplayableUnits: Interlocked.Read(ref emittedNonDisplayableUnits),
                DisplayableFrameRatio: Volatile.Read(ref displayableFrameRatio),
                IdrFramesEmitted: Interlocked.Read(ref idrFramesEmitted),
                PFramesEmitted: Interlocked.Read(ref pFramesEmitted),
                DroppedBFrames: Interlocked.Read(ref droppedBFrames),
                DroppedMultiPictureUnits: Interlocked.Read(ref droppedMultiPictureUnits),
                IdrFrameRatio: Volatile.Read(ref idrFrameRatio),
                AverageEncodedFrameBytes: Volatile.Read(ref averageEncodedFrameBytes),
                TransportIpOnlyMode: transportIpOnlyMode,
                LastAccessUnitKind: lastAccessUnitKind,
                LowDelayConfigApplied: lowDelayConfigApplied,
                SenderContinuityRecoveryActive: senderContinuityRecoveryActive,
                SenderContinuityLossCount: Interlocked.Read(ref senderContinuityLossCount),
                FramesDroppedWaitingForRecoveryKeyframe: Interlocked.Read(ref framesDroppedWaitingForRecoveryKeyframe),
                LastSenderContinuityLossReason: lastSenderContinuityLossReason,
                LastPreprocessDurationMs: Interlocked.Read(ref lastPreprocessDurationMs),
                LastPreprocessResizeDurationMs: Interlocked.Read(ref lastPreprocessResizeDurationMs),
                LastPreprocessColorConvertDurationMs: Interlocked.Read(ref lastPreprocessColorConvertDurationMs),
                PreprocessResizePath: preprocessResizePath,
                PreprocessDirectNv12Count: Interlocked.Read(ref preprocessDirectNv12Count),
                LastTransformEncodeDurationMs: Interlocked.Read(ref lastTransformEncodeDurationMs),
                LastEncodeTotalDurationMs: Interlocked.Read(ref lastEncodeTotalDurationMs),
                EncoderPath: activeEncoderPath,
                EncoderProfile: activeEncoderProfile,
                ActiveTargetWidth: Volatile.Read(ref activeTargetWidth),
                ActiveTargetHeight: Volatile.Read(ref activeTargetHeight),
                ActiveTargetBitrate: checked((uint)Math.Max(0L, Interlocked.Read(ref activeTargetBitrate))),
                ActiveTargetFramesPerSecond: Volatile.Read(ref activeTargetFramesPerSecond),
                MotionIntegrityGuardActive: motionIntegrityGuardActive,
                MotionIntegritySampledRatio: motionIntegritySampledRatio,
                MotionIntegrityPeakSampledRatio: motionIntegrityPeakSampledRatio,
                MotionIntegrityScrollMotionActiveBandCount: motionIntegrityScrollMotionActiveBandCount,
                MotionIntegrityScrollMotionPeakBandRatio: motionIntegrityScrollMotionPeakBandRatio,
                MotionIntegrityHighMotionFrameCount: motionIntegrityHighMotionFrameCount,
                MotionIntegrityScrollTriggerCount: motionIntegrityScrollTriggerCount,
                MotionIntegrityBurstEnterCount: motionIntegrityBurstEnterCount,
                MotionIntegrityBurstExitCount: motionIntegrityBurstExitCount,
                MotionIntegrityForcedKeyFrameCount: motionIntegrityForcedKeyFrameCount,
                MotionIntegrityLastTriggerKind: motionIntegrityLastTriggerKind,
                MotionIntegrityLastReason: motionIntegrityLastReason,
                MotionIntegrityIdrFrameRatio: motionIntegrityIdrFrameRatio,
                MotionIntegrityForcedIdrRequestedCount: motionIntegrityForcedIdrRequestedCount,
                MotionIntegrityForcedIdrConfirmedCount: motionIntegrityForcedIdrConfirmedCount,
                MotionIntegrityForcedIdrMissedCount: motionIntegrityForcedIdrMissedCount,
                MotionIntegrityForcedIdrPendingCount: motionIntegrityForcedIdrPendingCount,
                MotionIntegrityForcedIdrConsecutiveMissCount: motionIntegrityForcedIdrConsecutiveMissCount,
                MotionIntegrityForcedIdrBurstMissCount: motionIntegrityForcedIdrBurstMissCount,
                MotionIntegrityActiveIdrFrameRatio: motionIntegrityActiveIdrFrameRatio,
                MotionIntegrityForcedIdrLastMissReason: motionIntegrityForcedIdrLastMissReason,
                MotionIntegrityEncoderRebuildCount: motionIntegrityEncoderRebuildCount,
                MotionIntegrityEncoderRebuildSuppressedCount: motionIntegrityEncoderRebuildSuppressedCount,
                MotionIntegrityEncoderRebuildPending: motionIntegrityEncoderRebuildPending,
                MotionIntegrityEncoderRebuildLastReason: motionIntegrityEncoderRebuildLastReason,
                CursorCaptureControlSupported: cursorCaptureControlSupported,
                CursorCaptureEnabled: cursorCaptureEnabled,
                CursorCaptureFallbackReason: cursorCaptureFallbackReason,
                CursorCaptureDesiredEnabled: desiredCursorCaptureEnabled,
                CursorCaptureApplyStatus: cursorCaptureApplyStatus);
        }
    }

    public bool TrySetCursorCaptureEnabled(bool enabled, string reason)
    {
        IWindowsRawCaptureSource? currentRawSource;
        lock (sync)
        {
            desiredCursorCaptureEnabled = enabled;
            currentRawSource = rawCaptureSource;
            if (currentRawSource is null)
            {
            cursorCaptureEnabled = enabled;
            cursorCaptureFallbackReason = "queued_before_start";
            cursorCaptureApplyStatus = "queued_before_start";
            LogCursorCaptureMode("queued_before_start", enabled, supported: false, applied: false, reason);
            return false;
            }
        }

        return TryApplyCursorCapturePreferenceToRawSource(currentRawSource, enabled, reason);
    }

    private bool TryApplyCursorCapturePreferenceToRawSource(
        IWindowsRawCaptureSource rawSource,
        bool enabled,
        string reason)
    {
        lock (sync)
        {
            return TryApplyCursorCapturePreferenceToRawSource_NoLock(rawSource, enabled, reason);
        }
    }

    private bool TryApplyCursorCapturePreferenceToRawSource_NoLock(
        IWindowsRawCaptureSource rawSource,
        bool enabled,
        string reason)
    {
        if (rawSource is not IScreenCaptureCursorCaptureControl cursorControl)
        {
            cursorCaptureControlSupported = false;
            cursorCaptureEnabled = true;
            cursorCaptureFallbackReason = "unsupported_source";
            cursorCaptureApplyStatus = "unsupported_source";
            LogCursorCaptureMode("unsupported_source", enabled: true, supported: false, applied: false, reason);
            return false;
        }

        var supported = cursorControl.IsCursorCaptureControlSupported;
        var applied = cursorControl.TrySetCursorCaptureEnabled(enabled, reason);
        cursorCaptureControlSupported = supported;
        cursorCaptureEnabled = cursorControl.IsCursorCaptureEnabled;
        cursorCaptureFallbackReason = applied
            ? string.Empty
            : supported ? "apply_failed" : "unsupported";
        cursorCaptureApplyStatus = applied ? "applied" : cursorCaptureFallbackReason;
        LogCursorCaptureMode(
            applied ? "applied" : "fallback",
            cursorCaptureEnabled,
            supported,
            applied,
            reason);
        return applied;
    }

    private void LogCursorCaptureMode(string status, bool enabled, bool supported, bool applied, string reason)
    {
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_h264_cursor_capture_mode; source_role={sourceRole}; backend={(rawCaptureSource is null ? WindowsRawCaptureBackendKind.Unknown : GetRawCaptureBackendKind(rawCaptureSource))}; cursor_capture_desired_enabled={(desiredCursorCaptureEnabled ? 1 : 0)}; cursor_capture_enabled={(enabled ? 1 : 0)}; cursor_control_supported={(supported ? 1 : 0)}; applied={(applied ? 1 : 0)}; status={Sanitize(status)}; reason={Sanitize(string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason)}");
    }

    public int PurgePendingRawFrames()
    {
        PendingRawFrame? droppedFrame;
        lock (sync)
        {
            droppedFrame = ClearPendingRawFrame_NoLock();
        }

        droppedFrame?.Dispose();
        return droppedFrame is null ? 0 : 1;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        IWindowsH264FrameEncoder? createdEncoder = null;
        IWindowsRawCaptureSource? createdRawSource = null;
        CancellationTokenSource? createdCaptureCts = null;
        CancellationTokenSource? createdEncodeCts = null;
        Task? pendingStartTask = null;

        lock (sync)
        {
            if (started)
            {
                return;
            }

            createdEncoder = encoderFactory();
            if (!createdEncoder.IsSupported)
            {
                throw new NotSupportedException("Windows H.264 capture infrastructure is not supported on this system.");
            }

            createdRawSource = rawCaptureSourceFactory();
            if (!createdRawSource.IsSupported)
            {
                throw new NotSupportedException("Windows H.264 capture infrastructure is not supported on this system.");
            }

            TryApplyCursorCapturePreferenceToRawSource_NoLock(createdRawSource, desiredCursorCaptureEnabled, "start");

            createdCaptureCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            createdEncodeCts = CancellationTokenSource.CreateLinkedTokenSource(createdCaptureCts.Token);
            encoder = createdEncoder;
            rawCaptureSource = createdRawSource;
            captureCts = createdCaptureCts;
            encodeCts = createdEncodeCts;
            streamEpoch = Math.Max(1, streamEpoch + 1);
            Interlocked.Exchange(ref pendingForceKeyFrame, 1);
            loggedTerminalEncoderFailure = false;
            ResetFreshnessState_NoLock();
            ResetWgcLifetimeSummary();
            ApplyRawCaptureCadence_NoLock(createdRawSource, "start");
            ForceNextRawCapture_NoLock("start_first_frame");
            started = true;
            startTask = StartRawCaptureWithFallbackAsync(createdRawSource, createdCaptureCts.Token);
            pendingStartTask = startTask;
        }

        try
        {
            await pendingStartTask!.ConfigureAwait(false);
        }
        catch
        {
            await ResetFailedStartAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        IWindowsRawCaptureSource? oldRawSource;
        IWindowsH264FrameEncoder? oldEncoder;
        CancellationTokenSource? oldCts;
        CancellationTokenSource? oldEncodeCts;
        Task? oldRawEncodeLoopTask;
        PendingRawFrame? droppedPendingRawFrame;

        lock (sync)
        {
            if (!started)
            {
                return;
            }

            started = false;
            oldRawSource = rawCaptureSource;
            oldEncoder = encoder;
            oldCts = captureCts;
            oldEncodeCts = encodeCts;
            oldRawEncodeLoopTask = rawEncodeLoopTask;
            droppedPendingRawFrame = ClearPendingRawFrame_NoLock();
            rawCaptureSource = null;
            encoder = null;
            captureCts = null;
            encodeCts = null;
            rawEncodeLoopTask = null;
            Interlocked.Exchange(ref runtimeFallbackInProgress, 0);
            loggedTerminalEncoderFailure = false;
            TryLogWgcLifetimeSummary("stop");
        }

        droppedPendingRawFrame?.Dispose();

        if (oldRawSource is not null)
        {
            oldRawSource.FrameArrived -= OnRawFrameArrived;
            oldRawSource.CaptureFailed -= OnRawCaptureFailed;
        }

        oldCts?.Cancel();
        oldEncodeCts?.Cancel();

        try
        {
            if (oldRawSource is not null)
            {
                await oldRawSource.StopAsync().ConfigureAwait(false);
            }

            if (oldRawEncodeLoopTask is not null)
            {
                await oldRawEncodeLoopTask.ConfigureAwait(false);
            }
        }
        finally
        {
            oldCts?.Dispose();
            oldEncodeCts?.Dispose();
            if (oldRawSource is not null)
            {
                await oldRawSource.DisposeAsync().ConfigureAwait(false);
            }

            if (oldEncoder is not null)
            {
                await oldEncoder.DisposeAsync().ConfigureAwait(false);
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
        await StopAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    private async Task StartRawCaptureWithFallbackAsync(IWindowsRawCaptureSource initialSource, CancellationToken cancellationToken)
    {
        initialSource.FrameArrived += OnRawFrameArrived;
        initialSource.CaptureFailed += OnRawCaptureFailed;

        try
        {
            await initialSource.StartAsync(cancellationToken).ConfigureAwait(false);
            if (GetRawCaptureBackendKind(initialSource) == WindowsRawCaptureBackendKind.WindowsGraphicsCapture)
            {
                wgcStartSucceeded = true;
            }
        }
        catch (Exception ex) when (CanAttemptDesktopDuplicationFallback(initialSource))
        {
            wgcFallbackAttempted = true;
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_h264_raw_source_fallback; from=wgc; to=desktop_duplication; reason={ex.GetType().Name}; message={Sanitize(ex.Message)}");

            initialSource.FrameArrived -= OnRawFrameArrived;
            initialSource.CaptureFailed -= OnRawCaptureFailed;
            await initialSource.DisposeAsync().ConfigureAwait(false);

            var fallbackSource = CreateDesktopDuplicationFallbackSource();
            fallbackSource.FrameArrived += OnRawFrameArrived;
            fallbackSource.CaptureFailed += OnRawCaptureFailed;
            lock (sync)
            {
                rawCaptureSource = fallbackSource;
                ApplyRawCaptureCadence_NoLock(fallbackSource, "startup_fallback");
                ForceNextRawCapture_NoLock("startup_fallback_first_frame");
                TryApplyCursorCapturePreferenceToRawSource_NoLock(fallbackSource, desiredCursorCaptureEnabled, "startup_fallback");
            }

            try
            {
                await fallbackSource.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception fallbackEx)
            {
                DisableDesktopDuplicationForSession(
                    "startup_failed",
                    ResolveFailureStage(fallbackEx, "start_capture"),
                    fallbackEx.Message);
                fallbackSource.FrameArrived -= OnRawFrameArrived;
                fallbackSource.CaptureFailed -= OnRawCaptureFailed;
                await fallbackSource.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            wgcFallbackStarted = true;
            TryLogWgcLifetimeSummary("startup_fallback_started");
        }
    }

    private async Task ResetFailedStartAsync()
    {
        IWindowsRawCaptureSource? failedRawSource;
        IWindowsH264FrameEncoder? failedEncoder;
        CancellationTokenSource? failedCts;
        CancellationTokenSource? failedEncodeCts;
        Task? failedRawEncodeLoopTask;
        PendingRawFrame? droppedPendingRawFrame;

        lock (sync)
        {
            failedRawSource = rawCaptureSource;
            failedEncoder = encoder;
            failedCts = captureCts;
            failedEncodeCts = encodeCts;
            failedRawEncodeLoopTask = rawEncodeLoopTask;
            droppedPendingRawFrame = ClearPendingRawFrame_NoLock();
            rawCaptureSource = null;
            encoder = null;
            captureCts = null;
            encodeCts = null;
            startTask = null;
            rawEncodeLoopTask = null;
            started = false;
            Interlocked.Exchange(ref runtimeFallbackInProgress, 0);
            loggedTerminalEncoderFailure = false;
            ResetFreshnessState_NoLock();
            ResetWgcLifetimeSummary();
        }

        droppedPendingRawFrame?.Dispose();

        failedCts?.Cancel();
        failedEncodeCts?.Cancel();

        if (failedRawEncodeLoopTask is not null)
        {
            await failedRawEncodeLoopTask.ConfigureAwait(false);
        }

        if (failedRawSource is not null)
        {
            failedRawSource.FrameArrived -= OnRawFrameArrived;
            failedRawSource.CaptureFailed -= OnRawCaptureFailed;
            await failedRawSource.DisposeAsync().ConfigureAwait(false);
        }

        if (failedEncoder is not null)
        {
            await failedEncoder.DisposeAsync().ConfigureAwait(false);
        }

        failedCts?.Dispose();
        failedEncodeCts?.Dispose();
    }

    private void OnRawFrameArrived(object? sender, WindowsRawCaptureFrameEventArgs e)
    {
        PendingRawFrame? supersededPendingRawFrame = null;
        PendingRawFrame? droppedIncomingRawFrame = null;
        var shouldStartEncodeLoop = false;
        var shouldLogSupersededPendingRawFrame = false;
        var shouldCountRawFrameDeferredToEncodeSlot = false;
        var shouldCountRawFrameReplacementBeforeEncodeSlot = false;

        lock (sync)
        {
            Interlocked.Increment(ref rawCaptureEventCount);
            if (!started || encoder is null || encodeCts is null)
            {
                supersededPendingRawFrame = new PendingRawFrame(e.Frame);
            }
            else
            {
                var incomingRawFrame = new PendingRawFrame(e.Frame, Math.Max(1, streamEpoch));
                if (IsTransportSourceRole())
                {
                    if (pendingRawFrame is null)
                    {
                        pendingRawFrame = incomingRawFrame;
                        shouldCountRawFrameDeferredToEncodeSlot = rawEncodeLoopActive;
                    }
                    else if (incomingRawFrame.StreamEpoch > pendingRawFrame.StreamEpoch &&
                             !ShouldPreserveExistingPendingRawFrameForRecovery_NoLock(incomingRawFrame, pendingRawFrame))
                    {
                        supersededPendingRawFrame = ClearPendingRawFrame_NoLock();
                        pendingRawFrame = incomingRawFrame;
                        shouldCountRawFrameReplacementBeforeEncodeSlot = supersededPendingRawFrame is not null;
                    }
                    else if (!ShouldPreserveExistingPendingRawFrameForRecovery_NoLock(incomingRawFrame, pendingRawFrame))
                    {
                        supersededPendingRawFrame = ClearPendingRawFrame_NoLock();
                        pendingRawFrame = incomingRawFrame;
                        shouldCountRawFrameReplacementBeforeEncodeSlot = supersededPendingRawFrame is not null;
                    }
                    else
                    {
                        droppedIncomingRawFrame = incomingRawFrame;
                    }
                }
                else
                {
                    supersededPendingRawFrame = ClearPendingRawFrame_NoLock();
                    if (supersededPendingRawFrame is not null)
                    {
                        Interlocked.Increment(ref supersededPendingRawFrameCount);
                        shouldLogSupersededPendingRawFrame = ShouldLogSupersededPendingRawFrame_NoLock();
                    }

                    pendingRawFrame = incomingRawFrame;
                }
                if (!rawEncodeLoopActive)
                {
                    rawEncodeLoopActive = true;
                    rawEncodeLoopTask = Task.Run(ProcessPendingRawFramesAsync, CancellationToken.None);
                    shouldStartEncodeLoop = true;
                }
            }
        }

        supersededPendingRawFrame?.Dispose();
        droppedIncomingRawFrame?.Dispose();

        if (shouldCountRawFrameDeferredToEncodeSlot)
        {
            Interlocked.Increment(ref rawFramesDeferredToEncodeSlot);
        }

        if (shouldCountRawFrameReplacementBeforeEncodeSlot)
        {
            Interlocked.Increment(ref rawFramesReplacedBeforeEncodeSlot);
            Interlocked.Increment(ref rawFramesSkippedBeforeEncode);
        }

        if (shouldLogSupersededPendingRawFrame)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_raw_frame_superseded; dropped_count=1; cumulative_dropped_count={Interlocked.Read(ref supersededPendingRawFrameCount)}");
        }

        if (shouldStartEncodeLoop)
        {
            LogDebug("Started sender raw-frame encode loop.");
        }
    }

    private async Task ProcessPendingRawFramesAsync()
    {
        while (true)
        {
            PendingRawFrame? nextPendingFrame;
            IWindowsH264FrameEncoder? currentEncoder;
            CancellationToken cancellationToken;
            WindowsH264EncodeOptions encodeOptions = default;
            TimeSpan encodeDelay = TimeSpan.Zero;

            lock (sync)
            {
                if (!started || encoder is null || encodeCts is null)
                {
                    rawEncodeLoopActive = false;
                    rawEncodeLoopTask = null;
                    return;
                }

                nextPendingFrame = pendingRawFrame;
                if (nextPendingFrame is null)
                {
                    rawEncodeLoopActive = false;
                    rawEncodeLoopTask = null;
                    return;
                }

                currentEncoder = encoder;
                cancellationToken = encodeCts.Token;
                var encodeProfile = ResolveEncodeProfile_NoLock(nextPendingFrame);
                Volatile.Write(ref activeTargetWidth, encodeProfile.Width);
                Volatile.Write(ref activeTargetHeight, encodeProfile.Height);
                Interlocked.Exchange(ref activeTargetBitrate, encodeProfile.TargetBitrate);
                Volatile.Write(ref activeTargetFramesPerSecond, encodeProfile.TargetFramesPerSecond);
                activeEncoderProfile = encodeProfile.ProfileName;
                ApplyRawCaptureControls_NoLock(rawCaptureSource, encodeProfile, "active_encode_profile");
                var forceKeyFramePending = Volatile.Read(ref pendingForceKeyFrame) == 1;
                var nowUtc = DateTimeOffset.UtcNow;
                if (ShouldDelayTransportEncode_NoLock(nextPendingFrame, encodeProfile, forceKeyFramePending, nowUtc, out encodeDelay))
                {
                    Volatile.Write(ref rawSlotCoalescingActive, 1);
                }
                else
                {
                    Volatile.Write(ref rawSlotCoalescingActive, 0);
                    nextPendingFrame = ClearPendingRawFrame_NoLock();
                    Interlocked.Exchange(ref lastTransportEncodeStartedUtcMs, nowUtc.ToUnixTimeMilliseconds());
                    Interlocked.Exchange(ref lastEncodedStreamEpoch, nextPendingFrame.StreamEpoch);

                    encodeOptions = new WindowsH264EncodeOptions(
                        TargetFramesPerSecond: encodeProfile.TargetFramesPerSecond,
                        TuningLevel: tuningLevel,
                        ForceKeyFrame: Interlocked.Exchange(ref pendingForceKeyFrame, 0) == 1,
                        StreamEpoch: streamEpoch);
                }
            }

            if (encodeDelay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(Min(encodeDelay, EncodeCadenceWakePollInterval), cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }

                continue;
            }

            try
            {
                using var frame = nextPendingFrame!.Frame;
                var captureToEncodeStartAgeMs = frame.CapturedTsUtcMs > 0
                    ? Math.Max(0, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - frame.CapturedTsUtcMs)
                    : 0;
                Interlocked.Exchange(ref lastCaptureToEncodeStartAgeMs, captureToEncodeStartAgeMs);

                var encodeStartedAt = Stopwatch.GetTimestamp();
                var encodedFrame = await currentEncoder!.EncodeAsync(frame, encodeOptions, cancellationToken).ConfigureAwait(false);
                var encodeTotalDurationMs = (long)Stopwatch.GetElapsedTime(encodeStartedAt).TotalMilliseconds;
                Interlocked.Exchange(ref lastEncodeDurationMs, encodeTotalDurationMs);
                Interlocked.Exchange(ref lastEncodeTotalDurationMs, encodeTotalDurationMs);
                if (currentEncoder is IWindowsH264FrameEncoderMetricsSource encoderMetricsSource)
                {
                    var encoderMetrics = encoderMetricsSource.GetRuntimeMetricsSnapshot();
                    Interlocked.Exchange(ref lastPreprocessDurationMs, encoderMetrics.LastPreprocessDurationMs);
                    Interlocked.Exchange(ref lastPreprocessResizeDurationMs, encoderMetrics.LastPreprocessResizeDurationMs);
                    Interlocked.Exchange(ref lastPreprocessColorConvertDurationMs, encoderMetrics.LastPreprocessColorConvertDurationMs);
                    preprocessResizePath = encoderMetrics.PreprocessResizePath;
                    Interlocked.Exchange(ref preprocessDirectNv12Count, encoderMetrics.PreprocessDirectNv12Count);
                    Interlocked.Exchange(ref lastTransformEncodeDurationMs, encoderMetrics.LastTransformEncodeDurationMs);
                    if (encoderMetrics.LastEncodeTotalDurationMs >= 0)
                    {
                        Interlocked.Exchange(ref lastEncodeDurationMs, encoderMetrics.LastEncodeTotalDurationMs);
                        Interlocked.Exchange(ref lastEncodeTotalDurationMs, encoderMetrics.LastEncodeTotalDurationMs);
                    }

                    lock (sync)
                    {
                        activeEncoderPath = encoderMetrics.EncoderPath;
                        emittedDisplayableFrames = encoderMetrics.EmittedDisplayableFrames;
                        emittedNonDisplayableUnits = encoderMetrics.EmittedNonDisplayableUnits;
                        displayableFrameRatio = encoderMetrics.DisplayableFrameRatio;
                        idrFramesEmitted = encoderMetrics.IdrFramesEmitted;
                        pFramesEmitted = encoderMetrics.PFramesEmitted;
                        droppedBFrames = encoderMetrics.DroppedBFrames;
                        droppedMultiPictureUnits = encoderMetrics.DroppedMultiPictureUnits;
                        idrFrameRatio = encoderMetrics.IdrFrameRatio;
                        averageEncodedFrameBytes = encoderMetrics.AverageEncodedFrameBytes;
                        transportIpOnlyMode = encoderMetrics.TransportIpOnlyMode;
                        lastAccessUnitKind = encoderMetrics.LastAccessUnitKind;
                        lowDelayConfigApplied = encoderMetrics.LowDelayConfigApplied;
                        senderContinuityRecoveryActive = encoderMetrics.SenderContinuityRecoveryActive;
                        senderContinuityLossCount = encoderMetrics.SenderContinuityLossCount;
                        framesDroppedWaitingForRecoveryKeyframe = encoderMetrics.FramesDroppedWaitingForRecoveryKeyframe;
                        lastSenderContinuityLossReason = encoderMetrics.LastSenderContinuityLossReason;
                        motionIntegrityGuardActive = encoderMetrics.MotionIntegrityGuardActive;
                        motionIntegritySampledRatio = encoderMetrics.MotionIntegritySampledRatio;
                        motionIntegrityPeakSampledRatio = encoderMetrics.MotionIntegrityPeakSampledRatio;
                        motionIntegrityScrollMotionActiveBandCount = encoderMetrics.MotionIntegrityScrollMotionActiveBandCount;
                        motionIntegrityScrollMotionPeakBandRatio = encoderMetrics.MotionIntegrityScrollMotionPeakBandRatio;
                        motionIntegrityHighMotionFrameCount = encoderMetrics.MotionIntegrityHighMotionFrameCount;
                        motionIntegrityScrollTriggerCount = encoderMetrics.MotionIntegrityScrollTriggerCount;
                        motionIntegrityBurstEnterCount = encoderMetrics.MotionIntegrityBurstEnterCount;
                        motionIntegrityBurstExitCount = encoderMetrics.MotionIntegrityBurstExitCount;
                        motionIntegrityForcedKeyFrameCount = encoderMetrics.MotionIntegrityForcedKeyFrameCount;
                        motionIntegrityLastTriggerKind = encoderMetrics.MotionIntegrityLastTriggerKind;
                        motionIntegrityLastReason = encoderMetrics.MotionIntegrityLastReason;
                        motionIntegrityIdrFrameRatio = encoderMetrics.MotionIntegrityIdrFrameRatio;
                        motionIntegrityForcedIdrRequestedCount = encoderMetrics.MotionIntegrityForcedIdrRequestedCount;
                        motionIntegrityForcedIdrConfirmedCount = encoderMetrics.MotionIntegrityForcedIdrConfirmedCount;
                        motionIntegrityForcedIdrMissedCount = encoderMetrics.MotionIntegrityForcedIdrMissedCount;
                        motionIntegrityForcedIdrPendingCount = encoderMetrics.MotionIntegrityForcedIdrPendingCount;
                        motionIntegrityForcedIdrConsecutiveMissCount = encoderMetrics.MotionIntegrityForcedIdrConsecutiveMissCount;
                        motionIntegrityForcedIdrBurstMissCount = encoderMetrics.MotionIntegrityForcedIdrBurstMissCount;
                        motionIntegrityActiveIdrFrameRatio = encoderMetrics.MotionIntegrityActiveIdrFrameRatio;
                        motionIntegrityForcedIdrLastMissReason = encoderMetrics.MotionIntegrityForcedIdrLastMissReason;
                        motionIntegrityEncoderRebuildCount = encoderMetrics.MotionIntegrityEncoderRebuildCount;
                        motionIntegrityEncoderRebuildSuppressedCount = encoderMetrics.MotionIntegrityEncoderRebuildSuppressedCount;
                        motionIntegrityEncoderRebuildPending = encoderMetrics.MotionIntegrityEncoderRebuildPending;
                        motionIntegrityEncoderRebuildLastReason = encoderMetrics.MotionIntegrityEncoderRebuildLastReason;
                        if (motionIntegrityEncoderRebuildPending)
                        {
                            ForceNextRawCapture_NoLock("encoder_rebuild_pending");
                        }
                    }
                }

                if (encodedFrame is null)
                {
                    continue;
                }

                FrameArrived?.Invoke(
                    this,
                    new ScreenCaptureFrameEventArgs(
                        encodedFrame.Width,
                        encodedFrame.Height,
                        encodedFrame.EncodedBytes,
                        encoding: "h264",
                        capturedTsUtcMs: encodedFrame.CapturedTsUtcMs,
                        isKeyFrame: encodedFrame.IsKeyFrame,
                        streamEpoch: encodedFrame.StreamEpoch,
                        streamConfig: encodedFrame.StreamConfig));
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                var isTerminalEncoderFailure = ex is MediaFoundationH264FrameEncoder.RawInputBufferStrategyUnavailableException;
                if (!isTerminalEncoderFailure || (FeatureFlags.ScreenShareDeepDiagnostics && !loggedTerminalEncoderFailure))
                {
                    LocalOperationalLog.Info(
                        "ScreenShareTransport",
                        $"event=screenshare_h264_encode_failed; reason={ex.GetType().Name}; hresult=0x{ex.HResult:X8}; message={Sanitize(ex.Message)}");
                }

                if (isTerminalEncoderFailure)
                {
                    loggedTerminalEncoderFailure = true;
                }

                LogDebug($"Raw frame encode failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void OnRawCaptureFailed(object? sender, WindowsRawCaptureFailureEventArgs e)
    {
        if (sender is not IWindowsRawCaptureSource failingSource)
        {
            return;
        }

        CancellationToken cancellationToken;
        var shouldFallback = false;
        var shouldStopForTerminalFailure = false;
        var backendKind = GetRawCaptureBackendKind(failingSource);

        lock (sync)
        {
            cancellationToken = captureCts?.Token ?? CancellationToken.None;
            if (!started || !ReferenceEquals(rawCaptureSource, failingSource))
            {
                return;
            }

            if (backendKind == WindowsRawCaptureBackendKind.WindowsGraphicsCapture &&
                string.IsNullOrWhiteSpace(wgcFirstFailureStage))
            {
                wgcFirstFailureStage = e.Stage;
            }

            if (backendKind == WindowsRawCaptureBackendKind.DesktopDuplication)
            {
                if (e.IsFatal)
                {
                    DisableDesktopDuplicationForSession("runtime_failed", e.Stage, e.Message);
                    shouldStopForTerminalFailure = true;
                }

                if (!shouldStopForTerminalFailure)
                {
                    return;
                }
            }

            if (backendKind == WindowsRawCaptureBackendKind.WindowsGraphicsCapture && e.IsFatal)
            {
                shouldFallback = Interlocked.CompareExchange(ref runtimeFallbackInProgress, 1, 0) == 0;
            }
            else if (!shouldStopForTerminalFailure)
            {
                return;
            }
        }

        if (shouldStopForTerminalFailure)
        {
            _ = Task.Run(() => HandleTerminalCaptureFailureAsync("desktop_duplication_unavailable", e.Stage, e.Message, backend: "desktop_duplication"));
            return;
        }

        if (!shouldFallback)
        {
            return;
        }

        if (!CanAttemptDesktopDuplicationFallback(failingSource))
        {
            _ = Task.Run(() => HandleTerminalCaptureFailureAsync("desktop_duplication_unavailable", e.Stage, e.Message, backend: "desktop_duplication"));
            return;
        }

        wgcFallbackAttempted = true;
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_h264_raw_source_runtime_fallback_requested; from=wgc; to=desktop_duplication; stage={e.Stage}; reason={e.Reason}; message={Sanitize(e.Message)}");

        _ = Task.Run(
            async () =>
            {
                try
                {
                    await FallbackRawCaptureSourceAsync(failingSource, cancellationToken, e).ConfigureAwait(false);
                }
                finally
                {
                    Interlocked.Exchange(ref runtimeFallbackInProgress, 0);
                    }
                });
    }

    private async Task FallbackRawCaptureSourceAsync(
        IWindowsRawCaptureSource failingSource,
        CancellationToken cancellationToken,
        WindowsRawCaptureFailureEventArgs failure)
    {
        if (!CanAttemptDesktopDuplicationFallback(failingSource))
        {
            await HandleTerminalCaptureFailureAsync("desktop_duplication_unavailable", failure.Stage, failure.Message, backend: "desktop_duplication").ConfigureAwait(false);
            return;
        }

        var fallbackSource = CreateDesktopDuplicationFallbackSource();
        CancellationTokenSource? previousEncodeCts = null;
        Task? previousRawEncodeLoopTask = null;
        PendingRawFrame? droppedPendingRawFrame = null;
        try
        {
            lock (sync)
            {
                previousEncodeCts = encodeCts;
                previousRawEncodeLoopTask = rawEncodeLoopTask;
                droppedPendingRawFrame = ClearPendingRawFrame_NoLock();
                encodeCts = null;
            }

            droppedPendingRawFrame?.Dispose();
            previousEncodeCts?.Cancel();
            if (previousRawEncodeLoopTask is not null)
            {
                await previousRawEncodeLoopTask.ConfigureAwait(false);
            }

            failingSource.FrameArrived -= OnRawFrameArrived;
            failingSource.CaptureFailed -= OnRawCaptureFailed;
            await failingSource.StopAsync().ConfigureAwait(false);
            await failingSource.DisposeAsync().ConfigureAwait(false);

            fallbackSource.FrameArrived += OnRawFrameArrived;
            fallbackSource.CaptureFailed += OnRawCaptureFailed;

            lock (sync)
            {
                if (!started || !ReferenceEquals(rawCaptureSource, failingSource))
                {
                    fallbackSource.FrameArrived -= OnRawFrameArrived;
                    fallbackSource.CaptureFailed -= OnRawCaptureFailed;
                    return;
                }

                rawCaptureSource = fallbackSource;
                encodeCts = CancellationTokenSource.CreateLinkedTokenSource(captureCts?.Token ?? CancellationToken.None);
                Interlocked.Exchange(ref pendingForceKeyFrame, 1);
                streamEpoch = Math.Max(1, streamEpoch + 1);
                ApplyRawCaptureControls_NoLock(fallbackSource, "runtime_fallback");
                ForceNextRawCapture_NoLock("runtime_fallback_first_frame");
                TryApplyCursorCapturePreferenceToRawSource_NoLock(fallbackSource, desiredCursorCaptureEnabled, "runtime_fallback");
            }

            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_h264_raw_source_fallback; from=wgc; to=desktop_duplication; reason=runtime_failure; stage={failure.Stage}; failure_reason={failure.Reason}; message={Sanitize(failure.Message)}");

            await fallbackSource.StartAsync(cancellationToken).ConfigureAwait(false);
            wgcFallbackStarted = true;
            TryLogWgcLifetimeSummary("runtime_fallback_started");
        }
        catch (Exception ex)
        {
            fallbackSource.FrameArrived -= OnRawFrameArrived;
            fallbackSource.CaptureFailed -= OnRawCaptureFailed;
            await fallbackSource.DisposeAsync().ConfigureAwait(false);
            var fallbackStage = ResolveFailureStage(ex, failure.Stage);
            DisableDesktopDuplicationForSession("runtime_failed", fallbackStage, ex.Message);
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_h264_raw_source_fallback_failed; from=wgc; to=desktop_duplication; stage={fallbackStage}; reason={ex.GetType().Name}; message={Sanitize(ex.Message)}");
            TryLogWgcLifetimeSummary("runtime_fallback_failed");
            await HandleTerminalCaptureFailureAsync("desktop_duplication_runtime_failed", fallbackStage, ex.Message, backend: "desktop_duplication").ConfigureAwait(false);
        }
        finally
        {
            previousEncodeCts?.Dispose();
        }
    }

    private static IWindowsRawCaptureSource CreateDefaultRawSource(ScreenCaptureTargetSelection captureTarget, string sourceRole)
    {
        if (WindowsGraphicsCaptureRawSource.IsRuntimeSupported())
        {
            LocalOperationalLog.Info("ScreenShareTransport", "event=screenshare_h264_raw_source_selected; path=wgc");
            return new WindowsGraphicsCaptureRawSource(captureTarget, sourceRole);
        }

        if (DesktopDuplicationRawSource.IsRuntimeSupported())
        {
            LocalOperationalLog.Info("ScreenShareTransport", "event=screenshare_h264_raw_source_selected; path=desktop_duplication");
            return new DesktopDuplicationRawSource(captureTarget);
        }

        return new WindowsGraphicsCaptureRawSource(captureTarget, sourceRole);
    }

    private IWindowsRawCaptureSource CreateWindowsGraphicsCaptureSource()
    {
        return windowsGraphicsCaptureSourceFactory();
    }

    private IWindowsRawCaptureSource CreateDesktopDuplicationFallbackSource()
    {
        return desktopDuplicationSourceFactory();
    }

    private bool CanAttemptDesktopDuplicationFallback(IWindowsRawCaptureSource source)
    {
        return GetRawCaptureBackendKind(source) == WindowsRawCaptureBackendKind.WindowsGraphicsCapture &&
               !desktopDuplicationDisabledForSession;
    }

    private static WindowsRawCaptureBackendKind GetRawCaptureBackendKind(IWindowsRawCaptureSource source)
    {
        if (source is IWindowsRawCaptureBackendDescriptor descriptor)
        {
            return descriptor.BackendKind;
        }

        return source switch
        {
            WindowsGraphicsCaptureRawSource => WindowsRawCaptureBackendKind.WindowsGraphicsCapture,
            DesktopDuplicationRawSource => WindowsRawCaptureBackendKind.DesktopDuplication,
            _ => WindowsRawCaptureBackendKind.Unknown,
        };
    }

    private void ApplyRawCaptureCadence_NoLock(IWindowsRawCaptureSource? source, string reason)
        => ApplyRawCaptureControls_NoLock(source, reason);

    private void ApplyRawCaptureControls_NoLock(IWindowsRawCaptureSource? source, string reason)
    {
        if (source is not IWindowsRawCaptureCadenceControl cadenceControl)
        {
            return;
        }

        cadenceControl.SetRawCaptureCadence(ResolveRawCaptureCadenceTargetFps_NoLock(), reason);
        var currentTargetWidth = Volatile.Read(ref activeTargetWidth);
        var currentTargetHeight = Volatile.Read(ref activeTargetHeight);
        if (currentTargetWidth > 0 &&
            currentTargetHeight > 0 &&
            source is IWindowsRawCaptureOutputControl outputControl)
        {
            outputControl.SetRawCaptureOutputSizeHint(currentTargetWidth, currentTargetHeight, reason);
        }
    }

    private void ApplyRawCaptureControls_NoLock(
        IWindowsRawCaptureSource? source,
        WindowsH264EncodeProfile encodeProfile,
        string reason)
    {
        if (source is not IWindowsRawCaptureCadenceControl cadenceControl)
        {
            if (source is IWindowsRawCaptureOutputControl outputOnlyControl)
            {
                outputOnlyControl.SetRawCaptureOutputSizeHint(encodeProfile.Width, encodeProfile.Height, reason);
            }

            return;
        }

        cadenceControl.SetRawCaptureCadence(encodeProfile.TargetFramesPerSecond, reason);
        if (source is IWindowsRawCaptureOutputControl outputControl)
        {
            outputControl.SetRawCaptureOutputSizeHint(encodeProfile.Width, encodeProfile.Height, reason);
        }
    }

    private void ForceNextRawCapture_NoLock(string reason)
    {
        if (!IsTransportSourceRole())
        {
            return;
        }

        if (rawCaptureSource is IWindowsRawCaptureCadenceControl cadenceControl)
        {
            cadenceControl.ForceNextRawCapture(reason);
        }
    }

    private int ResolveRawCaptureCadenceTargetFps_NoLock()
    {
        if (!IsTransportSourceRole())
        {
            return 0;
        }

        return WindowsH264EncodePolicy.ResolveProfile(
            sourceWidth: 2,
            sourceHeight: 2,
            targetFramesPerSecond: captureFrameRateHint,
            tuningLevel,
            transportIpOnly: true).TargetFramesPerSecond;
    }

    private void ResetWgcLifetimeSummary()
    {
        wgcStartSucceeded = false;
        wgcFirstFailureStage = null;
        wgcFallbackAttempted = false;
        wgcFallbackStarted = false;
        desktopDuplicationDisabledForSession = false;
        wgcSummaryLogged = false;
        terminalCaptureFailureLogged = false;
    }

    private void ResetFreshnessState_NoLock()
    {
        pendingRawFrame = null;
        rawEncodeLoopTask = null;
        rawEncodeLoopActive = false;
        supersededPendingRawFrameCount = 0;
        rawFramesDeferredToEncodeSlot = 0;
        rawFramesReplacedBeforeEncodeSlot = 0;
        rawCaptureEventCount = 0;
        rawFramesSkippedBeforeEncode = 0;
        rawEncodeSlotEmptyCount = 0;
        rawSlotCoalescingActive = 0;
        lastTransportEncodeStartedUtcMs = 0;
        lastEncodedStreamEpoch = 0;
        actualEncodedFpsSampleUtcMs = 0;
        actualEncodedFpsSampleDisplayableFrames = 0;
        actualEncodedDisplayableFps = 0;
        lastCpuSampleTimestamp = 0;
        lastCpuSampleProcessorTicks = 0;
        senderProcessCpuPercent = -1;
        lastCaptureToEncodeStartAgeMs = -1;
        lastEncodeDurationMs = -1;
        emittedDisplayableFrames = 0;
        emittedNonDisplayableUnits = 0;
        idrFramesEmitted = 0;
        pFramesEmitted = 0;
        droppedBFrames = 0;
        droppedMultiPictureUnits = 0;
        displayableFrameRatio = 0;
        idrFrameRatio = 0;
        averageEncodedFrameBytes = 0;
        transportIpOnlyMode = false;
        lastPreprocessDurationMs = -1;
        lastPreprocessResizeDurationMs = -1;
        lastPreprocessColorConvertDurationMs = -1;
        preprocessResizePath = string.Empty;
        preprocessDirectNv12Count = 0;
        lastTransformEncodeDurationMs = -1;
        lastEncodeTotalDurationMs = -1;
        lastAccessUnitKind = string.Empty;
        lowDelayConfigApplied = string.Empty;
        senderContinuityRecoveryActive = false;
        senderContinuityLossCount = 0;
        framesDroppedWaitingForRecoveryKeyframe = 0;
        lastSenderContinuityLossReason = string.Empty;
        activeTargetWidth = 0;
        activeTargetHeight = 0;
        activeTargetBitrate = 0;
        activeTargetFramesPerSecond = 0;
        activeEncoderPath = string.Empty;
        activeEncoderProfile = string.Empty;
        motionIntegrityGuardActive = false;
        motionIntegritySampledRatio = 0;
        motionIntegrityPeakSampledRatio = 0;
        motionIntegrityScrollMotionActiveBandCount = 0;
        motionIntegrityScrollMotionPeakBandRatio = 0;
        motionIntegrityHighMotionFrameCount = 0;
        motionIntegrityScrollTriggerCount = 0;
        motionIntegrityBurstEnterCount = 0;
        motionIntegrityBurstExitCount = 0;
        motionIntegrityForcedKeyFrameCount = 0;
        motionIntegrityLastTriggerKind = string.Empty;
        motionIntegrityLastReason = string.Empty;
        motionIntegrityIdrFrameRatio = 0;
        motionIntegrityForcedIdrRequestedCount = 0;
        motionIntegrityForcedIdrConfirmedCount = 0;
        motionIntegrityForcedIdrMissedCount = 0;
        motionIntegrityForcedIdrPendingCount = 0;
        motionIntegrityForcedIdrConsecutiveMissCount = 0;
        motionIntegrityForcedIdrBurstMissCount = 0;
        motionIntegrityActiveIdrFrameRatio = 0;
        motionIntegrityForcedIdrLastMissReason = string.Empty;
        motionIntegrityEncoderRebuildCount = 0;
        motionIntegrityEncoderRebuildSuppressedCount = 0;
        motionIntegrityEncoderRebuildPending = false;
        motionIntegrityEncoderRebuildLastReason = string.Empty;
        lastSupersededPendingRawFrameLogUtc = default;
    }

    private PendingRawFrame? ClearPendingRawFrame_NoLock()
    {
        var dropped = pendingRawFrame;
        pendingRawFrame = null;
        return dropped;
    }

    private WindowsH264EncodeProfile ResolveEncodeProfile_NoLock(PendingRawFrame pendingFrame)
    {
        return WindowsH264EncodePolicy.ResolveProfile(
            pendingFrame.Frame.Bitmap.Width,
            pendingFrame.Frame.Bitmap.Height,
            captureFrameRateHint,
            tuningLevel,
            transportIpOnly: IsTransportSourceRole());
    }

    private bool ShouldPreserveExistingPendingRawFrameForRecovery_NoLock(PendingRawFrame incoming, PendingRawFrame existing)
    {
        if (!IsTransportSourceRole())
        {
            return false;
        }

        if (!senderContinuityRecoveryActive &&
            Volatile.Read(ref pendingForceKeyFrame) == 0)
        {
            return false;
        }

        return existing.StreamEpoch >= incoming.StreamEpoch;
    }

    private bool ShouldDelayTransportEncode_NoLock(
        PendingRawFrame pendingFrame,
        WindowsH264EncodeProfile encodeProfile,
        bool forceKeyFramePending,
        DateTimeOffset nowUtc,
        out TimeSpan delay)
    {
        delay = TimeSpan.Zero;
        if (!IsTransportSourceRole() ||
            forceKeyFramePending ||
            senderContinuityRecoveryActive ||
            motionIntegrityEncoderRebuildPending ||
            encodeProfile.TargetFramesPerSecond <= 0)
        {
            return false;
        }

        var lastEncodeUtcMs = Interlocked.Read(ref lastTransportEncodeStartedUtcMs);
        if (lastEncodeUtcMs <= 0 ||
            pendingFrame.StreamEpoch != Interlocked.Read(ref lastEncodedStreamEpoch))
        {
            return false;
        }

        var minInterval = TimeSpan.FromMilliseconds(1000d / Math.Max(1, encodeProfile.TargetFramesPerSecond));
        var elapsed = nowUtc - DateTimeOffset.FromUnixTimeMilliseconds(lastEncodeUtcMs);
        if (elapsed >= minInterval)
        {
            return false;
        }

        delay = minInterval - elapsed;
        if (delay <= TimeSpan.Zero)
        {
            return false;
        }

        return true;
    }

    private void UpdateActualEncodedDisplayableFps_NoLock(long nowUtcMs, long emittedDisplayableFrameCount)
    {
        var lastSampleUtcMs = Interlocked.Read(ref actualEncodedFpsSampleUtcMs);
        if (lastSampleUtcMs <= 0 ||
            emittedDisplayableFrameCount < Interlocked.Read(ref actualEncodedFpsSampleDisplayableFrames))
        {
            Interlocked.Exchange(ref actualEncodedFpsSampleUtcMs, nowUtcMs);
            Interlocked.Exchange(ref actualEncodedFpsSampleDisplayableFrames, emittedDisplayableFrameCount);
            return;
        }

        var elapsedMs = nowUtcMs - lastSampleUtcMs;
        if (elapsedMs < EncodedFpsSampleMinInterval.TotalMilliseconds)
        {
            return;
        }

        var frameDelta = Math.Max(0, emittedDisplayableFrameCount - Interlocked.Read(ref actualEncodedFpsSampleDisplayableFrames));
        Volatile.Write(ref actualEncodedDisplayableFps, frameDelta * 1000d / elapsedMs);
        Interlocked.Exchange(ref actualEncodedFpsSampleUtcMs, nowUtcMs);
        Interlocked.Exchange(ref actualEncodedFpsSampleDisplayableFrames, emittedDisplayableFrameCount);
    }

    private void UpdateProcessCpuPercent_NoLock()
    {
        var nowTimestamp = Stopwatch.GetTimestamp();
        long processorTicks;
        try
        {
            using var process = Process.GetCurrentProcess();
            processorTicks = process.TotalProcessorTime.Ticks;
        }
        catch
        {
            return;
        }

        var previousTimestamp = Interlocked.Read(ref lastCpuSampleTimestamp);
        var previousProcessorTicks = Interlocked.Read(ref lastCpuSampleProcessorTicks);
        if (previousTimestamp <= 0 || previousProcessorTicks <= 0)
        {
            Interlocked.Exchange(ref lastCpuSampleTimestamp, nowTimestamp);
            Interlocked.Exchange(ref lastCpuSampleProcessorTicks, processorTicks);
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(previousTimestamp, nowTimestamp);
        if (elapsed < CpuSampleMinInterval)
        {
            return;
        }

        var processorElapsedMs = TimeSpan.FromTicks(Math.Max(0, processorTicks - previousProcessorTicks)).TotalMilliseconds;
        var wallElapsedMs = Math.Max(1d, elapsed.TotalMilliseconds);
        var normalizedCpu = processorElapsedMs / wallElapsedMs / Math.Max(1, Environment.ProcessorCount) * 100d;
        Volatile.Write(ref senderProcessCpuPercent, Math.Clamp(normalizedCpu, 0d, 100d));
        Interlocked.Exchange(ref lastCpuSampleTimestamp, nowTimestamp);
        Interlocked.Exchange(ref lastCpuSampleProcessorTicks, processorTicks);
    }

    private static TimeSpan Min(TimeSpan left, TimeSpan right)
        => left <= right ? left : right;

    private PendingRawFrame? PurgePendingRawFrameForRecovery_NoLock()
    {
        if (!IsTransportSourceRole() || pendingRawFrame is null)
        {
            return null;
        }

        Interlocked.Increment(ref rawFramesReplacedBeforeEncodeSlot);
        return ClearPendingRawFrame_NoLock();
    }

    private bool ShouldLogSupersededPendingRawFrame_NoLock()
    {
        var now = DateTimeOffset.UtcNow;
        if (lastSupersededPendingRawFrameLogUtc != default &&
            now - lastSupersededPendingRawFrameLogUtc < SupersededPendingRawFrameLogInterval)
        {
            return false;
        }

        lastSupersededPendingRawFrameLogUtc = now;
        return true;
    }

    private bool IsTransportSourceRole()
    {
        return string.Equals(sourceRole, "transport", StringComparison.Ordinal);
    }

    private static bool ShouldStartSenderRecoveryBurst(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        if (string.Equals(reason, ScreenSharePressureProtocol.PressureReasonContinuityLoss, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return reason.Contains("recovery", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("continuity", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reason, "transport_stale_video_purged", StringComparison.OrdinalIgnoreCase);
    }

    private void DisableDesktopDuplicationForSession(string reason, string? stage, string? message)
    {
        if (desktopDuplicationDisabledForSession)
        {
            return;
        }

        desktopDuplicationDisabledForSession = true;
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_h264_raw_source_fallback_disabled; path=desktop_duplication; reason={reason}; stage={Sanitize(stage)}; message={Sanitize(message)}");
    }

    private static string ResolveFailureStage(Exception ex, string? fallbackStage)
    {
        var stageProperty = ex.GetType().GetProperty("Stage");
        if (stageProperty?.GetValue(ex) is string stage && !string.IsNullOrWhiteSpace(stage))
        {
            return stage.Trim();
        }

        return string.IsNullOrWhiteSpace(fallbackStage) ? "(none)" : fallbackStage.Trim();
    }

    private async Task HandleTerminalCaptureFailureAsync(string reason, string? stage, string? message, string backend = "wgc")
    {
        if (terminalCaptureFailureLogged)
        {
            return;
        }

        terminalCaptureFailureLogged = true;
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_h264_raw_source_terminal_failure; backend={backend}; reason={reason}; stage={Sanitize(stage)}; message={Sanitize(message)}");
        await StopAsync().ConfigureAwait(false);
    }

    private void TryLogWgcLifetimeSummary(string outcome)
    {
        if (wgcSummaryLogged || !wgcStartSucceeded)
        {
            return;
        }

        wgcSummaryLogged = true;
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_h264_wgc_lifetime_summary; outcome={outcome}; start_succeeded=1; first_runtime_failure_stage={Sanitize(wgcFirstFailureStage)}; fallback_attempted={(wgcFallbackAttempted ? 1 : 0)}; fallback_started={(wgcFallbackStarted ? 1 : 0)}");
    }

    private static string Sanitize(string? message)
    {
        return string.IsNullOrWhiteSpace(message)
            ? "(none)"
            : message.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }

    [Conditional("DEBUG")]
    private static void LogDebug(string message)
    {
        Trace.WriteLine($"[WindowsH264ScreenCaptureSource] {message}");
    }

    private static IWindowsH264FrameEncoder CreateDefaultEncoder(string sourceRole)
    {
        return MediaFoundationH264FrameEncoder.TryCreate(sourceRole) ?? new UnsupportedWindowsH264FrameEncoder();
    }

    private static bool ProbeSupport(
        Func<IWindowsRawCaptureSource> rawSourceFactory,
        Func<IWindowsH264FrameEncoder> encoderFactory)
    {
        IWindowsRawCaptureSource? rawSource = null;
        IWindowsH264FrameEncoder? frameEncoder = null;
        try
        {
            rawSource = rawSourceFactory();
            frameEncoder = encoderFactory();
            return rawSource.IsSupported && frameEncoder.IsSupported;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (rawSource is not null)
            {
                rawSource.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }

            if (frameEncoder is not null)
            {
                frameEncoder.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private static bool ProbeEncoderSupport(Func<IWindowsH264FrameEncoder?> encoderFactory)
    {
        IWindowsH264FrameEncoder? frameEncoder = null;
        try
        {
            frameEncoder = encoderFactory();
            return frameEncoder?.IsSupported == true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (frameEncoder is not null)
            {
                frameEncoder.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
        }
    }

    private static bool ProbeDecoderSupport(Func<IWindowsH264BitmapDecoder?> decoderFactory)
    {
        IWindowsH264BitmapDecoder? decoder = null;
        try
        {
            decoder = decoderFactory();
            return decoder?.IsSupported == true;
        }
        catch
        {
            return false;
        }
        finally
        {
            decoder?.Dispose();
        }
    }

    private sealed class UnsupportedWindowsH264FrameEncoder : IWindowsH264FrameEncoder
    {
        public bool IsSupported => false;

        public ValueTask<WindowsH264EncodedFrame?> EncodeAsync(
            WindowsRawCaptureFrame frame,
            WindowsH264EncodeOptions options,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException("Windows H.264 encoder is not supported.");
        }

        public ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        public void StartRecoveryBurst(string reason, long streamEpoch)
        {
        }
    }

    private sealed class PendingRawFrame : IDisposable
    {
        public PendingRawFrame(WindowsRawCaptureFrame frame, long streamEpoch = 0)
        {
            Frame = frame ?? throw new ArgumentNullException(nameof(frame));
            EnqueuedTsUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            EnqueuedUtcTicks = DateTimeOffset.UtcNow.UtcTicks;
            StreamEpoch = Math.Max(0, streamEpoch);
        }

        public WindowsRawCaptureFrame Frame { get; }

        public long EnqueuedTsUtcMs { get; }

        public long EnqueuedUtcTicks { get; }

        public long StreamEpoch { get; }

        public void Dispose()
        {
            Frame.Dispose();
        }
    }
}
