param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') { throw 'Frozen catalog changed.' }
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
$first = $envelopes[0]
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$families = $catalog.RequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
function Import-Definitions([string]$path, [string[]]$names) {
    $ast = [Management.Automation.Language.Parser]::ParseFile($path,[ref]$null,[ref]$null)
    foreach ($name in $names) {
        $definitions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name },$true))
        if ($definitions.Count -ne 1) { throw "Function not unique: $name" }
        . ([scriptblock]::Create(($definitions[0].Extent.Text -replace "function $name", "function script:$name")))
    }
}
Import-Definitions (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FileTunaPressureSettlementSnapshotEvidence.ps1') @('New-Effect','New-Lines')
Import-Definitions (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1') @('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification','Get-TunaGuiFileTransferSetupFailureClassification')
function Copy-Value($value) { $value | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json }
function Subject($envelope, [string]$role) {
    $matches = @($envelope.aggregate.operations | Where-Object { $_.family -ceq 'route_transition' -and $_.operationRole -ceq $role })
    if ($matches.Count -ne 1) { throw "Route subject not unique: $role" }
    $matches[0]
}
function Decision($values, [string]$kind) {
    $matches = @($values | Where-Object decisionKind -CEQ $kind)
    if ($matches.Count -ne 1) { throw "Decision not unique: $kind" }
    $matches[0]
}
function Assert-Parity([string[]]$lines, [bool]$failing, [string]$context) {
    $script:checks++
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated_diagnostic') + $lines)
    $compactLines = @($lines | Where-Object {
        $event = Get-FileTransferCoordinatorSnapshotEventName $_
        $catalog.SnapshotAuthorityV1 -contains $event -or $catalog.GenerationStampedEffects -contains $event
    })
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        $full.LegacySelectionCount -ne 0 -or
        ($null -ne $full.FirstUnresolvedBoundary) -ne $failing -or
        (-not $failing -and ($full.ArtifactEvidenceVerdict -cne 'pass' -or -not $full.StreamComplete))) {
        throw "Retained route grouping failed ($context): $($full.ArtifactEvidenceFailureReason); $($full.FirstUnresolvedBoundary | Select-Object * -ExcludeProperty operations | ConvertTo-Json -Compress)"
    }
    $path = Join-Path $OutputRoot 'retained-route-compact.log'
    [IO.File]::WriteAllLines($path,$compactLines)
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
    $boundary = $full.FirstUnresolvedBoundary
    if ($full.ArtifactEvidenceVerdict -ceq 'pass') {
        # This focused stream starts on established Tuna and first publishes
        # fallback generation 3. It cannot prove the omitted route prefix.
        $route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
        $compactRoute = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $compact
        $publications = @($full.Boundaries | Where-Object {
            @($_.operations | Where-Object { $_.family -ceq 'route_runtime' -and $_.operationRole -ceq 'route_publication' -and
                $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Published' -and $_.operationGeneration -eq 3 }).Count -eq 1
        })
        $scope = "$($first.coordinatorInstanceId)|fixture-writer|$($first.sessionId)|$($first.transferId)|$($first.direction)"
        $expectedFindings = @("snapshot route publication generation is not contiguous: scope=$scope; generation=3")
        $origin = if ($failing) { $boundary } else { $publications[0] }
        if ($failing) {
            if ($boundary.taxonomyClass -cne 'filetransfer_route_transition_incomplete') { throw "Unexpected retained-route failure ($context)." }
            $expectedFindings = @("snapshot route authority boundary is unresolved: class=$($boundary.taxonomyClass); transition_id=$($boundary.boundaryId)") + $expectedFindings
        }
        if ((Compare-FileTransferCoordinatorSnapshotRouteConsistency $route $compactRoute).Verdict -cne 'pass' -or
            $route.Verdict -cne 'fail' -or $publications.Count -ne 1 -or
            (ConvertTo-Json -InputObject @($route.Findings) -Compress) -cne (ConvertTo-Json -InputObject $expectedFindings -Compress) -or
            $route.FirstUnresolvedBoundary.boundaryId -cne $origin.boundaryId -or
            $route.FirstUnresolvedBoundary.boundaryHash -cne $origin.boundaryHash -or
            $gui.ClassificationSource -cne 'coordinator_snapshot_authority_v1_route_consistency') { throw "Unexpected partial-route rejection ($context)." }
        $boundary = $route.FirstUnresolvedBoundary
    }
    if ($null -eq $gui -or $null -eq $boundary) { throw "Missing GUI boundary ($context)." }
    else {
        if ($gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or $gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or
            $gui.FailureClass -cne $boundary.taxonomyClass -or $gui.CoordinatorWriter -cne $boundary.writer -or
            $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or $gui.CoordinatorRawCount -ne $boundary.rawCount -or
            $gui.CoordinatorDistinctCount -ne $boundary.distinctCount -or $gui.CoordinatorRecoveredCount -ne $boundary.recoveredCount -or
            $gui.CoordinatorUnresolvedCount -ne $boundary.unresolvedCount) {
            throw "GUI boundary differs ($context): expected=$($boundary | Select-Object boundaryId,boundaryHash,taxonomyClass,writerSequence | ConvertTo-Json -Compress); gui=$($gui | Select-Object FailureClass,CoordinatorBoundaryId,CoordinatorBoundaryHash,CoordinatorWriterSequence | ConvertTo-Json -Compress)"
        }
    }
}
$commit = Decision $envelopes 'CommitFileTransferRouteTransition'
$script:checks = 0
$terminal = Subject $commit 'terminal'
$prepared = Decision $envelopes 'PrepareFileTransferRouteTransition'
$disposition = Decision $envelopes 'ApplyFileTransferRouteTransitionDispositionReceipt'
$key = Get-FileTransferCoordinatorRouteTransitionLifecycleKey -Boundary ([pscustomobject]@{ coordinatorInstanceId=$first.coordinatorInstanceId; writer='fixture-writer' }) -Operation $terminal
foreach ($subject in @((Subject $prepared 'current'),(Subject $commit 'current'),(Subject $disposition $first.direction))) {
    if ((Get-FileTransferCoordinatorRouteTransitionLifecycleKey -Boundary ([pscustomobject]@{ coordinatorInstanceId=$first.coordinatorInstanceId; writer='fixture-writer' }) -Operation $subject) -cne $key) {
        throw "Commit-time frame attachment still changes the immutable lifecycle key: expected=$key; actual=$(Get-FileTransferCoordinatorRouteTransitionLifecycleKey -Boundary ([pscustomobject]@{ coordinatorInstanceId=$first.coordinatorInstanceId; writer='fixture-writer' }) -Operation $subject)"
    }
}
$lines = @(New-Lines $envelopes)
Assert-Parity $lines $false 'production retained-frame chain'
Assert-Parity (@($lines[0]) + $lines[1..($lines.Count-2)] + $lines[1..($lines.Count-2)] + @($lines[-1])) $false 'byte-identical duplicates'
$reordered = Copy-Value $envelopes
foreach ($e in $reordered) { [array]::Reverse($e.aggregate.operations) }
Assert-Parity @(New-Lines $reordered) $false 'descriptor order'

foreach ($kind in @('retained_frame_applied','route_transition_disposition_applied','completed_drain_receipt')) {
    Assert-Parity @($lines | Where-Object { (Get-FileTransferCoordinatorSnapshotLineField $_ effect_kind) -cne $kind }) $true "missing $kind"
}
if ($terminal.routeTransition.retainedFrameType -ceq 'filetransfer.frontier_request.v6') {
    Assert-Parity @($lines | Where-Object { (Get-FileTransferCoordinatorSnapshotLineField $_ effect_kind) -cne 'retained_frame_response' }) $true 'missing frontier response'
}
foreach ($mutation in @(
    { param($e,$o) $o.routeTransition.retainedFrameFingerprint='fte1-000000000000000000000000' },
    { param($e,$o) $o.routeTransition.retainedFrameType='filetransfer.wrong.v6' },
    { param($e,$o) $o.routeTransition.retainedFrameFingerprint='(none)'; $o.routeTransition.retainedFrameType='(none)' },
    { param($e,$o) $o.routeTransition.sourceBoundary.dataSessionGeneration++ },
    { param($e,$o) $o.routeTransition.sourceBoundary.checkpointGeneration++ },
    { param($e,$o) $o.routeTransition.targetBoundary.dataSessionIdentity+='-replacement' },
    { param($e,$o) $o.routeTransition.proofBoundaryFingerprint='fte1-000000000000000000000000' },
    { param($e,$o) $o.authorityId='fte1-000000000000000000000000' },
    { param($e,$o) $o.operationKey+='-foreign' },
    { param($e,$o) $o.operationGeneration++ },
    { param($e,$o) $o.afterState='None' },
    { param($e,$o) $copy=Copy-Value $o; $copy.routeTransition.retainedFrameFingerprint='fte1-000000000000000000000000'; $e.aggregate.operations+=$copy }
)) {
    $bad = Copy-Value $envelopes
    $e = Decision $bad 'ApplyFileTransferRouteTransitionRetainedFrameReceipt'
    & $mutation $e (Subject $e 'terminal')
    Assert-Parity @(New-Lines $bad) $true "mutated retained terminal: $mutation"
}
# A malformed intermediate terminal cannot be restored by a later exact carry.
$bad = Copy-Value $envelopes
$e = Decision $bad 'ApplyFileTransferRouteTransitionDispositionReceipt'
$o = Subject $e 'terminal'
$o.routeTransition.retainedFrameFingerprint='(none)'
$o.routeTransition.retainedFrameType='(none)'
Assert-Parity @(New-Lines $bad) $true 'terminal frame cleared'

foreach ($field in @('receipt_fingerprint','coordinator_state_generation','data_session_identity','operation_key')) {
    $bad = @($lines | ForEach-Object {
        if ((Get-FileTransferCoordinatorSnapshotLineField $_ effect_kind) -ceq 'retained_frame_applied') {
            $_ -replace "($field=)[^;]*", '${1}wrong'
        } else { $_ }
    })
    Assert-Parity $bad $true "mutated effect $field"
}
$effect = @($lines | Where-Object { (Get-FileTransferCoordinatorSnapshotLineField $_ effect_kind) -ceq 'retained_frame_applied' })
if ($effect.Count -ne 1) { throw 'Expected one exact retained-frame effect.' }
Assert-Parity (@($lines[0..($lines.Count-2)]) + @($effect[0] -replace 'effect_identity=[^;]*','effect_identity=conflicting-retained-frame-effect') + @($lines[-1])) $true 'competing retained-frame effect'
Write-Output "Retained-frame route lifecycle: $script:checks production grouping and strict full/compact/GUI controls passed."
