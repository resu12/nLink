$mode=$Case.Substring('cursor-join-'.Length)
$clone=[object].GetMethod('MemberwiseClone',$flags)
$proof=$owner.TrySealFiles()
if($null -eq $proof) { throw 'Cursor join seal missing' }
$finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
$binding=$finalizer.TryTakeFrontierBinding(); $result=$null; $match=$null; $reader=$null
try {
    if($null -eq $binding) { throw 'Cursor join binding missing' }
    $type=[NLink.Tools.LocalGuiWriterRequest+FrontierCursorMatch]
    if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) { throw 'Cursor join fabrication exposed' }
    if($mode -ceq 'copied-binding') {
        $copy=$clone.Invoke($binding,$null)
        if($null -ne $copy.TryProcessObservedLedger([string[]]@())) { throw 'Copied binding processed cursor' }
        $copy.Dispose(); if(-not $binding.IsCurrent) { throw 'Copied binding retired cursor' }
    }
    if($mode -cin @('valid','prefix','empty','copied-binding','copied-match','closed-output','cancel','complete','repeat-process','missing-initial')) {
        $result=$binding.TryProcessObservedLedger([string[]]@())
        if($mode -ceq 'missing-initial') {
            if($null -ne $result -or $binding.IsCurrent) { throw 'Missing initial observation accepted' }
        } else {
            if($null -eq $result) { throw 'Owned cursor processing failed' }
            $match=$binding.CursorMatch
        }
    } else {
        $reader=$binding.TryOpenSource()
        if($null -eq $reader -or -not $reader.TryBeginProcessingWork()) { throw 'Cursor join source missing' }
        $final=$null
        if($mode -cne 'missing-final') { $final=$binding.TryCaptureLedgerFinal(); if($null -eq $final) { throw 'Cursor join final observation missing' } }
        $markers=$binding.TryCaptureRecoveryMarkers()
        if($null -eq $markers) { throw 'Cursor join markers missing' }
        $lines=if($null -ne $final) {$final.LineCount} else {[IO.File]::ReadAllLines($source).Length}
        $bookmark=$initialObservation.Bookmark+[int]($mode -ceq 'wrong-bookmark')
        $arguments=[object[]]@($reader,[long]$lines,[long]$bookmark,[IO.Path]::Combine($Directory,'join-retained.tmp'),[IO.Path]::Combine($Directory,'join-writer.tmp'),
            'frontier-gui-run','frontier-gui-scenario',[string[]]@($markers.DeferredBatchKeys),[IO.Path]::Combine($Directory,'join-semantic.tmp'),[string[]]@(),$reader.ProcessingLifetime)
        $method=if($mode -ceq 'generic-reader') {
            [NLink.Tools.FrontierLedger.GuiSmokeLogLedgerStreamingProcessor].GetMethod('ProcessFromStreamWithRetainedOutputs')
        } else { [NLink.Tools.FrontierLedger.GuiSmokeLogLedgerStreamingProcessor].GetMethod('ProcessOwnedCursor',[Reflection.BindingFlags]'Static,NonPublic') }
        $result=$method.Invoke($null,$arguments)
        if($mode -cne 'missing-writer' -and $null -eq $binding.TryMatchProcessedWriters($result)) { throw 'Cursor join writer control failed' }
        if($mode -cnotin @('missing-writer','missing-recovery') -and $null -eq $binding.TryMatchRecovery($result)) { throw 'Cursor join recovery control failed' }
        if($mode -ceq 'changed-index') { $indices=$ledgerOrigin.GetType().GetField('lineStarts',$flags).GetValue($ledgerOrigin); $indices[1]++ }
        if($mode -ceq 'after-finish' -and -not $reader.TryFinishProcessingWork()) { throw 'Cursor finish control failed' }
        if($mode -ceq 'budget') {
            $work=$owner.GetType().GetField('frontierWork',$flags).GetValue($owner)
            $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*16))
        }
        if($mode -ceq 'copied-result') {
            if($null -ne $binding.TryMatchProcessedCursor($clone.Invoke($result,$null)) -or -not $binding.IsCurrent) { throw 'Copied result consumed cursor join' }
        }
        if($mode -ceq 'mutable-result') { $result.Writers=@(); $result.SourceLineVisitCount=999; $result.WriterGapCount=999; $result.RetainedLineCount=999 }
        $match=$binding.TryMatchProcessedCursor($result)
        if($mode -cin @('wrong-bookmark','missing-final','generic-reader','changed-index','missing-writer','missing-recovery','after-finish','budget')) {
            if($null -ne $match -or $binding.IsCurrent) { throw 'Invalid exact cursor join accepted' }
        } elseif($null -eq $match -or -not $reader.TryFinishProcessingWork()) { throw 'Original cursor join missing' }
    }
    if($null -ne $match) {
        if(-not $match.IsCurrent -or -not $match.CursorHistoryValidated -or -not $match.MatchesResult($result) -or
            -not [object]::ReferenceEquals($match.Initial,$initialObservation) -or $match.Final.Bookmark -ne $initialObservation.Bookmark -or
            $match.Final.GenerationId -cne $initialObservation.GenerationId -or $match.Final.GenerationId -ceq $initialObservation.LedgerId -or
            $match.LiveProofEligible -or $match.NativeWriterCustodyEstablished -or $match.PublicationCompletionIssued) { throw 'Cursor match ownership/authority mismatch' }
        $prefix=$result.StreamCapture.Validation.InitialWriters
        if($prefix.Count -ne [int]($mode -ceq 'prefix')) { throw 'Wrong immutable initial writer set' }
        if($mode -ceq 'prefix' -and ($prefix[0].FirstSequence -ne 1 -or $prefix[0].LastSequence -ne 2 -or $prefix[0].ObservedCount -ne 2)) { throw 'Wrong original writer prefix facts' }
        if($mode -ceq 'copied-match') {
            $copy=$clone.Invoke($match,$null)
            if($copy.IsCurrent -or $copy.CursorHistoryValidated -or $copy.MatchesResult($result)) { throw 'Copied cursor match current' }
            $copy.Dispose(); if(-not $match.IsCurrent) { throw 'Copied cursor match retired original' }
        }
        if($mode -ceq 'repeat-join') { if($null -ne $binding.TryMatchProcessedCursor($result)) { throw 'Repeated cursor join accepted' } }
        elseif($mode -ceq 'repeat-process') { if($null -ne $binding.TryProcessObservedLedger([string[]]@())) { throw 'Repeated cursor processing accepted' } }
        elseif($mode -ceq 'closed-output') {
            $lease=$result.StreamCapture.GetType().GetField('outputs',$flags).GetValue($result.StreamCapture)
            $lease.GetType().GetField('files',$flags).GetValue($lease)[1].Dispose()
        } elseif($mode -ceq 'cancel') {
            $cancel=[Threading.EventWaitHandle]::OpenExisting($Name+'.closed')
            try { [void]$cancel.Set(); if($match.IsCurrent) { throw 'Cancelled cursor match current' }; [void]$cancel.Reset() } finally { $cancel.Dispose() }
        } elseif($mode -ceq 'complete') { if($finalizer.Complete('','',0,0,$null,$null,$null,$null,$null,$null)) { throw 'Invalid legacy completion succeeded' } }
        else { $match.Dispose() }
        if($match.IsCurrent -or $match.CursorHistoryValidated -or $match.MatchesResult($result)) { throw 'Retired cursor match revived' }
    }
    if($binding.IsCurrent -or $proof.IsCurrent -or ($null -ne $result -and $result.StreamCapture.IsCurrent) -or $null -ne $binding.CursorMatch) { throw 'Cursor failure retained inputs/outputs' }
    if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) { throw 'Cursor failure retained payloads' }
    [IO.File]::AppendAllText($source,'join-released')
} finally {
    if($null -ne $result -and $null -ne $result.StreamCapture) { $result.StreamCapture.Dispose() }
    if($null -ne $reader) { $reader.Dispose() }
    if($null -ne $binding) { $binding.Dispose() }
}
