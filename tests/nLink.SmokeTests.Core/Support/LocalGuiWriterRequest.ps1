param([string]$RepoRoot,[string]$Directory,$AcceptedCompletion = $null,
    [string]$GeneratedSource='', [string]$GeneratedRun='', [string]$GeneratedScenario='',
    [AllowNull()]$ForwardRelay=$null,[string]$SingleCase='valid')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Set-Variable -Name ForwardRelay -Option Private -Value $ForwardRelay
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
$exitRead='$capturedExitCode = $process.ExitCode'
if($parent.Split([string[]]@($exitRead),[StringSplitOptions]::None).Length -ne 2) { throw 'Original exit seam changed' }
$parent=$parent.Replace($exitRead,('if ($script:mode -ceq ''unknown-exit'') { throw ''injected unavailable exit'' }; '+$exitRead))
$bind='if ($null -ne $localWriterRequest) { [void]$localWriterRequest.BindWorker($process) }'
if($parent.Split([string[]]@($bind),[StringSplitOptions]::None).Length -ne 2) { throw 'Exact parent bind seam changed' }
$parent=$parent.Replace($bind,($bind+@'

$script:ownedRequest = $localWriterRequest
if ($null -ne $localWriterRequest) {
    $script:requestName = $localWriterRequest.Name
    if ($script:mode -ceq 'closed') { $localWriterRequest.Dispose() }
    if ($script:mode -ceq 'early-adopt') { if ($null -ne $localWriterRequest.TakeAfterExit()) { throw 'Live worker adopted' } }
    if ($script:mode -ceq 'gate-contended') {
        $script:heldGate = [Threading.Mutex]::OpenExisting($localWriterRequest.Name+'.gate')
        if (-not $script:heldGate.WaitOne(0)) { throw 'Contention control unavailable' }
    }
}
'@))
Invoke-Expression $parent
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID='gui-writer-parent-run'
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='gui-writer-parent-run:scenario:1'
if($GeneratedSource) {
    $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID=$GeneratedRun
    $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID=$GeneratedScenario
}
$env:NLINK_FILETRANSFER_GUI_FINALIZATION_GRACE_SECONDS_TEST_ONLY='1'
$env:NLINK_GUI_WRITER_REQUEST='inherited-unrelated-request'
$env:NLINK_GUI_WRITER_REQUEST_ID=[guid]::NewGuid().ToString('D')
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
foreach($script:mode in $cases) {
    $output=Join-Path $Directory $mode; [void][IO.Directory]::CreateDirectory($output)
    $settings=Join-Path $output 'settings.json'
    [IO.File]::WriteAllText($settings,(@{RepoRoot=$RepoRoot; Directory=$output; Mode=$mode;GeneratedSource=$GeneratedSource} | ConvertTo-Json))
    $env:NLINK_GUI_WRITER_TEST_SETTINGS=$settings
    $completion=$null; [uri]$incompatible=$null; $script:heldGate=$null; $script:ownedRequest=$null; $script:requestName=''
    if($mode -ceq 'assigned') { $completion='existing-owner' }
    $arguments=@{RepoRoot=$RepoRoot; ArtifactDir=$output; ExePath='unused-controlled-child'; TimeoutSeconds=$(if($mode -ceq 'timeout') {10} else {30})}
    if($null -ne $ForwardRelay) { $arguments.LocalWriterRelay=$ForwardRelay }
    if($mode -cne 'baseline') { $arguments.LocalWriterCompletion=[ref]$completion }
    if($mode -ceq 'incompatible') { $arguments.LocalWriterCompletion=[ref]$incompatible }
    $failure=''; $result=$null
    try { $result=Invoke-TunaGuiSmokeWithHardDeadline @arguments } catch { $failure=$_.Exception.Message }
    finally { if($null -ne $script:heldGate) { $script:heldGate.ReleaseMutex(); $script:heldGate.Dispose() } }
    if($mode -ceq 'parent-state-fails') { if($failure -cne 'injected parent persistence failure') { throw 'Parent failure changed' } }
    elseif($failure) { throw "$mode : $failure" }
    $expectedCode=if($mode -ceq 'nonzero') {7} elseif($mode -ceq 'unknown-exit') {-1} elseif($mode -ceq 'timeout') {124} else {0}
    if($null -ne $result -and ($result.ExitCode -ne $expectedCode -or $result.ExitCodeAvailable -ne ($mode -cne 'unknown-exit') -or $result.TimedOut -ne ($mode -ceq 'timeout'))) {
        throw "Primary child outcome changed: $mode : $([IO.File]::ReadAllText((Join-Path $output 'gui-smoke-stderr.log')))"
    }
    if($mode -ceq 'assigned') { if($completion -cne 'existing-owner') { throw 'Assigned owner replaced' } }
    elseif(($null -ne $completion) -ne ($mode -ceq 'valid')) { throw "Wrong parent adoption: $mode" }
    if($null -ne $incompatible) { throw 'Incompatible owner changed' }
    if($null -ne $script:ownedRequest -and $null -ne $script:ownedRequest.TakeAfterExit()) { throw 'Repeated adoption succeeded' }
    if($script:requestName) {
        $existing=$null
        try { $existing=[Threading.EventWaitHandle]::OpenExisting($script:requestName+'.closed') }
        catch [Threading.WaitHandleCannotBeOpenedException] { }
        if($null -ne $existing) { $existing.Dispose(); throw 'Parent channel leaked after finally' }
    }
    if($mode -ceq 'valid') {
        $ledger=[IO.File]::ReadAllText((Join-Path $output 'filetransfer-gui-log-ledger.json')) | ConvertFrom-Json
        if($completion.ArtifactRoot -cne $output -or $completion.AcceptanceRunId -cne $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -or
            $completion.ScenarioRunId -cne $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID -or
            (-not $GeneratedSource -and ($completion.FrozenLineCount -ne 3 -or $completion.Writers.Count -ne 1 -or $completion.Writers[0].LastSequence -ne 3)) -or
            $completion.Outputs.Count -ne 4 -or
            $completion.Outputs[1].Sha256 -cne $ledger.writerEvidenceSlice.sha256 -or $completion.Outputs[3].Sha256 -cne $ledger.analyzerInput.captureSha256) { throw 'Accepted original facts changed' }
        foreach($descriptor in $completion.Outputs) {
            $hash=[NLink.Tools.LocalInputReadLease]::HashAndHold($descriptor.Path)
            try {
                if($hash.Sha256 -cne $descriptor.Sha256 -or $hash.Lease.ByteCount -ne $descriptor.ByteCount -or
                    ($null -ne $descriptor.OriginalFileIdentity -and $hash.Lease.FileIdentity -cne $descriptor.OriginalFileIdentity)) { throw 'Independent output verification failed' }
            } finally { $hash.Lease.Dispose() }
        }
        $state=[IO.File]::ReadAllText((Join-Path $output 'filetransfer-gui-evidence-finalization-state.json')) | ConvertFrom-Json
        if($state.lastCompletedStage -cne 'gui_child_exited' -or $completion.GuiProcessId -ne $state.guiChildProcessId -or $completion.GuiProcessCreationFileTime -le 0) { throw 'Parent/process persistence binding changed' }
        $rejected=$false; try { $completion.Writers.Clear() } catch { $rejected=$true }; if(-not $rejected) { throw 'Mutable completion collection' }
    }
    if($mode -cin @('baseline','valid','assigned','incompatible','wrong-request','worker-start','parent-start','wrong-worker','closed','early-adopt','gate-contended',
        'cancel-before-complete','final-stage-fails','nonzero','unknown-exit','parent-state-fails','output-fails','packet-overflow','packet-truncated','packet-stage')) {
        $hashes=@(foreach($name in @('filetransfer-gui-log-ledger-slice.log','filetransfer-gui-log-writer-evidence-slice.log','filetransfer-gui-semantic-evidence-slice.log','filetransfer-retained-log-slice.log')) {
            $hash=[NLink.Tools.LocalInputReadLease]::HashAndHold((Join-Path $output $name))
            try { $hash.Sha256 } finally { $hash.Lease.Dispose() }
        })
        if($mode -ceq 'baseline') { $baselineHashes=$hashes }
        elseif($null -ne $baselineHashes -and ($hashes -join '|') -cne ($baselineHashes -join '|')) { throw "Primary output bytes changed: $mode" }
    }
    if($env:NLINK_GUI_WRITER_REQUEST -cne 'inherited-unrelated-request') { throw 'Parent environment changed' }
    $rows.Add(@{Mode=$mode; CompletionAdopted=$mode -ceq 'valid'; PrimaryExitCode=$expectedCode; ParentFailure=$failure})
    if($mode -ceq 'valid' -and $AcceptedCompletion -is [ref]) { $AcceptedCompletion.Value=$completion }
}
'gui-writer-request='+(ConvertTo-Json -InputObject @{Cases=$rows.ToArray(); AuthorityEvaluations=0; LiveCaller=$false; PublicationCompletion=$false} -Depth 5 -Compress)
