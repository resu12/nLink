param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferTunaGuiSmoke.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) {throw 'Wrapper syntax invalid'}
$function=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-TunaGuiSmokeWithHardDeadline'},$true))[0]
if($function.Extent.Text -notmatch 'TryStartPayloadWrapper' -or $function.Extent.Text -notmatch 'TakeAfterExitWithPayloads') {throw 'Production wrapper payload callers missing'}
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$method=[NLink.Tools.LocalGuiWriterRequest].GetMethod('TakeAfterExitWithPayloads')
if($null -eq $method -or $method.ReturnType -ne [NLink.Tools.LocalGuiWriterRequest+Completion] -or $method.GetParameters().Count -ne 2 -or
    $method.GetParameters()[0].ParameterType -ne [NLink.Tools.LocalGuiWriterRequest+FrontierGuiReports] -or
    $method.GetParameters()[1].ParameterType -ne [NLink.Tools.LocalGuiWriterRequest+FrontierParentCustody]) {throw 'Original completion API missing'}
if($function.Extent.Text -notmatch '\$private:canonicalPayloadWrapper = \$null') {throw 'Payload wrapper is not private'}
$start=@($function.FindAll({param($n) $n -is [Management.Automation.Language.TryStatementAst] -and $n.Extent.Text.Contains('.TryStartPayloadWrapper(')},$true) | Sort-Object {$_.Extent.Text.Length})[0]
$finish=@($function.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Extent.Text.StartsWith('if ($null -ne $localWriterRequest -and $exitCodeAvailable -and $exitCode -eq 0 -and $outputCapture.Completed)')},$true))
$cleanup=@($function.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Extent.Text -ceq 'if ($null -ne $private:canonicalPayloadWrapper) { $private:canonicalPayloadWrapper.Dispose() }'},$true))
if($finish.Count -ne 1 -or $cleanup.Count -ne 1 -or $start.Extent.StartOffset -ge $finish[0].Extent.StartOffset -or
    $cleanup[0].Extent.StartOffset -le $finish[0].Extent.EndOffset) {throw 'Wrapper lifetime ordering changed'}
$frontierGuiReports=[object]::new();$frontierParentCustody=[object]::new()
$frontierRelayRequested=$false;$frontierHandoffRequested=$false;$LocalWriterRelay=$null
foreach($fault in @('valid','start-throw','nonzero','exit-unavailable','output-incomplete','no-request')) {
    $script:takes=0;$script:closes=0
    $private:canonicalPayloadWrapper=$null
    $localWriterRequest=[pscustomobject]@{Fault=$fault}
    $localWriterRequest | Add-Member ScriptMethod TryStartPayloadWrapper {
        param($reports)
        if($this.Fault -ceq 'start-throw') {throw 'optional startup omission'}
        $pair=[pscustomobject]@{}
        $pair | Add-Member ScriptMethod Dispose {$script:closes++}
        return $pair
    }
    $localWriterRequest | Add-Member ScriptMethod TakeAfterExitWithPayloads {param($reports,$custody) $script:takes++;return $this}
    Invoke-Expression $start.Extent.Text
    $exitCodeAvailable=$fault -cne 'exit-unavailable';$exitCode=if($fault -ceq 'nonzero'){7}else{0}
    $outputCapture=[pscustomobject]@{Completed=($fault -cne 'output-incomplete')}
    $localCompletion=$null;$LocalWriterCompletion=[ref]$localCompletion
    if($fault -ceq 'no-request') {$localWriterRequest=$null}
    Invoke-Expression $finish[0].Extent.Text
    $expectedTake=$fault -cin @('valid','start-throw')
    if($script:takes -ne [int]$expectedTake -or ($null -ne $localCompletion) -ne $expectedTake) {throw ('Original completion gate changed: '+$fault)}
    if($expectedTake -and -not [object]::ReferenceEquals($localCompletion,$localWriterRequest)) {throw 'Original completion replaced'}
    Invoke-Expression $cleanup[0].Extent.Text
    if($script:closes -ne $(if($fault -ceq 'start-throw'){0}else{1})) {throw 'Payload cleanup omitted'}
}
'production-wrapper-payload-contract-ok'
