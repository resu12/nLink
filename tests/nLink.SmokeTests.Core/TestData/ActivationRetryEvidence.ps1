param([string]$RepoRoot,[string]$InputPath,[int]$ExpectedAncestry)
$ErrorActionPreference='Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
function Check([bool]$Condition,[string]$Reason) { if (-not $Condition) { throw $Reason } }
function Copy-Value($Value) { ConvertFrom-Json (ConvertTo-Json -InputObject $Value -Depth 64 -Compress) }
$catalog=Get-FileTransferCoordinatorActivationRetryCatalog
$parent=Get-FileTransferCoordinatorRepairOwnerCatalog
Check ($parent.Hash -ceq '02ecc01ad2fae32da2da0688930b6720a55d5a4833f59e986aeee90e521039d7' -and
    $catalog.ParentCatalogHash -ceq $parent.Hash -and -not $catalog.ActivationRetryCatalog.physicalAuthority) 'catalog compatibility'
$lines=[IO.File]::ReadAllLines($InputPath)
$snapshot=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
Check ($snapshot.StreamComplete) $snapshot.ArtifactEvidenceFailureReason
# Completed projection fixtures omit physical effects and cannot claim acceptance.
Check ($snapshot.ArtifactEvidenceVerdict -ceq 'fail' -and $null -eq $snapshot.__ActivationRetryEvidence) 'incomplete artifact admitted'
Check (@($snapshot.Boundaries | Where-Object decisionKind -CEQ 'CommitFileTransferRouteTransition').Count -eq 1) 'completed lineage missing'
$authored=@($snapshot.Boundaries | Where-Object decisionKind -CNE 'ArtifactEvidenceValidation')
$proof=Get-FileTransferActivationRetryEvidence $authored $catalog
Check ($proof.Bindings.Count -eq 1 -and $proof.Errors.Count -eq 0) "exact history missing: $($proof.Errors -join ','); boundaries=$(@($snapshot.Boundaries).Count); artifact=$($snapshot.ArtifactEvidenceFailureReason)"
$binding=@($proof.Bindings.Values)[0]
Check ($binding.InstallAncestry.Count -eq $ExpectedAncestry) 'effective install ancestry lost'
$operation=$binding.PreparedOperation
$p=$operation.routeTransition.activationRetryBinding
Check ($p.retryAuthorityId -cmatch '^fte1-[0-9a-f]{24}$' -and $p.installReceiptFingerprint -cmatch '^fte1-[0-9a-f]{24}$' -and
    $p.pairedReleaseAuthorityId -cmatch '^fte1-[0-9a-f]{24}$') 'unprojected references'
Check (-not (Test-FileTransferActivationRetryOperation $operation $parent)) 'old profile accepted binding'
foreach ($field in $catalog.ActivationRetryCatalog.payloadFields.Keys) {
    $copy=Copy-Value $operation
    $copy.routeTransition.activationRetryBinding.PSObject.Properties.Remove($field)
    Check (-not (Test-FileTransferActivationRetryOperation $copy $catalog)) "missing:$field"
}
$cases=@('missing','install','fingerprint','release','authority','consumption','duplicate','scope','changed')
foreach ($case in $cases) {
    $boundaries=Copy-Value $authored
    $start=@($boundaries | Where-Object boundaryId -CEQ $binding.PreparedBoundary.boundaryId)[0]
    $op=@($start.operations | Where-Object operationKey -CEQ $operation.operationKey)[0]
    switch ($case) {
        'missing' { $op.routeTransition.PSObject.Properties.Remove('activationRetryBinding') }
        'install' { $op.routeTransition.activationRetryBinding.installGeneration++ }
        'fingerprint' { $op.routeTransition.activationRetryBinding.installReceiptFingerprint='fte1-000000000000000000000000' }
        'release' { $op.routeTransition.activationRetryBinding.pairedReleaseAuthorityId='fte1-000000000000000000000000' }
        'authority' { $op.routeTransition.activationRetryBinding.retryAuthorityId='fte1-000000000000000000000000' }
        'changed' { $op.routeTransition.proofBoundaryFingerprint='fte1-000000000000000000000000' }
        'consumption' {
            foreach ($b in $boundaries) { $b.operations=@($b.operations | Where-Object operationRole -CNE 'release_reservation_consumed') }
        }
        'duplicate' { $boundaries+=Copy-Value $binding.EffectiveInstall.Boundary }
        'scope' { $op.routeTransition.sourceBoundary.dataSessionGeneration++ }
    }
    $result=Get-FileTransferActivationRetryEvidence $boundaries $catalog
    Check ($result.Errors.Count -gt 0 -and $result.Bindings.Count -eq 0) "accepted:$case"
}
Write-Output 'activation-retry-evidence-ok'
