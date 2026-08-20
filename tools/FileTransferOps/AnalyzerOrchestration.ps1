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

function Write-FileTransferIndexedRawEvidenceArtifact {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)]$Index
    )

    $transferIds = @($Index.PSObject.Properties['TransferIds'].Value)
    $transferIdText = if ($transferIds.Count -gt 0) { $transferIds -join ',' } else { '(none)' }
    $highValueEventNames = @(
        'runtime_unlock_retired_pending_answer_transaction_handoff_decided',
        'runtime_unlock_precommit_probe_acked',
        'filetransfer_route_transition_prepared',
        'filetransfer_route_transition_draining',
        'filetransfer_route_transition_drain_receipt_applied',
        'filetransfer_route_transition_committed',
        'filetransfer_route_transition_disposition_applied',
        'filetransfer_fallback_v6_proof_bootstrap_observed',
        'filetransfer_fallback_v6_proof_bootstrap_decided',
        'filetransfer_fallback_v6_proof_bootstrap_issued',
        'filetransfer_fallback_v6_proof_bootstrap_execution_completed',
        'filetransfer_fallback_v6_proof_bootstrap_receipt_applied',
        'filetransfer_fallback_v6_proof_bootstrap_peer_frame_decided',
        'filetransfer_fallback_v6_proof_bootstrap_peer_boundary_bound',
        'filetransfer_route_selected',
        'filetransfer_live_route_epoch_started',
        'filetransfer_live_route_epoch_recovered',
        'file_transfer_inbound_terminal',
        'file_transfer_outbound_terminal',
        'transfer_terminal'
    )
    $rawEvidenceEvents = @(
        Get-FileTransferCompactIndexEventsByNamesOrdered `
            -Index $Index `
            -Names $highValueEventNames |
            Select-Object -First 250
    )
    $rawEvidenceLines = @(Get-FileTransferArtifactEvidenceLines -Events $rawEvidenceEvents -Limit 250)
    $rawLines = @(
        ("transfer_id={0}" -f $transferIdText),
        'selection=indexed_high_value_semantic_events',
        '',
        'matched_evidence:'
    ) + ($(if ($rawEvidenceLines.Count -gt 0) { $rawEvidenceLines } else { @('(none)') }))

    Write-FileTransferArtifact `
        -ArtifactDir $ArtifactDir `
        -FileName 'raw-log-slices.txt' `
        -Lines $rawLines | Out-Null
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

function Get-FileTransferCompactIndexEventsByNamesOrdered {
    param(
        [Parameter(Mandatory = $true)]$Index,
        [Parameter(Mandatory = $true)][string[]]$Names
    )

    $allEventsProperty = $Index.PSObject.Properties['AllEvents']
    if ($null -eq $allEventsProperty) {
        [object[]]$fallback = @(
            foreach ($name in $Names) {
                Get-FileTransferCompactIndexEventsByName -Index $Index -Name $name
            }
        )
        return @($fallback | Sort-Object Sequence, LineNumber)
    }

    $selectedNames = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([System.StringComparer]::Ordinal)
    foreach ($name in $Names) {
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            $selectedNames.Add($name) | Out-Null
        }
    }

    $selected = New-Object System.Collections.Generic.List[object]
    foreach ($event in $allEventsProperty.Value) {
        if ($null -ne $event -and $selectedNames.Contains([string]$event.EventName)) {
            $selected.Add($event) | Out-Null
        }
    }
    return @($selected.ToArray())
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

function Test-FileTransferCompactOutboundWorkerScopeMatch {
    param(
        [Parameter(Mandatory = $true)]$Anchor,
        [Parameter(Mandatory = $true)]$Candidate
    )

    foreach ($fieldName in @('session_id', 'transfer_id')) {
        $anchorValue = Get-FileTransferEventField -Event $Anchor -Name $fieldName -Default ''
        $candidateValue = Get-FileTransferEventField -Event $Candidate -Name $fieldName -Default ''
        if ([string]::IsNullOrWhiteSpace($anchorValue) -or
            $anchorValue -in @('(none)', 'none') -or
            [string]::IsNullOrWhiteSpace($candidateValue) -or
            $candidateValue -in @('(none)', 'none') -or
            -not [string]::Equals($anchorValue, $candidateValue, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    return $true
}

function Test-FileTransferCompactOutboundWorkerPublicationTupleMatch {
    param(
        [Parameter(Mandatory = $true)]$Publication,
        [Parameter(Mandatory = $true)]$Candidate
    )

    if (-not (Test-FileTransferCompactOutboundWorkerPublicationTupleComplete -Event $Publication) -or
        -not (Test-FileTransferCompactOutboundWorkerPublicationTupleComplete -Event $Candidate) -or
        -not (Test-FileTransferCompactOutboundWorkerScopeMatch -Anchor $Publication -Candidate $Candidate)) {
        return $false
    }

    foreach ($fieldName in @(
            'route',
            'protocol_version',
            'live_route_epoch',
            'runtime_generation',
            'terminal_generation',
            'leg_generation',
            'route_publication_generation',
            'route_commit_generation',
            'target_route',
            'target_protocol_version',
            'target_transport')) {
        $publicationValue = Get-FileTransferEventField -Event $Publication -Name $fieldName -Default ''
        $candidateValue = Get-FileTransferEventField -Event $Candidate -Name $fieldName -Default ''
        if (-not [string]::Equals($publicationValue, $candidateValue, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    return $true
}

function Test-FileTransferCompactOutboundWorkerPublicationTupleComplete {
    param([Parameter(Mandatory = $true)]$Event)

    foreach ($fieldName in @('session_id', 'transfer_id')) {
        $value = Get-FileTransferEventField -Event $Event -Name $fieldName -Default ''
        if ([string]::IsNullOrWhiteSpace($value) -or $value -in @('(none)', 'none')) {
            return $false
        }
    }

    if (-not [string]::Equals(
            (Get-FileTransferEventField -Event $Event -Name 'direction' -Default ''),
            'outbound',
            [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    foreach ($fieldName in @(
            'route',
            'protocol_version',
            'live_route_epoch',
            'runtime_generation',
            'terminal_generation',
            'leg_generation',
            'route_publication_generation',
            'route_commit_generation',
            'target_route',
            'target_protocol_version',
            'target_transport')) {
        $value = Get-FileTransferEventField -Event $Event -Name $fieldName -Default ''
        if ([string]::IsNullOrWhiteSpace($value) -or $value -in @('(none)', 'none')) {
            return $false
        }
    }

    return $true
}

function Test-FileTransferCompactOutboundWorkerFrontierTupleMatch {
    param(
        [Parameter(Mandatory = $true)]$Publication,
        [Parameter(Mandatory = $true)]$Candidate
    )

    if (-not (Test-FileTransferCompactOutboundWorkerPublicationTupleComplete -Event $Publication) -or
        -not (Test-FileTransferCompactOutboundWorkerScopeMatch -Anchor $Publication -Candidate $Candidate)) {
        return $false
    }

    foreach ($fieldName in @('route', 'protocol_version', 'live_route_epoch', 'leg_generation')) {
        $publicationValue = Get-FileTransferEventField -Event $Publication -Name $fieldName -Default ''
        $candidateValue = Get-FileTransferEventField -Event $Candidate -Name $fieldName -Default ''
        if ([string]::IsNullOrWhiteSpace($candidateValue) -or
            $candidateValue -in @('(none)', 'none') -or
            -not [string]::Equals($publicationValue, $candidateValue, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    return $true
}

function Get-FileTransferCompactOutboundWorkerFrontierCorrelationKey {
    param([Parameter(Mandatory = $true)]$Event)

    $repairRequestId = Get-FileTransferEventField -Event $Event -Name 'repair_request_id' -Default ''
    $requestCorrelationId = Get-FileTransferEventField -Event $Event -Name 'request_correlation_id' -Default ''
    if ([string]::IsNullOrWhiteSpace($repairRequestId) -or
        $repairRequestId -in @('(none)', 'none') -or
        [string]::IsNullOrWhiteSpace($requestCorrelationId) -or
        $requestCorrelationId -in @('(none)', 'none')) {
        return ''
    }

    return ('{0}|{1}' -f $repairRequestId, $requestCorrelationId).ToLowerInvariant()
}

function Get-FileTransferCompactOutboundWorkerInstallDescriptorFieldNames {
    @(
        'descriptor_terminal_generation',
        'descriptor_leg_generation',
        'installation_reason',
        'runtime_owner_session_id',
        'runtime_owner_transfer_id',
        'runtime_owner_direction',
        'runtime_owner_route',
        'runtime_owner_route_token',
        'runtime_owner_protocol_version',
        'runtime_owner_live_route_epoch',
        'runtime_owner_runtime_generation',
        'runtime_owner_terminal_generation',
        'runtime_owner_runtime_family',
        'runtime_owner_created_reason',
        'route_publication_session_id',
        'route_publication_transfer_id',
        'route_publication_direction',
        'route_publication_route',
        'route_publication_route_token',
        'route_publication_protocol_version',
        'route_publication_live_route_epoch',
        'route_publication_runtime_generation',
        'route_publication_terminal_generation',
        'route_publication_leg_generation',
        'route_publication_route_commit_generation',
        'route_publication_target_route',
        'route_publication_target_protocol_version',
        'route_publication_target_transport',
        'route_publication_token_generation',
        'route_publication_action_kind',
        'route_publication_reason',
        'route_publication_owner_session_id',
        'route_publication_owner_transfer_id',
        'route_publication_owner_direction',
        'route_publication_owner_route',
        'route_publication_owner_route_token',
        'route_publication_owner_protocol_version',
        'route_publication_owner_live_route_epoch',
        'route_publication_owner_runtime_generation',
        'route_publication_owner_terminal_generation',
        'route_publication_owner_runtime_family',
        'route_publication_owner_created_reason'
    )
}

function Test-FileTransferCompactOutboundWorkerInstallDescriptorComplete {
    param([Parameter(Mandatory = $true)]$Event)

    if (-not (Test-FileTransferCompactOutboundWorkerPublicationTupleComplete -Event $Event)) {
        return $false
    }

    $installGenerationText = Get-FileTransferEventField -Event $Event -Name 'install_generation' -Default ''
    $predecessorGenerationText = Get-FileTransferEventField -Event $Event -Name 'predecessor_install_generation' -Default ''
    $dataSessionGenerationText = Get-FileTransferEventField -Event $Event -Name 'data_session_generation' -Default ''
    $runtimeFamily = Get-FileTransferEventField -Event $Event -Name 'runtime_family' -Default ''
    if ($installGenerationText -notmatch '^\d+$' -or
        $predecessorGenerationText -notmatch '^\d+$' -or
        $dataSessionGenerationText -notmatch '^\d+$' -or
        [string]::IsNullOrWhiteSpace($runtimeFamily) -or
        $runtimeFamily -in @('(none)', 'none')) {
        return $false
    }

    foreach ($fieldName in Get-FileTransferCompactOutboundWorkerInstallDescriptorFieldNames) {
        $value = Get-FileTransferEventField -Event $Event -Name $fieldName -Default ''
        if ([string]::IsNullOrWhiteSpace($value) -or $value -in @('(none)', 'none')) {
            return $false
        }
    }

    foreach ($fieldName in @(
            'descriptor_terminal_generation',
            'descriptor_leg_generation',
            'runtime_owner_protocol_version',
            'runtime_owner_live_route_epoch',
            'runtime_owner_runtime_generation',
            'runtime_owner_terminal_generation',
            'route_publication_protocol_version',
            'route_publication_live_route_epoch',
            'route_publication_runtime_generation',
            'route_publication_terminal_generation',
            'route_publication_leg_generation',
            'route_publication_route_commit_generation',
            'route_publication_target_protocol_version',
            'route_publication_token_generation',
            'route_publication_owner_protocol_version',
            'route_publication_owner_live_route_epoch',
            'route_publication_owner_runtime_generation',
            'route_publication_owner_terminal_generation')) {
        if ((Get-FileTransferEventField -Event $Event -Name $fieldName -Default '') -notmatch '^\d+$') {
            return $false
        }
    }

    foreach ($fieldName in @('runtime_owner_direction', 'route_publication_direction', 'route_publication_owner_direction')) {
        if ((Get-FileTransferEventField -Event $Event -Name $fieldName -Default '') -ne 'outbound') {
            return $false
        }
    }

    $installGeneration = [int64]$installGenerationText
    $predecessorGeneration = [int64]$predecessorGenerationText
    $dataSessionGeneration = [int64]$dataSessionGenerationText
    return $installGeneration -gt 0 -and
        $predecessorGeneration -ge 0 -and
        $predecessorGeneration -lt $installGeneration -and
        $dataSessionGeneration -gt 0
}

function Test-FileTransferCompactOutboundWorkerInstallDescriptorMatch {
    param(
        [Parameter(Mandatory = $true)]$Anchor,
        [Parameter(Mandatory = $true)]$Candidate
    )

    if (-not (Test-FileTransferCompactOutboundWorkerInstallDescriptorComplete -Event $Anchor) -or
        -not (Test-FileTransferCompactOutboundWorkerInstallDescriptorComplete -Event $Candidate) -or
        -not (Test-FileTransferCompactOutboundWorkerPublicationTupleMatch -Publication $Anchor -Candidate $Candidate)) {
        return $false
    }

    foreach ($fieldName in @(
            'install_generation',
            'predecessor_install_generation',
            'data_session_generation',
            'runtime_family') + @(Get-FileTransferCompactOutboundWorkerInstallDescriptorFieldNames)) {
        $anchorValue = Get-FileTransferEventField -Event $Anchor -Name $fieldName -Default ''
        $candidateValue = Get-FileTransferEventField -Event $Candidate -Name $fieldName -Default ''
        if (-not [string]::Equals($anchorValue, $candidateValue, [System.StringComparison]::Ordinal)) {
            return $false
        }
    }

    return $true
}

function Test-FileTransferCompactOutboundWorkerDispatchTupleMatch {
    param(
        [Parameter(Mandatory = $true)]$Install,
        [Parameter(Mandatory = $true)]$Candidate
    )

    return Test-FileTransferCompactOutboundWorkerInstallDescriptorMatch -Anchor $Install -Candidate $Candidate
}

function Test-FileTransferCompactOutboundWorkerProgressMatch {
    param(
        [Parameter(Mandatory = $true)]$Install,
        [Parameter(Mandatory = $true)]$Progress,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Dispatches
    )

    if ($Progress.EventName -ne 'filetransfer_fallback_frontier_repair_progress_observed' -or
        (Get-FileTransferEventField -Event $Progress -Name 'direction' -Default '') -ne 'inbound' -or
        -not (Test-FileTransferCompactOutboundWorkerScopeMatch -Anchor $Install -Candidate $Progress)) {
        return $false
    }

    $repairRequestKey = Get-FileTransferEventField -Event $Progress -Name 'repair_request_key' -Default ''
    $frontierBeforeText = Get-FileTransferEventField -Event $Progress -Name 'committed_frontier_before' -Default ''
    $frontierAfterText = Get-FileTransferEventField -Event $Progress -Name 'committed_frontier_after' -Default ''
    $requestedChunkCountText = Get-FileTransferEventField -Event $Progress -Name 'requested_chunk_count' -Default ''
    if ([string]::IsNullOrWhiteSpace($repairRequestKey) -or
        $repairRequestKey -in @('(none)', 'none') -or
        $frontierBeforeText -notmatch '^\d+$' -or
        $frontierAfterText -notmatch '^\d+$' -or
        $requestedChunkCountText -notmatch '^\d+$' -or
        [int64]$frontierAfterText -le [int64]$frontierBeforeText -or
        [int64]$requestedChunkCountText -le 0) {
        return $false
    }

    return @($Dispatches | Where-Object {
        [int64]$_.Sequence -lt [int64]$Progress.Sequence -and
        (Test-FileTransferCompactOutboundWorkerInstallDescriptorMatch -Anchor $Install -Candidate $_) -and
        [string]::Equals(
            (Get-FileTransferEventField -Event $_ -Name 'repair_request_id' -Default ''),
            $repairRequestKey,
            [System.StringComparison]::Ordinal)
    } | Select-Object -First 1).Count -gt 0
}

function Get-FileTransferCompactOutboundReceiveWorkerHandoffProof {
    param([Parameter(Mandatory = $true)]$Index)

    [object[]]$publicationEvents = @(
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transitioned'
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_selected'
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_started'
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_recovered'
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_started'
    )
    [object[]]$frontierEvents = @(
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_control_plane_delivery_result'
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_control_plane_delivery_observed'
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_frontier_request_transport_queued'
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_frontier_request_transport_rejected'
    )
    [object[]]$terminalEvents = @(
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_outbound_terminal'
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'transfer_terminal'
    )
    [object[]]$installEvents = @(
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_outbound_sparse_worker_handoff_installed'
    )
    [object[]]$requiredEvents = @(
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_outbound_sparse_worker_handoff_required'
    )
    [object[]]$supervisorStoppedEvents = @(
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_outbound_sparse_receive_supervisor_stopped'
    )
    [object[]]$dispatchEvents = @(
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_frontier_request_receive_dispatched'
    )
    [object[]]$progressEvents = @(
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_frontier_repair_progress_observed'
    )
    $candidates = New-Object System.Collections.Generic.List[object]
    $staleFailures = @(
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_owner_stale_update_rejected' |
            Where-Object {
                (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'outbound' -and
                (Get-FileTransferEventField -Event $_ -Name 'source' -Default '') -eq 'v4_sender_failed' -and
                (Get-FileTransferEventField -Event $_ -Name 'attempted_action' -Default '') -eq 'read_failed' -and
                (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'runtime_generation_mismatch' -and
                (Get-FileTransferEventField -Event $_ -Name 'current_route' -Default '') -eq 'post_tuna_fallback_v6' -and
                (Get-FileTransferEventField -Event $_ -Name 'current_protocol_version' -Default '') -eq '6'
            } |
            Sort-Object Sequence
    )
    foreach ($staleFailure in $staleFailures) {
        $staleSequence = [int64]$staleFailure.Sequence
        $currentRuntimeGeneration = Get-FileTransferEventField -Event $staleFailure -Name 'current_runtime_generation' -Default ''
        $currentTerminalGeneration = Get-FileTransferEventField -Event $staleFailure -Name 'current_terminal_generation' -Default ''
        $currentLiveRouteEpoch = Get-FileTransferEventField -Event $staleFailure -Name 'current_live_route_epoch' -Default ''
        if ([string]::IsNullOrWhiteSpace($currentRuntimeGeneration) -or
            [string]::IsNullOrWhiteSpace($currentTerminalGeneration) -or
            [string]::IsNullOrWhiteSpace($currentLiveRouteEpoch)) {
            continue
        }

        $matchingPublications = @(
            $publicationEvents |
                Where-Object {
                    [int64]$_.Sequence -lt $staleSequence -and
                    (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'outbound' -and
                    (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'post_tuna_fallback_v6' -and
                    (Get-FileTransferEventField -Event $_ -Name 'protocol_version' -Default '') -eq '6' -and
                    (Get-FileTransferEventField -Event $_ -Name 'runtime_generation' -Default '') -eq $currentRuntimeGeneration -and
                    (Get-FileTransferEventField -Event $_ -Name 'terminal_generation' -Default '') -eq $currentTerminalGeneration -and
                    (Get-FileTransferEventField -Event $_ -Name 'live_route_epoch' -Default '') -eq $currentLiveRouteEpoch -and
                    (Get-FileTransferEventField -Event $_ -Name 'route_publication_action_kind' -Default '') -eq 'RoutePublication' -and
                    (Get-FileTransferEventField -Event $_ -Name 'coordinator_decision_reason' -Default '') -eq 'accepted' -and
                    (Test-FileTransferCompactOutboundWorkerScopeMatch -Anchor $staleFailure -Candidate $_) -and
                    (Test-FileTransferCompactOutboundWorkerPublicationTupleComplete -Event $_)
                } |
                Sort-Object Sequence -Descending |
                Select-Object -First 1
        )
        if ($matchingPublications.Count -eq 0) {
            continue
        }

        $publication = $matchingPublications[0]
        $supersedingPublication = @(
            $publicationEvents |
                Where-Object {
                    [int64]$_.Sequence -gt $staleSequence -and
                    (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'outbound' -and
                    (Get-FileTransferEventField -Event $_ -Name 'route_publication_action_kind' -Default '') -eq 'RoutePublication' -and
                    (Get-FileTransferEventField -Event $_ -Name 'coordinator_decision_reason' -Default '') -eq 'accepted' -and
                    (Test-FileTransferCompactOutboundWorkerScopeMatch -Anchor $staleFailure -Candidate $_) -and
                    (Test-FileTransferCompactOutboundWorkerPublicationTupleComplete -Event $_) -and
                    -not (Test-FileTransferCompactOutboundWorkerPublicationTupleMatch -Publication $publication -Candidate $_)
                } |
                Sort-Object Sequence |
                Select-Object -First 1
        )
        if ($supersedingPublication.Count -gt 0) {
            continue
        }

        $frontierEvidenceObserved = $false
        $terminalResolutionObserved = $false
        $publicationSuperseded = $false
        $currentInstall = $null
        $currentConsumerProven = $false
        $descriptorDispatchEvents = New-Object System.Collections.Generic.List[object]
        $matchedFrontierCorrelationKeys = [System.Collections.Generic.HashSet[string]]::new(
            [System.StringComparer]::OrdinalIgnoreCase)
        $scanEvents = @(
            @($publicationEvents) +
            @($installEvents) +
            @($supervisorStoppedEvents) +
            @($dispatchEvents) +
            @($progressEvents) +
            @($terminalEvents) +
            @($frontierEvents) |
                Sort-Object Sequence
        )
        foreach ($event in $scanEvents) {
            $eventSequence = [int64]$event.Sequence
            if ($eventSequence -le [int64]$publication.Sequence -or
                -not (Test-FileTransferCompactOutboundWorkerScopeMatch -Anchor $staleFailure -Candidate $event)) {
                continue
            }

            if ($eventSequence -gt $staleSequence -and
                $event.EventName -in @(
                    'filetransfer_route_transitioned',
                    'filetransfer_route_selected',
                    'filetransfer_live_route_epoch_started',
                    'filetransfer_live_route_epoch_recovered',
                    'filetransfer_runtime_started') -and
                (Get-FileTransferEventField -Event $event -Name 'direction' -Default '') -eq 'outbound' -and
                (Get-FileTransferEventField -Event $event -Name 'route_publication_action_kind' -Default '') -eq 'RoutePublication' -and
                (Get-FileTransferEventField -Event $event -Name 'coordinator_decision_reason' -Default '') -eq 'accepted' -and
                (Test-FileTransferCompactOutboundWorkerPublicationTupleComplete -Event $event) -and
                -not (Test-FileTransferCompactOutboundWorkerPublicationTupleMatch -Publication $publication -Candidate $event)) {
                $publicationSuperseded = $true
                break
            }

            if ($eventSequence -gt $staleSequence -and
                $event.EventName -in @('file_transfer_outbound_terminal', 'transfer_terminal') -and
                ($event.EventName -ne 'transfer_terminal' -or
                    (Get-FileTransferEventField -Event $event -Name 'direction' -Default '') -eq 'outbound')) {
                $terminalResolutionObserved = $true
                break
            }

            if ($event.EventName -eq 'filetransfer_outbound_sparse_worker_handoff_installed' -and
                (Test-FileTransferCompactOutboundWorkerInstallDescriptorComplete -Event $event) -and
                (Test-FileTransferCompactOutboundWorkerPublicationTupleMatch -Publication $publication -Candidate $event)) {
                $matchingRequired = @($requiredEvents | Where-Object {
                    [int64]$_.Sequence -lt $eventSequence -and
                    (Test-FileTransferCompactOutboundWorkerInstallDescriptorMatch -Anchor $_ -Candidate $event)
                } | Sort-Object Sequence -Descending | Select-Object -First 1)
                if ($matchingRequired.Count -gt 0) {
                    if ($null -eq $currentInstall -or
                        -not (Test-FileTransferCompactOutboundWorkerInstallDescriptorMatch -Anchor $currentInstall -Candidate $event)) {
                        $currentConsumerProven = $false
                    }
                    $currentInstall = $event
                }
                continue
            }

            if ($event.EventName -eq 'filetransfer_outbound_sparse_receive_supervisor_stopped' -and
                $null -ne $currentInstall -and
                (Test-FileTransferCompactOutboundWorkerInstallDescriptorMatch -Anchor $currentInstall -Candidate $event)) {
                $stopTerminal = Get-FileTransferEventField -Event $event -Name 'terminal' -Default ''
                if ($eventSequence -gt $staleSequence -and $stopTerminal -eq '1') {
                    $terminalResolutionObserved = $true
                    break
                }
                if ($stopTerminal -eq '0') {
                    $currentInstall = $null
                    $currentConsumerProven = $false
                }
                continue
            }

            if ($eventSequence -gt $staleSequence -and
                $event.EventName -eq 'filetransfer_v6_frontier_request_receive_dispatched' -and
                (Test-FileTransferCompactOutboundWorkerInstallDescriptorComplete -Event $event) -and
                (Test-FileTransferCompactOutboundWorkerPublicationTupleMatch -Publication $publication -Candidate $event)) {
                $descriptorDispatchEvents.Add($event) | Out-Null
                if ($null -ne $currentInstall -and
                    (Test-FileTransferCompactOutboundWorkerDispatchTupleMatch -Install $currentInstall -Candidate $event)) {
                    $currentConsumerProven = $true
                }
                continue
            }

            if ($eventSequence -gt $staleSequence -and
                $null -ne $currentInstall -and
                (Test-FileTransferCompactOutboundWorkerProgressMatch `
                    -Install $currentInstall `
                    -Progress $event `
                    -Dispatches $descriptorDispatchEvents.ToArray())) {
                $currentConsumerProven = $true
                continue
            }

            if ($eventSequence -le $staleSequence) {
                continue
            }

            if ($event.EventName -in @(
                    'filetransfer_v6_frontier_request_transport_queued',
                    'filetransfer_v6_frontier_request_transport_rejected') -and
                (Get-FileTransferEventField -Event $event -Name 'frame_type' -Default '') -eq 'filetransfer.frontier_request.v6') {
                $correlationKey = Get-FileTransferCompactOutboundWorkerFrontierCorrelationKey -Event $event
                if (-not [string]::IsNullOrWhiteSpace($correlationKey) -and
                    $matchedFrontierCorrelationKeys.Contains($correlationKey)) {
                    $frontierEvidenceObserved = $true
                }
                continue
            }

            if ($event.EventName -in @(
                    'filetransfer_control_plane_delivery_result',
                    'filetransfer_control_plane_delivery_observed') -and
                (Get-FileTransferEventField -Event $event -Name 'frame_type' -Default '') -eq 'filetransfer.frontier_request.v6' -and
                (Test-FileTransferCompactOutboundWorkerFrontierTupleMatch -Publication $publication -Candidate $event) -and
                (Get-FileTransferEventField -Event $event -Name 'peer_visible_any' -Default '0') -eq '1' -and
                (Get-FileTransferEventField -Event $event -Name 'accepted_any' -Default '0') -eq '1') {
                $correlationKey = Get-FileTransferCompactOutboundWorkerFrontierCorrelationKey -Event $event
                if (-not [string]::IsNullOrWhiteSpace($correlationKey)) {
                    $matchedFrontierCorrelationKeys.Add($correlationKey) | Out-Null
                    $frontierEvidenceObserved = $true
                }
            }
        }

        if (-not $publicationSuperseded -and
            -not $terminalResolutionObserved -and
            $frontierEvidenceObserved -and
            -not $currentConsumerProven) {
            $candidates.Add($staleFailure) | Out-Null
        }
    }

    $firstCandidate = @($candidates.ToArray() | Sort-Object Sequence, LineNumber | Select-Object -First 1)
    return [pscustomobject]@{
        Count = $candidates.Count
        FirstCandidate = if ($firstCandidate.Count -gt 0) { $firstCandidate[0] } else { $null }
    }
}

function Write-FileTransferCompactRetainedAnalysisArtifacts {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)]$Index,
        [System.Collections.IDictionary]$StageDiagnostics = $null,
        [ValidateSet('None', 'SwitchOff', 'MultiToggle', 'RegularActivationCycle')]
        [string]$LiveRouteProofMode = 'None'
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null

    function Invoke-FileTransferCompactCorrelator {
        param(
            [Parameter(Mandatory = $true)][string]$Name,
            [Parameter(Mandatory = $true)][int64]$InputCount,
            [Parameter(Mandatory = $true)][scriptblock]$Action
        )

        $stopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $historyEntry = $null
        if ($null -ne $StageDiagnostics) {
            $correlatorHistory = $StageDiagnostics['correlator_history']
            if ($correlatorHistory -isnot [System.Collections.Generic.List[object]]) {
                $existingHistory = @($correlatorHistory)
                $correlatorHistory =
                    New-Object System.Collections.Generic.List[object]
                foreach ($existingEntry in $existingHistory) {
                    if ($null -ne $existingEntry) {
                        $correlatorHistory.Add($existingEntry) | Out-Null
                    }
                }
                $StageDiagnostics['correlator_history'] = $correlatorHistory
            }
            $startedUtc =
                [datetime]::UtcNow.ToString('O', [System.Globalization.CultureInfo]::InvariantCulture)
            $historyEntry = [ordered]@{
                name = $Name
                input_count = $InputCount
                visit_count = 'unavailable'
                lookup_count = 'unavailable'
                started_utc = $startedUtc
                completed_utc = ''
                elapsed_ms = 0
                status = 'started'
            }
            $correlatorHistory.Add($historyEntry) | Out-Null
            $StageDiagnostics['current_correlator'] = $Name
            $StageDiagnostics['correlator_status'] = 'started'
            $StageDiagnostics['correlator_input_count'] = $InputCount
            $StageDiagnostics['correlator_visit_count'] = 'unavailable'
            $StageDiagnostics['correlator_lookup_count'] = 'unavailable'
            $StageDiagnostics['correlator_started_utc'] = $startedUtc
            $StageDiagnostics['correlator_completed_utc'] = ''
            $StageDiagnostics['correlator_elapsed_ms'] = 0
            Write-FileTransferRetainedAnalysisStageDiagnostics `
                -ArtifactDir $ArtifactDir `
                -Diagnostics $StageDiagnostics
        }

        try {
            $result = & $Action
            $stopwatch.Stop()
            if ($null -ne $StageDiagnostics) {
                $completedUtc =
                    [datetime]::UtcNow.ToString('O', [System.Globalization.CultureInfo]::InvariantCulture)
                $StageDiagnostics['correlator_status'] = 'completed'
                $StageDiagnostics['correlator_completed_utc'] = $completedUtc
                $StageDiagnostics['correlator_elapsed_ms'] = $stopwatch.ElapsedMilliseconds
                if ($null -ne $result -and
                    $null -ne $result.PSObject.Properties['IndexBuildVisitCount']) {
                    $StageDiagnostics['correlator_visit_count'] =
                        $result.PSObject.Properties['IndexBuildVisitCount'].Value
                }
                if ($null -ne $result -and
                    $null -ne $result.PSObject.Properties['BoundedLookupVisitCount']) {
                    $StageDiagnostics['correlator_lookup_count'] =
                        $result.PSObject.Properties['BoundedLookupVisitCount'].Value
                }
                $historyEntry['visit_count'] = $StageDiagnostics['correlator_visit_count']
                $historyEntry['lookup_count'] = $StageDiagnostics['correlator_lookup_count']
                $historyEntry['completed_utc'] = $completedUtc
                $historyEntry['elapsed_ms'] = $stopwatch.ElapsedMilliseconds
                $historyEntry['status'] = 'completed'
                Write-FileTransferRetainedAnalysisStageDiagnostics `
                    -ArtifactDir $ArtifactDir `
                    -Diagnostics $StageDiagnostics
            }
            return $result
        }
        catch {
            $stopwatch.Stop()
            if ($null -ne $StageDiagnostics) {
                $completedUtc =
                    [datetime]::UtcNow.ToString('O', [System.Globalization.CultureInfo]::InvariantCulture)
                $StageDiagnostics['correlator_status'] = 'failed'
                $StageDiagnostics['correlator_completed_utc'] = $completedUtc
                $StageDiagnostics['correlator_elapsed_ms'] = $stopwatch.ElapsedMilliseconds
                $historyEntry['completed_utc'] = $completedUtc
                $historyEntry['elapsed_ms'] = $stopwatch.ElapsedMilliseconds
                $historyEntry['status'] = 'failed'
                Write-FileTransferRetainedAnalysisStageDiagnostics `
                    -ArtifactDir $ArtifactDir `
                    -Diagnostics $StageDiagnostics
            }
            throw
        }
    }

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

    [object[]]$routeChronologyEvents = @(
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_selected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transitioned') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_recovered') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_tuna_gui_live_reactivation_first_transfer_terminal_before_activation') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'transfer_terminal') |
            Sort-Object Sequence, LineNumber
    )
    $routeEvents = @(
        Get-FileTransferCanonicalRouteSelectionEvents -Events $routeChronologyEvents
    )
    $compactLiveRouteProof =
        Get-FileTransferLiveRouteEpochProof `
            -TransferEvents $routeChronologyEvents `
            -Mode $LiveRouteProofMode
    $selectedRouteChanges = @(Get-FileTransferCompactRouteChanges -Events $routeEvents)
    $liveRouteChanges = @($compactLiveRouteProof.RouteChanges)

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
    $fallbackPairedYieldGrantedCount =
        Get-FileTransferCompactIndexEventCount -Index $Index -Name 'filetransfer_fallback_tail_paired_yield_granted'
    $fallbackTerminalProofCount = Get-FileTransferCompactIndexEventCount -Index $Index -Name 'filetransfer_fallback_tail_terminal_proof_consumed'
    $retiredStaleSendCount = Get-FileTransferCompactIndexEventCount -Index $Index -Name 'filetransfer_control_plane_send_retired_stale_generation'
    $nonCurrentSendCount = 0
    $runtimeOwnerStaleRejectedCount = Get-FileTransferCompactIndexEventCount -Index $Index -Name 'filetransfer_runtime_owner_stale_update_rejected'
    [object[]]$runtimeUnlockRetryDispatchInstallEvents = @(
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_decision_staged') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_install_receipt_issued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_install_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_install_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_lifecycle_stage_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_authority_granted')
    )
    [object[]]$runtimeUnlockRetryLifecycleEvents = @(
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_execution_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_lifecycle_execution_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_successor_evaluation_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_lifecycle_successor_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_deadline_evaluation_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_lifecycle_deadline_decided')
    )
    $runtimeUnlockRetryLifecycleProof =
        Get-FileTransferRuntimeUnlockRetryLifecycleCorrelation `
            -Events $runtimeUnlockRetryLifecycleEvents
    $runtimeUnlockRetryDispatchInstallProof =
        Get-FileTransferRuntimeUnlockRetryDispatchInstallProof `
            -Events $runtimeUnlockRetryDispatchInstallEvents
    [object[]]$runtimeUnlockRetryDispatchEffectiveGrantEvents = @(
        $runtimeUnlockRetryDispatchInstallProof.EffectiveGrantEvents
    )
    [object[]]$runtimeUnlockRetryDispatchObservedEvents = @(
        Get-FileTransferCompactIndexEventsByName `
            -Index $Index `
            -Name 'runtime_unlock_retry_dispatch_observed'
    )
    [object[]]$runtimeUnlockRetryDispatchPostGrantHoldEvents = @(
        $runtimeUnlockRetryDispatchInstallEvents +
        $runtimeUnlockRetryLifecycleEvents +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_held_by_active_negotiation') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_held_by_regular_v4_receive_recovery') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_held_for_missing_dispatch_successor') +
        $runtimeUnlockRetryDispatchObservedEvents +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_contract_settled') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_settlement_reported') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_settlement_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_recovery_contract_listener_rearm_completed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_recovery_contract_retry_dispatched') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_recovery_contract_retry_authority_offer_window_refreshed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_successor_intent_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_missing_dispatch_successor_limit_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_chain_closed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_lifecycle_chain_closed')
    )
    $runtimeUnlockRetryDispatchPostGrantHoldProof =
        Get-FileTransferRuntimeUnlockRetryDispatchPostGrantHoldCorrelation `
            -Events $runtimeUnlockRetryDispatchPostGrantHoldEvents `
            -EffectiveGrantEvents $runtimeUnlockRetryDispatchEffectiveGrantEvents
    [object[]]$runtimeUnlockPreCommitProbeStartHandoffEvents = @(
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_launch_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_precommit_probe_launch_handoff_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_launch_held_by_coordinator_denial') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_start_timeout_suppressed_by_coordinator_denial') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_precommit_probe_pending_held') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_precommit_probe_pending_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_launch_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_start_timeout') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_prepared') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_pause_bound') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_draining') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_drain_receipt_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_disposition_applied')
    )
    $runtimeUnlockPreCommitProbeStartHandoffProof =
        Get-FileTransferRuntimeUnlockPreCommitProbeStartHandoffCorrelation `
            -Events $runtimeUnlockPreCommitProbeStartHandoffEvents
    [object[]]$runtimeUnlockResponderPeerProbeHandoffEvents = @(
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_offer_queued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_transaction_answer_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_transaction_path_probe_acked') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_acked') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_peer_precommit_answer_acceptance_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_peer_precommit_answer_acceptance_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_receive_dispatched') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_tuna_probe_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_ack_delivery_result') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_ack_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_tuna_probe_ack_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_paired_commit_confirmed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_paired_commit_confirmed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_ack_pending') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_inbound_envelope_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_ack_received_raw') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_ack_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_answer_ack_probe_barrier_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_responder_answer_probe_convergence_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_handoff_pending_route_commit') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_route_commit_deferred_probe_missing') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_launch_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_responder_waiting_for_peer_probe') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_responder_peer_probe_handoff_bound') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_precommit_probe_launch_handoff_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_precommit_probe_pending') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_ack_authority_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_ignored') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_responder_peer_probe_route_transition_handoff_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_paired_release_reserved') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_paired_release_consumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_install_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_admission_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_pairing_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_session_recreated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_session_removed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_fallback_checkpoint_bound') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_prepared') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_draining') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_disposition_applied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_selected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transitioned') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_leg_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_transport_epoch_adopted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_leg_transport_epoch_adopted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_owner_changed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_recovered') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'transfer_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_inbound_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_terminalized') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_terminalized_by_peer_down') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_terminalized_by_session_end') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_epoch_terminal')
    )
    $runtimeUnlockResponderPeerProbeHandoffProof =
        Get-FileTransferRuntimeUnlockResponderPeerProbeHandoffCorrelation `
            -Events $runtimeUnlockResponderPeerProbeHandoffEvents
    $runtimeUnlockRetryDispatchObservedCount = $runtimeUnlockRetryDispatchObservedEvents.Count
    [object[]]$runtimeUnlockRetryDispatchUnobservedGrantEvents = @(
        $runtimeUnlockRetryDispatchPostGrantHoldProof.UnresolvedGrantEvents
    )
    $runtimeUnlockRetryDispatchProofVerdict = if (
        $runtimeUnlockRetryDispatchEffectiveGrantEvents.Count -gt 0 -and
        $runtimeUnlockRetryDispatchUnobservedGrantEvents.Count -eq 0 -and
        $runtimeUnlockRetryDispatchPostGrantHoldProof.UnresolvedCount -eq 0) {
        'pass'
    }
    elseif ($runtimeUnlockRetryDispatchEffectiveGrantEvents.Count -gt 0 -or
        $runtimeUnlockRetryDispatchPostGrantHoldProof.UnresolvedCount -gt 0) {
        'fail'
    }
    else {
        'none'
    }
    $outboundReceiveWorkerHandoffProof = Get-FileTransferCompactOutboundReceiveWorkerHandoffProof -Index $Index
    [object[]]$runtimeUnlockRegularV4RecoveryLeaseEvents = @(
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_bridge_recovery_authority_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_bridge_recovery_execution_command_issued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_bridge_recovery_execution_start_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_recovery_execution_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_recovery_execution_completed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_recovery_execution_completion_late') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_receive_stall_recovery_soft') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_receive_stall_recovery_soft_failed_hard_restart') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_receive_stall_recovery_hard_restart') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_recovery_execution_hard_restart_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_bridge_recovery_lifecycle_diagnostic_only') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_liveness_heartbeat_skipped_for_active_filetransfer_progress') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_liveness_heartbeat_required_for_receive_recovery_proof_state_recheck') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_liveness_heartbeat_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_liveness_heartbeat_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_liveness_proof_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_liveness_peer_proof_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_recovery_submission_barrier_closed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_recovery_submission_barrier_drain_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_recovery_submission_barrier_drain_completed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_recovery_submission_barrier_drain_invalidated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_recovery_submission_barrier_released') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_recovery_submission_barrier_invalidated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_submission_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_restart_rejected_stale_execution_command') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'bridge_spawned') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'bridge_ready') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_bridge_recovery_execution_receipt_decision_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_bridge_recovery_execution_completion_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_recovery_liveness_lease_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lifecycle_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lease_closure_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lease_closure_retained') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lease_closure_adopted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lease_closure_consumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lease_closure_liveness_deferral_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lease_closure_liveness_deferral_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_joined') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_retry_armed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_join_discarded') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_bounded_closure_retained') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_issued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_execution_start_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_execution_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_execution_receipt_applied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_execution_receipt_decision_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_receipt_applied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_proof_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_proof_decision_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_receive_proof_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_terminal_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_completed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_invalidated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_session_availability_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_epoch_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_hint_authorized_publication_accepted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_post_tuna_epoch_restart_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_closure_retained') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_closure_adopted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_closure_reserved') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_closure_consumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_superseded') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_recovery_evaluation_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_evaluation_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_liveness_timeout_filetransfer_recovery_state_not_deferred') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_offer_queued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_offer_received_raw') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_tuna_dial_attempt_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_tuna_dial_attempt_failed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_tuna_dial_offer_duplicate_ignored') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_tuna_dial_successor_offer_held') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_tuna_dial_successor_offer_replay_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_failed_dial_answer_shadow_bound') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_failed_dial_answer_replay_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_failed_dial_answer_replay_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_replay_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_acceleration_reply_target_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_received_raw') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_retry_scheduled') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_lifecycle_ack_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_offer_authority_boundary_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_runtime_unlock_pending_negotiation_wake_scheduled') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_payer_decision_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_payer_intent_queued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_post_fallback_paired_retry_offer_classified') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_control_priority_failed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_receive_stall_recovery_rejected_no_filetransfer_authority') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_activation_control_send_recovery_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_activation_control_send_recovery_joined_existing') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_offer_replay_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_installed_retry_answer_delivery_recovery_hold_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_listener_sidecar_event') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_state_changed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_reset') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_path_lease_failed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_path_lease_sidecar_unavailable') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_message_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_answer_handoff_boundary_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retired_pending_answer_slot_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_install_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_install_confirmed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_execution_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_lifecycle_execution_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_outbound_offer_retired') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_pending_runtime_unlock_answer_preserved') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_offer_peer_response_timeout_pending_answer_preserved') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_negotiation_slot_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_retired_offer_answer_accepted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_initial_activation_receive_recovery_hold_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_initial_activation_receive_recovery_hold_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_regular_nkn_state_refresh_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_fallback_checkpoint_bound') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_fallback_checkpoint_delivery_receipt_applied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_fallback_checkpoint_delivery_held') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_fallback_checkpoint_delivery_resumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_fallback_checkpoint_delivery_resume_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_fallback_checkpoint_response_timeout_recheck_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_fallback_checkpoint_command_scoped_post_send_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_checkpoint_response_timeout_reissued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_regular_nkn_state_refresh_send_queued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_regular_nkn_state_refresh_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_regular_nkn_state_refresh_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v4_feedback_first_success') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_receiver_state_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_fallback_checkpoint_peer_frame_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_outbound_sparse_worker_handoff_installed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_outbound_sparse_worker_handoff_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_fallback_checkpoint_lifecycle_advanced') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_fallback_checkpoint_invalidated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_leg_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_session_recreated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_transport_epoch_adopted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_leg_transport_epoch_adopted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_epoch_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_terminalized_by_peer_down') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_terminalized_by_session_end') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_disposition_applied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_frontier_request_receive_dispatch_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_frontier_request_ignored_obsolete') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_frontier_request_advanced_remote_frontier') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_frontier_request_receive_dispatch_staged') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_frontier_request_receive_dispatch_coalesced') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_fallback_checkpoint_frontier_request_hold_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_fallback_checkpoint_frontier_request_recheck_issued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_fallback_checkpoint_frontier_request_recheck_applied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_frontier_repair_authority_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_frontier_repair_dispatched') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_frontier_repair_progress_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_frontier_repair_contract_settled') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_frontier_repair_cleared') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_pairing_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_paired_release_reserved') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_paired_release_consumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_yield_blocked_by_frontier_obligation') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_zero_credit_breaker') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_checkpoint_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_clean_fallback_checkpoint_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_transaction_failed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_initial_activation_receive_recovery_hold_rebound') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_initial_activation_receive_recovery_hold_invalidated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_initial_activation_receive_recovery_hold_invalidation_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_initial_activation_receive_recovery_hold_resumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_initial_activation_receive_recovery_hold_directive_delivery_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_initial_activation_receive_recovery_hold_send_prevalidated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_initial_activation_receive_recovery_hold_post_prevalidation_submission') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_initial_activation_offer_envelope_refresh_issued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_initial_activation_offer_envelope_refresh_prepared') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_initial_activation_offer_envelope_refresh_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_initial_activation_offer_envelope_refresh_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_initial_activation_offer_envelope_refresh_physical_resume') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_path_lease_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_path_lease_listener_ready') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_listener_sidecar_event') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_path_lease_failed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_path_lease_sidecar_unavailable') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_sidecar_process_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_sidecar_stop_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_runtime_listener_restart_unlock_expired') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_listener_adoption_applied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_transaction_offer_generation_created') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_offer_received_raw') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_message_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_transaction_answer_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_ack_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_ack_replay_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_ack_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_answer_ack_probe_barrier_held') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_answer_ack_probe_barrier_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_answer_ack_probe_barrier_released') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_handoff_pending_route_commit') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_ack_received_raw') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_ack_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_ack_pending') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_ack_timeout') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_launch_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_precommit_probe_launch_handoff_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_launch_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_acked') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_peer_precommit_answer_acceptance_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_responder_peer_probe_handoff_bound') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_prepared') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_draining') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_activation_control_send_deferred_for_regular_v4_recovery') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_retry_exhausted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_offer_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_activation_offer_not_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_activation_offer_retry_not_scheduled') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_runtime_unlock_retry_after_recovery_armed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_install_rejected') +
        $runtimeUnlockRetryLifecycleEvents +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_contract_settled') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_settlement_reported') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_settlement_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_successor_intent_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_missing_dispatch_successor_limit_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_chain_closed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_lifecycle_chain_closed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_current_authority_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_recovery_authority_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_blocked_by_regular_v4_receive_recovery') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_held_by_regular_v4_receive_recovery') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_rejected_after_regular_v4_receive_recovery') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_preserved') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_pressure_binding_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_pressure_binding_retired') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_authority_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_settlement_accepted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_physical_send_lease_registered') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_bridge_recovery_payload_submission_held') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_bridge_recovery_payload_submission_resumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_peer_reachability_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_proof_state_publication_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_proof_state_publication_attempted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_proof_state_publication_held') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_proof_state_publication_resume_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_proof_state_publication_recheck_armed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_proof_state_publication_recheck_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_proof_state_publication_recheck_invalidated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_proof_state_publication_receipt_applied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_proof_state_publication_invalidated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_transport_paused') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_transport_resumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_tuna_activation_transport_pause_control_suppressed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_tuna_activation_local_barrier_released') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v4_state_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v4_state_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v4_repair_delivery_both_failed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v4_sender_failed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_post_tuna_fallback_recovery_submission_failure_deferred') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_post_tuna_fallback_recovery_submission_failure_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_post_tuna_fallback_recovery_submission_failure_finalized') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_post_tuna_fallback_recovery_submission_failure_resumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_post_tuna_fallback_recovery_submission_resumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_frontier_repair_settlement_handoff_adopted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_authority_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'transfer_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_inbound_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_outbound_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_terminalized')
    )
    # Physical-send retirement streams are intentionally kept out of the shared
    # receive-recovery input. Large transfers can contain hundreds of thousands
    # of these events, while only the submission-barrier drain correlator needs
    # them. Feeding them to every recovery correlator turns otherwise bounded
    # operation lookups into repeated whole-slice scans.
    [object[]]$bridgeRecoveryExecutionSubmissionBarrierDrainRetirementEvents = @(
        @(Get-FileTransferCompactIndexEventsByName `
            -Index $Index `
            -Name 'filetransfer_route_transition_physical_send_lease_retired') +
        @(Get-FileTransferCompactIndexEventsByName `
            -Index $Index `
            -Name 'filetransfer_outbound_physical_send_lease_retired')
    )
    $bridgeRecoveryExecutionSubmissionBarrierDrainIndex =
        New-FileTransferBridgeRecoveryExecutionSubmissionBarrierDrainIndex `
            -SemanticEvents $runtimeUnlockRegularV4RecoveryLeaseEvents `
            -RetirementEvents $bridgeRecoveryExecutionSubmissionBarrierDrainRetirementEvents
    $postTunaDeferredPhysicalLeaseGenerations = @{}
    foreach ($event in $runtimeUnlockRegularV4RecoveryLeaseEvents) {
        if ($event.EventName -ne 'filetransfer_post_tuna_fallback_recovery_submission_failure_deferred') {
            continue
        }
        $leaseGeneration = Get-FileTransferEventField -Event $event -Name 'physical_send_lease_generation' -Default ''
        if (-not [string]::IsNullOrWhiteSpace($leaseGeneration)) {
            $postTunaDeferredPhysicalLeaseGenerations[$leaseGeneration] = $true
        }
    }
    [object[]]$postTunaDeferredPhysicalLeaseRetirementEvents = @(
        $bridgeRecoveryExecutionSubmissionBarrierDrainRetirementEvents |
            Where-Object {
                $leaseGeneration = Get-FileTransferEventField -Event $_ -Name 'send_lease_generation' -Default ''
                -not [string]::IsNullOrWhiteSpace($leaseGeneration) -and
                    $postTunaDeferredPhysicalLeaseGenerations.ContainsKey($leaseGeneration)
            }
    )
    [object[]]$runtimeUnlockRegularV4RecoveryOrderedEvents = @(
        ($runtimeUnlockRegularV4RecoveryLeaseEvents + $postTunaDeferredPhysicalLeaseRetirementEvents) |
            Where-Object { $null -ne $_ } |
            Sort-Object Sequence, LineNumber, EventName -Unique
    )
    [object[]]$bridgeRecoveryExecutionPayloadHoldEvents = @(
        $runtimeUnlockRegularV4RecoveryOrderedEvents
    )
    [object[]]$runtimeUnlockRegularV4RecoveryOrderedEvents = @(
        $runtimeUnlockRegularV4RecoveryOrderedEvents |
            Where-Object {
                $_.EventName -notin @(
                    'filetransfer_route_transition_physical_send_lease_retired',
                    'filetransfer_outbound_physical_send_lease_retired')
            }
    )
    $fileTransferReceiveRecoveryOperationInput = @(
        $runtimeUnlockRegularV4RecoveryLeaseEvents +
        $runtimeUnlockRetryDispatchInstallEvents
    )
    $fileTransferReceiveRecoveryOperationProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'receive_recovery_operation' `
            -InputCount $fileTransferReceiveRecoveryOperationInput.Count `
            -Action {
                Get-FileTransferReceiveRecoveryOperationCorrelation `
                    -Events $fileTransferReceiveRecoveryOperationInput
            }
    $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'receive_recovery_completed_unproven_payload_hold' `
            -InputCount $runtimeUnlockRegularV4RecoveryOrderedEvents.Count `
            -Action {
                Get-FileTransferReceiveRecoveryCompletedUnprovenPayloadHoldCorrelation `
                    -Events $runtimeUnlockRegularV4RecoveryOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'receive_recovery_completed_retained_proof_state_publication' `
            -InputCount $runtimeUnlockRegularV4RecoveryOrderedEvents.Count `
            -Action {
                Get-FileTransferReceiveRecoveryCompletedRetainedProofStatePublicationCorrelation `
                    -Events $runtimeUnlockRegularV4RecoveryOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'receive_recovery_proof_state_recheck_heartbeat_suppression' `
            -InputCount $runtimeUnlockRegularV4RecoveryOrderedEvents.Count `
            -Action {
                Get-FileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionCorrelation `
                    -Events $runtimeUnlockRegularV4RecoveryOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'receive_recovery_completed_unproven_successor' `
            -InputCount $runtimeUnlockRegularV4RecoveryOrderedEvents.Count `
            -Action {
                Get-FileTransferReceiveRecoveryCompletedUnprovenSuccessorCorrelation `
                    -Events $runtimeUnlockRegularV4RecoveryOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $fileTransferReceiveRecoveryCompletedProvenSuccessorProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'receive_recovery_completed_proven_successor' `
            -InputCount $runtimeUnlockRegularV4RecoveryOrderedEvents.Count `
            -Action {
                Get-FileTransferReceiveRecoveryCompletedProvenSuccessorCorrelation `
                    -Events $runtimeUnlockRegularV4RecoveryOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'receive_recovery_post_tuna_runtime_successor_liveness_handoff' `
            -InputCount $runtimeUnlockRegularV4RecoveryOrderedEvents.Count `
            -Action {
                Get-FileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffCorrelation `
                    -Events $runtimeUnlockRegularV4RecoveryOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'receive_recovery_fallback_checkpoint_scope_handoff' `
            -InputCount $runtimeUnlockRegularV4RecoveryOrderedEvents.Count `
            -Action {
                Get-FileTransferReceiveRecoveryFallbackCheckpointScopeHandoffCorrelation `
                    -Events $runtimeUnlockRegularV4RecoveryOrderedEvents
            }
    $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'fallback_frontier_checkpoint_acceptance_recheck' `
            -InputCount $runtimeUnlockRegularV4RecoveryOrderedEvents.Count `
            -Action {
                Get-FileTransferFallbackFrontierCheckpointAcceptanceRecheckCorrelation `
                    -Events $runtimeUnlockRegularV4RecoveryOrderedEvents
            }
    [object[]]$legacyRuntimeUnlockRegularV4RecoveryLeaseEvents = @(
        Remove-FileTransferLegacyReceiveRecoveryEventsCoveredByOperations `
            -Events $runtimeUnlockRegularV4RecoveryOrderedEvents `
            -OperationEvents $runtimeUnlockRegularV4RecoveryOrderedEvents)
    $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'runtime_unlock_regular_v4_recovery_lease' `
            -InputCount $legacyRuntimeUnlockRegularV4RecoveryLeaseEvents.Count `
            -Action {
                Get-FileTransferRuntimeUnlockRegularV4RecoveryLeaseCorrelation `
                    -Events $legacyRuntimeUnlockRegularV4RecoveryLeaseEvents `
                    -EffectiveGrantEvents $runtimeUnlockRetryDispatchEffectiveGrantEvents `
                    -AlreadyChronologicallyOrdered
            }
    $bridgeRecoveryExecutionSoftToHardHandoffProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'bridge_recovery_soft_to_hard_handoff' `
            -InputCount $runtimeUnlockRegularV4RecoveryOrderedEvents.Count `
            -Action {
                Get-FileTransferBridgeRecoveryExecutionSoftToHardHandoffCorrelation `
                    -Events $runtimeUnlockRegularV4RecoveryOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $bridgeRecoveryExecutionStartupPayloadIsolationProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'bridge_recovery_startup_payload_isolation' `
            -InputCount $runtimeUnlockRegularV4RecoveryOrderedEvents.Count `
            -Action {
                Get-FileTransferBridgeRecoveryExecutionStartupPayloadIsolationCorrelation `
                    -Events $runtimeUnlockRegularV4RecoveryOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $bridgeRecoveryExecutionPayloadHoldProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'bridge_recovery_payload_hold' `
            -InputCount $bridgeRecoveryExecutionPayloadHoldEvents.Count `
            -Action {
                Get-FileTransferBridgeRecoveryExecutionPayloadHoldWithPostTunaCorrelation `
                    -Events $bridgeRecoveryExecutionPayloadHoldEvents `
                    -AlreadyChronologicallyOrdered
            }
    $bridgeRecoveryExecutionSubmissionBarrierDrainProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'bridge_recovery_submission_barrier_drain' `
            -InputCount ($runtimeUnlockRegularV4RecoveryLeaseEvents.Count +
                $bridgeRecoveryExecutionSubmissionBarrierDrainRetirementEvents.Count) `
            -Action {
                Get-FileTransferBridgeRecoveryExecutionSubmissionBarrierDrainCorrelation `
                    -Index $bridgeRecoveryExecutionSubmissionBarrierDrainIndex
            }
    $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'runtime_unlock_regular_v4_recovery_closure_offer_timeout' `
            -InputCount $runtimeUnlockRegularV4RecoveryOrderedEvents.Count `
            -Action {
                Get-FileTransferRuntimeUnlockRegularV4RecoveryClosureOfferTimeoutCorrelation `
                    -Events $runtimeUnlockRegularV4RecoveryOrderedEvents `
                    -EffectiveGrantEvents $runtimeUnlockRetryDispatchEffectiveGrantEvents `
                    -AlreadyChronologicallyOrdered
            }
    $runtimeUnlockInitialOfferRetryLifecycleHandoffProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'runtime_unlock_initial_offer_retry_lifecycle_handoff' `
            -InputCount $runtimeUnlockRegularV4RecoveryOrderedEvents.Count `
            -Action {
                Get-FileTransferRuntimeUnlockInitialOfferRetryLifecycleHandoffCorrelation `
                    -Events $runtimeUnlockRegularV4RecoveryOrderedEvents `
                    -EffectiveGrantEvents $runtimeUnlockRetryDispatchEffectiveGrantEvents `
                    -AlreadyChronologicallyOrdered
            }
    $runtimeUnlockInitialActivationAnswerPeerDeliveryProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'runtime_unlock_initial_activation_answer_peer_delivery' `
            -InputCount $runtimeUnlockRegularV4RecoveryOrderedEvents.Count `
            -Action {
                Get-FileTransferRuntimeUnlockInitialActivationAnswerPeerDeliveryCorrelation `
                    -Events $runtimeUnlockRegularV4RecoveryOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $runtimeUnlockInitialOfferReceiveRecoveryHoldProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'runtime_unlock_initial_offer_receive_recovery_hold' `
            -InputCount $runtimeUnlockRegularV4RecoveryOrderedEvents.Count `
            -Action {
                Get-FileTransferRuntimeUnlockInitialOfferReceiveRecoveryHoldCorrelation `
                    -Events $runtimeUnlockRegularV4RecoveryOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'runtime_unlock_initial_activation_receive_recovery_hold_release_delivery' `
            -InputCount $runtimeUnlockRegularV4RecoveryOrderedEvents.Count `
            -Action {
                Get-FileTransferRuntimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryCorrelation `
                    -Events $runtimeUnlockRegularV4RecoveryOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired' `
            -InputCount $runtimeUnlockRegularV4RecoveryOrderedEvents.Count `
            -Action {
                Get-FileTransferRuntimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredCorrelation `
                    -Events $runtimeUnlockRegularV4RecoveryOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired' `
            -InputCount $runtimeUnlockRegularV4RecoveryOrderedEvents.Count `
            -Action {
                Get-FileTransferRuntimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredCorrelation `
                    -Events $runtimeUnlockRegularV4RecoveryOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    [object[]]$runtimeUnlockRetryDispatchAuthorityTupleMismatchEvents = @(
        $runtimeUnlockRetryDispatchInstallEvents +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_decision_tuple_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_current_authority_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_contract_settled') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_settlement_reported') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_settlement_decided')
    )
    $runtimeUnlockRetryDispatchAuthorityTupleMismatchProof =
        Get-FileTransferRuntimeUnlockRetryDispatchAuthorityTupleMismatchCorrelation `
            -Events $runtimeUnlockRetryDispatchAuthorityTupleMismatchEvents `
            -EffectiveGrantEvents $runtimeUnlockRetryDispatchEffectiveGrantEvents `
            -AdditionalDiagnosticEvents $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.CorrelatedStaleAuthorityEvents
    [object[]]$regularV4FrontierRepairProjectionEvents = @(
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_authority_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v4_repair_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v4_repair_scheduled') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v4_repair_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v4_repair_suppressed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v4_repair_filled') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v4_repair_cleared') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_path_send_result') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v4_state_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v4_state_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_progress_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_file_tuna_v4_frontier_repair_progress_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_stalled') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_dispatched') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_missing_dispatch') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_settled_stalled') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_contract_settled') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_peer_silence_replay_cursor_retained') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_peer_silence_replay_cursor_advanced') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_peer_silence_safety_replay_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_settlement_accepted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_settlement_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_settlement_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_projection_retained') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_projection_published') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_projection_cleared') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_projection_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_tail_repair_state_authorized') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_tail_repair_state_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_initial_pressure_install_staged') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_initial_pressure_install_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_initial_pressure_install_accepted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_successor_pressure_install_staged') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_successor_pressure_install_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_successor_pressure_install_accepted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_successor_pressure_install_idempotent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_successor_pressure_install_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_pressure_install_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_pressure_install_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_frontier_repair_precontract_settlement_cached') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_frontier_repair_settlement_released') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_frontier_repair_settlement_held') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_frontier_repair_settlement_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_frontier_repair_settlement_handoff_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_frontier_repair_settlement_handoff_adopted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_frontier_repair_settlement_handoff_consumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_frontier_repair_settlement_handoff_discarded') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_frontier_repair_settlement_handoff_duplicate_ignored') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_frontier_repair_settlement_handoff_held_for_bridge_lifecycle') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_frontier_repair_precontract_settlement_consumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_frontier_repair_settlement_held_for_lease_closure_window') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_settlement_reported') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v4_receive_liveness_summary') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_control_feedback_pressure_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_pressure_binding_staged') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_pressure_binding_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_pressure_binding_retired') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_predecessor_pressure_retired') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_pressure_binding_invalidated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_pressure_settlement_apply_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_pressure_recovery_handoff_prepared') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_pressure_recovery_handoff_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_frontier_repair_pressure_recovery_handoff_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_receive_recovery_operation_issued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_recovery_authority_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_bridge_recovery_authority_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_recovery_liveness_lease_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_liveness_timeout_active_filetransfer_progress_observed_without_recovery_authority') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_liveness_timeout') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lifecycle_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lease_closure_retained') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_session_availability_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_transport_resumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_blocked_by_regular_v4_receive_recovery') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_held_by_regular_v4_receive_recovery') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_regular_v4_recovery_settlement_missing') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_manifest_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_manifest_replaced') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_session_replaced') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_session_recreated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_selected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transitioned') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_recovered') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_owner_changed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_terminalized') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'transfer_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_inbound_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_outbound_terminal')
    )
    [object[]]$regularV4FrontierRepairProjectionOrderedEvents = @(
        $regularV4FrontierRepairProjectionEvents |
            Where-Object { $null -ne $_ } |
            Sort-Object Sequence, LineNumber
    )
    # Bulk send-result evidence is required only by the frontier-stall lifecycle
    # correlator. Keep it out of every other repair correlator so each semantic
    # pass remains bounded by repair/control evidence rather than payload volume.
    [object[]]$regularV4FrontierRepairSemanticOrderedEvents = @(
        $regularV4FrontierRepairProjectionOrderedEvents |
            Where-Object { $_.EventName -ne 'filetransfer_data_path_send_result' }
    )
    $regularV4FrontierRepairMissingRangePreservationProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'regular_v4_missing_range_preservation' `
            -InputCount $regularV4FrontierRepairSemanticOrderedEvents.Count `
            -Action {
                Get-FileTransferRegularV4MissingRangePreservationProof `
                    -Events $regularV4FrontierRepairSemanticOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $regularV4FrontierTailRepairStatePublicationProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'regular_v4_frontier_tail_repair_state_publication' `
            -InputCount $regularV4FrontierRepairSemanticOrderedEvents.Count `
            -Action {
                Get-FileTransferRegularV4FrontierTailRepairStatePublicationProof `
                    -Events $regularV4FrontierRepairSemanticOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $regularV4FrontierRepairProjectionReauthorizationProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'regular_v4_repair_projection_reauthorization' `
            -InputCount $regularV4FrontierRepairSemanticOrderedEvents.Count `
            -Action {
                Get-FileTransferRegularV4RepairProjectionReauthorizationProof `
                    -Events $regularV4FrontierRepairSemanticOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $regularV4PeerSilenceReplayMissingDispatchRangeProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'regular_v4_peer_silence_replay_missing_dispatch_range' `
            -InputCount $regularV4FrontierRepairSemanticOrderedEvents.Count `
            -Action {
                Get-FileTransferRegularV4PeerSilenceReplayMissingDispatchRangeProof `
                    -Events $regularV4FrontierRepairSemanticOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $regularV4PeerSilenceReplayInstalledAuthorityDispatchProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'regular_v4_peer_silence_replay_installed_authority_dispatch' `
            -InputCount $regularV4FrontierRepairSemanticOrderedEvents.Count `
            -Action {
                Get-FileTransferRegularV4PeerSilenceReplayInstalledAuthorityDispatchProof `
                    -Events $regularV4FrontierRepairSemanticOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $regularV4FrontierRepairSettlementDispositionProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'regular_v4_frontier_repair_settlement_disposition' `
            -InputCount $regularV4FrontierRepairSemanticOrderedEvents.Count `
            -Action {
                Get-FileTransferRegularV4FrontierRepairSettlementDispositionProof `
                    -Events $regularV4FrontierRepairSemanticOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    [object[]]$regularV4FrontierRepairLifecycleOrderedEvents = @(
        @($regularV4FrontierRepairProjectionOrderedEvents) +
        @($runtimeUnlockRegularV4RecoveryOrderedEvents) |
            Sort-Object Sequence, LineNumber
    )
    $regularV4FrontierRepairLifecycleProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'regular_v4_frontier_repair_lifecycle' `
            -InputCount $regularV4FrontierRepairLifecycleOrderedEvents.Count `
            -Action {
                Get-FileTransferRegularV4FrontierRepairLifecycleProof `
                    -Events $regularV4FrontierRepairLifecycleOrderedEvents `
                    -SettlementDispositionProof $regularV4FrontierRepairSettlementDispositionProof `
                    -AlreadyChronologicallyOrdered
            }
    $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'regular_v4_frontier_repair_pressure_authority_lifecycle_split' `
            -InputCount $regularV4FrontierRepairSemanticOrderedEvents.Count `
            -Action {
                Get-FileTransferRegularV4FrontierRepairPressureAuthorityLifecycleSplitProof `
                    -Events $regularV4FrontierRepairSemanticOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $regularV4FrontierRepairPressureRecoveryHandoffProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'regular_v4_frontier_repair_pressure_recovery_handoff' `
            -InputCount $regularV4FrontierRepairSemanticOrderedEvents.Count `
            -Action {
                Get-FileTransferRegularV4FrontierRepairPressureRecoveryHandoffProof `
                    -Events $regularV4FrontierRepairSemanticOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $regularV4FrontierRepairSuccessorPressureInstallProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'regular_v4_frontier_repair_successor_pressure_install' `
            -InputCount $regularV4FrontierRepairSemanticOrderedEvents.Count `
            -Action {
                Get-FileTransferRegularV4FrontierRepairSuccessorPressureInstallProof `
                    -Events $regularV4FrontierRepairSemanticOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    $regularV4FrontierRepairInitialPressureInstallProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'regular_v4_frontier_repair_initial_pressure_install' `
            -InputCount $regularV4FrontierRepairSemanticOrderedEvents.Count `
            -Action {
                Get-FileTransferRegularV4FrontierRepairInitialPressureInstallProof `
                    -Events $regularV4FrontierRepairSemanticOrderedEvents `
                    -AlreadyChronologicallyOrdered
            }
    [object[]]$runtimeUnlockRetryFallbackTailUnblockRecheckEvents = @(
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_authority_held') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_authority_hold_preserved') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_authority_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_authority_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_chain_closed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_lifecycle_chain_closed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_recovery_contract_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_recovery_contract_completed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_recovery_contract_failed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_recovery_contract_recovery_settled') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_inbound_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_outbound_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'transfer_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_terminalized') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transitioned') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_selected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_recovered') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_owner_changed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_bridge_recovery_authority_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_paired_yield_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_epoch_tail_unblocked') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_frontier_repair_authority_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_frontier_repair_dispatched') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_path_receive_result') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_frontier_repair_progress_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_sparse_write_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_contiguous_write_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_receiver_state_coalesced') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_fallback_frontier_repair_progress_state_forced') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_receiver_state_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_receiver_state_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_fallback_tail_hold_bound') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_fallback_tail_hold_binding_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_discarded') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_frontier_repair_contract_settled') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_authority_request_published') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_paired_release_reserved') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_paired_release_consumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_offer_queued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_offer_dispatched_after_listener_rearm') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_offer_authority_boundary_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_transaction_answer_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_ack_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_ack_replay_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_launch_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_precommit_probe_launch_handoff_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_launch_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_prepared')
    )
    $runtimeUnlockRetryFallbackTailUnblockRecheckProof =
        Get-FileTransferRuntimeUnlockRetryFallbackTailUnblockRecheckCorrelation `
            -Events $runtimeUnlockRetryFallbackTailUnblockRecheckEvents `
            -EffectiveGrantEvents $runtimeUnlockRetryDispatchEffectiveGrantEvents `
            -Phase2ProtocolObserved $runtimeUnlockRetryDispatchInstallProof.ProtocolObserved
    [object[]]$runtimeUnlockRetryZeroOfferSuccessorEvents = @(
        $runtimeUnlockRetryLifecycleEvents +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_authority_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_missing_after_authority_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_contract_settled') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_settlement_reported') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_settlement_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_missing_dispatch_successor_intent_created') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_successor_evaluation_suppressed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_negotiation_slot_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_paired_release_consumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_successor_intent_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_successor_intent_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_chain_closed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_lifecycle_chain_closed')
    )
    $runtimeUnlockRetryZeroOfferSuccessorProof =
        Get-FileTransferRuntimeUnlockRetryZeroOfferSuccessorCorrelation `
            -Events $runtimeUnlockRetryZeroOfferSuccessorEvents `
            -EffectiveGrantEvents $runtimeUnlockRetryDispatchEffectiveGrantEvents
    $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof =
        Get-FileTransferRuntimeUnlockRetryFallbackPairedYieldSuccessorReleaseCorrelation `
            -Events $runtimeUnlockRetryZeroOfferSuccessorEvents `
            -EffectiveGrantEvents $runtimeUnlockRetryDispatchEffectiveGrantEvents
    [object[]]$runtimeUnlockRetryCurrentListenerAdoptionEvents = @(
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_install_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_install_confirmed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_authority_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_paired_release_reserved') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_paired_release_consumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_listener_adoption_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_path_lease_failed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_path_lease_retired') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_path_lease_listener_retired') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_path_lease_stopped') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_path_lease_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_path_lease_listener_ready') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_sidecar_process_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_sidecar_stop_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_sidecar_stop_completed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_sidecar_process_exited') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_selected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transitioned') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_hint_authorized_publication_accepted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_active_runtime_registered') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_active_runtime_unregistered') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_recovered') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_session_recreated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_terminalized') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'transfer_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_inbound_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_outbound_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_runtime_listener_restart_unlock_expired') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_listener_adoption_applied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_listener_adoption_applied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_offer_queued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_offer_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_offer_received_raw') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'offer_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_tuna_dial_attempt_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_received_raw') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_transaction_answer_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_ack_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_ack_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_answer_ack_received_raw') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_launch_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_launch_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_prepared') |
            Sort-Object Sequence, LineNumber
    )
    $runtimeUnlockRetryCurrentListenerAdoptionProof =
        Get-FileTransferRuntimeUnlockRetryCurrentListenerAdoptionCorrelation `
            -Events $runtimeUnlockRetryCurrentListenerAdoptionEvents
    [object[]]$runtimeUnlockRegularV4RecoverySettlementEvents = @(
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_regular_v4_recovery_settlement_missing') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_blocked_by_regular_v4_receive_recovery') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_held_by_regular_v4_receive_recovery') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_released_after_regular_v4_receive_recovery') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_rejected_after_regular_v4_receive_recovery') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_settlement_reported') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_settlement_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_frontier_repair_precontract_settlement_cached') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_frontier_repair_settlement_handoff_adopted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_frontier_repair_settlement_held') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_frontier_repair_settlement_handoff_held_for_bridge_lifecycle') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lifecycle_reported') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lifecycle_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lease_closure_retained') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lease_closure_adopted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lease_closure_consumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lease_closure_liveness_deferral_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_recovery_lease_closure_liveness_deferral_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_recovery_authority_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_pending_request_rechecked') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_authority_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'session_liveness_timeout_filetransfer_recovery_state_not_deferred') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'transfer_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_inbound_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_outbound_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_terminalized') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_manifest_replaced') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_session_replaced') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_session_recreated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_selected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transitioned') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_owner_changed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_bridge_recovery_authority_granted')
    )
    $runtimeUnlockRegularV4RecoverySettlementProof =
        Get-FileTransferRuntimeUnlockRegularV4RecoverySettlementCorrelation `
            -Events $runtimeUnlockRegularV4RecoverySettlementEvents `
            -EffectiveGrantEvents $runtimeUnlockRetryDispatchEffectiveGrantEvents `
            -ResolvedRepairSettlementEvents $regularV4FrontierRepairSettlementDispositionProof.ResolvedAcceptedEvents
    $runtimeUnlockRetryBlockedByRegularV4RecoveryCount =
        Get-FileTransferCompactIndexEventCount -Index $Index -Name 'runtime_unlock_retry_blocked_by_regular_v4_receive_recovery'
    $runtimeUnlockRetryHeldByRegularV4RecoveryCount =
        Get-FileTransferCompactIndexEventCount -Index $Index -Name 'runtime_unlock_retry_dispatch_held_by_regular_v4_receive_recovery'
    [object[]]$runtimeUnlockRetryRegularV4RecoverySettlementMissingUncoveredEvents = @(
        $runtimeUnlockRegularV4RecoverySettlementProof.UnresolvedCandidateEvents
    )
    [object[]]$runtimeUnlockRetryRegularV4SettlementHandoffBypassedEvents = @(
        $runtimeUnlockRegularV4RecoverySettlementProof.HandoffBypassCandidateEvents
    )
    $runtimeUnlockRetryRegularV4RecoverySettlementMissingCount =
        $runtimeUnlockRegularV4RecoverySettlementProof.UnresolvedCount
    [object[]]$runtimeUnlockPostTunaFallbackTransportProofHandoffEvents = @(
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_post_tuna_fallback_activation_intent_retained') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_post_tuna_fallback_activation_intent_held') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_post_tuna_fallback_activation_intent_recheck_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_post_tuna_fallback_activation_intent_recheck_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_post_tuna_fallback_activation_authority_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_post_tuna_fallback_activation_intent_invalidated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_post_tuna_fallback_activation_disposition') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_precommit_probe_pending_held') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_retry_fired') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_admission_denied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_fallback_survival_resume_held') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_timeline') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_filetransfer_owner_accepted_before_path_lease') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_filetransfer_owner_rejected_before_path_lease') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_filetransfer_owner_missing_before_path_lease') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_filetransfer_owner_stale_before_offer_dispatch') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_path_lease_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_activation_offer_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_activation_offer_queued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_precommit_probe_waiting_for_transport_proof') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_precommit_probe_pending_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_runtime_unlock_dispatch_deferred_for_regular_v4_receive_recovery') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_runtime_unlock_retry_suppressed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_negotiation_scheduling_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_negotiation_scheduling_held') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_negotiation_scheduling_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_negotiation_scheduling_invalidated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_negotiation_scheduling_completed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_selected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_hint_authorized_publication_accepted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_outbound_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_inbound_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'transfer_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_terminalized')
    )
    $runtimeUnlockPostTunaFallbackTransportProofHandoffProof =
        Get-FileTransferRuntimeUnlockPostTunaFallbackTransportProofHandoffCorrelation `
            -Events $runtimeUnlockPostTunaFallbackTransportProofHandoffEvents `
            -EffectiveGrantEvents $runtimeUnlockRetryDispatchEffectiveGrantEvents
    [object[]]$runtimeUnlockUncommittedTunaFallbackHandoffSuppressionEvents = @(
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_post_tuna_fallback_nkn_proved') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_session_availability_broadcast') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_session_availability_invoking') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_post_tuna_fallback_route_hint_suppressed_uncommitted_runtime_unlock') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_admission_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_route_commit_send_drain_deferred') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_route_commit_send_drain_completed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_uncommitted_tuna_fallback_peer_proof_binding_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_uncommitted_tuna_fallback_handoff_retained') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_uncommitted_tuna_fallback_peer_proof_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_uncommitted_tuna_fallback_handoff_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_uncommitted_tuna_fallback_handoff_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_uncommitted_tuna_fallback_handoff_invalidated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transitioned') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_selected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_hint_authorized_publication_accepted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_frame_ignored') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_outbound_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_inbound_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'transfer_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_terminalized') |
            Sort-Object Sequence, LineNumber
    )
    $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof =
        Get-FileTransferRuntimeUnlockUncommittedTunaFallbackProofAuthorityCorrelation `
            -Events $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionEvents
    $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof =
        Get-FileTransferRuntimeUnlockUncommittedTunaFallbackHandoffSuppressionCorrelation `
            -Events $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionEvents
    $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof =
        Get-FileTransferRuntimeUnlockUncommittedTunaFallbackPrecommitAuthorityCorrelation `
            -Events $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionEvents
    [object[]]$fallbackTailPairingEvents = @(
        @($routeEvents) +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_prepared') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_recovered') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_hint_authorized_publication_accepted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_owner_workers_retired') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_terminalized') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_leg_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_leg_authority_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_leg_transport_epoch_adopted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_transport_epoch_adopted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_leg_authority_completed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_leg_authority_superseded_by_route_hint') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_leg_frozen') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_session_recreated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_session_removed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_handoff_epoch_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_handoff_epoch_reopened') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_handoff_epoch_reused') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_epoch_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_epoch_reused') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_epoch_recovered') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_epoch_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_recovery_owner_retired_after_yield') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_retired_owner_update_suppressed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_transport_epoch_reauthorization_staged') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_transport_epoch_reauthorization_rebound') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_transport_epoch_reauthorization_successor_issued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_transport_epoch_reauthorization_invalidated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_yield_issued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_failed_yield_superseded') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_quiescence_timeout') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_frontier_repair_cleared') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_handoff_recovered') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_handoff_tail_unblocked') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_v6_handoff_tail_caught_up_to_accepted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_initial_activation_receive_recovery_hold_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_handoff_held_for_retry_authority') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_paired_tail_yield_runtime_unlock_retry_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_authority_request_queue_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_owner_stale_update_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_admission_denied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_peer_proof_pending_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_peer_proof_pending_bound') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_peer_proof_pending_invalidated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_local_proof_receipt_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_local_proof_receipt_applied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_local_proof_receipt_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_peer_proof_receipt_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_peer_proof_receipt_applied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_peer_proof_receipt_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_pairing_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_paired_release_reserved') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_paired_release_consumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_paired_release_carried_forward') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_quiescent_yield_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_yield_proof_sent') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_yield_proof_received') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_local_yield_blocked') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_paired_yield_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_yield_local_authority_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_yield_proof_stale_ignored') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_owner_generation_mismatch') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_terminal_proof_consumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_authority_request_queued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_negotiation_slot_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_post_fallback_negotiation_deferred') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_post_fallback_retry_intent_recheck_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_runtime_locked') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_payer_decision_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_filetransfer_owner_accepted_before_path_lease') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_payer_intent_queued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_path_lease_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_retry_dispatch_settlement_decided') +
        $runtimeUnlockRetryDispatchInstallEvents +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_terminalized_by_peer_down') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_terminalized_by_session_end') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_session_end_cancel_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_session_end_notice_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'transfer_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_inbound_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_outbound_terminal')
    )
    $fallbackTailFailedYieldQuiescentSuccessorProof =
        Get-FileTransferFallbackTailFailedYieldQuiescentSuccessorCorrelation `
            -Events $fallbackTailPairingEvents
    $fallbackTailPairedYieldValidation =
        Get-FileTransferFallbackTailPairedYieldValidation -Events $fallbackTailPairingEvents
    $fallbackSchema2YieldCount = @($fallbackTailPairedYieldValidation.Schema2YieldEvents).Count
    [object[]]$fallbackTailYieldAuthorityRejectedEvents = @(
        Get-FileTransferFallbackTailYieldAuthorityRejectionEvents -Events $fallbackTailPairingEvents
    )
    $fallbackTailYieldAuthorityRejectedCount =
        $fallbackTailYieldAuthorityRejectedEvents.Count +
        @($fallbackTailPairedYieldValidation.InvalidPairedEvents).Count
    $fallbackOwnerGenerationMismatchRawCount =
        Get-FileTransferCompactIndexEventCount -Index $Index -Name 'filetransfer_fallback_tail_owner_generation_mismatch'
    $fallbackTailBridgeRecoveryPairingProof =
        Get-FileTransferFallbackTailBridgeRecoveryPairingProof -Events $fallbackTailPairingEvents
    $fallbackTailTransportEpochReauthorizationProof =
        Get-FileTransferFallbackTailTransportEpochReauthorizationProof -Events $fallbackTailPairingEvents
    $fallbackTailCoordinatorAggregateProof =
        $fallbackTailTransportEpochReauthorizationProof.CoordinatorAggregateProof
    $fallbackTailYieldAuthorityRejectedCount +=
        $fallbackTailCoordinatorAggregateProof.InvalidUnresolvedCount
    [string[]]$fileTunaV4DataPathEventNames = @(
        Get-FileTransferFileTunaV4DataPathEventNames
    )
    [object[]]$fileTunaV4DataPathDownstreamEvents = @()
    $fileTunaV4RegisteredLeaseEvents = $null
    $fileTunaV4RetiredLeaseEvents = $null
    if ($null -ne $Index.PSObject.Properties['AllEvents']) {
        [string[]]$fileTunaV4PhysicalLeaseEventNames = @(
            'filetransfer_route_transition_physical_send_lease_registered',
            'filetransfer_route_transition_physical_send_lease_retired',
            'filetransfer_route_transition_physical_send_lease_rejected'
        )
        $fileTunaV4SemanticEventList =
            New-Object System.Collections.Generic.List[object]
        foreach ($eventName in @($fileTunaV4DataPathEventNames)) {
            if ($eventName -in $fileTunaV4PhysicalLeaseEventNames) {
                continue
            }
            foreach ($event in @(
                    Get-FileTransferCompactIndexEventsByName `
                        -Index $Index `
                        -Name $eventName)) {
                $fileTunaV4SemanticEventList.Add($event) | Out-Null
            }
        }
        $fileTunaV4DataPathDownstreamEvents = @(
            $fileTunaV4SemanticEventList.ToArray() |
                Sort-Object Sequence, LineNumber
        )
        [object[]]$fileTunaV4DataPathEvents =
            @($fileTunaV4DataPathDownstreamEvents)
        $eventsByName =
            [System.Collections.IDictionary]$Index.PSObject.Properties['EventsByName'].Value
        if ($eventsByName.Contains('filetransfer_route_transition_physical_send_lease_registered') -or
            $eventsByName.ContainsKey('filetransfer_route_transition_physical_send_lease_registered')) {
            $fileTunaV4RegisteredLeaseEvents = $eventsByName.Item(
                'filetransfer_route_transition_physical_send_lease_registered')
        }
        if ($eventsByName.Contains('filetransfer_route_transition_physical_send_lease_retired') -or
            $eventsByName.ContainsKey('filetransfer_route_transition_physical_send_lease_retired')) {
            $fileTunaV4RetiredLeaseEvents = $eventsByName.Item(
                'filetransfer_route_transition_physical_send_lease_retired')
        }
    }
    else {
    [object[]]$fileTunaV4DataPathEvents = @(
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_path_send_result') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_path_receive_result') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_tuna_data_path_peer_visible') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_tuna_data_path_mismatch') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_frame_ignored') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_leg_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_owner_workers_retired') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_route_commit_send_drain_staged') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_route_commit_send_drain_deferred') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_route_commit_send_drain_completed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_regular_v4_route_commit_send_drain_invalidated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_to_tuna_route_transition_prepared') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_to_tuna_route_transition_pause_bound') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_to_tuna_route_transition_draining') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_to_tuna_route_transition_drain_receipt_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_to_tuna_route_transition_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_to_tuna_route_transition_invalidated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_to_tuna_route_transition_disposition_applied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_physical_send_lease_registered') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_regular_v4_physical_send_lease_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_peer_proof_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_prepared') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_pause_bound') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_draining') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_supersession_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_drain_receipt_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_invalidated') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_disposition_applied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_transport_grace_exhausted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_transport_pause_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_retained_frame_applied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_retained_frame_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_yield_issued') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_local_proof_receipt_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_peer_proof_receipt_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_pairing_committed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_tail_paired_yield_granted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_precommit_probe_launch_handoff_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_responder_peer_probe_handoff_bound') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_unlock_responder_peer_probe_route_transition_handoff_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_physical_send_lease_registered') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_physical_send_lease_retired') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transition_physical_send_lease_rejected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_fallback_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_fallback_filetransfer_evidence_requested') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_nkn_proof_pending') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_nkn_proof_unconfirmed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_nkn_proof_waiting_for_v6_epoch') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_nkn_probe_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'nkn_bridge_post_tuna_fallback_proof_send_window_skipped') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_v6_proof_bootstrap_observed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_v6_proof_bootstrap_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_v6_proof_bootstrap_execution_completed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_v6_proof_bootstrap_receipt_applied') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_v6_proof_bootstrap_peer_frame_decided') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_fallback_v6_proof_bootstrap_peer_boundary_bound') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_post_tuna_fallback_v6_route_consumed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_selected') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_hint_authorized_publication_accepted') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_data_session_availability_changed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_transport_availability_changed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_availability_changed') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'runtime_unlock_precommit_probe_acked') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_route_transitioned') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_started') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_recovered') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_reset') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_acceleration_timeline') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'tuna_runtime_locked') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_outbound_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'file_transfer_inbound_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'transfer_terminal') +
        @(Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_runtime_terminalized') |
            Sort-Object Sequence, LineNumber
    )
    }
    $fileTunaV4RouteTransitionInputCount = [int64]$fileTunaV4DataPathEvents.Count
    if ($null -ne $fileTunaV4RegisteredLeaseEvents) {
        $fileTunaV4RouteTransitionInputCount +=
            [int64]$fileTunaV4RegisteredLeaseEvents.Count
    }
    if ($null -ne $fileTunaV4RetiredLeaseEvents) {
        $fileTunaV4RouteTransitionInputCount +=
            [int64]$fileTunaV4RetiredLeaseEvents.Count
    }
    $dataPathCorrelationStopwatch =
        [System.Diagnostics.Stopwatch]::StartNew()
    $fileTransferRouteTransitionProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'filetransfer_route_transition' `
            -InputCount $fileTunaV4RouteTransitionInputCount `
            -Action {
                Get-FileTransferRouteTransitionCorrelation `
                    -Events $fileTunaV4DataPathEvents `
                    -RegisteredLeaseEvents $fileTunaV4RegisteredLeaseEvents `
                    -RetiredLeaseEvents $fileTunaV4RetiredLeaseEvents `
                    -AlreadyChronologicallyOrdered
            }
    if ($fileTunaV4DataPathDownstreamEvents.Count -eq 0) {
        $fileTunaV4DataPathDownstreamEvents = @(
            $fileTunaV4DataPathEvents | Where-Object {
                $_.EventName -notin @(
                    'filetransfer_route_transition_physical_send_lease_registered',
                    'filetransfer_route_transition_physical_send_lease_retired',
                    'filetransfer_route_transition_physical_send_lease_rejected')
            }
        )
    }
    $fileTransferGuiLiveFallbackFaultWindowProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'filetransfer_gui_live_fallback_fault_window' `
            -InputCount $fileTunaV4DataPathDownstreamEvents.Count `
            -Action {
                Get-FileTransferGuiLiveFallbackFaultWindowCorrelation `
                    -Events $fileTunaV4DataPathDownstreamEvents `
                    -LiveRouteProofMode $LiveRouteProofMode
            }
    $fileTransferRouteTransitionLiveEpochProjectionProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'filetransfer_route_transition_live_epoch_projection' `
            -InputCount $fileTunaV4DataPathDownstreamEvents.Count `
            -Action {
                Get-FileTransferRouteTransitionLiveEpochProjectionCorrelation `
                    -Events $fileTunaV4DataPathDownstreamEvents
            }
    $fileTransferRouteTransitionDrainingTransportGraceProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'filetransfer_route_transition_draining_transport_grace' `
            -InputCount $fileTunaV4DataPathDownstreamEvents.Count `
            -Action {
                Get-FileTransferRouteTransitionDrainingTransportGraceCorrelation `
                    -Events $fileTunaV4DataPathDownstreamEvents
            }
    $dataPathIndexStopwatch =
        [System.Diagnostics.Stopwatch]::StartNew()
    $fileTunaV4DataPathCorrelationIndex =
        Invoke-FileTransferCompactCorrelator `
            -Name 'filetransfer_data_path_index' `
            -InputCount $fileTunaV4DataPathDownstreamEvents.Count `
            -Action {
                New-FileTransferFileTunaV4DataPathCorrelationIndex `
                    -Events $fileTunaV4DataPathDownstreamEvents
            }
    $dataPathIndexStopwatch.Stop()
    $runtimeUnlockRegularV4RouteCommitSendDrainProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'runtime_unlock_regular_v4_route_commit_send_drain' `
            -InputCount $fileTunaV4DataPathDownstreamEvents.Count `
            -Action {
                Get-FileTransferRuntimeUnlockRegularV4RouteCommitSendDrainCorrelation `
                    -Events $fileTunaV4DataPathDownstreamEvents `
                    -DataPathIndex $fileTunaV4DataPathCorrelationIndex
            }
    $fileTunaV4DataPathMismatchProof =
        Invoke-FileTransferCompactCorrelator `
            -Name 'filetransfer_tuna_v4_data_path_mismatch' `
            -InputCount $fileTunaV4DataPathDownstreamEvents.Count `
            -Action {
                Get-FileTransferFileTunaV4DataPathMismatchProof `
                    -Events $fileTunaV4DataPathDownstreamEvents `
                    -RouteCommitSendDrainProof $runtimeUnlockRegularV4RouteCommitSendDrainProof `
                    -DataPathIndex $fileTunaV4DataPathCorrelationIndex
            }
    $dataPathCorrelationStopwatch.Stop()
    $validFallbackTerminalProofCount =
        @($fallbackTailBridgeRecoveryPairingProof.ValidTerminalProofEvents).Count
    $fallbackPairedYield =
        @($fallbackTailCoordinatorAggregateProof.ValidPairingEvents).Count -gt 0 -or
        @($fallbackTailBridgeRecoveryPairingProof.LifecycleValidPairedEvents).Count -gt 0 -or
        ($fallbackSchema2YieldCount -eq 0 -and
            $fallbackTailCoordinatorAggregateProof.AuthoritativeEventCount -eq 0 -and
            $fallbackOwnerGenerationMismatchRawCount -eq 0 -and
            ($fallbackPairedYieldGrantedCount -gt 0 -or
                ($fallbackOutboundYieldCount -gt 0 -and $fallbackInboundYieldCount -gt 0)))
    [long[]]$fallbackTailTransportEpochCorroboratingMismatchSequences = @(
        $fallbackTailTransportEpochReauthorizationProof.CorroboratingMismatchSequences)
    $fallbackTailOwnerGenerationMismatchCount = @(
        $fallbackTailBridgeRecoveryPairingProof.OtherOwnerGenerationMismatchEvents |
            Where-Object {
                [int64]$_.Sequence -notin $fallbackTailTransportEpochCorroboratingMismatchSequences
            }).Count
    [string[]]$postFallbackGenericPauseEventNames = @(
        'filetransfer_tuna_activation_negotiation_regular_nkn_paused',
        'filetransfer_data_session_availability_observed',
        'filetransfer_post_tuna_fallback_tuna_activation_pause_suppressed',
        'tuna_payer_decision_started',
        'tuna_acceleration_payer_intent_queued',
        'runtime_unlock_retry_dispatch_authority_granted',
        'filetransfer_route_transition_disposition_applied',
        'filetransfer_route_transition_prepared',
        'filetransfer_route_selected',
        'filetransfer_runtime_terminalized',
        'filetransfer_live_route_epoch_terminal',
        'filetransfer_v6_epoch_terminal',
        'file_transfer_outbound_terminal',
        'transfer_terminal')
    [object[]]$postFallbackGenericPauseEvents = @(
        Get-FileTransferCompactIndexEventsByNamesOrdered `
            -Index $Index `
            -Names $postFallbackGenericPauseEventNames
    )
    $postFallbackGenericPauseProof =
        Get-FileTransferPostTunaFallbackGenericActivationPauseCorrelation `
            -Events $postFallbackGenericPauseEvents
    $compactProofChainCandidates = New-Object System.Collections.Generic.List[object]
    foreach ($candidateEvent in @($postFallbackGenericPauseProof.UnresolvedEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_fallback_separation_violation'
            Event = $candidateEvent
            Reason = 'post_tuna_fallback_regular_nkn_paused_by_stale_retry_receipt_despite_current_pause_suppression'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockRetryDispatchInstallProof.GrantEvidenceUnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete'
            Event = $candidateEvent
            Reason = Get-FileTransferEventField `
                -Event $candidateEvent `
                -Name 'decision_reason' `
                -Default 'retry_dispatch_grant_install_chain_incomplete'
        }) | Out-Null
    }
    foreach ($candidateEvent in @(
            $runtimeUnlockRetryRegularV4RecoverySettlementMissingUncoveredEvents |
                Where-Object {
                    Test-FileTransferRuntimeUnlockRecoverySettlementMissingFirstEdgeEligible `
                        -MissingEvent $_ `
                        -SettlementEvents $regularV4FrontierRepairProjectionEvents `
                        -DispositionGapEvents $regularV4FrontierRepairSettlementDispositionProof.UnresolvedCandidateEvents
                })) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_retry_regular_v4_recovery_settlement_missing'
            Event = $candidateEvent
            Reason = Get-FileTransferEventField `
                -Event $candidateEvent `
                -Name 'decision_reason' `
                -Default 'regular_v4_recovery_settlement_missing'
        }) | Out-Null
    }
    foreach ($candidateEvent in $runtimeUnlockRetryRegularV4SettlementHandoffBypassedEvents) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_retry_regular_v4_settlement_handoff_bypassed'
            Event = $candidateEvent
            Reason = 'regular_v4_recovery_settlement_handoff_bypassed'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockPostTunaFallbackTransportProofHandoffProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing'
            Event = $candidateEvent
            Reason = 'scheduled_runtime_unlock_handoff_not_started'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_precommit_fallback_recovery_handoff_suppressed'
            Event = $candidateEvent
            Reason = 'transport_recovered_availability_suppressed_with_uncommitted_runtime_unlock'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound'
            Event = $candidateEvent
            Reason = 'proof_boundary_not_current'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing'
            Event = $candidateEvent
            Reason = 'post_tuna_fallback_proof_completed_without_authority_scope'
        }) | Out-Null
    }
    if ($outboundReceiveWorkerHandoffProof.Count -gt 0 -and
        $null -ne $outboundReceiveWorkerHandoffProof.FirstCandidate) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'post_tuna_fallback_outbound_receive_worker_handoff_missing'
            Event = $outboundReceiveWorkerHandoffProof.FirstCandidate
            Reason = Get-FileTransferEventField `
                -Event $outboundReceiveWorkerHandoffProof.FirstCandidate `
                -Name 'reason' `
                -Default 'runtime_generation_mismatch'
        }) | Out-Null
    }
    $fallbackRepairSettlementRecheckSequences = @{}
    foreach ($candidateEvent in @($runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementUnresolvedCandidateEvents)) {
        $fallbackRepairSettlementRecheckSequences[[int64]$candidateEvent.Sequence] = $true
    }
    $fallbackRepairHoldBindingMissingSequences = @{}
    foreach ($candidateEvent in @($runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingUnresolvedCandidateEvents)) {
        $fallbackRepairHoldBindingMissingSequences[[int64]$candidateEvent.Sequence] = $true
    }
    $fallbackRepairProgressStateSequences = @{}
    foreach ($candidateEvent in @($runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairProgressStateUnresolvedCandidateEvents)) {
        $fallbackRepairProgressStateSequences[[int64]$candidateEvent.Sequence] = $true
    }
    foreach ($candidateEvent in @($runtimeUnlockRetryFallbackTailUnblockRecheckProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing'
            Event = $candidateEvent
            Reason = if ($fallbackRepairHoldBindingMissingSequences.ContainsKey([int64]$candidateEvent.Sequence)) {
                'unbound_held_fallback_tail_retry_could_not_recheck_after_blocking_repair_settlement'
            }
            elseif ($fallbackRepairSettlementRecheckSequences.ContainsKey([int64]$candidateEvent.Sequence)) {
                'settled_fallback_frontier_repair_did_not_republish_held_paired_tail_retry_request'
            }
            elseif ($fallbackRepairProgressStateSequences.ContainsKey([int64]$candidateEvent.Sequence)) {
                'fallback_frontier_repair_progress_receiver_state_coalesced_without_peer_publication'
            }
            else {
                'fallback_tail_unblock_retry_recheck_missing'
            }
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockRetryZeroOfferSuccessorProof.UnresolvedCandidateEvents)) {
        $candidateReason = if ($candidateEvent.EventName -in @(
                'runtime_unlock_retry_missing_dispatch_successor_intent_created',
                'runtime_unlock_retry_dispatch_successor_evaluation_suppressed')) {
            'pre_offer_missing_dispatch_successor_evaluation_suppressed_as_default'
        }
        else {
            'retry_dispatch_successor_retired_offer_mismatch'
        }
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_retry_dispatch_zero_offer_successor_rejected'
            Event = $candidateEvent
            Reason = $candidateReason
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingUnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_retry_dispatch_current_listener_adoption_missing'
            Event = $candidateEvent
            Reason = 'live_coordinator_scoped_initial_activation_listener_reported_missing_for_fallback_paired_retry'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockRetryCurrentListenerAdoptionProof.NoCurrentListenerUnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_retry_dispatch_current_listener_adoption_missing'
            Event = $candidateEvent
            Reason = 'installed_fallback_retry_did_not_adopt_current_coordinator_scoped_listener'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredUnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_retry_dispatch_current_listener_adoption_missing'
            Event = $candidateEvent
            Reason = 'installed_fallback_retry_did_not_adopt_current_coordinator_scoped_listener'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid'
            Event = $candidateEvent
            Reason = 'retry_dispatch_predecessor_release_invalid'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockRetryDispatchAuthorityTupleMismatchProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_retry_dispatch_authority_tuple_mismatch'
            Event = $candidateEvent
            Reason = Get-FileTransferEventField `
                -Event $candidateEvent `
                -Name 'decision_reason' `
                -Default 'runtime_unlock_retry_dispatch_authority_tuple_mismatch'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($fallbackTailBridgeRecoveryPairingProof.CandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'post_tuna_fallback_paired_yield_bridge_recovery_generation_mismatch'
            Event = $candidateEvent
            Reason = 'bridge_recovery_generation_mismatch'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($fallbackTailTransportEpochReauthorizationProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing'
            Event = $candidateEvent
            Reason = if ($candidateEvent.EventName -eq 'filetransfer_fallback_tail_local_yield_blocked') {
                'local_yield_transport_epoch_reauthorization_missing'
            }
            else {
                'retired_owner_cross_transport_epoch_suppression'
            }
        }) | Out-Null
    }
    foreach ($candidateRecord in @($runtimeUnlockRegularV4RouteCommitSendDrainProof.UnresolvedCandidateRecords)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_regular_v4_route_commit_send_drain_missing'
            Event = $candidateRecord.Event
            Reason = $candidateRecord.Reason
        }) | Out-Null
    }
    foreach ($candidateRecord in @($fileTransferRouteTransitionProof.UnresolvedCandidateRecords)) {
        $candidateClass = if (-not [string]::IsNullOrWhiteSpace([string]$candidateRecord.Class)) {
            [string]$candidateRecord.Class
        }
        else {
            'filetransfer_route_transition_incomplete'
        }
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = $candidateClass
            Event = $candidateRecord.Event
            Reason = $candidateRecord.Reason
        }) | Out-Null
    }
    foreach ($candidateRecord in @(
            $fileTransferGuiLiveFallbackFaultWindowProof.UnresolvedCandidateRecords)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'filetransfer_gui_live_fallback_fault_window_missed'
            Event = $candidateRecord.Event
            Reason = $candidateRecord.Reason
        }) | Out-Null
    }
    foreach ($candidateRecord in @(
            $fileTransferRouteTransitionLiveEpochProjectionProof.UnresolvedCandidateRecords)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'filetransfer_live_route_epoch_projection_missing'
            Event = $candidateRecord.Event
            Reason = $candidateRecord.Reason
        }) | Out-Null
    }
    foreach ($candidateRecord in @(
            $fileTransferRouteTransitionDrainingTransportGraceProof.UnresolvedCandidateRecords)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class =
                'filetransfer_route_transition_draining_transport_grace_terminalization'
            Event = $candidateRecord.Event
            Reason =
                'coordinator_route_transition_draining_treated_as_transport_disconnect'
        }) | Out-Null
    }
    if ($fileTunaV4DataPathMismatchProof.EffectiveCount -gt 0) {
        foreach ($candidateEvent in @($fileTunaV4DataPathMismatchProof.EffectiveEvents)) {
            $compactProofChainCandidates.Add([pscustomobject]@{
                Class = 'file_tuna_data_path_mismatch'
                Event = $candidateEvent
                Reason = 'file_tuna_data_path_mismatch'
            }) | Out-Null
        }
    }
    foreach ($candidateEvent in @(
            $fallbackTailYieldAuthorityRejectedEvents +
            @($fallbackTailPairedYieldValidation.InvalidPairedEvents) +
            @($fallbackTailCoordinatorAggregateProof.InvalidEvents))) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'fallback_tail_yield_authority_invalid'
            Event = $candidateEvent
            Reason = Get-FileTransferEventField `
                -Event $candidateEvent `
                -Name 'decision_reason' `
                -Default 'fallback_tail_yield_authority_invalid'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($fileTransferReceiveRecoveryOperationProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'filetransfer_receive_recovery_operation_incomplete'
            Event = $candidateEvent
            Reason = 'receive_recovery_operation_terminal_handoff_incomplete'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'filetransfer_receive_recovery_completed_unproven_successor_missing'
            Event = $candidateEvent
            Reason = 'completed_unproven_receive_recovery_operation_blocked_fresh_recovery'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($fileTransferReceiveRecoveryCompletedProvenSuccessorProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'filetransfer_receive_recovery_completed_proven_successor_missing'
            Event = $candidateEvent
            Reason = 'completed_proven_unreserved_receive_recovery_operation_blocked_fresh_recovery'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_regular_v4_recovery_lease_not_issued'
            Event = $candidateEvent
            Reason = 'coordinator_recovery_liveness_lease_missing'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedUnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_regular_v4_recovery_lease_closure_not_adopted'
            Event = $candidateEvent
            Reason = 'regular_v4_receive_recovery_pending'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessUnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved'
            Event = $candidateEvent
            Reason = if ($candidateEvent.EventName -eq
                    'runtime_unlock_regular_v4_recovery_lease_closure_rejected') {
                'coordinator_recovery_liveness_lease_stale'
            }
            else {
                'recovery_liveness_lease_missing'
            }
        }) | Out-Null
    }
    foreach ($candidateEvent in @($bridgeRecoveryExecutionSoftToHardHandoffProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing'
            Event = $candidateEvent
            Reason = 'bridge_recovery_execution_timeout_before_soft_to_hard_fallback'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($bridgeRecoveryExecutionStartupPayloadIsolationProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'filetransfer_bridge_recovery_execution_startup_payload_isolation_missing'
            Event = $candidateEvent
            Reason = 'bridge_recovery_execution_timeout_after_pre_ready_payload_submission'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($bridgeRecoveryExecutionPayloadHoldProof.UnresolvedCandidateEvents)) {
        $postTunaVariant = $candidateEvent -in @(
            $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredUnresolvedCandidateEvents)
        $lateObservationVariant = $candidateEvent -in @(
            $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationUnresolvedCandidateEvents)
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'filetransfer_bridge_recovery_execution_payload_hold_missing'
            Event = $candidateEvent
            Reason = if ($lateObservationVariant) {
                'exact_deferred_submission_epoch_rejection_observed_after_completed_proven_recovery_treated_as_terminal_sender_failure'
            }
            elseif ($postTunaVariant) {
                'current_post_tuna_fallback_recovery_submission_epoch_transition_treated_as_terminal_sender_failure'
            }
            else {
                'bridge_recovery_submission_hold_rejection_escalated_to_terminal'
            }
        }) | Out-Null
    }
    foreach ($candidateEvent in @($fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved'
            Event = $candidateEvent
            Reason =
                'completed_unproven_receive_recovery_payload_resumed_before_proof'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.UnresolvedCandidateEvents)) {
        $publicationReason = Get-FileTransferEventField `
            -Event $candidateEvent `
            -Name 'publication_reason' `
            -Default ''
        $failureReason = if (
            $candidateEvent.EventName -eq
                'filetransfer_receive_recovery_proof_state_publication_receipt_applied' -and
            $publicationReason -in @(
                'filetransfer_receive_recovery_proof_state_not_sent',
                'filetransfer_receive_recovery_proof_state_held_for_tuna_activation_pause')) {
            'coordinator_authorized_receive_recovery_proof_state_not_held_during_tuna_activation_pause'
        }
        else {
            'completed_retained_receive_recovery_peer_reachability_not_rechecked_for_v4_state_publication'
        }
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'filetransfer_receive_recovery_completed_retained_proof_state_publication_missing'
            Event = $candidateEvent
            Reason = $failureReason
        }) | Out-Null
    }
    foreach ($candidateEvent in @($fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed'
            Event = $candidateEvent
            Reason =
                'completed_retained_receive_recovery_peer_heartbeat_suppressed_by_outbound_progress'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($bridgeRecoveryExecutionSubmissionBarrierDrainProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'filetransfer_bridge_recovery_execution_submission_barrier_drain_missing'
            Event = $candidateEvent
            Reason = 'bridge_recovery_execution_restart_not_ready'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedUnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved'
            Event = $candidateEvent
            Reason = 'v4_recovery_terminal_no_proof'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing'
            Event = $candidateEvent
            Reason = if ($candidateEvent.EventName -eq 'tuna_acceleration_activation_offer_retry_not_scheduled') {
                'regular_v4_recovery_closure_window_fresh_chain_not_created'
            }
            else {
                'regular_v4_recovery_closure_window_join_missing'
            }
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockInitialOfferRetryLifecycleHandoffProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_initial_offer_retry_lifecycle_handoff_missing'
            Event = $candidateEvent
            Reason = if (
                $candidateEvent.EventName -eq 'filetransfer_runtime_unlock_handoff_pending_route_commit') {
                'accepted_retired_pending_answer_not_promoted_to_runtime_unlock_transaction'
            }
            elseif (
                $candidateEvent.EventName -eq 'runtime_unlock_retry_dispatch_install_rejected' -and
                (Get-FileTransferEventField -Event $candidateEvent -Name 'decision_reason' -Default '') -eq
                    'retry_dispatch_install_state_missing_answer_received') {
                'initial_offer_answer_misclassified_as_retry_install'
            }
            else {
                'runtime_unlock_offer_send_not_observed_retry_install_state_missing'
            }
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockInitialActivationAnswerPeerDeliveryProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_initial_activation_answer_peer_delivery_missing'
            Event = $candidateEvent
            Reason = if (@(
                    $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.InstalledRetryRecoveryJoinUnresolvedCandidateEvents |
                        Where-Object {
                            [int64]$_.Sequence -eq [int64]$candidateEvent.Sequence
                        }).Count -gt 0) {
                'post_fallback_installed_retry_offer_replay_failed_to_join_existing_filetransfer_recovery'
            }
            elseif (@(
                    $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.InstalledRetryPendingAnswerClearedUnresolvedCandidateEvents |
                        Where-Object {
                            [int64]$_.Sequence -eq [int64]$candidateEvent.Sequence
                        }).Count -gt 0) {
                'installed_retry_pending_answer_cleared_before_failed_dial_answer_arrived'
            }
            elseif (@(
                    $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.InstalledRetryAnswerContinuationWakeUnresolvedCandidateEvents |
                        Where-Object {
                            [int64]$_.Sequence -eq [int64]$candidateEvent.Sequence
                        }).Count -gt 0) {
                'initial_activation_wake_started_before_installed_retry_answer_continuation_completed'
            }
            elseif (@(
                $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AnswerContinuationSlotCollisionUnresolvedCandidateEvents |
                    Where-Object {
                        [int64]$_.Sequence -eq [int64]$candidateEvent.Sequence
                    }).Count -gt 0) {
                'retry_successor_slot_reserved_before_initial_activation_answer_continuation_completed'
            }
            elseif (@(
                $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AnswerHandoffTransferIdentityUnresolvedCandidateEvents |
                    Where-Object {
                        [int64]$_.Sequence -eq [int64]$candidateEvent.Sequence
                    }).Count -gt 0) {
                'accepted_initial_activation_answer_handoff_rejected_on_unresolved_transfer_identity'
            }
            elseif (@(
                $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.ReceivedAnswerAuthorityRejectedUnresolvedCandidateEvents |
                    Where-Object {
                        [int64]$_.Sequence -eq [int64]$candidateEvent.Sequence
                    }).Count -gt 0) {
                'validated_offer_answer_received_but_authority_tuple_rejected'
            }
            elseif (@(
                $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.InstalledRetryPendingSuccessorUnresolvedCandidateEvents |
                    Where-Object {
                        [int64]$_.Sequence -eq [int64]$candidateEvent.Sequence
                    }).Count -gt 0) {
                'installed_retry_offer_pending_duplicate_not_replayed_after_other_dial_failure'
            }
            elseif (@(
                $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerUnresolvedCandidateEvents |
                    Where-Object {
                        [int64]$_.Sequence -eq [int64]$candidateEvent.Sequence
                    }).Count -gt 0) {
                'exact_runtime_unlock_dial_failure_rejection_not_replayed_after_same_offer_refresh'
            }
            elseif (@(
                $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnUnresolvedCandidateEvents |
                    Where-Object {
                        [int64]$_.Sequence -eq [int64]$candidateEvent.Sequence
                    }).Count -gt 0) {
                'exact_source_answer_ack_not_delivered_to_responder_before_wire_deadline'
            }
            elseif (@(
                $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AnswerAckSubmissionBarrierUnresolvedCandidateEvents |
                    Where-Object {
                        [int64]$_.Sequence -eq [int64]$candidateEvent.Sequence
                    }).Count -gt 0) {
                'runtime_unlock_answer_ack_submission_failed_during_bridge_recovery_before_probe_handoff'
            }
            elseif (@(
                $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.PeerRecoveryReplayWindowUnresolvedCandidateEvents |
                    Where-Object {
                        [int64]$_.Sequence -eq [int64]$candidateEvent.Sequence
                    }).Count -gt 0) {
                'exact_source_answer_replay_exhausted_before_receive_stalled_peer_replacement_ready'
            }
            else {
                'validated_offer_answer_not_delivered_to_initiator'
            }
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockInitialOfferReceiveRecoveryHoldProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_initial_offer_receive_recovery_hold_not_preserved'
            Event = $candidateEvent
            Reason = 'initial_activation_retry_exhausted_while_receive_recovery_awaiting_proof'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing'
            Event = $candidateEvent
            Reason = if ($candidateEvent.EventName -eq
                'runtime_unlock_initial_activation_offer_envelope_refresh_rejected') {
                'released_hold_refresh_predecessor_boundary_not_current'
            }
            elseif ($candidateEvent.EventName -eq
                'runtime_unlock_initial_activation_receive_recovery_hold_directive_delivery_decided') {
                'released_hold_directive_delivery_rejected'
            }
            elseif ($candidateEvent.EventName -eq
                'runtime_unlock_initial_activation_receive_recovery_hold_send_prevalidated') {
                'committed_refresh_successor_envelope_not_accepted_by_release_prevalidation'
            }
            else {
                'released_hold_routed_through_exact_invalidation_boundary'
            }
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired'
            Event = $candidateEvent
            Reason = 'initial_activation_offer_expired_during_receive_recovery_hold'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired'
            Event = $candidateEvent
            Reason = 'initial_activation_path_lease_listener_expired_during_receive_recovery_hold'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($regularV4FrontierRepairMissingRangePreservationProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'regular_v4_frontier_repair_missing_range_not_preserved'
            Event = $candidateEvent
            Reason = 'unresolved_frontier_repair_missing_range_omitted'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($regularV4FrontierTailRepairStatePublicationProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'regular_v4_frontier_tail_repair_state_publication_missing'
            Event = $candidateEvent
            Reason = 'coordinator_projection_authority_missing'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($regularV4FrontierRepairProjectionReauthorizationProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'regular_v4_frontier_repair_projection_reauthorization_missing_after_recovery'
            Event = $candidateEvent
            Reason = Get-FileTransferEventField `
                -Event $candidateEvent `
                -Name 'decision_reason' `
                -Default 'fresh_projection_required_before_unrestricted_state'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($regularV4PeerSilenceReplayMissingDispatchRangeProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved'
            Event = $candidateEvent
            Reason = 'peer_silence_replay_range_rotated_after_missing_dispatch'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($regularV4PeerSilenceReplayInstalledAuthorityDispatchProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'regular_v4_peer_silence_replay_installed_authority_not_carried_to_dispatch'
            Event = $candidateEvent
            Reason = 'installed_peer_silence_repair_authority_not_carried_to_dispatch'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($regularV4FrontierRepairSettlementDispositionProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'regular_v4_frontier_repair_settlement_disposition_unverifiable'
            Event = $candidateEvent
            Reason = 'accepted_settlement_disposition_evidence_missing'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'regular_v4_frontier_repair_pressure_authority_lifecycle_split'
            Event = $candidateEvent
            Reason = 'coordinator_repair_authority_outlived_legacy_pressure_ttl'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($regularV4FrontierRepairPressureRecoveryHandoffProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'regular_v4_frontier_repair_pressure_recovery_handoff_missing'
            Event = $candidateEvent
            Reason = 'installed_repair_pressure_not_retired_before_receive_recovery'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($regularV4FrontierRepairLifecycleProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'regular_v4_frontier_repair_stalled'
            Event = $candidateEvent
            Reason = Get-FileTransferEventField `
                -Event $candidateEvent `
                -Name 'reason' `
                -Default 'regular_v4_frontier_repair_stalled'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($regularV4FrontierRepairInitialPressureInstallProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'regular_v4_frontier_repair_initial_pressure_install_missing'
            Event = $candidateEvent
            Reason = 'regular_v4_frontier_repair_pressure_missing'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($regularV4FrontierRepairSuccessorPressureInstallProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'regular_v4_frontier_repair_successor_pressure_install_missing'
            Event = $candidateEvent
            Reason = 'regular_v4_frontier_repair_pressure_missing'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockRetryDispatchPostGrantHoldProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_retry_dispatch_held_after_authority_granted'
            Event = $candidateEvent
            Reason = 'retry_dispatch_held_after_authority_granted'
        }) | Out-Null
    }
    foreach ($candidateEvent in @($runtimeUnlockPreCommitProbeStartHandoffProof.UnresolvedCandidateEvents)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_precommit_probe_start_handoff_missing'
            Event = $candidateEvent
            Reason = if ($candidateEvent.EventName -eq
                    'filetransfer_runtime_unlock_precommit_probe_launch_handoff_decided' -and
                (Get-FileTransferEventInt64Field -Event $candidateEvent -Name 'drain_generation' -Default -1) -eq 0) {
                    'post_fallback_initiator_probe_launch_accepted_without_route_transition_authority'
                }
                else {
                    'runtime_unlock_probe_start_timeout'
                }
        }) | Out-Null
    }
    foreach ($candidateRecord in @($runtimeUnlockResponderPeerProbeHandoffProof.UnresolvedCandidateRecords)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'runtime_unlock_responder_acked_probe_route_transition_handoff_missing'
            Event = $candidateRecord.Event
            Reason = $candidateRecord.Reason
            Variant = $candidateRecord.Variant
        }) | Out-Null
    }
    foreach ($candidateRecord in @($fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.UnresolvedCandidateRecords)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing'
            Event = $candidateRecord.Event
            Reason = $candidateRecord.Reason
        }) | Out-Null
    }
    foreach ($candidateRecord in @($fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.UnresolvedCandidateRecords)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing'
            Event = $candidateRecord.Event
            Reason = $candidateRecord.Reason
        }) | Out-Null
    }
    foreach ($candidateRecord in @($fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.UnresolvedCandidateRecords)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing'
            Event = $candidateRecord.Event
            Reason = $candidateRecord.Reason
        }) | Out-Null
    }
    foreach ($candidateRecord in @($fallbackTailFailedYieldQuiescentSuccessorProof.UnresolvedCandidateRecords)) {
        $compactProofChainCandidates.Add([pscustomobject]@{
            Class = 'paired_fallback_tail_yield_missing'
            Event = $candidateRecord.Event
            Reason = $candidateRecord.Reason
            Variant = $candidateRecord.Variant
        }) | Out-Null
    }
    $firstCompactProofChainFailure = @(
        $compactProofChainCandidates.ToArray() |
            Sort-Object @{ Expression = { [int64]$_.Event.Sequence } }, @{ Expression = { [int]$_.Event.LineNumber } }, Class |
            Select-Object -First 1
    )
    $firstCompactProofChainFailure = if ($firstCompactProofChainFailure.Count -gt 0) {
        $firstCompactProofChainFailure[0]
    }
    else {
        $null
    }
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
    elseif (-not $guiIntegrityOk -or
        $hardFailureCount -gt 0 -or
        $activeFileTunaV6Count -gt 0 -or
        $compactLiveRouteProof.Verdict -eq 'fail') {
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

    if ($runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteUnresolvedCount -gt 0 -or
        $regularV4FrontierRepairSettlementDispositionProof.UnresolvedCount -gt 0 -or
        $regularV4FrontierRepairPressureRecoveryHandoffProof.UnresolvedCount -gt 0 -or
        $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.UnresolvedCount -gt 0 -or
        $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.UnresolvedCount -gt 0) {
        $verdict = 'INCONCLUSIVE'
        $gateStatus = 'fail'
        $acceptanceFailureClass = 'incomplete_proof'
    }
    elseif ($runtimeUnlockRetryDispatchPostGrantHoldProof.UnresolvedCount -gt 0 -or
        $fileTransferReceiveRecoveryOperationProof.UnresolvedCount -gt 0 -or
        $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.UnresolvedCount -gt 0 -or
        $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.UnresolvedCount -gt 0 -or
        $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.UnresolvedCount -gt 0 -or
        $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.UnresolvedCount -gt 0 -or
        $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.UnresolvedCount -gt 0 -or
        $fallbackTailFailedYieldQuiescentSuccessorProof.UnresolvedCount -gt 0 -or
        $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.UnresolvedCount -gt 0 -or
        $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedUnresolvedCount -gt 0 -or
        $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessUnresolvedCount -gt 0 -or
        $bridgeRecoveryExecutionPayloadHoldProof.UnresolvedCount -gt 0 -or
        $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.UnresolvedCount -gt 0 -or
        $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.UnresolvedCount -gt 0 -or
        $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.UnresolvedCount -gt 0 -or
        $bridgeRecoveryExecutionSubmissionBarrierDrainProof.UnresolvedCount -gt 0 -or
        $bridgeRecoveryExecutionStartupPayloadIsolationProof.UnresolvedCount -gt 0 -or
        $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.UnresolvedCount -gt 0 -or
        $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.UnresolvedCount -gt 0 -or
        $runtimeUnlockResponderPeerProbeHandoffProof.UnresolvedCount -gt 0 -or
        $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.UnresolvedCount -gt 0 -or
        $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.UnresolvedCount -gt 0 -or
        $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.UnresolvedCount -gt 0 -or
        $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.UnresolvedCount -gt 0 -or
        $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.UnresolvedCount -gt 0 -or
        $runtimeUnlockRetryFallbackTailUnblockRecheckProof.UnresolvedCount -gt 0 -or
        $runtimeUnlockRetryCurrentListenerAdoptionProof.UnresolvedCount -gt 0 -or
        $runtimeUnlockRegularV4RecoverySettlementProof.HandoffBypassCount -gt 0 -or
        $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.UnresolvedCount -gt 0 -or
        $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.UnresolvedCount -gt 0 -or
        $fallbackTailTransportEpochReauthorizationProof.UnresolvedCount -gt 0 -or
        $regularV4FrontierTailRepairStatePublicationProof.UnresolvedCount -gt 0 -or
        $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.UnresolvedCount -gt 0 -or
        $fileTransferRouteTransitionDrainingTransportGraceProof.UnresolvedCount -gt 0 -or
        $fileTransferRouteTransitionProof.UnresolvedCount -gt 0 -or
        $runtimeUnlockRegularV4RouteCommitSendDrainProof.UnresolvedCount -gt 0) {
        $verdict = 'FAIL_PROTOCOL_OR_INTEGRITY'
        $gateStatus = 'fail'
        $acceptanceFailureClass = 'route_runtime'
    }
    elseif ($compactProofChainCandidates.Count -gt 0) {
        $verdict = 'INCONCLUSIVE'
        $gateStatus = 'fail'
        $acceptanceFailureClass = 'incomplete_proof'
    }

    # The compact result must be classified from the same chronological first
    # proof edge used for its first-edge fields.  Aggregate later failures
    # cannot promote an earlier incomplete-proof boundary to route_runtime (or
    # vice versa).
    if ($null -ne $firstCompactProofChainFailure) {
        $firstCompactClass = [string]$firstCompactProofChainFailure.Class
        if ($firstCompactClass -eq 'runtime_unlock_precommit_probe_start_handoff_missing' -and
            [string]$firstCompactProofChainFailure.Reason -eq
                'post_fallback_initiator_probe_launch_accepted_without_route_transition_authority') {
            $verdict = 'FAIL_PROTOCOL_OR_INTEGRITY'
            $gateStatus = 'fail'
            $acceptanceFailureClass = 'route_runtime'
        }
        elseif ($firstCompactClass -in @(
                'filetransfer_gui_live_fallback_fault_window_missed',
                'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete',
                'runtime_unlock_retry_regular_v4_recovery_settlement_missing',
                'runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved',
                'runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid',
                'filetransfer_live_route_epoch_projection_missing',
                'runtime_unlock_precommit_probe_start_handoff_missing',
                'runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing',
                'runtime_unlock_precommit_fallback_recovery_handoff_suppressed',
                'regular_v4_frontier_repair_settlement_disposition_unverifiable',
                'regular_v4_frontier_repair_initial_pressure_install_missing',
                'regular_v4_frontier_repair_successor_pressure_install_missing',
                'regular_v4_frontier_repair_projection_reauthorization_missing_after_recovery',
                'regular_v4_frontier_repair_pressure_recovery_handoff_missing',
                'regular_v4_frontier_repair_stalled')) {
            $verdict = 'INCONCLUSIVE'
            $gateStatus = 'fail'
            $acceptanceFailureClass = 'incomplete_proof'
        }
        elseif ($firstCompactClass -in @(
                'filetransfer_receive_recovery_operation_incomplete',
                'filetransfer_receive_recovery_completed_unproven_successor_missing',
                'filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved',
                'filetransfer_receive_recovery_completed_retained_proof_state_publication_missing',
                'filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed',
                'filetransfer_receive_recovery_completed_proven_successor_missing',
                'filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing',
                'filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing',
                'filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing',
                'runtime_unlock_regular_v4_recovery_lease_not_issued',
                'runtime_unlock_regular_v4_recovery_lease_closure_not_adopted',
                'runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved',
                'filetransfer_bridge_recovery_execution_payload_hold_missing',
                'filetransfer_bridge_recovery_execution_submission_barrier_drain_missing',
                'filetransfer_bridge_recovery_execution_startup_payload_isolation_missing',
                'filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing',
                'runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing',
                'runtime_unlock_initial_offer_retry_lifecycle_handoff_missing',
                'runtime_unlock_responder_acked_probe_route_transition_handoff_missing',
                'runtime_unlock_initial_activation_answer_peer_delivery_missing',
                'runtime_unlock_initial_offer_receive_recovery_hold_not_preserved',
                'runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing',
                'runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired',
                'runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired',
                'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing',
                'runtime_unlock_retry_dispatch_zero_offer_successor_rejected',
                'runtime_unlock_retry_dispatch_held_after_authority_granted',
                'runtime_unlock_retry_dispatch_current_listener_adoption_missing',
                'runtime_unlock_retry_dispatch_authority_tuple_mismatch',
                'runtime_fallback_separation_violation',
                'regular_v4_frontier_tail_repair_state_publication_missing',
                'regular_v4_frontier_repair_missing_range_not_preserved',
                'regular_v4_frontier_repair_pressure_authority_lifecycle_split',
                'regular_v4_peer_silence_replay_installed_authority_not_carried_to_dispatch',
                'runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing',
                'runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound',
                'fallback_tail_yield_authority_invalid',
                'paired_fallback_tail_yield_missing',
                'filetransfer_route_transition_draining_transport_grace_terminalization',
                'filetransfer_route_transition_tuna_v4_to_post_tuna_fallback_v6_proof_bootstrap_missing',
                'filetransfer_route_transition_incomplete',
                'runtime_unlock_regular_v4_route_commit_send_drain_missing')) {
            $verdict = 'FAIL_PROTOCOL_OR_INTEGRITY'
            $gateStatus = 'fail'
            $acceptanceFailureClass = 'route_runtime'
        }
        elseif ($firstCompactClass -eq 'file_tuna_data_path_mismatch') {
            $verdict = 'FAIL_PROTOCOL_OR_INTEGRITY'
            $gateStatus = 'fail'
            $acceptanceFailureClass = 'route_runtime'
        }
    }

    $recoveryFailureClass = if ($null -ne $firstCompactProofChainFailure) {
        [string]$firstCompactProofChainFailure.Class
    }
    elseif ($verdict -eq 'PASS') {
        'none'
    }
    else {
        $acceptanceFailureClass
    }
    $firstProofChainFailure = if ($null -ne $firstCompactProofChainFailure) {
        $firstCompactProofChainFailure.Event
    }
    else {
        $null
    }
    $firstProofChainFailureClass = if ($null -ne $firstCompactProofChainFailure) {
        [string]$firstCompactProofChainFailure.Class
    }
    else {
        '(none)'
    }
    $firstProofChainFailureEvent = if ($null -ne $firstProofChainFailure) {
        [string]$firstProofChainFailure.EventName
    }
    else {
        '(none)'
    }
    $firstProofChainFailureReason = if ($null -ne $firstCompactProofChainFailure) {
        [string]$firstCompactProofChainFailure.Reason
    }
    else {
        '(none)'
    }
    $failureVariant = if ($null -ne $firstCompactProofChainFailure -and
        [string]$firstCompactProofChainFailure.Class -in @(
            'runtime_unlock_responder_acked_probe_route_transition_handoff_missing',
            'paired_fallback_tail_yield_missing') -and
        $null -ne $firstCompactProofChainFailure.PSObject.Properties['Variant'] -and
        -not [string]::IsNullOrWhiteSpace([string]$firstCompactProofChainFailure.Variant)) {
        [string]$firstCompactProofChainFailure.Variant
    }
    else {
        '(none)'
    }
    $firstProofChainFailureSequence = if ($null -ne $firstProofChainFailure) { [int64]$firstProofChainFailure.Sequence } else { -1L }
    $firstProofChainFailureSourcePath = if ($null -ne $firstProofChainFailure) { [string]$firstProofChainFailure.FilePath } else { '(none)' }
    $firstProofChainFailureLineNumber = if ($null -ne $firstProofChainFailure) { [int]$firstProofChainFailure.LineNumber } else { 0 }
    $firstProofChainFailureWriterSequence = if ($null -ne $firstProofChainFailure) {
        Get-FileTransferEventInt64Field -Event $firstProofChainFailure -Name 'log_writer_sequence' -Default 0
    }
    else {
        0L
    }
    $semanticFailureDomain = if ($firstProofChainFailureClass -eq
        'filetransfer_gui_live_fallback_fault_window_missed') {
        'evidence_harness'
    }
    elseif ($firstProofChainFailureClass -eq
        'filetransfer_live_route_epoch_projection_missing') {
        'route_evidence'
    }
    elseif ($firstProofChainFailureClass -in @(
            'filetransfer_receive_recovery_operation_incomplete',
            'filetransfer_receive_recovery_completed_unproven_successor_missing',
            'filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved',
            'filetransfer_receive_recovery_completed_retained_proof_state_publication_missing',
            'filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed',
            'filetransfer_receive_recovery_completed_proven_successor_missing',
            'filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing',
            'filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing',
            'filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing',
            'filetransfer_route_transition_draining_transport_grace_terminalization',
            'filetransfer_route_transition_tuna_v4_to_post_tuna_fallback_v6_proof_bootstrap_missing',
            'filetransfer_route_transition_incomplete',
            'runtime_unlock_regular_v4_route_commit_send_drain_missing',
            'runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved',
            'filetransfer_bridge_recovery_execution_payload_hold_missing',
            'filetransfer_bridge_recovery_execution_submission_barrier_drain_missing',
            'filetransfer_bridge_recovery_execution_startup_payload_isolation_missing',
            'filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing',
            'runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing',
            'runtime_unlock_initial_offer_retry_lifecycle_handoff_missing',
            'runtime_unlock_responder_acked_probe_route_transition_handoff_missing',
            'runtime_unlock_initial_activation_answer_peer_delivery_missing',
            'runtime_unlock_initial_offer_receive_recovery_hold_not_preserved',
            'runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing',
            'runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired',
            'runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired',
            'runtime_unlock_retry_dispatch_held_after_authority_granted',
            'runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing',
            'runtime_fallback_separation_violation',
            'runtime_unlock_retry_dispatch_current_listener_adoption_missing',
            'runtime_unlock_precommit_probe_start_handoff_missing',
            'runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing',
            'runtime_unlock_precommit_fallback_recovery_handoff_suppressed',
            'runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound',
            'runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid',
            'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing',
            'fallback_tail_yield_authority_invalid',
            'paired_fallback_tail_yield_missing',
            'regular_v4_frontier_tail_repair_state_publication_missing',
            'regular_v4_frontier_repair_projection_reauthorization_missing_after_recovery',
            'regular_v4_peer_silence_replay_installed_authority_not_carried_to_dispatch',
            'regular_v4_frontier_repair_pressure_authority_lifecycle_split',
            'regular_v4_frontier_repair_pressure_recovery_handoff_missing')) {
        'route_runtime'
    }
    else {
        '(none)'
    }

    $runtimeUnlockProbeCommitVerdict = if ($probeAckCount -gt 0 -and $pairedCommitCount -gt 0) { 'pass' } else { 'none' }
    $fallbackTailQuiescenceVerdict = if ($fallbackPairedYield -or $validFallbackTerminalProofCount -gt 0) { 'pass' } elseif ($fallbackYieldCount -gt 0 -or $fallbackTailCoordinatorAggregateProof.YieldIssuedCount -gt 0) { 'fail' } elseif ($guiCompleted) { 'pass' } else { 'pending' }
    $fallbackTailPairedYieldVerdict = if ($fallbackPairedYield -or $validFallbackTerminalProofCount -gt 0) { 'pass' } elseif ($fallbackYieldCount -gt 0 -or $fallbackTailCoordinatorAggregateProof.YieldIssuedCount -gt 0) { 'fail' } else { 'none' }
    $liveRouteProofVerdict = [string]$compactLiveRouteProof.Verdict
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
        ("runtime_correctness_verdict={0}" -f ($(if ([string]::Equals($semanticFailureDomain, 'route_runtime', [System.StringComparison]::Ordinal)) { 'fail' } elseif ([string]::Equals($acceptanceFailureClass, 'incomplete_proof', [System.StringComparison]::Ordinal)) { 'incomplete_proof' } else { 'pass' }))),
        ("hard_failure_count={0}" -f $hardFailureCount),
        ("warning_count={0}" -f $warningKinds.Count),
        ("warning_kinds={0}" -f ($(if ($warningKinds.Count -gt 0) { $warningKinds -join ',' } else { '(none)' }))),
        ("recovery_failure_class={0}" -f $recoveryFailureClass),
        ("failure_variant={0}" -f $failureVariant),
        ("first_proof_chain_failure_class={0}" -f $firstProofChainFailureClass),
        ("first_proof_chain_failure_event={0}" -f $firstProofChainFailureEvent),
        ("first_proof_chain_failure_reason={0}" -f $firstProofChainFailureReason),
        ("first_proof_chain_failure_sequence={0}" -f $firstProofChainFailureSequence),
        ("first_proof_chain_failure_source_path={0}" -f $firstProofChainFailureSourcePath),
        ("first_proof_chain_failure_line_number={0}" -f $firstProofChainFailureLineNumber),
        ("first_proof_chain_failure_writer_sequence={0}" -f $firstProofChainFailureWriterSequence),
        ("semantic_failure_domain={0}" -f $semanticFailureDomain),
        ("filetransfer_gui_live_fallback_fault_window_missed_raw_count={0}" -f $fileTransferGuiLiveFallbackFaultWindowProof.RawCount),
        ("filetransfer_gui_live_fallback_fault_window_missed_distinct_count={0}" -f $fileTransferGuiLiveFallbackFaultWindowProof.DistinctCount),
        ("filetransfer_gui_live_fallback_fault_window_missed_recovered_count=0"),
        ("filetransfer_gui_live_fallback_fault_window_missed_unresolved_count={0}" -f $fileTransferGuiLiveFallbackFaultWindowProof.UnresolvedCount),
        ("filetransfer_gui_live_fallback_fault_window_missed_verdict={0}" -f $fileTransferGuiLiveFallbackFaultWindowProof.Verdict),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_raw_count={0}" -f $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingRawCount),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_distinct_count={0}" -f $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingDistinctCount),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_recovered_count={0}" -f $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingRecoveredCount),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_unresolved_count={0}" -f $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingUnresolvedCount),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_verdict={0}" -f ($(if ($fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingUnresolvedCount -gt 0) { 'fail' } else { 'pass' }))),
        ("filetransfer_live_route_epoch_projection_missing_raw_count={0}" -f $fileTransferRouteTransitionLiveEpochProjectionProof.RawCount),
        ("filetransfer_live_route_epoch_projection_missing_distinct_count={0}" -f $fileTransferRouteTransitionLiveEpochProjectionProof.DistinctCount),
        ("filetransfer_live_route_epoch_projection_missing_recovered_count={0}" -f $fileTransferRouteTransitionLiveEpochProjectionProof.RecoveredCount),
        ("filetransfer_live_route_epoch_projection_missing_unresolved_count={0}" -f $fileTransferRouteTransitionLiveEpochProjectionProof.UnresolvedCount),
        ("filetransfer_route_transition_incomplete_raw_count={0}" -f $fileTransferRouteTransitionProof.RawCount),
        ("filetransfer_route_transition_incomplete_distinct_count={0}" -f $fileTransferRouteTransitionProof.DistinctCount),
        ("filetransfer_route_transition_incomplete_recovered_count={0}" -f $fileTransferRouteTransitionProof.RecoveredCount),
        ("filetransfer_route_transition_incomplete_unresolved_count={0}" -f $fileTransferRouteTransitionProof.UnresolvedCount),
        ("filetransfer_route_transition_verdict={0}" -f $fileTransferRouteTransitionProof.Verdict),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_raw_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.RawCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_distinct_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.DistinctCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_recovered_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.RecoveredCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_unresolved_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.UnresolvedCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_verdict={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.Verdict),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.RawCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.DistinctCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_recovered_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.RecoveredCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.UnresolvedCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_verdict={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.Verdict),
        ("file_tuna_v4_data_path_mismatch_count={0}" -f $fileTunaV4DataPathMismatchProof.EffectiveCount),
        ("runtime_unlock_retry_dispatch_authority_granted_raw_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.AuthorityGrantedRawCount),
        ("runtime_unlock_retry_dispatch_decision_staged_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.DecisionStagedCount),
        ("runtime_unlock_retry_dispatch_install_receipt_issued_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.ReceiptIssuedCount),
        ("runtime_unlock_retry_dispatch_install_committed_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.CommittedCount),
        ("runtime_unlock_retry_dispatch_install_rejected_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.RejectedCount),
        ("runtime_unlock_retry_dispatch_authority_granted_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.EffectiveGrantEvents.Count),
        ("runtime_unlock_retry_dispatch_observed_count={0}" -f $runtimeUnlockRetryDispatchObservedCount),
        ("runtime_unlock_retry_dispatch_install_verdict={0}" -f $runtimeUnlockRetryDispatchInstallProof.Verdict),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_raw_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteRawCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_distinct_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteDistinctCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_recovered_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteRecoveredCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_unresolved_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteUnresolvedCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_verdict={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceVerdict),
        ("runtime_unlock_retry_lifecycle_execution_observation_count={0}" -f $runtimeUnlockRetryLifecycleProof.ExecutionObservationCount),
        ("runtime_unlock_retry_lifecycle_execution_decision_count={0}" -f $runtimeUnlockRetryLifecycleProof.ExecutionDecisionCount),
        ("runtime_unlock_retry_lifecycle_execution_unresolved_count={0}" -f @($runtimeUnlockRetryLifecycleProof.UnresolvedExecutionObservationEvents).Count),
        ("runtime_unlock_retry_lifecycle_successor_evaluation_count={0}" -f $runtimeUnlockRetryLifecycleProof.SuccessorEvaluationCount),
        ("runtime_unlock_retry_lifecycle_successor_decision_count={0}" -f $runtimeUnlockRetryLifecycleProof.SuccessorDecisionCount),
        ("runtime_unlock_retry_lifecycle_successor_unresolved_count={0}" -f @($runtimeUnlockRetryLifecycleProof.UnresolvedSuccessorEvaluationEvents).Count),
        ("runtime_unlock_retry_lifecycle_deadline_evaluation_count={0}" -f $runtimeUnlockRetryLifecycleProof.DeadlineEvaluationCount),
        ("runtime_unlock_retry_lifecycle_deadline_decision_count={0}" -f $runtimeUnlockRetryLifecycleProof.DeadlineDecisionCount),
        ("runtime_unlock_retry_lifecycle_deadline_unresolved_count={0}" -f @($runtimeUnlockRetryLifecycleProof.UnresolvedDeadlineEvaluationEvents).Count),
        ("runtime_unlock_retry_lifecycle_verdict={0}" -f $runtimeUnlockRetryLifecycleProof.Verdict),
        ("runtime_unlock_retry_dispatch_proof_verdict={0}" -f $runtimeUnlockRetryDispatchProofVerdict),
        ("runtime_unlock_retry_dispatch_post_grant_hold_count={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldProof.RawCount),
        ("runtime_unlock_retry_dispatch_post_grant_hold_raw_count={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldProof.RawCount),
        ("runtime_unlock_retry_dispatch_post_grant_hold_distinct_count={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldProof.DistinctCount),
        ("runtime_unlock_retry_dispatch_post_grant_hold_recovered_count={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldProof.RecoveredCount),
        ("runtime_unlock_retry_dispatch_post_grant_hold_unresolved_count={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldProof.UnresolvedCount),
        ("runtime_unlock_retry_dispatch_post_grant_hold_verdict={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldProof.Verdict),
        ("runtime_unlock_precommit_probe_start_handoff_missing_raw_count={0}" -f $runtimeUnlockPreCommitProbeStartHandoffProof.RawCount),
        ("runtime_unlock_precommit_probe_start_handoff_missing_distinct_count={0}" -f $runtimeUnlockPreCommitProbeStartHandoffProof.DistinctCount),
        ("runtime_unlock_precommit_probe_start_handoff_missing_recovered_count={0}" -f $runtimeUnlockPreCommitProbeStartHandoffProof.RecoveredCount),
        ("runtime_unlock_precommit_probe_start_handoff_missing_unresolved_count={0}" -f $runtimeUnlockPreCommitProbeStartHandoffProof.UnresolvedCount),
        ("runtime_unlock_precommit_probe_start_handoff_verdict={0}" -f $runtimeUnlockPreCommitProbeStartHandoffProof.Verdict),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_raw_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.RawCount),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_distinct_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.DistinctCount),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_recovered_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.RecoveredCount),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_unresolved_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.UnresolvedCount),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_verdict={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.Verdict),
        ("runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_raw_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.InitialActivationPeerAcceptanceScopeRejectedRawCount),
        ("runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_distinct_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.InitialActivationPeerAcceptanceScopeRejectedDistinctCount),
        ("runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_recovered_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.InitialActivationPeerAcceptanceScopeRejectedRecoveredCount),
        ("runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_unresolved_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.InitialActivationPeerAcceptanceScopeRejectedUnresolvedCount),
        ("runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_verdict={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.InitialActivationPeerAcceptanceScopeRejectedVerdict),
        ("runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_raw_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AckBeforeProbeAdoptionRecheckMissingRawCount),
        ("runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_distinct_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AckBeforeProbeAdoptionRecheckMissingDistinctCount),
        ("runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_recovered_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AckBeforeProbeAdoptionRecheckMissingRecoveredCount),
        ("runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_unresolved_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AckBeforeProbeAdoptionRecheckMissingUnresolvedCount),
        ("runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_verdict={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AckBeforeProbeAdoptionRecheckMissingVerdict),
        ("runtime_unlock_responder_acked_probe_binding_missing_raw_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.BindingMissingRawCount),
        ("runtime_unlock_responder_acked_probe_binding_missing_distinct_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.BindingMissingDistinctCount),
        ("runtime_unlock_responder_acked_probe_binding_missing_recovered_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.BindingMissingRecoveredCount),
        ("runtime_unlock_responder_acked_probe_binding_missing_unresolved_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.BindingMissingUnresolvedCount),
        ("runtime_unlock_responder_acked_probe_binding_missing_verdict={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.BindingMissingVerdict),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_raw_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AfterBoundHandoffMissingRawCount),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_distinct_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AfterBoundHandoffMissingDistinctCount),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_recovered_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AfterBoundHandoffMissingRecoveredCount),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_unresolved_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AfterBoundHandoffMissingUnresolvedCount),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_verdict={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AfterBoundHandoffMissingVerdict),
        ("regular_v4_frontier_repair_missing_range_not_preserved_raw_count={0}" -f $regularV4FrontierRepairMissingRangePreservationProof.RawCount),
        ("regular_v4_frontier_repair_missing_range_not_preserved_distinct_count={0}" -f $regularV4FrontierRepairMissingRangePreservationProof.DistinctCount),
        ("regular_v4_frontier_repair_missing_range_not_preserved_recovered_count={0}" -f $regularV4FrontierRepairMissingRangePreservationProof.RecoveredCount),
        ("regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count={0}" -f $regularV4FrontierRepairMissingRangePreservationProof.UnresolvedCount),
        ("regular_v4_frontier_repair_missing_range_preservation_verdict={0}" -f $regularV4FrontierRepairMissingRangePreservationProof.Verdict),
        ("regular_v4_frontier_tail_repair_state_publication_missing_raw_count={0}" -f $regularV4FrontierTailRepairStatePublicationProof.RawCount),
        ("regular_v4_frontier_tail_repair_state_publication_missing_distinct_count={0}" -f $regularV4FrontierTailRepairStatePublicationProof.DistinctCount),
        ("regular_v4_frontier_tail_repair_state_publication_missing_recovered_count={0}" -f $regularV4FrontierTailRepairStatePublicationProof.RecoveredCount),
        ("regular_v4_frontier_tail_repair_state_publication_missing_unresolved_count={0}" -f $regularV4FrontierTailRepairStatePublicationProof.UnresolvedCount),
        ("regular_v4_frontier_tail_repair_state_publication_verdict={0}" -f $regularV4FrontierTailRepairStatePublicationProof.Verdict),
        ("regular_v4_frontier_repair_projection_reauthorization_missing_raw_count={0}" -f $regularV4FrontierRepairProjectionReauthorizationProof.RawCount),
        ("regular_v4_frontier_repair_projection_reauthorization_missing_distinct_count={0}" -f $regularV4FrontierRepairProjectionReauthorizationProof.DistinctCount),
        ("regular_v4_frontier_repair_projection_reauthorization_missing_recovered_count={0}" -f $regularV4FrontierRepairProjectionReauthorizationProof.RecoveredCount),
        ("regular_v4_frontier_repair_projection_reauthorization_missing_unresolved_count={0}" -f $regularV4FrontierRepairProjectionReauthorizationProof.UnresolvedCount),
        ("regular_v4_frontier_repair_projection_reauthorization_verdict={0}" -f $regularV4FrontierRepairProjectionReauthorizationProof.Verdict),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_raw_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.RawCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_distinct_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.DistinctCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_recovered_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.RecoveredCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_unresolved_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.UnresolvedCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_preservation_verdict={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.Verdict),
        ("regular_v4_peer_silence_replay_installed_authority_not_carried_to_dispatch_raw_count={0}" -f $regularV4PeerSilenceReplayInstalledAuthorityDispatchProof.RawCount),
        ("regular_v4_peer_silence_replay_installed_authority_not_carried_to_dispatch_distinct_count={0}" -f $regularV4PeerSilenceReplayInstalledAuthorityDispatchProof.DistinctCount),
        ("regular_v4_peer_silence_replay_installed_authority_not_carried_to_dispatch_recovered_count={0}" -f $regularV4PeerSilenceReplayInstalledAuthorityDispatchProof.RecoveredCount),
        ("regular_v4_peer_silence_replay_installed_authority_not_carried_to_dispatch_unresolved_count={0}" -f $regularV4PeerSilenceReplayInstalledAuthorityDispatchProof.UnresolvedCount),
        ("regular_v4_peer_silence_replay_installed_authority_dispatch_verdict={0}" -f $regularV4PeerSilenceReplayInstalledAuthorityDispatchProof.Verdict),
        ("regular_v4_frontier_repair_stalled_raw_count={0}" -f $regularV4FrontierRepairLifecycleProof.RawCount),
        ("regular_v4_frontier_repair_stalled_distinct_count={0}" -f $regularV4FrontierRepairLifecycleProof.DistinctCount),
        ("regular_v4_frontier_repair_stalled_recovered_count={0}" -f $regularV4FrontierRepairLifecycleProof.RecoveredCount),
        ("regular_v4_frontier_repair_stalled_unresolved_count={0}" -f $regularV4FrontierRepairLifecycleProof.UnresolvedCount),
        ("regular_v4_frontier_repair_stalled_verdict={0}" -f $regularV4FrontierRepairLifecycleProof.Verdict),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_raw_count={0}" -f $regularV4FrontierRepairSettlementDispositionProof.RawCount),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_distinct_count={0}" -f $regularV4FrontierRepairSettlementDispositionProof.DistinctCount),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_recovered_count={0}" -f $regularV4FrontierRepairSettlementDispositionProof.RecoveredCount),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_unresolved_count={0}" -f $regularV4FrontierRepairSettlementDispositionProof.UnresolvedCount),
        ("regular_v4_frontier_repair_settlement_disposition_verdict={0}" -f $regularV4FrontierRepairSettlementDispositionProof.Verdict),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_raw_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.RawCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_distinct_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.DistinctCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_recovered_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.RecoveredCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_unresolved_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.UnresolvedCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_verdict={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.Verdict),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_raw_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.RawCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_distinct_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.DistinctCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_recovered_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.RecoveredCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_unresolved_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.UnresolvedCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_verdict={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.Verdict),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_raw_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.RawCount),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_distinct_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.DistinctCount),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_recovered_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.RecoveredCount),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_unresolved_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.UnresolvedCount),
        ("regular_v4_frontier_repair_successor_pressure_install_verdict={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.Verdict),
        ("regular_v4_frontier_repair_initial_pressure_install_missing_raw_count={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.RawCount),
        ("regular_v4_frontier_repair_initial_pressure_install_missing_distinct_count={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.DistinctCount),
        ("regular_v4_frontier_repair_initial_pressure_install_missing_recovered_count={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.RecoveredCount),
        ("regular_v4_frontier_repair_initial_pressure_install_missing_unresolved_count={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.UnresolvedCount),
        ("regular_v4_frontier_repair_initial_pressure_install_verdict={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.Verdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_rejected_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureRejectedCount),
        ("filetransfer_receive_recovery_operation_raw_count={0}" -f $fileTransferReceiveRecoveryOperationProof.RawCount),
        ("filetransfer_receive_recovery_operation_distinct_count={0}" -f $fileTransferReceiveRecoveryOperationProof.DistinctCount),
        ("filetransfer_receive_recovery_operation_recovered_count={0}" -f $fileTransferReceiveRecoveryOperationProof.RecoveredCount),
        ("filetransfer_receive_recovery_operation_unresolved_count={0}" -f $fileTransferReceiveRecoveryOperationProof.UnresolvedCount),
        ("filetransfer_receive_recovery_operation_verdict={0}" -f $fileTransferReceiveRecoveryOperationProof.Verdict),
        ("filetransfer_receive_recovery_operation_started_count={0}" -f $fileTransferReceiveRecoveryOperationProof.StartedCount),
        ("filetransfer_receive_recovery_operation_execution_receipt_applied_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ExecutionReceiptAppliedCount),
        ("filetransfer_receive_recovery_operation_proof_observed_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ProofObservedCount),
        ("filetransfer_receive_recovery_operation_closure_retained_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ClosureRetainedCount),
        ("filetransfer_receive_recovery_operation_closure_adopted_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ClosureAdoptedCount),
        ("filetransfer_receive_recovery_operation_closure_reserved_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ClosureReservedCount),
        ("filetransfer_receive_recovery_operation_closure_consumed_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ClosureConsumedCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.RawCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_verdict={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.Verdict),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.RawCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_verdict={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.Verdict),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.RawCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_verdict={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.Verdict),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.RawCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppression_verdict={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.Verdict),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.RawCount),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_proven_successor_verdict={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.Verdict),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_raw_count={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.RawCount),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.DistinctCount),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.RecoveredCount),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.UnresolvedCount),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_verdict={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.Verdict),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.RawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.RecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.UnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_verdict={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.Verdict),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredUnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingUnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffUnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldUnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckUnresolvedCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_raw_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.RawCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_distinct_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.DistinctCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_recovered_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.RecoveredCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_unresolved_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.UnresolvedCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_verdict={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.Verdict),
        ("filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_raw_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestRawCount),
        ("filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_distinct_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestDistinctCount),
        ("filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_recovered_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestRecoveredCount),
        ("filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_unresolved_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestUnresolvedCount),
        ("paired_fallback_tail_yield_missing_raw_count={0}" -f $fallbackTailFailedYieldQuiescentSuccessorProof.RawCount),
        ("paired_fallback_tail_yield_missing_distinct_count={0}" -f $fallbackTailFailedYieldQuiescentSuccessorProof.DistinctCount),
        ("paired_fallback_tail_yield_missing_recovered_count={0}" -f $fallbackTailFailedYieldQuiescentSuccessorProof.RecoveredCount),
        ("paired_fallback_tail_yield_missing_unresolved_count={0}" -f $fallbackTailFailedYieldQuiescentSuccessorProof.UnresolvedCount),
        ("paired_fallback_tail_yield_missing_verdict={0}" -f $fallbackTailFailedYieldQuiescentSuccessorProof.Verdict),
        ("failed_yield_quiescent_successor_missing_raw_count={0}" -f $fallbackTailFailedYieldQuiescentSuccessorProof.FailedYieldRawCount),
        ("failed_yield_quiescent_successor_missing_distinct_count={0}" -f $fallbackTailFailedYieldQuiescentSuccessorProof.FailedYieldDistinctCount),
        ("failed_yield_quiescent_successor_missing_recovered_count={0}" -f $fallbackTailFailedYieldQuiescentSuccessorProof.FailedYieldRecoveredCount),
        ("failed_yield_quiescent_successor_missing_unresolved_count={0}" -f $fallbackTailFailedYieldQuiescentSuccessorProof.FailedYieldUnresolvedCount),
        ("peer_proof_before_local_yield_not_retained_raw_count={0}" -f $fallbackTailFailedYieldQuiescentSuccessorProof.PeerFirstRawCount),
        ("peer_proof_before_local_yield_not_retained_distinct_count={0}" -f $fallbackTailFailedYieldQuiescentSuccessorProof.PeerFirstDistinctCount),
        ("peer_proof_before_local_yield_not_retained_recovered_count={0}" -f $fallbackTailFailedYieldQuiescentSuccessorProof.PeerFirstRecoveredCount),
        ("peer_proof_before_local_yield_not_retained_unresolved_count={0}" -f $fallbackTailFailedYieldQuiescentSuccessorProof.PeerFirstUnresolvedCount),
        ("peer_proof_before_local_yield_asymmetric_recovery_scope_not_retained_raw_count={0}" -f $fallbackTailFailedYieldQuiescentSuccessorProof.PeerFirstAsymmetricRecoveryScopeRawCount),
        ("peer_proof_before_local_yield_asymmetric_recovery_scope_not_retained_distinct_count={0}" -f $fallbackTailFailedYieldQuiescentSuccessorProof.PeerFirstAsymmetricRecoveryScopeDistinctCount),
        ("peer_proof_before_local_yield_asymmetric_recovery_scope_not_retained_recovered_count={0}" -f $fallbackTailFailedYieldQuiescentSuccessorProof.PeerFirstAsymmetricRecoveryScopeRecoveredCount),
        ("peer_proof_before_local_yield_asymmetric_recovery_scope_not_retained_unresolved_count={0}" -f $fallbackTailFailedYieldQuiescentSuccessorProof.PeerFirstAsymmetricRecoveryScopeUnresolvedCount),
        ("runtime_unlock_regular_v4_recovery_lease_not_issued_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.RawCount),
        ("runtime_unlock_regular_v4_recovery_lease_not_issued_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.DistinctCount),
        ("runtime_unlock_regular_v4_recovery_lease_not_issued_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.RecoveredCount),
        ("runtime_unlock_regular_v4_recovery_lease_not_issued_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.UnresolvedCount),
        ("runtime_unlock_regular_v4_recovery_lease_issuance_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.Verdict),
        ("runtime_unlock_recovery_lease_closure_not_adopted_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedRawCount),
        ("runtime_unlock_recovery_lease_closure_not_adopted_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedDistinctCount),
        ("runtime_unlock_recovery_lease_closure_not_adopted_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedRecoveredCount),
        ("runtime_unlock_recovery_lease_closure_not_adopted_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedUnresolvedCount),
        ("runtime_unlock_recovery_lease_closure_adoption_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureAdoptionVerdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_deferral_rejected_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessRejectedCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_deferral_granted_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessGrantedCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessRawCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessDistinctCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessRecoveredCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessUnresolvedCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_raw_count={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.RawCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_distinct_count={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.DistinctCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_recovered_count={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.RecoveredCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_unresolved_count={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.UnresolvedCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_verdict={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.Verdict),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_raw_count={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.RawCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_distinct_count={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.DistinctCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_recovered_count={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.RecoveredCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_unresolved_count={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.UnresolvedCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_verdict={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.Verdict),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_raw_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.RawCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_distinct_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.DistinctCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_recovered_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.RecoveredCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_unresolved_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.UnresolvedCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_verdict={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.Verdict),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_raw_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredRawCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_distinct_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredDistinctCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_recovered_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredRecoveredCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_unresolved_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredUnresolvedCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_raw_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationRawCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_distinct_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationDistinctCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_recovered_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationRecoveredCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_unresolved_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationUnresolvedCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_raw_count={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.RawCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_distinct_count={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.DistinctCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_recovered_count={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.RecoveredCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_unresolved_count={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.UnresolvedCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_verdict={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.Verdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessVerdict),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedRawCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedDistinctCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedRecoveredCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedUnresolvedCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_preservation_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldPreservationVerdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.RawCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.DistinctCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.RecoveredCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.UnresolvedCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_verdict={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.Verdict),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_raw_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.RawCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_distinct_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.DistinctCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_recovered_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.RecoveredCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_unresolved_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.UnresolvedCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_verdict={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.Verdict),
        ("runtime_unlock_initial_activation_answer_peer_delivery_missing_raw_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.RawCount),
        ("runtime_unlock_initial_activation_answer_peer_delivery_missing_distinct_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.DistinctCount),
        ("runtime_unlock_initial_activation_answer_peer_delivery_missing_recovered_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.RecoveredCount),
        ("runtime_unlock_initial_activation_answer_peer_delivery_missing_unresolved_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.UnresolvedCount),
        ("runtime_unlock_initial_activation_answer_peer_delivery_verdict={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.Verdict),
        ("runtime_unlock_initial_activation_answer_ack_return_delivery_missing_raw_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnRawCount),
        ("runtime_unlock_initial_activation_answer_ack_return_delivery_missing_distinct_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnDistinctCount),
        ("runtime_unlock_initial_activation_answer_ack_return_delivery_missing_recovered_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnRecoveredCount),
        ("runtime_unlock_initial_activation_answer_ack_return_delivery_missing_unresolved_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnUnresolvedCount),
        ("runtime_unlock_dial_failed_rejected_answer_not_retained_raw_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerRawCount),
        ("runtime_unlock_dial_failed_rejected_answer_not_retained_distinct_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerDistinctCount),
        ("runtime_unlock_dial_failed_rejected_answer_not_retained_recovered_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerRecoveredCount),
        ("runtime_unlock_dial_failed_rejected_answer_not_retained_unresolved_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerUnresolvedCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_raw_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.RawCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_distinct_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.DistinctCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_recovered_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.RecoveredCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_unresolved_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.UnresolvedCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_verdict={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.Verdict),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_raw_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.RawCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_distinct_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.DistinctCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_recovered_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.RecoveredCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_unresolved_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.UnresolvedCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_verdict={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.Verdict),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_raw_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.RawCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_distinct_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.DistinctCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_recovered_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.RecoveredCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_unresolved_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.UnresolvedCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_verdict={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.Verdict),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_raw_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.RawCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_distinct_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.DistinctCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_recovered_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.RecoveredCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_unresolved_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.UnresolvedCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_verdict={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.Verdict),
       ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_requested_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.RequestedCount),
       ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_decided_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.DecidedCount),
       ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_discarded_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.DiscardedCount),
       ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_raw_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.RawCount),
       ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_distinct_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.DistinctCount),
       ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_recovered_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.RecoveredCount),
       ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_unresolved_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.UnresolvedCount),
       ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_verdict={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.Verdict),
       ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_raw_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementRawCount),
       ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_distinct_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementDistinctCount),
       ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_recovered_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementRecoveredCount),
       ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_unresolved_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementUnresolvedCount),
       ("runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_raw_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingRawCount),
       ("runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_distinct_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingDistinctCount),
       ("runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_recovered_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingRecoveredCount),
       ("runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_unresolved_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingUnresolvedCount),
       ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_raw_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldRawCount),
       ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_distinct_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldDistinctCount),
       ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_recovered_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldRecoveredCount),
       ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_unresolved_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldUnresolvedCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_raw_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.RawCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_distinct_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.DistinctCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_recovered_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.RecoveredCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_unresolved_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.UnresolvedCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_raw_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationRawCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_distinct_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationDistinctCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_recovered_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationRecoveredCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_unresolved_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationUnresolvedCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_verdict={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.Verdict),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_missing_raw_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RawCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_missing_distinct_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.DistinctCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_missing_recovered_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RecoveredCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_missing_unresolved_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.UnresolvedCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_raw_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredRawCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_distinct_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredDistinctCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_recovered_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredRecoveredCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_unresolved_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredUnresolvedCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_raw_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingRawCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_distinct_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingDistinctCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_recovered_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingRecoveredCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_unresolved_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingUnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_raw_count={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.RawCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_distinct_count={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.DistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_recovered_count={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.RecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_unresolved_count={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.UnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_verdict={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.Verdict),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_count={0}" -f $runtimeUnlockRetryRegularV4RecoverySettlementMissingCount),
        ("runtime_unlock_retry_blocked_by_regular_v4_receive_recovery_count={0}" -f $runtimeUnlockRetryBlockedByRegularV4RecoveryCount),
        ("runtime_unlock_retry_held_by_regular_v4_receive_recovery_count={0}" -f $runtimeUnlockRetryHeldByRegularV4RecoveryCount),
        ("runtime_unlock_retry_blocked_by_regular_v4_recovery_count={0}" -f $runtimeUnlockRetryBlockedByRegularV4RecoveryCount),
        ("runtime_unlock_retry_held_by_regular_v4_recovery_count={0}" -f $runtimeUnlockRetryHeldByRegularV4RecoveryCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_raw_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.RawCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_distinct_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.DistinctCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_recovered_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.RecoveredCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.UnresolvedCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_verdict={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.Verdict),
        ("runtime_unlock_retry_regular_v4_settlement_handoff_bypassed_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.HandoffBypassCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_raw_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.RawCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_distinct_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.DistinctCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_recovered_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.RecoveredCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_unresolved_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.UnresolvedCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_verdict={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.Verdict),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_raw_count={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.RawCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_distinct_count={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.DistinctCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_recovered_count={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.RecoveredCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_unresolved_count={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.UnresolvedCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppression_verdict={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.Verdict),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_raw_count={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.RawCount),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_distinct_count={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.DistinctCount),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_recovered_count={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.RecoveredCount),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_unresolved_count={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.UnresolvedCount),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_verdict={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.Verdict),
        ("runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_raw_count={0}" -f $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.RawCount),
        ("runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_distinct_count={0}" -f $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.DistinctCount),
        ("runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_recovered_count={0}" -f $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.RecoveredCount),
        ("runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_unresolved_count={0}" -f $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.UnresolvedCount),
        ("post_tuna_fallback_outbound_receive_worker_handoff_missing_count={0}" -f $outboundReceiveWorkerHandoffProof.Count),
        ("fallback_tail_yield_authority_rejected_count={0}" -f $fallbackTailYieldAuthorityRejectedCount),
        ("fallback_tail_coordinator_aggregate_verdict={0}" -f $fallbackTailCoordinatorAggregateProof.Verdict),
        ("fallback_tail_coordinator_yield_issued_count={0}" -f $fallbackTailCoordinatorAggregateProof.YieldIssuedCount),
        ("fallback_tail_coordinator_local_proof_receipt_decided_count={0}" -f $fallbackTailCoordinatorAggregateProof.LocalProofReceiptDecidedCount),
        ("fallback_tail_coordinator_peer_proof_receipt_decided_count={0}" -f $fallbackTailCoordinatorAggregateProof.PeerProofReceiptDecidedCount),
        ("fallback_tail_coordinator_pairing_committed_count={0}" -f $fallbackTailCoordinatorAggregateProof.PairingCommittedCount),
        ("fallback_tail_coordinator_valid_pairing_count={0}" -f $fallbackTailCoordinatorAggregateProof.ValidPairingCount),
        ("fallback_tail_coordinator_paired_release_reserved_count={0}" -f $fallbackTailCoordinatorAggregateProof.PairedReleaseReservedCount),
        ("fallback_tail_coordinator_valid_paired_release_reserved_count={0}" -f $fallbackTailCoordinatorAggregateProof.ValidPairedReleaseReservedCount),
        ("fallback_tail_coordinator_paired_release_consumed_count={0}" -f $fallbackTailCoordinatorAggregateProof.PairedReleaseConsumedCount),
        ("fallback_tail_coordinator_valid_paired_release_consumed_count={0}" -f $fallbackTailCoordinatorAggregateProof.ValidPairedReleaseConsumedCount),
        ("fallback_tail_coordinator_paired_release_carry_forward_count={0}" -f $fallbackTailCoordinatorAggregateProof.PairedReleaseCarryForwardCount),
        ("fallback_tail_coordinator_valid_paired_release_carry_forward_count={0}" -f $fallbackTailCoordinatorAggregateProof.ValidPairedReleaseCarryForwardCount),
        ("fallback_tail_coordinator_invalid_raw_count={0}" -f $fallbackTailCoordinatorAggregateProof.InvalidRawCount),
        ("fallback_tail_coordinator_invalid_distinct_count={0}" -f $fallbackTailCoordinatorAggregateProof.InvalidDistinctCount),
        ("fallback_tail_coordinator_invalid_recovered_count={0}" -f $fallbackTailCoordinatorAggregateProof.InvalidRecoveredCount),
        ("fallback_tail_coordinator_invalid_unresolved_count={0}" -f $fallbackTailCoordinatorAggregateProof.InvalidUnresolvedCount),
        ("fallback_tail_coordinator_reauthorization_staged_count={0}" -f $fallbackTailCoordinatorAggregateProof.ReauthorizationStagedCount),
        ("fallback_tail_coordinator_reauthorization_rebound_count={0}" -f $fallbackTailCoordinatorAggregateProof.ReauthorizationReboundCount),
        ("fallback_tail_coordinator_reauthorization_successor_issued_count={0}" -f $fallbackTailCoordinatorAggregateProof.ReauthorizationSuccessorIssuedCount),
        ("fallback_tail_coordinator_reauthorization_invalidated_count={0}" -f $fallbackTailCoordinatorAggregateProof.ReauthorizationInvalidatedCount),
        ("fallback_tail_owner_generation_mismatch_count={0}" -f $fallbackTailOwnerGenerationMismatchCount),
        ("fallback_tail_bridge_recovery_generation_pairing_verdict={0}" -f $fallbackTailBridgeRecoveryPairingProof.Verdict),
        ("fallback_tail_bridge_recovery_generation_mismatch_raw_count={0}" -f $fallbackTailBridgeRecoveryPairingProof.RawCount),
        ("fallback_tail_bridge_recovery_generation_mismatch_distinct_count={0}" -f $fallbackTailBridgeRecoveryPairingProof.DistinctCount),
        ("fallback_tail_bridge_recovery_generation_mismatch_recovered_count={0}" -f $fallbackTailBridgeRecoveryPairingProof.RecoveredCount),
        ("fallback_tail_bridge_recovery_generation_mismatch_unresolved_count={0}" -f $fallbackTailBridgeRecoveryPairingProof.UnresolvedCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict={0}" -f $fallbackTailTransportEpochReauthorizationProof.Verdict),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count={0}" -f $fallbackTailTransportEpochReauthorizationProof.RawCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count={0}" -f $fallbackTailTransportEpochReauthorizationProof.DistinctCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count={0}" -f $fallbackTailTransportEpochReauthorizationProof.RecoveredCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count={0}" -f $fallbackTailTransportEpochReauthorizationProof.UnresolvedCount),
        ("runtime_unlock_precommit_probe_acked_count={0}" -f $probeAckCount),
        ("runtime_unlock_paired_commit_confirmed_count={0}" -f $pairedCommitCount),
        ("runtime_unlock_probe_commit_verdict={0}" -f $runtimeUnlockProbeCommitVerdict),
        ("fallback_tail_quiescence_verdict={0}" -f $fallbackTailQuiescenceVerdict),
        ("fallback_tail_quiescent_yield_granted_count={0}" -f $fallbackYieldCount),
        ("fallback_tail_outbound_yield_granted_count={0}" -f $fallbackOutboundYieldCount),
        ("fallback_tail_inbound_yield_granted_count={0}" -f $fallbackInboundYieldCount),
        ("fallback_tail_paired_yield_granted_count={0}" -f $fallbackPairedYieldGrantedCount),
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
        ("recovery_failure_class={0}" -f $recoveryFailureClass),
        ("failure_variant={0}" -f $failureVariant),
        ("first_proof_chain_failure_class={0}" -f $firstProofChainFailureClass),
        ("first_proof_chain_failure_event={0}" -f $firstProofChainFailureEvent),
        ("first_proof_chain_failure_reason={0}" -f $firstProofChainFailureReason),
        ("first_proof_chain_failure_sequence={0}" -f $firstProofChainFailureSequence),
        ("first_proof_chain_failure_source_path={0}" -f $firstProofChainFailureSourcePath),
        ("first_proof_chain_failure_line_number={0}" -f $firstProofChainFailureLineNumber),
        ("first_proof_chain_failure_writer_sequence={0}" -f $firstProofChainFailureWriterSequence),
        ("semantic_failure_domain={0}" -f $semanticFailureDomain),
        ("filetransfer_gui_live_fallback_fault_window_missed_raw_count={0}" -f $fileTransferGuiLiveFallbackFaultWindowProof.RawCount),
        ("filetransfer_gui_live_fallback_fault_window_missed_distinct_count={0}" -f $fileTransferGuiLiveFallbackFaultWindowProof.DistinctCount),
        ("filetransfer_gui_live_fallback_fault_window_missed_recovered_count=0"),
        ("filetransfer_gui_live_fallback_fault_window_missed_unresolved_count={0}" -f $fileTransferGuiLiveFallbackFaultWindowProof.UnresolvedCount),
        ("filetransfer_gui_live_fallback_fault_window_missed_verdict={0}" -f $fileTransferGuiLiveFallbackFaultWindowProof.Verdict),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_raw_count={0}" -f $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingRawCount),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_distinct_count={0}" -f $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingDistinctCount),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_recovered_count={0}" -f $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingRecoveredCount),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_unresolved_count={0}" -f $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingUnresolvedCount),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_verdict={0}" -f ($(if ($fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingUnresolvedCount -gt 0) { 'fail' } else { 'pass' }))),
        ("filetransfer_route_transition_incomplete_raw_count={0}" -f $fileTransferRouteTransitionProof.RawCount),
        ("filetransfer_route_transition_incomplete_distinct_count={0}" -f $fileTransferRouteTransitionProof.DistinctCount),
        ("filetransfer_route_transition_incomplete_recovered_count={0}" -f $fileTransferRouteTransitionProof.RecoveredCount),
        ("filetransfer_route_transition_incomplete_unresolved_count={0}" -f $fileTransferRouteTransitionProof.UnresolvedCount),
        ("filetransfer_live_route_epoch_projection_missing_raw_count={0}" -f $fileTransferRouteTransitionLiveEpochProjectionProof.RawCount),
        ("filetransfer_live_route_epoch_projection_missing_distinct_count={0}" -f $fileTransferRouteTransitionLiveEpochProjectionProof.DistinctCount),
        ("filetransfer_live_route_epoch_projection_missing_recovered_count={0}" -f $fileTransferRouteTransitionLiveEpochProjectionProof.RecoveredCount),
        ("filetransfer_live_route_epoch_projection_missing_unresolved_count={0}" -f $fileTransferRouteTransitionLiveEpochProjectionProof.UnresolvedCount),
        ("filetransfer_route_transition_verdict={0}" -f $fileTransferRouteTransitionProof.Verdict),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_raw_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.RawCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_distinct_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.DistinctCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_recovered_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.RecoveredCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_unresolved_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.UnresolvedCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_verdict={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.Verdict),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.RawCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.DistinctCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_recovered_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.RecoveredCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.UnresolvedCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_verdict={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.Verdict),
        ("file_tuna_v4_data_path_mismatch_count={0}" -f $fileTunaV4DataPathMismatchProof.EffectiveCount),
        ("runtime_unlock_retry_dispatch_authority_granted_raw_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.AuthorityGrantedRawCount),
        ("runtime_unlock_retry_dispatch_authority_granted_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.EffectiveGrantEvents.Count),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_raw_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteRawCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_distinct_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteDistinctCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_recovered_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteRecoveredCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_unresolved_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteUnresolvedCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_verdict={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceVerdict),
        ("runtime_unlock_retry_lifecycle_execution_observation_count={0}" -f $runtimeUnlockRetryLifecycleProof.ExecutionObservationCount),
        ("runtime_unlock_retry_lifecycle_execution_decision_count={0}" -f $runtimeUnlockRetryLifecycleProof.ExecutionDecisionCount),
        ("runtime_unlock_retry_lifecycle_execution_unresolved_count={0}" -f @($runtimeUnlockRetryLifecycleProof.UnresolvedExecutionObservationEvents).Count),
        ("runtime_unlock_retry_lifecycle_successor_evaluation_count={0}" -f $runtimeUnlockRetryLifecycleProof.SuccessorEvaluationCount),
        ("runtime_unlock_retry_lifecycle_successor_decision_count={0}" -f $runtimeUnlockRetryLifecycleProof.SuccessorDecisionCount),
        ("runtime_unlock_retry_lifecycle_successor_unresolved_count={0}" -f @($runtimeUnlockRetryLifecycleProof.UnresolvedSuccessorEvaluationEvents).Count),
        ("runtime_unlock_retry_lifecycle_deadline_evaluation_count={0}" -f $runtimeUnlockRetryLifecycleProof.DeadlineEvaluationCount),
        ("runtime_unlock_retry_lifecycle_deadline_decision_count={0}" -f $runtimeUnlockRetryLifecycleProof.DeadlineDecisionCount),
        ("runtime_unlock_retry_lifecycle_deadline_unresolved_count={0}" -f @($runtimeUnlockRetryLifecycleProof.UnresolvedDeadlineEvaluationEvents).Count),
        ("runtime_unlock_retry_lifecycle_verdict={0}" -f $runtimeUnlockRetryLifecycleProof.Verdict),
        ("regular_v4_frontier_repair_missing_range_not_preserved_raw_count={0}" -f $regularV4FrontierRepairMissingRangePreservationProof.RawCount),
        ("regular_v4_frontier_repair_missing_range_not_preserved_distinct_count={0}" -f $regularV4FrontierRepairMissingRangePreservationProof.DistinctCount),
        ("regular_v4_frontier_repair_missing_range_not_preserved_recovered_count={0}" -f $regularV4FrontierRepairMissingRangePreservationProof.RecoveredCount),
        ("regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count={0}" -f $regularV4FrontierRepairMissingRangePreservationProof.UnresolvedCount),
        ("regular_v4_frontier_repair_missing_range_preservation_verdict={0}" -f $regularV4FrontierRepairMissingRangePreservationProof.Verdict),
        ("regular_v4_frontier_tail_repair_state_publication_missing_raw_count={0}" -f $regularV4FrontierTailRepairStatePublicationProof.RawCount),
        ("regular_v4_frontier_tail_repair_state_publication_missing_distinct_count={0}" -f $regularV4FrontierTailRepairStatePublicationProof.DistinctCount),
        ("regular_v4_frontier_tail_repair_state_publication_missing_recovered_count={0}" -f $regularV4FrontierTailRepairStatePublicationProof.RecoveredCount),
        ("regular_v4_frontier_tail_repair_state_publication_missing_unresolved_count={0}" -f $regularV4FrontierTailRepairStatePublicationProof.UnresolvedCount),
        ("regular_v4_frontier_tail_repair_state_publication_verdict={0}" -f $regularV4FrontierTailRepairStatePublicationProof.Verdict),
        ("regular_v4_frontier_repair_projection_reauthorization_missing_raw_count={0}" -f $regularV4FrontierRepairProjectionReauthorizationProof.RawCount),
        ("regular_v4_frontier_repair_projection_reauthorization_missing_distinct_count={0}" -f $regularV4FrontierRepairProjectionReauthorizationProof.DistinctCount),
        ("regular_v4_frontier_repair_projection_reauthorization_missing_recovered_count={0}" -f $regularV4FrontierRepairProjectionReauthorizationProof.RecoveredCount),
        ("regular_v4_frontier_repair_projection_reauthorization_missing_unresolved_count={0}" -f $regularV4FrontierRepairProjectionReauthorizationProof.UnresolvedCount),
        ("regular_v4_frontier_repair_projection_reauthorization_verdict={0}" -f $regularV4FrontierRepairProjectionReauthorizationProof.Verdict),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_raw_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.RawCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_distinct_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.DistinctCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_recovered_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.RecoveredCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_unresolved_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.UnresolvedCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_preservation_verdict={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.Verdict),
        ("regular_v4_frontier_repair_stalled_raw_count={0}" -f $regularV4FrontierRepairLifecycleProof.RawCount),
        ("regular_v4_frontier_repair_stalled_distinct_count={0}" -f $regularV4FrontierRepairLifecycleProof.DistinctCount),
        ("regular_v4_frontier_repair_stalled_recovered_count={0}" -f $regularV4FrontierRepairLifecycleProof.RecoveredCount),
        ("regular_v4_frontier_repair_stalled_unresolved_count={0}" -f $regularV4FrontierRepairLifecycleProof.UnresolvedCount),
        ("regular_v4_frontier_repair_stalled_verdict={0}" -f $regularV4FrontierRepairLifecycleProof.Verdict),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_raw_count={0}" -f $regularV4FrontierRepairSettlementDispositionProof.RawCount),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_distinct_count={0}" -f $regularV4FrontierRepairSettlementDispositionProof.DistinctCount),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_recovered_count={0}" -f $regularV4FrontierRepairSettlementDispositionProof.RecoveredCount),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_unresolved_count={0}" -f $regularV4FrontierRepairSettlementDispositionProof.UnresolvedCount),
        ("regular_v4_frontier_repair_settlement_disposition_verdict={0}" -f $regularV4FrontierRepairSettlementDispositionProof.Verdict),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_raw_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.RawCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_distinct_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.DistinctCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_recovered_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.RecoveredCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_unresolved_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.UnresolvedCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_verdict={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.Verdict),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_raw_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.RawCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_distinct_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.DistinctCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_recovered_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.RecoveredCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_unresolved_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.UnresolvedCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_verdict={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.Verdict),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_raw_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.RawCount),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_distinct_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.DistinctCount),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_recovered_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.RecoveredCount),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_unresolved_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.UnresolvedCount),
        ("regular_v4_frontier_repair_successor_pressure_install_verdict={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.Verdict),
        ("regular_v4_frontier_repair_initial_pressure_install_missing_raw_count={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.RawCount),
        ("regular_v4_frontier_repair_initial_pressure_install_missing_distinct_count={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.DistinctCount),
        ("regular_v4_frontier_repair_initial_pressure_install_missing_recovered_count={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.RecoveredCount),
        ("regular_v4_frontier_repair_initial_pressure_install_missing_unresolved_count={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.UnresolvedCount),
        ("regular_v4_frontier_repair_initial_pressure_install_verdict={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.Verdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_rejected_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureRejectedCount),
        ("filetransfer_receive_recovery_operation_raw_count={0}" -f $fileTransferReceiveRecoveryOperationProof.RawCount),
        ("filetransfer_receive_recovery_operation_distinct_count={0}" -f $fileTransferReceiveRecoveryOperationProof.DistinctCount),
        ("filetransfer_receive_recovery_operation_recovered_count={0}" -f $fileTransferReceiveRecoveryOperationProof.RecoveredCount),
        ("filetransfer_receive_recovery_operation_unresolved_count={0}" -f $fileTransferReceiveRecoveryOperationProof.UnresolvedCount),
        ("filetransfer_receive_recovery_operation_verdict={0}" -f $fileTransferReceiveRecoveryOperationProof.Verdict),
        ("filetransfer_receive_recovery_operation_started_count={0}" -f $fileTransferReceiveRecoveryOperationProof.StartedCount),
        ("filetransfer_receive_recovery_operation_execution_receipt_applied_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ExecutionReceiptAppliedCount),
        ("filetransfer_receive_recovery_operation_proof_observed_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ProofObservedCount),
        ("filetransfer_receive_recovery_operation_closure_retained_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ClosureRetainedCount),
        ("filetransfer_receive_recovery_operation_closure_adopted_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ClosureAdoptedCount),
        ("filetransfer_receive_recovery_operation_closure_reserved_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ClosureReservedCount),
        ("filetransfer_receive_recovery_operation_closure_consumed_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ClosureConsumedCount),
        ("runtime_unlock_regular_v4_recovery_lease_not_issued_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.RawCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.RawCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_verdict={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.Verdict),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.RawCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_verdict={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.Verdict),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.RawCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_verdict={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.Verdict),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.RawCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppression_verdict={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.Verdict),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.RawCount),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_proven_successor_verdict={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.Verdict),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_raw_count={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.RawCount),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.DistinctCount),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.RecoveredCount),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.UnresolvedCount),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_verdict={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.Verdict),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.RawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.RecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.UnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_verdict={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.Verdict),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredUnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingUnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffUnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldUnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckUnresolvedCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_raw_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.RawCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_distinct_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.DistinctCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_recovered_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.RecoveredCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_unresolved_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.UnresolvedCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_verdict={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.Verdict),
        ("filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_raw_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestRawCount),
        ("filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_distinct_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestDistinctCount),
        ("filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_recovered_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestRecoveredCount),
        ("filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_unresolved_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestUnresolvedCount),
        ("runtime_unlock_regular_v4_recovery_lease_not_issued_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.DistinctCount),
        ("runtime_unlock_regular_v4_recovery_lease_not_issued_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.RecoveredCount),
        ("runtime_unlock_regular_v4_recovery_lease_not_issued_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.UnresolvedCount),
        ("runtime_unlock_regular_v4_recovery_lease_issuance_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.Verdict),
        ("runtime_unlock_recovery_lease_closure_not_adopted_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedRawCount),
        ("runtime_unlock_recovery_lease_closure_not_adopted_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedDistinctCount),
        ("runtime_unlock_recovery_lease_closure_not_adopted_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedRecoveredCount),
        ("runtime_unlock_recovery_lease_closure_not_adopted_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedUnresolvedCount),
        ("runtime_unlock_recovery_lease_closure_adoption_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureAdoptionVerdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_deferral_rejected_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessRejectedCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_deferral_granted_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessGrantedCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessRawCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessDistinctCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessRecoveredCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessUnresolvedCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_raw_count={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.RawCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_distinct_count={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.DistinctCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_recovered_count={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.RecoveredCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_unresolved_count={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.UnresolvedCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_verdict={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.Verdict),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_raw_count={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.RawCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_distinct_count={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.DistinctCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_recovered_count={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.RecoveredCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_unresolved_count={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.UnresolvedCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_verdict={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.Verdict),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_raw_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.RawCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_distinct_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.DistinctCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_recovered_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.RecoveredCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_unresolved_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.UnresolvedCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_verdict={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.Verdict),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_raw_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredRawCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_distinct_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredDistinctCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_recovered_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredRecoveredCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_unresolved_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredUnresolvedCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_raw_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationRawCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_distinct_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationDistinctCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_recovered_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationRecoveredCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_unresolved_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationUnresolvedCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_raw_count={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.RawCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_distinct_count={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.DistinctCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_recovered_count={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.RecoveredCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_unresolved_count={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.UnresolvedCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_verdict={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.Verdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessVerdict),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedRawCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedDistinctCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedRecoveredCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedUnresolvedCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_preservation_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldPreservationVerdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.RawCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.DistinctCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.RecoveredCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.UnresolvedCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_verdict={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.Verdict),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_raw_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.RawCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_distinct_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.DistinctCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_recovered_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.RecoveredCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_unresolved_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.UnresolvedCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_verdict={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.Verdict),
        ("runtime_unlock_initial_activation_answer_peer_delivery_missing_raw_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.RawCount),
        ("runtime_unlock_initial_activation_answer_peer_delivery_missing_distinct_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.DistinctCount),
        ("runtime_unlock_initial_activation_answer_peer_delivery_missing_recovered_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.RecoveredCount),
        ("runtime_unlock_initial_activation_answer_peer_delivery_missing_unresolved_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.UnresolvedCount),
        ("runtime_unlock_initial_activation_answer_peer_delivery_verdict={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.Verdict),
        ("runtime_unlock_initial_activation_answer_ack_return_delivery_missing_raw_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnRawCount),
        ("runtime_unlock_initial_activation_answer_ack_return_delivery_missing_distinct_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnDistinctCount),
        ("runtime_unlock_initial_activation_answer_ack_return_delivery_missing_recovered_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnRecoveredCount),
        ("runtime_unlock_initial_activation_answer_ack_return_delivery_missing_unresolved_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnUnresolvedCount),
        ("runtime_unlock_dial_failed_rejected_answer_not_retained_raw_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerRawCount),
        ("runtime_unlock_dial_failed_rejected_answer_not_retained_distinct_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerDistinctCount),
        ("runtime_unlock_dial_failed_rejected_answer_not_retained_recovered_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerRecoveredCount),
        ("runtime_unlock_dial_failed_rejected_answer_not_retained_unresolved_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerUnresolvedCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_raw_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.RawCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_distinct_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.DistinctCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_recovered_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.RecoveredCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_unresolved_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.UnresolvedCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_verdict={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.Verdict),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_raw_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.RawCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_distinct_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.DistinctCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_recovered_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.RecoveredCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_unresolved_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.UnresolvedCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_verdict={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.Verdict),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_raw_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.RawCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_distinct_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.DistinctCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_recovered_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.RecoveredCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_unresolved_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.UnresolvedCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_verdict={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.Verdict),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_raw_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.RawCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_distinct_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.DistinctCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_recovered_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.RecoveredCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_unresolved_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.UnresolvedCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_verdict={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.Verdict),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_requested_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.RequestedCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_decided_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.DecidedCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_discarded_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.DiscardedCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_raw_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.RawCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_distinct_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.DistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_recovered_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.RecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_unresolved_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.UnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_verdict={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.Verdict),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_raw_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementRawCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_distinct_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementDistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_recovered_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementRecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_unresolved_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementUnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_raw_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingRawCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_distinct_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingDistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_recovered_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingRecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_unresolved_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingUnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_raw_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldRawCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_distinct_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldDistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_recovered_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldRecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_unresolved_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldUnresolvedCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_raw_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.RawCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_distinct_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.DistinctCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_recovered_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.RecoveredCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_unresolved_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.UnresolvedCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_raw_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationRawCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_distinct_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationDistinctCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_recovered_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationRecoveredCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_unresolved_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationUnresolvedCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_verdict={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.Verdict),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_missing_raw_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RawCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_missing_distinct_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.DistinctCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_missing_recovered_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RecoveredCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_missing_unresolved_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.UnresolvedCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_raw_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredRawCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_distinct_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredDistinctCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_recovered_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredRecoveredCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_unresolved_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredUnresolvedCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_raw_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingRawCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_distinct_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingDistinctCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_recovered_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingRecoveredCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_unresolved_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingUnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_raw_count={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.RawCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_distinct_count={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.DistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_recovered_count={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.RecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_unresolved_count={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.UnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_verdict={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.Verdict),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_count={0}" -f $runtimeUnlockRetryRegularV4RecoverySettlementMissingCount),
        ("runtime_unlock_retry_blocked_by_regular_v4_receive_recovery_count={0}" -f $runtimeUnlockRetryBlockedByRegularV4RecoveryCount),
        ("runtime_unlock_retry_held_by_regular_v4_receive_recovery_count={0}" -f $runtimeUnlockRetryHeldByRegularV4RecoveryCount),
        ("runtime_unlock_retry_blocked_by_regular_v4_recovery_count={0}" -f $runtimeUnlockRetryBlockedByRegularV4RecoveryCount),
        ("runtime_unlock_retry_held_by_regular_v4_recovery_count={0}" -f $runtimeUnlockRetryHeldByRegularV4RecoveryCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_raw_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.RawCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_distinct_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.DistinctCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_recovered_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.RecoveredCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.UnresolvedCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_verdict={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.Verdict),
        ("runtime_unlock_retry_regular_v4_settlement_handoff_bypassed_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.HandoffBypassCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_raw_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.RawCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_distinct_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.DistinctCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_recovered_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.RecoveredCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_unresolved_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.UnresolvedCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_verdict={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.Verdict),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_raw_count={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.RawCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_distinct_count={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.DistinctCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_recovered_count={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.RecoveredCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_unresolved_count={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.UnresolvedCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppression_verdict={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.Verdict),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_raw_count={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.RawCount),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_distinct_count={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.DistinctCount),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_recovered_count={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.RecoveredCount),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_unresolved_count={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.UnresolvedCount),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_verdict={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.Verdict),
        ("post_tuna_fallback_outbound_receive_worker_handoff_missing_count={0}" -f $outboundReceiveWorkerHandoffProof.Count),
        ("runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_raw_count={0}" -f $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.RawCount),
        ("runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_distinct_count={0}" -f $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.DistinctCount),
        ("runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_recovered_count={0}" -f $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.RecoveredCount),
        ("runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_unresolved_count={0}" -f $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.UnresolvedCount),
        ("runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_raw_count={0}" -f $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.RawCount),
        ("runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_distinct_count={0}" -f $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.DistinctCount),
        ("runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_recovered_count={0}" -f $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.RecoveredCount),
        ("runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_unresolved_count={0}" -f $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.UnresolvedCount),
        ("fallback_tail_yield_authority_rejected_count={0}" -f $fallbackTailYieldAuthorityRejectedCount),
        ("fallback_tail_coordinator_aggregate_verdict={0}" -f $fallbackTailCoordinatorAggregateProof.Verdict),
        ("fallback_tail_coordinator_yield_issued_count={0}" -f $fallbackTailCoordinatorAggregateProof.YieldIssuedCount),
        ("fallback_tail_coordinator_local_proof_receipt_decided_count={0}" -f $fallbackTailCoordinatorAggregateProof.LocalProofReceiptDecidedCount),
        ("fallback_tail_coordinator_peer_proof_receipt_decided_count={0}" -f $fallbackTailCoordinatorAggregateProof.PeerProofReceiptDecidedCount),
        ("fallback_tail_coordinator_pairing_committed_count={0}" -f $fallbackTailCoordinatorAggregateProof.PairingCommittedCount),
        ("fallback_tail_coordinator_valid_pairing_count={0}" -f $fallbackTailCoordinatorAggregateProof.ValidPairingCount),
        ("fallback_tail_coordinator_paired_release_reserved_count={0}" -f $fallbackTailCoordinatorAggregateProof.PairedReleaseReservedCount),
        ("fallback_tail_coordinator_valid_paired_release_reserved_count={0}" -f $fallbackTailCoordinatorAggregateProof.ValidPairedReleaseReservedCount),
        ("fallback_tail_coordinator_paired_release_consumed_count={0}" -f $fallbackTailCoordinatorAggregateProof.PairedReleaseConsumedCount),
        ("fallback_tail_coordinator_valid_paired_release_consumed_count={0}" -f $fallbackTailCoordinatorAggregateProof.ValidPairedReleaseConsumedCount),
        ("fallback_tail_coordinator_paired_release_carry_forward_count={0}" -f $fallbackTailCoordinatorAggregateProof.PairedReleaseCarryForwardCount),
        ("fallback_tail_coordinator_valid_paired_release_carry_forward_count={0}" -f $fallbackTailCoordinatorAggregateProof.ValidPairedReleaseCarryForwardCount),
        ("fallback_tail_coordinator_invalid_raw_count={0}" -f $fallbackTailCoordinatorAggregateProof.InvalidRawCount),
        ("fallback_tail_coordinator_invalid_distinct_count={0}" -f $fallbackTailCoordinatorAggregateProof.InvalidDistinctCount),
        ("fallback_tail_coordinator_invalid_recovered_count={0}" -f $fallbackTailCoordinatorAggregateProof.InvalidRecoveredCount),
        ("fallback_tail_coordinator_invalid_unresolved_count={0}" -f $fallbackTailCoordinatorAggregateProof.InvalidUnresolvedCount),
        ("fallback_tail_coordinator_reauthorization_staged_count={0}" -f $fallbackTailCoordinatorAggregateProof.ReauthorizationStagedCount),
        ("fallback_tail_coordinator_reauthorization_rebound_count={0}" -f $fallbackTailCoordinatorAggregateProof.ReauthorizationReboundCount),
        ("fallback_tail_coordinator_reauthorization_successor_issued_count={0}" -f $fallbackTailCoordinatorAggregateProof.ReauthorizationSuccessorIssuedCount),
        ("fallback_tail_coordinator_reauthorization_invalidated_count={0}" -f $fallbackTailCoordinatorAggregateProof.ReauthorizationInvalidatedCount),
        ("fallback_tail_owner_generation_mismatch_count={0}" -f $fallbackTailOwnerGenerationMismatchCount),
        ("fallback_tail_bridge_recovery_generation_pairing_verdict={0}" -f $fallbackTailBridgeRecoveryPairingProof.Verdict),
        ("fallback_tail_bridge_recovery_generation_mismatch_raw_count={0}" -f $fallbackTailBridgeRecoveryPairingProof.RawCount),
        ("fallback_tail_bridge_recovery_generation_mismatch_distinct_count={0}" -f $fallbackTailBridgeRecoveryPairingProof.DistinctCount),
        ("fallback_tail_bridge_recovery_generation_mismatch_recovered_count={0}" -f $fallbackTailBridgeRecoveryPairingProof.RecoveredCount),
        ("fallback_tail_bridge_recovery_generation_mismatch_unresolved_count={0}" -f $fallbackTailBridgeRecoveryPairingProof.UnresolvedCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict={0}" -f $fallbackTailTransportEpochReauthorizationProof.Verdict),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count={0}" -f $fallbackTailTransportEpochReauthorizationProof.RawCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count={0}" -f $fallbackTailTransportEpochReauthorizationProof.DistinctCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count={0}" -f $fallbackTailTransportEpochReauthorizationProof.RecoveredCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count={0}" -f $fallbackTailTransportEpochReauthorizationProof.UnresolvedCount),
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
        ("recovery_failure_class={0}" -f $recoveryFailureClass),
        ("failure_variant={0}" -f $failureVariant),
        ("first_proof_chain_failure_class={0}" -f $firstProofChainFailureClass),
        ("first_proof_chain_failure_event={0}" -f $firstProofChainFailureEvent),
        ("first_proof_chain_failure_reason={0}" -f $firstProofChainFailureReason),
        ("first_proof_chain_failure_sequence={0}" -f $firstProofChainFailureSequence),
        ("first_proof_chain_failure_source_path={0}" -f $firstProofChainFailureSourcePath),
        ("first_proof_chain_failure_line_number={0}" -f $firstProofChainFailureLineNumber),
        ("first_proof_chain_failure_writer_sequence={0}" -f $firstProofChainFailureWriterSequence),
        ("semantic_failure_domain={0}" -f $semanticFailureDomain),
        ("filetransfer_gui_live_fallback_fault_window_missed_raw_count={0}" -f $fileTransferGuiLiveFallbackFaultWindowProof.RawCount),
        ("filetransfer_gui_live_fallback_fault_window_missed_distinct_count={0}" -f $fileTransferGuiLiveFallbackFaultWindowProof.DistinctCount),
        ("filetransfer_gui_live_fallback_fault_window_missed_recovered_count=0"),
        ("filetransfer_gui_live_fallback_fault_window_missed_unresolved_count={0}" -f $fileTransferGuiLiveFallbackFaultWindowProof.UnresolvedCount),
        ("filetransfer_gui_live_fallback_fault_window_missed_verdict={0}" -f $fileTransferGuiLiveFallbackFaultWindowProof.Verdict),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_raw_count={0}" -f $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingRawCount),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_distinct_count={0}" -f $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingDistinctCount),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_recovered_count={0}" -f $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingRecoveredCount),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_unresolved_count={0}" -f $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingUnresolvedCount),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_verdict={0}" -f ($(if ($fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingUnresolvedCount -gt 0) { 'fail' } else { 'pass' }))),
        ("filetransfer_route_transition_incomplete_raw_count={0}" -f $fileTransferRouteTransitionProof.RawCount),
        ("filetransfer_route_transition_incomplete_distinct_count={0}" -f $fileTransferRouteTransitionProof.DistinctCount),
        ("filetransfer_route_transition_incomplete_recovered_count={0}" -f $fileTransferRouteTransitionProof.RecoveredCount),
        ("filetransfer_route_transition_incomplete_unresolved_count={0}" -f $fileTransferRouteTransitionProof.UnresolvedCount),
        ("filetransfer_live_route_epoch_projection_missing_raw_count={0}" -f $fileTransferRouteTransitionLiveEpochProjectionProof.RawCount),
        ("filetransfer_live_route_epoch_projection_missing_distinct_count={0}" -f $fileTransferRouteTransitionLiveEpochProjectionProof.DistinctCount),
        ("filetransfer_live_route_epoch_projection_missing_recovered_count={0}" -f $fileTransferRouteTransitionLiveEpochProjectionProof.RecoveredCount),
        ("filetransfer_live_route_epoch_projection_missing_unresolved_count={0}" -f $fileTransferRouteTransitionLiveEpochProjectionProof.UnresolvedCount),
        ("filetransfer_route_transition_verdict={0}" -f $fileTransferRouteTransitionProof.Verdict),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_raw_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.RawCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_distinct_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.DistinctCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_recovered_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.RecoveredCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_unresolved_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.UnresolvedCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_verdict={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.Verdict),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.RawCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.DistinctCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_recovered_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.RecoveredCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.UnresolvedCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_verdict={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.Verdict),
        ("file_tuna_v4_data_path_mismatch_count={0}" -f $fileTunaV4DataPathMismatchProof.EffectiveCount),
        ("runtime_unlock_retry_dispatch_authority_granted_raw_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.AuthorityGrantedRawCount),
        ("runtime_unlock_retry_dispatch_authority_granted_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.EffectiveGrantEvents.Count),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_raw_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteRawCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_distinct_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteDistinctCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_recovered_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteRecoveredCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_unresolved_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteUnresolvedCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_verdict={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceVerdict),
        ("runtime_unlock_retry_lifecycle_execution_observation_count={0}" -f $runtimeUnlockRetryLifecycleProof.ExecutionObservationCount),
        ("runtime_unlock_retry_lifecycle_execution_decision_count={0}" -f $runtimeUnlockRetryLifecycleProof.ExecutionDecisionCount),
        ("runtime_unlock_retry_lifecycle_execution_unresolved_count={0}" -f @($runtimeUnlockRetryLifecycleProof.UnresolvedExecutionObservationEvents).Count),
        ("runtime_unlock_retry_lifecycle_successor_evaluation_count={0}" -f $runtimeUnlockRetryLifecycleProof.SuccessorEvaluationCount),
        ("runtime_unlock_retry_lifecycle_successor_decision_count={0}" -f $runtimeUnlockRetryLifecycleProof.SuccessorDecisionCount),
        ("runtime_unlock_retry_lifecycle_successor_unresolved_count={0}" -f @($runtimeUnlockRetryLifecycleProof.UnresolvedSuccessorEvaluationEvents).Count),
        ("runtime_unlock_retry_lifecycle_deadline_evaluation_count={0}" -f $runtimeUnlockRetryLifecycleProof.DeadlineEvaluationCount),
        ("runtime_unlock_retry_lifecycle_deadline_decision_count={0}" -f $runtimeUnlockRetryLifecycleProof.DeadlineDecisionCount),
        ("runtime_unlock_retry_lifecycle_deadline_unresolved_count={0}" -f @($runtimeUnlockRetryLifecycleProof.UnresolvedDeadlineEvaluationEvents).Count),
        ("runtime_unlock_retry_lifecycle_verdict={0}" -f $runtimeUnlockRetryLifecycleProof.Verdict),
        ("regular_v4_frontier_repair_missing_range_not_preserved_raw_count={0}" -f $regularV4FrontierRepairMissingRangePreservationProof.RawCount),
        ("regular_v4_frontier_repair_missing_range_not_preserved_distinct_count={0}" -f $regularV4FrontierRepairMissingRangePreservationProof.DistinctCount),
        ("regular_v4_frontier_repair_missing_range_not_preserved_recovered_count={0}" -f $regularV4FrontierRepairMissingRangePreservationProof.RecoveredCount),
        ("regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count={0}" -f $regularV4FrontierRepairMissingRangePreservationProof.UnresolvedCount),
        ("regular_v4_frontier_repair_missing_range_preservation_verdict={0}" -f $regularV4FrontierRepairMissingRangePreservationProof.Verdict),
        ("regular_v4_frontier_tail_repair_state_publication_missing_raw_count={0}" -f $regularV4FrontierTailRepairStatePublicationProof.RawCount),
        ("regular_v4_frontier_tail_repair_state_publication_missing_distinct_count={0}" -f $regularV4FrontierTailRepairStatePublicationProof.DistinctCount),
        ("regular_v4_frontier_tail_repair_state_publication_missing_recovered_count={0}" -f $regularV4FrontierTailRepairStatePublicationProof.RecoveredCount),
        ("regular_v4_frontier_tail_repair_state_publication_missing_unresolved_count={0}" -f $regularV4FrontierTailRepairStatePublicationProof.UnresolvedCount),
        ("regular_v4_frontier_tail_repair_state_publication_verdict={0}" -f $regularV4FrontierTailRepairStatePublicationProof.Verdict),
        ("regular_v4_frontier_repair_projection_reauthorization_missing_raw_count={0}" -f $regularV4FrontierRepairProjectionReauthorizationProof.RawCount),
        ("regular_v4_frontier_repair_projection_reauthorization_missing_distinct_count={0}" -f $regularV4FrontierRepairProjectionReauthorizationProof.DistinctCount),
        ("regular_v4_frontier_repair_projection_reauthorization_missing_recovered_count={0}" -f $regularV4FrontierRepairProjectionReauthorizationProof.RecoveredCount),
        ("regular_v4_frontier_repair_projection_reauthorization_missing_unresolved_count={0}" -f $regularV4FrontierRepairProjectionReauthorizationProof.UnresolvedCount),
        ("regular_v4_frontier_repair_projection_reauthorization_verdict={0}" -f $regularV4FrontierRepairProjectionReauthorizationProof.Verdict),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_raw_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.RawCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_distinct_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.DistinctCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_recovered_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.RecoveredCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_unresolved_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.UnresolvedCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_preservation_verdict={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.Verdict),
        ("regular_v4_frontier_repair_stalled_raw_count={0}" -f $regularV4FrontierRepairLifecycleProof.RawCount),
        ("regular_v4_frontier_repair_stalled_distinct_count={0}" -f $regularV4FrontierRepairLifecycleProof.DistinctCount),
        ("regular_v4_frontier_repair_stalled_recovered_count={0}" -f $regularV4FrontierRepairLifecycleProof.RecoveredCount),
        ("regular_v4_frontier_repair_stalled_unresolved_count={0}" -f $regularV4FrontierRepairLifecycleProof.UnresolvedCount),
        ("regular_v4_frontier_repair_stalled_verdict={0}" -f $regularV4FrontierRepairLifecycleProof.Verdict),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_raw_count={0}" -f $regularV4FrontierRepairSettlementDispositionProof.RawCount),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_distinct_count={0}" -f $regularV4FrontierRepairSettlementDispositionProof.DistinctCount),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_recovered_count={0}" -f $regularV4FrontierRepairSettlementDispositionProof.RecoveredCount),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_unresolved_count={0}" -f $regularV4FrontierRepairSettlementDispositionProof.UnresolvedCount),
        ("regular_v4_frontier_repair_settlement_disposition_verdict={0}" -f $regularV4FrontierRepairSettlementDispositionProof.Verdict),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_raw_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.RawCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_distinct_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.DistinctCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_recovered_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.RecoveredCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_unresolved_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.UnresolvedCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_verdict={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.Verdict),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_raw_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.RawCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_distinct_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.DistinctCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_recovered_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.RecoveredCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_unresolved_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.UnresolvedCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_verdict={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.Verdict),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_raw_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.RawCount),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_distinct_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.DistinctCount),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_recovered_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.RecoveredCount),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_unresolved_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.UnresolvedCount),
        ("regular_v4_frontier_repair_successor_pressure_install_verdict={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.Verdict),
        ("regular_v4_frontier_repair_initial_pressure_install_missing_raw_count={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.RawCount),
        ("regular_v4_frontier_repair_initial_pressure_install_missing_distinct_count={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.DistinctCount),
        ("regular_v4_frontier_repair_initial_pressure_install_missing_recovered_count={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.RecoveredCount),
        ("regular_v4_frontier_repair_initial_pressure_install_missing_unresolved_count={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.UnresolvedCount),
        ("regular_v4_frontier_repair_initial_pressure_install_verdict={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.Verdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_rejected_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureRejectedCount),
        ("filetransfer_receive_recovery_operation_raw_count={0}" -f $fileTransferReceiveRecoveryOperationProof.RawCount),
        ("filetransfer_receive_recovery_operation_distinct_count={0}" -f $fileTransferReceiveRecoveryOperationProof.DistinctCount),
        ("filetransfer_receive_recovery_operation_recovered_count={0}" -f $fileTransferReceiveRecoveryOperationProof.RecoveredCount),
        ("filetransfer_receive_recovery_operation_unresolved_count={0}" -f $fileTransferReceiveRecoveryOperationProof.UnresolvedCount),
        ("filetransfer_receive_recovery_operation_verdict={0}" -f $fileTransferReceiveRecoveryOperationProof.Verdict),
        ("filetransfer_receive_recovery_operation_started_count={0}" -f $fileTransferReceiveRecoveryOperationProof.StartedCount),
        ("filetransfer_receive_recovery_operation_execution_receipt_applied_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ExecutionReceiptAppliedCount),
        ("filetransfer_receive_recovery_operation_proof_observed_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ProofObservedCount),
        ("filetransfer_receive_recovery_operation_closure_retained_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ClosureRetainedCount),
        ("filetransfer_receive_recovery_operation_closure_adopted_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ClosureAdoptedCount),
        ("filetransfer_receive_recovery_operation_closure_reserved_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ClosureReservedCount),
        ("filetransfer_receive_recovery_operation_closure_consumed_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ClosureConsumedCount),
        ("runtime_unlock_regular_v4_recovery_lease_not_issued_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.RawCount),
        ("runtime_unlock_regular_v4_recovery_lease_not_issued_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.RawCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_verdict={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.Verdict),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.RawCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_verdict={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.Verdict),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.RawCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_verdict={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.Verdict),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.RawCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppression_verdict={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.Verdict),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.RawCount),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_proven_successor_verdict={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.Verdict),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_raw_count={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.RawCount),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.DistinctCount),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.RecoveredCount),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.UnresolvedCount),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_verdict={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.Verdict),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.RawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.RecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.UnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_verdict={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.Verdict),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredUnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingUnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffUnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldUnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckUnresolvedCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_raw_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.RawCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_distinct_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.DistinctCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_recovered_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.RecoveredCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_unresolved_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.UnresolvedCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_verdict={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.Verdict),
        ("filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_raw_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestRawCount),
        ("filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_distinct_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestDistinctCount),
        ("filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_recovered_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestRecoveredCount),
        ("filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_unresolved_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestUnresolvedCount),
        ("runtime_unlock_regular_v4_recovery_lease_not_issued_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.RecoveredCount),
        ("runtime_unlock_regular_v4_recovery_lease_not_issued_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.UnresolvedCount),
        ("runtime_unlock_regular_v4_recovery_lease_issuance_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.Verdict),
        ("runtime_unlock_recovery_lease_closure_not_adopted_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedRawCount),
        ("runtime_unlock_recovery_lease_closure_not_adopted_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedDistinctCount),
        ("runtime_unlock_recovery_lease_closure_not_adopted_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedRecoveredCount),
        ("runtime_unlock_recovery_lease_closure_not_adopted_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedUnresolvedCount),
        ("runtime_unlock_recovery_lease_closure_adoption_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureAdoptionVerdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_deferral_rejected_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessRejectedCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_deferral_granted_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessGrantedCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessRawCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessDistinctCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessRecoveredCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessUnresolvedCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_raw_count={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.RawCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_distinct_count={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.DistinctCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_recovered_count={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.RecoveredCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_unresolved_count={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.UnresolvedCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_verdict={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.Verdict),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_raw_count={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.RawCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_distinct_count={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.DistinctCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_recovered_count={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.RecoveredCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_unresolved_count={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.UnresolvedCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_verdict={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.Verdict),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_raw_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.RawCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_distinct_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.DistinctCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_recovered_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.RecoveredCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_unresolved_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.UnresolvedCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_verdict={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.Verdict),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_raw_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredRawCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_distinct_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredDistinctCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_recovered_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredRecoveredCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_unresolved_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredUnresolvedCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_raw_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationRawCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_distinct_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationDistinctCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_recovered_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationRecoveredCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_unresolved_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationUnresolvedCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_raw_count={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.RawCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_distinct_count={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.DistinctCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_recovered_count={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.RecoveredCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_unresolved_count={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.UnresolvedCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_verdict={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.Verdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessVerdict),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedRawCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedDistinctCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedRecoveredCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedUnresolvedCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_preservation_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldPreservationVerdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.RawCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.DistinctCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.RecoveredCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.UnresolvedCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_verdict={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.Verdict),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_raw_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.RawCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_distinct_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.DistinctCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_recovered_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.RecoveredCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_unresolved_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.UnresolvedCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_verdict={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.Verdict),
        ("runtime_unlock_initial_activation_answer_peer_delivery_missing_raw_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.RawCount),
        ("runtime_unlock_initial_activation_answer_peer_delivery_missing_distinct_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.DistinctCount),
        ("runtime_unlock_initial_activation_answer_peer_delivery_missing_recovered_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.RecoveredCount),
        ("runtime_unlock_initial_activation_answer_peer_delivery_missing_unresolved_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.UnresolvedCount),
        ("runtime_unlock_initial_activation_answer_peer_delivery_verdict={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.Verdict),
        ("runtime_unlock_initial_activation_answer_ack_return_delivery_missing_raw_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnRawCount),
        ("runtime_unlock_initial_activation_answer_ack_return_delivery_missing_distinct_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnDistinctCount),
        ("runtime_unlock_initial_activation_answer_ack_return_delivery_missing_recovered_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnRecoveredCount),
        ("runtime_unlock_initial_activation_answer_ack_return_delivery_missing_unresolved_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnUnresolvedCount),
        ("runtime_unlock_dial_failed_rejected_answer_not_retained_raw_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerRawCount),
        ("runtime_unlock_dial_failed_rejected_answer_not_retained_distinct_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerDistinctCount),
        ("runtime_unlock_dial_failed_rejected_answer_not_retained_recovered_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerRecoveredCount),
        ("runtime_unlock_dial_failed_rejected_answer_not_retained_unresolved_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerUnresolvedCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_raw_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.RawCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_distinct_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.DistinctCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_recovered_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.RecoveredCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_unresolved_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.UnresolvedCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_verdict={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.Verdict),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_raw_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.RawCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_distinct_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.DistinctCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_recovered_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.RecoveredCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_unresolved_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.UnresolvedCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_verdict={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.Verdict),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_raw_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.RawCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_distinct_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.DistinctCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_recovered_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.RecoveredCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_unresolved_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.UnresolvedCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_verdict={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.Verdict),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_raw_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.RawCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_distinct_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.DistinctCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_recovered_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.RecoveredCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_unresolved_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.UnresolvedCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_verdict={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.Verdict),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_requested_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.RequestedCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_decided_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.DecidedCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_discarded_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.DiscardedCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_raw_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.RawCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_distinct_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.DistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_recovered_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.RecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_unresolved_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.UnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_verdict={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.Verdict),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_raw_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementRawCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_distinct_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementDistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_recovered_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementRecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_unresolved_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementUnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_raw_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingRawCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_distinct_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingDistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_recovered_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingRecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_unresolved_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingUnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_raw_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldRawCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_distinct_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldDistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_recovered_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldRecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_unresolved_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldUnresolvedCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_raw_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.RawCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_distinct_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.DistinctCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_recovered_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.RecoveredCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_unresolved_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.UnresolvedCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_raw_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationRawCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_distinct_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationDistinctCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_recovered_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationRecoveredCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_unresolved_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationUnresolvedCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_verdict={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.Verdict),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_missing_raw_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RawCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_missing_distinct_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.DistinctCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_missing_recovered_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RecoveredCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_missing_unresolved_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.UnresolvedCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_raw_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredRawCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_distinct_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredDistinctCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_recovered_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredRecoveredCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_unresolved_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredUnresolvedCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_raw_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingRawCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_distinct_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingDistinctCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_recovered_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingRecoveredCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_unresolved_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingUnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_raw_count={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.RawCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_distinct_count={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.DistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_recovered_count={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.RecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_unresolved_count={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.UnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_verdict={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.Verdict),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_count={0}" -f $runtimeUnlockRetryRegularV4RecoverySettlementMissingCount),
        ("runtime_unlock_retry_blocked_by_regular_v4_receive_recovery_count={0}" -f $runtimeUnlockRetryBlockedByRegularV4RecoveryCount),
        ("runtime_unlock_retry_held_by_regular_v4_receive_recovery_count={0}" -f $runtimeUnlockRetryHeldByRegularV4RecoveryCount),
        ("runtime_unlock_retry_blocked_by_regular_v4_recovery_count={0}" -f $runtimeUnlockRetryBlockedByRegularV4RecoveryCount),
        ("runtime_unlock_retry_held_by_regular_v4_recovery_count={0}" -f $runtimeUnlockRetryHeldByRegularV4RecoveryCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_raw_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.RawCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_distinct_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.DistinctCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_recovered_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.RecoveredCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.UnresolvedCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_verdict={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.Verdict),
        ("runtime_unlock_retry_regular_v4_settlement_handoff_bypassed_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.HandoffBypassCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_raw_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.RawCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_distinct_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.DistinctCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_recovered_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.RecoveredCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_unresolved_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.UnresolvedCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_verdict={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.Verdict),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_raw_count={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.RawCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_distinct_count={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.DistinctCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_recovered_count={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.RecoveredCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_unresolved_count={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.UnresolvedCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppression_verdict={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.Verdict),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_raw_count={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.RawCount),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_distinct_count={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.DistinctCount),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_recovered_count={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.RecoveredCount),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_unresolved_count={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.UnresolvedCount),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_verdict={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.Verdict),
        ("post_tuna_fallback_outbound_receive_worker_handoff_missing_count={0}" -f $outboundReceiveWorkerHandoffProof.Count),
        ("fallback_tail_yield_authority_rejected_count={0}" -f $fallbackTailYieldAuthorityRejectedCount),
        ("fallback_tail_coordinator_aggregate_verdict={0}" -f $fallbackTailCoordinatorAggregateProof.Verdict),
        ("fallback_tail_coordinator_yield_issued_count={0}" -f $fallbackTailCoordinatorAggregateProof.YieldIssuedCount),
        ("fallback_tail_coordinator_local_proof_receipt_decided_count={0}" -f $fallbackTailCoordinatorAggregateProof.LocalProofReceiptDecidedCount),
        ("fallback_tail_coordinator_peer_proof_receipt_decided_count={0}" -f $fallbackTailCoordinatorAggregateProof.PeerProofReceiptDecidedCount),
        ("fallback_tail_coordinator_pairing_committed_count={0}" -f $fallbackTailCoordinatorAggregateProof.PairingCommittedCount),
        ("fallback_tail_coordinator_valid_pairing_count={0}" -f $fallbackTailCoordinatorAggregateProof.ValidPairingCount),
        ("fallback_tail_coordinator_paired_release_reserved_count={0}" -f $fallbackTailCoordinatorAggregateProof.PairedReleaseReservedCount),
        ("fallback_tail_coordinator_valid_paired_release_reserved_count={0}" -f $fallbackTailCoordinatorAggregateProof.ValidPairedReleaseReservedCount),
        ("fallback_tail_coordinator_paired_release_consumed_count={0}" -f $fallbackTailCoordinatorAggregateProof.PairedReleaseConsumedCount),
        ("fallback_tail_coordinator_valid_paired_release_consumed_count={0}" -f $fallbackTailCoordinatorAggregateProof.ValidPairedReleaseConsumedCount),
        ("fallback_tail_coordinator_paired_release_carry_forward_count={0}" -f $fallbackTailCoordinatorAggregateProof.PairedReleaseCarryForwardCount),
        ("fallback_tail_coordinator_valid_paired_release_carry_forward_count={0}" -f $fallbackTailCoordinatorAggregateProof.ValidPairedReleaseCarryForwardCount),
        ("fallback_tail_coordinator_invalid_raw_count={0}" -f $fallbackTailCoordinatorAggregateProof.InvalidRawCount),
        ("fallback_tail_coordinator_invalid_distinct_count={0}" -f $fallbackTailCoordinatorAggregateProof.InvalidDistinctCount),
        ("fallback_tail_coordinator_invalid_recovered_count={0}" -f $fallbackTailCoordinatorAggregateProof.InvalidRecoveredCount),
        ("fallback_tail_coordinator_invalid_unresolved_count={0}" -f $fallbackTailCoordinatorAggregateProof.InvalidUnresolvedCount),
        ("fallback_tail_coordinator_reauthorization_staged_count={0}" -f $fallbackTailCoordinatorAggregateProof.ReauthorizationStagedCount),
        ("fallback_tail_coordinator_reauthorization_rebound_count={0}" -f $fallbackTailCoordinatorAggregateProof.ReauthorizationReboundCount),
        ("fallback_tail_coordinator_reauthorization_successor_issued_count={0}" -f $fallbackTailCoordinatorAggregateProof.ReauthorizationSuccessorIssuedCount),
        ("fallback_tail_coordinator_reauthorization_invalidated_count={0}" -f $fallbackTailCoordinatorAggregateProof.ReauthorizationInvalidatedCount),
        ("fallback_tail_owner_generation_mismatch_count={0}" -f $fallbackTailOwnerGenerationMismatchCount),
        ("fallback_tail_bridge_recovery_generation_pairing_verdict={0}" -f $fallbackTailBridgeRecoveryPairingProof.Verdict),
        ("fallback_tail_bridge_recovery_generation_mismatch_raw_count={0}" -f $fallbackTailBridgeRecoveryPairingProof.RawCount),
        ("fallback_tail_bridge_recovery_generation_mismatch_distinct_count={0}" -f $fallbackTailBridgeRecoveryPairingProof.DistinctCount),
        ("fallback_tail_bridge_recovery_generation_mismatch_recovered_count={0}" -f $fallbackTailBridgeRecoveryPairingProof.RecoveredCount),
        ("fallback_tail_bridge_recovery_generation_mismatch_unresolved_count={0}" -f $fallbackTailBridgeRecoveryPairingProof.UnresolvedCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict={0}" -f $fallbackTailTransportEpochReauthorizationProof.Verdict),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count={0}" -f $fallbackTailTransportEpochReauthorizationProof.RawCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count={0}" -f $fallbackTailTransportEpochReauthorizationProof.DistinctCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count={0}" -f $fallbackTailTransportEpochReauthorizationProof.RecoveredCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count={0}" -f $fallbackTailTransportEpochReauthorizationProof.UnresolvedCount),
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
        ("recovery_failure_class={0}" -f $recoveryFailureClass),
        ("failure_variant={0}" -f $failureVariant),
        ("first_proof_chain_failure_class={0}" -f $firstProofChainFailureClass),
        ("first_proof_chain_failure_event={0}" -f $firstProofChainFailureEvent),
        ("first_proof_chain_failure_reason={0}" -f $firstProofChainFailureReason),
        ("first_proof_chain_failure_sequence={0}" -f $firstProofChainFailureSequence),
        ("first_proof_chain_failure_source_path={0}" -f $firstProofChainFailureSourcePath),
        ("first_proof_chain_failure_line_number={0}" -f $firstProofChainFailureLineNumber),
        ("first_proof_chain_failure_writer_sequence={0}" -f $firstProofChainFailureWriterSequence),
        ("semantic_failure_domain={0}" -f $semanticFailureDomain),
        ("filetransfer_gui_live_fallback_fault_window_missed_raw_count={0}" -f $fileTransferGuiLiveFallbackFaultWindowProof.RawCount),
        ("filetransfer_gui_live_fallback_fault_window_missed_distinct_count={0}" -f $fileTransferGuiLiveFallbackFaultWindowProof.DistinctCount),
        ("filetransfer_gui_live_fallback_fault_window_missed_recovered_count=0"),
        ("filetransfer_gui_live_fallback_fault_window_missed_unresolved_count={0}" -f $fileTransferGuiLiveFallbackFaultWindowProof.UnresolvedCount),
        ("filetransfer_gui_live_fallback_fault_window_missed_verdict={0}" -f $fileTransferGuiLiveFallbackFaultWindowProof.Verdict),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_raw_count={0}" -f $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingRawCount),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_distinct_count={0}" -f $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingDistinctCount),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_recovered_count={0}" -f $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingRecoveredCount),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_unresolved_count={0}" -f $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingUnresolvedCount),
        ("filetransfer_fallback_v6_proof_bootstrap_missing_verdict={0}" -f ($(if ($fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingUnresolvedCount -gt 0) { 'fail' } else { 'pass' }))),
        ("filetransfer_live_route_epoch_projection_missing_raw_count={0}" -f $fileTransferRouteTransitionLiveEpochProjectionProof.RawCount),
        ("filetransfer_live_route_epoch_projection_missing_distinct_count={0}" -f $fileTransferRouteTransitionLiveEpochProjectionProof.DistinctCount),
        ("filetransfer_live_route_epoch_projection_missing_recovered_count={0}" -f $fileTransferRouteTransitionLiveEpochProjectionProof.RecoveredCount),
        ("filetransfer_live_route_epoch_projection_missing_unresolved_count={0}" -f $fileTransferRouteTransitionLiveEpochProjectionProof.UnresolvedCount),
        ("filetransfer_route_transition_incomplete_raw_count={0}" -f $fileTransferRouteTransitionProof.RawCount),
        ("filetransfer_route_transition_incomplete_distinct_count={0}" -f $fileTransferRouteTransitionProof.DistinctCount),
        ("filetransfer_route_transition_incomplete_recovered_count={0}" -f $fileTransferRouteTransitionProof.RecoveredCount),
        ("filetransfer_route_transition_incomplete_unresolved_count={0}" -f $fileTransferRouteTransitionProof.UnresolvedCount),
        ("filetransfer_route_transition_verdict={0}" -f $fileTransferRouteTransitionProof.Verdict),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_raw_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.RawCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_distinct_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.DistinctCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_recovered_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.RecoveredCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_unresolved_count={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.UnresolvedCount),
        ("filetransfer_route_transition_draining_transport_grace_terminalization_verdict={0}" -f $fileTransferRouteTransitionDrainingTransportGraceProof.Verdict),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.RawCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.DistinctCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_recovered_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.RecoveredCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.UnresolvedCount),
        ("runtime_unlock_regular_v4_route_commit_send_drain_verdict={0}" -f $runtimeUnlockRegularV4RouteCommitSendDrainProof.Verdict),
        ("file_tuna_v4_data_path_mismatch_count={0}" -f $fileTunaV4DataPathMismatchProof.EffectiveCount),
        ("runtime_unlock_retry_dispatch_authority_granted_raw_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.AuthorityGrantedRawCount),
        ("runtime_unlock_retry_dispatch_decision_staged_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.DecisionStagedCount),
        ("runtime_unlock_retry_dispatch_install_receipt_issued_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.ReceiptIssuedCount),
        ("runtime_unlock_retry_dispatch_install_committed_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.CommittedCount),
        ("runtime_unlock_retry_dispatch_install_rejected_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.RejectedCount),
        ("runtime_unlock_retry_dispatch_authority_granted_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.EffectiveGrantEvents.Count),
        ("runtime_unlock_retry_dispatch_observed_count={0}" -f $runtimeUnlockRetryDispatchObservedCount),
        ("runtime_unlock_retry_dispatch_install_verdict={0}" -f $runtimeUnlockRetryDispatchInstallProof.Verdict),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_raw_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteRawCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_distinct_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteDistinctCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_recovered_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteRecoveredCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_unresolved_count={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteUnresolvedCount),
        ("runtime_unlock_retry_dispatch_install_grant_evidence_verdict={0}" -f $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceVerdict),
        ("runtime_unlock_retry_lifecycle_execution_observation_count={0}" -f $runtimeUnlockRetryLifecycleProof.ExecutionObservationCount),
        ("runtime_unlock_retry_lifecycle_execution_decision_count={0}" -f $runtimeUnlockRetryLifecycleProof.ExecutionDecisionCount),
        ("runtime_unlock_retry_lifecycle_execution_unresolved_count={0}" -f @($runtimeUnlockRetryLifecycleProof.UnresolvedExecutionObservationEvents).Count),
        ("runtime_unlock_retry_lifecycle_successor_evaluation_count={0}" -f $runtimeUnlockRetryLifecycleProof.SuccessorEvaluationCount),
        ("runtime_unlock_retry_lifecycle_successor_decision_count={0}" -f $runtimeUnlockRetryLifecycleProof.SuccessorDecisionCount),
        ("runtime_unlock_retry_lifecycle_successor_unresolved_count={0}" -f @($runtimeUnlockRetryLifecycleProof.UnresolvedSuccessorEvaluationEvents).Count),
        ("runtime_unlock_retry_lifecycle_deadline_evaluation_count={0}" -f $runtimeUnlockRetryLifecycleProof.DeadlineEvaluationCount),
        ("runtime_unlock_retry_lifecycle_deadline_decision_count={0}" -f $runtimeUnlockRetryLifecycleProof.DeadlineDecisionCount),
        ("runtime_unlock_retry_lifecycle_deadline_unresolved_count={0}" -f @($runtimeUnlockRetryLifecycleProof.UnresolvedDeadlineEvaluationEvents).Count),
        ("runtime_unlock_retry_lifecycle_verdict={0}" -f $runtimeUnlockRetryLifecycleProof.Verdict),
        ("runtime_unlock_retry_dispatch_proof_verdict={0}" -f $runtimeUnlockRetryDispatchProofVerdict),
        ("runtime_unlock_retry_dispatch_post_grant_hold_count={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldProof.RawCount),
        ("runtime_unlock_retry_dispatch_post_grant_hold_raw_count={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldProof.RawCount),
        ("runtime_unlock_retry_dispatch_post_grant_hold_distinct_count={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldProof.DistinctCount),
        ("runtime_unlock_retry_dispatch_post_grant_hold_recovered_count={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldProof.RecoveredCount),
        ("runtime_unlock_retry_dispatch_post_grant_hold_unresolved_count={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldProof.UnresolvedCount),
        ("runtime_unlock_retry_dispatch_post_grant_hold_verdict={0}" -f $runtimeUnlockRetryDispatchPostGrantHoldProof.Verdict),
        ("runtime_unlock_precommit_probe_start_handoff_missing_raw_count={0}" -f $runtimeUnlockPreCommitProbeStartHandoffProof.RawCount),
        ("runtime_unlock_precommit_probe_start_handoff_missing_distinct_count={0}" -f $runtimeUnlockPreCommitProbeStartHandoffProof.DistinctCount),
        ("runtime_unlock_precommit_probe_start_handoff_missing_recovered_count={0}" -f $runtimeUnlockPreCommitProbeStartHandoffProof.RecoveredCount),
        ("runtime_unlock_precommit_probe_start_handoff_missing_unresolved_count={0}" -f $runtimeUnlockPreCommitProbeStartHandoffProof.UnresolvedCount),
        ("runtime_unlock_precommit_probe_start_handoff_verdict={0}" -f $runtimeUnlockPreCommitProbeStartHandoffProof.Verdict),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_raw_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.RawCount),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_distinct_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.DistinctCount),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_recovered_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.RecoveredCount),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_unresolved_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.UnresolvedCount),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_verdict={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.Verdict),
        ("runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_raw_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.InitialActivationPeerAcceptanceScopeRejectedRawCount),
        ("runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_distinct_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.InitialActivationPeerAcceptanceScopeRejectedDistinctCount),
        ("runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_recovered_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.InitialActivationPeerAcceptanceScopeRejectedRecoveredCount),
        ("runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_unresolved_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.InitialActivationPeerAcceptanceScopeRejectedUnresolvedCount),
        ("runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_verdict={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.InitialActivationPeerAcceptanceScopeRejectedVerdict),
        ("runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_raw_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AckBeforeProbeAdoptionRecheckMissingRawCount),
        ("runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_distinct_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AckBeforeProbeAdoptionRecheckMissingDistinctCount),
        ("runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_recovered_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AckBeforeProbeAdoptionRecheckMissingRecoveredCount),
        ("runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_unresolved_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AckBeforeProbeAdoptionRecheckMissingUnresolvedCount),
        ("runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_verdict={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AckBeforeProbeAdoptionRecheckMissingVerdict),
        ("runtime_unlock_responder_acked_probe_binding_missing_raw_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.BindingMissingRawCount),
        ("runtime_unlock_responder_acked_probe_binding_missing_distinct_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.BindingMissingDistinctCount),
        ("runtime_unlock_responder_acked_probe_binding_missing_recovered_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.BindingMissingRecoveredCount),
        ("runtime_unlock_responder_acked_probe_binding_missing_unresolved_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.BindingMissingUnresolvedCount),
        ("runtime_unlock_responder_acked_probe_binding_missing_verdict={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.BindingMissingVerdict),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_raw_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AfterBoundHandoffMissingRawCount),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_distinct_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AfterBoundHandoffMissingDistinctCount),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_recovered_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AfterBoundHandoffMissingRecoveredCount),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_unresolved_count={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AfterBoundHandoffMissingUnresolvedCount),
        ("runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_verdict={0}" -f $runtimeUnlockResponderPeerProbeHandoffProof.AfterBoundHandoffMissingVerdict),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_raw_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.RawCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_distinct_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.DistinctCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_recovered_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.RecoveredCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_unresolved_count={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.UnresolvedCount),
        ("regular_v4_peer_silence_replay_missing_dispatch_range_preservation_verdict={0}" -f $regularV4PeerSilenceReplayMissingDispatchRangeProof.Verdict),
        ("regular_v4_frontier_repair_stalled_raw_count={0}" -f $regularV4FrontierRepairLifecycleProof.RawCount),
        ("regular_v4_frontier_repair_stalled_distinct_count={0}" -f $regularV4FrontierRepairLifecycleProof.DistinctCount),
        ("regular_v4_frontier_repair_stalled_recovered_count={0}" -f $regularV4FrontierRepairLifecycleProof.RecoveredCount),
        ("regular_v4_frontier_repair_stalled_unresolved_count={0}" -f $regularV4FrontierRepairLifecycleProof.UnresolvedCount),
        ("regular_v4_frontier_repair_stalled_verdict={0}" -f $regularV4FrontierRepairLifecycleProof.Verdict),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_raw_count={0}" -f $regularV4FrontierRepairSettlementDispositionProof.RawCount),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_distinct_count={0}" -f $regularV4FrontierRepairSettlementDispositionProof.DistinctCount),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_recovered_count={0}" -f $regularV4FrontierRepairSettlementDispositionProof.RecoveredCount),
        ("regular_v4_frontier_repair_settlement_disposition_unverifiable_unresolved_count={0}" -f $regularV4FrontierRepairSettlementDispositionProof.UnresolvedCount),
        ("regular_v4_frontier_repair_settlement_disposition_verdict={0}" -f $regularV4FrontierRepairSettlementDispositionProof.Verdict),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_raw_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.RawCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_distinct_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.DistinctCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_recovered_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.RecoveredCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_unresolved_count={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.UnresolvedCount),
        ("regular_v4_frontier_repair_pressure_authority_lifecycle_split_verdict={0}" -f $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.Verdict),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_raw_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.RawCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_distinct_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.DistinctCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_recovered_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.RecoveredCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_missing_unresolved_count={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.UnresolvedCount),
        ("regular_v4_frontier_repair_pressure_recovery_handoff_verdict={0}" -f $regularV4FrontierRepairPressureRecoveryHandoffProof.Verdict),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_raw_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.RawCount),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_distinct_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.DistinctCount),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_recovered_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.RecoveredCount),
        ("regular_v4_frontier_repair_successor_pressure_install_missing_unresolved_count={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.UnresolvedCount),
        ("regular_v4_frontier_repair_successor_pressure_install_verdict={0}" -f $regularV4FrontierRepairSuccessorPressureInstallProof.Verdict),
        ("regular_v4_frontier_repair_initial_pressure_install_missing_raw_count={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.RawCount),
        ("regular_v4_frontier_repair_initial_pressure_install_missing_distinct_count={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.DistinctCount),
        ("regular_v4_frontier_repair_initial_pressure_install_missing_recovered_count={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.RecoveredCount),
        ("regular_v4_frontier_repair_initial_pressure_install_missing_unresolved_count={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.UnresolvedCount),
        ("regular_v4_frontier_repair_initial_pressure_install_verdict={0}" -f $regularV4FrontierRepairInitialPressureInstallProof.Verdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_rejected_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureRejectedCount),
        ("filetransfer_receive_recovery_operation_raw_count={0}" -f $fileTransferReceiveRecoveryOperationProof.RawCount),
        ("filetransfer_receive_recovery_operation_distinct_count={0}" -f $fileTransferReceiveRecoveryOperationProof.DistinctCount),
        ("filetransfer_receive_recovery_operation_recovered_count={0}" -f $fileTransferReceiveRecoveryOperationProof.RecoveredCount),
        ("filetransfer_receive_recovery_operation_unresolved_count={0}" -f $fileTransferReceiveRecoveryOperationProof.UnresolvedCount),
        ("filetransfer_receive_recovery_operation_verdict={0}" -f $fileTransferReceiveRecoveryOperationProof.Verdict),
        ("filetransfer_receive_recovery_operation_started_count={0}" -f $fileTransferReceiveRecoveryOperationProof.StartedCount),
        ("filetransfer_receive_recovery_operation_execution_receipt_applied_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ExecutionReceiptAppliedCount),
        ("filetransfer_receive_recovery_operation_proof_observed_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ProofObservedCount),
        ("filetransfer_receive_recovery_operation_closure_retained_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ClosureRetainedCount),
        ("filetransfer_receive_recovery_operation_closure_adopted_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ClosureAdoptedCount),
        ("filetransfer_receive_recovery_operation_closure_reserved_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ClosureReservedCount),
        ("filetransfer_receive_recovery_operation_closure_consumed_count={0}" -f $fileTransferReceiveRecoveryOperationProof.ClosureConsumedCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.RawCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_unproven_successor_verdict={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.Verdict),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.RawCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_unproven_payload_hold_verdict={0}" -f $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.Verdict),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.RawCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_publication_verdict={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.Verdict),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.RawCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppression_verdict={0}" -f $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.Verdict),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_raw_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.RawCount),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.DistinctCount),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.RecoveredCount),
        ("filetransfer_receive_recovery_completed_proven_successor_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.UnresolvedCount),
        ("filetransfer_receive_recovery_completed_proven_successor_verdict={0}" -f $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.Verdict),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_raw_count={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.RawCount),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.DistinctCount),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.RecoveredCount),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.UnresolvedCount),
        ("filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_verdict={0}" -f $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.Verdict),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.RawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.RecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.UnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_verdict={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.Verdict),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredUnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingUnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffUnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldUnresolvedCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_raw_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckRawCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_distinct_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckDistinctCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_recovered_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckRecoveredCount),
        ("filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_unresolved_count={0}" -f $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckUnresolvedCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_raw_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.RawCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_distinct_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.DistinctCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_recovered_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.RecoveredCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_unresolved_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.UnresolvedCount),
        ("filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_verdict={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.Verdict),
        ("filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_raw_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestRawCount),
        ("filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_distinct_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestDistinctCount),
        ("filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_recovered_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestRecoveredCount),
        ("filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_unresolved_count={0}" -f $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestUnresolvedCount),
        ("runtime_unlock_regular_v4_recovery_lease_not_issued_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.RawCount),
        ("runtime_unlock_regular_v4_recovery_lease_not_issued_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.DistinctCount),
        ("runtime_unlock_regular_v4_recovery_lease_not_issued_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.RecoveredCount),
        ("runtime_unlock_regular_v4_recovery_lease_not_issued_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.UnresolvedCount),
        ("runtime_unlock_regular_v4_recovery_lease_issuance_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.Verdict),
        ("runtime_unlock_recovery_lease_closure_not_adopted_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedRawCount),
        ("runtime_unlock_recovery_lease_closure_not_adopted_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedDistinctCount),
        ("runtime_unlock_recovery_lease_closure_not_adopted_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedRecoveredCount),
        ("runtime_unlock_recovery_lease_closure_not_adopted_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedUnresolvedCount),
        ("runtime_unlock_recovery_lease_closure_adoption_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureAdoptionVerdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_deferral_rejected_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessRejectedCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_deferral_granted_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessGrantedCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessRawCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessDistinctCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessRecoveredCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessUnresolvedCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_raw_count={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.RawCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_distinct_count={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.DistinctCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_recovered_count={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.RecoveredCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_unresolved_count={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.UnresolvedCount),
        ("filetransfer_bridge_recovery_execution_soft_to_hard_handoff_verdict={0}" -f $bridgeRecoveryExecutionSoftToHardHandoffProof.Verdict),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_raw_count={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.RawCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_distinct_count={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.DistinctCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_recovered_count={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.RecoveredCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_unresolved_count={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.UnresolvedCount),
        ("filetransfer_bridge_recovery_execution_startup_payload_isolation_verdict={0}" -f $bridgeRecoveryExecutionStartupPayloadIsolationProof.Verdict),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_raw_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.RawCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_distinct_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.DistinctCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_recovered_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.RecoveredCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_missing_unresolved_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.UnresolvedCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_verdict={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.Verdict),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_raw_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredRawCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_distinct_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredDistinctCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_recovered_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredRecoveredCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_unresolved_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredUnresolvedCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_raw_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationRawCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_distinct_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationDistinctCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_recovered_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationRecoveredCount),
        ("filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_unresolved_count={0}" -f $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationUnresolvedCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_raw_count={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.RawCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_distinct_count={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.DistinctCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_recovered_count={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.RecoveredCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_unresolved_count={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.UnresolvedCount),
        ("filetransfer_bridge_recovery_execution_submission_barrier_drain_verdict={0}" -f $bridgeRecoveryExecutionSubmissionBarrierDrainProof.Verdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessVerdict),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedRawCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedDistinctCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedRecoveredCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedUnresolvedCount),
        ("runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_preservation_verdict={0}" -f $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldPreservationVerdict),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_raw_count={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.RawCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_distinct_count={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.DistinctCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_recovered_count={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.RecoveredCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.UnresolvedCount),
        ("runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_verdict={0}" -f $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.Verdict),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_raw_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.RawCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_distinct_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.DistinctCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_recovered_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.RecoveredCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_unresolved_count={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.UnresolvedCount),
        ("runtime_unlock_initial_offer_retry_lifecycle_handoff_verdict={0}" -f $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.Verdict),
        ("runtime_unlock_initial_activation_answer_peer_delivery_missing_raw_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.RawCount),
        ("runtime_unlock_initial_activation_answer_peer_delivery_missing_distinct_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.DistinctCount),
        ("runtime_unlock_initial_activation_answer_peer_delivery_missing_recovered_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.RecoveredCount),
        ("runtime_unlock_initial_activation_answer_peer_delivery_missing_unresolved_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.UnresolvedCount),
        ("runtime_unlock_initial_activation_answer_peer_delivery_verdict={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.Verdict),
        ("runtime_unlock_initial_activation_answer_ack_return_delivery_missing_raw_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnRawCount),
        ("runtime_unlock_initial_activation_answer_ack_return_delivery_missing_distinct_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnDistinctCount),
        ("runtime_unlock_initial_activation_answer_ack_return_delivery_missing_recovered_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnRecoveredCount),
        ("runtime_unlock_initial_activation_answer_ack_return_delivery_missing_unresolved_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnUnresolvedCount),
        ("runtime_unlock_dial_failed_rejected_answer_not_retained_raw_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerRawCount),
        ("runtime_unlock_dial_failed_rejected_answer_not_retained_distinct_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerDistinctCount),
        ("runtime_unlock_dial_failed_rejected_answer_not_retained_recovered_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerRecoveredCount),
        ("runtime_unlock_dial_failed_rejected_answer_not_retained_unresolved_count={0}" -f $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerUnresolvedCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_raw_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.RawCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_distinct_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.DistinctCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_recovered_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.RecoveredCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_unresolved_count={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.UnresolvedCount),
        ("runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_verdict={0}" -f $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.Verdict),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_raw_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.RawCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_distinct_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.DistinctCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_recovered_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.RecoveredCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_unresolved_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.UnresolvedCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_verdict={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.Verdict),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_raw_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.RawCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_distinct_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.DistinctCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_recovered_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.RecoveredCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_unresolved_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.UnresolvedCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_verdict={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.Verdict),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_raw_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.RawCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_distinct_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.DistinctCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_recovered_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.RecoveredCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_unresolved_count={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.UnresolvedCount),
        ("runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_verdict={0}" -f $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.Verdict),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_requested_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.RequestedCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_decided_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.DecidedCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_discarded_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.DiscardedCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_raw_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.RawCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_distinct_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.DistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_recovered_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.RecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_unresolved_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.UnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_verdict={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.Verdict),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_raw_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementRawCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_distinct_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementDistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_recovered_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementRecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_unresolved_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementUnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_raw_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingRawCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_distinct_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingDistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_recovered_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingRecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_unresolved_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingUnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_raw_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldRawCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_distinct_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldDistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_recovered_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldRecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_unresolved_count={0}" -f $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldUnresolvedCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_raw_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.RawCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_distinct_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.DistinctCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_recovered_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.RecoveredCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_rejected_unresolved_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.UnresolvedCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_raw_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationRawCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_distinct_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationDistinctCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_recovered_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationRecoveredCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_unresolved_count={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationUnresolvedCount),
        ("runtime_unlock_retry_dispatch_zero_offer_successor_verdict={0}" -f $runtimeUnlockRetryZeroOfferSuccessorProof.Verdict),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_missing_raw_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RawCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_missing_distinct_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.DistinctCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_missing_recovered_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RecoveredCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_missing_unresolved_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.UnresolvedCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_raw_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredRawCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_distinct_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredDistinctCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_recovered_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredRecoveredCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_unresolved_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredUnresolvedCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_raw_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingRawCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_distinct_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingDistinctCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_recovered_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingRecoveredCount),
        ("runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_unresolved_count={0}" -f $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingUnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_raw_count={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.RawCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_distinct_count={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.DistinctCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_recovered_count={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.RecoveredCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_unresolved_count={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.UnresolvedCount),
        ("runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_verdict={0}" -f $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.Verdict),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_count={0}" -f $runtimeUnlockRetryRegularV4RecoverySettlementMissingCount),
        ("runtime_unlock_retry_blocked_by_regular_v4_receive_recovery_count={0}" -f $runtimeUnlockRetryBlockedByRegularV4RecoveryCount),
        ("runtime_unlock_retry_held_by_regular_v4_receive_recovery_count={0}" -f $runtimeUnlockRetryHeldByRegularV4RecoveryCount),
        ("runtime_unlock_retry_blocked_by_regular_v4_recovery_count={0}" -f $runtimeUnlockRetryBlockedByRegularV4RecoveryCount),
        ("runtime_unlock_retry_held_by_regular_v4_recovery_count={0}" -f $runtimeUnlockRetryHeldByRegularV4RecoveryCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_raw_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.RawCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_distinct_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.DistinctCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_recovered_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.RecoveredCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_missing_unresolved_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.UnresolvedCount),
        ("runtime_unlock_retry_regular_v4_recovery_settlement_verdict={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.Verdict),
        ("runtime_unlock_retry_regular_v4_settlement_handoff_bypassed_count={0}" -f $runtimeUnlockRegularV4RecoverySettlementProof.HandoffBypassCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_raw_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.RawCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_distinct_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.DistinctCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_recovered_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.RecoveredCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_unresolved_count={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.UnresolvedCount),
        ("runtime_unlock_post_tuna_fallback_transport_proof_handoff_verdict={0}" -f $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.Verdict),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_raw_count={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.RawCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_distinct_count={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.DistinctCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_recovered_count={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.RecoveredCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppressed_unresolved_count={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.UnresolvedCount),
        ("runtime_unlock_precommit_fallback_recovery_handoff_suppression_verdict={0}" -f $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.Verdict),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_raw_count={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.RawCount),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_distinct_count={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.DistinctCount),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_recovered_count={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.RecoveredCount),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_unresolved_count={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.UnresolvedCount),
        ("runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_verdict={0}" -f $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.Verdict),
        ("fallback_tail_yield_authority_rejected_count={0}" -f $fallbackTailYieldAuthorityRejectedCount),
        ("fallback_tail_coordinator_aggregate_verdict={0}" -f $fallbackTailCoordinatorAggregateProof.Verdict),
        ("fallback_tail_coordinator_yield_issued_count={0}" -f $fallbackTailCoordinatorAggregateProof.YieldIssuedCount),
        ("fallback_tail_coordinator_local_proof_receipt_decided_count={0}" -f $fallbackTailCoordinatorAggregateProof.LocalProofReceiptDecidedCount),
        ("fallback_tail_coordinator_peer_proof_receipt_decided_count={0}" -f $fallbackTailCoordinatorAggregateProof.PeerProofReceiptDecidedCount),
        ("fallback_tail_coordinator_pairing_committed_count={0}" -f $fallbackTailCoordinatorAggregateProof.PairingCommittedCount),
        ("fallback_tail_coordinator_valid_pairing_count={0}" -f $fallbackTailCoordinatorAggregateProof.ValidPairingCount),
        ("fallback_tail_coordinator_paired_release_reserved_count={0}" -f $fallbackTailCoordinatorAggregateProof.PairedReleaseReservedCount),
        ("fallback_tail_coordinator_valid_paired_release_reserved_count={0}" -f $fallbackTailCoordinatorAggregateProof.ValidPairedReleaseReservedCount),
        ("fallback_tail_coordinator_paired_release_consumed_count={0}" -f $fallbackTailCoordinatorAggregateProof.PairedReleaseConsumedCount),
        ("fallback_tail_coordinator_valid_paired_release_consumed_count={0}" -f $fallbackTailCoordinatorAggregateProof.ValidPairedReleaseConsumedCount),
        ("fallback_tail_coordinator_paired_release_carry_forward_count={0}" -f $fallbackTailCoordinatorAggregateProof.PairedReleaseCarryForwardCount),
        ("fallback_tail_coordinator_valid_paired_release_carry_forward_count={0}" -f $fallbackTailCoordinatorAggregateProof.ValidPairedReleaseCarryForwardCount),
        ("fallback_tail_coordinator_invalid_raw_count={0}" -f $fallbackTailCoordinatorAggregateProof.InvalidRawCount),
        ("fallback_tail_coordinator_invalid_distinct_count={0}" -f $fallbackTailCoordinatorAggregateProof.InvalidDistinctCount),
        ("fallback_tail_coordinator_invalid_recovered_count={0}" -f $fallbackTailCoordinatorAggregateProof.InvalidRecoveredCount),
        ("fallback_tail_coordinator_invalid_unresolved_count={0}" -f $fallbackTailCoordinatorAggregateProof.InvalidUnresolvedCount),
        ("fallback_tail_coordinator_reauthorization_staged_count={0}" -f $fallbackTailCoordinatorAggregateProof.ReauthorizationStagedCount),
        ("fallback_tail_coordinator_reauthorization_rebound_count={0}" -f $fallbackTailCoordinatorAggregateProof.ReauthorizationReboundCount),
        ("fallback_tail_coordinator_reauthorization_successor_issued_count={0}" -f $fallbackTailCoordinatorAggregateProof.ReauthorizationSuccessorIssuedCount),
        ("fallback_tail_coordinator_reauthorization_invalidated_count={0}" -f $fallbackTailCoordinatorAggregateProof.ReauthorizationInvalidatedCount),
        ("runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_raw_count={0}" -f $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.RawCount),
        ("runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_distinct_count={0}" -f $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.DistinctCount),
        ("runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_recovered_count={0}" -f $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.RecoveredCount),
        ("runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_unresolved_count={0}" -f $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.UnresolvedCount),
        ("fallback_tail_owner_generation_mismatch_count={0}" -f $fallbackTailOwnerGenerationMismatchCount),
        ("fallback_tail_bridge_recovery_generation_pairing_verdict={0}" -f $fallbackTailBridgeRecoveryPairingProof.Verdict),
        ("fallback_tail_bridge_recovery_generation_mismatch_raw_count={0}" -f $fallbackTailBridgeRecoveryPairingProof.RawCount),
        ("fallback_tail_bridge_recovery_generation_mismatch_distinct_count={0}" -f $fallbackTailBridgeRecoveryPairingProof.DistinctCount),
        ("fallback_tail_bridge_recovery_generation_mismatch_recovered_count={0}" -f $fallbackTailBridgeRecoveryPairingProof.RecoveredCount),
        ("fallback_tail_bridge_recovery_generation_mismatch_unresolved_count={0}" -f $fallbackTailBridgeRecoveryPairingProof.UnresolvedCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict={0}" -f $fallbackTailTransportEpochReauthorizationProof.Verdict),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count={0}" -f $fallbackTailTransportEpochReauthorizationProof.RawCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count={0}" -f $fallbackTailTransportEpochReauthorizationProof.DistinctCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count={0}" -f $fallbackTailTransportEpochReauthorizationProof.RecoveredCount),
        ("post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count={0}" -f $fallbackTailTransportEpochReauthorizationProof.UnresolvedCount),
        'late_frame_classification=none'
    )
    Set-Content -LiteralPath (Join-Path $ArtifactDir 'phase0-invariant-ledger.txt') -Value $invariantLines -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-invariant-summary.txt') -Value $invariantLines -Encoding UTF8

    $compactDto = [ordered]@{
        retained_analysis_mode = 'compact'
        verdict = $verdict
        gate_status = $gateStatus
        acceptance_failure_class = $acceptanceFailureClass
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
        recovery_failure_class = $recoveryFailureClass
        failure_variant = $failureVariant
        first_proof_chain_failure_class = $firstProofChainFailureClass
        first_proof_chain_failure_event = $firstProofChainFailureEvent
        first_proof_chain_failure_reason = $firstProofChainFailureReason
        first_proof_chain_failure_sequence = $firstProofChainFailureSequence
        first_proof_chain_failure_source_path = $firstProofChainFailureSourcePath
        first_proof_chain_failure_line_number = $firstProofChainFailureLineNumber
        first_proof_chain_failure_writer_sequence = $firstProofChainFailureWriterSequence
        semantic_failure_domain = $semanticFailureDomain
        filetransfer_gui_live_fallback_fault_window_missed_raw_count = $fileTransferGuiLiveFallbackFaultWindowProof.RawCount
        filetransfer_gui_live_fallback_fault_window_missed_distinct_count = $fileTransferGuiLiveFallbackFaultWindowProof.DistinctCount
        filetransfer_gui_live_fallback_fault_window_missed_recovered_count = 0
        filetransfer_gui_live_fallback_fault_window_missed_unresolved_count = $fileTransferGuiLiveFallbackFaultWindowProof.UnresolvedCount
        filetransfer_gui_live_fallback_fault_window_missed_verdict = $fileTransferGuiLiveFallbackFaultWindowProof.Verdict
        filetransfer_fallback_v6_proof_bootstrap_missing_raw_count = $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingRawCount
        filetransfer_fallback_v6_proof_bootstrap_missing_distinct_count = $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingDistinctCount
        filetransfer_fallback_v6_proof_bootstrap_missing_recovered_count = $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingRecoveredCount
        filetransfer_fallback_v6_proof_bootstrap_missing_unresolved_count = $fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingUnresolvedCount
        filetransfer_fallback_v6_proof_bootstrap_missing_verdict = if ($fileTransferRouteTransitionProof.FallbackV6ProofBootstrapMissingUnresolvedCount -gt 0) { 'fail' } else { 'pass' }
        filetransfer_route_transition_incomplete_raw_count = $fileTransferRouteTransitionProof.RawCount
        filetransfer_route_transition_incomplete_distinct_count = $fileTransferRouteTransitionProof.DistinctCount
        filetransfer_route_transition_incomplete_recovered_count = $fileTransferRouteTransitionProof.RecoveredCount
        filetransfer_route_transition_incomplete_unresolved_count = $fileTransferRouteTransitionProof.UnresolvedCount
        filetransfer_route_transition_verdict = $fileTransferRouteTransitionProof.Verdict
        filetransfer_live_route_epoch_projection_missing_raw_count = $fileTransferRouteTransitionLiveEpochProjectionProof.RawCount
        filetransfer_live_route_epoch_projection_missing_distinct_count = $fileTransferRouteTransitionLiveEpochProjectionProof.DistinctCount
        filetransfer_live_route_epoch_projection_missing_recovered_count = $fileTransferRouteTransitionLiveEpochProjectionProof.RecoveredCount
        filetransfer_live_route_epoch_projection_missing_unresolved_count = $fileTransferRouteTransitionLiveEpochProjectionProof.UnresolvedCount
        filetransfer_route_transition_draining_transport_grace_terminalization_raw_count = $fileTransferRouteTransitionDrainingTransportGraceProof.RawCount
        filetransfer_route_transition_draining_transport_grace_terminalization_distinct_count = $fileTransferRouteTransitionDrainingTransportGraceProof.DistinctCount
        filetransfer_route_transition_draining_transport_grace_terminalization_recovered_count = $fileTransferRouteTransitionDrainingTransportGraceProof.RecoveredCount
        filetransfer_route_transition_draining_transport_grace_terminalization_unresolved_count = $fileTransferRouteTransitionDrainingTransportGraceProof.UnresolvedCount
        filetransfer_route_transition_draining_transport_grace_terminalization_verdict = $fileTransferRouteTransitionDrainingTransportGraceProof.Verdict
        runtime_unlock_regular_v4_route_commit_send_drain_missing_raw_count = $runtimeUnlockRegularV4RouteCommitSendDrainProof.RawCount
        runtime_unlock_regular_v4_route_commit_send_drain_missing_distinct_count = $runtimeUnlockRegularV4RouteCommitSendDrainProof.DistinctCount
        runtime_unlock_regular_v4_route_commit_send_drain_missing_recovered_count = $runtimeUnlockRegularV4RouteCommitSendDrainProof.RecoveredCount
        runtime_unlock_regular_v4_route_commit_send_drain_missing_unresolved_count = $runtimeUnlockRegularV4RouteCommitSendDrainProof.UnresolvedCount
        runtime_unlock_regular_v4_route_commit_send_drain_verdict = $runtimeUnlockRegularV4RouteCommitSendDrainProof.Verdict
        file_tuna_v4_data_path_mismatch_count = $fileTunaV4DataPathMismatchProof.EffectiveCount
        runtime_unlock_retry_dispatch_authority_granted_raw_count = $runtimeUnlockRetryDispatchInstallProof.AuthorityGrantedRawCount
        runtime_unlock_retry_dispatch_decision_staged_count = $runtimeUnlockRetryDispatchInstallProof.DecisionStagedCount
        runtime_unlock_retry_dispatch_install_receipt_issued_count = $runtimeUnlockRetryDispatchInstallProof.ReceiptIssuedCount
        runtime_unlock_retry_dispatch_install_committed_count = $runtimeUnlockRetryDispatchInstallProof.CommittedCount
        runtime_unlock_retry_dispatch_install_rejected_count = $runtimeUnlockRetryDispatchInstallProof.RejectedCount
        runtime_unlock_retry_dispatch_authority_granted_count = $runtimeUnlockRetryDispatchInstallProof.EffectiveGrantEvents.Count
        runtime_unlock_retry_dispatch_observed_count = $runtimeUnlockRetryDispatchObservedCount
        runtime_unlock_retry_dispatch_install_verdict = $runtimeUnlockRetryDispatchInstallProof.Verdict
        runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_raw_count = $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteRawCount
        runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_distinct_count = $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteDistinctCount
        runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_recovered_count = $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteRecoveredCount
        runtime_unlock_retry_dispatch_install_grant_evidence_incomplete_unresolved_count = $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceIncompleteUnresolvedCount
        runtime_unlock_retry_dispatch_install_grant_evidence_verdict = $runtimeUnlockRetryDispatchInstallProof.GrantEvidenceVerdict
        runtime_unlock_retry_lifecycle_execution_observation_count = $runtimeUnlockRetryLifecycleProof.ExecutionObservationCount
        runtime_unlock_retry_lifecycle_execution_decision_count = $runtimeUnlockRetryLifecycleProof.ExecutionDecisionCount
        runtime_unlock_retry_lifecycle_execution_unresolved_count = @($runtimeUnlockRetryLifecycleProof.UnresolvedExecutionObservationEvents).Count
        runtime_unlock_retry_lifecycle_successor_evaluation_count = $runtimeUnlockRetryLifecycleProof.SuccessorEvaluationCount
        runtime_unlock_retry_lifecycle_successor_decision_count = $runtimeUnlockRetryLifecycleProof.SuccessorDecisionCount
        runtime_unlock_retry_lifecycle_successor_unresolved_count = @($runtimeUnlockRetryLifecycleProof.UnresolvedSuccessorEvaluationEvents).Count
        runtime_unlock_retry_lifecycle_deadline_evaluation_count = $runtimeUnlockRetryLifecycleProof.DeadlineEvaluationCount
        runtime_unlock_retry_lifecycle_deadline_decision_count = $runtimeUnlockRetryLifecycleProof.DeadlineDecisionCount
        runtime_unlock_retry_lifecycle_deadline_unresolved_count = @($runtimeUnlockRetryLifecycleProof.UnresolvedDeadlineEvaluationEvents).Count
        runtime_unlock_retry_lifecycle_verdict = $runtimeUnlockRetryLifecycleProof.Verdict
        runtime_unlock_retry_dispatch_proof_verdict = $runtimeUnlockRetryDispatchProofVerdict
        runtime_unlock_retry_dispatch_post_grant_hold_count = $runtimeUnlockRetryDispatchPostGrantHoldProof.RawCount
        runtime_unlock_retry_dispatch_post_grant_hold_raw_count = $runtimeUnlockRetryDispatchPostGrantHoldProof.RawCount
        runtime_unlock_retry_dispatch_post_grant_hold_distinct_count = $runtimeUnlockRetryDispatchPostGrantHoldProof.DistinctCount
        runtime_unlock_retry_dispatch_post_grant_hold_recovered_count = $runtimeUnlockRetryDispatchPostGrantHoldProof.RecoveredCount
        runtime_unlock_retry_dispatch_post_grant_hold_unresolved_count = $runtimeUnlockRetryDispatchPostGrantHoldProof.UnresolvedCount
        runtime_unlock_retry_dispatch_post_grant_hold_verdict = $runtimeUnlockRetryDispatchPostGrantHoldProof.Verdict
        runtime_unlock_precommit_probe_start_handoff_missing_raw_count = $runtimeUnlockPreCommitProbeStartHandoffProof.RawCount
        runtime_unlock_precommit_probe_start_handoff_missing_distinct_count = $runtimeUnlockPreCommitProbeStartHandoffProof.DistinctCount
        runtime_unlock_precommit_probe_start_handoff_missing_recovered_count = $runtimeUnlockPreCommitProbeStartHandoffProof.RecoveredCount
        runtime_unlock_precommit_probe_start_handoff_missing_unresolved_count = $runtimeUnlockPreCommitProbeStartHandoffProof.UnresolvedCount
        runtime_unlock_responder_acked_probe_route_transition_handoff_missing_raw_count = $runtimeUnlockResponderPeerProbeHandoffProof.RawCount
        runtime_unlock_responder_acked_probe_route_transition_handoff_missing_distinct_count = $runtimeUnlockResponderPeerProbeHandoffProof.DistinctCount
        runtime_unlock_responder_acked_probe_route_transition_handoff_missing_recovered_count = $runtimeUnlockResponderPeerProbeHandoffProof.RecoveredCount
        runtime_unlock_responder_acked_probe_route_transition_handoff_missing_unresolved_count = $runtimeUnlockResponderPeerProbeHandoffProof.UnresolvedCount
        runtime_unlock_responder_acked_probe_route_transition_handoff_verdict = $runtimeUnlockResponderPeerProbeHandoffProof.Verdict
        runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_raw_count = $runtimeUnlockResponderPeerProbeHandoffProof.InitialActivationPeerAcceptanceScopeRejectedRawCount
        runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_distinct_count = $runtimeUnlockResponderPeerProbeHandoffProof.InitialActivationPeerAcceptanceScopeRejectedDistinctCount
        runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_recovered_count = $runtimeUnlockResponderPeerProbeHandoffProof.InitialActivationPeerAcceptanceScopeRejectedRecoveredCount
        runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_unresolved_count = $runtimeUnlockResponderPeerProbeHandoffProof.InitialActivationPeerAcceptanceScopeRejectedUnresolvedCount
        runtime_unlock_responder_acked_probe_initial_activation_peer_acceptance_scope_rejected_verdict = $runtimeUnlockResponderPeerProbeHandoffProof.InitialActivationPeerAcceptanceScopeRejectedVerdict
        runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_raw_count = $runtimeUnlockResponderPeerProbeHandoffProof.AckBeforeProbeAdoptionRecheckMissingRawCount
        runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_distinct_count = $runtimeUnlockResponderPeerProbeHandoffProof.AckBeforeProbeAdoptionRecheckMissingDistinctCount
        runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_recovered_count = $runtimeUnlockResponderPeerProbeHandoffProof.AckBeforeProbeAdoptionRecheckMissingRecoveredCount
        runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_unresolved_count = $runtimeUnlockResponderPeerProbeHandoffProof.AckBeforeProbeAdoptionRecheckMissingUnresolvedCount
        runtime_unlock_responder_acked_probe_adoption_recheck_missing_after_answer_ack_before_probe_verdict = $runtimeUnlockResponderPeerProbeHandoffProof.AckBeforeProbeAdoptionRecheckMissingVerdict
        runtime_unlock_responder_acked_probe_binding_missing_raw_count = $runtimeUnlockResponderPeerProbeHandoffProof.BindingMissingRawCount
        runtime_unlock_responder_acked_probe_binding_missing_distinct_count = $runtimeUnlockResponderPeerProbeHandoffProof.BindingMissingDistinctCount
        runtime_unlock_responder_acked_probe_binding_missing_recovered_count = $runtimeUnlockResponderPeerProbeHandoffProof.BindingMissingRecoveredCount
        runtime_unlock_responder_acked_probe_binding_missing_unresolved_count = $runtimeUnlockResponderPeerProbeHandoffProof.BindingMissingUnresolvedCount
        runtime_unlock_responder_acked_probe_binding_missing_verdict = $runtimeUnlockResponderPeerProbeHandoffProof.BindingMissingVerdict
        runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_raw_count = $runtimeUnlockResponderPeerProbeHandoffProof.AfterBoundHandoffMissingRawCount
        runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_distinct_count = $runtimeUnlockResponderPeerProbeHandoffProof.AfterBoundHandoffMissingDistinctCount
        runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_recovered_count = $runtimeUnlockResponderPeerProbeHandoffProof.AfterBoundHandoffMissingRecoveredCount
        runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_unresolved_count = $runtimeUnlockResponderPeerProbeHandoffProof.AfterBoundHandoffMissingUnresolvedCount
        runtime_unlock_responder_acked_probe_route_transition_handoff_missing_after_bound_probe_verdict = $runtimeUnlockResponderPeerProbeHandoffProof.AfterBoundHandoffMissingVerdict
        runtime_unlock_precommit_probe_start_handoff_verdict = $runtimeUnlockPreCommitProbeStartHandoffProof.Verdict
        regular_v4_frontier_repair_missing_range_not_preserved_raw_count = $regularV4FrontierRepairMissingRangePreservationProof.RawCount
        regular_v4_frontier_repair_missing_range_not_preserved_distinct_count = $regularV4FrontierRepairMissingRangePreservationProof.DistinctCount
        regular_v4_frontier_repair_missing_range_not_preserved_recovered_count = $regularV4FrontierRepairMissingRangePreservationProof.RecoveredCount
        regular_v4_frontier_repair_missing_range_not_preserved_unresolved_count = $regularV4FrontierRepairMissingRangePreservationProof.UnresolvedCount
        regular_v4_frontier_repair_missing_range_preservation_verdict = $regularV4FrontierRepairMissingRangePreservationProof.Verdict
        regular_v4_frontier_tail_repair_state_publication_missing_raw_count = $regularV4FrontierTailRepairStatePublicationProof.RawCount
        regular_v4_frontier_tail_repair_state_publication_missing_distinct_count = $regularV4FrontierTailRepairStatePublicationProof.DistinctCount
        regular_v4_frontier_tail_repair_state_publication_missing_recovered_count = $regularV4FrontierTailRepairStatePublicationProof.RecoveredCount
        regular_v4_frontier_tail_repair_state_publication_missing_unresolved_count = $regularV4FrontierTailRepairStatePublicationProof.UnresolvedCount
        regular_v4_frontier_tail_repair_state_publication_verdict = $regularV4FrontierTailRepairStatePublicationProof.Verdict
        regular_v4_frontier_repair_projection_reauthorization_missing_raw_count = $regularV4FrontierRepairProjectionReauthorizationProof.RawCount
        regular_v4_frontier_repair_projection_reauthorization_missing_distinct_count = $regularV4FrontierRepairProjectionReauthorizationProof.DistinctCount
        regular_v4_frontier_repair_projection_reauthorization_missing_recovered_count = $regularV4FrontierRepairProjectionReauthorizationProof.RecoveredCount
        regular_v4_frontier_repair_projection_reauthorization_missing_unresolved_count = $regularV4FrontierRepairProjectionReauthorizationProof.UnresolvedCount
        regular_v4_frontier_repair_projection_reauthorization_verdict = $regularV4FrontierRepairProjectionReauthorizationProof.Verdict
        regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_raw_count = $regularV4PeerSilenceReplayMissingDispatchRangeProof.RawCount
        regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_distinct_count = $regularV4PeerSilenceReplayMissingDispatchRangeProof.DistinctCount
        regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_recovered_count = $regularV4PeerSilenceReplayMissingDispatchRangeProof.RecoveredCount
        regular_v4_peer_silence_replay_missing_dispatch_range_not_preserved_unresolved_count = $regularV4PeerSilenceReplayMissingDispatchRangeProof.UnresolvedCount
        regular_v4_peer_silence_replay_missing_dispatch_range_preservation_verdict = $regularV4PeerSilenceReplayMissingDispatchRangeProof.Verdict
        regular_v4_peer_silence_replay_installed_authority_not_carried_to_dispatch_raw_count = $regularV4PeerSilenceReplayInstalledAuthorityDispatchProof.RawCount
        regular_v4_peer_silence_replay_installed_authority_not_carried_to_dispatch_distinct_count = $regularV4PeerSilenceReplayInstalledAuthorityDispatchProof.DistinctCount
        regular_v4_peer_silence_replay_installed_authority_not_carried_to_dispatch_recovered_count = $regularV4PeerSilenceReplayInstalledAuthorityDispatchProof.RecoveredCount
        regular_v4_peer_silence_replay_installed_authority_not_carried_to_dispatch_unresolved_count = $regularV4PeerSilenceReplayInstalledAuthorityDispatchProof.UnresolvedCount
        regular_v4_peer_silence_replay_installed_authority_dispatch_verdict = $regularV4PeerSilenceReplayInstalledAuthorityDispatchProof.Verdict
        regular_v4_frontier_repair_stalled_raw_count = $regularV4FrontierRepairLifecycleProof.RawCount
        regular_v4_frontier_repair_stalled_distinct_count = $regularV4FrontierRepairLifecycleProof.DistinctCount
        regular_v4_frontier_repair_stalled_recovered_count = $regularV4FrontierRepairLifecycleProof.RecoveredCount
        regular_v4_frontier_repair_stalled_unresolved_count = $regularV4FrontierRepairLifecycleProof.UnresolvedCount
        regular_v4_frontier_repair_stalled_verdict = $regularV4FrontierRepairLifecycleProof.Verdict
        regular_v4_frontier_repair_settlement_disposition_unverifiable_raw_count = $regularV4FrontierRepairSettlementDispositionProof.RawCount
        regular_v4_frontier_repair_settlement_disposition_unverifiable_distinct_count = $regularV4FrontierRepairSettlementDispositionProof.DistinctCount
        regular_v4_frontier_repair_settlement_disposition_unverifiable_recovered_count = $regularV4FrontierRepairSettlementDispositionProof.RecoveredCount
        regular_v4_frontier_repair_settlement_disposition_unverifiable_unresolved_count = $regularV4FrontierRepairSettlementDispositionProof.UnresolvedCount
        regular_v4_frontier_repair_settlement_disposition_verdict = $regularV4FrontierRepairSettlementDispositionProof.Verdict
        regular_v4_frontier_repair_pressure_authority_lifecycle_split_raw_count = $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.RawCount
        regular_v4_frontier_repair_pressure_authority_lifecycle_split_distinct_count = $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.DistinctCount
        regular_v4_frontier_repair_pressure_authority_lifecycle_split_recovered_count = $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.RecoveredCount
        regular_v4_frontier_repair_pressure_authority_lifecycle_split_unresolved_count = $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.UnresolvedCount
        regular_v4_frontier_repair_pressure_authority_lifecycle_split_verdict = $regularV4FrontierRepairPressureAuthorityLifecycleSplitProof.Verdict
        regular_v4_frontier_repair_pressure_recovery_handoff_missing_raw_count = $regularV4FrontierRepairPressureRecoveryHandoffProof.RawCount
        regular_v4_frontier_repair_pressure_recovery_handoff_missing_distinct_count = $regularV4FrontierRepairPressureRecoveryHandoffProof.DistinctCount
        regular_v4_frontier_repair_pressure_recovery_handoff_missing_recovered_count = $regularV4FrontierRepairPressureRecoveryHandoffProof.RecoveredCount
        regular_v4_frontier_repair_pressure_recovery_handoff_missing_unresolved_count = $regularV4FrontierRepairPressureRecoveryHandoffProof.UnresolvedCount
        regular_v4_frontier_repair_pressure_recovery_handoff_verdict = $regularV4FrontierRepairPressureRecoveryHandoffProof.Verdict
        regular_v4_frontier_repair_successor_pressure_install_missing_raw_count = $regularV4FrontierRepairSuccessorPressureInstallProof.RawCount
        regular_v4_frontier_repair_successor_pressure_install_missing_distinct_count = $regularV4FrontierRepairSuccessorPressureInstallProof.DistinctCount
        regular_v4_frontier_repair_successor_pressure_install_missing_recovered_count = $regularV4FrontierRepairSuccessorPressureInstallProof.RecoveredCount
        regular_v4_frontier_repair_successor_pressure_install_missing_unresolved_count = $regularV4FrontierRepairSuccessorPressureInstallProof.UnresolvedCount
        regular_v4_frontier_repair_successor_pressure_install_verdict = $regularV4FrontierRepairSuccessorPressureInstallProof.Verdict
        regular_v4_frontier_repair_initial_pressure_install_missing_raw_count = $regularV4FrontierRepairInitialPressureInstallProof.RawCount
        regular_v4_frontier_repair_initial_pressure_install_missing_distinct_count = $regularV4FrontierRepairInitialPressureInstallProof.DistinctCount
        regular_v4_frontier_repair_initial_pressure_install_missing_recovered_count = $regularV4FrontierRepairInitialPressureInstallProof.RecoveredCount
        regular_v4_frontier_repair_initial_pressure_install_missing_unresolved_count = $regularV4FrontierRepairInitialPressureInstallProof.UnresolvedCount
        regular_v4_frontier_repair_initial_pressure_install_verdict = $regularV4FrontierRepairInitialPressureInstallProof.Verdict
        runtime_unlock_regular_v4_recovery_lease_closure_rejected_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureRejectedCount
        filetransfer_receive_recovery_operation_raw_count = $fileTransferReceiveRecoveryOperationProof.RawCount
        filetransfer_receive_recovery_operation_distinct_count = $fileTransferReceiveRecoveryOperationProof.DistinctCount
        filetransfer_receive_recovery_operation_recovered_count = $fileTransferReceiveRecoveryOperationProof.RecoveredCount
        filetransfer_receive_recovery_operation_unresolved_count = $fileTransferReceiveRecoveryOperationProof.UnresolvedCount
        filetransfer_receive_recovery_operation_verdict = $fileTransferReceiveRecoveryOperationProof.Verdict
        filetransfer_receive_recovery_operation_started_count = $fileTransferReceiveRecoveryOperationProof.StartedCount
        filetransfer_receive_recovery_operation_execution_receipt_applied_count = $fileTransferReceiveRecoveryOperationProof.ExecutionReceiptAppliedCount
        filetransfer_receive_recovery_operation_proof_observed_count = $fileTransferReceiveRecoveryOperationProof.ProofObservedCount
        filetransfer_receive_recovery_operation_closure_retained_count = $fileTransferReceiveRecoveryOperationProof.ClosureRetainedCount
        filetransfer_receive_recovery_operation_closure_adopted_count = $fileTransferReceiveRecoveryOperationProof.ClosureAdoptedCount
        filetransfer_receive_recovery_operation_closure_reserved_count = $fileTransferReceiveRecoveryOperationProof.ClosureReservedCount
        filetransfer_receive_recovery_operation_closure_consumed_count = $fileTransferReceiveRecoveryOperationProof.ClosureConsumedCount
        filetransfer_receive_recovery_completed_unproven_successor_missing_raw_count = $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.RawCount
        filetransfer_receive_recovery_completed_unproven_successor_missing_distinct_count = $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.DistinctCount
        filetransfer_receive_recovery_completed_unproven_successor_missing_recovered_count = $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.RecoveredCount
        filetransfer_receive_recovery_completed_unproven_successor_missing_unresolved_count = $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.UnresolvedCount
        filetransfer_receive_recovery_completed_unproven_successor_verdict = $fileTransferReceiveRecoveryCompletedUnprovenSuccessorProof.Verdict
        filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_raw_count = $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.RawCount
        filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_distinct_count = $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.DistinctCount
        filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_recovered_count = $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.RecoveredCount
        filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved_unresolved_count = $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.UnresolvedCount
        filetransfer_receive_recovery_completed_unproven_payload_hold_verdict = $fileTransferReceiveRecoveryCompletedUnprovenPayloadHoldProof.Verdict
        filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_raw_count = $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.RawCount
        filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_distinct_count = $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.DistinctCount
        filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_recovered_count = $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.RecoveredCount
        filetransfer_receive_recovery_completed_retained_proof_state_publication_missing_unresolved_count = $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.UnresolvedCount
        filetransfer_receive_recovery_completed_retained_proof_state_publication_verdict = $fileTransferReceiveRecoveryCompletedRetainedProofStatePublicationProof.Verdict
        filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_raw_count = $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.RawCount
        filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_distinct_count = $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.DistinctCount
        filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_recovered_count = $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.RecoveredCount
        filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed_unresolved_count = $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.UnresolvedCount
        filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppression_verdict = $fileTransferReceiveRecoveryCompletedRetainedProofStateRecheckHeartbeatSuppressionProof.Verdict
        filetransfer_receive_recovery_completed_proven_successor_missing_raw_count = $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.RawCount
        filetransfer_receive_recovery_completed_proven_successor_missing_distinct_count = $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.DistinctCount
        filetransfer_receive_recovery_completed_proven_successor_missing_recovered_count = $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.RecoveredCount
        filetransfer_receive_recovery_completed_proven_successor_missing_unresolved_count = $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.UnresolvedCount
        filetransfer_receive_recovery_completed_proven_successor_verdict = $fileTransferReceiveRecoveryCompletedProvenSuccessorProof.Verdict
        filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_raw_count = $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.RawCount
        filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_distinct_count = $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.DistinctCount
        filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_recovered_count = $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.RecoveredCount
        filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing_unresolved_count = $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.UnresolvedCount
        filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_verdict = $fileTransferReceiveRecoveryPostTunaRuntimeSuccessorLivenessHandoffProof.Verdict
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_raw_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.RawCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_distinct_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DistinctCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_recovered_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.RecoveredCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing_unresolved_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.UnresolvedCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_verdict = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.Verdict
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_raw_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredRawCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_distinct_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredDistinctCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_recovered_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredRecoveredCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_held_delivery_retired_unresolved_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.HeldDeliveryRetiredUnresolvedCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_raw_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingRawCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_distinct_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingDistinctCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_recovered_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingRecoveredCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_child_binding_missing_unresolved_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.ChildBindingMissingUnresolvedCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_raw_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffRawCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_distinct_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffDistinctCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_recovered_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffRecoveredCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_sparse_pump_handoff_unresolved_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.SparsePumpHandoffUnresolvedCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_raw_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldRawCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_distinct_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldDistinctCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_recovered_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldRecoveredCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_answer_timeout_checkpoint_child_unresolved_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.InitialActivationAnswerTimeoutHoldUnresolvedCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_raw_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckRawCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_distinct_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckDistinctCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_recovered_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckRecoveredCount
        filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_delivered_peer_response_recheck_unresolved_count = $fileTransferReceiveRecoveryFallbackCheckpointScopeHandoffProof.DeliveredPeerResponseRecheckUnresolvedCount
        filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_raw_count = $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.RawCount
        filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_distinct_count = $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.DistinctCount
        filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_recovered_count = $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.RecoveredCount
        filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_unresolved_count = $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.UnresolvedCount
        filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing_verdict = $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.Verdict
        filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_raw_count = $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestRawCount
        filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_distinct_count = $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestDistinctCount
        filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_recovered_count = $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestRecoveredCount
        filetransfer_checkpoint_pending_frontier_request_not_rechecked_after_checkpoint_acceptance_unresolved_count = $fileTransferFallbackFrontierCheckpointAcceptanceRecheckProof.CheckpointPendingRequestUnresolvedCount
        paired_fallback_tail_yield_missing_raw_count = $fallbackTailFailedYieldQuiescentSuccessorProof.RawCount
        paired_fallback_tail_yield_missing_distinct_count = $fallbackTailFailedYieldQuiescentSuccessorProof.DistinctCount
        paired_fallback_tail_yield_missing_recovered_count = $fallbackTailFailedYieldQuiescentSuccessorProof.RecoveredCount
        paired_fallback_tail_yield_missing_unresolved_count = $fallbackTailFailedYieldQuiescentSuccessorProof.UnresolvedCount
        paired_fallback_tail_yield_missing_verdict = $fallbackTailFailedYieldQuiescentSuccessorProof.Verdict
        failed_yield_quiescent_successor_missing_raw_count = $fallbackTailFailedYieldQuiescentSuccessorProof.FailedYieldRawCount
        failed_yield_quiescent_successor_missing_distinct_count = $fallbackTailFailedYieldQuiescentSuccessorProof.FailedYieldDistinctCount
        failed_yield_quiescent_successor_missing_recovered_count = $fallbackTailFailedYieldQuiescentSuccessorProof.FailedYieldRecoveredCount
        failed_yield_quiescent_successor_missing_unresolved_count = $fallbackTailFailedYieldQuiescentSuccessorProof.FailedYieldUnresolvedCount
        peer_proof_before_local_yield_not_retained_raw_count = $fallbackTailFailedYieldQuiescentSuccessorProof.PeerFirstRawCount
        peer_proof_before_local_yield_not_retained_distinct_count = $fallbackTailFailedYieldQuiescentSuccessorProof.PeerFirstDistinctCount
        peer_proof_before_local_yield_not_retained_recovered_count = $fallbackTailFailedYieldQuiescentSuccessorProof.PeerFirstRecoveredCount
        peer_proof_before_local_yield_not_retained_unresolved_count = $fallbackTailFailedYieldQuiescentSuccessorProof.PeerFirstUnresolvedCount
        peer_proof_before_local_yield_asymmetric_recovery_scope_not_retained_raw_count = $fallbackTailFailedYieldQuiescentSuccessorProof.PeerFirstAsymmetricRecoveryScopeRawCount
        peer_proof_before_local_yield_asymmetric_recovery_scope_not_retained_distinct_count = $fallbackTailFailedYieldQuiescentSuccessorProof.PeerFirstAsymmetricRecoveryScopeDistinctCount
        peer_proof_before_local_yield_asymmetric_recovery_scope_not_retained_recovered_count = $fallbackTailFailedYieldQuiescentSuccessorProof.PeerFirstAsymmetricRecoveryScopeRecoveredCount
        peer_proof_before_local_yield_asymmetric_recovery_scope_not_retained_unresolved_count = $fallbackTailFailedYieldQuiescentSuccessorProof.PeerFirstAsymmetricRecoveryScopeUnresolvedCount
        runtime_unlock_regular_v4_recovery_lease_not_issued_raw_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.RawCount
        runtime_unlock_regular_v4_recovery_lease_not_issued_distinct_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.DistinctCount
        runtime_unlock_regular_v4_recovery_lease_not_issued_recovered_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.RecoveredCount
        runtime_unlock_regular_v4_recovery_lease_not_issued_unresolved_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.UnresolvedCount
        runtime_unlock_regular_v4_recovery_lease_issuance_verdict = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.Verdict
        runtime_unlock_recovery_lease_closure_not_adopted_raw_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedRawCount
        runtime_unlock_recovery_lease_closure_not_adopted_distinct_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedDistinctCount
        runtime_unlock_recovery_lease_closure_not_adopted_recovered_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedRecoveredCount
        runtime_unlock_recovery_lease_closure_not_adopted_unresolved_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureNotAdoptedUnresolvedCount
        runtime_unlock_recovery_lease_closure_adoption_verdict = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureAdoptionVerdict
        runtime_unlock_regular_v4_recovery_lease_closure_liveness_deferral_rejected_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessRejectedCount
        runtime_unlock_regular_v4_recovery_lease_closure_liveness_deferral_granted_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessGrantedCount
        runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_raw_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessRawCount
        runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_distinct_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessDistinctCount
        runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_recovered_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessRecoveredCount
        runtime_unlock_regular_v4_recovery_lease_closure_liveness_not_preserved_unresolved_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessUnresolvedCount
        runtime_unlock_regular_v4_recovery_lease_closure_liveness_preservation_verdict = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureLivenessVerdict
        filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_raw_count = $bridgeRecoveryExecutionSoftToHardHandoffProof.RawCount
        filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_distinct_count = $bridgeRecoveryExecutionSoftToHardHandoffProof.DistinctCount
        filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_recovered_count = $bridgeRecoveryExecutionSoftToHardHandoffProof.RecoveredCount
        filetransfer_bridge_recovery_execution_soft_to_hard_handoff_missing_unresolved_count = $bridgeRecoveryExecutionSoftToHardHandoffProof.UnresolvedCount
        filetransfer_bridge_recovery_execution_soft_to_hard_handoff_verdict = $bridgeRecoveryExecutionSoftToHardHandoffProof.Verdict
        filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_raw_count = $bridgeRecoveryExecutionStartupPayloadIsolationProof.RawCount
        filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_distinct_count = $bridgeRecoveryExecutionStartupPayloadIsolationProof.DistinctCount
        filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_recovered_count = $bridgeRecoveryExecutionStartupPayloadIsolationProof.RecoveredCount
        filetransfer_bridge_recovery_execution_startup_payload_isolation_missing_unresolved_count = $bridgeRecoveryExecutionStartupPayloadIsolationProof.UnresolvedCount
        filetransfer_bridge_recovery_execution_startup_payload_isolation_verdict = $bridgeRecoveryExecutionStartupPayloadIsolationProof.Verdict
        filetransfer_bridge_recovery_execution_payload_hold_missing_raw_count = $bridgeRecoveryExecutionPayloadHoldProof.RawCount
        filetransfer_bridge_recovery_execution_payload_hold_missing_distinct_count = $bridgeRecoveryExecutionPayloadHoldProof.DistinctCount
        filetransfer_bridge_recovery_execution_payload_hold_missing_recovered_count = $bridgeRecoveryExecutionPayloadHoldProof.RecoveredCount
        filetransfer_bridge_recovery_execution_payload_hold_missing_unresolved_count = $bridgeRecoveryExecutionPayloadHoldProof.UnresolvedCount
        filetransfer_bridge_recovery_execution_payload_hold_verdict = $bridgeRecoveryExecutionPayloadHoldProof.Verdict
        filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_raw_count = $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredRawCount
        filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_distinct_count = $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredDistinctCount
        filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_recovered_count = $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredRecoveredCount
        filetransfer_bridge_recovery_execution_payload_hold_post_tuna_submission_epoch_unresolved_count = $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackSubmissionEpochFailureNotDeferredUnresolvedCount
        filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_raw_count = $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationRawCount
        filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_distinct_count = $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationDistinctCount
        filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_recovered_count = $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationRecoveredCount
        filetransfer_bridge_recovery_execution_payload_hold_post_tuna_late_submission_observation_unresolved_count = $bridgeRecoveryExecutionPayloadHoldProof.PostTunaFallbackLateSubmissionObservationUnresolvedCount
        filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_raw_count = $bridgeRecoveryExecutionSubmissionBarrierDrainProof.RawCount
        filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_distinct_count = $bridgeRecoveryExecutionSubmissionBarrierDrainProof.DistinctCount
        filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_recovered_count = $bridgeRecoveryExecutionSubmissionBarrierDrainProof.RecoveredCount
        filetransfer_bridge_recovery_execution_submission_barrier_drain_missing_unresolved_count = $bridgeRecoveryExecutionSubmissionBarrierDrainProof.UnresolvedCount
        filetransfer_bridge_recovery_execution_submission_barrier_drain_verdict = $bridgeRecoveryExecutionSubmissionBarrierDrainProof.Verdict
        runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_raw_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedRawCount
        runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_distinct_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedDistinctCount
        runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_recovered_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedRecoveredCount
        runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_not_preserved_unresolved_count = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldNotPreservedUnresolvedCount
        runtime_unlock_retry_regular_v4_recovery_lease_closure_hold_preservation_verdict = $runtimeUnlockRegularV4RecoveryLeaseIssuanceProof.ClosureHoldPreservationVerdict
        runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_raw_count = $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.RawCount
        runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_distinct_count = $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.DistinctCount
        runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_recovered_count = $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.RecoveredCount
        runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_missing_unresolved_count = $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.UnresolvedCount
        runtime_unlock_regular_v4_recovery_lease_closure_offer_timeout_handoff_verdict = $runtimeUnlockRegularV4RecoveryClosureOfferTimeoutHandoffProof.Verdict
        runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_raw_count = $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.RawCount
        runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_distinct_count = $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.DistinctCount
        runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_recovered_count = $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.RecoveredCount
        runtime_unlock_initial_offer_retry_lifecycle_handoff_missing_unresolved_count = $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.UnresolvedCount
        runtime_unlock_initial_offer_retry_lifecycle_handoff_verdict = $runtimeUnlockInitialOfferRetryLifecycleHandoffProof.Verdict
        runtime_unlock_initial_activation_answer_peer_delivery_missing_raw_count = $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.RawCount
        runtime_unlock_initial_activation_answer_peer_delivery_missing_distinct_count = $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.DistinctCount
        runtime_unlock_initial_activation_answer_peer_delivery_missing_recovered_count = $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.RecoveredCount
        runtime_unlock_initial_activation_answer_peer_delivery_missing_unresolved_count = $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.UnresolvedCount
        runtime_unlock_initial_activation_answer_peer_delivery_verdict = $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.Verdict
        runtime_unlock_initial_activation_answer_ack_return_delivery_missing_raw_count = $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnRawCount
        runtime_unlock_initial_activation_answer_ack_return_delivery_missing_distinct_count = $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnDistinctCount
        runtime_unlock_initial_activation_answer_ack_return_delivery_missing_recovered_count = $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnRecoveredCount
        runtime_unlock_initial_activation_answer_ack_return_delivery_missing_unresolved_count = $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.AcceptedAnswerAckReturnUnresolvedCount
        runtime_unlock_dial_failed_rejected_answer_not_retained_raw_count = $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerRawCount
        runtime_unlock_dial_failed_rejected_answer_not_retained_distinct_count = $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerDistinctCount
        runtime_unlock_dial_failed_rejected_answer_not_retained_recovered_count = $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerRecoveredCount
        runtime_unlock_dial_failed_rejected_answer_not_retained_unresolved_count = $runtimeUnlockInitialActivationAnswerPeerDeliveryProof.FailedDialRejectedAnswerUnresolvedCount
        runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_raw_count = $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.RawCount
        runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_distinct_count = $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.DistinctCount
        runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_recovered_count = $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.RecoveredCount
        runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_unresolved_count = $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.UnresolvedCount
        runtime_unlock_initial_offer_receive_recovery_hold_not_preserved_verdict = $runtimeUnlockInitialOfferReceiveRecoveryHoldProof.Verdict
        runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_raw_count = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.RawCount
        runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_distinct_count = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.DistinctCount
        runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_recovered_count = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.RecoveredCount
        runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing_unresolved_count = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.UnresolvedCount
        runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_verdict = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleaseDeliveryProof.Verdict
        runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_raw_count = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.RawCount
        runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_distinct_count = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.DistinctCount
        runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_recovered_count = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.RecoveredCount
        runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_unresolved_count = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.UnresolvedCount
        runtime_unlock_initial_activation_receive_recovery_hold_released_offer_expired_verdict = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedOfferExpiredProof.Verdict
        runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_raw_count = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.RawCount
        runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_distinct_count = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.DistinctCount
        runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_recovered_count = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.RecoveredCount
        runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_unresolved_count = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.UnresolvedCount
        runtime_unlock_initial_activation_receive_recovery_hold_released_path_lease_expired_verdict = $runtimeUnlockInitialActivationReceiveRecoveryHoldReleasedPathLeaseExpiredProof.Verdict
        runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_requested_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.RequestedCount
        runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_decided_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.DecidedCount
        runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_discarded_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.DiscardedCount
        runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_raw_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.RawCount
        runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_distinct_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.DistinctCount
        runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_recovered_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.RecoveredCount
        runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_missing_unresolved_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.UnresolvedCount
        runtime_unlock_retry_dispatch_fallback_tail_unblock_recheck_verdict = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.Verdict
        runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_raw_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementRawCount
        runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_distinct_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementDistinctCount
        runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_recovered_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementRecoveredCount
        runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_unresolved_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementUnresolvedCount
        runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_raw_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingRawCount
        runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_distinct_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingDistinctCount
        runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_recovered_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingRecoveredCount
        runtime_unlock_retry_dispatch_fallback_frontier_repair_hold_binding_missing_unresolved_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairHoldBindingUnresolvedCount
        runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_raw_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldRawCount
        runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_distinct_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldDistinctCount
        runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_recovered_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldRecoveredCount
        runtime_unlock_retry_dispatch_fallback_frontier_repair_settlement_recheck_missing_after_bound_hold_unresolved_count = $runtimeUnlockRetryFallbackTailUnblockRecheckProof.FallbackFrontierRepairSettlementAfterBoundHoldUnresolvedCount
        runtime_unlock_retry_dispatch_zero_offer_successor_rejected_raw_count = $runtimeUnlockRetryZeroOfferSuccessorProof.RawCount
        runtime_unlock_retry_dispatch_zero_offer_successor_rejected_distinct_count = $runtimeUnlockRetryZeroOfferSuccessorProof.DistinctCount
        runtime_unlock_retry_dispatch_zero_offer_successor_rejected_recovered_count = $runtimeUnlockRetryZeroOfferSuccessorProof.RecoveredCount
        runtime_unlock_retry_dispatch_zero_offer_successor_rejected_unresolved_count = $runtimeUnlockRetryZeroOfferSuccessorProof.UnresolvedCount
        runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_raw_count = $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationRawCount
        runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_distinct_count = $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationDistinctCount
        runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_recovered_count = $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationRecoveredCount
        runtime_unlock_retry_dispatch_zero_offer_successor_evaluation_suppressed_unresolved_count = $runtimeUnlockRetryZeroOfferSuccessorProof.SuppressedEvaluationUnresolvedCount
        runtime_unlock_retry_dispatch_zero_offer_successor_verdict = $runtimeUnlockRetryZeroOfferSuccessorProof.Verdict
        runtime_unlock_retry_dispatch_current_listener_adoption_missing_raw_count = $runtimeUnlockRetryCurrentListenerAdoptionProof.RawCount
        runtime_unlock_retry_dispatch_current_listener_adoption_missing_distinct_count = $runtimeUnlockRetryCurrentListenerAdoptionProof.DistinctCount
        runtime_unlock_retry_dispatch_current_listener_adoption_missing_recovered_count = $runtimeUnlockRetryCurrentListenerAdoptionProof.RecoveredCount
        runtime_unlock_retry_dispatch_current_listener_adoption_missing_unresolved_count = $runtimeUnlockRetryCurrentListenerAdoptionProof.UnresolvedCount
        runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_raw_count = $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredRawCount
        runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_distinct_count = $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredDistinctCount
        runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_recovered_count = $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredRecoveredCount
        runtime_unlock_retry_dispatch_current_listener_adoption_restart_expired_unresolved_count = $runtimeUnlockRetryCurrentListenerAdoptionProof.RestartExpiredUnresolvedCount
        runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_raw_count = $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingRawCount
        runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_distinct_count = $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingDistinctCount
        runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_recovered_count = $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingRecoveredCount
        runtime_unlock_retry_dispatch_current_listener_adoption_live_projection_missing_unresolved_count = $runtimeUnlockRetryCurrentListenerAdoptionProof.LiveProjectionMissingUnresolvedCount
        runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_raw_count = $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.RawCount
        runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_distinct_count = $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.DistinctCount
        runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_recovered_count = $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.RecoveredCount
        runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_invalid_unresolved_count = $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.UnresolvedCount
        runtime_unlock_retry_dispatch_fallback_paired_yield_successor_release_verdict = $runtimeUnlockRetryFallbackPairedYieldSuccessorReleaseProof.Verdict
        runtime_unlock_retry_regular_v4_recovery_settlement_missing_count = $runtimeUnlockRetryRegularV4RecoverySettlementMissingCount
        runtime_unlock_retry_blocked_by_regular_v4_receive_recovery_count = $runtimeUnlockRetryBlockedByRegularV4RecoveryCount
        runtime_unlock_retry_held_by_regular_v4_receive_recovery_count = $runtimeUnlockRetryHeldByRegularV4RecoveryCount
        runtime_unlock_retry_blocked_by_regular_v4_recovery_count = $runtimeUnlockRetryBlockedByRegularV4RecoveryCount
        runtime_unlock_retry_held_by_regular_v4_recovery_count = $runtimeUnlockRetryHeldByRegularV4RecoveryCount
        runtime_unlock_retry_regular_v4_recovery_settlement_missing_raw_count = $runtimeUnlockRegularV4RecoverySettlementProof.RawCount
        runtime_unlock_retry_regular_v4_recovery_settlement_missing_distinct_count = $runtimeUnlockRegularV4RecoverySettlementProof.DistinctCount
        runtime_unlock_retry_regular_v4_recovery_settlement_missing_recovered_count = $runtimeUnlockRegularV4RecoverySettlementProof.RecoveredCount
        runtime_unlock_retry_regular_v4_recovery_settlement_missing_unresolved_count = $runtimeUnlockRegularV4RecoverySettlementProof.UnresolvedCount
        runtime_unlock_retry_regular_v4_recovery_settlement_verdict = $runtimeUnlockRegularV4RecoverySettlementProof.Verdict
        runtime_unlock_retry_regular_v4_settlement_handoff_bypassed_count = $runtimeUnlockRegularV4RecoverySettlementProof.HandoffBypassCount
        runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_raw_count = $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.RawCount
        runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_distinct_count = $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.DistinctCount
        runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_recovered_count = $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.RecoveredCount
        runtime_unlock_post_tuna_fallback_transport_proof_handoff_missing_unresolved_count = $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.UnresolvedCount
        runtime_unlock_post_tuna_fallback_transport_proof_handoff_verdict = $runtimeUnlockPostTunaFallbackTransportProofHandoffProof.Verdict
        runtime_unlock_precommit_fallback_recovery_handoff_suppressed_raw_count = $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.RawCount
        runtime_unlock_precommit_fallback_recovery_handoff_suppressed_distinct_count = $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.DistinctCount
        runtime_unlock_precommit_fallback_recovery_handoff_suppressed_recovered_count = $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.RecoveredCount
        runtime_unlock_precommit_fallback_recovery_handoff_suppressed_unresolved_count = $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.UnresolvedCount
        runtime_unlock_precommit_fallback_recovery_handoff_suppression_verdict = $runtimeUnlockUncommittedTunaFallbackHandoffSuppressionProof.Verdict
        runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_raw_count = $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.RawCount
        runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_distinct_count = $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.DistinctCount
        runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_recovered_count = $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.RecoveredCount
        runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_misbound_unresolved_count = $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.UnresolvedCount
        runtime_unlock_uncommitted_tuna_fallback_handoff_precommit_authority_verdict = $runtimeUnlockUncommittedTunaFallbackPrecommitAuthorityProof.Verdict
        runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_raw_count = $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.RawCount
        runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_distinct_count = $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.DistinctCount
        runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_recovered_count = $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.RecoveredCount
        runtime_unlock_uncommitted_tuna_fallback_proof_authority_missing_unresolved_count = $runtimeUnlockUncommittedTunaFallbackProofAuthorityProof.UnresolvedCount
        post_tuna_fallback_outbound_receive_worker_handoff_missing_count = $outboundReceiveWorkerHandoffProof.Count
        fallback_tail_yield_authority_rejected_count = $fallbackTailYieldAuthorityRejectedCount
        fallback_tail_coordinator_aggregate_verdict = $fallbackTailCoordinatorAggregateProof.Verdict
        fallback_tail_coordinator_yield_issued_count = $fallbackTailCoordinatorAggregateProof.YieldIssuedCount
        fallback_tail_coordinator_local_proof_receipt_decided_count = $fallbackTailCoordinatorAggregateProof.LocalProofReceiptDecidedCount
        fallback_tail_coordinator_peer_proof_receipt_decided_count = $fallbackTailCoordinatorAggregateProof.PeerProofReceiptDecidedCount
        fallback_tail_coordinator_pairing_committed_count = $fallbackTailCoordinatorAggregateProof.PairingCommittedCount
        fallback_tail_coordinator_valid_pairing_count = $fallbackTailCoordinatorAggregateProof.ValidPairingCount
        fallback_tail_coordinator_paired_release_reserved_count = $fallbackTailCoordinatorAggregateProof.PairedReleaseReservedCount
        fallback_tail_coordinator_valid_paired_release_reserved_count = $fallbackTailCoordinatorAggregateProof.ValidPairedReleaseReservedCount
        fallback_tail_coordinator_paired_release_consumed_count = $fallbackTailCoordinatorAggregateProof.PairedReleaseConsumedCount
        fallback_tail_coordinator_valid_paired_release_consumed_count = $fallbackTailCoordinatorAggregateProof.ValidPairedReleaseConsumedCount
        fallback_tail_coordinator_paired_release_carry_forward_count = $fallbackTailCoordinatorAggregateProof.PairedReleaseCarryForwardCount
        fallback_tail_coordinator_valid_paired_release_carry_forward_count = $fallbackTailCoordinatorAggregateProof.ValidPairedReleaseCarryForwardCount
        fallback_tail_coordinator_invalid_raw_count = $fallbackTailCoordinatorAggregateProof.InvalidRawCount
        fallback_tail_coordinator_invalid_distinct_count = $fallbackTailCoordinatorAggregateProof.InvalidDistinctCount
        fallback_tail_coordinator_invalid_recovered_count = $fallbackTailCoordinatorAggregateProof.InvalidRecoveredCount
        fallback_tail_coordinator_invalid_unresolved_count = $fallbackTailCoordinatorAggregateProof.InvalidUnresolvedCount
        fallback_tail_coordinator_reauthorization_staged_count = $fallbackTailCoordinatorAggregateProof.ReauthorizationStagedCount
        fallback_tail_coordinator_reauthorization_rebound_count = $fallbackTailCoordinatorAggregateProof.ReauthorizationReboundCount
        fallback_tail_coordinator_reauthorization_successor_issued_count = $fallbackTailCoordinatorAggregateProof.ReauthorizationSuccessorIssuedCount
        fallback_tail_coordinator_reauthorization_invalidated_count = $fallbackTailCoordinatorAggregateProof.ReauthorizationInvalidatedCount
        fallback_tail_owner_generation_mismatch_count = $fallbackTailOwnerGenerationMismatchCount
        fallback_tail_bridge_recovery_generation_pairing_verdict = $fallbackTailBridgeRecoveryPairingProof.Verdict
        fallback_tail_bridge_recovery_generation_mismatch_raw_count = $fallbackTailBridgeRecoveryPairingProof.RawCount
        fallback_tail_bridge_recovery_generation_mismatch_distinct_count = $fallbackTailBridgeRecoveryPairingProof.DistinctCount
        fallback_tail_bridge_recovery_generation_mismatch_recovered_count = $fallbackTailBridgeRecoveryPairingProof.RecoveredCount
        fallback_tail_bridge_recovery_generation_mismatch_unresolved_count = $fallbackTailBridgeRecoveryPairingProof.UnresolvedCount
        post_tuna_fallback_paired_yield_transport_epoch_reauthorization_verdict = $fallbackTailTransportEpochReauthorizationProof.Verdict
        post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_raw_count = $fallbackTailTransportEpochReauthorizationProof.RawCount
        post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_distinct_count = $fallbackTailTransportEpochReauthorizationProof.DistinctCount
        post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_recovered_count = $fallbackTailTransportEpochReauthorizationProof.RecoveredCount
        post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing_unresolved_count = $fallbackTailTransportEpochReauthorizationProof.UnresolvedCount
        data_path_correlation_index_ms = $dataPathIndexStopwatch.ElapsedMilliseconds
        data_path_correlation_total_ms = $dataPathCorrelationStopwatch.ElapsedMilliseconds
        data_path_correlation_index_build_visit_count = [int64]$fileTunaV4DataPathCorrelationIndex.Statistics.build_visit_count
        data_path_correlation_bounded_lookup_visit_count = [int64]$fileTunaV4DataPathCorrelationIndex.Statistics.bounded_lookup_visit_count
        bridge_recovery_submission_barrier_drain_index_build_visit_count =
            [int64]$bridgeRecoveryExecutionSubmissionBarrierDrainProof.IndexBuildVisitCount
        bridge_recovery_submission_barrier_drain_semantic_index_build_visit_count =
            [int64]$bridgeRecoveryExecutionSubmissionBarrierDrainProof.SemanticIndexBuildVisitCount
        bridge_recovery_submission_barrier_drain_retirement_index_build_visit_count =
            [int64]$bridgeRecoveryExecutionSubmissionBarrierDrainProof.RetirementIndexBuildVisitCount
        bridge_recovery_submission_barrier_drain_bounded_lookup_visit_count =
            [int64]$bridgeRecoveryExecutionSubmissionBarrierDrainProof.BoundedLookupVisitCount
    }
    $compactDto | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $ArtifactDir 'filetransfer-retained-compact-summary.json') -Encoding UTF8

    return [pscustomobject]@{
        Verdict = $verdict
        GateStatus = $gateStatus
        DataPathCorrelationIndexMilliseconds =
            $dataPathIndexStopwatch.ElapsedMilliseconds
        DataPathCorrelationTotalMilliseconds =
            $dataPathCorrelationStopwatch.ElapsedMilliseconds
        DataPathCorrelationIndexBuildVisitCount =
            [int64]$fileTunaV4DataPathCorrelationIndex.Statistics.build_visit_count
        DataPathCorrelationBoundedLookupVisitCount =
            [int64]$fileTunaV4DataPathCorrelationIndex.Statistics.bounded_lookup_visit_count
        BridgeRecoverySubmissionBarrierDrainIndexBuildVisitCount =
            [int64]$bridgeRecoveryExecutionSubmissionBarrierDrainProof.IndexBuildVisitCount
        BridgeRecoverySubmissionBarrierDrainBoundedLookupVisitCount =
            [int64]$bridgeRecoveryExecutionSubmissionBarrierDrainProof.BoundedLookupVisitCount
    }
}

function Get-FileTransferEfficiencyAllEventsFromIndex {
    param($Index)

    if ($null -ne $Index -and $null -ne $Index.PSObject.Properties['AllEvents']) {
        return @($Index.PSObject.Properties['AllEvents'].Value)
    }

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

function Get-FileTransferEfficiencySemanticEventsFromIndex {
    param([Parameter(Mandatory = $true)]$Index)

    $eventsByNameProperty = $Index.PSObject.Properties['EventsByName']
    if ($null -eq $eventsByNameProperty) {
        return [pscustomobject]@{
            Events = @()
            NameLookupCount = 0L
            EventVisitCount = 0L
        }
    }

    $eventsByName = [System.Collections.IDictionary]$eventsByNameProperty.Value
    $selectedNames = New-Object 'System.Collections.Generic.HashSet[string]' `
        ([System.StringComparer]::Ordinal)
    foreach ($name in @(
            'filetransfer_route_selected',
            'filetransfer_route_transitioned',
            'filetransfer_live_route_epoch_started',
            'file_transfer_inbound_terminal',
            'file_transfer_outbound_terminal',
            'transfer_terminal',
            'filetransfer_v4_throughput_summary',
            'filetransfer_v4_sender_throughput_summary',
            'filetransfer_v4_receiver_throughput_summary',
            'filetransfer_v4_efficiency_summary',
            'filetransfer_v4_sender_pump_summary',
            'filetransfer_receiver_sparse_commit_summary',
            'filetransfer_v4_sparse_write_committed',
            'filetransfer_v6_sparse_write_committed',
            'filetransfer_v6_receiver_state_sent',
            'filetransfer_v6_receiver_state_received',
            'filetransfer_frontier_stall_repair_due',
            'filetransfer_v4_frontier_stall_missing_range_due',
            'filetransfer_v6_post_tuna_fallback_frontier_progress_state_forced',
            'filetransfer_v4_state_sent',
            'filetransfer_v4_state_received',
            'filetransfer_v6_receiver_state_deferred',
            'filetransfer_v6_receiver_state_coalesced',
            'filetransfer_v6_frontier_request_sent',
            'session_liveness_recovery_deferred',
            'session_liveness_timeout_deferred',
            'session_liveness_timeout_deferred_for_filetransfer_recovery',
            'session_liveness_timeout_deferred_for_bridge_filetransfer_recovery',
            'filetransfer_liveness_deferral_granted',
            'filetransfer_runtime_owner_stale_update_rejected',
            'filetransfer_fallback_tail_retired_owner_update_suppressed',
            'filetransfer_fallback_tail_recovery_owner_retired_after_yield',
            'filetransfer_control_plane_send_retired_stale_generation',
            'filetransfer_fallback_tail_quiescent_yield_granted',
            'filetransfer_fallback_tail_terminal_proof_consumed',
            'filetransfer_post_tuna_fallback_control_plane_pressure_marked',
            'filetransfer_post_tuna_fallback_control_plane_pressure_recovered',
            'filetransfer_fallback_checkpoint_exchange_started',
            'filetransfer_fallback_checkpoint_reissue_requested',
            'filetransfer_fallback_checkpoint_exchange_retired',
            'filetransfer_control_plane_delivery_result',
            'filetransfer_control_plane_delivery_observed',
            'filetransfer_runtime_owned_workers_retired',
            'filetransfer_fallback_control_plane_owner_retired',
            'filetransfer_fallback_tail_progress_coalesced',
            'session_liveness_timeout',
            'filetransfer_live_progress_timeout')) {
        $selectedNames.Add($name) | Out-Null
    }

    # The fallback-tail treadmill metric intentionally treats these families
    # as semantic recovery activity. Select their name buckets without walking
    # the parser's complete (and potentially very large) event array.
    foreach ($key in @($eventsByName.Keys)) {
        $name = [string]$key
        if ($name -like '*state_refresh*' -or
            $name -like '*bridge_recovery*' -or
            $name -like '*receive_stall_recovery*') {
            $selectedNames.Add($name) | Out-Null
        }
    }

    $events = New-Object System.Collections.Generic.List[object]
    $seenSequences = New-Object 'System.Collections.Generic.HashSet[int]'
    $nameLookupCount = 0L
    $eventVisitCount = 0L
    foreach ($name in @($selectedNames)) {
        $nameLookupCount++
        if (-not $eventsByName.Contains($name) -and -not $eventsByName.ContainsKey($name)) {
            continue
        }

        foreach ($event in $eventsByName.Item($name)) {
            $eventVisitCount++
            if ($null -ne $event -and $seenSequences.Add([int]$event.Sequence)) {
                $events.Add($event) | Out-Null
            }
        }
    }

    return [pscustomobject]@{
        Events = @($events.ToArray() | Sort-Object Sequence, LineNumber)
        NameLookupCount = $nameLookupCount
        EventVisitCount = $eventVisitCount
    }
}

function Get-FileTransferEfficiencyIndexedMetrics {
    param([Parameter(Mandatory = $true)]$Index)

    $liveEpochEvents = @(
        Get-FileTransferCompactIndexEventsByName -Index $Index -Name 'filetransfer_live_route_epoch_started' |
            Sort-Object Sequence, LineNumber
    )
    $epochMetrics = New-Object System.Collections.Generic.List[object]
    foreach ($liveEvent in $liveEpochEvents) {
        $epochMetrics.Add([ordered]@{
            StartSequence = [int]$liveEvent.Sequence
            MinTimestampUtc = $null
            MaxTimestampUtc = $null
            MaxProgressBytes = 0L
            FallbackProgressBytes = 0L
        }) | Out-Null
    }

    $allEventsProperty = $Index.PSObject.Properties['AllEvents']
    $allEvents = if ($null -ne $allEventsProperty) { $allEventsProperty.Value } else { @() }
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
    $fallbackFields = @('raw_bytes', 'raw_chunk_bytes', 'bytes_written')
    $minTimestampUtc = $null
    $maxTimestampUtc = $null
    $maxSequence = 0
    $epochIndex = -1
    $visitCount = 0L
    foreach ($event in $allEvents) {
        $visitCount++
        if ($null -eq $event) {
            continue
        }

        $sequence = [int]$event.Sequence
        if ($sequence -gt $maxSequence) {
            $maxSequence = $sequence
        }
        while (($epochIndex + 1) -lt $liveEpochEvents.Count -and
            [int]$liveEpochEvents[$epochIndex + 1].Sequence -le $sequence) {
            $epochIndex++
        }

        if ($null -ne $event.TimestampUtc) {
            if ($null -eq $minTimestampUtc -or $event.TimestampUtc -lt $minTimestampUtc) {
                $minTimestampUtc = $event.TimestampUtc
            }
            if ($null -eq $maxTimestampUtc -or $event.TimestampUtc -gt $maxTimestampUtc) {
                $maxTimestampUtc = $event.TimestampUtc
            }

            if ($epochIndex -ge 0) {
                $epoch = $epochMetrics[$epochIndex]
                if ($null -eq $epoch.MinTimestampUtc -or $event.TimestampUtc -lt $epoch.MinTimestampUtc) {
                    $epoch.MinTimestampUtc = $event.TimestampUtc
                }
                if ($null -eq $epoch.MaxTimestampUtc -or $event.TimestampUtc -gt $epoch.MaxTimestampUtc) {
                    $epoch.MaxTimestampUtc = $event.TimestampUtc
                }
            }
        }

        if ($epochIndex -lt 0) {
            continue
        }

        $epoch = $epochMetrics[$epochIndex]
        $fields = $event.Fields
        if ($null -eq $fields) {
            continue
        }
        foreach ($fieldName in $candidateFields) {
            if (-not $fields.ContainsKey($fieldName)) {
                continue
            }
            $value = 0L
            if (-not [long]::TryParse([string]$fields[$fieldName], [ref]$value)) {
                continue
            }
            if ($value -gt [long]$epoch.MaxProgressBytes) {
                $epoch.MaxProgressBytes = $value
            }
        }
        foreach ($fieldName in $fallbackFields) {
            if (-not $fields.ContainsKey($fieldName)) {
                continue
            }
            $value = 0L
            if ([long]::TryParse([string]$fields[$fieldName], [ref]$value)) {
                $epoch.FallbackProgressBytes = [long]$epoch.FallbackProgressBytes + $value
            }
        }
    }

    $overallDurationMs = if ($null -ne $minTimestampUtc -and $null -ne $maxTimestampUtc) {
        [long][Math]::Max(0, ($maxTimestampUtc - $minTimestampUtc).TotalMilliseconds)
    }
    else {
        0L
    }
    $rows = @(
        foreach ($epoch in $epochMetrics) {
            $durationMs = if ($null -ne $epoch.MinTimestampUtc -and $null -ne $epoch.MaxTimestampUtc) {
                [long][Math]::Max(0, ($epoch.MaxTimestampUtc - $epoch.MinTimestampUtc).TotalMilliseconds)
            }
            else {
                0L
            }
            [pscustomobject]@{
                StartSequence = [int]$epoch.StartSequence
                DurationMs = $durationMs
                BytesAttributed = if ([long]$epoch.MaxProgressBytes -gt 0) {
                    [long]$epoch.MaxProgressBytes
                }
                else {
                    [long]$epoch.FallbackProgressBytes
                }
            }
        }
    )

    return [pscustomobject]@{
        OverallDurationMs = $overallDurationMs
        MaxSequence = $maxSequence
        Epochs = $rows
        EventVisitCount = $visitCount
    }
}

function Get-FileTransferEfficiencyEventsByName {
    param(
        $Index,
        [object[]]$Events,
        [Parameter(Mandatory = $true)][object[]]$Names
    )

    $normalizedNames = @($Names | ForEach-Object { [string]$_ } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $items = New-Object System.Collections.Generic.List[object]
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
        [string[]]$RouteSequence,
        $IndexedMetrics = $null
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
        $duration = if ($null -ne $IndexedMetrics) {
            [long]$IndexedMetrics.OverallDurationMs
        }
        else {
            Get-FileTransferEfficiencyDurationMs -Events $Events
        }
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
    $maxSequence = if ($null -ne $IndexedMetrics) { [int]$IndexedMetrics.MaxSequence } else { 0 }
    if ($null -eq $IndexedMetrics) {
        foreach ($event in @($Events)) {
            if ($event.Sequence -gt $maxSequence) {
                $maxSequence = [int]$event.Sequence
            }
        }
    }

    for ($i = 0; $i -lt $sorted.Count; $i++) {
        $startEvent = $sorted[$i]
        $nextEvent = if ($i + 1 -lt $sorted.Count) { $sorted[$i + 1] } else { $null }
        $startSequence = [int]$startEvent.Sequence
        $endSequence = if ($null -ne $nextEvent) { [int]$nextEvent.Sequence - 1 } else { $maxSequence }
        if ($null -ne $IndexedMetrics -and $i -lt @($IndexedMetrics.Epochs).Count) {
            $durationMs = [long]$IndexedMetrics.Epochs[$i].DurationMs
            $bytesAttributed = [long]$IndexedMetrics.Epochs[$i].BytesAttributed
        }
        else {
            $epochEvents = @($Events | Where-Object { [int]$_.Sequence -ge $startSequence -and [int]$_.Sequence -le $endSequence })
            $durationMs = Get-FileTransferEfficiencyDurationMs -Events $epochEvents
            $bytesAttributed = Get-FileTransferEfficiencyMaxProgressBytes -Events $epochEvents
        }
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
        $GateResult = $null,
        [System.Collections.IDictionary]$StageDiagnostics = $null
    )

    New-Item -ItemType Directory -Force -Path $ArtifactDir | Out-Null

    $efficiencyStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
    if ($null -ne $StageDiagnostics) {
        $StageDiagnostics['current_correlator'] = 'efficiency_summary'
        $StageDiagnostics['correlator_status'] = 'started'
        $StageDiagnostics['correlator_input_count'] = if ($null -ne $Index -and $null -ne $Index.PSObject.Properties['EventCount']) {
            [int64]$Index.EventCount
        }
        else {
            [int64]@($Events).Count
        }
        $StageDiagnostics['correlator_visit_count'] = 0
        $StageDiagnostics['correlator_lookup_count'] = 0
        $StageDiagnostics['correlator_started_utc'] =
            [datetime]::UtcNow.ToString('O', [System.Globalization.CultureInfo]::InvariantCulture)
        $StageDiagnostics['correlator_completed_utc'] = ''
        $StageDiagnostics['correlator_elapsed_ms'] = 0
        Write-FileTransferRetainedAnalysisStageDiagnostics -ArtifactDir $ArtifactDir -Diagnostics $StageDiagnostics
    }

    $indexedMetrics = $null
    if ($null -ne $Index) {
        $semanticSelection = Get-FileTransferEfficiencySemanticEventsFromIndex -Index $Index
        $indexedMetrics = Get-FileTransferEfficiencyIndexedMetrics -Index $Index
        $Events = @($semanticSelection.Events)
        if ($null -ne $StageDiagnostics) {
            $StageDiagnostics['correlator_input_count'] = $Events.Count
            $StageDiagnostics['correlator_visit_count'] =
                [int64]$semanticSelection.EventVisitCount + [int64]$indexedMetrics.EventVisitCount
            $StageDiagnostics['correlator_lookup_count'] = [int64]$semanticSelection.NameLookupCount
            $StageDiagnostics['efficiency_summary_all_events_single_pass_visit_count'] =
                [int64]$indexedMetrics.EventVisitCount
            $StageDiagnostics['efficiency_summary_semantic_event_count'] = $Events.Count
        }
    }
    else {
        $Events = @($Events | Where-Object { $null -ne $_ })
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

    $routeChronologyEvents = @(
        Get-FileTransferEfficiencyEventsByName `
            -Index $Index `
            -Events $Events `
            -Names @('filetransfer_route_selected', 'filetransfer_route_transitioned')
    )
    $routeEvents = @(
        Get-FileTransferCanonicalRouteSelectionEvents -Events $routeChronologyEvents
    )
    $liveEpochEvents = @(
        Get-FileTransferEfficiencyEventsByName `
            -Index $Index `
            -Events $Events `
            -Names @('filetransfer_live_route_epoch_started') |
            Where-Object { Test-FileTransferProductionRouteEvidenceEvent -Event $_ } |
            Sort-Object `
                @{ Expression = {
                        Get-FileTransferEventInt64Field `
                            -Event $_ `
                            -Name 'live_route_epoch' `
                            -Default 0
                    } }, `
                Sequence, LineNumber
    )
    $routeSequence = @(Get-FileTransferEfficiencyRouteChanges -Events $routeEvents)
    $liveEpochSequence = @(Get-FileTransferEfficiencyRouteChanges -Events $liveEpochEvents)

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

    $overallDurationMs = if ($null -ne $indexedMetrics) {
        [long]$indexedMetrics.OverallDurationMs
    }
    else {
        Get-FileTransferEfficiencyDurationMs -Events $Events
    }
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

    $epochRows = @(Get-FileTransferEfficiencyRouteEpochRows -Events $Events -RouteSequence $routeSequence -IndexedMetrics $indexedMetrics)
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

    $efficiencyStopwatch.Stop()
    if ($null -ne $StageDiagnostics) {
        $StageDiagnostics['correlator_status'] = 'completed'
        $StageDiagnostics['correlator_completed_utc'] =
            [datetime]::UtcNow.ToString('O', [System.Globalization.CultureInfo]::InvariantCulture)
        $StageDiagnostics['correlator_elapsed_ms'] = $efficiencyStopwatch.ElapsedMilliseconds
        Write-FileTransferRetainedAnalysisStageDiagnostics -ArtifactDir $ArtifactDir -Diagnostics $StageDiagnostics
    }
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
        data_path_correlation_index_ms = 0
        data_path_correlation_total_ms = 0
        data_path_correlation_index_build_visit_count = 0
        data_path_correlation_bounded_lookup_visit_count = 0
        total_ms = 0
        event_count = 0
        indexed_event_count = 0
        field_statistics_mode = 'not_decided'
        log_file_count = 0
        compact_mode = 0
        compact_requested = 0
        compact_for_large_slice = 0
        large_full_indexed = 0
        compact_selection_reason = 'not_selected'
        artifact_write_started = 0
        current_correlator = ''
        correlator_status = 'not_started'
        correlator_input_count = 0
        correlator_visit_count = 'unavailable'
        correlator_lookup_count = 'unavailable'
        correlator_started_utc = ''
        correlator_completed_utc = ''
        correlator_elapsed_ms = 0
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

        $compactRequested = [string]::Equals($env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT, '1', [System.StringComparison]::OrdinalIgnoreCase)
        $compactForLargeSlice = $eventsForArtifacts.Count -gt 25000 -and -not $IncludeRawSlices
        $largeFullIndexed = $eventsForArtifacts.Count -gt 25000 -and $IncludeRawSlices -and -not $compactRequested
        # FieldMax/FieldSum are acceleration caches only. Eagerly parsing every
        # field in a large raw slice duplicates the work and memory retained by
        # the canonical event index before full analysis can begin. Large full
        # analyses use the existing per-scope, per-field lazy cache instead.
        $deferEagerFieldStatistics = $eventsForArtifacts.Count -gt 25000
        $includeEagerFieldStatistics = -not ($compactRequested -or $deferEagerFieldStatistics)
        $stageDiagnostics['field_statistics_mode'] = if ($includeEagerFieldStatistics) {
            'eager_all_fields'
        }
        else {
            'deferred_per_scope'
        }
        $indexStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $index = Initialize-FileTransferRetainedEventIndex `
            -Events $eventsForArtifacts `
            -LogFiles $logFiles `
            -IncludeFieldStatistics:$includeEagerFieldStatistics
        $indexStopwatch.Stop()
        $stageDiagnostics['index_ms'] = $indexStopwatch.ElapsedMilliseconds
        $stageDiagnostics['indexed_event_count'] = Get-FileTransferCompactIndexTotalEventCount -Index $index
        $stageDiagnostics['total_ms'] = $totalStopwatch.ElapsedMilliseconds
        Write-FileTransferRetainedAnalysisStageDiagnostics -ArtifactDir $resolvedArtifactDir -Diagnostics $stageDiagnostics

        $stageDiagnostics['compact_requested'] = if ($compactRequested) { 1 } else { 0 }
        $stageDiagnostics['compact_for_large_slice'] = if ($compactForLargeSlice) { 1 } else { 0 }
        $stageDiagnostics['large_full_indexed'] = if ($largeFullIndexed) { 1 } else { 0 }
        if ($compactRequested -or $compactForLargeSlice -or $largeFullIndexed) {
            $stageDiagnostics['compact_mode'] = if ($largeFullIndexed) { 0 } else { 1 }
            $stageDiagnostics['compact_selection_reason'] = if ($compactRequested) {
                'environment_requested'
            }
            elseif ($largeFullIndexed) {
                'large_full_indexed_semantics'
            }
            else {
                'large_slice_without_raw_slices'
            }
            $stageDiagnostics['artifact_write_started'] = 1
            $stageDiagnostics['total_ms'] = $totalStopwatch.ElapsedMilliseconds
            Write-FileTransferRetainedAnalysisStageDiagnostics -ArtifactDir $resolvedArtifactDir -Diagnostics $stageDiagnostics
            $artifactStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
            $compactArtifactArgs = @{
                ArtifactDir = $resolvedArtifactDir
                Index = $index
                StageDiagnostics = $stageDiagnostics
                LiveRouteProofMode = $LiveRouteProofMode
            }
            $compactResult = Write-FileTransferCompactRetainedAnalysisArtifacts @compactArtifactArgs
            if ($largeFullIndexed) {
                Copy-Item `
                    -LiteralPath (Join-Path $resolvedArtifactDir 'filetransfer-retained-compact-summary.json') `
                    -Destination (Join-Path $resolvedArtifactDir 'filetransfer-retained-full-summary.json') `
                    -Force
                Write-FileTransferIndexedRawEvidenceArtifact `
                    -ArtifactDir $resolvedArtifactDir `
                    -Index $index
            }
            $stageDiagnostics['data_path_correlation_index_ms'] =
                $compactResult.DataPathCorrelationIndexMilliseconds
            $stageDiagnostics['data_path_correlation_total_ms'] =
                $compactResult.DataPathCorrelationTotalMilliseconds
            $stageDiagnostics['data_path_correlation_index_build_visit_count'] =
                $compactResult.DataPathCorrelationIndexBuildVisitCount
            $stageDiagnostics['data_path_correlation_bounded_lookup_visit_count'] =
                $compactResult.DataPathCorrelationBoundedLookupVisitCount
            $efficiencyArtifactArgs = @{
                ArtifactDir = $resolvedArtifactDir
                Index = $index
                StageDiagnostics = $stageDiagnostics
            }
            Write-FileTransferEfficiencySummaryArtifacts @efficiencyArtifactArgs
            $artifactStopwatch.Stop()
            $stageDiagnostics['artifact_write_ms'] = $artifactStopwatch.ElapsedMilliseconds
            $stageDiagnostics['artifact_write_started'] = 0
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

        $stageDiagnostics['compact_selection_reason'] = 'full_analysis_requested'
        $stageDiagnostics['artifact_write_started'] = 1
        $stageDiagnostics['total_ms'] = $totalStopwatch.ElapsedMilliseconds
        Write-FileTransferRetainedAnalysisStageDiagnostics -ArtifactDir $resolvedArtifactDir -Diagnostics $stageDiagnostics

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
        Write-FileTransferEfficiencySummaryArtifacts -ArtifactDir $resolvedArtifactDir -Index $index -Events $eventsForArtifacts -Summary $summary -GateResult $gate -StageDiagnostics $stageDiagnostics
        $artifactStopwatch.Stop()
        $stageDiagnostics['artifact_write_ms'] = $artifactStopwatch.ElapsedMilliseconds
        $stageDiagnostics['artifact_write_started'] = 0

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
