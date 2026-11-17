param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
class RelayReferenceConversion { RelayReferenceConversion([object]$value) {} }
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) {throw 'Actual parent parse failed'}
$definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-RouteAcceptanceChildScriptNoThrow'},$true))
if($definition.Count -ne 1) {throw 'Original child launcher ambiguous'}
$text=$definition[0].Extent.Text
$text=$text.Replace("Join-Path `$PSScriptRoot 'FileTransferOps/LocalGuiWriterRequest.ps1'","Join-Path `$script:relayRepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1'")
$text=$text.Replace('$private:relayStartInfo.FileName = ''powershell''','$private:relayStartInfo.FileName = (Get-Process -Id $PID).Path')
$text=$text.Replace('$private:relayStdout = $private:relayReports.Stdout',"`$private:relayStdout = `$private:relayReports.Stdout`n`$script:relayRequest = `$private:relayRequest; `$script:relayReports = `$private:relayReports; `$script:relayLaunches++")
if($Case -ceq 'output-fails') {$text=$text.Replace('$childOutput = @(foreach',"throw 'injected original stream collection failure'`n`$childOutput = @(foreach")}
Invoke-Expression $text
if($Case -ceq 'host-output-fails') {function Write-Host {param($Object) throw 'injected original host output failure'}}
$script:relayRepoRoot=$RepoRoot; $script:RouteAcceptanceExecutionMode='live'
$script:relayRequest=$null; $script:relayReports=$null; $script:relayLaunches=0
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID='cleanup-adoption-run'; $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='cleanup-adoption-scope'
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$completion=$null; $exported=$null; [uri]$incompatible=$null; [RelayReferenceConversion]$converted=$null
$args=@{ScriptPath=(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRelayWrapper.ps1');
    Arguments=@('-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Case',$Case);LocalWriterRelay=[ref]$completion;LocalWriterArtifactDir=$Directory;FrontierRelayCustody=[ref]$exported}
if($Case -ceq 'incompatible-output') {$args.FrontierRelayCustody=[ref]$incompatible}
if($Case -ceq 'coerced-output') {$args.FrontierRelayCustody=[ref]$converted}
$failure=''; $code=-1
try {
    try {$code=Invoke-RouteAcceptanceChildScriptNoThrow @args} catch {$failure=$_.Exception.Message}
    if($script:relayLaunches -ne 1 -or -not $script:relayReports.Stdout.IsCompleted -or -not $script:relayReports.Stderr.IsCompleted) {throw 'Original Wrapper launch/readers changed'}
    if($script:relayReports.Stdout.GetAwaiter().GetResult() -notmatch 'frontier-relay-wrapper-ok') {throw ('Original Wrapper failed: '+$script:relayReports.Stderr.GetAwaiter().GetResult())}
    if($Case -ceq 'host-output-fails') {if($failure -cne 'injected original host output failure') {throw 'Host output failure was hidden'}}
    elseif($failure -or $code -ne $(if($Case -ceq 'wrapper-nonzero'){7}elseif($Case -ceq 'output-fails'){1}else{0})) {throw "Original Wrapper outcome changed: $code / $failure"}
    $pending=$script:relayRequest.GetType().GetField('frontierRelayCustody',$flags).GetValue($script:relayRequest)
    if($null -eq $pending) {throw 'Actual wait did not pull offered pins'}
    $pins=$pending.GetType().GetField('pins',$flags).GetValue($pending)
    $handles=$pins.GetType().GetField('Handles',$flags).GetValue($pins)
    $expected=$Case -cin @('valid','analysis','analysis-changed')
    if(($null -ne $exported) -ne $expected -or $null -ne $incompatible -or $null -ne $converted) {throw 'Output gate exported foreign/unaccepted custody'}
    if($expected) {
        if(-not $exported.IsCurrent) {throw 'Exported pins did not survive actual launcher cleanup'}
        if($Case -ceq 'valid') {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRelayWorkflow.ps1')}
        if($Case.StartsWith('analysis')) {
            $writer=$completion.WriterContext
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRelayAnalysis.ps1')
        }
        $exported.Dispose()
    }
    if(@($handles | Where-Object {-not $_.IsClosed}).Count) {throw 'Actual launcher leaked provisional pins'}
    'frontier-relay-launcher='+$Case
} finally {if($null -ne $exported) {$exported.Dispose()}; if($null -ne $script:relayRequest) {$script:relayRequest.Dispose()}}
