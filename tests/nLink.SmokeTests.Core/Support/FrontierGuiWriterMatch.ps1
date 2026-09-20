. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierLedgerProcessing.ps1')
$mode=$Case.Substring('matching-'.Length)
$clone=[object].GetMethod('MemberwiseClone',$flags)
if($mode -cin @('extra-writer','snapshot-bounds')) {
    $extra=@()
    $extraCount=if($mode -ceq 'snapshot-bounds') {63} else {1}
    for($writer=0;$writer -lt $extraCount;$writer++) {
        for($i=1;$i -le 2;$i++) {
            $event=if($i -eq 1) {'operational_log_writer_started'} else {'operational_log_writer_drained'}
            $extra+="event=$event; acceptance_run_id=frontier-gui-run; scenario_run_id=frontier-gui-scenario; log_writer_id=extra-writer-$writer; log_writer_sequence=$i; log_writer_batch_generation=$i; log_writer_batch_first_sequence=$i; log_writer_batch_last_sequence=$i; log_writer_attempt_utc_ticks=$i"
        }
    }
    [IO.File]::AppendAllText($source,($extra -join "`n")+"`n")
}
if($mode -cin @('substituted-contribution','terminal-sequence')) {
    # Private transport-fault injection, not a claim that Core issued these facts.
    $items=$adopter.GetType().GetField('adopted',$flags).GetValue($adopter)
    $field=$items[0].GetType().GetField('Manifest',$flags)
    $manifest=$clone.Invoke($field.GetValue($items[0]),$null)
    if($mode -ceq 'substituted-contribution') { $manifest.GetType().GetField('Writer',$flags).SetValue($manifest,'substituted-writer') }
    else {
        $drainField=$manifest.GetType().GetField('Drain',$flags)
        $drain=$clone.Invoke($drainField.GetValue($manifest),$null)
        $last=$drain.GetType().GetField('Last',$flags)
        $last.SetValue($drain,[long]($last.GetValue($drain)+1))
        $drainField.SetValue($manifest,$drain)
    }
    $field.SetValue($items[0],$manifest)
}
$lines=[IO.File]::ReadAllLines($source); $bytes=[IO.File]::ReadAllBytes($source)
$proof=$owner.TrySealFiles()
if($null -eq $proof) { throw 'Writer match source seal missing' }
$finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
if($null -eq $finalizer) { throw 'Writer match original finalizer missing' }
$binding=$finalizer.TryTakeFrontierBinding()
if($null -eq $binding) { throw 'Writer match original binding missing' }
$reader=$null; $capture=$null; $otherCapture=$null; $otherSource=$null
try {
    $type=[NLink.Tools.LocalGuiWriterRequest+FrontierWriterMatch]
    if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) { throw 'Public writer match fabrication exposed' }
    $processorType=[NLink.Tools.FrontierLedger.GuiSmokeLogLedgerStreamingProcessor]
    $parameter=$binding.GetType().GetMethod('TryMatchProcessedWriters').GetParameters()[0].ParameterType
    if($parameter -ne [NLink.Tools.FrontierLedger.GuiSmokeLedgerStreamingResult] -or $processorType.Assembly -ne $binding.GetType().Assembly) { throw 'Writer match is not an isolated typed join' }
    $reader=$binding.TryOpenSource()
    if($null -eq $reader -or -not $reader.TryBeginProcessingWork()) { throw 'Writer match processing interval missing' }
    $events=[string[]]@('filetransfer_frontier_publication_receipt_closed','operational_log_writer_drained')
    $result=[NLink.Tools.FrontierLedger.GuiSmokeLogLedgerStreamingProcessor]::ProcessFromStreamWithRetainedOutputs(
        $reader,[long]$lines.Length,0L,(Join-Path $Directory 'match-retained.tmp'),(Join-Path $Directory 'match-writer.tmp'),
        'frontier-gui-run','frontier-gui-scenario',[string[]]@(),(Join-Path $Directory 'match-semantic.tmp'),$events,$reader.ProcessingLifetime)
    $capture=$result.StreamCapture; $snapshot=$capture.Validation
    if(-not $capture.HasRetainedOutputs) { throw 'Writer match original retained outputs missing' }
    if($mode -ceq 'snapshot-bounds') {
        if($null -ne $snapshot -or $result.Writers.Length -ne 65) { throw 'Writer match snapshot bound control invalid' }
    } elseif($null -eq $snapshot -or $snapshot.HasWriterClosureErrors) { throw 'Writer match original processed facts missing' }
    $lease=$capture.GetType().GetField('outputs',$flags).GetValue($capture)
    $outputFiles=$lease.GetType().GetField('files',$flags).GetValue($lease)
    if($mode -ceq 'copied-binding') {
        $copy=$clone.Invoke($binding,$null)
        if($null -ne $copy.TryMatchProcessedWriters($result)) { throw 'Copied binding minted writer match' }
        $copy.Dispose()
    }
    if($mode -ceq 'copied-result') {
        $copy=$clone.Invoke($result,$null)
        if($null -ne $binding.TryMatchProcessedWriters($copy)) { throw 'Copied result minted writer match' }
    }
    if($mode -cin @('foreign-result','legacy-result')) {
        $otherSource=[IO.MemoryStream]::new($bytes,$false)
        $paths=@('other-retained.tmp','other-writer.tmp','other-semantic.tmp') | ForEach-Object { Join-Path $Directory $_ }
        if($mode -ceq 'foreign-result') {
            $other=[NLink.Tools.FrontierLedger.GuiSmokeLogLedgerStreamingProcessor]::ProcessFromStreamWithRetainedOutputs(
                $otherSource,[long]$lines.Length,0L,$paths[0],$paths[1],'frontier-gui-run','frontier-gui-scenario',
                [string[]]@(),$paths[2],$events,[Threading.CancellationToken]::None)
            if($null -ne $binding.TryMatchProcessedWriters($other)) { throw 'Foreign source minted writer match' }
        } else {
            $other=[NLink.Tools.GuiSmokeLogLedgerStreamingProcessor]::ProcessFromStreamWithRetainedOutputs(
                $otherSource,[long]$lines.Length,0L,$paths[0],$paths[1],'frontier-gui-run','frontier-gui-scenario',
                [string[]]@(),$paths[2],$events,[Threading.CancellationToken]::None)
            $rejected=$false
            try { [void]$binding.TryMatchProcessedWriters($other) } catch [Management.Automation.MethodException] { $rejected=$true }
            if(-not $rejected) { throw 'Legacy result crossed typed optional join' }
        }
        $otherCapture=$other.StreamCapture
        if(-not $otherCapture.IsCurrent) { throw 'Foreign result control lost its own capture' }
        $otherCapture.Dispose(); $otherSource.Dispose()
    }
    if($mode -ceq 'mutable-result') {
        foreach($writer in $result.Writers) { $writer.WriterId='changed'; $writer.LastSequence=999 }
        foreach($drain in $result.Drains) { $drain.Sequences[0]=999 }
        $result.Writers=@(); $result.Drains=@(); $result.DeferredBatches=@(); $result.WriterGapCount=999
    }
    if(-not $binding.IsCurrent -or ($null -ne $snapshot -and -not $snapshot.IsCurrent) -or $null -ne $binding.WriterMatch) { throw 'Nonowned candidate changed original matching attempt' }
    if($mode -ceq 'after-finish' -and -not $reader.TryFinishProcessingWork()) { throw 'Writer match finish control failed' }
    $match=$binding.TryMatchProcessedWriters($result)
    $rejected=$mode -cin @('extra-writer','substituted-contribution','terminal-sequence','after-finish','snapshot-bounds')
    if($rejected) {
        if($null -ne $match -or $binding.IsCurrent -or $proof.IsCurrent) { throw 'Invalid writer pair retained original binding' }
    } else {
        if($null -eq $match -or -not $match.IsCurrent -or -not $match.MatchesResult($result) -or
            -not [object]::ReferenceEquals($match,$binding.WriterMatch) -or -not [object]::ReferenceEquals($snapshot,$match.Validation) -or
            $match.WriterCount -ne 2 -or $match.LiveProofEligible -or $match.NativeWriterCustodyEstablished -or $match.PublicationCompletionIssued) { throw 'Original writer match missing or enabled authority' }
        $copy=$clone.Invoke($result,$null)
        if($match.MatchesResult($copy)) { throw 'Writer match accepted copied result' }
        if($mode -ceq 'copied-match') {
            $copy=$clone.Invoke($match,$null)
            if($copy.IsCurrent -or $copy.MatchesResult($result)) { throw 'Copied writer match accepted' }
            $copy.Dispose(); if(-not $match.IsCurrent) { throw 'Copied writer match retired original' }
        }
        if(-not $reader.TryFinishProcessingWork() -or -not $match.IsCurrent) { throw 'Writer match did not survive successful processing finish' }
        if($mode -ceq 'repeat') {
            if($null -ne $binding.TryMatchProcessedWriters($result)) { throw 'Repeated writer match accepted' }
        } elseif($mode -ceq 'closed-output') { $outputFiles[1].Dispose() }
        elseif($mode -ceq 'cancel') {
            $cancel=[Threading.EventWaitHandle]::OpenExisting($Name+'.closed')
            try { [void]$cancel.Set(); if($match.IsCurrent) { throw 'Cancelled writer match current' }; [void]$cancel.Reset() } finally { $cancel.Dispose() }
        } elseif($mode -ceq 'complete') {
            if($finalizer.Complete('','',0,0,$null,$null,$null,$null,$null,$null)) { throw 'Invalid legacy completion succeeded' }
        } elseif($mode -ceq 'dispose') { $match.Dispose() }
        else { $binding.Dispose() }
        if($match.IsCurrent -or $match.MatchesResult($result) -or $null -ne $binding.WriterMatch) { throw 'Retired writer match revived' }
    }
    if(@($outputFiles | Where-Object {$_.CanRead}).Count -or ($null -ne $snapshot -and $snapshot.IsCurrent) -or $capture.IsCurrent -or $reader.IsCurrent -or $binding.IsCurrent -or $proof.IsCurrent) { throw 'Writer match retirement retained inputs or outputs' }
    foreach($role in @('Retained','Writer','Semantic')) {
        $path=$capture.$role.CreationPath; [IO.File]::AppendAllText($path,'released'); [IO.File]::Move($path,$path+'.released')
    }
    if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) { throw 'Writer match cleanup retained payloads' }
    [IO.File]::AppendAllText($source,'writer-match-released')
} finally {
    if($null -ne $otherCapture) { $otherCapture.Dispose() }
    if($null -ne $otherSource) { $otherSource.Dispose() }
    if($null -ne $capture) { $capture.Dispose() }
    if($null -ne $reader) { $reader.Dispose() }
    $binding.Dispose()
}
