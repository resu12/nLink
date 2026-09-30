# Test-only query over detached projections or the primary's original views.
# This helper has no input-reader, evaluator, authority or output capability.
function YRequire([bool]$Value,[string]$Reason) { if(-not $Value) { throw $Reason } }
function YOne($Values,[string]$Reason) { $items=@($Values); YRequire ($items.Count -eq 1) $Reason; $items[0] }
function YSame($Left,$Right,[string[]]$Keys,[string]$Reason) {
    foreach($key in $Keys) { YRequire ($null -ne $Left.$key -and $null -ne $Right.$key -and $Left.$key -ceq $Right.$key) ($Reason+':'+$key) }
}
function YOwned($Boundary,$Owner) {
    $Boundary.writer -ceq $Owner.writer -and $Boundary.coordinatorInstanceId -ceq $Owner.coordinatorId -and
        $Boundary.sessionId -ceq $Owner.sessionId -and $Boundary.transferId -ceq $Owner.transferId -and $Boundary.direction -ceq $Owner.direction
}
function YShape($Row) {
    $f=$Row.Fields
    YRequire (-not $f.ContainsKey('__nlink_duplicate_structured_field')) 'duplicate_fields'
    foreach($key in @('event','yield_evidence_version','yield_evidence_role','yield_evidence_id','yield_evidence_copy',
        'yield_evidence_status','yield_proof_identity','yield_frame_fingerprint','authority_effect','log_writer_id','log_writer_sequence')) {
        YRequire ($f.ContainsKey($key) -and -not [string]::IsNullOrWhiteSpace([string]$f[$key])) ('missing_field:'+ $key)
    }
    YRequire ($f.event -ceq 'filetransfer_fallback_tail_yield_evidence' -and $f.yield_evidence_version -ceq '1' -and
        $f.authority_effect -ceq 'none' -and $f.yield_evidence_id -cmatch '^ftye1-[0-9a-f]{32}$' -and
        $f.yield_proof_identity -cmatch '^fte1-[0-9a-f]{24}$' -and $f.yield_frame_fingerprint -cmatch '^fte1-[0-9a-f]{24}$' -and
        $f.log_writer_sequence -cmatch '^[1-9][0-9]*$') 'diagnostic_shape'
}
function YSecurity($Fields) {
    foreach($pair in @(@('data_path_envelope_fingerprint','ftenv1',64),@('security_payload_fingerprint','ftpay1',64),
        @('security_establishment_fingerprint','ftest1',64),@('security_transport_id','ftst1',32),@('security_installation_id','ftsi1',32),
        @('data_path_session_id','fte1',24),@('data_path_transfer_id','fte1',24),@('security_session_id','fte1',24),@('security_transfer_id','fte1',24))) {
        YRequire ($Fields.ContainsKey($pair[0]) -and $Fields[$pair[0]] -cmatch ('^'+$pair[1]+'-[0-9a-f]{'+$pair[2]+'}$')) ('security_shape:'+ $pair[0])
    }
    YRequire ($Fields.security_sequence -cmatch '^[1-9][0-9]*$' -and $Fields.security_installation_generation -cmatch '^[1-9][0-9]*$' -and
        $Fields.security_context_status -ceq 'captured' -and $Fields.yield_evidence_status -ceq 'bound' -and
        $Fields.data_path_session_id -ceq $Fields.security_session_id -and $Fields.data_path_transfer_id -ceq $Fields.security_transfer_id) 'security_lifetime'
}
function YReceipt($Boundaries,$Row,$Owner,[string]$Stage) {
    $f=$Row.Fields
    YRequire ($f.yield_evidence_role -ceq 'receipt_association' -and $f.yield_evidence_copy -ceq 'none' -and
        $f.yield_evidence_status -ceq 'bound' -and $f.yield_receipt_envelope -ceq 'captured' -and
        $f.yield_receipt_stage -ceq $Stage -and $f.yield_receipt_decision -ceq 'Accepted' -and $f.yield_receipt_state_changed -ceq '1' -and
        $f.receipt_stage -ceq $Stage -and $f.receipt_status -ceq 'Observed' -and $f.receipt_identity -ceq $f.yield_proof_identity) 'accepted_association'
    $b=YOne @($Boundaries | Where-Object { (YOwned $_ $Owner) -and $_.writer -ceq $f.log_writer_id -and
        $_.coordinatorInstanceId -ceq $f.coordinator_instance_id -and $_.boundaryId -ceq $f.transition_id }) 'receipt_boundary'
    $kind=if($Stage -ceq 'local_proof') {'ApplyFallbackTailLocalProofReceipt'} else {'ApplyFallbackTailPeerProofReceipt'}
    # Snapshot messages drain asynchronously; their log submission may follow
    # this association. Match the returned envelope, not relative log flushing.
    YRequire ($b.verdict -ceq 'pass' -and $b.decisionStatus -ceq 'Accepted' -and $b.decisionKind -ceq $kind -and
        $b.afterStateGeneration -eq [long]$f.coordinator_state_generation) 'receipt_boundary_state'
    $o=YOne @($b.operations | Where-Object { $_.operationKey -ceq $f.operation_key -and $_.authorityId -ceq $f.operation_authority_id -and
        $_.operationGeneration -eq [long]$f.operation_generation -and $_.family -ceq 'fallback_tail_yield' }) 'receipt_operation'
    YRequire ($o.stateApplied -and $o.receiptValidated -and $null -ne $o.fallbackTailYield -and
        $o.operationRole -cin @('yield_current','peer_proof_pending_initial')) 'receipt_operation_state'
    $r=YOne @($o.fallbackTailYield.receipts | Where-Object { $_.stage -ceq $f.receipt_stage -and $_.receiptIdentity -ceq $f.receipt_identity -and
        $_.receiptGeneration -eq [long]$f.receipt_generation -and $_.receiptStatus -ceq $f.receipt_status -and $_.receiptFingerprint -ceq $f.receipt_fingerprint }) 'receipt_exact'
    YRequire $r.receiptValidated 'receipt_validation'
    [pscustomobject]@{Boundary=$b;Operation=$o;Receipt=$r;Record=$Row}
}
function YPeerProof($Yield,$Local) {
    $lp=$Local.Operation.fallbackTailYield
    YRequire ($Yield.peerProofIdentity -ceq $lp.localProofIdentity -and $Yield.peerProofFingerprint -ceq $lp.localProofFingerprint -and
        (ConvertTo-FileTransferCoordinatorEvidenceToken $Yield.peerProofAuthorityId) -ceq $Local.Operation.authorityId -and
        $Yield.peerProofYieldGeneration -eq $Local.Operation.operationGeneration) 'peer_reference'
}
function YMatchPeerReceipt($Records,$Boundaries,$Local,$From,$To,[long]$AfterSequence,[long]$BeforeSequence) {
    $copies=@($Records | Where-Object { $_.Fields.log_writer_id -ceq $From.writer -and $_.Fields.yield_evidence_id -ceq $Local.Record.Fields.yield_evidence_id -and $_.Fields.yield_evidence_role -ceq 'send_copy' })
    YRequire ($copies.Count -ge 1 -and $copies.Count -le 2 -and @($copies | ForEach-Object { $_.Fields.yield_evidence_copy } | Select-Object -Unique).Count -eq $copies.Count) 'copy_slots'
    $matches=@(foreach($copy in $copies) {
        $s=$copy.Fields; YSecurity $s
        YSame $s $Local.Record.Fields @('yield_proof_identity','yield_frame_fingerprint') 'local_capture'
        YRequire ($s.yield_evidence_copy -cin @('control','bulk') -and [long]$s.log_writer_sequence -gt $AfterSequence -and
            [long]$s.log_writer_sequence -lt [long]$Local.Record.Fields.log_writer_sequence -and $s.data_path_session_id -ceq $From.sessionId -and $s.data_path_transfer_id -ceq $From.transferId) 'copy_scope'
        # An independently successful other slot cannot lend this slot success.
        $success=if($s.yield_evidence_copy -ceq 'control') {$s.control_copy -ceq '1' -or $s.control_ack -ceq '1'} else {$s.bulk_copy -ceq '1'}
        $ingresses=@($Records | Where-Object { $_.Fields.yield_evidence_role -ceq 'receive_dispatch' -and
            $_.Fields.data_path_envelope_fingerprint -ceq $s.data_path_envelope_fingerprint })
        YRequire ($ingresses.Count -le 1) 'ambiguous_ingress'
        if($ingresses.Count -eq 0) { continue }
        $ingress=$ingresses[0]; $r=$ingress.Fields; YSecurity $r
        YSame $s $r @('yield_evidence_copy','yield_proof_identity','yield_frame_fingerprint','data_path_envelope_fingerprint',
            'security_payload_fingerprint','security_sequence','security_establishment_fingerprint','data_path_session_id','data_path_transfer_id',
            'security_session_id','security_transfer_id') 'copy_envelope'
        YRequire ($r.log_writer_id -ceq $To.writer -and $r.yield_evidence_id -cne $s.yield_evidence_id -and
            $r.security_installation_id -cne $s.security_installation_id -and $r.security_transport_id -cne $s.security_transport_id -and
            @($Records | Where-Object { $_.Fields.yield_evidence_role -ceq 'receive_dispatch' -and $_.Fields.yield_evidence_id -ceq $r.yield_evidence_id }).Count -eq 1) 'ingress_owner'
        $accepted=@($Records | Where-Object { $_.Fields.yield_evidence_role -ceq 'receipt_association' -and
            $_.Fields.log_writer_id -ceq $To.writer -and $_.Fields.yield_evidence_id -ceq $r.yield_evidence_id -and $_.Fields.yield_receipt_decision -ceq 'Accepted' })
        YRequire ($accepted.Count -le 1) 'ambiguous_peer_association'
        if($accepted.Count -eq 0) { continue } # The other copy's own idempotent receipt is not authority.
        YRequire $success 'selected_copy_not_sent'
        $peer=YReceipt $Boundaries $accepted[0] $To 'peer_proof'
        YSame $r $accepted[0].Fields @('yield_proof_identity','yield_frame_fingerprint') 'ingress_receipt'
        YRequire ([long]$r.log_writer_sequence -lt [long]$accepted[0].Fields.log_writer_sequence -and
            [long]$peer.Boundary.writerSequence -lt [long]$BeforeSequence) 'peer_order'
        YPeerProof $peer.Operation.fallbackTailYield $local
        [pscustomobject]@{Copy=$copy;Ingress=$ingress;Peer=$peer}
    })
    YOne $matches 'accepted_copy_chain'
}
function Get-ServiceYieldReceiptCandidate($Records,$Boundaries,$Old,$Current) {
    YRequire ($Records.Count -le 4096 -and $Boundaries.Count -le 256) 'query_limit'
    foreach($b in $Boundaries) { YRequire ($b.operations.Count -le 64) 'operation_limit' }
    foreach($r in $Records) { YShape $r }
    $oldBoundary=YOne @($Boundaries | Where-Object { (YOwned $_ $Old) -and $_.boundaryId -ceq $Old.boundaryId }) 'old_publication'
    $commit=YOne @($Boundaries | Where-Object { (YOwned $_ $Current) -and $_.boundaryId -ceq $Current.boundaryId }) 'receiver_publication'
    YRequire ($oldBoundary.verdict -ceq 'pass' -and $commit.verdict -ceq 'pass' -and
        $oldBoundary.boundaryHash -ceq $Old.boundaryHash -and $commit.boundaryHash -ceq $Current.boundaryHash -and
        $oldBoundary.afterBoundary.route -ceq 'regular_nkn_v4_fast' -and $oldBoundary.afterBoundary.protocolVersion -eq 4 -and
        $commit.beforeBoundary.route -ceq 'post_tuna_fallback_v6' -and $commit.beforeBoundary.protocolVersion -eq 6 -and
        $commit.afterBoundary.route -ceq 'file_tuna_v4' -and $commit.afterBoundary.protocolVersion -eq 4 -and
        $Old.writer -cne $Current.writer -and $Old.sessionId -ceq $Current.sessionId -and $Old.transferId -ceq $Current.transferId) 'publication_history'
    $localRow=YOne @($Records | Where-Object { $_.Fields.log_writer_id -ceq $Old.writer -and
        $_.Fields.yield_evidence_role -ceq 'receipt_association' -and $_.Fields.yield_receipt_stage -ceq 'local_proof' -and
        $_.Fields.yield_receipt_decision -ceq 'Accepted' -and [long]$_.Fields.log_writer_sequence -gt $Old.useWriterSequence }) 'local_association'
    $local=YReceipt $Boundaries $localRow $Old 'local_proof'
    YRequire ([long]$local.Boundary.writerSequence -gt $Old.useWriterSequence -and $local.Operation.operationRole -ceq 'yield_current') 'local_episode'
    YRequire ($local.Operation.fallbackTailYield.localProofIdentity -ceq $localRow.Fields.yield_proof_identity -and
        $local.Operation.fallbackTailYield.localProofFingerprint -ceq $localRow.Fields.yield_frame_fingerprint) 'local_proof'
    $match=YMatchPeerReceipt $Records $Boundaries $local $Old $Current $Old.useWriterSequence $commit.writerSequence
    $peer=$match.Peer; $active=$peer.Operation; $paired=$null
    $chain=[Collections.Generic.List[object]]::new(); $chain.Add($peer.Boundary)
    $previousGeneration=[long]$peer.Boundary.afterStateGeneration
    if($active.afterState -ceq 'Paired') { $paired=$peer.Boundary }
    foreach($b in @($Boundaries | Where-Object { (YOwned $_ $Current) -and [long]$_.writerSequence -gt [long]$peer.Boundary.writerSequence -and
        [long]$_.writerSequence -lt [long]$commit.writerSequence } | Sort-Object {[long]$_.writerSequence})) {
        YRequire ($b.verdict -ceq 'pass' -and $b.beforeStateGeneration -eq $previousGeneration) 'history_gap'
        $previousGeneration=[long]$b.afterStateGeneration
        $ops=@($b.operations | Where-Object { $_.family -ceq 'fallback_tail_yield' -and $_.operationRole -cne 'attempted_decision' -and $_.stateApplied })
        if($ops.Count -eq 0) { continue }
        $chain.Add($b)
        foreach($o in $ops) { YRequire ($o.receiptValidated -and $o.fallbackTailYield.invalidationReason -ceq '(none)') 'yield_invalidated' }
        if($active.operationRole -ceq 'peer_proof_pending_initial') {
            $removed=YOne @($ops | Where-Object { $_.operationKey -ceq $active.operationKey -and $_.beforeState -ceq 'Pending' -and $_.afterState -ceq 'None' }) 'pending_removal'
            YRequire ($b.decisionKind -ceq 'FallbackTailQuiescent') 'pending_transition'
            $next=YOne @($ops | Where-Object { $_.operationRole -ceq 'yield_current' -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'PeerProofObserved' }) 'pending_adoption'
            YPeerProof $removed.fallbackTailYield $local
        } else {
            $next=YOne @($ops | Where-Object { $_.operationKey -ceq $active.operationKey -and $_.beforeState -ceq $active.afterState }) 'current_continuity'
        }
        YPeerProof $next.fallbackTailYield $local
        YSame $next.fallbackTailYield $active.fallbackTailYield @('ownerKey','pairKey') 'yield_episode'
        $receipt=YOne @($next.fallbackTailYield.receipts | Where-Object stage -CEQ 'peer_proof') 'retained_peer_receipt'
        YSame $receipt $peer.Receipt @('receiptIdentity','receiptGeneration','receiptFingerprint','receiptStatus') 'retained_peer_receipt'
        YRequire $receipt.receiptValidated 'retained_peer_validation'
        if($next.afterState -ceq 'Paired' -and $next.beforeState -cne 'Paired') { YRequire ($null -eq $paired) 'duplicate_pairing'; $paired=$b }
        $active=$next
    }
    YRequire ($commit.beforeStateGeneration -eq $previousGeneration -and $null -ne $paired -and $active.afterState -ceq 'Paired') 'pairing_missing'
    $release=YOne @($active.fallbackTailYield.receipts | Where-Object { $_.stage -ceq 'paired_release' -and $_.receiptValidated -and $_.receiptStatus -ceq 'Paired' }) 'paired_release'
    $handoff=YOne @($commit.operations | Where-Object { $_.operationRole -ceq 'handoff_current' -and $_.stateApplied -and $_.receiptValidated }) 'handoff_missing'
    $h=$handoff.responderProbeHandoff; $p=$active.fallbackTailYield
    YRequire ($h.transitionMode -ceq 'post_fallback_v6_paired' -and $h.peerProofIdentity -ceq $p.peerProofIdentity -and
        $h.peerProofFingerprint -ceq $p.peerProofFingerprint -and $h.pairedReleaseAuthorityId -ceq $p.pairedReleaseAuthorityId -and
        $h.pairedReleaseGeneration -eq $p.pairedReleaseGeneration -and $release.receiptGeneration -eq $p.pairedReleaseGeneration) 'handoff_release'
    # Receipt identity and release authority are distinct projections; the
    # original validated operation owns both. Do not equate their hashes.
    YSame $h.targetBoundary $Current.routeState @('route','protocolVersion','liveRouteEpoch','runtimeGeneration','terminalGeneration',
        'legGeneration','routePublicationGeneration','routeCommitGeneration','dataSessionIdentity','dataSessionGeneration') 'handoff_target'
    [pscustomobject]@{Local=$local;Copy=$match.Copy;Ingress=$match.Ingress;Peer=$peer;Pairing=$paired;Chain=$chain.ToArray();
        OldBoundary=$oldBoundary;Commit=$commit;Handoff=$handoff.operationKey;ProofIdentity=$localRow.Fields.yield_proof_identity}
}

