$mode=$Case.Substring('cursor-'.Length)
$clone=[object].GetMethod('MemberwiseClone',$flags)
$native=$ledgerOrigin.GetType().GetField('file',$flags).GetValue($ledgerOrigin)
$work=$owner.GetType().GetField('frontierWork',$flags).GetValue($owner)
if(-not [object]::ReferenceEquals($work,$adopter.GetType().GetField('work',$flags).GetValue($adopter)) -or $work.IsRunning) { throw 'Cursor work split or charged idle runtime' }
if($mode -ceq 'prefix-rewrite') {
    # Change an already-observed ordinary prefix, outside all writer ranges.
    $write=[IO.File]::Open($source,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite)
    try { $write.WriteByte([byte][char]'X') } finally { $write.Dispose() }
}
if($mode -ceq 'partial-tail') { [IO.File]::AppendAllText($source,'unterminated',[Text.UTF8Encoding]::new($false)) }
$expectedLines=[IO.File]::ReadAllLines($source).Length
$proof=$owner.TrySealFiles()
if($null -eq $proof) { throw 'Cursor original source seal missing' }
$finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
if($null -eq $finalizer) { throw 'Cursor original finalizer missing' }
$binding=$finalizer.TryTakeFrontierBinding(); $reader=$null; $capture=$null; $observation=$null
try {
    if($null -eq $binding) { throw 'Cursor original binding missing' }
    if($mode -ceq 'late-sync') {
        if($ledgerOrigin.TrySynchronize() -or $binding.IsCurrent) { throw 'Post-seal live sync accepted' }
    } else {
        $reader=$binding.TryOpenSource()
        if($null -eq $reader) { throw 'Cursor original source read missing' }
        if($mode -cne 'before-work' -and -not $reader.TryBeginProcessingWork()) { throw 'Cursor processing work missing' }
        if($mode -ceq 'source-started' -and $reader.ReadByte() -lt 0) { throw 'Cursor early source read control failed' }
        if($mode -ceq 'budget-final') {
            $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*11))
        }
        if($mode -ceq 'after-finish') {
            $buffer=[byte[]]::new(32768)
            while($reader.Read($buffer,0,$buffer.Length) -gt 0) { }
            if(-not $reader.TryFinishProcessingWork()) { throw 'Cursor finish control failed' }
        }
        if($mode -ceq 'copied-binding') {
            $copy=$clone.Invoke($binding,$null)
            if($null -ne $copy.TryCaptureLedgerFinal()) { throw 'Copied binding captured final cursor' }
            $copy.Dispose(); if(-not $initialObservation.IsCurrent) { throw 'Copied binding retired cursor' }
        }
        $observation=$binding.TryCaptureLedgerFinal()
        if($mode -cin @('missing-initial','before-work','source-started','prefix-rewrite','partial-tail','after-finish','budget-final')) {
            if($null -ne $observation -or $binding.IsCurrent -or $ledgerOrigin.IsCurrent) { throw 'Invalid original final cursor accepted' }
        } else {
            if($null -eq $observation -or -not $observation.IsCurrent -or -not $observation.IsFinal -or
                $observation.LineCount -ne $expectedLines -or $observation.BytesRead -ne $proof.SourceBytes -or
                $observation.CommittedBytes -ne $proof.SourceBytes -or $observation.Bookmark -ne $initialObservation.Bookmark -or
                $observation.SynchronizationCount -ne 4 -or $observation.LedgerId -cne $initialObservation.LedgerId -or
                -not $initialObservation.IsCurrent -or $reader.Position -ne 0 -or -not $work.IsRunning -or
                $observation.CursorHistoryValidated -or $observation.LiveProofEligible -or $observation.NativeWriterCustodyEstablished -or
                $observation.PublicationCompletionIssued) { throw 'Final cursor facts lost ownership/timing or enabled authority' }
            if($mode -ceq 'copied-final') {
                $copy=$clone.Invoke($observation,$null); if($copy.IsCurrent) { throw 'Copied final cursor current' }
                $copy.Dispose(); if(-not $observation.IsCurrent) { throw 'Copied final cursor retired original' }
            }
            if($mode -ceq 'repeat-final') {
                if($null -ne $binding.TryCaptureLedgerFinal() -or $observation.IsCurrent) { throw 'Repeated final cursor capture accepted' }
            } else {
                $markers=$binding.TryCaptureRecoveryMarkers()
                if($null -eq $markers) { throw 'Cursor path lost recovery capture' }
                $result=[NLink.Tools.FrontierLedger.GuiSmokeLogLedgerStreamingProcessor]::ProcessFromStreamWithRetainedOutputs(
                    $reader,[long]$observation.LineCount,[long]$observation.Bookmark,(Join-Path $Directory 'cursor-retained.tmp'),(Join-Path $Directory 'cursor-writer.tmp'),
                    'frontier-gui-run','frontier-gui-scenario',[string[]]@($markers.DeferredBatchKeys),(Join-Path $Directory 'cursor-semantic.tmp'),[string[]]@(),$reader.ProcessingLifetime)
                $capture=$result.StreamCapture
                if($null -eq $binding.TryMatchProcessedWriters($result) -or $null -eq $binding.TryMatchRecovery($result) -or -not $reader.TryFinishProcessingWork()) { throw 'Cursor path lost writer/recovery joins' }
                if(-not $observation.IsCurrent -or -not $initialObservation.IsCurrent -or $work.IsRunning) { throw 'Cursor processing retired valid observation or left clock running' }
                if($mode -ceq 'closed-origin') { $native.Dispose() }
                else { $observation.Dispose() }
            }
        }
    }
    if(($null -ne $initialObservation -and $initialObservation.IsCurrent) -or ($null -ne $observation -and $observation.IsCurrent) -or
        ($null -ne $capture -and $capture.IsCurrent) -or $binding.IsCurrent -or $proof.IsCurrent -or $native.CanRead) { throw 'Cursor retirement retained original resources' }
    if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) { throw 'Cursor retirement retained payloads' }
    [IO.File]::AppendAllText($source,'cursor-released')
} finally {
    if($null -ne $capture) { $capture.Dispose() }
    if($null -ne $reader) { $reader.Dispose() }
    if($null -ne $binding) { $binding.Dispose() }
}
