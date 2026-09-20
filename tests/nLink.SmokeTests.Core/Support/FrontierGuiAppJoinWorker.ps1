param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$Directory,[string]$Case,[string]$DeathResult='',
    [string]$CoreAssembly,[switch]$App,[string]$SignalName,[string]$AppMode='valid')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if($App) {
    $begin=[Threading.EventWaitHandle]::OpenExisting($SignalName+'.begin')
    $ready=[Threading.EventWaitHandle]::OpenExisting($SignalName+'.ready')
    $release=[Threading.EventWaitHandle]::OpenExisting($SignalName+'.release')
    $lease=$null
    try {
        if(-not $begin.WaitOne(20000)) { throw 'App bind deadline' }
        $core=[Reflection.Assembly]::LoadFrom($CoreAssembly)
        if([NLink.Tools.LocalFrontierAppRequest].Assembly -ne $core) { throw 'App did not use Core native codec' }
        if($AppMode -cnotin @('plain','missing-attachment')) {
            $run=if($AppMode -ceq 'wrong-run') {'wrong'} else {'frontier-gui-run'}
            $lease=[NLink.Tools.LocalFrontierAppRequest]::TryAttach($Name,$RequestId,$run,'frontier-gui-scenario')
            $expected=$AppMode -cnotin @('wrong-run','swapped-endpoint')
            if(($null -ne $lease) -ne $expected) { throw 'Wrong joined app attachment' }
        }
        [void]$ready.Set()
        if(-not $release.WaitOne(25000)) { throw 'App release deadline' }
        if($null -ne $lease) {
            if($AppMode -ceq 'abandoned-attachment') { $lease.Dispose() }
            $complete=$lease.CompleteAttachment()
            if($complete -ne ($AppMode -cin @('valid','nonzero'))) { throw "Wrong joined app completion: $AppMode" }
        }
        'frontier-joined-app=ok'
        if($AppMode -ceq 'nonzero') { exit 7 }
    } finally {
        if($null -ne $lease) { $lease.Dispose() }
        $begin.Dispose(); $ready.Dispose(); $release.Dispose()
    }
    exit 0
}
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierAppRequest.ps1')
$begin=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.begin')
$attached=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.attached')
$release=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.release')
$requests=[Collections.Generic.List[object]]::new()
$apps=[Collections.Generic.List[object]]::new()
$bindings=[Collections.Generic.List[object]]::new()
$owner=$null; $finalizer=$null
function Start-JoinApp($Request,[string]$Mode,[string]$EndpointName='',[guid]$EndpointId=[guid]::Empty) {
    $signal=$Name+'.join.'+$apps.Count
    $events=@{}
    foreach($part in @('begin','ready','release')) { $events[$part]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$signal+'.'+$part) }
    if($null -ne $Request -and -not $EndpointName) { $EndpointName=$Request.Name; $EndpointId=$Request.RequestId }
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=(Get-Command pwsh).Source; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$PSCommandPath,'-App','-Name',$EndpointName,'-RequestId',$EndpointId.ToString('D'),'-CoreAssembly',$CoreAssembly,'-SignalName',$signal,'-AppMode',$Mode)
    $start.Arguments=($arguments | ForEach-Object { '"'+$_.Replace('"','\"')+'"' }) -join ' '
    $process=[Diagnostics.Process]::Start($start); [void]$process.Handle
    $entry=@{Process=$process;Events=$events;Out=$process.StandardOutput.ReadToEndAsync();Err=$process.StandardError.ReadToEndAsync();Mode=$Mode}
    $apps.Add($entry)
    return $entry
}
function Begin-JoinApp($Entry) {
    [void]$Entry.Events.begin.Set()
    if(-not $Entry.Events.ready.WaitOne(20000)) { throw 'Joined app did not report readiness' }
}
function Finish-JoinApps {
    foreach($entry in $apps) { [void]$entry.Events.release.Set() }
    foreach($entry in $apps) {
        if(-not $entry.Process.WaitForExit(15000)) { throw 'Joined app did not exit' }
        $entry.Process.WaitForExit()
        $expected=if($entry.Mode -ceq 'nonzero') {7} else {0}
        $out=$entry.Out.GetAwaiter().GetResult(); $err=$entry.Err.GetAwaiter().GetResult()
        if($entry.Process.ExitCode -ne $expected -or $out -notmatch 'frontier-joined-app=ok') { throw "Joined app failed: $out $err" }
    }
}
try {
    if(-not $begin.WaitOne(20000)) { throw 'GUI binding deadline' }
    $owner=[NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncher($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
    if($null -eq $owner -or $owner.AllAppRequestsCompletedSuccessfully) { throw 'GUI owner unavailable or completed too early' }
    if($Case -ceq 'request-after-plain') {
        $entry=Start-JoinApp $null 'plain'
        if($null -eq $owner.BindApp($entry.Process)) { throw 'Plain launch failed' }
        Begin-JoinApp $entry
        if($null -ne $owner.TryCreateAppRequest()) { throw 'Mixed plain/request ownership accepted' }
    } else {
        $count=if($Case -cin @('incomplete-set','seal-before-bind','sequential-request')) {1} else {2}
        for($i=0;$i -lt $count;$i++) {
            $request=$owner.TryCreateAppRequest()
            if($null -eq $request -or -not $request.IsCurrent -or $request.LiveProofEligible -or $request.NativeWriterCustodyEstablished -or $request.PublicationCompletionIssued) { throw 'Original app request unavailable/eligible' }
            $requests.Add($request)
        }
        if($Case -ceq 'copied') {
            $copy=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic').Invoke($requests[0],$null)
            if($copy.IsCurrent -or $null -ne $copy.BindApp($null)) { throw 'Copied request claimed ownership' }
            $copy.Dispose()
            if(-not $requests[0].IsCurrent) { throw 'Copied request disposed original' }
        }
        if($Case -ceq 'third-request') {
            if($null -ne $owner.TryCreateAppRequest()) { throw 'Third request allocated/evicted a slot' }
        } elseif($Case -ceq 'seal-before-bind') {
            if($owner.SealLaunches()) { throw 'Unbound request sealed' }
        } elseif($Case -ceq 'plain-after-request') {
            $entry=Start-JoinApp $null 'plain'
            if($null -ne $owner.BindApp($entry.Process)) { throw 'Plain launch bypassed original request' }
            Begin-JoinApp $entry
        } else {
            $willRevoke=$Case -cin @('incomplete-set','request-disposed','owner-disposed','duplicate-bind','duplicate-process','after-seal-create',
                'cancel-after','registration-after','stage-after','app-metadata','app-cancel','parent-death')
            for($i=0;$i -lt $requests.Count;$i++) {
                if($Case -ceq 'duplicate-process' -and $i -eq 1) {
                    if($null -ne $requests[1].BindApp($apps[0].Process)) { throw 'Same native process filled both requests' }
                    break
                }
                $mode=if($willRevoke) {'revoked'} elseif($i -eq 1 -and $Case -cin @('nonzero','missing-attachment','abandoned-attachment','wrong-run','swapped-endpoint')) {$Case} else {'valid'}
                if($Case -ceq 'abandoned-attachment' -and $i -eq 0) { $mode='valid' }
                $entry=if($mode -ceq 'swapped-endpoint') {Start-JoinApp $requests[$i] $mode $requests[0].Name $requests[0].RequestId} else {Start-JoinApp $requests[$i] $mode}
                $binding=$requests[$i].BindApp($entry.Process)
                if($null -eq $binding -or $binding.AppProcessId -ne $entry.Process.Id -or -not $requests[$i].IsCurrent) { throw 'Joined launch failed' }
                $bindings.Add($binding)
                Begin-JoinApp $entry
                if($Case -ceq 'sequential-request' -and $i -eq 0) {
                    $next=$owner.TryCreateAppRequest()
                    if($null -eq $next -or -not $next.IsCurrent) { throw 'Second sequential request unavailable' }
                    $requests.Add($next)
                }
            }
            if($Case -ceq 'incomplete-set') { if($owner.SealLaunches()) { throw 'Incomplete request set sealed' } }
            elseif($Case -cne 'duplicate-process') {
                if(-not $owner.SealLaunches() -or $owner.AllAppRequestsCompletedSuccessfully) { throw 'Joined launch seal failed/early completion' }
                switch($Case) {
                    'request-disposed' { $requests[0].Dispose() }
                    'owner-disposed' { $owner.Dispose() }
                    'duplicate-bind' { if($null -ne $requests[0].BindApp($apps[0].Process)) { throw 'Joined app rebound' } }
                    'after-seal-create' { if($null -ne $owner.TryCreateAppRequest()) { throw 'Request added after seal' } }
                    'app-metadata' { $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($requests[0].Name); $view=$map.CreateViewAccessor(); try {$view.Write(60L,1)} finally {$view.Dispose();$map.Dispose()} }
                    'app-cancel' { $cancel=[Threading.EventWaitHandle]::OpenExisting($requests[0].Name+'.cancelled'); try {[void]$cancel.Set()} finally {$cancel.Dispose()} }
                }
            }
        }
    }
    [void]$attached.Set()
    if($Case -ceq 'parent-death') {
        $clock=[Diagnostics.Stopwatch]::StartNew()
        while($owner.IsCurrent -and $clock.ElapsedMilliseconds -lt 15000) { Start-Sleep -Milliseconds 20 }
    } elseif(-not $release.WaitOne(20000)) { throw 'GUI release deadline' }
    $expectedCurrent=$Case -cin @('valid','copied','sequential-request','nonzero','missing-attachment','abandoned-attachment','wrong-run','swapped-endpoint')
    if($owner.IsCurrent -ne $expectedCurrent -or $owner.AllAppRequestsCompletedSuccessfully) { throw "Wrong joined current/early completion: $Case" }
    if(-not $expectedCurrent -and @($requests | Where-Object {$_.IsCurrent}).Count) { throw 'Revoked GUI retained native app requests' }
    if(@($apps | Where-Object {$_.Process.HasExited}).Count) { throw 'Revocation stopped apps' }
    # Optional frontier revocation must not consume or cancel the old finalizer.
    if($Case -cnotin @('cancel-after','registration-after','stage-after','parent-death')) {
        $finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
        if($null -eq $finalizer) { throw 'App join interfered with legacy finalizer' }
    }
    Finish-JoinApps
    $expectedSuccess=$Case -cin @('valid','copied','sequential-request')
    if($owner.AllAppRequestsCompletedSuccessfully -ne $expectedSuccess) { throw "Wrong joined completion: $Case" }
    if($owner.AllAppOutputsDrainedSuccessfully) { throw 'Legacy joined completion acquired original output EOF' }
    if($Case -cin @('missing-attachment','wrong-run','swapped-endpoint') -and -not $owner.AllAppsExitedSuccessfully) { throw 'Missing-attachment control did not exit successfully' }
    if($Case -ceq 'copied' -and $copy.IsCurrent) { throw 'Completion upgraded copied request' }
    if($Case -ceq 'parent-death') { [IO.File]::WriteAllText($DeathResult,'{"ParentDeathRejected":true,"AppsStillRunning":true,"AppLeasesRevoked":true}'); exit 0 }
    'frontier-gui-worker='+(@{Case=$Case;JoinedCompletion=$expectedSuccess} | ConvertTo-Json -Compress)
} finally {
    if($null -ne $owner) { $owner.Dispose() }
    foreach($entry in $apps) {
        [void]$entry.Events.begin.Set(); [void]$entry.Events.release.Set()
        if(-not $entry.Process.HasExited -and -not $entry.Process.WaitForExit(5000)) { $entry.Process.Kill(); [void]$entry.Process.WaitForExit(5000) }
        $entry.Process.Dispose()
        foreach($event in $entry.Events.Values) { $event.Dispose() }
    }
    if($null -ne $finalizer) { $finalizer.Dispose() }
    $begin.Dispose(); $attached.Dispose(); $release.Dispose()
}
