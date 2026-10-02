param($Rows,$Native,$View,[int]$FailurePosition,$Endpoints,$Work,$Budget,$Positive)
# Detached query controls only. No replacement owner or authority evaluation.
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
function CRaw($Fields) {
    $keys=@('content_evidence_profile','content_scope_id','content_evidence_state','content_segment_count')
    foreach($slot in 0..2) { foreach($name in @('index','bytes','commitment')) { $keys+=('content_segment_'+$slot+'_'+$name) } }
    $body=@($Fields.Keys | Where-Object { $_ -cnotmatch '^content_' -and $_ -cnotmatch '^log_writer_' } | Sort-Object)
    $content=@($keys | Where-Object { $Fields.ContainsKey($_) })+@($Fields.Keys | Where-Object { $_ -cmatch '^content_' -and $_ -cnotin $keys } | Sort-Object)
    $writer=@('log_writer_id')+@($Fields.Keys | Where-Object { $_ -cmatch '^log_writer_' -and $_ -cne 'log_writer_id' } | Sort-Object)
    (@($body)+@($content)+@($writer) | ForEach-Object { $_+'='+$Fields[$_] }) -join '; '
}
$sendPosition=[int]$Positive.Coverage.TunaSend.NativePosition
$receivePosition=[int]$Positive.Coverage.TunaReceive.NativePosition
$sourcePosition=(YOne @($Rows | Where-Object { $_.Fields['log_writer_id'] -ceq $Positive.Coverage.OldSend.writer -and
    $_.Fields['log_writer_sequence'] -ceq [string]$Positive.Coverage.OldSend.useWriterSequence }) 'content_control_old_source').Position
$slot=[int]($Positive.Coverage.ChunkIndex-[long]$Rows[$sendPosition].Fields.start_chunk_index)
$prefix='content_segment_'+$slot
$expected=@{missing_content='content_suffix';profile='content_suffix';malformed_commitment='content_suffix';retired='content_suffix';
    duplicate_field='security_span_duplicate_fields';unknown_field='content_suffix';extra_slot='content_suffix';
    wrong_index='content_segment_shape';wrong_length='content_segment_total';wrong_sum='content_segment_total';wrong_count='content_fields';
    noncontiguous='content_segment_shape';overflow=('rejection_integer:'+ $prefix+'_bytes');wrong_scope='content_sender_scope:content_scope_id';
    changed_content='content_not_equal';missing_receive='content_tuna_receive_missing_or_ambiguous';
    not_dispatched='tuna_pair_receive_not_dispatched';not_sent='tuna_pair_send_not_successful';wrong_envelope='content_tuna_receive_missing_or_ambiguous';
    wrong_payload='content_tuna_envelope:security_payload_fingerprint';changed_installation='security_span_identity:security_installation_id';
    missing_forwarding='carrier_forwarding_unproven';incomplete_view='content_view';multiple_containing='content_containing_send_ambiguous';
    superseded='tuna_pair_publication_superseded';terminal='tuna_pair_publication_superseded';work_budget='content_work_limit';input_budget='content_input_budget';
    record_cap='content_bounds';boundary_cap='content_bounds';old_publication='content_old_publication_superseded'}
