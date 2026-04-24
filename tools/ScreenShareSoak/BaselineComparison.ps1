function Get-CurrentSoakComparisonMetrics {
    param([Parameter(Mandatory = $true)]$Summary)

    $latencyProxyName = 'helper_apply_ms_avg'
    $latencyProxyValue = if ($Summary.HelperApplyAvgMs -ge 0) { [double]$Summary.HelperApplyAvgMs } else { $null }
    if ($null -eq $latencyProxyValue -and $Summary.LatestHelperBaselineCaptureToRenderMs -ge 0) {
        $latencyProxyName = 'baseline_capture_to_render_ms'
        $latencyProxyValue = [double]$Summary.LatestHelperBaselineCaptureToRenderMs
    }

    return @{
        artifact_dir = ''
        visible_apply_ratio = if ($Summary.LatestHelperVisibleApplyRatio -ge 0) { [double]$Summary.LatestHelperVisibleApplyRatio } else { $null }
        helper_apply_ms_avg = if ($Summary.HelperApplyAvgMs -ge 0) { [double]$Summary.HelperApplyAvgMs } else { $null }
        helper_apply_ms_p95 = if ($Summary.HelperApplyP95Ms -ge 0) { [double]$Summary.HelperApplyP95Ms } else { $null }
        baseline_capture_to_render_ms = if ($Summary.LatestHelperBaselineCaptureToRenderMs -ge 0) { [double]$Summary.LatestHelperBaselineCaptureToRenderMs } else { $null }
        reassembler_loss_count = if ($Summary.LatestHelperReassemblerLossCount -ge 0) { [double]$Summary.LatestHelperReassemblerLossCount } else { $null }
        gap_count = if ($Summary.LatestHelperGapCount -ge 0) { [double]$Summary.LatestHelperGapCount } else { $null }
        resync_count = if ($Summary.LatestHelperResyncCount -ge 0) { [double]$Summary.LatestHelperResyncCount } else { $null }
        recovery_runway_overflow_reject_count = if ($Summary.LatestHelperRecoveryRunwayOverflowRejectCount -ge 0) { [double]$Summary.LatestHelperRecoveryRunwayOverflowRejectCount } else { $null }
        actionable_late_fragment_count = if ($Summary.LatestHelperActionableLateFragmentCount -ge 0) { [double]$Summary.LatestHelperActionableLateFragmentCount } else { $null }
        latency_proxy_name = $latencyProxyName
        latency_proxy_ms = $latencyProxyValue
    }
}

function Get-BaselineSoakComparisonMetrics {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    if (-not (Test-Path $ArtifactDir)) {
        return $null
    }

    $qualitySummary = Read-KeyValueSummaryFile -Path (Join-Path $ArtifactDir 'helper-quality-summary.txt')
    $frameLossSummary = Read-KeyValueSummaryFile -Path (Join-Path $ArtifactDir 'helper-frame-loss-epoch.txt')
    $rootCauseSummary = Read-KeyValueSummaryFile -Path (Join-Path $ArtifactDir 'helper-reassembler-root-cause-summary.txt')

    $latencyProxyName = $null
    $latencyProxyValue = Get-SummaryNumberValue -Values $qualitySummary -Keys @('helper_apply_ms_avg')
    if ($null -ne $latencyProxyValue) {
        $latencyProxyName = 'helper_apply_ms_avg'
    }
    else {
        $latencyProxyValue = Get-SummaryNumberValue -Values $qualitySummary -Keys @('avg_capture_to_render_ms', 'baseline_capture_to_render_ms')
        if ($null -ne $latencyProxyValue) {
            $latencyProxyName = if ($qualitySummary.ContainsKey('avg_capture_to_render_ms')) {
                'avg_capture_to_render_ms'
            }
            else {
                'baseline_capture_to_render_ms'
            }
        }
    }

    $reassemblerLossCount = Get-SummaryNumberValue -Values $qualitySummary -Keys @('reassembler_loss_count')
    if ($null -eq $reassemblerLossCount) {
        $reassemblerLossCount = Get-SummaryNumberValue -Values $frameLossSummary -Keys @('reassembler_loss_count')
    }
    if ($null -eq $reassemblerLossCount) {
        $reassemblerLossCount = Get-SummaryNumberValue -Values $rootCauseSummary -Keys @('reassembler_loss_count')
    }

    $actionableLateFragmentCount = Get-SummaryNumberValue -Values $qualitySummary -Keys @('actionable_late_fragment_count')
    if ($null -eq $actionableLateFragmentCount) {
        $actionableLateFragmentCount = Get-SummaryNumberValue -Values $rootCauseSummary -Keys @('actionable_late_fragment_count')
    }

    return @{
        artifact_dir = $ArtifactDir
        visible_apply_ratio = Get-SummaryNumberValue -Values $qualitySummary -Keys @('visible_apply_ratio')
        helper_apply_ms_avg = Get-SummaryNumberValue -Values $qualitySummary -Keys @('helper_apply_ms_avg')
        helper_apply_ms_p95 = Get-SummaryNumberValue -Values $qualitySummary -Keys @('helper_apply_ms_p95')
        reassembler_loss_count = $reassemblerLossCount
        gap_count = Get-SummaryNumberValue -Values $qualitySummary -Keys @('gap_count')
        resync_count = Get-SummaryNumberValue -Values $qualitySummary -Keys @('resync_count')
        recovery_runway_overflow_reject_count = Get-SummaryNumberValue -Values $qualitySummary -Keys @('recovery_runway_overflow_reject_count')
        actionable_late_fragment_count = $actionableLateFragmentCount
        latency_proxy_name = $latencyProxyName
        latency_proxy_ms = $latencyProxyValue
    }
}

