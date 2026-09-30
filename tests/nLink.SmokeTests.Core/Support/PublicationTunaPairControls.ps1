param($Rows,$Native,$View,$Sender,$Receiver,$Work)
# Detached mutations of the genuine fixture, never replacement authority.
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$positive=Get-TunaPublicationPayloadGate $Rows $Native $View $Sender $Receiver 0 $Work
YRequire ($positive.Status -ceq 'pass') 'tuna_pair_control_positive'
$sendPosition=$positive.Pair.Send.NativePosition; $receivePosition=$positive.Pair.Receive.NativePosition
$passed=0
foreach($case in @('missing_receive','wrong_range','wrong_identity','wrong_envelope','wrong_byte_count','not_sent',
    'not_dispatched','not_received','swapped_activation','superseded_publication','terminal_boundary','missing_forwarding','duplicate_receive','incomplete_view','budget')) {
    TWork $Work
    $copy=@(foreach($row in $Rows) { [pscustomobject]@{Position=$row.Position;Fields=$row.Fields.Clone()} })
    $nativeCopy=$Native.Clone(); $nativeCopy.Local=$Native.Local.Clone(); $nativeCopy.Local.Lines=$Native.Local.Lines.Clone()
    $viewCopy=$View; $workCopy=$Work; $changed=$receivePosition
    $f=$copy[$receivePosition].Fields; $expected=''
    switch($case) {
        'missing_receive' { $f.event='omitted_for_detached_control'; $expected='tuna_pair_receive_missing' }
        'wrong_range' { $f.start_chunk_index='17'; $f.last_end_chunk_exclusive='19'; $expected='tuna_pair_exact_payload:start_chunk_index' }
        'wrong_identity' { $f.security_payload_fingerprint='ftpay1-'+('a'*64); $expected='tuna_pair_exact_payload:security_payload_fingerprint' }
        'wrong_envelope' { $f.data_path_envelope_fingerprint='ftenv1-'+('b'*64); $expected='tuna_pair_receive_missing' }
        'wrong_byte_count' { $f.raw_payload_bytes='2049'; $expected='tuna_pair_exact_payload:raw_payload_bytes' }
        'not_sent' { $changed=$sendPosition; $copy[$sendPosition].Fields.delivery_result='queued'; $expected='tuna_pair_send_not_successful' }
        'not_dispatched' { $f.receive_disposition='rejected_before_dispatch'; $expected='tuna_pair_receive_not_dispatched' }
        'not_received' { $f.delivery_result='pending'; $expected='tuna_pair_receive_not_dispatched' }
        'swapped_activation' { $f.publication_evidence_boundary_id=$Receiver.boundaryId; $expected='carrier_forwarding_unproven' }
        'superseded_publication' { $f.log_writer_sequence=[string]($Receiver.boundaryWriterSequence+1); $expected='tuna_pair_publication_superseded' }
        'terminal_boundary' {
            $viewCopy=if($View -is [Collections.IDictionary]) {$View.Clone()} else {$View.PSObject.Copy()}
            $original=(TPublications $View $Receiver)[0].Boundary
            $terminal=if($original -is [Collections.IDictionary]) {$original.Clone()} else {$original.PSObject.Copy()}
            $terminal.afterBoundary=if($original.afterBoundary -is [Collections.IDictionary]) {$original.afterBoundary.Clone()} else {$original.afterBoundary.PSObject.Copy()}
            $terminal.afterBoundary.terminalGeneration=1; $terminal.operations=@()
            $terminal.writerSequence=[string]($positive.Pair.Receive.ForwardingSequence+1)
            $viewCopy.Boundaries=@($View.Boundaries)+@($terminal)
            $expected='tuna_pair_publication_superseded'
        }
        'missing_forwarding' {
            $nativeCopy.Effects=@($Native.Effects | Where-Object { $_.TransitionId -cne $positive.Pair.Receive.Publication -or $_.Writer -cne $Receiver.writer })
            $expected='carrier_forwarding_unproven'
        }
        'duplicate_receive' { $copy+=([pscustomobject]@{Position=$receivePosition;Fields=$f.Clone()}); $expected='tuna_pair_receive_ambiguous' }
        'incomplete_view' {
            $viewCopy=if($View -is [Collections.IDictionary]) {$View.Clone()} else {$View.PSObject.Copy()}
            $viewCopy.StreamComplete=$false; $expected='tuna_pair_incomplete_view'
        }
        'budget' { $workCopy=[pscustomobject]@{ElapsedMilliseconds=20001}; $expected='tuna_pair_work_limit' }
    }
    $fields=$copy[$changed].Fields
    $nativeCopy.Local.Lines[$changed]=($fields.Keys | Sort-Object | ForEach-Object { $_+'='+$fields[$_] }) -join '; '
    $r=Get-TunaPublicationPayloadGate $copy $nativeCopy $viewCopy $Sender $Receiver 0 $workCopy
    YRequire ($r.Status -ceq 'unproven' -and $null -eq $r.Pair -and $r.FirstMissingEdge -ceq $expected) ('tuna_pair_control:'+ $case+':'+$r.FirstMissingEdge)
    $passed++
}
$second=Get-TunaPublicationPayloadGate $Rows $Native $View $Sender $Receiver 1 $Work
YRequire ($second.Status -ceq 'pass' -and $second.Pair.Send.Publication -cne $positive.Pair.Send.Publication -and
    $second.Pair.Receive.Publication -cne $positive.Pair.Receive.Publication) 'tuna_pair_control_distinct_activations'
$withoutSecond=@($Rows | Where-Object { $_.Position -ne $second.Pair.Send.NativePosition })
$r=Get-TunaPublicationPayloadGate $withoutSecond $Native $View $Sender $Receiver 1 $Work
YRequire ($r.Status -ceq 'unproven' -and $r.FirstMissingEdge -ceq 'tuna_pair_send_missing') 'tuna_pair_control_first_cannot_replace_second'
$passed++
$r=Merge-TunaPayloadGate $positive $second
YRequire ($r.Status -ceq 'unproven' -and $r.FirstMissingEdge -ceq 'tuna_pair_parity' -and $null -eq $r.Pair) 'tuna_pair_control_parity'
$passed++
[ordered]@{Passed=$passed;DetachedOnly=$true;AuthorityEvaluations=0;AcceptanceChanged=$false}
