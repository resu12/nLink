param([string]$RepoRoot,[string]$InputPath)
$ErrorActionPreference='Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
function Check([bool]$Condition,[string]$Reason) { if (-not $Condition) { throw $Reason } }
function Copy-Value($Value) { ConvertFrom-Json (ConvertTo-Json -InputObject $Value -Depth 64 -Compress) }
$parent=Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $script:FileTransferCoordinatorBootstrapCallbackCatalogVersion
$catalog=Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $script:FileTransferCoordinatorRepairOwnerCatalogVersion
Check ($parent.Hash -ceq '0ad879a93ec4d4639ecffd0fecf1c64d2aa32dc594bd39c680b8ca30c3c492dd' -and
    $catalog.ParentCatalogHash -ceq $parent.Hash -and $catalog.Hash -ceq '02ecc01ad2fae32da2da0688930b6720a55d5a4833f59e986aeee90e521039d7' -and
    @($catalog.RepairOwnerCatalog.effectKinds).Count -eq 0 -and -not $catalog.RepairOwnerCatalog.physicalAuthority) 'catalog compatibility'
$lines=[IO.File]::ReadAllLines($InputPath)
$baseline=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
if ($baseline.ArtifactEvidenceVerdict -cne 'pass') {
    Write-Output ("selected="+$baseline.CatalogVersion+"/"+$baseline.CatalogHash)
    Write-Output ("emitted="+(Get-FileTransferCoordinatorSnapshotLineField $lines[0] 'snapshot_catalog_version')+"/"+
        (Get-FileTransferCoordinatorSnapshotLineField $lines[0] 'snapshot_catalog_hash'))
}
Check ($baseline.ArtifactEvidenceVerdict -ceq 'pass' -and $baseline.StreamComplete) $baseline.ArtifactEvidenceFailureReason
$snapshot=@($lines | Where-Object { (Get-FileTransferCoordinatorSnapshotEventName $_) -ceq 'filetransfer_coordinator_transition_snapshot' })[0]
$envelope=(ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $snapshot 'snapshot_payload')).Value
$operation=$envelope.aggregate.operations[0]
Check (Test-FileTransferCoordinatorRepairOwnerOperation $envelope $operation $catalog) 'projected operation rejected'
Check (-not (Test-FileTransferCoordinatorRepairOwnerOperation $envelope $operation $parent)) 'old catalog accepted new operation'
Check (-not (Test-FileTransferCoordinatorSnapshotEffectReceipt $operation ([pscustomobject]@{})) -and
    -not (Test-FileTransferCoordinatorSnapshotEffectScope $operation ([pscustomobject]@{}))) 'physical authority exposed'
$grantIndexes=@(0..($lines.Count-1) | Where-Object {
    (Get-FileTransferCoordinatorSnapshotEventName $lines[$_]) -ceq 'filetransfer_fallback_frontier_repair_authority_granted'
})
Check ($grantIndexes.Count -eq 1) 'expected one grant'
$grantIndex=$grantIndexes[0]
$event=ConvertFrom-FileTransferLogLine -Line $lines[$grantIndex] -FilePath $InputPath -LineNumber ($grantIndex+1) -Sequence ($grantIndex+1)
$binding=Resolve-FileTransferRepairOwnerEvidence $baseline $event
Check ($null -ne $binding) 'exact grant reference rejected'
Check ($binding.Operation.repairOwner.scope.leaseGeneration -eq 0 -and
    $binding.Operation.repairOwner.scope.boundary.checkpointGeneration -eq 0) 'active lease role changed'
# Consumer controls over the real emitted receipt, not new producer authority.
$redacted=$event.Fields.Clone()
$redacted.session_id='sess_[redacted]';$redacted.transfer_id='[redacted]'
$redacted.frontier_scope_session_id=$binding.Operation.repairOwner.sessionId
$redacted.frontier_scope_transfer_id=$binding.Operation.repairOwner.transferId
Check ($null -ne (Resolve-FileTransferRepairOwnerEvidence $baseline ([pscustomobject]@{Fields=$redacted}))) 'redacted exact grant reference rejected'
foreach ($field in @('frontier_scope_session_id','frontier_scope_transfer_id')) {
    foreach ($value in @($null,'wrong','fte1-'+('0'*24))) {
        $copy=$redacted.Clone()
        if ($null -eq $value) { $copy.Remove($field) } else { $copy[$field]=$value }
        Check ($null -eq (Resolve-FileTransferRepairOwnerEvidence $baseline ([pscustomobject]@{Fields=$copy}))) "redacted identity accepted:$field/$value"
    }
}
$data=[pscustomobject]@{Fields=@{data_session_identity='[redacted]:1';
    frontier_scope_data_session_identity=$binding.Operation.repairOwner.scope.boundary.dataSessionIdentity}}
