Set-StrictMode -Version Latest

function Read-FileTransferKeyValueArtifact {
    param([Parameter(Mandatory = $true)][string]$Path)

    $values = @{}
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return $values
    }

    foreach ($line in [System.IO.File]::ReadLines($Path)) {
        $separator = $line.IndexOf('=')
        if ($separator -le 0) {
            continue
        }

        $key = $line.Substring(0, $separator).Trim()
        $value = $line.Substring($separator + 1).Trim()
        if (-not [string]::IsNullOrWhiteSpace($key)) {
            $values[$key] = $value
        }
    }

    return $values
}

function ConvertTo-FileTransferDouble {
    param(
        $Values,
        [Parameter(Mandatory = $true)][string]$Name,
        [double]$Default = 0
    )

    if ($null -eq $Values -or -not $Values.ContainsKey($Name)) {
        return $Default
    }

    $text = [string]$Values[$Name]
    if ([string]::IsNullOrWhiteSpace($text) -or $text -eq '(none)') {
        return $Default
    }

    $result = 0.0
    if ([double]::TryParse($text, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$result)) {
        return $result
    }

    return $Default
}

function Get-FileTransferBaselineProtocolVersion {
    param($Values)

    if ($null -eq $Values -or $Values.Count -eq 0) {
        return '(unknown)'
    }

    if ($Values.ContainsKey('data_protocol_version')) {
        $value = [string]$Values['data_protocol_version']
        if (-not [string]::IsNullOrWhiteSpace($value) -and $value -ne '(none)') {
            return $value
        }
    }

    if ($Values.ContainsKey('v4_batch_ratio') -or
        $Values.ContainsKey('v4_state_feedback_count') -or
        $Values.ContainsKey('v4_feedback_redundant_success_count')) {
        if ((ConvertTo-FileTransferDouble -Values $Values -Name 'v4_batch_ratio' -Default 0) -gt 0 -or
            (ConvertTo-FileTransferDouble -Values $Values -Name 'v4_state_feedback_count' -Default 0) -gt 0 -or
            (ConvertTo-FileTransferDouble -Values $Values -Name 'v4_feedback_redundant_success_count' -Default 0) -gt 0) {
            return '4'
        }
    }

    return '(unknown)'
}

function Test-FileTransferBaselineProtocolMismatch {
    param(
        $Current,
        $Baseline
    )

    $currentProtocol = Get-FileTransferBaselineProtocolVersion -Values $Current
    $baselineProtocol = Get-FileTransferBaselineProtocolVersion -Values $Baseline
    return $currentProtocol -ne '(unknown)' -and
        $baselineProtocol -ne '(unknown)' -and
        $currentProtocol -ne $baselineProtocol
}

function Get-FileTransferProtocolBatchRatioKey {
    param([string]$ProtocolVersion)

    return 'v4_batch_ratio'
}

function Add-FileTransferRegressionFinding {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$List,
        [Parameter(Mandatory = $true)][string]$Finding
    )

    if (-not [string]::IsNullOrWhiteSpace($Finding)) {
        $List.Add($Finding) | Out-Null
    }
}

