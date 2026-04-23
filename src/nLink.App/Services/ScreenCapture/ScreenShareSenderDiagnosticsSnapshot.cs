using System;
using System.Globalization;

namespace NLink.App.Services.ScreenCapture;

internal readonly record struct ScreenShareSenderAutoTuneDecisionSnapshot(
    string SessionId,
    ScreenShareSenderFreshnessMode CurrentMode,
    ScreenShareSenderFreshnessMode NextMode,
    ScreenShareSenderOperatingState CurrentOperatingState,
    ScreenShareSenderOperatingState NextOperatingState,
    ScreenShareSenderGuardState GuardState,
    string DominantPressureBlocker,
    string Reason,
    long SourceStreamEpoch,
    ScreenShareRemotePressureMode RemotePressureMode,
    string RemotePressureReason,
    long RemoteObservedFrameAgeMs,
    int RemoteHighFrameAgeCatchUpEntryConsecutiveTicks,
    bool HelperVisibleOrApplyEvidenceActive,
    bool CurrentEpochRecoveryBurstActive,
    bool RecoveryLockActive,
    long RecoveryLockStreamEpoch,
    bool PostAckModeGraceActive,
    bool BootstrapModeGraceActive,
    bool ShouldEnterReduced,
    bool ShouldEnterCatchUp,
    bool CatchUpRecoverySuppressedDueToRemoteHighFrameAgePressure,
    string RemoteHighFrameAgeCatchUpSuppressionReason)
{
    public string ToLogMessage()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"event=screenshare_sender_auto_tune_decision; session_id={FormatValue(SessionId)}; current_mode={TransportScreenShareCoordinator.FormatSenderFreshnessMode(CurrentMode)}; next_mode={TransportScreenShareCoordinator.FormatSenderFreshnessMode(NextMode)}; current_operating_state={ScreenShareConceptualModelFormatter.FormatSenderOperatingState(CurrentOperatingState)}; next_operating_state={ScreenShareConceptualModelFormatter.FormatSenderOperatingState(NextOperatingState)}; guard_state={ScreenShareConceptualModelFormatter.FormatSenderGuardState(GuardState)}; dominant_pressure_blocker={FormatValue(DominantPressureBlocker)}; reason={FormatValue(Reason)}; source_stream_epoch={SourceStreamEpoch}; remote_pressure_mode={TransportScreenShareCoordinator.FormatRemotePressureMode(RemotePressureMode)}; remote_pressure_reason={FormatValue(RemotePressureReason)}; remote_observed_frame_age_ms={RemoteObservedFrameAgeMs}; remote_high_frame_age_catch_up_entry_consecutive_ticks={Math.Max(0, RemoteHighFrameAgeCatchUpEntryConsecutiveTicks)}; helper_visible_or_apply_evidence_active={(HelperVisibleOrApplyEvidenceActive ? 1 : 0)}; current_epoch_recovery_burst_active={(CurrentEpochRecoveryBurstActive ? 1 : 0)}; recovery_lock_active={(RecoveryLockActive ? 1 : 0)}; recovery_lock_stream_epoch={Math.Max(0, RecoveryLockStreamEpoch)}; post_ack_mode_grace_active={(PostAckModeGraceActive ? 1 : 0)}; bootstrap_mode_grace_active={(BootstrapModeGraceActive ? 1 : 0)}; should_enter_reduced={(ShouldEnterReduced ? 1 : 0)}; should_enter_catch_up={(ShouldEnterCatchUp ? 1 : 0)}; catch_up_recovery_suppressed_due_to_remote_high_frame_age_pressure={(CatchUpRecoverySuppressedDueToRemoteHighFrameAgePressure ? 1 : 0)}; remote_high_frame_age_catch_up_suppression_reason={FormatValue(RemoteHighFrameAgeCatchUpSuppressionReason)}");

    private static string FormatValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "(none)" : value.Trim();
}

internal readonly record struct ScreenShareSenderReducedPromotionSummarySnapshot(
    string SessionId,
    ScreenShareSenderFreshnessMode SenderFreshnessMode,
    long PromotionBlockerRateGateTicks,
    long PromotionBlockerHelperPressureTicks,
    long PromotionBlockerHelperWarmupTicks,
    long PromotionBlockerHelperApplyCountTicks,
    long PromotionBlockerBridgeHealthTicks,
    long PromotionBlockerRecoveryLockTicks,
    long PromotionBlockerQueueEvictTicks,
    long PromotionBlockerCaptureAgeTicks,
    long PromotionBlockerEncodeBudgetTicks,
    long PromotionBlockerTransitionGraceTicks,
    long PromotionEncodeSoftSpikeCount,
    long PromotionEncodeSoftSpikeResetSuppressedCount,
    long BlockedByEncodeBudgetAloneCount,
    long PostReceiptBlockerSuppressedCount,
    string LastPostReceiptBlockerSuppressedSet,
    string HealthyTickResetReasonCounts,
    string RecentEntries)
{
    public string ToLogMessage()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"event=screenshare_reduced_promotion_summary; session_id={SessionId}; sender_freshness_mode={TransportScreenShareCoordinator.FormatSenderFreshnessMode(SenderFreshnessMode)}; promotion_blocker_rate_gate_ticks={PromotionBlockerRateGateTicks}; promotion_blocker_helper_pressure_ticks={PromotionBlockerHelperPressureTicks}; promotion_blocker_helper_warmup_ticks={PromotionBlockerHelperWarmupTicks}; promotion_blocker_helper_apply_count_ticks={PromotionBlockerHelperApplyCountTicks}; promotion_blocker_bridge_health_ticks={PromotionBlockerBridgeHealthTicks}; promotion_blocker_recovery_lock_ticks={PromotionBlockerRecoveryLockTicks}; promotion_blocker_queue_evict_ticks={PromotionBlockerQueueEvictTicks}; promotion_blocker_capture_age_ticks={PromotionBlockerCaptureAgeTicks}; promotion_blocker_encode_budget_ticks={PromotionBlockerEncodeBudgetTicks}; promotion_blocker_transition_grace_ticks={PromotionBlockerTransitionGraceTicks}; promotion_encode_soft_spike_count={PromotionEncodeSoftSpikeCount}; promotion_encode_soft_spike_reset_suppressed_count={PromotionEncodeSoftSpikeResetSuppressedCount}; blocked_by_encode_budget_alone={BlockedByEncodeBudgetAloneCount}; post_receipt_blocker_suppressed_count={PostReceiptBlockerSuppressedCount}; last_post_receipt_blocker_suppressed_set={FormatValue(LastPostReceiptBlockerSuppressedSet)}; healthy_tick_reset_reason_counts={HealthyTickResetReasonCounts}; recent_entries={RecentEntries}");

    private static string FormatValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? "(none)" : value.Trim();
}
