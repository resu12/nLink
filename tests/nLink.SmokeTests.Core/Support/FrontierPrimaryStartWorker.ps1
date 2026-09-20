param([string]$RepoRoot,[string]$SourcePath,[string]$Case,[string]$Directory)
$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalRetainedSourceRequest.ps1')
$name=$env:NLINK_RETAINED_SOURCE_REQUEST; $id=[guid]$env:NLINK_RETAINED_SOURCE_REQUEST_ID
$signals=@{}; $worker=$null; $reader=$null
try {
    foreach($part in @('entered','finished')) {$signals[$part]=[Threading.EventWaitHandle]::OpenExisting($name+'.test.ready.'+$part)}
    if($Case -ceq 'wrong-request') {$id=[guid]::NewGuid()}
    $ready=[string]$env:NLINK_RETAINED_FRONTIER_START_V1 -ceq '1' -and $Case -cne 'legacy-selector'
    $watch=[Diagnostics.Stopwatch]::StartNew(); [void]$signals.entered.Set()
    $worker=Open-FileTransferRetainedSourceOwner -Name $name -RequestId $id -Path $SourcePath -Run 'frontier-gui-run' -Scenario 'frontier-gui-scenario' -FrontierReady:$ready
    $watch.Stop()
    $row=[ordered]@{Attached=$null -ne $worker; Native=$false; Complete=$false; Lines=0; Hash=''; Milliseconds=$watch.ElapsedMilliseconds; RetryRejected=$false}
    if($null -ne $worker) {
        $row.Native=$null -ne $worker.FrontierInputs
        if($Case -cne 'legacy-selector') {
            $reader=$worker.TryOpenSource(); if($null -eq $reader) {throw 'Ready source reader missing'}
            $lines=@($reader.ReadLines())
            $row.Complete=$reader.InputReadComplete -and $worker.OwnsCompletedSource($reader)
            $row.Lines=$lines.Count; $row.Hash=$worker.SourceSha256
        }
    } else {
        $retry=[NLink.Tools.LocalRetainedSourceRequest]::TryAttachFrontier($name,[guid]$env:NLINK_RETAINED_SOURCE_REQUEST_ID,$SourcePath,'frontier-gui-run','frontier-gui-scenario')
        $row.RetryRejected=$null -eq $retry
        if($null -ne $retry) {$retry.Dispose()}
    }
    if($Case -ceq 'parent-exit') {
        # The actual parent is gone, so its redirected pipes are no longer owned.
        # Publish only the fixture outcome atomically for the test runner.
        $path=Join-Path $Directory 'worker-after-parent-exit.json'
        [IO.File]::WriteAllText($path+'.tmp',($row | ConvertTo-Json -Compress))
        [IO.File]::Move($path+'.tmp',$path)
    } else {
        'frontier-start-worker='+($row | ConvertTo-Json -Compress)
        [Console]::Error.Write('ordinary readiness stderr')
    }
} finally {
    if($null -ne $reader) {$reader.Dispose()}; if($null -ne $worker) {$worker.Dispose()}
    if($signals.ContainsKey('finished')) {[void]$signals.finished.Set()}
    foreach($event in $signals.Values) {$event.Dispose()}
}
