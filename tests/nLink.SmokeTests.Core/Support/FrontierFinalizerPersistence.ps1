# The shared fixture created the real ordinary append-only ledger through its
# original enable/sync functions, alongside the native origin and real app pair.
$mode=$FinalizerCase.Substring('persistence-'.Length)
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID='cleanup-adoption-run'
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='cleanup-adoption-scope'
Remove-Item Env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH -ErrorAction SilentlyContinue
if($mode.StartsWith('handoff-')) {
    $env:NLINK_FILETRANSFER_EVIDENCE_FINALIZATION_STATE_PATH=Join-Path $Directory 'fixture-finalization-state.json'
}
foreach($name in @('Write-FileTransferGuiLogLedgerEvidence','Wait-GuiSmokeProcessOutputCaptureDrain')) {
    $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
    if($definition.Count -ne 1) {throw 'Original persistence finalizer ambiguous'}
    Invoke-Expression $definition[0].Extent.Text
}
$script:persistOwner=$owner; $script:persistAdoption=$adoption
$script:persistBinding=$null; $script:persistCopy=$null; $script:persistEvidence=$null
$script:persistProbeFailure=''; $script:persistProbed=$false
$script:persistCleanupCurrent=$false
$script:persistCleanupDetail=''
$script:GuiSmokeProcessOutputFiles=@($apps | ForEach-Object {
    Join-Path $reportRoot ($_.Id.ToString()+'.stdout.log')
    Join-Path $reportRoot ($_.Id.ToString()+'.stderr.log')
})
$copyPath=Join-Path $Directory 'filetransfer-frontier-retained-log-slice.log'
$evidencePath=Join-Path $Directory 'filetransfer-frontier-finalizer-evidence.json'
$legacyPath=Join-Path $Directory 'filetransfer-retained-log-slice.log'
function Assert-PersistencePrivacy {
    foreach($name in @('owner','origin','requests','FrontierOwner','FrontierReports','localWriterIssuer',
        'localFrontierBinding','localFrontierResult','localFrontierEvidence','frontierPrepared','frontierHandoffPrepared','selectedOwner','originalReports')) {
        if($null -ne (Get-Variable $name -ErrorAction SilentlyContinue)) {throw "Persistence capability leaked: $name"}
    }
}
function Read-PersistenceBytes([string]$Path) {
    $read=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $memory=[IO.MemoryStream]::new()
    try {$read.CopyTo($memory); return ,$memory.ToArray()} finally {$memory.Dispose(); $read.Dispose()}
}
function Update-FileTransferGuiEvidenceFinalizationStage {
    param($Stage,$CapturePath,$CaptureSha256,$CaptureLineCount)
    Assert-PersistencePrivacy
    if($Stage -ceq 'writer_validation_completed' -and $mode.StartsWith('handoff-')) {
        if($script:persistBinding.GetType().GetField('parentRendezvousAttempted',$flags).GetValue($script:persistBinding)) {throw 'Handoff preceded ordinary persistence/stage completion'}
        if($mode -ceq 'handoff-stage-failure') {throw 'fixture completion stage failure'}
    }
    if($Stage -ceq 'process_output_drained') {
        if($script:persistOwner.GetType().GetField('fileSealAttempted',$flags).GetValue($script:persistOwner)) {throw 'Persistence preceded native exit/EOF'}
        if($mode -ceq 'copy-collision') {[IO.File]::WriteAllText($copyPath,'copy-sentinel')}
        if($mode -ceq 'evidence-collision') {[IO.File]::WriteAllText($evidencePath,'evidence-sentinel')}
        if($mode -ceq 'legacy-collision') {[IO.File]::WriteAllText($legacyPath,'legacy-sentinel')}
    }
}
function Sync-GuiSmokeAppLogLedger {
    param([switch]$IgnoreScenarioDeadline)
    Assert-PersistencePrivacy
    if($script:persistProbed) {throw 'Persistence repeated ordinary final sync'}
    $script:persistProbed=$true
    try {
        $expectNative=$mode -cnotin @('default','copy-collision','evidence-collision')
        $reader=$script:persistAdoption.GetType().GetField('sealedReader',$flags).GetValue($script:persistAdoption)
        if($expectNative) {
            if($null -eq $reader -or -not $reader.IsCurrent) {
                $seal=$script:persistOwner.GetType().GetField('fileSealAttempted',$flags).GetValue($script:persistOwner)
                $work=$script:persistOwner.GetType().GetField('frontierWork',$flags).GetValue($script:persistOwner)
                throw "Native persistence missing: seal=$seal; reader=$($null -ne $reader); workMs=$($work.ElapsedMilliseconds); cleanupCurrent=$script:persistCleanupCurrent; cleanup=$script:persistCleanupDetail; drain=$processOutputDrainFailure"
            }
            $script:persistBinding=$reader.GetType().GetField('binding',$flags).GetValue($reader)
            $script:persistCopy=$script:persistBinding.SemanticCopy
            $script:persistEvidence=$script:persistBinding.PersistedEvidence
            if($null -eq $script:persistCopy -or $null -eq $script:persistEvidence -or
                -not $script:persistCopy.IsCurrent -or -not $script:persistEvidence.IsCurrent -or
                $script:persistCopy.Output.CreationPath -cne $copyPath -or $script:persistCopy.Output.CreationPath -ceq $legacyPath -or
                $script:persistEvidence.Output.CreationPath -cne $evidencePath -or $script:persistEvidence.LiveProofEligible -or
                $script:persistEvidence.NativeWriterCustodyEstablished -or $script:persistEvidence.PublicationCompletionIssued) {throw 'Native copy/persistence ownership changed'}
            $json=[Text.Encoding]::UTF8.GetString((Read-PersistenceBytes $evidencePath)) | ConvertFrom-Json
            $analyzer=@($json.outputs | Where-Object {$_.role -ceq 'analyzer'})
            if(-not $json.diagnosticOnly -or $json.schemaVersion -ne 1 -or $analyzer.Count -ne 1 -or
                $analyzer[0].path -cne $copyPath -or $analyzer[0].fileIdentity -cne $script:persistCopy.Output.FileIdentity -or
                $json.source.bookmark -ne $script:persistBinding.CursorMatch.Initial.Bookmark -or
                $json.ledgerId -cne $script:persistBinding.CursorMatch.Final.LedgerId -or
                $json.liveProofEligible -or $json.nativeWriterCustodyEstablished -or $json.publicationCompletionIssued) {throw 'Persisted facts lost original ownership'}
            $work=$script:persistOwner.GetType().GetField('frontierWork',$flags).GetValue($script:persistOwner)
            if($work.IsRunning) {throw 'Persistence left native work clock running'}
            if($mode -ceq 'cancel-after') {
                $script:persistOwner.Dispose()
                if($script:persistCopy.IsCurrent -or $script:persistEvidence.IsCurrent) {throw 'Cancelled persisted evidence survived'}
            }
        } elseif($mode -ceq 'default') {
            if($null -ne $reader -or -not $script:persistOwner.IsCurrent -or
                [IO.File]::Exists($copyPath) -or [IO.File]::Exists($evidencePath)) {
                throw "Default finalizer changed optional ownership: cleanupCurrent=$script:persistCleanupCurrent; reader=$($null -ne $reader); current=$($script:persistOwner.IsCurrent); copy=$([IO.File]::Exists($copyPath)); evidence=$([IO.File]::Exists($evidencePath)); cleanup=$script:persistCleanupDetail; drain=$processOutputDrainFailure"
            }
        } elseif($script:persistOwner.IsCurrent) {throw 'Native persistence collision retained optional ownership'}
    } catch {$script:persistProbeFailure=$_.Exception.Message; throw}
    Sync-GuiSmokeLogLedger -State $script:GuiSmokeAppLogLedger
}
if($mode -ceq 'legacy-throw') {
    function Get-GuiSmokeLogLedgerSnapshot {param($State,$Bookmark) Assert-PersistencePrivacy; throw 'fixture legacy persistence failure'}
}
$private:selectedOwner=if($mode -ceq 'default') {$null} else {$owner}
$private:originalReports=$reports
$capture=$null; $failure=''; $output=@()
foreach($pair in @(@('run',$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID),@('scenario',$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID),
    @('artifactRoot',[IO.Path]::GetFullPath($Directory)),@('endpointName',$env:NLINK_GUI_WRITER_REQUEST),@('id',[guid]$env:NLINK_GUI_WRITER_REQUEST_ID))) {
    if($owner.GetType().GetField($pair[0],$flags).GetValue($owner) -cne $pair[1]) {throw ('Fixture finalizer scope mismatch: '+$pair[0])}
}
if(-not $owner.IsCurrent) {throw 'Fixture owner expired before finalizer'}
try {
    $output=@(Write-FileTransferGuiLogLedgerEvidence -ArtifactDir $Directory -Bookmark $script:ordinaryInitial.bookmark `
        -InitialSnapshot $script:ordinaryInitial -FrontierOwner $selectedOwner -FrontierReports $originalReports `
        -LocalNativeWriterCapture ([ref]$capture) -ProcessOutputDrainAction {
            param($FrontierOwner)
            Set-Variable -Name FrontierOwner -Option Private
            $private:cleanupOwner=if($mode -ceq 'default') {$script:persistOwner} else {$FrontierOwner}
            $detail=$null
            Cleanup-ProcessesWithOperationalLogDrain -Processes $targets -ExePath fixture-only -FrontierOwner $cleanupOwner -CleanupDiagnostic ([ref]$detail)
            $script:persistCleanupCurrent=$cleanupOwner.IsCurrent
            $script:persistCleanupDetail=$detail | ConvertTo-Json -Depth 4 -Compress
            return $apps
        })
} catch {$failure=$_.Exception.Message}
if(-not $script:persistProbed -or $script:persistProbeFailure) {throw "Persistence probe failed: $script:persistProbeFailure; $failure"}
if($mode -ceq 'legacy-throw') {
    if($failure -cne 'fixture legacy persistence failure') {throw "Original legacy exception lost: $failure"}
} elseif($mode -ceq 'legacy-collision') {
    if($failure -cne 'filetransfer_gui_evidence_capture_not_persisted' -or
        [IO.File]::ReadAllText($legacyPath) -cne 'legacy-sentinel') {throw "Original legacy collision behavior changed: $failure"}
} elseif($mode -ceq 'handoff-stage-failure') {
    if($failure -cne 'fixture completion stage failure') {throw "Original completion-stage exception lost: $failure"}
} elseif($failure) {throw "Ordinary finalizer failed: $failure"}
if($output.Count) {throw 'Finalizer exported native persistence through the pipeline'}
if($mode -ceq 'exit-rundown' -and -not $script:rundownObserved) {throw 'Fixture missed the early HasExited observation'}
foreach($app in $apps) {if(-not $app.HasExited -or $app.ExitCode -ne 0) {throw 'Persistence lost ordinary child cleanup'}}
foreach($report in $reports) {foreach($task in @($report.Stdout,$report.Stderr)) {if(-not $task.Wait(5000)) {throw 'Persistence lost report EOF'}; [void]$task.GetAwaiter().GetResult()}}
if($script:persistOwner.IsCurrent -or ($null -ne $script:persistBinding -and
    ($script:persistBinding.IsCurrent -or $script:persistCopy.IsCurrent -or $script:persistEvidence.IsCurrent))) {throw 'Finalizer retained persistence after completion/failure'}
