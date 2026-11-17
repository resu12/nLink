param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
$wrapper=Read-AttemptAst (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchWrapper.ps1')
$ast=Read-AttemptAst (Join-Path $RepoRoot 'tools/Run-FileTransferTunaGuiSmoke.ps1')
foreach($name in @('NLINK_TEST_PAYLOAD_WRAPPER_CASE','NLINK_TEST_PRODUCTION_WRAPPER_PAYLOAD','NLINK_TEST_PRODUCTION_PARENT_PAYLOAD',
    'NLINK_FILETRANSFER_GUI_FINALIZATION_GRACE_SECONDS_TEST_ONLY')) {Set-Item -LiteralPath ('Env:'+$name) -Value ''}
$load=@($wrapper.EndBlock.Statements|Where-Object {$_ -is [Management.Automation.Language.ForEachStatementAst] -and
    $_.Extent.Text.StartsWith('foreach($name in @(''ConvertTo-TunaGuiProcessArgument''')})
if($load.Count -ne 1){throw 'Original fixture function loader ambiguous'}
Invoke-Expression $load[0].Extent.Text
foreach($name in @('Request-TunaGuiScenarioDeadline','Stop-TunaGuiSmokeApplicationDescendants')){
    if((Get-Command $name).ScriptBlock.Ast.Extent.Text -cne (Get-AttemptFunction $ast $name)){throw ('Fixture did not load original callback '+$name)}
    if(@($wrapper.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true)).Count){throw 'Fixture callback override returned'}
}
Invoke-Expression (Get-AttemptFunction $wrapper 'Stop-TunaGuiSmokeProcessTree')
$watchdog=(Get-Command Invoke-TunaGuiSmokeWithHardDeadline).ScriptBlock.Ast
$loops=@($watchdog.FindAll({param($n) $n -is [Management.Automation.Language.WhileStatementAst] -and
    $n.Extent.Text.Contains('if (-not $scenarioDeadlineRequested -and $nowUtc -ge $scenarioDeadlineUtc)')},$true))
$originalLoops=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.WhileStatementAst] -and
    $n.Extent.Text.Contains('if (-not $scenarioDeadlineRequested -and $nowUtc -ge $scenarioDeadlineUtc)')},$true))
if($loops.Count -ne 1 -or $originalLoops.Count -ne 1 -or $loops[0].Extent.Text -cne $originalLoops[0].Extent.Text){throw 'Original watchdog loop changed'}
# Only the diagnostic clock and process handles are projected in these controls.
$loop=Replace-AttemptSeam $loops[0].Extent.Text '$nowUtc = [DateTimeOffset]::UtcNow' '$nowUtc = $script:clock'
$descendants=Replace-AttemptSeam (Get-AttemptFunction $ast 'Stop-TunaGuiSmokeApplicationDescendants') '$application = [System.Diagnostics.Process]::GetProcessById([int]$target.ProcessId)' '$application = New-DeadlineControlApplication ([int]$target.ProcessId)'
Invoke-Expression $descendants
$script:closed=[Collections.Generic.List[int]]::new();$script:stopped=[Collections.Generic.List[int]]::new()
$script:snapshot=@();$script:metadataReads=0;$script:handleDisposals=0
function New-DeadlineControlApplication([int]$Id){
    $app=[pscustomobject]@{Id=$Id}
    $app|Add-Member ScriptMethod CloseMainWindow {$script:closed.Add($this.Id);return $true}
    $app|Add-Member ScriptMethod Dispose {$script:handleDisposals++}
    return $app
}
function Get-CimInstance {
    [CmdletBinding()]param($ClassName,[string]$Filter)
    $script:metadataReads++
    if($Case -ceq 'metadata-failure'){throw 'controlled process metadata failure'}
    if($Filter){
        $idValue=[int]($Filter -replace '\D','')
        $match=@($script:snapshot|Where-Object ProcessId -EQ $idValue)
        if($Case -ceq 'descendant-reused' -and $idValue -eq 102){return [pscustomobject]@{CreationDate='new-process'}}
        return $match
    }
    return $script:snapshot
}
function Get-Process {[CmdletBinding()]param([int]$Id) return $null}
function Stop-Process {[CmdletBinding()]param([int]$Id,[switch]$Force) $script:stopped.Add($Id)}
$ExePath=Join-Path $Directory 'headless-worker.exe'
$process=[pscustomobject]@{Id=101;HasExited=$false;Kills=0;Waits=[Collections.Generic.List[int]]::new()}
$process|Add-Member ScriptMethod Refresh {if($script:clock -ge $script:exitAt){$this.HasExited=$true}}
$process|Add-Member ScriptMethod WaitForExit {
    param([int]$Milliseconds)
    $this.Waits.Add($Milliseconds)
    if(!$this.HasExited){$script:clock=$script:clock.AddMilliseconds($Milliseconds)}
    $this.Refresh();return $this.HasExited
}
$process|Add-Member ScriptMethod Kill {$this.Kills++;$this.HasExited=$true}
$env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID='deadline-control-acceptance'
$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID='deadline-control-acceptance:exact-scenario:1'
$scenarioDeadlineRequestPath=Join-Path $Directory 'filetransfer-gui-scenario-deadline-request.json'
$terminalPhaseDeadlinePath=Join-Path $Directory 'filetransfer-gui-terminal-phase-deadline.json'
$statePath=Join-Path $Directory 'filetransfer-gui-evidence-finalization-state.json'
$ArtifactDir=$Directory
$startedUtc=[DateTimeOffset]::UtcNow;$TimeoutSeconds=210;$finalizationGraceSeconds=Get-TunaGuiFinalizationGraceSeconds
$originalScenarioDeadlineUtc=$startedUtc.AddSeconds($TimeoutSeconds);$scenarioDeadlineUtc=$originalScenarioDeadlineUtc
$deadlineUtc=$scenarioDeadlineUtc.AddSeconds($finalizationGraceSeconds)
$hardDeadlineSeconds=$TimeoutSeconds+$finalizationGraceSeconds;$maximumHardDeadlineSeconds=2*$TimeoutSeconds+$finalizationGraceSeconds
if($finalizationGraceSeconds -ne 120 -or $hardDeadlineSeconds -ne 330 -or $maximumHardDeadlineSeconds -ne 540){throw 'Original deadline defaults changed'}
$initial=[ordered]@{scenarioDeadlineRequested=$false;scenarioTimeoutSeconds=210;finalizationGraceSeconds=120;hardDeadlineSeconds=330;
    maximumHardDeadlineSeconds=540;captureMarker='unchanged';lastCompletedStage='gui_child_started'}
