param([string]$OutputDirectory,$Candidate)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
# Post-run detached matcher controls only. Serialized reports and reopened log
# records below cannot supply primary custody, recovery or a positive live gate.
. (Join-Path $PSScriptRoot 'PublicationServiceYieldMatcher.ps1')
. (Join-Path $PSScriptRoot '../../../tools/GuiSmokeStructuredFields.ps1')
. (Join-Path $PSScriptRoot '../../../tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
function YClone($Value) { ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $Value -Depth 64 -Compress) }
$lines=[IO.File]::ReadAllLines((Join-Path $OutputDirectory 'filetransfer-retained-log-slice.log'))
$records=@(for($i=0;$i -lt $lines.Length;$i++) { $f=ConvertFrom-GuiSmokeLogLedgerStructuredFields $lines[$i]
    if($f.event -ceq 'filetransfer_fallback_tail_yield_evidence') { [pscustomobject]@{Position=$i;Fields=$f} }
})
function YRecordCopy { @(foreach($r in $records) { [pscustomobject]@{Position=$r.Position;Fields=$r.Fields.Clone()} }) }
$controls=[Collections.Generic.List[string]]::new()
foreach($view in @('full','compact')) {
    $boundaries=(Get-Content -Raw (Join-Path $OutputDirectory ('filetransfer-coordinator-snapshot-'+$view+'-summary.json')) | ConvertFrom-Json).Boundaries
    $baseline=Get-ServiceYieldReceiptCandidate $records $boundaries $Candidate.Old $Candidate.Current
    YRequire ($baseline.ProofIdentity -ceq $Candidate.ProofIdentity) 'detached_baseline'
    foreach($owner in @(@($Candidate.Old,$baseline.Copy),@($Candidate.Current,$baseline.Ingress))) {
        $packet=YOne @(foreach($line in $lines) { $f=ConvertFrom-GuiSmokeLogLedgerStructuredFields $line
            if($f.event -cin @('filetransfer_data_path_send_result','filetransfer_data_path_receive_result') -and
                $f.log_writer_id -ceq $owner[0].writer -and $f.log_writer_sequence -ceq [string]$owner[0].useWriterSequence) { $f }
        }) 'local_security_packet'
        $keys=@('security_transport_id','security_installation_id','security_installation_generation')
        YSame $packet $owner[1].Fields $keys 'local_security_episode'
        foreach($key in $keys) {
            $changed=$owner[1].Fields.Clone(); $changed[$key]='different'
            $reason=''; try { YSame $packet $changed $keys 'local_security_episode' } catch { $reason=$_.Exception.Message }
            YRequire ($reason -ceq ('local_security_episode:'+ $key)) 'security_episode_control'
        }
    }
    $cases=@('missing_diagnostics','duplicate_fields','duplicate_slot','duplicate_ingress','duplicate_accepted_receipt',
        'wrong_source_writer','wrong_coordinator','wrong_episode','wrong_copy','queue_only','other_slot_success','retired_send','retired_association',
        'borrowed_idempotent','wrong_boundary','wrong_operation','wrong_receipt','wrong_receipt_fingerprint','missing_pending_adoption',
        'pending_invalidated','missing_pairing','missing_handoff','wrong_release','wrong_target','history_gap','send_after_association','ingress_after_association')
    $envelopeFields=@('data_path_envelope_fingerprint','security_payload_fingerprint','security_sequence','security_establishment_fingerprint',
        'data_path_session_id','data_path_transfer_id','security_session_id','security_transfer_id','yield_proof_identity','yield_frame_fingerprint')
    $cases+=@($envelopeFields | ForEach-Object { 'substitute_'+$_ })
    foreach($case in $cases) {
        $rs=YRecordCopy; $bs=YClone $boundaries
        $local=YOne @($rs | Where-Object Position -eq $Candidate.LocalPosition) 'control_local'
        $send=YOne @($rs | Where-Object Position -eq $Candidate.CopyPosition) 'control_send'
        $ingress=YOne @($rs | Where-Object Position -eq $Candidate.IngressPosition) 'control_ingress'
        $peer=YOne @($rs | Where-Object Position -eq $Candidate.PeerPosition) 'control_peer'
        $commit=YOne @($bs | Where-Object boundaryId -CEQ $Candidate.Current.boundaryId) 'control_commit'
        $pairing=YOne @($bs | Where-Object boundaryId -CEQ $Candidate.PairingBoundary) 'control_pairing'
        if($case.StartsWith('substitute_')) { $key=$case.Substring(11); $ingress.Fields[$key]='substituted' }
        switch($case) {
            'missing_diagnostics' { $rs=@() }
            'duplicate_fields' { $ingress.Fields['__nlink_duplicate_structured_field']=$true }
            'duplicate_slot' { $rs+=@($send) }
            'duplicate_ingress' { $rs+=@($ingress) }
            'duplicate_accepted_receipt' { $rs+=@($peer) }
            'wrong_source_writer' { $local.Fields.log_writer_id='foreign' }
            'wrong_coordinator' { $local.Fields.coordinator_instance_id='fte1-'+('0'*24) }
            'wrong_episode' { $send.Fields.data_path_transfer_id='fte1-'+('0'*24) }
            'wrong_copy' { $ingress.Fields.yield_evidence_copy='other' }
            'queue_only' { foreach($r in $rs | Where-Object { $_.Fields.yield_evidence_role -ceq 'send_copy' }) { $r.Fields.control_ack='0'; $r.Fields.control_copy='0'; $r.Fields.bulk_copy='0'; $r.Fields.control_queue='1' } }
            'other_slot_success' { $send.Fields.control_ack='0'; $send.Fields.control_copy='0'; $send.Fields.bulk_copy='0' }
            'retired_send' { $send.Fields.security_context_status='retired' }
            'retired_association' { $local.Fields.yield_evidence_status='retired' }
            'borrowed_idempotent' { $peer.Fields.yield_receipt_decision='Idempotent'; $peer.Fields.yield_evidence_status='diagnostic_only'; $peer.Fields.yield_receipt_state_changed='0' }
            'wrong_boundary' { $peer.Fields.transition_id='fte1-'+('0'*24) }
            'wrong_operation' { $peer.Fields.operation_key='foreign' }
            'wrong_receipt' { $peer.Fields.receipt_identity='fte1-'+('0'*24) }
            'wrong_receipt_fingerprint' { $peer.Fields.receipt_fingerprint='fte1-'+('0'*24) }
            'missing_pending_adoption' { foreach($b in $bs | Where-Object { $_.writer -ceq $Candidate.Current.writer -and $_.decisionKind -ceq 'FallbackTailQuiescent' }) { $b.operations=@($b.operations | Where-Object operationRole -CNE 'yield_current') } }
            'pending_invalidated' { foreach($b in $bs | Where-Object { $_.writer -ceq $Candidate.Current.writer -and $_.decisionKind -ceq 'FallbackTailQuiescent' }) { foreach($o in $b.operations | Where-Object operationRole -CEQ 'peer_proof_pending_initial') { $o.fallbackTailYield.invalidationReason='reset' } } }
            'missing_pairing' { $pairing.operations=@($pairing.operations | Where-Object operationRole -CNE 'yield_current') }
            'missing_handoff' { $commit.operations=@($commit.operations | Where-Object operationRole -CNE 'handoff_current') }
            'wrong_release' { ($commit.operations | Where-Object operationRole -CEQ 'handoff_current').responderProbeHandoff.pairedReleaseAuthorityId='fte1-'+('0'*24) }
            'wrong_target' { ($commit.operations | Where-Object operationRole -CEQ 'handoff_current').responderProbeHandoff.targetBoundary.routePublicationGeneration++ }
            'history_gap' { $pairing.beforeStateGeneration++ }
            'send_after_association' { $send.Fields.log_writer_sequence=$local.Fields.log_writer_sequence }
            'ingress_after_association' { $ingress.Fields.log_writer_sequence=$peer.Fields.log_writer_sequence }
        }
        $reason=''; try { $null=Get-ServiceYieldReceiptCandidate $rs $bs $Candidate.Old $Candidate.Current } catch { $reason=$_.Exception.Message }
        YRequire (-not [string]::IsNullOrWhiteSpace($reason)) ('negative_admitted:'+ $case)
        if($view -ceq 'full') { $controls.Add($case) }
    }
    $rs=YRecordCopy
    $local=YOne @($rs | Where-Object Position -eq $Candidate.LocalPosition) 'early_local_association'
    $peer=YOne @($rs | Where-Object Position -eq $Candidate.PeerPosition) 'early_peer_association'
    $local.Fields.log_writer_sequence=[string]([long]$baseline.Local.Boundary.writerSequence-1)
    $peer.Fields.log_writer_sequence=[string]([long]$baseline.Peer.Boundary.writerSequence-1)
    $null=Get-ServiceYieldReceiptCandidate $rs $boundaries $Candidate.Old $Candidate.Current
    # Detached positive projections exercise each allowed slot independently.
    # Their remapped local references/order are synthetic, never original proof.
    foreach($slot in @('control','bulk')) {
        $rs=YRecordCopy
        $chosen=YOne @($rs | Where-Object { $_.Fields.yield_evidence_role -ceq 'send_copy' -and $_.Fields.log_writer_id -ceq $Candidate.Old.writer -and $_.Fields.yield_evidence_copy -ceq $slot }) 'slot_positive'
        $ingress=YOne @($rs | Where-Object { $_.Fields.yield_evidence_role -ceq 'receive_dispatch' -and $_.Fields.data_path_envelope_fingerprint -ceq $chosen.Fields.data_path_envelope_fingerprint }) 'slot_ingress'
        $originalIngress=YOne @($rs | Where-Object Position -eq $Candidate.IngressPosition) 'original_ingress'
        $peer=YOne @($rs | Where-Object Position -eq $Candidate.PeerPosition) 'slot_peer'
        $peer.Fields.yield_evidence_id=$ingress.Fields.yield_evidence_id
        $ingress.Fields.log_writer_sequence=$originalIngress.Fields.log_writer_sequence
        $rs=@($rs | Where-Object { $_.Fields.yield_evidence_role -cne 'send_copy' -or $_.Fields.log_writer_id -cne $Candidate.Old.writer -or $_.Fields.yield_evidence_copy -ceq $slot })
        $positive=Get-ServiceYieldReceiptCandidate $rs $boundaries $Candidate.Old $Candidate.Current
        YRequire ($positive.Copy.Fields.yield_evidence_copy -ceq $slot) 'slot_positive_changed'
    }
    # Structural local-first branch, derived only for this detached unit check.
    # It is intentionally not a Snapshot-valid historical reconstruction.
    $bs=YClone $boundaries
    $accept=YOne @($bs | Where-Object boundaryId -CEQ $Candidate.PeerBoundary) 'local_first_accept'
    $paired=YOne @($bs | Where-Object boundaryId -CEQ $Candidate.PairingBoundary) 'local_first_pair'
    $current=YClone (YOne @($paired.operations | Where-Object operationRole -CEQ 'yield_current') 'local_first_current')
    $current.beforeState='LocalProofObserved'
    $accept.operations=@($current)
    foreach($b in $bs | Where-Object { $_.writer -ceq $Candidate.Current.writer -and [long]$_.writerSequence -gt [long]$accept.writerSequence -and [long]$_.writerSequence -lt [long]$Candidate.Current.boundaryWriterSequence }) {
        $b.operations=@($b.operations | Where-Object family -CNE 'fallback_tail_yield')
    }
    $rs=YRecordCopy; $peer=YOne @($rs | Where-Object Position -eq $Candidate.PeerPosition) 'local_first_association'
    $peer.Fields.operation_key=$current.operationKey; $peer.Fields.operation_authority_id=$current.authorityId; $peer.Fields.operation_generation=[string]$current.operationGeneration
    $positive=Get-ServiceYieldReceiptCandidate $rs $bs $Candidate.Old $Candidate.Current
    YRequire ($positive.Peer.Operation.operationRole -ceq 'yield_current' -and $positive.Pairing.boundaryId -ceq $accept.boundaryId) 'local_first_branch'
}
[pscustomobject]@{NegativeControls=$controls.ToArray();LocalSecurityEpisodeControls=6;
    DetachedPositiveControls=@('control_only','bulk_only','local_first','association_before_snapshot');Views=2;AuthorityEvaluations=0}
