param([string]$RepoRoot,[string]$Directory,[string]$RunId,[string]$ScenarioId,
    [ValidateSet('off','write','existing','write-error','guards')][string]$PersistenceMode='off',[switch]$LiveScaleFixture)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) { throw 'Route parent parse failed' }
foreach($name in @('ConvertTo-RouteAcceptanceInt','Read-RouteAcceptanceKeyValueArtifact','Get-RouteAcceptanceReportValue',
    'Write-RouteAcceptanceRetainedAnalysisTimeoutArtifacts','Test-RouteAcceptanceRetainedAnalysisCompletedProofArtifacts',
    'Write-RouteAcceptanceFakeAuthoritativeSnapshotEvidence','Stop-RouteAcceptanceRetainedAnalysisProcess',
    'Invoke-RouteAcceptanceChildScriptNoThrow','Invoke-RouteAcceptanceRetainedAnalysis','Invoke-TunaRouteAcceptanceRun')) {
    $definition=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
    if($definition.Count -ne 1) { throw "Parent function ambiguous: $name" }
    $text=$definition[0].Extent.Text
    $text=$text.Replace('Join-Path (Split-Path -Parent $PSScriptRoot) ''tools/FileTransferOps/LocalRetainedSourceRequest.ps1''',
        'Join-Path $script:moduleRoot ''tools/FileTransferOps/LocalRetainedSourceRequest.ps1''')
    $text=$text.Replace('Join-Path $PSScriptRoot ''FileTransferOps/LocalGuiWriterRequest.ps1''',
        'Join-Path $script:moduleRoot ''tools/FileTransferOps/LocalGuiWriterRequest.ps1''')
    if($name -ceq 'Invoke-RouteAcceptanceRetainedAnalysis') {
        if($text -notmatch '\$analysisTimeoutSeconds = 1800') { throw 'Production watchdog changed' }
        $text=$text.Replace('$startInfo.FileName = ''powershell''','$startInfo.FileName = (Get-Process -Id $PID).Path')
        $text=$text.Replace('$startInfo.UseShellExecute = $false','$startInfo.UseShellExecute = $false; $startInfo.CreateNoWindow = $true')
        if($LiveScaleFixture) {
            $needle="'-File', '.\tools\FileTransfer-Ops.ps1'"
            if($text.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) {throw 'Original primary CLI launch changed'}
            $text=$text.Replace($needle,"'-File', '.\tests\nLink.SmokeTests.Core\Support\PublicationLivePrimaryWorker.ps1'")
        }
        $needle='$private:localAdopted = $private:localAdoption.Take($private:localTerminalReceipt)'
        if($text.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) { throw 'Original adoption seam changed' }
        $text=$text.Replace($needle,$needle+'; $script:adoptionAttempted=$true; $script:omissionReason=$private:localAdoption.OmissionReason; $script:adoptedObservation=$private:localAdopted')
    }
    if($name -ceq 'Invoke-TunaRouteAcceptanceRun') {
        $needle='Join-Path $RepoRoot ''tools\Run-FileTransferTunaGuiSmoke.ps1'''
        if($text.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) { throw 'Whole Wrapper launch changed' }
        $text=$text.Replace($needle,'Join-Path $RepoRoot ''tests/nLink.SmokeTests.Core/Support/PublicationPositiveWrapperWorker.ps1''')
        $needle='if ($null -ne $private:localRelayedWriter) {'
        $text=$text.Replace($needle,$needle+' $script:relayObserved=$true; $script:wrapperPid=$private:localRelayedWriter.WrapperProcessId; $script:guiPid=$private:localRelayedWriter.WriterContext.GuiProcessId;')
        $needle='$reconciliation = Update-FileTransferTunaDirectEvidenceManifestFromReceipt'
        $text=$text.Replace($needle,'$script:retainedSourceHashes=$script:sourceHashes; $script:retainedSourceCounts=$script:sourceCounts; '+$needle)
    }
    Invoke-Expression $text
}
$script:moduleRoot=$RepoRoot; $script:RouteAcceptanceExecutionMode='live'; $RouteMode='live-regular-activation-cycle'
$TimeoutSeconds=30; $ProgressTimeoutSeconds=20
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID=$RunId; $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID=$ScenarioId
$output=Join-Path $Directory ('whole-wrapper-'+$PSVersionTable.PSVersion.Major)
if($PersistenceMode -cne 'off') { $output+='-'+$PersistenceMode }
$setup=Join-Path $output 'inert-setup'; [void][IO.Directory]::CreateDirectory((Join-Path $setup 'bridge/win-x64'))
foreach($name in @('unused-app.txt','unused-wallet.txt','unused-sidecar.txt','tuna-sidecar-manifest.json','bridge/win-x64/index.js')) {
    [IO.File]::WriteAllText((Join-Path $setup $name),'offline-inert-fixture-do-not-execute')
}
$settings=Join-Path $output 'offline-settings.json'
[IO.File]::WriteAllText($settings,(@{RepoRoot=$RepoRoot;Directory=$output;Output=$output;Mode='valid';GeneratedSource=(Join-Path $Directory 'nlink.log');LiveScaleFixture=$LiveScaleFixture.IsPresent} | ConvertTo-Json))
$env:NLINK_PUBLICATION_INTEGRATION_SETTINGS=$settings
$script:atomic=(Get-Command Write-FileTransferEvidenceAtomicJson).ScriptBlock
$script:hash=(Get-Command Get-FileTransferEvidenceSha256).ScriptBlock
$script:lines=(Get-Command Get-FileTransferEvidenceLineCount).ScriptBlock
$script:receiptWrites=0; $script:sourceHashes=0; $script:sourceCounts=0; $script:adoptionAttempted=$false
$script:omissionReason=''; $script:relayObserved=$false; $script:wrapperPid=0; $script:guiPid=0
$script:adoptedObservation=$null
$script:sourcePath=Join-Path $output 'filetransfer-retained-log-slice.log'
$companionPath=Join-Path $output 'filetransfer-local-publication-diagnostic.json'
if($PersistenceMode -ceq 'existing') { [IO.File]::WriteAllText($companionPath,'existing-companion-must-not-change') }
if($PersistenceMode -ceq 'write-error') { [void][IO.Directory]::CreateDirectory($companionPath) }
function Write-FileTransferEvidenceAtomicJson {
    param([string]$Path,$Value)
    if($Path.EndsWith('filetransfer-retained-analysis-attempt-receipt.json')) { $script:receiptWrites++ }
    & $script:atomic -Path $Path -Value $Value
}
function Get-FileTransferEvidenceSha256 {
    param([string]$Path,$ReadLease=$null)
    if($Path -ceq $script:sourcePath) { $script:sourceHashes++ }
    & $script:hash -Path $Path -ReadLease $ReadLease
}
function Get-FileTransferEvidenceLineCount {
    param([string]$Path)
    if($Path -ceq $script:sourcePath) { $script:sourceCounts++ }
    & $script:lines -Path $Path
}
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Parent must not evaluate authority' }
$publication=$null; $failure=''; $originalLocation=Get-Location
$attemptStarted=[Diagnostics.Stopwatch]::GetTimestamp()
try {
    Set-Location -LiteralPath $RepoRoot
    $runArgs=@{RepoRoot=$RepoRoot;ArtifactDir=$output;ResolvedExePath=(Join-Path $setup 'unused-app.txt')
        ResolvedWalletPath=(Join-Path $setup 'unused-wallet.txt');ResolvedSidecarPath=(Join-Path $setup 'unused-sidecar.txt')
        EffectiveWalletPassword='offline-not-a-wallet-secret';RouteMode=$RouteMode;Fault='none';PayloadSize='2B'
        LiveToggleSequence='on,off,on,off';LiveProofMode='RegularActivationCycle';LocalPublication=[ref]$publication}
    if($PersistenceMode -cin @('write','existing','write-error')) {
        $runArgs.Remove('LocalPublication'); $runArgs.CaptureLocalPublicationDiagnostics=$true
    }
    try { Invoke-TunaRouteAcceptanceRun @runArgs } catch { $failure=$_.Exception.Message }
} finally { Set-Location -LiteralPath $originalLocation.Path }
if($PersistenceMode -cin @('write','existing','write-error')) { $publication=$script:adoptedObservation }
if(-not $script:relayObserved -or -not $script:adoptionAttempted -or $null -eq $publication) {
    throw "Positive publication missing: relay=$script:relayObserved adoption=$script:adoptionAttempted omission=$script:omissionReason failure=$failure"
}
if($failure -notlike 'Retained analysis completed but the exact Tuna direct manifest did not prove the current runtime. Artifact: *') { throw "Original runtime-proof rejection changed: $failure" }
$receiptPath=Join-Path $output 'filetransfer-retained-analysis-attempt-receipt.json'
$receipt=[IO.File]::ReadAllText($receiptPath) | ConvertFrom-Json
if($receipt.status -cne 'completed' -or $receipt.exitCode -ne 0 -or $receipt.owner -cne 'RouteAcceptance' -or
    $publication.TerminalReceipt.Path -cne $receiptPath -or $publication.TerminalReceipt.Status -cne $receipt.status -or
    $publication.Completion.RequestId.ToString('N') -cne $receipt.attemptId -or $publication.Completion.WorkerProcessId -ne $receipt.processId -or
    $publication.Completion.CatalogSha256 -cne $publication.TerminalReceipt.CatalogSha256) { throw 'Original terminal/catalog/worker binding lost' }
