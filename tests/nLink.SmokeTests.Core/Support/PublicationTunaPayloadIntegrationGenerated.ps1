param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,
    [ValidateSet('exact','missing_receive','wrong_range','rebatch_same','rebatch_changed','rebatch_middle')][string]$TunaPayloadMode,[switch]$ServiceYieldFixture)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_PUBLICATION_TUNA_PAYLOAD_OBSERVER='1'
# Select the approved acceptance contract explicitly. Optional publication
# diagnostics need not manufacture a witness for an unproven activation.
$null=& (Join-Path $PSScriptRoot 'PublicationPositiveIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
    -RunId $RunId -ScenarioId $ScenarioId -PersistenceMode write -ObservePrimary -ReceiveFixture -TwoWriterFixture `
    -RecoveredIngressFixture -LiveScaleFixture -LiveProofMode RegularActivationCycle
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
$facts=[IO.File]::ReadAllText((Join-Path $output 'live-primary-observation.json')) | ConvertFrom-Json
$expected=if($TunaPayloadMode -ceq 'exact') {'recovered'} elseif($TunaPayloadMode -ceq 'missing_receive') {'missing_exact_tuna_receive'} else {'missing_tuna_resend'}
$candidates=@($facts.IngressCandidates)
if($facts.Evaluations -ne 3 -or ($facts.EvaluationCallers -join '|') -cne '<ScriptBlock>|<ScriptBlock>|Get-FileTransferRecoveryFailureClassification' -or
    $candidates.Count -ne 1 -or -not $candidates[0].ChainMatched -or $candidates[0].Reason -cne $expected -or
    $candidates[0].Recovered -ne ($TunaPayloadMode -ceq 'exact')) { throw 'Original primary exact-payload result changed' }
if($TunaPayloadMode -ceq 'exact' -and ($facts.Witness -or $facts.Completion)) { throw 'Recovered candidate acquired a failure witness' }
$summary=Get-Content -LiteralPath (Join-Path $output 'filetransfer-retained-compact-summary.json') -Raw | ConvertFrom-Json
$routeCycle=$summary.file_tuna_route_cycle_acceptance
if($routeCycle.contract -cne 'authenticated_stale_rejection_and_tuna_activation_delivery_v1' -or
    $routeCycle.full_compact_parity_verdict -cne 'pass' -or $routeCycle.candidate_count -ne 1 -or
    $routeCycle.safe_rejected_count -ne 1 -or $routeCycle.blocking_count -ne 0 -or
    $routeCycle.activations.Count -ne 2 -or $routeCycle.activations[0].verdict -cne 'unproven' -or
    $routeCycle.activations[1].verdict -cne $(if($TunaPayloadMode -ceq 'missing_receive') {'unproven'} else {'pass'}) -or
    $routeCycle.verdict -cne 'fail' -or $summary.gate_status -cne 'fail') {
    throw ('Selected contract conflated stale rejection with required activation delivery: '+($routeCycle | ConvertTo-Json -Depth 8 -Compress))
}
# This fixture has no first-activation payload pair. Later delivery, including
# a rebatch, cannot supply that missing leg or make whole-run acceptance pass.
$accounting=$summary.file_tuna_v4_candidate_accounting
if(-not $accounting.diagnostic_only -or -not $accounting.reconciled -or
    $accounting.reported_candidate_count -ne $summary.file_tuna_v4_data_path_mismatch_distinct_count -or
    $accounting.reported_recovered_count -ne $summary.file_tuna_v4_data_path_mismatch_recovered_count -or
    $accounting.reported_unresolved_count -ne $summary.file_tuna_v4_data_path_mismatch_unresolved_count) {
    throw 'Candidate report does not reconcile with the original primary counts'
}
$reportedReceive=@($accounting.candidates | Where-Object { $_.event_name -ceq 'filetransfer_data_path_receive_result' })
if($reportedReceive.Count -ne 1 -or $reportedReceive[0].source_line -ne $candidates[0].SourceLine -or
    $reportedReceive[0].diagnostic_reason -cne $expected -or -not $reportedReceive[0].rejection_chain_matched -or
    $reportedReceive[0].recovered -ne $candidates[0].Recovered) { throw 'Candidate report changed the existing receive decision' }
$snapshotViews=@{}
foreach($view in @('full','compact')) {
    $snapshot=[IO.File]::ReadAllText((Join-Path $output ('filetransfer-coordinator-snapshot-'+$view+'-summary.json'))) | ConvertFrom-Json
    if(-not $snapshot.StreamComplete -or $snapshot.ArtifactEvidenceVerdict -cne 'pass' -or $snapshot.BoundaryCount -le 0 -or
        @($snapshot.Boundaries | Where-Object { $_.verdict -cne 'pass' }).Count -ne 0) { throw "Incomplete $view Snapshot history" }
    $snapshotViews[$view]=$snapshot
}
$parity=[IO.File]::ReadAllText((Join-Path $output 'filetransfer-coordinator-snapshot-parity-summary.json')) | ConvertFrom-Json
if($parity.Verdict -cne 'pass') { throw 'Original Snapshot parity failed' }
# Inspect original records only; no additional authority or recovery evaluation.
function PayloadField([string]$Line,[string]$Name) {
    $matches=[regex]::Matches($Line,'(?:^|;\s*)'+[regex]::Escape($Name)+'=([^;]*)')
    if($matches.Count -ne 1) { throw "Missing or ambiguous payload field: $Name" }
    $matches[0].Groups[1].Value.Trim()
}
$lines=[IO.File]::ReadAllLines((Join-Path $output 'filetransfer-retained-log-slice.log'))
. (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Contract replay must reuse completed evaluations' }
$events=@(for($i=0;$i -lt $lines.Length;$i++) {
    ConvertFrom-FileTransferLogLine -Line $lines[$i] -FilePath (Join-Path $output 'filetransfer-retained-log-slice.log') -LineNumber ($i+1) -Sequence ($i+1)
})
$fullSummary=[pscustomobject]@{AllEvents=$events;GlobalEvents=@();TransferEvents=$events}
$fullProof=Get-FileTransferFileTunaV4DataPathProof -Summary $fullSummary -CoordinatorSnapshotEvaluation $snapshotViews.full `
    -TunaRouteCycleCompactEvaluation $snapshotViews.compact
if(($fullProof.RouteCycleAcceptance.Report | ConvertTo-Json -Depth 8 -Compress) -cne ($routeCycle | ConvertTo-Json -Depth 8 -Compress) -or
    $fullProof.MismatchRecoveredCount -ne $summary.file_tuna_v4_data_path_mismatch_recovered_count -or
    $fullProof.MismatchUnresolvedCount -ne $summary.file_tuna_v4_data_path_mismatch_unresolved_count) {
    throw 'Full and compact data-path consumers disagree or changed legacy accounting'
}
$old=@($lines | Where-Object { $_.Contains('event=filetransfer_data_path_receive_result;') -and
    $_.Contains('receive_disposition=rejected_before_dispatch;') })
$sends=@($lines | Where-Object { $_.Contains('event=filetransfer_data_path_send_result;') -and $_.Contains('actual_transport=tuna;') })
$receives=@($lines | Where-Object { $_.Contains('event=filetransfer_data_path_receive_result;') -and $_.Contains('actual_transport=tuna;') })
if($old.Count -ne 1 -or $sends.Count -ne 1 -or $receives.Count -ne $(if($TunaPayloadMode -ceq 'missing_receive') {0} else {1}) -or
    (PayloadField $sends[0] 'delivery_result') -cne 'sent' -or (PayloadField $sends[0] 'delivery_transport') -cne 'tuna' -or
    (PayloadField $sends[0] 'log_writer_id') -ceq (PayloadField $old[0] 'log_writer_id') -or
    [long](PayloadField $sends[0] 'log_writer_attempt_utc_ticks') -ge [long](PayloadField $old[0] 'log_writer_attempt_utc_ticks')) {
    throw 'Tuna send/receive topology or order changed'
}
$start=if($TunaPayloadMode -ceq 'wrong_range') {13} elseif($TunaPayloadMode -ceq 'rebatch_middle') {11} else {12}
$rebatching=$TunaPayloadMode -in @('rebatch_same','rebatch_changed','rebatch_middle')
$chunks=if($rebatching) {3} else {2}
foreach($line in @($sends)+@($receives)) {
    foreach($pair in @(@('route','file_tuna_v4'),@('protocol_version','4'),@('live_route_epoch','3'),
        @('start_chunk_index',[string]$start),@('last_end_chunk_exclusive',[string]($start+$chunks)),@('raw_payload_bytes',[string]($chunks*1024)),@('frame_chunk_count',[string]$chunks))) {
        if((PayloadField $line $pair[0]) -cne $pair[1]) { throw "Current exact Tuna tuple changed: $($pair[0])" }
    }
    if((PayloadField $line 'publication_evidence_token_fingerprint') -cnotmatch '^fte1-') { throw 'Tuna payload lacks its original publication binding' }
}
if($receives.Count -eq 1) {
    foreach($name in @('data_path_envelope_fingerprint','security_payload_fingerprint','security_sequence',
        'security_establishment_fingerprint','data_path_session_id','data_path_transfer_id','start_chunk_index',
        'last_end_chunk_exclusive','raw_payload_bytes','frame_chunk_count')) {
        if((PayloadField $sends[0] $name) -cne (PayloadField $receives[0] $name)) { throw "Original Tuna envelope changed: $name" }
    }
    if((PayloadField $receives[0] 'log_writer_id') -cne (PayloadField $old[0] 'log_writer_id') -or
        [long](PayloadField $receives[0] 'log_writer_sequence') -ge [long](PayloadField $old[0] 'log_writer_sequence')) {
        throw 'Exact Tuna receive did not precede the rejected regular packet'
    }
}
$merged=[IO.File]::ReadAllLines((Join-Path $Directory 'two-writer-source.log'))
$count=0; $pids=@()
foreach($role in @('sender','receiver')) {
    $process=[IO.File]::ReadAllText((Join-Path $Directory ($role+'-process.json'))) | ConvertFrom-Json
    $pids+=([int]$process.ProcessId)
    $path=Join-Path $Directory $(if($role -ceq 'sender') {'sender.log'} else {'nlink.log'})
    if($process.LogPath -cne $path) { throw 'Peer log path changed' }
    $original=[IO.File]::ReadAllLines($path); $count+=$original.Length
    $writer=PayloadField $original[0] 'log_writer_id'
    if($writer -cnotmatch ('^p'+$process.ProcessId+'r[0-9a-f]+$') -or
        -not $original[0].Contains('event=operational_log_writer_started;') -or
        -not $original[-1].Contains('event=operational_log_writer_drained;')) { throw 'Original writer lifecycle changed' }
    for($i=0;$i -lt $original.Length;$i++) {
        if((PayloadField $original[$i] 'log_writer_id') -cne $writer -or [long](PayloadField $original[$i] 'log_writer_sequence') -ne $i+1) {
            throw 'Original writer order changed'
        }
    }
    $collected=@($merged | Where-Object { (PayloadField $_ 'log_writer_id') -ceq $writer })
    if(($collected -join "`n") -cne ($original -join "`n")) { throw 'Merged capture changed original peer records' }
}
if($pids[0] -eq $pids[1] -or $count -ne $merged.Length) {
    throw 'Original source custody changed'
}
# The production retained projection filters unrelated log events. Every kept
# record must still be an exact, ordered member of the complete merged source.
$sourceIndex=[Collections.Generic.Dictionary[string,int]]::new([StringComparer]::Ordinal)
for($i=0;$i -lt $merged.Length;$i++) { $sourceIndex.Add($merged[$i],$i) }
$last=-1
foreach($line in $lines) {
    $index=0
    if(-not $sourceIndex.TryGetValue($line,[ref]$index) -or $index -le $last) { throw 'Retained projection rewrote or reordered an original record' }
    $last=$index
}
if($lines[$candidates[0].SourceLine-1] -cne $old[0]) { throw 'Original primary candidate is not the retained rejected receive' }
$result=[ordered]@{Mode=$TunaPayloadMode;OriginalEvaluations=3;FirstEdge=$expected
    Recovered=($TunaPayloadMode -ceq 'exact');WriterTopology='two_processes_two_writers';PeerLineage='not_evaluated';Live=$false}
if($rebatching) {
    # Fixture byte assertions are separate from original primary/authority results.
    $comparison=Get-Content -LiteralPath (Join-Path $Directory 'rebatching-fixture.json') -Raw | ConvertFrom-Json
    $receiverProcess=Get-Content -LiteralPath (Join-Path $Directory 'receiver-process.json') -Raw | ConvertFrom-Json
    if($comparison.Schema -cne 'test-only-rebatching-byte-comparison-v1' -or $comparison.Mode -cne $TunaPayloadMode -or
        $comparison.Comparison -cne 'decoded_held_wire_to_data_session_receive' -or
        $comparison.ContentEqual -ne ($TunaPayloadMode -cne 'rebatch_changed') -or
        $comparison.HeldPayloadFingerprint -cne (PayloadField $old[0] 'security_payload_fingerprint') -or
        $comparison.ReceiverProcessId -ne $receiverProcess.ProcessId -or $comparison.ChunkIndex -ne 12 -or
        $comparison.SegmentBytes -ne 1024 -or $comparison.HeldChunkCount -ne 1 -or $comparison.ReceivedChunkCount -ne 3) {
        throw 'Fixture content comparison changed or borrowed another held packet'
    }
    foreach($pair in @(@('start_chunk_index','12'),@('last_end_chunk_exclusive','13'),@('raw_payload_bytes','1024'),@('batch_chunk_count','1'))) {
        if((PayloadField $old[0] $pair[0]) -cne $pair[1]) { throw 'Held single-chunk shape changed' }
    }
    # Producer evidence only: bind the held encrypted packet to its original
    # sender row, then compare that sender's logical chunk commitments. This
    # does not run or change an authority/recovery classification.
    $heldFingerprint=PayloadField $old[0] 'security_payload_fingerprint'
    $heldSends=@($merged | Where-Object { $_.Contains('event=filetransfer_data_path_send_result;') -and
        (PayloadField $_ 'security_payload_fingerprint') -ceq $heldFingerprint })
    if($heldSends.Count -ne 1) { throw 'Held packet lacks one original sender record' }
    $heldSend=$heldSends[0]
    foreach($name in @('log_writer_id','security_transport_id','security_installation_id','security_installation_generation',
        'security_establishment_fingerprint','data_path_session_id','data_path_transfer_id','content_scope_id')) {
        if((PayloadField $heldSend $name) -cne (PayloadField $sends[0] $name)) { throw "Sender content scope changed: $name" }
    }
    $tunaSlot=if($TunaPayloadMode -ceq 'rebatch_middle') {1} else {0}
    foreach($line in @($heldSend,$sends[0])) {
        $slot=if($line -ceq $heldSend) {0} else {$tunaSlot}
        foreach($pair in @(@('content_evidence_profile','sender_chunk_hmac_v1'),@('content_evidence_state','captured'),
            @('publication_use_kind','v4_physical_submission'),@(('content_segment_'+$slot+'_index'),'12'),@(('content_segment_'+$slot+'_bytes'),'1024'))) {
            if((PayloadField $line $pair[0]) -cne $pair[1]) { throw "Missing exact producer content binding: $($pair[0])" }
        }
        if((PayloadField $line 'content_scope_id') -cnotmatch '^ftcs1-[a-f0-9]{32}$' -or
            (PayloadField $line ('content_segment_'+$slot+'_commitment')) -cnotmatch '^ftcc1-[a-f0-9]{64}$') {
            throw 'Malformed sender content evidence'
        }
    }
    if((PayloadField $heldSend 'content_segment_count') -cne '1' -or (PayloadField $sends[0] 'content_segment_count') -cne '3') {
        throw 'Sender content segment shape changed'
    }
    $hmacEqual=(PayloadField $heldSend 'content_segment_0_commitment') -ceq (PayloadField $sends[0] ('content_segment_'+$tunaSlot+'_commitment'))
    if($hmacEqual -ne $comparison.ContentEqual) { throw 'Sender commitment equality disagrees with private wire-byte comparison' }
    if(@($receives+$old | Where-Object { $_.Contains('content_evidence_profile=') }).Count -ne 0) {
        throw 'Sender-only evidence appeared on a receiver record'
    }
    $result.FixtureContentEqual=$comparison.ContentEqual
    $result.FixtureSenderCommitmentEqual=$hmacEqual
    $result.FixtureComparison=$comparison.Comparison
}
if($env:NLINK_PUBLICATION_CONTENT_COVERAGE_SHADOW -cne '1' -and
    (Test-Path -LiteralPath (Join-Path $output 'content-coverage-shadow.json'))) { throw 'Content consumer was not default off' }
'tuna-payload-integration='+($result | ConvertTo-Json -Compress)