# Follow a local receipt to its exact paired endpoint in one original writer.
# Unlike the forward peer-first walk, this never adopts a different operation.
function YLocalToPair($Boundaries,$Owner,$Local,$End,$Peer) {
    YRequire ([long]$Local.Boundary.writerSequence -le [long]$End.writerSequence) 'reciprocal_local_order'
    $active=$Local.Operation; $generation=[long]$Local.Boundary.afterStateGeneration
    $chain=[Collections.Generic.List[object]]::new(); $chain.Add($Local.Boundary)
    $steps=@($Boundaries | Where-Object { (YOwned $_ $Owner) -and
        [long]$_.writerSequence -gt [long]$Local.Boundary.writerSequence -and [long]$_.writerSequence -le [long]$End.writerSequence } |
        Sort-Object {[long]$_.writerSequence})
    foreach($b in $steps) {
        YRequire ($b.verdict -ceq 'pass' -and $b.beforeStateGeneration -eq $generation) 'reciprocal_history_gap'
        $generation=[long]$b.afterStateGeneration
        # Retry requests/reservations are companion operations, not another
        # proof lifecycle. The original authority view still validates them.
        $ops=@($b.operations | Where-Object { $_.family -ceq 'fallback_tail_yield' -and $_.stateApplied -and
            $_.operationRole -cin @('yield_current','yield_terminal','peer_proof_pending_initial') })
        if($ops.Count -eq 0) { continue }
        $next=YOne $ops 'reciprocal_operation_ambiguity'
        YSame $next $active @('operationKey','authorityId','operationGeneration','operationRole') 'reciprocal_operation_continuity'
        YRequire ($next.beforeState -ceq $active.afterState -and $next.receiptValidated -and
            $next.fallbackTailYield.invalidationReason -ceq '(none)') 'reciprocal_yield_invalidated'
        YSame $next.fallbackTailYield $active.fallbackTailYield @('ownerKey','pairKey','localProofIdentity','localProofFingerprint') 'reciprocal_local_continuity'
        $receipt=YOne @($next.fallbackTailYield.receipts | Where-Object stage -CEQ 'local_proof') 'reciprocal_retained_local'
        YSame $receipt $Local.Receipt @('receiptIdentity','receiptGeneration','receiptFingerprint','receiptStatus') 'reciprocal_retained_local'
        YRequire $receipt.receiptValidated 'reciprocal_local_validation'
        $active=$next; $chain.Add($b)
    }
    YRequire ($End.afterStateGeneration -eq $generation -and $active.afterState -ceq 'Paired' -and
        $active.receiptValidated -and $active.fallbackTailYield.invalidationReason -ceq '(none)') 'reciprocal_pairing_missing'
    $endOperation=YOne @($End.operations | Where-Object { $_.operationKey -ceq $active.operationKey -and $_.stateApplied }) 'reciprocal_pair_endpoint'
    YSame $endOperation $active @('authorityId','operationGeneration','afterState') 'reciprocal_pair_endpoint'
    $retained=YOne @($active.fallbackTailYield.receipts | Where-Object stage -CEQ 'peer_proof') 'reciprocal_retained_peer'
    YSame $retained $Peer.Receipt @('receiptIdentity','receiptGeneration','receiptFingerprint','receiptStatus') 'reciprocal_retained_peer'
    YRequire $retained.receiptValidated 'reciprocal_peer_validation'
    $release=YOne @($active.fallbackTailYield.receipts | Where-Object stage -CEQ 'paired_release') 'reciprocal_paired_release'
    YRequire ($release.receiptValidated -and $release.receiptStatus -ceq 'Paired' -and
        $release.receiptGeneration -eq $active.fallbackTailYield.pairedReleaseGeneration -and
        $active.fallbackTailYield.pairedReleaseAuthorityId -ceq $active.authorityId) 'reciprocal_release_validation'
    [pscustomobject]@{Operation=$active;Release=$release;Chain=$chain.ToArray()}
}