$wrapper=[IO.File]::ReadAllText((Join-Path $output 'whole-wrapper-returned.json')) | ConvertFrom-Json
$gui=[IO.File]::ReadAllText((Join-Path $output 'controlled-gui.json')) | ConvertFrom-Json
if($script:wrapperPid -ne $wrapper.WrapperPid -or $script:guiPid -ne $gui.GuiPid -or $script:wrapperPid -eq $script:guiPid) { throw 'Whole Wrapper/GUI identity lost' }
$report=[IO.File]::ReadAllText((Join-Path $output 'filetransfer-operator-verdict.txt'))
if($report -notmatch '(?m)^first_proof_chain_failure_class=file_tuna_data_path_mismatch\r?$' -or
    $report -notmatch '(?m)^first_proof_chain_failure_event=filetransfer_data_path_send_result\r?$') { throw 'Primary selected failure changed' }
$stages=[IO.File]::ReadAllText((Join-Path $output 'filetransfer-retained-analysis-stage-diagnostics.json')) | ConvertFrom-Json
if($stages.synthetic_event_count -ne 0 -or $stages.compact_mode -ne 1 -or $script:receiptWrites -ne 1 -or $script:retainedSourceHashes -ne 2 -or $script:retainedSourceCounts -ne 2) {
    throw 'Original input/receipt traversal changed'
}
$payload=[Text.Encoding]::UTF8.GetString($publication.CopyPayload())
$data=$payload | ConvertFrom-Json
if($LiveScaleFixture) {
    $observed=[IO.File]::ReadAllText((Join-Path $output 'live-primary-observation.json')) | ConvertFrom-Json
    if($observed.Evaluations -ne 3 -or ($observed.EvaluationCallers -join '|') -cne '<ScriptBlock>|<ScriptBlock>|Get-FileTransferRecoveryFailureClassification' -or
        -not $observed.Prepared -or -not $observed.Witness -or -not $observed.Completion -or
        $observed.FullRecords -ne $data.inputs.native.fullCapture.recordCount -or
        $observed.CompactRecords -ne $data.inputs.native.compactSnapshot.recordCount -or
        $observed.NativeRecordVisits -ne $observed.FullRecords+$observed.CompactRecords -or
        $observed.NativeCodeUnits -ne $data.inputs.native.fullCapture.codeUnitCount+$data.inputs.native.compactSnapshot.codeUnitCount -or
        $observed.NativeMilliseconds -gt 2000 -or $observed.OptionalMilliseconds -gt 20000) {throw 'Original primary call count/work/custody changed'}
}
$selected=$data.selection.witness; $row=$data.selection.row
$reportValues=Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $output 'filetransfer-operator-verdict.txt')
if($data.request.selectionPolicy -cne 'primary_reported_failure_witnesses_v1' -or $data.request.maximumWitnesses -ne 64 -or
    $data.scope -cne 'local_publication_forwarding' -or $data.peerLineageStatus -cne 'not_evaluated' -or
    $data.evaluations.fullInvocationId -ceq $data.evaluations.compactInvocationId -or
    $data.inputs.source.path -cne $script:sourcePath -or
    $selected.reportClass -cne $reportValues.first_proof_chain_failure_class -or
    $selected.reportReason -cne $reportValues.first_proof_chain_failure_reason -or
    $selected.eventName -cne $reportValues.first_proof_chain_failure_event -or
    $selected.sourceLine -ne [long]$reportValues.first_proof_chain_failure_line_number -or
    $selected.writerSequence -ne [long]$reportValues.first_proof_chain_failure_writer_sequence -or
    $row.writer -cne $selected.writer -or $row.useWriterSequence -ne $selected.writerSequence -or
    $row.routeState.route -cne 'file_tuna_v4' -or $row.routeState.protocolVersion -ne 4) { throw 'Adopted payload lost real selected failure' }
