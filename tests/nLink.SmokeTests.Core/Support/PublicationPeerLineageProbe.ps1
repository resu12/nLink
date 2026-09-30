param($Context,$Custody,$Evaluations,$Index,$Failure,$RouteState,[string]$ArtifactDir)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
# Bounded fixture-only query over completed primary views. This is neither an
# authority evaluator nor a stale-safety/delivery classifier. Mutations below
# exercise this query on detached projections, not Snapshot tamper acceptance.
function Require([bool]$Condition,[string]$Reason) { if(-not $Condition) { throw $Reason } }
function One($Items,[string]$Reason) { $a=@($Items); Require ($a.Count -eq 1) $Reason; return $a[0] }
function Clone($Value) { ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $Value -Depth 64 -Compress) }
function Json($Value) { ConvertTo-Json -InputObject $Value -Depth 64 -Compress }
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Prototype must not evaluate authority' }
Require ($null -ne $Context -and $null -ne $Custody -and
    $Custody.Owns($Evaluations,$Context,$Context.FullLines,$Context.CompactLines,$Context.FullView,$Context.CompactView) -and
    $null -ne $Context.WriterReader -and $Context.WriterReader.IsVerified) 'Completed primary custody unavailable'
Require ($Context.FullLines.Length -le 4096 -and $Context.FullView.Boundaries.Count -le 256) 'Prototype fixture limit'
Require ($RouteState.FullResult.Verdict -ceq 'pass' -and $RouteState.CompactResult.Verdict -ceq 'pass' -and
    $RouteState.Parity.Verdict -ceq 'pass') 'Original route consistency unavailable'
$before=Json @($Context.FullView,$Context.CompactView)
$originalLines=@($Context.FullLines.Clone(),$Context.CompactLines.Clone())
$receivePosition=[Array]::BinarySearch($Context.FullOrigins,[int]$Failure.Event.LineNumber)
Require ($receivePosition -ge 0 -and [object]::ReferenceEquals($Failure.Event,$Index.AllEvents[$receivePosition])) 'Selected failure ownership'
$records=@(for($i=0;$i -lt $Context.FullLines.Length;$i++) {
    $f=ConvertFrom-GuiSmokeLogLedgerStructuredFields $Context.FullLines[$i]
    if($f.event -cin @('filetransfer_data_path_send_result','filetransfer_data_path_receive_result')) {
        Require (-not $f.ContainsKey('__nlink_duplicate_structured_field')) 'Ambiguous transport record'
        [pscustomobject]@{Position=$i;Fields=$f}
    }
})
$receive=One @($records | Where-Object Position -eq $receivePosition) 'Selected receive missing'
$source=One @($records | Where-Object { $_.Fields.event -ceq 'filetransfer_data_path_send_result' -and
    $_.Fields.data_path_envelope_fingerprint -ceq $receive.Fields.data_path_envelope_fingerprint }) 'Old source ambiguous'
Require ($source.Fields.route -ceq 'regular_nkn_v4_fast' -and $source.Fields.delivery_result -ceq 'sent' -and
    $receive.Fields.route -ceq 'file_tuna_v4' -and $receive.Fields.receive_disposition -ceq 'rejected_before_dispatch' -and
    $receive.Fields.authority_effect -ceq 'none' -and $source.Fields.log_writer_id -cne $receive.Fields.log_writer_id) 'Old packet topology'
function SameEnvelope($Send,$Receive) {
    foreach($key in @('frame_type','data_path_envelope_fingerprint','data_path_session_id','data_path_transfer_id',
        'security_payload_fingerprint','security_sequence','security_establishment_fingerprint','security_session_id','security_transfer_id')) {
        Require ($Send.ContainsKey($key) -and $Receive.ContainsKey($key) -and $Send[$key] -ceq $Receive[$key]) ('Envelope mismatch: '+$key)
    }
    Require ($Send.security_context_status -ceq 'captured' -and $Receive.security_context_status -ceq 'captured') 'Uncaptured security context'
}
SameEnvelope $source.Fields $receive.Fields
$yieldSend=One @($records | Where-Object { $_.Fields.event -ceq 'filetransfer_data_path_send_result' -and
    $_.Fields.frame_type -ceq 'filetransfer.fallback_tail.yield.v6' -and
    $_.Fields.log_writer_id -ceq $source.Fields.log_writer_id }) 'Source yield ambiguous'
