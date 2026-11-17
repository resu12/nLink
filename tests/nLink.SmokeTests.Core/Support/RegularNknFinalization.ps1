param([string]$RepoRoot,[string]$Directory,[string]$Mode)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferRouteAcceptanceEvidence.ps1')
function Import-TestFunction([string]$Path,[string]$Name) {
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($Path,[ref]$tokens,[ref]$errors)
    if($errors.Count) {throw "Parse failed: $Path"}
    $function=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $Name},$true))
    if($function.Count -ne 1) {throw "Function missing: $Name"}
    return $function[0].Extent.Text
}
$nkn=Join-Path $RepoRoot 'tools/Run-FileTransferNknSoak.ps1'
foreach($name in @('Invoke-FileTransferGuiSmokeWithTimeout','Stop-FileTransferProcessTree','Add-FileTransferLiveNknSyntheticEvidence','Clear-FileTransferNknRunArtifacts')) {
    Invoke-Expression (Import-TestFunction $nkn $name)
}
if($Mode.StartsWith('reuse:',[StringComparison]::Ordinal)) {
    $marker=Join-Path $Directory $Mode.Substring(6)
    $capture=Join-Path $Directory 'filetransfer-retained-log-slice.log'
    $stdout=Join-Path $Directory 'gui-smoke-stdout.log'
    foreach($path in @($marker,$capture,$stdout)) { [IO.File]::WriteAllText($path,'preserve-original-bytes') }
    $failure=''
    try { Clear-FileTransferNknRunArtifacts -ArtifactDir $Directory } catch { $failure=$_.Exception.Message }
    if($failure -notmatch 'Refusing to reuse GUI finalization artifacts') {throw 'artifact reuse was not rejected before cleanup'}
    foreach($path in @($marker,$capture,$stdout)) {
        if(-not (Test-Path -LiteralPath $path) -or [IO.File]::ReadAllText($path) -cne 'preserve-original-bytes') {
            throw 'original evidence changed during reuse rejection'
        }
    }
    Write-Output 'regular-finalization-ok'
    exit 0
}
$parent=Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'
foreach($name in @('Write-RouteAcceptanceDirectAttemptManifest','Write-RouteAcceptanceAtomicJson')) {
    Invoke-Expression (Import-TestFunction $parent $name)
}
$env:NLINK_TEST_REGULAR_REPO=$RepoRoot
$env:NLINK_TEST_REGULAR_DIRECTORY=$Directory
$env:NLINK_TEST_REGULAR_MODE=$Mode
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID='regular-finalizer-run'
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='regular-finalizer-run:regular:1'
$env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH='previous-state'
$env:NLINK_FILETRANSFER_GUI_SCENARIO_DEADLINE_REQUEST_PATH='previous-request'
$timeoutSeconds=30
if($Mode -ceq 'hard-timeout') {
    $timeoutSeconds=1
    $env:NLINK_FILETRANSFER_GUI_FINALIZATION_GRACE_SECONDS_TEST_ONLY='1'
}
$exitCode=$null
$wrapperFailure=''
try {
    $exitCode=Invoke-FileTransferGuiSmokeWithTimeout `
        -GuiSmokeScript (Join-Path $PSScriptRoot 'RegularNknGuiChild.ps1') -ResolvedExePath 'fixture.exe' `
        -ArtifactDir $Directory -TimeoutSeconds $timeoutSeconds
} catch { $wrapperFailure=$_.Exception.Message }
if($Mode -ceq 'wrong-state') {
    if($wrapperFailure -notmatch 'regular_gui_finalization_identity_mismatch') {throw 'wrong-attempt journal was accepted'}
} elseif($wrapperFailure -ne '') {throw $wrapperFailure}
if($env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -cne 'previous-state' -or
    $env:NLINK_FILETRANSFER_GUI_SCENARIO_DEADLINE_REQUEST_PATH -cne 'previous-request') {throw 'wrapper environment not restored'}
