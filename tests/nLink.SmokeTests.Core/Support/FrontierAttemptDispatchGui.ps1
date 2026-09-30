param([ValidateSet('Entry','Dispatch')][string]$Stage)
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
$guiAst=Read-AttemptAst (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1')
if($Stage -ceq 'Entry') {
    . (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')
    foreach($name in @('Get-GuiSmokeFrontierLaunchOwner','Clear-AppLogsIfPresent')) {Invoke-Expression (Get-AttemptFunction $guiAst $name)}
    $entryTry=@($guiAst.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.TryStatementAst] -and $_.Body.Extent.Text.Contains('# GUI smoke intentionally')})
    $entry=@($entryTry[0].Body.Statements | Where-Object {$_.Extent.Text.StartsWith('$guiFrontierOwner =') -or
        $_.Extent.Text.StartsWith('Clear-AppLogsIfPresent ') -or $_.Extent.Text.StartsWith('if ($null -ne $guiFrontierOwner')})
    if($entryTry.Count -ne 1 -or $entry.Count -ne 3) {throw 'Original attempt GUI entry changed'}
    if([IO.Path]::GetFullPath($env:LOCALAPPDATA) -cne [IO.Path]::GetFullPath((Join-Path $Directory 'fixture-local-app-data'))) {throw 'Attempt log cleanup escaped fixture'}
    $script:GuiSmokeAppLogLedger=New-GuiSmokeLogLedgerState
    $private:guiFrontierOwner=$null; $private:guiFrontierOrigin=$null; $private:attemptTicket=$null
    foreach($statement in $entry) {Invoke-Expression $statement.Extent.Text}
    $private:owner=$guiFrontierOwner; $private:origin=$guiFrontierOrigin
    if($null -eq $owner -or $null -eq $origin -or -not $owner.IsCurrent -or -not $origin.IsCurrent) {throw 'Original attempt GUI entry missing'}
    return
}

# Restore the real environment consumers after the launch fixture's UI stubs.
foreach($name in @('ConvertFrom-FileTransferSoakPayloadSize','Get-FileTransferSoakPayloadSizes','Get-FileTransferSoakDirection',
    'Get-FileTransferSoakCycleTimeoutMs','Get-FileTransferSoakProgressTimeoutMs','Get-TunaGuiPayerRole','Get-TunaGuiFaultMode','Get-TunaGuiRouteMode')) {
    Invoke-Expression (Get-AttemptFunction $guiAst $name)
}
$private:projection=[string[]]@($ctx.ExePath,(Get-FileTransferSoakDirection),(Get-TunaGuiPayerRole),(Get-TunaGuiFaultMode),
    (Get-TunaGuiRouteMode),$env:NLINK_FILETRANSFER_SOAK_PAYLOAD_SIZES,$env:NLINK_TUNA_GUI_LIVE_MULTI_TOGGLE_SEQUENCE,
    [string]((Get-FileTransferSoakCycleTimeoutMs)/1000),[string]((Get-FileTransferSoakProgressTimeoutMs)/1000),
    'RegularActivationCycle',(Get-AttemptPhase (Get-TunaGuiRouteMode)),(Get-Process -Id $PID).Path)
