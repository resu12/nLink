param([string]$ExePath, [int]$TimeoutSeconds)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$RepoRoot=$env:NLINK_TEST_REGULAR_REPO
$Directory=$env:NLINK_TEST_REGULAR_DIRECTORY
$Mode=$env:NLINK_TEST_REGULAR_MODE
if($Mode -ceq 'hard-timeout') { Start-Sleep -Seconds 30; exit 0 }
. (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) {throw 'GUI script parse failed'}
foreach($name in @('Run-FileTransferNknSoakCore','Write-FileTransferGuiLogLedgerEvidence',
    'Get-GuiSmokeLogWriterChronology','Copy-FileTransferLiveLogSlice','Get-FileSha256Hex',
    'Write-GuiSmokeAtomicJson','Update-FileTransferGuiEvidenceFinalizationStage')) {
    $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
    if($definition.Count -ne 1) {throw "Function missing: $name"}
    Invoke-Expression $definition[0].Extent.Text
}
$script:GuiSmokeAppLogLedger=New-GuiSmokeLogLedgerState
$script:logPath=Join-Path $Directory 'fixture.log'
Enable-GuiSmokeAppendOnlyLogLedgerState -State $script:GuiSmokeAppLogLedger -Path $script:logPath
$outputDir=Join-Path $Directory 'process-output'
[void][IO.Directory]::CreateDirectory($outputDir)
$outputPath=Join-Path $outputDir 'fixture.stderr.log'
[IO.File]::WriteAllText($outputPath,'',[Text.UTF8Encoding]::new($false))
$script:GuiSmokeProcessOutputFiles=@($outputPath)
$script:drained=$false
function Append-WriterLine([string]$Writer,[int]$Sequence,[string]$Event) {
    $scenario=if($Mode -ceq 'wrong-writer' -and $Writer -ceq 'helper') {'other-attempt'} else {$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID}
    $line="event=$Event; acceptance_run_id=$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID; scenario_run_id=$scenario; log_writer_id=$Writer; log_writer_sequence=$Sequence; log_writer_batch_generation=$Sequence; log_writer_batch_first_sequence=$Sequence; log_writer_batch_last_sequence=$Sequence; log_writer_attempt_utc_ticks=$($Sequence*100); transfer_id=fixture"
    [IO.File]::AppendAllText($script:logPath,$line+[Environment]::NewLine,[Text.UTF8Encoding]::new($false))
}
function Read-AppLogLinesSafe {param([string]$Path) return @(Get-Content -LiteralPath $Path)}
function Sync-GuiSmokeAppLogLedger {
    param([switch]$IgnoreScenarioDeadline)
    if($IgnoreScenarioDeadline -and -not $script:drained) {throw 'capture preceded output drain'}
    Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger -Paths @($script:logPath)
}
function Get-AppLogBookmark { Sync-GuiSmokeAppLogLedger; Get-GuiSmokeLogLedgerBookmark -State $script:GuiSmokeAppLogLedger }
function Get-AppLogLinesAfterBookmark {param([int]$Bookmark) Sync-GuiSmokeAppLogLedger; Get-GuiSmokeLogLedgerLinesAfterBookmark -State $script:GuiSmokeAppLogLedger -Bookmark $Bookmark}
function Reset-ScenarioContext {param($Context)}
function Get-FileTransferSoakArtifactDir {return $Directory}
function Get-FileTransferSoakPayloadSizes {return 1024L}
function Get-FileTransferSoakCycleCount {param($PayloadSizes) return 1}
function Get-FileTransferSoakDirection {return 'helpee-to-helper'}
function Get-FileTransferSoakSeed {return 1}
function Get-FileTransferSoakCycleTimeoutMs {return 1000}
function Get-FileTransferSoakStartupTimeoutMs {return 1000}
function Get-FileTransferSoakProgressTimeoutMs {return 1000}
function Get-FileTransferMixedScreenShareWarmupTimeoutMs {return 1000}
function Get-FileTransferCycleDirection {param($Direction,$CycleIndex) return $Direction}
function Start-HelpeeFlow {param($Context) Append-WriterLine helpee 1 operational_log_writer_started}
function Start-HelperFlow {param($Context) Append-WriterLine helper 1 operational_log_writer_started}
function Connect-HelperAndHelpee {param($Context)}
function Invoke-FileTransferLiveCycle {
    param($Context,$ArtifactDir,$AutopickPath,$CycleIndex,$PayloadSizeBytes,$Direction,$Seed,$TimeoutMs,$StartupTimeoutMs,$ProgressTimeoutMs)
    Append-WriterLine helpee 2 file_transfer_outbound_terminal
    Append-WriterLine helper 2 file_transfer_inbound_terminal
    if($Mode -in @('finalizer-failure','both-failures')) {
        [IO.File]::WriteAllText((Join-Path $Directory 'filetransfer-gui-log-ledger.json'),'{}',[Text.UTF8Encoding]::new($false))
    }
    if($Mode -in @('scenario-failure','both-failures')) {throw 'original-scenario-failure'}
}
function Cleanup-ProcessesWithOperationalLogDrain {
    param($Processes,$ExePath,[ref]$CleanupDiagnostic)
    Append-WriterLine helpee 3 operational_log_writer_drained
    Append-WriterLine helper 3 operational_log_writer_drained
}
function Wait-GuiSmokeProcessOutputCaptureDrain {
    param($Processes)
    $script:drained=$true
    if($Mode -ceq 'drain-failure') {throw 'fixture-output-drain-failed'}
}
$context=[pscustomobject]@{Processes=[Collections.Generic.List[object]]::new();ExePath=$ExePath}
$env:NLINK_FEATURE_SCREENCAP_SCAFFOLD='original-scaffold'
$env:NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT='original-inbound'
try { Run-FileTransferNknSoakCore -Context $context -Mixed:$false }
finally {
    if($env:NLINK_FEATURE_SCREENCAP_SCAFFOLD -cne 'original-scaffold' -or
        $env:NLINK_FILE_TRANSFER_TEST_INBOUND_ROOT -cne 'original-inbound') {throw 'scenario environment not restored'}
}
if($Mode -in @('wrong-state','child-timeout')) {
    $state=Get-Content -LiteralPath $env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -Raw | ConvertFrom-Json
    if($Mode -ceq 'wrong-state') { $state.scenarioRunId='other-attempt' }
    else { $state.timedOut=$true }
    Write-GuiSmokeAtomicJson -Path $env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -Value $state
}