if($mode -cnotin @('legacy-throw','legacy-collision','handoff-stage-failure')) {
    $legacy=[IO.File]::ReadAllText((Join-Path $Directory 'filetransfer-gui-log-ledger.json')) | ConvertFrom-Json
        if($legacy.cursorIntegrityVerdict -cne 'pass' -or $legacy.writerIntegrityVerdict -cne 'pass' -or
            $null -eq $capture -or $capture.GetType().FullName -cne 'NLink.Tools.GuiSmokeLedgerNativeCapture') {
            throw ('Ordinary legacy evidence changed: '+($legacy | ConvertTo-Json -Depth 3 -Compress))
        }
        if($legacy.analyzerInput.path -cne $legacyPath -or -not [IO.File]::Exists($legacyPath)) {throw 'Native path replaced ordinary analyzer input'}
}
if($mode -ceq 'copy-collision' -and [IO.File]::ReadAllText($copyPath) -cne 'copy-sentinel') {throw 'Native copy overwrote existing file'}
if($mode -ceq 'evidence-collision' -and [IO.File]::ReadAllText($evidencePath) -cne 'evidence-sentinel') {throw 'Native persistence overwrote existing file'}
$nativeCompleted=$false
if($null -ne $script:persistBinding) {
    $issuer=$script:persistBinding.GetType().GetField('finalizer',$flags).GetValue($script:persistBinding)
    $nativeAttempted=$script:persistBinding.GetType().GetField('parentCompletionAttempted',$flags).GetValue($script:persistBinding)
    $issuerAttempted=$issuer.GetType().GetField('attempted',$flags).GetValue($issuer)
    $nativeCompleted=$nativeAttempted -and $issuerAttempted -ne 0
    if($mode -ceq 'handoff-early-commit' -and (-not $nativeAttempted -or $issuerAttempted -ne 0)) {throw 'Failed native completion was retried as ordinary'}
}
foreach($path in @($copyPath,$evidencePath)) {
    if([IO.File]::Exists($path)) {
        $blocked=$false
        try {$probe=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); $probe.Dispose()} catch [IO.IOException] {$blocked=$true}
        if($blocked -ne $nativeCompleted) {throw 'Finalizer native pins did not follow original parent lifetime'}
    }
}
'frontier-finalizer-source='+$FinalizerCase
if($mode -ceq 'handoff-gui-failure') {'cleanup-adoption-gui-ok'; [Environment]::Exit(7)}
