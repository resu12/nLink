param([string]$ExePath,[string]$WalletPath,[string]$WalletPassword,[string]$SidecarPath,[string]$PayerMode,
    [string]$RouteMode,[string]$Fault,[string]$Direction,[string]$PayloadSize,[string]$ArtifactDir,
    [int]$TimeoutSeconds,[int]$ProgressTimeoutSeconds,[string]$RetainedAnalysisOwner,[string]$LiveToggleSequence)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$RepoRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')); $Directory=$ArtifactDir
. (Join-Path $PSScriptRoot 'FrontierAttemptDispatchLoader.ps1') $RepoRoot
. (Join-Path $PSScriptRoot 'FrontierAttemptDispatchProjection.ps1')
$private:relay=[NLink.Tools.LocalGuiWriterRequest]::TryAttachRelayReady($env:NLINK_GUI_WRITER_RELAY,[guid]$env:NLINK_GUI_WRITER_RELAY_ID,
    $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,$Directory)
if($null -eq $relay) {throw 'Attempt wrapper relay missing'}
$private:handoff=$relay.TryAttachFrontier()
if($null -eq $handoff) {throw 'Attempt wrapper registration missing'}
$ast=Read-AttemptAst (Join-Path $RepoRoot 'tools/Run-FileTransferTunaGuiSmoke.ps1')
# Execute the original non-secret assignment statements used by the wrapper.
$names=@('NLINK_TUNA_GUI_PAYER_MODE','NLINK_TUNA_GUI_FAULT','NLINK_TUNA_GUI_ROUTE_MODE','NLINK_TUNA_GUI_LIVE_MULTI_TOGGLE_SEQUENCE',
    'NLINK_FILETRANSFER_SOAK_DIRECTION','NLINK_FILETRANSFER_SOAK_PAYLOAD_SIZES','NLINK_FILETRANSFER_SOAK_CYCLE_TIMEOUT_SECONDS',
    'NLINK_FILETRANSFER_SOAK_PROGRESS_TIMEOUT_SECONDS','NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR')
$resolvedArtifactDir=$Directory
foreach($name in $names) {
    $statement=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq ('$env:'+$name)},$true))
    if($statement.Count -ne 1) {throw ('Wrapper environment assignment ambiguous: '+$name)}
    Invoke-Expression $statement[0].Extent.Text
}
foreach($name in @('ConvertTo-TunaGuiProcessArgument','Write-TunaGuiAtomicJson','Get-TunaGuiFinalizationGraceSeconds',
    'Complete-TunaGuiChildOutputCapture','Get-TunaGuiTerminalPhaseDeadlineRequest','Invoke-TunaGuiSmokeWithHardDeadline')) {
    $text=Get-AttemptFunction $ast $name
    if($name -ceq 'Invoke-TunaGuiSmokeWithHardDeadline') {
        if(-not $env:NLINK_TEST_PRODUCTION_PARENT_PAYLOAD) {
            $offer=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
                $n.Extent.Text.StartsWith('if ($null -ne $private:canonicalPayloadWrapper)') -and
                $n.Extent.Text.Contains('.TryOfferPayloadPair(')},$true))[0]
            $text=Replace-AttemptSeam $text $offer.Extent.Text ''
        } else {
            $text=Replace-AttemptSeam $text 'try { $null = $FrontierRelayHandoff.TryOfferPayloadPair($private:canonicalPayloadWrapper, $private:acceptedWriter) } catch { }' @'
try { $null = $FrontierRelayHandoff.TryOfferPayloadPair($private:canonicalPayloadWrapper, $private:acceptedWriter) } catch { }
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionParentPayloadProbe.ps1') -Stage WrapperOffered
'@
        }
        if(-not $env:NLINK_TEST_PRODUCTION_WRAPPER_PAYLOAD) {
            # Preserve the existing component probes, which deliberately own
            # listener start and terminal take. New caller tests use production.
            $listener=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.TryStatementAst] -and
                $n.Extent.Text.Contains('.TryStartPayloadWrapper(')},$true) | Sort-Object {$_.Extent.Text.Length})[0]
            $text=Replace-AttemptSeam $text $listener.Extent.Text ''
            $text=Replace-AttemptSeam $text '$private:acceptedWriter = $localWriterRequest.TakeAfterExitWithPayloads($private:frontierGuiReports, $private:frontierParentCustody)' '$private:acceptedWriter = $localWriterRequest.TakeAfterExit()'
        }
        $text=Replace-AttemptSeam $text "(Join-Path `$RepoRoot 'tools\GuiSmoke-Windows.ps1')," "(Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierScenarioLaunchGui.ps1'), '-RepoRoot', `$RepoRoot, '-Directory', `$ArtifactDir, '-Case', 'valid',"
        $text=Replace-AttemptSeam $text "`$startInfo.FileName = 'powershell'" '$startInfo.FileName = (Get-Process -Id $PID).Path'
        $text=Replace-AttemptSeam $text '[void]$LocalWriterRelay.TryForwardCanonicalAttempt($localWriterRequest, $ExePath, $TimeoutSeconds, $startInfo)' @'
