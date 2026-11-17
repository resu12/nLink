. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalFrontierLedgerProcessing.ps1')
$mode=$Case.Substring('processing-'.Length)
if($mode -ceq 'rotated') { [IO.File]::Move($source,$source+'.original'); [IO.File]::WriteAllText($source,'replacement'); $source+='.original' }
$lines=[IO.File]::ReadAllLines($source)
$proof=$owner.TrySealFiles()
if($null -eq $proof) { throw 'Processing source seal missing' }
$finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($Name,$RequestId,'frontier-gui-run','frontier-gui-scenario',$Directory)
if($null -eq $finalizer) { throw 'Processing finalizer missing' }
$binding=$finalizer.TryTakeFrontierBinding()
if($null -eq $binding) { throw 'Processing binding missing' }
$reader=$null
try {
    $count=[long]$lines.Length
    $bookmark=if($mode -ceq 'bookmark') {1L} else {0L}
    $events=[string[]]@('filetransfer_frontier_publication_receipt_closed','operational_log_writer_drained')
    if($mode -ceq 'copied-binding') {
        $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($binding,$null)
        $copiedResult=Invoke-FileTransferFrontierLedgerProcessing -Binding $copy -FrozenLineCount $count -Bookmark $bookmark -SemanticEventNames $events
        if($null -ne $copiedResult -or -not $binding.IsCurrent) { throw 'Copied binding processed or retired original source' }
    }
    if($mode -ceq 'prior-open') { $reader=$binding.TryOpenSource(); if($null -eq $reader) { throw 'Prior source open missing' }; [void]$reader.ReadByte() }
    if($mode -ceq 'budget') {
        $work=$adopter.GetType().GetField('work',$flags).GetValue($adopter)
        $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*16))
    }
    if($mode -ceq 'short') { $count-- }
    if($mode -ceq 'long') { $count++ }
    $result=Invoke-FileTransferFrontierLedgerProcessing -Binding $binding -FrozenLineCount $count -Bookmark $bookmark -SemanticEventNames $events
    if($mode -cin @('short','long','prior-open','budget')) {
        if($null -ne $result -or $binding.IsCurrent -or $proof.IsCurrent) { throw 'Invalid source processing retained provenance' }
    } else {
        if($null -eq $result -or -not $result.OwnsStreamCapture($result.StreamCapture)) { throw 'Owned processor result missing' }
        $capture=$result.StreamCapture
        $validation=$capture.Validation
        if($null -eq $validation -or -not $capture.OwnsValidation($validation) -or -not $validation.IsCurrent) { throw 'Owned validation facts missing' }
        $reader=$capture.GetType().GetField('source',$flags).GetValue($capture)
        if($reader -isnot [NLink.Tools.LocalGuiWriterRequest+FrontierSourceRead] -or -not $reader.IsCurrent -or -not $reader.ReachedEnd -or
            -not $capture.MatchesOriginalSource($reader) -or $capture.SourceBytes -ne $proof.SourceBytes -or $capture.SourceLines -ne $lines.Length -or
            $capture.AcceptanceRunId -cne 'frontier-gui-run' -or $capture.ScenarioRunId -cne 'frontier-gui-scenario') { throw 'Owned source/result binding mismatch' }
        $work=$adopter.GetType().GetField('work',$flags).GetValue($adopter)
        if($work.IsRunning -or $work.ElapsedMilliseconds -ge 15000) { throw 'Processing did not finish within shared work budget' }
        $baselineRoot=Join-Path $Directory 'legacy-baseline'; [void][IO.Directory]::CreateDirectory($baselineRoot)
        $baseline=[NLink.Tools.GuiSmokeLogLedgerStreamingProcessor]::Process($source,$count,$bookmark,
            (Join-Path $baselineRoot 'retained.log'),(Join-Path $baselineRoot 'writer.log'),'frontier-gui-run','frontier-gui-scenario',
            [string[]]@(),(Join-Path $baselineRoot 'semantic.log'),$events)
        $actual=$result | Select-Object * -ExcludeProperty NativeCapture,StreamCapture | ConvertTo-Json -Depth 10 -Compress
        $expected=$baseline | Select-Object * -ExcludeProperty NativeCapture,StreamCapture | ConvertTo-Json -Depth 10 -Compress
        if($actual -cne $expected) { throw 'Owned processor changed validation or outputs' }
        $lease=$capture.GetType().GetField('outputs',$flags).GetValue($capture)
        if($null -eq $lease -or -not $capture.HasRetainedOutputs) { throw 'Owned retained outputs missing' }
        $outputFiles=$lease.GetType().GetField('files',$flags).GetValue($lease)
        foreach($role in @('Retained','Writer','Semantic')) {
            $output=$capture.$role; $origins=$capture.($role+'Origins')
            $read=[IO.File]::Open($output.CreationPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::ReadWrite)
            $text=[IO.StreamReader]::new($read)
            try { $outputLines=[Collections.Generic.List[string]]::new(); while(-not $text.EndOfStream) { $outputLines.Add($text.ReadLine()) } } finally { $text.Dispose() }
            if($output.LineCount -ne $origins.Count -or $outputLines.Count -ne $origins.Count) { throw 'Owned output origin count mismatch' }
            for($i=0;$i -lt $origins.Count;$i++) { if($outputLines[$i] -cne $lines[$origins[$i]-1]) { throw 'Owned physical source origin mismatch' } }
            $blocked=$false
            try { $probe=[IO.File]::Open($output.CreationPath,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete); $probe.Dispose() } catch [IO.IOException] { $blocked=$true }
            if(-not $blocked) { throw 'Owned output remained writable' }
        }
        if($capture.LiveProofEligible -or $capture.NativeWriterCustodyEstablished -or $capture.PublicationCompletionIssued -or $null -ne $result.NativeCapture) { throw 'Source processing relabeled old completion or enabled publication' }
        if($mode.StartsWith('validation-',[StringComparison]::Ordinal)) {
            if($validation.HasWriterClosureErrors -or $validation.Writers.Count -ne 2 -or
                $validation.SourceLineVisitCount -ne $lines.Length -or $validation.LiveProofEligible -or
                $validation.NativeWriterCustodyEstablished -or $validation.PublicationCompletionIssued) { throw 'Wrong original validation facts' }
            foreach($writer in $validation.Writers) {
                if($writer.DrainCount -ne 1 -or $writer.DrainSequence -ne $writer.LastSequence) { throw 'Original writer closure missing' }
            }
        }
        if($mode -ceq 'validation-mutable') {
            $before=$validation | ConvertTo-Json -Depth 8 -Compress
            foreach($writer in $result.Writers) { $writer.WriterId='changed'; $writer.LastSequence=999 }
            foreach($drain in $result.Drains) { $drain.Sequences[0]=999 }
            $result.Writers=@(); $result.Drains=@(); $result.DeferredBatches=@(); $result.WriterGapCount=999
            if(($validation | ConvertTo-Json -Depth 8 -Compress) -cne $before) { throw 'Mutable GUI result changed original validation facts' }
        }
        if($mode -ceq 'validation-copied') {
            $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($validation,$null)
            if($copy.IsCurrent -or $capture.OwnsValidation($copy) -or -not $validation.IsCurrent) { throw 'Copied GUI validation ownership changed' }
        }
        if($mode -ceq 'copied-result') {
            $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($result,$null)
            if($copy.OwnsStreamCapture($capture) -or -not $capture.IsCurrent) { throw 'Copied result ownership changed' }
        }
        if($mode -ceq 'copied-capture') {
            $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($capture,$null)
            if($copy.IsCurrent -or $copy.MatchesOriginalSource($reader) -or -not $capture.IsCurrent) { throw 'Copied capture ownership changed' }
        }
        if($mode -ceq 'output-copied-capture') {
            $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($capture,$null)
            $copy.Dispose(); if($copy.HasRetainedOutputs -or -not $capture.HasRetainedOutputs) { throw 'Copied output capture changed original custody' }
        }
        if($mode -cin @('output-closed','validation-closed-output')) {
            $outputFiles[1].Dispose(); if($capture.IsCurrent -or $binding.IsCurrent) { throw 'Closed output retained source custody' }
        } elseif($mode -ceq 'output-capture-dispose') { $capture.Dispose() }
        elseif($mode -ceq 'output-reader-dispose') { $reader.Dispose() }
        elseif($mode -ceq 'output-owner-dispose') { $owner.Dispose() }
        elseif($mode -ceq 'output-finalizer-dispose') { $finalizer.Dispose() }
        elseif($mode -ceq 'output-finish-repeat') { if($reader.TryFinishProcessingWork()) { throw 'Repeated processing finish succeeded' } }
        elseif($mode -ceq 'output-budget-after') {
            $elapsed=@($work.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
            $elapsed.SetValue($work,[long]([Diagnostics.Stopwatch]::Frequency*16))
            if($binding.IsCurrent) { throw 'Expired output budget retained source custody' }
        } elseif($mode -cin @('cancel-after','output-cancel','validation-cancel')) {
            $cancel=[Threading.EventWaitHandle]::OpenExisting($Name+'.closed')
            try { [void]$cancel.Set(); if($capture.IsCurrent) { throw 'Cancelled processing capture current' }; [void]$cancel.Reset() } finally { $cancel.Dispose() }
        } elseif($mode -cin @('complete-after','validation-complete')) {
            if($finalizer.Complete('','',0,0,$null,$null,$null,$null,$null,$null)) { throw 'Invalid legacy completion succeeded' }
        } else { $binding.Dispose() }
        if(@($outputFiles | Where-Object {$_.CanRead}).Count) { throw 'Owner retirement did not close outputs synchronously' }
        if($validation.IsCurrent -or $capture.IsCurrent -or $reader.IsCurrent -or $binding.IsCurrent -or $proof.IsCurrent) { throw 'Retired processing result revived' }
        foreach($role in @('Retained','Writer','Semantic')) {
            $path=$capture.$role.CreationPath; [IO.File]::AppendAllText($path,'released'); [IO.File]::Move($path,$path+'.released')
        }
    }
    if(@([IO.Directory]::GetFiles($Directory,'frontier-payload-*')).Count) { throw 'Processing cleanup retained source payloads' }
    [IO.File]::AppendAllText($source,'processing-released')
} finally {
    if($null -ne $reader) { $reader.Dispose() }
    $binding.Dispose()
}
