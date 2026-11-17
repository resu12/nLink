param([string]$RepoRoot,[ValidateSet('Contract','Offer')][string]$Mode)
$ErrorActionPreference='Stop'
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) {throw 'Production GUI parse failed'}
$function=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Write-FileTransferGuiLogLedgerEvidence'},$true))[0]
$assignment=@($function.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$private:frontierHandoffPrepared'},$true))
if($assignment.Count -ne 1) {throw 'Production finalizer completion block ambiguous'}
$completionText=($assignment[0].Parent.Statements | ForEach-Object {$_.Extent.Text}) -join "`n"
if($completionText -notmatch 'TryOfferClosedPayloadsToWrapper') {throw 'Production closed-payload handoff missing'}
$completion=[scriptblock]::Create($completionText)
if($Mode -ceq 'Offer') {
    # Execute the production completion block, not fixture-side joins/offers.
    . $completion
    return
}
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$type=[NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptDispatch]
$method=$type.GetMethod('TryOfferClosedPayloadsToWrapper')
if($null -eq $method -or $method.ReturnType -ne [bool] -or $method.GetParameters().Count -ne 1 -or
    $method.GetParameters()[0].ParameterType -ne [NLink.Tools.LocalGuiWriterRequest+FrontierFinalizerBinding]) {throw 'Original binding-only composition API missing'}
if(@($type.GetMethods([Reflection.BindingFlags]'Instance,Static,Public,NonPublic') | Where-Object Name -like '*ForTest').Count) {throw 'Test hooks leaked into normal loader'}
if($function.Extent.Text -notmatch 'Set-Variable -Name CanonicalAttempt -Option Private' -or
    @($function.Body.ParamBlock.Parameters | Where-Object {$_.Name.VariablePath.UserPath -ceq 'CanonicalAttempt'}).Count -ne 1) {throw 'Finalizer attempt parameter not private'}
$scenario=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Run-ScenarioFileTransferTunaHandoffFallback'},$true))[0]
$calls=@($scenario.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -ceq 'Write-FileTransferGuiLogLedgerEvidence'},$true))
if($calls.Count -ne 1 -or $calls[0].Extent.Text -notmatch '-CanonicalAttempt \$CanonicalAttempt') {throw 'Scenario did not forward original attempt'}
# Only the completion boundary is exercised here; all ordinary evidence gates
# remain outside it. Fakes prove omission/exception handling, not native custody.
foreach($fault in @('absent','rejected','throw','accepted','no-binding','prepare-rejected')) {
    $script:order=[Collections.Generic.List[string]]::new()
    $CanonicalAttempt=$null
    if($fault -cne 'absent') {
        $CanonicalAttempt=[pscustomobject]@{Fault=$fault}
        $CanonicalAttempt | Add-Member ScriptMethod TryOfferClosedPayloadsToWrapper {
            param($binding)
            $script:order.Add('offer')
            if($null -eq $binding) {throw 'Binding lost'}
            if($this.Fault -ceq 'throw') {throw 'optional payload failure'}
            return $this.Fault -ceq 'accepted'
        }
    }
    $localFrontierBinding=[pscustomobject]@{IsCurrent=$true;Prepared=($fault -cne 'prepare-rejected')}
    $localFrontierBinding | Add-Member ScriptMethod TryPrepareParentHandoffWhenReady {$script:order.Add('prepare');return $this.Prepared}
    $localFrontierBinding | Add-Member ScriptMethod TryCompleteParentHandoff {$script:order.Add('complete');return $false}
    if($fault -ceq 'no-binding') {$localFrontierBinding=$null}
    $output=[pscustomobject]@{Sha256='';FileIdentity='';ByteCount=0;LineCount=0}
    $nativeCapture=[pscustomobject]@{Retained=$output;Writer=$output;Semantic=$output;Writers=@();SourcePath='';FrozenLineCount=0;Bookmark=0}
    $script:GuiSmokeAppLogLedger=[pscustomobject]@{LedgerId=''}
    $localWriterIssuer=[pscustomobject]@{}
    $localWriterIssuer | Add-Member ScriptMethod Complete {$script:order.Add('ordinary')}
    . $completion
    $expected=switch($fault) {
        'absent' {'prepare,complete'}
        'no-binding' {'ordinary'}
        'prepare-rejected' {'offer,prepare,ordinary'}
        default {'offer,prepare,complete'}
    }
    if(($script:order -join ',') -cne $expected) {throw ('Completion ordering changed: '+$fault)}
}
'gui-closed-payload-contract-ok'
