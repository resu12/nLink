using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using NLink.Core.Logging;

namespace NLink.App.Services.ScreenCapture;

internal sealed partial class TransportScreenShareCoordinator
{
    private void ResetReducedPromotionDiagnostics_NoLock()
    {
        reducedPromotionJournal.Clear();
        healthyTickResetReasonCounts.Clear();
        promotionBlockerRateGateTicks = 0;
        promotionBlockerHelperPressureTicks = 0;
        promotionBlockerHelperWarmupTicks = 0;
        promotionBlockerHelperApplyCountTicks = 0;
        promotionBlockerBridgeHealthTicks = 0;
        promotionBlockerRecoveryLockTicks = 0;
        promotionBlockerQueueEvictTicks = 0;
        promotionBlockerCaptureAgeTicks = 0;
        promotionBlockerEncodeBudgetTicks = 0;
        promotionBlockerTransitionGraceTicks = 0;
        promotionEncodeSoftSpikeCount = 0;
        promotionEncodeSoftSpikeResetSuppressedCount = 0;
        promotionBlockedByEncodeBudgetAloneCount = 0;
        postReceiptBlockerSuppressedCount = 0;
        lastPostReceiptBlockerSuppressedSet = string.Empty;
        reducedPromotionEncodeSoftSpikeConsecutiveCount = 0;
        remoteHighFrameAgeCatchUpEntryConsecutiveTicks = 0;
        remoteHighFrameAgeCatchUpSuppressedDueToBootstrapGraceCount = 0;
        remoteHighFrameAgeCatchUpSuppressedDueToPostAckGraceCount = 0;
        remoteHighFrameAgeCatchUpSuppressedDueToCurrentEpochRecoveryBurstCount = 0;
        remoteHighFrameAgeCatchUpSuppressedDueToMissingHelperEvidenceCount = 0;
        remoteHighFrameAgeCatchUpSuppressedDueToUnderThresholdCount = 0;
        lastRemoteHighFrameAgeCatchUpSuppressionReason = string.Empty;
        recoveryLockAllowedSameTuningModeChangeCount = 0;
        lastRecoveryLockAllowedSameTuningModeChange = string.Empty;
        lastReducedPromotionSummaryLogUtc = default;
    }

    private bool HasHelperSteadyVisibleProgressPromotionProof_NoLock(long currentStreamEpoch)
    {
        var proofFrameId = Math.Max(GetLatestHelperVisibleProgressFrameId_NoLock(), remoteHelperFactProofFrameId);
        if (!remoteHelperFactHealthyActive || proofFrameId < 0)
        {
            return false;
        }

        if (helperFramesAppliedSinceLastGap >= 8 || proofFrameId >= 8)
        {
            return true;
        }

        return helperReducedModeEntryStreamEpoch > 0 &&
               helperReducedModeEntryStreamEpoch == currentStreamEpoch &&
               helperReducedModeEntryStableVisibleHeadFrameId >= 0 &&
               proofFrameId > helperReducedModeEntryStableVisibleHeadFrameId;
    }

    private void CaptureHelperStableVisibleHeadPromotionEntry_NoLock(long currentStreamEpoch)
    {
        helperReducedModeEntryStreamEpoch = Math.Max(0, currentStreamEpoch);
        helperReducedModeEntryStableVisibleHeadFrameId = helperStableVisibleHeadFrameId;
    }