function Get-ServiceYieldReciprocalCandidate($Records,$Boundaries,$Old,$Current,$Forward) {
    YRequire ($Records.Count -le 4096 -and $Boundaries.Count -le 256) 'query_limit'
    foreach($b in $Boundaries) { YRequire ($b.operations.Count -le 64) 'operation_limit' }
    foreach($r in $Records) { YShape $r }
    $paired=YOne @($Forward.Pairing.operations | Where-Object { $_.operationRole -ceq 'yield_current' -and $_.stateApplied }) 'reciprocal_receiver_pair'
    $row=YOne @($Records | Where-Object { $_.Fields.log_writer_id -ceq $Current.writer -and
        $_.Fields.yield_evidence_role -ceq 'receipt_association' -and $_.Fields.yield_receipt_stage -ceq 'local_proof' -and
        $_.Fields.yield_receipt_decision -ceq 'Accepted' -and $_.Fields.operation_key -ceq $paired.operationKey }) 'reciprocal_local_association'
    $local=YReceipt $Boundaries $row $Current 'local_proof'
    YSame $local.Operation $paired @('operationKey','authorityId','operationGeneration','operationRole') 'reciprocal_receiver_episode'
    YRequire ($local.Operation.operationRole -ceq 'yield_current' -and
        $local.Operation.fallbackTailYield.localProofIdentity -ceq $row.Fields.yield_proof_identity -and
        $local.Operation.fallbackTailYield.localProofFingerprint -ceq $row.Fields.yield_frame_fingerprint) 'reciprocal_local_proof'
    # The accepting peer boundary is constrained below to the forward source's
    # exact operation. Do not manufacture a symmetric sender responder handoff.
    $match=YMatchPeerReceipt $Records $Boundaries $local $Current $Old 0 ([long]::MaxValue)
    YSame $match.Peer.Operation $Forward.Local.Operation @('operationKey','authorityId','operationGeneration','operationRole') 'reciprocal_source_episode'
    YRequire ($match.Peer.Operation.afterState -ceq 'Paired' -and
        [long]$match.Peer.Boundary.writerSequence -gt [long]$Forward.Local.Boundary.writerSequence) 'reciprocal_source_pairing'
    $sourcePair=YLocalToPair $Boundaries $Old $Forward.Local $match.Peer.Boundary $match.Peer
    $receiverPair=YLocalToPair $Boundaries $Current $local $Forward.Pairing $Forward.Peer
    YPeerProof $sourcePair.Operation.fallbackTailYield $local
    YPeerProof $receiverPair.Operation.fallbackTailYield $Forward.Local
    $securityKeys=@('security_transport_id','security_installation_id','security_installation_generation')
    YSame $match.Copy.Fields $Forward.Ingress.Fields $securityKeys 'reciprocal_receiver_security_episode'
    YSame $match.Ingress.Fields $Forward.Copy.Fields $securityKeys 'reciprocal_source_security_episode'
    [pscustomobject]@{Local=$local;Copy=$match.Copy;Ingress=$match.Ingress;Peer=$match.Peer;
        SourcePair=$sourcePair;ReceiverPair=$receiverPair;ProofIdentity=$row.Fields.yield_proof_identity;
        Chain=@($sourcePair.Chain)+@($receiverPair.Chain)}
}