function Compare-FileTransferSafeBaseline {
    param(
        [Parameter(Mandatory = $true)]$Current,
        [Parameter(Mandatory = $true)]$Baseline
    )

    $findings = New-Object System.Collections.Generic.List[string]
    $currentMode = if ($Current.ContainsKey('mode')) { [string]$Current['mode'] } else { '' }
    $isLocalImpaired = ($currentMode -eq 'local-impaired')
    $throughputGateEnabled = (-not $isLocalImpaired)
    $currentProtocol = Get-FileTransferBaselineProtocolVersion -Values $Current

    if (Test-FileTransferBaselineProtocolMismatch -Current $Current -Baseline $Baseline) {
        return @()
    }

    $currentAvg = ConvertTo-FileTransferDouble -Values $Current -Name 'average_goodput_bytes_per_second'
    $baselineAvg = ConvertTo-FileTransferDouble -Values $Baseline -Name 'average_goodput_bytes_per_second'
    if ($throughputGateEnabled -and $baselineAvg -gt 0 -and $currentAvg -lt ($baselineAvg * 0.75)) {
        Add-FileTransferRegressionFinding -List $findings -Finding (
            'average goodput regressed below 75% of safe baseline: current={0:F3}; baseline={1:F3}' -f $currentAvg, $baselineAvg)
    }

    $currentMin = ConvertTo-FileTransferDouble -Values $Current -Name 'min_goodput_bytes_per_second'
    $baselineMin = ConvertTo-FileTransferDouble -Values $Baseline -Name 'min_goodput_bytes_per_second'
    if ($throughputGateEnabled -and $baselineMin -gt 0 -and $currentMin -lt ($baselineMin * 0.65)) {
        Add-FileTransferRegressionFinding -List $findings -Finding (
            'minimum goodput regressed below 65% of safe baseline: current={0:F3}; baseline={1:F3}' -f $currentMin, $baselineMin)
    }

    $batchRatioKey = Get-FileTransferProtocolBatchRatioKey -ProtocolVersion $currentProtocol
    $currentBatchRatio = ConvertTo-FileTransferDouble -Values $Current -Name $batchRatioKey -Default -1
    $baselineBatchRatio = ConvertTo-FileTransferDouble -Values $Baseline -Name $batchRatioKey -Default -1
    if ($baselineBatchRatio -gt 0 -and $currentBatchRatio -ge 0 -and $currentBatchRatio -lt ($baselineBatchRatio * 0.80)) {
        $batchRatioLabel = if ($currentProtocol -eq '6') { 'V6' } elseif ($currentProtocol -eq '5') { 'V5' } elseif ($currentProtocol -eq '4') { 'V4' } else { 'file-transfer' }
        Add-FileTransferRegressionFinding -List $findings -Finding (
            '{0} batch ratio regressed below 80% of safe baseline: current={1:F3}; baseline={2:F3}' -f $batchRatioLabel, $currentBatchRatio, $baselineBatchRatio)
    }

    if ($currentProtocol -eq '6' -or $currentProtocol -eq '5' -or $currentProtocol -eq '4') {
        $currentPayloadFill = ConvertTo-FileTransferDouble -Values $Current -Name 'v4_average_bridge_payload_fill_percent' -Default -1
        $baselinePayloadFill = ConvertTo-FileTransferDouble -Values $Baseline -Name 'v4_average_bridge_payload_fill_percent' -Default -1
        if ($baselinePayloadFill -gt 0 -and $currentPayloadFill -ge 0 -and $currentPayloadFill -lt ($baselinePayloadFill * 0.80)) {
            $payloadFillLabel = if ($currentProtocol -eq '6') { 'V6' } elseif ($currentProtocol -eq '5') { 'V5' } else { 'V4' }
            Add-FileTransferRegressionFinding -List $findings -Finding (
                '{0} bridge payload fill regressed below 80% of safe baseline: current={1:F3}; baseline={2:F3}' -f $payloadFillLabel, $currentPayloadFill, $baselinePayloadFill)
        }
    }

    foreach ($counterName in @(
        'reorder_event_count',
        'request_timeout_count',
        'retry_requested_count',
        'bridge_bulk_queue_waiting_count',
        'bridge_bulk_queue_severe_count',
        'media_queue_drop_count',
        'media_send_failure_count',
        'media_queue_severe_count')) {
        if ($isLocalImpaired -and @('reorder_event_count', 'request_timeout_count', 'retry_requested_count') -contains $counterName) {
            continue
        }

        $currentCount = ConvertTo-FileTransferDouble -Values $Current -Name $counterName
        $baselineCount = ConvertTo-FileTransferDouble -Values $Baseline -Name $counterName
        $limit = if ($baselineCount -gt 0) { $baselineCount * 1.5 } else { 0 }
        if ($currentCount -gt $limit) {
            Add-FileTransferRegressionFinding -List $findings -Finding (
                '{0} exceeded safe baseline by more than 50%: current={1}; baseline={2}' -f $counterName, $currentCount, $baselineCount)
        }
    }

    foreach ($hardCounter in @(
        'payload_rejected_count',
        'decode_failure_count',
        'message_rejected_count',
        'bridge_bulk_send_failure_count',
        'bridge_bulk_queue_clear_count',
        'terminal_failure_count',
        'v4_feedback_both_failed_count',
        'v4_sender_failed_count',
        'v4_receiver_failed_count',
        'legacy_data_protocol_started_count',
        'unexpected_legacy_data_frame_during_v4_count')) {
        $currentHard = ConvertTo-FileTransferDouble -Values $Current -Name $hardCounter
        if ($currentHard -gt 0) {
            Add-FileTransferRegressionFinding -List $findings -Finding (
                '{0} is nonzero in current run: current={1}' -f $hardCounter, $currentHard)
        }
    }

    return $findings.ToArray()
}

