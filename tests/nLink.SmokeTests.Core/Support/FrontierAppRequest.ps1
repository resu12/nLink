param([string]$RepoRoot,[string]$Directory,[string]$OnlyCase='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierAppRequest.ps1')

foreach($type in @([NLink.Tools.LocalFrontierAppRequest],[NLink.Tools.LocalFrontierAppRequest+AppLease],[NLink.Tools.LocalFrontierAppRequest+AttachmentCompletion])) {
    if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object { $null -ne $_.GetSetMethod() }).Count) { throw 'Public native receipt construction/mutation exposed' }
}
foreach($value in @('', '(none)', "bad`nrun", "bad`0run", ('x'*1025), [string][char]0xd800)) {
    $bad=[NLink.Tools.LocalFrontierAppRequest]::TryCreate($value,'scenario')
    if($null -ne $bad) { $bad.Dispose(); throw 'Invalid registration admitted' }
}
$bad=[NLink.Tools.LocalFrontierAppRequest]::TryCreate(([string][char]0x20ac)*1024,([string][char]0x20ac)*1024)
if($null -ne $bad) { $bad.Dispose(); throw 'UTF8 registration byte cap bypassed' }
$exact=[NLink.Tools.LocalFrontierAppRequest]::TryCreate(([string][char]0x20ac)*1024,('s'*1020))
if($null -eq $exact) { throw 'Exact 4096-byte registration rejected' }
$exact.Dispose()
$over=[NLink.Tools.LocalFrontierAppRequest]::TryCreate(([string][char]0x20ac)*1024,('s'*1021))
if($null -ne $over) { $over.Dispose(); throw '4097-byte registration admitted' }
$boundary=[NLink.Tools.LocalFrontierAppRequest]::TryCreate(('r'*1024),('s'*1024))
if($null -eq $boundary) { throw 'Bounded registration rejected' }
$self=[Diagnostics.Process]::GetCurrentProcess()
try { if($boundary.BindApp($self) -or $boundary.BindApp($self) -or $null -ne $boundary.TakeAfterExit()) { throw 'Parent was accepted as the app' } }
finally { $self.Dispose(); $boundary.Dispose() }
$deadStart=[Diagnostics.ProcessStartInfo]::new()
$deadStart.FileName=(Get-Process -Id $PID).Path; $deadStart.Arguments='-NoProfile -Command exit 0'
$deadStart.UseShellExecute=$false; $deadStart.CreateNoWindow=$true
$dead=[Diagnostics.Process]::Start($deadStart)
$deadRequest=[NLink.Tools.LocalFrontierAppRequest]::TryCreate('run','scenario')
try {
    if(-not $dead.WaitForExit(15000)) { throw 'Exited-process fixture timed out' }
    if($deadRequest.BindApp($dead) -or $null -ne $deadRequest.TakeAfterExit()) { throw 'Already-exited app bound' }
} finally { if(-not $dead.HasExited) { $dead.Kill(); [void]$dead.WaitForExit(5000) }; $dead.Dispose(); $deadRequest.Dispose() }

$cases=@('valid','wrong-run','wrong-scenario','wrong-request','app-incarnation','parent-incarnation','packet-version','packet-metadata','packet-length','packet-reserved',
    'cancel-before-attach','cancel-after-attach','early-take','partial','nonzero-exit','lease-disposed','request-disposed',
    'completion-contended','cancel-after-complete','completion-incarnation','scope-after-attach','completion-metadata','killed-app')
