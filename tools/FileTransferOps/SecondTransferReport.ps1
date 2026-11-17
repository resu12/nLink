Set-StrictMode -Version Latest

function Get-FileTransferSecondReportSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    $stream = [IO.File]::OpenRead($Path)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $sha.Dispose(); $stream.Dispose() }
}

# A derived view of the primary's already evaluated evidence, never an analyzer
# entry point. Serialized results are used only for output-integrity validation.
function Write-FileTransferSecondTransferReport {
    param(
        [Parameter(Mandatory = $true)][string]$ArtifactDir,
        [Parameter(Mandatory = $true)]$Request,
        [Parameter(Mandatory = $true)]$Index,
        [Parameter(Mandatory = $true)]$Full,
        [Parameter(Mandatory = $true)]$Compact,
        [Parameter(Mandatory = $true)]$Parity,
        [Parameter(Mandatory = $true)]$RouteState,
        [Parameter(Mandatory = $true)]$Gui,
        [Parameter(Mandatory = $true)][string]$ParentVerdict,
        [Parameter(Mandatory = $true)][string]$ParentGateStatus,
        [Parameter(Mandatory = $true)][int]$ParentHardFailures,
        [Parameter(Mandatory = $true)][int]$ParentProofFailures,
        [Parameter(Mandatory = $true)][string]$ParentWarningCapExceeded
    )

    . (Join-Path (Split-Path -Parent $PSScriptRoot) 'GuiSmokeEvidenceCatalog.ps1')
    $inputPath = Join-Path $ArtifactDir 'filetransfer-retained-log-slice.log'
    $slicePath = Join-Path $ArtifactDir 'filetransfer-second-transfer-retained-log-slice.log'
    $guiPath = Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json'
    $outputDir = Join-Path $ArtifactDir 'second-transfer-analysis'
    if (Test-Path -LiteralPath $outputDir) { throw 'second_transfer_report_already_exists' }
    if ([int]$Request.schemaVersion -ne 1 -or
        [string]$Request.acceptanceRunId -cne [string]$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -or
        [string]$Request.scenarioRunId -cne [string]$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID -or
        [string]::IsNullOrWhiteSpace([string]$Request.acceptanceRunId) -or
        [string]::IsNullOrWhiteSpace([string]$Request.scenarioRunId) -or
        [string]$Request.attemptId -notmatch '^[0-9a-f]{32}$' -or
        [IO.Path]::GetFullPath([string]$Request.artifactDir) -ine [IO.Path]::GetFullPath($ArtifactDir) -or
        [string]$Request.inputSha256 -cne (Get-FileTransferSecondReportSha256 $inputPath) -or
        [string]$Request.sliceSha256 -cne (Get-FileTransferSecondReportSha256 $slicePath) -or
        [string]$Request.guiSha256 -cne (Get-FileTransferSecondReportSha256 $guiPath)) {
        throw 'second_transfer_request_binding_invalid'
    }
    if ($ParentGateStatus -notin @('pass', 'warn') -or
        $ParentVerdict -notin @('PASS', 'WARN_EXTERNAL_TRANSPORT') -or
        $ParentHardFailures -ne 0 -or $ParentProofFailures -ne 0 -or
        $ParentWarningCapExceeded -cne '(none)' -or
        $Parity.Verdict -cne 'pass' -or $RouteState.Parity.Verdict -cne 'pass' -or
        $RouteState.EffectiveResult.Verdict -cne 'pass') {
        throw 'second_transfer_parent_proof_not_accepted'
    }
    foreach ($view in @($Full, $Compact)) {
        if ($view.Mode -cne 'SnapshotV1Authoritative' -or -not $view.StreamComplete -or
            $view.ArtifactEvidenceVerdict -cne 'pass' -or $null -ne $view.FirstUnresolvedBoundary) {
            throw 'second_transfer_parent_snapshot_incomplete'
        }
    }

    $semantic = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($name in @(Get-GuiSmokeSemanticEvidenceEventNames)) { [void]$semantic.Add($name) }
    $source = [Collections.Generic.Dictionary[string,int]]::new([StringComparer]::Ordinal)
    $events = @($Index.AllEvents)
    for ($i = 0; $i -lt $events.Count; $i++) {
        if ($semantic.Contains([string]$events[$i].EventName)) {
            if ($source.ContainsKey([string]$events[$i].RawLine)) { throw 'second_transfer_source_duplicate' }
            $source.Add([string]$events[$i].RawLine, $i)
        }
    }
    $selected = [Collections.Generic.List[object]]::new()
    $ordinals = [Collections.Generic.HashSet[int]]::new()
    $previous = -1
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadLines($slicePath)) {
        $lineNumber++
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $parsed = ConvertFrom-FileTransferLogLine -Line $line -FilePath $slicePath -LineNumber $lineNumber -Sequence $lineNumber
        if ($null -eq $parsed -or -not $semantic.Contains([string]$parsed.EventName)) { continue }
        if (-not $source.ContainsKey($line) -or $source[$line] -le $previous) {
            throw 'second_transfer_source_membership_invalid'
        }
        $previous = $source[$line]
        [void]$ordinals.Add($previous)
        $selected.Add($events[$previous])
    }
    if ($selected.Count -eq 0) { throw 'second_transfer_scope_empty' }
    $firstOrdinal = $source[[string]$selected[0].RawLine]
    for ($i = $firstOrdinal; $i -le $previous; $i++) {
        if ($semantic.Contains([string]$events[$i].EventName) -and -not $ordinals.Contains($i)) {
            throw 'second_transfer_scope_gap'
        }
    }
    $starts = @($selected | Where-Object EventName -CEQ 'filetransfer_coordinator_snapshot_stream_started')
    if ($starts.Count -ne 2 -or @($starts | ForEach-Object { $_.Fields.direction } | Sort-Object -Unique).Count -ne 2 -or
        @($starts | ForEach-Object { $_.Fields.log_writer_id } | Sort-Object -Unique).Count -ne 2) {
        throw 'second_transfer_paired_scope_missing'
    }
    $session = [string]$starts[0].Fields.session_id
    $transfer = [string]$starts[0].Fields.transfer_id
    if ([string]::IsNullOrWhiteSpace($session) -or [string]::IsNullOrWhiteSpace($transfer) -or
        $session -match 'redacted|\(none\)' -or $transfer -match 'redacted|\(none\)' -or
        @($starts | Where-Object { $_.Fields.session_id -cne $session -or $_.Fields.transfer_id -cne $transfer }).Count -ne 0) {
        throw 'second_transfer_scope_identity_invalid'
    }
    $boundaries = @($Full.Boundaries | Where-Object { $_.sessionId -ceq $session -and $_.transferId -ceq $transfer })
    $writerEvents = @{}
    foreach ($event in $selected) {
        if (-not [string]::IsNullOrWhiteSpace([string]$event.Fields['log_writer_id'])) {
            $key = '{0}|{1}' -f $event.Fields['log_writer_id'], $event.Fields['log_writer_sequence']
            if ($writerEvents.ContainsKey($key)) { throw 'second_transfer_writer_duplicate' }
            $writerEvents[$key] = $event
        }
        if ($event.EventName -like 'filetransfer_coordinator_*' -and
            $event.Fields.ContainsKey('coordinator_instance_id') -and
            ($event.Fields.session_id -cne $session -or $event.Fields.transfer_id -cne $transfer)) {
            throw 'second_transfer_foreign_coordinator_scope'
        }
    }
    foreach ($boundary in $boundaries) {
        $key = '{0}|{1}' -f $boundary.writer, $boundary.writerSequence
        if (-not $writerEvents.ContainsKey($key) -or
            $writerEvents[$key].EventName -cne 'filetransfer_coordinator_transition_snapshot' -or
            $writerEvents[$key].Fields.coordinator_instance_id -cne $boundary.coordinatorInstanceId -or
            [int]$boundary.unresolvedCount -ne 0) { throw 'second_transfer_boundary_coverage_incomplete' }
    }
    $routes = @((ConvertFrom-Json $RouteState.EffectiveResult.NormalizedJson).routeSelections |
        Where-Object { $_.sessionId -ceq $session -and $_.transferId -ceq $transfer })
    if ($routes.Count -ne 2 -or @($routes | Where-Object {
                $_.route -cne 'file_tuna_v4' -or [int]$_.protocolVersion -ne 4 -or [long]$_.publicationGeneration -ne 1
            }).Count -ne 0) { throw 'second_transfer_route_publication_invalid' }
    $terminalProof = [Collections.Generic.List[object]]::new()
    $lastTerminalOrdinal = -1
    $dataPath = @{}
    $priorTransfers = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($direction in @('outbound', 'inbound')) {
        $start = @($starts | Where-Object { $_.Fields.direction -ceq $direction })
        $route = @($routes | Where-Object direction -CEQ $direction)
        $terminal = @($boundaries | Where-Object {
            $_.direction -ceq $direction -and $_.decisionKind -ceq 'Terminalized' -and $_.decisionStatus -ceq 'Accepted' -and
            [long]$_.afterBoundary.terminalGeneration -eq 1
        })
        $initial = @($boundaries | Where-Object {
            $_.direction -ceq $direction -and $_.decisionKind -ceq 'StartTransfer' -and $_.decisionStatus -ceq 'Accepted'
        })
        if ($start.Count -ne 1 -or $route.Count -ne 1 -or $initial.Count -ne 1 -or $terminal.Count -ne 1) {
            throw 'second_transfer_lifecycle_incomplete'
        }
        $writer = [string]$start[0].Fields.log_writer_id
        if ([string]::IsNullOrWhiteSpace($writer) -or $route[0].writer -cne $writer -or $terminal[0].writer -cne $writer -or
            @($boundaries | Where-Object { $_.boundaryId -ceq $route[0].boundaryId -and $_.boundaryHash -ceq $route[0].boundaryHash }).Count -ne 1) {
            throw 'second_transfer_publication_source_invalid'
        }
        $prior = @($Full.Boundaries | Where-Object {
            $_.sessionId -ceq $session -and $_.transferId -cne $transfer -and $_.writer -ceq $writer -and
            $_.direction -ceq $direction -and $_.decisionKind -ceq 'Terminalized' -and $_.decisionStatus -ceq 'Accepted' -and
            [long]$_.writerSequence -lt [long]$start[0].Fields.log_writer_sequence
        })
        if ($prior.Count -ne 1) { throw 'second_transfer_prior_lifecycle_missing' }
        [void]$priorTransfers.Add([string]$prior[0].transferId)
        $ends = @($selected | Where-Object {
            $_.EventName -ceq 'filetransfer_coordinator_snapshot_stream_completed' -and $_.Fields.log_writer_id -ceq $writer -and
            $_.Fields.coordinator_instance_id -ceq $start[0].Fields.coordinator_instance_id
        })
        $completed = @($selected | Where-Object {
            $_.EventName -ceq "file_transfer_${direction}_terminal" -and $_.Fields.log_writer_id -ceq $writer
        })
        $payload = @($selected | Where-Object {
            $_.EventName -ceq 'filetransfer_v4_efficiency_summary' -and $_.Fields.log_writer_id -ceq $writer -and $_.Fields.direction -ceq $direction
        })
        if ($ends.Count -ne 1 -or $payload.Count -ne 1 -or $completed.Count -ne 1 -or
            $completed[0].Fields.state -cne 'Completed' -or $completed[0].Fields.error_code -cne '(none)' -or
            [long]$completed[0].Fields.log_writer_sequence -le [long]$terminal[0].writerSequence) { throw 'second_transfer_terminal_evidence_missing' }
        $fields = $payload[0].Fields
        $lastTerminalOrdinal = [Math]::Max($lastTerminalOrdinal, $source[[string]$completed[0].RawLine])
        $chunks = if ($direction -ceq 'outbound') { [long]$fields.remote_frontier_chunk_index } else { [long]$fields.next_chunk_index }
        $terminalSummaryOrdered = [long]$fields.log_writer_sequence -gt [long]$terminal[0].writerSequence
        if (-not $terminalSummaryOrdered) {
            # Snapshot emission is queued: writer order need not be decision
            # order. Only the accepted, same-writer coordinator boundary can
            # establish that this metric followed terminalization.
            $summaryTicks = 0L
            $decisionMilliseconds = 0L
            if ($fields.ContainsKey('log_writer_attempt_utc_ticks') -and
                $terminal[0].PSObject.Properties.Name -contains 'decisionUtcMilliseconds' -and
                [long]::TryParse([string]$fields.log_writer_attempt_utc_ticks, [ref]$summaryTicks) -and
                [long]::TryParse([string]$terminal[0].decisionUtcMilliseconds, [ref]$decisionMilliseconds) -and
                $summaryTicks -gt 0 -and $summaryTicks -le [DateTime]::MaxValue.Ticks -and $decisionMilliseconds -gt 0) {
                $summaryMilliseconds = [DateTimeOffset]::new([DateTime]::new($summaryTicks, [DateTimeKind]::Utc)).ToUnixTimeMilliseconds()
                $terminalSummaryOrdered = $summaryMilliseconds -ge $decisionMilliseconds
            }
        }
        if ($fields.terminal_state -cne 'completed' -or
            $fields.route -cne 'file_tuna_v4' -or [int]$fields.protocol_version -ne 4 -or
            [long]$fields.file_size_bytes -le 0 -or [long]$fields.bytes_transferred -ne [long]$fields.file_size_bytes -or
            $chunks -le 0 -or
            -not $terminalSummaryOrdered -or
            [long]$fields.log_writer_sequence -le [long]$route[0].writerSequence -or
            [long]$fields.log_writer_sequence -ge [long]$completed[0].Fields.log_writer_sequence) {
            throw 'second_transfer_terminal_data_path_invalid'
        }
        $terminalProof.Add([pscustomobject]@{ direction=$direction; writer=$writer; boundaryId=$terminal[0].boundaryId;
            writerSequence=$fields.log_writer_sequence; bytes=[long]$fields.bytes_transferred; chunks=$chunks })
        $eventName = if ($direction -ceq 'outbound') { 'filetransfer_data_path_send_result' } else { 'filetransfer_data_path_receive_result' }
        $delivery = if ($direction -ceq 'outbound') { 'sent' } else { 'received' }
        $dataPath[$direction] = @($selected | Where-Object {
            $_.EventName -ceq $eventName -and $_.Fields.log_writer_id -ceq $writer -and $_.Fields.direction -ceq $direction -and
            $_.Fields.frame_type -in @('filetransfer.chunk.v4','filetransfer.chunk_batch.v4') -and
            $_.Fields.actual_transport -ceq 'tuna' -and $_.Fields.delivery_result -ceq $delivery -and [long]$_.Fields.raw_payload_bytes -gt 0 -and
            $_.Fields.data_path_session_id -ceq $session -and $_.Fields.data_path_transfer_id -ceq $transfer -and
            $_.Fields.publication_evidence_boundary_id -ceq $route[0].boundaryId -and
            $_.Fields.publication_evidence_operation_key -ceq $route[0].operationKey -and
            [long]$_.Fields.route_publication_generation -eq [long]$route[0].publicationGeneration -and
            $_.Fields.route -ceq 'file_tuna_v4' -and [int]$_.Fields.protocol_version -eq 4 -and
            [long]$_.Fields.log_writer_sequence -gt [long]$route[0].writerSequence -and
            [long]$_.Fields.log_writer_sequence -lt [long]$terminal[0].writerSequence
        })
        if ($dataPath[$direction].Count -eq 0) { throw 'second_transfer_positive_data_path_missing' }
    }
    if ($priorTransfers.Count -ne 1 -or $terminalProof[0].bytes -ne $terminalProof[1].bytes -or $terminalProof[0].chunks -ne $terminalProof[1].chunks) {
        throw 'second_transfer_terminal_pair_mismatch'
    }
    $sent = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($event in $dataPath.outbound) {
        if (-not [string]::IsNullOrWhiteSpace([string]$event.Fields.data_path_envelope_fingerprint) -and
            -not [string]::IsNullOrWhiteSpace([string]$event.Fields.security_payload_fingerprint)) {
            [void]$sent.Add(('{0}|{1}' -f $event.Fields.data_path_envelope_fingerprint, $event.Fields.security_payload_fingerprint))
        }
    }
    $pairedPayloads = @($dataPath.inbound | Where-Object {
        $_.Fields.receive_disposition -ceq 'dispatch_entered' -and
        $sent.Contains(('{0}|{1}' -f $_.Fields.data_path_envelope_fingerprint, $_.Fields.security_payload_fingerprint))
    })
    if ($pairedPayloads.Count -eq 0) { throw 'second_transfer_paired_data_path_missing' }
    $completion = $Gui.secondTransfer
    if ($Gui.routeMode -cne 'live-reactivation-second-transfer' -or -not $completion.completed -or -not $completion.integrityOk -or
        $completion.inboundState -cne 'Completed' -or $completion.outboundState -cne 'Completed' -or
        $completion.inboundErrorCode -cne '(none)' -or $completion.outboundErrorCode -cne '(none)' -or
        [long]$completion.payloadBytes -ne $terminalProof[0].bytes -or [long]$completion.savedFileSizeBytes -ne $terminalProof[0].bytes -or
        [string]$completion.expectedSha256 -cnotmatch '^[0-9a-f]{64}$' -or $completion.expectedSha256 -cne $completion.receivedSha256) {
        throw 'second_transfer_integrity_evidence_invalid'
    }

    # Reuse only observational warning policy, not the stabilization/authority gate.
    $summary = New-FileTransferRetainedSummary -Events $selected.ToArray() -LogFiles @($slicePath) -AllTransfers
    $groups = @(
        @{ Kind='recovered_post_tuna_fallback_bridge_clear'; Function='Get-FileTransferPostTunaFallbackBridgeClearWarningEvents' },
        @{ Kind='recovered_runtime_unlock_bridge_clear'; Function='Get-FileTransferRuntimeUnlockBridgeClearWarningEvents' },
        @{ Kind='recovered_regular_v4_bridge_clear'; Function='Get-FileTransferRegularNknV4BridgeClearWarningEvents' },
        @{ Kind='fallback_v6_send_timeout_churn'; Function='Get-FileTransferPostTunaFallbackSendTimeoutWarningEvents' },
        @{ Kind='fallback_frontier_repair_churn'; Function='Get-FileTransferPostTunaFallbackFrontierRepairWarningEvents' },
        @{ Kind='fallback_receiver_state_churn'; Function='Get-FileTransferPostTunaFallbackReceiverStateWarningEvents' }
    ) | ForEach-Object { [pscustomobject]@{ Kind=$_.Kind; Events=@(& $_.Function -Summary $summary) } }
    $external = @(Get-FileTransferExternalTransportWarningEvents -Summary $summary)
    $groups += [pscustomobject]@{ Kind='external_transport_churn'; Events=@(Get-FileTransferExternalTransportCapWarningEvents -Events $external) }
    $cap = Get-FileTransferWarningCapResult -Summary $summary -WarningGroups $groups
    $warnings = @($groups | Where-Object { $_.Events.Count -gt 0 } | ForEach-Object Kind)
    if ($external.Count -gt 0 -and $warnings -notcontains 'external_transport_churn') { $warnings += 'external_transport_churn' }
    if (@(Get-FileTransferCohabitationWarningEvents -Summary $summary).Count -gt 0) { $warnings += 'cohabitation_pressure' }
    if (@(Get-FileTransferRecoveredPressureWarningEvents -Summary $summary).Count -gt 0) { $warnings += 'recovered_pressure' }
    $operatorVerdict = if ($cap.ExceededKinds.Count -gt 0) { 'FAIL_EXTERNAL_TRANSPORT_CHURN' } elseif ($warnings.Count -gt 0) { 'WARN_EXTERNAL_TRANSPORT' } else { 'PASS' }
    New-Item -ItemType Directory -Path $outputDir -ErrorAction Stop | Out-Null
    $routeLines = @('route_consistency_verdict=pass', 'selected_route_changes=file_tuna_v4', 'route_selected_count=2')
    for ($i = 0; $i -lt $routes.Count; $i++) {
        $routeLines += "selected.$($i+1).route=$($routes[$i].route)", "selected.$($i+1).protocol_version=$($routes[$i].protocolVersion)", "selected.$($i+1).direction=$($routes[$i].direction)"
    }
    $routeLines | Set-Content -LiteralPath (Join-Path $outputDir 'filetransfer-route-consistency-summary.txt') -Encoding UTF8
    @("verdict=$operatorVerdict", 'hard_failure_count=0', "warning_kinds=$(if ($warnings.Count) { $warnings -join ',' } else { '(none)' })",
        "warning_cap_exceeded_kinds=$($cap.ExceededKindsText)", "warning_kind_counts=$($cap.KindCounts)", "parent_verdict=$ParentVerdict") |
        Set-Content -LiteralPath (Join-Path $outputDir 'filetransfer-operator-verdict.txt') -Encoding UTF8
    $repair = @()
    foreach ($item in @(@('v4_repair_requested_count','filetransfer_v4_repair_requested'),
            @('v4_missing_range_repair_scheduled_count','filetransfer_v4_repair_scheduled'),
            @('v4_missing_range_repair_sent_count','filetransfer_v4_repair_sent'))) {
        $repair += '{0}={1}' -f $item[0], @($selected | Where-Object EventName -CEQ $item[1]).Count
    }
    foreach ($item in @(@('control_bulk','repair_delivery_mode','control_bulk_escalated'),
            @('credit_stall','repair_delivery_escalation_reason','credit_stall'),
            @('frontier_not_advanced','repair_delivery_escalation_reason','frontier_not_advanced'))) {
        $repair += 'v4_repair_delivery_{0}_escalated_count={1}' -f $item[0], @($selected | Where-Object {
            $_.EventName -ceq 'filetransfer_v4_repair_sent' -and $_.Fields[$item[1]] -ceq $item[2]
        }).Count
    }
    $repair | Set-Content -LiteralPath (Join-Path $outputDir 'repair-reorder-summary.txt') -Encoding UTF8
    $recoveryCount = @($selected | Where-Object {
        $source[[string]$_.RawLine] -le $lastTerminalOrdinal -and $_.EventName -in @(
            'nkn_bridge_receive_stall_detected', 'nkn_bridge_receive_stall_recovery_started',
            'nkn_bridge_receive_stall_recovery_completed', 'nkn_bridge_receive_stall_recovery_receive_resumed')
    }).Count
    @("measured_window_bridge_recovery_count=$recoveryCount", "first_source_ordinal=$firstOrdinal", "last_terminal_source_ordinal=$lastTerminalOrdinal") |
        Set-Content -LiteralPath (Join-Path $outputDir 'measurement-summary.txt') -Encoding UTF8
    $selected | ForEach-Object RawLine | Set-Content -LiteralPath (Join-Path $outputDir 'filetransfer-retained-log-slice.log') -Encoding UTF8
    $result = [ordered]@{ schemaVersion=1; owner='RouteAcceptance'; attemptId=$Request.attemptId;
        acceptanceRunId=$Request.acceptanceRunId; scenarioRunId=$Request.scenarioRunId;
        inputSha256=$Request.inputSha256; sliceSha256=$Request.sliceSha256; guiSha256=$Request.guiSha256; sessionId=$session; transferId=$transfer;
        firstSourceOrdinal=$firstOrdinal; lastSourceOrdinal=$previous; sourceEventCount=$selected.Count;
        parentBoundaryHash=$Full.BoundaryHash; parentVerdict=$ParentVerdict; parentAccepted=$true;
        routes=$routes; terminals=$terminalProof.ToArray(); pairedPayloadCount=$pairedPayloads.Count; payloadSha256=$completion.expectedSha256;
        warningCap=$cap; operatorVerdict=$operatorVerdict; artifacts=@() }
    foreach ($name in @('filetransfer-route-consistency-summary.txt','filetransfer-operator-verdict.txt','repair-reorder-summary.txt','measurement-summary.txt','filetransfer-retained-log-slice.log')) {
        $result.artifacts += [pscustomobject]@{ name=$name; sha256=(Get-FileTransferSecondReportSha256 (Join-Path $outputDir $name)) }
    }
    # Written last: partial output has no completion binding and is not reusable.
    $result | ConvertTo-Json -Depth 24 | Set-Content -LiteralPath (Join-Path $outputDir 'primary-derived-report.json') -Encoding UTF8
}

