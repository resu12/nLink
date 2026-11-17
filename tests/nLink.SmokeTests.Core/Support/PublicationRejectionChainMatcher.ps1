# Test-only query. Original custody and completed authority views stay with the caller.
function JInteger($Fields,[string]$Name,[long]$Minimum=0) {
    $value=0L
    YRequire ($Fields.ContainsKey($Name) -and $Fields[$Name] -cmatch '^(0|[1-9][0-9]*)$' -and
        [long]::TryParse($Fields[$Name],[ref]$value) -and $value -ge $Minimum) ('rejection_integer:'+ $Name)
    $value
}
function JPayload($Fields) {
    YRequire ($Fields.frame_type -ceq 'filetransfer.chunk_batch.v4') 'rejection_frame'
    $start=JInteger $Fields 'start_chunk_index'; $end=JInteger $Fields 'last_end_chunk_exclusive' 1
    $count=JInteger $Fields 'frame_chunk_count' 1; $batch=JInteger $Fields 'batch_chunk_count' 1
    $raw=JInteger $Fields 'raw_payload_bytes' 1; $bytes=JInteger $Fields 'payload_bytes' 1
    YRequire ($end -gt $start -and $end-$start -eq $count -and $count -eq $batch -and $raw -eq $bytes -and
        $Fields.chunk_index -ceq ($start.ToString([Globalization.CultureInfo]::InvariantCulture)+'-'+($end-1).ToString([Globalization.CultureInfo]::InvariantCulture))) 'rejection_range'
}
function JParity($Full,$Compact) {
    YRequire ((ConvertTo-FileTransferLocalCarrierJson $Full) -ceq (ConvertTo-FileTransferLocalCarrierJson $Compact)) 'rejection_parity'
}
function JWitnessBound($Witnesses) { YRequire ($Witnesses.Count -le 64) 'rejection_witness_limit' }
function Get-PublicationRejectionCompanions($Rows,$Source,$Receive) {
    YRequire ($Rows.Count -le 4096) 'rejection_record_limit'
    $r=$Receive.Fields; $s=$Source.Fields
    $sequence=JInteger $r 'log_writer_sequence' 1
    YRequire ($sequence -le [long]::MaxValue-2) 'rejection_sequence_overflow'
    $chain=@(foreach($offset in 0..2) {
        YOne @($Rows | Where-Object { $_.Fields['log_writer_id'] -ceq $r.log_writer_id -and
            $_.Fields['log_writer_sequence'] -ceq [string]($sequence+$offset) }) ('rejection_companion:'+ $offset)
    })
    YRequire ($chain[0].Position -eq $Receive.Position -and $chain[0].Position -lt $chain[1].Position -and
        $chain[1].Position -lt $chain[2].Position -and $chain[0].Fields.event -ceq 'filetransfer_data_path_receive_result' -and
        $chain[1].Fields.event -ceq 'filetransfer_tuna_data_path_mismatch' -and
        $chain[2].Fields.event -ceq 'filetransfer_data_frame_ignored') 'rejection_order'
    $range=@('chunk_index','start_chunk_index','last_end_chunk_exclusive','frame_chunk_count','batch_chunk_count','raw_payload_bytes','payload_bytes')
    JPayload $s; JPayload $r; JPayload $chain[1].Fields
    YSame $s $r $range 'rejection_source_range'
    YSame $r $chain[1].Fields $range 'rejection_mismatch_range'
    YRequire ($r.delivery_result -ceq 'received' -and $chain[1].Fields.proof_direction -ceq 'receive' -and
        $chain[1].Fields.mismatch_reason -ceq 'file_tuna_v4_resolved_to_regular_nkn' -and
        $chain[2].Fields.reason -ceq 'file_tuna_v4_payload_requires_tuna') 'rejection_reason'
    $shared=@('frame_type','chunk_index','lane','actual_transport','direction','route','protocol_version','live_route_epoch',
        'runtime_generation','terminal_generation','leg_generation','route_publication_generation','route_commit_generation',
        'target_route','target_protocol_version','target_transport','intended_target_transport','route_publication_action_kind',
        'coordinator_decision_reason','data_path_envelope_fingerprint','data_path_session_id','data_path_transfer_id',
        'security_transport_id','security_installation_id','security_installation_generation','security_establishment_fingerprint',
        'security_context_status','security_payload_fingerprint','security_sequence','security_session_id','security_transfer_id',
        'publication_evidence_version','publication_evidence_coordinator_id','publication_evidence_session_id','publication_evidence_transfer_id',
        'publication_evidence_boundary_id','publication_evidence_state_generation','publication_evidence_observed_state_generation',
        'publication_evidence_operation_key','publication_evidence_token_fingerprint','publication_use_kind','receive_disposition','authority_effect','log_writer_id')
    foreach($row in $chain) {
        $f=$row.Fields
        YRequire (-not $f.ContainsKey('__nlink_duplicate_structured_field')) 'rejection_duplicate_fields'
        foreach($key in $shared) { YRequire ($r.ContainsKey($key) -and $f.ContainsKey($key)) ('rejection_missing_field:'+ $key) }
        YSame $r $f $shared 'rejection_identity'
        YRequire ($f.publication_use_kind -ceq 'receive_guard' -and $f.direction -ceq 'inbound' -and
            $f.receive_disposition -ceq 'rejected_before_dispatch' -and $f.authority_effect -ceq 'none' -and
            $f.security_context_status -ceq 'captured' -and $f.actual_transport -ceq 'regular_nkn' -and
            $f.route -ceq 'file_tuna_v4' -and $f.protocol_version -ceq '4' -and $f.target_route -ceq 'file_tuna_v4' -and
            $f.target_protocol_version -ceq '4' -and $f.target_transport -ceq 'tuna' -and $f.intended_target_transport -ceq 'tuna') 'rejection_disposition'
    }
    return ,$chain
}
function Get-PublicationRejectionChain($Rows,$Source,$Receive,$Native,$Current) {
    $chain=Get-PublicationRejectionCompanions $Rows $Source $Receive
    $bindings=@(foreach($row in $chain) {
        $binding=Get-FileTransferLocalPublicationRow $Native ($row.Position+1L) $Native.Local.NativeRole
        # Use position/hash differ; every other publication/receipt field must be identical.
        foreach($key in $Current.Keys) {
            if($key -cin @('useWriterSequence','useContentSha256')) { continue }
            YRequire ((ConvertTo-FileTransferLocalCarrierJson $binding.Row[$key]) -ceq
                (ConvertTo-FileTransferLocalCarrierJson $Current[$key])) ('rejection_publication:'+ $key)
        }
        YRequire ($binding.Row.useWriterSequence -eq [long]$row.Fields.log_writer_sequence) 'rejection_use_sequence'
        $binding
    })
    [pscustomobject]@{Rows=$chain;Bindings=$bindings;Candidate=[ordered]@{SourcePosition=$Source.Position;
        ReceivePosition=$Receive.Position;MismatchPosition=$chain[1].Position;IgnorePosition=$chain[2].Position;
        Receive=$bindings[0].Row;Mismatch=$bindings[1].Row;Ignored=$bindings[2].Row}}
}
