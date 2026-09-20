param([string]$ExePath,[int]$TimeoutSeconds)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$settings=[IO.File]::ReadAllText($env:NLINK_GUI_WRITER_TEST_SETTINGS) | ConvertFrom-Json
$RepoRoot=[string]$settings.RepoRoot; $Directory=[string]$settings.Directory; $mode=[string]$settings.Mode
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$nativeLaunch=$null
if($settings.PSObject.Properties.Name -contains 'FrontierLaunch' -and $settings.FrontierLaunch) {
    if($env:NLINK_GUI_FRONTIER_START_V1 -cne '1') {throw 'Original GUI parent omitted readiness selector'}
    $nativeLaunch=[NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncherReady($env:NLINK_GUI_WRITER_REQUEST,
        [guid]$env:NLINK_GUI_WRITER_REQUEST_ID,$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,$Directory)
    if($null -eq $nativeLaunch -or -not $nativeLaunch.IsCurrent) {throw 'Original GUI parent failed early native attachment'}
} elseif(-not [string]::IsNullOrEmpty($env:NLINK_GUI_FRONTIER_START_V1)) {throw 'Default GUI parent inherited native readiness'}
. (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) { throw 'Actual finalizer parse failed' }
$definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq 'Write-FileTransferGuiLogLedgerEvidence'},$true))
if($definition.Count -ne 1) { throw 'Actual finalizer ambiguous' }
Invoke-Expression $definition[0].Extent.Text
function Sync-GuiSmokeAppLogLedger { }
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'No authority evaluation allowed' }
function Update-FileTransferGuiEvidenceFinalizationStage {
    param($Stage,$CapturePath,$CaptureSha256,$CaptureLineCount)
    # A called formatter/helper cannot inherit the finalizer's private issuer.
    if($null -ne (Get-Variable localWriterIssuer -ErrorAction SilentlyContinue)) { throw 'Issuer leaked into child scope' }
    if($mode -ceq 'final-stage-fails' -and $Stage -ceq 'writer_validation_completed') { throw 'injected primary stage failure' }
    if($mode -ceq 'cancel-before-complete' -and $Stage -ceq 'writer_validation_completed') {
        $closed=[Threading.EventWaitHandle]::OpenExisting($env:NLINK_GUI_WRITER_REQUEST+'.closed')
        try { [void]$closed.Set() } finally { $closed.Dispose() }
    }
    $script:stages.Add([string]$Stage)
}
$script:stages=[Collections.Generic.List[string]]::new()
$source=Join-Path $Directory 'append.log'
$script:GuiSmokeAppLogLedger=New-GuiSmokeLogLedgerState
$maximumBytes=1048576L
if($settings.PSObject.Properties.Name -contains 'LiveScaleFixture' -and $settings.LiveScaleFixture) { $maximumBytes=268435456L }
Enable-GuiSmokeAppendOnlyLogLedgerState -State $script:GuiSmokeAppLogLedger -Path $source -MaximumBytes $maximumBytes
$initial=Get-GuiSmokeLogLedgerSnapshot -State $script:GuiSmokeAppLogLedger -Bookmark 0
$lines=[Collections.Generic.List[string]]::new()
for($i=1;$i -le 3;$i++) {
    $event=if($i -eq 1) { 'operational_log_writer_started' } elseif($i -eq 3 -and $mode -cne 'no-drain') { 'operational_log_writer_drained' } else { 'filetransfer_data_path_send_result' }
    $lines.Add("event=$event; acceptance_run_id=$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID; scenario_run_id=$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID; log_writer_id=writer-a; log_writer_sequence=$i; log_writer_batch_generation=$i; log_writer_batch_first_sequence=$i; log_writer_batch_last_sequence=$i; log_writer_attempt_utc_ticks=$i")
}
[IO.File]::WriteAllLines($source,$lines.ToArray(),[Text.UTF8Encoding]::new($false))
if($settings.PSObject.Properties.Name -contains 'GeneratedSource' -and $settings.GeneratedSource) {
    [IO.File]::WriteAllLines($source,[IO.File]::ReadAllLines($settings.GeneratedSource),[Text.UTF8Encoding]::new($false))
}
Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger -Paths @($source)
if($mode -ceq 'cursor-failure') { $script:GuiSmokeAppLogLedger.FailureCode='injected_cursor_failure' }
$markerRoot=Join-Path $Directory 'process-output'; [void][IO.Directory]::CreateDirectory($markerRoot)
$marker=Join-Path $markerRoot 'writer.stderr.log'; [IO.File]::WriteAllText($marker,'')
$script:GuiSmokeProcessOutputFiles=[Collections.Generic.List[string]]::new()
if($mode -cne 'no-marker') { $script:GuiSmokeProcessOutputFiles.Add($marker) }
if($mode -ceq 'wrong-request') { $env:NLINK_GUI_WRITER_REQUEST_ID=[guid]::NewGuid().ToString('D') }
if($mode -ceq 'wrong-scope') { $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID+=':wrong' }
if($mode -cin @('worker-start','parent-start','wrong-worker')) {
    $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($env:NLINK_GUI_WRITER_REQUEST); $view=$map.CreateViewAccessor()
    try {
        if($mode -ceq 'wrong-worker') { $view.Write(16L,0) }
        else { $view.Write($(if($mode -ceq 'worker-start') { 40L } else { 8L }),0L) }
    } finally { $view.Dispose(); $map.Dispose() }
}
$capture=$null; $failure=''
try { Write-FileTransferGuiLogLedgerEvidence -ArtifactDir $Directory -Bookmark 0 -InitialSnapshot $initial -LocalNativeWriterCapture ([ref]$capture) }
catch { $failure=$_.Exception.Message }
if($mode -ceq 'final-stage-fails') { if($failure -cne 'injected primary stage failure') { throw 'Primary failure changed' } }
elseif($failure) { throw $failure }
# Native provenance alone exposes neither the issuer nor a completion factory.
if($null -ne $capture -and @($capture.PSObject.Properties | Where-Object { $_.Value -is [NLink.Tools.LocalGuiWriterRequest+Finalizer] }).Count) { throw 'Issuer leaked in native capture' }
if($null -ne (Get-Variable localWriterIssuer -ErrorAction SilentlyContinue)) { throw 'Issuer leaked after finalization' }
$second=[NLink.Tools.LocalGuiWriterRequest]::TryAttach([string]$env:NLINK_GUI_WRITER_REQUEST,
    $(if($env:NLINK_GUI_WRITER_REQUEST_ID) { [guid]$env:NLINK_GUI_WRITER_REQUEST_ID } else { [guid]::Empty }),
    $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,$Directory)
if($null -ne $second) { $second.Dispose(); throw 'Second issuer attached' }
if($null -ne $nativeLaunch) {
    if($nativeLaunch.IsCurrent) {throw 'Original finalizer failed to retire early native ownership'}
    $nativeLaunch.Dispose()
}
$result=@{NativeCapture=$null -ne $capture; Stages=$script:stages.ToArray(); PrimaryFailure=$failure; PrivateIssuerNotExposed=$true}
[IO.File]::WriteAllText((Join-Path $Directory 'worker-result.json'),($result | ConvertTo-Json -Depth 4))
if($mode -ceq 'nonzero') { exit 7 }
if($mode -ceq 'timeout') { [Threading.Thread]::Sleep(30000) }
exit 0
