param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) {throw 'Parent syntax invalid'}
$function=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-RouteAcceptanceChildScriptNoThrow'},$true))[0]
if($function.Extent.Text -notmatch 'TryStartPayloadParent' -or $function.Extent.Text -notmatch 'TakeRelayAfterExitWithPayloads' -or
    $function.Extent.Text -notmatch '\$private:canonicalPayloadParent = \$null') {throw 'Production parent payload callers missing'}
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$method=[NLink.Tools.LocalGuiWriterRequest].GetMethod('TakeRelayAfterExitWithPayloads')
if($null -eq $method -or $method.ReturnType -ne [NLink.Tools.LocalGuiWriterRequest+RelayedCompletion] -or
    $method.GetParameters().Count -ne 1 -or $method.GetParameters()[0].ParameterType -ne [NLink.Tools.LocalGuiWriterRequest+FrontierRelayReports]) {throw 'Native parent completion API missing'}
$start=@($function.FindAll({param($n) $n -is [Management.Automation.Language.TryStatementAst] -and $n.Extent.Text.Contains('.TryStartPayloadParent(')},$true) | Sort-Object {$_.Extent.Text.Length})[0]
$pull=@($function.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Extent.Text.StartsWith('if ($null -ne $private:canonicalPayloadParent)') -and $n.Extent.Text.Contains('.TryPullIfOffered(')},$true))[0]
$finish=@($function.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Extent.Text.StartsWith('if ($null -ne $private:relayRequest -and $private:relayOutputCaptured -and $exitCode -eq 0)')},$true))[0]
$cleanup=@($function.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Extent.Text -ceq 'if ($null -ne $private:canonicalPayloadParent) { $private:canonicalPayloadParent.Dispose() }'},$true))[0]
if($start.Extent.StartOffset -ge $pull.Extent.StartOffset -or $pull.Extent.StartOffset -ge $finish.Extent.StartOffset -or
    $cleanup.Extent.StartOffset -le $finish.Extent.EndOffset -or
    $function.Extent.Text.IndexOf('$private:canonicalPayloadParent.TryPullIfOffered()') -ge $function.Extent.Text.IndexOf('$private:relayReports.Registration.TryPullIfOffered()')) {throw 'Parent handoff ordering changed'}
$private:relayReports=[object]::new()
foreach($fault in @('valid','start-throw','pull-throw','nonzero','output-incomplete','no-request','no-export')) {
    $script:parentTakes=0;$script:parentPulls=0;$script:parentCloses=0;$script:nativeExports=0
    $script:pair=[pscustomobject]@{IsCurrent=$true;Fault=$fault}
    $script:pair | Add-Member ScriptMethod TryPullIfOffered {$script:parentPulls++;if($this.Fault -ceq 'pull-throw'){throw 'optional pull failure'};return $false}
    $script:pair | Add-Member ScriptMethod Dispose {$script:parentCloses++}
    $script:native=[object]::new();$script:result=[pscustomobject]@{}
    $script:result | Add-Member ScriptMethod TryTakeFrontierCustody {$script:nativeExports++;return $script:native}
    $private:relayRequest=[pscustomobject]@{Fault=$fault}
    $private:relayRequest | Add-Member ScriptMethod TryStartPayloadParent {param($reports) if($this.Fault -ceq 'start-throw'){throw 'optional start failure'};return $script:pair}
    $private:relayRequest | Add-Member ScriptMethod TakeRelayAfterExitWithPayloads {param($reports) $script:parentTakes++;return $script:result}
    $private:canonicalPayloadParent=$null;$private:pendingRelayCustody=$null
    Invoke-Expression $start.Extent.Text
    Invoke-Expression $pull.Extent.Text
    $private:relayOutputCaptured=$fault -cne 'output-incomplete';$exitCode=if($fault -ceq 'nonzero'){7}else{0}
    $value=$null;$nativeValue=if($fault -ceq 'no-export'){[object]::new()}else{$null}
    $LocalWriterRelay=[ref]$value;$FrontierRelayCustody=[ref]$nativeValue
    if($fault -ceq 'no-request') {$private:relayRequest=$null}
    Invoke-Expression $finish.Extent.Text
    $expectedTake=$fault -cin @('valid','start-throw','pull-throw','no-export')
    $expectedExport=$expectedTake -and $fault -cne 'no-export'
    if($script:parentTakes -ne [int]$expectedTake -or $script:nativeExports -ne [int]$expectedExport -or
        ($expectedTake -and -not [object]::ReferenceEquals($value,$script:result)) -or
        ($expectedExport -and -not [object]::ReferenceEquals($nativeValue,$script:native))) {throw ('Original terminal gate/export changed: '+$fault)}
    Invoke-Expression $cleanup.Extent.Text
    $expectedClose=$fault -cin @('nonzero','output-incomplete','no-request','no-export')
    if($script:parentCloses -ne [int]$expectedClose -or $script:parentPulls -ne $(if($fault -ceq 'start-throw'){0}else{1})) {throw ('Parent cleanup/pull changed: '+$fault)}
}
$wrapper=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferTunaGuiSmoke.ps1'),[ref]$tokens,[ref]$errors)
$offer=@($wrapper.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
    $n.Extent.Text.StartsWith('if ($private:frontierRelayRequested -and $null -ne $private:frontierParentCustody)')},$true))[0]
foreach($fault in @('valid','offer-throw','no-pair','no-native')) {
    $script:order=@();$frontierRelayRequested=$true;$private:frontierParentCustody=if($fault -ceq 'no-native'){$null}else{[object]::new()}
    $private:canonicalPayloadWrapper=if($fault -ceq 'no-pair'){$null}else{[object]::new()};$private:acceptedWriter=[object]::new()
    $FrontierRelayHandoff=[pscustomobject]@{Fault=$fault}
    $FrontierRelayHandoff | Add-Member ScriptMethod TryOfferPayloadPair {param($pair,$completion) $script:order+='payload';if($this.Fault -ceq 'offer-throw'){throw 'optional offer failure'};return $false}
    $FrontierRelayHandoff | Add-Member ScriptMethod TryOfferAndWait {param($custody,$completion) $script:order+='native';return $true}
    Invoke-Expression $offer.Extent.Text
    $expected=switch($fault){'no-native'{''};'no-pair'{'native'};default{'payload,native'}}
    if(($script:order -join ',') -cne $expected) {throw 'Optional payload changed native forwarding order'}
}
'production-parent-payload-contract-ok'