    private void RecordReducedPromotionEvaluation(
        string currentSessionId,
        ScreenShareSenderFreshnessMode currentSenderMode,
        long captureToSendAgeMs,
        int promotionCaptureToSendBudgetMs,
        long lastEncodeTotalDurationMs,
        int promotionEncodeBudgetMs,
        ScreenShareRemotePressureMode remotePressureMode,
        string remotePressureReason,
        bool helperCurrentEpochWarmupActive,
        int helperCurrentEpochApplyCount,
        long helperCurrentEpochNeedMoreInputCount,
        int helperCurrentEpochHealthySignalCount,
        long helperCurrentEpochStaleDrops,
        bool helperSteadyVisibleProgressActive,
        long helperStableVisibleHeadFrameId,
        long helperFramesAppliedSinceLastGap,
        bool helperProgressProofSatisfied,
        bool helperPressureBlocker,
        bool helperApplyCountBlocker,
        string bridgeHealthKind,
        long recentHealthIssueCount,
        long rateGateDropDelta,
        long queueEvictDropDelta,
        long sourceSupersededPendingFramesDelta,
        bool recoveryLockBlocker,
        bool transitionGraceActive,
        bool encodeBudgetBlocker,
        bool encodeSoftPromotionOverrun,
        bool encodeSoftSpikeResetSuppressed,
        int healthyTicksBefore,
        int healthyTicksAfter,
        int laneQueueDepth,
        long laneRecentDrops,
        bool hasActionableHealthDegradation,
        bool fileTransferDegradedHint,
        bool fileTransferCatchUpOnlyHint)
    {
        if (currentSenderMode != ScreenShareSenderFreshnessMode.Reduced ||
            string.IsNullOrWhiteSpace(currentSessionId))
        {
            return;
        }

        var rateGateBlocker = rateGateDropDelta > 0;
        var queueEvictBlocker = queueEvictDropDelta > 0;
        var captureAgeBlocker =
            captureToSendAgeMs < 0 ||
            captureToSendAgeMs > promotionCaptureToSendBudgetMs;
        var bridgeHealthBlocker =
            hasActionableHealthDegradation &&
            laneQueueDepth <= 0 &&
            laneRecentDrops <= 0 &&
            remotePressureMode == ScreenShareRemotePressureMode.None;
        var queueDepthBlocker = laneQueueDepth > 0;
        var staleDropBlocker = laneRecentDrops > 0 || helperCurrentEpochStaleDrops > 0;

        var blockers = new List<string>(12);
        if (recoveryLockBlocker)
        {
            blockers.Add("recovery_lock_active");
        }

        if (transitionGraceActive)
        {
            blockers.Add("transition_grace_active");
        }

        if (helperPressureBlocker)
        {
            blockers.Add("helper_pressure");
        }

        if (helperCurrentEpochWarmupActive)
        {
            blockers.Add("helper_warmup");
        }

        if (helperApplyCountBlocker)
        {
            blockers.Add("helper_apply_count");
        }

        if (rateGateBlocker)
        {
            blockers.Add("rate_gate");
        }

        if (queueEvictBlocker)
        {
            blockers.Add("queue_evict");
        }

        if (queueDepthBlocker)
        {
            blockers.Add("transport_queue_depth");
        }

        if (staleDropBlocker)
        {
            blockers.Add("transport_stale_drops");
        }

        if (bridgeHealthBlocker)
        {
            blockers.Add("bridge_health");
        }

        if (captureAgeBlocker)
        {
            blockers.Add("capture_to_send_age");
        }

        if (encodeBudgetBlocker)
        {
            blockers.Add("encode_over_budget");
        }

        if (fileTransferCatchUpOnlyHint)
        {
            blockers.Add("file_transfer_catch_up");
        }

        if (fileTransferDegradedHint)
        {
            blockers.Add("file_transfer");
        }

        var healthyTickResetReason = healthyTicksBefore > 0 && healthyTicksAfter == 0
            ? ResolveHealthyTickResetReason(
                recoveryLockBlocker,
                transitionGraceActive,
                helperPressureBlocker,
                helperCurrentEpochWarmupActive,
                helperApplyCountBlocker,
                rateGateBlocker,
                queueEvictBlocker,
                queueDepthBlocker,
                staleDropBlocker,
                bridgeHealthBlocker,
                captureAgeBlocker,
                encodeBudgetBlocker,
                fileTransferCatchUpOnlyHint,
                fileTransferDegradedHint)
            : "none";

        lock (gate)
        {
            if (rateGateBlocker)
            {
                promotionBlockerRateGateTicks++;
            }

            if (helperPressureBlocker)
            {
                promotionBlockerHelperPressureTicks++;
            }

            if (helperCurrentEpochWarmupActive)
            {
                promotionBlockerHelperWarmupTicks++;
            }

            if (helperApplyCountBlocker)
            {
                promotionBlockerHelperApplyCountTicks++;
            }

            if (bridgeHealthBlocker)
            {
                promotionBlockerBridgeHealthTicks++;
            }

            if (recoveryLockBlocker)
            {
                promotionBlockerRecoveryLockTicks++;
            }

            if (queueEvictBlocker)
            {
                promotionBlockerQueueEvictTicks++;
            }

            if (captureAgeBlocker)
            {
                promotionBlockerCaptureAgeTicks++;
            }

            if (encodeBudgetBlocker)
            {
                promotionBlockerEncodeBudgetTicks++;
            }

            if (encodeSoftPromotionOverrun)
            {
                promotionEncodeSoftSpikeCount++;
            }

            if (encodeSoftSpikeResetSuppressed)
            {
                promotionEncodeSoftSpikeResetSuppressedCount++;
            }

            var blockedByEncodeBudgetAlone =
                encodeBudgetBlocker &&
                helperProgressProofSatisfied &&
                !helperPressureBlocker &&
                !helperCurrentEpochWarmupActive &&
                !recoveryLockBlocker &&
                !transitionGraceActive &&
                !rateGateBlocker &&
                !queueEvictBlocker &&
                !queueDepthBlocker &&
                !staleDropBlocker &&
                !bridgeHealthBlocker &&
                !captureAgeBlocker &&
                !fileTransferCatchUpOnlyHint &&
                !fileTransferDegradedHint;
            if (blockedByEncodeBudgetAlone)
            {
                promotionBlockedByEncodeBudgetAloneCount++;
            }

            if (transitionGraceActive)
            {
                promotionBlockerTransitionGraceTicks++;
            }

            if (!string.Equals(healthyTickResetReason, "none", StringComparison.Ordinal))
            {
                if (!healthyTickResetReasonCounts.TryAdd(healthyTickResetReason, 1))
                {
                    healthyTickResetReasonCounts[healthyTickResetReason]++;
                }
            }

            reducedPromotionJournal.Add(
                new ReducedPromotionEvaluationEntry(
                    clock.UtcNow,
                    captureToSendAgeMs,
                    promotionCaptureToSendBudgetMs,
                    lastEncodeTotalDurationMs,
                    promotionEncodeBudgetMs,
                    FormatRemotePressureMode(remotePressureMode),
                    FormatMetricValue(remotePressureReason),
                    helperCurrentEpochWarmupActive,
                    helperCurrentEpochApplyCount,
                    helperCurrentEpochNeedMoreInputCount,
                    helperCurrentEpochHealthySignalCount,
                    helperCurrentEpochStaleDrops,
                    helperSteadyVisibleProgressActive,
                    helperStableVisibleHeadFrameId,
                    helperFramesAppliedSinceLastGap,
                    bridgeHealthKind,
                    recentHealthIssueCount,
                    rateGateDropDelta,
                    queueEvictDropDelta,
                    sourceSupersededPendingFramesDelta,
                    recoveryLockBlocker,
                    transitionGraceActive,
                    healthyTicksBefore,
                    healthyTicksAfter,
                    encodeBudgetBlocker,
                    encodeSoftPromotionOverrun,
                    encodeSoftSpikeResetSuppressed,
                    blockers.Count == 0 ? "none" : string.Join(",", blockers),
                    healthyTickResetReason));

            while (reducedPromotionJournal.Count > MaxReducedPromotionJournalEntries)
            {
                reducedPromotionJournal.RemoveAt(0);
            }
        }
    }

