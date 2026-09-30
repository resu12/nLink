# Same original attempt/scenario dispatch, with real Core writer-only children.
# The app executable is pwsh on both GUI hosts because Core targets .NET 8.
foreach($role in @('helpee','helper')) {
    $script:signals[$role+'.writer-ready']=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,
        $env:NLINK_GUI_WRITER_REQUEST+'.'+$role+'.writer-ready')
}
if($attemptCase -ceq 'writer-helper-first') {
    $script:signals['helper.writer-completed']=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,
        $env:NLINK_GUI_WRITER_REQUEST+'.helper.writer-completed')
}
function Get-AppLaunchEnvironmentOverrides {
    param($RoleName)
    Assert-PrivateScenarioInputs
    $arguments=@('-NoProfile','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptWritersApp.ps1'),
        '-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Role',$RoleName,'-Signal',$env:NLINK_GUI_WRITER_REQUEST,'-Case',$attemptCase)
    $script:appArguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
    return @{'NLINK_FILETRANSFER_ACCEPTANCE_LOG_PATH'=(Join-Path $Directory 'live-operational-log/nlink.log')}
}
function Connect-HelperAndHelpee {
    param($Context)
    Assert-PrivateScenarioInputs
    foreach($role in @('helpee','helper')) {
        if(-not $script:signals[$role+'.writer-ready'].WaitOne(20000)) {throw 'Core writer setup missing'}
    }
    if(-not $script:testOwner.IsCurrent -or $script:testOwner.LiveProofEligible -or
        $script:testOwner.NativeWriterCustodyEstablished -or $script:testOwner.PublicationCompletionIssued) {throw 'Core setup changed ownership or authority'}
}
$cleanup=Get-AttemptFunction $guiAst 'Cleanup-ProcessesWithOperationalLogDrain'
$cleanup=Replace-AttemptSeam $cleanup 'function Cleanup-ProcessesWithOperationalLogDrain' 'function Invoke-WriterFixtureCleanup'
Invoke-Expression $cleanup
function Stop-BridgeNodeProcessesForExePath {param($ExePath)}
function Stop-Process {
    param([int]$Id,[switch]$Force,[string]$ErrorAction)
    if($Id -cnotin @($ctx.Processes | ForEach-Object {$_.Id})) {throw 'Writer cleanup escaped fixture'}
    Microsoft.PowerShell.Management\Stop-Process -Id $Id -Force -ErrorAction SilentlyContinue
}
function Cleanup-ProcessesWithOperationalLogDrain {
    param($Processes,$ExePath,$CleanupDiagnostic,$FrontierOwner)
    Set-Variable -Name FrontierOwner -Option Private
    Assert-PrivateScenarioInputs
    $targets=@(foreach($process in $Processes) {
        $role=if($process.Id -eq $ctx.HelpeeProc.Id) {'helpee'} else {'helper'}
        $target=[pscustomobject]@{Process=$process;Id=$process.Id;Finish=$script:signals[$role+'.exit']}
        $target | Add-Member ScriptProperty HasExited {return $this.Process.HasExited}
        $target | Add-Member ScriptMethod CloseMainWindow {[void]$this.Finish.Set(); return $true}
        $target | Add-Member ScriptMethod WaitForExit {param([int]$Milliseconds) return $this.Process.WaitForExit($Milliseconds)}
        $target
    })
    Invoke-WriterFixtureCleanup -Processes $targets -ExePath fixture-only -CleanupDiagnostic $CleanupDiagnostic -FrontierOwner $FrontierOwner
}
$writerScenario=Get-AttemptFunction $guiAst 'Run-ScenarioFileTransferTunaHandoffFallback'
$writerScenario=Replace-AttemptSeam $writerScenario 'param([Parameter(Mandatory = $true)]$Context, [AllowNull()]$FrontierOwner = $null, [AllowNull()]$CanonicalAttempt = $null)' `
    'param([Parameter(Mandatory = $true)]$Context, [AllowNull()]$FrontierOwner = $null, [AllowNull()]$CanonicalAttempt = $null, [AllowNull()]$attemptTicket = $null)'
$writerScenario=Replace-AttemptSeam $writerScenario '$private:frontierHelpeeRequest = $null' ('Set-Variable -Name attemptTicket -Option Private'+"`n"+'$private:frontierHelpeeRequest = $null')
$writerScenario=Replace-AttemptSeam $writerScenario '[void](Connect-HelperAndHelpee -Context $Context)' @'
[void](Connect-HelperAndHelpee -Context $Context)
if(-not $attemptTicket.MatchesRoleLaunchForTest('helpee',$frontierHelpeeRequest,$frontierHelpeeReports,$Context.HelpeeProc) -or
    -not $attemptTicket.MatchesRoleLaunchForTest('helper',$frontierHelperRequest,$frontierHelperReports,$Context.HelperProc)) {throw 'Core role launch not bound'}
