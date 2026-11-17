param([string]$RepoRoot,[string]$Directory,[switch]$LegacyAnalyzer)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
function Definition([string]$Path,[string]$Name) {
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($Path,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw "Parse failed: $Path : $errors"}
    $definitions=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $Name},$true))
    if($definitions.Count -ne 1){throw "Definition ambiguous: $Name"}
    return $definitions[0].Extent.Text
}
$runner=Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'
$script:moduleRoot=$RepoRoot
$launch=(Definition $runner 'Invoke-RouteAcceptanceChildScriptNoThrow').Replace("Join-Path `$PSScriptRoot 'FileTransferOps/LocalGuiWriterRequest.ps1'","Join-Path `$script:moduleRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1'")
Invoke-Expression $launch
$analysis=Definition $runner 'Invoke-RouteAcceptanceRetainedAnalysis'
$analysis=$analysis.Replace("'.\tools\FileTransfer-Ops.ps1'","(Join-Path `$script:moduleRoot 'tests/nLink.SmokeTests.Core/Support/FailedGuiDiagnosticAnalyzer.ps1')")
if(-not $LegacyAnalyzer){$analysis=$analysis.Replace("`$startInfo.FileName = 'powershell'","`$startInfo.FileName = (Get-Process -Id `$PID).Path")}
$analysis=$analysis.Replace('$analysisTimeoutSeconds = 3600','$analysisTimeoutSeconds = if($env:NLINK_FAILED_DIAGNOSTIC_TEST_OUTCOME -ceq ''timeout''){5}else{30}').Replace('$process.WaitForExit(5000)','$process.WaitForExit(50)')
Invoke-Expression $analysis
function Write-RouteAcceptanceFakeAuthoritativeSnapshotEvidence {param($ArtifactDir)}
function Test-RouteAcceptanceRetainedAnalysisCompletedProofArtifacts {param($ArtifactDir,$LiveRouteProofMode) return $true}
function Stop-RouteAcceptanceRetainedAnalysisProcess {param($Process) if(-not $Process.HasExited){$Process.Kill()};$Process.WaitForExit()}
function Write-RouteAcceptanceRetainedAnalysisTimeoutArtifacts {param($ArtifactDir,$LogPath,$TimeoutSeconds,$ProcessId,$StartedUtc,$LiveRouteProofMode)}
$script:RouteAcceptanceExecutionMode='live'
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID='gui-writer-parent-run'
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='gui-writer-parent-run:scenario:1'
$env:NLINK_FAILED_DIAGNOSTIC_TEST_REPO=$RepoRoot
function Capture([string]$Name,[string]$Gui='nonzero',[int]$Exit=9) {
    $dir=Join-Path $Directory $Name;[void][IO.Directory]::CreateDirectory($dir)
    $root=Join-Path $dir $Gui
    $success=$null;$failed=$null
    $arguments=@('-RepoRoot',$RepoRoot,'-Directory',$dir,'-GuiMode',$Gui,'-WrapperExit',[string]$Exit)
    if($Name -ceq 'mutated'){$arguments+='-MutateSource'}
    if($Name -ceq 'native-gui'){$arguments+='-FrontierLaunch'}
    $code=Invoke-RouteAcceptanceChildScriptNoThrow -ScriptPath (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FailedGuiDiagnosticWrapper.ps1') `
        -Arguments $arguments `
        -LocalWriterRelay ([ref]$success) -LocalWriterArtifactDir $root -FailedGuiCapture ([ref]$failed)
    if($code -ne $Exit){throw "Wrapper outcome changed: $Name/$code"}
    if($null -ne $success){throw 'Failed wrapper produced success ownership'}
    return $failed
}
$cap=Capture 'valid'
try {
    if($null -eq $cap -or $cap.GuiExitCode -ne 7){throw 'Failed GUI capture missing'}
    if($cap.GetType().GetConstructors().Length -or @($cap.GetType().GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count){throw 'Failed capture has public construction/mutation'}
    $root=Split-Path -Parent $cap.SourcePath
    if($cap.Matches('wrong-run',$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,$root)){throw 'Cross-run capture accepted'}
    if($null -ne $cap.TryPreparePrimary([guid]::NewGuid(),'wrong-run',$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,$root)){throw 'Cross-run dispatch accepted'}
    $forged=$cap | ConvertTo-Json | ConvertFrom-Json
    $rejected=$false
    try {Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $root -FailedGuiCapture $forged} catch {$rejected=$_.Exception.Message -like '*capture_invalid*'}
    if(-not $rejected){throw 'Serialized capture accepted'}
    $env:NLINK_FAILED_DIAGNOSTIC_TEST_OUTCOME='failed'
    $failure=''
    try {Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $root -FailedGuiCapture $cap} catch {$failure=$_.Exception.Message}
    if($failure -notlike '*exit code 42*'){throw "Original analyzer failure missing: $failure"}
    if(@(Get-Content (Join-Path $root 'stub-launches.txt')).Count -ne 1){throw 'Wrong analyzer launch count'}
    $rejected=$false
    try {Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $root -FailedGuiCapture $cap} catch {$rejected=$_.Exception.Message -like '*already_terminal*'}
    if(-not $rejected -or $null -ne $cap.TryPreparePrimary([guid]::NewGuid(),$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,$root)){throw 'Repeated dispatch accepted'}
    $manifest=[ordered]@{event='filetransfer_tuna_gui_direct_evidence_manifest';acceptanceRunId=$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID
        scenarioRunId=$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID;retainedAnalysisOwner='RouteAcceptance';guiExitCodeAvailable=$true;guiExitCode=7
        currentRuntimeProof=$false;artifactFinalized=$true;originalFailure='progress_timeout';correctnessVerdict='FAIL';retainedAnalysisStatus='not_started_due_to_gui_failure'}
    Write-FileTransferEvidenceAtomicJson -Path (Join-Path $root 'filetransfer-tuna-gui-evidence-manifest.json') -Value $manifest
    $updated=Update-FileTransferFailedGuiDiagnosticManifest -ArtifactDir $root -LiveRouteProofMode None -FailedCapture $cap -DiagnosticError $failure
    if($updated.currentRuntimeProof -or -not $updated.artifactFinalized -or $updated.originalFailure -cne 'progress_timeout' -or
        $updated.correctnessVerdict -cne 'FAIL' -or $updated.guiExitCode -ne 7 -or $updated.retainedAnalysisStatus -cne 'failed' -or $updated.retainedAnalysisExitCode -ne 42){throw 'Failed diagnostic changed original outcome'}
    $receiptPath=Join-Path $root 'filetransfer-retained-analysis-attempt-receipt.json'
    $receiptBytes=[IO.File]::ReadAllBytes($receiptPath)
    foreach($malformed in @('{','{}','{"attemptId":"foreign"}')) {
        [IO.File]::WriteAllText($receiptPath,$malformed)
        $updated=Update-FileTransferFailedGuiDiagnosticManifest -ArtifactDir $root -LiveRouteProofMode None -FailedCapture $cap -DiagnosticError invalid_receipt
        if($updated.currentRuntimeProof -or $updated.retainedAnalysisReceiptValid -or $updated.retainedAnalysisCompleted -or $updated.retainedAnalysisStatus -cne 'failed'){throw 'Invalid receipt accepted'}
    }
    # Losing a disk receipt must not permit a second in-memory dispatch.
    Remove-Item -LiteralPath $receiptPath
    $rejected=$false
    try {Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $root -FailedGuiCapture $cap} catch {$rejected=$_.Exception.Message -like '*claim_unavailable*'}
    if(-not $rejected -or @(Get-Content (Join-Path $root 'stub-launches.txt')).Count -ne 1){throw 'Receipt loss permitted replay'}
    [IO.File]::WriteAllBytes($receiptPath,$receiptBytes)
    $cap.Dispose()
    if($cap.IsCurrent){throw 'Disposed capture stayed current'}
} finally {if($null -ne $cap){$cap.Dispose()}}
foreach($case in @(@('gui-success','valid',9),@('wrapper-success','nonzero',0),@('unknown-exit','unknown-exit',9),@('unsealed','no-drain',9),@('output-failure','output-fails',9),@('mutated','nonzero',9))) {
    $bad=Capture $case[0] $case[1] $case[2]
    if($null -ne $bad){$bad.Dispose();throw "Invalid failed capture admitted: $($case[0])"}
}

$cap=Capture 'native-gui'
try {
    if($null -eq $cap){throw 'Native GUI startup prevented sealed failed-capture adoption'}
    $root=Split-Path -Parent $cap.SourcePath
    $launchFailure=$analysis.Replace($(if($LegacyAnalyzer){"`$startInfo.FileName = 'powershell'"}else{'$startInfo.FileName = (Get-Process -Id $PID).Path'}),"`$startInfo.FileName = (Join-Path `$script:moduleRoot 'missing-failed-diagnostic-test.exe')")
    Invoke-Expression $launchFailure
    $failed=$false
    try {Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $root -FailedGuiCapture $cap} catch {$failed=$true}
    if(-not $failed -or [string]::IsNullOrWhiteSpace($cap.AnalysisAttemptId) -or
        $null -ne $cap.TryPreparePrimary([guid]::NewGuid(),$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,$root)){throw 'Launch failure did not consume dispatch'}
    $manifest.guiExitCode=7
    Write-FileTransferEvidenceAtomicJson -Path (Join-Path $root 'filetransfer-tuna-gui-evidence-manifest.json') -Value $manifest
    $updated=Update-FileTransferFailedGuiDiagnosticManifest -ArtifactDir $root -LiveRouteProofMode None -FailedCapture $cap -DiagnosticError launch_failed
    if($updated.retainedAnalysisStatus -cne 'failed' -or $updated.retainedAnalysisCompleted -or $updated.currentRuntimeProof -or $updated.retainedAnalysisExitCodeAvailable){throw 'Launch failure was mislabeled'}
} finally {if($null -ne $cap){$cap.Dispose()};Invoke-Expression $analysis}

