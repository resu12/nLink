# Synthetic validated-boundary DTOs exercise classification only. The separate
# RepairOwnerEvidence fixture validates the real Coordinator/emitter schema.
function RepairOwnerSupersessionFixture {
    $f=SupersessionFixture
    function Id([string]$Value) { ConvertTo-FileTransferCoordinatorEvidenceToken $Value }
    function Clone($Value) { ConvertFrom-Json (ConvertTo-Json -InputObject $Value -Depth 64 -Compress) }
    $owner=$scope.continuity_coordinator_id; $session=Id 'session'; $transfer=Id 'transfer'; $data=Id 'data'
    $publicationFingerprint=Id 'owner-publication'; $scopeFingerprint=Id 'owner-scope'
    foreach ($event in $f.Events) { $event.Fields.checkpoint_generation='0' }
    foreach ($boundary in $f.Snapshot.Boundaries) {
        $boundary.sessionId=$session; $boundary.transferId=$transfer
        $typed=$boundary.operations[0].routeTransition
        $typed.sourceBoundary=Clone $typed.sourceBoundary; $typed.targetBoundary=Clone $typed.targetBoundary
        $typed.sourceBoundary.dataSessionIdentity=$data
        $typed.sourceBoundary.checkpointRequestId=Id 'checkpoint:1'
        $typed.targetBoundary.checkpointGeneration=0
        $typed.durableCheckpointFingerprint=Id 'durable-checkpoint'
        $boundary.beforeBoundary=Clone $typed.sourceBoundary
        $boundary.beforeBoundary.checkpointGeneration=0; $boundary.beforeBoundary.checkpointRequestId='(none)'
        $boundary.afterBoundary=Clone $typed.targetBoundary
        $boundary.afterBoundary.dataSessionIdentity=$data
    }
    # Disposition is authored after the commit, not at the same state generation.
    $f.Snapshot.Boundaries[1].beforeStateGeneration=41
    $f.Snapshot.Boundaries[1].afterStateGeneration=42
    foreach ($event in @($f.Events | Where-Object Stage -CIn @('commit','disposition'))) { $event.Fields.checkpoint_generation='1' }
    $source=Clone $f.Snapshot.Boundaries[0].beforeBoundary
    $publicationId=Id 'publication:3'
    $publicationKey="route_runtime|route_publication|$publicationId|3|RoutePublication"
    $publication=[pscustomobject]@{verdict='pass';writer='writer';writerSequence=1;boundaryId=(Id 'source-publication');
        coordinatorInstanceId=$owner;sessionId=$session;transferId=$transfer;direction='inbound';
        decisionKind='RoutePublication';decisionStatus='Accepted';beforeStateGeneration=9;afterStateGeneration=10;
        beforeBoundary=(Clone $source);afterBoundary=(Clone $source);operations=@([pscustomobject]@{
            family='route_runtime';operationRole='route_publication';operationKind='RoutePublication';operationKey=$publicationKey;
            authorityId=$publicationId;operationGeneration=3;receiptGeneration=3;receiptIdentity=$publicationId;
            receiptStatus='Accepted';receiptValidated=$true;receiptFingerprint=$publicationFingerprint;afterState='Published';
            routeRuntime=[pscustomobject]@{role='route_publication';boundary=(Clone $source);tokenFingerprint=$publicationFingerprint}})}
    foreach ($event in @($f.Events | Where-Object Stage -CIn @('publication','hold'))) {
        $event.Fields.publication_evidence_version='1'; $event.Fields.publication_evidence_coordinator_id=$owner
        $event.Fields.publication_evidence_session_id=$session; $event.Fields.publication_evidence_transfer_id=$transfer
        $event.Fields.publication_evidence_boundary_id=$publication.boundaryId
        $event.Fields.publication_evidence_state_generation='10'; $event.Fields.publication_evidence_observed_state_generation='10'
        $event.Fields.publication_evidence_operation_key=$publicationKey; $event.Fields.publication_evidence_token_fingerprint=$publicationFingerprint
        $event.Fields.continuity_source_fingerprint=$scopeFingerprint
    }
    $ownerScope=[pscustomobject]@{boundary=(Clone $source);runtimeOwnerFingerprint=(Id 'runtime');publicationFingerprint=$publicationFingerprint;
        commitFingerprint='(none)';bridgeDecisionGeneration=0;aggregateBridgeDecisionGeneration=1;aggregateTransportEpoch=1;
        retainedLegCheckpointGeneration=0;retainedLegCheckpointRequestId='(none)';leaseGeneration=0;scopeFingerprint=$scopeFingerprint}
    $repairBoundaries=@()
    foreach ($pair in @(@('grant','settlement',20),@('later_grant','retirement',30))) {
        $grant=@($f.Events | Where-Object Stage -CEQ $pair[0])[0]
        $settlement=@($f.Events | Where-Object Stage -CEQ $pair[1])[0]
        $fields=$grant.Fields
        $fields.protocol_repair_request_id=$fields.primary_protocol_repair_request_id
        $fields.request_correlation_id=$fields.primary_request_correlation_id
        $repair=[pscustomobject]@{authorityId=(Id $fields.authority_id);predecessorAuthorityId=(Id $fields.predecessor_authority_id);
            decisionGeneration=[long]$fields.repair_generation;canonicalRepairKey=(Id $fields.repair_request_key);rangeFingerprint=(Id $fields.range_fingerprint);
            frontierChunkIndex=3547;durableReceivedHighestChunkIndex=6418;rangeCount=1;requestedChunkCount=1;sourceKind='ReceiverRequest';targetTransport='RegularNkn';
            protocolRepairRequestId=(Id $fields.protocol_repair_request_id);requestCorrelationId=(Id $fields.request_correlation_id);checkpointRequestId='(none)';repairFingerprint=(Id ('repair-'+$pair[0]))}
        $op=[pscustomobject]@{family='route_runtime';operationRole='fallback_frontier_repair_owner';operationKind='FallbackFrontierRepairOwnerEvidence';
            operationKey="route_runtime|fallback_frontier_repair_owner|$($repair.authorityId)|$($repair.decisionGeneration)|FallbackFrontierRepairOwnerEvidence";
            receiptIdentity=(Id ('receipt-'+$pair[0]));receiptFingerprint=(Id ('fingerprint-'+$pair[0]));stateApplied=$false;
            repairOwner=[pscustomobject]@{version=1;coordinatorInstanceId=$owner;sessionId=$session;transferId=$transfer;direction='inbound';scope=(Clone $ownerScope);repair=$repair}}
        $b=[pscustomobject]@{verdict='pass';writer='writer';writerSequence=([long]$grant.Fields.log_writer_sequence-1);evidenceLineIndex=0;
            boundaryId=(Id ('owner-boundary-'+$pair[0]));coordinatorInstanceId=$owner;sessionId=$session;transferId=$transfer;direction='inbound';
            decisionKind='CaptureRepairOwnerEvidence';decisionStatus='Idempotent';beforeStateGeneration=$pair[2];afterStateGeneration=$pair[2];
            beforeBoundary=(Clone $source);afterBoundary=(Clone $source);operations=@($op)}
        $repairBoundaries+=$b
        foreach ($event in @($grant,$settlement)) {
            $event.Fields.Remove('continuity_coordinator_id')
            $event.Fields.repair_owner_profile=$script:FileTransferCoordinatorRepairOwnerCatalogVersion
            $event.Fields.repair_owner_coordinator_id=$owner; $event.Fields.repair_owner_transition_id=$b.boundaryId
            $event.Fields.repair_owner_state_generation=[string]$pair[2]; $event.Fields.repair_owner_operation_key=$op.operationKey
            $event.Fields.repair_owner_receipt_id=$op.receiptIdentity; $event.Fields.repair_owner_receipt_fingerprint=$op.receiptFingerprint
        }
    }
    $f.Snapshot.AuthoritativeFamilies=@('route_runtime','route_transition')
    $f.Snapshot.Boundaries=@($publication)+$repairBoundaries+$f.Snapshot.Boundaries
    $f.Snapshot | Add-Member NoteProperty CatalogVersion $script:FileTransferCoordinatorRepairOwnerCatalogVersion
    $catalog=Get-FileTransferCoordinatorRepairOwnerCatalog
    $f.Snapshot | Add-Member NoteProperty __RepairOwnerEvidence (Get-FileTransferRepairOwnerEvidence $f.Snapshot.Boundaries $catalog)
    return $f
}