if($OnlyCase) { $cases=@($OnlyCase) }
$results=[Collections.Generic.List[object]]::new()
foreach($case in $cases) {
    $request=[NLink.Tools.LocalFrontierAppRequest]::TryCreate('frontier-native-run','frontier-native-scenario')
    if($null -eq $request) { throw 'Native request unavailable' }
    $name=$request.Name
    $events=@{}
    foreach($part in @('begin','attached','release','attempted','retry')) { $events[$part]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$name+'.test.'+$part) }
    $process=$null; $held=$false; $mutex=$null; $view=$null; $map=$null
    try {
        $start=[Diagnostics.ProcessStartInfo]::new()
        $start.FileName=(Get-Process -Id $PID).Path
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        $worker=Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAppWorker.ps1'
        $death=Join-Path $Directory 'parent-death-result.json'
        $arguments=@('-NoProfile','-ExecutionPolicy','Bypass','-File',$worker,'-RepoRoot',$RepoRoot,'-Name',$name,'-RequestId',$request.RequestId.ToString('D'),'-Case',$case,'-DeathResult',$death)
        $start.Arguments=($arguments | ForEach-Object { '"'+$_.Replace('"','\"')+'"' }) -join ' '
        $process=[Diagnostics.Process]::Start($start)
        [void]$process.Handle
        $stdout=$process.StandardOutput.ReadToEndAsync(); $stderr=$process.StandardError.ReadToEndAsync()
        if(-not $request.BindApp($process) -or $request.BindApp($process)) { throw 'Native process binding not one-shot' }
        if($null -ne [NLink.Tools.LocalFrontierAppRequest]::TryAttach($name,$request.RequestId,'frontier-native-run','frontier-native-scenario')) { throw 'Foreign parent attached as app' }
        $map=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name); $view=$map.CreateViewAccessor()
        switch($case) {
            'app-incarnation' { $view.Write(32L,$view.ReadInt64(32L)+1L) }
            'parent-incarnation' { $view.Write(16L,$view.ReadInt64(16L)+1L) }
            'packet-version' { $view.Write(4L,99) }
            'packet-metadata' { $view.Write(64L,[byte]0) }
            'packet-length' { $view.Write(56L,4097) }
            'packet-reserved' { $view.Write(28L,1) }
            'cancel-before-attach' { $request.Cancel() }
        }
        [void]$events.begin.Set()
        if(-not $events.attached.WaitOne(15000)) { throw "App did not report attachment: $case" }
        if($case -ceq 'parent-death') {
            # Deliberately bypass finally/Dispose: the child must observe native
            # parent process exit, not a managed cancellation notification.
            'frontier-app-parent-death='+(@{AppProcessId=$process.Id;AppCreation=$process.StartTime.ToUniversalTime().ToFileTimeUtc();Result=$death} | ConvertTo-Json -Compress)
            [Environment]::Exit(0)
        }
        switch($case) {
            'cancel-after-attach' { $request.Cancel() }
            'early-take' { if($null -ne $request.TakeAfterExit()) { throw 'Live app accepted' } }
            'request-disposed' { $request.Dispose() }
            'scope-after-attach' { $view.Write(64L,[byte]0) }
            'killed-app' { $process.Kill(); [void]$process.WaitForExit(5000) }
            'completion-contended' { $mutex=[Threading.Mutex]::OpenExisting($name+'.gate'); $held=$mutex.WaitOne(0); if(-not $held) { throw 'Fixture contention unavailable' } }
        }
        [void]$events.release.Set()
        if($case -ceq 'completion-contended') {
            if(-not $events.attempted.WaitOne(15000)) { throw 'Contended attempt missing' }
            $mutex.ReleaseMutex(); $held=$false; [void]$events.retry.Set()
        }
        if(-not $process.WaitForExit(15000)) { throw "App fixture timed out: $case" }
        $process.WaitForExit()
        $out=$stdout.GetAwaiter().GetResult(); $err=$stderr.GetAwaiter().GetResult()
        $exitExpected=if($case -ceq 'nonzero-exit') {7} else {0}
        if($case -cne 'killed-app' -and ($process.ExitCode -ne $exitExpected -or $out -notmatch 'frontier-app-worker=')) { throw "Fixture failed $case`: $out $err" }
        if($case -ceq 'cancel-after-complete') { $request.Cancel() }
        if($case -ceq 'completion-incarnation') { $view.Write(32L,$view.ReadInt64(32L)+1L) }
        if($case -ceq 'completion-metadata') { $view.Write(64L,[byte]0) }
        $completion=$request.TakeAfterExit(); $expected=$case -ceq 'valid'
        if(($null -ne $completion) -ne $expected -or $null -ne $request.TakeAfterExit()) { throw "Wrong parent result: $case" }
        if($expected) {
            if($completion.RequestId -ne $request.RequestId -or $completion.AppProcessId -ne $process.Id -or
                $completion.AppProcessCreationFileTime -ne $process.StartTime.ToUniversalTime().ToFileTimeUtc() -or $completion.ParentProcessId -ne $PID -or
                $completion.AcceptanceRunId -cne 'frontier-native-run' -or $completion.ScenarioRunId -cne 'frontier-native-scenario' -or
                $completion.LiveProofEligible -or $completion.NativeWriterCustodyEstablished -or $completion.PublicationCompletionIssued) { throw 'Native attachment identity/flags mismatch' }
        }
        $results.Add(@{Case=$case;AttachedProcess=$process.Id;Accepted=$expected;WriterCustody=$false;PayloadPublished=$false})
    } finally {
        if($held) { $mutex.ReleaseMutex() }
        if($null -ne $mutex) { $mutex.Dispose() }
        if($null -ne $view) { $view.Dispose() }
        if($null -ne $map) { $map.Dispose() }
        if($null -ne $process) { if(-not $process.HasExited) { $process.Kill(); [void]$process.WaitForExit(5000) }; $process.Dispose() }
        $request.Dispose()
        foreach($event in $events.Values) { $event.Dispose() }
    }
    $leftover=$null
    try { $leftover=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name) } catch [IO.FileNotFoundException] { }
    if($null -ne $leftover) { $leftover.Dispose(); throw 'Owned endpoint was not cleaned up' }
}
'frontier-app-request='+(@{Cases=$results.Count;Results=@($results.ToArray());FileIo=$false} | ConvertTo-Json -Depth 4 -Compress)