$script:attemptForwarded=$LocalWriterRelay.TryForwardCanonicalAttempt($localWriterRequest, $ExePath, $TimeoutSeconds, $startInfo)
'@
        $text=Replace-AttemptSeam $text '$process = [System.Diagnostics.Process]::new()' @'
if(-not $script:attemptForwarded) {
    if($env:NLINK_TEST_ATTEMPT_CASE -ceq 'argv-changed') {throw 'attempt_wrapper_projection_rejected'}
    throw 'Original attempt not forwarded'
}
if($LocalWriterRelay.TryForwardCanonicalAttempt($localWriterRequest,$ExePath,$TimeoutSeconds,$startInfo)) {throw 'Duplicate attempt forwarding'}
$startInfo.EnvironmentVariables['LOCALAPPDATA']=Join-Path $ArtifactDir 'fixture-local-app-data'
$process = [System.Diagnostics.Process]::new()
'@
        $text=Replace-AttemptSeam $text '$stderrTask = $private:frontierGuiReports.Stderr' @'
$stderrTask = $private:frontierGuiReports.Stderr
$script:guiReports=$private:frontierGuiReports; $script:guiLaunches++
if($env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -and -not $env:NLINK_TEST_PRODUCTION_WRAPPER_PAYLOAD) {
    if($env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -like 'listener-*') {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPayloadListenerProbe.ps1') -Stage Prepare
    }
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadWrapperProbe.ps1') -Stage Start
    if($env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -like 'listener-*') {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPayloadListenerProbe.ps1') -Stage Start
    }
}
if($env:NLINK_TEST_ATTEMPT_CASE -ceq 'gui-killed') {$process.Kill(); [void]$process.WaitForExit(5000)}
'@
        if($env:NLINK_TEST_PRODUCTION_WRAPPER_PAYLOAD) {
            $text=Replace-AttemptSeam $text 'catch { } # Optional payload listener never replaces the original launch.' @'
catch { } # Optional payload listener never replaces the original launch.
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionWrapperPayloadProbe.ps1') -Stage Started
'@
            $text=Replace-AttemptSeam $text 'if ($null -ne $localWriterRequest -and $exitCodeAvailable -and $exitCode -eq 0 -and $outputCapture.Completed) {' @'
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionWrapperPayloadProbe.ps1') -Stage BeforeTake
if ($null -ne $localWriterRequest -and $exitCodeAvailable -and $exitCode -eq 0 -and $outputCapture.Completed) {
'@
            $text=Replace-AttemptSeam $text '$private:acceptedWriter = $localWriterRequest.TakeAfterExitWithPayloads($private:frontierGuiReports, $private:frontierParentCustody)' @'
$private:acceptedWriter = $localWriterRequest.TakeAfterExitWithPayloads($private:frontierGuiReports, $private:frontierParentCustody)
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionWrapperPayloadProbe.ps1') -Stage AfterTake
'@
        } elseif($env:NLINK_TEST_PAYLOAD_WRAPPER_CASE) {
            $text=Replace-AttemptSeam $text '$nowUtc = [DateTimeOffset]::UtcNow' @'
if($env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -ceq 'gui-killed' -and $script:payloadWrapper.ProvisionalCustody) {
    $process.Kill(); [void]$process.WaitForExit(5000); continue
}
$nowUtc = [DateTimeOffset]::UtcNow
'@
            $text=Replace-AttemptSeam $text 'if ($null -ne $localWriterRequest -and $exitCodeAvailable -and $exitCode -eq 0 -and $outputCapture.Completed) {' @'
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadWrapperProbe.ps1') -Stage Finish
if ($null -ne $localWriterRequest -and $exitCodeAvailable -and $exitCode -eq 0 -and $outputCapture.Completed) {
'@
            $text=Replace-AttemptSeam $text 'if ($null -ne $localWriterRequest) { $localWriterRequest.Dispose() }' @'
if($null -ne $script:payloadWrapper) {$script:payloadWrapper.Dispose()}
if ($null -ne $localWriterRequest) { $localWriterRequest.Dispose() }
'@
            if($env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -like 'writer-*') {
                if($env:NLINK_TEST_PAYLOAD_PARENT_CASE) {
                    $text=Replace-AttemptSeam $text '$private:acceptedWriter = $localWriterRequest.TakeAfterExit()' @'
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadParentProbe.ps1') -Stage WrapperOffer
'@
                } else {
                $text=Replace-AttemptSeam $text '$private:acceptedWriter = $localWriterRequest.TakeAfterExit()' @'
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadWrapperProbe.ps1') -Stage Writers
'@
                }
            }
        }
    }
    Invoke-Expression $text
}
function Stop-TunaGuiSmokeProcessTree {param($Process) $Process.Kill(); [void]$Process.WaitForExit(5000)}
function Stop-TunaGuiSmokeApplicationDescendants {throw 'Unexpected attempt GUI deadline'}
function Request-TunaGuiScenarioDeadline {throw 'Unexpected attempt GUI deadline'}
$script:guiReports=$null; $script:guiLaunches=0; $failure=''; $result=$null
$script:attemptForwarded=$false
$script:payloadWrapper=$null; $script:payloadTaken=$false; $script:payloadChecked=$false
$script:payloadWritersChecked=$false; $script:payloadWritersFailure=''
try {
    try {$result=Invoke-TunaGuiSmokeWithHardDeadline -RepoRoot $RepoRoot -ArtifactDir $Directory -ExePath $ExePath -TimeoutSeconds $TimeoutSeconds -LocalWriterRelay $relay -FrontierRelayHandoff $handoff}
    catch {$failure=$_.Exception.Message}
    if($env:NLINK_TEST_PRODUCTION_WRAPPER_PAYLOAD) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionWrapperPayloadProbe.ps1') -Stage Finished
    }
    if($env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -like 'listener-*') {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPayloadListenerProbe.ps1') -Stage AfterFinally
        'attempt-dispatch-wrapper-ok'
        return
    }
    if($env:NLINK_TEST_ATTEMPT_CASE -ceq 'argv-changed') {
        if(-not $failure.Contains('attempt_wrapper_projection_rejected') -or $script:guiLaunches -ne 0) {throw "Changed argv escaped: $failure"}
    } else {
        if($failure -or $script:guiLaunches -ne 1 -or $result.TimedOut -or -not $result.ExitCodeAvailable -or
            -not $script:guiReports.Stdout.IsCompleted -or -not $script:guiReports.Stderr.IsCompleted -or
            ($env:NLINK_TEST_ATTEMPT_CASE -cne 'gui-killed' -and $env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -cne 'gui-killed' -and
                $script:guiReports.Stdout.GetAwaiter().GetResult() -notmatch 'attempt-dispatch-gui-ok')) {
            throw ('Attempt GUI failed: '+$failure+' / '+[IO.File]::ReadAllText((Join-Path $Directory 'gui-smoke-stderr.log')))
        }
        if($env:NLINK_TEST_ATTEMPT_CASE -ceq 'gui-killed' -or $env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -ceq 'gui-killed') {if($result.ExitCode -eq 0) {throw 'Killed GUI accepted'}}
        elseif($result.ExitCode -ne $(if($env:NLINK_TEST_ATTEMPT_CASE -ceq 'gui-nonzero' -or $env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -ceq 'gui-nonzero'){7}else{0})) {throw 'GUI exit gate changed'}
        if($env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -and -not $script:payloadChecked) {throw 'Payload terminal check omitted'}
        if($env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -like 'writer-*' -and -not $script:payloadWritersChecked) {
            throw ('Native writer association check omitted: '+$script:payloadWritersFailure)
        }
    }
    'attempt-dispatch-wrapper-ok'
} finally {
    $relay.Dispose()
    if($env:NLINK_TEST_PAYLOAD_PARENT_CASE) {
        [Console]::Error.WriteLine('native-relay-wrapper; forward='+$handoff.NativeForwardDiagnosticForTest+'; transfer_close='+$handoff.NativeTransferCloseDiagnosticForTest)
    }
}
if($env:NLINK_TEST_ATTEMPT_CASE -cin @('argv-changed','gui-nonzero','gui-killed') -or
    $env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -cin @('gui-nonzero','gui-killed')) {exit 7}
if($env:NLINK_TEST_PAYLOAD_PARENT_CASE -ceq 'wrapper-nonzero') {exit 7}
