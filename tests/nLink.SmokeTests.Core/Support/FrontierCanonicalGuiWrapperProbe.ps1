param([string]$ExePath,[string]$WalletPath,[string]$WalletPassword,[string]$SidecarPath,[string]$PayerMode,
    [string]$RouteMode,[string]$Fault,[string]$Direction,[string]$PayloadSize,[string]$ArtifactDir,
    [int]$TimeoutSeconds,[int]$ProgressTimeoutSeconds,[string]$RetainedAnalysisOwner,[string]$LiveToggleSequence)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$RepoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
. (Join-Path $PSScriptRoot 'FrontierAttemptDispatchProjection.ps1')
$case=$env:NLINK_TEST_CANONICAL_GUI_DISPATCH
$private:relay=[NLink.Tools.LocalGuiWriterRequest]::TryAttachRelayReady($env:NLINK_GUI_WRITER_RELAY,[guid]$env:NLINK_GUI_WRITER_RELAY_ID,
    $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,$ArtifactDir)
if($null -eq $relay) {throw 'Original wrapper relay missing'}
$private:handoff=$relay.TryAttachFrontier()
if(($null -eq $handoff) -ne ($case -ceq 'disabled')) {throw 'Original wrapper opt-in changed'}
$ast=Read-AttemptAst (Join-Path $RepoRoot 'tools/Run-FileTransferTunaGuiSmoke.ps1')
$resolvedArtifactDir=$ArtifactDir
foreach($name in @('NLINK_TUNA_GUI_PAYER_MODE','NLINK_TUNA_GUI_FAULT','NLINK_TUNA_GUI_ROUTE_MODE',
    'NLINK_TUNA_GUI_LIVE_MULTI_TOGGLE_SEQUENCE','NLINK_FILETRANSFER_SOAK_DIRECTION','NLINK_FILETRANSFER_SOAK_PAYLOAD_SIZES',
    'NLINK_FILETRANSFER_SOAK_CYCLE_TIMEOUT_SECONDS','NLINK_FILETRANSFER_SOAK_PROGRESS_TIMEOUT_SECONDS','NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR')) {
    $statements=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq ('$env:'+$name)},$true))
    if($statements.Count -ne 1) {throw 'Original environment assignment ambiguous'}
    Invoke-Expression $statements[0].Extent.Text
}
if($case -ceq 'wrapper-environment') {$env:NLINK_FILETRANSFER_SOAK_PROGRESS_TIMEOUT_SECONDS='31'}
foreach($name in @('ConvertTo-TunaGuiProcessArgument','Write-TunaGuiAtomicJson','Get-TunaGuiFinalizationGraceSeconds',
    'Complete-TunaGuiChildOutputCapture','Get-TunaGuiTerminalPhaseDeadlineRequest','Invoke-TunaGuiSmokeWithHardDeadline')) {
    $text=Get-AttemptFunction $ast $name
    if($name -ceq 'Invoke-TunaGuiSmokeWithHardDeadline') {
        # Only substitute the headless GUI script, host under test, and capture
        # assertions. Keep production forwarding and launch/watchdogs intact.
        $text=Replace-AttemptSeam $text "(Join-Path `$RepoRoot 'tools\GuiSmoke-Windows.ps1')," "(Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalGuiConsumerProbe.ps1'),"
        $text=Replace-AttemptSeam $text "`$startInfo.FileName = 'powershell'" '$startInfo.FileName = (Get-Process -Id $PID).Path'
        $text=Replace-AttemptSeam $text '[void]$LocalWriterRelay.TryForwardCanonicalAttempt($localWriterRequest, $ExePath, $TimeoutSeconds, $startInfo)' @'
if($env:NLINK_TEST_CANONICAL_GUI_DISPATCH -ceq 'wrong-host') {
    # A canonical but different host path must not match the original bytes.
    $savedHost=$startInfo.FileName
    $startInfo.FileName=Join-Path (Split-Path $savedHost) 'different-host.exe'
}
$script:forwarded=$LocalWriterRelay.TryForwardCanonicalAttempt($localWriterRequest,$ExePath,$TimeoutSeconds,$startInfo)
$script:duplicateAccepted=$LocalWriterRelay.TryForwardCanonicalAttempt($localWriterRequest,$ExePath,$TimeoutSeconds,$startInfo)
if($env:NLINK_TEST_CANONICAL_GUI_DISPATCH -ceq 'wrong-host') {$startInfo.FileName=$savedHost}
'@
        $text=Replace-AttemptSeam $text '$state.guiChildProcessId = $process.Id' @'
$script:childPid=$process.Id
$state.guiChildProcessId = $process.Id
'@
    }
    Invoke-Expression $text
}
function Stop-TunaGuiSmokeProcessTree {param($Process) $Process.Kill(); [void]$Process.WaitForExit(5000)}
function Stop-TunaGuiSmokeApplicationDescendants {throw 'Unexpected GUI deadline'}
function Request-TunaGuiScenarioDeadline {throw 'Unexpected GUI deadline'}
$script:forwarded=$false; $script:duplicateAccepted=$false; $script:childPid=0
try {
    $result=Invoke-TunaGuiSmokeWithHardDeadline -RepoRoot $RepoRoot -ArtifactDir $ArtifactDir -ExePath $ExePath -TimeoutSeconds $TimeoutSeconds -LocalWriterRelay $relay -FrontierRelayHandoff $handoff
    if($result.TimedOut -or -not $result.ExitCodeAvailable -or
        $result.ExitCode -ne $(if($case -ceq 'gui-nonzero'){7}else{0})) {
        throw ('Original GUI exit/output gate failed: '+[IO.File]::ReadAllText((Join-Path $ArtifactDir 'gui-smoke-stderr.log')))
    }
    if($script:forwarded -ne ($case -cin @('enabled','gui-environment','gui-nonzero','copied-ticket','unconsumed-ticket','retired-ticket','parent-cancelled'))) {throw 'Forwarding admitted a different attempt'}
    if($script:duplicateAccepted) {throw 'Duplicate forwarding accepted'}
    $gui=Get-Content -LiteralPath (Join-Path $ArtifactDir 'canonical-gui-consumer.json') -Raw | ConvertFrom-Json
    if($gui.Consumed -ne ($case -cin @('enabled','gui-nonzero','copied-ticket','retired-ticket','parent-cancelled')) -or
        $gui.ScenarioOwned -ne ($case -cin @('enabled','gui-nonzero')) -or $gui.ScenarioCalls -ne 2 -or
        -not $gui.ReplayRejected -or -not $gui.PrivateInputs -or -not $gui.Retired -or $gui.Pid -ne $script:childPid) {throw 'GUI scope or retirement changed'}
    [ordered]@{Checked=$true;WrapperPid=$PID;GuiPid=$script:childPid;Forwarded=$script:forwarded;
        Consumed=$gui.Consumed;ScenarioOwned=$gui.ScenarioOwned;ReplayRejected=$gui.ReplayRejected;PrivateInputs=$gui.PrivateInputs} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ArtifactDir 'canonical-gui-chain.json') -Encoding UTF8
} finally {$relay.Dispose()}
