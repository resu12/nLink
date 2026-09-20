param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$variant=$Case; $core=$Case.StartsWith('core-'); if($core) {$Case=$Case.Substring(5)}
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierAppRequest.ps1')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$request=[NLink.Tools.LocalFrontierAppRequest]::TryCreate('app-start-run','app-start-scenario')
if($null -eq $request) {throw 'Native app start request missing'}
$type=$request.GetType(); $name=$request.Name
$prepare=$type.GetMethod('PrepareStart',$flags); $publish=$type.GetMethod('PublishStartReady',$flags)
$process=[Diagnostics.Process]::new(); $events=@{}; $foreign=$null; $map=$null; $view=$null; $other=$null
try {
    foreach($part in @('entered','finished')) {$events[$part]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$name+'.test.start.'+$part)}
    $appHost=if($core) {(Get-Command pwsh -ErrorAction Stop).Source} else {(Get-Process -Id $PID).Path}
    $start=[Diagnostics.ProcessStartInfo]::new($appHost)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAppStartWorker.ps1'),
        '-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Case',$variant,'-Signal',$name)
    $start.Arguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
    $originalEnvironment=@{}
    foreach($key in @('NLINK_APP_FRONTIER_START_V1','NLINK_APP_FRONTIER_REQUEST','NLINK_APP_FRONTIER_REQUEST_ID')) {
        $originalEnvironment[$key]=[Environment]::GetEnvironmentVariable($key); $start.EnvironmentVariables[$key]='inherited-marker'
    }
    if($Case -ceq 'dispose-race') {
        Add-Type @'
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
public static class AppStartDisposalRace {
    public static void Run(object request, ProcessStartInfo start) {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = request.GetType(); var sync = type.GetField("startSync", flags).GetValue(request);
        Exception failure = null;
        var disposer = new Thread(() => { try { ((IDisposable)request).Dispose(); } catch(Exception error) { failure = error; } });
        disposer.IsBackground = true;
        lock(sync) {
            disposer.Start();
            if(!SpinWait.SpinUntil(() => (disposer.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, 5000))
                throw new InvalidOperationException("disposal_interleaving_unavailable");
            if(!(bool)type.GetMethod("PrepareStart", flags).Invoke(request, new object[]{start, true}))
                throw new InvalidOperationException("race_preparation_failed");
        }
        if(!disposer.Join(5000) || failure != null) throw new InvalidOperationException("disposal_failed", failure);
    }
}
'@
        [AppStartDisposalRace]::Run($request,$start)
        foreach($suffix in @('.app-start-v1.ready','.app-start-v1.failed')) {
            $leftover=$null; try {$leftover=[Threading.EventWaitHandle]::OpenExisting($name+$suffix)} catch [Threading.WaitHandleCannotBeOpenedException] { }
            if($null -ne $leftover) {$leftover.Dispose(); throw 'Concurrent disposal leaked app signals'}
        }
        'frontier-app-start='+$variant; return
    }
    if($Case.StartsWith('collision-')) {$foreign=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$name+'.app-start-v1.'+$Case.Substring(10))}
    if($Case -ceq 'dispose-before') {$request.Dispose()}
    $prepared=$prepare.Invoke($request,@($start,$true))
    foreach($key in $originalEnvironment.Keys) {if([Environment]::GetEnvironmentVariable($key) -cne $originalEnvironment[$key]) {throw 'Preparation mutated GUI environment'}}
    $omitted=$Case.StartsWith('collision-') -or $Case -ceq 'dispose-before'
    if($prepared -eq $omitted) {throw 'Wrong app readiness preparation'}
    $repeat=[Diagnostics.ProcessStartInfo]::new()
    if($prepare.Invoke($request,@($repeat,$true))) {throw 'Repeated preparation admitted'}
    if($omitted) {
        foreach($key in @('NLINK_APP_FRONTIER_START_V1','NLINK_APP_FRONTIER_REQUEST','NLINK_APP_FRONTIER_REQUEST_ID')) {
            if($start.EnvironmentVariables.ContainsKey($key)) {throw 'Stale child selector survived omission'}
        }
    } else {
        if($start.EnvironmentVariables['NLINK_APP_FRONTIER_REQUEST'] -cne $name -or
            $start.EnvironmentVariables['NLINK_APP_FRONTIER_REQUEST_ID'] -cne $request.RequestId.ToString('D')) {throw 'Request delivery changed identity'}
    }
    if($Case -ceq 'selector-off') {$start.EnvironmentVariables.Remove('NLINK_APP_FRONTIER_START_V1')}
    if($Case -ceq 'selector-case') {$start.EnvironmentVariables['NLINK_APP_FRONTIER_START_V1']='true'}
    if($Case -ceq 'bad-id') {$start.EnvironmentVariables['NLINK_APP_FRONTIER_REQUEST_ID']=$request.RequestId.ToString('N')}
    if($Case -ceq 'wrong-request') {$start.EnvironmentVariables['NLINK_APP_FRONTIER_REQUEST_ID']=[guid]::NewGuid().ToString('D')}
    $process.StartInfo=$start; if(-not $process.Start()) {throw 'App fixture failed to start'}
    $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
    $deferred=$Case -cin @('delayed','forged-ready','cancel-wait','dispose-wait','timeout','parent-death')
    if(-not $omitted -and -not $deferred) {
        $bound=$process
        if($Case -ceq 'wrong-app') {
            $otherStart=[Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
            $otherStart.UseShellExecute=$false; $otherStart.CreateNoWindow=$true
            $otherStart.Arguments='-NoProfile -Command "Start-Sleep -Seconds 40"'
            $other=[Diagnostics.Process]::Start($otherStart); $bound=$other
        }
        if(-not $request.BindApp($bound)) {throw 'App binding failed'}
        $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name); $view=$map.CreateViewAccessor()
        if($Case -ceq 'wrong-parent') {$view.Write(16L,$view.ReadInt64(16L)+1L)}
        if($Case -ceq 'app-incarnation') {$view.Write(32L,$view.ReadInt64(32L)+1L)}
    }
    if(-not $omitted) {
        $signal=$type.GetField('startSignal',$flags).GetValue($request)
        $ready=$signal.GetType().GetField('Ready',$flags).GetValue($signal)
        $failed=$signal.GetType().GetField('Failed',$flags).GetValue($signal)
        if(-not $deferred) {
            if($Case -cin @('wrong-parent','app-incarnation')) {
                if($publish.Invoke($request,$null)) {throw 'Changed incarnation published readiness'}
                [void]$ready.Set() # A forged signal must still fail native checks.
            } else {if(-not $publish.Invoke($request,$null)) {throw 'Readiness publication failed'}}
            if($Case -ceq 'both-signals') {[void]$failed.Set()}
            if($Case -ceq 'cancel-ready') {$request.Cancel()}
        }
    }
    if(-not $events.entered.WaitOne(15000)) {throw 'App did not reach attachment'}
    if($deferred) {
        if($events.finished.WaitOne(150)) {throw 'Early app did not wait for native readiness'}
        $gate=$type.GetField('gate',$flags).GetValue($request)
        if(-not $gate.WaitOne(0)) {throw 'Readiness wait held the ownership mutex'}
        $gate.ReleaseMutex()
        switch($Case) {
            'delayed' {if(-not $request.BindApp($process) -or -not $publish.Invoke($request,$null)) {throw 'Delayed actual binding failed'}}
            'forged-ready' {[void]$ready.Set()}
            'cancel-wait' {$request.Cancel()}
            'dispose-wait' {$request.Dispose()}
            'parent-death' {'frontier-app-start='+$variant; [Environment]::Exit(0)}
        }
    }
    if(-not $process.WaitForExit(30000)) {throw 'App readiness deadline'}
    $out=$stdout.GetAwaiter().GetResult(); $err=$stderr.GetAwaiter().GetResult()
    if($process.ExitCode -ne 0 -or $out -notmatch 'app-start-worker-ok') {throw "App readiness failed: $out $err"}
    $completion=$request.TakeAfterExit(); $accepted=$Case -cin @('ready','delayed')
    if(($null -ne $completion) -ne $accepted) {throw 'Wrong attachment completion after readiness'}
    if($accepted -and ($completion.LiveProofEligible -or $completion.NativeWriterCustodyEstablished -or $completion.PublicationCompletionIssued)) {throw 'Readiness granted publication authority'}
    if($null -ne $foreign -and $foreign.WaitOne(0)) {throw 'Readiness collision signalled foreign event'}
    'frontier-app-start='+$variant
} finally {
    foreach($child in @($process,$other)) {if($null -ne $child) {try {if(-not $child.HasExited) {$child.Kill(); [void]$child.WaitForExit(5000)}} catch [InvalidOperationException] { }; $child.Dispose()}}
    if($null -ne $view) {$view.Dispose()}; if($null -ne $map) {$map.Dispose()}
    $request.Dispose(); if($null -ne $foreign) {$foreign.Dispose()}; foreach($event in $events.Values) {$event.Dispose()}
}
