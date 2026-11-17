param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$relay=[NLink.Tools.LocalGuiWriterRequest]::TryAttachRelayReady($env:NLINK_GUI_WRITER_RELAY,[guid]$env:NLINK_GUI_WRITER_RELAY_ID,
    $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,$Directory)
if($null -eq $relay) {throw 'Actual Wrapper relay unavailable'}
$handoff=$relay.TryAttachFrontier(); if($null -eq $handoff) {throw 'Actual Wrapper native registration unavailable'}
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferTunaGuiSmoke.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) {throw 'Actual Wrapper parse failed'}
foreach($name in @('ConvertTo-TunaGuiProcessArgument','Write-TunaGuiAtomicJson','Get-TunaGuiFinalizationGraceSeconds',
    'Complete-TunaGuiChildOutputCapture','Get-TunaGuiTerminalPhaseDeadlineRequest','Invoke-TunaGuiSmokeWithHardDeadline')) {
    $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
    if($definition.Count -ne 1) {throw ('Actual Wrapper function ambiguous: '+$name)}
    $text=$definition[0].Extent.Text
    if($name -ceq 'Invoke-TunaGuiSmokeWithHardDeadline') {
        $text=$text.Replace("(Join-Path `$RepoRoot 'tools\GuiSmoke-Windows.ps1')","(Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierWrapperHandoffGui.ps1')")
        $text=$text.Replace("`$startInfo.FileName = 'powershell'","`$startInfo.FileName = (Get-Process -Id `$PID).Path")
        $text=$text.Replace('$stderrTask = $private:frontierGuiReports.Stderr',"`$stderrTask = `$private:frontierGuiReports.Stderr`n`$script:guiReports = `$private:frontierGuiReports; `$script:guiLaunches++")
    }
    Invoke-Expression $text
}
function Stop-TunaGuiSmokeProcessTree {param($Process) $Process.Kill(); [void]$Process.WaitForExit(5000)}
function Stop-TunaGuiSmokeApplicationDescendants {throw 'Unexpected headless GUI deadline'}
function Request-TunaGuiScenarioDeadline {throw 'Unexpected headless GUI deadline'}
$script:guiReports=$null; $script:guiLaunches=0
$settings=Join-Path $Directory 'settings.json'
[IO.File]::WriteAllText($settings,(@{RepoRoot=$RepoRoot;Directory=$Directory;Case='positive';FinalizerCase='persistence-handoff-positive'} | ConvertTo-Json))
try {
    $result=Invoke-TunaGuiSmokeWithHardDeadline -RepoRoot $RepoRoot -ArtifactDir $Directory -ExePath $settings -TimeoutSeconds 100 -LocalWriterRelay $relay -FrontierRelayHandoff $handoff
    if($script:guiLaunches -ne 1 -or -not $script:guiReports.Stdout.IsCompleted -or -not $script:guiReports.Stderr.IsCompleted -or
        $result.TimedOut -or -not $result.ExitCodeAvailable -or $result.ExitCode -ne 0) {throw 'Original GUI outcome/readers changed'}
    if(Test-Path -LiteralPath (Join-Path $Directory 'primary-pid.txt')) {throw 'Primary started inside Wrapper'}
    'frontier-relay-wrapper-ok'
} finally {$relay.Dispose()}
if($Case -ceq 'wrapper-nonzero') {exit 7}
