param($Rows,[int[]]$SelectedPositions,$Endpoints,[Diagnostics.Stopwatch]$Work)
# Detached field projections only. Original raw strings, views and custody stay untouched.
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
function SCloneRows { @(foreach($row in $Rows) { [pscustomobject]@{Position=$row.Position;OriginalPosition=$row.Position;Fields=$row.Fields.Clone()} }) }
function SAdd($Copy,$Fields) {
    $items=[Collections.Generic.List[object]]::new(); $added=$false
    $new=[pscustomobject]@{Position=-1;OriginalPosition=-1;Fields=$Fields}
    foreach($row in $Copy) {
        if(-not $added -and $row.Fields['log_writer_id'] -ceq $Fields.log_writer_id -and
            [long]$row.Fields.log_writer_sequence -gt [long]$Fields.log_writer_sequence) { $items.Add($new); $added=$true }
        $items.Add($row)
    }
    if(-not $added) { $items.Add($new) }
    return ,$items.ToArray()
}
function SFree($Copy,[string]$Writer,[long]$Minimum,[long]$Maximum) {
    $used=[Collections.Generic.HashSet[long]]::new()
    foreach($row in $Copy) { if($row.Fields['log_writer_id'] -ceq $Writer) { [void]$used.Add([long]$row.Fields.log_writer_sequence) } }
    for($i=$Minimum;$i -le $Maximum;$i++) { if(-not $used.Contains($i)) { return $i } }
    throw 'security_span_control_no_free_sequence'
}
function SRun($Copy,$Ends=$Endpoints) {
    $map=@{}
    for($i=0;$i -lt $Copy.Count;$i++) { $Copy[$i].Position=$i; if($Copy[$i].OriginalPosition -ge 0) { $map[$Copy[$i].OriginalPosition]=$i } }
    $selected=[int[]]@($SelectedPositions | ForEach-Object { $map[$_] })
    (Get-RecordedSecurityContextSpans $Copy $selected $Ends $Work).Result
}
$positive=[ordered]@{}; $negative=[ordered]@{}
function SNegative([string]$Name,[scriptblock]$Action,[string]$Expected) {
    $reason=''; try { $null=& $Action } catch { $reason=$_.Exception.Message }
    YRequire ($reason -ceq $Expected) ('security_span_control:'+ $Name+':'+$reason)
    $negative[$Name]=$reason
}
$baseline=SRun (SCloneRows)
YRequire ($baseline.Status -ceq 'matched_recorded_security_context_spans') 'security_span_control_baseline'
$positive['original_projection']=$baseline.Status
foreach($peer in 0..1) {
    $usePosition=$SelectedPositions[$(if($peer -eq 0) {0} else {5})]
    $writer=$Rows[$usePosition].Fields.log_writer_id
    $install=YOne @($Rows | Where-Object { $_.Fields['log_writer_id'] -ceq $writer -and $_.Fields['event'] -ceq 'filetransfer_security_context_installed' }) 'security_span_control_install'
    $retire=YOne @($Rows | Where-Object { $_.Fields['log_writer_id'] -ceq $writer -and $_.Fields['event'] -ceq 'filetransfer_security_context_retired' }) 'security_span_control_retire'
    $endpoint=YOne @($Endpoints | Where-Object WriterId -CEQ $writer) 'security_span_control_endpoint'
    foreach($case in @('unrelated_context','later_open_context','later_closed_context')) {
        $copy=SCloneRows; $new=$install.Fields.Clone(); $new.security_installation_id='ftsi1-'+('e'*32); $new.security_installation_generation='2'
        $minimum=[long]$retire.Fields.log_writer_sequence+1; $maximum=[long]$endpoint.LastSequence
        if($case -ceq 'unrelated_context') {
            $new.security_transport_id='ftst1-'+('e'*32); $minimum=[long]$install.Fields.log_writer_sequence+1; $maximum=[long]$retire.Fields.log_writer_sequence-1
        }
        $new.log_writer_sequence=[string](SFree $copy $writer $minimum $maximum); $copy=SAdd $copy $new
        if($case -ceq 'later_closed_context') {
            $last=$new.Clone(); $last.event='filetransfer_security_context_retired'; $last.security_context_status='retired'
            $last.log_writer_sequence=[string](SFree $copy $writer ([long]$new.log_writer_sequence+1) $maximum); $copy=SAdd $copy $last
        }
        $r=SRun $copy; YRequire ($r.Status -ceq $baseline.Status) ('security_span_positive:'+ $case)
        $positive[('peer'+$peer+'_'+$case)]=$r.Status
    }
    foreach($case in @('missing_install','missing_retire','duplicate_install','duplicate_retire','transport','generation','establishment','session',
        'installation','foreign_owner','unknown_writer','unknown_transport','missing_generation','overflow_generation','zero_generation',
        'wrong_case_event','duplicate_field','install_unbound','retire_captured','authority','selected_retired','selected_unbound',
        'selected_overflow_sequence','selected_generation','captured_after_retire','early_endpoint','foreign_endpoint','missing_endpoint',
        'intervening_install','intervening_retire','enclosing_context','selected_id_reuse','generation_reuse')) {
        $copy=SCloneRows; $f=$copy[$install.Position].Fields; $ends=$Endpoints; $expected=''
        switch($case) {
            'missing_install' { $f.event='unrelated'; $expected='security_span_install' }
            'missing_retire' { $copy[$retire.Position].Fields.event='unrelated'; $expected='security_span_retire' }
            'duplicate_install' { $new=$f.Clone(); $new.log_writer_sequence=[string](SFree $copy $writer 1 ([long]$install.Fields.log_writer_sequence-1)); $copy=SAdd $copy $new; $expected='security_span_install' }
            'duplicate_retire' { $new=$copy[$retire.Position].Fields.Clone(); $new.log_writer_sequence=[string](SFree $copy $writer 1 ([long]$install.Fields.log_writer_sequence-1)); $copy=SAdd $copy $new; $expected='security_span_retire' }
            'transport' { $f.security_transport_id='ftst1-'+('0'*32); $expected='security_span_identity:security_transport_id' }
            'generation' { $f.security_installation_generation='2'; $expected='security_span_identity:security_installation_generation' }
            'establishment' { $f.security_establishment_fingerprint='ftest1-'+('0'*64); $expected='security_span_identity:security_establishment_fingerprint' }
            'session' { $f.security_session_id='fte1-'+('0'*24); $expected='security_span_identity:security_session_id' }
            'installation' { $f.security_installation_id='ftsi1-'+('0'*32); $expected='security_span_generation_reused' }
            'foreign_owner' { $f.log_writer_id='p999999r'+('0'*16); $expected='security_span_identity:log_writer_id' }
            'unknown_writer' { $f.log_writer_id='(none)'; $expected='security_span_shape:log_writer_id' }
            'unknown_transport' { $f.security_transport_id='(none)'; $expected='security_span_shape:security_transport_id' }
            'missing_generation' { $f.Remove('security_installation_generation'); $expected='security_span_integer:security_installation_generation' }
            'overflow_generation' { $f.security_installation_generation='9223372036854775808'; $expected='security_span_integer:security_installation_generation' }
            'zero_generation' { $f.security_installation_generation='0'; $expected='security_span_integer:security_installation_generation' }
            'wrong_case_event' { $f.event=$f.event.ToUpperInvariant(); $expected='security_span_event_case' }
            'duplicate_field' { $f['__nlink_duplicate_structured_field']='security_installation_id'; $expected='security_span_duplicate_fields' }
            'install_unbound' { $f.security_context_status='unbound'; $expected='security_span_install_status' }
            'retire_captured' { $copy[$retire.Position].Fields.security_context_status='captured'; $expected='security_span_lifecycle_status' }
            'authority' { $f.authority_effect='granted'; $expected='security_span_lifecycle_status' }
            'selected_retired' { $copy[$usePosition].Fields.security_context_status='retired'; $expected='security_span_selected_status' }
            'selected_unbound' { $copy[$usePosition].Fields.security_context_status='unbound'; $expected='security_span_selected_status' }
            'selected_overflow_sequence' { $copy[$usePosition].Fields.security_sequence='9223372036854775808'; $expected='security_span_integer:security_sequence' }
            'selected_generation' { $copy[$usePosition].Fields.security_installation_generation='2'; $expected='security_span_identity:security_installation_generation' }
            'captured_after_retire' {
                $moved=$copy[$usePosition]; $copy=@($copy | Where-Object OriginalPosition -NE $usePosition)
                $moved.Fields.log_writer_sequence=[string](SFree $copy $writer ([long]$retire.Fields.log_writer_sequence+1) $endpoint.LastSequence)
                $copy=SAdd $copy $moved.Fields; $copy[-1].OriginalPosition=$usePosition; $expected='security_span_use_outside_interval'
            }
            'early_endpoint' { $ends=@($Endpoints | ForEach-Object { [pscustomobject]@{WriterId=$_.WriterId;LastSequence=$(if($_.WriterId -ceq $writer) {[long]$retire.Fields.log_writer_sequence-1} else {$_.LastSequence})} }); $expected='security_span_writer_order' }
            'foreign_endpoint' { $ends=@($Endpoints | ForEach-Object { [pscustomobject]@{WriterId=$(if($_.WriterId -ceq $writer) {'foreign'} else {$_.WriterId});LastSequence=$_.LastSequence} }); $expected='security_span_endpoint' }
            'missing_endpoint' { $ends=@($Endpoints | Where-Object WriterId -CNE $writer); $expected='security_span_writer_scope' }
            {$_ -in @('intervening_install','intervening_retire','enclosing_context','selected_id_reuse','generation_reuse')} {
                $new=$f.Clone(); $new.security_installation_id='ftsi1-'+('e'*32); $new.security_installation_generation='2'
                $minimum=[long]$install.Fields.log_writer_sequence+1; $maximum=[long]$retire.Fields.log_writer_sequence-1
                $expected='security_span_intervening_context'
                if($case -ceq 'intervening_retire') { $new.event='filetransfer_security_context_retired'; $new.security_context_status='retired' }
                if($case -ceq 'enclosing_context') { $minimum=1; $maximum=[long]$install.Fields.log_writer_sequence-1; $expected='security_span_other_context_overlap' }
                if($case -ceq 'selected_id_reuse') { $new.security_installation_id=$f.security_installation_id; $new.security_transport_id='ftst1-'+('e'*32); $expected='security_span_identity:security_transport_id' }
                if($case -ceq 'generation_reuse') { $new.security_installation_generation=$f.security_installation_generation; $expected='security_span_generation_reused' }
                $new.log_writer_sequence=[string](SFree $copy $writer $minimum $maximum); $copy=SAdd $copy $new
                if($case -ceq 'enclosing_context') {
                    $last=$new.Clone(); $last.event='filetransfer_security_context_retired'; $last.security_context_status='retired'
                    $last.log_writer_sequence=[string](SFree $copy $writer ([long]$retire.Fields.log_writer_sequence+1) $endpoint.LastSequence); $copy=SAdd $copy $last
                }
            }
        }
        SNegative ('peer'+$peer+'_'+$case) { SRun $copy $ends } $expected
    }
}
$bad=SCloneRows; $bad[0].Position++
SNegative 'origins' { Get-RecordedSecurityContextSpans $bad $SelectedPositions $Endpoints $Work } 'security_span_origins'
SNegative 'selection' { Get-RecordedSecurityContextSpans $Rows $SelectedPositions[0..6] $Endpoints $Work } 'security_span_selection'
SNegative 'missing_work' { Get-RecordedSecurityContextSpans $Rows $SelectedPositions $Endpoints $null } 'security_span_work_limit'
SNegative 'parity' { SParity @{Status='one'} @{Status='two'} } 'security_span_parity'
$witnesses=[Collections.Generic.HashSet[int]]::new(); foreach($i in 1..64) { [void]$witnesses.Add($i) }
SWitnessBound $witnesses; [void]$witnesses.Add(64); SWitnessBound $witnesses; [void]$witnesses.Add(65)
SNegative 'shared_witness_overflow' { SWitnessBound $witnesses } 'security_span_witness_limit'
[ordered]@{Kind='detached_recorded_span_controls';Positive=$positive;Negative=$negative;
    AuthorityEvaluations=0;GenuineSecurityHistory=$false;OriginalInputMutation=$false}
