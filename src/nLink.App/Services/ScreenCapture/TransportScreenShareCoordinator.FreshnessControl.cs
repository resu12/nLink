using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using NLink.App.Configuration;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;

namespace NLink.App.Services.ScreenCapture;

internal sealed partial class TransportScreenShareCoordinator
{
    internal void SetFileTransferDegradedHint(bool active)
    {
        lock (gate)
        {
            fileTransferDegradedHintActive = active;
        }

        OnAutoTuneTimerTick();
    }

    internal void SetFileTransferCatchUpOnlyHint(bool active)
    {
        lock (gate)
        {
            fileTransferCatchUpOnlyHintActive = active;
        }

        OnAutoTuneTimerTick();
    }

    internal bool BeginTransportRebindRecovery(string reason)
    {
        string currentSessionId;
        long generation;
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "transport_rebind"
            : reason.Trim();

        lock (gate)
        {
            if (captureSource is null ||
                sendPipeline is null ||
                string.IsNullOrWhiteSpace(sessionId))
            {
                return false;
            }

            currentSessionId = sessionId;
            generation = ++transportRebindGeneration;
            transportRebindPendingGeneration = generation;
            transportRebindReason = normalizedReason;
        }

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_transport_rebind_generation_started; direction=outbound; session_id={currentSessionId}; reason={normalizedReason}; rebind_generation={generation}");

        flushTransportQueue?.Invoke("transport_rebind_" + normalizedReason);

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_transport_rebind_keyframe_requested; direction=outbound; session_id={currentSessionId}; reason={normalizedReason}; rebind_generation={generation}");
        RequestKeyFrame("transport_rebind_recovery_" + normalizedReason);
        return true;
    }

    internal void RequestKeyFrame(string reason)
    {
        IScreenCaptureSource? currentCaptureSource;
        ScreenShareFrameSendPipeline? currentPipeline;
        string currentSessionId;
        ScreenShareTransportTuningLevel currentTransportTuningLevel;
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? "keyframe_request"
            : reason.Trim();
        var nowUtc = clock.UtcNow;
        var shouldStartRecoveryBurst = ShouldStartRecoveryBurstForReason(normalizedReason);
        var currentStreamEpoch = 0L;
        var recoveryBurstToken = 0L;
        var startedRecoveryBurst = false;
        var restartedRecoveryBurstForEpochTakeover = false;
        var recoveryBurstBecameActive = false;
        var suppressedRecoveryBurstRequest = false;
        var recoveryBurstSuppressedDueToHelperAck = false;
        var recoveryBurstTransportDisarmToken = 0L;
        var recoveryGapToRequestMsValue = -1L;
        var recoveryBurstStartedWhileHelperProofHealthy = false;
        lock (gate)
        {
            currentCaptureSource = captureSource;
            currentPipeline = sendPipeline;
            currentSessionId = sessionId;
            currentTransportTuningLevel = transportTuningLevel;
        }

        if (shouldStartRecoveryBurst)
        {
            currentStreamEpoch = GetCaptureFreshnessMetricsSnapshot(currentCaptureSource).CurrentStreamEpoch;
            lock (gate)
            {
                var burstDecision = EvaluateRecoveryBurstRequest_NoLock(
                    currentStreamEpoch,
                    normalizedReason,
                    nowUtc,
                    out recoveryGapToRequestMsValue,
                    out recoveryBurstTransportDisarmToken,
                    out recoveryBurstSuppressedDueToHelperAck,
                    out recoveryBurstStartedWhileHelperProofHealthy);
                startedRecoveryBurst = burstDecision == RecoveryBurstRequestDecision.Start;
                restartedRecoveryBurstForEpochTakeover = burstDecision == RecoveryBurstRequestDecision.EpochTakeover;
                recoveryBurstBecameActive = startedRecoveryBurst || restartedRecoveryBurstForEpochTakeover;
                suppressedRecoveryBurstRequest = burstDecision == RecoveryBurstRequestDecision.Suppress;
                recoveryBurstToken = recoveryBurstBecameActive
                    ? GetActiveRecoveryBurstToken_NoLock()
                    : 0;
            }

            if (recoveryBurstTransportDisarmToken > 0)
            {
                clearRecoveryBurstTransportFallback?.Invoke(recoveryBurstTransportDisarmToken);
            }

            if (recoveryBurstBecameActive)
            {
                var recoveryBurstStartReason = restartedRecoveryBurstForEpochTakeover
                    ? "recovery_burst_takeover"
                    : "recovery_burst_start";
                var droppedQueuedFrames = 0;
                var droppedPendingRawFrames = 0;
                var resetEpoch = currentStreamEpoch;

                if (currentPipeline is not null)
                {
                    droppedQueuedFrames = currentPipeline.FlushPendingFrames();
                    currentPipeline.ResetPacingWindow();
                }

                flushTransportQueue?.Invoke(recoveryBurstStartReason);
                droppedPendingRawFrames = PurgeSenderRawBacklog(currentCaptureSource);
                if (currentCaptureSource is IScreenCaptureTransportRecoveryResetSource resetSource)
                {
                    resetEpoch = resetSource.ForceTransportRecoveryReset(currentTransportTuningLevel);
                }
                else
                {
                    resetEpoch = GetCaptureFreshnessMetricsSnapshot(currentCaptureSource).CurrentStreamEpoch;
                }

                if (currentCaptureSource is IScreenCaptureAdaptiveTuning adaptiveCaptureSource &&
                    captureFpsHint > 0)
                {
                    adaptiveCaptureSource.SetCaptureFrameRateHint(captureFpsHint);
                }

                lock (gate)
                {
                    if (activeRecoveryBurst is { } activeBurst &&
                        activeBurst.BurstToken == recoveryBurstToken)
                    {
                        currentStreamEpoch = resetEpoch > 0
                            ? resetEpoch
                            : activeBurst.StreamEpoch;
                        activeRecoveryBurst = new ActiveRecoveryBurst
                        {
                            StreamEpoch = currentStreamEpoch,
                            Phase = RecoveryBurstPhase.Requested,
                            OwnerFrameId = -1,
                            BurstToken = activeBurst.BurstToken,
                            ProtectedFollowerBudgetRemaining = 0,
                            NextProtectedFollowerFrameId = -1,
                            RequestedUtc = activeBurst.RequestedUtc == default ? nowUtc : activeBurst.RequestedUtc,
                            OwnerEmittedUtc = default,
                            PostAckHoldStartedUtc = default,
                            ForcedResetIssued = true,
                        };
                        ResetHelperCurrentEpochState_NoLock(currentStreamEpoch);
                        recoveryFirstHelperHeadAdvanceUtc = default;
                        recoveryProtectedFollowerCount = 0;
                        recoveryProtectedFrameCount = 0;
                        recoveryStartAppliedHeadFrameId = helperAppliedHeadFrameId;
                        recoveryStartLastVisibleApplyFrameId = helperLastVisibleApplyFrameId;
                        recoveryOwnerAckFrameId = -1;
                        recoveryOwnerEmitToAckMs = -1;
                        recoveryOwnerAckWindowMs = -1;
                        recoveryOwnerEmitToFirstVisibleApplyMs = -1;
                        recoveryAckSource = string.Empty;
                        ClearRecoveryOwnerPendingHold_NoLock();
                    }
                    else if (resetEpoch > 0)
                    {
                        currentStreamEpoch = resetEpoch;
                    }
                }

                if (droppedQueuedFrames > 0)
                {
                    LocalOperationalLog.Info(
                        "ScreenShareTransport",
                        $"event=screenshare_sender_frame_dropped_backlog; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; dropped_count={droppedQueuedFrames}; reason={recoveryBurstStartReason}");
                }

                if (droppedPendingRawFrames > 0)
                {
                    LocalOperationalLog.Info(
                        "ScreenShareTransport",
                        $"event=screenshare_sender_raw_backlog_purged; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; dropped_count={droppedPendingRawFrames}; reason={recoveryBurstStartReason}");
                }

                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_sender_recovery_epoch_reset; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; stream_epoch={Math.Max(0, currentStreamEpoch)}; reason={normalizedReason}; transport_tuning_level={currentTransportTuningLevel}; burst_token={Math.Max(0, recoveryBurstToken)}");
            }
        }

        var shouldIssueKeyFrameRequest =
            !shouldStartRecoveryBurst ||
            !suppressedRecoveryBurstRequest;
        if (shouldIssueKeyFrameRequest &&
            currentCaptureSource is IScreenCaptureKeyFrameRequestSource keyFrameRequestSource)
        {
            keyFrameRequestSource.RequestKeyFrame(normalizedReason);
            if (shouldStartRecoveryBurst &&
                currentStreamEpoch > 0)
            {
                lock (gate)
                {
                    MarkRecoveryBurstKeyframeRequestIssued_NoLock(currentStreamEpoch);
                }
            }

            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_keyframe_requested; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; reason={normalizedReason}");
        }

        if (startedRecoveryBurst)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_recovery_burst_started; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; stream_epoch={Math.Max(0, currentStreamEpoch)}; reason={normalizedReason}; recovery_gap_to_keyframe_request_ms={(recoveryGapToRequestMsValue >= 0 ? recoveryGapToRequestMsValue.ToString(CultureInfo.InvariantCulture) : "(none)")}; helper_proof_healthy_at_start={(recoveryBurstStartedWhileHelperProofHealthy ? 1 : 0)}");
        }