Write-TunaGuiAtomicJson -Path $statePath -Value $initial
if($Case -ceq 'state-unreadable'){[IO.File]::WriteAllText($statePath,'invalid-json')}
$script:atomicWriter=(Get-Command Write-TunaGuiAtomicJson).ScriptBlock
$script:writes=[Collections.Generic.List[string]]::new();$script:requestWrites=0
function Write-TunaGuiAtomicJson {
    param($Path,$Value)
    $script:writes.Add($Path)
    if($Path -ceq $scenarioDeadlineRequestPath){
        $script:requestWrites++
        if($Case -cin @('request-failure','loop-request-failure')){throw 'controlled request-write failure'}
    }
    & $script:atomicWriter -Path $Path -Value $Value
}
$script:clock=$startedUtc;$script:exitAt=[DateTimeOffset]::MaxValue
$timedOut=$false;$scenarioDeadlineRequested=$false;$scenarioDeadlineRequestedUtc=''
$terminalPhaseDeadlineAdopted=$false;$terminalPhaseDeadlineUtc=''
$private:frontierHandoffRequested=$false;$private:frontierRelayRequested=$false;$private:frontierParentCustody=$null;$private:frontierGuiReports=$null
$caught=$null
if($Case -cin @('descendants','descendant-reused','metadata-failure')){
    $script:snapshot=@(
        [pscustomobject]@{ProcessId=101;ParentProcessId=1;ExecutablePath=$ExePath;CreationDate='root'},
        [pscustomobject]@{ProcessId=102;ParentProcessId=101;ExecutablePath=$ExePath;CreationDate='child'},
        [pscustomobject]@{ProcessId=103;ParentProcessId=102;ExecutablePath=$ExePath;CreationDate='grandchild'},
        [pscustomobject]@{ProcessId=104;ParentProcessId=101;ExecutablePath=($ExePath+'.different');CreationDate='wrong-exe'},
        [pscustomobject]@{ProcessId=105;ParentProcessId=1;ExecutablePath=$ExePath;CreationDate='unrelated'})
    Stop-TunaGuiSmokeApplicationDescendants -GuiChildProcess $process -ExePath $ExePath -GraceMilliseconds 0
    $expectedStops=@(if($Case -ceq 'descendants'){102;103}elseif($Case -ceq 'descendant-reused'){103})
    $expectedClosed=@(if($Case -cne 'metadata-failure'){102;103})
    if(@(Compare-Object @($expectedStops) @($script:stopped)).Count -or
        @(Compare-Object @($expectedClosed) @($script:closed)).Count -or $script:handleDisposals -ne $expectedClosed.Count){throw 'Descendant identity/cleanup scope changed'}
} else {
    try {
        if($Case -cin @('request','state-unreadable','request-failure')){
            Request-TunaGuiScenarioDeadline -RequestPath $scenarioDeadlineRequestPath -StatePath $statePath -ArtifactDir $Directory -GuiChildProcess $process -ScenarioDeadlineUtc $scenarioDeadlineUtc
        } else {
            $script:exitAt=switch -CaseSensitive ($Case) {
                'exit-before' {$startedUtc.AddSeconds(209)}
                'scenario-race' {$startedUtc.AddSeconds(210)}
                'grace-exit' {$startedUtc.AddSeconds(215)}
                'hard-race' {$startedUtc.AddSeconds(330)}
                'hard-timeout' {[DateTimeOffset]::MaxValue}
                'loop-request-failure' {[DateTimeOffset]::MaxValue}
                default {throw 'Unknown deadline control'}
            }
            Invoke-Expression $loop
        }
    } catch {$caught=$_}
    $failure=$Case -cin @('request-failure','loop-request-failure')
    if($failure){
        if($null -eq $caught -or $caught.Exception.Message -cne 'controlled request-write failure' -or (Test-Path -LiteralPath $scenarioDeadlineRequestPath) -or
            $process.Kills -ne 0 -or $script:metadataReads -ne 0){throw 'Request failure was hidden/promoted or cleanup continued'}
    } elseif($null -ne $caught){throw $caught}
    $requested=$Case -cnotin @('exit-before','scenario-race')
    if($script:requestWrites -ne [int]$requested){throw 'Deadline request omitted or repeated'}
    if($requested -and !$failure){
        $request=Get-Content -LiteralPath $scenarioDeadlineRequestPath -Raw|ConvertFrom-Json
        if($request.event -cne 'filetransfer_gui_scenario_deadline_requested' -or $request.schemaVersion -ne 1 -or
            $request.acceptanceRunId -cne $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID -or
            $request.scenarioRunId -cne $env:NLINK_FILETRANSFER_SCENARIO_RUN_ID -or $request.artifactDir -cne $Directory -or
            $request.guiChildProcessId -ne 101 -or [DateTimeOffset]$request.scenarioDeadlineUtc -ne $scenarioDeadlineUtc -or
            $request.reason -cne 'gui_smoke_scenario_deadline_exceeded' -or @($request.PSObject.Properties).Count -ne 9){throw 'Deadline request correlation/shape changed'}
        if($Case -cne 'state-unreadable'){
            $state=Get-Content -LiteralPath $statePath -Raw|ConvertFrom-Json
            if(!$state.scenarioDeadlineRequested -or $state.captureMarker -cne 'unchanged' -or $script:writes[0] -cne $statePath -or
                $script:writes[1] -cne $scenarioDeadlineRequestPath){throw 'Request/state publication order changed'}
        }
    }
    if($Case -ceq 'state-unreadable' -and [IO.File]::ReadAllText($statePath) -cne 'invalid-json'){throw 'Unreadable state evidence replaced'}
    if($Case -cin @('exit-before','scenario-race','grace-exit','hard-race','hard-timeout')){
        $hard=$Case -ceq 'hard-timeout'
        if($timedOut -ne $hard -or $process.Kills -ne [int]$hard -or !$process.HasExited -or
            $scenarioDeadlineRequested -ne $requested -or $script:metadataReads -ne [int]$requested -or
            $deadlineUtc -ne $startedUtc.AddSeconds(330) -or $hardDeadlineSeconds -ne 330 -or $maximumHardDeadlineSeconds -ne 540){throw 'Watchdog grace/hard bound or exit race changed'}
        if($hard -and ($script:clock -ne $deadlineUtc -or @($process.Waits|Where-Object {$_ -eq 5000}).Count -ne 1)){throw 'Bounded hard cleanup changed'}
    }
}
[pscustomobject]@{case=$Case;originalCallbacksLoaded=$true;originalWatchdogLoopVerified=$true;scenarioSeconds=210;graceSeconds=120;hardSeconds=330;
    maximumSeconds=540;requestWrites=$script:requestWrites;metadataReads=$script:metadataReads;closed=@($script:closed);stopped=@($script:stopped);
    hardTimedOut=$timedOut;kills=$process.Kills;requestFailurePreserved=($null -ne $caught);virtualElapsedSeconds=($script:clock-$startedUtc).TotalSeconds;
    qualification='Process/clock doubles only; no app launch, network, native custody or full composition.'}|ConvertTo-Json -Compress
'wrapper-deadline-control='+$Case
