param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$Case,[string]$DeathResult='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierAppRequest.ps1')
$begin=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.begin')
$attached=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.attached')
$release=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.release')
$attempted=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.attempted')
$retry=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.retry')
$lease=$null
try {
    if(-not $begin.WaitOne(15000)) { throw 'Fixture binding deadline' }
    $run='frontier-native-run'; $scenario='frontier-native-scenario'
    if($Case -ceq 'wrong-run') { $run='foreign-run' }
    if($Case -ceq 'wrong-scenario') { $scenario='foreign-scenario' }
    if($Case -ceq 'wrong-request') { $RequestId=[guid]::NewGuid() }
    $lease=[NLink.Tools.LocalFrontierAppRequest]::TryAttach($Name,$RequestId,$run,$scenario)
    $rejected=$Case -cin @('wrong-run','wrong-scenario','wrong-request','app-incarnation','parent-incarnation','packet-version','packet-metadata','packet-length','packet-reserved','cancel-before-attach')
    if(($null -eq $lease) -ne $rejected) { throw "Wrong attachment: $Case" }
    if($rejected) { [void]$attached.Set(); 'frontier-app-worker=rejected'; exit 0 }
    if(-not $lease.IsCurrent -or $lease.AppProcessId -ne $PID) { throw 'App lease not native/current' }
    if($null -ne [NLink.Tools.LocalFrontierAppRequest]::TryAttach($Name,$RequestId,$run,$scenario)) { throw 'Duplicate attachment admitted' }
    [void]$attached.Set()
    if($Case -ceq 'parent-death') {
        $clock=[Diagnostics.Stopwatch]::StartNew()
        while($lease.IsCurrent -and $clock.ElapsedMilliseconds -lt 10000) { [Threading.Thread]::Sleep(10) }
        if($lease.IsCurrent -or $lease.CompleteAttachment()) { throw 'Dead native parent retained ownership' }
        [IO.File]::WriteAllText($DeathResult,'{"ParentDeathRejected":true}')
        exit 0
    }
    if(-not $release.WaitOne(15000)) { throw 'Fixture completion deadline' }
    if($Case -ceq 'partial') { 'frontier-app-worker=partial'; exit 0 }
    if($Case -ceq 'lease-disposed') { $lease.Dispose() }
    $completed=$lease.CompleteAttachment()
    $expected=$Case -cin @('valid','nonzero-exit','cancel-after-complete','completion-incarnation','completion-metadata')
    if($completed -ne $expected) { throw "Wrong attachment completion: $Case" }
    if($Case -ceq 'completion-contended') {
        [void]$attempted.Set()
        if(-not $retry.WaitOne(15000)) { throw 'Fixture retry deadline' }
    }
    if($lease.CompleteAttachment() -or $lease.IsCurrent) { throw 'Completion/retry reopened the lease' }
    'frontier-app-worker='+(@{Case=$Case;Completed=$completed;AppProcessId=$PID} | ConvertTo-Json -Compress)
    if($Case -ceq 'nonzero-exit') { exit 7 }
} finally {
    if($null -ne $lease) { $lease.Dispose() }
    $begin.Dispose(); $attached.Dispose(); $release.Dispose(); $attempted.Dispose(); $retry.Dispose()
}
