param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) { throw 'Route parent parse failed' }
foreach($name in @('ConvertTo-RouteAcceptanceInt','Read-RouteAcceptanceKeyValueArtifact','Get-RouteAcceptanceReportValue',
    'Write-RouteAcceptanceRetainedAnalysisTimeoutArtifacts','Test-RouteAcceptanceRetainedAnalysisCompletedProofArtifacts',
    'Write-RouteAcceptanceFakeAuthoritativeSnapshotEvidence','Stop-RouteAcceptanceRetainedAnalysisProcess',
    'Invoke-RouteAcceptanceChildScriptNoThrow','Invoke-RouteAcceptanceRetainedAnalysis')) {
    $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
    if($definition.Count -ne 1) { throw "Parent function ambiguous: $name" }
    $text=$definition[0].Extent.Text
    $text=$text.Replace("Join-Path (Split-Path -Parent `$PSScriptRoot) 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1'",
        "Join-Path `$script:moduleRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1'")
    $text=$text.Replace("Join-Path `$PSScriptRoot 'FileTransferOps/LocalGuiWriterRequest.ps1'",
        "Join-Path `$script:moduleRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1'")
    if($name -ceq 'Invoke-RouteAcceptanceRetainedAnalysis') {
        if($text -notmatch '\$analysisTimeoutSeconds = 1800') { throw 'Production watchdog changed' }
        $text=$text.Replace("`$startInfo.FileName = 'powershell'","`$startInfo.FileName = (Get-Process -Id `$PID).Path")
        $text=$text.Replace('$startInfo.UseShellExecute = $false',"`$startInfo.UseShellExecute = `$false`n`$startInfo.CreateNoWindow = `$true")
        $needle='$private:localAdopted = $private:localAdoption.Take($private:localTerminalReceipt)'
        if($text.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) { throw 'Original adoption seam changed' }
        $text=$text.Replace($needle,$needle+"`n`$script:adoptionAttempted=`$true; `$script:omissionReason=`$private:localAdoption.OmissionReason")
    }
    Invoke-Expression $text
}
$script:moduleRoot=$RepoRoot; $script:RouteAcceptanceExecutionMode='live'; $RouteMode='live-regular-activation-cycle'
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID=$RunId; $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID=$ScenarioId
$root=Join-Path $Directory ('integration-'+$PSVersionTable.PSVersion.Major)
$output=Join-Path $root 'valid'; [void][IO.Directory]::CreateDirectory($output)
$private:relayed=$null
$code=Invoke-RouteAcceptanceChildScriptNoThrow -ScriptPath (Join-Path $PSScriptRoot 'RetainedAnalysisIntegrationRelayWorker.ps1') `
    -Arguments @('-RepoRoot',$RepoRoot,'-Directory',$root,'-GeneratedSource',(Join-Path $Directory 'nlink.log'),'-RunId',$RunId,'-ScenarioId',$ScenarioId) `
    -LocalWriterRelay ([ref]$private:relayed) -LocalWriterArtifactDir $output
if($code -ne 0 -or $null -eq $private:relayed) { throw "Combined actual relay missing: $code" }
$ids=[IO.File]::ReadAllText((Join-Path $output 'integration-relay.json')) | ConvertFrom-Json
if($private:relayed.WrapperProcessId -ne $ids.WrapperPid -or $private:relayed.WriterContext.GuiProcessId -ne $ids.GuiPid -or $ids.WrapperPid -eq $ids.GuiPid) {
    throw 'Combined relay lost original process identity'
}
$script:atomic=(Get-Command Write-FileTransferEvidenceAtomicJson).ScriptBlock
$script:hash=(Get-Command Get-FileTransferEvidenceSha256).ScriptBlock
$script:lines=(Get-Command Get-FileTransferEvidenceLineCount).ScriptBlock
$script:receiptWrites=0; $script:sourceHashes=0; $script:sourceCounts=0; $script:adoptionAttempted=$false; $script:omissionReason=''
$script:sourcePath=Join-Path $output 'filetransfer-retained-log-slice.log'
function Write-FileTransferEvidenceAtomicJson {
    param([string]$Path,$Value)
    if($Path.EndsWith('filetransfer-retained-analysis-attempt-receipt.json')) { $script:receiptWrites++ }
    & $script:atomic -Path $Path -Value $Value
}
function Get-FileTransferEvidenceSha256 {
    param([string]$Path,$ReadLease=$null)
    if($Path -ceq $script:sourcePath) { $script:sourceHashes++ }
    & $script:hash -Path $Path -ReadLease $ReadLease
}
function Get-FileTransferEvidenceLineCount {
    param([string]$Path)
    if($Path -ceq $script:sourcePath) { $script:sourceCounts++ }
    & $script:lines -Path $Path
}
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Parent must not evaluate authority' }
$publication=$null; $failure=''; $originalLocation=Get-Location
try {
    Set-Location -LiteralPath $RepoRoot
    try { Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $output -LiveRouteProofMode RegularActivationCycle `
        -CaptureLocalSourceOwnership -LocalWriterContext $private:relayed.WriterContext -LocalPublication ([ref]$publication) }
    catch { $failure=$_.Exception.Message }
} finally { Set-Location -LiteralPath $originalLocation.Path }
if($null -ne $publication -or -not $script:adoptionAttempted) { throw 'No-failure source did not reach real adoption/omission' }
if($failure) { throw "Original report completion changed: $failure" }
$receipt=[IO.File]::ReadAllText((Join-Path $output 'filetransfer-retained-analysis-attempt-receipt.json')) | ConvertFrom-Json
if($receipt.status -cne 'completed' -or $receipt.owner -cne 'RouteAcceptance' -or $receipt.exitCode -ne 0 -or
    $receipt.acceptanceRunId -cne $RunId -or $receipt.scenarioRunId -cne $ScenarioId) { throw 'Real terminal receipt changed' }
$report=[IO.File]::ReadAllText((Join-Path $output 'filetransfer-operator-verdict.txt'))
if($report -notmatch '(?m)^first_proof_chain_failure_class=\(none\)\r?$') { throw 'Normal fixture unexpectedly selected a failure witness' }
if($report -notmatch '(?m)^verdict=INCONCLUSIVE\r?$' -or $report -notmatch '(?m)^gate_status=fail\r?$') {
    throw 'Completed report was promoted into route acceptance'
}
$stages=[IO.File]::ReadAllText((Join-Path $output 'filetransfer-retained-analysis-stage-diagnostics.json')) | ConvertFrom-Json
if($stages.synthetic_event_count -ne 0 -or $stages.compact_mode -ne 1) { throw 'Primary input was augmented or changed mode' }
if($script:receiptWrites -ne 1 -or $script:sourceHashes -ne 2 -or $script:sourceCounts -ne 2) { throw 'Original receipt/source traversal counts changed' }
$result=[ordered]@{RelayAccepted=$true;RealReports=$true;AdoptionAttempted=$true;Adopted=$false;Status=$receipt.status
    FirstFailure='(none)';PrimaryGate='INCONCLUSIVE';ReceiptWrites=1;SourceHashes=2;SourceCounts=2;ProductionWatchdogSeconds=1800
    SyntheticEvents=0;LiveRun=$false;WholeWrapperMain=$false}
[IO.File]::WriteAllText((Join-Path $output 'integration-result.json'),($result | ConvertTo-Json))
'retained-analysis-integration='+($result | ConvertTo-Json -Compress)
