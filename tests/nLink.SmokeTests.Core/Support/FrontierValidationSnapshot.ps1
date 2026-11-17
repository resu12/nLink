param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -Path (Join-Path $RepoRoot 'tools/GuiSmokeLogLedgerStreamingProcessor.cs')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
foreach($type in @([NLink.Tools.GuiSmokeLedgerValidationSnapshot],[NLink.Tools.GuiSmokeLedgerValidationWriter],[NLink.Tools.GuiSmokeLedgerValidationBatch])) {
    if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) { throw 'Public validation fabrication exposed' }
}
function Writer-Line([int]$Sequence,[string]$Event,[string]$Writer='writer-a') {
    return "event=$Event; acceptance_run_id=run; scenario_run_id=scenario; log_writer_id=$Writer; log_writer_sequence=$Sequence; log_writer_batch_generation=$Sequence; log_writer_batch_first_sequence=$Sequence; log_writer_batch_last_sequence=$Sequence; log_writer_attempt_utc_ticks=$Sequence"
}
function Process-Validation($InputStream,[string]$Root,[long]$Count,[long]$Bookmark,[string[]]$Keys,$Lifetime) {
    [void][IO.Directory]::CreateDirectory($Root)
    return [NLink.Tools.GuiSmokeLogLedgerStreamingProcessor]::ProcessFromStreamWithRetainedOutputs(
        $InputStream,$Count,$Bookmark,(Join-Path $Root 'retained.log'),(Join-Path $Root 'writer.log'),
        'run','scenario',$Keys,(Join-Path $Root 'semantic.log'),[string[]]@('operational_log_writer_drained'),$Lifetime)
}
$checks=[Collections.Generic.List[string]]::new()
foreach($mode in @('valid','missing-drain','duplicate-drain','nonterminal-drain','start-sequence','gap','duplicate-sequence','scope-mismatch',
    'chronology-inversion','chronology-collision','invalid-batch','no-writers','bookmark','multi-writer','writer-64','writer-65',
    'writer-id-limit','metadata-max','metadata-limit','batches-64','batches-65','copied-snapshot','foreign-snapshot','closed-output','cancel','source-disposed')) {
    $lines=@((Writer-Line 1 'operational_log_writer_started'),(Writer-Line 2 'selected'),(Writer-Line 3 'operational_log_writer_drained'))
    $keys=[string[]]@('8:writer-a:2'); $bookmark=0L
    if($mode -ceq 'missing-drain') { $lines[2]=Writer-Line 3 'selected' }
    if($mode -ceq 'duplicate-drain') { $lines[1]=Writer-Line 2 'operational_log_writer_drained' }
    if($mode -ceq 'nonterminal-drain') { $lines[1]=Writer-Line 2 'operational_log_writer_drained'; $lines[2]=Writer-Line 3 'selected' }
    if($mode -ceq 'gap') { $lines[1]=Writer-Line 3 'selected'; $lines[2]=Writer-Line 4 'operational_log_writer_drained' }
    if($mode -ceq 'start-sequence') { $lines=@((Writer-Line 2 'operational_log_writer_started'),(Writer-Line 3 'selected'),(Writer-Line 4 'operational_log_writer_drained')) }
    if($mode -ceq 'duplicate-sequence') { $lines[1]=$lines[1].Replace('log_writer_sequence=2','log_writer_sequence=1') }
    if($mode -ceq 'scope-mismatch') { $lines[0]=$lines[0].Replace('scenario_run_id=scenario','scenario_run_id=foreign') }
    if($mode -ceq 'chronology-inversion') { $lines[2]=$lines[2].Replace('log_writer_attempt_utc_ticks=3','log_writer_attempt_utc_ticks=1') }
    if($mode -ceq 'chronology-collision') { $lines[2]=$lines[2].Replace('log_writer_attempt_utc_ticks=3','log_writer_attempt_utc_ticks=2') }
    if($mode -ceq 'invalid-batch') { $lines[1]=$lines[1].Replace('log_writer_batch_last_sequence=2','log_writer_batch_last_sequence=1') }
    if($mode -ceq 'no-writers') { $lines=@('event=ordinary'); $bookmark=1L; $keys=[string[]]@() }
    if($mode -ceq 'bookmark') { $bookmark=2L }
    if($mode -ceq 'multi-writer') { $lines+=@((Writer-Line 1 'operational_log_writer_started' 'writer-b'),(Writer-Line 2 'operational_log_writer_drained' 'writer-b')) }
    if($mode -cin @('writer-64','writer-65','writer-id-limit','metadata-max','metadata-limit')) {
        $count=if($mode -ceq 'writer-id-limit') {1} elseif($mode -ceq 'writer-65') {65} else {64}
        $lines=@(); $keys=[string[]]@()
        for($i=0;$i -lt $count;$i++) {
            $id='writer-'+$i
            if($mode -ceq 'writer-id-limit') { $id='w'*1025 }
            if($mode -ceq 'metadata-limit') { $id=$id.PadRight(1024,'w') }
            if($mode -ceq 'metadata-max') { $id=$id.PadRight(1013,'w') }
            $lines+=@((Writer-Line 1 'operational_log_writer_started' $id),(Writer-Line 2 'operational_log_writer_drained' $id))
        }
    }
    if($mode -cin @('batches-64','batches-65')) {
        $count=if($mode -ceq 'batches-64') {64} else {65}
        $lines=@(); $keys=[string[]]@()
        for($i=1;$i -le $count;$i++) { $event=if($i -eq 1) {'operational_log_writer_started'} else {'selected'}; $lines+=Writer-Line $i $event; $keys+='8:writer-a:'+$i }
        $lines+=Writer-Line ($count+1) 'operational_log_writer_drained'
    }
    $bytes=[Text.Encoding]::UTF8.GetBytes(($lines -join "`n")+"`n")
    $input=[IO.MemoryStream]::new($bytes,$false); $lifetime=[Threading.CancellationTokenSource]::new()
    $capture=$null; $other=$null
    try {
        $rejected=$false
        try { $result=Process-Validation $input (Join-Path $Directory $mode) $lines.Count $bookmark $keys $lifetime.Token }
        catch { if($mode -cne 'batches-65' -or $_.ToString() -notmatch 'stream_source_bounds') { throw }; $rejected=$true }
        if($mode -ceq 'batches-65') { if(-not $rejected) { throw 'Oversized batch selection accepted' }; $checks.Add($mode); continue }
        $capture=$result.StreamCapture; $snapshot=$capture.Validation
        if(-not $capture.HasRetainedOutputs) { throw 'Validation capture lost original outputs' }
        if($mode -cin @('writer-65','writer-id-limit','metadata-limit')) {
            if($null -ne $snapshot -or $capture.OwnsValidation($null)) { throw 'Unbounded validation snapshot created' }
            $checks.Add($mode); continue
        }
        if($null -eq $snapshot -or -not $capture.OwnsValidation($snapshot) -or -not $snapshot.IsCurrent) { throw 'Original validation snapshot missing' }
        foreach($property in @('SourceLineVisitCount','WriterGapCount','WriterDuplicateCount','WriterIdentityMismatchCount',
            'ChronologyInversionCount','ChronologyCollisionCount','ChronologyBatchInvalid')) {
            if($snapshot.$property -ne $result.$property) { throw "Validation scalar mismatch: $mode/$property" }
        }
        if($snapshot.Writers.Count -ne $result.Writers.Length -or $snapshot.DeferredBatches.Count -ne $result.DeferredBatches.Length -or
            $snapshot.MetadataCodeUnits -gt 65536) { throw 'Validation collection/count mismatch' }
        if($mode -ceq 'metadata-max' -and $snapshot.MetadataCodeUnits -ne 65536) { throw 'Exact metadata boundary not exercised' }
        for($i=0;$i -lt $snapshot.Writers.Count;$i++) {
            foreach($property in @('WriterId','AcceptanceRunId','ScenarioRunId','FirstSequence','LastSequence','ObservedCount')) {
                if($snapshot.Writers[$i].$property -cne $result.Writers[$i].$property) { throw 'Original writer fact changed' }
            }
            $drain=@($result.Drains | Where-Object {$_.WriterId -ieq $snapshot.Writers[$i].WriterId})
            $drainCount=if($drain.Count) {$drain[0].Sequences.Length} else {0}
            $sequence=if($drainCount -eq 1) {$drain[0].Sequences[0]} else {0L}
            if($snapshot.Writers[$i].DrainCount -ne $drainCount -or $snapshot.Writers[$i].DrainSequence -ne $sequence) { throw 'Original drain facts changed' }
        }
        for($i=0;$i -lt $snapshot.DeferredBatches.Count;$i++) {
            if(($snapshot.DeferredBatches[$i] | ConvertTo-Json -Compress) -cne ($result.DeferredBatches[$i] | ConvertTo-Json -Compress)) { throw 'Original deferred batch facts changed' }
        }
        $invalid=$mode -cin @('missing-drain','duplicate-drain','nonterminal-drain','start-sequence','gap','duplicate-sequence','scope-mismatch',
            'chronology-inversion','chronology-collision','invalid-batch','no-writers')
        if($snapshot.HasWriterClosureErrors -ne $invalid -or $snapshot.HasProcessorErrors -ne $capture.HasWriterValidationErrors) { throw "Wrong writer closure check: $mode" }
        if($mode -cin @('missing-drain','duplicate-drain','nonterminal-drain') -and
            ($snapshot.HasProcessorErrors -or $snapshot.WriterDrainMissingCount -ne 1)) { throw 'Drain failure conflated with processor errors' }
        if($mode -ceq 'start-sequence' -and $snapshot.WriterBoundaryMismatchCount -ne 1) { throw 'Writer first-sequence boundary was not checked' }
        if($snapshot.LiveProofEligible -or $snapshot.NativeWriterCustodyEstablished -or $snapshot.PublicationCompletionIssued) { throw 'Validation facts enabled approval' }
        if($mode -ceq 'copied-snapshot') {
            $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($snapshot,$null)
            $captureCopy=[object].GetMethod('MemberwiseClone',$flags).Invoke($capture,$null)
            if($copy.IsCurrent -or $capture.OwnsValidation($copy) -or $captureCopy.OwnsValidation($snapshot)) { throw 'Copied validation ownership admitted' }
        }
        if($mode -ceq 'foreign-snapshot') {
            $otherInput=[IO.MemoryStream]::new($bytes,$false)
            $other=Process-Validation $otherInput (Join-Path $Directory 'foreign-control') $lines.Count $bookmark $keys ([Threading.CancellationToken]::None)
            if($capture.OwnsValidation($other.StreamCapture.Validation) -or $other.StreamCapture.OwnsValidation($snapshot)) { throw 'Foreign snapshot accepted' }
        }
        $before=$snapshot | ConvertTo-Json -Depth 8 -Compress
        foreach($writer in $result.Writers) { $writer.WriterId='changed'; $writer.LastSequence=999; $writer.ObservedCount=999 }
        foreach($drain in $result.Drains) { if($drain.Sequences.Length) {$drain.Sequences[0]=999}; $drain.Sequences=[long[]]@() }
        foreach($batch in $result.DeferredBatches) { $batch.PayloadSha256='changed'; $batch.TupleValid=-not $batch.TupleValid; $batch.AttemptUtcTicks=0 }
        $result.Writers=@(); $result.Drains=@(); $result.DeferredBatches=@()
        $result.SourceLineVisitCount=0; $result.WriterGapCount=0; $result.WriterDuplicateCount=0; $result.WriterIdentityMismatchCount=0
        $result.ChronologyBatchInvalid=$false; $result.ChronologyInversionCount=0; $result.ChronologyCollisionCount=0
        if(($snapshot | ConvertTo-Json -Depth 8 -Compress) -cne $before) { throw 'Mutable result changed original validation snapshot' }
        $blocked=$false; try { ([Collections.IList]$snapshot.Writers).Clear() } catch { $blocked=$true }
        if(-not $blocked) { throw 'Validation writer collection remained mutable' }
        if($mode -ceq 'closed-output') {
            $lease=$capture.GetType().GetField('outputs',$flags).GetValue($capture)
            $lease.GetType().GetField('files',$flags).GetValue($lease)[1].Dispose()
        } elseif($mode -ceq 'cancel') { $lifetime.Cancel() }
        elseif($mode -ceq 'source-disposed') { $input.Dispose() }
        else { $capture.Dispose() }
        if($snapshot.IsCurrent -or $capture.IsCurrent) { throw 'Retired validation facts remained current' }
        $checks.Add($mode)
    } finally {
        if($null -ne $other) { $other.StreamCapture.Dispose() }
        if($null -ne $capture) { $capture.Dispose() }
        $input.Dispose(); $lifetime.Dispose()
    }
}
'frontier-validation-snapshot='+($checks.ToArray() | ConvertTo-Json -Compress)