function New-BaselineComparisonReport {
    param(
        [string]$Label,
        $CurrentMetrics,
        $BaselineMetrics
    )

    $lines = New-Object System.Collections.Generic.List[string]
    if ($null -eq $BaselineMetrics) {
        $lines.Add(("{0}_baseline_available=0" -f $Label))
        return $lines
    }

    $lines.Add(("{0}_baseline_available=1" -f $Label))
    $lines.Add(("{0}_baseline_artifact_dir={1}" -f $Label, $BaselineMetrics.artifact_dir))
    foreach ($metricName in @(
            'visible_apply_ratio',
            'helper_apply_ms_avg',
            'helper_apply_ms_p95',
            'reassembler_loss_count',
            'gap_count',
            'resync_count',
            'recovery_runway_overflow_reject_count',
            'actionable_late_fragment_count')) {
        $currentValue = $CurrentMetrics[$metricName]
        $baselineValue = $BaselineMetrics[$metricName]
        $lines.Add(("{0}_{1}_current={2}" -f $Label, $metricName, $(if ($null -ne $currentValue) { $currentValue.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })))
        $lines.Add(("{0}_{1}_baseline={2}" -f $Label, $metricName, $(if ($null -ne $baselineValue) { $baselineValue.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })))
        if ($null -ne $currentValue -and $null -ne $baselineValue) {
            $delta = [math]::Round(($currentValue - $baselineValue), 3)
            $lines.Add(("{0}_{1}_delta={2}" -f $Label, $metricName, $delta.ToString([System.Globalization.CultureInfo]::InvariantCulture)))
        }
        else {
            $lines.Add(("{0}_{1}_delta=(none)" -f $Label, $metricName))
        }
    }

    $baselineLatencyMetricName = if ([string]::IsNullOrWhiteSpace($BaselineMetrics.latency_proxy_name)) { '(none)' } else { $BaselineMetrics.latency_proxy_name }
    $currentLatencyMetricKey = switch ($BaselineMetrics.latency_proxy_name) {
        'helper_apply_ms_avg' { 'helper_apply_ms_avg' }
        'avg_capture_to_render_ms' { 'baseline_capture_to_render_ms' }
        'baseline_capture_to_render_ms' { 'baseline_capture_to_render_ms' }
        default { 'latency_proxy_ms' }
    }
    $currentComparableLatency = if ([string]::Equals($currentLatencyMetricKey, 'latency_proxy_ms', [System.StringComparison]::OrdinalIgnoreCase)) {
        $CurrentMetrics.latency_proxy_ms
    }
    else {
        $CurrentMetrics[$currentLatencyMetricKey]
    }

    $lines.Add(("{0}_latency_proxy_name={1}" -f $Label, $baselineLatencyMetricName))
    $lines.Add(("{0}_latency_proxy_current_metric={1}" -f $Label, $currentLatencyMetricKey))
    $lines.Add(("{0}_latency_proxy_ms_baseline={1}" -f $Label, $(if ($null -ne $BaselineMetrics.latency_proxy_ms) { $BaselineMetrics.latency_proxy_ms.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })))
    $lines.Add(("{0}_latency_proxy_ms_current={1}" -f $Label, $(if ($null -ne $currentComparableLatency) { $currentComparableLatency.ToString([System.Globalization.CultureInfo]::InvariantCulture) } else { '(none)' })))
    if ($null -ne $currentComparableLatency -and $null -ne $BaselineMetrics.latency_proxy_ms) {
        $latencyDelta = [math]::Round(($currentComparableLatency - $BaselineMetrics.latency_proxy_ms), 3)
        $lines.Add(("{0}_latency_proxy_ms_delta={1}" -f $Label, $latencyDelta.ToString([System.Globalization.CultureInfo]::InvariantCulture)))
    }
    else {
        $lines.Add(("{0}_latency_proxy_ms_delta=(none)" -f $Label))
    }

    return $lines
}

function Get-TopNamedCount {
    param(
        [Parameter(Mandatory = $true)][object[]]$Candidates,
        [string]$DefaultValue = 'none'
    )

    if ($Candidates.Count -eq 0) {
        return $DefaultValue
    }

    $best = $Candidates |
        Sort-Object -Property @{ Expression = { [int64]$_.Count }; Descending = $true }, @{ Expression = { [string]$_.Name }; Descending = $false } |
        Select-Object -First 1

    if ($null -eq $best -or [int64]$best.Count -le 0) {
        return $DefaultValue
    }

    return [string]$best.Name
}
