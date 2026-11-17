# Applied only to test copies, after the original attempt was consumed. Roles
# come from the original named scenario statements, not log order or app argv.
$roleScenario=Get-AttemptFunction $guiAst 'Run-ScenarioFileTransferTunaHandoffFallback'
$roleScenario=Replace-AttemptSeam $roleScenario 'param([Parameter(Mandatory = $true)]$Context, [AllowNull()]$FrontierOwner = $null, [AllowNull()]$CanonicalAttempt = $null)' `
    'param([Parameter(Mandatory = $true)]$Context, [AllowNull()]$FrontierOwner = $null, [AllowNull()]$CanonicalAttempt = $null, [AllowNull()]$attemptTicket = $null)'
$roleScenario=Replace-AttemptSeam $roleScenario '$private:frontierHelpeeRequest = $null' ('Set-Variable -Name attemptTicket -Option Private'+"`n"+'$private:frontierHelpeeRequest = $null')
$roleScenario=Replace-AttemptSeam $roleScenario '[void](Connect-HelperAndHelpee -Context $Context)' @'
[void](Connect-HelperAndHelpee -Context $Context)
$private:clone=[object].GetMethod('MemberwiseClone',$flags)
if(-not $attemptTicket.MatchesRoleLaunchForTest('helpee',$frontierHelpeeRequest,$frontierHelpeeReports,$Context.HelpeeProc) -or
    -not $attemptTicket.MatchesRoleLaunchForTest('helper',$frontierHelperRequest,$frontierHelperReports,$Context.HelperProc)) {throw 'Original role launch binding missing'}
Set-Variable -Name row -Option Private
foreach($row in @(@('helpee',$frontierHelpeeRequest,$frontierHelpeeReports,$Context.HelpeeProc),
    @('helper',$frontierHelperRequest,$frontierHelperReports,$Context.HelperProc))) {
    $private:role=[string]$row[0]; $private:request=$row[1]; $private:reports=$row[2]; $private:process=$row[3]
    $private:other=if($role -ceq 'helpee') {'helper'} else {'helpee'}
    if($attemptTicket.MatchesRoleLaunchForTest($other,$request,$reports,$process) -or
        $attemptTicket.MatchesRoleLaunchForTest('Helpee',$request,$reports,$process) -or
        $attemptTicket.MatchesRoleLaunchForTest($role,$clone.Invoke($request,$null),$reports,$process) -or
        $attemptTicket.MatchesRoleLaunchForTest($role,$request,$clone.Invoke($reports,$null),$process) -or
        $attemptTicket.MatchesRoleLaunchForTest($role,$request,$reports,$null) -or
        $null -ne $FrontierOwner.TryCreateAttemptRoleRequestForTest($role)) {throw 'Swapped/copied/repeated role admitted'}
    $private:ticketCopy=$clone.Invoke($attemptTicket,$null)
    if($ticketCopy.MatchesRoleLaunchForTest($role,$request,$reports,$process)) {throw 'Copied ticket matched role'}
    $ticketCopy.Dispose()
    if(-not $attemptTicket.MatchesRoleLaunchForTest($role,$request,$reports,$process)) {throw 'Refusal retired original role'}
}
if($attemptTicket.MatchesRoleLaunchForTest('helpee',$frontierHelperRequest,$frontierHelpeeReports,$Context.HelpeeProc) -or
    $attemptTicket.MatchesRoleLaunchForTest('helpee',$frontierHelpeeRequest,$frontierHelperReports,$Context.HelpeeProc) -or
    $attemptTicket.MatchesRoleLaunchForTest('helpee',$frontierHelpeeRequest,$frontierHelpeeReports,$Context.HelperProc)) {throw 'Mixed app role admitted'}
if($attemptCase -ceq 'role-parent-cancelled') {
    [void]$attemptTicket.GetType().GetField('originClosed',$flags).GetValue($attemptTicket).Set()
} elseif($attemptCase -ceq 'role-scope-changed') {
    $private:view=$FrontierOwner.GetType().GetField('view',$flags).GetValue($FrontierOwner)
    $view.Write(64+8192+8,[byte]($view.ReadByte(64+8192+8) -bxor 1))
} elseif($attemptCase -cin @('role-wrong-pid','role-wrong-creation')) {
    $private:native=$frontierHelpeeRequest.GetType().GetField('native',$flags).GetValue($frontierHelpeeRequest)
    $private:view=$native.GetType().GetField('view',$flags).GetValue($native)
    if($attemptCase -ceq 'role-wrong-pid') {$view.Write(24,[int]$PID)} else {$view.Write(32,[long]1)}
} elseif($attemptCase -ceq 'role-exited') {
    [void]$script:signals['helpee.exit'].Set()
    if(-not $Context.HelpeeProc.WaitForExit(5000)) {throw 'Role app did not exit'}
}
$roleExpected=$attemptCase -ceq 'role-valid'
if($attemptTicket.MatchesRoleLaunchForTest('helpee',$frontierHelpeeRequest,$frontierHelpeeReports,$Context.HelpeeProc) -ne $roleExpected -or
    $attemptTicket.MatchesRoleLaunchForTest('helper',$frontierHelperRequest,$frontierHelperReports,$Context.HelperProc) -ne $roleExpected) {throw 'Role lifetime mismatch'}
