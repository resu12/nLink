# Normal loader only. Keep the original scenario and launch code; replace UI
# boundaries in the shared headless fixture and add assertions at its seams.
$roleCase=$env:NLINK_TEST_CANONICAL_ROLES_CASE
if($null -ne [NLink.Tools.LocalGuiWriterRequest].GetMethod('StartAttemptRoleAppForTest') -or
    $null -ne $owner.GetType().GetMethod('TryCreateAttemptRoleRequestForTest')) {throw 'Test-only role implementation loaded'}
Invoke-Expression (Get-AttemptFunction $guiAst 'Open-GuiSmokeCanonicalAttempt')
foreach($name in @('ConvertFrom-FileTransferSoakPayloadSize','Get-FileTransferSoakPayloadSizes','Get-FileTransferSoakDirection',
    'Get-FileTransferSoakCycleTimeoutMs','Get-FileTransferSoakProgressTimeoutMs','Get-TunaGuiPayerRole','Get-TunaGuiFaultMode','Get-TunaGuiRouteMode')) {
    Invoke-Expression (Get-AttemptFunction $guiAst $name)
}
function Invoke-FileTransferTunaHandoffFallbackCycle {
    param($PayloadSizeBytes,$Direction,$PayerMode,$FaultMode,$RouteMode,$TimeoutMs,$ProgressTimeoutMs)
    Assert-PrivateScenarioInputs
    if($PayloadSizeBytes -ne 268435456 -or $Direction -cne 'helpee-to-helper' -or $PayerMode -cne 'helpee' -or
        $FaultMode -cne 'switch-off' -or $RouteMode -cne 'live-regular-activation-cycle' -or
        $TimeoutMs -ne 100000 -or $ProgressTimeoutMs -ne 30000) {throw 'Scenario consumed another projection'}
    $script:cycleCalled=$true
}
$private:attemptTicket=$null
$private:roleAttempt=$null
if($roleCase -ceq 'unconsumed') {
    $private:projection=[string[]]@($ctx.ExePath,$env:NLINK_FILETRANSFER_SOAK_DIRECTION,$env:NLINK_TUNA_GUI_PAYER_MODE,
        $env:NLINK_TUNA_GUI_FAULT,$env:NLINK_TUNA_GUI_ROUTE_MODE,$env:NLINK_FILETRANSFER_SOAK_PAYLOAD_SIZES,
        $env:NLINK_TUNA_GUI_LIVE_MULTI_TOGGLE_SEQUENCE,[string]$TimeoutSeconds,
        $env:NLINK_FILETRANSFER_SOAK_PROGRESS_TIMEOUT_SECONDS,'RegularActivationCycle',
        (Get-AttemptPhase $env:NLINK_TUNA_GUI_ROUTE_MODE),(Get-Process -Id $PID).Path)
    $attemptTicket=$owner.TryOpenCanonicalAttemptDispatch(1,$projection)
    if($null -eq $attemptTicket -or $null -ne $attemptTicket.TryCreateRoleRequest('helpee',$owner)) {throw 'Unconsumed role admitted'}
} else {
    $attemptTicket=Open-GuiSmokeCanonicalAttempt -FrontierOwner $owner -ExePath $ctx.ExePath -TimeoutSeconds $TimeoutSeconds
    if($null -eq $attemptTicket) {throw 'Original consumed attempt missing'}
}
$roleAttempt=$attemptTicket
if($roleCase -ceq 'copied-attempt') {$roleAttempt=[object].GetMethod('MemberwiseClone',$flags).Invoke($attemptTicket,$null)}
if($roleCase -cin @('unconsumed','copied-attempt')) {$Case='cancel-before'}
elseif($roleCase.EndsWith('-report-failure')) {$Case=$roleCase}
$script:canonicalRows=@()
$roleScenario=(Get-Item Function:Run-ScenarioFileTransferTunaHandoffFallback).Definition
$roleScenario=Replace-AttemptSeam $roleScenario '[void](Connect-HelperAndHelpee -Context $Context)' @'
[void](Connect-HelperAndHelpee -Context $Context)
$script:canonicalRows=@(@('helpee',$frontierHelpeeRequest,$frontierHelpeeReports,$Context.HelpeeProc),
    @('helper',$frontierHelperRequest,$frontierHelperReports,$Context.HelperProc))
