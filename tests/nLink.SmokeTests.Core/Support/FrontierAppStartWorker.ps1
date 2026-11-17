param([string]$RepoRoot,[string]$Directory,[string]$Case,[string]$Signal)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$core=$Case.StartsWith('core-')
if($core) {
    $Case=$Case.Substring(5)
    [void][Reflection.Assembly]::LoadFrom((Join-Path $RepoRoot 'src/nLink.Core/bin/Debug/net8.0/nLink.Core.dll'))
}
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierAppRequest.ps1')
if($core -and [NLink.Tools.LocalFrontierAppRequest].Assembly.GetName().Name -cne 'nLink.Core') {throw 'Built Core receiver was not loaded'}
$entered=[Threading.EventWaitHandle]::OpenExisting($Signal+'.test.start.entered')
$finished=[Threading.EventWaitHandle]::OpenExisting($Signal+'.test.start.finished')
$lease=$null; $cancelled=$null; $map=$null; $parent=$null; $ready=$null; $failed=$null
try {
    $run='app-start-run'; $scenario='app-start-scenario'
    if($Case -ceq 'wrong-run') {$run='foreign'}
    if($Case -ceq 'wrong-scenario') {$scenario='foreign'}
    if($Case -ceq 'parent-death') {
        # Retain observation handles so endpoint disappearance cannot masquerade
        # as native parent death or managed cancellation in this fixture.
        $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($Signal)
        $view=$map.CreateViewAccessor(); try {$parent=[Diagnostics.Process]::GetProcessById($view.ReadInt32(12L)); [void]$parent.Handle} finally {$view.Dispose()}
        $cancelled=[Threading.EventWaitHandle]::OpenExisting($Signal+'.cancelled')
        $ready=[Threading.EventWaitHandle]::OpenExisting($Signal+'.app-start-v1.ready')
        $failed=[Threading.EventWaitHandle]::OpenExisting($Signal+'.app-start-v1.failed')
    }
    [void]$entered.Set(); $clock=[Diagnostics.Stopwatch]::StartNew()
    $lease=[NLink.Tools.LocalFrontierAppRequest]::TryAttachReadyFromEnvironment($run,$scenario)
    $accepted=$Case -cin @('ready','delayed')
    if(($null -ne $lease) -ne $accepted) {throw 'Wrong ready-aware app attachment'}
    if($accepted) {
        if(-not $lease.IsCurrent -or $lease.AppProcessId -ne $PID) {throw 'Ready did not preserve native identity'}
        if($null -ne [NLink.Tools.LocalFrontierAppRequest]::TryAttachReadyFromEnvironment($run,$scenario) -or
            $null -ne [NLink.Tools.LocalFrontierAppRequest]::TryAttach($env:NLINK_APP_FRONTIER_REQUEST,[guid]$env:NLINK_APP_FRONTIER_REQUEST_ID,$run,$scenario)) {throw 'Readiness replay bypassed one-shot attachment'}
        if(-not $lease.CompleteAttachment()) {throw 'Original app completion failed'}
    }
    if($Case -ceq 'timeout' -and ($clock.ElapsedMilliseconds -lt 19500 -or $clock.ElapsedMilliseconds -gt 27000)) {throw 'Producer readiness deadline changed'}
    if($Case -ceq 'parent-death') {
        $death=$parent.WaitForExit(1000) -and -not $cancelled.WaitOne(0) -and -not $ready.WaitOne(0) -and -not $failed.WaitOne(0)
        [IO.File]::WriteAllText((Join-Path $Directory 'app-result.json'),(@{Rejected=$null -eq $lease;ParentDeathWithoutCancellation=$death} | ConvertTo-Json -Compress))
        if(-not $death) {throw 'Parent death was replaced by cancellation or readiness'}
    }
    [void]$finished.Set(); 'app-start-worker-ok'
} finally {
    if($null -ne $lease) {$lease.Dispose()}
    foreach($item in @($entered,$finished,$cancelled,$ready,$failed,$map,$parent)) {if($null -ne $item) {$item.Dispose()}}
}
