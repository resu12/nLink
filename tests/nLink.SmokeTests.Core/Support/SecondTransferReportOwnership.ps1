param([Parameter(Mandatory=$true)][string]$RepoRoot)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repo = $RepoRoot
function Read-Ast([string]$Relative) {
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repo $Relative),[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw "Parse failed: $Relative" }
    return $ast
}
$parent=Read-Ast 'tools/Run-FileTransferRouteAcceptance.ps1'
$analyzer=Read-Ast 'tools/FileTransferOps/AnalyzerOrchestration.ps1'
$primary=$analyzer.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $n.Name -ceq 'Write-FileTransferCompactRetainedAnalysisArtifacts'},$true)
$evaluations=@($primary.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst] -and
    $n.GetCommandName() -ceq 'Get-FileTransferCoordinatorSnapshotEvaluation'},$true))
$reports=@($primary.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst] -and
    $n.GetCommandName() -ceq 'Write-FileTransferSecondTransferReport'},$true))
if ($evaluations.Count -ne 2 -or $reports.Count -ne 1 -or
    -not $reports[0].Extent.Text.Contains('-Full $fullSnapshotEvaluation -Compact $compactSnapshotEvaluation')) {
    throw 'primary_report_did_not_reuse_exact_evaluation_pair'
}
$wrapper=Read-Ast 'tools/Run-FileTransferTunaGuiSmoke.ps1'
foreach ($name in @('Invoke-TunaRouteAcceptanceRun','Try-ApplySecondTransferSplitProofAcceptance',
        'ConvertTo-RouteAcceptanceBool','Join-RouteAcceptanceTokenList','Get-JsonPropertyValue')) {
    $definitions=@($parent.FindAll({param($n)
        $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name
    },$true))
    if ($definitions.Count -ne 1) { throw "Production function not unique: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
$branches=@($wrapper.FindAll({param($n)
    $n -is [Management.Automation.Language.IfStatementAst] -and
    $n.Extent.Text.Contains('elseif ($RetainedAnalysisOwner -eq ''Wrapper'' -and ($RouteMode')
},$true))
if ($branches.Count -ne 1) { throw 'Wrapper owner branch is not unique' }
$script:wrapperBranch=[scriptblock]::Create($branches[0].Extent.Text)
$script:calls=New-Object 'System.Collections.Generic.List[object]'
function Invoke-TestWrapper([string]$Owner,[string]$Directory) {
    $RetainedAnalysisOwner=$Owner; $RouteMode='live-reactivation-second-transfer'
    $repoRoot=$repo; $resolvedArtifactDir=$Directory
    . $script:wrapperBranch
}
function Merge-TunaGuiMilestoneEvidenceIntoRetainedLogSlice { param($ArtifactDir) }
function Ensure-TunaGuiSecondTransferRetainedLogSlice {
    param($ArtifactDir)
    Join-Path $ArtifactDir 'filetransfer-second-transfer-retained-log-slice.log'
}
function Invoke-TunaGuiRetainedAnalysis {
    param($RepoRoot,$AnalysisDir,$LogPath,$LiveRouteProofMode)
    $script:calls.Add([pscustomobject]@{kind='wrapper_analysis';directory=$AnalysisDir})
}
function Invoke-RouteAcceptanceChildScriptNoThrow {
    param($ScriptPath,$Arguments)
    $ownerIndex=[Array]::IndexOf($Arguments,'-RetainedAnalysisOwner')
    $artifactIndex=[Array]::IndexOf($Arguments,'-ArtifactDir')
    if ($ownerIndex -lt 0 -or $artifactIndex -lt 0 -or $Arguments[$ownerIndex+1] -cne 'RouteAcceptance') {
        throw 'Parent did not delegate ownership as expected'
    }
    $script:calls.Add([pscustomobject]@{kind='child_delegation';owner=$Arguments[$ownerIndex+1]})
    Invoke-TestWrapper $Arguments[$ownerIndex+1] $Arguments[$artifactIndex+1]
    return 0
}
function Invoke-RouteAcceptanceRetainedAnalysis {
    param($ArtifactDir,$LiveRouteProofMode,[switch]$SecondTransferReport)
    if (-not $SecondTransferReport) { throw 'delegated_second_report_request_missing' }; $script:calls.Add([pscustomobject]@{kind='parent_analysis';directory=$ArtifactDir})
}
function Update-FileTransferTunaDirectEvidenceManifestFromReceipt {
    param($RepoRoot,$ArtifactDir,$ExpectedOwner,$AcceptanceRunId,$ScenarioRunId,$ExePath,
        $RouteMode,$PayloadSize,$Direction,$PayerMode,$Fault,$LiveToggleSequence,$LiveRouteProofMode)
    $script:calls.Add([pscustomobject]@{kind='receipt_reconciliation';directory=$ArtifactDir})
    [pscustomobject]@{CurrentRuntimeProof=$true}
}

# No child process, GUI, wallet, network, or authority evaluator is called.
# Only the actual ownership control flow runs; side effects are recorded stubs.
$directory=Join-Path $RepoRoot ('artifacts/second-report-not-created-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $directory) { throw 'Probe directory unexpectedly exists' }
$results=New-Object 'System.Collections.Generic.List[object]'
Invoke-TestWrapper 'Wrapper' $directory
if ($script:calls.Count -ne 2 -or $script:calls[0].directory -cne $directory -or
    $script:calls[1].directory -cne (Join-Path $directory 'second-transfer-analysis')) {
    throw 'Wrapper-owned control no longer schedules both analyses'
}
$results.Add([pscustomobject]@{case='wrapper_owned_control';passed=$true;analysisCalls=2;calls=@($script:calls.ToArray())})
$script:calls.Clear()
$TimeoutSeconds=30; $ProgressTimeoutSeconds=30; $script:RouteAcceptanceExecutionMode='fake'
Invoke-TunaRouteAcceptanceRun -RepoRoot $repo -ArtifactDir $directory -ResolvedExePath 'not-launched.exe' `
    -ResolvedWalletPath 'not-read.json' -ResolvedSidecarPath 'not-launched-sidecar.exe' `
    -EffectiveWalletPassword 'nonsecret-fixture' -RouteMode 'live-reactivation-second-transfer' `
    -Fault 'switch-off' -LiveToggleSequence 'off,on'
if ($script:calls.Count -ne 3 -or $script:calls[0].kind -cne 'child_delegation' -or
    $script:calls[1].kind -cne 'parent_analysis' -or $script:calls[1].directory -cne $directory -or
    $script:calls[2].kind -cne 'receipt_reconciliation') { throw 'Delegated ownership shape changed' }
$results.Add([pscustomobject]@{case='delegated_secondary_report_requested';passed=$true;analysisCalls=1;calls=@($script:calls.ToArray())})
$accepted=Try-ApplySecondTransferSplitProofAcceptance -Result ([pscustomobject]@{artifactDir=$directory}) `
    -Summary ([pscustomobject]@{firstTransferTerminalBeforeLiveReactivation=$false;
        liveRouteEpochRouteChanges=@('post_tuna_fallback_v6','file_tuna_v4')})
if ($accepted) { throw 'Missing secondary report was accepted' }
$results.Add([pscustomobject]@{case='missing_report_rejected';passed=$true;accepted=$false})
if (Test-Path -LiteralPath $directory) { throw 'Probe produced scenario artifacts' }
Write-Output 'second-transfer-owner-ok: one parent analysis, derived report requested, legacy wrapper unchanged'
