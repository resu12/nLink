param([string]$RepoRoot,[string]$Directory,[string]$Case,[string]$PayloadWrapperCase='',[string]$ParentPayloadCase='',[string]$PrimaryPayloadCase='',
    [ValidateSet('','frontier-schedule','frontier-late-publication')][string]$DiagnosticCustodyCase='')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
# Only the reviewed actual-pair composite may use the full-size tail worker.
$canonicalTailProfile=$Case -ceq 'writer-source-read-cross-process-carrier-full-canonical-tail-late-publication'
if(($canonicalTailProfile -or $PrimaryPayloadCase -ceq 'canonical-tail-late-publication') -and
    (-not $canonicalTailProfile -or $PayloadWrapperCase -cne 'writer-valid' -or $ParentPayloadCase -cne 'valid' -or
        $PrimaryPayloadCase -cne 'canonical-tail-late-publication' -or $DiagnosticCustodyCase)) {
    throw 'Canonical tail requires the exact actual-pair composite profile'
}
$env:NLINK_TEST_PAYLOAD_WRAPPER_CASE=$PayloadWrapperCase
$env:NLINK_TEST_PAYLOAD_PARENT_CASE=$ParentPayloadCase
$env:NLINK_TEST_PAYLOAD_PRIMARY_CASE=$PrimaryPayloadCase
$env:NLINK_TEST_DIAGNOSTIC_CUSTODY=$DiagnosticCustodyCase
if($Case.EndsWith('-frontier-late-publication') -and $DiagnosticCustodyCase -cne 'frontier-late-publication') {
    throw 'Diagnostic custody requires the separate small profile without transfer-pair hooks'
}
if($DiagnosticCustodyCase -and ($Case -cne ('writer-source-read-cross-process-carrier-'+$DiagnosticCustodyCase) -or
    $PayloadWrapperCase -or $ParentPayloadCase -or $PrimaryPayloadCase)) {throw 'Diagnostic custody requires the separate small profile without transfer-pair hooks'}
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchLoader.ps1') $RepoRoot
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
$ast=Read-AttemptAst (Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1')
foreach($name in @('Invoke-RouteAcceptanceScenarioIdentityScope','New-Phase4RouteAcceptanceScenario',
    'Get-Phase5RouteAcceptanceScenarios','Invoke-TunaRouteAcceptanceRun','Invoke-RouteAcceptanceChildScriptNoThrow')) {
    $text=Get-AttemptFunction $ast $name
    if($name -ceq 'Invoke-RouteAcceptanceChildScriptNoThrow') {
        if(-not $env:NLINK_TEST_PRODUCTION_PARENT_PAYLOAD) {
            # Component fixtures retain their explicit payload owner and take.
            $start=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.TryStatementAst] -and
                $n.Extent.Text.Contains('.TryStartPayloadParent(')},$true) | Sort-Object {$_.Extent.Text.Length})[0]
            $pull=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
                $n.Extent.Text.StartsWith('if ($null -ne $private:canonicalPayloadParent)') -and
                $n.Extent.Text.Contains('.TryPullIfOffered(')},$true))[0]
            $text=Replace-AttemptSeam $text $start.Extent.Text ''
            $text=Replace-AttemptSeam $text $pull.Extent.Text ''
            $text=Replace-AttemptSeam $text '$private:relayedWriter = $private:relayRequest.TakeRelayAfterExitWithPayloads($private:relayReports)' '$private:relayedWriter = $private:relayRequest.TakeRelayAfterExit()'
        } else {
            $text=Replace-AttemptSeam $text 'catch { } # Optional payload owner cannot replace the original launch.' @'
catch { } # Optional payload owner cannot replace the original launch.
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionParentPayloadProbe.ps1') -Stage Started
'@
            $text=Replace-AttemptSeam $text 'try { $null = $private:canonicalPayloadParent.TryPullIfOffered() } catch { }' @'
try { $null = $private:canonicalPayloadParent.TryPullIfOffered() } catch { }
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionParentPayloadProbe.ps1') -Stage Polled
'@
            $text=Replace-AttemptSeam $text '$private:relayedWriter = $private:relayRequest.TakeRelayAfterExitWithPayloads($private:relayReports)' @'
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionParentPayloadProbe.ps1') -Stage BeforeTake
$private:relayedWriter = $private:relayRequest.TakeRelayAfterExitWithPayloads($private:relayReports)
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionParentPayloadProbe.ps1') -Stage AfterTake
'@
        }
        $text=Replace-AttemptSeam $text 'Set-Variable -Name LocalWriterRelay -Option Private -Value $LocalWriterRelay' @'
