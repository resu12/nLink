param([string]$RepoRoot, [string]$FixturePath, [int]$PublicationCount)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$evaluation = Get-FileTransferCoordinatorSnapshotEvaluation -Lines ([IO.File]::ReadAllLines($FixturePath))
$route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $evaluation
if ($PublicationCount -eq 2) {
    # An initial-owner proof is not a same-owner Tuna republication exemption.
    if ($route.Verdict -cne 'fail' -or
        -not (@($route.Findings) -match 'no unique runtime owner entrant: route=file_tuna_v4')) {
        throw 'A second same-owner Tuna publication bypassed route-transition proof.'
    }
    Write-Output 'initial-tuna-republication-still-requires-transition'
    return
}
if ($evaluation.ArtifactEvidenceVerdict -cne 'pass' -or -not $evaluation.StreamComplete -or
    $evaluation.LegacySelectionCount -ne 0) { throw 'Actual Core snapshot stream must be complete.' }
if ($route.Verdict -cne 'pass' -or $null -ne $evaluation.FirstUnresolvedBoundary) {
    throw "Prepared initial Tuna owner/publication rejected: routes=$($route.Findings -join ';'); taxonomy=$($evaluation.FirstUnresolvedBoundary.taxonomyClass)"
}
if ($route.RouteSelectedEvents.Count -ne 1 -or @($route.SelectedRouteChanges).Count -ne 1 -or
    $route.SelectedRouteChanges[0] -cne 'file_tuna_v4') { throw 'Expected exactly one initial Tuna route.' }
function Copy-Value($Value) { ConvertFrom-Json (ConvertTo-Json -InputObject $Value -Depth 64 -Compress) }
function Get-Proofs([object[]]$Boundaries, [object[]]$Effects) {
    $index = New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries @(
        $Boundaries | ForEach-Object { [pscustomobject]@{ Boundary=$_ } })
    Get-FileTransferCoordinatorSnapshotInitialPublications -AggregateIndex $index -Effects $Effects
}
$proofs = Get-Proofs $evaluation.Boundaries $evaluation.__RoutePublicationEffects
if ($proofs.Count -ne 1) { throw 'Missing exact initial Tuna proof.' }
# Invalid controls mutate only fresh, production-emitted fixture evidence.
# Readiness observations alone never create either an owner or a publication.
$cases = @('missing_birth','missing_owner','duplicate_owner','birth_rejected',
    'wrong_owner','retired_owner','missing_epoch','negative_epoch','later_epoch','wrong_protocol','invalid_tuna_v6',
    'route_mutation','runtime_mutation','leg_mutation','commit_mutation','terminal_mutation',
    'data_session_mutation','checkpoint_mutation','generation_gap','hash_gap','scope_mutation',
    'missing_effect','wrong_receipt','wrong_effect_owner','duplicate_effect','unvalidated_receipt',
    'publication_rejected','preparation_only')
foreach ($case in $cases) {
    $boundaries = Copy-Value $evaluation.Boundaries
    $effects = Copy-Value $evaluation.__RoutePublicationEffects
    $birth = @($boundaries | Where-Object decisionKind -CEQ 'StartTransfer')[0]
    $target = @($boundaries | Where-Object decisionKind -CEQ 'RoutePublication')[0]
    $owner = @($birth.operations | Where-Object operationRole -CEQ 'runtime_owner')[0]
    $publication = @($target.operations | Where-Object operationRole -CEQ 'route_publication')[0]
    switch ($case) {
        'missing_birth' { $boundaries = @($boundaries | Where-Object decisionKind -CNE 'StartTransfer') }
        'missing_owner' { $birth.operations = @() }
        'duplicate_owner' { $birth.operations = @($birth.operations) + @(Copy-Value $owner) }
        'birth_rejected' { $birth.decisionStatus = 'Rejected' }
        'wrong_owner' { $owner.authorityId = 'another-owner' }
        'retired_owner' { $owner.afterState = 'None' }
        'missing_epoch' { $target.afterBoundary.PSObject.Properties.Remove('liveRouteEpoch') }
        'negative_epoch' { $target.afterBoundary.liveRouteEpoch = -1L }
        'later_epoch' { $target.afterBoundary.liveRouteEpoch = 1L }
        'wrong_protocol' { $target.afterBoundary.protocolVersion = 6L }
        'invalid_tuna_v6' { $target.afterBoundary.route = 'file_tuna_v6'; $target.afterBoundary.protocolVersion = 6L }
        'route_mutation' { $target.afterBoundary.route = 'regular_nkn_v4_fast' }
        'runtime_mutation' { $target.afterBoundary.runtimeGeneration++ }
        'leg_mutation' { $target.afterBoundary.legGeneration++ }
        'commit_mutation' { $target.afterBoundary.routeCommitGeneration = 1L }
        'terminal_mutation' { $target.afterBoundary.terminalGeneration = 1L }
        'data_session_mutation' { $target.afterBoundary.dataSessionGeneration++ }
        'checkpoint_mutation' { $target.afterBoundary.checkpointGeneration++ }
        'generation_gap' { $target.beforeStateGeneration++; $target.afterStateGeneration++ }
        'hash_gap' { $target.beforeStateHash = 'unexplained-hash-gap' }
        'scope_mutation' { $target.transferId = 'another-transfer' }
        'missing_effect' { $effects = @() }
        'wrong_receipt' { $effects[0].ReceiptFingerprint = 'wrong-receipt' }
        'wrong_effect_owner' { $effects[0].AuthorityId = 'another-publication' }
        'duplicate_effect' { $effects = @($effects) + @(Copy-Value $effects[0]) }
        'unvalidated_receipt' { $publication.receiptValidated = $false }
        'publication_rejected' { $target.decisionStatus = 'Rejected' }
        'preparation_only' { foreach ($boundary in $boundaries) { $boundary.operations = @() } }
    }
    if ((Get-Proofs $boundaries $effects).Count -ne 0) { throw "Invalid $case retained initial Tuna proof." }
}
Write-Output "initial-prepared-tuna-route-ok negative-controls=$($cases.Count)"