    private void MaybeLogReducedPromotionSummary(string currentSessionId, ScreenShareSenderFreshnessMode currentSenderMode)
    {
        if (string.IsNullOrWhiteSpace(currentSessionId))
        {
            return;
        }

        string recentEntries;
        string healthyTickResetReasonCountsValue;
        long rateGateTicks;
        long helperPressureTicks;
        long helperWarmupTicks;
        long helperApplyCountTicks;
        long bridgeHealthTicks;
        long recoveryLockTicks;
        long queueEvictTicks;
        long captureAgeTicks;
        long encodeBudgetTicks;
        long transitionGraceTicks;
        long encodeSoftSpikeCount;
        long encodeSoftSpikeResetSuppressedCount;
        long blockedByEncodeBudgetAloneCount;
        long postReceiptSuppressedCount;
        string lastPostReceiptSuppressedSet;
        var nowUtc = clock.UtcNow;
        lock (gate)
        {
            if (lastReducedPromotionSummaryLogUtc != default &&
                nowUtc - lastReducedPromotionSummaryLogUtc < ReducedPromotionSummaryLogInterval)
            {
                return;
            }

            lastReducedPromotionSummaryLogUtc = nowUtc;
            recentEntries = FormatReducedPromotionRecentEntries_NoLock();
            healthyTickResetReasonCountsValue = FormatHealthyTickResetReasonCounts_NoLock();
            rateGateTicks = promotionBlockerRateGateTicks;
            helperPressureTicks = promotionBlockerHelperPressureTicks;
            helperWarmupTicks = promotionBlockerHelperWarmupTicks;
            helperApplyCountTicks = promotionBlockerHelperApplyCountTicks;
            bridgeHealthTicks = promotionBlockerBridgeHealthTicks;
            recoveryLockTicks = promotionBlockerRecoveryLockTicks;
            queueEvictTicks = promotionBlockerQueueEvictTicks;
            captureAgeTicks = promotionBlockerCaptureAgeTicks;
            encodeBudgetTicks = promotionBlockerEncodeBudgetTicks;
            transitionGraceTicks = promotionBlockerTransitionGraceTicks;
            encodeSoftSpikeCount = promotionEncodeSoftSpikeCount;
            encodeSoftSpikeResetSuppressedCount = promotionEncodeSoftSpikeResetSuppressedCount;
            blockedByEncodeBudgetAloneCount = promotionBlockedByEncodeBudgetAloneCount;
            postReceiptSuppressedCount = postReceiptBlockerSuppressedCount;
            lastPostReceiptSuppressedSet = lastPostReceiptBlockerSuppressedSet;
        }

        LocalOperationalLog.Info(
            "ScreenShareTransport",
            new ScreenShareSenderReducedPromotionSummarySnapshot(
                currentSessionId,
                currentSenderMode,
                rateGateTicks,
                helperPressureTicks,
                helperWarmupTicks,
                helperApplyCountTicks,
                bridgeHealthTicks,
                recoveryLockTicks,
                queueEvictTicks,
                captureAgeTicks,
                encodeBudgetTicks,
                transitionGraceTicks,
                encodeSoftSpikeCount,
                encodeSoftSpikeResetSuppressedCount,
                blockedByEncodeBudgetAloneCount,
                postReceiptSuppressedCount,
                lastPostReceiptSuppressedSet,
                healthyTickResetReasonCountsValue,
                recentEntries).ToLogMessage());
    }

