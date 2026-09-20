param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalGuiWriterRequest.ps1')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$processor=[NLink.Tools.FrontierLedger.GuiSmokeLogLedgerStreamingProcessor].GetMethod('ProcessOwnedCursor',[Reflection.BindingFlags]'Static,NonPublic')
$type=[NLink.Tools.FrontierLedger.GuiSmokeLedgerPrefixWriter]
if($type.GetConstructors().Length -or @($type.GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) { throw 'Mutable/public prefix construction exposed' }
function Writer-Line([int]$Sequence,[string]$Event,[string]$Id='writer-a') {
    "event=$Event; acceptance_run_id=run; scenario_run_id=scenario; log_writer_id=$Id; log_writer_sequence=$Sequence; log_writer_batch_generation=$Sequence; log_writer_batch_first_sequence=$Sequence; log_writer_batch_last_sequence=$Sequence; log_writer_attempt_utc_ticks=$Sequence"
}
$checks=[Collections.Generic.List[string]]::new()
foreach($mode in @('prefix','empty-prefix','bookmark-eof','interleaved','bare-cr','bom','boundary','max-line','line-over',
    'invalid-utf8','truncated-utf8','utf16','unterminated','short','extra','prefix-limit','mutable-result','readonly-prefix','cancel')) {
    $lines=@((Writer-Line 1 'operational_log_writer_started'),(Writer-Line 2 'selected'),(Writer-Line 3 'operational_log_writer_drained'))
    $bookmark=1L; $prefixCount=1
    if($mode -ceq 'empty-prefix') { $bookmark=0; $prefixCount=0 }
    if($mode -ceq 'bookmark-eof') { $bookmark=3 }
    if($mode -ceq 'interleaved') { $lines=@($lines[0],(Writer-Line 1 'operational_log_writer_started' 'writer-b'),$lines[1],(Writer-Line 2 'operational_log_writer_drained' 'writer-b'),$lines[2]); $bookmark=2; $prefixCount=2 }
    if($mode -ceq 'bare-cr') { $lines=@("ordinary`rcarriage")+$lines; $prefixCount=0 }
    if($mode -ceq 'boundary') { $lines=@((('é'*16383)+'x'),('x'*32767),('é'*32769))+$lines; $bookmark=3; $prefixCount=0 }
    if($mode -cin @('max-line','line-over')) { $lines=@(('z'*(262144+[int]($mode -ceq 'line-over'))))+$lines; $prefixCount=0 }
    if($mode -ceq 'prefix-limit') {
        $lines=@(); for($i=0;$i -lt 65;$i++) { $lines+=Writer-Line 1 'operational_log_writer_started' ('w'+$i) }; $bookmark=65
    }
    $text=($lines -join "`r`n")+"`r`n"
    if($mode -ceq 'bom') { $text=[string][char]0xfeff+$text }
    if($mode -ceq 'unterminated') { $text=$text.TrimEnd([char[]]"`r`n") }
    $bytes=[Text.UTF8Encoding]::new($false,$true).GetBytes($text)
    if($mode -ceq 'invalid-utf8') { $bytes=[byte[]]@(0xff,10) }
    if($mode -ceq 'truncated-utf8') { $bytes=[byte[]]@(0xe2,0x82,10) }
    if($mode -ceq 'utf16') { $bytes=[byte[]]@([Text.Encoding]::Unicode.GetPreamble()+[Text.Encoding]::Unicode.GetBytes($text)) }
    $count=[long]$lines.Count
    if($mode -cin @('invalid-utf8','truncated-utf8')) { $count=1 }
    if($mode -ceq 'short') { $count++ }
    if($mode -ceq 'extra') { $count-- }
    $root=Join-Path $Directory $mode; [void][IO.Directory]::CreateDirectory($root)
    $input=[IO.MemoryStream]::new($bytes,$false); $lifetime=[Threading.CancellationTokenSource]::new(); $result=$null; $rejected=$false; $failure=''
    try {
        if($mode -ceq 'cancel') { $lifetime.Cancel() }
        try {
            $result=$processor.Invoke($null,[object[]]@($input,$count,$bookmark,[IO.Path]::Combine($root,'retained.tmp'),[IO.Path]::Combine($root,'writer.tmp'),
                'run','scenario',[string[]]@(),[IO.Path]::Combine($root,'semantic.tmp'),[string[]]@(),$lifetime.Token))
        } catch { $rejected=$true; $failure=$_.Exception.ToString() }
        $invalid=$mode -cin @('line-over','invalid-utf8','truncated-utf8','utf16','unterminated','short','extra','prefix-limit','cancel')
        if($rejected -ne $invalid) { throw "Wrong cursor processor outcome: $mode rejected=$rejected $failure" }
        if($invalid) { $checks.Add($mode); continue }
        $capture=$result.StreamCapture; $validation=$capture.Validation; $prefix=$validation.InitialWriters
        if($null -eq $validation -or -not $validation.IsCurrent -or $prefix.Count -ne $prefixCount -or $validation.SourceLineVisitCount -ne $count) { throw "Wrong prefix capture: $mode" }
        $starts=$capture.GetType().GetField('CursorLineStarts',$flags).GetValue($capture)
        $expected=[Collections.Generic.List[long]]::new(); $start=0L
        for($i=0;$i -lt $bytes.Length;$i++) { if($bytes[$i] -eq 10) { $expected.Add($start); $start=$i+1L } }
        if($starts.Count -ne $expected.Count) { throw 'LF index count mismatch' }
        for($i=0;$i -lt $expected.Count;$i++) { if($starts[$i] -ne $expected[$i]) { throw 'LF byte index mismatch' } }
        if($prefixCount) {
            $expectedLast=if($mode -ceq 'bookmark-eof') {3} else {1}
            if($prefix[0].FirstSequence -ne 1 -or $prefix[0].LastSequence -ne $expectedLast -or $prefix[0].ObservedCount -ne $expectedLast) { throw 'Wrong prefix sequence facts' }
        }
        $before=$prefix | ConvertTo-Json -Depth 5 -Compress
        foreach($writer in $result.Writers) { $writer.WriterId='mutated'; $writer.LastSequence=999; $writer.ObservedCount=999 }
        $result.Writers=@(); $result.SourceLineVisitCount=0
        if(($prefix | ConvertTo-Json -Depth 5 -Compress) -cne $before) { throw 'Public result mutated prefix snapshot' }
        $blocked=$false; try { ([Collections.IList]$prefix).Clear() } catch { $blocked=$true }
        if(-not $blocked -or $capture.LiveProofEligible -or $capture.PublicationCompletionIssued) { throw 'Prefix mutated or enabled authority' }
        $checks.Add($mode)
    } finally { if($null -ne $result) { $result.StreamCapture.Dispose() }; $input.Dispose(); $lifetime.Dispose() }
}
'frontier-cursor-processor='+($checks.ToArray() | ConvertTo-Json -Compress)