$passed=0
foreach($case in @('missing_content','profile','malformed_commitment','retired','duplicate_field','unknown_field','extra_slot',
    'wrong_index','wrong_length','wrong_sum','wrong_count','noncontiguous','overflow','wrong_scope','changed_content',
    'missing_receive','late_receive','not_dispatched','not_sent','wrong_envelope','wrong_payload','changed_installation',
    'missing_forwarding','incomplete_view','multiple_containing','superseded','terminal','work_budget','input_budget','record_cap','boundary_cap','old_publication')) {
    CWork $Work $Budget
    $copy=@(foreach($row in $Rows) { [pscustomobject]@{Position=$row.Position;Fields=$row.Fields.Clone()} })
    $nativeCopy=$Native.Clone(); $nativeCopy.Local=$Native.Local.Clone(); $nativeCopy.Local.Lines=$Native.Local.Lines.Clone()
    $viewCopy=$View; $workCopy=$Work; $budgetCopy=$Budget; $changed=$sendPosition
    $f=$copy[$sendPosition].Fields
    switch($case) {
        'missing_content' { foreach($key in @($f.Keys | Where-Object { $_ -cmatch '^content_' })) { $f.Remove($key) } }
        'profile' { $f.content_evidence_profile='other' }
        'malformed_commitment' { $f[($prefix+'_commitment')]='ftcc1-'+('a'*63) }
        'retired' { $f.content_evidence_state='retired' }
        'duplicate_field' { $f['__nlink_duplicate_structured_field']='content_scope_id' }
        'unknown_field' { $f['content_unrecognized']='1' }
        'extra_slot' { $f['content_segment_3_index']='15' }
        'wrong_index' { $f[($prefix+'_index')]='15' }
        'wrong_length' { $f[($prefix+'_bytes')]='1025' }
        'wrong_sum' { $f['content_segment_2_bytes']='1025' }
        'wrong_count' { $f.content_segment_count='2' }
        'noncontiguous' { $f['content_segment_2_index']='20' }
        'overflow' { $f[($prefix+'_bytes')]='9223372036854775808' }
        'wrong_scope' { $f.content_scope_id='ftcs1-'+('a'*32) }
        'changed_content' { $f[($prefix+'_commitment')]='ftcc1-'+('a'*64) }
        'missing_receive' { $changed=$receivePosition; $copy[$changed].Fields.event='omitted_for_detached_control' }
        'late_receive' { $changed=$receivePosition; $copy[$changed].Fields.log_writer_sequence=[string]([long]$Rows[$FailurePosition].Fields.log_writer_sequence+3) }
        'not_dispatched' { $changed=$receivePosition; $copy[$changed].Fields.receive_disposition='rejected_before_dispatch' }
        'not_sent' { $f.delivery_result='queued' }
        'wrong_envelope' { $changed=$receivePosition; $copy[$changed].Fields.data_path_envelope_fingerprint='ftenv1-'+('b'*64) }
        'wrong_payload' { $changed=$receivePosition; $copy[$changed].Fields.security_payload_fingerprint='ftpay1-'+('b'*64) }
        'changed_installation' { $f.security_installation_id='ftsi1-'+('a'*32) }
        'missing_forwarding' { $nativeCopy.Effects=@($Native.Effects | Where-Object { $_.TransitionId -cne $Positive.Coverage.TunaSend.Publication -or $_.Writer -cne $Positive.Coverage.TunaSend.Writer }) }
        'incomplete_view' { $viewCopy=if($View -is [Collections.IDictionary]) {$View.Clone()} else {$View.PSObject.Copy()}; $viewCopy.StreamComplete=$false }
        'multiple_containing' {
            $copy+=([pscustomobject]@{Position=$copy.Count;Fields=$f.Clone()})
            $nativeCopy.Local.Lines+=@(CRaw $f)
        }
        { $_ -in @('superseded','terminal') } {
            $viewCopy=if($View -is [Collections.IDictionary]) {$View.Clone()} else {$View.PSObject.Copy()}
            $original=(TPublications $View $Positive.Coverage.OldSend)[1].Boundary
            $later=if($original -is [Collections.IDictionary]) {$original.Clone()} else {$original.PSObject.Copy()}
            $later.afterBoundary=if($original.afterBoundary -is [Collections.IDictionary]) {$original.afterBoundary.Clone()} else {$original.afterBoundary.PSObject.Copy()}
            if($case -ceq 'terminal') { $later.afterBoundary.terminalGeneration=1 } else { $later.afterBoundary.bridgeRecoveryGeneration=1 }
            $later.operations=@(); $later.writerSequence=[string]($Positive.Coverage.TunaSend.ForwardingSequence+1)
            $viewCopy.Boundaries=@($View.Boundaries)+@($later)
        }
        'work_budget' { $workCopy=[pscustomobject]@{ElapsedMilliseconds=20001} }
        'input_budget' { $budgetCopy=$Budget.Clone(); $budgetCopy.CodeUnits=1073741825L }
        'record_cap' { $copy=@($copy[0])*4097 }
        'boundary_cap' { $viewCopy=if($View -is [Collections.IDictionary]) {$View.Clone()} else {$View.PSObject.Copy()}; $viewCopy.Boundaries=@($View.Boundaries[0])*257 }
        'old_publication' {
            $changed=$sourcePosition
            $copy[$changed].Fields.log_writer_sequence=[string]([long](TPublications $View $Positive.Coverage.OldSend)[0].Boundary.writerSequence+1)
        }
    }
    $nativeCopy.Local.Lines[$changed]=CRaw $copy[$changed].Fields
    $r=Get-PublicationContentCoverage $copy $nativeCopy $viewCopy $FailurePosition $Endpoints $workCopy $budgetCopy
    YRequire ($r.Result.Status -ceq 'unproven' -and $null -eq $r.Result.Coverage -and
        (($case -ceq 'late_receive' -and $r.Result.FirstMissingEdge -cin @('content_local_order','tuna_pair_publication_superseded','carrier_forwarding_unproven')) -or
         ($case -cne 'late_receive' -and $r.Result.FirstMissingEdge -ceq $expected[$case]))) ('content_control:'+ $case+':'+$r.Result.FirstMissingEdge)
    $passed++
}
$altered=[ordered]@{}; foreach($key in $Positive.Keys) { $altered[$key]=$Positive[$key] }
$altered.Coverage=[ordered]@{}; foreach($key in $Positive.Coverage.Keys) { $altered.Coverage[$key]=$Positive.Coverage[$key] }
$altered.Coverage.Bytes++
$r=Merge-PublicationContentCoverage $Positive $altered
YRequire ($r.Status -ceq 'unproven' -and $r.FirstMissingEdge -ceq 'content_parity' -and $null -eq $r.Coverage) 'content_control_parity'
$passed++
# A detached selection control puts a disjoint pair before the containing pair.
# It tests query selection only; none of its rewritten rows acquire source custody.
$copy=@(foreach($row in $Rows) { [pscustomobject]@{Position=$row.Position;Fields=$row.Fields.Clone()} })
$nativeCopy=$Native.Clone(); $nativeCopy.Local=$Native.Local.Clone(); $nativeCopy.Local.Lines=$Native.Local.Lines.Clone()
foreach($position in @($sendPosition,$receivePosition)) {
    $original=$Rows[$position]
    $after=YOne @($Rows | Where-Object { $_.Fields['event'] -ceq 'filetransfer_tuna_data_path_peer_visible' -and
        $_.Fields['log_writer_id'] -ceq $original.Fields.log_writer_id -and
        $_.Fields['data_path_envelope_fingerprint'] -ceq $original.Fields.data_path_envelope_fingerprint }) 'content_control_companion'
    YRequire ($after.Position -gt $position) 'content_control_companion_order'
    $containing=$original.Fields.Clone(); $containing.log_writer_sequence=$after.Fields.log_writer_sequence
    $copy[$after.Position].Fields=$containing; $nativeCopy.Local.Lines[$after.Position]=CRaw $containing
    $disjoint=$copy[$position].Fields
    $disjoint.start_chunk_index='20'; $disjoint.last_end_chunk_exclusive='23'; $disjoint.chunk_index='20-22'
    $disjoint.data_path_envelope_fingerprint='ftenv1-'+('b'*64); $disjoint.security_payload_fingerprint='ftpay1-'+('b'*64)
    foreach($i in 0..2) { if($disjoint.ContainsKey('content_segment_'+$i+'_index')) { $disjoint[('content_segment_'+$i+'_index')]=[string](20+$i) } }
    $nativeCopy.Local.Lines[$position]=CRaw $disjoint
}
$r=Get-PublicationContentCoverage $copy $nativeCopy $View $FailurePosition $Endpoints $Work $Budget
YRequire ($r.Result.Status -ceq 'pass' -and $r.Result.Coverage.TunaSend.NativePosition -gt $sendPosition -and
    $r.Result.Coverage.TunaReceive.NativePosition -gt $receivePosition) ('content_control_disjoint_first:'+ $r.Result.FirstMissingEdge)
$passed++
$tooMany=[Collections.Generic.HashSet[int]]::new(); foreach($i in 1..65) { [void]$tooMany.Add($i) }
$reason=''; try { CWitnesses $tooMany } catch { $reason=$_.Exception.Message }
YRequire ($reason -ceq 'content_witness_limit') 'content_control_witnesses'; $passed++
CWork $Work $Budget
[ordered]@{Passed=$passed;DetachedOnly=$true;AddedAuthorityEvaluations=0;AcceptanceChanged=$false}