    private static string ResolveHealthyTickResetReason(
        bool recoveryLockActive,
        bool transitionGraceActive,
        bool helperPressureBlocker,
        bool helperCurrentEpochWarmupActive,
        bool helperApplyCountBlocker,
        bool rateGateBlocker,
        bool queueEvictBlocker,
        bool queueDepthBlocker,
        bool staleDropBlocker,
        bool bridgeHealthBlocker,
        bool captureAgeBlocker,
        bool encodeBudgetBlocker,
        bool fileTransferCatchUpOnlyHint,
        bool fileTransferDegradedHint)
    {
        if (recoveryLockActive)
        {
            return "recovery_lock_active";
        }

        if (transitionGraceActive)
        {
            return "transition_grace_active";
        }

        if (helperPressureBlocker)
        {
            return "helper_pressure";
        }

        if (helperCurrentEpochWarmupActive)
        {
            return "helper_warmup";
        }

        if (helperApplyCountBlocker)
        {
            return "helper_apply_count";
        }

        if (rateGateBlocker)
        {
            return "rate_gate";
        }

        if (queueEvictBlocker)
        {
            return "queue_evict";
        }

        if (queueDepthBlocker)
        {
            return "transport_queue_depth";
        }

        if (staleDropBlocker)
        {
            return "transport_stale_drops";
        }

        if (bridgeHealthBlocker)
        {
            return "bridge_health";
        }

        if (captureAgeBlocker)
        {
            return "capture_to_send_age";
        }

        if (encodeBudgetBlocker)
        {
            return "encode_over_budget";
        }

        if (fileTransferCatchUpOnlyHint)
        {
            return "file_transfer_catch_up";
        }

        if (fileTransferDegradedHint)
        {
            return "file_transfer";
        }

        return "unknown";
    }