Set-Variable -Name LocalWriterRelay -Option Private -Value $LocalWriterRelay
# Redirect before optional native setup, so even its failure cannot launch the
# real live wrapper from this offline fixture.
$ScriptPath=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchWrapper.ps1'
'@
        $text=Replace-AttemptSeam $text "Join-Path `$PSScriptRoot 'FileTransferOps/LocalGuiWriterRequest.ps1'" "Join-Path `$RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1'"
        $text=Replace-AttemptSeam $text '$private:relayStartInfo.FileName = ''powershell''' '$private:relayStartInfo.FileName = (Get-Process -Id $PID).Path'
        $text=Replace-AttemptSeam $text '$private:relayStartInfo = [Diagnostics.ProcessStartInfo]::new()' @'
$private:projection=Get-AttemptLaunchProjection $Arguments $LiveProofMode
$clone=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($private:relayRequest,$null)
if($clone.TryBindAttemptForTest($AttemptIndex,$projection)) {throw 'Copied parent bound attempt'}
if(-not $private:relayRequest.TryBindAttemptForTest($AttemptIndex,$projection) -or $private:relayRequest.TryBindAttemptForTest($AttemptIndex,$projection)) {throw 'Parent attempt binding missing/repeated'}
$private:relayStartInfo = [Diagnostics.ProcessStartInfo]::new()
'@
        $text=Replace-AttemptSeam $text '$private:relayProcess = [Diagnostics.Process]::new()' @'
$private:relayStartInfo.EnvironmentVariables['NLINK_TEST_ATTEMPT_DISPATCH']='1'
$private:relayStartInfo.EnvironmentVariables['NLINK_TEST_ATTEMPT_CASE']=$Case
if($Case -ceq 'argv-changed') {$private:relayStartInfo.Arguments=$private:relayStartInfo.Arguments.Replace('256MiB','128MiB')}
$private:relayProcess = [Diagnostics.Process]::new()
'@
        $text=Replace-AttemptSeam $text '$private:relayStdout = $private:relayReports.Stdout' @'
