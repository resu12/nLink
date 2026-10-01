param([string]$RepoRoot, [string]$FixturePath, [string]$Correlated)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
function Copy-Value($Value) { ConvertFrom-Json (ConvertTo-Json -InputObject $Value -Depth 64 -Compress) }
function Get-Proofs($Boundaries, $Effects) {
    $index = New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries @(
        $Boundaries | ForEach-Object { [pscustomobject]@{ Boundary=$_ } })
    Get-FileTransferCoordinatorSnapshotInitialPublications -AggregateIndex $index -Effects $Effects
}
function Set-RequestedField($Boundaries, $Birth, [string]$Field, $Value) {
    foreach ($boundary in $Boundaries) {
        if ([long]$boundary.afterStateGeneration -le [long]$Birth.beforeStateGeneration) {
            $boundary.beforeBoundary.$Field = $Value; $boundary.afterBoundary.$Field = $Value
        }
    }
    $Birth.beforeBoundary.$Field = $Value
}
function Set-OwnedField($Boundaries, $Birth, $Effects, [string]$Field, $Value) {
    foreach ($boundary in $Boundaries) {
        if ([long]$boundary.afterStateGeneration -lt [long]$Birth.afterStateGeneration) { continue }
        $boundary.afterBoundary.$Field = $Value
        if ($boundary -ne $Birth) { $boundary.beforeBoundary.$Field = $Value }
        foreach ($operation in @($boundary.operations | Where-Object { $_.family -ceq 'route_runtime' -and $_.stateApplied })) {
            $operation.routeRuntime.boundary.$Field = $Value
        }
    }
    foreach ($effect in $Effects) { $effect.$Field = $Value }
}
$lines = [IO.File]::ReadAllLines($FixturePath)
$full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
$records = @(for ($i=0; $i -lt $lines.Count; $i++) { [pscustomobject]@{ Line=$lines[$i]; LineIndex=$i } })
$compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
$route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
$compactRoute = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $compact
if ($full.ArtifactEvidenceVerdict -cne 'pass' -or -not $full.StreamComplete -or
    (Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
    (Compare-FileTransferCoordinatorSnapshotRouteConsistency $route $compactRoute).Verdict -cne 'pass') {
    throw "Snapshot integrity or parity failed: $($full.ArtifactEvidenceFailureReason)"
}
if ($route.Verdict -cne 'pass') { throw "Initial selection route proof rejected: $($route.Findings -join ';')" }
$starts = @($full.Boundaries | Where-Object decisionKind -CEQ 'StartTransfer')
$pins = @($full.Boundaries | Where-Object decisionKind -CEQ 'PinFallbackBootstrapOffer')
if ($starts.Count -ne 1 -or $pins.Count -ne [int][bool]::Parse($Correlated) -or
    $starts[0].beforeBoundary.route -cne 'file_tuna_v4' -or $starts[0].afterBoundary.route -cne 'regular_nkn_v4_fast' -or
    (Get-Proofs $full.Boundaries $full.__RoutePublicationEffects).Count -ne 1 -or
    ($route.SelectedRouteChanges -join ',') -cne 'regular_nkn_v4_fast') { throw 'Unexpected initial-selection fixture.' }

# The GUI must consume the same real-emitter evidence, not invent a route-cycle pass.
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$null, [ref]$null)
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification',
        'Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definitions = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name}, $true))
    if ($definitions.Count -ne 1) { throw "GUI function not unique: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
$gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' `
    -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($FixturePath)
if ($null -ne $gui) { throw 'GUI invented an initial-selection failure.' }

# Mutations start at the validated history reducer. Requested/selected route
# mutations preserve surrounding tuple continuity so the birth rule is exercised.
$cases = @('requested_fallback','requested_v6','inbound','reverse_selection','selected_fallback','selected_v6',
    'existing_owner','existing_leg','noninitial_owner','noninitial_leg','epoch','commit','terminal',
    'transport_changed','checkpoint_changed','session_changed','missing_owner','duplicate_owner','birth_rejected',
    'generation_gap','hash_gap','missing_effect','wrong_receipt','duplicate_effect','publication_rejected','post_owner_selection')
foreach ($case in $cases) {
    $boundaries = Copy-Value $full.Boundaries; $effects = Copy-Value $full.__RoutePublicationEffects
    $birth = @($boundaries | Where-Object decisionKind -CEQ 'StartTransfer')[0]
    $pub = @($boundaries | Where-Object decisionKind -CEQ 'RoutePublication')[0]
    $owner = @($birth.operations | Where-Object { $_.operationRole -ceq 'runtime_owner' -and $_.stateApplied })[0]
    switch ($case) {
        'requested_fallback' { Set-RequestedField $boundaries $birth route 'post_tuna_fallback_v6' }
        'requested_v6' { Set-RequestedField $boundaries $birth protocolVersion 6L }
        'inbound' { foreach ($boundary in $boundaries) { $boundary.direction='inbound' } }
        'reverse_selection' {
            Set-RequestedField $boundaries $birth route 'regular_nkn_v4_fast'
            Set-OwnedField $boundaries $birth $effects route 'file_tuna_v4'
        }
        'selected_fallback' {
            Set-OwnedField $boundaries $birth $effects route 'post_tuna_fallback_v6'
            Set-OwnedField $boundaries $birth $effects protocolVersion 6L
        }
        'selected_v6' { Set-OwnedField $boundaries $birth $effects protocolVersion 6L }
        'existing_owner' { Set-RequestedField $boundaries $birth runtimeGeneration 1L }
        'existing_leg' { Set-RequestedField $boundaries $birth legGeneration 1L }
        'noninitial_owner' {
            Set-OwnedField $boundaries $birth $effects runtimeGeneration 2L
            $owner.operationGeneration=2L; $owner.commandGeneration=2L
            $owner.operationKey="route_runtime|runtime_owner|$($owner.authorityId)|2|RuntimeOwner"
        }
        'noninitial_leg' { Set-OwnedField $boundaries $birth $effects legGeneration 2L }
        'epoch' { Set-RequestedField $boundaries $birth liveRouteEpoch 1L; Set-OwnedField $boundaries $birth $effects liveRouteEpoch 1L }
        'commit' { Set-RequestedField $boundaries $birth routeCommitGeneration 1L; Set-OwnedField $boundaries $birth $effects routeCommitGeneration 1L }
        'terminal' { Set-RequestedField $boundaries $birth terminalGeneration 1L; Set-OwnedField $boundaries $birth $effects terminalGeneration 1L }
        'transport_changed' { Set-OwnedField $boundaries $birth $effects transportEpoch 1L }
        'checkpoint_changed' { Set-OwnedField $boundaries $birth $effects checkpointGeneration 1L }
        'session_changed' { Set-OwnedField $boundaries $birth $effects dataSessionGeneration 1L }
        'missing_owner' { $birth.operations=@($birth.operations | Where-Object operationRole -CNE 'runtime_owner') }
        'duplicate_owner' { $birth.operations=@($birth.operations)+@(Copy-Value $owner) }
        'birth_rejected' { $birth.decisionStatus='Rejected' }
        'generation_gap' { $pub.beforeStateGeneration++; $pub.afterStateGeneration++ }
        'hash_gap' { $pub.beforeStateHash='unexplained-gap' }
        'missing_effect' { $effects=@() }
        'wrong_receipt' { $effects[0].ReceiptFingerprint='wrong-receipt' }
        'duplicate_effect' { $effects=@($effects)+@(Copy-Value $effects[0]) }
        'publication_rejected' { $pub.decisionStatus='Rejected' }
        'post_owner_selection' {
            $birth.afterBoundary.route='file_tuna_v4'; $owner.routeRuntime.boundary.route='file_tuna_v4'
            $pub.beforeBoundary.route='file_tuna_v4'
        }
    }
    if ((Get-Proofs $boundaries $effects).Count -ne 0) { throw "Invalid $case retained initial publication proof." }
}
[pscustomobject]@{ Verdict='pass'; BoundaryHash=$full.BoundaryHash; Correlated=[bool]::Parse($Correlated);
    PublicationProofs=1; NegativeControls=$cases.Count; RouteChanges=@($route.SelectedRouteChanges) } | ConvertTo-Json -Compress
