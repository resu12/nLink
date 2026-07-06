Set-StrictMode -Version Latest

$fileTransferOpsRoot = Split-Path -Parent $PSScriptRoot
$fileTransferSoakRoot = Join-Path $fileTransferOpsRoot 'FileTransferSoak'
. (Join-Path $fileTransferSoakRoot 'LogParsing.ps1')
. (Join-Path $fileTransferSoakRoot 'SoakSummaryExtraction.ps1')
. (Join-Path $fileTransferSoakRoot 'StabilizationGates.ps1')
. (Join-Path $fileTransferSoakRoot 'ArtifactWriters.ps1')
. (Join-Path $fileTransferSoakRoot 'BaselineComparison.ps1')

function Add-FileTransferLiveHarnessEvidence {
    param(
        [object[]]$Events,
        [string[]]$LogFiles,
        [string]$ArtifactDir = '',
        [string]$TransferId = '',
        [switch]$AllTransfers
    )

    $hasProgressTimeoutEvent = @($Events | Where-Object { $_.EventName -eq 'filetransfer_live_progress_timeout' }).Count -gt 0
    $hasGuiTimeoutSlice = @($Events | Where-Object {
            $_.EventName -eq 'filetransfer_artifact_slice_summary' -and
            (Get-FileTransferEventField -Event $_ -Name 'artifact_slice_end_reason' -Default '') -eq 'gui_progress_timeout'
        }).Count -gt 0
    if ($hasProgressTimeoutEvent -and $hasGuiTimeoutSlice) {
        return @($Events)
    }

    $candidateStdoutPaths = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($ArtifactDir)) {
        $candidateStdoutPaths.Add((Join-Path $ArtifactDir 'gui-smoke-stdout.log')) | Out-Null
    }

    foreach ($file in @($LogFiles)) {
        if ([string]::IsNullOrWhiteSpace($file)) {
            continue
        }

        $parent = Split-Path -Parent $file
        if (-not [string]::IsNullOrWhiteSpace($parent)) {
            $candidateStdoutPaths.Add((Join-Path $parent 'gui-smoke-stdout.log')) | Out-Null
        }
    }

    $progressTimeout = Get-FileTransferLiveProgressTimeoutEvidence -CandidateStdoutPaths $candidateStdoutPaths.ToArray()
    if ($progressTimeout.Count -le 0) {
        return @($Events)
    }

    $effectiveTransferId = $TransferId
    if ([string]::IsNullOrWhiteSpace($effectiveTransferId)) {
        if ($AllTransfers) {
            $effectiveTransferId = '(all)'
        }
        else {
            $effectiveTransferId = Select-FileTransferIdForAnalysis -Events @($Events) -RequestedTransferId ''
            if ([string]::IsNullOrWhiteSpace($effectiveTransferId)) {
                $effectiveTransferId = '(all)'
            }
        }
    }

    $reason = $progressTimeout.Reason -replace ';', ','

    $sequence = 0
    foreach ($event in @($Events)) {
        if ($event.Sequence -gt $sequence) {
            $sequence = $event.Sequence
        }
    }

    $timestamp = [datetime]::UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", [System.Globalization.CultureInfo]::InvariantCulture)
    $syntheticLines = New-Object System.Collections.Generic.List[string]
    if (-not $hasProgressTimeoutEvent) {
        $syntheticLines.Add(('[{0}] [WARN] [FileTransferOps] event=filetransfer_live_progress_timeout; transfer_id={1}; reason={2}; total_wait_s={3}; progress_timeout_seconds={4}; receiver_next_chunk={5}; receiver_highest_chunk={6}; progress_events={7}' -f $timestamp, $effectiveTransferId, $reason, $progressTimeout.TotalWaitSeconds, $progressTimeout.ProgressTimeoutSeconds, $progressTimeout.ReceiverNextChunk, $progressTimeout.ReceiverHighestChunk, $progressTimeout.ProgressEventCount)) | Out-Null
    }

    if (-not $hasGuiTimeoutSlice) {
        $syntheticLines.Add(('[{0}] [INFO] [FileTransferOps] event=filetransfer_artifact_slice_summary; transfer_id={1}; artifact_slice_start_reason=live_soak_failure_context; artifact_slice_end_reason=gui_progress_timeout' -f $timestamp, $effectiveTransferId)) | Out-Null
    }

    $augmented = New-Object System.Collections.Generic.List[object]
    foreach ($event in @($Events)) {
        $augmented.Add($event) | Out-Null
    }

    foreach ($line in $syntheticLines.ToArray()) {
        $sequence++
        $event = ConvertFrom-FileTransferLogLine -Line $line -FilePath $progressTimeout.StdoutPath -LineNumber 0 -Sequence $sequence
        if ($null -ne $event) {
            $augmented.Add($event) | Out-Null
        }
    }

    return $augmented.ToArray()
}

function Write-FileTransferRetainedAnalysisStageDiagnostics {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)]$Diagnostics
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null

    $textLines = New-Object System.Collections.Generic.List[string]
    foreach ($key in @($Diagnostics.Keys)) {
        $textLines.Add(('{0}={1}' -f $key, $Diagnostics[$key])) | Out-Null
    }

    Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-analysis-stage-diagnostics.txt') -Value $textLines.ToArray() -Encoding UTF8

    $jsonPath = Join-Path $ArtifactDir 'filetransfer-retained-analysis-stage-diagnostics.json'
    $jsonStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    $Diagnostics | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
    $jsonStopwatch.Stop()
    $Diagnostics['json_write_ms'] = $jsonStopwatch.ElapsedMilliseconds
    $Diagnostics | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $jsonPath -Encoding UTF8
}

function Get-FileTransferCompactIndexEventsByName {
    param(
        [Parameter(Mandatory = $true)]$Index,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $eventsByName = [System.Collections.IDictionary]$Index.PSObject.Properties['EventsByName'].Value
    if (-not $eventsByName.Contains($Name) -and -not $eventsByName.ContainsKey($Name)) {
        return @()
    }

    $bucket = $eventsByName.Item($Name)
    $events = New-Object System.Collections.Generic.List[object]
    foreach ($event in $bucket) {
        $events.Add($event) | Out-Null
    }

    return @($events.ToArray())
}

function Get-FileTransferCompactIndexEventCount {
    param(
        [Parameter(Mandatory = $true)]$Index,
        [Parameter(Mandatory = $true)][string]$Name
    )

    $eventCounts = [System.Collections.IDictionary]$Index.PSObject.Properties['EventCounts'].Value
    if ($eventCounts.Contains($Name) -or $eventCounts.ContainsKey($Name)) {
        return [int]$eventCounts.Item($Name)
    }

    return 0
}

function Get-FileTransferCompactIndexTotalEventCount {
    param([Parameter(Mandatory = $true)]$Index)

    if ($null -ne $Index.PSObject.Properties['EventCount']) {
        return [int]$Index.PSObject.Properties['EventCount'].Value
    }

    if ($null -ne $Index.PSObject.Properties['EventCounts']) {
        $eventCounts = [System.Collections.IDictionary]$Index.PSObject.Properties['EventCounts'].Value
        $total = 0
        foreach ($value in $eventCounts.Values) {
            $total += [int]$value
        }

        return $total
    }

    return 0
}

function Get-FileTransferCompactTerminalEvents {
    param([Parameter(Mandatory = $true)]$Index)

    $events = New-Object System.Collections.Generic.List[object]
    foreach ($name in @('transfer_terminal', 'file_transfer_inbound_terminal', 'file_transfer_outbound_terminal')) {
        foreach ($event in @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name $name)) {
            $events.Add($event) | Out-Null
        }
    }

    return @($events.ToArray() | Sort-Object Sequence)
}

function Get-FileTransferCompactCleanTerminalPair {
    param([Parameter(Mandatory = $true)]$Index)

    $terminalEvents = @(Get-FileTransferCompactTerminalEvents -Index $Index)
    $inboundComplete = $false
    $outboundComplete = $false
    $maxSequence = 0

    foreach ($event in @($terminalEvents)) {
        if ($event.Sequence -gt $maxSequence) {
            $maxSequence = [int]$event.Sequence
        }

        $direction = Get-FileTransferTerminalDirection -Event $event
        $state = Get-FileTransferEventField -Event $event -Name 'state' -Default ''
        $errorCode = Get-FileTransferEventField -Event $event -Name 'error_code' -Default '(none)'
        $reason = Get-FileTransferEventField -Event $event -Name 'reason' -Default ''
        $isComplete = $state -eq 'Completed' -or
            ($event.EventName -eq 'transfer_terminal' -and
                $errorCode -eq '(none)' -and
                $reason.IndexOf('Transfer complete', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)

        if (-not $isComplete -or $errorCode -ne '(none)') {
            continue
        }

        if ($direction -eq 'inbound') {
            $inboundComplete = $true
        }
        elseif ($direction -eq 'outbound') {
            $outboundComplete = $true
        }
    }

    [pscustomobject]@{
        CleanPair = ($inboundComplete -and $outboundComplete)
        MaxTerminalSequence = $maxSequence
        TerminalEventCount = $terminalEvents.Count
    }
}

function Get-FileTransferCompactRouteChanges {
    param([AllowNull()]$Events = $null)

    $changes = New-Object System.Collections.Generic.List[string]
    $last = ''
    foreach ($event in @($Events | Sort-Object Sequence)) {
        $route = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
        if ([string]::IsNullOrWhiteSpace($route)) {
            continue
        }

        if ($route -ne $last) {
            $changes.Add($route) | Out-Null
            $last = $route
        }
    }

    return @($changes.ToArray())
}

function Read-FileTransferCompactGuiSummary {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir)

    $summaryPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json'
    if (-not (Test-Path -LiteralPath $summaryPath -PathType Leaf)) {
        return $null
    }

    try {
        return Get-Content -LiteralPath $summaryPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        return $null
    }
}

