function Write-StabilizationArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)]$Summary,
        [Parameter(Mandatory = $true)]$CurrentMetrics,
        $StrongBaselineMetrics,
        $SafeBaselineMetrics
    )

    $invariantFailures = New-Object System.Collections.Generic.List[string]
    $regressionFailures = New-Object System.Collections.Generic.List[string]
    $latencyGateMetricName = [string]$CurrentMetrics.latency_proxy_name
    $latencyGateCurrentValue = $CurrentMetrics.latency_proxy_ms
    $latencyGateBaselineMetricName = if ($null -ne $SafeBaselineMetrics) { [string]$SafeBaselineMetrics.latency_proxy_name } else { '(none)' }
    $latencyGateBaselineValue = if ($null -ne $SafeBaselineMetrics) { $SafeBaselineMetrics.latency_proxy_ms } else { $null }

    if ($Summary.LatestRecoveryOwnerReplacedBeforeAckCount -gt 0) {
        $invariantFailures.Add(("recovery_owner_replaced_before_ack_count={0}" -f $Summary.LatestRecoveryOwnerReplacedBeforeAckCount))
    }

    if ($Summary.LatestHelperRecoveryRunwayOverflowRejectCount -gt 1) {
        $invariantFailures.Add(("recovery_runway_overflow_reject_count={0}" -f $Summary.LatestHelperRecoveryRunwayOverflowRejectCount))
    }

    if ($Summary.LatestHelperStartupCorridorReleaseCount -gt 0) {
        $invariantFailures.Add(("startup_corridor_release_count={0}" -f $Summary.LatestHelperStartupCorridorReleaseCount))
    }

    if ($Summary.LatestHelperRecoveryFollowerWindowBufferedCount -gt 0) {
        $invariantFailures.Add(("recovery_follower_window_buffered_count={0}" -f $Summary.LatestHelperRecoveryFollowerWindowBufferedCount))
    }

    if ($Summary.LatestRecoveryCompletionAccountingMismatch -gt 0) {
        $invariantFailures.Add(("recovery_completion_accounting_mismatch={0}" -f $Summary.LatestRecoveryCompletionAccountingMismatch))
    }

    if ($Summary.RecoveryControlBootstrapRetryQueuedAfterBurstResolutionCount -gt 0) {
        $invariantFailures.Add(("recovery_control_bootstrap_retry_queued_after_burst_resolution_count={0}" -f $Summary.RecoveryControlBootstrapRetryQueuedAfterBurstResolutionCount))
    }

    if ($Summary.LatestHelperBridgeHealthActionableWithoutQueueOrDropCount -gt 0) {
        $invariantFailures.Add(("bridge_health_became_actionable_without_queue_or_drop_count={0}" -f $Summary.LatestHelperBridgeHealthActionableWithoutQueueOrDropCount))
    }

    if (@(
            'late_fragment_after_applied_head',
            'late_fragment_after_ordered_head',
            'late_fragment_after_stable_visible_head',
            'superseded_recovery_tail_cleanup'
        ) -contains $Summary.DominantReassemblerRootCause) {
        $invariantFailures.Add(("dominant_reassembler_root_cause_benign={0}" -f $Summary.DominantReassemblerRootCause))
    }

    if ($null -ne $SafeBaselineMetrics) {
        $safeLatencyMetricKey = switch ($SafeBaselineMetrics.latency_proxy_name) {
            'helper_apply_ms_avg' { 'helper_apply_ms_avg' }
            'avg_capture_to_render_ms' { 'baseline_capture_to_render_ms' }
            'baseline_capture_to_render_ms' { 'baseline_capture_to_render_ms' }
            default { 'helper_apply_ms_avg' }
        }
        $currentComparableLatency = $CurrentMetrics[$safeLatencyMetricKey]
        $latencyGateMetricName = $safeLatencyMetricKey
        $latencyGateCurrentValue = $currentComparableLatency
        if ($null -ne $currentComparableLatency -and
            $null -ne $SafeBaselineMetrics.latency_proxy_ms -and
            $currentComparableLatency -gt $SafeBaselineMetrics.latency_proxy_ms) {
            $regressionFailures.Add(
                ("latency_proxy_regressed current_{0}={1} safe_baseline_{2}={3}" -f
                    $safeLatencyMetricKey,
                    $currentComparableLatency.ToString([System.Globalization.CultureInfo]::InvariantCulture),
                    $SafeBaselineMetrics.latency_proxy_name,
                    $SafeBaselineMetrics.latency_proxy_ms.ToString([System.Globalization.CultureInfo]::InvariantCulture)))
        }

        if ($null -ne $CurrentMetrics.reassembler_loss_count -and
            $null -ne $SafeBaselineMetrics.reassembler_loss_count -and
            $CurrentMetrics.reassembler_loss_count -gt $SafeBaselineMetrics.reassembler_loss_count) {
            $regressionFailures.Add(
                ("reassembler_loss_regressed current={0} safe_baseline={1}" -f
                    $CurrentMetrics.reassembler_loss_count.ToString([System.Globalization.CultureInfo]::InvariantCulture),
                    $SafeBaselineMetrics.reassembler_loss_count.ToString([System.Globalization.CultureInfo]::InvariantCulture)))
        }
    }

    if ($null -ne $CurrentMetrics.visible_apply_ratio -and
        $CurrentMetrics.visible_apply_ratio -lt 0.98) {
        $regressionFailures.Add(
            ("visible_apply_ratio_below_target current={0} target=0.98" -f
                $CurrentMetrics.visible_apply_ratio.ToString([System.Globalization.CultureInfo]::InvariantCulture)))
    }

    if ($null -ne $CurrentMetrics.helper_apply_ms_avg -and
        $CurrentMetrics.helper_apply_ms_avg -gt 550) {
        $regressionFailures.Add(
            ("helper_apply_ms_avg_above_target current={0} target=550" -f
                $CurrentMetrics.helper_apply_ms_avg.ToString([System.Globalization.CultureInfo]::InvariantCulture)))
    }

    if ($null -ne $CurrentMetrics.reassembler_loss_count -and
        $CurrentMetrics.reassembler_loss_count -gt 15) {
        $regressionFailures.Add(
            ("reassembler_loss_count_above_target current={0} target=15" -f
                $CurrentMetrics.reassembler_loss_count.ToString([System.Globalization.CultureInfo]::InvariantCulture)))
    }

    $comparisonLines = @()
    $comparisonLines += @(New-BaselineComparisonReport -Label 'strong' -CurrentMetrics $CurrentMetrics -BaselineMetrics $StrongBaselineMetrics)
    $comparisonLines += ''
    $comparisonLines += @(New-BaselineComparisonReport -Label 'safe' -CurrentMetrics $CurrentMetrics -BaselineMetrics $SafeBaselineMetrics)

    Set-Content -Path (Join-Path $ArtifactDir 'baseline-comparison.txt') -Value $comparisonLines

    $gateStatus = if ($invariantFailures.Count -eq 0 -and $regressionFailures.Count -eq 0) { 'pass' } else { 'fail' }
    $gateLines = @(
        ("behavior_first_gate_status={0}" -f $gateStatus),
        ("invariant_failure_count={0}" -f $invariantFailures.Count),
        ("regression_failure_count={0}" -f $regressionFailures.Count),
        ("latency_gate_metric_name={0}" -f $latencyGateMetricName),
        ("latency_gate_current_value={0}" -f $(if ($null -ne $latencyGateCurrentValue) { $latencyGateCurrentValue.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })),
        ("latency_gate_baseline_metric_name={0}" -f $latencyGateBaselineMetricName),
        ("latency_gate_baseline_value={0}" -f $(if ($null -ne $latencyGateBaselineValue) { $latencyGateBaselineValue.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })),
        ("current_latency_proxy_name={0}" -f $CurrentMetrics.latency_proxy_name),
        ("current_latency_proxy_ms={0}" -f $(if ($null -ne $CurrentMetrics.latency_proxy_ms) { $CurrentMetrics.latency_proxy_ms.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })),
        ("current_reassembler_loss_count={0}" -f $(if ($null -ne $CurrentMetrics.reassembler_loss_count) { $CurrentMetrics.reassembler_loss_count.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })),
        ("current_recovery_post_ack_hold_started_count={0}" -f $Summary.LatestRecoveryPostAckHoldStartedCount),
        ("current_recovery_post_ack_hold_expired_count={0}" -f $Summary.LatestRecoveryPostAckHoldExpiredCount),
        '',
        'invariant_failures:'
    ) + $(if ($invariantFailures.Count -gt 0) { @($invariantFailures.ToArray()) } else { @('none') }) + @(
        '',
        'regression_failures:'
    ) + $(if ($regressionFailures.Count -gt 0) { @($regressionFailures.ToArray()) } else { @('none') })

    $gateLines | Set-Content -Path (Join-Path $ArtifactDir 'stability-gates-summary.txt')

    return [pscustomobject]@{
        GateStatus = $gateStatus
        InvariantFailures = @($invariantFailures.ToArray())
        RegressionFailures = @($regressionFailures.ToArray())
    }
}
