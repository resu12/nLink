param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
foreach ($envelope in $envelopes) { $envelope.snapshotSelectionMode = 'SnapshotV1Shadow' }
# Reuse serialization/evaluation helpers only. These effects describe an offline
# contract fixture, never a live route or physical transport success.
$helpers = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/PendingScopeInvalidationSnapshotEvidence.ps1'), [ref]$null, [ref]$null)
foreach ($name in @('Copy-Value','Subject','New-AppliedEffect','New-Lines')) {
    $definitions = @($helpers.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name }, $true))
    if ($definitions.Count -ne 1) { throw "Helper not unique: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
function Assert-Parity([string[]]$lines, [bool]$valid, [string]$reason = '') {
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated') + $lines)
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        ($full.ArtifactEvidenceVerdict -ceq 'pass') -ne $valid -or
        ($valid -and $null -ne $full.FirstUnresolvedBoundary) -or
        (-not $valid -and $full.ArtifactEvidenceFailureReason -notlike "*$reason*" -and
            $full.FirstUnresolvedBoundary.variant -cne $reason)) {
        throw "Retirement evidence mismatch: expected=$reason; actual=$($full.ArtifactEvidenceFailureReason)"
    }
}
$prepared = $envelopes[0]
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($prepared.coordinatorInstanceId); session_id=$($prepared.sessionId); transfer_id=$($prepared.transferId); direction=$($prepared.direction)"
$families = $script:FileTransferCoordinatorSnapshotRequiredFamilies -join ','
# This two-transition slice validates the retirement contract only. It does not
# contain the initial route publication history needed for route authority.
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=$families; authoritative_family_coverage="
if ((Subject $prepared).runtimeUnlockRetry.receipts[0].receiptReason -cne 'retry_dispatch_post_fallback_probe_rollback') { throw 'Retirement reason lost.' }
foreach ($envelope in $envelopes) {
    if (-not (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog $envelope (Subject $envelope))) { throw 'Exact retirement snapshot rejected.' }
}
Assert-Parity (New-Lines $envelopes) $true
Assert-Parity (New-Lines $envelopes $false 'missing') $false 'snapshot_scope_invalidation_required_effect_missing'
Assert-Parity (New-Lines @($envelopes[1])) $false 'family2_scope_invalidation_application_without_exact_pending_history'
foreach ($mutation in @(
    { param($op) $op.runtimeUnlockRetry.sourceBoundary.dataSessionGeneration++ },
    { param($op) $op.runtimeUnlockRetry.sourceBoundary.transportEpoch++ },
    { param($op) $op.commandFingerprint = 'changed'; $op.runtimeUnlockRetry.receipts[0].commandFingerprint = 'changed' }
)) {
    $bad = Copy-Value $envelopes[1]
    & $mutation (Subject $bad)
    Assert-Parity (New-Lines @($envelopes[0], $bad)) $false 'family2_scope_invalidation_application_without_exact_pending_history'
}
Write-Output 'Exact probe rollback retirement and five negative full/compact evidence controls passed; no live or GUI route-authority claim.'
