param([string]$RepoRoot, [string]$InputPath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
. (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')
$lines = [IO.File]::ReadAllLines($InputPath)
$names = @(Get-GuiSmokeSemanticEvidenceEventNames)
$records = @(for ($i=0; $i -lt $lines.Count; $i++) {
    if ($names -ccontains (Get-FileTransferCoordinatorSnapshotEventName -Line $lines[$i])) {
        [pscustomobject]@{Line=$lines[$i]; LineIndex=[long]$i}
    }
})
$full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
$compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
foreach ($mode in @('full','compact')) {
    $result = Get-Variable -Name $mode -ValueOnly
    $result | ConvertTo-Json -Depth 64 | Set-Content -LiteralPath (Join-Path $OutputRoot "$mode.json") -Encoding UTF8
    if ($result.Mode -cne 'SnapshotV1Authoritative' -or -not $result.StreamComplete -or
        $result.ArtifactEvidenceVerdict -cne 'pass' -or $result.LegacySelectionCount -ne 0) {
        throw "Invalid fresh service stream: $($result.ArtifactEvidenceFailureReason)"
    }
    $rejected = @($result.Boundaries | Where-Object {
        $_.decisionKind -ceq 'ObserveRuntimeUnlockResponderPeerProbeHandoff' -and
        $_.decisionReason -ceq 'data_session_availability_stale'
    })
    if ($rejected.Count -ne 1 -or $rejected[0].decisionStatus -cne 'Rejected' -or
        $rejected[0].beforeStateGeneration -ne $rejected[0].afterStateGeneration -or
        $rejected[0].beforeStateHash -cne $rejected[0].afterStateHash -or
        $rejected[0].operations[0].stateApplied) { throw 'Stale service callback was lost or mutated state.' }
    if (@($result.Boundaries | Where-Object verdict -CNE 'pass').Count -ne 0) {
        throw 'Completed initial handoff was reopened by stale availability.'
    }
}
if ((Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact).Verdict -cne 'pass') {
    throw 'Full/compact parity failed.'
}
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$null,[ref]$null)
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification','Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definitions = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if ($definitions.Count -ne 1) { throw "GUI function not unique: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
$gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' `
    -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($InputPath)
[pscustomobject]@{ classification=$gui } | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath (Join-Path $OutputRoot 'gui.json') -Encoding UTF8
if ($null -ne $gui) { throw 'GUI reopened the completed initial handoff.' }
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/InitialStaleResponderControls.ps1') -Evaluation $full
'PASS: fresh initial stale responder; full/compact/GUI; original rejected observation retained.'
