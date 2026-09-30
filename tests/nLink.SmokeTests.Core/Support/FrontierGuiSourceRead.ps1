# Original parent/GUI/two-app fixture: no paths or descriptors enter TryOpenSource.
$sourceCase=$Case.Substring('source-'.Length)
if($sourceCase -ceq 'rotated') {
    [IO.File]::Move($source,$source+'.original'); [IO.File]::WriteAllText($source,'replacement'); $source+='.original'
}
$proof=$owner.TrySealFiles()
if($null -eq $proof) { throw 'Source fixture seal missing' }
$finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
if($null -eq $finalizer) { throw 'Source fixture finalizer missing' }
$binding=$finalizer.TryTakeFrontierBinding()
if($null -eq $binding) { throw 'Source fixture binding missing' }
if([NLink.Tools.LocalGuiWriterRequest+FrontierSourceRead].GetConstructors().Length -or $binding.GetType().GetMethod('TryOpenSource').GetParameters().Length) { throw 'Source reader accepts caller construction/evidence' }
$reader=$null; $memory=[IO.MemoryStream]::new()
function Read-SourceRejected($Value) {
    $rejected=$false
    try { [void]$Value.Read([byte[]]::new(1),0,1) } catch [IO.IOException] { $rejected=$true }
    if(-not $rejected) { throw 'Retired/copied reader returned source bytes' }
}
try {
    if($sourceCase -ceq 'copied-binding') {
        $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($binding,$null)
        if($null -ne $copy.TryOpenSource()) { throw 'Copied binding opened source' }
        $copy.Dispose(); if(-not $binding.IsCurrent) { throw 'Copied binding revoked source' }
    }
    if($sourceCase -ceq 'before-open') {
        $binding.Dispose()
        if($null -ne $binding.TryOpenSource()) { throw 'Disposed binding opened source' }
    } else {
        $nativeStream=$adopter.GetType().GetField('sealedSource',$flags).GetValue($adopter)
        $nativeHandle=$nativeStream.SafeFileHandle
        $reader=$binding.TryOpenSource()
        if($null -eq $reader -or -not $reader.IsCurrent -or $reader.ReachedEnd -or $reader.CanSeek -or $reader.CanWrite) { throw 'Original source reader invalid' }
        if(-not [object]::ReferenceEquals($nativeHandle,$nativeStream.SafeFileHandle)) { throw 'Source reader replaced sealed handle' }
        if($reader.LiveProofEligible -or $reader.NativeWriterCustodyEstablished -or $reader.PublicationCompletionIssued) { throw 'Source reader enabled publication' }
        $buffer=[byte[]]::new(65536)
        if($reader.Read($buffer,0,0) -ne 0 -or $reader.ReachedEnd) { throw 'Empty read established source EOF' }
        if($sourceCase -ceq 'copied-reader') {
            $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($reader,$null)
            Read-SourceRejected $copy
            $copy.Dispose(); if(-not $reader.IsCurrent) { throw 'Copied reader revoked original' }
        }
        if($sourceCase -ceq 'stream-api') {
            $rejected=0
            try { [void]$reader.Seek(0,[IO.SeekOrigin]::Begin) } catch [NotSupportedException] { $rejected++ }
            try { $reader.Write($buffer,0,1) } catch [NotSupportedException] { $rejected++ }
            try { $reader.SetLength(1) } catch [NotSupportedException] { $rejected++ }
            if($rejected -ne 3 -or -not $reader.IsCurrent) { throw 'Source stream mutation API accepted' }
        }
        $first=$reader.Read($buffer,0,1)
        if($first -ne 1 -or $reader.ReachedEnd) { throw 'Partial read established EOF' }
        $memory.Write($buffer,0,$first)
        switch($sourceCase) {
            'repeat' { if($null -ne $binding.TryOpenSource()) { throw 'Repeated source open succeeded' } }
            'partial' { $reader.Dispose() }
            'binding-disposed' { $binding.Dispose() }
            'owner-disposed' { $owner.Dispose() }
            'finalizer-disposed' { $finalizer.Dispose() }
            'complete' { if($finalizer.Complete('', '',0,0,$null,$null,$null,$null,$null,$null)) { throw 'Invalid completion accepted' } }
            'budget' {
                $work=$adopter.GetType().GetField('work',$flags).GetValue($adopter)
                $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
                $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*11))
            }
            'cancel' {
                $event=[Threading.EventWaitHandle]::OpenExisting($Name+'.closed')
                try { [void]$event.Set(); Read-SourceRejected $reader; [void]$event.Reset() } finally { $event.Dispose() }
            }
        }
        if($sourceCase -cin @('valid','rotated','copied-binding','copied-reader','stream-api')) {
            while($true) {
                $read=$reader.Read($buffer,0,$buffer.Length)
                if($read -lt 0 -or $read -gt 32768) { throw 'Unbounded native source read' }
                if($read -eq 0) { break }
                $memory.Write($buffer,0,$read)
            }
            $hash=[Security.Cryptography.SHA256]::Create()
            try { $actualHash=[BitConverter]::ToString($hash.ComputeHash($memory.ToArray())).Replace('-','').ToLowerInvariant() } finally { $hash.Dispose() }
            if(-not $reader.ReachedEnd -or $memory.Length -ne $bytes.Length -or $actualHash -cne $expectedHash) { throw 'Owned source bytes or EOF mismatch' }
            if($sourceCase -ceq 'rotated' -and [IO.File]::ReadAllText((Join-Path $Directory 'shared.log')) -cne 'replacement') { throw 'Reader followed or changed replacement path' }
            $reader.Dispose()
        } else { Read-SourceRejected $reader }
        if($reader.IsCurrent -or $reader.ReachedEnd -or $reader.CanRead -or -not $nativeHandle.IsClosed) { throw 'Revoked source retained capability or handle' }
    }
    if($binding.IsCurrent -or $proof.IsCurrent -or $owner.IsCurrent -or $null -ne $binding.TryOpenSource()) { throw 'Source scope revived' }
    if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) { throw 'Source cleanup retained payload handles' }
    [IO.File]::AppendAllText($source,'source-released')
} finally {
    if($null -ne $reader) { $reader.Dispose() }
    $binding.Dispose(); $memory.Dispose()
}
