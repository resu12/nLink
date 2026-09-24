param([string]$RepoRoot, [string]$Directory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
. (Join-Path $RepoRoot 'tools/GuiSmokeEvidenceCatalog.ps1')
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID = 'fresh-report-run'
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID = 'fresh-report-run:second-transfer-after-reactivation:1'
$all = [Collections.Generic.List[string]]::new()
$second = [Collections.Generic.List[string]]::new()
$sequences = @{outbound=0;inbound=0}
foreach ($transfer in @('first','second')) {
    foreach ($direction in @('outbound','inbound')) {
        $part = [Collections.Generic.List[string]]::new()
        $publication = $null
        foreach ($line in [IO.File]::ReadAllLines((Join-Path $Directory "$transfer-$direction.log"))) {
            $parsed = ConvertFrom-FileTransferLogLine -Line $line -FilePath 'fresh' -LineNumber 1 -Sequence 1
            if ($parsed.EventName -ceq 'filetransfer_coordinator_transition_snapshot') {
                $envelope = (ConvertFrom-FileTransferCoordinatorSnapshotPayload -Payload $parsed.Fields.snapshot_payload).Value
                if ($envelope.decisionKind -ceq 'RoutePublication') { $publication=$envelope }
                if ($envelope.decisionKind -ceq 'Terminalized') {
                    if ($null -eq $publication) { throw 'fixture_publication_missing' }
                    $operation = @($publication.aggregate.operations | Where-Object operationRole -CEQ 'route_publication')[0]
                    $eventName = if ($direction -ceq 'outbound') {'filetransfer_data_path_send_result'} else {'filetransfer_data_path_receive_result'}
                    $delivery = if ($direction -ceq 'outbound') {'sent'} else {'received'}
                    $sequences[$direction]++
                    # Modeled observational delivery, keyed to the real projected publication.
                    $part.Add("event=$eventName; direction=$direction; log_writer_id=writer-$direction; log_writer_sequence=$($sequences[$direction]); frame_type=filetransfer.chunk_batch.v4; actual_transport=tuna; delivery_result=$delivery; raw_payload_bytes=64512; data_path_session_id=$($publication.sessionId); data_path_transfer_id=$($publication.transferId); publication_evidence_boundary_id=$($publication.transitionId); publication_evidence_operation_key=$($operation.operationKey); route_publication_generation=1; route=file_tuna_v4; protocol_version=4; data_path_envelope_fingerprint=envelope-$transfer; security_payload_fingerprint=payload-$transfer; receive_disposition=dispatch_entered")
                }
            }
            $sequences[$direction]++
            $part.Add("$line; log_writer_id=writer-$direction; log_writer_sequence=$($sequences[$direction])")
        }
        $sequences[$direction]++
        $part.Add("event=filetransfer_v4_efficiency_summary; session_id=sess_[redacted]; transfer_id=[redacted]; direction=$direction; log_writer_id=writer-$direction; log_writer_sequence=$($sequences[$direction]); file_size_bytes=16777216; bytes_transferred=16777216; remote_frontier_chunk_index=781; next_chunk_index=781; terminal_state=completed; route=file_tuna_v4; protocol_version=4")
        $sequences[$direction]++
        $part.Add("event=file_transfer_${direction}_terminal; session_id=sess_[redacted]; transfer_id=[redacted]; log_writer_id=writer-$direction; log_writer_sequence=$($sequences[$direction]); state=Completed; error_code=(none)")
        foreach ($line in $part) { $all.Add($line); if ($transfer -ceq 'second') { $second.Add($line) } }
    }
}
$inputPath = Join-Path $Directory 'filetransfer-retained-log-slice.log'
$slicePath = Join-Path $Directory 'filetransfer-second-transfer-retained-log-slice.log'
$all | Set-Content -LiteralPath $inputPath -Encoding UTF8
$second | Set-Content -LiteralPath $slicePath -Encoding UTF8
$events = @(for ($i=0; $i -lt $all.Count; $i++) {
    ConvertFrom-FileTransferLogLine -Line $all[$i] -FilePath $inputPath -LineNumber ($i+1) -Sequence ($i+1)
})
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
$names = @($catalog.SnapshotAuthorityV1 + $catalog.GenerationStampedEffects + $catalog.NonAuthorityEvidence)
$full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $all.ToArray()
$compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines @($events | Where-Object { $_.EventName -in $names } | ForEach-Object RawLine)
$parity = Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact
$route = Get-FileTransferCoordinatorSnapshotRouteConsistencyParityState -FullEvaluation $full -CompactEvaluation $compact
if ($full.ArtifactEvidenceVerdict -cne 'pass' -or $parity.Verdict -cne 'pass' -or $route.EffectiveResult.Verdict -cne 'pass') {
    throw "fresh_fixture_invalid:$($full.ArtifactEvidenceFailureReason):$($route.EffectiveResult.Findings -join ',')"
}
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'publisher_reevaluated_fresh_input' }
$gui = [pscustomobject]@{routeMode='live-reactivation-second-transfer';secondTransfer=[pscustomobject]@{
    completed=$true;integrityOk=$true;inboundState='Completed';outboundState='Completed';inboundErrorCode='(none)';outboundErrorCode='(none)';
    payloadBytes=16777216;savedFileSizeBytes=16777216;expectedSha256=('a'*64);receivedSha256=('a'*64)}}
