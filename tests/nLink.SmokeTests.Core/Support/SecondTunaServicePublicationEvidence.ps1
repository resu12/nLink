param([string]$RepoRoot, [string]$InputPath, [string]$IncludeRejectedObservation = '0')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Fresh full, independently selected compact and shared-GUI evaluations of the
# generated receiver service stream. No live artifact or primary is reevaluated.
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/InboundBootstrapCheckpointEvidence.ps1') -RepoRoot $RepoRoot -InputPath $InputPath
foreach ($result in @($fullResult, $compactResult, $guiResult)) {
    $commits = @($result.Boundaries | Where-Object {
        $_.decisionKind -ceq 'CommitFileTransferRouteTransition' -and
        $_.afterBoundary.route -ceq 'file_tuna_v4'
    })
    if ($commits.Count -ne 2 -or
        @($commits | Where-Object { $_.afterBoundary.liveRouteEpoch -eq 3 }).Count -ne 1) {
        throw 'Expected actual initial and second-Tuna service commit boundaries.'
    }
    $failed = @($result.Boundaries | Where-Object { $_.verdict -cne 'pass' })
    $expectedFailures = 0
    if ($failed.Count -ne $expectedFailures) {
        Write-Output ('second-tuna-failures=' + ($failed | Select-Object evidenceLineIndex,decisionKind,decisionStatus,taxonomyClass | ConvertTo-Json -Compress))
        throw 'A service Snapshot boundary failed authority validation.'
    }
}
if (@($fullResult.Boundaries | Where-Object {
    $_.decisionKind -ceq 'ObserveRuntimeUnlockResponderPeerProbeHandoff' -and
    $_.decisionStatus -ceq 'Rejected' -and $_.afterBoundary.liveRouteEpoch -eq 3
}).Count -ne $(if ($IncludeRejectedObservation -ceq '1') { 2 } else { 0 })) {
    throw 'Expected both original rejected observations to remain in the evaluated stream.'
}
if ($IncludeRejectedObservation -ceq '1') {
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/SecondTunaBoundHandoffControls.ps1') -Evaluation $fullResult
}
Write-Output "second-tuna-service=checked; unresolved=$expectedFailures; boundaries=$($fullResult.Boundaries.Count); boundary_hash=$($fullResult.BoundaryHash); full_compact_gui=pass"