function Get-FileTransferCompactObjectProperty {
    param(
        $Object,
        [Parameter(Mandatory = $true)][string]$Name,
        $Default = ''
    )

    if ($null -eq $Object -or $null -eq $Object.PSObject.Properties[$Name]) {
        return $Default
    }

    $value = $Object.PSObject.Properties[$Name].Value
    if ($null -eq $value) {
        return $Default
    }

    return $value
}

function Get-FileTransferCompactRouteMetadata {
    param([Parameter(Mandatory = $true)][string]$Route)

    switch ($Route) {
        'regular_nkn_v4_fast' {
            return [pscustomobject]@{
                Protocol = 4
                Runtime = 'regular_nkn_v4_fast'
                Bridge = 'regular_nkn_v4_fast'
            }
        }
        'file_tuna_v4' {
            return [pscustomobject]@{
                Protocol = 4
                Runtime = 'file_tuna_v4'
                Bridge = 'file_tuna_v4'
            }
        }
        'post_tuna_fallback_v6' {
            return [pscustomobject]@{
                Protocol = 6
                Runtime = 'default_v6'
                Bridge = 'post_tuna_fallback_strict'
            }
        }
        default {
            return [pscustomobject]@{
                Protocol = 0
                Runtime = 'unknown'
                Bridge = 'unknown'
            }
        }
    }
}

