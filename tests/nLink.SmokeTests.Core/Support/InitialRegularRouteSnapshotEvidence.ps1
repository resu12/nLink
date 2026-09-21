param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot, [int]$PublicationCount, [string]$Canceled)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') { throw 'Frozen catalog changed.' }
$guiAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$null, [ref]$null)
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult', 'Get-TunaGuiExplicitFailureClassification', 'Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definitions = @($guiAst.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $true))
    if ($definitions.Count -ne 1) { throw "Missing GUI consumer: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
function Copy-Value($Value) { ConvertFrom-Json (ConvertTo-Json -InputObject $Value -Depth 64 -Compress) }
function Get-Proofs([object[]]$Boundaries, [object[]]$Effects) {
    $index = New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries @(
        $Boundaries | ForEach-Object { [pscustomobject]@{ Boundary=$_ } })
    Get-FileTransferCoordinatorSnapshotInitialPublications -AggregateIndex $index -Effects $Effects
}
function Publication($Boundary) {
    $matches = @($Boundary.operations | Where-Object {
        $_.family -ceq 'route_runtime' -and $_.operationRole -ceq 'route_publication' -and $_.afterState -ceq 'Published'
    })
    if ($matches.Count -ne 1) { throw 'Publication entrant not unique in fixture.' }
    $matches[0]
}
function Proof-Key($Boundary) {
    "$($Boundary.coordinatorInstanceId)|$($Boundary.writer)|$($Boundary.boundaryId)|$((Publication $Boundary).operationKey)"
}

$lines = [IO.File]::ReadAllLines($FixturePath)
$fullLines = @('event=unrelated_diagnostic; log_writer_id=diagnostic; log_writer_sequence=1') + $lines
$compactLines = @($fullLines | Where-Object {
    $event = Get-FileTransferCoordinatorSnapshotEventName $_
    $catalog.SnapshotAuthorityV1 -contains $event -or $catalog.GenerationStampedEffects -contains $event
})
$full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $fullLines
$compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
$fullRoute = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
$compactRoute = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $compact
if ($full.ArtifactEvidenceVerdict -cne 'pass' -or -not $full.StreamComplete -or $full.LegacySelectionCount -ne 0 -or
    (Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
    (Compare-FileTransferCoordinatorSnapshotRouteConsistency $fullRoute $compactRoute).Verdict -cne 'pass') {
    throw "Real-emitter evidence or independent parity failed: $($full.ArtifactEvidenceFailureReason)"
}
if ($null -ne $full.FirstUnresolvedBoundary -or $fullRoute.Verdict -cne 'pass') {
    throw "Initial Regular lifecycle must pass: $($full.FirstUnresolvedBoundary | Select-Object * -ExcludeProperty operations | ConvertTo-Json -Compress); routes=$($fullRoute.Findings -join ';')"
}
$publications = @($full.Boundaries | Where-Object { $_.decisionKind -ceq 'RoutePublication' -and $_.decisionStatus -ceq 'Accepted' } | Sort-Object afterStateGeneration)
$starts = @($full.Boundaries | Where-Object decisionKind -CEQ 'StartTransfer')
$terminals = @($full.Boundaries | Where-Object decisionKind -CEQ 'Terminalized')
if ($starts.Count -ne 1 -or $publications.Count -ne $PublicationCount -or
    $terminals.Count -ne [int][bool]::Parse($Canceled) -or $full.EffectCount -ne $PublicationCount) {
    throw 'Actual Start/publication/cancellation/effect coverage changed.'
}
$proofs = Get-Proofs $full.Boundaries $full.__RoutePublicationEffects
if ($proofs.Count -ne $PublicationCount) { throw "Expected $PublicationCount exact publication proofs, got $($proofs.Count)." }
$ownerKeys = @($proofs.Values | ForEach-Object { $_.RuntimeOwner.operationKey } | Sort-Object -Unique)
if ($ownerKeys.Count -ne 1 -or $fullRoute.RouteSelectedEvents.Count -ne $PublicationCount -or
    @($fullRoute.SelectedRouteChanges).Count -ne 1 -or $fullRoute.SelectedRouteChanges[0] -cne 'regular_nkn_v4_fast' -or
    @($full.Boundaries | ForEach-Object operations | Where-Object family -CEQ 'route_transition').Count -ne 0 -or
    @($full.Boundaries | Where-Object { $_.recoveredCount -gt 0 }).Count -ne 0) {
    throw 'Same-owner publication must not become a route step, recovery, or second Tuna route.'
}
$compactPath = Join-Path $OutputRoot 'independent-compact.log'
[IO.File]::WriteAllLines($compactPath, $compactLines)
$gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' `
    -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($compactPath)
if ($null -ne $gui) { throw "GUI invented a lifecycle boundary: $($gui | ConvertTo-Json -Depth 8 -Compress)" }

# Negative controls start with the validated, production-emitted aggregate and
# exact bound effects. Only invalid evidence is mutated; positives above are raw.
$cases = @('missing_epoch','negative_epoch','tuna_epoch_zero','fallback_epoch_zero','wrong_protocol',
    'data_session_mutation','runtime_mutation','leg_mutation','commit_mutation','checkpoint_mutation',
    'missing_owner','duplicate_owner','replaced_owner','generation_gap','hash_gap',
    'missing_effect','wrong_receipt','wrong_operation','duplicate_effect','unvalidated_receipt','publication_rejected','birth_rejected')
if ($PublicationCount -eq 2) { $cases += @('skipped_publication','regressed_publication','reused_authority','retirement_mismatch','terminal_aba') }
foreach ($case in $cases) {
    $boundaries = Copy-Value $full.Boundaries
    $effects = Copy-Value $full.__RoutePublicationEffects
    $pubs = @($boundaries | Where-Object { $_.decisionKind -ceq 'RoutePublication' -and $_.decisionStatus -ceq 'Accepted' } | Sort-Object afterStateGeneration)
    $target = $pubs[-1]
    $birth = @($boundaries | Where-Object decisionKind -CEQ 'StartTransfer')[0]
    $operation = Publication $target
    $key = Proof-Key $target
    $effect = @($effects | Where-Object TransitionId -CEQ $target.boundaryId)[0]
    switch ($case) {
        'missing_epoch' { $target.afterBoundary.PSObject.Properties.Remove('liveRouteEpoch') }
        'negative_epoch' { $target.afterBoundary.liveRouteEpoch = -1L }
        'tuna_epoch_zero' { $target.afterBoundary.route = 'file_tuna_v4' }
        'fallback_epoch_zero' { $target.afterBoundary.route = 'post_tuna_fallback_v6'; $target.afterBoundary.protocolVersion = 6L }
        'wrong_protocol' { $target.afterBoundary.protocolVersion = 6L }
        'data_session_mutation' { $target.afterBoundary.dataSessionGeneration = [long]$target.afterBoundary.dataSessionGeneration + 1L }
        'runtime_mutation' { $target.afterBoundary.runtimeGeneration = [long]$target.afterBoundary.runtimeGeneration + 1L }
        'leg_mutation' { $target.afterBoundary.legGeneration = [long]$target.afterBoundary.legGeneration + 1L }
        'commit_mutation' { $target.afterBoundary.routeCommitGeneration = 1L }
        'checkpoint_mutation' { $target.afterBoundary.checkpointGeneration = [long]$target.afterBoundary.checkpointGeneration + 1L }
        'missing_owner' { $birth.operations = @($birth.operations | Where-Object operationRole -CNE 'runtime_owner') }
        'duplicate_owner' {
            $owner = @($birth.operations | Where-Object { $_.operationRole -ceq 'runtime_owner' -and $_.stateApplied })[0]
            $birth.operations = @($birth.operations) + @(Copy-Value $owner)
        }
        'replaced_owner' {
            $owner = Copy-Value (@($birth.operations | Where-Object { $_.operationRole -ceq 'runtime_owner' -and $_.stateApplied })[0])
            $owner.authorityId = 'replacement-owner'; $target.operations = @($target.operations) + @($owner)
        }
        'generation_gap' { $target.beforeStateGeneration = [long]$target.beforeStateGeneration + 1L; $target.afterStateGeneration = [long]$target.afterStateGeneration + 1L }
        'hash_gap' { $target.beforeStateHash = 'unexplained-hash-gap' }
        'missing_effect' { $effects = @($effects | Where-Object TransitionId -CNE $target.boundaryId) }
        'wrong_receipt' { $effect.ReceiptFingerprint = 'wrong-receipt-fingerprint' }
        'wrong_operation' { $effect.OperationKey = 'wrong-operation-key' }
        'duplicate_effect' { $effects = @($effects) + @(Copy-Value $effect) }
        'unvalidated_receipt' { $operation.receiptValidated = $false }
        'publication_rejected' { $target.decisionStatus = 'Rejected' }
        'birth_rejected' { $birth.decisionStatus = 'Rejected' }
        'skipped_publication' { $target.afterBoundary.routePublicationGeneration = 3L; $operation.operationGeneration = 3L }
        'regressed_publication' { $target.afterBoundary.routePublicationGeneration = 1L; $operation.operationGeneration = 1L }
        'reused_authority' {
            $first = Publication $pubs[0]
            $operation.authorityId = $first.authorityId
            $operation.commandIdentity = $first.authorityId; $operation.receiptIdentity = $first.authorityId
            $operation.operationKey = "route_runtime|route_publication|$($first.authorityId)|2|RoutePublication"
            $effect.AuthorityId = $first.authorityId; $effect.ReceiptIdentity = $first.authorityId
            $effect.OperationKey = $operation.operationKey
            $key = Proof-Key $target
        }
        'retirement_mismatch' {
            @($target.operations | Where-Object { $_.operationRole -ceq 'route_publication' -and $_.afterState -ceq 'None' })[0].receiptFingerprint = 'wrong-retired-token'
        }
        'terminal_aba' {
            # Restore the apparent scope before publication two, after a terminal
            # mutation and matching restoration. The historical poison must stick.
            $pubs[0].afterBoundary.terminalGeneration = 1L
            $restore = Copy-Value $pubs[0]
            $restore.boundaryId = 'terminal-scope-restoration'
            $restore.beforeStateGeneration = $pubs[0].afterStateGeneration
            $restore.afterStateGeneration = [long]$restore.beforeStateGeneration + 1L
            $restore.beforeStateHash = $pubs[0].afterStateHash; $restore.afterStateHash = $target.beforeStateHash + '-restored'
            $restore.beforeBoundary = Copy-Value $pubs[0].afterBoundary
            $restore.afterBoundary = Copy-Value $target.beforeBoundary
            $restore.operations = @(); $restore.decisionKind = 'DiagnosticScopeRestoration'
            $target.beforeStateGeneration = $restore.afterStateGeneration
            $target.afterStateGeneration = [long]$target.beforeStateGeneration + 1L
            $target.beforeStateHash = $restore.afterStateHash
            $boundaries += @($restore)
        }
    }
    $negativeProofs = Get-Proofs $boundaries $effects
    if ($negativeProofs.ContainsKey($key)) { throw "Invalid $case retained publication proof." }
}
Write-Output "initial-regular-route-ok publications=$PublicationCount canceled=$Canceled negative-controls=$($cases.Count)"
