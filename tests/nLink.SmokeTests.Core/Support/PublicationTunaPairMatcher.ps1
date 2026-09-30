# Test-only query over original-custody inputs (or detached negative controls).
# No I/O, evaluator, witness selection, runtime or acceptance capability.
function TWork($Work) {
    YRequire ($null -ne $Work -and $Work.ElapsedMilliseconds -le 20000) 'tuna_pair_work_limit'
}
function TPublications($View,$Owner) {
    $items=@(foreach($b in $View.Boundaries) {
        if(-not (YOwned $b $Owner)) { continue }
        foreach($o in $b.operations) {
            if($o.family -ceq 'route_runtime' -and $o.operationRole -ceq 'route_publication' -and
                $o.afterState -ceq 'Published' -and $o.stateApplied) {
                YRequire ($o.receiptValidated -and $o.receiptStatus -ceq 'Accepted') 'tuna_pair_publication_receipt'
                [pscustomobject]@{Boundary=$b;Operation=$o}
            }
        }
    })
    $items=@($items | Sort-Object { [long]$_.Boundary.writerSequence })
    $tuna=@($items | Where-Object { $_.Operation.routeRuntime.boundary.route -ceq 'file_tuna_v4' })
    YRequire ($tuna.Count -eq 2) 'tuna_pair_activation_history'
    foreach($p in $tuna) { YRequire ($p.Operation.routeRuntime.boundary.protocolVersion -eq 4) 'tuna_pair_protocol' }
    $fallback=@($items | Where-Object { $_.Operation.routeRuntime.boundary.route -ceq 'post_tuna_fallback_v6' -and
        $_.Operation.routeRuntime.boundary.protocolVersion -eq 6 -and
        [long]$_.Boundary.writerSequence -gt [long]$tuna[0].Boundary.writerSequence -and
        [long]$_.Boundary.writerSequence -lt [long]$tuna[1].Boundary.writerSequence })
    YRequire ($fallback.Count -eq 1) 'tuna_pair_activation_history'
    $tuna
}
function TBoundUse($Record,$Native,$View,$Owner,$Publication,[bool]$Send) {
    $f=$Record.Fields
    YRequire (-not $f.ContainsKey('__nlink_duplicate_structured_field') -and
        $f.frame_type -ceq 'filetransfer.chunk_batch.v4' -and $f.actual_transport -ceq 'tuna') 'tuna_pair_payload_shape'
    if($Send) {
        YRequire ($f.event -ceq 'filetransfer_data_path_send_result' -and $f.delivery_result -ceq 'sent' -and
            $f.delivery_transport -ceq 'tuna' -and $f.publication_use_kind -ceq 'v4_physical_submission') 'tuna_pair_send_not_successful'
    } else {
        YRequire ($f.event -ceq 'filetransfer_data_path_receive_result' -and $f.delivery_result -ceq 'received' -and $f.receive_disposition -ceq 'dispatch_entered' -and
            $f.publication_use_kind -ceq 'receive_dispatch') 'tuna_pair_receive_not_dispatched'
    }
    JPayload $f
    $local=Get-FileTransferLocalPublicationRow $Native ($Record.Position+1L) $Native.Local.NativeRole
    $row=$local.Row; $b=$Publication.Boundary
    YRequire ($row.writer -ceq $Owner.writer -and $row.coordinatorId -ceq $Owner.coordinatorId -and
        $row.sessionId -ceq $Owner.sessionId -and $row.transferId -ceq $Owner.transferId -and $row.direction -ceq $Owner.direction -and
        $row.boundaryId -ceq $b.boundaryId -and $row.operationKey -ceq $Publication.Operation.operationKey -and
        $row.routeState.route -ceq 'file_tuna_v4' -and $row.routeState.protocolVersion -eq 4) 'tuna_pair_publication_binding'
    # Original local writer order, never merged timestamps. Reject an old token
    # even if its historical forwarding and observed-generation fields match.
    foreach($later in $View.Boundaries) {
        if((YOwned $later $Owner) -and [long]$later.writerSequence -gt [long]$b.writerSequence -and
            [long]$later.writerSequence -lt $row.useWriterSequence) {
            foreach($key in $row.routeState.Keys) {
                YRequire ([string]$later.afterBoundary.$key -ceq [string]$row.routeState[$key]) 'tuna_pair_publication_superseded'
            }
        }
    }
    [ordered]@{Publication=$row.boundaryId;Operation=$row.operationKey;Writer=$row.writer;
        UseSequence=$row.useWriterSequence;ForwardingSequence=$row.effectWriterSequence;RouteState=$row.routeState;
        NativePosition=$Record.Position;UseSha256=$row.useContentSha256}
}
function Get-TunaPublicationPayloadGate($Rows,$Native,$View,$Sender,$Receiver,[int]$Activation,$Work) {
    $result=[ordered]@{Status='unproven';FirstMissingEdge='tuna_pair_inputs';Pair=$null}
    try {
        TWork $Work
        YRequire ($Rows.Count -le 4096 -and $View.Boundaries.Count -le 256 -and $Activation -in @(0,1)) 'tuna_pair_bounds'
        YRequire ($View.StreamComplete -and $View.ArtifactEvidenceVerdict -ceq 'pass' -and
            @($View.Boundaries | Where-Object verdict -CNE 'pass').Count -eq 0) 'tuna_pair_incomplete_view'
        foreach($b in $View.Boundaries) { YRequire ($b.operations.Count -le 64) 'tuna_pair_operation_limit' }
        YRequire ($Sender.direction -ceq 'outbound' -and $Receiver.direction -ceq 'inbound' -and
            $Sender.writer -cne $Receiver.writer -and $Sender.sessionId -ceq $Receiver.sessionId -and
            $Sender.transferId -ceq $Receiver.transferId) 'tuna_pair_owner_scope'
        $sendPublications=@(TPublications $View $Sender); $receivePublications=@(TPublications $View $Receiver)
        YRequire ($receivePublications[1].Boundary.boundaryId -ceq $Receiver.boundaryId -and
            [long]$sendPublications[0].Boundary.writerSequence -gt $Sender.boundaryWriterSequence) 'tuna_pair_selected_episode'
        $sendPublication=$sendPublications[$Activation]; $receivePublication=$receivePublications[$Activation]
        $sends=@($Rows | Where-Object { $_.Fields.event -ceq 'filetransfer_data_path_send_result' -and
            $_.Fields['log_writer_id'] -ceq $Sender.writer -and $_.Fields['frame_type'] -ceq 'filetransfer.chunk_batch.v4' -and
            $_.Fields['actual_transport'] -ceq 'tuna' -and $_.Fields['publication_evidence_boundary_id'] -ceq $sendPublication.Boundary.boundaryId })
        YRequire ($sends.Count -gt 0) 'tuna_pair_send_missing'
        foreach($send in $sends) {
            TWork $Work
            $receives=@($Rows | Where-Object { $_.Fields.event -ceq 'filetransfer_data_path_receive_result' -and
                $_.Fields['log_writer_id'] -ceq $Receiver.writer -and
                $_.Fields['data_path_envelope_fingerprint'] -ceq $send.Fields.data_path_envelope_fingerprint })
            if($receives.Count -eq 0) { continue }
            $receive=YOne $receives 'tuna_pair_receive_ambiguous'
            YSame $send.Fields $receive.Fields @('frame_type','data_path_envelope_fingerprint','security_payload_fingerprint',
                'security_sequence','security_establishment_fingerprint','data_path_session_id','data_path_transfer_id',
                'security_session_id','security_transfer_id','start_chunk_index','last_end_chunk_exclusive','raw_payload_bytes','frame_chunk_count') 'tuna_pair_exact_payload'
            $s=TBoundUse $send $Native $View $Sender $sendPublication $true
            $r=TBoundUse $receive $Native $View $Receiver $receivePublication $false
            TWork $Work
            $result.Pair=[ordered]@{Activation=$Activation+1;Send=$s;Receive=$r;
                Envelope=$send.Fields.data_path_envelope_fingerprint;Payload=$send.Fields.security_payload_fingerprint;
                StartChunk=[long]$send.Fields.start_chunk_index;EndChunk=[long]$send.Fields.last_end_chunk_exclusive;
                Bytes=[long]$send.Fields.raw_payload_bytes;Chunks=[long]$send.Fields.frame_chunk_count}
            $result.Status='pass'; $result.FirstMissingEdge='(none)'; return $result
        }
        throw 'tuna_pair_receive_missing'
    } catch { $result.FirstMissingEdge=$_.Exception.Message }
    $result
}
function Merge-TunaPayloadGate($Full,$Compact) {
    if((ConvertTo-FileTransferLocalCarrierJson $Full) -cne (ConvertTo-FileTransferLocalCarrierJson $Compact)) {
        return [ordered]@{Status='unproven';FirstMissingEdge='tuna_pair_parity';Pair=$null}
    }
    $Full
}
