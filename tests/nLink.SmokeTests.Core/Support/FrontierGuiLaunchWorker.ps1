param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$Directory,[string]$Case,[string]$DeathResult='',
    [switch]$App,[int]$AppExitCode=0)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($App) {
    $release=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.apps')
    try { if(-not $release.WaitOne(30000)) { throw 'App fixture release deadline' }; exit $AppExitCode }
    finally { $release.Dispose() }
}
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$begin=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.begin')
$attached=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.attached')
$release=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.release')
$appRelease=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$Name+'.test.apps')
$apps=[Collections.Generic.List[Diagnostics.Process]]::new()
$bindings=[Collections.Generic.List[object]]::new()
$owner=$null; $finalizer=$null
function Start-TestApp([int]$Code=0) {
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=(Get-Process -Id $PID).Path; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-App','-Name',$Name,'-AppExitCode',[string]$Code)
    $start.Arguments=($arguments | ForEach-Object { '"'+$_.Replace('"','\"')+'"' }) -join ' '
    $process=[Diagnostics.Process]::Start($start)
    [void]$process.Handle
    $apps.Add($process)
    return $process
}
try {
    if(-not $begin.WaitOne(20000)) { throw 'GUI binding deadline' }
    if($Case -ceq 'finalizer-first') {
        $finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
        if($null -eq $finalizer) { throw 'Original finalizer unavailable' }
    }
    $run=if($Case -ceq 'wrong-run') {'wrong'} else {'frontier-gui-run'}
    $scenario=if($Case -ceq 'wrong-scenario') {'wrong'} else {'frontier-gui-scenario'}
    $id=if($Case -ceq 'wrong-request') {[guid]::NewGuid()} else {$RequestId}
    $root=if($Case -ceq 'wrong-root') {Join-Path $Directory 'wrong'} else {$Directory}
    $owner=[NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncher($Name,$id,$run,$scenario,$root)
    $expectedAttach=$Case -cnotin @('wrong-run','wrong-scenario','wrong-root','wrong-request','gui-incarnation','parent-incarnation','reserved','cancel-before')
    if(($null -ne $owner) -ne $expectedAttach) { throw "Wrong early GUI attach: $Case" }
    if($null -ne [NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncher($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)) { throw 'GUI attach was not one-shot' }
    if(-not $expectedAttach) { [void]$attached.Set(); 'frontier-gui-worker=rejected'; exit 0 }
    if(-not $owner.IsCurrent -or $owner.GuiProcessId -ne $PID -or
        $owner.GuiProcessCreationFileTime -ne (Get-Process -Id $PID).StartTime.ToUniversalTime().ToFileTimeUtc() -or
        $owner.RequestId -ne $RequestId -or $owner.AllAppsExitedSuccessfully -or
        $owner.LiveProofEligible -or $owner.NativeWriterCustodyEstablished -or $owner.PublicationCompletionIssued) { throw 'Original GUI identity/flags mismatch' }
    if($Case -ceq 'self') {
        $self=[Diagnostics.Process]::GetCurrentProcess()
        try { if($null -ne $owner.BindApp($self)) { throw 'GUI accepted as its own app' } } finally { $self.Dispose() }
    } elseif($Case -ceq 'null-app') {
        if($null -ne $owner.BindApp($null)) { throw 'Null app accepted' }
    } elseif($Case -ceq 'already-exited') {
        $dead=Start-TestApp
        [void]$appRelease.Set()
        if(-not $dead.WaitForExit(15000)) { throw 'Dead app fixture timeout' }
        if($null -ne $owner.BindApp($dead)) { throw 'Exited app accepted' }
    } else {
        $first=Start-TestApp
        $binding=$owner.BindApp($first)
        if($null -eq $binding) { throw 'First app binding failed' }
        $bindings.Add($binding)
        if($Case -ceq 'incomplete-seal') {
            if($owner.SealLaunches()) { throw 'Incomplete app set sealed' }
        } elseif($Case -ceq 'duplicate-app') {
            if($null -ne $owner.BindApp($first)) { throw 'Duplicate app accepted' }
        } else {
            $code=if($Case -ceq 'nonzero') {7} else {0}
            $second=Start-TestApp $code
            $binding=$owner.BindApp($second)
            if($null -eq $binding) { throw 'Second app binding failed' }
            $bindings.Add($binding)
            for($i=0;$i -lt 2;$i++) {
                if(-not $bindings[$i].IsCurrent -or $bindings[$i].AppProcessId -ne $apps[$i].Id -or
                    $bindings[$i].AppProcessCreationFileTime -ne $apps[$i].StartTime.ToUniversalTime().ToFileTimeUtc() -or
                    $bindings[$i].ExitedSuccessfully -or $bindings[$i].LiveProofEligible -or
                    $bindings[$i].NativeWriterCustodyEstablished -or $bindings[$i].PublicationCompletionIssued) { throw 'App identity/flags mismatch' }
            }
            if($Case -ceq 'third-app') {
                $third=Start-TestApp
                if($null -ne $owner.BindApp($third)) { throw 'Third app accepted/evicted another' }
            } else {
                if($Case -ceq 'copied') {
                    $clone=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic')
                    $ownerCopy=$clone.Invoke($owner,$null)
                    $appCopy=$clone.Invoke($bindings[0],$null)
                    if($ownerCopy.IsCurrent -or $ownerCopy.SealLaunches() -or $ownerCopy.AllAppsExitedSuccessfully -or $appCopy.IsCurrent -or $appCopy.ExitedSuccessfully) { throw 'Copied launch authority accepted' }
                    $ownerCopy.Dispose()
                    if(-not $owner.IsCurrent -or -not $bindings[0].IsCurrent) { throw 'Copy disposed original authority' }
                }
                if($Case -cne 'seal-after-exit' -and ($owner.AllAppsExitedSuccessfully -or -not $owner.SealLaunches() -or $owner.SealLaunches() -or $owner.AllAppsExitedSuccessfully)) { throw 'Launch sealing/early exit gate failed' }
                if($Case -ceq 'bind-after-seal' -and $null -ne $owner.BindApp($first)) { throw 'App rebound after sealing' }
                if($Case -ceq 'owner-disposed') { $owner.Dispose(); $owner.Dispose() }
            }
        }
    }
    if($Case -ceq 'finalizer-coexistence' -or $Case -ceq 'owner-disposed') {
        $finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
        if($null -eq $finalizer) { throw 'Early owner consumed/cancelled original finalizer' }
    }
    [void]$attached.Set()
    if($Case -ceq 'parent-death') {
        $deadline=[Diagnostics.Stopwatch]::StartNew()
        while($owner.IsCurrent -and $deadline.ElapsedMilliseconds -lt 15000) { Start-Sleep -Milliseconds 20 }
        if($owner.IsCurrent -or $owner.AllAppsExitedSuccessfully -or @($bindings | Where-Object {$_.IsCurrent -or $_.ExitedSuccessfully}).Count -or @($apps | Where-Object {$_.HasExited}).Count) { throw 'Parent death did not revoke without stopping apps' }
        [IO.File]::WriteAllText($DeathResult,'{"ParentDeathRejected":true,"AppsStillRunning":true}')
        exit 0
    }
    if(-not $release.WaitOne(20000)) { throw 'GUI fixture release deadline' }
    $expectedCurrent=$Case -cin @('valid','nonzero','copied','finalizer-coexistence','finalizer-first','seal-after-exit')
    if($owner.IsCurrent -ne $expectedCurrent) { throw "Wrong current launch owner: $Case" }
    if(-not $expectedCurrent -and ($owner.AllAppsExitedSuccessfully -or $owner.SealLaunches() -or @($bindings | Where-Object {$_.IsCurrent -or $_.ExitedSuccessfully}).Count)) { throw 'Rejected scope retained app evidence' }
    if($Case -cne 'already-exited' -and @($apps | Where-Object {$_.HasExited}).Count) { throw 'Revocation stopped a running app' }
    [void]$appRelease.Set()
    foreach($appProcess in $apps) { if(-not $appProcess.WaitForExit(15000)) { throw 'App fixture did not exit' } }
    if($Case -ceq 'seal-after-exit' -and ($owner.AllAppsExitedSuccessfully -or -not $owner.SealLaunches() -or $owner.SealLaunches())) { throw 'Successful app exits bypassed launch sealing' }
    $expectedSuccess=$expectedCurrent -and $Case -cne 'nonzero'
    if($owner.AllAppsExitedSuccessfully -ne $expectedSuccess) { throw "Wrong all-app exit evidence: $Case" }
    if($expectedCurrent -and (-not $bindings[0].ExitedSuccessfully -or $bindings[1].ExitedSuccessfully -ne ($Case -cne 'nonzero'))) { throw 'Individual native exit evidence mismatch' }
    if($Case -ceq 'copied' -and ($ownerCopy.IsCurrent -or $ownerCopy.AllAppsExitedSuccessfully -or $appCopy.IsCurrent -or $appCopy.ExitedSuccessfully)) { throw 'Successful exits upgraded copied authority' }
    # Exit evidence must remain tied to the retained native handles, not the
    # lifetime of the Process wrappers supplied by the trusted launcher.
    foreach($appProcess in $apps) { $appProcess.Dispose() }
    $apps.Clear()
    if($owner.AllAppsExitedSuccessfully -ne $expectedSuccess) { throw 'Process wrapper disposal changed retained native exit evidence' }
    if($owner.AllAppOutputsDrainedSuccessfully) { throw 'Legacy launch evidence acquired original output EOF' }
    'frontier-gui-worker='+(@{Case=$Case;AllAppsExitedSuccessfully=$expectedSuccess} | ConvertTo-Json -Compress)
} finally {
    [void]$appRelease.Set()
    foreach($appProcess in $apps) {
        if(-not $appProcess.HasExited -and -not $appProcess.WaitForExit(5000)) { $appProcess.Kill(); [void]$appProcess.WaitForExit(5000) }
        $appProcess.Dispose()
    }
    if($null -ne $owner) { $owner.Dispose() }
    if($null -ne $finalizer) { $finalizer.Dispose() }
    $begin.Dispose(); $attached.Dispose(); $release.Dispose(); $appRelease.Dispose()
}
