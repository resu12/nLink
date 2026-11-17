param([string]$RepoRoot,[string]$Directory,[string]$ProcessorVersion='current')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$missingEntry=$ProcessorVersion -ceq 'missing-entry'
if($missingEntry) {
    # Test-only preloaded API-absence control. The production file is untouched.
    $code=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tools/GuiSmokeLogLedgerStreamingProcessor.cs'))
    $needle='public static GuiSmokeLedgerStreamingResult ProcessWithNativeCapture('
    if($code.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) { throw 'Optional native entry seam changed' }
    Add-Type -TypeDefinition ($code.Replace($needle,'internal static GuiSmokeLedgerStreamingResult ProcessWithNativeCapture('))
    if($null -ne [NLink.Tools.GuiSmokeLogLedgerStreamingProcessor].GetMethod('ProcessWithNativeCapture')) { throw 'Missing-entry control still exposed optional method' }
}
. (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) { throw 'GUI finalizer parse failed' }
$definition=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq 'Write-FileTransferGuiLogLedgerEvidence'},$true))
if($definition.Count -ne 1) { throw 'Actual finalizer missing or ambiguous' }
Invoke-Expression $definition[0].Extent.Text
# No GUI: use the actual append-only ledger/snapshot/diagnostics/validation code.
# The source is already frozen and there are no process-output producers to drain.
function Sync-GuiSmokeAppLogLedger { }
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Writer provenance must not evaluate authority' }
function Update-FileTransferGuiEvidenceFinalizationStage {
    param($Stage,$CapturePath,$CaptureSha256,$CaptureLineCount)
    if($script:failFinalStage -and $Stage -ceq 'writer_validation_completed') { throw 'injected final primary stage failure' }
}
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID='writer-finalizer-run'
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='writer-finalizer-run:scenario:1'
Remove-Item Env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -ErrorAction SilentlyContinue
$rows=[Collections.Generic.List[object]]::new()
$scenarios=if($missingEntry) { @('valid') } else { @('valid','no-drain','no-marker-file','unresolved-marker','cursor-failure') }
foreach($scenario in $scenarios) {
    $sourceDir=Join-Path $Directory $scenario; [void][IO.Directory]::CreateDirectory($sourceDir)
    $source=Join-Path $sourceDir 'append.log'
    $lines=[Collections.Generic.List[string]]::new()
    for($i=1;$i -le 3;$i++) {
        $event=if($i -eq 1) { 'operational_log_writer_started' } elseif($i -eq 3 -and $scenario -cne 'no-drain') { 'operational_log_writer_drained' } else { 'filetransfer_data_path_send_result' }
        $lines.Add("event=$event; acceptance_run_id=$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID; scenario_run_id=$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID; log_writer_id=writer-a; log_writer_sequence=$i; log_writer_batch_generation=$i; log_writer_batch_first_sequence=$i; log_writer_batch_last_sequence=$i; log_writer_attempt_utc_ticks=$i")
    }
    $script:GuiSmokeAppLogLedger=New-GuiSmokeLogLedgerState
    Enable-GuiSmokeAppendOnlyLogLedgerState -State $script:GuiSmokeAppLogLedger -Path $source -MaximumBytes 1048576
    $initial=Get-GuiSmokeLogLedgerSnapshot -State $script:GuiSmokeAppLogLedger -Bookmark 0
    [IO.File]::WriteAllLines($source,$lines.ToArray(),[Text.UTF8Encoding]::new($false))
    Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger -Paths @($source)
    if($scenario -ceq 'cursor-failure') { $script:GuiSmokeAppLogLedger.FailureCode='injected_cursor_failure' }
    $modes=if($scenario -ceq 'valid' -and -not $missingEntry) { @('baseline','capture','assigned','incompatible','final-stage-fails') } else { @('baseline','capture') }
    $baseline=$null; $baselineHashes=$null
    foreach($mode in $modes) {
        $output=Join-Path $sourceDir $mode; $markerRoot=Join-Path $output 'process-output'; [void][IO.Directory]::CreateDirectory($markerRoot)
        $markerPath=Join-Path $markerRoot 'writer.stderr.log'
        $marker=if($scenario -ceq 'unresolved-marker') {
            "event=operational_log_write_deferred; acceptance_run_id=$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID; scenario_run_id=$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID; log_writer_id=writer-a; batch_generation=2; first_sequence=2; last_sequence=2; line_count=1; payload_sha256=$('0'*64); attempt_utc_ticks=2; attempt_count=1; failure_code=mutex_timeout"
        } else { '' }
        [IO.File]::WriteAllText($markerPath,$marker,[Text.UTF8Encoding]::new($false))
        $script:GuiSmokeProcessOutputFiles=[Collections.Generic.List[string]]::new()
        if($scenario -cne 'no-marker-file') { $script:GuiSmokeProcessOutputFiles.Add($markerPath) }
        $capture=$null; [uri]$incompatible=$null
        if($mode -ceq 'assigned') { $capture='preassigned' }
        $args=@{ArtifactDir=$output;Bookmark=0;InitialSnapshot=$initial}
        if($mode -cne 'baseline') { $args.LocalNativeWriterCapture=[ref]$capture }
        if($mode -ceq 'incompatible') { $args.LocalNativeWriterCapture=[ref]$incompatible }
        $script:failFinalStage=$mode -ceq 'final-stage-fails'
        if($script:failFinalStage) { $env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH=Join-Path $output 'test-stage.json' }
        $failure=''
        try { Write-FileTransferGuiLogLedgerEvidence @args } catch { $failure=$_.Exception.Message }
        Remove-Item Env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -ErrorAction SilentlyContinue
        if($script:failFinalStage) { if($failure -cne 'injected final primary stage failure') { throw 'Original stage failure changed' } }
        elseif($failure) { throw "$scenario/$mode : $failure" }
        $ledgerPath=Join-Path $output 'filetransfer-gui-log-ledger.json'
        $ledgerText=[IO.File]::ReadAllText($ledgerPath)
        $ledger=$ledgerText | ConvertFrom-Json
        $expected=$scenario -ceq 'valid' -and $mode -ceq 'capture' -and -not $missingEntry
        if($mode -ceq 'assigned') { if($capture -cne 'preassigned') { throw 'Existing output owner replaced' } }
        elseif(($null -ne $capture) -ne $expected) { throw "Wrong finalizer capture: $scenario/$mode : $($ledger.cursorIntegrityReasons -join ',')" }
        if($null -ne $incompatible) { throw 'Typed optional reference overwritten' }
        if($ledger.writerIntegrityVerdict -cne $(if($scenario -cin @('valid','cursor-failure')) { 'pass' } else { 'fail' }) -or
            $ledger.cursorIntegrityVerdict -cne $(if($scenario -ceq 'valid') { 'pass' } else { 'fail' })) { throw "Primary validation drift: $scenario" }
        # The same original ledger state/input is used for both attempts. Only
        # output-directory paths differ in the unchanged primary ledger JSON.
        $normalized=$ledgerText.Replace($output,'CASE').Replace($output.Replace('\','\\'),'CASE')
        $hashes=@(foreach($name in @('filetransfer-gui-log-ledger-slice.log','filetransfer-gui-log-writer-evidence-slice.log',
            'filetransfer-gui-semantic-evidence-slice.log','filetransfer-retained-log-slice.log')) {
            $hash=[NLink.Tools.LocalInputReadLease]::HashAndHold((Join-Path $output $name))
            try { $hash.Sha256 } finally { $hash.Lease.Dispose() }
        })
        if($mode -ceq 'baseline') { $baseline=$normalized; $baselineHashes=$hashes }
        elseif($normalized -cne $baseline -or ($hashes -join '|') -cne ($baselineHashes -join '|')) { throw "Optional native capture changed primary artifacts: $scenario/$mode" }
        if($expected) {
            if($capture.Writer.Sha256 -cne $ledger.writerEvidenceSlice.sha256 -or $capture.Writer.LineCount -ne $ledger.writerEvidenceSlice.lineCount -or
                $capture.Semantic.Sha256 -cne $ledger.analyzerInput.captureSha256) { throw 'Native output did not match actual finalizer artifacts' }
        }
        $rows.Add([ordered]@{Scenario=$scenario;Mode=$mode;CaptureReturned=$expected;PrimaryWriterVerdict=$ledger.writerIntegrityVerdict
            PrimaryCursorVerdict=$ledger.cursorIntegrityVerdict;PrimaryArtifactsPreserved=$true})
    }
}
'native-writer-capture='+(ConvertTo-Json -InputObject ([ordered]@{FinalizerAttempts=$rows.Count;Rows=$rows.ToArray();AuthorityEvaluations=0;OptionalEntryMissing=$missingEntry
    FinalizerCompletionIssued=$false;ParentWorkerBound=$false;PublicationCompletionIssued=$false;LiveCaller=$false}) -Depth 5 -Compress)