$current=RepairOwnerSupersessionFixture
Check 'repair_owner_exact_role_separation' $current.Events $current.Snapshot 'SourceSuperseded' $true
# Sanitization preserves the snapshot-bound identity token, not the raw label.
foreach ($rawIdentity in @('[redacted]', '[redacted]:1', 'data')) {
    $sanitized=RepairOwnerSupersessionFixture
    foreach ($event in $sanitized.Events) {
        $event.Fields.data_session_identity=$rawIdentity
        $event.Fields.frontier_scope_data_session_identity=ConvertTo-FileTransferCoordinatorEvidenceToken 'data'
    }
    Check ('repair_owner_sanitized_identity_'+$rawIdentity) $sanitized.Events $sanitized.Snapshot 'SourceSuperseded' $true
}
foreach ($case in @('missing','malformed','mismatched','raw_conflict','invalid_redaction','snapshot_mismatch')) {
    $bad=RepairOwnerSupersessionFixture
    foreach ($event in $bad.Events) {
        $event.Fields.data_session_identity='[redacted]:1'
        $event.Fields.frontier_scope_data_session_identity=ConvertTo-FileTransferCoordinatorEvidenceToken 'data'
        switch ($case) {
            'missing' { $event.Fields.Remove('frontier_scope_data_session_identity') }
            'malformed' { $event.Fields.frontier_scope_data_session_identity='fte1-invalid' }
            'mismatched' { $event.Fields.frontier_scope_data_session_identity=ConvertTo-FileTransferCoordinatorEvidenceToken 'other-data' }
            'raw_conflict' { $event.Fields.data_session_identity='other-data' }
            'invalid_redaction' { $event.Fields.data_session_identity='prefix[redacted]:1' }
        }
    }
    if ($case -ceq 'snapshot_mismatch') {
        $bad.Snapshot.Boundaries[1].operations[0].repairOwner.scope.boundary.dataSessionIdentity=
            ConvertTo-FileTransferCoordinatorEvidenceToken 'other-data'
    }
    Check ('repair_owner_sanitized_reject_'+$case) $bad.Events $bad.Snapshot 'DispatchedStalled'
}
# The emitter drains asynchronously. Its physical write may follow either the
# grant or the settlement that already holds the exact authored reference.
foreach ($stage in @('grant','settlement')) {
    $delayed=RepairOwnerSupersessionFixture
    $delayed.Snapshot.Boundaries[1].writerSequence=[long](@($delayed.Events | Where-Object Stage -CEQ $stage)[0].Fields.log_writer_sequence)+1
    $delayed.Snapshot.Boundaries[2].writerSequence=[long](@($delayed.Events | Where-Object Stage -CEQ 'retirement')[0].Fields.log_writer_sequence)+1
    Check ('repair_owner_delayed_drain_after_'+$stage) $delayed.Events $delayed.Snapshot 'SourceSuperseded' $true
}
foreach ($generation in @(9,10,41)) {
    $bad=RepairOwnerSupersessionFixture
    $bad.Snapshot.Boundaries[1].beforeStateGeneration=$generation
    $bad.Snapshot.Boundaries[1].afterStateGeneration=$generation
    foreach ($event in @($bad.Events | Where-Object Stage -CIn @('grant','settlement'))) {
        $event.Fields.repair_owner_state_generation=[string]$generation
    }
    Check ('repair_owner_outside_authored_interval_'+$generation) $bad.Events $bad.Snapshot 'DispatchedStalled'
}
# A pre-hold checkpoint mutation must not be pulled into the interval merely
# because its delayed diagnostic write occurs after the hold.
$preHold=RepairOwnerSupersessionFixture
$edge=ConvertFrom-Json (ConvertTo-Json -InputObject $preHold.Snapshot.Boundaries[1] -Depth 32 -Compress)
$edge.writerSequence=1500; $edge.beforeStateGeneration=8; $edge.afterStateGeneration=9; $edge.operations=@()
$edge.beforeBoundary.checkpointGeneration=1
$preHold.Snapshot.Boundaries+=@($edge)
Check 'repair_owner_pre_hold_delayed_history' $preHold.Events $preHold.Snapshot 'SourceSuperseded' $true
foreach ($stage in @('grant','settlement','later_grant','retirement')) {
    foreach ($field in @('repair_owner_coordinator_id','repair_owner_receipt_id','repair_owner_transition_id')) {
        $bad=RepairOwnerSupersessionFixture
        @($bad.Events | Where-Object Stage -CEQ $stage)[0].Fields.Remove($field)
        Check ("repair_owner_missing_${stage}_${field}") $bad.Events $bad.Snapshot 'DispatchedStalled'
    }
}
foreach ($case in @('retained_leg_role','lease_role','durable_generation','durable_identity','durable_fingerprint',
        'foreign_peer','replacement_data','replacement_publication','checkpoint_aba','delayed_checkpoint_aba','stale_receipt')) {
    $bad=RepairOwnerSupersessionFixture
    $ownerBoundary=$bad.Snapshot.Boundaries[1]; $p=$ownerBoundary.operations[0].repairOwner
    $commit=$bad.Snapshot.Boundaries[3].operations[0].routeTransition
    switch ($case) {
        'retained_leg_role' { $p.scope.retainedLegCheckpointGeneration=1 }
        'lease_role' { $p.scope.boundary.checkpointGeneration=1 }
        'durable_generation' { $commit.sourceBoundary.checkpointGeneration=0 }
        'durable_identity' { $commit.sourceBoundary.checkpointRequestId='(none)' }
        'durable_fingerprint' { $commit.durableCheckpointFingerprint='(none)' }
        'foreign_peer' { $p.coordinatorInstanceId='fte1-'+('9'*24) }
        'replacement_data' { $p.scope.boundary.dataSessionIdentity='fte1-'+('9'*24) }
        'replacement_publication' { $p.scope.publicationFingerprint='fte1-'+('9'*24) }
        'stale_receipt' {
            $a=@($bad.Events | Where-Object Stage -CEQ 'grant')[0]
            $b=@($bad.Events | Where-Object Stage -CEQ 'retirement')[0]
            foreach ($field in $script:FileTransferCoordinatorRepairOwnerCatalog.diagnosticFields) { $b.Fields[$field]=$a.Fields[$field] }
        }
        { $_ -cin @('checkpoint_aba','delayed_checkpoint_aba') } {
            $edge=ConvertFrom-Json (ConvertTo-Json -InputObject $ownerBoundary -Depth 32 -Compress)
            $edge.writerSequence=if ($case -ceq 'delayed_checkpoint_aba') { 3300 } else { 2500 }
            $edge.beforeStateGeneration=31; $edge.afterStateGeneration=32; $edge.operations=@()
            $edge.beforeBoundary.checkpointGeneration=1
            $bad.Snapshot.Boundaries+=@($edge)
        }
    }
    Check ('repair_owner_reject_'+$case) $bad.Events $bad.Snapshot 'DispatchedStalled'
}
