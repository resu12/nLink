param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
class HandoffReferenceConversion { HandoffReferenceConversion([object]$value) {} }
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferTunaGuiSmoke.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) {throw 'Actual wrapper parse failed'}
function Definition([string]$Name) {
    $matches=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $Name},$true))
    if($matches.Count -ne 1) {throw ('Actual wrapper function ambiguous: '+$Name)}
    return $matches[0].Extent.Text
}
foreach($name in @('ConvertTo-TunaGuiProcessArgument','Write-TunaGuiAtomicJson','Get-TunaGuiFinalizationGraceSeconds',
    'Complete-TunaGuiChildOutputCapture','Get-TunaGuiTerminalPhaseDeadlineRequest')) {Invoke-Expression (Definition $name)}
$originalAtomic=${function:Write-TunaGuiAtomicJson}; $originalOutput=${function:Complete-TunaGuiChildOutputCapture}
function Write-TunaGuiAtomicJson {
    param($Path,$Value)
    foreach($name in @('PreparedFrontierSource','frontierGuiReports','frontierParentCustody','pendingFrontierSource')) {
        if($null -ne (Get-Variable $name -ErrorAction SilentlyContinue)) {throw ('Private wrapper ownership leaked: '+$name)}
    }
    if($Case -ceq 'state-failure' -and $Value.lastCompletedStage -ceq 'gui_child_exited') {throw 'injected parent persistence failure'}
    & $originalAtomic @PSBoundParameters
}
function Complete-TunaGuiChildOutputCapture {
    param($StdoutTask,$StderrTask,$StdoutPath,$StderrPath)
    $output=& $originalOutput @PSBoundParameters
    if($Case -ceq 'output-failure') {$output.Completed=$false}
    return $output
}
# Never inspect/stop unrelated GUI, bridge, or native network processes.
function Stop-TunaGuiSmokeProcessTree {param($Process) $Process.Kill(); [void]$Process.WaitForExit(5000)}
function Stop-TunaGuiSmokeApplicationDescendants {throw 'Unexpected scenario deadline in headless fixture'}
function Request-TunaGuiScenarioDeadline {throw 'Unexpected scenario deadline in headless fixture'}
$parent=Definition 'Invoke-TunaGuiSmokeWithHardDeadline'
$needle="(Join-Path `$RepoRoot 'tools\GuiSmoke-Windows.ps1')"
if($parent.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) {throw 'Original GUI launch seam changed'}
$parent=$parent.Replace($needle,"(Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierWrapperHandoffGui.ps1')")
$parent=$parent.Replace("`$startInfo.FileName = 'powershell'","`$startInfo.FileName = (Get-Process -Id `$PID).Path")
$seam='$stderrTask = $private:frontierGuiReports.Stderr'
if($parent.Split([string[]]@($seam),[StringSplitOptions]::None).Length -ne 2) {throw 'Original GUI readers changed'}
$parent=$parent.Replace($seam,$seam+"`n`$script:guiRequest = `$localWriterRequest; `$script:guiReports = `$private:frontierGuiReports; `$script:guiLaunches++")
Invoke-Expression $parent
$script:guiRequest=$null; $script:guiReports=$null; $script:guiLaunches=0
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID='cleanup-adoption-run'
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='cleanup-adoption-scope'
$settings=Join-Path $Directory 'settings.json'
$fixtureCase=if($Case -ceq 'positive' -or $Case.StartsWith('merge-')) {'positive'} else {'writer-only'}
$finalizerCase=if($Case -cin @('gui-failure','stage-failure')) {'persistence-handoff-'+$Case} else {'persistence-handoff-valid'}
[IO.File]::WriteAllText($settings,(@{RepoRoot=$RepoRoot;Directory=$Directory;Case=$fixtureCase;FinalizerCase=$finalizerCase} | ConvertTo-Json))
$completion=$null; $prepared=$null; [uri]$incompatible=$null; [HandoffReferenceConversion]$converted=$null; $failure=''; $result=$null
if($Case -ceq 'assigned-output') {$prepared='preserve-existing'}
if($Case -ceq 'assigned-completion') {$completion='preserve-existing'}
$arguments=@{RepoRoot=$RepoRoot;ArtifactDir=$Directory;ExePath=$settings;TimeoutSeconds=100;
    CaptureFrontierLaunch=$true;LocalWriterCompletion=[ref]$completion;PreparedFrontierSource=[ref]$prepared}