# Post-run test verification only; not another primary evaluation or capture.
$sourceLines=[IO.File]::ReadAllLines($script:sourcePath)
$selectedLine=$sourceLines[$selected.sourceLine-1]
$sha=[Security.Cryptography.SHA256]::Create()
try { $selectedHash=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($selectedLine))).Replace('-','').ToLowerInvariant() }
finally { $sha.Dispose() }
if($selected.contentSha256 -cne $selectedHash -or $row.useContentSha256 -cne $selectedHash -or
    -not $selectedLine.Contains('offline_fixture_fault=wrong_physical_transport') -or
    -not $sourceLines[$data.selection.provenance.publicationSourceLine-1].Contains('event=filetransfer_coordinator_transition_snapshot;') -or
    -not $sourceLines[$data.selection.provenance.effectSourceLine-1].Contains('effect_kind=route_publication_forwarded;')) { throw 'Exact source provenance changed' }
$manifest=[IO.File]::ReadAllText((Join-Path $output 'filetransfer-tuna-gui-evidence-manifest.json')) | ConvertFrom-Json
if($manifest.currentRuntimeProof -or -not $manifest.retainedAnalysisReceiptValid -or $manifest.retainedAnalysisStatus -cne 'completed' -or
    $reportValues.verdict -cne 'FAIL_PROTOCOL_OR_INTEGRITY' -or $reportValues.gate_status -cne 'fail') { throw 'Diagnostic adoption changed primary failure or live proof' }
