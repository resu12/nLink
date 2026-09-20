param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$Directory,[string]$Case,[string]$DeathResult='',
    [string]$CoreAssembly,[switch]$App,[switch]$HoldPipe,[string]$SignalName)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
function Initialize-NativeOutput {
    if($null -eq ('OutputFixtureNative' -as [type])) {
        Add-Type 'using System; using System.Runtime.InteropServices; public static class OutputFixtureNative { [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int kind); [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle); [DllImport("kernel32.dll")] public static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags); }'
    }
}
function Close-NativeOutput([int]$Which) {
    Initialize-NativeOutput
    if(-not [OutputFixtureNative]::CloseHandle([OutputFixtureNative]::GetStdHandle($Which))) { throw 'Fixture pipe close failed' }
}
if($HoldPipe) {
    $ready=[Threading.EventWaitHandle]::OpenExisting($SignalName+'.held-ready')
    $release=[Threading.EventWaitHandle]::OpenExisting($SignalName+'.held-release')
    try {
        [void]$ready.Set()
        if(-not $release.WaitOne(25000)) { exit 9 }
    } finally { $ready.Dispose(); $release.Dispose() }
    exit 0
}
if($App) {
    $events=@{}
    foreach($part in @('begin','ready','emit','done','exit','held-ready')) { $events[$part]=[Threading.EventWaitHandle]::OpenExisting($SignalName+'.'+$part) }
    $lease=$null
    try {
        if(-not $events.begin.WaitOne(20000)) { throw 'App bind deadline' }
        [void][Reflection.Assembly]::LoadFrom($CoreAssembly)
        $lease=[NLink.Tools.LocalFrontierAppRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario')
        if($null -eq $lease) { throw 'App attachment failed' }
        [void]$events.ready.Set()
        if(-not $events.emit.WaitOne(20000)) { throw 'App emit deadline' }
        if($Case -cne 'empty') {
            # Longer than the pump buffer, with no line framing or UTF-8 decoding.
            $bytes=[byte[]]::new(70003)
            for($i=0;$i -lt $bytes.Length;$i++) { $bytes[$i]=[byte]($i % 256) }
            $out=[Console]::OpenStandardOutput(); $err=[Console]::OpenStandardError()
            $out.Write($bytes,0,$bytes.Length); $out.Flush()
            $err.Write($bytes,0,$bytes.Length); $err.Flush()
        }
        if($Case -cin @('held-stdout','held-stderr')) {
            # No child redirection: inherit exactly the selected original pipe,
            # not both originals followed by closing just one standard alias.
            Initialize-NativeOutput
            foreach($kind in @(-11,-12)) {
                $inherit=if(($Case -ceq 'held-stdout' -and $kind -eq -11) -or ($Case -ceq 'held-stderr' -and $kind -eq -12)) {1} else {0}
                if(-not [OutputFixtureNative]::SetHandleInformation([OutputFixtureNative]::GetStdHandle($kind),1,$inherit)) { throw 'Fixture inherit flag failed' }
            }
            $start=[Diagnostics.ProcessStartInfo]::new()
            $start.FileName=(Get-Command pwsh).Source; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
            $args=@('-NoProfile','-File',$PSCommandPath,'-HoldPipe','-SignalName',$SignalName,'-Case',$Case)
            $start.Arguments=($args | ForEach-Object { '"'+$_+'"' }) -join ' '
            $holder=[Diagnostics.Process]::Start($start)
            try { if(-not $events['held-ready'].WaitOne(15000)) { throw 'Pipe holder did not start' } } finally { $holder.Dispose() }
        }
        $complete=$lease.CompleteAttachment()
        if($complete -ne ($Case -cnotin @('repeat-start','after-seal','cancel-after','owner-disposed'))) { throw 'Wrong app completion' }
        if($Case -ceq 'early-eof') { Close-NativeOutput -11; Close-NativeOutput -12 }
        [void]$events.done.Set()
        if(-not $events.exit.WaitOne(20000)) { throw 'App exit deadline' }
        if($Case -ceq 'nonzero') { exit 7 }
    } finally {
        if($null -ne $lease) { $lease.Dispose() }
        foreach($event in $events.Values) { $event.Dispose() }
    }
    exit 0
}
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$begin=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.begin')
$attached=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.attached')
$release=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.release')
$apps=[Collections.Generic.List[object]]::new()
$owner=$null; $finalizer=$null
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
function Wait-Check([scriptblock]$Check,[string]$Message) {
    $clock=[Diagnostics.Stopwatch]::StartNew()
    while(-not (& $Check)) {
        if($clock.ElapsedMilliseconds -ge 10000) { throw $Message }
        Start-Sleep -Milliseconds 10
    }
}
function New-OutputApp($Request,[int]$Ordinal) {
    $signal=$Name+'.output.'+$Ordinal
    $events=@{}
    foreach($part in @('begin','ready','emit','done','exit','held-ready','held-release')) {
        $events[$part]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$signal+'.'+$part)
    }
    $process=[Diagnostics.Process]::new()
    $process.StartInfo.FileName=(Get-Command pwsh).Source
    $process.StartInfo.UseShellExecute=$false; $process.StartInfo.CreateNoWindow=$true
    $process.StartInfo.RedirectStandardOutput=$true; $process.StartInfo.RedirectStandardError=$true
    $args=@('-NoProfile','-File',$PSCommandPath,'-App','-Name',$Request.Name,'-RequestId',$Request.RequestId.ToString('D'),'-CoreAssembly',$CoreAssembly,'-SignalName',$signal,'-Case',$Case)
    $process.StartInfo.Arguments=($args | ForEach-Object { '"'+$_+'"' }) -join ' '
    $entry=@{Process=$process;Events=$events;Request=$Request;Binding=$null;Out=$null;Err=$null;Started=$false}
    $apps.Add($entry)
    return $entry
}
try {
    if(-not $begin.WaitOne(20000)) { throw 'GUI binding deadline' }
    $owner=[NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncher($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
    if($null -eq $owner -or $owner.AllAppOutputsDrainedSuccessfully) { throw 'GUI owner unavailable/early EOF' }
    $requests=@($owner.TryCreateAppRequest(),$owner.TryCreateAppRequest())
    if($Case -ceq 'copied') {
        $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($requests[0],$null)
        $unused=[Diagnostics.Process]::new()
        try { if($null -ne $copy.StartAppWithOutput($unused)) { throw 'Copied request started app' } } finally { $unused.Dispose() }
        $copy.Dispose()
        if(-not $requests[0].IsCurrent) { throw 'Copied request revoked original' }
    }
    $invalid=$Case -cin @('no-stdout','no-stderr','already-started','start-failure','sink-open-failure')
    for($i=0;$i -lt 2;$i++) {
        $entry=New-OutputApp $requests[$i] $i
        if($invalid) {
            if($Case -ceq 'no-stdout') { $entry.Process.StartInfo.RedirectStandardOutput=$false }
            if($Case -ceq 'no-stderr') { $entry.Process.StartInfo.RedirectStandardError=$false }
            if($Case -ceq 'start-failure') { $entry.Process.StartInfo.FileName=Join-Path $Directory 'missing-app.exe' }
            if($Case -ceq 'sink-open-failure') {
                [void][IO.Directory]::CreateDirectory((Join-Path $Directory ('frontier-process-output-'+$RequestId.ToString('N')+'-0-stderr.log')))
            }
            if($Case -ceq 'already-started') {
                [void]$entry.Process.Start(); $entry.Started=$true
                $entry.Out=$entry.Process.StandardOutput.ReadToEndAsync(); $entry.Err=$entry.Process.StandardError.ReadToEndAsync()
                $originalId=$entry.Process.Id
            }
            if($null -ne $entry.Request.StartAppWithOutput($entry.Process) -or $owner.IsCurrent) { throw 'Invalid output launch accepted' }
            if($Case -ceq 'already-started' -and ($entry.Process.Id -ne $originalId -or $entry.Process.HasExited)) { throw 'Existing process restarted/stopped' }
            break
        }
        if($Case -ceq 'missing-capture' -and $i -eq 1) {
            [void]$entry.Process.Start()
            $entry.Out=$entry.Process.StandardOutput.ReadToEndAsync(); $entry.Err=$entry.Process.StandardError.ReadToEndAsync()
            $entry.Binding=$entry.Request.BindApp($entry.Process)
        } else { $entry.Binding=$entry.Request.StartAppWithOutput($entry.Process) }
        $entry.Started=$true
        if($null -eq $entry.Binding -or $entry.Binding.OutputDrainedSuccessfully) { throw 'Original output launch missing/early EOF' }
        if($Case -ceq 'capture-fault' -and $i -eq 0) {
            $capture=$entry.Binding.GetType().GetField('Output',$flags).GetValue($entry.Binding)
            $capture.GetType().GetField('stdoutTarget',$flags).GetValue($capture).Dispose()
        }
        [void]$entry.Events.begin.Set()
        if(-not $entry.Events.ready.WaitOne(15000)) { throw 'App attachment deadline' }
    }
    if(-not $invalid) {
        if(-not $owner.SealLaunches()) { throw 'Original output registry did not seal' }
        if($Case -ceq 'repeat-start') {
            if($null -ne $requests[0].StartAppWithOutput($apps[0].Process)) { throw 'Repeated start accepted' }
        }
        if($Case -ceq 'after-seal') {
            $unused=[Diagnostics.Process]::new()
            try { if($null -ne $requests[0].StartAppWithOutput($unused)) { throw 'Post-seal start accepted' } } finally { $unused.Dispose() }
        }
        if($Case -ceq 'owner-disposed') { $owner.Dispose() }
    }
    [void]$attached.Set()
    if(-not $release.WaitOne(20000)) { throw 'GUI release deadline' }
    if($invalid) {
        if($owner.AllAppOutputsDrainedSuccessfully) { throw 'Invalid launch acquired EOF evidence' }
        if($Case -cin @('start-failure','sink-open-failure')) {
            $paths=@('stdout')
            if($Case -ceq 'start-failure') { $paths+=@('stderr') }
            foreach($stream in $paths) {
                $path=Join-Path $Directory ('frontier-process-output-'+$RequestId.ToString('N')+'-0-'+$stream+'.log')
                $probe=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
                $probe.Dispose()
            }
        }
        'frontier-gui-worker=invalid-output-rejected'
        exit 0
    }
    $revoked=$Case -cin @('repeat-start','after-seal','cancel-after','owner-disposed')
    if($owner.IsCurrent -eq $revoked) { throw 'Wrong original output owner status' }
    if(@($apps | Where-Object {$_.Process.HasExited}).Count) { throw 'Revocation stopped apps' }
    foreach($entry in $apps) { [void]$entry.Events.emit.Set() }
    foreach($entry in $apps) { if(-not $entry.Events.done.WaitOne(15000)) { throw 'Output app did not finish writes' } }
    if($Case -ceq 'early-eof') {
        Wait-Check { $owner.AllAppOutputsDrainedSuccessfully } 'Closed pipes did not report EOF'
        if($owner.AllAppsExitedSuccessfully -or $owner.AllAppRequestsCompletedSuccessfully) { throw 'EOF granted native exit evidence' }
    } elseif($owner.AllAppOutputsDrainedSuccessfully) { throw 'Open pipes reported EOF from quiet capture files' }
    foreach($entry in $apps) { [void]$entry.Events.exit.Set() }
    foreach($entry in $apps) {
        if(-not $entry.Process.WaitForExit(15000)) { throw 'Output app did not exit' }
        $expected=if($Case -ceq 'nonzero') {7} else {0}
        if($entry.Process.ExitCode -ne $expected) { throw "Output app exited $($entry.Process.ExitCode)" }
    }
    if($Case -cin @('held-stdout','held-stderr')) {
        if(-not $owner.AllAppRequestsCompletedSuccessfully) { throw 'Original app exits/completions missing' }
        foreach($entry in $apps) {
            $capture=$entry.Binding.GetType().GetField('Output',$flags).GetValue($entry.Binding)
            $closedStream=if($Case -ceq 'held-stdout') {'stderr'} else {'stdout'}
            $task=$capture.GetType().GetField($closedStream,$flags).GetValue($capture)
            Wait-Check { $task.IsCompleted } 'Non-inherited pipe failed to reach EOF'
        }
        if($owner.AllAppOutputsDrainedSuccessfully) { throw 'Successful exit substituted for inherited-pipe EOF' }
        foreach($entry in $apps) { [void]$entry.Events['held-release'].Set() }
    }
    # Wait for the original pumps, not filesystem quiet time. Read failures and
    # target failures remain permanently ineligible even when tasks complete.
    foreach($entry in $apps) {
        $capture=$entry.Binding.GetType().GetField('Output',$flags).GetValue($entry.Binding)
        if($null -ne $capture) {
            $outTask=$capture.GetType().GetField('stdout',$flags).GetValue($capture)
            $errTask=$capture.GetType().GetField('stderr',$flags).GetValue($capture)
            Wait-Check { $outTask.IsCompleted -and $errTask.IsCompleted } 'Original capture did not finish'
        }
    }
    $expectedDrain=-not $revoked -and $Case -cnotin @('missing-capture','capture-fault')
    if($owner.AllAppOutputsDrainedSuccessfully -ne $expectedDrain) { throw 'Wrong final original output evidence' }
    if($Case -ceq 'nonzero' -and $owner.AllAppsExitedSuccessfully) { throw 'Output EOF upgraded failed native exit' }
    if(-not $revoked -and $Case -cne 'nonzero' -and -not $owner.AllAppRequestsCompletedSuccessfully) { throw 'Output capture lost attachment/exit evidence' }
    if($Case -ceq 'copied') {
        $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($apps[0].Binding,$null)
        $ownerCopy=[object].GetMethod('MemberwiseClone',$flags).Invoke($owner,$null)
        if($copy.OutputDrainedSuccessfully -or $ownerCopy.AllAppOutputsDrainedSuccessfully) { throw 'Copied receipt acquired EOF evidence' }
        $ownerCopy.Dispose()
        if(-not $owner.AllAppOutputsDrainedSuccessfully) { throw 'Copied owner disposed original EOF evidence' }
    }
    if($Case -ceq 'valid') {
        foreach($entry in $apps) { $entry.Process.Dispose(); $entry.Started=$false }
        if(-not $owner.AllAppOutputsDrainedSuccessfully -or -not $owner.AllAppRequestsCompletedSuccessfully) { throw 'Closed Process wrappers lost original retained evidence' }
    }
    for($i=0;$i -lt $apps.Count;$i++) {
        if($Case -ceq 'missing-capture' -and $i -eq 1) { continue }
        foreach($stream in @('stdout','stderr')) {
            if($Case -ceq 'capture-fault' -and $i -eq 0 -and $stream -ceq 'stdout') { continue }
            $path=Join-Path $Directory ('frontier-process-output-'+$RequestId.ToString('N')+'-'+$i+'-'+$stream+'.log')
            # The same creation file object now remains held read-only after EOF.
            # Its original sharing contract still requires readers to share write.
            $read=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
            $copy=[IO.MemoryStream]::new()
            try { $read.CopyTo($copy); $bytes=$copy.ToArray() } finally { $copy.Dispose(); $read.Dispose() }
            $expected=if($Case -ceq 'empty') {0} else {70003}
            if($bytes.Length -ne $expected) { throw "Wrong raw capture length: $stream $($bytes.Length)" }
            for($j=0;$j -lt $bytes.Length;$j++) { if($bytes[$j] -ne [byte]($j % 256)) { throw 'Raw output capture changed bytes' } }
        }
    }
    if($Case -cne 'cancel-after') {
        $finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
        if($null -eq $finalizer) { throw 'Output capture consumed legacy finalizer' }
    }
    if($owner.LiveProofEligible -or $owner.NativeWriterCustodyEstablished -or $owner.PublicationCompletionIssued) { throw 'EOF evidence enabled publication' }
    $owner.Dispose()
    if($owner.AllAppOutputsDrainedSuccessfully) { throw 'EOF evidence survived revocation' }
    'frontier-gui-worker=original-output-verified'
} finally {
    if($null -ne $owner) { $owner.Dispose() }
    foreach($entry in $apps) {
        foreach($part in @('begin','emit','exit','held-release')) { [void]$entry.Events[$part].Set() }
        if($entry.Started -and -not $entry.Process.HasExited -and -not $entry.Process.WaitForExit(5000)) { $entry.Process.Kill(); [void]$entry.Process.WaitForExit(5000) }
        $entry.Process.Dispose()
        foreach($event in $entry.Events.Values) { $event.Dispose() }
    }
    if($null -ne $finalizer) { $finalizer.Dispose() }
    $begin.Dispose(); $attached.Dispose(); $release.Dispose()
}