        if (restartedRecoveryBurstForEpochTakeover)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_recovery_burst_takeover; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; stream_epoch={Math.Max(0, currentStreamEpoch)}; reason={normalizedReason}; recovery_gap_to_keyframe_request_ms={(recoveryGapToRequestMsValue >= 0 ? recoveryGapToRequestMsValue.ToString(CultureInfo.InvariantCulture) : "(none)")}");
        }

        if (suppressedRecoveryBurstRequest)
        {
            var burstPhase = RecoveryBurstPhase.Idle;
            lock (gate)
            {
                burstPhase = GetActiveRecoveryBurstPhase_NoLock();
            }

            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_recovery_burst_request_suppressed; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; stream_epoch={Math.Max(0, currentStreamEpoch)}; reason={normalizedReason}; phase={FormatRecoveryBurstPhase(burstPhase)}; suppress_reason={(recoveryBurstSuppressedDueToHelperAck ? "helper_acknowledged" : "active_burst")}");
        }
    }

    private static bool ShouldStartRecoveryBurstForReason(string reason)
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
               reason.Contains("frame_gap", StringComparison.OrdinalIgnoreCase) ||
               reason.Contains("continuity", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(reason, "transport_stale_video_purged", StringComparison.OrdinalIgnoreCase);
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

            var autoTuneNowUtc = clock.UtcNow;
            MaybeCompleteTransportProfileTransition(currentSessionId, autoTuneNowUtc);
            if (TryTimeoutRecoveryBurst(
                    autoTuneNowUtc,
                    out var timedOutBurstEpoch,
                    out var timedOutOwnerFrameId,
                    out var timedOutBurstToken,
                    out var timedOutCompletionKind,
                    out var timedOutCompletionAckSource))
            {
                if (timedOutBurstToken > 0)
                {
                    clearRecoveryBurstTransportFallback?.Invoke(timedOutBurstToken);
                }

                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_sender_recovery_burst_completed; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; stream_epoch={Math.Max(0, timedOutBurstEpoch)}; recovery_owner_frame_id={(timedOutOwnerFrameId >= 0 ? timedOutOwnerFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}; completion={timedOutCompletionKind}; ack_source={(string.IsNullOrWhiteSpace(timedOutCompletionAckSource) ? "(none)" : timedOutCompletionAckSource)}; timeout_ms={(long)RecoveryBurstTimeout.TotalMilliseconds}");
            }

            if (TryExpireRecoveryPostAckHold(
                    autoTuneNowUtc,
                    out var timedOutSettleEpoch,
                    out var timedOutSettleOwnerFrameId,
                    out var timedOutSettleBurstToken))
            {
                if (timedOutSettleBurstToken > 0)
                {
                    clearRecoveryBurstTransportFallback?.Invoke(timedOutSettleBurstToken);
                }

                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_sender_recovery_post_ack_hold_expired; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; stream_epoch={Math.Max(0, timedOutSettleEpoch)}; recovery_owner_frame_id={(timedOutSettleOwnerFrameId >= 0 ? timedOutSettleOwnerFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}; timeout_ms={(long)RecoveryPostAckHoldTimeout.TotalMilliseconds}");
            }

            var transitionGraceActive = IsTransportProfileTransitionGraceActive(autoTuneNowUtc);

            var metrics = GetMetricsSnapshot();
            var sourceFreshnessMetrics = GetCaptureFreshnessMetricsSnapshot(currentCaptureSource);
            RefreshHelperCurrentEpochState(sourceFreshnessMetrics.CurrentStreamEpoch);
            lock (gate)
            {
                RefreshRemoteHelperFactHealthyState_NoLock(
                    sourceFreshnessMetrics.CurrentStreamEpoch,
                    continuityRecoverySignal: false,
                    inboundSteadyVisibleProgressActive: false,
                    inboundFramesAppliedSinceLastGap: -1,
                    autoTuneNowUtc);
            }

            var rateGateDropDelta = ConsumeAutoTuneCounterDelta(
                metrics.FramesDroppedByRateGate,
                ref lastAutoTuneRateGateDrops);
            var queueEvictDropDelta = ConsumeAutoTuneCounterDelta(
                metrics.FramesDroppedByQueueEvict,
                ref lastAutoTuneQueueEvictDrops);
            var supersededPendingRawFrameDelta = ConsumeAutoTuneCounterDelta(
                sourceFreshnessMetrics.SupersededPendingRawFrameCount,
                ref lastAutoTuneSourceSupersededPendingFrames);

            var maxTransportFps = FeatureFlags.ScreenShareTransportMaxFps;
            var minAutoTuneFps = Math.Min(MinAutoTuneFramesPerSecond, maxTransportFps);
            var configuredCap = Math.Clamp(
                Math.Min(FeatureFlags.ScreenShareMaxFps, maxTransportFps),
                minAutoTuneFps,
                maxTransportFps);

            ScreenShareSenderFreshnessMode currentSenderMode;
            int currentCaptureFpsHint;
            ScreenShareTransportTuningLevel currentTransportTuningLevel;
            string currentRemotePressureReason;
            bool currentHelperEpochWarmupActive;
            int currentHelperEpochApplyCount;
            long currentHelperEpochNeedMoreInputCount;
            int currentHelperEpochHealthySignalCount;
            long currentHelperEpochStaleDrops;
            bool currentHelperSteadyVisibleProgressActive;
            long currentHelperLastVisibleApplyFrameId;
            long currentHelperVisibleHeadFrameId;
            long currentHelperStableVisibleHeadFrameId;
            long currentHelperFramesAppliedSinceLastGap;
            long currentHelperVisibleRecoveryFloorFrameId;
            bool currentRemoteHelperFactHealthyActive;
            bool currentPostReceiptPromotionSafe;
            long currentAcknowledgedHelperHeadFrameId;
            long currentLastAcknowledgedVisibleHelperHeadFrameId;
            long currentSatisfiedRecoveryFloorEpoch;
            long currentSatisfiedRecoveryFloorFrameId;
            bool currentRecoveryLockActive;
            long currentRecoveryLockStreamEpoch;
            bool currentRecoveryBurstActive;
            long currentRecoveryBurstStreamEpoch;
            long currentRecoveryOwnerFrameId;
            int currentRecoveryProtectedFollowerCount;
            long currentRecoveryGapCount;
            long currentRecoveryGapToKeyframeRequestMs;
            long currentRecoveryKeyframeRequestToOwnerEmitMs;
            long currentRecoveryOwnerAckWindowMs;
            long currentRecoveryOwnerEmitToFirstVisibleApplyMs;
            long currentRecoveryBurstControlFallbackCount;
            long currentRecoveryBurstTimeoutCount;
            long currentRecoveryBurstCompletedCount;
            long currentRecoveryOwnerUnackedNonKeyHeldCount;
            long currentRecoveryOwnerUnackedNonKeyReplacedCount;
            long currentRecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount;
            long currentRecoveryOwnerReplacedBeforeAckCount;
            long currentHighFrameAgeSuppressedDuringOwnerAckCount;
            lock (gate)
            {
                currentSenderMode = senderFreshnessMode;
                currentCaptureFpsHint = captureFpsHint;
                currentTransportTuningLevel = transportTuningLevel;
                currentRemotePressureReason = remotePressureReason;
                currentHelperEpochWarmupActive = helperCurrentEpochWarmupActive;
                currentHelperEpochApplyCount = helperCurrentEpochApplyCount;
                currentHelperEpochNeedMoreInputCount = helperCurrentEpochNeedMoreInputCount;
                currentHelperEpochHealthySignalCount = helperCurrentEpochHealthySignalCount;
                currentHelperEpochStaleDrops = helperCurrentEpochStaleDrops;
                currentHelperSteadyVisibleProgressActive = helperSteadyVisibleProgressActive;
                currentHelperLastVisibleApplyFrameId = helperLastVisibleApplyFrameId;
                currentHelperVisibleHeadFrameId = helperVisibleHeadFrameId;
                currentHelperVisibleRecoveryFloorFrameId = helperVisibleRecoveryFloorFrameId;
                currentHelperStableVisibleHeadFrameId = helperStableVisibleHeadFrameId;
                currentHelperFramesAppliedSinceLastGap = helperFramesAppliedSinceLastGap;
                currentRemoteHelperFactHealthyActive = remoteHelperFactHealthyActive;
                currentPostReceiptPromotionSafe = IsReceiptCompletedEpochPromotionSafe_NoLock(sourceFreshnessMetrics.CurrentStreamEpoch);
                currentAcknowledgedHelperHeadFrameId = acknowledgedHelperHeadFrameId;
                currentLastAcknowledgedVisibleHelperHeadFrameId = acknowledgedVisibleHelperHeadFrameId;
                currentSatisfiedRecoveryFloorEpoch = satisfiedRecoveryFloorEpoch;
                currentSatisfiedRecoveryFloorFrameId = satisfiedRecoveryFloorFrameId;
                currentRecoveryLockActive = recoveryLockActive;
                currentRecoveryLockStreamEpoch = recoveryLockStreamEpoch;
                currentRecoveryBurstActive = activeRecoveryBurst is not null;
                currentRecoveryBurstStreamEpoch = activeRecoveryBurst?.StreamEpoch ?? 0;
                currentRecoveryOwnerFrameId = activeRecoveryBurst?.OwnerFrameId ?? -1;
                currentRecoveryProtectedFollowerCount = recoveryProtectedFollowerCount;
                currentRecoveryGapCount = recoveryGapCount;
                currentRecoveryGapToKeyframeRequestMs = recoveryGapToKeyframeRequestMs;
                currentRecoveryKeyframeRequestToOwnerEmitMs = recoveryKeyframeRequestToOwnerEmitMs;
                currentRecoveryOwnerAckWindowMs = recoveryOwnerAckWindowMs;
                currentRecoveryOwnerEmitToFirstVisibleApplyMs = recoveryOwnerEmitToFirstVisibleApplyMs;
                currentRecoveryBurstControlFallbackCount = recoveryBurstControlFallbackCount;
                currentRecoveryBurstTimeoutCount = recoveryBurstTimeoutCount;
                currentRecoveryBurstCompletedCount = recoveryBurstCompletedCount;
                currentRecoveryOwnerUnackedNonKeyHeldCount = recoveryOwnerUnackedNonKeyHeldCount;
                currentRecoveryOwnerUnackedNonKeyReplacedCount = recoveryOwnerUnackedNonKeyReplacedCount;
                currentRecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount = recoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount;
                currentRecoveryOwnerReplacedBeforeAckCount = recoveryOwnerReplacedBeforeAckCount;
                currentHighFrameAgeSuppressedDuringOwnerAckCount = highFrameAgeSuppressedDuringOwnerAckCount;
            }

            var captureToSendAgeMs = metrics.LastCaptureToSendAgeMs;
            var sourcePendingRawFrames = sourceFreshnessMetrics.PendingRawFrameCount;
            var sourceOldestPendingAgeMs = sourceFreshnessMetrics.OldestPendingRawFrameAgeMs;
            var captureToEncodeStartAgeMs = sourceFreshnessMetrics.LastCaptureToEncodeStartAgeMs;
            var lastEncodeDurationMs = sourceFreshnessMetrics.LastEncodeDurationMs;
            var lastEncodeTotalDurationMs = sourceFreshnessMetrics.LastEncodeTotalDurationMs;
            var inStartupWarmup = startupWarmupUntilUtc > clock.UtcNow;
            bool postAckModeGraceActive;
            bool bootstrapModeGraceActive;
            lock (gate)
            {
                postAckModeGraceActive = IsPostAckModeGraceActive_NoLock(sourceFreshnessMetrics.CurrentStreamEpoch, autoTuneNowUtc);
                bootstrapModeGraceActive = IsBeforeFirstVisibleApplyBootstrapGraceActive_NoLock(sourceFreshnessMetrics.CurrentStreamEpoch, autoTuneNowUtc);
            }
            var hasRateGatePressure = rateGateDropDelta > 0;
            var hasQueuePressure = queueEvictDropDelta > 0;
            var backpressureProbe = transportBackpressureProbeResolver?.Invoke();
            var hasLaneCongestion = backpressureProbe?.IsScreenShareTransportCongested == true;
            var hasSevereLaneCongestion = backpressureProbe?.IsScreenShareTransportSeverelyCongested == true;
            var laneQueueDepth = backpressureProbe?.ScreenShareTransportQueueDepth ?? 0;
            var laneQueuedBytes = backpressureProbe?.ScreenShareTransportQueuedBytes ?? 0;
            var laneOldestQueuedAgeMs = backpressureProbe?.ScreenShareTransportOldestQueuedAgeMs ?? 0;
            var laneRecentDrops = backpressureProbe?.ScreenShareTransportRecentDropCount ?? 0;
            var hasLaneRecentDrops = laneRecentDrops > 0;
            var recentHealthIssueCount = backpressureProbe?.ScreenShareTransportRecentHealthIssueCount ?? 0;
            var hasSevereHealthDegradation = backpressureProbe?.IsScreenShareTransportHealthSeverelyDegraded == true;
            var hasHealthDegradation = recentHealthIssueCount > 0;
            var hasActionableHealthDegradation = HasActionableBridgeHealth(
                recentHealthIssueCount,
                hasSevereHealthDegradation,
                hasLaneCongestion,
                hasSevereLaneCongestion,
                laneQueueDepth,
                laneRecentDrops,
                remotePressureMode);
            var bridgeHealthKind = FormatBridgeHealthKind(hasHealthDegradation || hasSevereHealthDegradation, hasActionableHealthDegradation);
            var recoveryLockSevereOverride =
                hasQueuePressure ||
                hasSevereLaneCongestion ||
                hasSevereHealthDegradation;
            var suppressAgeOnlyPressureForGrace = postAckModeGraceActive || bootstrapModeGraceActive;
            var remoteHighFrameAgePressure =
                remotePressureMode == ScreenShareRemotePressureMode.ReduceFps &&
                string.Equals(currentRemotePressureReason, ScreenSharePressureProtocol.PressureReasonHighFrameAge, StringComparison.Ordinal);
            var hasLocalReducedPressureFromAge =
                captureToSendAgeMs >= NormalToReducedCaptureToSendThresholdMs ||
                sourceOldestPendingAgeMs >= NormalToReducedCaptureToSendThresholdMs ||
                captureToEncodeStartAgeMs >= NormalToReducedCaptureToSendThresholdMs;
            var hasLocalCatchUpPressureFromAge =
                captureToSendAgeMs >= CatchUpModeCaptureToSendThresholdMs ||
                sourceOldestPendingAgeMs >= CatchUpModeCaptureToSendThresholdMs ||
                captureToEncodeStartAgeMs >= CatchUpModeCaptureToSendThresholdMs;

            if (postAckModeGraceActive && remoteHighFrameAgePressure)
            {
                postAckModeGraceSuppressedHighFrameAgeCount++;
            }

            var expectedSenderFpsHint = ResolveSenderTargetFramesPerSecond(
                currentSenderMode,
                configuredCap,
                inStartupWarmup && remotePressureMode == ScreenShareRemotePressureMode.None);
            var promotionEncodeBudgetMs = ResolvePromotionEncodeBudgetMs(expectedSenderFpsHint);
            var promotionCaptureToSendBudgetMs = ResolvePromotionCaptureToSendBudgetMs(expectedSenderFpsHint);
            var normalSenderFpsHint = ResolveSenderTargetFramesPerSecond(
                ScreenShareSenderFreshnessMode.Normal,
                configuredCap,
                false);
            var demotionEncodePressureMs = ResolveDemotionEncodePressureMs(normalSenderFpsHint);
            var hasLocalEncodePressure =
                lastEncodeTotalDurationMs >= demotionEncodePressureMs;
            var hasLocalCatchUpEncodePressure =
                lastEncodeTotalDurationMs >= CatchUpModeCaptureToSendThresholdMs;
            var helperProgressProofSatisfied =
                HasCurrentHelperVisibleOrApplyEvidence(
                    currentHelperEpochApplyCount,
                    currentHelperLastVisibleApplyFrameId,
                    currentHelperVisibleHeadFrameId,
                    currentHelperVisibleRecoveryFloorFrameId,
                    currentHelperFramesAppliedSinceLastGap);
            var rawHelperPressureBlocker =
                remotePressureMode != ScreenShareRemotePressureMode.None ||
                !string.Equals(currentRemotePressureReason, ScreenSharePressureProtocol.PressureReasonHealthy, StringComparison.Ordinal);
            var staleContinuityLossPressureOnly =
                currentPostReceiptPromotionSafe &&
                remotePressureMode == ScreenShareRemotePressureMode.None &&
                string.Equals(currentRemotePressureReason, ScreenSharePressureProtocol.PressureReasonContinuityLoss, StringComparison.Ordinal);
            var helperPressureBlocker =
                rawHelperPressureBlocker &&
                !staleContinuityLossPressureOnly;
            var rawHelperApplyCountBlocker = !helperProgressProofSatisfied;
            var helperApplyCountBlocker =
                rawHelperApplyCountBlocker &&
                !currentPostReceiptPromotionSafe;
            var recoveryLockBlocker =
                currentRecoveryLockActive &&
                !currentPostReceiptPromotionSafe;
            if (currentPostReceiptPromotionSafe &&
                ((currentRecoveryLockActive && !recoveryLockBlocker) ||
                 (rawHelperPressureBlocker && !helperPressureBlocker) ||
                 (rawHelperApplyCountBlocker && !helperApplyCountBlocker)))
            {
                var suppressedBlockers = new List<string>(3);
                if (currentRecoveryLockActive && !recoveryLockBlocker)
                {
                    suppressedBlockers.Add("recovery_lock_active");
                }

                if (rawHelperPressureBlocker && !helperPressureBlocker)
                {
                    suppressedBlockers.Add("helper_pressure");
                }

                if (rawHelperApplyCountBlocker && !helperApplyCountBlocker)
                {
                    suppressedBlockers.Add("helper_apply_count");
                }

                if (suppressedBlockers.Count > 0)
                {
                    lock (gate)
                    {
                        if (postReceiptBlockerSuppressedCount < long.MaxValue)
                        {
                            postReceiptBlockerSuppressedCount++;
                        }

                        lastPostReceiptBlockerSuppressedSet = string.Join(",", suppressedBlockers);
                    }
                }
            }
            var helperPromotionHealthy =
                !currentHelperEpochWarmupActive &&
                helperProgressProofSatisfied &&
                !helperPressureBlocker &&
                currentHelperEpochStaleDrops == 0;

            LogLocalLaneCongestionTransitions(hasLaneCongestion, hasSevereLaneCongestion, laneQueueDepth, laneQueuedBytes, laneRecentDrops);
            MaybeHandleTransportLaneRecentDrops(hasLaneRecentDrops, currentSessionId);
            MaybeLogFreshnessSummary(
                currentSessionId,
                metrics,
                sourceFreshnessMetrics,
                laneQueueDepth,
                laneQueuedBytes,
                laneOldestQueuedAgeMs,
                laneRecentDrops,
                bridgeHealthKind,
                recentHealthIssueCount,
                promotionCaptureToSendBudgetMs,
                promotionEncodeBudgetMs,
                demotionEncodePressureMs);

            var expectedTransportTuningLevel = ResolveSenderTuningLevel(currentSenderMode);

            if (captureToSendAgeMs < 0 &&
                sourcePendingRawFrames == 0 &&
                sourceOldestPendingAgeMs <= 0 &&
                captureToEncodeStartAgeMs < 0 &&
                !hasRateGatePressure &&
                !hasQueuePressure &&
                remotePressureMode == ScreenShareRemotePressureMode.None &&
                !hasLaneCongestion &&
                !hasSevereLaneCongestion &&
                !hasLaneRecentDrops &&
                !hasActionableHealthDegradation &&
                !hasSevereHealthDegradation &&
                !fileTransferDegradedHint &&
                !fileTransferCatchUpOnlyHintActive &&
                currentSenderMode == ScreenShareSenderFreshnessMode.Reduced &&
                currentCaptureFpsHint == expectedSenderFpsHint &&
                currentTransportTuningLevel == expectedTransportTuningLevel)
            {
                return;
            }

            var currentEpochRecoveryBurstActive =
                currentRecoveryBurstActive &&
                currentRecoveryBurstStreamEpoch == sourceFreshnessMetrics.CurrentStreamEpoch;
            var hasLocalReducedPressure =
                ((!suppressAgeOnlyPressureForGrace && hasLocalReducedPressureFromAge) ||
                 hasLocalEncodePressure ||
                 (!suppressAgeOnlyPressureForGrace && remoteHighFrameAgePressure));
            var hasLocalCatchUpPressure =
                ((!suppressAgeOnlyPressureForGrace && hasLocalCatchUpPressureFromAge) ||
                 hasLocalCatchUpEncodePressure);
            var hasImmediateReducedPressure =
                fileTransferDegradedHint ||
                hasLaneCongestion ||
                hasQueuePressure;
            var hasCatchUpExternalPressure =
                fileTransferCatchUpOnlyHintActive ||
                remotePressureMode == ScreenShareRemotePressureMode.CatchUpOnly ||
                hasLaneRecentDrops ||
                hasSevereLaneCongestion;

            var reducedHealthyTicksBefore = reducedRecoveryLowPressureTicks;
            var autoTuneDecision = ScreenShareSenderAutoTuneEvaluator.Evaluate(
                new ScreenShareSenderAutoTuneInputs(
                    currentSenderMode,
                    captureToSendCatchUpPressureTicks,
                    remoteObservedCatchUpPressureTicks,
                    normalToReducedPressureTicks,
                    remoteHighFrameAgeCatchUpEntryConsecutiveTicks,
                    catchUpRecoveryLowPressureTicks,
                    reducedRecoveryLowPressureTicks,
                    reducedPromotionEncodeSoftSpikeConsecutiveCount,
                    suppressAgeOnlyPressureForGrace,
                    remoteHighFrameAgePressure,
                    helperProgressProofSatisfied,
                    currentEpochRecoveryBurstActive,
                    bootstrapModeGraceActive,
                    postAckModeGraceActive,
                    hasCatchUpExternalPressure,
                    hasImmediateReducedPressure,
                    hasLocalCatchUpPressure,
                    hasLocalReducedPressure,
                    remotePressureObservedFrameAgeMs,
                    captureToSendAgeMs,
                    promotionCaptureToSendBudgetMs,
                    lastEncodeTotalDurationMs,
                    promotionEncodeBudgetMs,
                    demotionEncodePressureMs,
                    transitionGraceActive,
                    inStartupWarmup,
                    helperPromotionHealthy,
                    laneQueueDepth,
                    laneRecentDrops,
                    fileTransferDegradedHint,
                    fileTransferCatchUpOnlyHintActive,
                    hasLaneCongestion,
                    hasSevereLaneCongestion,
                    hasQueuePressure,
                    laneOldestQueuedAgeMs,
                    hasSevereHealthDegradation,
                    hasActionableHealthDegradation,
                    recoveryLockBlocker,
                    recoveryLockSevereOverride,
                    remotePressureMode,
                    currentRemotePressureReason));
            captureToSendCatchUpPressureTicks = autoTuneDecision.CaptureToSendCatchUpPressureTicks;
            remoteObservedCatchUpPressureTicks = autoTuneDecision.RemoteObservedCatchUpPressureTicks;
            normalToReducedPressureTicks = autoTuneDecision.NormalToReducedPressureTicks;
            remoteHighFrameAgeCatchUpEntryConsecutiveTicks = autoTuneDecision.RemoteHighFrameAgeCatchUpEntryConsecutiveTicks;
            catchUpRecoveryLowPressureTicks = autoTuneDecision.CatchUpRecoveryLowPressureTicks;
            reducedRecoveryLowPressureTicks = autoTuneDecision.ReducedRecoveryLowPressureTicks;
            reducedPromotionEncodeSoftSpikeConsecutiveCount = autoTuneDecision.ReducedPromotionEncodeSoftSpikeConsecutiveCount;
            var hasRemoteHighFrameAgeCatchUpPressure = autoTuneDecision.HasRemoteHighFrameAgeCatchUpPressure;
            var shouldEnterCatchUp = autoTuneDecision.ShouldEnterCatchUp;
            var shouldEnterReduced = autoTuneDecision.ShouldEnterReduced;
            var encodeBudgetBlocker = autoTuneDecision.EncodeBudgetBlocker;
            var encodeSoftPromotionOverrun = autoTuneDecision.EncodeSoftPromotionOverrun;
            var encodeSoftSpikeResetSuppressedThisTick = autoTuneDecision.EncodeSoftSpikeResetSuppressed;
            var catchUpRecoverySuppressedDueToRemoteHighFrameAgePressure =
                autoTuneDecision.CatchUpRecoverySuppressedDueToRemoteHighFrameAgePressure;
            var remoteHighFrameAgeCatchUpSuppressionReason = autoTuneDecision.RemoteHighFrameAgeCatchUpSuppressionReason;
            var nextSenderMode = autoTuneDecision.NextSenderMode;
            var currentOperatingState = autoTuneDecision.CurrentOperatingState;
            var nextOperatingState = autoTuneDecision.NextOperatingState;
            var currentGuardState = autoTuneDecision.GuardState;
            var dominantPressureBlocker = autoTuneDecision.DominantPressureBlocker;
            var recoveryLockAllowsSameTuningModeChange = false;
            if (remoteHighFrameAgePressure &&
                currentSenderMode != ScreenShareSenderFreshnessMode.CatchUp &&
                !hasRemoteHighFrameAgeCatchUpPressure &&
                !string.IsNullOrWhiteSpace(remoteHighFrameAgeCatchUpSuppressionReason))
            {
                lock (gate)
                {
                    switch (remoteHighFrameAgeCatchUpSuppressionReason)
                    {
                        case "bootstrap_grace":
                            if (remoteHighFrameAgeCatchUpSuppressedDueToBootstrapGraceCount < long.MaxValue)
                            {
                                remoteHighFrameAgeCatchUpSuppressedDueToBootstrapGraceCount++;
                            }

                            break;
                        case "post_ack_grace":
                            if (remoteHighFrameAgeCatchUpSuppressedDueToPostAckGraceCount < long.MaxValue)
                            {
                                remoteHighFrameAgeCatchUpSuppressedDueToPostAckGraceCount++;
                            }

                            break;
                        case "current_epoch_recovery_burst":
                            if (remoteHighFrameAgeCatchUpSuppressedDueToCurrentEpochRecoveryBurstCount < long.MaxValue)
                            {
                                remoteHighFrameAgeCatchUpSuppressedDueToCurrentEpochRecoveryBurstCount++;
                            }

                            break;
                        case "missing_helper_evidence":
                            if (remoteHighFrameAgeCatchUpSuppressedDueToMissingHelperEvidenceCount < long.MaxValue)
                            {
                                remoteHighFrameAgeCatchUpSuppressedDueToMissingHelperEvidenceCount++;
                            }

                            break;
                        default:
                            if (remoteHighFrameAgeCatchUpSuppressedDueToUnderThresholdCount < long.MaxValue)
                            {
                                remoteHighFrameAgeCatchUpSuppressedDueToUnderThresholdCount++;
                            }

                            break;
                    }

                    lastRemoteHighFrameAgeCatchUpSuppressionReason = remoteHighFrameAgeCatchUpSuppressionReason;
                }
            }

            if (catchUpRecoverySuppressedDueToRemoteHighFrameAgePressure)
            {
                lock (gate)
                {
                    if (catchUpRecoverySuppressedDueToRemoteHighFrameAgeCount < long.MaxValue)
                    {
                        catchUpRecoverySuppressedDueToRemoteHighFrameAgeCount++;
                    }
                }
            }

            if (bootstrapModeGraceActive &&
                !hasCatchUpExternalPressure &&
                (captureToSendCatchUpPressureTicks > 0 || remoteObservedCatchUpPressureTicks > 0 || remoteHighFrameAgePressure))
            {
                bootstrapGraceSuppressedCatchUpCount++;
            }

            ApplyRemotePressureHold(ref nextSenderMode);
            recoveryLockAllowsSameTuningModeChange =
                recoveryLockBlocker &&
                !recoveryLockSevereOverride &&
                nextSenderMode != currentSenderMode &&
                ScreenShareSenderAutoTuneEvaluator.CanRecoveryLockAllowModeTransition(currentSenderMode, nextSenderMode);
            if (recoveryLockAllowsSameTuningModeChange)
            {
                lock (gate)
                {
                    if (recoveryLockAllowedSameTuningModeChangeCount < long.MaxValue)
                    {
                        recoveryLockAllowedSameTuningModeChangeCount++;
                    }

                    lastRecoveryLockAllowedSameTuningModeChange =
                        $"{FormatSenderFreshnessMode(currentSenderMode)}->{FormatSenderFreshnessMode(nextSenderMode)}";
                }
            }

            if (recoveryLockBlocker &&
                !recoveryLockAllowsSameTuningModeChange &&
                !recoveryLockSevereOverride &&
                nextSenderMode != currentSenderMode)
            {
                LogTransportProfileTransitionBlocked(
                    currentSessionId,
                    currentSenderMode,
                    nextSenderMode,
                    "recovery_lock_active");
                nextSenderMode = currentSenderMode;
            }

            var wouldChangeTransportProfile =
                ResolveSenderTuningLevel(currentSenderMode) != ResolveSenderTuningLevel(nextSenderMode);
            var severeTransitionPressure =
                hasQueuePressure ||
                laneRecentDrops > 0 ||
                hasSevereLaneCongestion ||
                remotePressureMode == ScreenShareRemotePressureMode.CatchUpOnly ||
                fileTransferCatchUpOnlyHintActive;
            if (transitionGraceActive &&
                wouldChangeTransportProfile &&
                !severeTransitionPressure)
            {
                LogTransportProfileTransitionBlocked(
                    currentSessionId,
                    currentSenderMode,
                    nextSenderMode,
                    "transition_grace_active");
                nextSenderMode = currentSenderMode;
            }

            if (currentSenderMode == ScreenShareSenderFreshnessMode.CatchUp &&
                nextSenderMode == ScreenShareSenderFreshnessMode.Reduced &&
                remoteHighFrameAgePressure)
            {
                lock (gate)
                {
                    if (catchUpExitWhileRemoteHighFrameAgePressureCount < long.MaxValue)
                    {
                        catchUpExitWhileRemoteHighFrameAgePressureCount++;
                    }
                }
            }

            var reducedHealthyTicksAfter = reducedRecoveryLowPressureTicks;
            RecordReducedPromotionEvaluation(
                currentSessionId,
                currentSenderMode,
                captureToSendAgeMs,
                promotionCaptureToSendBudgetMs,
                lastEncodeTotalDurationMs,
                promotionEncodeBudgetMs,
                remotePressureMode,
                currentRemotePressureReason,
                currentHelperEpochWarmupActive,
                currentHelperEpochApplyCount,
                currentHelperEpochNeedMoreInputCount,
                currentHelperEpochHealthySignalCount,
                currentHelperEpochStaleDrops,
                currentHelperSteadyVisibleProgressActive,
                currentHelperStableVisibleHeadFrameId,
                currentHelperFramesAppliedSinceLastGap,
                helperProgressProofSatisfied,
                helperPressureBlocker,
                helperApplyCountBlocker,
                bridgeHealthKind,
                recentHealthIssueCount,
                rateGateDropDelta,
                queueEvictDropDelta,
                supersededPendingRawFrameDelta,
                recoveryLockBlocker,
                transitionGraceActive,
                encodeBudgetBlocker,
                encodeSoftPromotionOverrun,
                encodeSoftSpikeResetSuppressedThisTick,
                reducedHealthyTicksBefore,
                reducedHealthyTicksAfter,
                laneQueueDepth,
                laneRecentDrops,
                hasActionableHealthDegradation,
                fileTransferDegradedHint,
                fileTransferCatchUpOnlyHintActive);

            MaybeLogSenderPromotionBlocked(
                currentSessionId,
                currentSenderMode,
                nextSenderMode,
                inStartupWarmup,
                captureToSendAgeMs,
                promotionCaptureToSendBudgetMs,
                lastEncodeTotalDurationMs,
                remotePressureObservedFrameAgeMs,
                laneQueueDepth,
                laneRecentDrops,
                hasActionableHealthDegradation,
                bridgeHealthKind,
                recentHealthIssueCount,
                remotePressureMode,
                currentRemotePressureReason,
                fileTransferDegradedHint,
                fileTransferCatchUpOnlyHintActive,
                hasRateGatePressure,
                hasQueuePressure,
                reducedRecoveryLowPressureTicks,
                supersededPendingRawFrameDelta,
                transitionGraceActive,
                encodeBudgetBlocker,
                promotionEncodeBudgetMs,
                demotionEncodePressureMs,
                currentHelperEpochWarmupActive,
                currentHelperEpochApplyCount,
                currentHelperEpochNeedMoreInputCount,
                currentHelperEpochHealthySignalCount,
                currentHelperEpochStaleDrops,
                currentHelperSteadyVisibleProgressActive,
                currentHelperStableVisibleHeadFrameId,
                currentHelperFramesAppliedSinceLastGap,
                currentRemoteHelperFactHealthyActive,
                helperProgressProofSatisfied,
                helperPressureBlocker,
                helperApplyCountBlocker,
                recoveryLockBlocker);
            MaybeLogReducedPromotionSummary(currentSessionId, currentSenderMode);

            if (!inStartupWarmup && startupWarmupUntilUtc != default)
            {
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_startup_warmup_exited; capture_to_send_age_ms={captureToSendAgeMs}; remote_pressure_mode={FormatRemotePressureMode(remotePressureMode)}");
                startupWarmupUntilUtc = default;
            }

            var nextSenderModeReason = autoTuneDecision.NextSenderModeReason;
            if (recoveryLockAllowsSameTuningModeChange)
            {
                LogRecoveryLockAllowedSameTuningModeChange(
                    currentSessionId,
                    currentSenderMode,
                    nextSenderMode,
                    nextSenderModeReason,
                    currentRecoveryLockStreamEpoch);
            }

            if (remoteHighFrameAgePressure ||
                remoteHighFrameAgeCatchUpEntryConsecutiveTicks > 0 ||
                nextSenderMode == ScreenShareSenderFreshnessMode.CatchUp ||
                !string.IsNullOrWhiteSpace(remoteHighFrameAgeCatchUpSuppressionReason))
            {
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    new ScreenShareSenderAutoTuneDecisionSnapshot(
                        currentSessionId,
                        currentSenderMode,
                        nextSenderMode,
                        currentOperatingState,
                        nextOperatingState,
                        currentGuardState,
                        dominantPressureBlocker,
                        nextSenderModeReason,
                        sourceFreshnessMetrics.CurrentStreamEpoch,
                        remotePressureMode,
                        currentRemotePressureReason,
                        remotePressureObservedFrameAgeMs,
                        remoteHighFrameAgeCatchUpEntryConsecutiveTicks,
                        helperProgressProofSatisfied,
                        currentEpochRecoveryBurstActive,
                        currentRecoveryLockActive,
                        currentRecoveryLockStreamEpoch,
                        postAckModeGraceActive,
                        bootstrapModeGraceActive,
                        shouldEnterReduced,
                        shouldEnterCatchUp,
                        catchUpRecoverySuppressedDueToRemoteHighFrameAgePressure,
                        remoteHighFrameAgeCatchUpSuppressionReason).ToLogMessage());
            }

            ApplySenderFreshnessMode(
                currentPipeline,
                currentCaptureSource,
                currentSessionId,
                nextSenderMode,
                nextSenderModeReason,
                configuredCap,
                inStartupWarmup);
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
            return IsSenderFreshnessDegraded(senderFreshnessMode);
        }
    }

    private static ScreenCaptureFreshnessMetrics GetCaptureFreshnessMetricsSnapshot(IScreenCaptureSource? currentCaptureSource)
    {
        return currentCaptureSource is IScreenCaptureFreshnessMetricsSource freshnessMetricsSource
            ? freshnessMetricsSource.GetFreshnessMetricsSnapshot()
            : new ScreenCaptureFreshnessMetrics();
    }

    private int PurgeSenderRawBacklog(IScreenCaptureSource? currentCaptureSource)
    {
        return currentCaptureSource is IScreenCaptureFreshnessMetricsSource freshnessMetricsSource
            ? freshnessMetricsSource.PurgePendingRawFrames()
            : 0;
    }

    private void MaybeRequestSenderFreshnessKeyFrame(string reason)
    {
        var now = clock.UtcNow;
        lock (gate)
        {
            if (lastSenderFreshnessKeyFrameRequestedUtc.HasValue &&
                now - lastSenderFreshnessKeyFrameRequestedUtc.Value < SenderFreshnessKeyFrameRequestInterval)
            {
                return;
            }

            lastSenderFreshnessKeyFrameRequestedUtc = now;
        }

        RequestKeyFrame(reason);
    }

    private void ApplyRemotePressureHold(ref ScreenShareSenderFreshnessMode nextMode)
    {
        DateTimeOffset? appliedUtc;
        ScreenShareRemotePressureMode currentMode;
        lock (gate)
        {
            appliedUtc = remotePressureAppliedUtc;
            currentMode = remotePressureMode;
        }

        if (!appliedUtc.HasValue)
        {
            return;
        }

        var elapsed = clock.UtcNow - appliedUtc.Value;
        if (currentMode == ScreenShareRemotePressureMode.CatchUpOnly &&
            !string.Equals(remotePressureReason, ScreenSharePressureProtocol.PressureReasonContinuityLoss, StringComparison.Ordinal) &&
            elapsed < RemoteCatchUpOnlyMinimumHold)
        {
            nextMode = MaxFreshnessMode(nextMode, ScreenShareSenderFreshnessMode.CatchUp);
        }
        else if (currentMode == ScreenShareRemotePressureMode.ReduceFps &&
                 elapsed < RemoteReduceFpsMinimumHold)
        {
            nextMode = MaxFreshnessMode(nextMode, ScreenShareSenderFreshnessMode.Reduced);
        }
    }

    private void StartTransportProfileTransition(
        string currentSessionId,
        ScreenShareTransportTuningLevel fromTransportTuningLevel,
        ScreenShareTransportTuningLevel toTransportTuningLevel,
        long streamEpoch)
    {
        lock (gate)
        {
            transitionActive = true;
            transitionStreamEpoch = Math.Max(0, streamEpoch);
            transitionStartedUtc = clock.UtcNow;
            transitionFirstRemoteApplySeen = false;
            transitionRemoteApplyCount = 0;
            ResetHelperCurrentEpochState_NoLock(streamEpoch);
            transitionFromTransportTuningLevel = fromTransportTuningLevel;
            transitionToTransportTuningLevel = toTransportTuningLevel;
        }

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_transport_profile_transition_started; session_id={currentSessionId}; from_profile={FormatTransportTuningLevel(fromTransportTuningLevel)}; to_profile={FormatTransportTuningLevel(toTransportTuningLevel)}; stream_epoch={Math.Max(0, streamEpoch)}; grace_active=1; current_epoch_remote_apply_count=0; current_epoch_need_more_input_count=unavailable; current_epoch_stale_drops=0");
    }

    private void RefreshHelperCurrentEpochState(long currentStreamEpoch)
    {
        if (currentStreamEpoch <= 0)
        {
            return;
        }

        lock (gate)
        {
            if (helperCurrentEpochStateStreamEpoch == currentStreamEpoch)
            {
                return;
            }

            if (helperCurrentEpochStateStreamEpoch == 0)
            {
                helperCurrentEpochStateStreamEpoch = currentStreamEpoch;
                return;
            }

            ResetHelperCurrentEpochState_NoLock(currentStreamEpoch);
        }
    }

    private void ResetHelperCurrentEpochState_NoLock(long currentStreamEpoch)
    {
        if (currentStreamEpoch > 0 &&
            activeRecoveryBurst is { } recoveryBurst &&
            recoveryBurst.StreamEpoch != currentStreamEpoch)
        {
            if (ShouldFreezeRecoveryBurstIdentity_NoLock(recoveryBurst))
            {
                RecordRecoveryEpochTakeoverSuppressed_NoLock(
                    recoveryBurst.StreamEpoch,
                    currentStreamEpoch,
                    recoveryBurst.Phase);
            }
            else
            {
                if (recoveryBurst.BurstToken > 0)
                {
                    clearRecoveryBurstTransportFallback?.Invoke(recoveryBurst.BurstToken);
                }

                if (recoveryBurst.OwnerFrameId >= 0)
                {
                    recoveryOwnerReplacedBeforeAckCount++;
                }

                StopRecoveryOwnerPendingTimer_NoLock();
                activeRecoveryBurst = null;
                recoveryFirstHelperHeadAdvanceUtc = default;
                recoveryProtectedFollowerCount = 0;
                recoveryProtectedFrameCount = 0;
                ClearRecoveryOwnerPendingHold_NoLock();
            }
        }

        if (currentStreamEpoch > 0 &&
            lastCompletedRecovery is { } completedRecovery &&
            completedRecovery.StreamEpoch > 0 &&
            completedRecovery.StreamEpoch != currentStreamEpoch)
        {
            ClearLastCompletedRecoveryOutcome_NoLock();
        }

        helperCurrentEpochStateStreamEpoch = Math.Max(0, currentStreamEpoch);
        helperCurrentEpochWarmupActive = true;
        helperCurrentEpochApplyCount = 0;
        helperCurrentEpochNeedMoreInputCount = 0;
        helperCurrentEpochHealthySignalCount = 0;
        helperCurrentEpochStaleDrops = 0;
        helperSteadyVisibleProgressActive = false;
        helperVisibleHeadFrameId = -1;
        helperVisibleRecoveryFloorFrameId = -1;
        helperLastVisibleApplyFrameId = -1;
        helperAppliedHeadFrameId = -1;
        helperStableVisibleHeadFrameId = -1;
        helperCurrentEpochRecoveryKeyframeApplyCount = 0;
        helperFramesAppliedSinceLastGap = 0;
        helperLatestVisibleProgressEpoch = 0;
        helperLatestVisibleProgressUtc = default;
        ClearPersistedHelperProof_NoLock();
        if (remoteHelperFactHealthyActive &&
            remoteHelperFactHealthyClearCount < long.MaxValue)
        {
            remoteHelperFactHealthyClearCount++;
        }
        remoteHelperFactHealthyActive = false;
        remoteHelperFactHealthySource = string.Empty;
        remoteHelperFactProofFrameId = -1;
        remoteHelperFactHealthyClearReason = "new_stream_epoch";
        postRecoveryAgeGraceEpoch = 0;
        postRecoveryAgeGraceUntilUtc = default;
        postRecoveryAgeGraceSuppressedCount = 0;
        helperReducedModeEntryStableVisibleHeadFrameId = -1;
        helperReducedModeEntryStreamEpoch = 0;
        ClearSatisfiedRecoveryFloor_NoLock();
    }

    private bool IsTransportProfileTransitionGraceActive(DateTimeOffset nowUtc)
    {
        lock (gate)
        {
            return IsTransportProfileTransitionGraceActive_NoLock(nowUtc);
        }
    }

    private bool IsTransportProfileTransitionGraceActive_NoLock(DateTimeOffset nowUtc)
    {
        if (!transitionActive || transitionStartedUtc == default)
        {
            return false;
        }

        if (nowUtc - transitionStartedUtc >= TransportProfileTransitionGraceWindow)
        {
            return false;
        }

        return !transitionFirstRemoteApplySeen || transitionRemoteApplyCount < 3;
    }

    private void MaybeCompleteTransportProfileTransition(string currentSessionId, DateTimeOffset nowUtc)
    {
        ScreenShareTransportTuningLevel fromTransportTuningLevel;
        ScreenShareTransportTuningLevel toTransportTuningLevel;
        long streamEpoch;
        int remoteApplyCount;
        bool shouldLogCompletion = false;

        lock (gate)
        {
            if (!transitionActive || IsTransportProfileTransitionGraceActive_NoLock(nowUtc))
            {
                return;
            }

            fromTransportTuningLevel = transitionFromTransportTuningLevel;
            toTransportTuningLevel = transitionToTransportTuningLevel;
            streamEpoch = transitionStreamEpoch;
            remoteApplyCount = transitionRemoteApplyCount;
            transitionActive = false;
            transitionStreamEpoch = 0;
            transitionStartedUtc = default;
            transitionFirstRemoteApplySeen = false;
            transitionRemoteApplyCount = 0;
            transitionFromTransportTuningLevel = ScreenShareTransportTuningLevel.Normal;
            transitionToTransportTuningLevel = ScreenShareTransportTuningLevel.Normal;
            shouldLogCompletion = true;
        }

        if (!shouldLogCompletion)
        {
            return;
        }

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_transport_profile_transition_completed; session_id={currentSessionId}; from_profile={FormatTransportTuningLevel(fromTransportTuningLevel)}; to_profile={FormatTransportTuningLevel(toTransportTuningLevel)}; stream_epoch={streamEpoch}; grace_active=0; current_epoch_remote_apply_count={Math.Max(0, remoteApplyCount)}; current_epoch_need_more_input_count=unavailable; current_epoch_stale_drops=0");
    }

    private void RecordTransitionRemoteApplySignal(string currentSessionId, string reason, long observedFrameAgeMs, long recentStaleDrops)
    {
        ScreenShareTransportTuningLevel fromTransportTuningLevel;
        ScreenShareTransportTuningLevel toTransportTuningLevel;
        long streamEpoch;
        int remoteApplyCount;
        bool shouldLogCompletion = false;
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? ScreenSharePressureProtocol.PressureReasonHealthy
            : reason.Trim();

        lock (gate)
        {
            if (!transitionActive)
            {
                return;
            }

            if (!IsApplyDerivedRemotePressureReason(normalizedReason) || Math.Max(0, recentStaleDrops) > 0)
            {
                return;
            }

            transitionFirstRemoteApplySeen = true;
            if (transitionRemoteApplyCount < int.MaxValue)
            {
                transitionRemoteApplyCount++;
            }

            if (IsTransportProfileTransitionGraceActive_NoLock(clock.UtcNow))
            {
                return;
            }

            fromTransportTuningLevel = transitionFromTransportTuningLevel;
            toTransportTuningLevel = transitionToTransportTuningLevel;
            streamEpoch = transitionStreamEpoch;
            remoteApplyCount = transitionRemoteApplyCount;
            transitionActive = false;
            transitionStreamEpoch = 0;
            transitionStartedUtc = default;
            transitionFirstRemoteApplySeen = false;
            transitionRemoteApplyCount = 0;
            transitionFromTransportTuningLevel = ScreenShareTransportTuningLevel.Normal;
            transitionToTransportTuningLevel = ScreenShareTransportTuningLevel.Normal;
            shouldLogCompletion = true;
        }

        if (shouldLogCompletion)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_transport_profile_transition_completed; session_id={currentSessionId}; from_profile={FormatTransportTuningLevel(fromTransportTuningLevel)}; to_profile={FormatTransportTuningLevel(toTransportTuningLevel)}; stream_epoch={streamEpoch}; grace_active=0; current_epoch_remote_apply_count={Math.Max(0, remoteApplyCount)}; current_epoch_need_more_input_count=unavailable; current_epoch_stale_drops=0; trigger_reason={normalizedReason}; observed_frame_age_ms={Math.Max(0, observedFrameAgeMs)}");
        }
    }

    private void LogTransportProfileTransitionBlocked(
        string currentSessionId,
        ScreenShareSenderFreshnessMode currentSenderMode,
        ScreenShareSenderFreshnessMode blockedNextMode,
        string reason)
    {
        if (string.IsNullOrWhiteSpace(currentSessionId))
        {
            return;
        }

        long streamEpoch;
        int remoteApplyCount;
        long currentRemoteStaleDrops;
        lock (gate)
        {
            streamEpoch = transitionStreamEpoch;
            remoteApplyCount = transitionRemoteApplyCount;
            currentRemoteStaleDrops = remotePressureRecentStaleDrops;
        }

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_transport_profile_transition_blocked; session_id={currentSessionId}; from_mode={FormatSenderFreshnessMode(currentSenderMode)}; blocked_to_mode={FormatSenderFreshnessMode(blockedNextMode)}; reason={reason}; grace_active=1; stream_epoch={streamEpoch}; current_epoch_remote_apply_count={Math.Max(0, remoteApplyCount)}; current_epoch_need_more_input_count=unavailable; current_epoch_stale_drops={Math.Max(0, currentRemoteStaleDrops)}");
    }

    private void LogRecoveryLockAllowedSameTuningModeChange(
        string currentSessionId,
        ScreenShareSenderFreshnessMode currentSenderMode,
        ScreenShareSenderFreshnessMode nextSenderMode,
        string reason,
        long recoveryLockEpoch)
    {
        if (string.IsNullOrWhiteSpace(currentSessionId))
        {
            return;
        }

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_recovery_lock_allowed_same_tuning_mode_change; session_id={currentSessionId}; current_mode={FormatSenderFreshnessMode(currentSenderMode)}; next_mode={FormatSenderFreshnessMode(nextSenderMode)}; reason={(string.IsNullOrWhiteSpace(reason) ? "(none)" : reason)}; recovery_lock_stream_epoch={Math.Max(0, recoveryLockEpoch)}");
    }

    private static bool IsApplyDerivedRemotePressureReason(string reason)
    {
        return string.Equals(reason, ScreenSharePressureProtocol.PressureReasonHealthy, StringComparison.Ordinal) ||
               string.Equals(reason, ScreenSharePressureProtocol.PressureReasonHighFrameAge, StringComparison.Ordinal) ||
               string.Equals(reason, ScreenSharePressureProtocol.PressureReasonSlowApplyCadence, StringComparison.Ordinal);
    }

    private void LogLocalLaneCongestionTransitions(
        bool isCongested,
        bool isSeverelyCongested,
        int queueDepth,
        int queuedBytes,
        long recentDrops)
    {
        string? transitionEvent = null;
        lock (gate)
        {
            if (lastLocalLaneCongestionActive != isCongested)
            {
                lastLocalLaneCongestionActive = isCongested;
                transitionEvent = isCongested
                    ? "screenshare_lane_congestion_entered"
                    : "screenshare_lane_congestion_exited";
            }

            lastLocalLaneSevereCongestionActive = isSeverelyCongested;
        }

        if (transitionEvent is not null)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event={transitionEvent}; queue_depth={queueDepth}; queued_bytes={queuedBytes}; severe={(isSeverelyCongested ? 1 : 0)}; recent_drops={recentDrops}");
        }
    }

    private void MaybeHandleTransportLaneRecentDrops(bool hasLaneRecentDrops, string currentSessionId)
    {
        var shouldRequestKeyFrame = false;
        lock (gate)
        {
            if (lastLocalLaneRecentDropActive != hasLaneRecentDrops)
            {
                lastLocalLaneRecentDropActive = hasLaneRecentDrops;
                shouldRequestKeyFrame = hasLaneRecentDrops;
            }
        }

        if (!shouldRequestKeyFrame)
        {
            return;
        }

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_transport_stale_video_purge_detected; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}");
        RequestKeyFrame("transport_stale_video_purged");
    }

    private static bool HasActionableBridgeHealth(
        long recentHealthIssueCount,
        bool isSeverelyDegraded,
        bool hasLaneCongestion,
        bool hasSevereLaneCongestion,
        int laneQueueDepth,
        long laneRecentDrops,
        ScreenShareRemotePressureMode remotePressureMode)
    {
        var hasBridgeHealth = recentHealthIssueCount > 0 || isSeverelyDegraded;
        if (!hasBridgeHealth)
        {
            return false;
        }

        return hasLaneCongestion ||
               hasSevereLaneCongestion ||
               laneQueueDepth > 0 ||
               laneRecentDrops > 0 ||
               remotePressureMode != ScreenShareRemotePressureMode.None;
    }

    private static string FormatBridgeHealthKind(bool hasBridgeHealth, bool hasActionableBridgeHealth)
    {
        if (hasActionableBridgeHealth)
        {
            return "actionable";
        }

        return hasBridgeHealth ? "advisory" : "none";
    }

    internal static string FormatTransportTuningLevel(ScreenShareTransportTuningLevel level)
        => level switch
        {
            ScreenShareTransportTuningLevel.QualityProtected => "quality_protected",
            ScreenShareTransportTuningLevel.BandwidthReduced => "bandwidth_reduced",
            _ => "normal",
        };

    internal static string FormatRemotePressureMode(ScreenShareRemotePressureMode mode)
        => mode switch
        {
            ScreenShareRemotePressureMode.ReduceFps => "reduce_fps",
            ScreenShareRemotePressureMode.CatchUpOnly => "catch_up_only",
            _ => "none",
        };

    internal static string FormatSenderFreshnessMode(ScreenShareSenderFreshnessMode mode)
        => mode switch
        {
            ScreenShareSenderFreshnessMode.Reduced => "reduced",
            ScreenShareSenderFreshnessMode.CatchUp => "catch_up",
            _ => "normal",
        };

    private long GetRecoveryLockDurationMs(DateTimeOffset nowUtc)
    {
        lock (gate)
        {
            return GetRecoveryLockDurationMs_NoLock(nowUtc);
        }
    }

    private long GetRecoveryLockDurationMs_NoLock(DateTimeOffset nowUtc)
    {
        if (!recoveryLockActive || recoveryLockStartedUtc == default)
        {
            return 0;
        }

        return Math.Max(0, (long)(nowUtc - recoveryLockStartedUtc).TotalMilliseconds);
    }

    private static int ResolvePromotionEncodeBudgetMs(int expectedSenderFpsHint)
        => ResolveFrameIntervalBudgetMs(
            expectedSenderFpsHint,
            PromotionEncodeBudgetFraction,
            PromotionEncodeBudgetMinMs,
            PromotionEncodeBudgetMaxMs);

    private static int ResolvePromotionCaptureToSendBudgetMs(int expectedSenderFpsHint)
        => ResolveFrameIntervalBudgetMs(
            expectedSenderFpsHint,
            PromotionCaptureToSendBudgetFraction,
            PromotionCaptureToSendBudgetMinMs,
            PromotionCaptureToSendBudgetMaxMs);

    private static int ResolveDemotionEncodePressureMs(int normalSenderFpsHint)
        => ResolveFrameIntervalBudgetMs(
            normalSenderFpsHint,
            DemotionEncodePressureBudgetFraction,
            DemotionEncodePressureBudgetMinMs,
            DemotionEncodePressureBudgetMaxMs);

    private static int ResolveFrameIntervalBudgetMs(
        int framesPerSecond,
        double fraction,
        int minBudgetMs,
        int maxBudgetMs)
    {
        if (framesPerSecond <= 0)
        {
            return minBudgetMs;
        }

        var frameIntervalMs = 1000d / framesPerSecond;
        var budgetMs = (int)Math.Floor(frameIntervalMs * fraction);
        return Math.Clamp(budgetMs, minBudgetMs, maxBudgetMs);
    }

    private static bool IsSenderFreshnessDegraded(ScreenShareSenderFreshnessMode mode)
    {
        return mode != ScreenShareSenderFreshnessMode.Normal;
    }

    private static ScreenShareSenderFreshnessMode MaxFreshnessMode(
        ScreenShareSenderFreshnessMode left,
        ScreenShareSenderFreshnessMode right)
    {
        return left >= right ? left : right;
    }

    private static int ResolveSenderTargetFramesPerSecond(
        ScreenShareSenderFreshnessMode mode,
        int configuredCap,
        bool inStartupWarmup)
    {
        var normalSenderCeiling = string.Equals(
            FeatureFlags.ScreenShareQualityProfile,
            FeatureFlags.ScreenShareQualityProfileTunaQuality,
            StringComparison.Ordinal)
            ? TunaQualitySenderFramesPerSecond
            : NormalSenderFramesPerSecond;

        return mode switch
        {
            ScreenShareSenderFreshnessMode.CatchUp => CatchUpSenderFramesPerSecond,
            ScreenShareSenderFreshnessMode.Reduced => ReducedSenderFramesPerSecond,
            _ => Math.Min(configuredCap, normalSenderCeiling),
        };
    }

    private static ScreenShareTransportTuningLevel ResolveSenderTuningLevel(ScreenShareSenderFreshnessMode mode)
        => ScreenShareSenderAutoTuneEvaluator.ResolveSenderTuningLevel(mode);

    private static bool CanRecoveryLockAllowModeTransition_NoLock(
        ScreenShareSenderFreshnessMode currentMode,
        ScreenShareSenderFreshnessMode nextMode)
        => ScreenShareSenderAutoTuneEvaluator.CanRecoveryLockAllowModeTransition(currentMode, nextMode);

    private static string FormatRecoveryBurstPhase(RecoveryBurstPhase phase)
    {
        return phase switch
        {
            RecoveryBurstPhase.Requested => "requested",
            RecoveryBurstPhase.OwnerPending => "owner_pending",
            RecoveryBurstPhase.OwnerEmittedAwaitingHelperAck => "owner_emitted_awaiting_helper_ack",
            RecoveryBurstPhase.PostAckHold => "post_ack_hold",
            _ => "idle",
        };
    }

    private static long GetRecoveryAckTargetFrameId_NoLock(ActiveRecoveryBurst recoveryBurst)
    {
        if (recoveryBurst.OwnerFrameId < 0)
        {
            return -1;
        }

        return recoveryBurst.OwnerFrameId;
    }

    private bool IsRecoveryBurstInterlocked_NoLock()
    {
        return activeRecoveryBurst is { } recoveryBurst &&
               (recoveryBurst.Phase == RecoveryBurstPhase.Requested ||
                recoveryBurst.Phase == RecoveryBurstPhase.OwnerPending ||
                recoveryBurst.Phase == RecoveryBurstPhase.OwnerEmittedAwaitingHelperAck);
    }

    private bool ShouldFreezeRecoveryBurstIdentity_NoLock(ActiveRecoveryBurst recoveryBurst)
    {
        return recoveryBurst.OwnerFrameId >= 0 &&
               (recoveryBurst.Phase == RecoveryBurstPhase.OwnerEmittedAwaitingHelperAck ||
                recoveryBurst.Phase == RecoveryBurstPhase.PostAckHold);
    }

    private void RecordRecoveryEpochTakeoverSuppressed_NoLock(long fromEpoch, long toEpoch, RecoveryBurstPhase phase)
    {
        if (recoveryEpochTakeoverSuppressedAfterOwnerEmitCount < long.MaxValue)
        {
            recoveryEpochTakeoverSuppressedAfterOwnerEmitCount++;
        }

        lastRecoveryEpochTakeoverSuppressedFromEpoch = Math.Max(0, fromEpoch);
        lastRecoveryEpochTakeoverSuppressedToEpoch = Math.Max(0, toEpoch);
        lastRecoveryEpochTakeoverSuppressedPhase = FormatRecoveryBurstPhase(phase);

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_recovery_epoch_takeover_suppressed; session_id={(string.IsNullOrWhiteSpace(sessionId) ? "(none)" : sessionId)}; from_stream_epoch={(fromEpoch > 0 ? fromEpoch.ToString(CultureInfo.InvariantCulture) : "(none)")}; to_stream_epoch={(toEpoch > 0 ? toEpoch.ToString(CultureInfo.InvariantCulture) : "(none)")}; phase={(string.IsNullOrWhiteSpace(lastRecoveryEpochTakeoverSuppressedPhase) ? "(none)" : lastRecoveryEpochTakeoverSuppressedPhase)}");
    }

    private bool TryGetProfileTransitionDeferralReason_NoLock(
        long streamEpoch,
        DateTimeOffset nowUtc,
        out string deferralReason)
    {
        if (IsRecoveryBurstInterlocked_NoLock())
        {
            deferralReason = "recovery_burst_interlocked";
            return true;
        }

        if (recoveryLockActive)
        {
            deferralReason = "recovery_lock_active";
            return true;
        }

        if (streamEpoch > 0 &&
            helperCurrentEpochStateStreamEpoch == streamEpoch)
        {
            if (helperCurrentEpochNeedMoreInputCount > 0)
            {
                deferralReason = "helper_need_more_input";
                return true;
            }

            if (helperCurrentEpochStaleDrops > 0)
            {
                deferralReason = "helper_stale_recovery";
                return true;
            }

            var helperVisibleProgressFresh =
                helperLatestVisibleProgressEpoch == streamEpoch &&
                helperLatestVisibleProgressUtc != default &&
                nowUtc - helperLatestVisibleProgressUtc <= TimeSpan.FromMilliseconds(400);
            if (helperVisibleProgressFresh &&
                (string.Equals(remotePressureReason, ScreenSharePressureProtocol.PressureReasonHighFrameAge, StringComparison.Ordinal) ||
                 string.Equals(remotePressureReason, ScreenSharePressureProtocol.PressureReasonSlowApplyCadence, StringComparison.Ordinal)))
            {
                deferralReason = "helper_progress_still_advancing";
                return true;
            }
        }

        deferralReason = string.Empty;
        return false;
    }

    private string GetCurrentRecoveryBurstPhaseForLog()
    {
        lock (gate)
        {
            return FormatRecoveryBurstPhase(GetActiveRecoveryBurstPhase_NoLock());
        }
    }

    private static string FormatMetricValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "(none)"
            : value;
    }

    private static double ComputeAverage(long total, long count)
    {
        return count > 0 ? total / (double)count : 0d;
    }

    private void ApplySenderFreshnessMode(
        ScreenShareFrameSendPipeline? currentPipeline,
        IScreenCaptureSource? currentCaptureSource,
        string currentSessionId,
        ScreenShareSenderFreshnessMode nextMode,
        string reason,
        int configuredCap,
        bool inStartupWarmup)
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

        var nextHint = ResolveSenderTargetFramesPerSecond(
            nextMode,
            configuredCap,
            inStartupWarmup && remotePressureMode == ScreenShareRemotePressureMode.None);
        var nextTransportTuningLevel = ResolveSenderTuningLevel(nextMode);
        ScreenShareSenderFreshnessMode previousMode;
        var previousHint = 0;
        ScreenShareTransportTuningLevel previousTransportTuningLevel;
        bool modeChanged;
        bool degradedStateChanged;
        bool profileChanged;
        var deferProfileTransition = false;
        var profileTransitionDeferralReason = string.Empty;
        var droppedPendingRawFrames = 0;
        var droppedQueuedFrames = 0;
        var transitionStreamEpoch = 0L;
        var profileDecisionEpoch = GetCaptureFreshnessMetricsSnapshot(currentCaptureSource).CurrentStreamEpoch;

        lock (gate)
        {
            previousMode = senderFreshnessMode;
            previousHint = captureFpsHint;
            previousTransportTuningLevel = transportTuningLevel;
            modeChanged = previousMode != nextMode;
            degradedStateChanged = IsSenderFreshnessDegraded(previousMode) != IsSenderFreshnessDegraded(nextMode);
            profileChanged = previousTransportTuningLevel != nextTransportTuningLevel;
            if (profileChanged &&
                nextMode != ScreenShareSenderFreshnessMode.CatchUp &&
                TryGetProfileTransitionDeferralReason_NoLock(profileDecisionEpoch, clock.UtcNow, out profileTransitionDeferralReason))
            {
                recoveryBurstProfileTransitionDeferredCount++;
                deferProfileTransition = true;
            }
            else
            {
                senderFreshnessMode = nextMode;
                captureFpsHint = nextHint;
                transportTuningLevel = nextTransportTuningLevel;
                if (modeChanged && nextMode == ScreenShareSenderFreshnessMode.Reduced)
                {
                    CaptureHelperStableVisibleHeadPromotionEntry_NoLock(GetCaptureFreshnessMetricsSnapshot(currentCaptureSource).CurrentStreamEpoch);
                }
                else if (modeChanged && nextMode != ScreenShareSenderFreshnessMode.Reduced)
                {
                    helperReducedModeEntryStableVisibleHeadFrameId = -1;
                    helperReducedModeEntryStreamEpoch = 0;
                }
            }
        }

        if (deferProfileTransition)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_transport_profile_transition_deferred; session_id={currentSessionId}; from_profile={FormatTransportTuningLevel(previousTransportTuningLevel)}; to_profile={FormatTransportTuningLevel(nextTransportTuningLevel)}; reason={reason}; defer_reason={(string.IsNullOrWhiteSpace(profileTransitionDeferralReason) ? "(none)" : profileTransitionDeferralReason)}; burst_phase={GetCurrentRecoveryBurstPhaseForLog()}");
            return;
        }

        if (currentPipeline is not null)
        {
            currentPipeline.SetMaxFramesPerSecond(nextHint);
            if (profileChanged)
            {
                droppedQueuedFrames = currentPipeline.FlushPendingFrames();
                currentPipeline.ResetPacingWindow();
                if (droppedQueuedFrames > 0)
                {
                    LocalOperationalLog.Info(
                        "ScreenShareTransport",
                        $"event=screenshare_sender_frame_dropped_backlog; session_id={currentSessionId}; dropped_count={droppedQueuedFrames}; reason=profile_transition");
                }
            }
            else if (modeChanged && nextMode == ScreenShareSenderFreshnessMode.CatchUp)
            {
                droppedQueuedFrames = currentPipeline.FlushPendingFrames();
                if (droppedQueuedFrames > 0)
                {
                    LocalOperationalLog.Info(
                        "ScreenShareTransport",
                        $"event=screenshare_sender_frame_dropped_backlog; session_id={currentSessionId}; dropped_count={droppedQueuedFrames}; reason={reason}");
                }
            }
            else if (modeChanged && previousMode == ScreenShareSenderFreshnessMode.CatchUp)
            {
                currentPipeline.ResetPacingWindow();
            }
        }

        if (profileChanged || (modeChanged && nextMode == ScreenShareSenderFreshnessMode.CatchUp))
        {
            droppedPendingRawFrames = PurgeSenderRawBacklog(currentCaptureSource);
            if (droppedPendingRawFrames > 0)
            {
                LocalOperationalLog.Info(
                    "ScreenShareTransport",
                    $"event=screenshare_sender_raw_backlog_purged; session_id={currentSessionId}; dropped_count={droppedPendingRawFrames}; reason={(profileChanged ? "profile_transition" : reason)}");
            }
        }

        if (currentCaptureSource is IScreenCaptureAdaptiveTuning tunableCaptureSource)
        {
            tunableCaptureSource.SetCaptureFrameRateHint(nextHint);
            tunableCaptureSource.SetTransportTuningLevel(nextTransportTuningLevel);
            if (profileChanged)
            {
                transitionStreamEpoch = GetCaptureFreshnessMetricsSnapshot(currentCaptureSource).CurrentStreamEpoch;
            }
        }

        Volatile.Write(ref preferFreshestPendingFrameOnly, IsSenderFreshnessDegraded(nextMode) ? 1 : 0);

        if (modeChanged ||
            previousHint != nextHint ||
            previousTransportTuningLevel != nextTransportTuningLevel)
        {
            LocalOperationalLog.Info(
                "ScreenShareTransport",
                $"event=screenshare_sender_mode_changed; session_id={currentSessionId}; from={FormatSenderFreshnessMode(previousMode)}; to={FormatSenderFreshnessMode(nextMode)}; reason={reason}; fps_target={nextHint}; transport_tuning_level={FormatTransportTuningLevel(nextTransportTuningLevel)}");
        }

        if (degradedStateChanged)
        {
            SenderDegradedModeChanged?.Invoke(
                this,
                new ScreenShareSenderDegradedModeChangedEventArgs(IsSenderFreshnessDegraded(nextMode)));
        }

        if (!modeChanged)
        {
            return;
        }

        if (nextMode == ScreenShareSenderFreshnessMode.CatchUp &&
            string.Equals(reason, "remote_high_frame_age_escalation", StringComparison.Ordinal))
        {
            lock (gate)
            {
                if (senderCatchUpEnteredDueToRemoteHighFrameAgeCount < long.MaxValue)
                {
                    senderCatchUpEnteredDueToRemoteHighFrameAgeCount++;
                }
            }
        }

        if (profileChanged)
        {
            StartTransportProfileTransition(
                currentSessionId,
                previousTransportTuningLevel,
                nextTransportTuningLevel,
                transitionStreamEpoch);
            RequestKeyFrame("sender_profile_transition");
            return;
        }

        if (nextMode == ScreenShareSenderFreshnessMode.CatchUp &&
            !string.Equals(reason, "remote_pressure", StringComparison.Ordinal) &&
            !string.Equals(reason, "remote_high_frame_age_escalation", StringComparison.Ordinal) &&
            !string.Equals(reason, "transport_stale_video_purge", StringComparison.Ordinal))
        {
            RequestKeyFrame("catch_up_mode_enter");
            return;
        }
    }
}
