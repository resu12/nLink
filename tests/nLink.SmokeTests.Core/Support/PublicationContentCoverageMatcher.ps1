# Test-only query over original-custody observations or detached controls.
# No I/O, evaluator, runtime, acceptance or witness-selection capability.
function CWork($Work,$Budget) {
    YRequire ($null -ne $Work -and $Work.ElapsedMilliseconds -le 20000) 'content_work_limit'
    YRequire ($Budget.Records -le [NLink.Tools.LocalPublicationInputLimits]::ProducerRecords -and
        $Budget.CodeUnits -le [NLink.Tools.LocalPublicationInputLimits]::ProducerCodeUnits) 'content_input_budget'
}
function CWitnesses($Witnesses) { YRequire ($Witnesses.Count -ge 1 -and $Witnesses.Count -le 64) 'content_witness_limit' }
function Merge-PublicationContentCoverage($Full,$Compact) {
    if((ConvertTo-FileTransferLocalCarrierJson $Full) -cne (ConvertTo-FileTransferLocalCarrierJson $Compact)) {
        return [ordered]@{Policy='pre_rejection_tuna_content_coverage_v1';Status='unproven';FirstMissingEdge='content_parity';Coverage=$null}
    }
    $Full
}
function CContent($Record,$Native) {
    $f=$Record.Fields; JPayload $f
    YRequire (-not $f.ContainsKey('__nlink_duplicate_structured_field') -and
        $f.event -ceq 'filetransfer_data_path_send_result' -and $f.delivery_result -ceq 'sent' -and
        $f.publication_use_kind -ceq 'v4_physical_submission') 'content_sender_record'
    $raw=$Native.Local.Lines[$Record.Position]
    $suffix=[regex]::Match($raw,'; content_evidence_profile=sender_chunk_hmac_v1; content_scope_id=ftcs1-[0-9a-f]{32}; content_evidence_state=captured; content_segment_count=[1-3](?:; content_segment_[0-2]_index=(?:0|[1-9][0-9]*); content_segment_[0-2]_bytes=[1-9][0-9]*; content_segment_[0-2]_commitment=ftcc1-[0-9a-f]{64})+(?=; log_writer_id=|\z)')
    YRequire ($suffix.Success -and $suffix.Length -le 768 -and
        [regex]::Matches($raw,'(?<![A-Za-z0-9._/-])event=').Count -eq 1) 'content_suffix'
    $count=JInteger $f 'content_segment_count' 1
    YRequire ($count -le 3 -and $count -eq [long]$f.frame_chunk_count -and
        @($f.Keys | Where-Object { $_ -cmatch '^content_' }).Count -eq 4+3*$count -and
        [regex]::Matches($raw,'(?<![A-Za-z0-9._/-])content_[A-Za-z0-9_]+=').Count -eq 4+3*$count) 'content_fields'
    $segments=@(); $bytes=0L; $keys=@()
    for($slot=0;$slot -lt $count;$slot++) {
        $prefix='content_segment_'+$slot
        $index=JInteger $f ($prefix+'_index'); $length=JInteger $f ($prefix+'_bytes') 1
        YRequire ($index -eq [long]$f.start_chunk_index+$slot -and $index -lt 1500000 -and
            $length -le 49152 -and $length -le 65536-$bytes -and
            $f[($prefix+'_commitment')] -cmatch '\Aftcc1-[0-9a-f]{64}\z') 'content_segment_shape'
        $bytes+=$length
        $segments+=,[ordered]@{Index=$index;Bytes=$length;Commitment=$f[($prefix+'_commitment')]}
        $keys+=@(($prefix+'_index'),($prefix+'_bytes'),($prefix+'_commitment'))
    }
    $actual=@([regex]::Matches($suffix.Value,'content_segment_[0-2]_(?:index|bytes|commitment)=') | ForEach-Object { $_.Value.TrimEnd('=') })
    YRequire (($actual -join '|') -ceq ($keys -join '|') -and $bytes -eq [long]$f.raw_payload_bytes) 'content_segment_total'
    [ordered]@{Scope=$f.content_scope_id;Segments=$segments}
}
function CFrame($Fields) {
    JPayload $Fields; [void](SContext $Fields); [void](SInteger $Fields 'security_sequence')
    YRequire ($Fields.security_context_status -ceq 'captured' -and
        [long]$Fields.frame_chunk_count -le 3 -and [long]$Fields.last_end_chunk_exclusive -le 1500000 -and
        [long]$Fields.raw_payload_bytes -le 65536) 'content_frame_bounds'
    foreach($pair in @(@('data_path_envelope_fingerprint','ftenv1',64),@('security_payload_fingerprint','ftpay1',64),
        @('data_path_session_id','fte1',24),@('data_path_transfer_id','fte1',24),@('security_transfer_id','fte1',24))) {
        YRequire ($Fields[$pair[0]] -cmatch ('\A'+$pair[1]+'-[0-9a-f]{'+$pair[2]+'}\z')) 'content_frame_identity'
    }
    YRequire ($Fields.data_path_session_id -ceq $Fields.security_session_id -and
        $Fields.data_path_transfer_id -ceq $Fields.security_transfer_id) 'content_frame_scope'
}
function Get-PublicationContentCoverage($Rows,$Native,$View,[int]$FailurePosition,$Endpoints,$Work,$Budget) {
    $result=[ordered]@{Policy='pre_rejection_tuna_content_coverage_v1';Status='unproven';FirstMissingEdge='content_inputs';Coverage=$null}
    try {
        CWork $Work $Budget
        YRequire ($Rows.Count -gt 0 -and $Rows.Count -le 4096 -and $Native.Local.Lines.Length -eq $Rows.Count -and
            $View.Boundaries.Count -le 256 -and $FailurePosition -ge 0 -and $FailurePosition -lt $Rows.Count) 'content_bounds'
        YRequire ($View.StreamComplete -and $View.ArtifactEvidenceVerdict -ceq 'pass' -and
            @($View.Boundaries | Where-Object verdict -CNE 'pass').Count -eq 0) 'content_view'
        $operations=0L
        foreach($b in $View.Boundaries) { YRequire ($b.operations.Count -le 64) 'content_operation_limit'; $operations+=$b.operations.Count }
        YRequire ($operations -le 8192 -and $Native.Effects.Count -le 8192) 'content_operation_limit'
        # Conservative reservation for the bounded query's row/view/native passes.
        # The caller starts from the primary's accumulated producer accounting.
        $Budget.Records+=64L*($Rows.Count+$View.Boundaries.Count+$Native.Effects.Count+$operations)
        $Budget.CodeUnits+=8L*$Budget.InputCodeUnits
        CWork $Work $Budget
        $receive=$Rows[$FailurePosition]; CFrame $receive.Fields
        $source=YOne @($Rows | Where-Object { $_.Fields['event'] -ceq 'filetransfer_data_path_send_result' -and
            $_.Fields['data_path_envelope_fingerprint'] -ceq $receive.Fields.data_path_envelope_fingerprint }) 'content_old_send_ambiguous'
        CFrame $source.Fields
        $exact=@('frame_type','data_path_envelope_fingerprint','security_payload_fingerprint','security_sequence',
            'security_establishment_fingerprint','data_path_session_id','data_path_transfer_id','security_session_id','security_transfer_id',
            'chunk_index','start_chunk_index','last_end_chunk_exclusive','raw_payload_bytes','payload_bytes','frame_chunk_count','batch_chunk_count')
        YSame $source.Fields $receive.Fields $exact 'content_old_envelope'
        YRequire ($source.Fields.delivery_result -ceq 'sent' -and $source.Fields.delivery_transport -ceq 'regular_nkn' -and
            $source.Fields.actual_transport -ceq 'regular_nkn' -and $source.Fields.publication_use_kind -ceq 'v4_physical_submission' -and
            $source.Fields.frame_chunk_count -ceq '1') 'content_old_shape'
        $old=Get-FileTransferLocalPublicationRow $Native ($source.Position+1L) $Native.Local.NativeRole
        $current=Get-FileTransferLocalPublicationRow $Native ($receive.Position+1L) $Native.Local.NativeRole
        $sender=$old.Row; $receiver=$current.Row
        YRequire ($sender.direction -ceq 'outbound' -and $receiver.direction -ceq 'inbound' -and
            $sender.writer -cne $receiver.writer -and $sender.sessionId -ceq $receiver.sessionId -and
            $sender.transferId -ceq $receiver.transferId -and $sender.routeState.route -ceq 'regular_nkn_v4_fast' -and
            $sender.routeState.protocolVersion -eq 4) 'content_owner_scope'
        foreach($later in $View.Boundaries) {
            if((YOwned $later $sender) -and [long]$later.writerSequence -gt $sender.boundaryWriterSequence -and
                [long]$later.writerSequence -lt $sender.useWriterSequence) {
                foreach($key in $sender.routeState.Keys) {
                    YRequire ([string]$later.afterBoundary.$key -ceq [string]$sender.routeState[$key]) 'content_old_publication_superseded'
                }
            }
        }
        $chain=Get-PublicationRejectionChain $Rows $source $receive $Native $receiver
        $sp=@(TPublications $View $sender); $rp=@(TPublications $View $receiver)
        YRequire ($rp[1].Boundary.boundaryId -ceq $receiver.boundaryId -and
            [long]$sp[0].Boundary.writerSequence -gt $sender.boundaryWriterSequence) 'content_second_episode'
        $index=[long]$source.Fields.start_chunk_index
        # Select by declared position and publication, never by commitment equality.
        $sends=@($Rows | Where-Object { $_.Fields['event'] -ceq 'filetransfer_data_path_send_result' -and
            $_.Fields['log_writer_id'] -ceq $sender.writer -and $_.Fields['actual_transport'] -ceq 'tuna' -and
            $_.Fields['publication_evidence_boundary_id'] -ceq $sp[1].Boundary.boundaryId })
        $containing=@(foreach($row in $sends) {
            CWork $Work $Budget; CFrame $row.Fields
            if([long]$row.Fields.start_chunk_index -le $index -and [long]$row.Fields.last_end_chunk_exclusive -gt $index) { $row }
        })
        $send=YOne $containing 'content_containing_send_ambiguous'
        $tunaReceive=YOne @($Rows | Where-Object { $_.Fields['event'] -ceq 'filetransfer_data_path_receive_result' -and
            $_.Fields['log_writer_id'] -ceq $receiver.writer -and
            $_.Fields['data_path_envelope_fingerprint'] -ceq $send.Fields.data_path_envelope_fingerprint }) 'content_tuna_receive_missing_or_ambiguous'
        CFrame $tunaReceive.Fields
        YSame $send.Fields $tunaReceive.Fields $exact 'content_tuna_envelope'
        $s=TBoundUse $send $Native $View $sender $sp[1] $true
        $r=TBoundUse $tunaReceive $Native $View $receiver $rp[1] $false
        YRequire ($sender.useWriterSequence -lt $s.UseSequence -and
            [long]$rp[1].Boundary.writerSequence -lt $r.UseSequence -and $r.UseSequence -lt $receiver.useWriterSequence) 'content_local_order'
        $uses=[int[]]@($source.Position,$send.Position,$tunaReceive.Position,$receive.Position)
        $spans=Get-RecordedSecurityContextSpans $Rows $uses $Endpoints $Work -ContentCoverage
        $a=CContent $source $Native; $b=CContent $send $Native
        YSame $source.Fields $send.Fields @('log_writer_id','security_transport_id','security_installation_id',
            'security_installation_generation','security_establishment_fingerprint','data_path_session_id','data_path_transfer_id',
            'security_session_id','security_transfer_id','content_scope_id') 'content_sender_scope'
        $segment=$b.Segments[[int]($index-[long]$send.Fields.start_chunk_index)]
        YRequire ($a.Scope -ceq $b.Scope -and $a.Segments[0].Index -eq $segment.Index -and
            $a.Segments[0].Bytes -eq $segment.Bytes -and $a.Segments[0].Commitment -ceq $segment.Commitment) 'content_not_equal'
        $bindings=@($old,$current)+@($chain.Bindings)
        foreach($row in @($send,$tunaReceive)) { $bindings+=@(Get-FileTransferLocalPublicationRow $Native ($row.Position+1L) $Native.Local.NativeRole) }
        $positions=[Collections.Generic.HashSet[int]]::new([int[]]$spans.Positions)
        foreach($row in $chain.Rows) { [void]$positions.Add($row.Position) }
        foreach($binding in $bindings) {
            [void]$positions.Add([int]$binding.Location.useLine-1)
            # Publication/effect locations use this view's separate origin mapping.
        }
        CWork $Work $Budget
        $result.Coverage=[ordered]@{ChunkIndex=$index;Bytes=$segment.Bytes;OldSend=$sender;TunaSend=$s;TunaReceive=$r;
            SecuritySpans=$spans.Result;Positions=@($positions | Sort-Object)}
        $result.Status='pass'; $result.FirstMissingEdge='(none)'
        return [pscustomobject]@{Result=$result;Bindings=$bindings;Publications=@($sp)+@($rp);Positions=$spans.Positions}
    } catch { $result.FirstMissingEdge=$_.Exception.Message }
    [pscustomobject]@{Result=$result;Bindings=@();Publications=@();Positions=@()}
}
