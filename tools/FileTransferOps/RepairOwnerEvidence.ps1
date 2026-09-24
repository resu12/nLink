Set-StrictMode -Version Latest

$script:FileTransferCoordinatorRepairOwnerCatalogVersion =
    'snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1-fallback-bootstrap-initiation-v1-source-callback-v1-repair-owner-v1'
$script:FileTransferCoordinatorRepairOwnerCatalogHash = '02ecc01ad2fae32da2da0688930b6720a55d5a4833f59e986aeee90e521039d7'
$script:FileTransferCoordinatorRepairOwnerCatalog = [ordered]@{
    version=1; family='route_runtime'; operationKind='FallbackFrontierRepairOwnerEvidence'
    operationRole='fallback_frontier_repair_owner'; decisionKind='CaptureRepairOwnerEvidence'
    receiptStage='repair_owner_evidence'; payloadName='repairOwner'; effectKinds=@(); physicalAuthority=$false
    payloadFields=[ordered]@{
        version='int64'; coordinatorInstanceId='string'; sessionId='string'; transferId='string'; direction='string'
        scope='CheckpointRepairContinuityScope'; repair='CheckpointRepairEvidence'
        receipts='FileTransferCoordinatorReceiptTransitionSnapshot[]'
    }
    scopeFields=$script:FileTransferCoordinatorCheckpointRepairContinuityCatalog.scopeFields
    repairFields=$script:FileTransferCoordinatorCheckpointRepairContinuityCatalog.repairFields
    diagnosticFields=@('repair_owner_profile','repair_owner_coordinator_id','repair_owner_transition_id',
        'repair_owner_state_generation','repair_owner_operation_key','repair_owner_receipt_id','repair_owner_receipt_fingerprint')
    checkpointRoles='scope.boundary=active-lease;scope.retainedLegCheckpointGeneration=retained-leg;routeTransition.sourceBoundary=durable-transition'
    binding='exact-coordinator-transition-operation-receipt-v1'
}

function Get-FileTransferCoordinatorRepairOwnerCatalog {
    $parent=Get-FileTransferCoordinatorBootstrapCallbackCatalog
    $json=ConvertTo-Json -InputObject $script:FileTransferCoordinatorRepairOwnerCatalog -Depth 16 -Compress
    $hash=Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([Text.UTF8Encoding]::new($false).GetBytes(
        "parentHash=$($parent.Hash)`nRepairOwnerCatalog=$json"))
    if ($hash -cne $script:FileTransferCoordinatorRepairOwnerCatalogHash) { throw "repair_owner_catalog_hash_invalid:$hash" }
    $result=[ordered]@{}
    foreach ($property in $parent.PSObject.Properties) { $result[$property.Name]=$property.Value }
    $result.Version=$script:FileTransferCoordinatorRepairOwnerCatalogVersion
    $result.Hash=$hash; $result.ParentCatalogHash=$parent.Hash
    $result.RepairOwnerCatalog=$script:FileTransferCoordinatorRepairOwnerCatalog
    [pscustomobject]$result
}

function Test-FileTransferCoordinatorRepairOwnerMarker {
    param($Operation)
    $null -ne $Operation -and (($Operation.PSObject.Properties.Name -contains 'repairOwner') -or
        ($Operation.PSObject.Properties.Name -contains 'operationRole' -and $Operation.operationRole -ceq 'fallback_frontier_repair_owner') -or
        ($Operation.PSObject.Properties.Name -contains 'operationKind' -and $Operation.operationKind -ceq 'FallbackFrontierRepairOwnerEvidence'))
}

