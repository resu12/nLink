param($Rows,$Receive,$Current,$Chain,$Endpoint,$Natives,$Origins,$Source,$Reader,$SourceOutput,$Writers,$WriterOutput,$WriterLinesRead)
# Constructed structural controls inside the test probe, not new authenticated
# histories. Only cloned rows/native containers are changed; views stay original.
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
function DCloneRows { @(foreach($row in $Rows) { [pscustomobject]@{Position=$row.Position;Fields=$row.Fields.Clone()} }) }
function DPosition([long]$Sequence) {
    (YOne @($Rows | Where-Object { $_.Fields['log_writer_id'] -ceq $Current.writer -and
        $_.Fields['log_writer_sequence'] -ceq [string]$Sequence }) 'dispatch_control_position').Position
}
$before=DPosition ($Chain.Receive.useWriterSequence-1)
$after=DPosition ($Chain.Ignored.useWriterSequence+1)
$peerPosition=DPosition ($Chain.Ignored.useWriterSequence+2)
$complete=YOne @($Rows | Where-Object { $_.Fields['log_writer_id'] -ceq $Current.writer -and
    $_.Fields.event -ceq 'filetransfer_coordinator_snapshot_stream_completed' }) 'dispatch_control_completion'
$late=DPosition ([long]$complete.Fields.log_writer_sequence+1)
$older=YOne @($Rows | Where-Object { $_.Fields['log_writer_id'] -ceq $Current.writer -and
    $_.Fields.event -ceq 'filetransfer_data_path_receive_result' -and $_.Fields['publication_use_kind'] -ceq 'receive_dispatch' -and
    $_.Fields['publication_evidence_boundary_id'] -cne $Current.boundaryId } | Select-Object -First 1) 'dispatch_control_old_publication'
