$primaryCase=$commitCase.Substring('primary-'.Length)
$primary=$null; $inputs=$null; $hashRead=$null; $primaryMap=$null; $primaryView=$null; $child=$null; $childStop=$null
$readers=[Collections.Generic.List[object]]::new(); $primaryPins=@()
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class PrimaryPositionProbe {
 [DllImport("kernel32.dll")] private static extern bool SetFilePointerEx(SafeFileHandle h,long n,out long p,uint method);
 public static long Position(SafeFileHandle h) {long p; if(!SetFilePointerEx(h,0,out p,1)) throw new InvalidOperationException(); return p;}
 public static void Set(SafeFileHandle h,long p) {long actual; if(!SetFilePointerEx(h,p,out actual,0)||actual!=p) throw new InvalidOperationException();}
}
'@
try {
    if($primaryCase.StartsWith('parent-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRetainedParent.ps1')
        return
    }
    if($primaryCase.StartsWith('prepare-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryPreparation.ps1')
        return
    }
    if($primaryCase -ceq 'copied-hash') {
        $copy=$parentClone.Invoke($custody,$null)
        if($null -ne $copy.TryHashPrimarySource() -or -not $custody.IsCurrent) {throw 'Copied custody obtained/retired native source hash'}
    }
    [PrimaryPositionProbe]::Set($custodyPins[4],7L)
    $hashPosition=[PrimaryPositionProbe]::Position($custodyPins[4])
    if($primaryCase -ceq 'hash-budget') {
        $elapsed=@($clock.GetType().GetFields($parentFlags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($clock,[long]([Diagnostics.Stopwatch]::Frequency*6))
    }
    if($primaryCase -ceq 'hash-pin') {$custodyPins[4].Dispose()}
    $hashRead=$custody.TryHashPrimarySource()
    if($primaryCase -cin @('hash-budget','hash-pin')) {
        if($null -ne $hashRead -or $custody.IsCurrent) {throw 'Invalid native hash accepted'}
        'frontier-primary-request='+$primaryCase; return
    }
    if($null -eq $hashRead -or $null -eq $hashRead.Lease -or -not $hashRead.Lease.IsVerified -or
        $hashRead.Sha256 -cne $completion.Outputs[3].Sha256 -or [PrimaryPositionProbe]::Position($custodyPins[4]) -ne $hashPosition) {throw 'Native primary hash lost identity/position/verification'}
    if($primaryCase -ceq 'repeat-hash') {
        if($null -ne $custody.TryHashPrimarySource() -or $custody.IsCurrent) {throw 'Native source hash replay accepted'}
        'frontier-primary-request='+$primaryCase; return
    }
    $primarySourceLines=$completion.Outputs[3].LineCount
    if($primaryCase.StartsWith('count-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimarySourceCount.ps1')
        if($countCase -cin @('disposed-lease','closed-pin')) {return}
    }
    $writer=$completion
    if($primaryCase -ceq 'copied-completion') {$writer=$parentClone.Invoke($completion,$null)}
    $primary=if($primaryCase -ceq 'no-writer') {
        [NLink.Tools.LocalRetainedSourceRequest]::TryCreate([guid]::NewGuid(),'Wrapper','frontier-gui-run','frontier-gui-scenario',$hashRead.Lease,$primarySourceLines)
    } elseif($primaryCase -ceq 'worker-existing-publication') {
        [NLink.Tools.LocalRetainedSourceRequest]::TryCreateWithPublication([guid]::NewGuid(),'Wrapper','frontier-gui-run','frontier-gui-scenario',$hashRead.Lease,$primarySourceLines,$writer)
    } else {
        [NLink.Tools.LocalRetainedSourceRequest]::TryCreateWithWriter([guid]::NewGuid(),'Wrapper','frontier-gui-run','frontier-gui-scenario',$hashRead.Lease,$primarySourceLines,$writer)
    }
    if($null -eq $primary) {throw 'Original primary request unavailable'}
    $primaryMap=[IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting($primary.Name); $primaryView=$primaryMap.CreateViewAccessor()
    if($primaryCase -cin @('bound-request','worker-exit')) {
        $stopName='Local\frontier-primary-test-'+[guid]::NewGuid().ToString('N')
        $childStop=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$stopName)
        $start=[Diagnostics.ProcessStartInfo]::new(); $start.FileName=(Get-Process -Id $PID).Path
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $command="`$e=[Threading.EventWaitHandle]::OpenExisting('$stopName'); try {[void]`$e.WaitOne(20000)} finally {`$e.Dispose()}"
        $start.Arguments='-NoProfile -EncodedCommand '+[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
        $child=[Diagnostics.Process]::Start($start); [void]$child.Handle
        if($primaryCase -ceq 'bound-request' -and -not $primary.BindWorker($child)) {throw 'Prebound primary fixture failed'}
    }
    switch($primaryCase) {
        'copied-custody' {
            $copy=$parentClone.Invoke($custody,$null)
            if($null -ne $copy.TryTransferToPrimary($primary)) {throw 'Copied custody transferred'}
            $copy.Dispose(); if(-not $custody.IsCurrent) {throw 'Copied custody retired original'}
        }
        'copied-request' {
            $copy=$parentClone.Invoke($primary,$null)
            if($null -ne $custody.TryTransferToPrimary($copy)) {throw 'Copied primary request transferred'}
            $copy.Dispose(); if(-not $custody.IsCurrent) {throw 'Copied request retired custody'}
        }
        'disposed-request' {$primary.Dispose()}
        'disposed-parent' {$request.Dispose()}
        'parent-budget' {
            $elapsed=@($clock.GetType().GetFields($parentFlags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($clock,[long]([Diagnostics.Stopwatch]::Frequency*6))
        }
        'header' {$primaryView.Write(24L,[byte]($primaryView.ReadByte(24L) -bxor 1))}
        'body' {$primaryView.Write(64L,[byte]($primaryView.ReadByte(64L) -bxor 1))}
        'pin' {$custodyPins[0].Dispose()}
        'source-pin' {$primary.GetType().GetField('frontierSourceHandle',$parentFlags).SetValue($primary,$custodyPins[0])}
    }
    $inputs=$custody.TryTransferToPrimary($primary)
    $rejected=$primaryCase -cin @('copied-completion','no-writer','disposed-request','disposed-parent','bound-request','parent-budget','header','body','pin','source-pin')
    if($rejected) {
        if($null -ne $inputs -or $custody.IsCurrent -or @($custodyPins | Where-Object {-not $_.IsClosed}).Count) {throw 'Invalid request accepted/leaked original custody'}
        'frontier-primary-request='+$primaryCase; return
    }
    if($null -eq $inputs -or -not $inputs.IsCurrent -or $custody.IsCurrent -or
        @($custodyPins | Where-Object {-not $_.IsClosed}).Count -or $inputs.NativeWriterCustodyEstablished -or $inputs.LiveProofEligible -or $inputs.PublicationCompletionIssued) {throw 'Primary transfer incomplete or promoted authority'}
    $request.Dispose(); $hashRead.Lease.Dispose()
    if(-not $inputs.IsCurrent) {throw 'Transferred lifetime depended on retired GUI parent/hash lease'}
    $nativePins=$inputs.GetType().GetField('pins',$parentFlags).GetValue($inputs)
    $primaryPins=@($nativePins.GetType().GetField('Handles',$parentFlags).GetValue($nativePins))
    if($inputs.FileCount -ne $primaryPins.Count -or $inputs.SourceLineCount -le 0 -or
        $inputs.GetType().GetConstructors().Length -or @($inputs.GetType().GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) {throw 'Primary native set or construction boundary invalid'}
    $open=$inputs.GetType().GetMethod('TryOpenRead',$parentFlags)
    if($primaryCase -ceq 'copied-inputs') {
        $copy=$parentClone.Invoke($inputs,$null)
        if($copy.IsCurrent -or $null -ne $open.Invoke($copy,@(0))) {throw 'Copied inputs exposed native reader'}
        $copy.Dispose(); if(-not $inputs.IsCurrent) {throw 'Copied inputs retired original'}
    }
    foreach($pin in $primaryPins) {if(-not [ParentPinProbe]::WriteDenied($pin)) {throw 'Primary pin has write access'}}
    # Preserve A8c1's original reader/worker-exit regression path.
    if($primaryCase -cne 'worker-exit' -and $primaryCase.StartsWith('worker-',[StringComparison]::Ordinal)) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierPrimaryWorkerParent.ps1')
        'frontier-primary-request='+$primaryCase; return
    }
    if($primaryCase -ceq 'repeat-transfer' -and ($null -ne $custody.TryTransferToPrimary($primary) -or -not $inputs.IsCurrent)) {throw 'Transfer replay affected successor'}
    if($primaryCase -ceq 'reader-index') {
        if($null -ne $open.Invoke($inputs,@($primaryPins.Count)) -or $inputs.IsCurrent) {throw 'Out-of-bound reader survived'}
    } else {
        # Shift native positions deliberately; reader offsets must be independent.
        for($i=0;$i -lt $primaryPins.Count;$i++) {[PrimaryPositionProbe]::Set($primaryPins[$i],19L)}
        $reader=$open.Invoke($inputs,@(4)); if($null -eq $reader) {throw 'Private original native reader unavailable'}
        $readers.Add($reader); $read=$reader.GetType().GetMethod('Read',$parentFlags)
        $buffer=[byte[]]::new(32769)
        if($primaryCase -ceq 'copied-reader') {
            $copy=$parentClone.Invoke($reader,$null)
            if($read.Invoke($copy,@($buffer,0,32)) -ne -1) {throw 'Copied reader exposed bytes'}
            $copy.Dispose(); if(-not $inputs.IsCurrent) {throw 'Copied reader retired original'}
        }
        switch($primaryCase) {
            'post-pin' {$primaryPins[0].Dispose()}
            'post-body' {$primaryView.Write(64L,[byte]($primaryView.ReadByte(64L) -bxor 1))}
            'cancel-signal' {
                $signal=[Threading.EventWaitHandle]::OpenExisting($primary.Name+'.closed')
                try {[void]$signal.Set()} finally {$signal.Dispose()}
            }
            'reader-replay' {if($null -ne $open.Invoke($inputs,@(4))) {throw 'Reader opened twice'}}
            'reader-disposed' {$reader.Dispose()}
            'reader-budget' {
                $readerClock=$inputs.GetType().GetField('work',$parentFlags).GetValue($inputs)
                $elapsed=@($readerClock.GetType().GetFields($parentFlags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
                $elapsed.SetValue($readerClock,[long]([Diagnostics.Stopwatch]::Frequency*21))
            }
            'request-dispose' {$primary.Dispose()}
            'worker-exit' {
                if(-not $primary.BindWorker($child) -or -not $inputs.IsCurrent) {throw 'Transferred request failed native worker binding'}
                [void]$childStop.Set(); if(-not $child.WaitForExit(10000)) {throw 'Primary worker exit deadline'}
            }
        }
        $stale=$primaryCase -cin @('post-pin','post-body','cancel-signal','reader-replay','reader-size','reader-disposed','reader-budget','request-dispose','worker-exit')
        if($stale) {
            $count=if($primaryCase -ceq 'reader-size') {32769} else {32}
            if($read.Invoke($reader,@($buffer,0,$count)) -ne -1 -or $inputs.IsCurrent) {throw 'Rejected reader retained bytes/currentness'}
        } else {
            $hash=[Security.Cryptography.SHA256]::Create(); $total=0L
            try {
                while(($count=$read.Invoke($reader,@($buffer,0,32768))) -gt 0) {
                    [void]$hash.TransformBlock($buffer,0,$count,$buffer,0); $total+=$count
                }
                [void]$hash.TransformFinalBlock([byte[]]::new(0),0,0)
                $digest=[BitConverter]::ToString($hash.Hash).Replace('-','').ToLowerInvariant()
                if($count -ne 0 -or $total -ne $completion.Outputs[3].ByteCount -or $digest -cne $completion.Outputs[3].Sha256) {throw 'Native reader bytes differ from original source'}
            } finally {$hash.Dispose()}
            for($i=0;$i -lt $primaryPins.Count;$i++) {
                if([PrimaryPositionProbe]::Position($primaryPins[$i]) -ne 19L) {throw 'Mapped reader changed shared native position'}
                if($i -eq 4) {continue}
                $other=$open.Invoke($inputs,@($i)); if($null -eq $other) {throw 'Incomplete native reader set'}
                $readers.Add($other); $otherRead=$other.GetType().GetMethod('Read',$parentFlags)
                do {$n=$otherRead.Invoke($other,@($buffer,0,32768)); if($n -lt 0) {throw 'Original native input read failed'}} while($n -gt 0)
                if([PrimaryPositionProbe]::Position($primaryPins[$i]) -ne 19L) {throw 'Input traversal changed shared position'}
            }
            if(-not $inputs.IsCurrent) {throw 'Completed bounded reads retired current custody'}
        }
    }
    $primary.Dispose()
    if($inputs.IsCurrent -or @($primaryPins | Where-Object {-not $_.IsClosed}).Count) {throw 'Primary request leaked custody pins'}
    foreach($reader in $readers) {
        $stream=$reader.GetType().GetField('stream',$parentFlags).GetValue($reader)
        if($stream.CanRead) {throw 'Request cancellation leaked native reader'}
    }
    'frontier-primary-request='+$primaryCase
} finally {
    foreach($reader in $readers) {$reader.Dispose()}
    if($null -ne $inputs) {$inputs.Dispose()}; if($null -ne $primary) {$primary.Dispose()}
    if($null -ne $hashRead -and $null -ne $hashRead.Lease) {$hashRead.Lease.Dispose()}
    if($null -ne $primaryView) {$primaryView.Dispose()}; if($null -ne $primaryMap) {$primaryMap.Dispose()}
    if($null -ne $childStop) {[void]$childStop.Set()}
    if($null -ne $child) {if(-not $child.WaitForExit(5000)) {$child.Kill(); [void]$child.WaitForExit(5000)}; $child.Dispose()}
    if($null -ne $childStop) {$childStop.Dispose()}
}
