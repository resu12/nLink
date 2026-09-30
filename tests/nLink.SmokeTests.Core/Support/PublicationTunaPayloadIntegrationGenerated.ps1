param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,
    [ValidateSet('exact','missing_receive','wrong_range')][string]$TunaPayloadMode,[switch]$ServiceYieldFixture)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$env:NLINK_PUBLICATION_TUNA_PAYLOAD_OBSERVER='1'
if($TunaPayloadMode -ceq 'exact') {
    $null=& (Join-Path $PSScriptRoot 'PublicationPositiveIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
        -RunId $RunId -ScenarioId $ScenarioId -PersistenceMode write -ObservePrimary -ReceiveFixture -TwoWriterFixture -RecoveredIngressFixture -LiveScaleFixture
} else {
    $null=& (Join-Path $PSScriptRoot 'PublicationReceiveIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
        -RunId $RunId -ScenarioId $ScenarioId -TwoWriterFixture -ServiceIngressFixture -TunaPayloadMode $TunaPayloadMode -ServiceYieldFixture:$ServiceYieldFixture
}
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
$facts=[IO.File]::ReadAllText((Join-Path $output 'live-primary-observation.json')) | ConvertFrom-Json
$expected=if($TunaPayloadMode -ceq 'exact') {'recovered'} elseif($TunaPayloadMode -ceq 'missing_receive') {'missing_exact_tuna_receive'} else {'missing_tuna_resend'}
$candidates=@($facts.IngressCandidates)
if($facts.Evaluations -ne 3 -or ($facts.EvaluationCallers -join '|') -cne '<ScriptBlock>|<ScriptBlock>|Get-FileTransferRecoveryFailureClassification' -or
    $candidates.Count -ne 1 -or -not $candidates[0].ChainMatched -or $candidates[0].Reason -cne $expected -or
    $candidates[0].Recovered -ne ($TunaPayloadMode -ceq 'exact')) { throw 'Original primary exact-payload result changed' }
if($TunaPayloadMode -ceq 'exact' -and ($facts.Witness -or $facts.Completion)) { throw 'Recovered candidate acquired a failure witness' }
foreach($view in @('full','compact')) {
    $snapshot=[IO.File]::ReadAllText((Join-Path $output ('filetransfer-coordinator-snapshot-'+$view+'-summary.json'))) | ConvertFrom-Json
    if(-not $snapshot.StreamComplete -or $snapshot.ArtifactEvidenceVerdict -cne 'pass' -or $snapshot.BoundaryCount -le 0 -or
        @($snapshot.Boundaries | Where-Object { $_.verdict -cne 'pass' }).Count -ne 0) { throw "Incomplete $view Snapshot history" }
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
$start=if($TunaPayloadMode -ceq 'wrong_range') {13} else {12}
foreach($line in @($sends)+@($receives)) {
    foreach($pair in @(@('route','file_tuna_v4'),@('protocol_version','4'),@('live_route_epoch','3'),
        @('start_chunk_index',[string]$start),@('last_end_chunk_exclusive',[string]($start+2)),@('raw_payload_bytes','2048'),@('frame_chunk_count','2'))) {
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
'tuna-payload-integration='+([ordered]@{Mode=$TunaPayloadMode;OriginalEvaluations=3;FirstEdge=$expected
    Recovered=($TunaPayloadMode -ceq 'exact');WriterTopology='two_processes_two_writers';PeerLineage='not_evaluated';Live=$false} | ConvertTo-Json -Compress)