function Resolve-FileTransferSummaryArtifactPath {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $liveSummary = Join-Path $ArtifactDir 'filetransfer-live-nkn-summary.txt'
    if (Test-Path -LiteralPath $liveSummary -PathType Leaf) {
        return $liveSummary
    }

    return (Join-Path $ArtifactDir 'filetransfer-local-soak-summary.txt')
}

function New-FileTransferBaselineComparisonLines {
    param(
        [Parameter(Mandatory = $true)]$Current,
        $SafeBaseline,
        $StrongBaseline,
        [string[]]$RegressionFindings = @()
    )

    $safeAvailable = if ($null -ne $SafeBaseline -and $SafeBaseline.Count -gt 0) { 1 } else { 0 }
    $strongAvailable = if ($null -ne $StrongBaseline -and $StrongBaseline.Count -gt 0) { 1 } else { 0 }
    $currentProtocol = Get-FileTransferBaselineProtocolVersion -Values $Current
    $safeProtocol = Get-FileTransferBaselineProtocolVersion -Values $SafeBaseline
    $strongProtocol = Get-FileTransferBaselineProtocolVersion -Values $StrongBaseline
    $safeProtocolMismatch = if ($safeAvailable -eq 1 -and (Test-FileTransferBaselineProtocolMismatch -Current $Current -Baseline $SafeBaseline)) { 1 } else { 0 }
    $strongProtocolMismatch = if ($strongAvailable -eq 1 -and (Test-FileTransferBaselineProtocolMismatch -Current $Current -Baseline $StrongBaseline)) { 1 } else { 0 }
    $lines = New-Object System.Collections.Generic.List[string]
    $lines.Add(("safe_baseline_available={0}" -f $safeAvailable)) | Out-Null
    $lines.Add(("strong_baseline_available={0}" -f $strongAvailable)) | Out-Null
    $lines.Add(("baseline_protocol_mismatch={0}" -f $safeProtocolMismatch)) | Out-Null
    $lines.Add(("strong_baseline_protocol_mismatch={0}" -f $strongProtocolMismatch)) | Out-Null
    $lines.Add(("regression_failed={0}" -f ($(if ($RegressionFindings.Count -gt 0) { 1 } else { 0 })))) | Out-Null
    $lines.Add(("current_artifact_kind={0}" -f ($(if ($Current.ContainsKey('artifact_kind')) { $Current['artifact_kind'] } else { 'local' })))) | Out-Null
    $lines.Add(("current_verdict={0}" -f ($(if ($Current.ContainsKey('verdict')) { $Current['verdict'] } else { '(unknown)' })))) | Out-Null
    $lines.Add(("current_data_protocol_version={0}" -f $currentProtocol)) | Out-Null
    $lines.Add(("current_average_goodput_bytes_per_second={0}" -f (ConvertTo-FileTransferDouble -Values $Current -Name 'average_goodput_bytes_per_second'))) | Out-Null
    $lines.Add(("current_min_goodput_bytes_per_second={0}" -f (ConvertTo-FileTransferDouble -Values $Current -Name 'min_goodput_bytes_per_second'))) | Out-Null
    $lines.Add(("current_v4_batch_ratio={0}" -f ($(if ($Current.ContainsKey('v4_batch_ratio')) { $Current['v4_batch_ratio'] } else { '(none)' })))) | Out-Null
    $lines.Add(("current_v4_average_bridge_payload_fill_percent={0}" -f ($(if ($Current.ContainsKey('v4_average_bridge_payload_fill_percent')) { $Current['v4_average_bridge_payload_fill_percent'] } else { '(none)' })))) | Out-Null
    foreach ($counterName in @('reorder_event_count', 'request_timeout_count', 'retry_requested_count', 'bridge_bulk_queue_waiting_count', 'bridge_bulk_queue_severe_count', 'media_queue_drop_count', 'media_send_failure_count', 'media_queue_severe_count')) {
        $lines.Add(("current_{0}={1}" -f $counterName, (ConvertTo-FileTransferDouble -Values $Current -Name $counterName))) | Out-Null
    }

    if ($safeAvailable -eq 1) {
        $lines.Add(("safe_data_protocol_version={0}" -f $safeProtocol)) | Out-Null
        $lines.Add(("safe_average_goodput_bytes_per_second={0}" -f (ConvertTo-FileTransferDouble -Values $SafeBaseline -Name 'average_goodput_bytes_per_second'))) | Out-Null
        $lines.Add(("safe_min_goodput_bytes_per_second={0}" -f (ConvertTo-FileTransferDouble -Values $SafeBaseline -Name 'min_goodput_bytes_per_second'))) | Out-Null
        $lines.Add(("safe_v4_batch_ratio={0}" -f ($(if ($SafeBaseline.ContainsKey('v4_batch_ratio')) { $SafeBaseline['v4_batch_ratio'] } else { '(none)' })))) | Out-Null
        $lines.Add(("safe_v4_average_bridge_payload_fill_percent={0}" -f ($(if ($SafeBaseline.ContainsKey('v4_average_bridge_payload_fill_percent')) { $SafeBaseline['v4_average_bridge_payload_fill_percent'] } else { '(none)' })))) | Out-Null
        foreach ($counterName in @('reorder_event_count', 'request_timeout_count', 'retry_requested_count', 'bridge_bulk_queue_waiting_count', 'bridge_bulk_queue_severe_count', 'media_queue_drop_count', 'media_send_failure_count', 'media_queue_severe_count')) {
            $lines.Add(("safe_{0}={1}" -f $counterName, (ConvertTo-FileTransferDouble -Values $SafeBaseline -Name $counterName))) | Out-Null
        }
    }

    if ($strongAvailable -eq 1) {
        $lines.Add(("strong_data_protocol_version={0}" -f $strongProtocol)) | Out-Null
        $lines.Add(("strong_average_goodput_bytes_per_second={0}" -f (ConvertTo-FileTransferDouble -Values $StrongBaseline -Name 'average_goodput_bytes_per_second'))) | Out-Null
        $lines.Add(("strong_min_goodput_bytes_per_second={0}" -f (ConvertTo-FileTransferDouble -Values $StrongBaseline -Name 'min_goodput_bytes_per_second'))) | Out-Null
        $lines.Add(("strong_v4_batch_ratio={0}" -f ($(if ($StrongBaseline.ContainsKey('v4_batch_ratio')) { $StrongBaseline['v4_batch_ratio'] } else { '(none)' })))) | Out-Null
        $lines.Add(("strong_v4_average_bridge_payload_fill_percent={0}" -f ($(if ($StrongBaseline.ContainsKey('v4_average_bridge_payload_fill_percent')) { $StrongBaseline['v4_average_bridge_payload_fill_percent'] } else { '(none)' })))) | Out-Null
        foreach ($counterName in @('reorder_event_count', 'request_timeout_count', 'retry_requested_count', 'bridge_bulk_queue_waiting_count', 'bridge_bulk_queue_severe_count', 'media_queue_drop_count', 'media_send_failure_count', 'media_queue_severe_count')) {
            $lines.Add(("strong_{0}={1}" -f $counterName, (ConvertTo-FileTransferDouble -Values $StrongBaseline -Name $counterName))) | Out-Null
        }
    }

    $lines.Add('') | Out-Null
    $lines.Add('regression_findings:') | Out-Null
    if ($RegressionFindings.Count -gt 0) {
        foreach ($finding in $RegressionFindings) {
            $lines.Add($finding) | Out-Null
        }
    }
    else {
        $lines.Add('(none)') | Out-Null
    }

    return $lines.ToArray()
}

