. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierLedgerProcessing.ps1')
$mode=$Case.Substring('markers-'.Length)
$clone=[object].GetMethod('MemberwiseClone',$flags)
$lines=[IO.File]::ReadAllLines($source)
$nativeFiles=@()
foreach($appEntry in $apps) {
    $output=$appEntry.Binding.GetType().GetField('Output',$flags).GetValue($appEntry.Binding)
    foreach($retained in $output.GetType().GetField('RecoveryOutputs',$flags).GetValue($output)) {
        $nativeFiles+=,$retained.GetType().GetField('file',$flags).GetValue($retained)
    }
}
$proof=$owner.TrySealFiles()
if($null -eq $proof) { throw 'Marker source seal missing' }
$finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
if($null -eq $finalizer) { throw 'Marker original finalizer missing' }
$binding=$finalizer.TryTakeFrontierBinding()
if($null -eq $binding) { throw 'Marker original binding missing' }
$reader=$null; $capture=$null; $markers=$null; $match=$null; $outputFiles=@()
function Expire-MarkerWork {
    $work=$adopter.GetType().GetField('work',$flags).GetValue($adopter)
    $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
    $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*16))
}
try {
    if($mode.StartsWith('buffered-',[StringComparison]::Ordinal)) {
        [NLink.Tests.RecoveryBufferProbe]::Run($binding,$adopter,$Name,$mode.Substring('buffered-'.Length))
        if($binding.IsCurrent -or $proof.IsCurrent -or @($nativeFiles | Where-Object {$_.CanRead}).Count) {throw 'Buffered recovery retained native handles'}
        if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) {throw 'Buffered recovery retained payloads'}
        [IO.File]::AppendAllText($source,'recovery-buffer-released')
        return
    }
    foreach($type in @([NLink.Tools.FrontierRecoveryMarker],[NLink.Tools.LocalGuiWriterRequest+FrontierRecoveryMarkers],[NLink.Tools.LocalGuiWriterRequest+FrontierRecoveryMatch])) {
        if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) { throw 'Public recovery proof fabrication exposed' }
    }
    if($mode -cin @('valid','no-markers','missing','orphan','duplicate','wrong-run','wrong-writer','ticks','hash','generation','duplicate-field','terminal','invalid-utf8','truncated-utf8')) {
        $result=Invoke-FileTransferFrontierLedgerProcessing -Binding $binding -FrozenLineCount $lines.Length -Bookmark 0
        if($mode -cin @('valid','no-markers')) {
            if($null -eq $result) { throw 'Valid owned marker processing failed' }
            $capture=$result.StreamCapture; $match=$binding.RecoveryMatch; $markers=$match.Markers
            $expected=if($mode -ceq 'no-markers') {0} else {2}
            if($match.RecoveredBatchCount -ne $expected -or $capture.Validation.DeferredBatches.Count -ne $expected -or $markers.Markers.Count -ne 2*$expected) { throw 'Derived marker keys not processed exactly' }
        } elseif($null -ne $result -or $binding.IsCurrent -or $proof.IsCurrent) { throw 'Invalid recovery proof accepted' }
    } else {
        $reader=$binding.TryOpenSource()
        if($null -eq $reader) { throw 'Marker original source missing' }
        if($mode -cne 'before-work' -and -not $reader.TryBeginProcessingWork()) { throw 'Marker work interval missing' }
        if($mode -ceq 'prior-open' -and $null -eq $binding.TryOpenRecoveryOutputs()) { throw 'Prior reader open control missing' }
        if($mode -ceq 'budget-capture') { Expire-MarkerWork }
        if($mode -ceq 'source-started') { if($reader.ReadByte() -lt 0) { throw 'Early source read control failed' } }
        if($mode -ceq 'copied-binding') {
            $copy=$clone.Invoke($binding,$null)
            if($null -ne $copy.TryCaptureRecoveryMarkers()) { throw 'Copied binding captured markers' }
            $copy.Dispose(); if(-not $binding.IsCurrent) { throw 'Copied binding retired original markers' }
        }
        if($mode -cne 'missing-capture') { $markers=$binding.TryCaptureRecoveryMarkers() }
        if($mode -cin @('prior-open','before-work','budget-capture','source-started')) {
            if($null -ne $markers -or $binding.IsCurrent) { throw 'Invalid capture retained binding' }
        } else {
            if($mode -cne 'missing-capture' -and ($null -eq $markers -or -not $markers.IsCurrent -or $markers.Markers.Count -ne 4 -or $markers.DeferredBatchKeys.Count -ne 2)) { throw 'Original marker facts missing' }
            if($mode -ceq 'copied-markers') {
                $copy=$clone.Invoke($markers,$null)
                if($copy.IsCurrent) { throw 'Copied markers current' }
                $copy.Dispose(); if(-not $markers.IsCurrent) { throw 'Copied markers retired original' }
            }
            if($mode -ceq 'repeat-capture') {
                if($null -ne $binding.TryCaptureRecoveryMarkers() -or $markers.IsCurrent) { throw 'Repeated capture accepted' }
            } else {
                $keys=[string[]]@()
                if($null -ne $markers) { $keys=[string[]]@($markers.DeferredBatchKeys) }
                $result=[NLink.Tools.FrontierLedger.GuiSmokeLogLedgerStreamingProcessor]::ProcessFromStreamWithRetainedOutputs(
                    $reader,[long]$lines.Length,0L,(Join-Path $Directory 'marker-retained.tmp'),(Join-Path $Directory 'marker-writer.tmp'),
                    'frontier-gui-run','frontier-gui-scenario',[string[]]@($keys),(Join-Path $Directory 'marker-semantic.tmp'),[string[]]@(),$reader.ProcessingLifetime)
                $capture=$result.StreamCapture
                if($null -eq $binding.TryMatchProcessedWriters($result)) { throw 'Original marker writer join missing' }
                if($mode -ceq 'copied-result') {
                    if($null -ne $binding.TryMatchRecovery($clone.Invoke($result,$null)) -or -not $markers.IsCurrent) { throw 'Copied result consumed recovery join' }
                }
                if($mode -ceq 'mutable-result') {
                    foreach($batch in $result.DeferredBatches) { $batch.PayloadSha256='changed'; $batch.LineCount=999; $batch.TupleValid=$false }
                    $result.DeferredBatches=@(); $result.Writers=@(); $result.WriterGapCount=999
                }
                if($mode -ceq 'budget-match') { Expire-MarkerWork }
                if($mode -ceq 'after-finish' -and -not $reader.TryFinishProcessingWork()) { throw 'Finish control failed' }
                $match=$binding.TryMatchRecovery($result)
                if($mode -cin @('budget-match','after-finish','missing-capture')) {
                    if($null -ne $match -or $binding.IsCurrent) { throw 'Expired/late recovery join accepted' }
                } elseif($null -eq $match -or -not $reader.TryFinishProcessingWork()) { throw 'Original recovery join missing' }
            }
        }
    }
    if($null -ne $match) {
        if(-not $match.IsCurrent -or -not $match.MatchesResult($result) -or -not [object]::ReferenceEquals($match,$binding.RecoveryMatch) -or
            $match.LiveProofEligible -or $match.NativeWriterCustodyEstablished -or $match.PublicationCompletionIssued -or
            $markers.LiveProofEligible -or $markers.NativeWriterCustodyEstablished -or $markers.PublicationCompletionIssued) { throw 'Recovery match lost ownership or enabled authority' }
        if($match.MatchesResult($clone.Invoke($result,$null))) { throw 'Recovery match accepted copied result' }
        $outputFiles=$capture.GetType().GetField('outputs',$flags).GetValue($capture).GetType().GetField('files',$flags).GetValue($capture.GetType().GetField('outputs',$flags).GetValue($capture))
        if($mode -ceq 'copied-match') {
            $copy=$clone.Invoke($match,$null)
            if($copy.IsCurrent -or $copy.MatchesResult($result)) { throw 'Copied recovery match current' }
            $copy.Dispose(); if(-not $match.IsCurrent) { throw 'Copied match retired original' }
        }
        if($mode -ceq 'repeat-match') { if($null -ne $binding.TryMatchRecovery($result)) { throw 'Repeated recovery match accepted' } }
        elseif($mode -ceq 'closed-marker') { $nativeFiles[1].Dispose() }
        elseif($mode -ceq 'closed-output') { $outputFiles[1].Dispose() }
        elseif($mode -ceq 'cancel') {
            $cancel=[Threading.EventWaitHandle]::OpenExisting($Name+'.closed')
            try { [void]$cancel.Set(); if($match.IsCurrent) { throw 'Cancelled match current' }; [void]$cancel.Reset() } finally { $cancel.Dispose() }
        } elseif($mode -ceq 'complete') {
            if($finalizer.Complete('','',0,0,$null,$null,$null,$null,$null,$null)) { throw 'Invalid legacy completion succeeded' }
        } elseif($mode -ceq 'dispose') { $markers.Dispose() }
        else { $binding.Dispose() }
        if($match.IsCurrent -or $match.MatchesResult($result) -or $null -ne $binding.RecoveryMatch) { throw 'Retired recovery match revived' }
    }
    if(($null -ne $markers -and $markers.IsCurrent) -or ($null -ne $capture -and $capture.IsCurrent) -or $binding.IsCurrent -or $proof.IsCurrent -or
        @($nativeFiles | Where-Object {$_.CanRead}).Count -or @($outputFiles | Where-Object {$_.CanRead}).Count) { throw 'Recovery retirement retained inputs/outputs' }
    if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) { throw 'Recovery cleanup retained payloads' }
    [IO.File]::AppendAllText($source,'recovery-match-released')
} finally {
    if($null -ne $capture) { $capture.Dispose() }
    if($null -ne $reader) { $reader.Dispose() }
    $binding.Dispose()
}