'@
$roleScenario=Replace-AttemptSeam $roleScenario 'finally { if ($null -ne $FrontierOwner) { $FrontierOwner.Dispose() } }' @'
finally {
    if ($null -ne $FrontierOwner) { $FrontierOwner.Dispose() }
    if($attemptTicket.MatchesRoleLaunchForTest('helpee',$frontierHelpeeRequest,$frontierHelpeeReports,$Context.HelpeeProc) -or
        $attemptTicket.MatchesRoleLaunchForTest('helper',$frontierHelperRequest,$frontierHelperReports,$Context.HelperProc)) {throw 'Disposed owner retained role binding'}
}
'@
Invoke-Expression $roleScenario

# Keep original process start and output readers inside the native request.
# Rejected role/request combinations must not start even a headless child.
$roleStart=(Get-Item Function:Start-AppInstance).Definition
$roleStart=Replace-AttemptSeam $roleStart '$FrontierReports.Value = $CanonicalAttempt.StartRoleApp($RoleName, $FrontierRequest, $process, $createReports)' @'
$private:copiedRequest=[object].GetMethod('MemberwiseClone',$flags).Invoke($FrontierRequest,$null)
$refused=$false
try {$null=[NLink.Tools.LocalGuiWriterRequest]::StartAttemptRoleAppForTest($RoleName,$copiedRequest,$process,$createReports)}
catch {if(-not $_.Exception.Message.Contains('attempt_role_launch_rejected')) {throw}; $refused=$true}
if(-not $refused) {throw 'Copied request started app'}
$private:foreignNative=[NLink.Tools.LocalFrontierAppRequest]::TryCreate($env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID)
if($null -eq $foreignNative) {throw 'Foreign request fixture missing'}
try {
    $private:foreignRequest=[NLink.Tools.LocalGuiWriterRequest+FrontierAppRequest].GetConstructors($flags)[0].Invoke(@($script:testOwner,$foreignNative))
    $refused=$false
    try {$null=[NLink.Tools.LocalGuiWriterRequest]::StartAttemptRoleAppForTest($RoleName,$foreignRequest,$process,$createReports)}
    catch {if(-not $_.Exception.Message.Contains('attempt_role_launch_rejected')) {throw}; $refused=$true}
    if(-not $refused) {throw 'Foreign same-label request started app'}
} finally {$foreignNative.Dispose()}
foreach($wrongRole in @('unknown',$(if($RoleName -ceq 'helpee') {'helper'} else {'helpee'}))) {
    $refused=$false
    try {$null=[NLink.Tools.LocalGuiWriterRequest]::StartAttemptRoleAppForTest($wrongRole,$FrontierRequest,$process,$createReports)}
    catch {if(-not $_.Exception.Message.Contains('attempt_role_launch_rejected')) {throw}; $refused=$true}
    if(-not $refused) {throw 'Wrong role started app'}
}
$FrontierReports.Value = [NLink.Tools.LocalGuiWriterRequest]::StartAttemptRoleAppForTest($RoleName,$FrontierRequest,$process,$createReports)
$refused=$false
try {$null=[NLink.Tools.LocalGuiWriterRequest]::StartAttemptRoleAppForTest($RoleName,$FrontierRequest,$process,$createReports)}
catch {if(-not $_.Exception.Message.Contains('attempt_role_launch_rejected')) {throw}; $refused=$true}
if(-not $refused) {throw 'Repeated role started app'}
'@
Invoke-Expression ('function Start-AppInstance {'+$roleStart+'}')
if($null -ne $owner.TryCreateAttemptRoleRequestForTest('unknown') -or
    $null -ne $ownerCopy.TryCreateAttemptRoleRequestForTest('helpee')) {throw 'Foreign/invalid role created request'}
