param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[switch]$TwoWriterFixture,[switch]$ServiceIngressFixture,
    [string]$TunaPayloadMode='none',[switch]$LiveScaleFixture,[switch]$LaterFallbackFixture)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$null=& (Join-Path $PSScriptRoot 'PublicationPositiveIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
    -RunId $RunId -ScenarioId $ScenarioId -PersistenceMode write -ObservePrimary -ReceiveFixture -TwoWriterFixture:$TwoWriterFixture `
    -LiveScaleFixture:($TunaPayloadMode -cne 'none' -or $LiveScaleFixture)
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
$facts=[IO.File]::ReadAllText((Join-Path $output 'live-primary-observation.json')) | ConvertFrom-Json
if($facts.Evaluations -ne 3 -or ($facts.EvaluationCallers -join '|') -cne '<ScriptBlock>|<ScriptBlock>|Get-FileTransferRecoveryFailureClassification' -or
    -not $facts.Prepared -or -not $facts.Witness -or -not $facts.Completion -or $facts.OptionalMilliseconds -gt 20000) {
    throw 'Receive fixture changed original primary evaluation/custody contract'
}
$expectedEdge=if($TunaPayloadMode -ceq 'missing_receive') { 'missing_exact_tuna_receive' } elseif($ServiceIngressFixture) { 'missing_tuna_resend' } elseif($TwoWriterFixture) { 'missing_receiver_route_transition' } else { 'missing_earlier_source_send' }
if($facts.SelectedRejectionReason -cne $expectedEdge -or
    -not $facts.SelectedRejectionChainMatched -or $facts.SelectedRejectionRecovered) {
    throw ('Original selected rejection edge changed: '+$facts.SelectedRejectionReason)
}
$expectedVariant=if($expectedEdge -ceq 'missing_tuna_resend') {
    'second_tuna_rejected_regular_ingress_exact_tuna_resend_missing'
} elseif($expectedEdge -ceq 'missing_exact_tuna_receive') {
    'second_tuna_rejected_regular_ingress_exact_tuna_receive_missing'
} else { '(none)' }
$expectedExplanation=$null
if($expectedEdge -in @('missing_tuna_resend','missing_exact_tuna_receive')) {
    $kind=if($expectedEdge -ceq 'missing_tuna_resend') {'send'} else {'receive'}
    $expectedExplanation="The rejection chain matched, but the required pre-rejection exact Tuna $kind was not found. Later fallback delivery, if present, is a separate outcome and does not satisfy this recovery contract. Stale safety and durable delivery are not established by this diagnostic."
}
if($facts.SelectedRejectionExplanation -cne $expectedExplanation) { throw 'Original selected diagnostic explanation changed' }
$compactSummary=[IO.File]::ReadAllText((Join-Path $output 'filetransfer-retained-compact-summary.json')) | ConvertFrom-Json
if($compactSummary.failure_variant -cne $expectedVariant -or
    $compactSummary.first_proof_chain_failure_class -cne 'file_tuna_data_path_mismatch') {
    throw 'Original primary lost the exact rejected-ingress diagnostic variant'
}
# Post-run provenance assertions, not another capture or authority evaluation.
foreach($view in @('full','compact')) {
    $snapshot=[IO.File]::ReadAllText((Join-Path $output ('filetransfer-coordinator-snapshot-'+$view+'-summary.json'))) | ConvertFrom-Json
    if(-not $snapshot.StreamComplete -or $snapshot.ArtifactEvidenceVerdict -cne 'pass' -or $snapshot.BoundaryCount -le 0 -or
        @($snapshot.Boundaries | Where-Object { $_.verdict -cne 'pass' }).Count -ne 0) {
        throw "Receive fixture has incomplete $view Snapshot history"
    }
}
$parity=[IO.File]::ReadAllText((Join-Path $output 'filetransfer-coordinator-snapshot-parity-summary.json')) | ConvertFrom-Json
if($parity.Verdict -cne 'pass') { throw 'Receive fixture lost original full/compact authority parity' }
function Field([string]$Line,[string]$Name) {
    $matches=[regex]::Matches($Line,'(?:^|;\s*)'+[regex]::Escape($Name)+'=([^;]*)')
    if($matches.Count -ne 1) { throw "Missing or ambiguous fixture field: $Name" }
    $matches[0].Groups[1].Value.Trim()
}
$publication=[IO.File]::ReadAllText((Join-Path $output 'adopted-offline-publication.json')) | ConvertFrom-Json
$lines=[IO.File]::ReadAllLines((Join-Path $output 'filetransfer-retained-log-slice.log'))
$receive=$lines[$publication.selection.witness.sourceLine-1]
$fingerprint=Field $receive 'data_path_envelope_fingerprint'
$sends=@($lines | Where-Object { $_.Contains('event=filetransfer_data_path_send_result;') -and
    $_.Contains('data_path_envelope_fingerprint='+$fingerprint+';') })
if($sends.Count -ne 1 -or (Field $sends[0] 'route') -cne 'regular_nkn_v4_fast' -or
    (Field $sends[0] 'live_route_epoch') -cne '0' -or (Field $sends[0] 'delivery_result') -cne 'sent' -or
    (Field $sends[0] 'actual_transport') -cne 'regular_nkn' -or (Field $receive 'actual_transport') -cne 'regular_nkn' -or
    ((Field $sends[0] 'log_writer_id') -ceq (Field $receive 'log_writer_id')) -eq $TwoWriterFixture.IsPresent -or
    (-not $TwoWriterFixture -and [long](Field $sends[0] 'log_writer_sequence') -ge [long](Field $receive 'log_writer_sequence')) -or
    [long](Field $sends[0] 'log_writer_attempt_utc_ticks') -ge [long](Field $receive 'log_writer_attempt_utc_ticks')) {
    throw 'Fresh source/receive provenance or explicit writer topology changed'
}
foreach($name in @('data_path_session_id','data_path_transfer_id','security_payload_fingerprint','security_sequence',
    'security_establishment_fingerprint','frame_type','start_chunk_index','last_end_chunk_exclusive','raw_payload_bytes','frame_chunk_count')) {
    if((Field $sends[0] $name) -cne (Field $receive $name)) { throw "Delayed envelope provenance changed: $name" }
}
if($TwoWriterFixture) {
    $merged=[IO.File]::ReadAllLines((Join-Path $Directory 'two-writer-source.log'))
    $processes=@(); $originalCount=0
    foreach($role in @('sender','receiver')) {
        $process=[IO.File]::ReadAllText((Join-Path $Directory ($role+'-process.json'))) | ConvertFrom-Json
        $processes+=([int]$process.ProcessId)
        $name=if($role -ceq 'sender') { 'sender.log' } else { 'nlink.log' }
        $path=Join-Path $Directory $name
        if($process.LogPath -cne $path -or $process.Sender -ne ($role -ceq 'sender')) { throw 'Actual peer log/process scope changed' }
        $original=[IO.File]::ReadAllLines($path); $originalCount+=$original.Length
        $writer=Field $original[0] 'log_writer_id'
        if($writer -cnotmatch ('^p'+$process.ProcessId+'r[0-9a-f]+$') -or
            -not $original[0].Contains('event=operational_log_writer_started;') -or
            -not $original[-1].Contains('event=operational_log_writer_drained;')) { throw 'Peer lacks its genuine writer lifecycle' }
        for($i=0;$i -lt $original.Length;$i++) {
            if((Field $original[$i] 'log_writer_id') -cne $writer -or [long](Field $original[$i] 'log_writer_sequence') -ne $i+1) {
                throw 'Original peer writer identity/order changed'
            }
        }
        $collected=@($merged | Where-Object { (Field $_ 'log_writer_id') -ceq $writer })
        if(($collected -join "`n") -cne ($original -join "`n")) { throw 'Collection rewrote or dropped original peer records' }
        $witnessLine=if($role -ceq 'sender') { $sends[0] } else { $receive }
        if((Field $witnessLine 'log_writer_id') -cne $writer -or -not ($original -ccontains $witnessLine)) {
            throw 'Selected envelope is not an exact original peer record'
        }
    }
    if($processes[0] -eq $processes[1] -or $originalCount -ne $merged.Length -or
        (Field $sends[0] 'security_installation_id') -ceq (Field $receive 'security_installation_id')) { throw 'Two genuine peer lifecycles collapsed' }
    if(-not $ServiceIngressFixture -and @($lines | Where-Object { $_.Contains('event=filetransfer_route_transitioned;') }).Count -ne 0) {
        throw 'Fixture unexpectedly supplied a service route-transition observation'
    }
    if($ServiceIngressFixture) {
        $transitions=@($lines | Where-Object { $_.Contains('event=filetransfer_route_transitioned;') })
        if($transitions.Count -ne $(if($LaterFallbackFixture) {4} else {3}) -or
            (Field $transitions[2] 'previous_route') -cne 'post_tuna_fallback_v6') {
            throw 'Service did not emit its expected committed transitions'
        }
        foreach($name in @('log_writer_id','session_id','transfer_id','route','protocol_version','live_route_epoch',
            'runtime_generation','terminal_generation','leg_generation','route_publication_generation','route_commit_generation')) {
            if((Field $transitions[2] $name) -cne (Field $receive $name)) { throw "Service transition/receive scope changed: $name" }
        }
        if([long](Field $transitions[2] 'log_writer_sequence') -ge [long](Field $receive 'log_writer_sequence')) {
            throw 'Service transition did not precede the original delayed receive'
        }
        $yieldSends=@($lines | Where-Object { $_.Contains('event=filetransfer_data_path_send_result;') -and
            $_.Contains('frame_type=filetransfer.fallback_tail.yield.v6;') -and $_.Contains('direction=outbound;') })
        $yieldReceives=@($lines | Where-Object { $_.Contains('event=filetransfer_data_path_receive_result;') -and
            $_.Contains('frame_type=filetransfer.fallback_tail.yield.v6;') -and $_.Contains('direction=inbound;') })
        if($yieldSends.Count -ne 1 -or $yieldReceives.Count -ne 1 -or
            (Field $yieldSends[0] 'log_writer_id') -cne (Field $sends[0] 'log_writer_id') -or
            (Field $yieldReceives[0] 'log_writer_id') -cne (Field $receive 'log_writer_id') -or
            (Field $yieldSends[0] 'delivery_result') -cne 'sent') { throw 'Yield proof did not cross the actual peer transports' }
        foreach($name in @('data_path_envelope_fingerprint','security_payload_fingerprint','security_sequence',
            'security_establishment_fingerprint','data_path_session_id','data_path_transfer_id')) {
            if((Field $yieldSends[0] $name) -cne (Field $yieldReceives[0] $name)) { throw "Original peer yield provenance changed: $name" }
        }
        if([long](Field $yieldReceives[0] 'log_writer_sequence') -ge [long](Field $transitions[2] 'log_writer_sequence')) {
            throw 'Peer yield arrived after the service transition'
        }
    }
}
$topology=if($TwoWriterFixture) { 'two_processes_two_writers' } else { 'single_process_two_peers' }
'receive-integration='+([ordered]@{OriginalEvaluations=3;GenuineReceiveGuard=$true;Epoch=3;Companion=$true
    WriterTopology=$topology;PeerLineage='not_evaluated';AuthorityEffect='none'
    RejectionChainMatched=$true;FirstMissingRecoveryEdge=$facts.SelectedRejectionReason;Recovered=$false;Live=$false} | ConvertTo-Json -Compress)