if($Case -ceq 'incompatible-output') {$arguments.PreparedFrontierSource=[ref]$incompatible}
if($Case -ceq 'coerced-output') {$arguments.PreparedFrontierSource=[ref]$converted}
if($Case -ceq 'incompatible-completion') {$arguments.LocalWriterCompletion=[ref]$incompatible}
try {
    try {$result=Invoke-TunaGuiSmokeWithHardDeadline @arguments} catch {$failure=$_.Exception.Message}
    if(($Case -ceq 'state-failure' -and $failure -cne 'injected parent persistence failure') -or
        ($Case -cne 'state-failure' -and $failure)) {throw ('Original wrapper failure changed: '+$failure)}
    if($script:guiLaunches -ne 1 -or -not $script:guiReports.Stdout.IsCompleted -or -not $script:guiReports.Stderr.IsCompleted) {throw 'Original GUI launch/readers changed'}
    $guiStdout=[IO.File]::ReadAllText((Join-Path $Directory 'gui-smoke-stdout.log'))
    $guiStderr=[IO.File]::ReadAllText((Join-Path $Directory 'gui-smoke-stderr.log'))
    if($guiStdout -cnotmatch 'cleanup-adoption-gui-ok') {throw ('Actual finalizer fixture failed: '+$guiStdout+' / '+$guiStderr)}
    if($null -ne $result -and ($result.TimedOut -or -not $result.ExitCodeAvailable -or $result.ExitCode -ne $(if($Case -ceq 'gui-failure') {7} else {0}))) {throw 'Original GUI exit gates changed'}
    $expected=$Case -cin @('valid','positive') -or $Case.StartsWith('merge-')
    if(($null -ne $prepared -and $prepared -isnot [string]) -ne $expected -or $null -ne $incompatible -or $null -ne $converted -or
        ($Case -ceq 'assigned-output' -and $prepared -cne 'preserve-existing') -or
        ($Case -ceq 'assigned-completion' -and $completion -cne 'preserve-existing')) {throw 'Wrapper export admission/ownership changed'}
    $parentFlags=[Reflection.BindingFlags]'Instance,NonPublic'
    $parentClone=[object].GetMethod('MemberwiseClone',$parentFlags)
    $registration=$script:guiReports.ParentRegistration
    $pending=$registration.GetType().GetField('PendingFiles',$parentFlags).GetValue($registration)
    if($null -ne $pending) {
        $pins=$pending.GetType().GetField('handles',$parentFlags).GetValue($pending)
        if(@($pins | Where-Object {-not $_.IsClosed}).Count) {throw 'GUI watchdog retained raw parent pins'}
    }
    if($registration.IsCurrent -or $null -ne $script:guiRequest.TakeAfterExit()) {throw 'GUI watchdog failed to retire its original request'}
    if($expected) {
        if(-not $prepared.IsCurrent -or $prepared.LiveProofEligible -or $prepared.NativeWriterCustodyEstablished -or $prepared.PublicationCompletionIssued) {throw 'Prepared source lost lifetime or gained authority'}
        $expectedAttempt=$prepared.Request.RequestId.ToString('N')
        if($Case.StartsWith('merge-')) {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierMilestoneCompatibility.ps1')
        } else {
        # Exercise an intervening ordinary legacy update before the unchanged
        # analysis call. It cannot replace/reopen the pinned native source.
        [IO.File]::AppendAllText($completion.Outputs[3].Path,"`nintervening-wrapper-milestone`n")
        if([IO.File]::Exists((Join-Path $Directory 'primary-pid.txt'))) {throw 'Analysis launched inside the GUI watchdog'}
        $primaryCase='parent-Wrapper-prepared'
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRetainedParent.ps1') -PreparedSource $prepared
        $receipt=Get-Content -LiteralPath (Join-Path $Directory 'filetransfer-retained-analysis-attempt-receipt.json') -Raw | ConvertFrom-Json
        if($receipt.attemptId -cne $expectedAttempt) {throw 'Later analysis replaced the prepared request attempt'}
        }
    } elseif([IO.File]::Exists((Join-Path $Directory 'primary-pid.txt'))) {throw 'Rejected export launched a primary'}
} finally {if($null -ne $prepared -and $prepared -is [IDisposable]) {$prepared.Dispose()}}
foreach($file in @('filetransfer-frontier-retained-log-slice.log','filetransfer-frontier-finalizer-evidence.json')) {
    $path=Join-Path $Directory $file
    if([IO.File]::Exists($path)) {$probe=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $probe.Dispose()}
}
if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) {throw 'Wrapper handoff retained payload resources'}
'frontier-wrapper-handoff='+$Case
