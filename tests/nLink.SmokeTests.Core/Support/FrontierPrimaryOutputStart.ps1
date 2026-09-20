$pipeCase=$workerCase.Substring('join-pipes-'.Length); $skipPipes=$false
$nativeProcess=[Diagnostics.Process]::new(); $nativeProcess.StartInfo=$start
if($pipeCase.StartsWith('terminal-reports-launch-',[StringComparison]::Ordinal)) {
    . (Join-Path $PSScriptRoot 'FrontierPrimaryReportLaunch.ps1')
    $skipPipes=$true; return
}
if($pipeCase -ceq 'copied') {
    $copy=$parentClone.Invoke($primary,$null)
    if($null -ne $copy.TryStartFrontierWorkerWithOutput($nativeProcess,$Directory)) {throw 'Copied request started primary'}
    $copy.Dispose(); if(-not $inputs.IsCurrent) {throw 'Copied request retired primary inputs'}
}
switch($pipeCase) {
    'no-stdout' {$start.RedirectStandardOutput=$false}
    'no-stderr' {$start.RedirectStandardError=$false}
    'start-failure' {$start.FileName=Join-Path $Directory 'missing-primary-fixture.exe'}
}
$terminalStarted=[DateTimeOffset]::UtcNow.ToString('O')
$reportRun=$null
$primaryOutput=if($pipeCase.StartsWith('terminal-reports-',[StringComparison]::Ordinal)) {
    if(Get-Variable readyLaunch -ValueOnly -ErrorAction SilentlyContinue) {
        $reportRun=$sourcePreparation.StartWorkerWithReports($nativeProcess,$Directory,'RegularActivationCycle',$terminalStarted)
        if(-not $reportRun.NativeHandoffReady) {throw 'Prepared source readiness missing'}
    } else {$reportRun=$primary.StartFrontierWorkerWithReports($nativeProcess,$Directory,'RegularActivationCycle',$terminalStarted)}
    if(-not $reportRun.SourceBound) {throw 'Report launch did not bind original source'}
    $reportRun.DiagnosticOutput
} elseif($pipeCase.StartsWith('terminal-',[StringComparison]::Ordinal)) {
    $primary.TryStartFrontierWorkerWithOutput($nativeProcess,$Directory,'RegularActivationCycle',$terminalStarted)
} else {$primary.TryStartFrontierWorkerWithOutput($nativeProcess,$Directory)}
if($pipeCase -cin @('no-stdout','no-stderr','start-failure')) {
    if($null -ne $primaryOutput) {throw 'Invalid primary launch acquired pipe ownership'}
    # PowerShell suppresses the exception from an unstarted Process.Id getter
    # and returns null; it does not necessarily enter a catch block.
    $started=$null -ne $nativeProcess.Id
    if($started) {throw 'Invalid primary launch started a worker'}
    $primary.Dispose(); $skipPipes=$true; 'frontier-primary-output='+$pipeCase; return
}
if($null -eq $primaryOutput -or $primaryOutput.OutputsDrainedSuccessfully) {throw 'Original primary output launch missing or already drained'}
$capture=$primaryOutput.GetType().GetField('capture',$parentFlags).GetValue($primaryOutput)
$stdoutPath=$capture.GetType().GetField('stdoutTarget',$parentFlags).GetValue($capture).Name
$stderrPath=$capture.GetType().GetField('stderrTarget',$parentFlags).GetValue($capture).Name
foreach($part in @('held-ready','held-release')) {
    $signals[$part]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$primary.Name+'.test.worker.'+$part)
}
