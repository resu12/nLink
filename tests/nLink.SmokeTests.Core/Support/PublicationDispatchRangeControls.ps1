param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/PublicationServiceYieldMatcher.ps1')
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/PublicationRejectionChainMatcher.ps1')
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/PublicationDispatchWindowMatcher.ps1')
function ConvertTo-FileTransferCoordinatorEvidenceToken { throw 'Redacted-scope range controls must not borrow scope evidence' }
$selected=@{frame_type='filetransfer.chunk_batch.v4';chunk_index='12-13';start_chunk_index='12';last_end_chunk_exclusive='14';
    frame_chunk_count='2';batch_chunk_count='2';raw_payload_bytes='2048';payload_bytes='2048';
    data_path_session_id='fte1-'+('a'*24);data_path_transfer_id='fte1-'+('b'*24)}
$generic=@{event='filetransfer_data_frame_dispatched';frame_type='filetransfer.chunk_batch.v4';
    session_id='[redacted]';transfer_id='[redacted]';chunk_index='20-21'}
$passed=0
function CheckRange($Fields,$Selected,[bool]$Expected,[string]$Case) {
    $before=ConvertTo-Json @($Fields,$Selected) -Depth 8 -Compress
    $actual=DGenericExcluded $Fields $Selected
    YRequire ($actual -is [bool] -and $actual -eq $Expected) ('dispatch_range_control:'+ $Case+':'+$actual)
    YRequire ($before -ceq (ConvertTo-Json @($Fields,$Selected) -Depth 8 -Compress)) 'dispatch_range_control_mutated_input'
    $script:passed++
}
# All observations have ambiguous/redacted scope. The range itself must suffice
# or remain unproven; no nearby record, transport label or identity is borrowed.
foreach($range in @('20-21','9-11','14-16','0-0','11-11','14-14','1499997-1499999','1499999-1499999')) {
    $f=$generic.Clone();$f.chunk_index=$range;CheckRange $f $selected $true $range
}
foreach($range in @('12-13','11-12','13-14','11-13','12-14','12-12','13-13',
    '','(none)','(unknown)','20','20-19','020-021','+20-21','-1-0','20 -21','20-21 ',' 20-21',
    "20-21`n",'20-21-22','20.0-21','2e1-21','20,21','٢٠-٢١','20-23','1500000-1500000',
    '1499999-1500000','2147483647-2147483647','9223372036854775807-9223372036854775807')) {
    $f=$generic.Clone();$f.chunk_index=$range;CheckRange $f $selected $false $range
}
$f=$generic.Clone();$f.Remove('chunk_index');CheckRange $f $selected $false 'missing_range'
$f=$generic.Clone();$f.chunk_index=20;CheckRange $f $selected $false 'non_string_range'
$f=$generic.Clone();$f['__nlink_duplicate_structured_field']='chunk_index';CheckRange $f $selected $false 'duplicate_range'
foreach($key in @('security_payload_fingerprint','data_path_envelope_fingerprint','publication_evidence_boundary_id','start_chunk_index')) {
    $f=$generic.Clone();$f[$key]='unexpected';CheckRange $f $selected $false ('enriched_record:'+ $key)
}
foreach($frame in @('','(none)','(unknown)','garbage','FILETRANSFER.CHUNK_BATCH.V4','filetransfer.feedback.v4')) {
    $f=$generic.Clone();$f.frame_type=$frame;CheckRange $f $selected $false ('frame:'+ $frame)
}
foreach($event in @('filetransfer_data_path_receive_result','filetransfer_tuna_data_path_peer_visible','')) {
    $f=$generic.Clone();$f.event=$event;CheckRange $f $selected $false ('event:'+ $event)
}
foreach($scope in @('', '   ', '(unknown)', 'sess_[redacted]')) {
    $f=$generic.Clone();$f.session_id=$scope;CheckRange $f $selected $true ('disjoint_redacted_scope:'+ $scope)
    $f.chunk_index='12-13';CheckRange $f $selected $false ('overlap_redacted_scope:'+ $scope)
}
$f=$generic.Clone();$f.Remove('session_id');$f.Remove('transfer_id');CheckRange $f $selected $true 'disjoint_missing_scope'
$f.chunk_index='12-13';CheckRange $f $selected $false 'overlap_missing_scope'
foreach($key in @('chunk_index','start_chunk_index','last_end_chunk_exclusive','frame_chunk_count','batch_chunk_count','raw_payload_bytes','payload_bytes')) {
    $s=$selected.Clone();$s.Remove($key);CheckRange $generic $s $false ('selected_missing:'+ $key)
}
$s=$selected.Clone();$s.chunk_index='10-11';CheckRange $generic $s $false 'selected_inconsistent_range'
$s=$selected.Clone();$s['__nlink_duplicate_structured_field']='chunk_index';CheckRange $generic $s $false 'selected_duplicate'
$s=$selected.Clone();$s.chunk_index='10-13';$s.start_chunk_index='10';$s.frame_chunk_count='4';$s.batch_chunk_count='4'
CheckRange $generic $s $false 'selected_oversized_batch'
$s=$selected.Clone();$s.chunk_index='1500000-1500001';$s.start_chunk_index='1500000';$s.last_end_chunk_exclusive='1500002'
CheckRange $generic $s $false 'selected_outside_protocol'
# Existing independently sufficient known-frame exclusion is unchanged.
$f=$generic.Clone();$f.frame_type='filetransfer.state.v4';$f.chunk_index='(none)';CheckRange $f $selected $true 'known_other_frame'
'dispatch-range-controls='+([ordered]@{Passed=$passed;DetachedOnly=$true;AuthorityEvaluations=0;AcceptanceChanged=$false} | ConvertTo-Json -Compress)
