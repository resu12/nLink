param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') { throw 'Frozen catalog changed.' }
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
$failed = $envelopes[-1]; $first = $failed
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$families = $catalog.RequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
foreach ($source in @(
    @{ Path='tests/nLink.SmokeTests.Core/Support/FileTunaPressureSettlementSnapshotEvidence.ps1'; Names=@('New-Effect','New-Lines') },
    @{ Path='tools/GuiSmoke-Windows.ps1'; Names=@('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification','Get-TunaGuiFileTransferSetupFailureClassification') })) {
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot $source.Path),[ref]$null,[ref]$null)
    foreach ($name in $source.Names) {
        $definitions = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
        if ($definitions.Count -ne 1) { throw "Definition not unique: $name" }
        . ([scriptblock]::Create($definitions[0].Extent.Text))
    }
}
function Copy-Value($value) { $value | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json }
function Assert-Parity($value, [bool]$valid) {
    $lines = @(New-Lines @($value))
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated') + $lines)
    $compactLines = @($lines | Where-Object { $catalog.SnapshotAuthorityV1 -contains (Get-FileTransferCoordinatorSnapshotEventName $_) })
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        ($full.ArtifactEvidenceVerdict -ceq 'pass') -ne $valid -or
        ($valid -and $null -ne $full.FirstUnresolvedBoundary)) { throw "Failed issuance parity: $($full.ArtifactEvidenceFailureReason)" }
    $path = Join-Path $OutputRoot 'compact.log'; [IO.File]::WriteAllLines($path,$compactLines)
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
    if ($valid) { if ($null -ne $gui) { throw 'GUI invented a Failed-yield incident.' } }
    elseif ($gui.CoordinatorBoundaryHash -cne $full.FirstUnresolvedBoundary.boundaryHash -or
        $gui.CoordinatorBoundaryId -cne $full.FirstUnresolvedBoundary.boundaryId -or
        $gui.CoordinatorWriterSequence -ne $full.FirstUnresolvedBoundary.writerSequence) { throw 'GUI failure boundary differs.' }
}
Assert-Parity $failed $true
foreach ($mutate in @(
    {param($e,$o) $o.fallbackTailYield.reservationGeneration=1},
    {param($e,$o) $o.fallbackTailYield.consumingInstallGeneration=1},
    {param($e,$o) $o.fallbackTailYield.reservedRetryAuthorityId='foreign'},
    {param($e,$o) $o.fallbackTailYield.pairedReleaseAuthorityId=$o.authorityId},
    {param($e,$o) $o.operationKey+='-wrong'},
    {param($e,$o) $o.receiptFingerprint='wrong'},
    {param($e,$o) $o.operationGeneration++},
    {param($e,$o) $o.fallbackTailYield.sourceBoundary.dataSessionGeneration++},
    {param($e,$o) $e.decisionStatus='Rejected'},
    {param($e,$o) $e.aggregate.operations+=Copy-Value $o},
    {param($e,$o) $e.aggregate.operations=@()}
)) {
    $bad=Copy-Value $failed; & $mutate $bad $bad.aggregate.operations[0]
    Assert-Parity $bad $false
}
$historicalShape=Copy-Value $failed
$consumed = @($envelopes | Where-Object decisionKind -CEQ 'CommitRuntimeUnlockRetryDispatchInstall')[0]
$old = @($consumed.aggregate.operations | Where-Object operationRole -CEQ 'release_reservation_consumed')[0]
$historicalShape.aggregate.operations += Copy-Value $old
Assert-Parity $historicalShape $false

# Exercise the shared generation-indexed ownership reducer with the complete,
# production-generated reservation/stage/install prefix (no event-order joins).
function New-Index($values) {
    $entries = foreach ($e in $values) {
        [pscustomobject]@{
            Boundary=[pscustomobject]@{
                coordinatorInstanceId=$e.coordinatorInstanceId; writer='fixture-writer'; boundaryId=$e.transitionId
                beforeStateGeneration=$e.aggregate.beforeStateGeneration; afterStateGeneration=$e.aggregate.afterStateGeneration
                beforeStateHash=$e.aggregate.beforeStateFingerprint; afterStateHash=$e.aggregate.afterStateFingerprint
                operations=$e.aggregate.operations; lineIndex=10
            }
            BeforeBoundary=$e.aggregate.beforeBoundary; AfterBoundary=$e.aggregate.afterBoundary
        }
    }
    New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries @($entries)
}
$prefixValues = @($envelopes | Where-Object { $_.aggregate.afterStateGeneration -le $consumed.aggregate.afterStateGeneration })
if (@(Resolve-FileTransferCoordinatorConsumedReleaseHistory (New-Index $prefixValues) @{}).Count -ne 0) { throw 'Exact consuming install rejected.' }
foreach ($mutate in @(
    {param($e,$o) $o.fallbackTailYield.pairKey='foreign'},
    {param($e,$o) $o.fallbackTailYield.sourceBoundary.dataSessionGeneration++},
    {param($e,$o) $o.receiptFingerprint='foreign'},
    {param($e,$o) $o.commandGeneration++},
    {param($e,$o) $e.aggregate.operations=@($e.aggregate.operations | Where-Object operationRole -CNE 'release_reservation_current')},
    {param($e,$o) $e.aggregate.operations=@($e.aggregate.operations | Where-Object operationRole -CNE 'attempt_installed')},
    {param($e,$o) $e.aggregate.operations+=Copy-Value (@($e.aggregate.operations | Where-Object operationRole -CEQ 'attempt_installed')[0])},
    {param($e,$o) (@($e.aggregate.operations | Where-Object operationRole -CEQ 'attempt_installed')[0]).runtimeUnlockRetry.sourceBoundary.dataSessionGeneration++}
)) {
    $values=Copy-Value $prefixValues; $last=$values[-1]
    $operation=@($last.aggregate.operations | Where-Object operationRole -CEQ 'release_reservation_consumed')[0]
    & $mutate $last $operation
    if (@(Resolve-FileTransferCoordinatorConsumedReleaseHistory (New-Index $values) @{}).Count -ne 1) { throw 'Malformed consumption passed.' }
}
# The exact reservation entrant is required even if retirement/install are present.
$missing = Copy-Value $prefixValues
foreach($e in $missing) { $e.aggregate.operations=@($e.aggregate.operations | Where-Object { -not ($_.operationRole -ceq 'release_reservation_current' -and $_.beforeState -ceq 'None') }) }
if (@(Resolve-FileTransferCoordinatorConsumedReleaseHistory (New-Index $missing) @{}).Count -ne 1) { throw 'Missing reservation history passed.' }
Write-Output 'Failed-yield full/compact/GUI parity and exact reservation ownership controls passed.'
