Set-StrictMode -Version Latest

# A closure of the old-source obligation, not successful settlement of repair A.
# Only the shared authoritative snapshot can supply cross-peer authority.
function Get-FileTransferStallGraceSupersessionClosure {
    param([hashtable]$Index,$Candidate,[ref]$FailureReason,[object[]]$EvidenceEvents = $null)
    try {
        $snapshot=$Index['__CheckpointRepairContinuitySnapshotEvidence']
        # Preserve the existing stall admission; the shared target proof
        # below cannot manufacture a settled repair for a recent-progress episode.
        if ($null -eq $snapshot -or $snapshot.CatalogVersion -cnotin @($script:FileTransferCoordinatorActivationRetryCatalogVersion,
                $script:FileTransferCoordinatorReceiverPreAdmissionCatalogVersion)) {
            if ($null -ne $FailureReason) { $FailureReason.Value='authoritative_profile_required' }
            return $null
        }
        $diagnostic=Get-FileTransferStallGraceUnresolvedDiagnostic $Index $Candidate
        if ($null -eq $diagnostic -or $diagnostic.Kind -cne 'SourceSuperseded') {
            if ($null -ne $FailureReason) { $FailureReason.Value='local_source_supersession_unproven' }
            return $null
        }
        if ($null -eq $EvidenceEvents) {
            $EvidenceEvents=@(foreach ($key in $Index.Keys) { if (-not $key.StartsWith('__')) { $Index[$key] } })
        }
        $proof=Get-FileTransferCoordinatorTunaSupersessionProof $snapshot $diagnostic.SupersedingTransitionId $FailureReason $EvidenceEvents
        if ($null -eq $proof) { return $null }
        return [pscustomobject]@{Kind='SourceSuperseded';ObligationDisposition='Superseded';
            RepairAuthorityId=$diagnostic.RepairAuthorityId;RepairGeneration=$diagnostic.RepairGeneration;
            RepairOutcome='SettledStalled';SuccessorOutcome='StaleRejected';PairKey=$proof.PairKey;
            SourceCommitBoundaryId=$diagnostic.SupersedingTransitionId;RetirementWriterSequence=$diagnostic.RetirementWriterSequence;
            LocalTarget=$proof.LocalTarget;PeerTarget=$proof.PeerTarget;RetryAuthorityId=$proof.RetryAuthorityId;
            InstallReceiptFingerprint=$proof.InstallReceiptFingerprint}
    } catch {
        if ($null -ne $FailureReason) { $FailureReason.Value=$_.Exception.Message }
        return $null
    }
}