$statePath=Join-Path $Directory 'filetransfer-gui-evidence-finalization-state.json'
if(-not (Test-Path -LiteralPath $statePath)) {throw 'missing original wrapper finalization state'}
$state=Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
if($Mode -ceq 'hard-timeout') {
    if($exitCode -ne 124 -or -not $state.timedOut -or $state.lastCompletedStage -ceq 'gui_child_exited' -or
        -not (Test-Path -LiteralPath (Join-Path $Directory 'filetransfer-gui-evidence-finalization-timeout.json'))) {throw 'hard timeout was finalized'}
    if(Get-Process -Id $state.guiChildProcessId -ErrorAction SilentlyContinue) {throw 'timed-out child survived'}
}
$script:RouteAcceptanceRunId=$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID
$attempt=[pscustomobject]@{ArtifactDir=$Directory;ScenarioRunId=$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID}
Write-RouteAcceptanceDirectAttemptManifest -Attempt $attempt
$manifest=Get-Content -LiteralPath (Join-Path $Directory 'filetransfer-route-attempt-evidence-manifest.json') -Raw | ConvertFrom-Json
if($Mode -in @('hard-timeout','child-timeout','wrong-state','finalizer-failure','both-failures')) {
    if($manifest.currentRuntimeProof) {throw 'incomplete or wrong-attempt evidence accepted'}
    if($Mode -ceq 'child-timeout' -and ($exitCode -ne 124 -or -not $state.timedOut -or
        $state.lastCompletedStage -ceq 'gui_child_exited')) {throw 'child timeout was cleared by normal exit'}
    if($Mode -in @('finalizer-failure','both-failures') -and ($exitCode -eq 0 -or
        $state.childLastCompletedStage -ceq 'writer_validation_completed')) {throw 'failed finalizer marked complete'}
    if($Mode -ceq 'both-failures' -and (Get-Content -LiteralPath (Join-Path $Directory 'gui-smoke-stderr.log') -Raw) -notmatch 'original-scenario-failure') {
        throw 'finalizer error replaced original scenario failure'
    }
    Write-Output 'regular-finalization-ok'
    exit 0
}
$ledger=Get-Content -LiteralPath (Join-Path $Directory 'filetransfer-gui-log-ledger.json') -Raw | ConvertFrom-Json
if($state.lastCompletedStage -cne 'gui_child_exited' -or $state.timedOut -or
    $state.childLastCompletedStage -cne 'writer_validation_completed') {throw 'producer/wrapper stage handoff failed'}
if($Mode -ceq 'scenario-failure') {
    if($exitCode -eq 0 -or (Get-Content -LiteralPath (Join-Path $Directory 'gui-smoke-stderr.log') -Raw) -notmatch 'original-scenario-failure') {throw 'original scenario failure lost'}
} elseif($Mode -ceq 'success' -and $exitCode -ne 0) {throw 'successful child failed'}
$shouldPass=$Mode -in @('success','scenario-failure')
if(($ledger.writerIntegrityVerdict -ceq 'pass') -ne $shouldPass) {throw "writer verdict unexpected: $($ledger.writerIntegrityReasons -join ',')"}
if($manifest.currentRuntimeProof -ne $shouldPass) {throw "parent proof mismatch: $($manifest | ConvertTo-Json -Compress)"}
$capture=Join-Path $Directory 'filetransfer-retained-log-slice.log'
$before=Get-FileTransferEvidenceSha256 -Path $capture
if($before -cne [string]$ledger.analyzerInput.captureSha256 -or
    (Get-FileTransferEvidenceSha256 -Path ([string]$ledger.retainedSlice.path)) -cne [string]$state.captureSha256) {
    throw 'sealed producer hashes do not match persisted bytes'
}
Add-FileTransferLiveNknSyntheticEvidence -ArtifactDir $Directory -CyclesRequested 1
if((Get-FileTransferEvidenceSha256 -Path $capture) -cne $before) {throw 'sealed analyzer input mutated'}
Write-Output 'regular-finalization-ok'
