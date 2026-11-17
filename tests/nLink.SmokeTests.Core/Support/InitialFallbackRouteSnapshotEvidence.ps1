param([string]$RepoRoot, [string]$FixturePath, [int]$PublicationCount)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$lines = [IO.File]::ReadAllLines($FixturePath)
$full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
$records = @(for ($i = 0; $i -lt $lines.Count; $i++) { [pscustomobject]@{ Line = $lines[$i]; LineIndex = $i } })
$compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
$route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
$compactRoute = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $compact
if ($full.ArtifactEvidenceVerdict -cne 'pass' -or -not $full.StreamComplete -or $full.LegacySelectionCount -ne 0 -or
    (Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
    (Compare-FileTransferCoordinatorSnapshotRouteConsistency $route $compactRoute).Verdict -cne 'pass') {
    throw "Fresh evidence or parity failed: $($full.ArtifactEvidenceFailureReason)"
}
$birth = @($full.Boundaries | Where-Object decisionKind -CEQ 'StartTransfer')
$publication = @($full.Boundaries | Where-Object decisionKind -CEQ 'RoutePublication')
$owners = @($birth.operations | Where-Object {
    $_.operationRole -ceq 'runtime_owner' -and $_.beforeState -ceq 'None' -and
    $_.afterState -ceq 'Current' -and $_.stateApplied
})
if ($birth.Count -ne 1 -or $publication.Count -ne $PublicationCount -or $owners.Count -ne 1 -or
    $full.__RoutePublicationEffects.Count -ne $PublicationCount -or
    $birth[0].afterStateHash -cne $publication[0].beforeStateHash -or
    $publication[0].afterBoundary.route -cne 'post_tuna_fallback_v6' -or
    $publication[0].afterBoundary.protocolVersion -ne 6 -or
    $publication[0].afterBoundary.liveRouteEpoch -ne 0 -or
    $owners[0].operationGeneration -ne $publication[0].afterBoundary.runtimeGeneration) {
    throw 'Fresh fixture lost its actual initial owner/publication chain.'
}
function Copy-Value($Value) { ConvertFrom-Json (ConvertTo-Json -InputObject $Value -Depth 64 -Compress) }
function Get-Proofs([object[]]$Boundaries, [object[]]$Effects) {
    $index = New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries @(
        $Boundaries | ForEach-Object { [pscustomobject]@{ Boundary = $_ } })
    Get-FileTransferCoordinatorSnapshotInitialPublications -AggregateIndex $index -Effects $Effects
}
$proofs = Get-Proofs $full.Boundaries $full.__RoutePublicationEffects
if ($proofs.Count -ne 1 -or $route.RouteSelectedEvents.Count -ne $PublicationCount) {
    throw 'Only the first initial fallback publication may retain the owner proof.'
}
$guiAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$null, [ref]$null)
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult', 'Get-TunaGuiExplicitFailureClassification', 'Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definition = @($guiAst.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $true))
    if ($definition.Count -ne 1) { throw "Missing GUI consumer: $name" }
    . ([scriptblock]::Create($definition[0].Extent.Text))
}
$gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' `
    -RouteMode 'live-reactivation-second-transfer' -ImmutableEvidencePaths @($FixturePath)
