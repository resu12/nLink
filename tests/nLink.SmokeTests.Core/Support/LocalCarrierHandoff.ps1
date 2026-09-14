param([string]$RepoRoot,[string]$ArtifactRoot,[string]$TransportMode)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferSoak/RetainedAnalysisEvidence.ps1')
$readLeaseMode=$TransportMode -ceq 'read-lease'
$sourceRequestMode=$TransportMode -ceq 'source-request'
if($readLeaseMode) { $script:inputReadLease=$null }
$nativeCapture=$TransportMode -ceq 'native'
$slotTransport=$nativeCapture -or $TransportMode -ceq 'slot'
if($slotTransport) {
    . (Join-Path $RepoRoot 'tools/FileTransferOps/LocalEvidenceExportSlot.ps1')
    $script:slot=$null; $script:slotGate=$null
}
function Parse([string]$Path) {
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($Path,[ref]$tokens,[ref]$errors)
    if(@($errors).Count) { throw "Parse failed: $Path" }
    return $ast
}
function Definition($Ast,[string]$Name) {
    $matches=@($Ast.FindAll({param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $Name
    },$true))
    if($matches.Count -ne 1) { throw "Function ambiguous or missing: $Name" }
    return $matches[0]
}
$route=Parse (Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1')
$wrapper=Parse (Join-Path $RepoRoot 'tools/Run-FileTransferTunaGuiSmoke.ps1')
foreach($name in @('ConvertTo-RouteAcceptanceInt','Read-RouteAcceptanceKeyValueArtifact','Get-RouteAcceptanceReportValue',
    'Write-RouteAcceptanceRetainedAnalysisTimeoutArtifacts','Test-RouteAcceptanceRetainedAnalysisCompletedProofArtifacts',
    'Write-RouteAcceptanceFakeAuthoritativeSnapshotEvidence','Stop-RouteAcceptanceRetainedAnalysisProcess')) {
    Invoke-Expression (Definition $route $name).Extent.Text
}
foreach($name in @('Read-TunaGuiReportValue','Write-TunaGuiRetainedAnalysisTimeoutArtifacts',
    'Test-TunaGuiRetainedAnalysisCompletedProofArtifacts','Stop-TunaGuiRetainedAnalysisProcess')) {
    Invoke-Expression (Definition $wrapper $name).Extent.Text
}
foreach($pair in @(@($route,'Invoke-RouteAcceptanceRetainedAnalysis'),@($wrapper,'Invoke-TunaGuiRetainedAnalysis'))) {
    $definition=Definition $pair[0] $pair[1]
    $text=$definition.Extent.Text
    # Scale only the local test watchdog and polling interval. Pin the original
    # 1800s value and retain the actual exit, cancellation, receipt and finally
    # branches. The controlled child and all its files stay in ArtifactRoot.
    $deadline=@($definition.FindAll({param($node)
        $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq '$analysisTimeoutSeconds'
    },$true))
    if($deadline.Count -ne 1 -or $deadline[0].Right.Extent.Text -cne '1800') { throw 'Production watchdog changed' }
    if(([regex]::Matches($text,'while \(-not \$process.WaitForExit\(5000\)\)')).Count -ne 1) { throw 'Production exit wait changed' }
    $testWatchdog=if($slotTransport -or $sourceRequestMode) { 5 } else { 2 }
    $text=$text.Replace($deadline[0].Extent.Text,('$analysisTimeoutSeconds = '+$testWatchdog))
    $text=$text.Replace('while (-not $process.WaitForExit(5000))','while (-not $process.WaitForExit(25))')
    $text=$text.Replace('$startInfo.UseShellExecute = $false',"`$startInfo.UseShellExecute = `$false`n`$startInfo.CreateNoWindow = `$true")
    if($sourceRequestMode) {
        $loader="Join-Path (Split-Path -Parent `$PSScriptRoot) 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1'"
        if($text.Split([string[]]@($loader),[StringSplitOptions]::None).Length -ne 2) { throw 'Actual parent source loader seam changed' }
        $text=$text.Replace($loader,"Join-Path `$script:sourceModuleRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1'")
    }
    if($readLeaseMode) {
        $hashAssignment=@($definition.FindAll({param($node)
            $node -is [Management.Automation.Language.AssignmentStatementAst] -and $node.Left.Extent.Text -ceq '$inputSha256Before'
        },$true))
        if($hashAssignment.Count -ne 1 -or $hashAssignment[0].Right.Extent.Text -notlike 'Get-FileTransferEvidenceSha256 *') { throw 'Parent source hash seam changed' }
        $needle='-ReadLease $sourceLeaseReference'
        if($hashAssignment[0].Extent.Text.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) { throw 'Parent optional source lease changed' }
        $text=$text.Replace($hashAssignment[0].Extent.Text,($hashAssignment[0].Extent.Text.Replace($needle,'-ReadLease ([ref]$script:inputReadLease)')))
    }
    if($slotTransport) {
        $startOutput='$stdoutTask = $process.StandardOutput.ReadToEndAsync()'
        if(([regex]::Matches($text,[regex]::Escape($startOutput))).Count -ne 1) { throw 'Primary output capture insertion ambiguous' }
        # Test-only offer hookup. Keep the real wait, exit, terminal receipt and
        # process disposal branches. This is not production projection wiring.
        $binding=@'
if (-not $script:slot.BindProducer($process)) { throw 'Test slot binding failed' }
if ($script:slotCase -ceq 'slot-contended') {
    $script:slotGate = [Threading.Mutex]::OpenExisting($script:slot.Name+'.gate')
    if (-not $script:slotGate.WaitOne(0)) { throw 'Test slot contention unavailable' }
}
if ($script:slotCase -ceq 'slot-cancelled') { $script:slot.Cancel() }
'@
        $text=$text.Replace($startOutput,($binding+"`n"+$startOutput))
    }
    Invoke-Expression $text
}
# Run only extracted non-GUI parents. The existing fake-summary hook stays off.
$script:RouteAcceptanceExecutionMode='live'; $RouteMode='live-regular-activation-cycle'
$script:sourceModuleRoot=$RepoRoot
function Get-FileTransferCoordinatorSnapshotEvaluation { throw 'Lifecycle test must not evaluate authority' }
function New-FileTransferLocalPublicationEvidenceCarrier { throw 'Lifecycle test must not fabricate C0 provenance' }
$originalLocation=Get-Location
$originalRun=$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID
$originalScenario=$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID=[guid]::NewGuid().ToString('D')
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID=$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID+':lifecycle:1'
$rows=[Collections.Generic.List[object]]::new()
$primaryFiles=@('filetransfer-operator-verdict.txt','stability-gates-summary.txt','filetransfer-route-consistency-summary.txt',
    'filetransfer-coordinator-snapshot-full-summary.json','filetransfer-coordinator-snapshot-compact-summary.json',
    'filetransfer-coordinator-snapshot-parity-summary.json','filetransfer-retained-compact-summary.json',
    'filetransfer-retained-analysis-stage-diagnostics.json','filetransfer-tuna-gui-summary.json')
try {
    Set-Location -LiteralPath $ArtifactRoot
    foreach($owner in @('RouteAcceptance','Wrapper')) {
        foreach($outcome in @('gui-success-proof-pass','gui-success-proof-fail','gui-failure-proof-fail')) {
            $baseline=$null
                $optionalCases=if($sourceRequestMode) { @('none','completed','caught-failure','nonzero','stall') }
                    elseif($readLeaseMode) { @('none','completed','nonzero','stall') }
                    elseif($nativeCapture) { @('none','native-completed','native-budget','native-cancelled') }
                    elseif($slotTransport) { @('none','slot-completed','slot-contended','slot-cancelled') }
                else { @('none','completed','caught-failure','malformed','nonzero','stall') }
            foreach($optional in $optionalCases) {
                $casePath=Join-Path $ArtifactRoot ($owner+'-'+$outcome+'-'+$optional)
                [void](New-Item -ItemType Directory -Path $casePath)
                $proofPass=$outcome -ceq 'gui-success-proof-pass'
                $guiPass=$outcome -cne 'gui-failure-proof-fail'
                $log=Join-Path $casePath 'filetransfer-retained-log-slice.log'
                [IO.File]::WriteAllText($log,"event=lifecycle-only`n",[Text.UTF8Encoding]::new($false))
                $settings=@{proofPass=$proofPass;optionalMode=$optional}
                if($sourceRequestMode) { $settings.sourceRequest=$true; $settings.repoRoot=$RepoRoot }
                if($readLeaseMode) { $script:inputReadLease=$null }
                if($slotTransport) {
                    $script:slotGate=$null; $script:slotCase=$optional
                    $slotRequest=[guid]::NewGuid()
                    $slotBytes=[Text.Encoding]::UTF8.GetBytes('prepared-lifecycle-only-payload')
                    $sha=[Security.Cryptography.SHA256]::Create()
                    try { $slotHash=([BitConverter]::ToString($sha.ComputeHash($slotBytes))).Replace('-','').ToLowerInvariant() }
                    finally { $sha.Dispose() }
                    $script:slot=[NLink.Tools.LocalEvidenceExportSlot]::new($slotRequest,$slotHash)
                    $settings.slotName=$script:slot.Name; $settings.slotRequest=$slotRequest.ToString('D'); $settings.repoRoot=$RepoRoot
                    if($nativeCapture) { $settings.nativeCapture=$true }
                }
                Write-FileTransferEvidenceAtomicJson (Join-Path $casePath 'case.json') $settings
                Write-FileTransferEvidenceAtomicJson (Join-Path $casePath 'filetransfer-tuna-gui-summary.json') @{
                    completed=$guiPass;integrityOk=$guiPass;inboundTerminalState='Completed';outboundTerminalState='Completed'}
                $failure=''; $watch=[Diagnostics.Stopwatch]::StartNew()
                try {
                    $sourceArgs=@{}
                    if($sourceRequestMode -and $optional -cne 'none') { $sourceArgs.CaptureLocalSourceOwnership=$true }
                    if($owner -ceq 'RouteAcceptance') { Invoke-RouteAcceptanceRetainedAnalysis -ArtifactDir $casePath -LiveRouteProofMode RegularActivationCycle @sourceArgs }
                    else { Invoke-TunaGuiRetainedAnalysis -RepoRoot $ArtifactRoot -AnalysisDir $casePath -LogPath $log -LiveRouteProofMode RegularActivationCycle @sourceArgs }
                }
                catch { $failure=$_.Exception.Message }
                $watch.Stop()
                if($slotTransport -and $null -ne $script:slotGate) {
                    $script:slotGate.ReleaseMutex(); $script:slotGate.Dispose(); $script:slotGate=$null
                }
                $receiptPath=Join-Path $casePath 'filetransfer-retained-analysis-attempt-receipt.json'
                if(-not (Test-Path -LiteralPath $receiptPath)) { throw "Terminal receipt missing: $owner/$outcome/$optional : $failure" }
                $receipt=[IO.File]::ReadAllText($receiptPath) | ConvertFrom-Json
                $expected=if($optional -ceq 'stall') { 'timed_out' } elseif($optional -ceq 'nonzero') { 'failed' }
                    elseif(-not $proofPass) { 'proof_incomplete' } else { 'completed' }
                if($receipt.status -cne $expected -or $receipt.owner -cne $owner -or
                    $receipt.acceptanceRunId -cne $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -or
                    $receipt.scenarioRunId -cne $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID -or
                    -not $receipt.analyzerInputUnchanged -or
                    $receipt.analyzerInputSha256Before -cne (Get-FileTransferEvidenceSha256 $log) -or
                    (($expected -ceq 'completed') -ne [string]::IsNullOrEmpty($failure))) {
                    throw "Primary lifecycle drift: $owner/$outcome/$optional : $failure : $($receipt.status)"
                }
                $persisted=[IO.File]::ReadAllText((Join-Path $casePath 'primary-persisted.json')) | ConvertFrom-Json
                if($persisted.processId -ne $receipt.processId -or -not $persisted.optionalNotStarted -or
                    $null -ne (Get-Process -Id $receipt.processId -ErrorAction SilentlyContinue)) { throw 'Primary worker identity/exit not bound' }
                if(Test-Path -LiteralPath (Join-Path $casePath 'late-optional.json')) { throw 'Late worker survived cancellation' }
                $hashes=@(foreach($name in $primaryFiles) { Get-FileTransferEvidenceSha256 (Join-Path $casePath $name) })
                if($optional -ceq 'none') { $baseline=$hashes }
                if($optional -cne 'stall' -and ($hashes -join '|') -cne ($baseline -join '|')) { throw 'Optional result changed primary report bytes' }
                if($optional -ceq 'stall') {
                    if(-not (Test-Path -LiteralPath (Join-Path $casePath 'optional-started.json')) -or
                        $receipt.exitCodeAvailable -or -not $receipt.timedOut -or $receipt.completedProofArtifacts -or
                        $watch.ElapsedMilliseconds -gt 15000 -or $hashes[0] -ceq $baseline[0] -or
                        (($hashes | Select-Object -Skip 1) -join '|') -cne (($baseline | Select-Object -Skip 1) -join '|')) {
                        throw 'Pre-exit timeout counterexample or cleanup not established'
                    }
                    $timeout=[IO.File]::ReadAllText((Join-Path $casePath 'filetransfer-retained-analysis-timeout-summary.json')) | ConvertFrom-Json
                    if($timeout.timeout_seconds -ne $testWatchdog -or $timeout.acceptance_failure_class -cne 'incomplete_proof') { throw 'Timeout did not remain fail closed' }
                }
                else {
                    if(-not $receipt.exitCodeAvailable -or $receipt.timedOut -or
                        $receipt.exitCode -ne $(if($optional -ceq 'nonzero') { 9 } else { 0 }) -or
                        $receipt.completedProofArtifacts -ne $proofPass) { throw 'Exit/proof receipt metadata changed' }
                }
                foreach($binding in @('diagnostics','stageDiagnostics','compactSummary','coordinatorSnapshotFullSummary',
                    'coordinatorSnapshotCompactSummary','coordinatorSnapshotParitySummary','routeSummary','operatorVerdict','stabilitySummary')) {
                    if($receipt.$binding.sha256 -cne (Get-FileTransferEvidenceSha256 $receipt.$binding.path)) { throw "Receipt not frozen to output: $binding" }
                }
                # Terminal receipt bytes must not be replaced by a retry or an
                # optional file arriving after primary finalization.
                $receiptHash=Get-FileTransferEvidenceSha256 $receiptPath
                $duplicateRejected=$false
                try {
                    Write-FileTransferRetainedAnalysisAttemptReceipt -ArtifactDir $casePath -Owner $owner -AttemptId 'duplicate' `
                        -LogPath $log -InputSha256Before $receipt.analyzerInputSha256Before -InputLineCountBefore 1 `
                        -LiveRouteProofMode RegularActivationCycle -CompactRequested $true -ProcessId $receipt.processId `
                        -StartedUtc $receipt.startedUtc -EndedUtc $receipt.endedUtc -TimedOut $false -ExitCodeAvailable $true `
                        -ExitCode 0 -CompletedProofArtifacts $true | Out-Null
                }
                catch { $duplicateRejected=$_.Exception.Message -like 'retained_analysis_attempt_receipt_already_terminal:*' }
                if(-not $duplicateRejected -or (Get-FileTransferEvidenceSha256 $receiptPath) -cne $receiptHash) { throw 'Terminal receipt replacement admitted' }
                if($slotTransport) {
                    $consumed=$script:slot.TakeAfterExit()
                    $expectedSlot=$optional -cin @('slot-completed','native-completed')
                    if(($null -ne $consumed) -ne $expectedSlot -or ($expectedSlot -and
                        [Text.Encoding]::UTF8.GetString($consumed) -cne 'prepared-lifecycle-only-payload') -or
                        $null -ne $script:slot.TakeAfterExit()) { throw 'Slot read after primary receipt/disposal failed' }
                    if($optional -cne 'none') {
                        $offer=[IO.File]::ReadAllText((Join-Path $casePath 'slot-offer.json')) | ConvertFrom-Json
                        if($offer.offered -ne $expectedSlot -or ($optional -ceq 'slot-contended' -and $offer.elapsedMilliseconds -gt 500)) {
                            throw 'Optional offer blocked primary exit or admitted a rejected candidate'
                        }
                        if($nativeCapture) {
                            $native=[IO.File]::ReadAllText((Join-Path $casePath 'native-work.json')) | ConvertFrom-Json
                            if($native.prepared -ne $expectedSlot -or $native.recordVisits -ne $(if($expectedSlot) { 4 } else { 0 }) -or
                                ($optional -ceq 'native-budget' -and $native.reason -cne 'active_work_budget') -or
                                ($optional -ceq 'native-cancelled' -and $native.reason -cne 'cancelled')) { throw 'Native preparation omission changed' }
                        }
                    }
                    if((Get-FileTransferEvidenceSha256 $receiptPath) -cne $receiptHash -or
                        (@(foreach($name in $primaryFiles) { Get-FileTransferEvidenceSha256 (Join-Path $casePath $name) }) -join '|') -cne ($hashes -join '|')) {
                        throw 'Slot consumption mutated frozen primary'
                    }
                    $script:slot.Dispose(); $script:slot=$null
                }
                $rows.Add([ordered]@{Owner=$owner;Outcome=$outcome;Optional=$optional;Status=$receipt.status;WorkerExited=$true
                    ReceiptFrozen=$true;PrimaryBytesPreserved=($optional -cne 'stall');PreExitSafe=($optional -cnotin @('stall','nonzero'))})
                if($readLeaseMode) {
                    if($null -eq $script:inputReadLease -or -not $script:inputReadLease.IsVerified -or
                        $script:inputReadLease.ExpectedSha256 -cne $receipt.analyzerInputSha256Before -or
                        (Get-FileTransferEvidenceLineCount $log) -ne 1) { throw 'Read-only source owner did not survive the original parent/receipt path' }
                    $script:inputReadLease.Dispose(); $script:inputReadLease=$null
                    $released=[IO.File]::Open($log,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
                    $released.Dispose()
                }
                if($sourceRequestMode) {
                    $source=[IO.File]::ReadAllText((Join-Path $casePath 'source-request-control.json')) | ConvertFrom-Json
                    $expectedSource=$optional -cnotin @('none','caught-failure')
                    if($source.attached -ne $expectedSource -or $source.complete -ne $expectedSource -or
                        ($expectedSource -and ($source.requestId.Replace('-','') -cne $receipt.attemptId -or
                            $source.sourceSha256 -cne $receipt.analyzerInputSha256Before -or $source.lines -ne 1 -or $source.owner -cne $owner))) {
                        throw 'Actual parent/worker source request did not preserve attempt ownership'
                    }
                    $released=[IO.File]::Open($log,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
                    $released.Dispose()
                    if($optional -cne 'none') {
                        $stillOpen=$null
                        try { $stillOpen=[Threading.Mutex]::OpenExisting($source.name+'.gate') } catch [Threading.WaitHandleCannotBeOpenedException] { }
                        if($null -ne $stillOpen) { $stillOpen.Dispose(); throw 'Parent source request survived primary finally' }
                    }
                }
            }
        }
    }
}
finally {
    if($readLeaseMode -and $null -ne $script:inputReadLease) { $script:inputReadLease.Dispose(); $script:inputReadLease=$null }
    if($slotTransport) {
        if($null -ne $script:slotGate) { $script:slotGate.ReleaseMutex(); $script:slotGate.Dispose() }
        if($null -ne $script:slot) { $script:slot.Dispose() }
    }
    Set-Location -LiteralPath $originalLocation.Path
    $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID=$originalRun
    $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID=$originalScenario
}
'local-carrier-handoff='+ (ConvertTo-Json -InputObject ([ordered]@{Cases=$rows.Count;Rows=@($rows.ToArray());ProductionWatchdogSeconds=1800
    TestWatchdogSeconds=$testWatchdog;ProductionCarrierWired=$false;AuthorityEvaluations=0;PreExitHandoffGate='not_satisfied'}) -Depth 6 -Compress)