$gui | ConvertTo-Json | Set-Content (Join-Path $Directory 'filetransfer-tuna-gui-summary.json') -Encoding UTF8
$request = [pscustomobject]@{schemaVersion=1;attemptId=[guid]::NewGuid().ToString('N');artifactDir=$Directory;
    acceptanceRunId=$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID;scenarioRunId=$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID;
    inputSha256=(Get-FileHash $inputPath).Hash.ToLowerInvariant();sliceSha256=(Get-FileHash $slicePath).Hash.ToLowerInvariant();
    guiSha256=(Get-FileHash (Join-Path $Directory 'filetransfer-tuna-gui-summary.json')).Hash.ToLowerInvariant()}
$before = @($full,$compact,$parity,$route,$events) | ConvertTo-Json -Depth 64 -Compress
Write-FileTransferSecondTransferReport -ArtifactDir $Directory -Request $request -Index ([pscustomobject]@{AllEvents=$events}) `
    -Full $full -Compact $compact -Parity $parity -RouteState $route -Gui $gui -ParentVerdict PASS -ParentGateStatus pass `
    -ParentHardFailures 0 -ParentProofFailures 0 -ParentWarningCapExceeded '(none)'
if ((@($full,$compact,$parity,$route,$events) | ConvertTo-Json -Depth 64 -Compress) -cne $before) { throw 'fresh_parent_mutated' }
$report = Get-Content (Join-Path $Directory 'second-transfer-analysis/primary-derived-report.json') -Raw | ConvertFrom-Json
if ($report.routes.Count -ne 2 -or $report.terminals.Count -ne 2 -or $report.pairedPayloadCount -ne 1 -or $report.operatorVerdict -cne 'PASS') { throw 'fresh_report_incorrect' }

# Exercise the real primary receipt and live split-proof consumer. Supporting
# process artifacts below model a completed offline worker, not a GUI run.
foreach ($name in @('filetransfer-retained-analysis.stdout.txt','filetransfer-retained-analysis.stderr.txt',
        'filetransfer-retained-analysis-diagnostics.json','filetransfer-retained-analysis-stage-diagnostics.json',
        'filetransfer-retained-compact-summary.json','filetransfer-route-consistency-summary.txt','filetransfer-operator-verdict.txt','stability-gates-summary.txt')) {
    '{}' | Set-Content (Join-Path $Directory $name) -Encoding UTF8
}
$full | ConvertTo-Json -Depth 64 | Set-Content (Join-Path $Directory 'filetransfer-coordinator-snapshot-full-summary.json') -Encoding UTF8
$compact | ConvertTo-Json -Depth 64 | Set-Content (Join-Path $Directory 'filetransfer-coordinator-snapshot-compact-summary.json') -Encoding UTF8
$parity | ConvertTo-Json -Depth 12 | Set-Content (Join-Path $Directory 'filetransfer-coordinator-snapshot-parity-summary.json') -Encoding UTF8
$receipt = Write-FileTransferRetainedAnalysisAttemptReceipt -ArtifactDir $Directory -Owner RouteAcceptance -AttemptId $request.attemptId `
    -AcceptanceRunId $request.acceptanceRunId -ScenarioRunId $request.scenarioRunId -LogPath $inputPath `
    -InputSha256Before $request.inputSha256 -InputLineCountBefore $all.Count -LiveRouteProofMode None -CompactRequested $true `
    -ProcessId $PID -StartedUtc ([DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('O')) -EndedUtc ([DateTimeOffset]::UtcNow.ToString('O')) `
    -TimedOut $false -ExitCodeAvailable $true -ExitCode 0 -CompletedProofArtifacts $true -SecondTransferReportRequired $true