Test-CanonicalRoleRows -Attempt $CanonicalAttempt -Owner $FrontierOwner -Rows $script:canonicalRows
'@
Invoke-Expression ('function Run-ScenarioFileTransferTunaHandoffFallback {'+$roleScenario+'}')
function Test-CanonicalRoleRows {
    param($Attempt,$Owner,$Rows)
    Set-Variable -Name Attempt,Owner,Rows -Option Private
    $expected=$roleCase -cnotin @('unconsumed','copied-attempt')
    $private:clone=[object].GetMethod('MemberwiseClone',$flags)
    Set-Variable -Name row -Option Private
    foreach($row in $Rows) {
        $private:role=$row[0]; $private:request=$row[1]; $private:reports=$row[2]; $private:process=$row[3]
        if($Attempt.MatchesRoleLaunch($role,$request,$reports,$process) -ne $expected) {throw 'Role did not match original launch'}
        if($expected) {
            $private:other=if($role -ceq 'helpee') {'helper'} else {'helpee'}
            if($Attempt.MatchesRoleLaunch($other,$request,$reports,$process) -or
                $Attempt.MatchesRoleLaunch($role,$clone.Invoke($request,$null),$reports,$process) -or
                $Attempt.MatchesRoleLaunch($role,$request,$clone.Invoke($reports,$null),$process) -or
                $Attempt.MatchesRoleLaunch($role,$request,$reports,$null) -or
                $null -ne $Attempt.TryCreateRoleRequest($role,$Owner)) {throw 'Mixed/copied/repeated role admitted'}
        }
    }
    if($expected -and ($Owner.LiveProofEligible -or $Owner.NativeWriterCustodyEstablished -or $Owner.PublicationCompletionIssued)) {throw 'Role launch granted authority'}
    if($roleCase -ceq 'parent-cancelled') {
        [void]$Attempt.GetType().GetField('originClosed',$flags).GetValue($Attempt).Set()
        foreach($row in $Rows) {
            if($Attempt.MatchesRoleLaunch($row[0],$row[1],$row[2],$row[3])) {throw 'Parent cancellation retained role'}
        }
    }
}
$roleStart=(Get-Item Function:Start-AppInstance).Definition
$roleStart=Replace-AttemptSeam $roleStart '$FrontierReports.Value = $CanonicalAttempt.StartRoleApp($RoleName, $FrontierRequest, $process, $createReports)' @'
$private:clone=[object].GetMethod('MemberwiseClone',$flags)
$private:copiedRequest=$clone.Invoke($FrontierRequest,$null)
foreach($wrongRole in @('unknown',$(if($RoleName -ceq 'helpee') {'helper'} else {'helpee'}))) {
    $refused=$false
    try {$null=$CanonicalAttempt.StartRoleApp($wrongRole,$FrontierRequest,$process,$createReports)}
    catch {if(-not $_.Exception.Message.Contains('attempt_role_launch_rejected')) {throw}; $refused=$true}
    if(-not $refused) {throw 'Wrong role started an app'}
}
$refused=$false
try {$null=$CanonicalAttempt.StartRoleApp($RoleName,$copiedRequest,$process,$createReports)}
catch {if(-not $_.Exception.Message.Contains('attempt_role_launch_rejected')) {throw}; $refused=$true}
if(-not $refused) {throw 'Copied request started an app'}
$private:copy=$clone.Invoke($CanonicalAttempt,$null)
$refused=$false
try {$null=$copy.StartRoleApp($RoleName,$FrontierRequest,$process,$createReports)}
catch {if(-not $_.Exception.Message.Contains('attempt_role_launch_rejected')) {throw}; $refused=$true}
if(-not $refused) {throw 'Copied attempt started an app'}
$copy.Dispose()
$FrontierReports.Value = $CanonicalAttempt.StartRoleApp($RoleName, $FrontierRequest, $process, $createReports)
$refused=$false
try {$null=$CanonicalAttempt.StartRoleApp($RoleName,$FrontierRequest,$process,$createReports)}
catch {if(-not $_.Exception.Message.Contains('attempt_role_launch_rejected')) {throw}; $refused=$true}
if(-not $refused) {throw 'Repeated role started an app'}
'@
Invoke-Expression ('function Start-AppInstance {'+$roleStart+'}')
if($null -ne $attemptTicket.TryCreateRoleRequest('unknown',$owner) -or
    $null -ne $attemptTicket.TryCreateRoleRequest('helpee',[object].GetMethod('MemberwiseClone',$flags).Invoke($owner,$null))) {throw 'Unknown/foreign role request admitted'}
if($env:NLINK_TEST_CANONICAL_SOURCE_CASE) {
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalSourceGui.ps1')
}
if($env:NLINK_TEST_GUI_DESTINATION_CASE) {
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiDestinationLifetime.ps1')
}
try {
    Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx -FrontierOwner $owner -CanonicalAttempt $roleAttempt
} catch {
    [Console]::Error.WriteLine('canonical-role-scenario: '+$_.Exception.Message)
    throw
} finally {
    if($env:NLINK_TEST_CANONICAL_SOURCE_CASE) {Complete-CanonicalSourceChecks -Attempt $attemptTicket}
    foreach($row in $script:canonicalRows) {
        if($attemptTicket.MatchesRoleLaunch($row[0],$row[1],$row[2],$row[3])) {throw 'Cleanup retained role binding'}
    }
    if($null -ne $attemptTicket.TryCreateRoleRequest('helpee',$owner)) {throw 'Cleanup created a new role'}
    [ordered]@{Pid=$PID;Consumed=($roleCase -cne 'unconsumed');Retired=(-not $attemptTicket.IsCurrent)} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'canonical-gui-consumer.json') -Encoding UTF8
    [ordered]@{Checked=$true;Case=$roleCase;AppPids=@($ctx.Processes | ForEach-Object {$_.Id})} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $Directory 'canonical-roles.json') -Encoding UTF8
}