function Test-FileTransferCoordinatorRepairOwnerOperation {
    param($Envelope,$Operation,$Catalog)
    try {
        $definition=$script:FileTransferCoordinatorRepairOwnerCatalog
        if ($Catalog.Version -cnotin @($script:FileTransferCoordinatorRepairOwnerCatalogVersion, $script:FileTransferCoordinatorActivationRetryCatalogVersion, $script:FileTransferCoordinatorReceiverPreAdmissionCatalogVersion, $script:FileTransferCoordinatorInitialDataSessionCatalogVersion) -or
            $Operation.family -cne $definition.family -or $Operation.operationKind -cne $definition.operationKind -or
            $Operation.operationRole -cne $definition.operationRole -or
            $Envelope.decisionKind -cne $definition.decisionKind -or $Envelope.decisionStatus -cne 'Idempotent' -or
            $Envelope.decisionReason -cne 'already_issued_repair_owner' -or
            $Operation.stateApplied -isnot [bool] -or $Operation.stateApplied -or
            $Operation.receiptValidated -isnot [bool] -or -not $Operation.receiptValidated -or
            $Operation.beforeState -cne 'Validated' -or $Operation.afterState -cne 'Validated' -or
            @($Envelope.aggregate.operations).Count -ne 1 -or
            $Envelope.aggregate.beforeStateGeneration -ne $Envelope.aggregate.afterStateGeneration -or
            $Envelope.aggregate.beforeStateFingerprint -cne $Envelope.aggregate.afterStateFingerprint -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $Envelope.aggregate.beforeBoundary $Envelope.aggregate.afterBoundary)) { return $false }
        foreach ($name in @('routeRuntime','routeTransition','runtimeUnlockRetry','receiveRecovery','fallbackTailYield',
                'regularV4FrontierRepairPressure','responderProbeHandoff','receiverBarrier','checkpointRepairContinuity')) {
            if ($Operation.PSObject.Properties.Name -contains $name -and $null -ne $Operation.$name) { return $false }
        }
        $p=$Operation.repairOwner; $r=$p.repair; $s=$p.scope
        if (-not (Test-FileTransferCheckpointRepairContinuityFields $p $definition.payloadFields) -or $p.version -ne 1 -or
            $p.direction -cne 'inbound' -or -not (Test-FileTransferCheckpointRepairContinuityScope $s) -or
            -not (Test-FileTransferCheckpointRepairContinuityAggregateScope $s $Envelope.aggregate.afterBoundary) -or
            -not (Test-FileTransferCheckpointRepairContinuityFields $r $definition.repairFields) -or
            $s.boundary.route -cne 'post_tuna_fallback_v6' -or $s.boundary.protocolVersion -ne 6 -or
            $s.boundary.transportEpoch -le 0 -or $s.boundary.routePublicationGeneration -le 0 -or
            $s.boundary.dataSessionGeneration -le 0 -or $s.boundary.dataSessionIdentity -ceq '(none)' -or
            $r.sourceKind -cne 'ReceiverRequest' -or $r.targetTransport -cne 'RegularNkn' -or
            $r.decisionGeneration -le 0 -or $r.frontierChunkIndex -lt 0 -or $r.durableReceivedHighestChunkIndex -lt -1 -or
            $r.rangeCount -le 0 -or $r.requestedChunkCount -le 0 -or
            $r.checkpointRequestId -cne $s.retainedLegCheckpointRequestId -or
            $Operation.authorityId -cne $r.authorityId -or $Operation.predecessorAuthorityId -cne $r.predecessorAuthorityId -or
            $Operation.operationGeneration -ne $r.decisionGeneration -or
            $Operation.operationKey -cne "route_runtime|fallback_frontier_repair_owner|$($r.authorityId)|$($r.decisionGeneration)|FallbackFrontierRepairOwnerEvidence") { return $false }
        foreach ($name in @('coordinatorInstanceId','sessionId','transferId','direction')) {
            if ($p.$name -cne $Envelope.$name) { return $false }
        }
        foreach ($name in @('authorityId','canonicalRepairKey','rangeFingerprint','protocolRepairRequestId','requestCorrelationId','repairFingerprint')) {
            if ($r.$name -ceq '(none)') { return $false }
        }
        if ($r.repairFingerprint -cnotmatch '^(?:[0-9a-f]{64}|fte1-[0-9a-f]{24})$' -or @($p.receipts).Count -ne 1) { return $false }
        $receipt=$p.receipts[0]
        $receiptFields=[ordered]@{ stage='string'; commandIdentity='string'; commandGeneration='int64'; commandFingerprint='string';
            receiptIdentity='string'; receiptGeneration='int64'; receiptStatus='string'; receiptReason='string'; receiptFingerprint='string'; receiptValidated='bool' }
        if (-not (Test-FileTransferCheckpointRepairContinuityFields $receipt $receiptFields) -or
            $receipt.stage -cne 'repair_owner_evidence' -or $receipt.receiptStatus -cne 'Validated' -or
            $receipt.receiptReason -cne 'already_issued_repair_owner' -or
            $receipt.receiptValidated -isnot [bool] -or -not $receipt.receiptValidated -or
            $receipt.commandGeneration -ne $r.decisionGeneration -or $receipt.receiptGeneration -ne $r.decisionGeneration) { return $false }
        foreach ($name in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity','receiptGeneration','receiptStatus','receiptFingerprint')) {
            if ([string]$Operation.$name -cne [string]$receipt.$name -or [string]$receipt.$name -ceq '(none)') { return $false }
        }
        foreach ($name in @('commandFingerprint','receiptFingerprint')) {
            if ($receipt.$name -cnotmatch '^(?:[0-9a-f]{64}|fte1-[0-9a-f]{24})$') { return $false }
        }
        return $true
    } catch { return $false }
}

