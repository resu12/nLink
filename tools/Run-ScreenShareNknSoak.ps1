param(
    [string]$ExePath = "",
    [int]$DurationSeconds = 30,
    [switch]$Build,
    [int]$TimeoutSeconds = 180,
    [string]$StrongBaselineArtifactDir = "",
    [string]$SafeBaselineArtifactDir = "",
    [switch]$SkipBehaviorFirstGate
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$implementationRoot = Join-Path $PSScriptRoot 'ScreenShareSoak'
$implementationFiles = @(
    'ProcessAndBridge.ps1',
    'LogParsing.ps1',
    'BaselineComparison.ps1',
    'SoakSummaryExtraction.ps1',
    'ArtifactWriters.ps1',
    'StabilizationGates.ps1'
)

foreach ($implementationFile in $implementationFiles) {
    $implementationPath = Join-Path $implementationRoot $implementationFile
    if (-not (Test-Path -LiteralPath $implementationPath)) {
        throw "NKN soak implementation file not found: $implementationPath"
    }

    . $implementationPath
}

$repoRoot = Resolve-RepoRoot
$resolvedStrongBaselineArtifactDir = if ([string]::IsNullOrWhiteSpace($StrongBaselineArtifactDir)) {
    Join-Path $repoRoot 'artifacts\soak\20260418-154032'
}
else {
    if ([System.IO.Path]::IsPathRooted($StrongBaselineArtifactDir)) { $StrongBaselineArtifactDir } else { Join-Path $repoRoot $StrongBaselineArtifactDir }
}

$resolvedSafeBaselineArtifactDir = if ([string]::IsNullOrWhiteSpace($SafeBaselineArtifactDir)) {
    Join-Path $repoRoot 'artifacts\soak\20260418-200524'
}
else {
    if ([System.IO.Path]::IsPathRooted($SafeBaselineArtifactDir)) { $SafeBaselineArtifactDir } else { Join-Path $repoRoot $SafeBaselineArtifactDir }
}

$guiSmokeScript = Join-Path $repoRoot "tools\GuiSmoke-Windows.ps1"
if (-not (Test-Path $guiSmokeScript)) {
    throw "GUI smoke harness not found: $guiSmokeScript"
}

$resolvedExePath = Resolve-ExePath -RepoRoot $repoRoot -RequestedPath $ExePath
Stop-NLinkProcesses -ResolvedExePath $resolvedExePath
Build-LocalExeIfNeeded -RepoRoot $repoRoot -ResolvedExePath $resolvedExePath -ForceBuild:$Build.IsPresent
Ensure-NknBridgeRuntimeForExe -RepoRoot $repoRoot -ResolvedExePath $resolvedExePath

$previousScenarioEnv = $env:NLINK_GUI_SMOKE_SCENARIOS
$previousTransportEnv = $env:NLINK_TRANSPORT
$previousDurationEnv = $env:NLINK_SCREENSHARE_SOAK_SECONDS
$previousUnsafeDeveloperModeEnv = $env:NLINK_UNSAFE_DEVELOPER_MODE

try {
    $env:NLINK_UNSAFE_DEVELOPER_MODE = '1'
    $env:NLINK_GUI_SMOKE_SCENARIOS = 'SCREENSHARE_NKN_SOAK'
    $env:NLINK_TRANSPORT = 'NKN'
    $env:NLINK_SCREENSHARE_SOAK_SECONDS = [string][Math]::Max(1, $DurationSeconds)

    Write-Host "Running live NKN screenshare soak..." -ForegroundColor Cyan
    Write-Host "  ExePath: $resolvedExePath"
    Write-Host "  DurationSeconds: $DurationSeconds"
    Write-Host "  TimeoutSeconds: $TimeoutSeconds"

    $guiHarnessExitCode = 0
    & powershell -ExecutionPolicy Bypass -File $guiSmokeScript -ExePath $resolvedExePath -TimeoutSeconds $TimeoutSeconds
    $guiHarnessExitCode = $LASTEXITCODE

    $summary = Get-SoakSummaryFromLog
    if ($summary.HelperQualitySummaryLines.Count -gt 0) {
        $missingHelperDiagnostics = @()
        if ($summary.HelperEpochLossLines.Count -eq 0) { $missingHelperDiagnostics += 'helper-frame-loss-epoch' }
        if ($summary.HelperEpochTimelineLines.Count -eq 0) { $missingHelperDiagnostics += 'helper-epoch-timeline' }
        if ($summary.HelperReassemblerRootCauseSummaryLines.Count -eq 0) { $missingHelperDiagnostics += 'helper-reassembler-root-cause-summary' }
        if ($summary.HelperPressureSummaryLines.Count -eq 0) { $missingHelperDiagnostics += 'helper-pressure-summary' }
        if ($missingHelperDiagnostics.Count -gt 0) {
            throw ("Helper debug artifacts were not emitted to the app log: {0}" -f ($missingHelperDiagnostics -join ', '))
        }
    }

    $soakArtifactDir = Write-SoakDiagnosticsArtifacts -RepoRoot $repoRoot -Summary $summary
    $currentComparisonMetrics = Get-CurrentSoakComparisonMetrics -Summary $summary
    $currentComparisonMetrics['artifact_dir'] = $soakArtifactDir
    $strongBaselineMetrics = Get-BaselineSoakComparisonMetrics -ArtifactDir $resolvedStrongBaselineArtifactDir
    $safeBaselineMetrics = Get-BaselineSoakComparisonMetrics -ArtifactDir $resolvedSafeBaselineArtifactDir
    $stabilizationArtifacts = Write-StabilizationArtifacts -ArtifactDir $soakArtifactDir -Summary $summary -CurrentMetrics $currentComparisonMetrics -StrongBaselineMetrics $strongBaselineMetrics -SafeBaselineMetrics $safeBaselineMetrics
    Write-Host ("[NKN Soak] capture_to_send_ms avg={0} min={1} max={2} samples={3}" -f `
        $summary.CaptureAvgMs,
        $summary.CaptureMinMs,
        $summary.CaptureMaxMs,
        $summary.CaptureSampleCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_apply_ms avg={0} min={1} max={2} p95={3} samples={4}; helper_stale_drops={5}" -f `
        $summary.HelperApplyAvgMs,
        $summary.HelperApplyMinMs,
        $summary.HelperApplyMaxMs,
        $summary.HelperApplyP95Ms,
        $summary.HelperApplyCount,
        $summary.HelperStaleDrops) -ForegroundColor Green
    $decodedRatio = if ($summary.LatestHelperFramesCompleted -gt 0 -and $summary.LatestHelperFramesDecoded -ge 0) { [math]::Round(($summary.LatestHelperFramesDecoded / [double]$summary.LatestHelperFramesCompleted), 2) } else { -1 }
    $appliedRatio = if ($summary.LatestHelperFramesCompleted -gt 0 -and $summary.LatestHelperFramesApplied -ge 0) { [math]::Round(($summary.LatestHelperFramesApplied / [double]$summary.LatestHelperFramesCompleted), 2) } else { -1 }
    Write-Host ("[NKN Soak] helper_cadence decode_avg_ms={0} apply_avg_interval_ms={1}; receiver_completed={2} decode_enqueued={3} helper_decoded={4} helper_applied={5}; decoded_ratio={6} applied_ratio={7}; dropped_before_decode={8} dropped_after_decode={9}; receiver_superseded_frames={10}" -f `
        $summary.LatestHelperDecodeDurationMs,
        $summary.LatestHelperApplyIntervalMs,
        $summary.LatestHelperFramesCompleted,
        $summary.LatestHelperFramesEnqueuedForDecode,
        $summary.LatestHelperFramesDecoded,
        $summary.LatestHelperFramesApplied,
        $decodedRatio,
        $appliedRatio,
        $summary.LatestHelperFramesDroppedBeforeDecode,
        $summary.LatestHelperFramesDroppedAfterDecode,
        $summary.ReceiverSupersededFrames) -ForegroundColor Green
    Write-Host ("[NKN Soak] recovery_burst phase={0}; gap_count={1}; gap_to_request_ms={2}; request_to_owner_ms={3}; owner_to_first_visible_apply_ms={4}; control_fallbacks={5}; completed={6}; timeouts={7}; suppressed_restarts={8}; rerequests={9}; forced_resets={10}; emitted_after_forced_reset={11}" -f `
        $summary.LatestRecoveryBurstPhase,
        $summary.LatestRecoveryGapCount,
        $summary.LatestRecoveryGapToKeyframeRequestMs,
        $summary.LatestRecoveryKeyframeRequestToOwnerEmitMs,
        $summary.LatestRecoveryOwnerEmitToFirstVisibleApplyMs,
        $summary.LatestRecoveryBurstControlFallbackCount,
        $summary.LatestRecoveryBurstCompletedCount,
        $summary.LatestRecoveryBurstTimeoutCount,
        $summary.LatestRecoveryBurstRestartSuppressedCount,
        $summary.LatestRecoveryBurstEncoderRerequestCount,
        $summary.LatestRecoveryOwnerPendingForcedResetCount,
        $summary.LatestRecoveryKeyframeEmittedAfterForcedResetCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_decode_worker max_pending_encoded_depth={0} max_pending_decoded_depth={1}; avg_enqueue_to_decode_start_ms={2} avg_enqueue_to_drop_ms={3}; queue_overflow={4} age_budget={5} generation_changed={6} stopped={7}" -f `
        $summary.LatestHelperMaxPendingEncodedDepth,
        $summary.LatestHelperMaxPendingDecodedDepth,
        $summary.LatestHelperAvgEnqueueToDecodeStartMs,
        $summary.LatestHelperAvgEnqueueToDropMs,
        $summary.LatestHelperDecodeWorkerDropQueueOverflowCount,
        $summary.LatestHelperDecodeWorkerDropAgeBudgetCount,
        $summary.LatestHelperDecodeWorkerDropGenerationCount,
        $summary.LatestHelperDecodeWorkerDropStoppedCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_frame_loss reassembler_loss_count={0} enqueue_reject_count={1} decode_worker_drop_count={2} post_decode_drop_count={3} decoded_frame_replaced_before_apply_count={4} stale_dropped_after_decode_count={5} dropped_waiting_for_recovery_keyframe_count={6} waiting_before_runway_count={7} runway_overflow_reject_count={8} suppressed_emit_during_recovery_wait_count={9} stale_superseded_recovery_suppressed_count={10} soft_stale_cleanup_count={11} pre_candidate_gap_tail_emitted_to_viewer_count={12} gap_non_key_pruned_count={13} future_tail_quarantined_during_gap_count={14} future_tail_quarantined_after_gap_count={15} pre_candidate_gap_tail_rejected_count={16} recovery_candidate_present_count={17} visible_recovery_floor_frame_id={18} stable_visible_head_frame_id={19} applied_head_frame_id={20} visible_head_frame_id={21} ordered_emit_head_frame_id={22} winning_recovery_frame_id={23} recovery_owner_replaced_count={24} late_fragment_after_applied_head_count={25} late_fragment_after_ordered_head_count={26} late_fragment_after_stable_visible_head_count={27} late_fragment_after_visible_recovery_count={28} actionable_late_fragment_count={29} runway_buffered_count={30} runway_applied_count={31} runway_abort_count={32} recovery_follower_window_buffered_count={33} recovery_follower_window_applied_count={34} recovery_follower_window_trimmed_count={35} recovery_progress_corridor_count={36} recovery_progress_corridor_success_count={37} recovery_progress_corridor_abort_count={38} recovery_progress_corridor_applied_count={39} recovery_keyframe_resync_count={40} gap_active={41} gap_expected_frame_id={42} buffered_recovery_keyframe_frame_id={43} future_non_key_buffered_count={44} post_recovery_visible_generation_reset_count={45} post_recovery_purged_pre_recovery_follower_count={46} post_recovery_stale_drop_bypass_count={47} unattributed_loss_count={48}; recent_losses={49}" -f `
        $summary.LatestHelperReassemblerLossCount,
        $summary.LatestHelperEnqueueRejectCount,
        $summary.LatestHelperDecodeWorkerDropCount,
        $summary.LatestHelperPostDecodeDropCount,
        $summary.LatestHelperDecodedFrameReplacedBeforeApplyCount,
        $summary.LatestHelperStaleDroppedAfterDecodeCount,
        $summary.LatestHelperDroppedWaitingForRecoveryKeyframeCount,
        $summary.LatestHelperRecoveryWaitRejectBeforeRunwayCount,
        $summary.LatestHelperRecoveryRunwayOverflowRejectCount,
        $summary.LatestHelperSuppressedEmitDuringRecoveryWaitCount,
        $summary.LatestHelperStaleSupersededRecoverySuppressedCount,
        $summary.LatestHelperSoftStaleCleanupCount,
        $summary.LatestHelperPreCandidateGapTailEmittedToViewerCount,
        $summary.LatestHelperGapNonKeyPrunedCount,
        $summary.LatestHelperFutureTailQuarantinedDuringGapCount,
        $summary.LatestHelperFutureTailQuarantinedAfterGapCount,
        $summary.LatestHelperPreCandidateGapTailRejectedCount,
        $summary.LatestHelperRecoveryCandidatePresentCount,
        $summary.LatestHelperVisibleRecoveryFloorFrameId,
        $summary.LatestHelperStableVisibleHeadFrameId,
        $summary.LatestHelperAppliedHeadFrameId,
        $summary.LatestHelperVisibleHeadFrameId,
        $summary.LatestHelperOrderedEmitHeadFrameId,
        $summary.LatestHelperWinningRecoveryFrameId,
        $summary.LatestHelperRecoveryOwnerReplacedCount,
        $summary.LatestHelperLateFragmentAfterAppliedHeadCount,
        $summary.LatestHelperLateFragmentAfterOrderedHeadCount,
        $summary.LatestHelperLateFragmentAfterStableVisibleHeadCount,
        $summary.LatestHelperLateFragmentAfterVisibleRecoveryCount,
        $summary.LatestHelperActionableLateFragmentCount,
        $summary.LatestHelperRecoveryRunwayContiguousFollowerBufferCount,
        $summary.LatestHelperRecoveryRunwayContiguousFollowerApplyCount,
        $summary.LatestHelperRecoveryRunwayAbortCount,
        $summary.LatestHelperRecoveryFollowerWindowBufferedCount,
        $summary.LatestHelperRecoveryFollowerWindowAppliedCount,
        $summary.LatestHelperRecoveryFollowerWindowTrimmedCount,
        $summary.LatestHelperRecoveryProgressCorridorCount,
        $summary.LatestHelperRecoveryProgressCorridorSuccessCount,
        $summary.LatestHelperRecoveryProgressCorridorAbortCount,
        $summary.LatestHelperRecoveryProgressCorridorAppliedCount,
        $summary.LatestHelperRecoveryKeyframeResyncCount,
        $summary.LatestHelperGapActive,
        $summary.LatestHelperGapExpectedFrameId,
        $summary.LatestHelperBufferedRecoveryKeyframeFrameId,
        $summary.LatestHelperFutureNonKeyBufferedCount,
        $summary.LatestHelperPostRecoveryVisibleGenerationResetCount,
        $summary.LatestHelperPostRecoveryPurgedPreRecoveryFollowerCount,
        $summary.LatestHelperPostRecoveryStaleDropBypassCount,
        $summary.LatestHelperUnattributedLossCount,
        ($(if ([string]::IsNullOrWhiteSpace($summary.LatestHelperRecentLosses)) { '(none)' } else { $summary.LatestHelperRecentLosses }))) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_quality visible_apply_ratio={0} gap_count={1} recovery_keyframe_apply_count={2} resync_count={3}; dominant_reassembler_root_cause={4}; dominant_helper_admission_reject_reason={5}; dominant_helper_pressure_blocker={6}; baseline_capture_to_render_ms={7}; age_excess_ms={8}; progress_stall_ms={9}; baseline_reseed_in_progress={10}; age_pressure_consecutive_count={11}; cadence_pressure_consecutive_count={12}; catch_up_suppressed_due_to_progress_count={13}; baseline_frozen_due_to_stall_count={14}; baseline_reseed_after_recovery_count={15}; cadence_stall_window_count={16}; cadence_stall_trigger_count={17}; high_frame_age_suppressed_due_to_visible_progress_count={18}; high_frame_age_suppressed_due_to_head_advance_count={19}; actionable_high_frame_age_count={20}; post_recovery_high_frame_age_suppressed_ticks={21}; recovery_progress_corridor_count={22}; recovery_progress_corridor_success_count={23}; recovery_progress_corridor_abort_count={24}; recovery_progress_corridor_applied_count={25}; recovery_candidate_present_count={26}; visible_recovery_floor_frame_id={27}; stable_visible_head_frame_id={28}; applied_head_frame_id={29}; visible_head_frame_id={30}; ordered_emit_head_frame_id={31}; winning_recovery_frame_id={32}; recovery_owner_replaced_count={33}; steady_visible_progress_active={34}; frames_applied_since_last_gap={35}; pre_candidate_gap_tail_emitted_to_viewer_count={36}; late_fragment_after_applied_head_count={37}; late_fragment_after_ordered_head_count={38}; late_fragment_after_stable_visible_head_count={39}; late_fragment_after_visible_recovery_count={40}; actionable_late_fragment_count={41}; suppressed_emit_during_recovery_wait_count={42}; stale_superseded_recovery_suppressed_count={43}; soft_stale_cleanup_count={44}; pre_candidate_gap_tail_rejected_count={45}" -f `
        $summary.LatestHelperVisibleApplyRatio,
        $summary.LatestHelperGapCount,
        $summary.LatestHelperRecoveryKeyframeApplyCount,
        $summary.LatestHelperResyncCount,
        $summary.DominantReassemblerRootCause,
        $summary.LatestHelperDominantAdmissionRejectReason,
        $summary.DominantHelperPressureBlocker,
        $summary.LatestHelperBaselineCaptureToRenderMs,
        $summary.LatestHelperAgeExcessMs,
        $summary.LatestHelperProgressStallMs,
        $summary.LatestHelperBaselineReseedInProgress,
        $summary.LatestHelperAgePressureConsecutiveCount,
        $summary.LatestHelperCadencePressureConsecutiveCount,
        $summary.LatestHelperCatchUpSuppressedDueToProgressCount,
        $summary.LatestHelperBaselineFrozenDueToStallCount,
        $summary.LatestHelperBaselineReseedAfterRecoveryCount,
        $summary.LatestHelperCadenceStallWindowCount,
        $summary.LatestHelperCadenceStallTriggerCount,
        $summary.AggregateHighFrameAgeSuppressedDueToVisibleProgressCount,
        $summary.LatestHelperHighFrameAgeSuppressedDueToHeadAdvanceCount,
        $summary.LatestHelperActionableHighFrameAgeCount,
        $summary.AggregatePostRecoveryHighFrameAgeSuppressedTicks,
        $summary.LatestHelperRecoveryProgressCorridorCount,
        $summary.LatestHelperRecoveryProgressCorridorSuccessCount,
        $summary.LatestHelperRecoveryProgressCorridorAbortCount,
        $summary.LatestHelperRecoveryProgressCorridorAppliedCount,
        $summary.LatestHelperRecoveryCandidatePresentCount,
        $summary.LatestHelperVisibleRecoveryFloorFrameId,
        $summary.LatestHelperStableVisibleHeadFrameId,
        $summary.LatestHelperAppliedHeadFrameId,
        $summary.LatestHelperVisibleHeadFrameId,
        $summary.LatestHelperOrderedEmitHeadFrameId,
        $summary.LatestHelperWinningRecoveryFrameId,
        $summary.LatestHelperRecoveryOwnerReplacedCount,
        $summary.LatestHelperSteadyVisibleProgressActive,
        $summary.LatestHelperFramesAppliedSinceLastGap,
        $summary.LatestHelperPreCandidateGapTailEmittedToViewerCount,
        $summary.LatestHelperLateFragmentAfterAppliedHeadCount,
        $summary.LatestHelperLateFragmentAfterOrderedHeadCount,
        $summary.LatestHelperLateFragmentAfterStableVisibleHeadCount,
        $summary.LatestHelperLateFragmentAfterVisibleRecoveryCount,
        $summary.LatestHelperActionableLateFragmentCount,
        $summary.LatestHelperSuppressedEmitDuringRecoveryWaitCount,
        $summary.LatestHelperStaleSupersededRecoverySuppressedCount,
        $summary.LatestHelperSoftStaleCleanupCount,
        $summary.LatestHelperPreCandidateGapTailRejectedCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_visible_progress steady_active={0}; activation_frame_id={1}; stable_visible_head_frame_id={2}; last_sent_stable_visible_head_frame_id={3}; frames_applied_since_last_gap={4}; steady_visible_progress_cleared_count={5}; steady_visible_progress_cleared_reason={6}; helper_visible_head_runtime_sender_mismatch={7}" -f `
        $summary.LatestHelperSteadyVisibleProgressActive,
        $summary.LatestHelperSteadyVisibleProgressActivationFrameId,
        $summary.LatestHelperStableVisibleHeadFrameId,
        $summary.LatestHelperLastSentStableVisibleHeadFrameId,
        $summary.LatestHelperFramesAppliedSinceLastGap,
        $summary.LatestHelperSteadyVisibleProgressClearedCount,
        $summary.LatestHelperSteadyVisibleProgressClearedReason,
        $summary.HelperVisibleHeadRuntimeSenderMismatch) -ForegroundColor Green
    Write-Host ("[NKN Soak] health sender_operating_state={0}; sender_guard_state={1}; helper_session_phase={2}; helper_recovery_mechanism={3}; dominant_loss_class={4}; dominant_pressure_blocker={5}; dominant_trouble_domain={6}" -f `
        $summary.LatestHealthSenderOperatingState,
        $summary.LatestHealthSenderGuardState,
        $summary.LatestHealthHelperSessionPhase,
        $summary.LatestHealthHelperRecoveryMechanism,
        $summary.LatestHealthDominantLossClass,
        $summary.LatestHealthDominantPressureBlocker,
        $summary.LatestHealthDominantTroubleDomain) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_startup_corridor recovery_keyframe_pending_visible_apply_count={0}; startup_corridor_buffered_follower_count={1}; startup_corridor_release_count={2}; startup_corridor_abort_count={3}; startup_corridor_abort_reason={4}" -f `
        $summary.LatestHelperRecoveryKeyframePendingVisibleApplyCount,
        $summary.LatestHelperStartupCorridorBufferedFollowerCount,
        $summary.LatestHelperStartupCorridorReleaseCount,
        $summary.LatestHelperStartupCorridorAbortCount,
        $summary.LatestHelperStartupCorridorAbortReason) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_worst_epochs visible_apply_ratio stream_epoch={0} ratio={1}; recovery_lock stream_epoch={2} time_ms={3}" -f `
        $summary.WorstEpochByVisibleApplyRatio,
        $summary.WorstEpochVisibleApplyRatio,
        $summary.WorstEpochByRecoveryLockTime,
        $summary.WorstEpochRecoveryLockTimeMs) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_decode_loss_buckets decode_queue_overflow_count={0} decode_age_budget_count={1} decode_generation_changed_count={2} decode_stopped_count={3} decoded_apply_queue_overflow_count={4}" -f `
        $summary.LatestHelperDecodeQueueOverflowCount,
        $summary.LatestHelperDecodeAgeBudgetCount,
        $summary.LatestHelperDecodeGenerationChangedCount,
        $summary.LatestHelperDecodeStoppedCount,
        $summary.LatestHelperDecodedApplyQueueOverflowCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_decode_outcomes need_more_input={0} completed_without_picture={1}" -f `
        $summary.LatestHelperNeedMoreInputCount,
        $summary.LatestHelperCompletedWithoutPictureCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] reduced_promotion blockers rate_gate={0} helper_pressure={1} helper_warmup={2} helper_apply_count={3} bridge_health={4} recovery_lock={5} queue_evict={6} capture_age={7} encode_budget={8} transition_grace={9}; soft_spikes={10}; soft_spike_resets_suppressed={11}; blocked_by_missing_helper_proof={12}; blocked_by_stale_helper_proof={13}; blocked_by_encode_budget={14}; blocked_by_encode_budget_alone={15}; reset_reasons={16}" -f `
        $summary.LatestPromotionBlockerRateGateTicks,
        $summary.LatestPromotionBlockerHelperPressureTicks,
        $summary.LatestPromotionBlockerHelperWarmupTicks,
        $summary.LatestPromotionBlockerHelperApplyCountTicks,
        $summary.LatestPromotionBlockerBridgeHealthTicks,
        $summary.LatestPromotionBlockerRecoveryLockTicks,
        $summary.LatestPromotionBlockerQueueEvictTicks,
        $summary.LatestPromotionBlockerCaptureAgeTicks,
        $summary.LatestPromotionBlockerEncodeBudgetTicks,
        $summary.LatestPromotionBlockerTransitionGraceTicks,
        $summary.LatestPromotionEncodeSoftSpikeCount,
        $summary.LatestPromotionEncodeSoftSpikeResetSuppressedCount,
        $summary.PromotionBlockedByMissingHelperProofCount,
        $summary.PromotionBlockedByStaleHelperProofCount,
        $summary.PromotionBlockedByEncodeBudgetCount,
        $summary.PromotionBlockedByEncodeBudgetAloneCount,
        ($(if ([string]::IsNullOrWhiteSpace($summary.LatestHealthyTickResetReasonCounts)) { '(none)' } else { $summary.LatestHealthyTickResetReasonCounts }))) -ForegroundColor Green
    Write-Host ("[NKN Soak] sender_cadence frames_deferred_to_send_slot={0} frames_replaced_before_send_slot={1} frames_dropped_by_queue_evict={2} send_slot_empty_count={3} slot_coalescing_active={4}; promotion_rate_gate_ticks={5} source_frames_queued={6}" -f `
        $summary.LatestFramesDeferredToSendSlot,
        $summary.LatestFramesReplacedBeforeSendSlot,
        $summary.LatestFramesDroppedByQueueEvict,
        $summary.LatestSendSlotEmptyCount,
        $summary.LatestSlotCoalescingActive,
        $summary.LatestPromotionBlockerRateGateTicks,
        $summary.LatestFramesQueued) -ForegroundColor Green
    Write-Host ("[NKN Soak] raw_cadence raw_frames_deferred_to_encode_slot={0} raw_frames_replaced_before_encode_slot={1} raw_encode_slot_empty_count={2} raw_slot_coalescing_active={3}; source_superseded_pending_frames={4}; promotion_capture_to_send_budget_ms={5}" -f `
        $summary.LatestRawFramesDeferredToEncodeSlot,
        $summary.LatestRawFramesReplacedBeforeEncodeSlot,
        $summary.LatestRawEncodeSlotEmptyCount,
        $summary.LatestRawSlotCoalescingActive,
        $summary.LatestSourceSupersededPendingFrames,
        $summary.LatestPromotionCaptureToSendBudgetMs) -ForegroundColor Green
    Write-Host ("[NKN Soak] ordinary_freshness_boundary raw_loss_count={0} sender_loss_count={1} helper_loss_count={2}; dominant_boundary={3}" -f `
        $summary.LatestOrdinaryRawLossCount,
        $summary.LatestOrdinarySenderLossCount,
        $summary.LatestOrdinaryHelperLossCount,
        $summary.DominantOrdinaryFreshnessLossBoundary) -ForegroundColor Green
    Write-Host ("[NKN Soak] reduced_promotion recent_entries={0}" -f `
        ($(if ([string]::IsNullOrWhiteSpace($summary.LatestReducedPromotionRecentEntries)) { '(none)' } else { $summary.LatestReducedPromotionRecentEntries }))) -ForegroundColor Green
    Write-Host ("[NKN Soak] encoder_path summaries: persistent_transform={0} sink_writer_fallback={1}" -f `
        $summary.PersistentSummaryCount,
        $summary.SinkWriterSummaryCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] sender_mode summaries: normal={0} reduced={1} catch_up={2}; bridge_health advisory={3} actionable={4}" -f `
        $summary.NormalModeSummaryCount,
        $summary.ReducedModeSummaryCount,
        $summary.CatchUpModeSummaryCount,
        $summary.BridgeHealthAdvisorySummaryCount,
        $summary.BridgeHealthActionableSummaryCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] transport_shape frames_queued={0} avg_fragments_per_frame={1} avg_payloads_per_frame={2}; batched_payloads={3} legacy_fragment_payloads={4}; ordinary_non_key_batched={5} ordinary_non_key_legacy={6} keyframe_recovery_batched={7}" -f `
        $summary.LatestFramesQueued,
        $summary.LatestAvgFragmentsPerFrame,
        $summary.LatestAvgPayloadsPerFrame,
        $summary.LatestBatchPayloadCount,
        $summary.LatestLegacyPayloadCount,
        $summary.LatestOrdinaryNonKeyBatchedPayloadCount,
        $summary.LatestOrdinaryNonKeyLegacyPayloadCount,
        $summary.LatestKeyframeRecoveryBatchedPayloadCount) -ForegroundColor Green
    Write-Host ("[NKN Soak] transport_mode effective_media_plane_active={0}; recovery_used_control_fallback={1}; steady_state_used_control_fallback={2}; bridge_media_messages_received={3}; media_plane_frames_sent={4}; media_plane_attached={5}" -f `
        $summary.EffectiveMediaPlaneActive,
        $summary.RecoveryUsedControlFallback,
        $summary.SteadyStateUsedControlFallback,
        $summary.LatestBridgeMediaMessagesReceived,
        $summary.LatestMediaPlaneFramesSent,
        $summary.LatestMediaPlaneAttached) -ForegroundColor Green
    Write-Host ("[NKN Soak] encoder_output displayable={0} non_displayable={1} idr_frames={2} p_frames={3} dropped_b_frames={4} dropped_multi_picture_units={5}; ratio={6}; idr_ratio={7}; avg_encoded_frame_bytes={8}; transport_ip_only_mode={9}; last_access_unit_kind={10}; low_delay_config_applied={11}" -f `
        $summary.LatestEmittedDisplayableFrames,
        $summary.LatestEmittedNonDisplayableUnits,
        $summary.LatestEmittedIdrFrames,
        $summary.LatestEmittedPFrames,
        $summary.LatestDroppedBFrames,
        $summary.LatestDroppedMultiPictureUnits,
        $summary.LatestDisplayableFrameRatio,
        $summary.LatestIdrFrameRatio,
        $summary.LatestAverageEncodedFrameBytes,
        $summary.LatestTransportIpOnlyMode,
        ($(if ([string]::IsNullOrWhiteSpace($summary.LatestLastAccessUnitKind)) { '(none)' } else { $summary.LatestLastAccessUnitKind })),
        ($(if ([string]::IsNullOrWhiteSpace($summary.LatestLowDelayConfigApplied)) { '(none)' } else { $summary.LatestLowDelayConfigApplied }))) -ForegroundColor Green
    Write-Host ("[NKN Soak] helper_bootstrap run_id={0} listener_generation={1}" -f `
        ($(if ([string]::IsNullOrWhiteSpace($summary.LatestHelperRunId)) { '(none)' } else { $summary.LatestHelperRunId })),
        $summary.LatestHelperListenerGeneration) -ForegroundColor DarkGray
    Write-Host ("[NKN Soak] baseline_compare strong_artifact={0}; safe_artifact={1}; current_latency_proxy_name={2}; current_latency_proxy_ms={3}; safe_latency_proxy_ms={4}; current_reassembler_loss_count={5}; safe_reassembler_loss_count={6}" -f `
        ($(if ($null -ne $strongBaselineMetrics) { $strongBaselineMetrics.artifact_dir } else { '(missing)' })),
        ($(if ($null -ne $safeBaselineMetrics) { $safeBaselineMetrics.artifact_dir } else { '(missing)' })),
        $currentComparisonMetrics.latency_proxy_name,
        ($(if ($null -ne $currentComparisonMetrics.latency_proxy_ms) { $currentComparisonMetrics.latency_proxy_ms.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })),
        ($(if ($null -ne $safeBaselineMetrics -and $null -ne $safeBaselineMetrics.latency_proxy_ms) { $safeBaselineMetrics.latency_proxy_ms.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })),
        ($(if ($null -ne $currentComparisonMetrics.reassembler_loss_count) { $currentComparisonMetrics.reassembler_loss_count.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })),
        ($(if ($null -ne $safeBaselineMetrics -and $null -ne $safeBaselineMetrics.reassembler_loss_count) { $safeBaselineMetrics.reassembler_loss_count.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' }))) -ForegroundColor Green
    Write-Host ("[NKN Soak] behavior_first_gate status={0}; invariant_failures={1}; regression_failures={2}; skip_gate={3}" -f `
        $stabilizationArtifacts.GateStatus,
        $stabilizationArtifacts.InvariantFailures.Count,
        $stabilizationArtifacts.RegressionFailures.Count,
        ($(if ($SkipBehaviorFirstGate.IsPresent) { 1 } else { 0 }))) -ForegroundColor $(if ($stabilizationArtifacts.GateStatus -eq 'pass') { 'Green' } else { 'Yellow' })
    Write-Host ("[NKN Soak] Artifacts: {0}" -f $soakArtifactDir) -ForegroundColor DarkGray
    Write-Host ("[NKN Soak] Log: {0}" -f $summary.LogPath) -ForegroundColor DarkGray

    $terminalFailures = New-Object System.Collections.Generic.List[string]
    if ($guiHarnessExitCode -ne 0) {
        $terminalFailures.Add("GUI soak harness exited with code $guiHarnessExitCode")
    }

    if ($stabilizationArtifacts.GateStatus -ne 'pass' -and -not $SkipBehaviorFirstGate.IsPresent) {
        $gateFailureDetail = @($stabilizationArtifacts.InvariantFailures + $stabilizationArtifacts.RegressionFailures) -join '; '
        $terminalFailures.Add(("behavior-first gate failed: {0}" -f $(if ([string]::IsNullOrWhiteSpace($gateFailureDetail)) { 'see stability-gates-summary.txt' } else { $gateFailureDetail })))
    }

    if ($terminalFailures.Count -gt 0) {
        throw ("{0}. Diagnostics were still collected at {1}." -f ($terminalFailures -join '; '), $soakArtifactDir)
    }
}
finally {
    Stop-NLinkProcesses -ResolvedExePath $resolvedExePath
    $env:NLINK_GUI_SMOKE_SCENARIOS = $previousScenarioEnv
    if ($null -eq $previousTransportEnv) {
        Remove-Item Env:NLINK_TRANSPORT -ErrorAction SilentlyContinue
    }
    else {
        $env:NLINK_TRANSPORT = $previousTransportEnv
    }

    if ($null -eq $previousDurationEnv) {
        Remove-Item Env:NLINK_SCREENSHARE_SOAK_SECONDS -ErrorAction SilentlyContinue
    }
    else {
        $env:NLINK_SCREENSHARE_SOAK_SECONDS = $previousDurationEnv
    }

    if ($null -eq $previousUnsafeDeveloperModeEnv) {
        Remove-Item Env:NLINK_UNSAFE_DEVELOPER_MODE -ErrorAction SilentlyContinue
    }
    else {
        $env:NLINK_UNSAFE_DEVELOPER_MODE = $previousUnsafeDeveloperModeEnv
    }
}