$yieldReceive=One @($records | Where-Object { $_.Fields.event -ceq 'filetransfer_data_path_receive_result' -and
    $_.Fields.frame_type -ceq 'filetransfer.fallback_tail.yield.v6' -and
    $_.Fields.log_writer_id -ceq $receive.Fields.log_writer_id }) 'Receiver yield ambiguous'
SameEnvelope $yieldSend.Fields $yieldReceive.Fields
Require ($yieldSend.Fields.delivery_result -ceq 'sent' -and $yieldReceive.Fields.receive_disposition -ceq 'dispatch_entered') 'Yield transport outcome'

function Candidate($Boundaries,$Old,$Current) {
    function Owned($B,$Row) {
        $B.writer -ceq $Row.writer -and $B.coordinatorInstanceId -ceq $Row.coordinatorId -and
        $B.sessionId -ceq $Row.sessionId -and $B.transferId -ceq $Row.transferId -and $B.direction -ceq $Row.direction
    }
    $oldBoundary=One @($Boundaries | Where-Object { (Owned $_ $Old) -and $_.boundaryId -ceq $Old.boundaryId }) 'old_publication_owner'
    $currentBoundary=One @($Boundaries | Where-Object { (Owned $_ $Current) -and $_.boundaryId -ceq $Current.boundaryId }) 'current_publication_owner'
    Require ($oldBoundary.boundaryHash -ceq $Old.boundaryHash -and $currentBoundary.boundaryHash -ceq $Current.boundaryHash -and
        $oldBoundary.afterBoundary.route -ceq 'regular_nkn_v4_fast' -and
        $currentBoundary.beforeBoundary.route -ceq 'post_tuna_fallback_v6' -and $currentBoundary.afterBoundary.route -ceq 'file_tuna_v4') 'publication_history'
    $local=One @(foreach($b in $Boundaries) {
        if((Owned $b $Old) -and $b.decisionKind -ceq 'ApplyFallbackTailLocalProofReceipt' -and [long]$b.writerSequence -gt $Old.useWriterSequence) {
            foreach($o in $b.operations) { if($o.operationRole -ceq 'yield_current' -and $o.afterState -ceq 'LocalProofObserved') {
                [pscustomobject]@{Boundary=$b;Operation=$o}
            } }
        }
    }) 'source_local_proof'
    $paired=One @(foreach($b in $Boundaries) {
        if((Owned $b $Current) -and [long]$b.writerSequence -lt $Current.boundaryWriterSequence) {
            foreach($o in $b.operations) { if($o.operationRole -ceq 'yield_current' -and $o.afterState -ceq 'Paired' -and $o.beforeState -cne 'Paired') {
                [pscustomobject]@{Boundary=$b;Operation=$o}
            } }
        }
    }) 'receiver_paired_proof'
    $l=$local.Operation; $p=$paired.Operation; $lp=$l.fallbackTailYield; $pp=$p.fallbackTailYield
    Require ($l.stateApplied -and $p.stateApplied -and $l.receiptValidated -and $p.receiptValidated -and
        @($lp.receipts | Where-Object { $_.stage -ceq 'local_proof' -and $_.receiptValidated -and $_.receiptStatus -ceq 'Observed' }).Count -eq 1 -and
        @($pp.receipts | Where-Object { $_.stage -ceq 'peer_proof' -and $_.receiptValidated -and $_.receiptStatus -ceq 'Observed' }).Count -eq 1 -and
        @($pp.receipts | Where-Object { $_.stage -ceq 'paired_release' -and $_.receiptValidated }).Count -eq 1) 'validated_receipts'
    Require ($Old.sessionId -ceq $Current.sessionId -and $Old.transferId -ceq $Current.transferId -and
        $lp.localProofIdentity -cmatch '^fte1-[0-9a-f]{24}$' -and $lp.localProofFingerprint -cmatch '^fte1-[0-9a-f]{24}$' -and
        $lp.localProofIdentity -ceq $pp.peerProofIdentity -and $lp.localProofFingerprint -ceq $pp.peerProofFingerprint -and
        $l.authorityId -ceq (ConvertTo-FileTransferCoordinatorEvidenceToken $pp.peerProofAuthorityId) -and
        $l.operationGeneration -eq $pp.peerProofYieldGeneration) 'exact_peer_reference'
    $handoff=One @($currentBoundary.operations | Where-Object { $_.operationRole -ceq 'handoff_current' -and $_.stateApplied -and $_.receiptValidated }) 'receiver_handoff'
    $h=$handoff.responderProbeHandoff
    Require ($h.transitionMode -ceq 'post_fallback_v6_paired' -and $h.peerProofIdentity -ceq $pp.peerProofIdentity -and
        $h.peerProofFingerprint -ceq $pp.peerProofFingerprint -and $h.pairedReleaseAuthorityId -ceq $pp.pairedReleaseAuthorityId -and
        $h.pairedReleaseGeneration -eq $pp.pairedReleaseGeneration) 'paired_release_handoff'
    foreach($key in @('route','protocolVersion','liveRouteEpoch','runtimeGeneration','terminalGeneration','legGeneration',
        'routePublicationGeneration','routeCommitGeneration','dataSessionIdentity','dataSessionGeneration')) {
        Require ($h.targetBoundary.$key -ceq $Current.routeState.$key) ('handoff_publication_'+$key)
    }
    [ordered]@{OldPublication=$Old;ReceiverPublication=$Current
        LocalProofBoundary=$local.Boundary.boundaryId;LocalProofWriterSequence=[long]$local.Boundary.writerSequence
        PeerProofBoundary=$paired.Boundary.boundaryId;PeerProofWriterSequence=[long]$paired.Boundary.writerSequence
        ProofIdentity=$lp.localProofIdentity;ProofFingerprint=$lp.localProofFingerprint
        SourceAuthority=$l.authorityId;SourceYieldGeneration=$l.operationGeneration
        ReceiverPairedRelease=$pp.pairedReleaseAuthorityId;ReceiverPairedReleaseGeneration=$pp.pairedReleaseGeneration
        ReceiverHandoff=$handoff.operationKey}
}

