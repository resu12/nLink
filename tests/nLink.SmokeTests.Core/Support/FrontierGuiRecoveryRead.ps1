$mode=$Case.Substring('recovery-'.Length)
$paths=@(); $expected=@(); $nativeFiles=@()
for($i=0;$i -lt 2;$i++) {
    $output=$apps[$i].Binding.GetType().GetField('Output',$flags).GetValue($apps[$i].Binding)
    $retained=$output.GetType().GetField('RecoveryOutputs',$flags).GetValue($output)
    foreach($stream in @('stdout','stderr')) {
        $index=if($stream -ceq 'stderr') {1} else {0}
        $nativeFiles+=,$retained[$index].GetType().GetField('file',$flags).GetValue($retained[$index])
        $path=Join-Path $Directory ('frontier-process-output-'+$RequestId.ToString('N')+'-'+$i+'-'+$stream+'.log')
        $paths+=,$path
        $read=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
        $copy=[IO.MemoryStream]::new()
        try { $read.CopyTo($copy); $expected+=,$copy.ToArray() } finally { $copy.Dispose(); $read.Dispose() }
    }
}
$proof=$owner.TrySealFiles()
if($null -eq $proof) { throw 'Recovery input source seal missing' }
$finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
if($null -eq $finalizer) { throw 'Recovery input finalizer missing' }
$binding=$finalizer.TryTakeFrontierBinding()
if($null -eq $binding) { throw 'Recovery input binding missing' }
$sourceReader=$null; $readers=$null
try {
    $type=[NLink.Tools.LocalGuiWriterRequest+FrontierRecoveryRead]
    if($type.GetConstructors().Length -or @($type.GetProperties([Reflection.BindingFlags]'Public,Instance,DeclaredOnly') | Where-Object {$null -ne $_.GetSetMethod() -and $_.Name -ne 'Position'}).Count) { throw 'Recovery reader exposes fabrication' }
    if($mode -ceq 'copied-binding') {
        $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($binding,$null)
        if($null -ne $copy.TryOpenRecoveryOutputs()) { throw 'Copied binding acquired original recovery files' }
        $copy.Dispose(); if(-not $binding.IsCurrent) { throw 'Copied binding retired recovery inputs' }
    }
    $readers=$binding.TryOpenRecoveryOutputs()
    if($null -eq $readers -or $readers.Count -ne 4) { throw 'Original four-stream reader set missing' }
    $buffer=[byte[]]::new(65536)
    for($i=0;$i -lt 4;$i++) {
        $reader=$readers[$i]
        if(-not $reader.IsCurrent -or -not $reader.CanRead -or $reader.CanWrite -or $reader.CanSeek -or $reader.Position -ne 0 -or
            $reader.Length -ne $expected[$i].Length -or $reader.IsStandardError -ne (($i % 2) -eq 1) -or
            $reader.AppProcessId -ne $apps[[int][Math]::Floor($i/2)].Binding.AppProcessId -or
            $reader.AppProcessCreationFileTime -ne $apps[[int][Math]::Floor($i/2)].Binding.AppProcessCreationFileTime -or
            $reader.LiveProofEligible -or $reader.NativeWriterCustodyEstablished -or $reader.PublicationCompletionIssued) { throw 'Wrong original recovery reader scope/access' }
        if($reader.Read($buffer,0,0) -ne 0 -or $reader.ReachedEnd -or $reader.Position -ne 0) { throw 'Zero-length read asserted recovery EOF' }
        foreach($operation in @({$reader.Position=0L},{$reader.Seek(0L,[IO.SeekOrigin]::Begin)},{$reader.SetLength(0L)},{$reader.Write($buffer,0,1)})) {
            $rejected=$false; try { & $operation } catch { $rejected=$true }
            if(-not $rejected) { throw 'Recovery reader permitted seek/write access' }
        }
        if($nativeFiles[$i].CanWrite) { throw 'Retained recovery handle has write access' }
        $blocked=$false
        try { $probe=[IO.File]::Open($paths[$i],[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete); $probe.Dispose() } catch [IO.IOException] { $blocked=$true }
        if(-not $blocked) { throw 'Retained recovery source remained writable' }
        $blocked=$false
        try { [IO.File]::Move($paths[$i],$paths[$i]+'.substituted') } catch [IO.IOException] { $blocked=$true }
        if(-not $blocked) { throw 'Retained recovery source remained replaceable' }
    }
    if($mode -ceq 'copied-reader') {
        $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($readers[0],$null)
        if($copy.CanRead -or $copy.IsCurrent) { throw 'Copied recovery reader current' }
        $rejected=$false; try { [void]$copy.Read($buffer,0,1) } catch { $rejected=$true }
        if(-not $rejected) { throw 'Copied reader read original output' }
        $copy.Dispose(); if(-not $readers[0].IsCurrent) { throw 'Copied recovery reader retired original' }
    }
    $work=$adopter.GetType().GetField('work',$flags).GetValue($adopter)
    if($mode -ceq 'processing-active') {
        $sourceReader=$binding.TryOpenSource()
        if($null -eq $sourceReader -or -not $sourceReader.TryBeginProcessingWork()) { throw 'Recovery processing interval missing' }
    }
    if($mode -ceq 'budget') {
        $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*11))
    }
    if($mode -ceq 'closed-handle') { $nativeFiles[1].Dispose() }
    if($mode -ceq 'position-change') { $nativeFiles[0].Position=1 }
    if($mode -cin @('budget','closed-handle','position-change')) {
        $rejected=$false; try { [void]$readers[0].Read($buffer,0,1) } catch { $rejected=$true }
        if(-not $rejected -or $binding.IsCurrent) { throw 'Stale recovery input remained readable' }
    } else {
        for($i=0;$i -lt 4;$i++) {
            $reader=$readers[$i]; $actual=[IO.MemoryStream]::new()
            try {
                do {
                    $read=$reader.Read($buffer,0,$buffer.Length)
                    if($read -gt 32768) { throw 'Recovery read exceeded its chunk bound' }
                    $actual.Write($buffer,0,$read)
                    if($work.IsRunning -ne ($mode -ceq 'processing-active')) { throw 'Recovery I/O changed the shared processing interval' }
                    if($reader.ReachedEnd -ne ($actual.Length -eq $reader.Length)) { throw 'Recovery EOF did not match exact bytes' }
                } while($read -ne 0)
                $bytes=$actual.ToArray()
                if($bytes.Length -ne $expected[$i].Length) { throw 'Recovery input length changed' }
                if([Convert]::ToBase64String($bytes) -cne [Convert]::ToBase64String($expected[$i])) { throw 'Recovery input bytes changed' }
            } finally { $actual.Dispose() }
        }
        # The positive service emits a disposal diagnostic on its stdout. Keep
        # that real byte sequence; both stderr streams and writer-only stdout
        # must still exercise genuine zero-byte input.
        if($mode -ceq 'empty' -and ($expected[1].Length -ne 0 -or $expected[2].Length -ne 0 -or $expected[3].Length -ne 0)) { throw 'Empty recovery controls not empty' }
        if($mode -ceq 'processing-active') {
            while($sourceReader.Read($buffer,0,$buffer.Length) -ne 0) { }
            if(-not $sourceReader.TryFinishProcessingWork() -or $work.IsRunning) { throw 'Recovery I/O reset or broke processing finish' }
        }
        if($mode -ceq 'repeat') { if($null -ne $binding.TryOpenRecoveryOutputs()) { throw 'Repeated recovery open accepted' } }
        elseif($mode -ceq 'cancel') {
            $cancel=[Threading.EventWaitHandle]::OpenExisting($Name+'.closed')
            try { [void]$cancel.Set(); if($readers[0].IsCurrent) { throw 'Cancelled recovery reader current' }; [void]$cancel.Reset() } finally { $cancel.Dispose() }
        } elseif($mode -ceq 'complete') { if($finalizer.Complete('','',0,0,$null,$null,$null,$null,$null,$null)) { throw 'Invalid legacy completion accepted' } }
        elseif($mode -ceq 'dispose') { $readers[1].Dispose() }
        elseif($mode -ceq 'owner-dispose') { $owner.Dispose() }
        elseif($mode -ceq 'source-dispose') { $sourceReader=$binding.TryOpenSource(); $sourceReader.Dispose() }
        else { $binding.Dispose() }
    }
    if(@($readers | Where-Object {$_.IsCurrent}).Count -or @($nativeFiles | Where-Object {$_.CanRead}).Count -or $binding.IsCurrent -or $proof.IsCurrent) { throw 'Recovery retirement retained original handles' }
    foreach($path in $paths) { [IO.File]::AppendAllText($path,'released'); [IO.File]::Move($path,$path+'.released') }
    if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) { throw 'Recovery cleanup retained payloads' }
    [IO.File]::AppendAllText($source,'recovery-released')
} finally {
    if($null -ne $sourceReader) { $sourceReader.Dispose() }
    $binding.Dispose()
}