function Get-FileTransferRepairOwnerEvidence {
    param([AllowEmptyCollection()][object[]]$Boundaries,$Catalog)
    $bindings=@{}; $signatures=@{}; $errors=[System.Collections.Generic.List[string]]::new()
    foreach ($boundary in $Boundaries) {
        foreach ($operation in @($boundary.operations | Where-Object { Test-FileTransferCoordinatorRepairOwnerMarker $_ })) {
            $key=@($boundary.coordinatorInstanceId,$boundary.boundaryId,$operation.operationKey,
                $operation.receiptIdentity,$operation.receiptFingerprint) -join [char]31
            $receiptKey=@($boundary.coordinatorInstanceId,$operation.receiptIdentity) -join [char]31
            $signature=ConvertTo-Json -InputObject $operation -Depth 32 -Compress
            if ($bindings.ContainsKey($key) -or ($signatures.ContainsKey($receiptKey) -and $signatures[$receiptKey] -cne $signature)) {
                $errors.Add("snapshot_repair_owner_binding_conflict:$($boundary.evidenceLineIndex)")
            }
            $signatures[$receiptKey]=$signature
            $bindings[$key]=[pscustomobject]@{ Boundary=$boundary; Operation=$operation }
        }
    }
    [pscustomobject]@{ CatalogVersion=$Catalog.Version; CatalogHash=$Catalog.Hash; Bindings=$bindings; Errors=@($errors.ToArray()) }
}

