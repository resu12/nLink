param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$type=[NLink.Tools.FrontierRecoveryMarker].Assembly.GetType('NLink.Tools.FrontierRecoveryMarkerScan')
Add-Type 'using System; public static class MarkerTestCheck { public static int Calls, Limit = Int32.MaxValue; public static void Check() { if (++Calls > Limit) throw new OperationCanceledException(); } }'
$check=[Action][Delegate]::CreateDelegate([Action],[MarkerTestCheck].GetMethod('Check'))
$hash='a'*64
function Marker([string]$event='deferred',[int]$generation=1,[string]$writer='writer') {
    "event=operational_log_write_$event; acceptance_run_id=run; scenario_run_id=scenario; log_writer_id=$writer; batch_generation=$generation; first_sequence=2; last_sequence=3; line_count=2; payload_sha256=$hash; attempt_utc_ticks=4; attempt_count=1; failure_code=none"
}
function Scan([byte[]]$bytes) {
    $scan=$type.GetConstructors($flags)[0].Invoke([object[]]@($check))
    $stream=[IO.MemoryStream]::new($bytes,$false)
    try { [void]$type.GetMethod('Read',$flags).Invoke($scan,[object[]]@($stream,0,'run','scenario')) }
    finally { $stream.Dispose() }
    return $scan
}
function TextScan([string]$text) { Scan ([Text.Encoding]::UTF8.GetBytes($text)) }
function Paired($scan) { [bool]$type.GetMethod('PairsMatch',$flags).Invoke($scan,$null) }
function Reject([string]$name,[scriptblock]$action) {
    $failed=$false; try { & $action | Out-Null } catch { $failed=$true }
    if(-not $failed) { throw "Accepted invalid parser input: $name" }
}
function Unpaired([string]$name,[string]$text) {
    $scan=TextScan $text
    if(Paired $scan) { throw "Accepted unmatched tuple: $name" }
}
$d=Marker; $r=Marker 'recovered'
$scan=TextScan "$d`n$r"
if(-not (Paired $scan)) { throw 'Valid marker pair rejected' }
$records=$type.GetField('Markers',$flags).GetValue($scan)
if($records.Count -ne 2 -or $records[0].SourceLine -ne 1 -or $records[1].SourceLine -ne 2 -or $records[0].StreamIndex -ne 0) { throw 'Marker origins lost' }
foreach($text in @('',([string][char]0xfeff), "$d`r$r`r", "$d`n$r`n", "$d`r`n$r`r`n", (([string][char]0xfeff)+"[prefix] $d`n[p1] [p2] $($r.ToUpperInvariant().Replace('=RUN','=run').Replace('=SCENARIO','=scenario').Replace('=WRITER','=writer'))"))) {
    if(-not (Paired (TextScan $text))) { throw 'Framing/case/empty control rejected' }
}
foreach($fault in @(('event=x; '+$d),($d+'; EVENT=other'),($d+'; failure_code=other'),($d+'; arbitrary=1; arbitrary=2'),'event=operational_log_write_failed; failure_code=io')) {
    Reject 'duplicate/terminal' { TextScan $fault }
}
foreach($pair in @(@('batch_generation=1','batch_generation=0'),@('first_sequence=2','first_sequence=-2'),@('last_sequence=3','last_sequence=1'),@('line_count=2','line_count=3'),@('attempt_utc_ticks=4','attempt_utc_ticks=3155378976000000000'),@('attempt_count=1','attempt_count=x'),@(('payload_sha256='+$hash),'payload_sha256=x'),@('failure_code=none','failure_code='),@('acceptance_run_id=run','acceptance_run_id=other'),@('scenario_run_id=scenario','scenario_run_id=other'))) {
    $fault=$d.Replace($pair[0],$pair[1]); Reject 'field validation' { TextScan $fault }
}
Reject 'missing field' { TextScan ($d.Replace('attempt_count=1; ','')) }
Reject 'duplicate deferred' { TextScan "$d`n$d`n$r" }
Reject 'duplicate recovered' { TextScan "$d`n$r`n$r" }
Unpaired 'missing' $d; Unpaired 'orphan' $r
foreach($pair in @(@('batch_generation=1','batch_generation=2'),@('log_writer_id=writer','log_writer_id=other'),@('attempt_utc_ticks=4','attempt_utc_ticks=5'),@($hash,('b'*64)))) {
    Unpaired 'different tuple' "$d`n$($r.Replace($pair[0],$pair[1]))"
}
Reject 'bad UTF8' { Scan ([byte[]]@(0xc3,0x28)) }
Reject 'truncated UTF8' { Scan ([byte[]]@(0xe2,0x82)) }
Reject 'UTF16 BOM' { Scan ([Text.Encoding]::Unicode.GetPreamble()+[Text.Encoding]::Unicode.GetBytes($d)) }
$split=('x'*32767)+[char]0x17e+"`n$d`n$r"
if(-not (Paired (TextScan $split))) { throw 'Split UTF8 character rejected' }
$split=('x'*32765)+[char]::ConvertFromUtf32(0x1f600)+('x'*32767)+"`n$d`n$r"
if(-not (Paired (TextScan $split))) { throw 'Split four-byte UTF8 character rejected' }
[void](TextScan ('x'*262144)); Reject 'line over bound' { TextScan ('x'*262145) }
$scan=TextScan ("`n"*262144)
if($type.GetField('SourceLines',$flags).GetValue($scan) -ne 262144) { throw 'Exact line bound rejected' }
Reject 'line count over bound' { TextScan ("`n"*262145) }
$pairs=@(); for($i=1;$i -le 64;$i++) { $pairs+=(Marker 'deferred' $i); $pairs+=(Marker 'recovered' $i) }
if(-not (Paired (TextScan ($pairs -join "`n")))) { throw '64-pair bound rejected' }
Reject '65th pair' { TextScan (($pairs+@(Marker 'deferred' 65; Marker 'recovered' 65)) -join "`n") }
Reject '65 deferred batches' { TextScan ((1..65 | ForEach-Object { Marker 'deferred' $_ }) -join "`n") }
[void](TextScan ($d.Replace('failure_code=none','failure_code='+('x'*1024))))
Reject 'field over bound' { TextScan ($d.Replace('failure_code=none','failure_code='+('x'*1025))) }
# Real marker strings at the exact aggregate bound, then one code unit over.
$scan=TextScan ($pairs -join "`n")
$remaining=65536-[int]$type.GetField('MetadataCodeUnits',$flags).GetValue($scan)
$bounded=@()
for($i=0;$i -lt $pairs.Count;$i++) {
    $extra=[int][Math]::Floor($remaining/($pairs.Count-$i)); $remaining-=$extra
    $bounded+=$pairs[$i].Replace('failure_code=none','failure_code='+('x'*(4+$extra)))
}
$scan=TextScan ($bounded -join "`n")
if($type.GetField('MetadataCodeUnits',$flags).GetValue($scan) -ne 65536) { throw 'Exact metadata bound rejected' }
$bounded[0]+='x'
Reject 'metadata over bound' { TextScan ($bounded -join "`n") }
[MarkerTestCheck]::Limit=[MarkerTestCheck]::Calls+3
Reject 'work cancellation' { TextScan "$d`n$r" }
[MarkerTestCheck]::Limit=[int]::MaxValue
$scan=TextScan "$d`n$r"
$dto=[NLink.Tools.FrontierLedger.GuiSmokeLedgerBatchResult]::new()
$dto.WriterId='writer'; $dto.BatchGeneration=1; $dto.BatchFirstSequence=2; $dto.BatchLastSequence=3
$dto.MinSequence=2; $dto.MaxSequence=3; $dto.LineCount=2; $dto.AttemptUtcTicks=4; $dto.TupleValid=$true; $dto.PayloadSha256=$hash
$ctor=[NLink.Tools.FrontierLedger.GuiSmokeLedgerValidationBatch].GetConstructors($flags)[0]
function BatchMatch($values) { [bool]$type.GetMethod('Matches',$flags).Invoke($scan,[object[]]@(,[NLink.Tools.FrontierLedger.GuiSmokeLedgerValidationBatch[]]$values)) }
$batch=$ctor.Invoke([object[]]@($dto))
if(-not (BatchMatch @($batch)) -or (BatchMatch @()) -or (BatchMatch @($batch,$batch))) { throw 'Exact batch set check failed' }
foreach($property in @('WriterId','BatchGeneration','BatchFirstSequence','BatchLastSequence','MinSequence','MaxSequence','LineCount','AttemptUtcTicks','TupleValid','PayloadSha256')) {
    $original=$dto.$property
    if($original -is [bool]) { $dto.$property=$false } elseif($original -is [string]) { $dto.$property='wrong' } else { $dto.$property=$original+1 }
    $bad=$ctor.Invoke([object[]]@($dto)); $dto.$property=$original
    if(BatchMatch @($bad)) { throw "Mismatched batch field accepted: $property" }
}
'frontier-recovery-markers=bounded-parser-and-tuple-checks-passed'
