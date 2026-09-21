param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,[switch]$ServiceFallbackFixture)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
# Original primary evaluation on this new two-writer fixture only. The shared
# assertions verify writer custody, rejection safety and Snapshot completeness.
$null=& (Join-Path $PSScriptRoot 'PublicationReceiveIntegrationGenerated.ps1') -RepoRoot $RepoRoot -Directory $Directory `
    -RunId $RunId -ScenarioId $ScenarioId -TwoWriterFixture -LiveScaleFixture `
    -ServiceIngressFixture:$ServiceFallbackFixture -LaterFallbackFixture:$ServiceFallbackFixture
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major+'-write')
$lines=[IO.File]::ReadAllLines((Join-Path $output 'filetransfer-retained-log-slice.log'))
function Field([string]$Line,[string]$Name) {
    $matches=[regex]::Matches($Line,'(?:^|;\s*)'+[regex]::Escape($Name)+'=([^;]*)')
    if($matches.Count -ne 1) { throw "Missing or ambiguous fallback field: $Name" }
    $matches[0].Groups[1].Value.Trim()
}
$rejected=@($lines | Where-Object { $_.Contains('event=filetransfer_data_path_receive_result;') -and
    $_.Contains('receive_disposition=rejected_before_dispatch;') })
$sent=@($lines | Where-Object { $_.Contains('event=filetransfer_data_path_send_result;') -and
    $_.Contains('frame_type=filetransfer.chunk_batch.v6;') })
$received=@($lines | Where-Object { $_.Contains('event=filetransfer_data_path_receive_result;') -and
    $_.Contains('frame_type=filetransfer.chunk_batch.v6;') })
if($rejected.Count -ne 1 -or $sent.Count -ne 1 -or $received.Count -ne 1 -or
    @($lines | Where-Object { $_.Contains('actual_transport=tuna;') }).Count -ne 0) {
    throw 'Expected one rejected Regular envelope and one later fallback delivery, with no Tuna payload'
}
$start=if($ServiceFallbackFixture) {100} else {12}
$bytes=if($ServiceFallbackFixture) {43008} else {2048}
foreach($line in @($sent)+@($received)) {
    foreach($pair in @(@('route','post_tuna_fallback_v6'),@('protocol_version','6'),@('live_route_epoch','4'),
        @('route_publication_generation','5'),@('actual_transport','regular_nkn'),@('delivery_transport','regular_nkn'),
        @('start_chunk_index',[string]$start),@('last_end_chunk_exclusive',[string]($start+2)),@('frame_chunk_count','2'),@('raw_payload_bytes',[string]$bytes))) {
        if((Field $line $pair[0]) -cne $pair[1]) { throw "Later fallback binding changed: $($pair[0])" }
    }
    if([long](Field $line 'log_writer_attempt_utc_ticks') -le [long](Field $rejected[0] 'log_writer_attempt_utc_ticks')) {
        throw 'Fallback delivery must follow rejection, not be borrowed as an earlier Tuna receipt'
    }
}
foreach($name in @('data_path_envelope_fingerprint','security_payload_fingerprint','security_sequence',
    'security_establishment_fingerprint','data_path_session_id','data_path_transfer_id')) {
    if((Field $sent[0] $name) -cne (Field $received[0] $name)) { throw "Fallback envelope provenance changed: $name" }
}
foreach($name in @('security_establishment_fingerprint','data_path_session_id','data_path_transfer_id',
    'start_chunk_index','last_end_chunk_exclusive','frame_chunk_count','raw_payload_bytes')) {
    if((Field $received[0] $name) -cne (Field $rejected[0] $name)) { throw "Later delivery changed the original scope/range: $name" }
}
if((Field $sent[0] 'delivery_result') -cne 'sent' -or
    (Field $sent[0] 'log_writer_id') -ceq (Field $received[0] 'log_writer_id') -or
    (Field $received[0] 'log_writer_id') -cne (Field $rejected[0] 'log_writer_id') -or
    (Field $sent[0] 'data_path_envelope_fingerprint') -ceq (Field $rejected[0] 'data_path_envelope_fingerprint') -or
    [long](Field $sent[0] 'security_sequence') -le [long](Field $rejected[0] 'security_sequence')) {
    throw 'Later delivery must be a fresh authenticated envelope between the original peers'
}
if($ServiceFallbackFixture) {
    $progress=[IO.File]::ReadAllText((Join-Path $Directory 'service-durable-progress.json')) | ConvertFrom-Json
    if($progress.PreviousEpoch -ne 3 -or $progress.Epoch -ne 4 -or $progress.Publication -ne 5 -or $progress.Protocol -ne 6 -or
        -not $progress.OwnerChanged -or $progress.BeforeFrontier -ne 100 -or $progress.AfterFrontier -ne 102 -or
        $progress.BeforeBytes -ne 2150400 -or $progress.AfterBytes -ne 2193408 -or -not $progress.FlushedAndReopened -or
        $progress.BoundSessionGeneration -ne 1 -or -not $progress.StaleTunaDrainIgnored -or
        $progress.WrittenRangeSha256 -cne 'f961b37a5d7c3911ebb62a5c0a765c2a6e005a75b9e71d3093be381d526b44cd' -or
        $progress.WholeTransferCompleted) { throw 'Service durable range progression changed' }
    $transition=@($lines | Where-Object { $_.Contains('event=filetransfer_route_transitioned; direction=inbound;') -and
        $_.Contains('live_route_epoch=4;') })
    if($transition.Count -ne 1) { throw 'Missing exact Service final-fallback transition' }
    foreach($name in @('log_writer_id','session_id','transfer_id','route','protocol_version','live_route_epoch',
        'runtime_generation','terminal_generation','leg_generation','route_publication_generation','route_commit_generation')) {
        if((Field $transition[0] $name) -cne (Field $received[0] $name)) { throw "Service final publication binding changed: $name" }
    }
    if([long](Field $transition[0] 'log_writer_sequence') -le [long](Field $rejected[0] 'log_writer_sequence') -or
        [long](Field $transition[0] 'log_writer_sequence') -ge [long](Field $received[0] 'log_writer_sequence')) {
        throw 'Service fallback transition must follow rejection and precede new delivery'
    }
}
$firstEdge=if($ServiceFallbackFixture) {'missing_tuna_resend'} else {'missing_receiver_route_transition'}
'later-fallback-integration='+([ordered]@{WriterTopology='two_processes_two_writers';RejectedEpoch=3;DeliveredEpoch=4
    Publication=5;Protocol=6;Bytes=$bytes;Recovered=$false;FirstEdge=$firstEdge
    DurableRangeProven=$ServiceFallbackFixture.IsPresent;WholeTransferCompleted=$false;Live=$false} | ConvertTo-Json -Compress)
