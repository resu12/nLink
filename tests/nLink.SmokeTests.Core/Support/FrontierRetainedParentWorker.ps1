param([string]$Mode,[string]$LogPath,[string]$ArtifactDir,[int]$TailMinutes,[string]$LiveRouteProofMode)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest; $ProgressPreference='SilentlyContinue'
$config=Get-Content -LiteralPath (Join-Path $ArtifactDir 'parent-case.json') -Raw | ConvertFrom-Json
$RepoRoot=$config.repoRoot; $Case=$config.scenario
[IO.File]::WriteAllText((Join-Path $ArtifactDir 'primary-pid.txt'),[string]$PID)
if($Case -ceq 'timeout') {Start-Sleep -Seconds 60; throw 'Timed-out primary survived'}
if($Case -ceq 'delayed-offer') {Start-Sleep -Seconds 22}
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
$realEvaluator=(Get-Command Get-FileTransferCoordinatorSnapshotEvaluation).ScriptBlock
$realParser=(Get-Command Read-FileTransferLogEvents).ScriptBlock
$script:evaluations=0; $script:nativeReads=0; $script:observedSource=$null
function Read-FileTransferLogEvents {
    param([string[]]$LogFiles,[int]$TailMinutes,$OwnedSource=$null)
    $script:observedSource=$OwnedSource
    if($null -ne $OwnedSource) {
        $native=$null -ne $OwnedSource.GetType().GetField('frontier',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($OwnedSource)
        if($native -eq ($Case -cin @('preparation-budget','merged-ordinary'))) {throw 'Preparation rejection changed native/legacy source selection'}
    }
    if($null -ne $OwnedSource) {$script:nativeReads++}
    if($Case.StartsWith('merged-')) {
        $events=@(& $realParser -LogFiles $LogFiles -TailMinutes $TailMinutes -OwnedSource $OwnedSource)
        $origins=@($events | ForEach-Object {$OwnedSource.GetFrontierPhysicalOrigin($_.LineNumber,$_.RawLine)})
        [IO.File]::WriteAllText((Join-Path $ArtifactDir 'analysis-observed-source.json'),(@{
            native=$native;lines=@($events | ForEach-Object {$_.RawLine});origins=$origins} | ConvertTo-Json -Depth 5))
        return $events
    }
    & $realParser -LogFiles $LogFiles -TailMinutes $TailMinutes -OwnedSource $OwnedSource
}
function Get-FileTransferCoordinatorSnapshotEvaluation {
    param([AllowEmptyCollection()][string[]]$Lines)
    $script:evaluations++; $value=& $realEvaluator -Lines $Lines
    if($Case -ceq 'diagnostic-rejected' -and $script:evaluations -eq 1 -and $null -ne $script:observedSource) {
        $flags=[Reflection.BindingFlags]'Instance,NonPublic'
        $owner=$script:observedSource.GetType().GetField('worker',$flags).GetValue($script:observedSource)
        $inputs=$owner.GetType().GetField('frontierWorkerInputs',$flags).GetValue($owner)
        $work=$inputs.GetType().GetField('work',$flags).GetValue($inputs)
        $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*21))
    }
    return $value
}
if($Case -ceq 'report-failure') {
    function Write-FileTransferEfficiencySummaryArtifacts {throw 'injected original analyzer report failure'}
}
$failure=''
try {$null=Invoke-FileTransferRetainedAnalysis -RepoRoot $RepoRoot -ArtifactDir $ArtifactDir -LogPath @($LogPath) -TailMinutes $TailMinutes -LiveRouteProofMode $LiveRouteProofMode}
catch {$failure=$_.Exception.Message; [Console]::Out.WriteLine($_.ScriptStackTrace)}
if($script:evaluations -ne 3 -or $script:nativeReads -ne 1) {throw "Parent analyzer source/evaluations changed: $script:evaluations/$script:nativeReads : $failure"}
$expected=if($Case -ceq 'report-failure') {'injected original analyzer report failure'} else {''}
if($failure -cne $expected) {throw ('Original analyzer failure changed: '+$failure)}
[Console]::Out.WriteLine('original parent analyzer evaluations=3 nativeReads=1')
[Console]::Error.Write('original parent analyzer stderr')
if($failure) {exit 7}
