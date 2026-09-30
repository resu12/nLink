param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) {throw 'Original parent parse failed'}
$phase=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Invoke-Phase4RouteAcceptanceScenario'},$true))[0]
$attemptCalls=@($phase.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -ceq 'Invoke-TunaRouteAcceptanceRun'},$true))
if($attemptCalls.Count -ne 2 -or $attemptCalls[0].Extent.Text -notmatch '-AttemptIndex 1' -or
    -not $attemptCalls[1].Extent.Text.Contains('-AttemptIndex ($rerun + 1)')) {throw 'Actual first/rerun attempt identity missing'}
$gui=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
$guiCalls=@($gui.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -ceq 'Run-ScenarioFileTransferTunaHandoffFallback'},$true))
if($errors.Count -or $guiCalls.Count -ne 1 -or $guiCalls[0].Extent.Text -cne
    'Run-ScenarioFileTransferTunaHandoffFallback -Context $ctx -FrontierOwner $private:scenarioFrontierOwner -CanonicalAttempt $CanonicalAttempt') {throw 'Guarded GUI scenario call changed'}
foreach($name in @('Invoke-RouteAcceptanceScenarioIdentityScope','Invoke-TunaRouteAcceptanceRun','Invoke-RouteAcceptanceChildScriptNoThrow')) {
    $definitions=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
    if($definitions.Count -ne 1) {throw 'Original parent function ambiguous'}
    $text=$definitions[0].Extent.Text
    # Substitute only the child executable script and script-root resolution.
    # The original argument construction and binding/launch code execute intact.
    $text=$text.Replace("Join-Path `$RepoRoot 'tools\Run-FileTransferTunaGuiSmoke.ps1'","Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalAttemptChildProbe.ps1'")
    $text=$text.Replace("Join-Path `$PSScriptRoot 'FileTransferOps/LocalGuiWriterRequest.ps1'","Join-Path `$RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1'")
    Invoke-Expression $text
}
function Invoke-RouteAcceptanceRetainedAnalysis {throw 'Canonical binding probe stopped before analysis'}
$script:RouteAcceptanceRunId='canonical-launch-'+[guid]::NewGuid().ToString('N')
$script:RouteAcceptanceExecutionMode='fake'
$TimeoutSeconds=100; $ProgressTimeoutSeconds=30
$env:NLINK_TEST_CANONICAL_ATTEMPT_BINDING=$Case
$attempt=if($Case -ceq 'second-attempt'){2}else{1}
$payload=if($Case -ceq 'wrong-size'){'128MiB'}else{'256MiB'}
$publication=$null; $failure=''
try {
    Invoke-RouteAcceptanceScenarioIdentityScope -ScenarioName 'regular-v4-live-activation-off-on-off-256mb' -AttemptIndex $attempt -Action {
        $call=@{RepoRoot=$RepoRoot;ArtifactDir=$Directory;ResolvedExePath=(Get-Process -Id $PID).Path;
            ResolvedWalletPath='private-wallet-path';ResolvedSidecarPath='private-sidecar-path';EffectiveWalletPassword='private-wallet-password';
            RouteMode='live-regular-activation-cycle';Fault='switch-off';PayerMode='helpee';PayloadSize=$payload;
            LiveToggleSequence='on,off,on,off';LiveProofMode='RegularActivationCycle';LocalPublication=[ref]$publication;
            CaptureLocalPublicationDiagnostics=($Case -cne 'disabled')}
        # Explicit attempt input is part of the production integration.
        Invoke-TunaRouteAcceptanceRun @call -AttemptIndex $attempt
    }
} catch {$failure=$_.Exception.Message}
if($failure -cne 'Canonical binding probe stopped before analysis') {throw ('Original child/launch failed: '+$failure)}
if($null -ne $publication) {throw 'Child probe manufactured publication'}
$evidence=Get-Content -LiteralPath (Join-Path $Directory 'canonical-attempt-child.json') -Raw | ConvertFrom-Json
if($evidence.Bound -ne ($Case -ceq 'enabled') -or $evidence.TestApiPresent -or $evidence.ChildPid -le 0) {throw 'Original child scope changed'}
try {$alive=Get-Process -Id $evidence.ChildPid -ErrorAction Stop; throw 'Original child survived cleanup'}
catch [Microsoft.PowerShell.Commands.ProcessCommandException] {}
'canonical-attempt-binding='+$Case