if($PersistenceMode -ceq 'guards') {
    if($publication.TryPersistForAttempt($output,$RunId+'-other',$ScenarioId,$attemptStarted) -or
        $publication.TryPersistForAttempt($output,$RunId,$ScenarioId+'-other',$attemptStarted) -or
        $publication.TryPersistForAttempt($output+'-other',$RunId,$ScenarioId,$attemptStarted) -or
        $publication.TryPersistForAttempt($output,$RunId,$ScenarioId,([Diagnostics.Stopwatch]::GetTimestamp()+[Diagnostics.Stopwatch]::Frequency)) -or
        $publication.TryPersistForAttempt('relative-output',$RunId,$ScenarioId,$attemptStarted)) { throw 'Wrong/stale attempt scope persisted' }
    if(-not $publication.TryPersistForAttempt($output,$RunId,$ScenarioId,$attemptStarted)) { throw 'Genuine scoped persistence omitted' }
}
if($PersistenceMode -cin @('write','guards')) {
    $companion=[IO.File]::ReadAllText($companionPath); $saved=$companion | ConvertFrom-Json
    if([IO.File]::ReadAllBytes($companionPath).Length -gt 262144 -or
        $saved.format -cne 'nlink-adopted-local-publication-v1' -or $saved.authorityEffect -cne 'none' -or
        -not $companion.Contains('"publication":'+$payload+',') -or
        $saved.terminalReceipt.requestId -cne $publication.TerminalReceipt.RequestId.ToString('D') -or
        $saved.terminalReceipt.path -cne $receiptPath -or $saved.terminalReceipt.status -cne $receipt.status -or
        $saved.terminalReceipt.catalogSha256 -cne $publication.TerminalReceipt.CatalogSha256 -or
        $saved.terminalReceipt.workerProcessId -ne $receipt.processId) { throw 'Persisted companion lost owned bytes/terminal binding' }
}
elseif($PersistenceMode -ceq 'existing') {
    if([IO.File]::ReadAllText($companionPath) -cne 'existing-companion-must-not-change') { throw 'Existing companion overwritten' }
}
elseif($PersistenceMode -ceq 'write-error') {
    if(-not [IO.Directory]::Exists($companionPath) -or [IO.Directory]::GetFileSystemEntries($companionPath).Length -ne 0) { throw 'Write error changed destination' }
}
elseif(Test-Path -LiteralPath $companionPath) { throw 'Disabled persistence wrote a companion' }
if($PersistenceMode -cne 'off' -and $publication.TryPersistForAttempt($output,$RunId,$ScenarioId,$attemptStarted)) { throw 'Persistence attempt replayed' }
if([IO.Directory]::GetFiles($output,'filetransfer-local-publication-diagnostic.json.*.tmp').Length -ne 0) { throw 'Owned staging file leaked' }
[IO.File]::WriteAllText((Join-Path $output 'adopted-offline-publication.json'),$payload)
$result=[ordered]@{WholeWrapperMain=$true;PrimarySelectedSeededFailure=$true;RealReports=$true;Adopted=$true;Status=$receipt.status
    Persistence=$PersistenceMode;OriginalRuntimeProofRejection=$true;ReceiptWrites=1;SourceHashes=2;SourceCounts=2;SyntheticEvents=0;ProductionWatchdogSeconds=1800;Live=$false}
[IO.File]::WriteAllText((Join-Path $output 'positive-integration-result.json'),($result | ConvertTo-Json))
'positive-integration='+($result | ConvertTo-Json -Compress)
