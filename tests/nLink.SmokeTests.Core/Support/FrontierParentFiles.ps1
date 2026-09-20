$filesCase=$parentCase.Substring('files-'.Length)
$pending=$null; $fileMap=$null; $fileView=$null; $pulledHandles=@()
try {
    foreach($type in @([NLink.Tools.LocalGuiWriterRequest+FrontierParentFileSubmission],[NLink.Tools.LocalGuiWriterRequest+FrontierParentFilesPending])) {
        if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) {throw 'Parent file capability fabrication exposed'}
    }
    if($parentRegistration.GetType().GetMethod('TryPullFiles').GetParameters().Length) {throw 'Parent scalar import exposed'}
    if(-not $events['files-offered'].WaitOne(20000)) {throw "File offer deadline: $($process.HasExited)"}
    $early=$filesCase -cin @('no-preparation','closed-pin','budget','repeat-publish')
    if(-not $early) {
        $fileMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($name+'.frontier-parent-v1.files-v1')
        $fileView=$fileMap.CreateViewAccessor(0L,2376L)
        if($filesCase -ceq 'copied-registration') {
            $copy=$parentClone.Invoke($parentRegistration,$null)
            if($null -ne $copy.TryPullFiles()) {throw 'Copied registration pulled files'}
            $copy.Dispose(); if(-not $parentRegistration.IsCurrent) {throw 'Copied registration retired original'}
        }
        if($filesCase -ceq 'foreign-open') {
            $type=[NLink.Tools.LocalGuiWriterRequest].Assembly.GetType('NLink.Tools.LocalFrontierParentFileChannel')
            $foreign=$type.GetMethod('Open',[Reflection.BindingFlags]'Static,NonPublic').Invoke($null,[object[]]@(($name+'.frontier-parent-v1'),[byte[]]::new(32)))
            if($null -ne $foreign -or -not $parentRegistration.IsCurrent) {throw 'Foreign file open affected original'}
        }
        switch($filesCase) {
            'header' {$fileView.Write(4L,2)}
            'binding' {$fileView.Write(16L,[byte]($fileView.ReadByte(16L) -bxor 1))}
            'nonce' {for($i=48;$i -lt 64;$i++) {$fileView.Write([long]$i,[byte]0)}}
            'digest' {$fileView.Write(96L,[byte]($fileView.ReadByte(96L) -bxor 1))}
            'reserved' {$fileView.Write(68L,1)}
            'length' {$fileView.Write(64L,[int]::MaxValue)}
            'body' {$fileView.Write(128L,0)}
            'tail' {$fileView.Write(2375L,[byte]1)}
            'state' {$fileView.Write(8L,2)}
            # Last mandatory role fails after nine successful native pulls.
            'descriptor-handle' {$fileView.Write(684L,9223372036854775806L)}
            'descriptor-identity' {$fileView.Write(692L,$fileView.ReadUInt32(692L)+[uint32]1)}
            'descriptor-length' {$fileView.Write(704L,$fileView.ReadInt64(704L)+1L)}
            'descriptor-role' {$fileView.Write(660L,8)}
            'descriptor-count' {$fileView.Write(136L,43)}
            'descriptor-slot' {$fileView.Write(716L,2)}
        }
        if($filesCase.StartsWith('descriptor-',[StringComparison]::Ordinal)) {
            $body=[byte[]]::new($fileView.ReadInt32(64L)); [void]$fileView.ReadArray(128L,$body,0,$body.Length)
            $hash=[Security.Cryptography.SHA256]::Create()
            try {$digest=$hash.ComputeHash($body)} finally {$hash.Dispose()}
            for($i=0;$i -lt 32;$i++) {$fileView.Write([long](96+$i),$digest[$i])}
        }
    }
    $pending=$parentRegistration.TryPullFiles()
    $rejected=$early -or $filesCase -cin @('header','binding','nonce','digest','reserved','length','body','tail','state') -or $filesCase.StartsWith('descriptor-',[StringComparison]::Ordinal)
    if($rejected) {
        if($null -ne $pending -or $parentRegistration.IsCurrent) {throw 'Invalid native file pull accepted'}
        $partial=$parentRegistration.GetType().GetField('PendingFiles',$parentFlags).GetValue($parentRegistration)
        if($null -ne $partial) {
            $pulledHandles=@($partial.GetType().GetField('handles',$parentFlags).GetValue($partial))
            if(@($pulledHandles | Where-Object {-not $_.IsClosed}).Count) {throw 'Partial native pull leaked'}
            if($filesCase -cin @('descriptor-handle','descriptor-identity','descriptor-length') -and $pulledHandles.Count -ne 9) {throw 'Native rollback fixture did not reach ninth pin'}
        } elseif($filesCase -cin @('descriptor-handle','descriptor-identity','descriptor-length')) {throw 'Native rollback rejected before native pull'}
    } else {
        $expected=if($filesCase -cin @('both-positive','two-payloads','facts-two-payloads',
            'facts-ack-commit-primary-worker-payloads-two-payloads','facts-ack-commit-primary-worker-payloads-both-writers',
            'facts-ack-commit-primary-worker-payloads-partial-hash')) {12} else {11}
        if($null -eq $pending -or -not $pending.IsCurrent -or $pending.FileCount -ne $expected -or $pending.PayloadCount -ne $expected-10 -or
            $pending.LiveProofEligible -or $pending.NativeWriterCustodyEstablished -or $pending.PublicationCompletionIssued) {throw 'Complete pending native set missing/authority'}
        $pulledHandles=@($pending.GetType().GetField('handles',$parentFlags).GetValue($pending))
        Add-Type @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class ParentPinProbe {
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool WriteFile(SafeFileHandle h, byte[] b, uint n, out uint written, IntPtr overlapped);
    public static bool WriteDenied(SafeFileHandle h) {uint written; return !WriteFile(h,new byte[]{1},1,out written,IntPtr.Zero) && Marshal.GetLastWin32Error()==5;}
}
'@
        foreach($pin in $pulledHandles) {if(-not [ParentPinProbe]::WriteDenied($pin)) {throw 'Parent pin retained write access'}}
        if($filesCase -ceq 'valid') {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierParentFilePacket.ps1')
        }
        if($filesCase -ceq 'copied-pending') {
            $copy=$parentClone.Invoke($pending,$null)
            if($copy.IsCurrent -or $copy.FileCount) {throw 'Copied pending set current'}
            $copy.Dispose(); if(-not $pending.IsCurrent) {throw 'Copied pending set retired original'}
        }
        switch($filesCase) {
            'repeat-pull' {if($null -ne $parentRegistration.TryPullFiles()) {throw 'Native pull repeated'}}
            'state-regression' {$fileView.Write(8L,1)}
            'pending-pin' {$pulledHandles[0].Dispose()}
            'parent-dispose' {$parentRegistration.Dispose()}
            'request-dispose' {$request.Dispose()}
            'post-body' {$fileView.Write(128L,[byte]($fileView.ReadByte(128L) -bxor 1))}
            'post-scope' {$view.Write(64L,[byte]($view.ReadByte(64L) -bxor 1))}
        }
        if($filesCase -cin @('repeat-pull','state-regression','pending-pin','parent-dispose','request-dispose','post-body','post-scope') -and $pending.IsCurrent) {throw 'Stale pending set retained'}
    }
    if($filesCase.StartsWith('facts-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierParentFacts.ps1')
        if($filesCase.StartsWith('facts-ack-commit-',[StringComparison]::Ordinal)) {return}
        if($filesCase -cin @('facts-legacy-complete','facts-gui-exit')) {$filesCase=$filesCase.Substring('facts-'.Length)}
        if($filesCase -cin @('facts-ack-legacy-complete','facts-ack-legacy-invalid','facts-ack-gui-exit','facts-ack-gui-failure')) {$filesCase=$filesCase.Substring('facts-ack-'.Length)}
    }
    [void]$events['files-reviewed'].Set()
    if($filesCase -cin @('gui-exit','gui-failure')) {if(-not $process.WaitForExit(20000)) {throw 'Native GUI exit deadline'}}
    elseif(-not $events['files-retired'].WaitOne(20000)) {throw 'GUI file retirement deadline'}
    # Test proactive resource cleanup before querying capability currentness.
    $cleanup=[Diagnostics.Stopwatch]::StartNew()
    while(@($pulledHandles | Where-Object {-not $_.IsClosed}).Count -and $cleanup.ElapsedMilliseconds -lt 5000) {Start-Sleep -Milliseconds 10}
    if(@($pulledHandles | Where-Object {-not $_.IsClosed}).Count) {throw 'Pending pins did not close on native lifetime signal'}
    if($null -ne $pending -and ($pending.IsCurrent -or $pending.FileCount)) {throw 'Retired GUI kept pending custody'}
    if($case.StartsWith('cursor-parent-files-facts-',[StringComparison]::Ordinal) -and $null -ne $facts -and
        ($facts.IsCurrent -or $facts.SourceLineCount -or $facts.OriginCount -or $facts.RecoveryMarkerCount)) {throw 'Retired GUI kept fact receipt'}
    if($case.StartsWith('cursor-parent-files-facts-ack-',[StringComparison]::Ordinal) -and $null -ne $ack -and
        ($ack.IsCurrent -or $ack.GuiConfirmed)) {throw 'Retired GUI kept acknowledgment'}
    [void]$events['files-finish'].Set()
} finally {
    [void]$events['files-reviewed'].Set(); [void]$events['files-finish'].Set()
    if($null -ne $pending) {$pending.Dispose()}
    if($null -ne $fileView) {$fileView.Dispose()}
    if($null -ne $fileMap) {$fileMap.Dispose()}
}