    private string FormatReducedPromotionRecentEntries_NoLock()
    {
        if (reducedPromotionJournal.Count == 0)
        {
            return "(none)";
        }

        var startIndex = Math.Max(0, reducedPromotionJournal.Count - 6);
        var entries = new List<string>(reducedPromotionJournal.Count - startIndex);
        for (var i = startIndex; i < reducedPromotionJournal.Count; i++)
        {
            var entry = reducedPromotionJournal[i];
            entries.Add(
                $"{entry.TimestampUtc:HHmmss}|h={entry.HealthyTicksBefore}>{entry.HealthyTicksAfter}|blockers={entry.Blockers}|reset={entry.HealthyTickResetReason}|cap={entry.CaptureToSendAgeMs}/{entry.PromotionCaptureToSendBudgetMs}|enc={entry.LastEncodeTotalDurationMs}/{entry.PromotionEncodeBudgetMs}|pressure={entry.RemotePressureMode}/{entry.RemotePressureReason}|apply={entry.HelperCurrentEpochApplyCount}|steady={(entry.HelperSteadyVisibleProgressActive ? 1 : 0)}|head={(entry.HelperStableVisibleHeadFrameId >= 0 ? entry.HelperStableVisibleHeadFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}|gap_apply={entry.HelperFramesAppliedSinceLastGap}|nmi={entry.HelperCurrentEpochNeedMoreInputCount}|stale={entry.HelperCurrentEpochStaleDrops}|bridge={entry.BridgeHealthKind}:{entry.RecentHealthIssues}|rg={entry.RateGateDropDelta}|qe={entry.QueueEvictDropDelta}|sup={entry.SourceSupersededPendingFramesDelta}|lock={(entry.RecoveryLockActive ? 1 : 0)}|grace={(entry.TransitionGraceActive ? 1 : 0)}");
        }

        return string.Join("~", entries);
    }

    private string FormatHealthyTickResetReasonCounts_NoLock()
    {
        if (healthyTickResetReasonCounts.Count == 0)
        {
            return "(none)";
        }

        var items = healthyTickResetReasonCounts
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal);
        var formatted = new List<string>();
        foreach (var item in items)
        {
            formatted.Add($"{item.Key}:{item.Value}");
        }