function DInsert($Copy,[int]$Position) {
    $f=$Receive.Fields.Clone(); $f.log_writer_sequence=$Copy[$Position].Fields.log_writer_sequence
    $f.publication_use_kind='receive_dispatch'; $f.receive_disposition='dispatch_entered'; $f.Remove('authority_effect')
    $Copy[$Position].Fields=$f
}
function DGenericRange($Copy,[int]$Position,[string]$Range) {
    $old=$Copy[$Position].Fields; $f=@{}
    foreach($key in $old.Keys) {
        if($key.StartsWith('log_writer_') -or $key -cin @('session_id','transfer_id','frame_type','lane')) { $f[$key]=$old[$key] }
    }
    $f.event='filetransfer_data_frame_dispatched'; $f.chunk_index=$Range
    $Copy[$Position].Fields=$f
}
function DRun($Copy,$End=$Endpoint) {
    # Controls replace only dispatch-surface rows. Keep the other original raw
    # strings, especially large Snapshots, instead of serializing the complete
    # capture twice per control. The array is detached and shared by both views.
    $lines=$Natives.full.Local.Lines.Clone()
    for($i=0;$i -lt $Copy.Count;$i++) {
        $row=$Copy[$i]
        if($row.Fields['event'] -cin @('filetransfer_data_path_receive_result','filetransfer_tuna_data_path_peer_visible',
            'filetransfer_data_frame_dispatched')) {
            $lines[$i]=($row.Fields.Keys | Sort-Object | ForEach-Object { $_+'='+$row.Fields[$_] }) -join '; '
        }
    }
    $results=@{}
    foreach($prefix in @('full','compact')) {
        $native=$Natives[$prefix].Clone(); $native.Local=$native.Local.Clone()
        $native.Local.Lines=$lines
        $results[$prefix]=Get-PublicationDispatchWindow $Copy $Receive $Current $Chain $End $native
    }
    DParity $results.full.Result $results.compact.Result
    $results.full.Result
}
$positive=[ordered]@{}
foreach($case in @('before_ignore','after_ignore','after_snapshot_completion','later_observed_state','peer_companion',
    'other_payload','other_publication','other_writer','unrelated_generic','other_generic_scope',
    'generic_range_before','generic_range_after','generic_range_redacted')) {
    $copy=DCloneRows; $position=$after
    if($case -ceq 'before_ignore') { $position=$before }
    if($case -ceq 'after_snapshot_completion') { $position=$late }
    DInsert $copy $position; $f=$copy[$position].Fields; $expected=1
    switch($case) {
        'later_observed_state' { $f.publication_evidence_observed_state_generation=[string]([long]$Current.authoredStateGeneration+1) }
        'peer_companion' {
            $f.actual_transport='tuna'
            DInsert $copy $peerPosition; $copy[$peerPosition].Fields.event='filetransfer_tuna_data_path_peer_visible'
            $copy[$peerPosition].Fields.proof_direction='receive'; $copy[$peerPosition].Fields.actual_transport='tuna'
        }
        'other_payload' { $f.security_payload_fingerprint='ftpay1-'+('0'*64); $f.data_path_envelope_fingerprint='ftenv1-'+('0'*64); $expected=0 }
        'other_publication' {
            foreach($key in @($older.Fields.Keys)) {
                if($key.StartsWith('publication_') -or $key -cin @('route','protocol_version','live_route_epoch','runtime_generation',
                    'terminal_generation','leg_generation','route_publication_generation','route_commit_generation','continuity_coordinator_id',
                    'data_session_generation','data_session_identity')) { $f[$key]=$older.Fields[$key] }
            }
            $expected=0
        }
        'other_writer' { $f.log_writer_id='detached_other_writer'; $expected=0 }
        'unrelated_generic' { $f.event='filetransfer_data_frame_dispatched'; $f.frame_type='filetransfer.state.v4'; $expected=0 }
        'other_generic_scope' { $f.event='filetransfer_data_frame_dispatched'; $f.session_id='detached-other-session'; $expected=0 }
        'generic_range_before' { DGenericRange $copy $position '9-11'; $expected=0 }
        'generic_range_after' { DGenericRange $copy $position '20-21'; $expected=0 }
        'generic_range_redacted' {
            DGenericRange $copy $position '20-21'
            $copy[$position].Fields.session_id='[redacted]'; $copy[$position].Fields.transfer_id='[redacted]'; $expected=0
        }
    }
    $result=DRun $copy
    YRequire ($result.MatchingDispatchCount -eq $expected -and
        $result.Status -ceq $(if($expected) {'observed_matching_bound_dispatch'} else {'no_matching_bound_dispatch_observed_in_capture'})) ('dispatch_positive:'+ $case)
    if($case -ceq 'peer_companion') { YRequire ($result.PeerVisibleCompanionCount -eq 1) 'dispatch_peer_double_count' }
    $positive[$case]=$result.Status
}
$negative=[ordered]@{}
foreach($case in @('orphan_peer','ambiguous_peer','duplicate_peer','generic_unbound','generic_redacted_scope','duplicate_origin',
    'duplicate_sequence','payload_other_envelope','envelope_other_payload','missing_payload','missing_disposition','wrong_disposition',
    'missing_range','bad_range','duplicate_field','installation','security_sequence','scope_conflict','unsupported_state',
    'route_conflict','operation_conflict','token_conflict','before_boundary','early_endpoint','foreign_endpoint',
    'guard_other_envelope','guard_missing_payload','peer_wrong_direction','peer_wrong_transport','receive_not_received',
    'generic_missing_frame','generic_none_frame','generic_unknown_frame','generic_invalid_frame','generic_unsupported_frame',
    'generic_wrong_case_frame','generic_whitespace_scope','generic_unknown_scope','generic_partial_redaction_scope',
    'generic_range_overlap_left','generic_range_overlap_right','generic_range_missing','generic_range_overflow')) {
    $copy=DCloneRows; DInsert $copy $after; $f=$copy[$after].Fields; $end=$Endpoint
    switch($case) {
        'orphan_peer' { $f.event='filetransfer_tuna_data_path_peer_visible'; $f.proof_direction='receive'; $f.actual_transport='tuna' }
        'ambiguous_peer' {
            $f.actual_transport='tuna'
            DInsert $copy $before; DInsert $copy $peerPosition
            $copy[$before].Fields.actual_transport='tuna'
            $copy[$peerPosition].Fields.event='filetransfer_tuna_data_path_peer_visible'; $copy[$peerPosition].Fields.proof_direction='receive'; $copy[$peerPosition].Fields.actual_transport='tuna'
        }
        'duplicate_peer' {
            $f.actual_transport='tuna'
            foreach($p in @($peerPosition,$late)) { DInsert $copy $p; $copy[$p].Fields.event='filetransfer_tuna_data_path_peer_visible'; $copy[$p].Fields.proof_direction='receive'; $copy[$p].Fields.actual_transport='tuna' }
        }
        'generic_unbound' { $f.event='filetransfer_data_frame_dispatched' }
        'generic_redacted_scope' { $f.event='filetransfer_data_frame_dispatched'; $f.session_id='sess_[redacted]'; $f.transfer_id='[redacted]' }
        'duplicate_origin' { $copy[$after].Position=$copy[$before].Position }
        'duplicate_sequence' { $f.log_writer_sequence=$Receive.Fields.log_writer_sequence }
        'payload_other_envelope' { $f.data_path_envelope_fingerprint='ftenv1-'+('0'*64) }
        'envelope_other_payload' { $f.security_payload_fingerprint='ftpay1-'+('0'*64) }
        'missing_payload' { $f.Remove('security_payload_fingerprint') }
        'missing_disposition' { $f.Remove('receive_disposition') }
        'wrong_disposition' { $f.receive_disposition='rejected_before_dispatch' }
        'missing_range' { $f.Remove('frame_chunk_count') }
        'bad_range' { $f.last_end_chunk_exclusive=$f.start_chunk_index }
        'duplicate_field' { $f['__nlink_duplicate_structured_field']='security_sequence' }
        'installation' { $f.security_installation_id='ftsi1-'+('0'*32) }
        'security_sequence' { $f.security_sequence='99999' }
        'scope_conflict' { $f.data_path_session_id='fte1-'+('0'*24) }
        'unsupported_state' { $f.publication_evidence_observed_state_generation='99999' }
        'route_conflict' { $f.route='file_tuna_v6' }
        'operation_conflict' { $f.publication_evidence_operation_key='foreign' }
        'token_conflict' { $f.publication_evidence_token_fingerprint='fte1-'+('0'*24) }
        'before_boundary' {
            $p=(YOne @($Rows | Where-Object { $_.Fields['log_writer_id'] -ceq $Current.writer -and
                [long]$_.Fields['log_writer_sequence'] -lt $Current.boundaryWriterSequence } | Select-Object -Last 1) 'dispatch_control_early').Position
            DInsert $copy $p
        }
        'early_endpoint' { $end=[pscustomobject]@{WriterId=$Endpoint.WriterId;LastSequence=$Chain.Ignored.useWriterSequence} }
        'foreign_endpoint' { $end=[pscustomobject]@{WriterId='foreign';LastSequence=$Endpoint.LastSequence} }
        'guard_other_envelope' { $f.publication_use_kind='receive_guard'; $f.receive_disposition='rejected_before_dispatch'; $f.authority_effect='none'; $f.data_path_envelope_fingerprint='ftenv1-'+('0'*64) }
        'guard_missing_payload' { $f.publication_use_kind='receive_guard'; $f.receive_disposition='rejected_before_dispatch'; $f.authority_effect='none'; $f.Remove('security_payload_fingerprint') }
        'peer_wrong_direction' { $f.event='filetransfer_tuna_data_path_peer_visible'; $f.proof_direction='send'; $f.actual_transport='tuna' }
        'peer_wrong_transport' { $f.event='filetransfer_tuna_data_path_peer_visible'; $f.proof_direction='receive' }
        'receive_not_received' { $f.delivery_result='sent' }
        'generic_missing_frame' { $f.event='filetransfer_data_frame_dispatched'; $f.Remove('frame_type') }
        'generic_none_frame' { $f.event='filetransfer_data_frame_dispatched'; $f.frame_type='(none)' }
        'generic_unknown_frame' { $f.event='filetransfer_data_frame_dispatched'; $f.frame_type='(unknown)' }
        'generic_invalid_frame' { $f.event='filetransfer_data_frame_dispatched'; $f.frame_type='garbage' }
        'generic_unsupported_frame' { $f.event='filetransfer_data_frame_dispatched'; $f.frame_type='filetransfer.feedback.v4' }
        'generic_wrong_case_frame' { $f.event='filetransfer_data_frame_dispatched'; $f.frame_type='FILETRANSFER.STATE.V4' }
        'generic_whitespace_scope' { $f.event='filetransfer_data_frame_dispatched'; $f.session_id='   '; $f.transfer_id='[redacted]' }
        'generic_unknown_scope' { $f.event='filetransfer_data_frame_dispatched'; $f.session_id='(unknown)'; $f.transfer_id='[redacted]' }
        'generic_partial_redaction_scope' { $f.event='filetransfer_data_frame_dispatched'; $f.session_id='sess_[redacted]'; $f.transfer_id='[unknown]' }
        'generic_range_overlap_left' { DGenericRange $copy $after '11-12' }
        'generic_range_overlap_right' { DGenericRange $copy $after '13-14' }
        'generic_range_missing' { DGenericRange $copy $after ''; $copy[$after].Fields.Remove('chunk_index') }
        'generic_range_overflow' { DGenericRange $copy $after '2147483647-2147483647' }
    }
    $reason=''; try { $null=DRun $copy $end } catch { $reason=$_.Exception.Message }
    YRequire (-not [string]::IsNullOrWhiteSpace($reason)) ('dispatch_negative_admitted:'+ $case)
    if($case -clike 'generic_*') { YRequire ($reason -ceq 'dispatch_unbound_generic_observation') ('dispatch_generic_reason:'+ $case) }
    $negative[$case]=$reason
}
function DNegative([string]$Name,[scriptblock]$Action,[string]$Expected) {
    $reason=''; try { & $Action } catch { $reason=$_.Exception.Message }
    YRequire ($reason -ceq $Expected) ('dispatch_control_reason:'+ $Name+':'+$reason)
    $negative[$Name]=$reason
}
$badOrigins=$Origins.Clone(); $badOrigins[0]++
DNegative 'source_origins' { DSourceCoverage $badOrigins $Source $Reader $SourceOutput } 'dispatch_source_origins'
DNegative 'source_subset' { DSourceCoverage $Origins[1..($Origins.Length-1)] $Source $Reader $SourceOutput } 'dispatch_source_coverage'
DNegative 'source_incomplete' { DSourceCoverage $Origins $Source ([pscustomobject]@{InputReadComplete=$false;LinesRead=$Reader.LinesRead}) $SourceOutput } 'dispatch_source_coverage'
$foreignSource=[pscustomobject]@{Path='foreign';Sha256=$SourceOutput.Sha256;ByteCount=$SourceOutput.ByteCount;LineCount=$SourceOutput.LineCount}
DNegative 'source_output' { DSourceCoverage $Origins $Source $Reader $foreignSource } 'dispatch_source_coverage'
DNegative 'missing_endpoint' { DCaptureEnd @() $Current.writer $WriterOutput $WriterLinesRead } 'dispatch_capture_descriptor'
DNegative 'foreign_writer' { DCaptureEnd $Writers 'foreign' $WriterOutput $WriterLinesRead } 'dispatch_writer_endpoint'
DNegative 'incomplete_writer_read' { DCaptureEnd $Writers $Current.writer $WriterOutput ($WriterLinesRead-1) } 'dispatch_capture_descriptor'
$shortWriters=@($Writers | ForEach-Object { [pscustomobject]@{WriterId=$_.WriterId;LastSequence=$_.LastSequence-1} })
DNegative 'early_eof_metadata' { DCaptureEnd $shortWriters $Current.writer $WriterOutput $WriterLinesRead } 'dispatch_capture_count'
$wrongOrder=$Current.Clone(); $wrongOrder.effectWriterSequence=$wrongOrder.boundaryWriterSequence
DNegative 'interval_order' { DInterval $wrongOrder $Chain $Endpoint } 'dispatch_interval_order'
DNegative 'parity' { DParity @{Status='one'} @{Status='two'} } 'dispatch_parity'
$witnesses=[Collections.Generic.HashSet[int]]::new(); foreach($i in 1..64) { [void]$witnesses.Add($i) }
DWitnessBound $witnesses; [void]$witnesses.Add(64); DWitnessBound $witnesses; [void]$witnesses.Add(65)
DNegative 'shared_witness_overflow' { DWitnessBound $witnesses } 'dispatch_witness_limit'
[ordered]@{Kind='detached_structural_query_controls';Positive=$positive;Negative=$negative;AuthorityEvaluations=0;
    GenuineDispatchHistory=$false;OriginalInputMutation=$false}
