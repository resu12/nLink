param([string]$RepoRoot,[string]$Directory,$AcceptedCompletion = $null,
    [string]$GeneratedSource='', [string]$GeneratedRun='', [string]$GeneratedScenario='',
    [AllowNull()]$ForwardRelay=$null,[string]$SingleCase='valid',[switch]$FrontierLaunch,
    [AllowNull()]$TerminalCollectorFiles=$null,[string]$CollectorMutation='',[switch]$CollectorFinalizer,
    [switch]$CollectorOutputs,$ForwardCollectorOutputs=$null)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Set-Variable -Name ForwardRelay -Option Private -Value $ForwardRelay
if($CollectorOutputs -and (-not $CollectorFinalizer -or $SingleCase -cnotin @('valid','nonzero','output-fails','capture-expired'))) {throw 'Invalid collector output scope'}
if($null -ne $ForwardCollectorOutputs -and ($ForwardCollectorOutputs -isnot [ref] -or -not $CollectorOutputs -or
    $SingleCase -cne 'valid' -or $null -ne $ForwardCollectorOutputs.Value)) {throw 'Invalid collector output forwarding scope'}
if($null -ne $TerminalCollectorFiles -and ($AcceptedCompletion -isnot [ref] -or ($SingleCase -cne 'valid' -and -not $CollectorOutputs) -or
    -not $GeneratedSource -or $FrontierLaunch -or $null -ne $ForwardRelay)) {throw 'Invalid native collector fixture scope'}
if($CollectorMutation -cnotin @('','request','identity','truncated') -or ($CollectorMutation -and $null -eq $TerminalCollectorFiles)) {
    throw 'Invalid native collector mutation'
}
$script:terminalCollectorFiles=$TerminalCollectorFiles
if($CollectorFinalizer -and ($null -eq $TerminalCollectorFiles -or $CollectorMutation)) {throw 'Invalid collector finalizer scope'}
$script:terminalCollectorMutation=$CollectorMutation
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/Run-FileTransferTunaGuiSmoke.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count) { throw 'Actual parent parse failed' }
function Definition([string]$Name) {
    $matches=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $Name},$true))
    if($matches.Count -ne 1) { throw "Actual parent function ambiguous: $Name" }
    return $matches[0].Extent.Text
}
foreach($name in @('ConvertTo-TunaGuiProcessArgument','Write-TunaGuiAtomicJson','Get-TunaGuiFinalizationGraceSeconds',
    'Complete-TunaGuiChildOutputCapture','Get-TunaGuiTerminalPhaseDeadlineRequest')) { Invoke-Expression (Definition $name) }
