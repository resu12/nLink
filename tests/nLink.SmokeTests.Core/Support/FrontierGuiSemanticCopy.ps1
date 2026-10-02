$mode=$Case.Substring('cursor-copy-'.Length)
$clone=[object].GetMethod('MemberwiseClone',$flags)
$proof=$owner.TrySealFiles()
if($null -eq $proof) { throw 'Semantic copy seal missing' }
$finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
$binding=$finalizer.TryTakeFrontierBinding(); $result=$null; $copy=$null
$destination=[IO.Path]::Combine($Directory,'filetransfer-frontier-retained-log-slice.log')
$legacyDestination=[IO.Path]::Combine($Directory,'filetransfer-retained-log-slice.log')
if($mode -ceq 'legacy-isolation') {[IO.File]::WriteAllText($legacyDestination,'legacy-sentinel')}
function Read-CopyFixtureBytes([string]$Path) {
    $read=[IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
    $bytes=[IO.MemoryStream]::new()
    try { $read.CopyTo($bytes); return [Convert]::ToBase64String($bytes.ToArray()) }
    finally { $bytes.Dispose(); $read.Dispose() }
}
try {
    if($null -eq $binding) { throw 'Semantic copy binding missing' }
    $type=[NLink.Tools.LocalGuiWriterRequest+FrontierSemanticCopy]
    if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) { throw 'Copy fabrication exposed' }
    if($mode -ceq 'early') {
        if($null -ne $binding.TryCopySemanticEvidence() -or $binding.IsCurrent) { throw 'Early copy accepted' }
    } else {
        if($mode -ceq 'copied-binding') {
            $foreign=$clone.Invoke($binding,$null)
            if($null -ne $foreign.TryCopySemanticEvidence()) { throw 'Copied binding copied' }
            $foreign.Dispose(); if(-not $binding.IsCurrent) { throw 'Copied binding retired original' }
        }
        $events=if($mode -ceq 'empty') {[string[]]@()} else {[string[]]@('operational_log_writer_started','writer_only_contribution')}
        $result=$binding.TryProcessObservedLedger($events)
        if($null -eq $result -or $null -eq $binding.CursorMatch) { throw 'Original copy prerequisite failed' }
        $cursor=$binding.CursorMatch; $capture=$result.StreamCapture
        $work=$owner.GetType().GetField('frontierWork',$flags).GetValue($owner); $elapsedBefore=$work.ElapsedTicks
        if($work.IsRunning) { throw 'Processor work not finished before copy' }
        if($mode -ceq 'missing-cursor') { $binding.GetType().GetField('cursorMatch',$flags).SetValue($binding,$null) }
        if($mode -ceq 'active-work') { $adopter.GetType().GetField('processingRunning',$flags).SetValue($adopter,$true) }
        if($mode -ceq 'collision') { [IO.File]::WriteAllText($destination,'sentinel') }
        if($mode -ceq 'hash') { $capture.Semantic.GetType().GetProperty('Sha256').SetValue($capture.Semantic,('0'*64),$null) }
        if($mode -ceq 'lines') { $capture.Semantic.GetType().GetProperty('LineCount').SetValue($capture.Semantic,99L,$null) }
        if($mode -ceq 'closed-output') { $lease=$capture.GetType().GetField('outputs',$flags).GetValue($capture); $lease.GetType().GetField('files',$flags).GetValue($lease)[2].Dispose() }
        if($mode -ceq 'mutable-result') { $result.SemanticSha256='wrong'; $result.SemanticByteCount=999; $result.SemanticLineCount=999; $result.Writers=@() }
        if($mode -ceq 'budget') {
            $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*16))
        }
        if($mode -ceq 'profile-valid') {
            $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*14))
        }
        if($mode -ceq 'profile-budget') {
            [NLink.Tests.NativeSemanticCopyProbeForTest]::FaultAfterFirstChunkForTest=[Action]{
                $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
                $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*16))
            }
        }
        $copy=$binding.TryCopySemanticEvidence()
        if($mode.StartsWith('profile-',[StringComparison]::Ordinal)) {
            $probe=[ordered]@{Completed=[NLink.Tests.NativeSemanticCopyProbeForTest]::Completed;
                RejectedPredicate=[NLink.Tests.NativeSemanticCopyProbeForTest]::RejectedPredicate;
                RejectedCumulativeMilliseconds=[NLink.Tests.NativeSemanticCopyProbeForTest]::RejectedCumulativeMilliseconds;
                CopiedBytes=[NLink.Tests.NativeSemanticCopyProbeForTest]::CopiedBytes;ExpectedBytes=[NLink.Tests.NativeSemanticCopyProbeForTest]::ExpectedBytes;
                CumulativeMilliseconds=$work.ElapsedMilliseconds}
            if($mode -ceq 'profile-budget' -and ($null -ne $copy -or $probe.Completed -or $probe.RejectedPredicate -cne 'adoption_entry_budget' -or $probe.RejectedCumulativeMilliseconds -lt 16000 -or $probe.CopiedBytes -le 0)) {throw 'Forced semantic-copy budget observation differs'}
            if($mode -ceq 'profile-valid' -and ($null -eq $copy -or -not $probe.Completed -or $null -ne $probe.RejectedPredicate -or $probe.CopiedBytes -ne $probe.ExpectedBytes -or $probe.CumulativeMilliseconds -lt 14000 -or $probe.CumulativeMilliseconds -gt 15000)) {throw 'Successful semantic-copy observation differs'}
            [IO.File]::WriteAllText((Join-Path $Directory 'semantic-copy-control.json'),($probe | ConvertTo-Json -Compress),[Text.UTF8Encoding]::new($false))
        }
        if($mode -cin @('missing-cursor','active-work','collision','hash','lines','closed-output','budget','profile-budget')) {
            if($null -ne $copy -or $binding.IsCurrent) { throw ('Invalid copy accepted: '+$mode) }
            if($mode -ceq 'collision' -and [IO.File]::ReadAllText($destination) -cne 'sentinel') { throw 'Copy overwrote existing evidence' }
        } else {
            if($mode -ceq 'legacy-isolation' -and ($null -eq $copy -or
                $copy.Output.CreationPath -ceq $legacyDestination -or [IO.File]::ReadAllText($legacyDestination) -cne 'legacy-sentinel')) {
                throw 'Native semantic copy collided with the ordinary analyzer destination'
            }
            if($null -eq $copy -or -not $copy.IsCurrent -or -not $copy.SemanticCopyValidated -or -not [object]::ReferenceEquals($copy,$binding.SemanticCopy) -or
                $copy.LiveProofEligible -or $copy.NativeWriterCustodyEstablished -or $copy.PublicationCompletionIssued) { throw 'Owned semantic copy missing/authority' }
            if($work.IsRunning -or $work.ElapsedTicks -le $elapsedBefore) { throw 'Copy reset/stopped bypassed cumulative work' }
            if(-not [object]::ReferenceEquals($copy.Origins,$capture.SemanticOrigins) -or $copy.Output.LineCount -ne $copy.Origins.Count -or
                $copy.Output.Sha256 -cne $capture.Semantic.Sha256 -or $copy.Output.ByteCount -ne $capture.Semantic.ByteCount -or
                $copy.Output.FileIdentity -ceq $capture.Semantic.FileIdentity -or $copy.Output.CreationPath -cne $destination) { throw 'Copy provenance differs' }
            if(($mode -ceq 'empty') -ne ($copy.Output.ByteCount -eq 0)) { throw 'Wrong empty/nonempty copy' }
            if((Read-CopyFixtureBytes $destination) -cne (Read-CopyFixtureBytes $capture.Semantic.CreationPath)) { throw 'Actual copy differs' }
            $lease=$capture.GetType().GetField('outputs',$flags).GetValue($capture)
            $file=$lease.GetType().GetField('semanticCopy',$flags).GetValue($lease)
            if($mode -ceq 'copied-copy') {
                $foreign=$clone.Invoke($copy,$null)
                if($foreign.IsCurrent -or $foreign.SemanticCopyValidated) { throw 'Copied copy current' }
                $foreign.Dispose(); if(-not $copy.IsCurrent) { throw 'Copied copy retired original' }
            }
            if($mode -ceq 'repeat') { if($null -ne $binding.TryCopySemanticEvidence()) { throw 'Repeated copy accepted' } }
            elseif($mode -ceq 'closed-copy') { $file.Dispose() }
            elseif($mode -ceq 'cursor-dispose') { $cursor.Dispose() }
            elseif($mode -ceq 'cancel') {
                $cancel=[Threading.EventWaitHandle]::OpenExisting($Name+'.closed')
                try { [void]$cancel.Set(); if($copy.IsCurrent) { throw 'Cancelled copy current' }; [void]$cancel.Reset() } finally {$cancel.Dispose()}
            } elseif($mode -ceq 'complete') { if($finalizer.Complete('','',0,0,$null,$null,$null,$null,$null,$null)) { throw 'Invalid legacy completion succeeded' } }
            else { $copy.Dispose() }
            if($copy.IsCurrent -or $copy.SemanticCopyValidated -or $file.CanRead) { throw 'Copy retirement failed' }
        }
    }
    if($binding.IsCurrent -or $proof.IsCurrent -or $null -ne $binding.SemanticCopy -or ($null -ne $result -and $result.StreamCapture.IsCurrent)) { throw 'Retired copy kept input set' }
    if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) { throw 'Copy failure retained payloads' }
    if([IO.File]::Exists($destination)) { [IO.File]::AppendAllText($destination,'released') }
    [IO.File]::AppendAllText($source,'copy-released')
} finally { if($null -ne $result) {$result.StreamCapture.Dispose()}; if($null -ne $binding) {$binding.Dispose()} }
