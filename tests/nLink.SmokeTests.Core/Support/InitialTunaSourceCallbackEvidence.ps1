param([string]$RepoRoot, [string]$InputPath, [string]$Terminal)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$lines = [IO.File]::ReadAllLines($InputPath)
$full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
$records = @(for ($i = 0; $i -lt $lines.Count; $i++) { [pscustomobject]@{Line=$lines[$i]; LineIndex=$i} })
$compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
$route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
if ($full.ArtifactEvidenceVerdict -cne 'pass' -or -not $full.StreamComplete -or
    $null -ne $full.FirstUnresolvedBoundary -or $route.Verdict -cne 'pass') {
    throw "Initial source callbacks failed: $($full.ArtifactEvidenceFailureReason); routes=$($route.Findings -join ';')"
}
if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
    (Compare-FileTransferCoordinatorSnapshotRouteConsistency $route (Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $compact)).Verdict -cne 'pass') {
    throw 'Independent initial source callback consumers differ.'
}
$callbacks = @($full.Boundaries | ForEach-Object operations | Where-Object operationRole -CEQ 'fallback_bootstrap_source_callback')
$consumed = @($callbacks | Where-Object afterState -CEQ 'Consumed')
$expected = if ([bool]::Parse($Terminal)) { 'Stale' } else { 'Current' }
if ($callbacks.Count -ne 2 -or $consumed.Count -ne 1 -or
    $consumed[0].routeTransition.bootstrapCallbacks.disposition -cne $expected -or
    $consumed[0].routeTransition.bootstrapCallbacks.allowedEffect -cne 'none') {
    throw 'Callback lifetime or diagnostic-only retirement changed.'
}
Write-Output "initial-source-callback-evidence-ok disposition=$expected"