function Get-FileTransferCoordinatorTunaSupersessionProof {
    param($Snapshot,[string]$LocalCommitId,[ref]$FailureReason,[object[]]$EvidenceEvents)
    try {
        if ($null -eq $snapshot -or $snapshot.Mode -cne 'SnapshotV1Authoritative' -or
            $snapshot.ArtifactEvidenceVerdict -cne 'pass' -or -not $snapshot.StreamComplete -or
            $snapshot.CatalogVersion -cnotin @($script:FileTransferCoordinatorActivationRetryCatalogVersion,
                $script:FileTransferCoordinatorReceiverPreAdmissionCatalogVersion) -or
            $snapshot.CatalogHash -cne (Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $snapshot.CatalogVersion).Hash) { throw 'authoritative_profile_required' }
        foreach ($family in @('route_runtime','route_transition','runtime_unlock_retry_lifecycle','fallback_tail_yield','responder_probe_handoff')) {
            if ($family -cnotin $snapshot.AuthoritativeFamilies) { throw 'authoritative_family_missing' }
        }
        $boundaries=@($snapshot.Boundaries)
        $localCommit=@($boundaries | Where-Object boundaryId -CEQ $LocalCommitId)
        if ($localCommit.Count -ne 1) { throw 'local_commit_ambiguous' }
        $localCommit=$localCommit[0]
        function SameStream($a,$b) {
            foreach ($name in @('coordinatorInstanceId','writer','sessionId','transferId','direction')) {
                if ($a.$name -cne $b.$name) { return $false }
            }
            return $true
        }
        function Terminal($b) {
            $ops=@($b.operations | Where-Object { $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'terminal' -and
                $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Committed' -and $_.receiptValidated -and $_.receiptStatus -ceq 'Committed' })
            if ($ops.Count -ne 1) { throw 'commit_authority_missing' }
            return $ops[0]
        }
        $localAuthority=Terminal $localCommit
        $localPrepared=@(foreach ($b in $boundaries) {
            if (-not (SameStream $b $localCommit) -or $b.decisionKind -cne 'PrepareFileTransferRouteTransition' -or $b.decisionStatus -cne 'Prepared') { continue }
            foreach ($o in $b.operations) {
                if ($o.family -ceq 'responder_probe_handoff' -and $o.operationRole -ceq 'route_transition_handoff_pending' -and
                    $o.responderProbeHandoff.linkedTransitionAuthorityId -ceq $localAuthority.authorityId -and
                    $o.responderProbeHandoff.linkedTransitionGeneration -eq $localAuthority.operationGeneration -and $o.receiptValidated) {
                    [pscustomobject]@{Boundary=$b;Operation=$o}
                }
            }
        })
        if ($localPrepared.Count -ne 1) { throw 'responder_transition_link_missing' }
        $handoff=$localPrepared[0].Operation.responderProbeHandoff
        $pairs=@($snapshot.__FallbackTailPeerProofBindings | Where-Object BindingKind -CEQ 'paired_yield')
        $localPair=@($pairs | Where-Object {
            $_.CoordinatorInstanceId -ceq $localCommit.coordinatorInstanceId -and $_.Writer -ceq $localCommit.writer -and
            $_.SessionId -ceq $localCommit.sessionId -and $_.TransferId -ceq $localCommit.transferId -and $_.Direction -ceq $localCommit.direction -and
            $_.PairKey -ceq $handoff.pairKey -and $_.PairedReleaseAuthorityId -ceq $handoff.pairedReleaseAuthorityId -and
            $_.PairedReleaseGeneration -eq $handoff.pairedReleaseGeneration -and $_.PairedReleaseOutcome -ceq 'Quiescent' -and
            $_.LocalProofIdentity -ceq $handoff.localProofIdentity -and $_.LocalProofFingerprint -ceq $handoff.localProofFingerprint -and
            $_.PeerProofIdentity -ceq $handoff.peerProofIdentity -and $_.PeerProofFingerprint -ceq $handoff.peerProofFingerprint
        })
        if ($localPair.Count -ne 1) { throw 'responder_paired_release_missing' }
        $localPair=$localPair[0]
        $peerPair=@($pairs | Where-Object {
            $_.CoordinatorInstanceId -cne $localPair.CoordinatorInstanceId -and $_.Direction -cne $localPair.Direction -and
            $_.SessionId -ceq $localPair.SessionId -and $_.TransferId -ceq $localPair.TransferId -and $_.PairKey -ceq $localPair.PairKey -and
            $_.LocalProofIdentity -ceq $localPair.PeerProofIdentity -and $_.LocalProofFingerprint -ceq $localPair.PeerProofFingerprint -and
            $_.PeerProofIdentity -ceq $localPair.LocalProofIdentity -and $_.PeerProofFingerprint -ceq $localPair.LocalProofFingerprint -and
            $_.PairedReleaseOutcome -ceq 'Quiescent'
        })
        if ($peerPair.Count -ne 1) { throw 'reciprocal_peer_release_missing' }
        $peerPair=$peerPair[0]
        $retryEvidence=$snapshot.__ActivationRetryEvidence
        if ($null -eq $retryEvidence -or @($retryEvidence.Errors).Count -ne 0) { throw 'initiating_retry_evidence_missing' }
        $initiators=@($retryEvidence.Bindings.Values | Where-Object {
            $_.PreparedBoundary.coordinatorInstanceId -ceq $peerPair.CoordinatorInstanceId -and $_.PreparedBoundary.writer -ceq $peerPair.Writer -and
            $_.PreparedBoundary.sessionId -ceq $peerPair.SessionId -and $_.PreparedBoundary.transferId -ceq $peerPair.TransferId -and
            $_.PreparedBoundary.direction -ceq $peerPair.Direction -and
            $_.PreparedOperation.routeTransition.activationRetryBinding.pairedReleaseAuthorityId -ceq $peerPair.PairedReleaseAuthorityId -and
            $_.PreparedOperation.routeTransition.activationRetryBinding.pairedReleaseGeneration -eq $peerPair.PairedReleaseGeneration
        })
        if ($initiators.Count -ne 1) { throw 'initiator_transition_link_missing' }
        $initiator=$initiators[0]; $retry=$initiator.EffectiveInstall.Operation
        $dispatch=@(foreach ($b in $boundaries) {
            if (-not (SameStream $b $initiator.PreparedBoundary) -or $b.afterStateGeneration -gt $initiator.PreparedBoundary.beforeStateGeneration -or
                $b.decisionKind -cne 'ObserveRuntimeUnlockRetryDispatchSettlement') { continue }
            $b.operations | Where-Object { $_.family -ceq 'runtime_unlock_retry_lifecycle' -and $_.operationRole -ceq 'attempt_terminal' -and
                $_.authorityId -ceq $retry.authorityId -and $_.operationGeneration -eq $retry.operationGeneration -and
                $_.receiptStatus -ceq 'DispatchObserved' -and $_.receiptValidated }
        })
        if ($dispatch.Count -ne 1) { throw 'initiator_dispatch_unproven' }
        $peerCommit=@($boundaries | Where-Object {
            (SameStream $_ $initiator.PreparedBoundary) -and $_.decisionKind -ceq 'CommitFileTransferRouteTransition' -and
            @($_.operations | Where-Object { $_.operationRole -ceq 'terminal' -and $_.family -ceq 'route_transition' -and
                $_.authorityId -ceq $initiator.PreparedOperation.authorityId -and $_.operationGeneration -eq $initiator.PreparedOperation.operationGeneration -and
                $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Committed' }).Count -eq 1
        })
        if ($peerCommit.Count -ne 1) { throw 'initiator_commit_missing' }
        function TargetProof($commit,$pair,$prepared) {
            $op=Terminal $commit; $t=$op.routeTransition
            if ($commit.decisionKind -cne 'CommitFileTransferRouteTransition' -or $commit.decisionStatus -cne 'Committed' -or
                $t.transitionKind -cne 'PostTunaFallbackV6ToTunaV4' -or $t.proofAdmissionMode -cne 'runtime_unlock_final_proof' -or
                $t.proofBoundaryKind -cne 'FileTransferTunaActivationTransitionProofBoundary' -or
                $t.sourceBoundary.route -cne 'post_tuna_fallback_v6' -or $t.sourceBoundary.protocolVersion -ne 6 -or
                $t.targetRoute -cne 'file_tuna_v4' -or $t.targetProtocolVersion -ne 4 -or $t.targetTransport -cne 'Tuna' -or
                $t.sourceBoundary.checkpointGeneration -le 0 -or $t.durableCheckpointFingerprint -cnotmatch '^fte1-[0-9a-f]{24}$' -or
                -not (Test-FileTransferCoordinatorSnapshotRouteTransitionSourceCurrent $t.sourceBoundary $pair.EffectiveBoundary)) { throw 'target_source_or_checkpoint_mismatch' }
            foreach ($name in @('transportEpoch','bridgeRecoveryGeneration')) {
                if ($t.sourceBoundary.$name -ne $pair.EffectiveBoundary.$name) { throw 'target_source_transport_mismatch' }
            }
            $dispositions=@(foreach ($b in $boundaries) {
                if (-not (SameStream $b $commit) -or $b.decisionKind -cne 'ApplyFileTransferRouteTransitionDispositionReceipt' -or
                    $b.afterStateGeneration -le $commit.afterStateGeneration) { continue }
                foreach ($o in $b.operations) {
                    if ($o.family -ceq 'route_transition' -and $o.operationRole -ceq 'terminal' -and $o.authorityId -ceq $op.authorityId -and
                        $o.operationGeneration -eq $op.operationGeneration -and $o.receiptValidated -and $o.receiptStatus -ceq 'Applied') {
                        [pscustomobject]@{Boundary=$b;Operation=$o}
                    }
                }
            })
            if ($dispositions.Count -ne 1) { throw 'target_disposition_missing' }
            $disposition=$dispositions[0]; $dt=$disposition.Operation.routeTransition
            foreach ($name in @('proofBoundaryFingerprint','durableCheckpointFingerprint','targetRoute','targetProtocolVersion','targetTransport')) {
                if ($dt.$name -cne $t.$name) { throw 'target_disposition_proof_mismatch' }
            }
            if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $dt.sourceBoundary $t.sourceBoundary) -or
                -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $dt.targetBoundary $t.targetBoundary)) { throw 'target_disposition_scope_mismatch' }
            $publications=@($commit.operations | Where-Object { $_.family -ceq 'route_runtime' -and $_.operationRole -ceq 'route_publication' -and
                $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Published' -and $_.receiptValidated -and
                $_.routeRuntime.boundary.route -ceq 'file_tuna_v4' -and $_.routeRuntime.boundary.protocolVersion -eq 4 })
            if ($publications.Count -ne 1) { throw 'target_publication_missing' }
            $publication=$publications[0]; $target=$publication.routeRuntime.boundary
            if (-not (Test-FileTransferCoordinatorSnapshotRouteTransitionTargetCurrent $t.targetBoundary $target) -or
                (ConvertTo-FileTransferCoordinatorEvidenceToken $t.targetBoundary.dataSessionIdentity) -cne $target.dataSessionIdentity -or
                $target.dataSessionIdentity -cne $t.sourceBoundary.dataSessionIdentity -or
                $target.dataSessionGeneration -ne $t.sourceBoundary.dataSessionGeneration -or
                -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $commit.afterBoundary $target) -or
                -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $disposition.Boundary.afterBoundary $target)) { throw 'target_data_or_publication_scope_mismatch' }
            if ($pair.StateGeneration -ge $prepared.beforeStateGeneration -or $prepared.afterStateGeneration -ge $commit.afterStateGeneration) { throw 'target_causality_mismatch' }
            foreach ($b in $boundaries) {
                if (-not (SameStream $b $commit) -or $b.afterStateGeneration -le $pair.StateGeneration -or
                    $b.afterStateGeneration -gt $commit.beforeStateGeneration) { continue }
                if (-not (Test-FileTransferCoordinatorSnapshotRouteTransitionSourceCurrent $t.sourceBoundary $b.beforeBoundary) -or
                    -not (Test-FileTransferCoordinatorSnapshotRouteTransitionSourceCurrent $t.sourceBoundary $b.afterBoundary)) { throw 'target_source_interval_changed' }
                foreach ($name in @('transportEpoch','bridgeRecoveryGeneration')) {
                    # Aggregate fields describe its active scope, not the
                    # retained leg carried by the typed transition source.
                    if ($commit.beforeBoundary.$name -ne $b.beforeBoundary.$name -or $commit.beforeBoundary.$name -ne $b.afterBoundary.$name) {
                        throw 'target_source_transport_interval_changed'
                    }
                }
                foreach ($o in $b.operations) {
                    if (-not $o.stateApplied -or $o.beforeState -ceq $o.afterState) { continue }
                    if (($o.family -ceq 'route_runtime' -and $o.operationRole -cin @('runtime_owner','route_commit','route_publication')) -or
                        ($o.family -ceq 'route_transition' -and $o.operationRole -ceq 'current' -and $o.authorityId -cne $op.authorityId)) { throw 'target_source_owner_changed' }
                }
            }
            foreach ($b in $boundaries) {
                if (-not (SameStream $b $commit) -or $b.afterStateGeneration -le $commit.afterStateGeneration -or
                    $b.afterStateGeneration -gt $disposition.Boundary.afterStateGeneration) { continue }
                if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $b.beforeBoundary $target) -or
                    -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $b.afterBoundary $target)) { throw 'target_disposition_interval_changed' }
                if (@($b.operations | Where-Object { $_.stateApplied -and $_.beforeState -cne $_.afterState -and
                    $_.family -ceq 'route_runtime' -and $_.operationRole -cin @('runtime_owner','route_commit','route_publication') }).Count) {
                    throw 'target_disposition_owner_changed'
                }
            }
            # Observed pause/terminal/data replacement is a veto only, never
            # route authority. Indexed partitions supply the full peer input.
            foreach ($event in $EvidenceEvents) {
                $f=$event.Fields
                if ([string]$f['log_writer_id'] -cne $pair.Writer -or [string]$f['direction'] -cne $pair.Direction -or
                    (ConvertTo-FileTransferCoordinatorEvidenceToken ([string]$f['transfer_id'])) -cne $pair.TransferId) { continue }
                $sequence=0L
                if (-not [long]::TryParse([string]$f['log_writer_sequence'],[ref]$sequence)) { throw 'peer_poison_sequence_missing' }
                if ($sequence -le $pair.TransitionSequence -or $sequence -ge [long]$disposition.Boundary.writerSequence) { continue }
                if ([string]$f['transfer_paused'] -ceq '1' -or [string]$f['event'] -cin @(
                    'file_transfer_inbound_terminal','filetransfer_live_route_epoch_terminal','filetransfer_terminalized_by_session_end',
                    'filetransfer_data_session_removed','filetransfer_data_session_recreated')) { throw 'peer_source_interval_poisoned' }
            }
            return [pscustomobject]@{CoordinatorInstanceId=$commit.coordinatorInstanceId;Direction=$commit.direction;
                PairedReleaseAuthorityId=$pair.PairedReleaseAuthorityId;PairedReleaseGeneration=$pair.PairedReleaseGeneration;
                PreparedBoundaryId=$prepared.boundaryId;CommitBoundaryId=$commit.boundaryId;TransitionAuthorityId=$op.authorityId;
                TransitionGeneration=$op.operationGeneration;DispositionBoundaryId=$disposition.Boundary.boundaryId;
                DurableCheckpointFingerprint=$t.durableCheckpointFingerprint;PublicationOperationKey=$publication.operationKey;
                PublicationFingerprint=$publication.routeRuntime.tokenFingerprint;TargetBoundary=$target}
        }
        $local=TargetProof $localCommit $localPair $localPrepared[0].Boundary
        $peer=TargetProof $peerCommit[0] $peerPair $initiator.PreparedBoundary
        return [pscustomobject]@{PairKey=$localPair.PairKey;
            LocalTarget=$local;PeerTarget=$peer;RetryAuthorityId=$retry.authorityId;InstallReceiptFingerprint=$retry.receiptFingerprint}
    } catch {
        if ($null -ne $FailureReason) { $FailureReason.Value=$_.Exception.Message }
        return $null
    }
}
