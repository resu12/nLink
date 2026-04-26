using System;
using System.Collections.Generic;
using System.Globalization;
using NLink.App.Configuration;
using NLink.Core.Logging;
using NLink.Core.ScreenShare;
using NLink.Infra.Nkn;

namespace NLink.App.Services.ScreenCapture;

internal sealed partial class TransportScreenShareCoordinator
{
    private void MaybeLogFreshnessSummary(
        string currentSessionId,
        ScreenShareMetrics metrics,
        ScreenCaptureFreshnessMetrics sourceFreshnessMetrics,
        int laneQueueDepth,
        int laneQueuedBytes,
        long laneOldestQueuedAgeMs,
        long laneRecentDrops,
        string bridgeHealthKind,
        long recentHealthIssueCount,
        int promotionCaptureToSendBudgetMs,
        int promotionEncodeBudgetMs,
        int demotionEncodePressureMs)
    {
        var nowUtc = clock.UtcNow;
        MaybeCompleteTransportProfileTransition(currentSessionId, nowUtc);
        ScreenShareRemotePressureMode currentRemotePressureMode;
        long currentRemoteObservedFrameAgeMs;
        ScreenShareSenderFreshnessMode currentSenderMode;
        bool currentTransitionGraceActive;
        long currentTransitionStreamEpoch;
        int currentTransitionRemoteApplyCount;
        bool currentHelperEpochWarmupActive;
        int currentHelperEpochApplyCount;
        long currentHelperEpochNeedMoreInputCount;
        int currentHelperEpochHealthySignalCount;
        long currentHelperEpochStaleDrops;
        bool currentHelperSteadyVisibleProgressActive;
        long currentHelperStableVisibleHeadFrameId;
        long currentHelperFramesAppliedSinceLastGap;
        bool currentRemoteHelperFactHealthyActive;
        string currentRemoteHelperFactHealthySource;
        long currentRemoteHelperFactProofFrameId;
        long currentRemoteHelperFactLastMessageAgeMs;
        long currentRemoteHelperFactHealthyClearCount;
        string currentRemoteHelperFactHealthyClearReason;
        bool currentRecoveryLockActive;
        long currentRecoveryLockStreamEpoch;
        long currentRecoveryLockDurationMs;
        int currentRecoveryTimeoutResetCount;
        bool currentRecoveryBurstActive;
        RecoveryBurstPhase currentRecoveryBurstPhase;
        long currentRecoveryBurstStreamEpoch;
        long currentRecoveryOwnerFrameId;
        int currentRecoveryProtectedFollowerCount;
        long currentRecoveryGapCount;
        long currentRecoveryGapToKeyframeRequestMs;
        long currentRecoveryKeyframeRequestToOwnerEmitMs;
        long currentRecoveryOwnerAckWindowMs;
        long currentRecoveryOwnerEmitToFirstVisibleApplyMs;
        long currentRecoveryOwnerEmitToAckMs;
        long currentRecoveryOwnerAckFrameId;
        string currentRecoveryAckSource;
        long currentRecoveryBurstControlFallbackCount;
        long currentRecoveryBurstTimeoutCount;
        long currentRecoveryBurstCompletedCount;
        long currentRecoveryBurstRestartSuppressedCount;
        long currentRecoveryBurstEncoderRerequestCount;
        long currentRecoveryOwnerPendingForcedResetCount;
        long currentRecoveryKeyframeEmittedAfterForcedResetCount;
        long currentRecoveryBurstCompletedByHelperAckCount;
        long currentRecoveryBurstCompletedByHelperVisibleReceiptCount;
        long currentRecoveryBurstCompletedByAppliedHeadAckCount;
        long currentRecoveryBurstCompletedByLastVisibleApplyAckCount;
        long currentRecoveryBurstCompletedByVisibleRecoveryFloorCount;
        long currentRecoveryBurstCompletedByVisibleApplyFallbackCount;
        long currentRecoveryBurstCompletedByTimeoutCount;
        long currentRecoveryBurstCompletedByProtectedFramesCount;
        long currentRecoveryBurstProfileTransitionDeferredCount;
        long currentRecoveryBurstProfileTransitionTakeoverCount;
        long currentRecoveryBurstStaleRequestSuppressedCount;
        long currentRecoveryBurstRequestSuppressedDueToHelperAckCount;
        long currentRecoveryBurstStartedWhileHelperProofHealthyCount;
        long currentHelperProgressPastOwnerWithoutBurstAckCount;
        bool currentPostRecoveryAgeGraceActive;
        long currentPostRecoveryAgeGraceSuppressedCount;
        long currentLastAcknowledgedRecoveryOwnerFrameId;
        long currentHelperVisibleHeadFrameId;
        long currentHelperVisibleRecoveryFloorFrameId;
        long currentHelperCurrentEpochRecoveryKeyframeApplyCount;
        long currentLastAcknowledgedHelperHeadFrameId;
        long currentLastAcknowledgedVisibleHelperHeadFrameId;
        long currentLastAcknowledgedHelperProofAgeMs;
        long currentSatisfiedRecoveryFloorFrameId;
        string currentSatisfiedRecoveryFloorSource;
        long currentSatisfiedRecoveryFloorVisibleProofCount;
        long currentPersistedReleaseFloorEpoch;
        long currentContinuitySignalIgnoredDueToSatisfiedFloorCount;
        long currentContinuitySignalIgnoredDueToVisibleSatisfiedFloorCount;
        long currentRecoveryLockClearedByAcknowledgedProofCount;
        long currentRecoveryLockClearedByVisibleProofCount;
        string currentRecoveryLockLastClearReason;
        long currentLastRemoteRecoveryReceiptStreamEpoch;
        long currentLastRemoteRecoveryReceiptOwnerFrameId;
        long currentLastRemoteRecoveryReceiptVisibleRecoveryFrameId;
        long currentLastRemoteRecoveryReceiptVisibleHeadFrameId;
        string currentLastRemoteRecoveryReceiptKind;
        long currentRemoteRecoveryReceiptRejectedCount;
        string currentLastRemoteRecoveryReceiptRejectReason;
        long currentLastRemoteRecoveryReceiptRejectActiveStreamEpoch;
        long currentLastRemoteRecoveryReceiptRejectActiveOwnerFrameId;
        string currentLastRemoteRecoveryReceiptRejectActivePhase;
        long currentRecoveryEpochTakeoverSuppressedAfterOwnerEmitCount;
        long currentLastRecoveryEpochTakeoverSuppressedFromEpoch;
        long currentLastRecoveryEpochTakeoverSuppressedToEpoch;
        string currentLastRecoveryEpochTakeoverSuppressedPhase;
        long currentLastCompletedRecoveryEpoch;
        long currentLastCompletedRecoveryOwnerFrameId;
        long currentLastCompletedRecoveryAckFrameId;
        string currentLastCompletedRecoveryAckSource;
        long currentLastCompletedRecoveryOwnerEmitToAckMs;
        string currentLastCompletedRecoveryCompletionKind;
        long currentRecoveryCompletionAccountingMismatch;
        int currentRemoteHighFrameAgeCatchUpEntryConsecutiveTicks;
        long currentSenderCatchUpEnteredDueToRemoteHighFrameAgeCount;
        long currentRemoteHighFrameAgeCatchUpSuppressedDueToBootstrapGraceCount;
        long currentRemoteHighFrameAgeCatchUpSuppressedDueToPostAckGraceCount;
        long currentRemoteHighFrameAgeCatchUpSuppressedDueToCurrentEpochRecoveryBurstCount;
        long currentRemoteHighFrameAgeCatchUpSuppressedDueToMissingHelperEvidenceCount;
        long currentRemoteHighFrameAgeCatchUpSuppressedDueToUnderThresholdCount;
        string currentLastRemoteHighFrameAgeCatchUpSuppressionReason;
        long currentCatchUpRecoverySuppressedDueToRemoteHighFrameAgeCount;
        long currentCatchUpExitWhileRemoteHighFrameAgePressureCount;
        long currentRecoveryLockAllowedSameTuningModeChangeCount;
        string currentLastRecoveryLockAllowedSameTuningModeChange;
        long currentPostAckModeGraceSuppressedHighFrameAgeCount;
        long currentBootstrapGraceSuppressedCatchUpCount;
        long currentRecoveryOwnerPendingNonKeyHeldCount;
        long currentRecoveryOwnerPendingNonKeyReplacedCount;
        long currentRecoveryOwnerUnackedNonKeyHeldCount;
        long currentRecoveryOwnerUnackedNonKeyReplacedCount;
        long currentRecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount;
        long currentRecoveryOwnerReplacedBeforeAckCount;
        long currentHighFrameAgeSuppressedDuringOwnerAckCount;
        long currentSenderReceivedHelperProgressDuringContinuityLossCount;
        long currentHelperAckAfterFactSendMs;
        bool currentRecoveryPostAckHoldActive;
        long currentRecoveryPostAckHoldStartedCount;
        long currentRecoveryPostAckHoldExpiredCount;
        long currentRecoveryPostAckHoldSuppressedReopenCount;
        string currentCursorDeliveryMode;
        string currentCursorOverlayStatus;
        long currentCursorOverlayUpdatesSentCount;
        long currentCursorOverlaySendFailureCount;
        long currentCursorOverlayMappingFailureCount;
        lock (gate)
        {
            if (lastFreshnessSummaryUtc != default &&
                nowUtc - lastFreshnessSummaryUtc < TimeSpan.FromSeconds(2))
            {
                return;
            }

            lastFreshnessSummaryUtc = nowUtc;
            RefreshRemoteHelperFactHealthyState_NoLock(
                sourceFreshnessMetrics.CurrentStreamEpoch,
                continuityRecoverySignal: false,
                inboundSteadyVisibleProgressActive: false,
                inboundFramesAppliedSinceLastGap: -1,
                nowUtc);
            currentRemotePressureMode = remotePressureMode;
            currentRemoteObservedFrameAgeMs = remotePressureObservedFrameAgeMs;
            currentSenderMode = senderFreshnessMode;
            currentTransitionGraceActive = IsTransportProfileTransitionGraceActive_NoLock(nowUtc);
            currentTransitionStreamEpoch = transitionStreamEpoch > 0
                ? transitionStreamEpoch
                : sourceFreshnessMetrics.CurrentStreamEpoch;
            currentTransitionRemoteApplyCount = transitionRemoteApplyCount;
            currentHelperEpochWarmupActive = helperCurrentEpochWarmupActive;
            currentHelperEpochApplyCount = helperCurrentEpochApplyCount;
            currentHelperEpochNeedMoreInputCount = helperCurrentEpochNeedMoreInputCount;
            currentHelperEpochHealthySignalCount = helperCurrentEpochHealthySignalCount;
            currentHelperEpochStaleDrops = helperCurrentEpochStaleDrops;
            currentHelperSteadyVisibleProgressActive = helperSteadyVisibleProgressActive;
            currentHelperVisibleHeadFrameId = helperVisibleHeadFrameId;
            currentHelperStableVisibleHeadFrameId = helperStableVisibleHeadFrameId;
            currentHelperFramesAppliedSinceLastGap = helperFramesAppliedSinceLastGap;
            currentRemoteHelperFactHealthyActive = remoteHelperFactHealthyActive;
            currentRemoteHelperFactHealthySource = remoteHelperFactHealthySource;
            currentRemoteHelperFactProofFrameId = remoteHelperFactProofFrameId;
            currentRemoteHelperFactLastMessageAgeMs =
                lastHelperProgressFactReceivedUtc == default
                    ? -1
                    : Math.Max(0, (long)(nowUtc - lastHelperProgressFactReceivedUtc).TotalMilliseconds);
            currentRemoteHelperFactHealthyClearCount = remoteHelperFactHealthyClearCount;
            currentRemoteHelperFactHealthyClearReason = remoteHelperFactHealthyClearReason;
            currentRecoveryLockActive = recoveryLockActive;
            currentRecoveryLockStreamEpoch = recoveryLockStreamEpoch;
            currentRecoveryLockDurationMs = GetRecoveryLockDurationMs_NoLock(nowUtc);
            currentRecoveryTimeoutResetCount = recoveryTimeoutResetCount;
            currentRecoveryBurstActive = activeRecoveryBurst is not null;
            currentRecoveryBurstPhase = activeRecoveryBurst?.Phase ?? RecoveryBurstPhase.Idle;
            currentRecoveryBurstStreamEpoch = activeRecoveryBurst?.StreamEpoch ?? 0;
            currentRecoveryOwnerFrameId = activeRecoveryBurst?.OwnerFrameId ?? -1;
            currentRecoveryProtectedFollowerCount = recoveryProtectedFollowerCount;
            currentRecoveryGapCount = recoveryGapCount;
            currentRecoveryGapToKeyframeRequestMs = recoveryGapToKeyframeRequestMs;
            currentRecoveryKeyframeRequestToOwnerEmitMs = recoveryKeyframeRequestToOwnerEmitMs;
            currentRecoveryOwnerAckWindowMs = recoveryOwnerAckWindowMs;
            currentRecoveryOwnerEmitToFirstVisibleApplyMs = recoveryOwnerEmitToFirstVisibleApplyMs;
            currentRecoveryOwnerEmitToAckMs = recoveryOwnerEmitToAckMs;
            currentRecoveryOwnerAckFrameId = recoveryOwnerAckFrameId;
            currentRecoveryAckSource = recoveryAckSource;
            currentRecoveryBurstControlFallbackCount = recoveryBurstControlFallbackCount;
            currentRecoveryBurstTimeoutCount = recoveryBurstTimeoutCount;
            currentRecoveryBurstCompletedCount = recoveryBurstCompletedCount;
            currentRecoveryBurstRestartSuppressedCount = recoveryBurstRestartSuppressedCount;
            currentRecoveryBurstEncoderRerequestCount = recoveryBurstEncoderRerequestCount;
            currentRecoveryOwnerPendingForcedResetCount = recoveryOwnerPendingForcedResetCount;
            currentRecoveryKeyframeEmittedAfterForcedResetCount = recoveryKeyframeEmittedAfterForcedResetCount;
            currentRecoveryBurstCompletedByHelperAckCount = recoveryBurstCompletedByHelperAckCount;
            currentRecoveryBurstCompletedByHelperVisibleReceiptCount = recoveryBurstCompletedByHelperVisibleReceiptCount;
            currentRecoveryBurstCompletedByAppliedHeadAckCount = recoveryBurstCompletedByAppliedHeadAckCount;
            currentRecoveryBurstCompletedByLastVisibleApplyAckCount = recoveryBurstCompletedByLastVisibleApplyAckCount;
            currentRecoveryBurstCompletedByVisibleRecoveryFloorCount = recoveryBurstCompletedByVisibleRecoveryFloorCount;
            currentRecoveryBurstCompletedByVisibleApplyFallbackCount = recoveryBurstCompletedByVisibleApplyFallbackCount;
            currentRecoveryBurstCompletedByTimeoutCount = recoveryBurstCompletedByTimeoutCount;
            currentRecoveryBurstCompletedByProtectedFramesCount = recoveryBurstCompletedByProtectedFramesCount;
            currentRecoveryBurstProfileTransitionDeferredCount = recoveryBurstProfileTransitionDeferredCount;
            currentRecoveryBurstProfileTransitionTakeoverCount = recoveryBurstProfileTransitionTakeoverCount;
            currentRecoveryBurstStaleRequestSuppressedCount = recoveryBurstStaleRequestSuppressedCount;
            currentRecoveryBurstRequestSuppressedDueToHelperAckCount = recoveryBurstRequestSuppressedDueToHelperAckCount;
            currentRecoveryBurstStartedWhileHelperProofHealthyCount = recoveryBurstStartedWhileHelperProofHealthyCount;
            currentHelperProgressPastOwnerWithoutBurstAckCount = helperProgressPastOwnerWithoutBurstAckCount;
            currentPostRecoveryAgeGraceActive = IsPostRecoveryAgeGraceActive_NoLock(sourceFreshnessMetrics.CurrentStreamEpoch, nowUtc);
            currentPostRecoveryAgeGraceSuppressedCount = postRecoveryAgeGraceSuppressedCount;
            currentLastAcknowledgedRecoveryOwnerFrameId = lastCompletedRecovery?.OwnerFrameId ?? -1;
            currentLastAcknowledgedHelperHeadFrameId = acknowledgedHelperHeadFrameId;
            currentLastAcknowledgedVisibleHelperHeadFrameId = acknowledgedVisibleHelperHeadFrameId;
            currentHelperVisibleRecoveryFloorFrameId = helperVisibleRecoveryFloorFrameId;
            currentHelperCurrentEpochRecoveryKeyframeApplyCount = helperCurrentEpochRecoveryKeyframeApplyCount;
            currentLastAcknowledgedHelperProofAgeMs =
                acknowledgedVisibleHelperProofUtc == default
                    ? -1
                    : Math.Max(0, (long)(nowUtc - acknowledgedVisibleHelperProofUtc).TotalMilliseconds);
            currentPersistedReleaseFloorEpoch = satisfiedRecoveryFloorEpoch;
            currentSatisfiedRecoveryFloorFrameId = satisfiedRecoveryFloorEpoch == sourceFreshnessMetrics.CurrentStreamEpoch
                ? satisfiedRecoveryFloorFrameId
                : -1;
            currentSatisfiedRecoveryFloorSource =
                satisfiedRecoveryFloorEpoch == sourceFreshnessMetrics.CurrentStreamEpoch &&
                !string.IsNullOrWhiteSpace(satisfiedRecoveryFloorSource)
                    ? satisfiedRecoveryFloorSource
                    : string.Empty;
            currentSatisfiedRecoveryFloorVisibleProofCount = satisfiedRecoveryFloorVisibleProofCount;
            currentContinuitySignalIgnoredDueToSatisfiedFloorCount = continuitySignalIgnoredDueToSatisfiedFloorCount;
            currentContinuitySignalIgnoredDueToVisibleSatisfiedFloorCount = continuitySignalIgnoredDueToVisibleSatisfiedFloorCount;
            currentRecoveryLockClearedByAcknowledgedProofCount = recoveryLockClearedByAcknowledgedProofCount;
            currentRecoveryLockClearedByVisibleProofCount = recoveryLockClearedByVisibleProofCount;
            currentRecoveryLockLastClearReason = recoveryLockLastClearReason;
            currentLastRemoteRecoveryReceiptStreamEpoch = lastRemoteRecoveryReceiptStreamEpoch;
            currentLastRemoteRecoveryReceiptOwnerFrameId = lastRemoteRecoveryReceiptOwnerFrameId;
            currentLastRemoteRecoveryReceiptVisibleRecoveryFrameId = lastRemoteRecoveryReceiptVisibleRecoveryFrameId;
            currentLastRemoteRecoveryReceiptVisibleHeadFrameId = lastRemoteRecoveryReceiptVisibleHeadFrameId;
            currentLastRemoteRecoveryReceiptKind = lastRemoteRecoveryReceiptKind;
            currentRemoteRecoveryReceiptRejectedCount = remoteRecoveryReceiptRejectedCount;
            currentLastRemoteRecoveryReceiptRejectReason = lastRemoteRecoveryReceiptRejectReason;
            currentLastRemoteRecoveryReceiptRejectActiveStreamEpoch = lastRemoteRecoveryReceiptRejectActiveStreamEpoch;
            currentLastRemoteRecoveryReceiptRejectActiveOwnerFrameId = lastRemoteRecoveryReceiptRejectActiveOwnerFrameId;
            currentLastRemoteRecoveryReceiptRejectActivePhase = lastRemoteRecoveryReceiptRejectActivePhase;
            currentRecoveryEpochTakeoverSuppressedAfterOwnerEmitCount = recoveryEpochTakeoverSuppressedAfterOwnerEmitCount;
            currentLastRecoveryEpochTakeoverSuppressedFromEpoch = lastRecoveryEpochTakeoverSuppressedFromEpoch;
            currentLastRecoveryEpochTakeoverSuppressedToEpoch = lastRecoveryEpochTakeoverSuppressedToEpoch;
            currentLastRecoveryEpochTakeoverSuppressedPhase = lastRecoveryEpochTakeoverSuppressedPhase;
            currentLastCompletedRecoveryEpoch = lastCompletedRecovery?.StreamEpoch ?? 0;
            currentLastCompletedRecoveryOwnerFrameId = lastCompletedRecovery?.OwnerFrameId ?? -1;
            currentLastCompletedRecoveryAckFrameId = lastCompletedRecovery?.AckFrameId ?? -1;
            currentLastCompletedRecoveryAckSource = lastCompletedRecovery?.AckSource ?? string.Empty;
            currentLastCompletedRecoveryOwnerEmitToAckMs = lastCompletedRecovery?.OwnerEmitToAckMs ?? -1;
            currentLastCompletedRecoveryCompletionKind = lastCompletedRecovery?.CompletionKind ?? string.Empty;
            currentRecoveryCompletionAccountingMismatch = ComputeRecoveryCompletionAccountingMismatch(lastCompletedRecovery);
            currentRemoteHighFrameAgeCatchUpEntryConsecutiveTicks = remoteHighFrameAgeCatchUpEntryConsecutiveTicks;
            currentSenderCatchUpEnteredDueToRemoteHighFrameAgeCount = senderCatchUpEnteredDueToRemoteHighFrameAgeCount;
            currentRemoteHighFrameAgeCatchUpSuppressedDueToBootstrapGraceCount = remoteHighFrameAgeCatchUpSuppressedDueToBootstrapGraceCount;
            currentRemoteHighFrameAgeCatchUpSuppressedDueToPostAckGraceCount = remoteHighFrameAgeCatchUpSuppressedDueToPostAckGraceCount;
            currentRemoteHighFrameAgeCatchUpSuppressedDueToCurrentEpochRecoveryBurstCount = remoteHighFrameAgeCatchUpSuppressedDueToCurrentEpochRecoveryBurstCount;
            currentRemoteHighFrameAgeCatchUpSuppressedDueToMissingHelperEvidenceCount = remoteHighFrameAgeCatchUpSuppressedDueToMissingHelperEvidenceCount;
            currentRemoteHighFrameAgeCatchUpSuppressedDueToUnderThresholdCount = remoteHighFrameAgeCatchUpSuppressedDueToUnderThresholdCount;
            currentLastRemoteHighFrameAgeCatchUpSuppressionReason = lastRemoteHighFrameAgeCatchUpSuppressionReason;
            currentCatchUpRecoverySuppressedDueToRemoteHighFrameAgeCount = catchUpRecoverySuppressedDueToRemoteHighFrameAgeCount;
            currentCatchUpExitWhileRemoteHighFrameAgePressureCount = catchUpExitWhileRemoteHighFrameAgePressureCount;
            currentRecoveryLockAllowedSameTuningModeChangeCount = recoveryLockAllowedSameTuningModeChangeCount;
            currentLastRecoveryLockAllowedSameTuningModeChange = lastRecoveryLockAllowedSameTuningModeChange;
            currentPostAckModeGraceSuppressedHighFrameAgeCount = postAckModeGraceSuppressedHighFrameAgeCount;
            currentBootstrapGraceSuppressedCatchUpCount = bootstrapGraceSuppressedCatchUpCount;

            currentRecoveryOwnerPendingNonKeyHeldCount = recoveryOwnerPendingNonKeyHeldCount;
            currentRecoveryOwnerPendingNonKeyReplacedCount = recoveryOwnerPendingNonKeyReplacedCount;
            currentRecoveryOwnerUnackedNonKeyHeldCount = recoveryOwnerUnackedNonKeyHeldCount;
            currentRecoveryOwnerUnackedNonKeyReplacedCount = recoveryOwnerUnackedNonKeyReplacedCount;
            currentRecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount = recoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount;
            currentRecoveryOwnerReplacedBeforeAckCount = recoveryOwnerReplacedBeforeAckCount;
            currentHighFrameAgeSuppressedDuringOwnerAckCount = highFrameAgeSuppressedDuringOwnerAckCount;
            currentSenderReceivedHelperProgressDuringContinuityLossCount = senderReceivedHelperProgressDuringContinuityLossCount;
            currentHelperAckAfterFactSendMs = helperAckAfterFactSendMs;
            currentRecoveryPostAckHoldActive = activeRecoveryBurst?.Phase == RecoveryBurstPhase.PostAckHold;
            currentRecoveryPostAckHoldStartedCount = recoveryPostAckHoldStartedCount;
            currentRecoveryPostAckHoldExpiredCount = recoveryPostAckHoldExpiredCount;
            currentRecoveryPostAckHoldSuppressedReopenCount = recoveryPostAckHoldSuppressedReopenCount;
            currentCursorDeliveryMode = cursorOverlayDeliveryMode;
            currentCursorOverlayStatus = cursorOverlayLastStatus;
            currentCursorOverlayUpdatesSentCount = cursorOverlayUpdatesSentCount;
            currentCursorOverlaySendFailureCount = cursorOverlaySendFailureCount;
            currentCursorOverlayMappingFailureCount = cursorOverlayMappingFailureCount;
        }

        var nknSnapshot = NknRuntimeDiagnostics.Snapshot();
        var qualityState = ScreenShareQualitySettings.GetCurrentEnvironmentState();
        var freshnessSummaryFields = new[]
        {
            "event=screenshare_freshness_summary",
            $"session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}",
            $"remote_pressure_mode={FormatRemotePressureMode(currentRemotePressureMode)}",
            $"capture_to_send_age_ms={metrics.LastCaptureToSendAgeMs}",
            $"frames_queued={metrics.FramesQueued}",
            $"transport_queue_depth={laneQueueDepth}",
            $"transport_queued_bytes={laneQueuedBytes}",
            $"transport_oldest_queued_age_ms={laneOldestQueuedAgeMs}",
            $"recent_stale_drops={laneRecentDrops}",
            $"remote_observed_frame_age_ms={currentRemoteObservedFrameAgeMs}",
            $"sender_continuity_recovery_active={(sourceFreshnessMetrics.SenderContinuityRecoveryActive ? 1 : 0)}",
            $"sender_continuity_loss_count={sourceFreshnessMetrics.SenderContinuityLossCount}",
            $"sender_last_continuity_loss_reason={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.LastSenderContinuityLossReason) ? "(none)" : sourceFreshnessMetrics.LastSenderContinuityLossReason)}",
            $"source_stream_epoch={sourceFreshnessMetrics.CurrentStreamEpoch}",
            $"stream_epoch={sourceFreshnessMetrics.CurrentStreamEpoch}",
            $"source_pending_raw_frames={sourceFreshnessMetrics.PendingRawFrameCount}",
            $"source_oldest_pending_age_ms={sourceFreshnessMetrics.OldestPendingRawFrameAgeMs}",
            $"sender_process_cpu_percent={sourceFreshnessMetrics.SenderProcessCpuPercent.ToString("F1", CultureInfo.InvariantCulture)}",
            $"capture_to_encode_start_age_ms={sourceFreshnessMetrics.LastCaptureToEncodeStartAgeMs}",
            $"last_encode_duration_ms={sourceFreshnessMetrics.LastEncodeDurationMs}",
            $"actual_encoded_displayable_fps={sourceFreshnessMetrics.ActualEncodedDisplayableFps.ToString("F2", CultureInfo.InvariantCulture)}",
            $"encode_cadence_target_fps={sourceFreshnessMetrics.EncodeCadenceTargetFps}",
            $"emitted_displayable_frames={sourceFreshnessMetrics.EmittedDisplayableFrames}",
            $"emitted_non_displayable_units={sourceFreshnessMetrics.EmittedNonDisplayableUnits}",
            $"emitted_idr_frames={sourceFreshnessMetrics.IdrFramesEmitted}",
            $"emitted_p_frames={sourceFreshnessMetrics.PFramesEmitted}",
            $"dropped_b_frames={sourceFreshnessMetrics.DroppedBFrames}",
            $"dropped_multi_picture_units={sourceFreshnessMetrics.DroppedMultiPictureUnits}",
            $"displayable_frame_ratio={sourceFreshnessMetrics.DisplayableFrameRatio.ToString("F2", CultureInfo.InvariantCulture)}",
            $"idr_frame_ratio={sourceFreshnessMetrics.IdrFrameRatio.ToString("F2", CultureInfo.InvariantCulture)}",
            $"motion_integrity_guard_active={(sourceFreshnessMetrics.MotionIntegrityGuardActive ? 1 : 0)}",
            $"motion_integrity_sampled_ratio={sourceFreshnessMetrics.MotionIntegritySampledRatio.ToString("F3", CultureInfo.InvariantCulture)}",
            $"motion_integrity_peak_sampled_ratio={sourceFreshnessMetrics.MotionIntegrityPeakSampledRatio.ToString("F3", CultureInfo.InvariantCulture)}",
            $"motion_integrity_scroll_active_band_count={sourceFreshnessMetrics.MotionIntegrityScrollMotionActiveBandCount}",
            $"motion_integrity_scroll_peak_band_ratio={sourceFreshnessMetrics.MotionIntegrityScrollMotionPeakBandRatio.ToString("F3", CultureInfo.InvariantCulture)}",
            $"motion_integrity_high_motion_frame_count={sourceFreshnessMetrics.MotionIntegrityHighMotionFrameCount}",
            $"motion_integrity_scroll_trigger_count={sourceFreshnessMetrics.MotionIntegrityScrollTriggerCount}",
            $"motion_integrity_burst_enter_count={sourceFreshnessMetrics.MotionIntegrityBurstEnterCount}",
            $"motion_integrity_burst_exit_count={sourceFreshnessMetrics.MotionIntegrityBurstExitCount}",
            $"motion_integrity_forced_keyframe_count={sourceFreshnessMetrics.MotionIntegrityForcedKeyFrameCount}",
            $"motion_integrity_last_trigger_kind={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.MotionIntegrityLastTriggerKind) ? "(none)" : sourceFreshnessMetrics.MotionIntegrityLastTriggerKind)}",
            $"motion_integrity_last_reason={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.MotionIntegrityLastReason) ? "(none)" : sourceFreshnessMetrics.MotionIntegrityLastReason)}",
            $"motion_integrity_idr_frame_ratio={sourceFreshnessMetrics.MotionIntegrityIdrFrameRatio.ToString("F2", CultureInfo.InvariantCulture)}",
            $"motion_integrity_forced_idr_requested_count={sourceFreshnessMetrics.MotionIntegrityForcedIdrRequestedCount}",
            $"motion_integrity_forced_idr_confirmed_count={sourceFreshnessMetrics.MotionIntegrityForcedIdrConfirmedCount}",
            $"motion_integrity_forced_idr_missed_count={sourceFreshnessMetrics.MotionIntegrityForcedIdrMissedCount}",
            $"motion_integrity_forced_idr_pending_count={sourceFreshnessMetrics.MotionIntegrityForcedIdrPendingCount}",
            $"motion_integrity_forced_idr_consecutive_miss_count={sourceFreshnessMetrics.MotionIntegrityForcedIdrConsecutiveMissCount}",
            $"motion_integrity_forced_idr_burst_miss_count={sourceFreshnessMetrics.MotionIntegrityForcedIdrBurstMissCount}",
            $"motion_integrity_active_idr_frame_ratio={sourceFreshnessMetrics.MotionIntegrityActiveIdrFrameRatio.ToString("F2", CultureInfo.InvariantCulture)}",
            $"motion_integrity_forced_idr_last_miss_reason={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.MotionIntegrityForcedIdrLastMissReason) ? "(none)" : sourceFreshnessMetrics.MotionIntegrityForcedIdrLastMissReason)}",
            $"motion_integrity_encoder_rebuild_count={sourceFreshnessMetrics.MotionIntegrityEncoderRebuildCount}",
            $"motion_integrity_encoder_rebuild_suppressed_count={sourceFreshnessMetrics.MotionIntegrityEncoderRebuildSuppressedCount}",
            $"motion_integrity_encoder_rebuild_pending={(sourceFreshnessMetrics.MotionIntegrityEncoderRebuildPending ? 1 : 0)}",
            $"motion_integrity_encoder_rebuild_last_reason={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.MotionIntegrityEncoderRebuildLastReason) ? "(none)" : sourceFreshnessMetrics.MotionIntegrityEncoderRebuildLastReason)}",
            $"cursor_capture_enabled={(sourceFreshnessMetrics.CursorCaptureEnabled ? 1 : 0)}",
            $"cursor_capture_desired_enabled={(sourceFreshnessMetrics.CursorCaptureDesiredEnabled ? 1 : 0)}",
            $"cursor_capture_control_supported={(sourceFreshnessMetrics.CursorCaptureControlSupported ? 1 : 0)}",
            $"cursor_capture_apply_status={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.CursorCaptureApplyStatus) ? "(none)" : sourceFreshnessMetrics.CursorCaptureApplyStatus)}",
            $"cursor_capture_fallback_reason={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.CursorCaptureFallbackReason) ? "(none)" : sourceFreshnessMetrics.CursorCaptureFallbackReason)}",
            $"cursor_delivery_mode={(string.IsNullOrWhiteSpace(currentCursorDeliveryMode) ? "captured_video" : currentCursorDeliveryMode)}",
            $"cursor_overlay_updates_sent_count={Math.Max(0, currentCursorOverlayUpdatesSentCount)}",
            $"cursor_overlay_send_failure_count={Math.Max(0, currentCursorOverlaySendFailureCount)}",
            $"cursor_overlay_mapping_failure_count={Math.Max(0, currentCursorOverlayMappingFailureCount)}",
            $"cursor_overlay_last_status={(string.IsNullOrWhiteSpace(currentCursorOverlayStatus) ? "(none)" : currentCursorOverlayStatus)}",
            $"avg_encoded_frame_bytes={sourceFreshnessMetrics.AverageEncodedFrameBytes.ToString("F1", CultureInfo.InvariantCulture)}",
            $"transport_ip_only_mode={(sourceFreshnessMetrics.TransportIpOnlyMode ? 1 : 0)}",
            $"last_access_unit_kind={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.LastAccessUnitKind) ? "(none)" : sourceFreshnessMetrics.LastAccessUnitKind)}",
            $"low_delay_config_applied={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.LowDelayConfigApplied) ? "(none)" : sourceFreshnessMetrics.LowDelayConfigApplied)}",
            $"last_preprocess_duration_ms={sourceFreshnessMetrics.LastPreprocessDurationMs}",
            $"last_preprocess_resize_duration_ms={sourceFreshnessMetrics.LastPreprocessResizeDurationMs}",
            $"last_preprocess_color_convert_duration_ms={sourceFreshnessMetrics.LastPreprocessColorConvertDurationMs}",
            $"preprocess_resize_path={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.PreprocessResizePath) ? "(none)" : sourceFreshnessMetrics.PreprocessResizePath)}",
            $"preprocess_direct_nv12_count={sourceFreshnessMetrics.PreprocessDirectNv12Count}",
            $"last_transform_encode_duration_ms={sourceFreshnessMetrics.LastTransformEncodeDurationMs}",
            $"last_encode_total_duration_ms={sourceFreshnessMetrics.LastEncodeTotalDurationMs}",
            $"promotion_capture_to_send_budget_ms={promotionCaptureToSendBudgetMs}",
            $"promotion_encode_budget_ms={promotionEncodeBudgetMs}",
            $"demotion_encode_pressure_ms={demotionEncodePressureMs}",
            $"encoder_path={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.EncoderPath) ? "(none)" : sourceFreshnessMetrics.EncoderPath)}",
            $"encoder_profile={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.EncoderProfile) ? "(none)" : sourceFreshnessMetrics.EncoderProfile)}",
            $"capture_scale={ScreenShareQualitySettings.FormatScale(qualityState.CaptureScale)}",
            $"effective_quality_preset={(string.IsNullOrWhiteSpace(qualityState.EffectivePresetKey) ? "(none)" : qualityState.EffectivePresetKey)}",
            $"legacy_preset_migrated={(qualityState.LegacyHigherClarityPresetMigrated ? 1 : 0)}",
            $"source_superseded_pending_frames={sourceFreshnessMetrics.SupersededPendingRawFrameCount}",
            $"raw_capture_event_count={sourceFreshnessMetrics.RawCaptureEventCount}",
            $"raw_frames_deferred_to_encode_slot={sourceFreshnessMetrics.RawFramesDeferredToEncodeSlot}",
            $"raw_frames_replaced_before_encode_slot={sourceFreshnessMetrics.RawFramesReplacedBeforeEncodeSlot}",
            $"raw_frames_skipped_before_encode={sourceFreshnessMetrics.RawFramesSkippedBeforeEncode}",
            $"raw_encode_slot_empty_count={sourceFreshnessMetrics.RawEncodeSlotEmptyCount}",
            $"raw_slot_coalescing_active={(sourceFreshnessMetrics.RawSlotCoalescingActive ? 1 : 0)}",
            $"raw_source_frame_arrived_count={sourceFreshnessMetrics.RawSourceFrameArrivedCount}",
            $"raw_source_frames_skipped_before_readback={sourceFreshnessMetrics.RawSourceFramesSkippedBeforeReadback}",
            $"raw_source_frames_readback_count={sourceFreshnessMetrics.RawSourceFramesReadbackCount}",
            $"raw_source_readback_fps={sourceFreshnessMetrics.RawSourceReadbackFps.ToString("F2", CultureInfo.InvariantCulture)}",
            $"raw_source_last_readback_duration_ms={sourceFreshnessMetrics.RawSourceLastReadbackDurationMs}",
            $"raw_source_avg_readback_duration_ms={sourceFreshnessMetrics.RawSourceAverageReadbackDurationMs.ToString("F1", CultureInfo.InvariantCulture)}",
            $"raw_source_cadence_target_fps={sourceFreshnessMetrics.RawSourceCadenceTargetFps}",
            $"raw_source_urgent_bypass_count={sourceFreshnessMetrics.RawSourceUrgentBypassCount}",
            $"raw_source_output_width={sourceFreshnessMetrics.RawSourceOutputWidth}",
            $"raw_source_output_height={sourceFreshnessMetrics.RawSourceOutputHeight}",
            $"raw_source_gpu_scale_enabled={(sourceFreshnessMetrics.RawSourceGpuScaleEnabled ? 1 : 0)}",
            $"raw_source_gpu_scale_fallback_reason={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.RawSourceGpuScaleFallbackReason) ? "(none)" : sourceFreshnessMetrics.RawSourceGpuScaleFallbackReason)}",
            $"raw_source_capture_active={(sourceFreshnessMetrics.RawSourceCaptureActive ? 1 : 0)}",
            $"wgc_border_required_control_supported={(sourceFreshnessMetrics.RawSourceBorderRequiredControlSupported ? 1 : 0)}",
            $"wgc_border_required_desired={(sourceFreshnessMetrics.RawSourceBorderRequiredDesired ? 1 : 0)}",
            $"wgc_border_required={(sourceFreshnessMetrics.RawSourceBorderRequired ? 1 : 0)}",
            $"wgc_border_required_apply_status={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.RawSourceBorderRequiredApplyStatus) ? "(none)" : sourceFreshnessMetrics.RawSourceBorderRequiredApplyStatus)}",
            $"wgc_border_required_fallback_reason={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.RawSourceBorderRequiredFallbackReason) ? "(none)" : sourceFreshnessMetrics.RawSourceBorderRequiredFallbackReason)}",
            $"wgc_last_stop_duration_ms={sourceFreshnessMetrics.RawSourceLastStopDurationMs}",
            $"wgc_last_stop_reason={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.RawSourceLastStopReason) ? "(none)" : sourceFreshnessMetrics.RawSourceLastStopReason)}",
            $"wgc_active_session_lease_count={sourceFreshnessMetrics.RawSourceActiveSessionLeaseCount}",
            $"wgc_last_session_close_status={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.RawSourceLastSessionCloseStatus) ? "(none)" : sourceFreshnessMetrics.RawSourceLastSessionCloseStatus)}",
            $"wgc_last_session_close_method={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.RawSourceLastSessionCloseMethod) ? "(none)" : sourceFreshnessMetrics.RawSourceLastSessionCloseMethod)}",
            $"wgc_last_session_close_hresult={(string.IsNullOrWhiteSpace(sourceFreshnessMetrics.RawSourceLastSessionCloseHResult) ? "(none)" : sourceFreshnessMetrics.RawSourceLastSessionCloseHResult)}",
            $"wgc_force_close_count={sourceFreshnessMetrics.RawSourceForceCloseCount}",
            $"wgc_session_close_anomaly_count={sourceFreshnessMetrics.RawSourceSessionCloseAnomalyCount}",
            $"wgc_session_owner_thread_id={sourceFreshnessMetrics.RawSourceSessionOwnerThreadId}",
            $"wgc_session_close_thread_id={sourceFreshnessMetrics.RawSourceLastSessionCloseThreadId}",
            $"wgc_close_on_owner_thread={(sourceFreshnessMetrics.RawSourceLastSessionCloseOnOwnerThread ? 1 : 0)}",
            $"wgc_owner_dispatcher_active={(sourceFreshnessMetrics.RawSourceOwnerDispatcherActive ? 1 : 0)}",
            $"wgc_owner_thread_close_timeout_count={sourceFreshnessMetrics.RawSourceOwnerThreadCloseTimeoutCount}",
            $"active_encode_target_width={sourceFreshnessMetrics.ActiveTargetWidth}",
            $"active_encode_target_height={sourceFreshnessMetrics.ActiveTargetHeight}",
            $"active_encode_target_bitrate={sourceFreshnessMetrics.ActiveTargetBitrate}",
            $"active_encode_target_fps={sourceFreshnessMetrics.ActiveTargetFramesPerSecond}",
            $"sender_freshness_mode={FormatSenderFreshnessMode(currentSenderMode)}",
            $"sender_operating_state={ScreenShareConceptualModelFormatter.FormatValue(metrics.SenderOperatingState, "normal")}",
            $"sender_guard_state={ScreenShareConceptualModelFormatter.FormatValue(metrics.SenderGuardState, "none")}",
            $"dominant_pressure_blocker={ScreenShareConceptualModelFormatter.FormatValue(metrics.DominantPressureBlocker, "none")}",
            $"transition_grace_active={(currentTransitionGraceActive ? 1 : 0)}",
            $"transition_stream_epoch={currentTransitionStreamEpoch}",
            $"current_epoch_remote_apply_count={Math.Max(0, currentTransitionRemoteApplyCount)}",
            $"current_epoch_need_more_input_count={Math.Max(0, currentHelperEpochNeedMoreInputCount)}",
            $"helper_current_epoch_warmup_active={(currentHelperEpochWarmupActive ? 1 : 0)}",
            $"helper_current_epoch_apply_count={Math.Max(0, currentHelperEpochApplyCount)}",
            $"helper_current_epoch_healthy_signal_count={Math.Max(0, currentHelperEpochHealthySignalCount)}",
            $"helper_current_epoch_stale_drops={Math.Max(0, currentHelperEpochStaleDrops)}",
            $"helper_steady_visible_progress_active={(currentHelperSteadyVisibleProgressActive ? 1 : 0)}",
            $"helper_stable_visible_head_frame_id={(currentHelperStableVisibleHeadFrameId >= 0 ? currentHelperStableVisibleHeadFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"helper_frames_applied_since_last_gap={Math.Max(0, currentHelperFramesAppliedSinceLastGap)}",
            $"remote_helper_fact_healthy_active={(currentRemoteHelperFactHealthyActive ? 1 : 0)}",
            $"remote_helper_fact_healthy_source={(string.IsNullOrWhiteSpace(currentRemoteHelperFactHealthySource) ? "(none)" : currentRemoteHelperFactHealthySource)}",
            $"remote_helper_fact_proof_frame_id={(currentRemoteHelperFactProofFrameId >= 0 ? currentRemoteHelperFactProofFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"remote_helper_fact_last_message_age_ms={(currentRemoteHelperFactLastMessageAgeMs >= 0 ? currentRemoteHelperFactLastMessageAgeMs.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"remote_helper_fact_healthy_clear_count={Math.Max(0, currentRemoteHelperFactHealthyClearCount)}",
            $"remote_helper_fact_healthy_clear_reason={(string.IsNullOrWhiteSpace(currentRemoteHelperFactHealthyClearReason) ? "(none)" : currentRemoteHelperFactHealthyClearReason)}",
            $"remote_helper_visible_head_frame_id={(currentHelperVisibleHeadFrameId >= 0 ? currentHelperVisibleHeadFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"remote_helper_visible_recovery_floor_frame_id={(currentHelperVisibleRecoveryFloorFrameId >= 0 ? currentHelperVisibleRecoveryFloorFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"remote_helper_current_epoch_recovery_keyframe_apply_count={Math.Max(0, currentHelperCurrentEpochRecoveryKeyframeApplyCount)}",
            $"recovery_lock_active={(currentRecoveryLockActive ? 1 : 0)}",
            $"recovery_lock_stream_epoch={currentRecoveryLockStreamEpoch}",
            $"recovery_lock_duration_ms={currentRecoveryLockDurationMs}",
            $"recovery_timeout_reset_count={currentRecoveryTimeoutResetCount}",
            $"recovery_burst_active={(currentRecoveryBurstActive ? 1 : 0)}",
            $"recovery_burst_phase={FormatRecoveryBurstPhase(currentRecoveryBurstPhase)}",
            $"recovery_burst_stream_epoch={currentRecoveryBurstStreamEpoch}",
            $"recovery_owner_frame_id={(currentRecoveryOwnerFrameId >= 0 ? currentRecoveryOwnerFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"recovery_protected_follower_count={Math.Max(0, currentRecoveryProtectedFollowerCount)}",
            $"recovery_gap_count={Math.Max(0, currentRecoveryGapCount)}",
            $"recovery_gap_to_keyframe_request_ms={(currentRecoveryGapToKeyframeRequestMs >= 0 ? currentRecoveryGapToKeyframeRequestMs.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"recovery_keyframe_request_to_owner_emit_ms={(currentRecoveryKeyframeRequestToOwnerEmitMs >= 0 ? currentRecoveryKeyframeRequestToOwnerEmitMs.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"recovery_owner_ack_window_ms={(currentRecoveryOwnerAckWindowMs >= 0 ? currentRecoveryOwnerAckWindowMs.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"recovery_owner_emit_to_ack_ms={(currentRecoveryOwnerEmitToAckMs >= 0 ? currentRecoveryOwnerEmitToAckMs.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"recovery_owner_emit_to_first_visible_apply_ms={(currentRecoveryOwnerEmitToFirstVisibleApplyMs >= 0 ? currentRecoveryOwnerEmitToFirstVisibleApplyMs.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"recovery_owner_ack_frame_id={(currentRecoveryOwnerAckFrameId >= 0 ? currentRecoveryOwnerAckFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"recovery_ack_source={(string.IsNullOrWhiteSpace(currentRecoveryAckSource) ? "(none)" : currentRecoveryAckSource)}",
            $"helper_ack_after_fact_send_ms={(currentHelperAckAfterFactSendMs >= 0 ? currentHelperAckAfterFactSendMs.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"recovery_post_ack_hold_active={(currentRecoveryPostAckHoldActive ? 1 : 0)}",
            $"recovery_post_ack_hold_started_count={Math.Max(0, currentRecoveryPostAckHoldStartedCount)}",
            $"recovery_post_ack_hold_expired_count={Math.Max(0, currentRecoveryPostAckHoldExpiredCount)}",
            $"recovery_post_ack_hold_suppressed_reopen_count={Math.Max(0, currentRecoveryPostAckHoldSuppressedReopenCount)}",
            $"recovery_burst_control_fallback_count={Math.Max(0, currentRecoveryBurstControlFallbackCount)}",
            $"recovery_burst_timeout_count={Math.Max(0, currentRecoveryBurstTimeoutCount)}",
            $"recovery_burst_completed_count={Math.Max(0, currentRecoveryBurstCompletedCount)}",
            $"recovery_burst_restart_suppressed_count={Math.Max(0, currentRecoveryBurstRestartSuppressedCount)}",
            $"recovery_burst_encoder_rerequest_count={Math.Max(0, currentRecoveryBurstEncoderRerequestCount)}",
            $"recovery_owner_pending_forced_reset_count={Math.Max(0, currentRecoveryOwnerPendingForcedResetCount)}",
            $"recovery_keyframe_emitted_after_forced_reset_count={Math.Max(0, currentRecoveryKeyframeEmittedAfterForcedResetCount)}",
            $"recovery_burst_completed_by_helper_ack_count={Math.Max(0, currentRecoveryBurstCompletedByHelperAckCount)}",
            $"recovery_burst_completed_by_applied_head_ack_count={Math.Max(0, currentRecoveryBurstCompletedByAppliedHeadAckCount)}",
            $"recovery_burst_completed_by_last_visible_apply_ack_count={Math.Max(0, currentRecoveryBurstCompletedByLastVisibleApplyAckCount)}",
            $"recovery_burst_completed_by_visible_recovery_floor_count={Math.Max(0, currentRecoveryBurstCompletedByVisibleRecoveryFloorCount)}",
            $"recovery_burst_completed_by_visible_apply_fallback_count={Math.Max(0, currentRecoveryBurstCompletedByVisibleApplyFallbackCount)}",
            $"recovery_burst_completed_by_timeout_count={Math.Max(0, currentRecoveryBurstCompletedByTimeoutCount)}",
            $"recovery_burst_completed_by_protected_frames_count={Math.Max(0, currentRecoveryBurstCompletedByProtectedFramesCount)}",
            $"recovery_burst_profile_transition_deferred_count={Math.Max(0, currentRecoveryBurstProfileTransitionDeferredCount)}",
            $"recovery_burst_profile_transition_takeover_count={Math.Max(0, currentRecoveryBurstProfileTransitionTakeoverCount)}",
            $"recovery_burst_stale_request_suppressed_count={Math.Max(0, currentRecoveryBurstStaleRequestSuppressedCount)}",
            $"recovery_burst_request_suppressed_due_to_helper_ack_count={Math.Max(0, currentRecoveryBurstRequestSuppressedDueToHelperAckCount)}",
            $"recovery_burst_started_while_helper_proof_healthy_count={Math.Max(0, currentRecoveryBurstStartedWhileHelperProofHealthyCount)}",
            $"helper_progress_past_owner_without_burst_ack_count={Math.Max(0, currentHelperProgressPastOwnerWithoutBurstAckCount)}",
            $"post_recovery_age_grace_active={(currentPostRecoveryAgeGraceActive ? 1 : 0)}",
            $"post_recovery_age_grace_suppressed_count={Math.Max(0, currentPostRecoveryAgeGraceSuppressedCount)}",
            $"last_acknowledged_recovery_owner_frame_id={(currentLastAcknowledgedRecoveryOwnerFrameId >= 0 ? currentLastAcknowledgedRecoveryOwnerFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"last_acknowledged_helper_head_frame_id={(currentLastAcknowledgedHelperHeadFrameId >= 0 ? currentLastAcknowledgedHelperHeadFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"last_acknowledged_visible_helper_head_frame_id={(currentLastAcknowledgedVisibleHelperHeadFrameId >= 0 ? currentLastAcknowledgedVisibleHelperHeadFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"last_acknowledged_helper_proof_age_ms={(currentLastAcknowledgedHelperProofAgeMs >= 0 ? currentLastAcknowledgedHelperProofAgeMs.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"persisted_release_floor_epoch={(currentPersistedReleaseFloorEpoch > 0 ? currentPersistedReleaseFloorEpoch.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"satisfied_recovery_floor_frame_id={(currentSatisfiedRecoveryFloorFrameId >= 0 ? currentSatisfiedRecoveryFloorFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"satisfied_recovery_floor_source={(string.IsNullOrWhiteSpace(currentSatisfiedRecoveryFloorSource) ? "(none)" : currentSatisfiedRecoveryFloorSource)}",
            $"satisfied_recovery_floor_visible_proof_count={Math.Max(0, currentSatisfiedRecoveryFloorVisibleProofCount)}",
            $"continuity_signal_ignored_due_to_satisfied_floor_count={Math.Max(0, currentContinuitySignalIgnoredDueToSatisfiedFloorCount)}",
            $"recovery_lock_cleared_by_acknowledged_proof_count={Math.Max(0, currentRecoveryLockClearedByAcknowledgedProofCount)}",
            $"recovery_lock_last_clear_reason={(string.IsNullOrWhiteSpace(currentRecoveryLockLastClearReason) ? "(none)" : currentRecoveryLockLastClearReason)}",
            $"last_completed_recovery_epoch={(currentLastCompletedRecoveryEpoch > 0 ? currentLastCompletedRecoveryEpoch.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"last_completed_recovery_owner_frame_id={(currentLastCompletedRecoveryOwnerFrameId >= 0 ? currentLastCompletedRecoveryOwnerFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"last_completed_recovery_ack_frame_id={(currentLastCompletedRecoveryAckFrameId >= 0 ? currentLastCompletedRecoveryAckFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"last_completed_recovery_ack_source={(string.IsNullOrWhiteSpace(currentLastCompletedRecoveryAckSource) ? "(none)" : currentLastCompletedRecoveryAckSource)}",
            $"last_completed_recovery_owner_emit_to_ack_ms={(currentLastCompletedRecoveryOwnerEmitToAckMs >= 0 ? currentLastCompletedRecoveryOwnerEmitToAckMs.ToString(CultureInfo.InvariantCulture) : "(none)")}",
            $"last_completed_recovery_completion_kind={(string.IsNullOrWhiteSpace(currentLastCompletedRecoveryCompletionKind) ? "(none)" : currentLastCompletedRecoveryCompletionKind)}",
            $"recovery_completion_accounting_mismatch={Math.Max(0, currentRecoveryCompletionAccountingMismatch)}",
            $"remote_high_frame_age_catch_up_entry_consecutive_ticks={Math.Max(0, currentRemoteHighFrameAgeCatchUpEntryConsecutiveTicks)}",
            $"sender_catch_up_entered_due_to_remote_high_frame_age_count={Math.Max(0, currentSenderCatchUpEnteredDueToRemoteHighFrameAgeCount)}",
            $"remote_high_frame_age_catch_up_suppressed_due_to_bootstrap_grace_count={Math.Max(0, currentRemoteHighFrameAgeCatchUpSuppressedDueToBootstrapGraceCount)}",
            $"remote_high_frame_age_catch_up_suppressed_due_to_post_ack_grace_count={Math.Max(0, currentRemoteHighFrameAgeCatchUpSuppressedDueToPostAckGraceCount)}",
            $"remote_high_frame_age_catch_up_suppressed_due_to_current_epoch_recovery_burst_count={Math.Max(0, currentRemoteHighFrameAgeCatchUpSuppressedDueToCurrentEpochRecoveryBurstCount)}",
            $"remote_high_frame_age_catch_up_suppressed_due_to_missing_helper_evidence_count={Math.Max(0, currentRemoteHighFrameAgeCatchUpSuppressedDueToMissingHelperEvidenceCount)}",
            $"remote_high_frame_age_catch_up_suppressed_due_to_under_threshold_count={Math.Max(0, currentRemoteHighFrameAgeCatchUpSuppressedDueToUnderThresholdCount)}",
            $"last_remote_high_frame_age_catch_up_suppression_reason={(string.IsNullOrWhiteSpace(currentLastRemoteHighFrameAgeCatchUpSuppressionReason) ? "(none)" : currentLastRemoteHighFrameAgeCatchUpSuppressionReason)}",
            $"catch_up_recovery_suppressed_due_to_remote_high_frame_age_count={Math.Max(0, currentCatchUpRecoverySuppressedDueToRemoteHighFrameAgeCount)}",
            $"catch_up_exit_while_remote_high_frame_age_pressure_count={Math.Max(0, currentCatchUpExitWhileRemoteHighFrameAgePressureCount)}",
            $"recovery_lock_allowed_same_tuning_mode_change_count={Math.Max(0, currentRecoveryLockAllowedSameTuningModeChangeCount)}",
            $"last_recovery_lock_allowed_same_tuning_mode_change={(string.IsNullOrWhiteSpace(currentLastRecoveryLockAllowedSameTuningModeChange) ? "(none)" : currentLastRecoveryLockAllowedSameTuningModeChange)}",
            $"recovery_owner_pending_non_key_held_count={Math.Max(0, currentRecoveryOwnerPendingNonKeyHeldCount)}",
            $"recovery_owner_pending_non_key_replaced_count={Math.Max(0, currentRecoveryOwnerPendingNonKeyReplacedCount)}",
            $"recovery_owner_unacked_non_key_held_count={Math.Max(0, currentRecoveryOwnerUnackedNonKeyHeldCount)}",
            $"recovery_owner_unacked_non_key_replaced_count={Math.Max(0, currentRecoveryOwnerUnackedNonKeyReplacedCount)}",
            $"recovery_same_epoch_keyframe_suppressed_while_owner_unacked_count={Math.Max(0, currentRecoverySameEpochKeyframeSuppressedWhileOwnerUnackedCount)}",
            $"recovery_owner_replaced_before_ack_count={Math.Max(0, currentRecoveryOwnerReplacedBeforeAckCount)}",
            $"high_frame_age_suppressed_during_owner_ack_count={Math.Max(0, currentHighFrameAgeSuppressedDuringOwnerAckCount)}",
            $"sender_received_helper_progress_during_continuity_loss_count={Math.Max(0, currentSenderReceivedHelperProgressDuringContinuityLossCount)}",
            $"post_ack_mode_grace_suppressed_high_frame_age_count={Math.Max(0, currentPostAckModeGraceSuppressedHighFrameAgeCount)}",
            $"bootstrap_grace_suppressed_catch_up_count={Math.Max(0, currentBootstrapGraceSuppressedCatchUpCount)}",
            $"protected_recovery_frames_dispatched_count={Math.Max(0, metrics.ProtectedRecoveryFramesDispatched)}",
            $"recovery_protected_frame_blocked_by_ordinary_count={Math.Max(0, metrics.RecoveryProtectedFrameBlockedByOrdinaryCount)}",
            $"avg_fragments_per_frame={metrics.AverageFragmentsPerFrame.ToString("F2", CultureInfo.InvariantCulture)}",
            $"avg_transport_payloads_per_frame={metrics.AverageTransportPayloadsPerFrame.ToString("F2", CultureInfo.InvariantCulture)}",
            $"transport_payloads_sent={metrics.TransportPayloadsSent}",
            $"batched_payloads_sent={metrics.BatchedPayloadsSent}",
            $"legacy_fragment_payloads_sent={metrics.LegacyFragmentPayloadsSent}",
            $"bridge_health_kind={bridgeHealthKind}",
            $"recent_health_issues={recentHealthIssueCount}",
            $"frames_deferred_to_send_slot={metrics.FramesDeferredToSendSlot}",
            $"frames_replaced_before_send_slot={metrics.FramesReplacedBeforeSendSlot}",
            $"frames_dropped_by_queue_evict={metrics.FramesDroppedByQueueEvict}",
            $"ordinary_sender_slot_replace_count={metrics.FramesReplacedBeforeSendSlot}",
            $"ordinary_sender_queue_evict_count={metrics.FramesDroppedByQueueEvict}",
            $"send_slot_empty_count={metrics.SendSlotEmptyCount}",
            $"slot_coalescing_active={(metrics.SlotCoalescingActive ? 1 : 0)}",
            $"bridge_raw_messages_received={nknSnapshot.BridgeRawMessagesReceived}",
            $"bridge_control_messages_received={nknSnapshot.BridgeControlMessagesReceived}",
            $"bridge_media_messages_received={nknSnapshot.BridgeMediaMessagesReceived}",
            $"bridge_control_bytes_received={nknSnapshot.BridgeControlBytesReceived}",
            $"bridge_media_bytes_received={nknSnapshot.BridgeMediaBytesReceived}",
            $"media_plane_frames_sent={nknSnapshot.MediaPlane.FramesSent}",
            $"media_plane_policy_reject_count={nknSnapshot.MediaPlane.PolicyRejectCount}",
            $"media_plane_replay_reject_count={nknSnapshot.MediaPlane.ReplayRejectCount}",
            $"media_plane_last_reject_reason={(string.IsNullOrWhiteSpace(nknSnapshot.MediaPlane.LastRejectReason) ? "(none)" : nknSnapshot.MediaPlane.LastRejectReason)}",
            $"media_plane_attached={(nknSnapshot.MediaPlane.Attached ? 1 : 0)}",
            $"media_plane_generation={nknSnapshot.MediaPlane.MediaGeneration}"
        };
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            string.Join("; ", freshnessSummaryFields));
        var healthSenderOperatingState = string.Equals(metrics.SenderOperatingState, "catch_up", StringComparison.Ordinal)
            ? ScreenShareSenderOperatingState.CatchUp
            : string.Equals(metrics.SenderOperatingState, "reduced", StringComparison.Ordinal)
                ? ScreenShareSenderOperatingState.Reduced
                : ScreenShareSenderOperatingState.Normal;
        var healthSenderGuardState = metrics.SenderGuardState switch
        {
            "recovery_locked" => ScreenShareSenderGuardState.RecoveryLocked,
            "post_ack_grace" => ScreenShareSenderGuardState.PostAckGrace,
            "bootstrap_grace" => ScreenShareSenderGuardState.BootstrapGrace,
            "transition_grace" => ScreenShareSenderGuardState.TransitionGrace,
            _ => ScreenShareSenderGuardState.None,
        };
        var healthSnapshot = ScreenShareOperationalHealthSnapshotBuilder.BuildFromSenderSummary(
            healthSenderOperatingState,
            healthSenderGuardState,
            metrics.DominantPressureBlocker,
            recoveryActive: currentRecoveryLockActive || currentRecoveryBurstActive,
            helperSteadyVisibleProgressActive: currentHelperSteadyVisibleProgressActive,
            helperFactHealthyActive: currentRemoteHelperFactHealthyActive,
            helperVisibleHeadFrameId: currentHelperVisibleHeadFrameId,
            helperStableVisibleHeadFrameId: currentHelperStableVisibleHeadFrameId,
            helperVisibleRecoveryFloorFrameId: currentHelperVisibleRecoveryFloorFrameId,
            helperRecoveryKeyframeApplyCount: currentHelperCurrentEpochRecoveryKeyframeApplyCount);
        LocalOperationalLog.Info("ScreenShare", healthSnapshot.ToLogMessage());
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_visible_proof_summary; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; remote_helper_visible_head_frame_id={(currentHelperVisibleHeadFrameId >= 0 ? currentHelperVisibleHeadFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}; remote_helper_visible_recovery_floor_frame_id={(currentHelperVisibleRecoveryFloorFrameId >= 0 ? currentHelperVisibleRecoveryFloorFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}; remote_helper_current_epoch_recovery_keyframe_apply_count={Math.Max(0, currentHelperCurrentEpochRecoveryKeyframeApplyCount)}; last_acknowledged_visible_helper_head_frame_id={(currentLastAcknowledgedVisibleHelperHeadFrameId >= 0 ? currentLastAcknowledgedVisibleHelperHeadFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}; persisted_release_floor_epoch={(currentPersistedReleaseFloorEpoch > 0 ? currentPersistedReleaseFloorEpoch.ToString(CultureInfo.InvariantCulture) : "(none)")}; satisfied_recovery_floor_frame_id={(currentSatisfiedRecoveryFloorFrameId >= 0 ? currentSatisfiedRecoveryFloorFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}; satisfied_recovery_floor_source={(string.IsNullOrWhiteSpace(currentSatisfiedRecoveryFloorSource) ? "(none)" : currentSatisfiedRecoveryFloorSource)}; satisfied_recovery_floor_visible_proof_count={Math.Max(0, currentSatisfiedRecoveryFloorVisibleProofCount)}; recovery_burst_completed_by_visible_recovery_floor_count={Math.Max(0, currentRecoveryBurstCompletedByVisibleRecoveryFloorCount)}; recovery_burst_completed_by_visible_apply_fallback_count={Math.Max(0, currentRecoveryBurstCompletedByVisibleApplyFallbackCount)}; continuity_signal_ignored_due_to_visible_satisfied_floor_count={Math.Max(0, currentContinuitySignalIgnoredDueToVisibleSatisfiedFloorCount)}; recovery_lock_cleared_by_visible_proof_count={Math.Max(0, currentRecoveryLockClearedByVisibleProofCount)}; recovery_lock_last_clear_reason={(string.IsNullOrWhiteSpace(currentRecoveryLockLastClearReason) ? "(none)" : currentRecoveryLockLastClearReason)}");
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_recovery_receipt_summary; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; recovery_burst_completed_by_helper_visible_receipt_count={Math.Max(0, currentRecoveryBurstCompletedByHelperVisibleReceiptCount)}; remote_recovery_receipt_rejected_count={Math.Max(0, currentRemoteRecoveryReceiptRejectedCount)}; last_remote_recovery_receipt_stream_epoch={(currentLastRemoteRecoveryReceiptStreamEpoch > 0 ? currentLastRemoteRecoveryReceiptStreamEpoch.ToString(CultureInfo.InvariantCulture) : "(none)")}; last_remote_recovery_receipt_owner_frame_id={(currentLastRemoteRecoveryReceiptOwnerFrameId >= 0 ? currentLastRemoteRecoveryReceiptOwnerFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}; last_remote_recovery_receipt_visible_recovery_frame_id={(currentLastRemoteRecoveryReceiptVisibleRecoveryFrameId >= 0 ? currentLastRemoteRecoveryReceiptVisibleRecoveryFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}; last_remote_recovery_receipt_visible_head_frame_id={(currentLastRemoteRecoveryReceiptVisibleHeadFrameId >= 0 ? currentLastRemoteRecoveryReceiptVisibleHeadFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}; last_remote_recovery_receipt_kind={(string.IsNullOrWhiteSpace(currentLastRemoteRecoveryReceiptKind) ? "(none)" : currentLastRemoteRecoveryReceiptKind)}; last_remote_recovery_receipt_reject_reason={(string.IsNullOrWhiteSpace(currentLastRemoteRecoveryReceiptRejectReason) ? "(none)" : currentLastRemoteRecoveryReceiptRejectReason)}; last_remote_recovery_receipt_reject_active_stream_epoch={(currentLastRemoteRecoveryReceiptRejectActiveStreamEpoch > 0 ? currentLastRemoteRecoveryReceiptRejectActiveStreamEpoch.ToString(CultureInfo.InvariantCulture) : "(none)")}; last_remote_recovery_receipt_reject_active_owner_frame_id={(currentLastRemoteRecoveryReceiptRejectActiveOwnerFrameId >= 0 ? currentLastRemoteRecoveryReceiptRejectActiveOwnerFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}; last_remote_recovery_receipt_reject_active_phase={(string.IsNullOrWhiteSpace(currentLastRemoteRecoveryReceiptRejectActivePhase) ? "(none)" : currentLastRemoteRecoveryReceiptRejectActivePhase)}; recovery_epoch_takeover_suppressed_after_owner_emit_count={Math.Max(0, currentRecoveryEpochTakeoverSuppressedAfterOwnerEmitCount)}; last_recovery_epoch_takeover_suppressed_from_epoch={(currentLastRecoveryEpochTakeoverSuppressedFromEpoch > 0 ? currentLastRecoveryEpochTakeoverSuppressedFromEpoch.ToString(CultureInfo.InvariantCulture) : "(none)")}; last_recovery_epoch_takeover_suppressed_to_epoch={(currentLastRecoveryEpochTakeoverSuppressedToEpoch > 0 ? currentLastRecoveryEpochTakeoverSuppressedToEpoch.ToString(CultureInfo.InvariantCulture) : "(none)")}; last_recovery_epoch_takeover_suppressed_phase={(string.IsNullOrWhiteSpace(currentLastRecoveryEpochTakeoverSuppressedPhase) ? "(none)" : currentLastRecoveryEpochTakeoverSuppressedPhase)}; recovery_ack_source={(string.IsNullOrWhiteSpace(currentRecoveryAckSource) ? "(none)" : currentRecoveryAckSource)}");
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_transport_batch_summary; session_id={(string.IsNullOrWhiteSpace(currentSessionId) ? "(none)" : currentSessionId)}; avg_fragments_per_frame={metrics.AverageFragmentsPerFrame:F2}; avg_transport_payloads_per_frame={metrics.AverageTransportPayloadsPerFrame:F2}; transport_payloads_sent={metrics.TransportPayloadsSent}; batched_payloads_sent={metrics.BatchedPayloadsSent}; legacy_fragment_payloads_sent={metrics.LegacyFragmentPayloadsSent}; ordinary_non_key_batched_payloads_sent={metrics.OrdinaryNonKeyBatchedPayloadsSent}; ordinary_non_key_legacy_payloads_sent={metrics.OrdinaryNonKeyLegacyPayloadsSent}; keyframe_recovery_batched_payloads_sent={metrics.KeyframeOrRecoveryBatchedPayloadsSent}");
        MaybeLogSoftScaleWarning(nowUtc);
    }

    private void MaybeLogSoftScaleWarning(DateTimeOffset nowUtc)
    {
        if (FeatureFlags.ScreenShareScale >= 1d)
        {
            return;
        }

        lock (gate)
        {
            if (lastSoftScaleWarningUtc != default &&
                nowUtc - lastSoftScaleWarningUtc < SoftScaleWarningInterval)
            {
                return;
            }

            lastSoftScaleWarningUtc = nowUtc;
        }

        var qualityState = ScreenShareQualitySettings.GetCurrentEnvironmentState();
        LocalOperationalLog.Warn(
            "ScreenShareTransport",
            $"event=screenshare_soft_scale_active; capture_scale={ScreenShareQualitySettings.FormatScale(qualityState.CaptureScale)}; effective_quality_preset={qualityState.EffectivePresetKey}; legacy_preset_migrated={(qualityState.LegacyHigherClarityPresetMigrated ? 1 : 0)}; message=scale_below_1_may_reduce_text_sharpness");
    }

}