if ($PublicationCount -eq 2) {
    if ($route.Verdict -cne 'fail' -or
        @($route.Findings | Where-Object { $_ -ceq 'snapshot route publication has no unique runtime owner entrant: route=post_tuna_fallback_v6' }).Count -ne 1 -or
        $null -eq $gui -or $gui.CoordinatorBoundaryId -cne $route.FirstUnresolvedBoundary.boundaryId -or
        $gui.CoordinatorBoundaryHash -cne $route.FirstUnresolvedBoundary.boundaryHash) {
        throw 'Second same-owner fallback publication bypassed its independent proof or lost GUI parity.'
    }
}
elseif ($route.Verdict -cne 'pass' -or $null -ne $full.FirstUnresolvedBoundary -or $null -ne $gui -or
    @($route.SelectedRouteChanges).Count -ne 1 -or $route.SelectedRouteChanges[0] -cne 'post_tuna_fallback_v6') {
    throw "Exact initial fallback proof rejected: $($route.Findings -join ';')"
}
$cases = @()
if ($PublicationCount -eq 1) {
    # Only negative controls alter fresh fixture evidence; the positive above
    # uses unmodified Core snapshots and production-emitted modeled effects.
    $cases = @('missing_birth','missing_owner','duplicate_owner','birth_rejected','wrong_owner','retired_owner',
        'missing_epoch','negative_epoch','later_epoch','wrong_protocol','invalid_tuna_v6','route_mutation',
        'runtime_mutation','leg_mutation','commit_mutation','terminal_mutation','data_session_mutation','checkpoint_mutation',
        'generation_gap','hash_gap','session_mutation','transfer_mutation','direction_mutation','writer_mutation',
        'missing_effect','wrong_receipt','wrong_effect_owner','duplicate_effect','unvalidated_receipt','publication_rejected',
        'diagnostics_only','fallback_protocol4_coherent','regular_protocol6_coherent','tuna_protocol6_coherent')
    foreach ($case in $cases) {
        $boundaries = Copy-Value $full.Boundaries
        $effects = Copy-Value $full.__RoutePublicationEffects
        $start = @($boundaries | Where-Object decisionKind -CEQ 'StartTransfer')[0]
        $target = @($boundaries | Where-Object decisionKind -CEQ 'RoutePublication')[0]
        $owner = @($start.operations | Where-Object { $_.operationRole -ceq 'runtime_owner' -and $_.stateApplied })[0]
        $operation = @($target.operations | Where-Object operationRole -CEQ 'route_publication')[0]
        switch ($case) {
            'missing_birth' { $boundaries = @($boundaries | Where-Object decisionKind -CNE 'StartTransfer') }
            'missing_owner' { $start.operations = @() }
            'duplicate_owner' { $start.operations = @($start.operations) + @(Copy-Value $owner) }
            'birth_rejected' { $start.decisionStatus = 'Rejected' }
            'wrong_owner' { $owner.authorityId = 'another-owner' }
            'retired_owner' { $owner.afterState = 'None' }
            'missing_epoch' { $target.afterBoundary.PSObject.Properties.Remove('liveRouteEpoch') }
            'negative_epoch' { $target.afterBoundary.liveRouteEpoch = -1L }
            'later_epoch' { $target.afterBoundary.liveRouteEpoch = 1L }
            'wrong_protocol' { $target.afterBoundary.protocolVersion = 4L }
            'invalid_tuna_v6' { $target.afterBoundary.route = 'file_tuna_v6' }
            'route_mutation' { $target.afterBoundary.route = 'regular_nkn_v4_fast'; $target.afterBoundary.protocolVersion = 4L }
            'runtime_mutation' { $target.afterBoundary.runtimeGeneration++ }
            'leg_mutation' { $target.afterBoundary.legGeneration++ }
            'commit_mutation' { $target.afterBoundary.routeCommitGeneration = 1L }
            'terminal_mutation' { $target.afterBoundary.terminalGeneration = 1L }
            'data_session_mutation' { $target.afterBoundary.dataSessionGeneration++ }
            'checkpoint_mutation' { $target.afterBoundary.checkpointGeneration++ }
            'generation_gap' { $target.beforeStateGeneration++; $target.afterStateGeneration++ }
            'hash_gap' { $target.beforeStateHash = 'unexplained-gap' }
            'session_mutation' { $target.sessionId = 'another-session' }
            'transfer_mutation' { $target.transferId = 'another-transfer' }
            'direction_mutation' { $target.direction = if ($target.direction -ceq 'inbound') { 'outbound' } else { 'inbound' } }
            'writer_mutation' { $target.writer = 'another-writer' }
            'missing_effect' { $effects = @() }
            'wrong_receipt' { $effects[0].ReceiptFingerprint = 'another-receipt' }
            'wrong_effect_owner' { $effects[0].AuthorityId = 'another-publication' }
            'duplicate_effect' { $effects = @($effects) + @(Copy-Value $effects[0]) }
            'unvalidated_receipt' { $operation.receiptValidated = $false }
            'publication_rejected' { $target.decisionStatus = 'Rejected' }
            'diagnostics_only' { foreach ($boundary in $boundaries) { $boundary.operations = @() } }
            { $_ -in @('fallback_protocol4_coherent','regular_protocol6_coherent','tuna_protocol6_coherent') } {
                $invalidRoute = switch ($case) { 'fallback_protocol4_coherent' { 'post_tuna_fallback_v6' }; 'regular_protocol6_coherent' { 'regular_nkn_v4_fast' }; default { 'file_tuna_v4' } }
                $invalidProtocol = if ($case -ceq 'fallback_protocol4_coherent') { 4L } else { 6L }
                foreach ($boundary in $boundaries) {
                    $tuples = @($boundary.beforeBoundary, $boundary.afterBoundary) + @($boundary.operations | Where-Object { $null -ne $_.routeRuntime } | ForEach-Object { $_.routeRuntime.boundary })
                    foreach ($tuple in $tuples) { $tuple.route = $invalidRoute; $tuple.protocolVersion = $invalidProtocol }
                }
                foreach ($effect in $effects) { $effect.Route = $invalidRoute; $effect.ProtocolVersion = $invalidProtocol }
            }
        }
        if ((Get-Proofs $boundaries $effects).Count -ne 0) { throw "Invalid $case retained initial fallback proof." }
    }
}
[ordered]@{
    direction = $birth[0].direction; runtimeGeneration = $owners[0].operationGeneration
    legGeneration = $publication[0].afterBoundary.legGeneration; publicationCount = $PublicationCount
    artifactEvidenceVerdict = $full.ArtifactEvidenceVerdict; routeVerdict = $route.Verdict
    initialProofCount = $proofs.Count; negativeControls = $cases.Count; findings = @($route.Findings)
} | ConvertTo-Json -Depth 4 -Compress
Write-Output 'initial-fallback-owner-contract-ok'