function Test-FileTransferSecondTransferReport {
    param([Parameter(Mandatory = $true)][string]$ArtifactDir, [Parameter(Mandatory = $true)]$Receipt)
    try {
        $path = Join-Path $ArtifactDir 'second-transfer-analysis/primary-derived-report.json'
        if ([string]$Receipt.secondTransferReportSha256 -cne (Get-FileTransferSecondReportSha256 $path)) { return $false }
        $report = Get-Content -LiteralPath $path -Raw -ErrorAction Stop | ConvertFrom-Json
        if ([int]$report.schemaVersion -ne 1 -or $report.owner -cne 'RouteAcceptance' -or -not $report.parentAccepted -or
            $report.parentVerdict -notin @('PASS','WARN_EXTERNAL_TRANSPORT') -or
            $report.attemptId -cne $Receipt.attemptId -or $report.acceptanceRunId -cne $Receipt.acceptanceRunId -or
            $report.scenarioRunId -cne $Receipt.scenarioRunId -or $report.inputSha256 -ine $Receipt.analyzerInputSha256After -or
            $report.parentBoundaryHash -cne $Receipt.coordinatorSnapshotBoundaryHash -or
            $report.guiSha256 -cne (Get-FileTransferSecondReportSha256 (Join-Path $ArtifactDir 'filetransfer-tuna-gui-summary.json')) -or
            $report.sliceSha256 -cne (Get-FileTransferSecondReportSha256 (Join-Path $ArtifactDir 'filetransfer-second-transfer-retained-log-slice.log'))) { return $false }
        $names = @('filetransfer-route-consistency-summary.txt','filetransfer-operator-verdict.txt','repair-reorder-summary.txt','measurement-summary.txt','filetransfer-retained-log-slice.log')
        if (@($report.artifacts).Count -ne $names.Count) { return $false }
        foreach ($name in $names) {
            $binding = @($report.artifacts | Where-Object name -CEQ $name)
            if ($binding.Count -ne 1 -or [string]$binding[0].sha256 -cne
                (Get-FileTransferSecondReportSha256 (Join-Path (Split-Path -Parent $path) $name))) { return $false }
        }
        return $true
    }
    catch { return $false }
}
