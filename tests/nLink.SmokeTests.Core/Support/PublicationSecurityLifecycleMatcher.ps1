# Test-only recorded-span query. No reader, evaluator, or authority capability.
function SInteger($Fields,[string]$Name) {
    $value=0L
    YRequire ($Fields.ContainsKey($Name) -and $Fields[$Name] -cmatch '^[1-9][0-9]*$' -and
        [long]::TryParse($Fields[$Name],[ref]$value) -and $value -gt 0) ('security_span_integer:'+ $Name)
    $value
}
function SIdentity($Fields) {
    YRequire (-not $Fields.ContainsKey('__nlink_duplicate_structured_field')) 'security_span_duplicate_fields'
    foreach($pair in @(@('log_writer_id','^p[1-9][0-9]*r[0-9a-f]{16}$'),
        @('security_transport_id','^ftst1-[0-9a-f]{32}$'),@('security_installation_id','^ftsi1-[0-9a-f]{32}$'))) {
        YRequire ($Fields.ContainsKey($pair[0]) -and $Fields[$pair[0]] -cmatch $pair[1]) ('security_span_shape:'+ $pair[0])
    }
    [void](SInteger $Fields 'log_writer_sequence'); [void](SInteger $Fields 'security_installation_generation')
}
function SContext($Fields) {
    SIdentity $Fields
    foreach($pair in @(@('security_establishment_fingerprint','^ftest1-[0-9a-f]{64}$'),@('security_session_id','^fte1-[0-9a-f]{24}$'))) {
        YRequire ($Fields.ContainsKey($pair[0]) -and $Fields[$pair[0]] -cmatch $pair[1]) ('security_span_shape:'+ $pair[0])
    }
    $tuple=[ordered]@{}
    foreach($key in @('log_writer_id','security_transport_id','security_installation_id','security_installation_generation',
        'security_establishment_fingerprint','security_session_id')) { $tuple[$key]=$Fields[$key] }
    $tuple
}
function SSame($Left,$Right) {
    foreach($key in $Right.Keys) { YRequire ($Left.Contains($key) -and $Left[$key] -ceq $Right[$key]) ('security_span_identity:'+ $key) }
}
function SParity($Full,$Compact) {
    YRequire ((ConvertTo-FileTransferLocalCarrierJson $Full) -ceq (ConvertTo-FileTransferLocalCarrierJson $Compact)) 'security_span_parity'
}
function SWitnessBound($Witnesses) { YRequire ($Witnesses.Count -le 64) 'security_span_witness_limit' }
function SWork($Work) {
    YRequire ($null -ne $Work -and $Work.ElapsedMilliseconds -le [NLink.Tools.LocalPublicationInputLimits]::ProducerMilliseconds) 'security_span_work_limit'
}
function Get-RecordedSecurityContextSpans($Rows,[int[]]$SelectedPositions,$Endpoints,[Diagnostics.Stopwatch]$Work,[switch]$ContentCoverage) {
    SWork $Work
    YRequire ($Rows.Count -gt 0 -and $Rows.Count -le 4096) 'security_span_record_limit'
    $selectionCount=if($ContentCoverage) {4} else {8}
    YRequire ($SelectedPositions.Count -eq $selectionCount -and @($SelectedPositions | Sort-Object -Unique).Count -eq $selectionCount) 'security_span_selection'
    foreach($p in $SelectedPositions) { YRequire ($p -ge 0 -and $p -lt $Rows.Count) 'security_span_selection' }
    $selected=@($SelectedPositions | ForEach-Object { $Rows[$_] })
    $receiverPosition=if($ContentCoverage) {2} else {5}
    $sender=$selected[0].Fields['log_writer_id']; $receiver=$selected[$receiverPosition].Fields['log_writer_id']
    YRequire ($sender -cne $receiver -and $Endpoints.Count -eq 2) 'security_span_writer_scope'
    $owners=@($sender,$sender,$receiver,$receiver,$sender,$receiver,$receiver,$receiver)
    $events=@('filetransfer_data_path_send_result','filetransfer_fallback_tail_yield_evidence','filetransfer_fallback_tail_yield_evidence',
        'filetransfer_fallback_tail_yield_evidence','filetransfer_fallback_tail_yield_evidence','filetransfer_data_path_receive_result',
        'filetransfer_tuna_data_path_mismatch','filetransfer_data_frame_ignored')
    if($ContentCoverage) {
        $owners=@($sender,$sender,$receiver,$receiver)
        $events=@('filetransfer_data_path_send_result','filetransfer_data_path_send_result',
            'filetransfer_data_path_receive_result','filetransfer_data_path_receive_result')
    }
    $contexts=@{}; $uses=@{}
    for($i=0;$i -lt $selected.Count;$i++) {
        $row=$selected[$i]; $f=$row.Fields; $tuple=SContext $f; $writer=$owners[$i]
        YRequire ($f['event'] -ceq $events[$i] -and $f['log_writer_id'] -ceq $writer -and
            $f['security_context_status'] -ceq 'captured') 'security_span_selected_status'
        [void](SInteger $f 'security_sequence')
        if(-not $ContentCoverage -and $i -ge 1 -and $i -le 4) {
            $role=if($i -in @(1,3)) {'send_copy'} else {'receive_dispatch'}
            YRequire ($f['yield_evidence_role'] -ceq $role -and $f['yield_evidence_status'] -ceq 'bound') 'security_span_selected_yield'
        }
        if($contexts.ContainsKey($writer)) { SSame $tuple $contexts[$writer] }
        else { $contexts[$writer]=$tuple; $uses[$writer]=[Collections.Generic.List[object]]::new() }
        $uses[$writer].Add($row)
    }
    YRequire ($contexts[$sender].security_transport_id -cne $contexts[$receiver].security_transport_id -and
        $contexts[$sender].security_installation_id -cne $contexts[$receiver].security_installation_id) 'security_span_local_identity'
    $ends=@{}; $previous=@{}
    foreach($writer in @($sender,$receiver)) {
        $end=YOne @($Endpoints | Where-Object WriterId -CEQ $writer) 'security_span_endpoint'
        YRequire ($end.LastSequence -ge 1 -and $end.LastSequence -le 4096) 'security_span_endpoint'
        $ends[$writer]=$end; $previous[$writer]=0L
    }
    $names=@('filetransfer_security_context_installed','filetransfer_security_context_retired')
    $lifecycle=[Collections.Generic.List[object]]::new()
    for($i=0;$i -lt $Rows.Count;$i++) {
        if($i % 64 -eq 0) { SWork $Work }
        $row=$Rows[$i]; $f=$row.Fields; $writer=$f['log_writer_id']
        YRequire ($row.Position -eq $i) 'security_span_origins'
        if($null -ne $writer -and $ends.ContainsKey($writer)) {
            $seq=SInteger $f 'log_writer_sequence'
            YRequire ($seq -gt $previous[$writer] -and $seq -le $ends[$writer].LastSequence) 'security_span_writer_order'
            $previous[$writer]=$seq
        }
        if($f['event'] -notin $names) { continue }
        YRequire ($f['event'] -cin $names) 'security_span_event_case'
        SIdentity $f
        YRequire ($f['authority_effect'] -ceq 'none' -and
            (($f['event'] -ceq $names[0] -and $f['security_context_status'] -cin @('captured','unbound')) -or
             ($f['event'] -ceq $names[1] -and $f['security_context_status'] -ceq 'retired'))) 'security_span_lifecycle_status'
        $lifecycle.Add($row)
    }
    $spans=[Collections.Generic.List[object]]::new(); $positions=[Collections.Generic.HashSet[int]]::new($SelectedPositions)
    foreach($writer in @($sender,$receiver)) {
        $tuple=$contexts[$writer]; $same=[Collections.Generic.List[object]]::new(); $other=[Collections.Generic.List[object]]::new(); $otherById=@{}
        foreach($row in $lifecycle) {
            SWork $Work
            $f=$row.Fields
            if($f.security_installation_id -ceq $tuple.security_installation_id) {
                $record=SContext $f; SSame $record $tuple; $same.Add($row)
            } elseif($f.security_transport_id -ceq $tuple.security_transport_id) {
                YRequire ($f.log_writer_id -ceq $writer) 'security_span_transport_owner'
                YRequire ($f.security_installation_generation -cne $tuple.security_installation_generation) 'security_span_generation_reused'
                $other.Add($row)
                $id=$f.security_installation_id
                if(-not $otherById.ContainsKey($id)) { $otherById[$id]=[Collections.Generic.List[object]]::new() }
                $otherById[$id].Add($row)
                YRequire ($otherById[$id].Count -le 2) 'security_span_other_context_ambiguous'
            }
        }
        $install=YOne @($same | Where-Object { $_.Fields.event -ceq $names[0] }) 'security_span_install'
        $retire=YOne @($same | Where-Object { $_.Fields.event -ceq $names[1] }) 'security_span_retire'
        YRequire ($install.Fields.security_context_status -ceq 'captured') 'security_span_install_status'
        $start=[long]$install.Fields.log_writer_sequence; $end=[long]$retire.Fields.log_writer_sequence
        YRequire ($start -lt $end -and $end -le $ends[$writer].LastSequence) 'security_span_interval'
        foreach($use in $uses[$writer]) {
            $seq=[long]$use.Fields.log_writer_sequence
            YRequire ($seq -gt $start -and $seq -lt $end) 'security_span_use_outside_interval'
        }
        foreach($row in $other) {
            SWork $Work
            $seq=[long]$row.Fields.log_writer_sequence
            YRequire ($seq -lt $start -or $seq -gt $end) 'security_span_intervening_context'
            # An older install/newer retire can enclose the whole selected span.
            if(($row.Fields.event -ceq $names[0] -and $seq -lt $start) -or
                ($row.Fields.event -ceq $names[1] -and $seq -gt $end)) {
                $opposite=if($row.Fields.event -ceq $names[0]) {$names[1]} else {$names[0]}
                $mate=YOne @($otherById[$row.Fields.security_installation_id] | Where-Object { $_.Fields.event -ceq $opposite }) 'security_span_other_context_overlap'
                foreach($key in @('security_installation_generation','security_establishment_fingerprint','security_session_id')) {
                    YRequire ($row.Fields.ContainsKey($key) -and $mate.Fields.ContainsKey($key) -and
                        $row.Fields[$key] -ceq $mate.Fields[$key]) 'security_span_other_context_identity'
                }
                $mateSeq=[long]$mate.Fields.log_writer_sequence
                YRequire (($seq -lt $start -and $seq -lt $mateSeq -and $mateSeq -lt $start) -or
                    ($seq -gt $end -and $mateSeq -gt $end -and $mateSeq -lt $seq)) 'security_span_other_context_overlap'
            }
        }
        [void]$positions.Add($install.Position); [void]$positions.Add($retire.Position)
        $spans.Add([ordered]@{Identity=$tuple;InstallSequence=$start;RetireSequence=$end;CaptureEnd=[long]$ends[$writer].LastSequence;
            InstallSourceLine=$install.Position+1;RetireSourceLine=$retire.Position+1;
            UseSourceLines=@($uses[$writer] | ForEach-Object { $_.Position+1 } | Sort-Object);
            UseSequences=@($uses[$writer] | ForEach-Object { [long]$_.Fields.log_writer_sequence } | Sort-Object)})
    }
    SWork $Work
    [pscustomobject]@{Result=[ordered]@{Status='matched_recorded_security_context_spans';Contexts=$spans.ToArray();
        SelectedSourceLines=@($SelectedPositions | ForEach-Object { $_+1 } | Sort-Object);SecurityLifetimeProven=$false;RuntimeRetirementProven=$false};
        Positions=@($positions | Sort-Object)}
}