$results=@{}; $controls=[Collections.Generic.List[string]]::new()
$expectedFailures=[ordered]@{
    wrong_source_writer='source_local_proof';source_restart='source_local_proof';missing_old_publication='old_publication_owner'
    duplicate_local_proof='source_local_proof';peer_identity='exact_peer_reference';peer_fingerprint='exact_peer_reference'
    peer_authority='exact_peer_reference';peer_generation='exact_peer_reference';missing_peer_receipt='validated_receipts'
    missing_handoff='receiver_handoff';handoff_release='paired_release_handoff';handoff_target='handoff_publication_routePublicationGeneration'
}
foreach($prefix in @('full','compact')) {
    $view=$Context.FullView; $snapshot=$Context.FullLines
    if($prefix -ceq 'compact') { $view=$Context.CompactView; $snapshot=$Context.CompactLines }
    Require ($view.ArtifactEvidenceVerdict -ceq 'pass' -and $view.StreamComplete -and
        @($view.Boundaries | Where-Object verdict -cne 'pass').Count -eq 0) 'Completed Snapshot failed'
    $native=@{Reason='';SnapshotPath='';SnapshotRole=$prefix+'Snapshot';SnapshotLines=$snapshot;Effects=$view.__RoutePublicationEffects
        Local=@{Reason='';Path='';NativeRole=$prefix+'Capture';Lines=$Context.FullLines;Publications=(New-FileTransferPublicationEvidenceIndex $view)}}
    foreach($record in @($source,$receive)) {
        $raw=$Context.FullLines[$record.Position]
        $use=Resolve-FileTransferPublicationUseEvidence $native.Local '' ($record.Position+1L) $raw ($prefix+'Capture')
        $forward=Resolve-FileTransferPublicationForwardingEvidence $native '' ($record.Position+1L) $raw ($prefix+'Capture')
        Require ($forward.Status -ceq 'matched_local_forwarding') ("$prefix local use=$($use.Reason) forwarding=$($forward.Reason) position=$($record.Position)")
    }
    $old=Get-FileTransferLocalPublicationRow $native ($source.Position+1L) ($prefix+'Capture')
    $current=Get-FileTransferLocalPublicationRow $native ($receive.Position+1L) ($prefix+'Capture')
    $linked=Candidate $view.Boundaries $old.Row $current.Row
    $results[$prefix]=$linked
    foreach($case in $expectedFailures.Keys) {
        $copy=Clone $view.Boundaries
        $local=One @($copy | Where-Object boundaryId -ceq $linked.LocalProofBoundary) 'Control source selection'
        $paired=One @($copy | Where-Object boundaryId -ceq $linked.PeerProofBoundary) 'Control peer selection'
        $p=One @($paired.operations | Where-Object operationRole -ceq 'yield_current') 'Control peer operation'
        $commit=One @($copy | Where-Object boundaryId -ceq $current.Row.boundaryId) 'Control commit selection'
        $h=One @($commit.operations | Where-Object operationRole -ceq 'handoff_current') 'Control handoff selection'
        switch($case) {
            'wrong_source_writer' { $local.writer='foreign-writer' }
            'source_restart' { $local.coordinatorInstanceId='fte1-'+('0'*24) }
            'missing_old_publication' { $copy=@($copy | Where-Object boundaryId -cne $old.Row.boundaryId) }
            'duplicate_local_proof' { $copy+=@($local) }
            'peer_identity' { $p.fallbackTailYield.peerProofIdentity='fte1-'+('0'*24) }
            'peer_fingerprint' { $p.fallbackTailYield.peerProofFingerprint='fte1-'+('0'*24) }
            'peer_authority' { $p.fallbackTailYield.peerProofAuthorityId='foreign-authority' }
            'peer_generation' { $p.fallbackTailYield.peerProofYieldGeneration++ }
            'missing_peer_receipt' { $p.fallbackTailYield.receipts=@($p.fallbackTailYield.receipts | Where-Object stage -cne 'peer_proof') }
            'missing_handoff' { $commit.operations=@($commit.operations | Where-Object operationRole -cne 'handoff_current') }
            'handoff_release' { $h.responderProbeHandoff.pairedReleaseAuthorityId='fte1-'+('0'*24) }
            'handoff_target' { $h.responderProbeHandoff.targetBoundary.routePublicationGeneration++ }
        }
        $reason=''
        try { $null=Candidate $copy $old.Row $current.Row } catch { $reason=$_.Exception.Message }
        Require ($reason -ceq $expectedFailures[$case]) ("Wrong detached negative result: $case = $reason")
        if($prefix -ceq 'full') { $controls.Add($case) }
    }
}
Require ((ConvertTo-FileTransferLocalCarrierJson $results.full) -ceq (ConvertTo-FileTransferLocalCarrierJson $results.compact)) 'Candidate full/compact parity'
# Different local data-session generations are genuine in this fixture; never
# equate the peers' local counters or compare their clocks to construct a link.
Require ($results.full.OldPublication.routeState.dataSessionGeneration -ne $results.full.ReceiverPublication.routeState.dataSessionGeneration) 'Different-local-generation control absent'
Require ([long]$yieldSend.Fields.log_writer_sequence -gt $results.full.LocalProofWriterSequence -and
    [long]$yieldReceive.Fields.log_writer_sequence -lt $results.full.PeerProofWriterSequence) 'Fixture yield/receipt ordering changed'
