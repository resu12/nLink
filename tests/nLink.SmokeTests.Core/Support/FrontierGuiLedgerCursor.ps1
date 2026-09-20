param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$Directory,[string]$Case,[string]$DeathResult='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$clone=[object].GetMethod('MemberwiseClone',$flags)
$events=@{}; $apps=[Collections.Generic.List[object]]::new(); $owner=$null; $finalizer=$null
foreach($part in @('begin','attached','release','app-exit')) {
    if($part -ceq 'app-exit') { $events[$part]=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$Name+'.test.'+$part) }
    else { $events[$part]=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.'+$part) }
}
function Read-CursorField([string]$Field) { $origin.GetType().GetField($Field,$flags).GetValue($origin) }
try {
    if(-not $events.begin.WaitOne(20000)) { throw 'Cursor GUI bind deadline' }
    $owner=[NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncher($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
    if($null -eq $owner) { throw 'Cursor original owner missing' }
    $origin=$owner.TryCreateLedgerOrigin(); $path=$origin.SourcePath; $native=Read-CursorField 'file'
    $type=[NLink.Tools.LocalGuiWriterRequest+FrontierLedgerObservation]
    if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) { throw 'Public cursor observation fabrication exposed' }
    $count=if($Case -ceq 'no-app') {0} elseif($Case -ceq 'two-apps') {2} else {1}
    for($i=0;$i -lt $count;$i++) {
        $start=[Diagnostics.ProcessStartInfo]::new(); $start.FileName=(Get-Process -Id $PID).Path
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.Arguments="-NoProfile -Command `"`$e=[Threading.EventWaitHandle]::OpenExisting('$Name.test.app-exit'); [void]`$e.WaitOne(20000); `$e.Dispose()`""
        $app=[Diagnostics.Process]::Start($start); $apps.Add($app)
        if($null -eq $owner.BindApp($app)) { throw 'Cursor app binding missing' }
    }
    [void]$events.attached.Set()
    if(-not $events.release.WaitOne(20000)) { throw 'Cursor GUI release deadline' }
    if($Case -ceq 'exited-app') { [void]$events['app-exit'].Set(); if(-not $apps[0].WaitForExit(10000)) { throw 'Cursor app exit control missing' } }
    $text="a`nb`r`nc`r"
    if($Case -ceq 'chunk') { $text=('x'*32767)+"`n"+('y'*32767)+"`r`npartial" }
    if($Case -cin @('exact-lines','over-lines')) { $text="`n"*([NLink.Tools.LocalGuiWriterRequest+FrontierLedgerOrigin]::MaximumIndexedLines+[int]($Case -ceq 'over-lines')) }
    [IO.File]::AppendAllText($path,$text,[Text.UTF8Encoding]::new($false))
    if($Case -ceq 'copied-origin') {
        $copy=$clone.Invoke($origin,$null)
        if($copy.TrySynchronize() -or $null -ne $copy.TryCaptureInitialObservation()) { throw 'Copied origin operated cursor' }
        $copy.Dispose(); if(-not $origin.IsCurrent) { throw 'Copied origin retired cursor' }
    }
    $initial=$origin.TryCaptureInitialObservation()
    if($Case -cin @('no-app','two-apps','exited-app','over-lines')) {
        if($null -ne $initial -or $owner.IsCurrent -or $native.CanRead) { throw 'Invalid original initial observation accepted' }
    } else {
        $expectedLines=if($Case -ceq 'exact-lines') {262144} else {2}
        $expectedBytes=[Text.Encoding]::UTF8.GetByteCount($text)
        $committed=if($Case -ceq 'exact-lines') {$expectedBytes} elseif($Case -ceq 'chunk') {65537} else {5}
        if($null -eq $initial -or -not $initial.IsCurrent -or $initial.IsFinal -or $initial.LineCount -ne $expectedLines -or
            $initial.Bookmark -ne $expectedLines -or $initial.BytesRead -ne $expectedBytes -or $initial.CommittedBytes -ne $committed -or
            $initial.SynchronizationCount -ne 1 -or $initial.LedgerId -cne $origin.LedgerId) { throw 'Wrong original initial cursor facts' }
        if($Case -ceq 'copied-observation') {
            $copy=$clone.Invoke($initial,$null); if($copy.IsCurrent) { throw 'Copied observation current' }
            $copy.Dispose(); if(-not $initial.IsCurrent) { throw 'Copied observation retired original' }
        }
        if($Case -cne 'exact-lines') {
            if(-not $origin.TrySynchronize()) { throw 'Unchanged sync failed' }
            [IO.File]::AppendAllText($path,'d',[Text.UTF8Encoding]::new($false))
            if(-not $origin.TrySynchronize() -or (Read-CursorField 'committedPosition') -ne $committed) { throw 'Partial tail incorrectly committed' }
            [IO.File]::AppendAllText($path,"`nlast`n",[Text.UTF8Encoding]::new($false))
            if(-not $origin.TrySynchronize() -or (Read-CursorField 'readPosition') -ne ($expectedBytes+7) -or
                (Read-CursorField 'lineStarts').Count -ne 4 -or (Read-CursorField 'synchronizationCount') -ne 4) { throw 'Incremental indexing lost bytes or lines' }
            $starts=Read-CursorField 'lineStarts'
            if($starts[0] -ne 0 -or $starts[1] -ne $(if($Case -ceq 'chunk') {32768} else {2}) -or $starts[2] -ne $committed -or $starts[3] -ne ($expectedBytes+2)) { throw 'Gap or duplicate line index' }
            if($initial.LineCount -ne 2 -or $initial.BytesRead -ne $expectedBytes -or $initial.CommittedBytes -ne $committed) { throw 'Initial observation mutated' }
        } elseif(-not $origin.TrySynchronize()) { throw 'Exact line cap rejected on no-op sync' }
        if($initial.CursorHistoryValidated -or $initial.LiveProofEligible -or $initial.NativeWriterCustodyEstablished -or $initial.PublicationCompletionIssued) { throw 'Observation enabled authority' }
        if($Case -ceq 'repeat-initial') { if($null -ne $origin.TryCaptureInitialObservation()) { throw 'Repeated initial observation accepted' } }
        elseif($Case -ceq 'shrink') {
            $write=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
            try { $write.SetLength($native.Length-1) } finally { $write.Dispose() }
            if($origin.TrySynchronize()) { throw 'Shrunken cursor accepted' }
        } elseif($Case -ceq 'position') { $native.Position=0; if($origin.TrySynchronize()) { throw 'Changed cursor position accepted' } }
        elseif($Case -ceq 'closed') { $native.Dispose(); if($origin.TrySynchronize()) { throw 'Closed cursor accepted' } }
        elseif($Case -ceq 'budget') {
            $work=$owner.GetType().GetField('frontierWork',$flags).GetValue($owner)
            $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*6))
            if($origin.TrySynchronize()) { throw 'Expired cursor accepted' }
        } elseif($Case -ceq 'cancel') {
            $cancel=[Threading.EventWaitHandle]::OpenExisting($Name+'.closed')
            try { [void]$cancel.Set(); if($origin.TrySynchronize()) { throw 'Cancelled cursor accepted' }; [void]$cancel.Reset() } finally { $cancel.Dispose() }
        } elseif($Case -ceq 'complete') {
            $finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
            if($null -eq $finalizer -or $finalizer.Complete('','',0,0,$null,$null,$null,$null,$null,$null)) { throw 'Legacy completion control failed' }
        } else { $initial.Dispose() }
        if($initial.IsCurrent -or $origin.IsCurrent -or $owner.IsCurrent -or $native.CanRead) { throw 'Retired cursor retained observations/resources' }
    }
    [IO.File]::AppendAllText($path,'released')
    'frontier-gui-worker=original-ledger-cursor-verified'
} finally {
    if($null -ne $owner) { $owner.Dispose() }
    if($null -ne $finalizer) { $finalizer.Dispose() }
    [void]$events['app-exit'].Set()
    foreach($app in $apps) { if(-not $app.WaitForExit(5000)) { $app.Kill(); [void]$app.WaitForExit(5000) }; $app.Dispose() }
    foreach($event in $events.Values) { $event.Dispose() }
}
