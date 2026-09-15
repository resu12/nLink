param([string]$RepoRoot, [string]$ArtifactRoot)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$tokens=$null; $errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$tokens, [ref]$errors)
if (@($errors).Count) { throw 'GUI parse failed' }
foreach ($name in @('Cleanup-ProcessesWithOperationalLogDrain','Write-GuiSmokeAtomicJson')) {
    $definition=$ast.Find({param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name
    }, $true)
    Invoke-Expression $definition.Extent.Text
}
$script:actions=[Collections.Generic.List[string]]::new()
$script:waits=[Collections.Generic.List[int]]::new()
$script:processes=@{}
function New-FakeProcess([int]$Id,[string]$Mode) {
    $process=[pscustomobject]@{Id=$Id;Mode=$Mode;Exited=($Mode -eq 'already_exited')}
    $process | Add-Member ScriptProperty HasExited {
        $script:actions.Add("state:$($this.Id)")
        return $this.Exited
    }
    $process | Add-Member ScriptMethod CloseMainWindow {
        $script:actions.Add("close:$($this.Id)")
        if ($this.Mode -eq 'close_exception') { throw 'private close error' }
        if ($this.Mode -eq 'close_exits') { $this.Exited=$true }
        return $this.Mode -ne 'close_rejected'
    }
    $process | Add-Member ScriptMethod WaitForExit { param([int]$Milliseconds)
        $script:actions.Add("wait:$($this.Id)"); $script:waits.Add($Milliseconds)
        if ($this.Mode -eq 'wait_exception') { throw 'private wait error' }
        if ($this.Mode -eq 'exhausts_budget') { [Threading.Thread]::Sleep(65) }
        if ($this.Mode -eq 'wait_exits') { $this.Exited=$true }
        return $this.Exited
    }
    $script:processes[$Id]=$process
    return $process
}
function Stop-Process { param([int]$Id,[switch]$Force,[string]$ErrorAction)
    $script:actions.Add("stop:$Id")
    if (-not $Force -or $ErrorAction -cne 'SilentlyContinue') { throw 'Stop policy changed' }
    if ($script:processes[$Id].Mode -eq 'stop_exception') { throw 'private stop error' }
    $script:processes[$Id].Exited=$true
}
function Stop-BridgeNodeProcessesForExePath { param([string]$ExePath)
    $script:actions.Add("bridge:$ExePath")
}
function Invoke-CleanupCase([string[]]$Modes,[bool]$Capture,[int]$Timeout=7000,[switch]$Duplicate) {
    $script:actions.Clear(); $script:waits.Clear(); $script:processes=@{}
    $targets=@(for($i=0;$i -lt $Modes.Count;$i++) { New-FakeProcess ($i+1) $Modes[$i] })
    if ($Duplicate) { $targets=@($targets[0],$targets[0]) }
    $detail=$null; $arguments=@{}
    if ($Capture -and (Get-Command Cleanup-ProcessesWithOperationalLogDrain).Parameters.ContainsKey('CleanupDiagnostic')) {
        $arguments.CleanupDiagnostic=[ref]$detail
    }
    $output=@(Cleanup-ProcessesWithOperationalLogDrain -Processes $targets -ExePath fixture-exe -TimeoutMs $Timeout @arguments)
    if ($output.Count -ne 0) { throw 'Cleanup polluted its process-output pipeline' }
    return [pscustomobject]@{Detail=$detail;Actions=@($script:actions.ToArray());Waits=@($script:waits.ToArray())}
}
$expected=@{
    already_exited=@('already_exited','already_exited','already_exited')
    close_exits=@('accepted','already_exited','already_exited')
    wait_exits=@('accepted','exited','already_exited')
    never_exits=@('accepted','elapsed','requested')
    close_rejected=@('not_accepted','elapsed','requested')
    close_exception=@('exception','elapsed','requested')
    wait_exception=@('accepted','exception','requested')
    stop_exception=@('accepted','elapsed','exception')
}
foreach ($mode in $expected.Keys) {
    $without=Invoke-CleanupCase @($mode,'wait_exits') $false
    $with=Invoke-CleanupCase @($mode,'wait_exits') $true
    if ($null -eq $with.Detail) { throw "Cleanup outcome missing: $mode" }
    if (($without.Actions -join '|') -cne ($with.Actions -join '|')) { throw "Actions changed: $mode" }
    $expectedActions=@('state:1')
    if ($mode -ne 'already_exited') { $expectedActions+='close:1' }
    $expectedActions+=@('state:2','close:2','state:1')
    if ($mode -notin @('already_exited','close_exits')) { $expectedActions+='wait:1' }
    $expectedActions+=@('state:2','wait:2','state:1')
    if ($mode -notin @('already_exited','close_exits','wait_exits')) { $expectedActions+='stop:1' }
    $expectedActions+=@('state:2','bridge:fixture-exe')
    if (($with.Actions -join '|') -cne ($expectedActions -join '|')) { throw "Original action order or exit polls changed: $mode" }
    $detail=$with.Detail; $row=$detail.processes[0]
    if ($detail.schemaVersion -ne 1 -or $detail.authorityEffect -cne 'none' -or
        $detail.sharedWaitBudgetMilliseconds -ne 7000 -or @($detail.processes).Count -ne 2 -or
        $row.targetOrdinal -ne 0 -or $row.processId -ne 1 -or
        (@($row.closeOutcome,$row.waitOutcome,$row.stopOutcome) -join '|') -cne ($expected[$mode] -join '|')) {
        throw "Incorrect cleanup outcome: $mode"
    }
    if (@($with.Waits | Where-Object {$_ -lt 1 -or $_ -gt 7000}).Count -ne 0) { throw 'Wait budget changed' }
    if (($row.waitOutcome -eq 'already_exited') -ne ($row.waitBudgetMilliseconds -eq 0)) { throw 'Unobserved wait budget misreported' }
    $json=$detail | ConvertTo-Json -Depth 8
    if ([Text.Encoding]::UTF8.GetByteCount($json) -gt 4096 -or $json -match 'private .* error|fixture-exe|writer_drained') {
        throw 'Cleanup diagnostic leaked data, authority, or exceeded its bound'
    }
}
$budget=Invoke-CleanupCase @('exhausts_budget','never_exits') $true 50
if ($budget.Waits.Count -ne 2 -or $budget.Waits[1] -ne 1 -or
    $budget.Detail.processes[1].waitBudgetMilliseconds -ne 1) { throw 'Shared wait budget was renewed' }