# The transport pair carries exact encrypted payload/security references. The
# Coordinator pair carries exact decoded proof references. Neither projection
# carries an explicit reference to the other component. Do not infer that edge
# from proximity, equal generation numbers, or the driver-authored send effect.
$proofJson=Json @($Context.FullView.Boundaries | Where-Object { $_.boundaryId -cin @($results.full.LocalProofBoundary,$results.full.PeerProofBoundary) })
Require (-not $proofJson.Contains($yieldSend.Fields.security_payload_fingerprint) -and
    -not $proofJson.Contains($yieldSend.Fields.security_installation_id) -and
    -not $Context.FullLines[$yieldSend.Position].Contains($results.full.ProofIdentity) -and
    -not $Context.FullLines[$yieldReceive.Position].Contains($results.full.ProofFingerprint)) 'Explicit security/proof binding now exists; revisit prototype boundary'
Require ($before -ceq (Json @($Context.FullView,$Context.CompactView)) -and
    ($originalLines[0] -join "`n") -ceq ($Context.FullLines -join "`n") -and
    ($originalLines[1] -join "`n") -ceq ($Context.CompactLines -join "`n")) 'Prototype mutated primary inputs'
[IO.File]::WriteAllText((Join-Path $ArtifactDir 'peer-lineage-prototype.json'),(Json ([ordered]@{
    Status='candidate_links_only';PeerLineage='unproven';FirstUnprovenEdge='authenticated_yield_to_coordinator_receipt'
    ClassifierEnabled=$false;AcceptanceChanged=$false;InputsUnchanged=$true;FullCompactParity='pass'
    NegativeControls=$controls.ToArray();Candidate=$results.full
    YieldSendWriterSequence=[long]$yieldSend.Fields.log_writer_sequence;YieldReceiveWriterSequence=[long]$yieldReceive.Fields.log_writer_sequence
    LocalProofPrecedesPhysicalYieldSend=$true;TransportEnvelopeMatched=$true;Live=$false
})))