        return string.Join("|", formatted);
    }

    private void MaybeLogSenderPromotionBlocked(
        string currentSessionId,
        ScreenShareSenderFreshnessMode currentSenderMode,
        ScreenShareSenderFreshnessMode nextSenderMode,
        bool inStartupWarmup,
        long captureToSendAgeMs,
        int promotionCaptureToSendBudgetMs,
        long lastEncodeTotalDurationMs,
        long remoteObservedFrameAgeMs,
        int laneQueueDepth,
        long laneRecentDrops,
        bool hasActionableHealthDegradation,
        string bridgeHealthKind,
        long recentHealthIssueCount,
        ScreenShareRemotePressureMode remotePressureMode,
        string remotePressureReason,
        bool fileTransferDegradedHint,
        bool fileTransferCatchUpOnlyHint,
        bool hasRateGatePressure,
        bool hasQueuePressure,
        int reducedRecoveryLowPressureTickCount,
        long supersededPendingRawFrameDelta,
        bool transitionGraceActive,
        bool encodeBudgetBlocker,
        int promotionEncodeBudgetMs,
        int demotionEncodePressureMs,
        bool helperCurrentEpochWarmupActive,
        int helperCurrentEpochApplyCount,
        long helperCurrentEpochNeedMoreInputCount,
        int helperCurrentEpochHealthySignalCount,
        long helperCurrentEpochStaleDrops,
        bool helperSteadyVisibleProgressActive,
        long helperStableVisibleHeadFrameId,
        long helperFramesAppliedSinceLastGap,
        bool remoteHelperFactHealthyActive,
        bool helperProgressProofSatisfied,
        bool helperPressureBlocker,
        bool helperApplyCountBlocker,
        bool recoveryLockBlocker)
    {
        if (inStartupWarmup ||
            nextSenderMode != ScreenShareSenderFreshnessMode.Reduced ||
            string.IsNullOrWhiteSpace(currentSessionId))
        {
            return;
        }

        var nowUtc = clock.UtcNow;
        if (lastSenderPromotionBlockedLogUtc != default &&
            nowUtc - lastSenderPromotionBlockedLogUtc < SenderPromotionBlockedLogInterval)
        {
            return;
        }

        var blockers = new List<string>(8);
        if (currentSenderMode == ScreenShareSenderFreshnessMode.CatchUp)
        {
            blockers.Add("catch_up_recovery");
        }

        if (transitionGraceActive)
        {
            blockers.Add("transition_grace_active");
        }

        if (recoveryLockBlocker)
        {
            blockers.Add("recovery_lock_active");
        }

        if (helperPressureBlocker)
        {
            blockers.Add("helper_pressure");
        }

        if (helperCurrentEpochWarmupActive)
        {
            blockers.Add("helper_warmup");
        }

        if (helperApplyCountBlocker)
        {
            blockers.Add("helper_apply_count");
        }

        if (fileTransferCatchUpOnlyHint)
        {
            blockers.Add("file_transfer_catch_up");
        }

        if (fileTransferDegradedHint)
        {
            blockers.Add("file_transfer");
        }

        if (captureToSendAgeMs < 0 || captureToSendAgeMs > promotionCaptureToSendBudgetMs)
        {
            blockers.Add("capture_to_send_age");
        }

        if (encodeBudgetBlocker)
        {
            blockers.Add("encode_over_budget");
        }

        if (laneQueueDepth > 0)
        {
            blockers.Add("transport_queue_depth");
        }

        if (laneRecentDrops > 0 || helperCurrentEpochStaleDrops > 0)
        {
            blockers.Add("transport_stale_drops");
        }

        if (hasActionableHealthDegradation &&
            laneQueueDepth <= 0 &&
            laneRecentDrops <= 0 &&
            remotePressureMode == ScreenShareRemotePressureMode.None)
        {
            blockers.Add("bridge_health");
        }

        if (hasRateGatePressure)
        {
            blockers.Add("rate_gate");
        }

        if (hasQueuePressure)
        {
            blockers.Add("queue_evict");
        }

        if (reducedRecoveryLowPressureTickCount < ReducedRecoveryConsecutiveTicks)
        {
            blockers.Add("healthy_ticks_pending");
        }

        if (blockers.Count == 0)
        {
            return;
        }

        lastSenderPromotionBlockedLogUtc = nowUtc;
        LocalOperationalLog.Info(
            "ScreenShareTransport",
            $"event=screenshare_sender_promotion_blocked; session_id={currentSessionId}; blockers={string.Join(",", blockers)}; healthy_ticks={Math.Max(0, reducedRecoveryLowPressureTickCount)}; required_healthy_ticks={ReducedRecoveryConsecutiveTicks}; capture_to_send_age_ms={captureToSendAgeMs}; promotion_capture_to_send_budget_ms={promotionCaptureToSendBudgetMs}; last_encode_total_duration_ms={lastEncodeTotalDurationMs}; promotion_encode_budget_ms={promotionEncodeBudgetMs}; demotion_encode_pressure_ms={demotionEncodePressureMs}; remote_observed_frame_age_ms={remoteObservedFrameAgeMs}; transport_queue_depth={laneQueueDepth}; recent_stale_drops={laneRecentDrops}; helper_current_epoch_warmup_active={(helperCurrentEpochWarmupActive ? 1 : 0)}; helper_current_epoch_apply_count={Math.Max(0, helperCurrentEpochApplyCount)}; helper_current_epoch_need_more_input_count={Math.Max(0, helperCurrentEpochNeedMoreInputCount)}; helper_current_epoch_healthy_signal_count={Math.Max(0, helperCurrentEpochHealthySignalCount)}; helper_current_epoch_stale_drops={Math.Max(0, helperCurrentEpochStaleDrops)}; helper_steady_visible_progress_active={(helperSteadyVisibleProgressActive ? 1 : 0)}; helper_stable_visible_head_frame_id={(helperStableVisibleHeadFrameId >= 0 ? helperStableVisibleHeadFrameId.ToString(CultureInfo.InvariantCulture) : "(none)")}; helper_frames_applied_since_last_gap={Math.Max(0, helperFramesAppliedSinceLastGap)}; helper_progress_proof_satisfied={(helperProgressProofSatisfied ? 1 : 0)}; remote_helper_fact_healthy_active={(remoteHelperFactHealthyActive ? 1 : 0)}; bridge_health_kind={bridgeHealthKind}; recent_health_issues={recentHealthIssueCount}; remote_pressure_mode={FormatRemotePressureMode(remotePressureMode)}; source_superseded_pending_frames_delta={supersededPendingRawFrameDelta}; transition_grace_active={(transitionGraceActive ? 1 : 0)}");
    }

    private static string ResolveSenderFreshnessReason(
        bool fileTransferDegradedHint,
        bool fileTransferCatchUpOnlyHint,
        bool hasSevereHealthDegradation,
        bool hasActionableHealthDegradation,
        bool hasSevereLaneCongestion,
        bool hasLaneCongestion,
        long oldestQueuedAgeMs,
        bool hasQueuePressure,
        bool hasSenderPressure,
        bool hasCatchUpPressure,
        bool hasRemoteHighFrameAgeCatchUpPressure,
        ScreenShareRemotePressureMode remotePressureMode)
    {
        if (hasRemoteHighFrameAgeCatchUpPressure)
        {
            return "remote_high_frame_age_escalation";
        }

        if (remotePressureMode == ScreenShareRemotePressureMode.CatchUpOnly)
        {
            return "remote_pressure";
        }

        if (remotePressureMode == ScreenShareRemotePressureMode.ReduceFps)
        {
            return "remote_pressure";
        }

        if (fileTransferCatchUpOnlyHint)
        {
            return "file_transfer_pressure";
        }

        if (fileTransferDegradedHint)
        {
            return "file_transfer";
        }

        if (hasSevereHealthDegradation && hasActionableHealthDegradation)
        {
            return "bridge_health";
        }

        if (hasSevereLaneCongestion || hasLaneCongestion)
        {
            return oldestQueuedAgeMs > 0 ? "bridge_queue" : "lane_congestion";
        }

        if (hasQueuePressure)
        {
            return "queue_pressure";
        }

        if (hasCatchUpPressure)
        {
            return "catch_up_pressure";
        }

        if (hasSenderPressure)
        {
            return "capture_age";
        }

        return "recovered";
    }

    private sealed record ReducedPromotionEvaluationEntry(
        DateTimeOffset TimestampUtc,
        long CaptureToSendAgeMs,
        int PromotionCaptureToSendBudgetMs,
        long LastEncodeTotalDurationMs,
        int PromotionEncodeBudgetMs,
        string RemotePressureMode,
        string RemotePressureReason,
        bool HelperCurrentEpochWarmupActive,
        int HelperCurrentEpochApplyCount,
        long HelperCurrentEpochNeedMoreInputCount,
        int HelperCurrentEpochHealthySignalCount,
        long HelperCurrentEpochStaleDrops,
        bool HelperSteadyVisibleProgressActive,
        long HelperStableVisibleHeadFrameId,
        long HelperFramesAppliedSinceLastGap,
        string BridgeHealthKind,
        long RecentHealthIssues,
        long RateGateDropDelta,
        long QueueEvictDropDelta,
        long SourceSupersededPendingFramesDelta,
        bool RecoveryLockActive,
        bool TransitionGraceActive,
        int HealthyTicksBefore,
        int HealthyTicksAfter,
        bool EncodeBudgetBlocker,
        bool EncodeSoftPromotionOverrun,
        bool EncodeSoftSpikeResetSuppressed,
        string Blockers,
        string HealthyTickResetReason);
}