# Returns an issuance-time attestation, including on a later settlement log.
# It does not assert that the repair is still current or authorize any effect.
function Resolve-FileTransferRepairOwnerEvidence {
    param($SnapshotEvidence,$Event)
    function F([string]$Name) { [string]$Event.Fields[$Name] }
    if ((Get-FileTransferCoordinatorSnapshotObjectProperty $SnapshotEvidence 'Mode') -cne 'SnapshotV1Authoritative' -or
        (Get-FileTransferCoordinatorSnapshotObjectProperty $SnapshotEvidence 'ArtifactEvidenceVerdict') -cne 'pass' -or
        (Get-FileTransferCoordinatorSnapshotObjectProperty $SnapshotEvidence 'StreamComplete') -ne $true -or
        'route_runtime' -cnotin @(Get-FileTransferCoordinatorSnapshotObjectProperty $SnapshotEvidence 'AuthoritativeFamilies' @()) -or
        (F 'repair_owner_profile') -cnotin @($script:FileTransferCoordinatorRepairOwnerCatalogVersion, $script:FileTransferCoordinatorActivationRetryCatalogVersion, $script:FileTransferCoordinatorReceiverPreAdmissionCatalogVersion, $script:FileTransferCoordinatorInitialDataSessionCatalogVersion)) { return $null }
    $index=Get-FileTransferCoordinatorSnapshotObjectProperty $SnapshotEvidence '__RepairOwnerEvidence'
    if ($null -eq $index -or $index.CatalogVersion -cne (F 'repair_owner_profile') -or
        @($index.Errors).Count -gt 0 -or
        $index.CatalogHash -cne (Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion (F 'repair_owner_profile')).Hash) { return $null }
    foreach ($name in @('repair_owner_coordinator_id','repair_owner_transition_id','repair_owner_receipt_id','repair_owner_receipt_fingerprint')) {
        if ((F $name) -cnotmatch '^fte1-[0-9a-f]{24}$') { return $null }
    }
    $generation=0L
    if ((F 'repair_owner_state_generation') -cnotmatch '^(0|[1-9][0-9]*)$' -or
        -not [long]::TryParse((F 'repair_owner_state_generation'),[ref]$generation)) { return $null }
    $key=@((F 'repair_owner_coordinator_id'),(F 'repair_owner_transition_id'),(F 'repair_owner_operation_key'),
        (F 'repair_owner_receipt_id'),(F 'repair_owner_receipt_fingerprint')) -join [char]31
    if (-not $index.Bindings.ContainsKey($key)) { return $null }
    $binding=$index.Bindings[$key]; $p=$binding.Operation.repairOwner
    if ($binding.Boundary.afterStateGeneration -ne $generation -or (F 'direction') -cne $p.direction -or
        ((F 'continuity_coordinator_id') -ne '' -and (F 'continuity_coordinator_id') -cne $p.coordinatorInstanceId)) { return $null }
    foreach ($pair in @(@('session_id',$p.sessionId),@('transfer_id',$p.transferId))) {
        $raw=F $pair[0]
        if ([string]::IsNullOrWhiteSpace($raw) -or ($raw -cne '[redacted]' -and
            (ConvertTo-FileTransferCoordinatorEvidenceToken $raw) -cne $pair[1])) { return $null }
    }
    $tokens=@{ authority_id='authorityId'; predecessor_authority_id='predecessorAuthorityId'; repair_request_key='canonicalRepairKey';
        range_fingerprint='rangeFingerprint'; protocol_repair_request_id='protocolRepairRequestId'; request_correlation_id='requestCorrelationId';
        checkpoint_request_id='checkpointRequestId' }
    foreach ($name in $tokens.Keys) {
        if ([string]::IsNullOrWhiteSpace((F $name)) -or (ConvertTo-FileTransferCoordinatorEvidenceToken (F $name)) -cne $p.repair.($tokens[$name])) { return $null }
    }
    $repairFields=@{ repair_generation='decisionGeneration'; frontier_chunk_index='frontierChunkIndex';
        durable_received_highest_chunk_index='durableReceivedHighestChunkIndex'; range_count='rangeCount'; requested_chunk_count='requestedChunkCount'; source_kind='sourceKind' }
    foreach ($name in $repairFields.Keys) { if ((F $name) -cne [string]$p.repair.($repairFields[$name])) { return $null } }
    $scopeFields=@{ route='route'; protocol_version='protocolVersion'; live_route_epoch='liveRouteEpoch'; runtime_generation='runtimeGeneration';
        terminal_generation='terminalGeneration'; leg_generation='legGeneration'; transport_epoch='transportEpoch'; bridge_recovery_generation='bridgeRecoveryGeneration';
        route_publication_generation='routePublicationGeneration'; route_commit_generation='routeCommitGeneration' }
    foreach ($name in $scopeFields.Keys) { if ((F $name) -cne [string]$p.scope.boundary.($scopeFields[$name])) { return $null } }
    if ((F 'bridge_decision_generation') -cne [string]$p.scope.bridgeDecisionGeneration -or (F 'target_transport') -cne 'regular_nkn') { return $null }
    return $binding
}
