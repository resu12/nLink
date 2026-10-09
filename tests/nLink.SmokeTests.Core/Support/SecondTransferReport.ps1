param([string]$RepoRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
# These are modeled *evaluated views* for the derived-report contract, not live
# authority evidence. The publisher must not evaluate these or any source again.
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'unexpected_authority_evaluation' }
$root = Join-Path $RepoRoot ('artifacts/second-transfer-report-implementation-20260924/tests/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID = 'report-fixture-run'
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID = 'report-fixture-run:second-transfer-after-reactivation:1'

function New-Fixture([string]$Name, [switch]$DelayedTerminalSnapshot) {
    $dir = Join-Path $root $Name
    New-Item -ItemType Directory -Path $dir | Out-Null
    $lines = [Collections.Generic.List[string]]::new()
    $second = [Collections.Generic.List[string]]::new()
    $boundaries = @()
    $routes = @()
    foreach ($transfer in @('first', 'second')) {
        foreach ($direction in @('outbound', 'inbound')) {
            $writer = 'writer-' + $direction
            $instance = "$transfer-$direction"
            $seq = if ($transfer -ceq 'first') { 1 } else { 101 }
            $summaryWritten = $false
            $scope = "session_id=session; transfer_id=$transfer; direction=$direction; coordinator_instance_id=$instance; log_writer_id=$writer"
            $part = @("event=filetransfer_coordinator_snapshot_stream_started; $scope; log_writer_sequence=$seq")
            foreach ($kind in @('StartTransfer','RoutePublication','Terminalized')) {
                if ($kind -ceq 'Terminalized') {
                    $seq++
                    $eventName = if ($direction -ceq 'outbound') {'filetransfer_data_path_send_result'} else {'filetransfer_data_path_receive_result'}
                    $delivery = if ($direction -ceq 'outbound') {'sent'} else {'received'}
                    $part += "event=$eventName; $scope; log_writer_sequence=$seq; frame_type=filetransfer.chunk_batch.v4; actual_transport=tuna; delivery_result=$delivery; raw_payload_bytes=64512; data_path_session_id=session; data_path_transfer_id=$transfer; publication_evidence_boundary_id=$instance-RoutePublication; publication_evidence_operation_key=op-$instance; route_publication_generation=1; route=file_tuna_v4; protocol_version=4; data_path_envelope_fingerprint=envelope-$transfer; security_payload_fingerprint=payload-$transfer; receive_disposition=dispatch_entered"
                    if ($DelayedTerminalSnapshot -and $transfer -ceq 'second') {
                        $ticks = [DateTimeOffset]::FromUnixTimeMilliseconds(1700000000001).UtcDateTime.Ticks
                        $seq++
                        $part += "event=filetransfer_v4_efficiency_summary; session_id=sess_[redacted]; transfer_id=[redacted]; direction=$direction; log_writer_id=$writer; log_writer_sequence=$seq; log_writer_attempt_utc_ticks=$ticks; file_size_bytes=16777216; bytes_transferred=16777216; remote_frontier_chunk_index=781; next_chunk_index=781; terminal_state=completed; route=file_tuna_v4; protocol_version=4"
                        $summaryWritten = $true
                    }
                }
                $seq++
                $part += "event=filetransfer_coordinator_transition_snapshot; $scope; log_writer_sequence=$seq"
                $id = "$instance-$kind"
                $boundaries += [pscustomobject]@{ sessionId='session'; transferId=$transfer; direction=$direction; writer=$writer;
                    coordinatorInstanceId=$instance; writerSequence=$seq; boundaryId=$id; boundaryHash="hash-$id";
                    decisionKind=$kind; decisionStatus='Accepted'; decisionUtcMilliseconds=1700000000000L; unresolvedCount=0; afterBoundary=[pscustomobject]@{terminalGeneration=$(if ($kind -ceq 'Terminalized') {1} else {0})} }
                if ($kind -ceq 'RoutePublication') {
                    $routes += [pscustomobject]@{ sessionId='session'; transferId=$transfer; direction=$direction; writer=$writer; writerSequence=$seq;
                        boundaryId=$id; boundaryHash="hash-$id"; route='file_tuna_v4'; protocolVersion=4; publicationGeneration=1; operationKey="op-$instance" }
                }
            }
            $seq++
            $part += "event=filetransfer_coordinator_snapshot_stream_completed; $scope; log_writer_sequence=$seq"
            if (-not $summaryWritten) {
                $seq++
                $part += "event=filetransfer_v4_efficiency_summary; session_id=sess_[redacted]; transfer_id=[redacted]; direction=$direction; log_writer_id=$writer; log_writer_sequence=$seq; file_size_bytes=16777216; bytes_transferred=16777216; remote_frontier_chunk_index=781; next_chunk_index=781; terminal_state=completed; route=file_tuna_v4; protocol_version=4"
            }
            $seq++
            $part += "event=file_transfer_${direction}_terminal; session_id=sess_[redacted]; transfer_id=[redacted]; log_writer_id=$writer; log_writer_sequence=$seq; state=Completed; error_code=(none)"
            foreach ($line in $part) { $lines.Add($line); if ($transfer -ceq 'second') { $second.Add($line) } }
        }
    }
    # A first-transfer-only warning must not become a second-transfer warning.
    $lines.Insert(0, 'event=filetransfer_v6_receiver_state_deferred; log_writer_id=writer-inbound; log_writer_sequence=0; transfer_id=[redacted]')
    # Raw diagnostics absent from semantic primary capture are allowed by the same catalog.
    $second.Add('event=screenshare_bridge_event_loop_summary; diagnostic_only=1')
    $input = Join-Path $dir 'filetransfer-retained-log-slice.log'
    $slice = Join-Path $dir 'filetransfer-second-transfer-retained-log-slice.log'
    $lines | Set-Content -LiteralPath $input -Encoding UTF8
    $second | Set-Content -LiteralPath $slice -Encoding UTF8
    $events = @(for ($i=0; $i -lt $lines.Count; $i++) {
        ConvertFrom-FileTransferLogLine -Line $lines[$i] -FilePath $input -LineNumber ($i+1) -Sequence ($i+1)
    })
    $full = [pscustomobject]@{ Mode='SnapshotV1Authoritative'; StreamComplete=$true; ArtifactEvidenceVerdict='pass';
        FirstUnresolvedBoundary=$null; BoundaryHash='parent-boundary-hash'; Boundaries=$boundaries }
    $gui = [pscustomobject]@{routeMode='live-reactivation-second-transfer'; secondTransfer=[pscustomobject]@{
        completed=$true; integrityOk=$true; inboundState='Completed'; outboundState='Completed'; inboundErrorCode='(none)'; outboundErrorCode='(none)';
        payloadBytes=16777216; savedFileSizeBytes=16777216; expectedSha256=('a'*64); receivedSha256=('a'*64) }}
    $gui | ConvertTo-Json | Set-Content (Join-Path $dir 'filetransfer-tuna-gui-summary.json') -Encoding UTF8
    $request = [pscustomobject]@{ schemaVersion=1; attemptId=[guid]::NewGuid().ToString('N'); acceptanceRunId=$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID;
        scenarioRunId=$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID; artifactDir=$dir;
        inputSha256=(Get-FileHash $input).Hash.ToLowerInvariant(); sliceSha256=(Get-FileHash $slice).Hash.ToLowerInvariant();
        guiSha256=(Get-FileHash (Join-Path $dir 'filetransfer-tuna-gui-summary.json')).Hash.ToLowerInvariant() }
    return @{ ArtifactDir=$dir; Request=$request; Index=[pscustomobject]@{AllEvents=$events}; Full=$full; Gui=$gui;
        Compact=($full | ConvertTo-Json -Depth 20 | ConvertFrom-Json); Parity=[pscustomobject]@{Verdict='pass'};
        RouteState=[pscustomobject]@{Parity=[pscustomobject]@{Verdict='pass'};
            EffectiveResult=[pscustomobject]@{Verdict='pass'; NormalizedJson=(@{routeSelections=$routes} | ConvertTo-Json -Depth 12 -Compress)}};
        ParentVerdict='WARN_EXTERNAL_TRANSPORT'; ParentGateStatus='warn'; ParentHardFailures=0; ParentProofFailures=0; ParentWarningCapExceeded='(none)' }
}

$good = New-Fixture 'good'
$before = $good | ConvertTo-Json -Depth 40 -Compress
Write-FileTransferSecondTransferReport @good
if (($good | ConvertTo-Json -Depth 40 -Compress) -cne $before) { throw 'publisher_mutated_parent_views' }
$reportPath = Join-Path $good.ArtifactDir 'second-transfer-analysis/primary-derived-report.json'
$report = Get-Content $reportPath -Raw | ConvertFrom-Json
if ($report.transferId -cne 'second' -or $report.routes.Count -ne 2 -or $report.terminals.Count -ne 2 -or $report.operatorVerdict -cne 'PASS') {
    throw 'incorrect_second_report'
}
$receipt = [pscustomobject]@{ secondTransferReportSha256=(Get-FileHash $reportPath).Hash.ToLowerInvariant();
    attemptId=$good.Request.attemptId; acceptanceRunId=$good.Request.acceptanceRunId; scenarioRunId=$good.Request.scenarioRunId;
    analyzerInputSha256After=$good.Request.inputSha256; coordinatorSnapshotBoundaryHash=$good.Full.BoundaryHash }
if (-not (Test-FileTransferSecondTransferReport -ArtifactDir $good.ArtifactDir -Receipt $receipt)) { throw 'valid_report_rejected' }
foreach ($field in @('attemptId','acceptanceRunId','scenarioRunId','analyzerInputSha256After','coordinatorSnapshotBoundaryHash','secondTransferReportSha256')) {
    $badReceipt = $receipt | ConvertTo-Json | ConvertFrom-Json
    $badReceipt.$field = 'wrong'
    if (Test-FileTransferSecondTransferReport -ArtifactDir $good.ArtifactDir -Receipt $badReceipt) { throw "receipt_binding_accepted:$field" }
}
foreach ($name in @('filetransfer-route-consistency-summary.txt','filetransfer-operator-verdict.txt','repair-reorder-summary.txt','measurement-summary.txt','filetransfer-retained-log-slice.log')) {
    $path = Join-Path $good.ArtifactDir "second-transfer-analysis/$name"
    $bytes = [IO.File]::ReadAllBytes($path)
    [IO.File]::WriteAllText($path, 'tampered')
    if (Test-FileTransferSecondTransferReport -ArtifactDir $good.ArtifactDir -Receipt $receipt) { throw "tampered_output_accepted:$name" }
    [IO.File]::WriteAllBytes($path, $bytes)
}
try { Write-FileTransferSecondTransferReport @good; throw 'duplicate_publication_accepted' }
catch { if ($_.Exception.Message -cne 'second_transfer_report_already_exists') { throw } }

$cases = @('wrong_run','wrong_scenario','wrong_input','wrong_slice','wrong_directory','parent_failure','shared_failure','hard_failure',
    'warning_cap','parity','route_parity','route_failure','incomplete_stream','snapshot_failure','unresolved','missing_direction',
    'missing_terminal','missing_boundary','wrong_session','first_overlap','redacted_identity','wrong_protocol','fallback','tuna_v6',
    'missing_publication','wrong_publication_source','reordered','changed','foreign','gap','duplicate','terminal_bytes','terminal_route','missing_prior',
    'missing_send','missing_receive','unpaired_payload','bad_sha','bad_size','incomplete_gui','wrong_gui_hash',
    'delayed_before_decision','delayed_missing_time','delayed_invalid_time','delayed_missing_decision_time','delayed_rejected_terminal',
    'delayed_missing_send','delayed_unpaired_payload','delayed_bad_sha')
foreach ($case in $cases) {
    $f = New-Fixture $case -DelayedTerminalSnapshot:($case -like 'delayed_*')
    $slice = Join-Path $f.ArtifactDir 'filetransfer-second-transfer-retained-log-slice.log'
    $sliceLines = [IO.File]::ReadAllLines($slice)
    $updateSlice = $false
    switch ($case) {
        'delayed_before_decision' { $ticks=[DateTimeOffset]::FromUnixTimeMilliseconds(1699999999999).UtcDateTime.Ticks; $sliceLines=$sliceLines -replace 'log_writer_attempt_utc_ticks=\d+',"log_writer_attempt_utc_ticks=$ticks"; $updateSlice=$true }
        'delayed_missing_time' { $sliceLines=$sliceLines -replace '; log_writer_attempt_utc_ticks=\d+',''; $updateSlice=$true }
        'delayed_invalid_time' { $sliceLines=$sliceLines -replace 'log_writer_attempt_utc_ticks=\d+','log_writer_attempt_utc_ticks=9223372036854775807'; $updateSlice=$true }
        'delayed_missing_decision_time' { $f.Full.Boundaries | Where-Object { $_.transferId -ceq 'second' -and $_.decisionKind -ceq 'Terminalized' } | ForEach-Object { $_.PSObject.Properties.Remove('decisionUtcMilliseconds') } }
        'delayed_rejected_terminal' { $f.Full.Boundaries | Where-Object { $_.transferId -ceq 'second' -and $_.decisionKind -ceq 'Terminalized' } | ForEach-Object { $_.decisionStatus='Rejected' } }
        'delayed_missing_send' { $sliceLines=@($sliceLines | Where-Object { $_ -notmatch 'event=filetransfer_data_path_send_result' }); $updateSlice=$true }
        'delayed_unpaired_payload' { $sliceLines=@($sliceLines | ForEach-Object { if ($_ -match 'event=filetransfer_data_path_receive_result') { $_ -replace 'security_payload_fingerprint=payload-second','security_payload_fingerprint=foreign' } else { $_ } }); $updateSlice=$true }
        'delayed_bad_sha' { $f.Gui.secondTransfer.receivedSha256='b'*64 }
        'wrong_run' { $f.Request.acceptanceRunId='wrong' }
        'wrong_scenario' { $f.Request.scenarioRunId='wrong' }
        'wrong_input' { $f.Request.inputSha256='wrong' }
        'wrong_slice' { $f.Request.sliceSha256='wrong' }
        'wrong_directory' { $f.Request.artifactDir=$root }
        'parent_failure' { $f.ParentVerdict='FAIL_PROTOCOL_OR_INTEGRITY'; $f.ParentGateStatus='fail' }
        'shared_failure' { $f.ParentProofFailures=1 }
        'hard_failure' { $f.ParentHardFailures=1 }
        'warning_cap' { $f.ParentWarningCapExceeded='external_transport_churn' }
        'parity' { $f.Parity.Verdict='fail' }
        'route_parity' { $f.RouteState.Parity.Verdict='fail' }
        'route_failure' { $f.RouteState.EffectiveResult.Verdict='fail' }
        'incomplete_stream' { $f.Full.StreamComplete=$false }
        'snapshot_failure' { $f.Compact.ArtifactEvidenceVerdict='fail' }
        'unresolved' { $f.Full.FirstUnresolvedBoundary=[pscustomobject]@{unresolvedCount=1} }
        'missing_boundary' { $f.Full.Boundaries[6].writerSequence=999 }
        'missing_prior' { $f.Full.Boundaries=@($f.Full.Boundaries | Where-Object transferId -CEQ 'second') }
        'missing_send' { $sliceLines=@($sliceLines | Where-Object { $_ -notmatch 'event=filetransfer_data_path_send_result' }); $updateSlice=$true }
        'missing_receive' { $sliceLines=@($sliceLines | Where-Object { $_ -notmatch 'event=filetransfer_data_path_receive_result' }); $updateSlice=$true }
        'unpaired_payload' { $sliceLines=@($sliceLines | ForEach-Object { if ($_ -match 'event=filetransfer_data_path_receive_result') { $_ -replace 'security_payload_fingerprint=payload-second','security_payload_fingerprint=foreign' } else { $_ } }); $updateSlice=$true }
        'bad_sha' { $f.Gui.secondTransfer.receivedSha256='b'*64 }
        'bad_size' { $f.Gui.secondTransfer.savedFileSizeBytes=1 }
        'incomplete_gui' { $f.Gui.secondTransfer.completed=$false }
        'wrong_gui_hash' { $f.Request.guiSha256='wrong' }
        'wrong_protocol' { $j=$f.RouteState.EffectiveResult.NormalizedJson | ConvertFrom-Json; $j.routeSelections[2].protocolVersion=6; $f.RouteState.EffectiveResult.NormalizedJson=$j | ConvertTo-Json -Depth 12 }
        'fallback' { $j=$f.RouteState.EffectiveResult.NormalizedJson | ConvertFrom-Json; $j.routeSelections[2].route='post_tuna_fallback_v6'; $f.RouteState.EffectiveResult.NormalizedJson=$j | ConvertTo-Json -Depth 12 }
        'tuna_v6' { $j=$f.RouteState.EffectiveResult.NormalizedJson | ConvertFrom-Json; $j.routeSelections[2].route='file_tuna_v6'; $f.RouteState.EffectiveResult.NormalizedJson=$j | ConvertTo-Json -Depth 12 }
        'missing_publication' { $j=$f.RouteState.EffectiveResult.NormalizedJson | ConvertFrom-Json; $j.routeSelections=@($j.routeSelections | Select-Object -First 3); $f.RouteState.EffectiveResult.NormalizedJson=$j | ConvertTo-Json -Depth 12 }
        'wrong_publication_source' { $j=$f.RouteState.EffectiveResult.NormalizedJson | ConvertFrom-Json; $j.routeSelections[2].boundaryHash='wrong'; $f.RouteState.EffectiveResult.NormalizedJson=$j | ConvertTo-Json -Depth 12 }
        'missing_direction' { $sliceLines=@($sliceLines | Where-Object { $_ -notmatch 'inbound' }); $updateSlice=$true }
        'missing_terminal' { $sliceLines=@($sliceLines | Where-Object { $_ -notmatch 'event=file_transfer_.*_terminal' }); $updateSlice=$true }
        'wrong_session' { $sliceLines=$sliceLines -replace 'session_id=session','session_id=foreign'; $updateSlice=$true }
        'first_overlap' { $sliceLines=@($f.Index.AllEvents | ForEach-Object RawLine); $updateSlice=$true }
        'redacted_identity' { $sliceLines=$sliceLines -replace 'transfer_id=second','transfer_id=[redacted]'; $updateSlice=$true }
        'reordered' { $tmp=$sliceLines[0]; $sliceLines[0]=$sliceLines[1]; $sliceLines[1]=$tmp; $updateSlice=$true }
        'changed' { $sliceLines[0]+=' '; $updateSlice=$true }
        'foreign' { $sliceLines += 'event=file_transfer_inbound_terminal; foreign=1'; $updateSlice=$true }
        'gap' { $sliceLines=@($sliceLines | Where-Object { $_ -notmatch 'log_writer_sequence=103$' }); $updateSlice=$true }
        'duplicate' { $sliceLines += $sliceLines[0]; $updateSlice=$true }
        'terminal_bytes' { $sliceLines=$sliceLines -replace 'bytes_transferred=16777216','bytes_transferred=1'; $updateSlice=$true }
        'terminal_route' { $sliceLines=$sliceLines -replace 'route=file_tuna_v4','route=file_tuna_v6'; $updateSlice=$true }
    }
    if ($updateSlice) { $sliceLines | Set-Content $slice -Encoding UTF8; $f.Request.sliceSha256=(Get-FileHash $slice).Hash.ToLowerInvariant() }
    # Some controls preserve source membership so they reach the semantic proof
    # checks rather than being rejected only for a different raw line.
    if ($case -like 'delayed_*' -or $case -in @('wrong_session','redacted_identity','missing_terminal','missing_direction','terminal_bytes','terminal_route','missing_send','missing_receive','unpaired_payload')) {
        $start = @($f.Index.AllEvents | Where-Object { $_.EventName -ceq 'filetransfer_coordinator_snapshot_stream_started' -and $_.Fields.transfer_id -ceq 'second' })[0]
        $newLines = @($f.Index.AllEvents | Where-Object { $_.Sequence -lt $start.Sequence } | ForEach-Object RawLine) + $sliceLines
        $inputPath = Join-Path $f.ArtifactDir 'filetransfer-retained-log-slice.log'
        $newLines | Set-Content $inputPath -Encoding UTF8
        $f.Request.inputSha256=(Get-FileHash $inputPath).Hash.ToLowerInvariant()
        $f.Index.AllEvents=@(for ($i=0;$i -lt $newLines.Count;$i++) {
            ConvertFrom-FileTransferLogLine -Line $newLines[$i] -FilePath $inputPath -LineNumber ($i+1) -Sequence ($i+1)
        })
    }
    $rejected = $false
    try { Write-FileTransferSecondTransferReport @f }
    catch {
        if ($_.Exception.Message -notlike 'second_transfer_*') { throw }
        if ($case -like 'delayed_*') {
            $expected = switch ($case) {
                'delayed_rejected_terminal' { 'second_transfer_lifecycle_incomplete' }
                'delayed_missing_send' { 'second_transfer_positive_data_path_missing' }
                'delayed_unpaired_payload' { 'second_transfer_paired_data_path_missing' }
                'delayed_bad_sha' { 'second_transfer_integrity_evidence_invalid' }
                default { 'second_transfer_terminal_data_path_invalid' }
            }
            if ($_.Exception.Message -cne $expected) { throw }
        }
        $rejected=$true
    }
    if (-not $rejected -or (Test-Path (Join-Path $f.ArtifactDir 'second-transfer-analysis/primary-derived-report.json'))) { throw "negative_control_accepted:$case" }
}

# Positive measured-context control, using the same source window and receipt
# binding; post-window recovery must not be counted as active-transfer recovery.
$measured = New-Fixture 'measured'
$inputPath = Join-Path $measured.ArtifactDir 'filetransfer-retained-log-slice.log'
$slicePath = Join-Path $measured.ArtifactDir 'filetransfer-second-transfer-retained-log-slice.log'
$observations = @('event=filetransfer_v4_repair_sent; repair_delivery_mode=control_bulk_escalated; repair_delivery_escalation_reason=credit_stall',
    'event=nkn_bridge_receive_stall_recovery_completed; reason=fixture_measured')
$newLines = [Collections.Generic.List[string]]::new()
$sliceLines = [Collections.Generic.List[string]]::new()
$inside = $false
foreach ($event in $measured.Index.AllEvents) {
    if ($event.EventName -ceq 'filetransfer_coordinator_snapshot_stream_started' -and $event.Fields.transfer_id -ceq 'second' -and -not $inside) {
        $inside=$true
        foreach ($line in $observations) { $newLines.Add($line); $sliceLines.Add($line) }
    }
    $newLines.Add($event.RawLine)
    if ($inside) { $sliceLines.Add($event.RawLine) }
}
$post = 'event=nkn_bridge_receive_stall_recovery_completed; reason=fixture_after_terminal'
$newLines.Add($post); $sliceLines.Add($post)
$newLines | Set-Content $inputPath -Encoding UTF8
$sliceLines | Set-Content $slicePath -Encoding UTF8
$measured.Request.inputSha256=(Get-FileHash $inputPath).Hash.ToLowerInvariant()
$measured.Request.sliceSha256=(Get-FileHash $slicePath).Hash.ToLowerInvariant()
$measured.Index.AllEvents=@(for ($i=0;$i -lt $newLines.Count;$i++) {
    ConvertFrom-FileTransferLogLine -Line $newLines[$i] -FilePath $inputPath -LineNumber ($i+1) -Sequence ($i+1)
})
Write-FileTransferSecondTransferReport @measured
$repairText = Get-Content (Join-Path $measured.ArtifactDir 'second-transfer-analysis/repair-reorder-summary.txt') -Raw
foreach ($expected in @('v4_missing_range_repair_sent_count=1','v4_repair_delivery_control_bulk_escalated_count=1','v4_repair_delivery_credit_stall_escalated_count=1')) {
    if (-not $repairText.Contains($expected)) { throw "measured_repair_missing:$expected" }
}
if ((Get-Content (Join-Path $measured.ArtifactDir 'second-transfer-analysis/measurement-summary.txt') -Raw) -notmatch 'measured_window_bridge_recovery_count=1') {
    throw 'measured_recovery_window_incorrect'
}
Write-Output "second-transfer-report-ok: $($cases.Count) rejection controls; receipt bindings; unchanged parent; zero evaluator calls"
