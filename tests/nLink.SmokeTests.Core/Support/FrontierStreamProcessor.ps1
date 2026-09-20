param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
Add-Type -Path (Join-Path $RepoRoot 'tools/GuiSmokeLogLedgerStreamingProcessor.cs')
. (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
function Invoke-StreamCase($InputStream,[string]$Mode,[long]$Lines,[long]$Bookmark,[string[]]$Events) {
    $root=Join-Path $Directory $Mode; [void][IO.Directory]::CreateDirectory($root)
    return [NLink.Tools.GuiSmokeLogLedgerStreamingProcessor]::ProcessFromStream($InputStream,$Lines,$Bookmark,
        (Join-Path $root 'retained.log'),(Join-Path $root 'writer.log'),'run','scenario',[string[]]@(),(Join-Path $root 'semantic.log'),$Events)
}
function Writer-Line([int]$Sequence,[string]$Event) {
    return "event=$Event; acceptance_run_id=run; scenario_run_id=scenario; log_writer_id=writer-a; log_writer_sequence=$Sequence; log_writer_batch_generation=$Sequence; log_writer_batch_first_sequence=$Sequence; log_writer_batch_last_sequence=$Sequence; log_writer_attempt_utc_ticks=$Sequence"
}
$checks=[Collections.Generic.List[string]]::new()
foreach($mode in @('valid','bookmark','mixed-newlines','boundary','gap','unknown','empty-semantic','max-line','catalog','event-count-boundary','metadata-boundary')) {
    $lines=@((Writer-Line 1 'operational_log_writer_started'),(Writer-Line 2 'selected'),(Writer-Line 3 'operational_log_writer_drained'))
    if($mode -ceq 'gap') { $lines[1]=Writer-Line 4 'selected' }
    if($mode -ceq 'unknown') { $lines=@('event=unknown')+$lines }
    if($mode -ceq 'boundary') { $lines=@(('x'*32767),('y'*32768),'',('é'*32769))+$lines }
    if($mode -ceq 'max-line') { $lines=@(('z'*262144))+$lines }
    $bookmark=if($mode -ceq 'bookmark') {1L} else {0L}
    $events=if($mode -ceq 'empty-semantic') {[string[]]@('absent')} else {[string[]]@('selected')}
    if($mode -ceq 'catalog') {$events=[string[]]@(Get-GuiSmokeSemanticEvidenceEventNames)}
    if($mode -ceq 'event-count-boundary') {$events=[string[]]@(1..2048 | ForEach-Object {'e'+$_})}
    if($mode -ceq 'metadata-boundary') {$events=[string[]]@(1..64 | ForEach-Object {'x'*1024})}
    $text=if($mode -ceq 'mixed-newlines') {$lines[0]+"`r"+$lines[1]+"`n"+$lines[2]+"`r`n"} else {($lines -join "`r`n")+"`r`n"}
    $bytes=[Text.UTF8Encoding]::new($false,$true).GetBytes($text)
    $input=[IO.MemoryStream]::new($bytes,$false)
    try {
        $result=Invoke-StreamCase $input $mode $lines.Count $bookmark $events
        $capture=$result.StreamCapture
        if($null -eq $capture -or -not $result.OwnsStreamCapture($capture) -or -not $capture.MatchesOriginalSource($input) -or $null -ne $result.NativeCapture) { throw "Wrong stream provenance: $mode" }
        $path=Join-Path $Directory ($mode+'.source.log'); [IO.File]::WriteAllBytes($path,$bytes)
        $baselineRoot=Join-Path $Directory ($mode+'-baseline'); [void][IO.Directory]::CreateDirectory($baselineRoot)
        $baseline=[NLink.Tools.GuiSmokeLogLedgerStreamingProcessor]::Process($path,$lines.Count,$bookmark,
            (Join-Path $baselineRoot 'retained.log'),(Join-Path $baselineRoot 'writer.log'),'run','scenario',[string[]]@(),(Join-Path $baselineRoot 'semantic.log'),$events)
        $actual=$result | Select-Object * -ExcludeProperty NativeCapture,StreamCapture | ConvertTo-Json -Depth 10 -Compress
        $expected=$baseline | Select-Object * -ExcludeProperty NativeCapture,StreamCapture | ConvertTo-Json -Depth 10 -Compress
        if($actual -cne $expected) { throw "Stream processing changed legacy result: $mode" }
        foreach($role in @('Retained','Writer','Semantic')) {
            $output=$capture.$role; $origins=$capture.($role+'Origins'); $outputLines=[IO.File]::ReadAllLines($output.CreationPath)
            if($output.LineCount -ne $origins.Count -or $outputLines.Length -ne $origins.Count) { throw "Origin count mismatch: $mode/$role" }
            for($i=0;$i -lt $origins.Count;$i++) { if($outputLines[$i] -cne $lines[$origins[$i]-1]) { throw "Physical origin mismatch: $mode/$role/$i" } }
            $hash=[Security.Cryptography.SHA256]::Create()
            try { $digest=[BitConverter]::ToString($hash.ComputeHash([IO.File]::ReadAllBytes($output.CreationPath))).Replace('-','').ToLowerInvariant() } finally { $hash.Dispose() }
            if($digest -cne $output.Sha256) { throw 'Output hash mismatch' }
        }
        if($capture.LiveProofEligible -or $capture.NativeWriterCustodyEstablished -or $capture.PublicationCompletionIssued) { throw 'Stream provenance enabled authority' }
        $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($result,$null)
        if($copy.OwnsStreamCapture($capture)) { throw 'Copied processor result retained ownership' }
        $copy=[object].GetMethod('MemberwiseClone',$flags).Invoke($capture,$null)
        if($copy.IsCurrent -or $copy.MatchesOriginalSource($input)) { throw 'Copied stream capture retained ownership' }
        $errorFlag=$capture.HasWriterValidationErrors; $result.WriterGapCount=0; $result.WriterIdentityMismatchCount=0
        if($capture.HasWriterValidationErrors -ne $errorFlag) { throw 'Mutable result changed error snapshot' }
        $input.Dispose(); if($capture.IsCurrent) { throw 'Closed source retained provenance' }
        $checks.Add($mode)
    } finally { $input.Dispose() }
}
foreach($mode in @('extra','short','prior-read','invalid-utf8','line-limit','origin-limit','metadata-limit','record-limit','event-count-over','metadata-over')) {
    $text="event=selected`n"; $count=1L; $events=[string[]]@('selected')
    if($mode -ceq 'extra') { $text+=$text }
    if($mode -ceq 'short') { $count=2 }
    if($mode -ceq 'line-limit') { $text=('x'*262145)+"`n" }
    if($mode -ceq 'origin-limit') { $text=$text*131073; $count=131073 }
    if($mode -ceq 'record-limit') { $count=262145 }
    if($mode -ceq 'metadata-limit') { $events=[string[]]@(('a'*1025)) }
    if($mode -ceq 'event-count-over') {$events=[string[]]@(1..2049 | ForEach-Object {'e'+$_})}
    if($mode -ceq 'metadata-over') {$events=[string[]]@(@(1..64 | ForEach-Object {'x'*1024})+'x')}
    $bytes=if($mode -ceq 'invalid-utf8') {[byte[]]@(255,10)} else {[Text.Encoding]::UTF8.GetBytes($text)}
    $input=[IO.MemoryStream]::new($bytes,$false); $rejected=$false
    try {
        if($mode -ceq 'prior-read') { [void]$input.ReadByte() }
        try { [void](Invoke-StreamCase $input $mode $count 0 $events) } catch { $rejected=$true; if($mode -ceq 'origin-limit' -and $_.ToString() -notmatch 'stream_source_origin_limit') { throw } }
        if(-not $rejected) { throw "Stream failure admitted: $mode" }
        if($mode -cin @('event-count-over','metadata-over') -and $input.Position -ne 0) {throw 'Metadata rejection read source bytes'}
        $checks.Add($mode)
    } finally { $input.Dispose() }
}
'frontier-stream-processor='+($checks.ToArray() | ConvertTo-Json -Compress)