$originalAtomic=${function:Write-TunaGuiAtomicJson}; $originalOutput=${function:Complete-TunaGuiChildOutputCapture}
function Write-TunaGuiAtomicJson {
    param($Path,$Value)
    if($null -ne (Get-Variable frontierGuiReports -ErrorAction SilentlyContinue)) {throw 'Private native GUI reports leaked into formatter'}
    if($script:mode -ceq 'parent-state-fails' -and $Value.lastCompletedStage -ceq 'gui_child_exited') { throw 'injected parent persistence failure' }
    & $originalAtomic -Path $Path -Value $Value
    if($Value.lastCompletedStage -ceq 'gui_child_exited' -and $script:mode -cin @('packet-overflow','packet-truncated','packet-stage')) {
        $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($script:requestName); $view=$map.CreateViewAccessor()
        try {
            if($script:mode -ceq 'packet-stage') { $view.Write(48L,1) }
            else { $view.Write(52L,$(if($script:mode -ceq 'packet-overflow') {65537} else {1})) }
        } finally { $view.Dispose(); $map.Dispose() }
    }
}
function Complete-TunaGuiChildOutputCapture {
    param($StdoutTask,$StderrTask,$StdoutPath,$StderrPath)
    $output=& $originalOutput @PSBoundParameters
    if($script:mode -ceq 'output-fails') { $output.Completed=$false }
    return $output
}
# Only the controlled child exists. Never invoke WMI/GUI descendant discovery.
function Stop-TunaGuiSmokeProcessTree { param($Process) $Process.Kill(); [void]$Process.WaitForExit(5000) }
function Stop-TunaGuiSmokeApplicationDescendants { }
function Request-TunaGuiScenarioDeadline { }
$parent=Definition 'Invoke-TunaGuiSmokeWithHardDeadline'
$needle="(Join-Path `$RepoRoot 'tools\GuiSmoke-Windows.ps1')"
if($parent.Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) { throw 'Child launch seam changed' }
$parent=$parent.Replace($needle,"(Join-Path `$RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalGuiWriterWorker.ps1')")
$parent=$parent.Replace("`$startInfo.FileName = 'powershell'","`$startInfo.FileName = (Get-Process -Id `$PID).Path")
if($null -ne $TerminalCollectorFiles) {
    $redirect='$startInfo.RedirectStandardError = $true'
    if($parent.Split([string[]]@($redirect),[StringSplitOptions]::None).Length -ne 2) {throw 'Collector input seam changed'}
    $parent=$parent.Replace($redirect,($redirect+"`n`$startInfo.RedirectStandardInput = `$true"))
}
$exitRead='$capturedExitCode = $process.ExitCode'
if($parent.Split([string[]]@($exitRead),[StringSplitOptions]::None).Length -ne 2) { throw 'Original exit seam changed' }
$parent=$parent.Replace($exitRead,('if ($script:mode -ceq ''unknown-exit'') { throw ''injected unavailable exit'' }; '+$exitRead))
$bind='if ($null -ne $localWriterRequest) { [void]$localWriterRequest.BindWorker($process) }'
if($parent.Split([string[]]@($bind),[StringSplitOptions]::None).Length -ne 2) { throw 'Exact parent bind seam changed' }
$parent=$parent.Replace($bind,($bind+@'

$script:ownedRequest = $localWriterRequest
if ($null -ne $localWriterRequest) {
    $script:requestName = $localWriterRequest.Name
    if ($null -ne $script:terminalCollectorFiles) {
        $copy=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($localWriterRequest,@())
        if($copy.SendTerminalCollectorLogForTest($script:terminalCollectorFiles,$process,'')) {throw 'Copied GUI request sent log'}
        $parentProcess=[Diagnostics.Process]::GetCurrentProcess()
        try {
            if($localWriterRequest.SendTerminalCollectorLogForTest($script:terminalCollectorFiles,$parentProcess,'')) {throw 'Unbound collector accepted'}
        } finally {$parentProcess.Dispose()}
        $copiedFiles=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($script:terminalCollectorFiles,@())
        if($localWriterRequest.SendTerminalCollectorLogForTest($copiedFiles,$process,'')) {throw 'Copied file owner sent log'}
        if(-not $localWriterRequest.SendTerminalCollectorLogForTest($script:terminalCollectorFiles,$process,$script:terminalCollectorMutation)) {
            throw 'Original collector log send failed'
        }
        if($localWriterRequest.SendTerminalCollectorLogForTest($script:terminalCollectorFiles,$process,'')) {throw 'Repeated collector send admitted'}
    }
    if ($script:mode -ceq 'closed') { $localWriterRequest.Dispose() }
    if ($script:mode -ceq 'early-adopt') { if ($null -ne $localWriterRequest.TakeAfterExit()) { throw 'Live worker adopted' } }
    if ($script:mode -ceq 'gate-contended') {
        $script:heldGate = [Threading.Mutex]::OpenExisting($localWriterRequest.Name+'.gate')
        if (-not $script:heldGate.WaitOne(0)) { throw 'Contention control unavailable' }
    }
}
'@))
if($CollectorOutputs) {$parent=& (Join-Path $PSScriptRoot 'TerminalCollectorOutputs.ps1') $parent}
Invoke-Expression $parent
if($FrontierLaunch) {
    $nativeRead='$stderrTask = $private:frontierGuiReports.Stderr'
    if($parent.Split([string[]]@($nativeRead),[StringSplitOptions]::None).Length -ne 2) {throw 'Native GUI report seam changed'}
    $parent=$parent.Replace($nativeRead,($nativeRead+@'

$script:ownedRequest = $localWriterRequest
$script:requestName = $localWriterRequest.Name
$script:nativeReports = $private:frontierGuiReports
'@))
    Invoke-Expression $parent
}
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID='gui-writer-parent-run'
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='gui-writer-parent-run:scenario:1'
if($GeneratedSource) {
    $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID=$GeneratedRun
    $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID=$GeneratedScenario
}
$env:NLINK_FILETRANSFER_GUI_FINALIZATION_GRACE_SECONDS_TEST_ONLY='1'
$env:NLINK_GUI_WRITER_REQUEST='inherited-unrelated-request'
$env:NLINK_GUI_WRITER_REQUEST_ID=[guid]::NewGuid().ToString('D')
$env:NLINK_GUI_FRONTIER_START_V1='inherited-unrelated-marker'
foreach($type in @([NLink.Tools.LocalGuiWriterRequest],[NLink.Tools.LocalGuiWriterRequest+Finalizer],
    [NLink.Tools.LocalGuiWriterRequest+Completion],[NLink.Tools.LocalGuiWriterRequest+Output],[NLink.Tools.LocalGuiWriterRequest+Writer])) {
    if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object { $null -ne $_.GetSetMethod() }).Count) { throw 'Public construction/mutation exposed' }
}
foreach($invalid in @('empty-id','run','scenario','relative-root')) {
    $id=[guid]::NewGuid(); $run=$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID; $scenario=$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID; $root=$Directory
    switch($invalid) { 'empty-id' {$id=[guid]::Empty} 'run' {$run='x'*1025} 'scenario' {$scenario='(none)'} 'relative-root' {$root='relative'} }
    $bad=[NLink.Tools.LocalGuiWriterRequest]::TryCreate($id,$run,$scenario,$root)
    if($null -ne $bad) { $bad.Dispose(); throw "Invalid request admitted: $invalid" }
}
$rows=[Collections.Generic.List[object]]::new(); $baselineHashes=$null
$cases=if($AcceptedCompletion -is [ref]) { @($SingleCase) } else { @('baseline','valid','assigned','incompatible','wrong-request','wrong-scope','worker-start','parent-start','wrong-worker',
    'closed','early-adopt','gate-contended','cancel-before-complete','no-drain','no-marker','cursor-failure','final-stage-fails',
    'nonzero','unknown-exit','parent-state-fails','output-fails','packet-overflow','packet-truncated','packet-stage','timeout') }
$script:collectorOutputOwner=$null; $collectorOutputForwarded=$false
try {
foreach($script:mode in $cases) {
    $output=Join-Path $Directory $mode; [void][IO.Directory]::CreateDirectory($output)
    $settings=Join-Path $output 'settings.json'
    [IO.File]::WriteAllText($settings,(@{RepoRoot=$RepoRoot; Directory=$output; Mode=$mode;GeneratedSource=$GeneratedSource;FrontierLaunch=[bool]$FrontierLaunch;
        NativeCollector=($null -ne $TerminalCollectorFiles);CollectorFinalizer=[bool]$CollectorFinalizer;CollectorOutputs=[bool]$CollectorOutputs} | ConvertTo-Json))
    $env:NLINK_GUI_WRITER_TEST_SETTINGS=$settings
    $completion=$null; [uri]$incompatible=$null; $script:heldGate=$null; $script:ownedRequest=$null; $script:requestName=''
    if($mode -ceq 'assigned') { $completion='existing-owner' }
    $arguments=@{RepoRoot=$RepoRoot; ArtifactDir=$output; ExePath='unused-controlled-child'; TimeoutSeconds=$(if($mode -ceq 'timeout') {10} else {30})}
    if($null -ne $ForwardRelay) { $arguments.LocalWriterRelay=$ForwardRelay }
    if($mode -cne 'baseline') { $arguments.LocalWriterCompletion=[ref]$completion }
    if($FrontierLaunch) {$arguments.CaptureFrontierLaunch=$true; $script:nativeReports=$null}
    if($mode -ceq 'incompatible') { $arguments.LocalWriterCompletion=[ref]$incompatible }
    $failure=''; $result=$null
    try { $result=Invoke-TunaGuiSmokeWithHardDeadline @arguments } catch { $failure=$_.Exception.Message }
    finally { if($null -ne $script:heldGate) { $script:heldGate.ReleaseMutex(); $script:heldGate.Dispose() } }
    if($mode -ceq 'parent-state-fails') { if($failure -cne 'injected parent persistence failure') { throw 'Parent failure changed' } }
    elseif($failure) { throw "$mode : $failure" }
    $expectedCode=if($CollectorMutation -or $mode -ceq 'capture-expired') {1} elseif($mode -ceq 'nonzero') {7} elseif($mode -ceq 'unknown-exit') {-1} elseif($mode -ceq 'timeout') {124} else {0}
    if($null -ne $result -and ($result.ExitCode -ne $expectedCode -or $result.ExitCodeAvailable -ne ($mode -cne 'unknown-exit') -or $result.TimedOut -ne ($mode -ceq 'timeout'))) {
        throw "Primary child outcome changed: $mode : $([IO.File]::ReadAllText((Join-Path $output 'gui-smoke-stderr.log')))"
    }
    if($mode -ceq 'assigned') { if($completion -cne 'existing-owner') { throw 'Assigned owner replaced' } }
    elseif(($null -ne $completion) -ne ($mode -ceq 'valid' -and -not $CollectorMutation)) { throw "Wrong parent adoption: $mode" }
    if($null -ne $incompatible) { throw 'Incompatible owner changed' }
    if($null -ne $script:ownedRequest -and $null -ne $script:ownedRequest.TakeAfterExit()) { throw 'Repeated adoption succeeded' }
    if($script:requestName) {
        $existing=$null
        try { $existing=[Threading.EventWaitHandle]::OpenExisting($script:requestName+'.closed') }
        catch [Threading.WaitHandleCannotBeOpenedException] { }
        if($null -ne $existing) { $existing.Dispose(); throw 'Parent channel leaked after finally' }
    }
    if($CollectorOutputs) {
        foreach($part in @('ready','ack')) {
            $existing=$null
            try {$existing=[Threading.EventWaitHandle]::OpenExisting($script:requestName+'.terminal-outputs-v1.'+$part)}
            catch [Threading.WaitHandleCannotBeOpenedException] { }
            if($null -ne $existing) {$existing.Dispose(); throw 'Collector output channel leaked'}
        }
        if($mode -ceq 'valid') {
            if($null -eq $script:collectorOutputOwner -or -not $script:collectorOutputOwner.OwnsCompletion($completion)) {throw 'Parent output custody missing'}
            $copy=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($script:collectorOutputOwner,@())
            if($copy.OwnsCompletion($completion)) {throw 'Copied output owner admitted'}
            $copy.Dispose()
            if(-not $script:collectorOutputOwner.IsCurrent) {throw 'Copied output owner retired original'}
            $blocked=0
            foreach($descriptor in $completion.Outputs) {
                try {$write=[IO.File]::Open($descriptor.Path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]'ReadWrite,Delete'); $write.Dispose()}
                catch [IO.IOException] {if(($_.Exception.HResult -band 0xffff) -ne 32) {throw}; $blocked++}
                try {[IO.File]::Move($descriptor.Path,$descriptor.Path+'.denied-replacement')}
                catch [IO.IOException] {if(($_.Exception.HResult -band 0xffff) -ne 32) {throw}; $blocked++}
            }
            if($blocked -ne 8) {throw 'Parent output owner lost protection after child exit'}
            if($null -eq $ForwardCollectorOutputs) {
                # The default fixture ends here. The opt-in primary fixture
                # keeps this exact owner and replaces pathname-based reads.
                $script:collectorOutputOwner.Dispose()
                if($script:collectorOutputOwner.IsCurrent -or $script:collectorOutputOwner.OwnsCompletion($completion)) {throw 'Disposed output custody revived'}
                foreach($descriptor in $completion.Outputs) {
                    $write=[IO.File]::Open($descriptor.Path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]'ReadWrite,Delete'); $write.Dispose()
                }
            }
            [IO.File]::WriteAllText((Join-Path $Directory 'collector-output-parent-result.json'),([ordered]@{
                WriterRequestId=$completion.RequestId.ToString('D');OriginalCompletionOwned=$true;FourOutputsHeldAcrossSuccessfulExit=$true;
                OutputProtectionAfterSuccessfulExit=$blocked;CopiedRequestCompletionAndOwnerRejected=$true;RepeatedAdoptionRejected=$true;
                FourOutputsReleased=($null -eq $ForwardCollectorOutputs);ChannelsClosed=$true;ReleasedBeforePrimary=($null -eq $ForwardCollectorOutputs);
                PrimaryProvenanceHandoffProven=$false;WriterCopyProvenanceProven=$false;AcceptanceChanged=$false
            } | ConvertTo-Json -Compress))
        } else {
            if($null -ne $script:collectorOutputOwner -or $null -ne $completion) {throw 'Failed child adopted collector outputs'}
            $outputNames=@('filetransfer-gui-log-ledger-slice.log','filetransfer-gui-log-writer-evidence-slice.log','filetransfer-gui-semantic-evidence-slice.log')
            if($mode -cne 'capture-expired') {$outputNames+='filetransfer-retained-log-slice.log'}
            foreach($name in $outputNames) {
                $write=[IO.File]::Open((Join-Path $output $name),[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]'ReadWrite,Delete'); $write.Dispose()
            }
            if($mode -ceq 'capture-expired') {
                if([IO.File]::Exists((Join-Path $output 'filetransfer-retained-log-slice.log'))) {throw 'Expired capture published source copy'}
                foreach($name in @($script:requestName,($script:requestName+'.terminal-outputs-v1'))) {
                    $existing=$null
                    try {$existing=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name)} catch [IO.FileNotFoundException] { }
                    if($null -ne $existing) {$existing.Dispose(); throw 'Expired capture channel map leaked'}
                }
                $existing=$null
                try {$existing=[Threading.Mutex]::OpenExisting($script:requestName+'.gate')} catch [Threading.WaitHandleCannotBeOpenedException] { }
                if($null -ne $existing) {$existing.Dispose(); throw 'Expired capture channel gate leaked'}
            }
            [IO.File]::WriteAllText((Join-Path $Directory 'collector-output-rejection-result.json'),([ordered]@{
                Mode=$mode;ChildExitCode=$result.ExitCode;CompletionAccepted=$false;OutputOwnerAccepted=$false;
                FourOutputsReleased=($mode -cne 'capture-expired');RetainedOutputCount=$outputNames.Count;RetainedOutputsReleased=$true;
                ExpiryChannelMapsAndGateClosed=($mode -ceq 'capture-expired');ChannelsClosed=$true;AuthorityEvaluations=0;AcceptanceChanged=$false
            } | ConvertTo-Json -Compress))
        }
    }
    if($FrontierLaunch) {
        if($null -eq $script:nativeReports -or -not $script:nativeReports.NativeLaunchReady -or $null -eq $script:nativeReports.ParentRegistration -or
            $script:nativeReports.ParentRegistration.IsCurrent) {throw 'Original GUI parent native launch/cleanup changed'}
        foreach($part in @('ready','failed')) {
            $existing=$null; try {$existing=[Threading.EventWaitHandle]::OpenExisting($script:requestName+'.frontier-gui-start-v1.'+$part)} catch [Threading.WaitHandleCannotBeOpenedException] { }
            if($null -ne $existing) {$existing.Dispose(); throw 'Native GUI readiness signal leaked after parent cleanup'}
        }
    }
    if($CollectorMutation) {
        $expected=switch($CollectorMutation) {'request' {'native_collector_request'} 'identity' {'native_collector_identity'} 'truncated' {'native_collector_packet_truncated'}}
        $errorPath=Join-Path $output 'native-collector-error.txt'
        if(-not [IO.File]::Exists($errorPath) -or -not [IO.File]::ReadAllText($errorPath).Contains($expected) -or
            [IO.File]::Exists((Join-Path $output 'worker-result.json')) -or [IO.File]::Exists((Join-Path $output 'filetransfer-gui-log-ledger.json')) -or
            (Get-Item (Join-Path $output 'append.log')).Length -ne 0) {throw 'Native collector rejection boundary changed'}
        [IO.File]::WriteAllText((Join-Path $Directory 'native-collector-rejection-result.json'),([ordered]@{
            Mutation=$CollectorMutation;ChildExitCode=$result.ExitCode;ChildExited=$true;WriterCompletionAccepted=$false;
            CollectorReadCompleted=$false;TargetBytes=0;AuthorityEvaluations=0;AcceptanceChanged=$false
        } | ConvertTo-Json -Compress))
    }
    if($mode -ceq 'valid' -and -not $CollectorMutation) {
        $ledger=[IO.File]::ReadAllText((Join-Path $output 'filetransfer-gui-log-ledger.json')) | ConvertFrom-Json
        if($completion.ArtifactRoot -cne $output -or $completion.AcceptanceRunId -cne $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -or
            $completion.ScenarioRunId -cne $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID -or
            (-not $GeneratedSource -and ($completion.FrozenLineCount -ne 3 -or $completion.Writers.Count -ne 1 -or $completion.Writers[0].LastSequence -ne 3)) -or
            $completion.Outputs.Count -ne 4 -or
            $completion.Outputs[1].Sha256 -cne $ledger.writerEvidenceSlice.sha256 -or $completion.Outputs[3].Sha256 -cne $ledger.analyzerInput.captureSha256) { throw 'Accepted original facts changed' }
        if($null -eq $ForwardCollectorOutputs) { foreach($descriptor in $completion.Outputs) {
            $hash=[NLink.Tools.LocalInputReadLease]::HashAndHold($descriptor.Path)
            try {
                if($hash.Sha256 -cne $descriptor.Sha256 -or $hash.Lease.ByteCount -ne $descriptor.ByteCount -or
                    ($null -ne $descriptor.OriginalFileIdentity -and $hash.Lease.FileIdentity -cne $descriptor.OriginalFileIdentity)) { throw 'Independent output verification failed' }
            } finally { $hash.Lease.Dispose() }
        } }
        $state=[IO.File]::ReadAllText((Join-Path $output 'filetransfer-gui-evidence-finalization-state.json')) | ConvertFrom-Json
        if($state.lastCompletedStage -cne 'gui_child_exited' -or $completion.GuiProcessId -ne $state.guiChildProcessId -or $completion.GuiProcessCreationFileTime -le 0) { throw 'Parent/process persistence binding changed' }
        $rejected=$false; try { $completion.Writers.Clear() } catch { $rejected=$true }; if(-not $rejected) { throw 'Mutable completion collection' }
    }
    if($null -eq $ForwardCollectorOutputs -and -not $CollectorMutation -and $mode -cin @('baseline','valid','assigned','incompatible','wrong-request','worker-start','parent-start','wrong-worker','closed','early-adopt','gate-contended',
        'cancel-before-complete','final-stage-fails','nonzero','unknown-exit','parent-state-fails','output-fails','packet-overflow','packet-truncated','packet-stage')) {
        $hashes=@(foreach($name in @('filetransfer-gui-log-ledger-slice.log','filetransfer-gui-log-writer-evidence-slice.log','filetransfer-gui-semantic-evidence-slice.log','filetransfer-retained-log-slice.log')) {
            $hash=[NLink.Tools.LocalInputReadLease]::HashAndHold((Join-Path $output $name))
            try { $hash.Sha256 } finally { $hash.Lease.Dispose() }
        })
        if($mode -ceq 'baseline') { $baselineHashes=$hashes }
        elseif($null -ne $baselineHashes -and ($hashes -join '|') -cne ($baselineHashes -join '|')) { throw "Primary output bytes changed: $mode" }
    }
    if($env:NLINK_GUI_WRITER_REQUEST -cne 'inherited-unrelated-request') { throw 'Parent environment changed' }
    if($env:NLINK_GUI_FRONTIER_START_V1 -cne 'inherited-unrelated-marker') {throw 'Parent readiness environment changed'}
    $rows.Add(@{Mode=$mode; CompletionAdopted=($mode -ceq 'valid' -and -not $CollectorMutation); PrimaryExitCode=$expectedCode; ParentFailure=$failure})
    if($mode -ceq 'valid' -and $AcceptedCompletion -is [ref]) { $AcceptedCompletion.Value=$completion }
    if($null -ne $ForwardCollectorOutputs) {$ForwardCollectorOutputs.Value=$script:collectorOutputOwner; $collectorOutputForwarded=$true}
}
} finally {if($null -ne $script:collectorOutputOwner -and -not $collectorOutputForwarded) {$script:collectorOutputOwner.Dispose()}}
$script:terminalCollectorFiles=$null
'gui-writer-request='+(ConvertTo-Json -InputObject @{Cases=$rows.ToArray(); AuthorityEvaluations=0; LiveCaller=$false; PublicationCompletion=$false} -Depth 5 -Compress)