function Write-FileTransferBaselineComparison {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [string]$SafeBaselineArtifactDir = '',
        [string]$StrongBaselineArtifactDir = ''
    )

    $summaryPath = Resolve-FileTransferSummaryArtifactPath -ArtifactDir $ArtifactDir
    $current = Read-FileTransferKeyValueArtifact -Path $summaryPath

    $safeBaseline = @{}
    if (-not [string]::IsNullOrWhiteSpace($SafeBaselineArtifactDir)) {
        $safeBaseline = Read-FileTransferKeyValueArtifact -Path (Resolve-FileTransferSummaryArtifactPath -ArtifactDir $SafeBaselineArtifactDir)
    }

    $strongBaseline = @{}
    if (-not [string]::IsNullOrWhiteSpace($StrongBaselineArtifactDir)) {
        $strongBaseline = Read-FileTransferKeyValueArtifact -Path (Resolve-FileTransferSummaryArtifactPath -ArtifactDir $StrongBaselineArtifactDir)
    }

    $regressionFindings = @()
    if ($safeBaseline.Count -gt 0) {
        $regressionFindings = @(Compare-FileTransferSafeBaseline -Current $current -Baseline $safeBaseline)
    }

    $lines = New-FileTransferBaselineComparisonLines `
        -Current $current `
        -SafeBaseline $safeBaseline `
        -StrongBaseline $strongBaseline `
        -RegressionFindings $regressionFindings
    $path = Join-Path $ArtifactDir 'baseline-comparison.txt'
    $lines | Set-Content -LiteralPath $path -Encoding UTF8

    if (Get-Command -Name Write-FileTransferV4PromotionDecision -ErrorAction SilentlyContinue) {
        Write-FileTransferV4PromotionDecision `
            -ArtifactDir $ArtifactDir `
            -SafeBaselineArtifactDir $SafeBaselineArtifactDir
    }

    return [pscustomobject]@{
        Path = $path
        RegressionFailed = ($regressionFindings.Count -gt 0)
        RegressionFindings = @($regressionFindings)
    }
}

function Set-FileTransferRegressionVerdict {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [string]$TransferId = '(local-fast)',
        [string[]]$RegressionFindings = @()
    )

    $lines = @(
        'verdict=FAIL_REGRESSION_BUDGET',
        'gate_status=fail',
        ("transfer_id={0}" -f $TransferId),
        'next_artifact=baseline-comparison.txt',
        'hard_failure_count=0',
        ('warning_count={0}' -f $RegressionFindings.Count),
        '',
        'hard_failures:',
        '(none)',
        '',
        'warnings:'
    ) + ($(if ($RegressionFindings.Count -gt 0) { @($RegressionFindings) } else { @('(none)') })) + @(
        '',
        'top_evidence:',
        'baseline-comparison.txt'
    )

    $lines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-operator-verdict.txt') -Encoding UTF8
    $lines | Set-Content -LiteralPath (Join-Path $ArtifactDir 'stability-gates-summary.txt') -Encoding UTF8
}