$private:relayStdout = $private:relayReports.Stdout
$script:attemptReports=$private:relayReports; $script:attemptLaunches++
if($ParentPayloadCase) {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadParentProbe.ps1') -Stage Start}
'@
        if($ParentPayloadCase) {
            $text=Replace-AttemptSeam $text 'try { $null = $private:relayReports.Registration.TryPullIfOffered() } catch { }' @'
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadParentProbe.ps1') -Stage Poll
try { $null = $private:relayReports.Registration.TryPullIfOffered() } catch { }
'@
            $text=Replace-AttemptSeam $text '$private:relayedWriter = $private:relayRequest.TakeRelayAfterExit()' @'
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadParentProbe.ps1') -Stage Take
$private:relayedWriter = $script:payloadParentResult
'@
            $text=Replace-AttemptSeam $text '$private:pendingRelayCustody = $private:relayedWriter.TryTakeFrontierCustody()' '$private:pendingRelayCustody = $script:payloadParent.WriterCustody($private:relayedWriter)'
        }
    }
    Invoke-Expression $text
}
# Ordinary cases stop at the launch outcome. The primary-payload profile invokes
# the original retained analyzer, preserves its result and stops before route acceptance.
function Invoke-RouteAcceptanceRetainedAnalysis {
    param($ArtifactDir,$LiveRouteProofMode,[switch]$CaptureLocalSourceOwnership,[switch]$CaptureFrontierReports,
        $LocalWriterContext=$null,$LocalPublication=$null,$FrontierRelayCustody=$null)
    $script:analysisCalls++
    if($env:NLINK_TEST_PRODUCTION_PARENT_PAYLOAD) {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionParentPayloadProbe.ps1') -Stage AnalysisBoundary}
    if($DiagnosticCustodyCase) {
        if(-not $CaptureLocalSourceOwnership -or -not $CaptureFrontierReports -or
            $null -eq $LocalWriterContext -or $null -eq $FrontierRelayCustody) {throw 'Original diagnostic relay custody missing'}
        $writer=$LocalWriterContext; $exported=$FrontierRelayCustody
        $flags=[Reflection.BindingFlags]'Instance,NonPublic'; $Case='analysis'
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRelayAnalysis.ps1')
    }
    if($ParentPayloadCase) {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadParentProbe.ps1') -Stage AnalysisBoundary}
    if($PrimaryPayloadCase) {
        $writer=$script:payloadParentResult.WriterContext
        $exported=$script:payloadParent.WriterCustody($script:payloadParentResult)
        $flags=[Reflection.BindingFlags]'Instance,NonPublic'; $Case='analysis'
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRelayAnalysis.ps1')
    }
}
function Update-FileTransferTunaDirectEvidenceManifestFromReceipt {return [pscustomobject]@{CurrentRuntimeProof=$true}}
$script:RouteAcceptanceRunId='attempt-dispatch-'+[guid]::NewGuid().ToString('N')
$script:RouteAcceptanceExecutionMode='fake'
$script:attemptReports=$null; $script:attemptLaunches=0; $script:analysisCalls=0
$script:payloadParent=$null; $script:payloadParentResult=$null; $script:payloadParentFiles=$null
$script:payloadParentPulled=$false; $script:payloadParentFaulted=$false; $script:payloadParentFailure=''
$scenario=@(Get-Phase5RouteAcceptanceScenarios ([pscustomobject]@{Scenarios=@{}}) | Where-Object Name -CEQ 'regular-v4-live-activation-off-on-off-256mb')[0]
if($scenario.PayloadBytes -ne 268435456 -or ($scenario.ExpectedRouteChanges -join ',') -cne 'regular_nkn_v4_fast,file_tuna_v4,post_tuna_fallback_v6,file_tuna_v4,post_tuna_fallback_v6') {throw 'Canonical scenario changed'}
$TimeoutSeconds=if($Case.StartsWith('writer-source-read-cross-process-carrier-full-')) {210} else {100}
$ProgressTimeoutSeconds=30; $publication=$null; $failure=''
$beforeRun=$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID; $beforeScenario=$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID
$appExe=if($Case.StartsWith('writer-')) {(Get-Command pwsh -ErrorAction Stop).Source} else {(Get-Process -Id $PID).Path}
try {
    Invoke-RouteAcceptanceScenarioIdentityScope -ScenarioName $scenario.Name -AttemptIndex 1 -Action {
        Invoke-TunaRouteAcceptanceRun -RepoRoot $RepoRoot -ArtifactDir $Directory -ResolvedExePath $appExe `
            -ResolvedWalletPath 'unused-headless-wallet' -ResolvedSidecarPath 'unused-headless-sidecar' -EffectiveWalletPassword 'unused-headless-password' `
            -RouteMode $scenario.RouteMode -Fault $scenario.Fault -PayerMode $scenario.PayerMode -PayloadSize (($scenario.PayloadBytes/1MB).ToString()+'MiB') `
            -LiveToggleSequence $scenario.LiveToggleSequence -LiveProofMode $scenario.LiveProofMode -LocalPublication ([ref]$publication) -CaptureLocalPublicationDiagnostics
    }
} catch {
    $failure=$_.Exception.Message
    [Console]::Error.WriteLine('attempt-dispatch-failure; type='+$_.Exception.GetType().FullName+'; message='+$failure)
}
if($ParentPayloadCase) {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadParentProbe.ps1') -Stage Finish}
if($env:NLINK_TEST_PRODUCTION_PARENT_PAYLOAD) {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierProductionParentPayloadProbe.ps1') -Stage Finished}
if($script:attemptLaunches -ne 1 -or -not $script:attemptReports.Stdout.IsCompleted -or -not $script:attemptReports.Stderr.IsCompleted) {throw 'Original parent launch/report tasks lost'}
$stdout=$script:attemptReports.Stdout.GetAwaiter().GetResult(); $stderr=$script:attemptReports.Stderr.GetAwaiter().GetResult()
$expectedFailure=$Case -cin @('argv-changed','gui-nonzero','gui-killed') -or $PayloadWrapperCase -cin @('gui-nonzero','gui-killed') -or $ParentPayloadCase -cin @('wrapper-nonzero','wrapper-killed') -or [bool]$PrimaryPayloadCase -or [bool]$DiagnosticCustodyCase
if(([bool]$failure) -ne $expectedFailure -or ($ParentPayloadCase -cne 'wrapper-killed' -and $stdout -notmatch 'attempt-dispatch-wrapper-ok')) {throw "Attempt workflow failed: $failure / $stdout / $stderr"}
if($canonicalTailProfile) {
    if($failure -cne 'Offline canonical-tail verification stopped before route-cycle acceptance') {throw ('Canonical tail verification failed: '+$failure)}
} elseif($DiagnosticCustodyCase) {
    if($failure -cne 'Offline diagnostic-custody verification stopped before route-cycle acceptance') {throw ('Diagnostic custody verification failed: '+$failure)}
} elseif($PrimaryPayloadCase -ceq 'canonical-source') {
    if($failure -cne 'Offline canonical-source verification stopped before route-cycle acceptance') {throw ('Canonical source verification failed: '+$failure)}
} elseif($PrimaryPayloadCase -ceq 'canonical-fallback') {
    if($failure -cne 'Offline canonical-fallback verification stopped before route-cycle acceptance') {throw ('Canonical fallback verification failed: '+$failure)}
} elseif($PrimaryPayloadCase -and -not $failure.StartsWith('FileTransfer-Ops retained analysis exited successfully without complete proof artifacts for ')) {throw 'Original incomplete-proof rejection changed'}
if($script:analysisCalls -ne $(if($expectedFailure -and -not $PrimaryPayloadCase -and -not $DiagnosticCustodyCase){0}else{1}) -or $null -ne $publication) {throw 'Launch outcome promoted/lost'}
if($env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -cne $beforeRun -or $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID -cne $beforeScenario) {throw 'Original identity scope did not restore environment'}
'frontier-attempt-dispatch='+$Case