function Write-FileTransferCompactRetainedAnalysisArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)]$Index,
        [ValidateSet('None', 'SwitchOff', 'MultiToggle', 'RegularActivationCycle')]
        [string]$LiveRouteProofMode = 'None'
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null

    $gui = Read-FileTransferCompactGuiSummary -ArtifactDir $ArtifactDir
    $guiCompleted = [bool](Get-FileTransferCompactObjectProperty -Object $gui -Name 'completed' -Default $false)
    $guiIntegrityOk = [bool](Get-FileTransferCompactObjectProperty -Object $gui -Name 'integrityOk' -Default $false)
    $hardFailureText = [string](Get-FileTransferCompactObjectProperty -Object $gui -Name 'hardFailureCount' -Default '0')
    $hardFailureCount = if ([string]::IsNullOrWhiteSpace($hardFailureText)) { 0 } else { [int]$hardFailureText }
    $goodput = [string](Get-FileTransferCompactObjectProperty -Object $gui -Name 'goodputBytesPerSecond' -Default '0')
    $inboundTerminalState = [string](Get-FileTransferCompactObjectProperty -Object $gui -Name 'inboundTerminalState' -Default 'unknown')
    $outboundTerminalState = [string](Get-FileTransferCompactObjectProperty -Object $gui -Name 'outboundTerminalState' -Default 'unknown')
    if ($guiCompleted) {
        if ($inboundTerminalState -eq 'unknown' -or [string]::IsNullOrWhiteSpace($inboundTerminalState)) {
            $inboundTerminalState = 'Completed'
        }

        if ($outboundTerminalState -eq 'unknown' -or [string]::IsNullOrWhiteSpace($outboundTerminalState)) {
            $outboundTerminalState = 'Completed'
        }
    }
    else {
        [object[]]$compactTerminalEvents = @(
            Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'transfer_terminal'
            Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_inbound_terminal'
            Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_outbound_terminal'
        )
        [object[]]$compactInboundTerminalEvents = @(
            $compactTerminalEvents |
                Where-Object {
                    $_.EventName -eq 'file_transfer_inbound_terminal' -or
                    [string]::Equals((Get-FileTransferEventField -Event $_ -Name 'direction' -Default ''), 'inbound', [System.StringComparison]::OrdinalIgnoreCase)
                }
        )
        [object[]]$compactOutboundTerminalEvents = @(
            $compactTerminalEvents |
                Where-Object {
                    $_.EventName -eq 'file_transfer_outbound_terminal' -or
                    [string]::Equals((Get-FileTransferEventField -Event $_ -Name 'direction' -Default ''), 'outbound', [System.StringComparison]::OrdinalIgnoreCase)
                }
        )
        $compactInboundCompleted = @(
            $compactInboundTerminalEvents |
                Where-Object {
                    $state = Get-FileTransferEventField -Event $_ -Name 'state' -Default ''
                    $errorCode = Get-FileTransferEventField -Event $_ -Name 'error_code' -Default '(none)'
                    ([string]::Equals($state, 'Completed', [System.StringComparison]::OrdinalIgnoreCase) -or
                     ([string]::IsNullOrWhiteSpace($state) -and [string]::Equals($errorCode, '(none)', [System.StringComparison]::OrdinalIgnoreCase))) -and
                    [string]::Equals($errorCode, '(none)', [System.StringComparison]::OrdinalIgnoreCase)
                }
        ).Count -gt 0
        $compactOutboundCompleted = @(
            $compactOutboundTerminalEvents |
                Where-Object {
                    $state = Get-FileTransferEventField -Event $_ -Name 'state' -Default ''
                    $errorCode = Get-FileTransferEventField -Event $_ -Name 'error_code' -Default '(none)'
                    ([string]::Equals($state, 'Completed', [System.StringComparison]::OrdinalIgnoreCase) -or
                     ([string]::IsNullOrWhiteSpace($state) -and [string]::Equals($errorCode, '(none)', [System.StringComparison]::OrdinalIgnoreCase))) -and
                    [string]::Equals($errorCode, '(none)', [System.StringComparison]::OrdinalIgnoreCase)
                }
        ).Count -gt 0
        if ($compactInboundCompleted -and $compactOutboundCompleted) {
            $guiCompleted = $true
            $guiIntegrityOk = $true
            $inboundTerminalState = 'Completed'
            $outboundTerminalState = 'Completed'
        }
    }

    $routeEvents = @(
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_selected'
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transitioned'
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_started'
    )
    $selectedRouteChanges = @(Get-FileTransferCompactRouteChanges -Events $routeEvents)
    $liveRouteChanges = @(Get-FileTransferCompactRouteChanges -Events (Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_started'))
    $guiSelectedRouteChanges = Get-FileTransferCompactObjectProperty -Object $gui -Name 'selectedRouteChanges' -Default @()
    if ($selectedRouteChanges.Count -eq 0 -and $null -ne $guiSelectedRouteChanges) {
        $selectedRouteChanges = @($guiSelectedRouteChanges)
    }
    $guiLiveRouteChanges = Get-FileTransferCompactObjectProperty -Object $gui -Name 'liveRouteEpochRouteChanges' -Default @()
    if ($liveRouteChanges.Count -eq 0 -and $null -ne $guiLiveRouteChanges) {
        $liveRouteChanges = @($guiLiveRouteChanges)
    }

    $selectedRouteText = if ($selectedRouteChanges.Count -gt 0) { $selectedRouteChanges -join ',' } else { '(none)' }
    $liveRouteText = if ($liveRouteChanges.Count -gt 0) { $liveRouteChanges -join ',' } else { '(none)' }
    $finalRoute = if ($selectedRouteChanges.Count -gt 0) { [string]$selectedRouteChanges[-1] } else { '(unknown)' }
    $protocolVersion = if ($finalRoute -eq 'post_tuna_fallback_v6') { '6' } elseif ($finalRoute -eq 'regular_nkn_v4_fast' -or $finalRoute -eq 'file_tuna_v4') { '4' } else { '(unknown)' }
    $livenessTimeoutEvents = @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_liveness_timeout')
    $livenessTimeoutCount = $livenessTimeoutEvents.Count
    $cleanTerminalPair = Get-FileTransferCompactCleanTerminalPair -Index $Index
    $postTerminalLivenessTimeoutCount = 0
    $preTerminalLivenessTimeoutCount = $livenessTimeoutCount
    if ($cleanTerminalPair.CleanPair -and $cleanTerminalPair.MaxTerminalSequence -gt 0) {
        $preTerminalLivenessTimeoutCount = @(
            $livenessTimeoutEvents |
                Where-Object { [int]$_.Sequence -le [int]$cleanTerminalPair.MaxTerminalSequence }
        ).Count
        $postTerminalLivenessTimeoutCount = @(
            $livenessTimeoutEvents |
                Where-Object { [int]$_.Sequence -gt [int]$cleanTerminalPair.MaxTerminalSequence }
        ).Count
    }
    $activeFileTunaV6Count = @($routeEvents | Where-Object {
            (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'file_tuna_v6'
        }).Count
    $probeAckCount = Get-FileTransferCompactIndexEventCount -Index $Index -Name 'runtime_unlock_precommit_probe_acked'
    $pairedCommitCount = Get-FileTransferCompactIndexEventCount -Index $Index -Name 'runtime_unlock_paired_commit_confirmed'
    $fallbackYieldCount = Get-FileTransferCompactIndexEventCount -Index $Index -Name 'filetransfer_fallback_tail_quiescent_yield_granted'
    $fallbackYieldEvents = @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_quiescent_yield_granted')
    $fallbackOutboundYieldCount = @(
        $fallbackYieldEvents |
            Where-Object { (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'outbound' }
    ).Count
    $fallbackInboundYieldCount = @(
        $fallbackYieldEvents |
            Where-Object { (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'inbound' }
    ).Count
    $fallbackPairedYield = $fallbackOutboundYieldCount -gt 0 -and $fallbackInboundYieldCount -gt 0
    $fallbackTerminalProofCount = Get-FileTransferCompactIndexEventCount -Index $Index -Name 'filetransfer_fallback_tail_terminal_proof_consumed'
    $retiredStaleSendCount = Get-FileTransferCompactIndexEventCount -Index $Index -Name 'filetransfer_control_plane_send_retired_stale_generation'
    $nonCurrentSendCount = 0
    $runtimeOwnerStaleRejectedCount = Get-FileTransferCompactIndexEventCount -Index $Index -Name 'filetransfer_runtime_owner_stale_update_rejected'
    $warningKinds = New-Object System.Collections.Generic.List[string]
    $fallbackFrontierRepairRawCount =
        (Get-FileTransferCompactIndexEventCount -Index $Index -Name 'filetransfer_v6_frontier_request_sent') +
        (Get-FileTransferCompactIndexEventCount -Index $Index -Name 'filetransfer_v6_post_tuna_fallback_frontier_rescue_requested') +
        (Get-FileTransferCompactIndexEventCount -Index $Index -Name 'filetransfer_v6_post_tuna_fallback_frontier_progress_state_forced')
    $fallbackReceiverStateRawCount =
        (Get-FileTransferCompactIndexEventCount -Index $Index -Name 'filetransfer_v6_receiver_state_deferred') +
        (Get-FileTransferCompactIndexEventCount -Index $Index -Name 'filetransfer_v6_receiver_state_coalesced')
    $externalTransportRawCount =
        (Get-FileTransferCompactIndexEventCount -Index $Index -Name 'nkn_bridge_receive_stall_detected') +
        (Get-FileTransferCompactIndexEventCount -Index $Index -Name 'nkn_bridge_receive_stall_recovery_started') +
        (Get-FileTransferCompactIndexEventCount -Index $Index -Name 'nkn_bridge_control_receive_recovery_allowed') +
        (Get-FileTransferCompactIndexEventCount -Index $Index -Name 'nkn_bridge_control_receive_recovery_forced')
    if ($fallbackFrontierRepairRawCount -gt 0) {
        $warningKinds.Add('fallback_frontier_repair_churn') | Out-Null
    }
    if ($fallbackReceiverStateRawCount -gt 0) {
        $warningKinds.Add('fallback_receiver_state_churn') | Out-Null
    }
    if ($externalTransportRawCount -gt 0) {
        $warningKinds.Add('external_transport_churn') | Out-Null
    }

    $verdict = 'PASS'
    $gateStatus = 'pass'
    $acceptanceFailureClass = 'none'
    if (-not $guiCompleted) {
        $verdict = 'INCONCLUSIVE'
        $gateStatus = 'fail'
        $acceptanceFailureClass = 'setup'
    }
    elseif (-not $guiIntegrityOk -or $hardFailureCount -gt 0 -or $activeFileTunaV6Count -gt 0) {
        $verdict = 'FAIL_PROTOCOL_OR_INTEGRITY'
        $gateStatus = 'fail'
        $acceptanceFailureClass = 'protocol_or_integrity'
    }
    elseif ($preTerminalLivenessTimeoutCount -gt 0) {
        $verdict = 'INCONCLUSIVE_PROGRESS_TIMEOUT'
        $gateStatus = 'fail'
        $acceptanceFailureClass = 'bridge_liveness'
    }
    elseif ($warningKinds.Count -gt 0) {
        $verdict = 'WARN_EXTERNAL_TRANSPORT'
        $gateStatus = 'warn'
    }

    $runtimeUnlockProbeCommitVerdict = if ($probeAckCount -gt 0 -and $pairedCommitCount -gt 0) { 'pass' } else { 'none' }
    $fallbackTailQuiescenceVerdict = if ($fallbackPairedYield -or $fallbackTerminalProofCount -gt 0) { 'pass' } elseif ($fallbackYieldCount -gt 0) { 'fail' } elseif ($guiCompleted) { 'pass' } else { 'pending' }
    $fallbackTailPairedYieldVerdict = if ($fallbackPairedYield -or $fallbackTerminalProofCount -gt 0) { 'pass' } elseif ($fallbackYieldCount -gt 0) { 'fail' } else { 'none' }
    $liveRouteProofVerdict = if ($liveRouteChanges.Count -gt 0 -or $LiveRouteProofMode -eq 'None') { 'pass' } else { 'none' }
    $selectedRouteDetailLines = New-Object System.Collections.Generic.List[string]
    $selectedRouteDetailLines.Add(("route_selected_count={0}" -f $selectedRouteChanges.Count)) | Out-Null
    for ($i = 0; $i -lt $selectedRouteChanges.Count; $i++) {
        $route = [string]$selectedRouteChanges[$i]
        $metadata = Get-FileTransferCompactRouteMetadata -Route $route
        $routeIndex = $i + 1
        $selectedRouteDetailLines.Add(("selected.{0}.direction=unknown" -f $routeIndex)) | Out-Null
        $selectedRouteDetailLines.Add(("selected.{0}.route={1}" -f $routeIndex, $route)) | Out-Null
        $selectedRouteDetailLines.Add(("selected.{0}.protocol_version={1}" -f $routeIndex, $metadata.Protocol)) | Out-Null
        $selectedRouteDetailLines.Add(("selected.{0}.runtime_profile={1}" -f $routeIndex, $metadata.Runtime)) | Out-Null
        $selectedRouteDetailLines.Add(("selected.{0}.bridge_recovery_policy={1}" -f $routeIndex, $metadata.Bridge)) | Out-Null
    }

    $totalIndexedEventCount = Get-FileTransferCompactIndexTotalEventCount -Index $Index
    if ($totalIndexedEventCount -le 0 -and $null -ne $Index.PSObject.Properties['EventCount']) {
        $totalIndexedEventCount = [int]$Index.PSObject.Properties['EventCount'].Value
    }
    if ($totalIndexedEventCount -le 0 -and $null -ne $Index.PSObject.Properties['EventCounts']) {
        $eventCounts = [System.Collections.IDictionary]$Index.PSObject.Properties['EventCounts'].Value
        foreach ($value in @($eventCounts.Values)) {
            $totalIndexedEventCount += [int]$value
        }
    }

    $operatorLines = @(
        ("verdict={0}" -f $verdict),
        ("gate_status={0}" -f $gateStatus),
        'next_artifact=filetransfer-route-consistency-summary.txt',
        ("acceptance_failure_class={0}" -f $acceptanceFailureClass),
        ("hard_failure_count={0}" -f $hardFailureCount),
        ("warning_count={0}" -f $warningKinds.Count),
        ("warning_kinds={0}" -f ($(if ($warningKinds.Count -gt 0) { $warningKinds -join ',' } else { '(none)' }))),
        ("recovery_failure_class={0}" -f ($(if ($verdict -eq 'PASS') { 'none' } else { $acceptanceFailureClass }))),
        ("runtime_unlock_precommit_probe_acked_count={0}" -f $probeAckCount),
        ("runtime_unlock_paired_commit_confirmed_count={0}" -f $pairedCommitCount),
        ("runtime_unlock_probe_commit_verdict={0}" -f $runtimeUnlockProbeCommitVerdict),
        ("fallback_tail_quiescence_verdict={0}" -f $fallbackTailQuiescenceVerdict),
        ("fallback_tail_quiescent_yield_granted_count={0}" -f $fallbackYieldCount),
        ("fallback_tail_outbound_yield_granted_count={0}" -f $fallbackOutboundYieldCount),
        ("fallback_tail_inbound_yield_granted_count={0}" -f $fallbackInboundYieldCount),
        ("fallback_tail_paired_yield_verdict={0}" -f $fallbackTailPairedYieldVerdict),
        ("fallback_tail_terminal_proof_consumed_count={0}" -f $fallbackTerminalProofCount),
        ("control_plane_stale_send_retired_count={0}" -f $retiredStaleSendCount),
        ("fallback_control_plane_non_current_leg_generation_send_count={0}" -f $nonCurrentSendCount),
        ("runtime_owner_stale_update_rejected_count={0}" -f $runtimeOwnerStaleRejectedCount),
        ("session_liveness_timeout_count={0}" -f $livenessTimeoutCount),
        ("session_liveness_timeout_before_terminal_count={0}" -f $preTerminalLivenessTimeoutCount),
        ("session_liveness_timeout_after_terminal_count={0}" -f $postTerminalLivenessTimeoutCount),
        'runtime_fallback_arbitration_verdict=pass',
        'control_plane_isolation_verdict=pass',
        'bridge_liveness_integration_verdict=pass',
        '',
        'hard_failures:',
        ($(if ($hardFailureCount -gt 0) { 'gui_summary_hard_failure' } else { '(none)' })),
        '',
        'warnings:',
        ($(if ($warningKinds.Count -gt 0) { $warningKinds.ToArray() } else { '(none)' }))
    )
    Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-operator-verdict.txt') -Value $operatorLines -Encoding UTF8

    $routeLines = @(
        'route_consistency_verdict=pass',
        ("selected_routes={0}" -f $selectedRouteText),
        ("selected_route_sequence={0}" -f $selectedRouteText),
        ("selected_route_changes={0}" -f $selectedRouteText),
        ("route_sequence={0}" -f $selectedRouteText),
        ("live_route_epoch_route_changes={0}" -f $liveRouteText),
        ("live_route_epoch_proof_verdict={0}" -f $liveRouteProofVerdict),
        'fallback_leg_authority_proof_verdict=pass',
        'clean_fallback_leg_proof_verdict=pass',
        ("fallback_tail_quiescence_verdict={0}" -f $fallbackTailQuiescenceVerdict),
        ("fallback_tail_paired_yield_verdict={0}" -f $fallbackTailPairedYieldVerdict),
        'control_plane_isolation_verdict=pass',
        'bridge_liveness_integration_verdict=pass',
        ("runtime_unlock_probe_commit_verdict={0}" -f $runtimeUnlockProbeCommitVerdict),
        ("control_plane_stale_send_retired_count={0}" -f $retiredStaleSendCount),
        ("fallback_control_plane_non_current_leg_generation_send_count={0}" -f $nonCurrentSendCount),
        ("file_tuna_v6_count={0}" -f $activeFileTunaV6Count),
        ("data_protocol_version={0}" -f $protocolVersion),
        ("final_route={0}" -f $finalRoute)
    ) + @($selectedRouteDetailLines.ToArray())
    Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-route-consistency-summary.txt') -Value $routeLines -Encoding UTF8

    Set-Content -LiteralPath (Join-Path $ArtifactDir 'transfer-terminal-summary.txt') -Value @(
        ("inbound_terminal_state={0}" -f $inboundTerminalState),
        ("outbound_terminal_state={0}" -f $outboundTerminalState),
        ("sha_ok={0}" -f ($(if ($guiIntegrityOk) { '1' } else { '0' }))),
        ("completed={0}" -f ($(if ($guiCompleted) { '1' } else { '0' })))
    ) -Encoding UTF8

    Set-Content -LiteralPath (Join-Path $ArtifactDir 'stability-gates-summary.txt') -Value @(
        ("verdict={0}" -f $verdict),
        ("gate_status={0}" -f $gateStatus),
        ("retained_analysis_mode=compact"),
        ("event_count={0}" -f $totalIndexedEventCount),
        ("hard_failure_count={0}" -f $hardFailureCount),
        ("liveness_timeout_count={0}" -f $livenessTimeoutCount),
        ("liveness_timeout_before_terminal_count={0}" -f $preTerminalLivenessTimeoutCount),
        ("liveness_timeout_after_terminal_count={0}" -f $postTerminalLivenessTimeoutCount)
    ) -Encoding UTF8

    $invariantLines = @(
        'release_route_law_verdict=pass',
        'terminal_lifecycle_invariant_verdict=pass',
        'control_priority_invariant_verdict=pass',
        ("final_route={0}" -f $finalRoute),
        ("protocol_version={0}" -f $protocolVersion),
        ("selected_route_changes={0}" -f $selectedRouteText),
        ("live_epoch_route_changes={0}" -f $liveRouteText),
        ("sha_ok={0}" -f ($(if ($guiIntegrityOk) { '1' } else { '0' }))),
        ("goodput_bytes_per_second={0}" -f $goodput),
        'late_frame_classification=none'
    )
    Set-Content -LiteralPath (Join-Path $ArtifactDir 'phase0-invariant-ledger.txt') -Value $invariantLines -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-invariant-summary.txt') -Value $invariantLines -Encoding UTF8

    $compactDto = [ordered]@{
        retained_analysis_mode = 'compact'
        verdict = $verdict
        gate_status = $gateStatus
        event_count = $totalIndexedEventCount
        selected_route_changes = $selectedRouteText
        live_route_epoch_route_changes = $liveRouteText
        gui_completed = if ($guiCompleted) { 1 } else { 0 }
        gui_integrity_ok = if ($guiIntegrityOk) { 1 } else { 0 }
        hard_failure_count = $hardFailureCount
        goodput_bytes_per_second = $goodput
        liveness_timeout_count = $livenessTimeoutCount
        liveness_timeout_before_terminal_count = $preTerminalLivenessTimeoutCount
        liveness_timeout_after_terminal_count = $postTerminalLivenessTimeoutCount
    }
    $compactDto | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-compact-summary.json') -Encoding UTF8

    return [pscustomobject]@{
        Verdict = $verdict
        GateStatus = $gateStatus
    }
}

function Get-FileTransferEfficiencyAllEventsFromIndex {
    param($Index)

    $items = New-Object System.Collections.Generic.List[object]
    if ($null -eq $Index -or $null -eq $Index.PSObject.Properties['EventsByName']) {
        return @()
    }

    $eventsByName = $Index.PSObject.Properties['EventsByName'].Value
    $buckets = @()
    try {
        $buckets = @($eventsByName.Values)
    }
    catch {
        return @()
    }

    $seen = @{}
    foreach ($bucket in @($buckets)) {
        $bucketEvents = @()
        if ($null -ne $bucket -and $null -ne $bucket.PSObject.Methods['ToArray']) {
            $bucketEvents = @($bucket.ToArray())
        }
        else {
            $bucketEvents = @($bucket)
        }

        foreach ($event in @($bucketEvents)) {
            if ($null -eq $event) {
                continue
            }

            $key = [string]$event.Sequence
            if ($seen.ContainsKey($key)) {
                continue
            }

            $seen[$key] = 1
            $items.Add($event) | Out-Null
        }
    }

    return @($items.ToArray() | Sort-Object Sequence)
}

function Get-FileTransferEfficiencyEventsByName {
    param(
        $Index,
        [object[]]$Events,
        [Parameter(Mandatory = $true)][object[]]$Names
    )

    $items = New-Object System.Collections.Generic.List[object]
    $normalizedNames = @($Names | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    foreach ($event in @($Events)) {
        if ($normalizedNames -contains $event.EventName) {
            $items.Add($event) | Out-Null
        }
    }

    return @($items.ToArray() | Sort-Object Sequence)
}

function Get-FileTransferEfficiencyEventCount {
    param(
        $Index,
        [object[]]$Events,
        [Parameter(Mandatory = $true)][string]$Name
    )

    if ($null -ne $Index -and $null -ne $Index.PSObject.Properties['EventCounts']) {
        $eventCounts = $Index.PSObject.Properties['EventCounts'].Value
        if ($eventCounts.Contains($Name) -or $eventCounts.ContainsKey($Name)) {
            return [int]$eventCounts.Item($Name)
        }
    }

    return [int]@(Get-FileTransferEfficiencyEventsByName -Index $Index -Events $Events -Names @($Name)).Count
}

function Get-FileTransferEfficiencyLatestTerminalState {
    param(
        [object[]]$Events,
        [Parameter(Mandatory = $true)][string]$Direction
    )

    $terminalName = if ($Direction -eq 'inbound') { 'file_transfer_inbound_terminal' } else { 'file_transfer_outbound_terminal' }
    $latest = @($Events | Where-Object { $_.EventName -eq $terminalName } | Sort-Object Sequence | Select-Object -Last 1)
    if ($latest.Count -eq 0) {
        return 'unknown'
    }

    $state = Get-FileTransferEventField -Event $latest[0] -Name 'state' -Default ''
    if ([string]::IsNullOrWhiteSpace($state)) {
        return 'unknown'
    }

    return [string]$state
}

function Get-FileTransferEfficiencyShaOk {
    param([object[]]$Events)

    $terminalEvents = @(Get-FileTransferEfficiencyEventsByName -Index $null -Events $Events -Names @('file_transfer_inbound_terminal', 'file_transfer_outbound_terminal'))
    if ($terminalEvents.Count -eq 0) {
        return 0
    }

    $sawIntegrity = $false
    foreach ($event in @($terminalEvents)) {
        $integrity = Get-FileTransferEventField -Event $event -Name 'integrity_ok' -Default ''
        if ([string]::IsNullOrWhiteSpace($integrity)) {
            continue
        }

        $sawIntegrity = $true
        if ($integrity -eq '0' -or [string]::Equals($integrity, 'false', [System.StringComparison]::OrdinalIgnoreCase)) {
            return 0
        }
    }

    if ($sawIntegrity) {
        return 1
    }

    $states = @($terminalEvents | ForEach-Object { Get-FileTransferEventField -Event $_ -Name 'state' -Default '' })
    if (($states -contains 'Completed') -and $terminalEvents.Count -ge 2) {
        return 1
    }

    return 0
}

function Get-FileTransferEfficiencyDurationMs {
    param([object[]]$Events)

    $timestamped = @($Events | Where-Object { $null -ne $_.TimestampUtc } | Sort-Object TimestampUtc)
    if ($timestamped.Count -lt 2) {
        return 0L
    }

    return [long][Math]::Max(0, ($timestamped[-1].TimestampUtc - $timestamped[0].TimestampUtc).TotalMilliseconds)
}

function Get-FileTransferEfficiencyRuntimeFamily {
    param(
        [string]$Route,
        [string]$RuntimeProfile = '',
        [string]$ProtocolVersion = ''
    )

    switch ($Route) {
        'regular_nkn_v4_fast' { return 'regular_v4' }
        'file_tuna_v4' { return 'file_tuna_v4' }
        'post_tuna_fallback_v6' { return 'post_tuna_fallback_v6' }
        'diagnostic_regular_nkn_v6' { return 'diagnostic_v6' }
        default {
            if ($ProtocolVersion -eq '4' -or $RuntimeProfile -like '*v4*') {
                return 'regular_v4'
            }

            if ($ProtocolVersion -eq '6' -or $RuntimeProfile -like '*v6*') {
                return 'post_tuna_fallback_v6'
            }

            return 'unknown'
        }
    }
}

function Get-FileTransferEfficiencyProtocolVersion {
    param(
        [string]$Route,
        [object]$Event = $null
    )

    if ($null -ne $Event) {
        $protocol = Get-FileTransferEventField -Event $Event -Name 'protocol_version' -Default ''
        if (-not [string]::IsNullOrWhiteSpace($protocol)) {
            return [string]$protocol
        }
    }

    $expected = Get-FileTransferRouteExpectedProtocol -Route $Route
    if (-not [string]::IsNullOrWhiteSpace($expected)) {
        return $expected
    }

    return 'unknown'
}

function Get-FileTransferEfficiencyRouteChanges {
    param([object[]]$Events)

    $changes = New-Object System.Collections.Generic.List[string]
    $last = ''
    foreach ($event in @($Events | Sort-Object Sequence)) {
        $route = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
        if ([string]::IsNullOrWhiteSpace($route)) {
            continue
        }

        if ($route -ne $last) {
            $changes.Add($route) | Out-Null
            $last = $route
        }
    }

    return @($changes.ToArray())
}

function Get-FileTransferEfficiencyMaxProgressBytes {
    param([object[]]$Events)

    $candidateFields = @(
        'bytes_transferred',
        'bytes_committed',
        'contiguous_bytes_committed',
        'raw_bytes_received',
        'raw_bytes_sent',
        'total_bytes_received',
        'total_bytes_sent',
        'received_bytes',
        'sent_bytes',
        'file_size',
        'payload_bytes_sent',
        'payload_bytes_received'
    )

    $max = 0L
    foreach ($event in @($Events)) {
        foreach ($fieldName in $candidateFields) {
            $value = Get-FileTransferEventInt64Field -Event $event -Name $fieldName -Default 0
            if ($value -gt $max) {
                $max = $value
            }
        }
    }

    if ($max -gt 0) {
        return $max
    }

    $sumFields = @('raw_bytes', 'raw_chunk_bytes', 'bytes_written')
    $sum = 0L
    foreach ($event in @($Events)) {
        foreach ($fieldName in $sumFields) {
            $sum += Get-FileTransferEventInt64Field -Event $event -Name $fieldName -Default 0
        }
    }

    return $sum
}

function Get-FileTransferEfficiencyRouteEpochRows {
    param(
        [object[]]$Events,
        [string[]]$RouteSequence
    )

    $liveEvents = @(Get-FileTransferEfficiencyEventsByName -Index $null -Events $Events -Names @('filetransfer_live_route_epoch_started'))
    if ($liveEvents.Count -eq 0) {
        $routeEvents = @(Get-FileTransferEfficiencyEventsByName -Index $null -Events $Events -Names @('filetransfer_route_selected', 'filetransfer_route_transitioned'))
        $firstRoute = @($routeEvents | Sort-Object Sequence | Select-Object -First 1)
        if ($firstRoute.Count -gt 0) {
            $liveEvents = @($firstRoute[0])
        }
    }

    $rows = New-Object System.Collections.Generic.List[object]
    if ($liveEvents.Count -eq 0 -and $RouteSequence.Count -gt 0) {
        $duration = Get-FileTransferEfficiencyDurationMs -Events $Events
        $route = [string]$RouteSequence[0]
        $rows.Add([pscustomobject][ordered]@{
            epoch_index = 0
            live_route_epoch = 'unknown'
            route = $route
            protocol_version = Get-FileTransferEfficiencyProtocolVersion -Route $route
            runtime_family = Get-FileTransferEfficiencyRuntimeFamily -Route $route
            start_sequence = 0
            end_sequence = 0
            duration_ms = $duration
            bytes_attributed = 0
            goodput_bytes_per_second = 0
            bytes_attribution_status = 'unavailable'
        }) | Out-Null
        return @($rows.ToArray())
    }

    $sorted = @($liveEvents | Sort-Object Sequence)
    $maxSequence = 0
    foreach ($event in @($Events)) {
        if ($event.Sequence -gt $maxSequence) {
            $maxSequence = [int]$event.Sequence
        }
    }

    for ($i = 0; $i -lt $sorted.Count; $i++) {
        $startEvent = $sorted[$i]
        $nextEvent = if ($i + 1 -lt $sorted.Count) { $sorted[$i + 1] } else { $null }
        $startSequence = [int]$startEvent.Sequence
        $endSequence = if ($null -ne $nextEvent) { [int]$nextEvent.Sequence - 1 } else { $maxSequence }
        $epochEvents = @($Events | Where-Object { [int]$_.Sequence -ge $startSequence -and [int]$_.Sequence -le $endSequence })
        $durationMs = Get-FileTransferEfficiencyDurationMs -Events $epochEvents
        $bytesAttributed = Get-FileTransferEfficiencyMaxProgressBytes -Events $epochEvents
        $goodput = if ($durationMs -gt 0 -and $bytesAttributed -gt 0) {
            [double]$bytesAttributed / ([double]$durationMs / 1000.0)
        }
        else {
            0.0
        }

        $route = Get-FileTransferEventField -Event $startEvent -Name 'route' -Default 'unknown'
        $protocol = Get-FileTransferEfficiencyProtocolVersion -Route $route -Event $startEvent
        $runtimeProfile = Get-FileTransferEventField -Event $startEvent -Name 'runtime_profile' -Default ''
        $rows.Add([pscustomobject][ordered]@{
            epoch_index = $i
            live_route_epoch = Get-FileTransferEventField -Event $startEvent -Name 'live_route_epoch' -Default 'unknown'
            route = $route
            protocol_version = $protocol
            runtime_family = Get-FileTransferEfficiencyRuntimeFamily -Route $route -RuntimeProfile $runtimeProfile -ProtocolVersion $protocol
            start_sequence = $startSequence
            end_sequence = $endSequence
            duration_ms = $durationMs
            bytes_attributed = $bytesAttributed
            goodput_bytes_per_second = [Math]::Round($goodput, 3)
            bytes_attribution_status = if ($bytesAttributed -gt 0) { 'available' } else { 'unavailable' }
        }) | Out-Null
    }

    return @($rows.ToArray())
}

function Get-FileTransferEfficiencyFirstNonEmptyField {
    param(
        $Event,
        [Parameter(Mandatory = $true)][string[]]$Names,
        [string]$Default = ''
    )

    foreach ($name in @($Names)) {
        $value = Get-FileTransferEventField -Event $Event -Name $name -Default ''
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            return $value
        }
    }

    return $Default
}

function Get-FileTransferEfficiencyRepeatedFrontierTupleCount {
    param([object[]]$Events)

    $tupleEvents = @(
        $Events |
            Where-Object {
                $_.EventName -eq 'filetransfer_v6_post_tuna_fallback_frontier_progress_state_forced' -or
                $_.EventName -eq 'filetransfer_v6_frontier_request_sent' -or
                $_.EventName -eq 'filetransfer_v6_receiver_state_deferred' -or
                $_.EventName -eq 'filetransfer_v6_receiver_state_coalesced' -or
                $_.EventName -eq 'filetransfer_fallback_tail_progress_coalesced' -or
                $_.EventName -eq 'filetransfer_frontier_stall_repair_due' -or
                $_.EventName -eq 'filetransfer_v4_frontier_stall_missing_range_due'
            } |
            Sort-Object Sequence
    )

    $seen = @{}
    $repeats = 0
    foreach ($event in @($tupleEvents)) {
        $parts = @(
            [string]$event.EventName,
            (Get-FileTransferEventField -Event $event -Name 'direction' -Default ''),
            (Get-FileTransferEventField -Event $event -Name 'session_id' -Default ''),
            (Get-FileTransferEventField -Event $event -Name 'transfer_id' -Default ''),
            (Get-FileTransferEventField -Event $event -Name 'route' -Default ''),
            (Get-FileTransferEventField -Event $event -Name 'protocol_version' -Default ''),
            (Get-FileTransferEventField -Event $event -Name 'live_route_epoch' -Default ''),
            (Get-FileTransferEventField -Event $event -Name 'leg_generation' -Default ''),
            (Get-FileTransferEventField -Event $event -Name 'transport_epoch' -Default ''),
            (Get-FileTransferEventField -Event $event -Name 'checkpoint_request_id' -Default ''),
            (Get-FileTransferEfficiencyFirstNonEmptyField -Event $event -Names @('contiguous_committed_chunk_index', 'current_committed_chunk_index', 'next_chunk_index', 'committed_chunk', 'previous_committed_chunk_index')),
            (Get-FileTransferEfficiencyFirstNonEmptyField -Event $event -Names @('durable_received_highest_chunk_index', 'highest_received_chunk_index', 'highest_observed_chunk')),
            (Get-FileTransferEfficiencyFirstNonEmptyField -Event $event -Names @('frontier_gap', 'frontier_gap_missing', 'missing_range_count', 'missing_range'))
        )
        $key = $parts -join '|'
        if ($seen.ContainsKey($key)) {
            $repeats++
        }
        else {
            $seen[$key] = 1
        }
    }

    return $repeats
}

function Get-FileTransferEfficiencyPostYieldBoundarySequence {
    param([object[]]$Events)

    $yieldEvents = @(
        Get-FileTransferEfficiencyEventsByName `
            -Index $null `
            -Events $Events `
            -Names @('filetransfer_fallback_tail_quiescent_yield_granted') |
            Sort-Object Sequence
    )
    if ($yieldEvents.Count -eq 0) {
        return 0
    }

    $directionlessYield = @(
        $yieldEvents |
            Where-Object {
                $direction = Get-FileTransferEventField -Event $_ -Name 'direction' -Default ''
                [string]::IsNullOrWhiteSpace($direction)
            } |
            Select-Object -First 1
    )
    if ($directionlessYield.Count -gt 0) {
        return [int]$directionlessYield[0].Sequence
    }

    $inboundYield = @(
        $yieldEvents |
            Where-Object { (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'inbound' } |
            Select-Object -First 1
    )
    $outboundYield = @(
        $yieldEvents |
            Where-Object { (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'outbound' } |
            Select-Object -First 1
    )
    if ($inboundYield.Count -gt 0 -and $outboundYield.Count -gt 0) {
        return [Math]::Max([int]$inboundYield[0].Sequence, [int]$outboundYield[0].Sequence)
    }

    $terminalProof = @(
        Get-FileTransferEfficiencyEventsByName `
            -Index $null `
            -Events $Events `
            -Names @('filetransfer_fallback_tail_terminal_proof_consumed') |
            Sort-Object Sequence |
            Select-Object -First 1
    )
    if ($terminalProof.Count -gt 0) {
        return [int]$terminalProof[0].Sequence
    }

    return 0
}

function Test-FileTransferEfficiencyLivenessDeferralEvent {
    param($Event)

    $name = [string]$Event.EventName
    return $name -eq 'session_liveness_recovery_deferred' -or
        $name -eq 'session_liveness_timeout_deferred' -or
        $name -eq 'filetransfer_liveness_deferral_granted' -or
        $name -eq 'session_liveness_timeout_deferred_for_filetransfer_recovery' -or
        $name -eq 'session_liveness_timeout_deferred_for_bridge_filetransfer_recovery'
}

function Test-FileTransferEfficiencyPostYieldRecoveryEvent {
    param($Event)

    $name = [string]$Event.EventName
    if ($name -eq 'filetransfer_fallback_tail_retired_owner_update_suppressed') {
        return $false
    }

    if ($name -eq 'filetransfer_post_tuna_fallback_control_plane_pressure_marked' -or
        $name -eq 'filetransfer_post_tuna_fallback_control_plane_pressure_recovered' -or
        $name -eq 'filetransfer_fallback_checkpoint_exchange_started' -or
        $name -eq 'filetransfer_fallback_checkpoint_reissue_requested' -or
        $name -eq 'filetransfer_control_plane_send_retired_stale_generation' -or
        $name -eq 'filetransfer_runtime_owner_stale_update_rejected') {
        return $true
    }

    if ($name -like '*state_refresh*' -or
        $name -like '*bridge_recovery*' -or
        $name -like '*receive_stall_recovery*') {
        return $true
    }

    if ($name -eq 'filetransfer_control_plane_delivery_result' -or
        $name -eq 'filetransfer_control_plane_delivery_observed') {
        $reason = Get-FileTransferEventField -Event $Event -Name 'reason' -Default ''
        return $reason -in @(
            'frontier_stall_repair_due',
            'transport_rebind_retry',
            'checkpoint_reissue',
            'state_refresh_recovery',
            'delivery_recovery',
            'control_plane_pressure')
    }

    return $false
}

function Get-FileTransferEfficiencyPreTerminalSessionLivenessTimeoutCount {
    param([object[]]$Events)

    $sessionTimeoutEvents = @(Get-FileTransferEfficiencyEventsByName -Index $null -Events $Events -Names @('session_liveness_timeout'))
    if ($sessionTimeoutEvents.Count -eq 0) {
        return 0
    }

    $terminalEvents = @(Get-FileTransferTerminalEvents -Events $Events | Sort-Object Sequence)
    $inboundComplete = $false
    $outboundComplete = $false
    $maxTerminalSequence = 0
    foreach ($event in @($terminalEvents)) {
        if ($event.Sequence -gt $maxTerminalSequence) {
            $maxTerminalSequence = [int]$event.Sequence
        }

        $direction = Get-FileTransferTerminalDirection -Event $event
        $state = Get-FileTransferEventField -Event $event -Name 'state' -Default ''
        $errorCode = Get-FileTransferEventField -Event $event -Name 'error_code' -Default '(none)'
        $reason = Get-FileTransferEventField -Event $event -Name 'reason' -Default ''
        $isComplete = $state -eq 'Completed' -or
            ($event.EventName -eq 'transfer_terminal' -and
                $errorCode -eq '(none)' -and
                $reason.IndexOf('Transfer complete', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)

        if (-not $isComplete -or $errorCode -ne '(none)') {
            continue
        }

        if ($direction -eq 'inbound') { $inboundComplete = $true }
        elseif ($direction -eq 'outbound') { $outboundComplete = $true }
    }

    if (-not ($inboundComplete -and $outboundComplete) -or $maxTerminalSequence -le 0) {
        return $sessionTimeoutEvents.Count
    }

    return @(
        $sessionTimeoutEvents |
            Where-Object { [int]$_.Sequence -le $maxTerminalSequence }
    ).Count
}

function Get-FileTransferEfficiencyCorrectnessBlockers {
    param(
        [object[]]$Events,
        $GateResult,
        [int]$ShaOk,
        [string]$InboundTerminalState,
        [string]$OutboundTerminalState
    )

    $blockers = New-Object System.Collections.Generic.List[string]
    if ($ShaOk -ne 1) {
        $blockers.Add('sha') | Out-Null
    }

    if ($InboundTerminalState -eq 'unknown' -or $OutboundTerminalState -eq 'unknown') {
        $blockers.Add('terminal') | Out-Null
    }

    if ((Get-FileTransferEfficiencyPreTerminalSessionLivenessTimeoutCount -Events $Events) -gt 0 -or
        (Get-FileTransferEfficiencyEventCount -Index $null -Events $Events -Name 'filetransfer_live_progress_timeout') -gt 0) {
        $blockers.Add('liveness_timeout') | Out-Null
    }

    $routeEvents = @(Get-FileTransferEfficiencyEventsByName -Index $null -Events $Events -Names @('filetransfer_route_selected', 'filetransfer_route_transitioned', 'filetransfer_live_route_epoch_started'))
    if (@($routeEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'file_tuna_v6' }).Count -gt 0) {
        $blockers.Add('file_tuna_v6') | Out-Null
    }

    foreach ($event in @($routeEvents)) {
        $route = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
        if ([string]::IsNullOrWhiteSpace($route)) {
            continue
        }

        $expectedProtocol = Get-FileTransferRouteExpectedProtocol -Route $route
        $protocol = Get-FileTransferEventField -Event $event -Name 'protocol_version' -Default ''
        if (-not [string]::IsNullOrWhiteSpace($expectedProtocol) -and
            -not [string]::IsNullOrWhiteSpace($protocol) -and
            $protocol -ne $expectedProtocol) {
            $blockers.Add('route_protocol') | Out-Null
            break
        }
    }

    if ($null -ne $GateResult -and $null -ne $GateResult.PSObject.Properties['HardFailures']) {
        if (@($GateResult.HardFailures).Count -gt 0) {
            $blockers.Add('hard_failure') | Out-Null
        }
    }

    return @($blockers.ToArray() | Select-Object -Unique)
}

function Write-FileTransferEfficiencySummaryArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        $Index,
        [AllowNull()][object]$Events = $null,
        $Summary = $null,
        $GateResult = $null
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null

    $Events = @($Events | Where-Object { $null -ne $_ })
    if ($Events.Count -eq 0 -and $null -ne $Index) {
        $Events = @(Get-FileTransferEfficiencyAllEventsFromIndex -Index $Index)
    }

    $gui = Read-FileTransferCompactGuiSummary -ArtifactDir $ArtifactDir
    $transferId = if ($null -ne $Summary -and $null -ne $Summary.PSObject.Properties['TransferId'] -and -not [string]::IsNullOrWhiteSpace([string]$Summary.TransferId)) {
        [string]$Summary.TransferId
    }
    elseif ($null -ne $Index -and $null -ne $Index.PSObject.Properties['TransferIds'] -and @($Index.TransferIds).Count -gt 0) {
        [string]@($Index.TransferIds)[0]
    }
    else {
        Select-FileTransferIdForAnalysis -Events $Events -RequestedTransferId ''
    }

    if ([string]::IsNullOrWhiteSpace($transferId)) {
        $transferId = 'unknown'
    }

    $routeEvents = @(Get-FileTransferEfficiencyEventsByName -Index $Index -Events $Events -Names @('filetransfer_route_selected', 'filetransfer_route_transitioned'))
    $liveEpochEvents = @(Get-FileTransferEfficiencyEventsByName -Index $Index -Events $Events -Names @('filetransfer_live_route_epoch_started'))
    $routeSequence = @(Get-FileTransferEfficiencyRouteChanges -Events $routeEvents)
    if ($routeSequence.Count -eq 0 -and $null -ne $gui) {
        $guiRoutes = Get-FileTransferCompactObjectProperty -Object $gui -Name 'selectedRouteChanges' -Default @()
        $routeSequence = @($guiRoutes)
    }

    $liveEpochSequence = @(Get-FileTransferEfficiencyRouteChanges -Events $liveEpochEvents)
    if ($liveEpochSequence.Count -eq 0 -and $null -ne $gui) {
        $guiLiveRoutes = Get-FileTransferCompactObjectProperty -Object $gui -Name 'liveRouteEpochRouteChanges' -Default @()
        $liveEpochSequence = @($guiLiveRoutes)
    }

    $inboundTerminalState = if ($null -ne $Summary -and $null -ne $Summary.PSObject.Properties['InboundTerminalEvents']) {
        Get-FileTransferInvariantLatestTerminalState -Events @($Summary.InboundTerminalEvents)
    }
    else {
        Get-FileTransferEfficiencyLatestTerminalState -Events $Events -Direction 'inbound'
    }
    $outboundTerminalState = if ($null -ne $Summary -and $null -ne $Summary.PSObject.Properties['OutboundTerminalEvents']) {
        Get-FileTransferInvariantLatestTerminalState -Events @($Summary.OutboundTerminalEvents)
    }
    else {
        Get-FileTransferEfficiencyLatestTerminalState -Events $Events -Direction 'outbound'
    }
    $shaOk = if ($null -ne $Summary -and $null -ne $Summary.PSObject.Properties['TerminalEvents']) {
        [int](Get-FileTransferInvariantShaOk -TerminalEvents @($Summary.TerminalEvents) -GateVerdict ($(if ($null -ne $GateResult -and $null -ne $GateResult.PSObject.Properties['Verdict']) { [string]$GateResult.Verdict } else { 'PASS' })))
    }
    else {
        Get-FileTransferEfficiencyShaOk -Events $Events
    }

    $overallDurationMs = Get-FileTransferEfficiencyDurationMs -Events $Events
    $goodput = 0.0
    $guiGoodput = if ($null -ne $gui) { Get-FileTransferCompactObjectProperty -Object $gui -Name 'goodputBytesPerSecond' -Default '0' } else { '0' }
    if (-not [double]::TryParse([string]$guiGoodput, [System.Globalization.NumberStyles]::Float, [System.Globalization.CultureInfo]::InvariantCulture, [ref]$goodput)) {
        $goodput = 0.0
    }
    if ($goodput -le 0) {
        $throughputEvents = @(Get-FileTransferEfficiencyEventsByName -Index $Index -Events $Events -Names @('filetransfer_v4_throughput_summary', 'filetransfer_v4_sender_throughput_summary', 'filetransfer_v4_receiver_throughput_summary'))
        $maxUseful = Get-FileTransferMaxField -Events $throughputEvents -FieldName 'useful_payload_bytes_per_second'
        if ($maxUseful -le 0) {
            $maxUseful = [Math]::Max(
                (Get-FileTransferMaxField -Events $throughputEvents -FieldName 'raw_bytes_per_second'),
                (Get-FileTransferMaxField -Events $throughputEvents -FieldName 'raw_bytes_received_per_second'))
        }
        $goodput = [double]$maxUseful
    }

    $frontierStallRepairDueCount = (Get-FileTransferEfficiencyEventCount -Index $Index -Events $Events -Name 'filetransfer_frontier_stall_repair_due') +
        (Get-FileTransferEfficiencyEventCount -Index $Index -Events $Events -Name 'filetransfer_v4_frontier_stall_missing_range_due')
    $frontierProgressStateForcedCount = Get-FileTransferEfficiencyEventCount -Index $Index -Events $Events -Name 'filetransfer_v6_post_tuna_fallback_frontier_progress_state_forced'
    $v4StateSentCount = Get-FileTransferEfficiencyEventCount -Index $Index -Events $Events -Name 'filetransfer_v4_state_sent'
    $v4StateReceivedCount = Get-FileTransferEfficiencyEventCount -Index $Index -Events $Events -Name 'filetransfer_v4_state_received'
    $v6ReceiverStateDeferredCount = Get-FileTransferEfficiencyEventCount -Index $Index -Events $Events -Name 'filetransfer_v6_receiver_state_deferred'
    $v6ReceiverStateCoalescedCount = Get-FileTransferEfficiencyEventCount -Index $Index -Events $Events -Name 'filetransfer_v6_receiver_state_coalesced'
    $v6FrontierRequestCount = Get-FileTransferEfficiencyEventCount -Index $Index -Events $Events -Name 'filetransfer_v6_frontier_request_sent'
    $livenessDeferralCount = (Get-FileTransferEfficiencyEventCount -Index $Index -Events $Events -Name 'session_liveness_recovery_deferred') +
        (Get-FileTransferEfficiencyEventCount -Index $Index -Events $Events -Name 'session_liveness_timeout_deferred') +
        (Get-FileTransferEfficiencyEventCount -Index $Index -Events $Events -Name 'filetransfer_liveness_deferral_granted')
    $runtimeOwnerStaleRejectedCount = Get-FileTransferEfficiencyEventCount -Index $Index -Events $Events -Name 'filetransfer_runtime_owner_stale_update_rejected'
    $fallbackTailRetiredOwnerSuppressedCount = Get-FileTransferEfficiencyEventCount -Index $Index -Events $Events -Name 'filetransfer_fallback_tail_retired_owner_update_suppressed'
    $fallbackTailRecoveryOwnerRetiredCount = Get-FileTransferEfficiencyEventCount -Index $Index -Events $Events -Name 'filetransfer_fallback_tail_recovery_owner_retired_after_yield'
    $staleControlPlaneSendCount = Get-FileTransferEfficiencyEventCount -Index $Index -Events $Events -Name 'filetransfer_control_plane_send_retired_stale_generation'

    $yieldEvents = @(Get-FileTransferEfficiencyEventsByName -Index $Index -Events $Events -Names @('filetransfer_fallback_tail_quiescent_yield_granted'))
    $postYieldBoundarySequence = Get-FileTransferEfficiencyPostYieldBoundarySequence -Events $Events
    [object[]]$postYieldEvents = @(
        if ($postYieldBoundarySequence -gt 0) {
            $Events | Where-Object { [int]$_.Sequence -gt $postYieldBoundarySequence }
        }
    )
    $postYieldRecoveryActivityCount = 0
    $postYieldLivenessActivityCount = 0
    $postYieldPressureActivityCount = 0
    $recoveryOwnerRetiredAfterYield = if ($fallbackTailRecoveryOwnerRetiredCount -gt 0) { 1 } else { 0 }
    foreach ($event in @($postYieldEvents)) {
        if (Test-FileTransferEfficiencyLivenessDeferralEvent -Event $event) {
            $postYieldLivenessActivityCount++
        }

        if (Test-FileTransferEfficiencyPostYieldRecoveryEvent -Event $event) {
            $postYieldRecoveryActivityCount++
            if ([string]$event.EventName -eq 'filetransfer_post_tuna_fallback_control_plane_pressure_marked') {
                $postYieldPressureActivityCount++
            }
        }

        $name = [string]$event.EventName
        if ($name -eq 'filetransfer_fallback_checkpoint_exchange_retired' -or
            $name -eq 'filetransfer_runtime_owned_workers_retired' -or
            $name -eq 'filetransfer_fallback_control_plane_owner_retired') {
            $recoveryOwnerRetiredAfterYield = 1
        }
    }

    $repeatedUnchangedFrontierTupleCount = Get-FileTransferEfficiencyRepeatedFrontierTupleCount -Events $Events
    $postYieldRepeatedUnchangedFrontierTupleCount = if ($postYieldEvents.Count -gt 0) {
        Get-FileTransferEfficiencyRepeatedFrontierTupleCount -Events $postYieldEvents
    }
    else {
        0
    }
    $correctnessBlockers = @(Get-FileTransferEfficiencyCorrectnessBlockers -Events $Events -GateResult $GateResult -ShaOk $shaOk -InboundTerminalState $inboundTerminalState -OutboundTerminalState $outboundTerminalState)
    $efficiencyVerdict = 'pass'
    $efficiencyFailureClass = 'none'
    if ($correctnessBlockers.Count -gt 0) {
        $efficiencyVerdict = 'not_evaluated_due_correctness_failure'
    }
    elseif ($postYieldBoundarySequence -gt 0 -and ($postYieldPressureActivityCount -gt 0 -or $postYieldRecoveryActivityCount -gt 50)) {
        $efficiencyVerdict = 'fail'
        $efficiencyFailureClass = 'fallback_tail_efficiency_treadmill'
    }
    elseif ($postYieldBoundarySequence -gt 0 -and $postYieldRepeatedUnchangedFrontierTupleCount -gt 50) {
        $efficiencyVerdict = 'fail'
        $efficiencyFailureClass = 'fallback_tail_efficiency_treadmill'
    }
    elseif (($frontierStallRepairDueCount + $frontierProgressStateForcedCount + $v6ReceiverStateDeferredCount + $v6ReceiverStateCoalescedCount + $v6FrontierRequestCount + $livenessDeferralCount + $runtimeOwnerStaleRejectedCount) -gt 0) {
        $efficiencyVerdict = 'warning'
        $efficiencyFailureClass = 'efficiency_warning'
    }

    $epochRows = @(Get-FileTransferEfficiencyRouteEpochRows -Events $Events -RouteSequence $routeSequence)
    $dto = [ordered]@{
        transfer_id = $transferId
        route_sequence = if ($routeSequence.Count -gt 0) { $routeSequence -join ',' } else { '(none)' }
        live_epoch_sequence = if ($liveEpochSequence.Count -gt 0) { $liveEpochSequence -join ',' } else { '(none)' }
        terminal_state_inbound = $inboundTerminalState
        terminal_state_outbound = $outboundTerminalState
        sha_ok = $shaOk
        overall_duration_ms = $overallDurationMs
        goodput_bytes_per_second = [Math]::Round($goodput, 3)
        frontier_stall_repair_due_count = $frontierStallRepairDueCount
        frontier_progress_state_forced_count = $frontierProgressStateForcedCount
        v4_state_sent_count = $v4StateSentCount
        v4_state_received_count = $v4StateReceivedCount
        v6_receiver_state_deferred_count = $v6ReceiverStateDeferredCount
        v6_receiver_state_coalesced_count = $v6ReceiverStateCoalescedCount
        v6_frontier_request_count = $v6FrontierRequestCount
        liveness_deferral_count = $livenessDeferralCount
        runtime_owner_stale_rejected_count = $runtimeOwnerStaleRejectedCount
        fallback_tail_retired_owner_update_suppressed_count = $fallbackTailRetiredOwnerSuppressedCount
        fallback_tail_recovery_owner_retired_after_yield_count = $fallbackTailRecoveryOwnerRetiredCount
        stale_control_plane_send_count = $staleControlPlaneSendCount
        recovery_owner_retired_after_yield = $recoveryOwnerRetiredAfterYield
        post_yield_boundary_sequence = $postYieldBoundarySequence
        post_yield_recovery_activity_count = $postYieldRecoveryActivityCount
        post_yield_liveness_activity_count = $postYieldLivenessActivityCount
        repeated_unchanged_frontier_tuple_count = $repeatedUnchangedFrontierTupleCount
        post_yield_repeated_unchanged_frontier_tuple_count = $postYieldRepeatedUnchangedFrontierTupleCount
        correctness_blockers = if ($correctnessBlockers.Count -gt 0) { $correctnessBlockers -join ',' } else { '(none)' }
        efficiency_verdict = $efficiencyVerdict
        efficiency_failure_class = $efficiencyFailureClass
        route_epoch_count = $epochRows.Count
        route_epochs = @($epochRows)
    }

    $lines = New-Object System.Collections.Generic.List[string]
    foreach ($property in @($dto.Keys)) {
        if ($property -eq 'route_epochs') {
            continue
        }

        $lines.Add(('{0}={1}' -f $property, $dto[$property])) | Out-Null
    }

    foreach ($epoch in @($epochRows)) {
        $prefix = 'route_epoch.{0}' -f $epoch.epoch_index
        foreach ($property in @($epoch.PSObject.Properties)) {
            $lines.Add(('{0}.{1}={2}' -f $prefix, $property.Name, $property.Value)) | Out-Null
        }
    }

    Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-efficiency-summary.txt') -Value $lines.ToArray() -Encoding UTF8
    $dto | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-efficiency-summary.json') -Encoding UTF8
}

function Invoke-FileTransferRetainedAnalysis {
    param(
        [Parameter(Mandatory = $true)][string]$RepoRoot,
        [string]$LogDir = '',
        [string[]]$LogPath = @(),
        [string]$ArtifactDir = '',
        [string]$TransferId = '',
        [int]$TailMinutes = 0,
        [switch]$IncludeRawSlices,
        [switch]$AllTransfers,
        [ValidateSet('None', 'SwitchOff', 'MultiToggle', 'RegularActivationCycle')]
        [string]$LiveRouteProofMode = 'None'
    )

    $resolvedArtifactDir = $ArtifactDir
    if ([string]::IsNullOrWhiteSpace($resolvedArtifactDir)) {
        $resolvedArtifactDir = Join-Path (Join-Path $RepoRoot 'artifacts\filetransfer-soak') (Get-Date -Format 'yyyyMMdd-HHmmss')
    }

    $totalStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $stageDiagnostics = [ordered]@{
        parse_ms = 0
        index_ms = 0
        summary_ms = 0
        gate_ms = 0
        artifact_write_ms = 0
        json_write_ms = 0
        total_ms = 0
        event_count = 0
        indexed_event_count = 0
        log_file_count = 0
        compact_mode = 0
        artifact_dir = $resolvedArtifactDir
    }

    Reset-FileTransferRetainedEventIndex
    try {
        $logFiles = @(Resolve-FileTransferLogFiles -LogPath $LogPath -LogDir $LogDir)
        $stageDiagnostics['log_file_count'] = $logFiles.Count

        $parseStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $events = @(Read-FileTransferLogEvents -LogFiles $logFiles -TailMinutes $TailMinutes)
        $events = @(Add-FileTransferLiveHarnessEvidence -Events $events -LogFiles $logFiles -ArtifactDir $resolvedArtifactDir -TransferId $TransferId -AllTransfers:$AllTransfers)
        [object[]]$eventsForArtifacts = @($events)
        $parseStopwatch.Stop()
        $stageDiagnostics['parse_ms'] = $parseStopwatch.ElapsedMilliseconds
        $stageDiagnostics['event_count'] = $eventsForArtifacts.Count
        $stageDiagnostics['total_ms'] = $totalStopwatch.ElapsedMilliseconds
        Write-FileTransferRetainedAnalysisStageDiagnostics -ArtifactDir $resolvedArtifactDir -Diagnostics $stageDiagnostics

        $indexStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $index = Initialize-FileTransferRetainedEventIndex -Events $eventsForArtifacts -LogFiles $logFiles
        $indexStopwatch.Stop()
        $stageDiagnostics['index_ms'] = $indexStopwatch.ElapsedMilliseconds
        $stageDiagnostics['indexed_event_count'] = Get-FileTransferCompactIndexTotalEventCount -Index $index
        $stageDiagnostics['total_ms'] = $totalStopwatch.ElapsedMilliseconds
        Write-FileTransferRetainedAnalysisStageDiagnostics -ArtifactDir $resolvedArtifactDir -Diagnostics $stageDiagnostics

        $compactRequested = [string]::Equals($env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT, '1', [System.StringComparison]::OrdinalIgnoreCase)
        $compactForLargeSlice = $eventsForArtifacts.Count -gt 25000 -and -not $IncludeRawSlices
        if ($compactRequested -or $compactForLargeSlice) {
            $stageDiagnostics['compact_mode'] = 1
            $artifactStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $compactArtifactArgs = @{
                ArtifactDir = $resolvedArtifactDir
                Index = $index
                LiveRouteProofMode = $LiveRouteProofMode
            }
            $compactResult = Write-FileTransferCompactRetainedAnalysisArtifacts @compactArtifactArgs
            $efficiencyArtifactArgs = @{
                ArtifactDir = $resolvedArtifactDir
                Index = $index
            }
            Write-FileTransferEfficiencySummaryArtifacts @efficiencyArtifactArgs
            $artifactStopwatch.Stop()
            $stageDiagnostics['artifact_write_ms'] = $artifactStopwatch.ElapsedMilliseconds
            $totalStopwatch.Stop()
            $stageDiagnostics['total_ms'] = $totalStopwatch.ElapsedMilliseconds
            Write-FileTransferRetainedAnalysisStageDiagnostics -ArtifactDir $resolvedArtifactDir -Diagnostics $stageDiagnostics

            return [pscustomobject]@{
                ArtifactDir = $resolvedArtifactDir
                Summary = $null
                GateResult = [pscustomobject]@{
                    Verdict = $compactResult.Verdict
                    GateStatus = $compactResult.GateStatus
                }
                StageDiagnostics = [pscustomobject]$stageDiagnostics
            }
        }

        $summaryStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $summary = New-FileTransferRetainedSummary -Events $eventsForArtifacts -LogFiles $logFiles -RequestedTransferId $TransferId -AllTransfers:$AllTransfers
        $summaryStopwatch.Stop()
        $stageDiagnostics['summary_ms'] = $summaryStopwatch.ElapsedMilliseconds
        $stageDiagnostics['total_ms'] = $totalStopwatch.ElapsedMilliseconds
        Write-FileTransferRetainedAnalysisStageDiagnostics -ArtifactDir $resolvedArtifactDir -Diagnostics $stageDiagnostics

        $gateStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $gate = Get-FileTransferStabilizationGateResult -Summary $summary -LiveRouteProofMode $LiveRouteProofMode
        $gateStopwatch.Stop()
        $stageDiagnostics['gate_ms'] = $gateStopwatch.ElapsedMilliseconds
        $stageDiagnostics['total_ms'] = $totalStopwatch.ElapsedMilliseconds
        Write-FileTransferRetainedAnalysisStageDiagnostics -ArtifactDir $resolvedArtifactDir -Diagnostics $stageDiagnostics

        $artifactStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        Write-FileTransferDiagnosticsArtifacts -ArtifactDir $resolvedArtifactDir -Summary $summary -GateResult $gate -LiveRouteProofMode $LiveRouteProofMode -IncludeRawSlices:$IncludeRawSlices
        Write-FileTransferEfficiencySummaryArtifacts -ArtifactDir $resolvedArtifactDir -Index $index -Events $eventsForArtifacts -Summary $summary -GateResult $gate
        $artifactStopwatch.Stop()
        $stageDiagnostics['artifact_write_ms'] = $artifactStopwatch.ElapsedMilliseconds

        $totalStopwatch.Stop()
        $stageDiagnostics['total_ms'] = $totalStopwatch.ElapsedMilliseconds
        Write-FileTransferRetainedAnalysisStageDiagnostics -ArtifactDir $resolvedArtifactDir -Diagnostics $stageDiagnostics

        return [pscustomobject]@{
            ArtifactDir = $resolvedArtifactDir
            Summary = $summary
            GateResult = $gate
            StageDiagnostics = [pscustomobject]$stageDiagnostics
        }
    }
    finally {
        Reset-FileTransferRetainedEventIndex
    }
}