if($ExePath -cne $ctx.ExePath -or $TimeoutSeconds -ne [int]$projection[7]) {throw 'Actual GUI argv projection changed'}
$attemptCase=$env:NLINK_TEST_ATTEMPT_CASE
if($attemptCase -ceq 'environment-changed') {$env:NLINK_FILETRANSFER_SOAK_PAYLOAD_SIZES='128MiB'; $projection[5]='128MiB'}
$private:attemptTicket=$owner.TryOpenAttemptDispatchForTest(1,$projection)
if($attemptCase -ceq 'environment-changed') {
    if($null -ne $attemptTicket) {throw 'Changed environment gained scope'}
    $Case='default'; $selected=$null
} else {
    if($null -eq $attemptTicket) {throw 'Original attempt scope missing'}
    if($attemptCase.StartsWith('role-') -and $null -ne $owner.TryCreateAttemptRoleRequestForTest('helpee')) {throw 'Unconsumed attempt created role request'}
    # Every launch/proof/phase field is compared to the original native bytes.
    for($i=0;$i -lt $projection.Length;$i++) {
        $changed=[string[]]$projection.Clone(); $changed[$i]+='-changed'
        if($i -cin @(7,8)) {$changed[$i]=[string]([int]$projection[$i]+1)}
        if($null -ne $attemptTicket.TryConsume($owner,1,$changed)) {throw ('Changed scope field admitted: '+$i)}
    }
    if($null -ne $attemptTicket.TryConsume($owner,2,$projection) -or $null -ne $attemptTicket.TryConsume($null,1,$projection)) {throw 'Foreign attempt admitted'}
    $private:ownerCopy=[object].GetMethod('MemberwiseClone',$flags).Invoke($owner,$null)
    $private:ticketCopy=[object].GetMethod('MemberwiseClone',$flags).Invoke($attemptTicket,$null)
    if($null -ne $attemptTicket.TryConsume($ownerCopy,1,$projection) -or $ticketCopy.IsCurrent -or
        $null -ne $ticketCopy.TryConsume($owner,1,$projection)) {throw 'Copied capability admitted'}
    $ticketCopy.Dispose()
    if(-not $attemptTicket.IsCurrent) {throw 'Copy disposal retired original attempt'}
    if($attemptCase -ceq 'cancel-before') {$owner.Dispose()}
    if($attemptCase -ceq 'parent-cancelled') {
        $closed=$attemptTicket.GetType().GetField('originClosed',$flags).GetValue($attemptTicket)
        [void]$closed.Set()
    }
    if($attemptCase -cin @('wrong-pid','wrong-creation')) {
        $view=$owner.GetType().GetField('view',$flags).GetValue($owner)
        if($attemptCase -ceq 'wrong-pid') {$view.Write(16,[int]($PID+1))} else {$view.Write(40,[long]1)}
    }
    if($attemptCase -ceq 'tampered-request') {
        $view=$owner.GetType().GetField('view',$flags).GetValue($owner)
        $view.Write(64+8192+8,[byte]($view.ReadByte(64+8192+8) -bxor 1))
    }
    $selected=$attemptTicket.TryConsume($owner,1,$projection)
    $rejected=$attemptCase -cin @('cancel-before','parent-cancelled','wrong-pid','wrong-creation','tampered-request')
    if(($null -eq $selected) -ne $rejected) {throw 'Attempt dispatch admission changed'}
    if($rejected) {$Case='default'}
    elseif($null -ne $attemptTicket.TryConsume($owner,1,$projection) -or $null -ne $owner.TryOpenAttemptDispatchForTest(1,$projection)) {throw 'Repeated attempt dispatch admitted'}
}
function Invoke-FileTransferTunaHandoffFallbackCycle {
    param($PayloadSizeBytes,$Direction,$PayerMode,$FaultMode,$RouteMode,$TimeoutMs,$ProgressTimeoutMs)
    Assert-PrivateScenarioInputs
    if($PayloadSizeBytes -ne $(if($attemptCase -ceq 'environment-changed'){134217728}else{268435456}) -or
        $Direction -cne 'helpee-to-helper' -or $PayerMode -cne 'helpee' -or $FaultMode -cne 'switch-off' -or
        $RouteMode -cne 'live-regular-activation-cycle' -or
        $TimeoutMs -ne $(if($attemptCase.StartsWith('writer-source-read-cross-process-carrier-full-')){210000}else{100000}) -or
        $ProgressTimeoutMs -ne 30000) {throw 'Actual scenario consumed another projection'}
    $script:cycleCalled=$true
}
# Component fixture: call the original scenario with this fixture's selected
# owner. The normal-loader chain separately executes the guarded top-level call.
$dispatch=Replace-AttemptSeam $liveCalls[0].Extent.Text ' -FrontierOwner $private:scenarioFrontierOwner -CanonicalAttempt $CanonicalAttempt' ''
if($null -ne $selected) {$dispatch += ' -FrontierOwner $private:selected'}
if($attemptCase.StartsWith('role-')) {
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptRolesGui.ps1')
    $dispatch += ' -CanonicalAttempt $private:attemptTicket -attemptTicket $private:attemptTicket'
}
if($attemptCase.StartsWith('writer-')) {
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptWritersGui.ps1')
    $dispatch += ' -CanonicalAttempt $private:attemptTicket -attemptTicket $private:attemptTicket'
}
$ownerWasCurrent=$owner.IsCurrent
Invoke-Expression $dispatch
if($null -eq $selected -and $owner.IsCurrent -ne $ownerWasCurrent) {throw 'Rejected scope changed original owner lifetime'}
if($null -ne $attemptTicket -and $Case -ceq 'valid' -and $attemptTicket.IsCurrent) {throw 'Scenario cleanup retained attempt capability'}
if($attemptCase.StartsWith('role-')) {
    if($null -ne $owner.TryCreateAttemptRoleRequestForTest('helpee') -or
        $attemptTicket.MatchesRoleLaunchForTest('helpee',$null,$null,$null)) {throw 'Cleanup retained role binding'}
    'attempt-role-binding-ok'
}