# Actual admission guards, evaluated on invented ownership states before any
# parser/index/coordinator work. Required ownership must never fall back to disk.
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1'),[ref]$tokens,[ref]$errors)
foreach($message in @('failed_gui_diagnostic_source_owner_unavailable','failed_gui_diagnostic_source_read_incomplete')) {
    $guard=@($ast.FindAll({param($n)$n -is [Management.Automation.Language.IfStatementAst] -and $n.Extent.Text.StartsWith('if ($sourceRequired -and') -and $n.Extent.Text.Contains($message)},$true))
    if($guard.Count -ne 1){throw 'Required-source guard missing or ambiguous'}
    $sourceRequired=$true;$retainedSourceOwner=$null;$retainedSourceReader=$null
    if($message -like '*read_incomplete'){$retainedSourceOwner=[pscustomobject]@{};$retainedSourceOwner | Add-Member ScriptMethod OwnsCompletedSource {param($reader)return $false}}
    $rejected=$false
    try {Invoke-Expression $guard[0].Extent.Text} catch {$rejected=$_.Exception.Message -ceq $message}
    if(-not $rejected){throw 'Required-source rejection guard failed'}
    $sourceRequired=$false
    Invoke-Expression $guard[0].Extent.Text
}

# Exercise both actual scenario branches; substitute only the GUI executable's
# wrapper at the existing launch seam. The parent and primary dispatch code run.
Invoke-Expression (Definition $runner 'Invoke-TunaRouteAcceptanceRun')
$originalChild=${function:Invoke-RouteAcceptanceChildScriptNoThrow}
function Invoke-RouteAcceptanceChildScriptNoThrow {
    param($ScriptPath,$Arguments,$LocalWriterRelay,$LocalWriterArtifactDir,$FrontierRelayCustody,$FailedGuiCapture)
    & $originalChild -ScriptPath (Join-Path $script:moduleRoot 'tests/nLink.SmokeTests.Core/Support/FailedGuiDiagnosticWrapper.ps1') `
        -Arguments @('-RepoRoot',$script:moduleRoot,'-Directory',(Split-Path -Parent $LocalWriterArtifactDir),'-GuiMode','nonzero','-WrapperExit','9') `
        -LocalWriterRelay $LocalWriterRelay -LocalWriterArtifactDir $LocalWriterArtifactDir -FailedGuiCapture $FailedGuiCapture -FrontierRelayCustody $FrontierRelayCustody
}
$TimeoutSeconds=30;$ProgressTimeoutSeconds=10
foreach($scenario in @(@('cycle','live-regular-activation-cycle','RegularActivationCycle','256MiB','on,off,on,off','completed'),
    @('second','live-reactivation-second-transfer','None','128MiB','off,on','timeout'))) {
    $root=Join-Path (Join-Path $Directory $scenario[0]) 'nonzero';[void][IO.Directory]::CreateDirectory($root)
    $manifest=[ordered]@{event='filetransfer_tuna_gui_direct_evidence_manifest';acceptanceRunId=$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID
        scenarioRunId=$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID;retainedAnalysisOwner='RouteAcceptance';guiExitCodeAvailable=$true;guiExitCode=7
        currentRuntimeProof=$false;artifactFinalized=$true;originalFailure='progress_timeout';correctnessVerdict='FAIL';retainedAnalysisStatus='not_started_due_to_gui_failure'}
    Write-FileTransferEvidenceAtomicJson -Path (Join-Path $root 'filetransfer-tuna-gui-evidence-manifest.json') -Value $manifest
    $env:NLINK_FAILED_DIAGNOSTIC_TEST_OUTCOME=$scenario[5]
    $failure=''
    try {Invoke-TunaRouteAcceptanceRun -RepoRoot $RepoRoot -ArtifactDir $root -ResolvedExePath unused -ResolvedWalletPath unused `
        -ResolvedSidecarPath unused -EffectiveWalletPassword unused -RouteMode $scenario[1] -Fault switch-off -PayerMode helpee `
        -PayloadSize $scenario[3] -LiveToggleSequence $scenario[4] -LiveProofMode $scenario[2] -CaptureLocalPublicationDiagnostics} catch {$failure=$_.Exception.Message}
    if($failure -notlike '*failed with exit code 9; diagnostic outcome*'){throw "GUI outcome changed: $failure"}
    $result=Get-Content (Join-Path $root 'filetransfer-tuna-gui-evidence-manifest.json') -Raw | ConvertFrom-Json
    $expected=if($scenario[5] -ceq 'timeout'){'timed_out'}else{'completed'}
    if($result.retainedAnalysisStatus -cne $expected -or $result.currentRuntimeProof -or -not $result.artifactFinalized -or
        $result.originalFailure -cne 'progress_timeout' -or $result.correctnessVerdict -cne 'FAIL' -or $result.guiExitCode -ne 7){throw "Diagnostic reporting drift: $($result | ConvertTo-Json -Compress)"}
    if(@(Get-Content (Join-Path $root 'stub-launches.txt')).Count -ne 1){throw 'Scoped diagnostic launched more than once'}
}
'failed-gui-diagnostic-ok'