if($null -ne $attemptTicket.TryBindRoleWriters($null,$null)) {throw 'Early role writer join admitted'}
if($attemptCase.StartsWith('writer-destination-gui-')) {
    $private:attemptDestination=$null
    $private:fault=$attemptCase.Substring('writer-destination-gui-'.Length)
    $private:clone=[object].GetMethod('MemberwiseClone',$flags)
    if($null -ne $attemptTicket.TryAdoptDestinationForTest($frontierHelpeeRequest,2097175,'','valid') -or
        $null -ne $attemptTicket.TryAdoptDestinationForTest($clone.Invoke($frontierHelperRequest,$null),2097175,'','valid') -or
        $null -ne $clone.Invoke($attemptTicket,$null).TryAdoptDestinationForTest($frontierHelperRequest,2097175,'','valid')) {throw 'Foreign destination request admitted'}
    $attemptDestination=$attemptTicket.TryAdoptDestinationForTest($frontierHelperRequest,2097175,
        [NLink.Tools.LocalGuiWriterRequest]::SmallDestinationHashForTest(),$fault)
    if(($null -ne $attemptDestination) -ne ($fault -ceq 'valid')) {throw 'Destination GUI adoption outcome changed'}
    if($null -ne $attemptTicket.TryAdoptDestinationForTest($frontierHelperRequest,2097175,'','valid')) {throw 'Repeated destination adoption admitted'}
    if($null -ne $attemptDestination) {
        $private:copy=$clone.Invoke($attemptDestination,$null)
        if($copy.IsCurrent -or $copy.TryBindClosedWriters($null)) {throw 'Copied destination current'}
        $copy.Dispose()
        if(-not $attemptDestination.IsCurrent -or $attemptDestination.ByteCount -ne 2097175 -or
            $attemptDestination.LiveProofEligible -or $attemptDestination.TransferIntegrityEstablished -or
            $attemptDestination.PublicationCompletionIssued) {throw 'Destination custody/authority changed'}
    }
    'attempt-destination-gui-adoption-ok'
}
'@
$writerScenario=Replace-AttemptSeam $writerScenario 'finally { if ($null -ne $FrontierOwner) { $FrontierOwner.Dispose() } }' @'
finally {
    try {
        if($script:drained) {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptWritersJoin.ps1')}
    } finally {if($null -ne $FrontierOwner) {$FrontierOwner.Dispose()}}
}
'@
if($attemptCase.StartsWith('writer-source-')) {
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptSourceGui.ps1')
}
Invoke-Expression $writerScenario
$writerStart=(Get-Item Function:Start-AppInstance).Definition
if($attemptCase.StartsWith('writer-source-read-')) {
    $script:signals['sender-read-done']=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,
        $env:NLINK_GUI_WRITER_REQUEST+'.sender-read-done')
    if($attemptCase.StartsWith('writer-source-read-native-')) {
        $script:signals['sender-source-packet-ready']=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,
            $env:NLINK_GUI_WRITER_REQUEST+'.sender-source-packet-ready')
    }
    $writerStart=Replace-AttemptSeam $writerStart '$startInfo.UseShellExecute = $false' `
        ('$startInfo.UseShellExecute = $false'+"`n"+'if($RoleName -ceq ''helper'' -and $attemptCase.StartsWith(''writer-source-read-cross-process-'')) {$startInfo.RedirectStandardInput=$true}')
    if($attemptCase.StartsWith('writer-source-read-cross-process-')) {
        foreach($name in @('receiver-read-done','cross-host-ready','destination-verified','destination-handoff-ready')) {
            $script:signals[$name]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,
                $env:NLINK_GUI_WRITER_REQUEST+'.'+$name)
        }
    }
}
Invoke-Expression ('function Start-AppInstance {'+$writerStart+'}')