$zero=Invoke-CleanupCase @('never_exits','never_exits') $true 0
if (($zero.Waits -join '|') -cne '1|1') { throw 'Original expired-budget clamp changed' }
$duplicate=Invoke-CleanupCase @('wait_exits') $true -Duplicate
if (@($duplicate.Detail.processes).Count -ne 2 -or $duplicate.Detail.processes[1].targetOrdinal -ne 1 -or
    $duplicate.Detail.processes[1].waitOutcome -ne 'already_exited') { throw 'Duplicate targets were conflated' }
$oversized=Invoke-CleanupCase @('never_exits','never_exits','never_exits') $true
if ($null -ne $oversized.Detail -or @($oversized.Actions | Where-Object {$_ -like 'stop:*'}).Count -ne 3) {
    throw 'Oversized diagnostic changed cleanup or was admitted'
}
$occupied='existing'; $process=New-FakeProcess 9 wait_exits
Cleanup-ProcessesWithOperationalLogDrain -Processes @($process) -CleanupDiagnostic ([ref]$occupied)
if ($occupied -cne 'existing' -or -not $process.Exited) { throw 'Occupied output was overwritten or cleanup skipped' }
$empty=$null
Cleanup-ProcessesWithOperationalLogDrain -Processes @() -CleanupDiagnostic ([ref]$empty)
if ($null -ne $empty) { throw 'Empty cleanup invented observations' }

# Run the actual finalizer callback, not GUI interaction. It must return only
# the original app objects even when the optional artifact cannot be written.
$scenario=$ast.Find({param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Run-ScenarioFileTransferTunaHandoffFallback'
},$true)
$callback=$scenario.Find({param($node)
    $node -is [Management.Automation.Language.ScriptBlockExpressionAst] -and
    $node.Extent.Text -match '\$processesToDrain = @\(\$Context.Processes.ToArray\(\)\)'
},$true)
$callbackBlock=[scriptblock]::Create($callback.ScriptBlock.Extent.Text.TrimStart('{').TrimEnd('}'))
$artifactDir=$ArtifactRoot
$Context=[pscustomobject]@{Processes=[Collections.Generic.List[object]]::new();ExePath='fixture-exe'}
foreach($mode in @('never_exits','wait_exits')) { $Context.Processes.Add((New-FakeProcess ($Context.Processes.Count+1) $mode)) }
$returned=@(& $callbackBlock)
$path=Join-Path $artifactDir 'filetransfer-gui-process-cleanup-diagnostic.json'
if (-not (Test-Path -LiteralPath $path)) { throw 'Finalizer did not persist cleanup diagnostic' }
if ((Get-Item -LiteralPath $path).Length -gt 4096) { throw 'Persisted cleanup diagnostic exceeded its bound' }
$persisted=Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
if ($persisted.processes[0].stopOutcome -cne 'requested' -or $returned.Count -ne 2 -or
    -not [object]::ReferenceEquals($returned[0],$Context.Processes[0])) { throw 'Callback changed process ownership or outcomes' }
function Write-GuiSmokeAtomicJson { throw 'diagnostic disk unavailable' }
$returned=@(& $callbackBlock)
if ($returned.Count -ne 2 -or -not [object]::ReferenceEquals($returned[1],$Context.Processes[1])) {
    throw 'Optional write failure changed finalizer process handoff'
}
$script:actions.Clear(); $empty=$null
Cleanup-ProcessesWithOperationalLogDrain -Processes @($null) -CleanupDiagnostic ([ref]$empty)
if ($null -ne $empty -or ($script:actions -join '|') -cne 'bridge:') { throw 'Null-target filtering changed bridge cleanup' }
function Stop-BridgeNodeProcessesForExePath { throw 'original bridge cleanup failure' }
foreach ($capture in @($false,$true)) {
    $process=New-FakeProcess 1 wait_exits; $detail=$null; $arguments=@{}
    if ($capture) { $arguments.CleanupDiagnostic=[ref]$detail }
    $caught=$null
    try { Cleanup-ProcessesWithOperationalLogDrain -Processes @($process) @arguments }
    catch { $caught=$_.Exception.Message }
    if ($caught -cne 'original bridge cleanup failure') { throw 'Diagnostic changed original cleanup exception' }
}
Write-Output 'gui-process-cleanup-diagnostic-ok'