$receiptArgs = @{Receipt=$receipt; ArtifactDir=$Directory;ExpectedOwner='RouteAcceptance';AcceptanceRunId=$request.acceptanceRunId;
    ScenarioRunId=$request.scenarioRunId;LogPath=$inputPath;LiveRouteProofMode='None';CompactRequired=$true;SecondTransferReportRequired=$true}
if ($receipt.status -cne 'completed' -or -not (Test-FileTransferRetainedAnalysisAttemptReceipt @receiptArgs)) { throw 'primary_receipt_rejected' }
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'),[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw 'parent_parse_failed' }
foreach ($definition in $ast.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.FunctionDefinitionAst] }) {
    . ([scriptblock]::Create($definition.Extent.Text))
}
$script:RouteAcceptanceExecutionMode='live'
$script:RouteAcceptanceRetainedMarkerIndexCache=@{}
[pscustomobject]@{acceptanceRunId=$request.acceptanceRunId;scenarioRunId=$request.scenarioRunId} | ConvertTo-Json |
    Set-Content (Join-Path $Directory 'filetransfer-tuna-gui-evidence-manifest.json') -Encoding UTF8
$result = New-RouteAcceptanceRunResult -Name 'second-transfer-after-reactivation' -ArtifactDir $Directory -ExpectedRoute file_tuna_v4 -ExpectedProtocol 4
$cycle = [pscustomobject]@{firstTransferTerminalBeforeLiveReactivation=$false;liveRouteEpochRouteChanges=@('post_tuna_fallback_v6','file_tuna_v4')}
if (-not (Try-ApplySecondTransferSplitProofAcceptance -Result $result -Summary $cycle)) { throw 'live_split_proof_rejected' }
$scenario = [pscustomobject]@{Name='second-transfer-after-reactivation';Kind='tuna'}
Set-Phase4MeasurementContamination -Scenario $scenario -Result $result
if ($result.failures.Count -ne 0) { throw "clean_measurement_rejected:$($result.failures -join ',')" }
$measurementPath = Join-Path $Directory 'second-transfer-analysis/measurement-summary.txt'
$measurementBytes = [IO.File]::ReadAllBytes($measurementPath)
[IO.File]::WriteAllText($measurementPath, 'measured_window_bridge_recovery_count=0')
if (Test-FileTransferRetainedAnalysisAttemptReceipt @receiptArgs) { throw 'changed_measurement_receipt_accepted' }
$result.failures.Add('operator hard failures observed: shared failure')
if (Try-ApplySecondTransferSplitProofAcceptance -Result $result -Summary $cycle) { throw 'tampered_split_proof_accepted' }
if ($result.failures.Count -ne 1) { throw 'rejected_split_proof_erased_failure' }
Set-Phase4MeasurementContamination -Scenario $scenario -Result $result
if ($result.failures.Count -ne 2) { throw 'missing_measurement_not_failed_closed' }
[IO.File]::WriteAllBytes($measurementPath, $measurementBytes)
$receipt.PSObject.Properties.Remove('secondTransferReportRequired')
if (Test-FileTransferRetainedAnalysisAttemptReceipt @receiptArgs) { throw 'missing_required_report_flag_accepted' }
Write-Output 'second-transfer-fresh-ok: two evaluations, same primary views, real Coordinator snapshots and modeled delivery'
