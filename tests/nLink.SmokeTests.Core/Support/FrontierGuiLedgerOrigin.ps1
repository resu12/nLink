param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$Directory,[string]$Case,[string]$DeathResult='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$clone=[object].GetMethod('MemberwiseClone',$flags)
$events=@{}
foreach($part in @('begin','attached','release')) { $events[$part]=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.'+$part) }
$owner=$null; $origin=$null; $finalizer=$null; $native=$null
$path=Join-Path $Directory 'live-operational-log/nlink.log'
try {
    if(-not $events.begin.WaitOne(20000)) { throw 'Ledger origin GUI bind deadline' }
    $owner=[NLink.Tools.LocalGuiWriterRequest]::TryAttachFrontierLauncher($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
    if($null -eq $owner) { throw 'Ledger origin owner missing' }
    $type=[NLink.Tools.LocalGuiWriterRequest+FrontierLedgerOrigin]
    if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) { throw 'Public ledger origin fabrication exposed' }
    $work=$owner.GetType().GetField('frontierWork',$flags).GetValue($owner)
    if($Case -ceq 'existing') {
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)); [IO.File]::WriteAllText($path,'preserve-existing')
    }
    if($Case -ceq 'after-request' -and $null -eq $owner.TryCreateAppRequest()) { throw 'Late creation request control failed' }
    if($Case -ceq 'copied-owner') {
        $copy=$clone.Invoke($owner,$null)
        if($null -ne $copy.TryCreateLedgerOrigin()) { throw 'Copied owner created ledger origin' }
        $copy.Dispose(); if(-not $owner.IsCurrent -or [IO.File]::Exists($path)) { throw 'Copied owner changed original creation' }
    }
    if($Case -ceq 'budget') {
        $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*16))
    }
    $origin=$owner.TryCreateLedgerOrigin()
    if($Case -cin @('existing','after-request','budget')) {
        if($null -ne $origin -or $owner.IsCurrent) { throw 'Invalid original creation accepted' }
        if($Case -ceq 'existing' -and [IO.File]::ReadAllText($path) -cne 'preserve-existing') { throw 'Creation changed existing file' }
        if($Case -cne 'existing' -and [IO.File]::Exists($path)) { throw 'Rejected creation left an unexpected source' }
    } else {
        if($null -eq $origin -or -not $origin.IsCurrent -or -not $origin.WasCreatedEmpty -or $origin.SourcePath -cne [IO.Path]::GetFullPath($path) -or
            $origin.LedgerId -notmatch '^[0-9a-f]{32}$' -or $origin.CursorHistoryValidated -or $origin.LiveProofEligible -or
            $origin.NativeWriterCustodyEstablished -or $origin.PublicationCompletionIssued) { throw 'Original ledger creation missing or granted approval' }
        $native=$origin.GetType().GetField('file',$flags).GetValue($origin)
        if(-not $native.CanRead -or $native.CanWrite -or $native.Length -ne 0 -or $work.IsRunning) { throw 'Wrong native creation access/budget' }
        Add-Type 'using System; using System.Runtime.InteropServices; using Microsoft.Win32.SafeHandles; public static class OriginNativeWrite { [DllImport("kernel32.dll",SetLastError=true)] public static extern bool WriteFile(SafeFileHandle file, byte[] bytes, uint size, out uint written, IntPtr overlap); }'
        [uint32]$written=0
        if([OriginNativeWrite]::WriteFile($native.SafeFileHandle,[byte[]]@(1),1,[ref]$written,[IntPtr]::Zero) -or [Runtime.InteropServices.Marshal]::GetLastWin32Error() -ne 5) { throw 'Native origin handle permits writing' }
        [IO.File]::AppendAllText($path,'append-allowed')
        if(-not $origin.IsCurrent -or $native.Length -eq 0) { throw 'Original origin did not permit app writes' }
        $blocked=$false; try { [IO.File]::Move($path,$path+'.replacement') } catch [IO.IOException] { $blocked=$true }
        if(-not $blocked) { throw 'Owned original file could be replaced' }
        if($Case -ceq 'copied-origin') {
            $copy=$clone.Invoke($origin,$null)
            if($copy.IsCurrent) { throw 'Copied origin current' }
            $copy.Dispose(); if(-not $origin.IsCurrent) { throw 'Copied origin disposed original' }
        }
    }
    [void]$events.attached.Set()
    if(-not $events.release.WaitOne(20000)) { throw 'Ledger origin release deadline' }
    if($null -ne $origin) {
        if($Case -ceq 'repeat') { if($null -ne $owner.TryCreateLedgerOrigin()) { throw 'Repeated ledger creation accepted' } }
        elseif($Case -ceq 'closed-handle') { $native.Dispose() }
        elseif($Case -ceq 'position-change') { $native.Position=1 }
        elseif($Case -ceq 'over-bound') {
            $writer=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
            try { $writer.SetLength(268435457L) } finally { $writer.Dispose() }
        } elseif($Case -ceq 'cancel') {
            $cancel=[Threading.EventWaitHandle]::OpenExisting($Name+'.closed')
            try { [void]$cancel.Set(); if($origin.IsCurrent) { throw 'Cancelled origin current' }; [void]$cancel.Reset() } finally { $cancel.Dispose() }
        } elseif($Case -ceq 'complete') {
            $finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
            if($null -eq $finalizer) { throw 'Creation consumed legacy finalizer slot' }
            if($finalizer.Complete('','',0,0,$null,$null,$null,$null,$null,$null)) { throw 'Invalid legacy completion accepted' }
        } elseif($Case -ceq 'dispose') { $origin.Dispose() }
        else { $owner.Dispose() }
        if($origin.IsCurrent -or $owner.IsCurrent -or $native.CanRead) { throw 'Retired ledger origin retained native resources' }
        [IO.File]::AppendAllText($path,'released'); [IO.File]::Move($path,$path+'.released')
    }
    'frontier-gui-worker=original-ledger-origin-verified'
} finally {
    if($null -ne $finalizer) { $finalizer.Dispose() }
    if($null -ne $owner) { $owner.Dispose() }
    foreach($event in $events.Values) { $event.Dispose() }
}
