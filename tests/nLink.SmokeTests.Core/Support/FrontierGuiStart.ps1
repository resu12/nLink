param([string]$RepoRoot,[string]$Directory,[string]$Case)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
if($Case.StartsWith('parent-')) {
    $completion=$null
    & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/LocalGuiWriterRequest.ps1') -RepoRoot $RepoRoot -Directory $Directory `
        -AcceptedCompletion ([ref]$completion) -SingleCase $Case.Substring('parent-'.Length) -FrontierLaunch
    'frontier-gui-start='+$Case; return
}
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'; $static=[Reflection.BindingFlags]'Static,NonPublic'
$request=[NLink.Tools.LocalGuiWriterRequest]::TryCreate([guid]::NewGuid(),'gui-start-run','gui-start-scenario',$Directory)
if($null -eq $request) {throw 'GUI request unavailable'}
$process=[Diagnostics.Process]::new(); $signals=@{}; $foreign=$null; $view=$null; $map=$null; $reports=$null
$nativeSignal=$null; $output=$null; $errorOutput=$null
try {
    foreach($part in @('entered','finished','release')) {$signals[$part]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$request.Name+'.test.gui-start.'+$part)}
    if($Case -ceq 'dispose-race') {
        # Reproduce disposal after the launch guard but before signal creation,
        # while the original launch still owns its existing synchronization lock.
        Add-Type @'
using System;
using System.Reflection;
using System.Threading;
public static class GuiStartDisposalRace {
    public static bool Run(object request, string name) {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = request.GetType(); var sync = type.GetField("frontierParentSync", flags).GetValue(request);
        var field = type.GetField("frontierGuiStart", flags); var state = type.GetField("sealedState", flags);
        var signalType = type.GetNestedType("FrontierGuiStartSignal", BindingFlags.NonPublic);
        Exception failure = null; object signal = null;
        var disposer = new Thread(() => { try { ((IDisposable)request).Dispose(); } catch(Exception error) { failure = error; } });
        disposer.IsBackground = true;
        try {
            lock(sync) {
                disposer.Start();
                if (!SpinWait.SpinUntil(() => (int)state.GetValue(request) == 1 &&
                    (disposer.ThreadState & ThreadState.WaitSleepJoin) != 0, 5000)) throw new InvalidOperationException("disposal_interleaving_unavailable");
                signal = signalType.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[]{name});
                if(signal == null) throw new InvalidOperationException("race_signal_creation_failed");
                field.SetValue(request, signal);
            }
            if(!disposer.Join(5000) || failure != null) throw new InvalidOperationException("disposal_did_not_finish", failure);
            return field.GetValue(request) == null;
        } finally { if(signal != null) ((IDisposable)signal).Dispose(); }
    }
}
'@
        if(-not [GuiStartDisposalRace]::Run($request,$request.Name)) {throw 'Concurrent GUI request disposal leaked launch readiness'}
        'frontier-gui-start='+$Case; return
    }
    $start=[Diagnostics.ProcessStartInfo]::new(); $start.FileName=(Get-Process -Id $PID).Path
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',(Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiStartWorker.ps1'),
        '-RepoRoot',$RepoRoot,'-Directory',$Directory,'-Case',$Case)
    $start.Arguments=($arguments | ForEach-Object {'"'+$_+'"'}) -join ' '
    $start.EnvironmentVariables['NLINK_GUI_FRONTIER_START_V1']='inherited-marker'
    $process.StartInfo=$start
    if($Case -ceq 'guards') {
        $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($request,$null); $rejected=$false
        try {$null=$copy.StartFrontierGuiWorkerWithReports($process)} catch {$rejected=$true}
        $copy.Dispose(); if(-not $rejected) {throw 'Copied request launched GUI'}
        foreach($property in @('RedirectStandardOutput','RedirectStandardError')) {
            $start.$property=$false; $rejected=$false
            try {$null=$request.StartFrontierGuiWorkerWithReports($process)} catch {$rejected=$true}
            $start.$property=$true; if(-not $rejected) {throw 'Missing redirect consumed launch'}
        }
        $alreadyStart=[Diagnostics.ProcessStartInfo]::new(); $alreadyStart.FileName=(Get-Process -Id $PID).Path
        $alreadyStart.Arguments='-NoProfile -Command "exit 0"'; $alreadyStart.UseShellExecute=$false; $alreadyStart.CreateNoWindow=$true
        $alreadyStart.RedirectStandardOutput=$true; $alreadyStart.RedirectStandardError=$true
        $already=[Diagnostics.Process]::Start($alreadyStart); $rejected=''
        try {
            if(-not $already.WaitForExit(10000)) {throw 'Already-started guard fixture deadline'}
            try {$null=$request.StartFrontierGuiWorkerWithReports($already)} catch {$rejected=$_.Exception.Message}
        } finally {if(-not $already.HasExited) {$already.Kill(); [void]$already.WaitForExit(5000)}; $already.Dispose()}
        if($rejected -notmatch 'frontier_gui_already_started') {throw 'Already-started GUI guard missing'}
        $relay=[NLink.Tools.LocalGuiWriterRequest]::TryCreateRelay([guid]::NewGuid(),'gui-start-run','gui-start-scenario',$Directory); $rejected=$false
        try {$null=$relay.StartFrontierGuiWorkerWithReports($process)} catch {$rejected=$true} finally {$relay.Dispose()}
        if(-not $rejected -or $request.GetType().GetField('frontierGuiStartAttempted',$flags).GetValue($request)) {throw 'Foreign/invalid launch consumed original slot'}
    }
    if($Case.StartsWith('collision-')) {
        $foreign=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$request.Name+'.frontier-gui-start-v1.'+$Case.Substring('collision-'.Length))
    }
    if($Case -ceq 'registration-failure') {$foreign=[IO.MemoryMappedFiles.MemoryMappedFile]::CreateNew($request.Name+'.frontier-parent-v1',128)}
    if($Case -ceq 'bind-failure') {$request.GetType().GetField('gate',$flags).GetValue($request).Dispose()}
    if($Case -ceq 'start-failure') {$start.FileName=Join-Path $Directory 'missing-gui-worker.exe'}
    $manual=$Case -cin @('delayed','forged-ready','both-signals','cancel','wrong-parent','timeout')
    if($manual) {
        $type=[NLink.Tools.LocalGuiWriterRequest].GetNestedType('FrontierGuiStartSignal',[Reflection.BindingFlags]'NonPublic')
        $nativeSignal=$type.GetMethod('Create',$static).Invoke($null,@($request.Name))
        if($null -eq $nativeSignal) {throw 'Manual readiness signals unavailable'}
        $request.GetType().GetField('frontierGuiStart',$flags).SetValue($request,$nativeSignal)
        $ready=$type.GetField('Ready',$flags).GetValue($nativeSignal); $failed=$type.GetField('Failed',$flags).GetValue($nativeSignal)
        $start.EnvironmentVariables['NLINK_GUI_WRITER_REQUEST']=$request.Name
        $start.EnvironmentVariables['NLINK_GUI_WRITER_REQUEST_ID']=$request.RequestId.ToString('D')
        $start.EnvironmentVariables['NLINK_GUI_FRONTIER_START_V1']='1'
        if(-not $process.Start()) {throw 'Manual GUI start failed'}
        $output=$process.StandardOutput.ReadToEndAsync(); $errorOutput=$process.StandardError.ReadToEndAsync()
        if($Case -ceq 'wrong-parent') {
            $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($request.Name); $view=$map.CreateViewAccessor()
            $view.Write(8L,$view.ReadInt64(8L)+1L)
        }
        if(-not $signals.entered.WaitOne(20000)) {throw 'Manual GUI wait entry deadline'}
        if($Case -cne 'wrong-parent' -and $signals.finished.WaitOne(250)) {throw 'GUI did not wait for native binding'}
        switch($Case) {
            'delayed' {
                if(-not $request.BindWorker($process) -or $null -eq $request.TryRegisterFrontierParent()) {throw 'Wait held native mutex or binding failed'}
                [void]$ready.Set()
            }
            'forged-ready' {[void]$ready.Set()}
            'both-signals' {[void]$failed.Set(); [void]$ready.Set()}
            'cancel' {$request.Dispose()}
        }
    } else {
        $failure=$null
        try {$reports=$request.StartFrontierGuiWorkerWithReports($process)} catch {$failure=$_.Exception}
        if($Case -ceq 'start-failure') {
            $inner=$failure; while($null -ne $inner -and $null -ne $inner.InnerException) {$inner=$inner.InnerException}
            if($null -ne $reports -or $inner -isnot [ComponentModel.Win32Exception] -or $inner.NativeErrorCode -ne 2) {throw 'Real GUI start failure hidden'}
            $failed=[Threading.EventWaitHandle]::OpenExisting($request.Name+'.frontier-gui-start-v1.failed')
            try {if(-not $failed.WaitOne(0)) {throw 'GUI start failure did not wake waiter'}} finally {$failed.Dispose()}
            'frontier-gui-start='+$Case; return
        }
        if($null -ne $failure) {throw $failure}
        $output=$reports.Stdout; $errorOutput=$reports.Stderr
        $expectedReady=$Case -cin @('valid','guards','replay','wrong-scope')
        if($reports.NativeLaunchReady -ne $expectedReady -or ($null -ne $reports.ParentRegistration) -ne $expectedReady -or
            $reports.WorkerBound -ne ($Case -cne 'bind-failure')) {throw 'GUI launch/readiness/report outcome changed'}
        if($expectedReady -and (-not $reports.ParentRegistration.IsCurrent -or $reports.ParentRegistration.GuiAttached -or
            $reports.ParentRegistration.LiveProofEligible -or $reports.ParentRegistration.NativeWriterCustodyEstablished -or $reports.ParentRegistration.PublicationCompletionIssued)) {throw 'Registration promoted beyond original launch'}
        if($Case -ceq 'replay') {
            $rejected=$false; try {$null=$request.StartFrontierGuiWorkerWithReports($process)} catch {$rejected=$true}
            if(-not $rejected -or -not $reports.ParentRegistration.IsCurrent) {throw 'Launch replay acquired/retired original registration'}
        }
    }
    if(-not $signals.finished.WaitOne(25000)) {throw 'GUI readiness completion deadline'}
    [void]$signals.release.Set()
    if(-not $process.WaitForExit(20000)) {throw 'GUI fixture exit deadline'}
    $stdout=$output.GetAwaiter().GetResult(); $stderr=$errorOutput.GetAwaiter().GetResult()
    if($process.ExitCode -ne 0 -or $stdout.TrimEnd() -cne 'ordinary GUI stdout' -or $stderr -cne 'ordinary GUI stderr') {throw ("GUI ordinary reports changed: $stdout $stderr")}
    $result=Get-Content -LiteralPath (Join-Path $Directory 'gui-start-result.json') -Raw | ConvertFrom-Json
    if($result.attached -ne ($Case -cin @('valid','guards','replay','delayed')) -or
        $result.legacyFinalizer -ne ($Case -cnotin @('bind-failure','forged-ready','both-signals','cancel','wrong-parent','timeout'))) {throw 'Native launch or separate legacy finalizer outcome changed'}
    if($Case -ceq 'timeout' -and $result.waitMilliseconds -lt 19000) {throw 'Producer idle deadline not exercised'}
    $request.Dispose()
    if($null -ne $reports -and $null -ne $reports.ParentRegistration -and $reports.ParentRegistration.IsCurrent) {throw 'Parent registration survived cleanup'}
    if($Case.StartsWith('collision-') -and $foreign.WaitOne(0)) {throw 'GUI readiness collision altered foreign signal'}
    if($Case -ceq 'registration-failure') {
        $foreignView=$foreign.CreateViewAccessor(); try {for($i=0;$i -lt 128;$i++) {if($foreignView.ReadByte($i)) {throw 'Foreign registration mapping overwritten'}}} finally {$foreignView.Dispose()}
    }
    'frontier-gui-start='+$Case
} finally {
    [void]$signals.release.Set()
    try {if(-not $process.HasExited) {$process.Kill(); [void]$process.WaitForExit(5000)}} catch [InvalidOperationException] { }
    $process.Dispose(); $request.Dispose()
    if($null -ne $view) {$view.Dispose()}; if($null -ne $map) {$map.Dispose()}; if($null -ne $foreign) {$foreign.Dispose()}
    foreach($signal in $signals.Values) {$signal.Dispose()}
}