Check ((Get-FileTransferDiagnosticScopeEvidenceToken $data 'data_session_identity') -ceq
    $binding.Operation.repairOwner.scope.boundary.dataSessionIdentity) 'redacted data identity rejected'
$data.Fields.Remove('frontier_scope_data_session_identity')
Check ((Get-FileTransferDiagnosticScopeEvidenceToken $data 'data_session_identity') -ceq '') 'missing redacted data identity accepted'
$data.Fields.data_session_identity='unredacted-data'
$data.Fields.frontier_scope_data_session_identity='fte1-'+('0'*24)
Check ((Get-FileTransferDiagnosticScopeEvidenceToken $data 'data_session_identity') -ceq '') 'conflicting raw/stable data identity accepted'
foreach ($field in $catalog.RepairOwnerCatalog.diagnosticFields) {
    $fields=$event.Fields.Clone(); $fields.Remove($field)
    Check ($null -eq (Resolve-FileTransferRepairOwnerEvidence $baseline ([pscustomobject]@{Fields=$fields}))) "missing:$field"
}
foreach ($field in @('repair_owner_coordinator_id','repair_owner_transition_id','repair_owner_receipt_id',
        'repair_owner_receipt_fingerprint','repair_owner_state_generation','repair_owner_operation_key',
        'authority_id','predecessor_authority_id','protocol_repair_request_id','request_correlation_id',
        'range_fingerprint','checkpoint_request_id','repair_generation','route_publication_generation','direction','session_id')) {
    $fields=$event.Fields.Clone(); $fields[$field]='wrong'
    Check ($null -eq (Resolve-FileTransferRepairOwnerEvidence $baseline ([pscustomobject]@{Fields=$fields}))) "wrong:$field"
}
foreach ($case in @('scope_owner','data_session','retained_checkpoint_request','lease_checkpoint','receipt','range_count',
        'repair_generation','effect','coordinator','extra_payload','state_generation')) {
    $copy=Copy-Value $envelope; $op=$copy.aggregate.operations[0]; $p=$op.repairOwner
    switch ($case) {
        'scope_owner' { $p.scope.boundary.runtimeGeneration++ }
        'data_session' { $p.scope.boundary.dataSessionIdentity='replacement' }
        'retained_checkpoint_request' { $p.scope.retainedLegCheckpointRequestId='replacement' }
        'lease_checkpoint' { $p.scope.boundary.checkpointGeneration++ }
        'receipt' { $p.receipts[0].receiptFingerprint='fte1-'+('a'*24) }
        'range_count' { $p.repair.rangeCount=0 }
        'repair_generation' { $p.repair.decisionGeneration++ }
        'effect' { $op.stateApplied=$true }
        'coordinator' { $p.coordinatorInstanceId='another-peer' }
        'extra_payload' { $op.routeRuntime=[pscustomobject]@{role='repair'} }
        'state_generation' { $copy.aggregate.afterStateGeneration++ }
    }
    Check (-not (Test-FileTransferCoordinatorRepairOwnerOperation $copy $op $catalog)) "malformed:$case"
}
$bad=Copy-Value $baseline
$bad.ArtifactEvidenceVerdict='fail'
Check ($null -eq (Resolve-FileTransferRepairOwnerEvidence $bad $event)) 'failed artifact accepted'
$duplicate=Get-FileTransferRepairOwnerEvidence -Boundaries @($binding.Boundary,$binding.Boundary) -Catalog $catalog
Check (@($duplicate.Errors).Count -eq 1) 'duplicate binding accepted'
Write-Output 'repair-owner-evidence-ok'
