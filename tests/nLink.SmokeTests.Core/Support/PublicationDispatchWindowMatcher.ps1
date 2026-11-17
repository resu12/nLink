# Test-only captured-observation query. No reader, evaluator or authority capability.
function DSourceCoverage($Origins,$Source,$Reader,$Descriptor) {
    YRequire ($Reader.InputReadComplete -and $Reader.LinesRead -eq $Source.SourceLines -and
        $Source.SourceLines -gt 0 -and $Origins.Count -eq $Source.SourceLines -and
        $Descriptor.Path -ceq $Source.SourcePath -and $Descriptor.Sha256 -ceq $Source.SourceSha256 -and
        $Descriptor.ByteCount -eq $Source.SourceBytes -and $Descriptor.LineCount -eq $Source.SourceLines) 'dispatch_source_coverage'
    for($i=0;$i -lt $Origins.Count;$i++) { YRequire ($Origins[$i] -eq $i+1) 'dispatch_source_origins' }
}
function DCaptureEnd($Writers,[string]$Writer,$Descriptor,[long]$LinesRead) {
    YRequire ($Writers.Count -ge 1 -and $Writers.Count -le 64 -and $Descriptor.LineCount -ge 1 -and
        $Descriptor.LineCount -le 4096 -and $LinesRead -eq $Descriptor.LineCount) 'dispatch_capture_descriptor'
    $ids=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal); $total=0L
    foreach($item in $Writers) {
        YRequire ($ids.Add($item.WriterId) -and $item.LastSequence -ge 1 -and
            $item.LastSequence -le 4096-$total) 'dispatch_writer_descriptor'
        $total+=$item.LastSequence
    }
    YRequire ($total -eq $Descriptor.LineCount) 'dispatch_capture_count'
    YOne @($Writers | Where-Object WriterId -CEQ $Writer) 'dispatch_writer_endpoint'
}
function DStablePublication($Left,$Right) {
    foreach($key in $Right.Keys) {
        # The dispatch use has its own independently resolved observed state.
        if($key -cin @('useKind','useWriterSequence','useContentSha256','observedStateGeneration')) { continue }
        if((ConvertTo-FileTransferLocalCarrierJson $Left[$key]) -cne
            (ConvertTo-FileTransferLocalCarrierJson $Right[$key])) { return $false }
    }
    $true
}
function DParity($Full,$Compact) {
    YRequire ((ConvertTo-FileTransferLocalCarrierJson $Full) -ceq
        (ConvertTo-FileTransferLocalCarrierJson $Compact)) 'dispatch_parity'
}
function DWitnessBound($Witnesses) { YRequire ($Witnesses.Count -le 64) 'dispatch_witness_limit' }
function DInterval($Current,$Chain,$Endpoint) {
    $start=[long]$Current.boundaryWriterSequence; $forward=[long]$Current.effectWriterSequence
    YRequire ($Endpoint.WriterId -ceq $Current.writer -and $start -gt 0 -and $start -lt $forward -and
        $forward -lt $Chain.Receive.useWriterSequence -and
        $Chain.Receive.useWriterSequence -lt $Chain.Mismatch.useWriterSequence -and
        $Chain.Mismatch.useWriterSequence -lt $Chain.Ignored.useWriterSequence -and
        $Chain.Ignored.useWriterSequence -le $Endpoint.LastSequence) 'dispatch_interval_order'
    [ordered]@{Writer=$Current.writer;Start=$start;StartKind='publication_boundary_record';
        ForwardingSequence=$forward;End=[long]$Endpoint.LastSequence;EndKind='verified_writer_capture_eof_metadata';
        RuntimeRetirementProven=$false}
}
function DGenericDisjointV4Range($Fields,$Selected) {
    if($Fields['event'] -cne 'filetransfer_data_frame_dispatched' -or
        $Fields['frame_type'] -cne 'filetransfer.chunk_batch.v4' -or $Selected['frame_type'] -cne 'filetransfer.chunk_batch.v4' -or
        $Fields.ContainsKey('__nlink_duplicate_structured_field') -or $Selected.ContainsKey('__nlink_duplicate_structured_field')) { return $false }
    foreach($key in $Fields.Keys) {
        if($key -cmatch '^(security_|publication_|data_path_)' -or $key -cin @('start_chunk_index','last_end_chunk_exclusive',
            'frame_chunk_count','batch_chunk_count','raw_payload_bytes','payload_bytes')) { return $false }
    } # A future enriched/contradictory record needs its own binding, not this generic-only rule.
    try { JPayload $Selected } catch { return $false }
    $ranges=@()
    foreach($text in @($Fields['chunk_index'],$Selected['chunk_index'])) {
        if($text -isnot [string]) { return $false }
        $match=[regex]::Match($text,'\A(0|[1-9][0-9]{0,6})-(0|[1-9][0-9]{0,6})\z')
        if(-not $match.Success) { return $false }
        $first=[int]$match.Groups[1].Value; $last=[int]$match.Groups[2].Value
        # GetFileTransferDataFrameChunkIndex emits inclusive endpoints. The
        # v4 codec accepts at most 3 segments with end-exclusive <= 1,500,000.
        # The focused test pins these bounds to FileTransferProtocol constants.
        if($last -lt $first -or $last -ge 1500000 -or $last-$first -ge 3) { return $false }
        $ranges+=,@($first,$last)
    }
    # Negative identity evidence only: never associate the generic record with
    # a nearby receive, infer its route, or claim absence of unlogged effects.
    $ranges[0][1] -lt $ranges[1][0] -or $ranges[0][0] -gt $ranges[1][1]
}
function DGenericExcluded($Fields,$Selected) {
    # Only fields carried by this generic event may exclude it. Redacted raw
    # identifiers and transport=nkn cannot identify a peer scope or route.
    # Recognized FileTransferDataFrame types only (FileTransferProtocol). Unknown
    # or malformed labels are missing evidence, not a different frame identity.
    $knownFrames=@('filetransfer.manifest.v4','filetransfer.state.v4','filetransfer.chunk_batch.v4',
        'filetransfer.complete.v4','filetransfer.cancel.v4','filetransfer.error.v4','filetransfer.pause_control.v4',
        'filetransfer.manifest.v6','filetransfer.receiver_state.v6','filetransfer.chunk_batch.v6',
        'filetransfer.transport_epoch.v6','filetransfer.transport_probe.v6',
        'filetransfer.runtime_unlock.precommit_probe','filetransfer.runtime_unlock.precommit_probe_ack',
        'filetransfer.fallback_tail.yield.v6','filetransfer.frontier_request.v6','filetransfer.repair_proof.v6',
        'filetransfer.complete.v6','filetransfer.cancel.v6','filetransfer.error.v6','filetransfer.pause_control.v6',
        'filetransfer.heartbeat.v6','filetransfer.fallback_bootstrap_request.v6','filetransfer.fallback_bootstrap_state.v6')
    if($Fields['frame_type'] -cin $knownFrames -and $Fields['frame_type'] -cne $Selected.frame_type) { return $true }
    foreach($scope in @('session','transfer')) {
        $key=$scope+'_id'; $value=[string]$Fields[$key]
        if($value -and $value -cnotmatch '[\s\[\]()]' -and
            (ConvertTo-FileTransferCoordinatorEvidenceToken $value) -cne $Selected[('data_path_'+$key)]) { return $true }
    }
    DGenericDisjointV4Range $Fields $Selected
}
function Get-PublicationDispatchWindow($Rows,$Receive,$Current,$Chain,$Endpoint,$Native) {
    YRequire ($Rows.Count -le 4096) 'dispatch_record_limit'
    $window=DInterval $Current $Chain $Endpoint
    $s=$Receive.Fields; $positions=[Collections.Generic.HashSet[int]]::new()
    $bindings=[Collections.Generic.List[object]]::new(); $entries=[Collections.Generic.List[object]]::new()
    $peers=[Collections.Generic.List[object]]::new(); $seen=[Collections.Generic.HashSet[long]]::new()
    $last=0L; $lastPosition=-1; $previous=0L; $intervalRecords=0
    $identity=@('data_path_envelope_fingerprint','security_payload_fingerprint','security_establishment_fingerprint',
        'security_sequence','security_transport_id','security_installation_id','security_installation_generation',
        'data_path_session_id','data_path_transfer_id','security_session_id','security_transfer_id',
        'frame_type','chunk_index','start_chunk_index','last_end_chunk_exclusive','frame_chunk_count','batch_chunk_count',
        'raw_payload_bytes','payload_bytes')
    for($i=0;$i -lt $Rows.Count;$i++) {
        $row=$Rows[$i]; $f=$row.Fields
        YRequire ($row.Position -eq $i) 'dispatch_duplicate_or_incomplete_origins'
        if($f['log_writer_id'] -cne $Current.writer) { continue }
        $seq=JInteger $f 'log_writer_sequence' 1
        YRequire ($seen.Add($seq) -and $seq -gt $previous -and $seq -le $window.End) 'dispatch_writer_order'
        $previous=$seq; $last=$seq; $lastPosition=$row.Position
        $selectedPublication=$f['publication_evidence_coordinator_id'] -ceq $Current.coordinatorId -and
            $f['publication_evidence_boundary_id'] -ceq $Current.boundaryId
        if($seq -lt $window.Start) {
            YRequire (-not $selectedPublication) 'dispatch_publication_outside_interval'
            continue
        }
        $intervalRecords++
        if($f['event'] -cnotin @('filetransfer_data_path_receive_result','filetransfer_tuna_data_path_peer_visible',
            'filetransfer_data_frame_dispatched')) { continue }
        YRequire (-not $f.ContainsKey('__nlink_duplicate_structured_field')) 'dispatch_duplicate_fields'
        if($f.event -ceq 'filetransfer_data_frame_dispatched') {
            YRequire (DGenericExcluded $f $s) 'dispatch_unbound_generic_observation'
            continue
        }
        $samePayload=$f['security_payload_fingerprint'] -ceq $s.security_payload_fingerprint
        $sameEnvelope=$f['data_path_envelope_fingerprint'] -ceq $s.data_path_envelope_fingerprint
        if(-not $samePayload -and -not $sameEnvelope -and -not $selectedPublication) {
            if(DGenericExcluded $f $s) { continue }
            if($f.event -ceq 'filetransfer_tuna_data_path_peer_visible' -and $f['proof_direction'] -ceq 'send' -and
                $f['direction'] -ceq 'outbound') { continue }
        }
        $guard=$f['publication_use_kind'] -ceq 'receive_guard' -and $f['receive_disposition'] -ceq 'rejected_before_dispatch' -and
            $f['authority_effect'] -ceq 'none'
        YRequire ($f['direction'] -ceq 'inbound' -and ($guard -or
            ($f['publication_use_kind'] -ceq 'receive_dispatch' -and $f['receive_disposition'] -ceq 'dispatch_entered'))) 'dispatch_disposition'
        JPayload $f
        # This resolves the entry's own security, owner, publication, forwarding,
        # route tuple and observed state, even when it will be excluded below.
        $binding=Get-FileTransferLocalPublicationRow $Native ($row.Position+1L) $Native.Local.NativeRole
        [void]$positions.Add($row.Position); $bindings.Add($binding)
        if(-not $samePayload) {
            YRequire (-not $sameEnvelope) 'dispatch_payload_envelope_conflict'
            continue
        }
        foreach($key in $identity) {
            YRequire ($f.ContainsKey($key) -and $f[$key] -ceq $s[$key]) ('dispatch_candidate_identity:'+ $key)
        }
        if(-not (DStablePublication $binding.Row $Current)) {
            # Another fully resolved publication is not this candidate. A
            # changed field under the same publication is inconsistent instead.
            YRequire (-not $selectedPublication) 'dispatch_stable_publication_conflict'
            continue
        }
        if($guard) { continue }
        YRequire ($f['actual_transport'] -cin @('regular_nkn','tuna') -and
            (($f.event -ceq 'filetransfer_data_path_receive_result' -and $f['delivery_result'] -ceq 'received') -or
             ($f.event -ceq 'filetransfer_tuna_data_path_peer_visible' -and $f['proof_direction'] -ceq 'receive' -and
              $f['actual_transport'] -ceq 'tuna'))) 'dispatch_event_disposition'
        $keyFields=[ordered]@{}
        foreach($key in $identity) { $keyFields[$key]=$f[$key] }
        foreach($key in @('actual_transport','lane','publication_evidence_observed_state_generation')) {
            YRequire ($f.ContainsKey($key) -and -not [string]::IsNullOrWhiteSpace($f[$key])) ('dispatch_missing_field:'+ $key)
            $keyFields[$key]=$f[$key]
        }
        $key=ConvertTo-FileTransferLocalCarrierJson $keyFields
        $entry=[pscustomobject]@{Position=$row.Position;Key=$key;Publication=$binding.Row}
        if($f.event -ceq 'filetransfer_data_path_receive_result') { $entries.Add($entry) }
        else { $peers.Add($entry) }
    }
    $paired=[Collections.Generic.HashSet[int]]::new()
    foreach($peer in $peers) {
        $entry=YOne @($entries | Where-Object Key -CEQ $peer.Key) 'dispatch_orphan_or_ambiguous_peer_visible'
        YRequire ($paired.Add($entry.Position)) 'dispatch_duplicate_peer_visible'
    }
    [void]$positions.Add($lastPosition)
    $window.LastRetainedWriterSequence=$last; $window.LastRetainedSourceLine=$lastPosition+1
    $window.RetainedIntervalRecords=$intervalRecords
    $window.MatchingDispatchCount=$entries.Count; $window.PeerVisibleCompanionCount=$peers.Count
    $window.UnboundObservationReason='(none)'
    $window.Status=if($entries.Count -gt 0) {'observed_matching_bound_dispatch'} else {'no_matching_bound_dispatch_observed_in_capture'}
    $window.Matches=@($entries | ForEach-Object { [ordered]@{Position=$_.Position;Publication=$_.Publication} })
    [pscustomobject]@{Result=$window;Positions=@($positions | Sort-Object);Bindings=$bindings.ToArray()}
}
