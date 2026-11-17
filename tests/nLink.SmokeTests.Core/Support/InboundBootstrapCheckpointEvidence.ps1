param([string]$RepoRoot, [string]$InputPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
. (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')
$lines = [IO.File]::ReadAllLines($InputPath)
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
$compactNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($name in (Get-GuiSmokeSemanticEvidenceEventNames)) { [void]$compactNames.Add($name) }
$snapshotNames = @($catalog.SnapshotAuthorityV1) + @($catalog.GenerationStampedEffects)
$compact = [Collections.Generic.List[object]]::new()
$gui = [Collections.Generic.List[object]]::new()
for ($i = 0; $i -lt $lines.Count; $i++) {
    $name = Get-FileTransferCoordinatorSnapshotEventName -Line $lines[$i]
    if ($compactNames.Contains($name)) {
        $compact.Add([pscustomobject]@{Line=$lines[$i]; LineIndex=[long]$i})
    }
    if ($snapshotNames -ccontains $name) {
        # Same immutable indexed input contract as the GUI consumer.
        $gui.Add([pscustomobject]@{Line=$lines[$i]; LineIndex=[long]$i})
    }
}
$fullResult = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
$compactResult = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $compact.ToArray()
$guiResult = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $gui.ToArray()
foreach ($result in @($fullResult, $compactResult, $guiResult)) {
    if ($result.Mode -cne 'SnapshotV1Authoritative' -or -not $result.StreamComplete -or
        $result.ArtifactEvidenceVerdict -cne 'pass' -or $result.LegacySelectionCount -ne 0) {
        throw "Invalid generated stream: $($result.ArtifactEvidenceFailureReason)"
    }
    $acceptances = @($result.Boundaries | Where-Object {
        $_.decisionKind -ceq 'FallbackCheckpointAccepted' -and $_.decisionStatus -ceq 'Accepted'
    })
    if ($acceptances.Count -ne 1) { throw 'Expected one exact checkpoint acceptance.' }
    $commits = @($result.Boundaries | Where-Object {
        $_.decisionKind -ceq 'CommitFileTransferRouteTransition' -and
        $_.afterBoundary.route -ceq 'post_tuna_fallback_v6'
    })
    if ($commits.Count -ne 1 -or $commits[0].afterBoundary.checkpointGeneration -le 0 -or
        $commits[0].afterBoundary.checkpointRequestId -ceq '(none)') {
        throw 'Fallback commit did not atomically register the checkpoint.'
    }
}
foreach ($other in @($compactResult, $guiResult)) {
    $parity = Compare-FileTransferCoordinatorSnapshotEvaluations -Full $fullResult -Compact $other
    if ($parity.Verdict -cne 'pass' -or
        ($fullResult.Boundaries | ConvertTo-Json -Depth 100 -Compress) -cne
        ($other.Boundaries | ConvertTo-Json -Depth 100 -Compress)) {
        throw 'Generated full/compact/shared-GUI boundary parity failed.'
    }
}
Write-Output "PASS: inbound bootstrap checkpoint; boundary_hash=$($fullResult.BoundaryHash)"
