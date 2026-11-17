param($Borrower,$Payload,$Context,$Completion,$Custody,[string]$ArtifactDir)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$case=$env:NLINK_TEST_SELECTED_TERMINAL_CASE
if($case -cnotin @('active','dispose','copy','scope','coordinate','swapped')) {throw 'selected_terminal_case'}
function Field($value,[string]$name) {return $value.GetType().GetField($name,[Reflection.BindingFlags]'Instance,NonPublic')}
$clone=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic')
$index=(Field $Borrower 'index').GetValue($Borrower)
$member=(Field $Borrower 'membership').GetValue($Borrower)
$files=$Context.SourceOwner.TerminalFilesForTest; $clock=$Context.Work
$native=(Field $files 'files').GetValue($files); $binding=(Field $native 'transfer').GetValue($native)
$terminals=[object[]]$index.EventsByName['transfer_terminal'].ToArray()
if($terminals.Length -ne 2) {throw 'selected_terminal_original_count'}
$beforeReads=$Context.WriterReader.LinesRead; $beforeVisits=$Borrower.CumulativeRecordVisits; $beforeUnits=$Borrower.CumulativeCodeUnits
$pre=@('borrower','payload','missing_payload','missing_terminals','one_terminal','three_terminals','stopped_clock','record_budget','code_unit_budget')
foreach($control in $pre) {
    $b=$Borrower; $p=$Payload; $rows=$terminals; $restore={}
    switch($control) {
        'borrower' {$b=$clone.Invoke($b,@())} 'payload' {$p=$clone.Invoke($p,@())}
        'missing_payload' {$p=$null} 'missing_terminals' {$rows=$null}
        'one_terminal' {$rows=[object[]]@($terminals[0])} 'three_terminals' {$rows=[object[]]@($terminals[0],$terminals[1],$terminals[0])}
        'stopped_clock' {$clock.Stop();$restore={$clock.Start()}}
        'record_budget' {$field=Field $member 'borrowerPriorRecords';$prior=$field.GetValue($member);$field.SetValue($member,4096L);$restore={$field.SetValue($member,$prior)}}
        'code_unit_budget' {$field=Field $member 'borrowerPriorUnits';$prior=$field.GetValue($member);$field.SetValue($member,[NLink.Tools.LocalPublicationInputLimits]::ProducerCodeUnits);$restore={$field.SetValue($member,$prior)}}
    }
    try {if($null -ne $b.TryJoinSelectedTerminalsForTest($p,$rows)) {throw ('selected_terminal_precontrol:'+ $control)}} finally {& $restore}
}
if($Borrower.CumulativeRecordVisits -ne $beforeVisits -or $Borrower.CumulativeCodeUnits -ne $beforeUnits) {throw 'selected_terminal_precontrol_spent'}
$candidate=$terminals; $restore={}
switch($case) {
    'copy' {$candidate=[object[]]@($terminals[0].PSObject.Copy(),$terminals[1])}
    'scope' {$field=Field $binding 'Transfer';$prior=$field.GetValue($binding);$field.SetValue($binding,'fte1-'+('0'*24));$restore={$field.SetValue($binding,$prior)}}
    'coordinate' {$field=Field $binding 'InboundLine';$prior=$field.GetValue($binding);$field.SetValue($binding,4096);$restore={$field.SetValue($binding,$prior)}}
    'swapped' {
        $field=Field $binding 'InboundLine';$second=Field $binding 'OutboundLine';$prior=$field.GetValue($binding);$other=$second.GetValue($binding)
        $field.SetValue($binding,$other);$second.SetValue($binding,$prior);$restore={$field.SetValue($binding,$prior);$second.SetValue($binding,$other)}
    }
}
try {$result=$Borrower.TryJoinSelectedTerminalsForTest($Payload,$candidate)} finally {& $restore}
$expected=$case -cin @('active','dispose'); $joined=$null -ne $result
if($joined -ne $expected -or ($joined -and -not $result.IsCurrent)) {throw 'selected_terminal_join_outcome'}
$post=@(); $parserControls=0; $inbound=$null; $outbound=$null
if($joined) {
    $inbound=$result.InboundOrigin; $outbound=$result.OutboundOrigin
    $row=$terminals[0]; $at=[Array]::BinarySearch($Context.FullOrigins,[int]$row.LineNumber)
    if($at -lt 0) {throw 'selected_terminal_original_position'}
    $post=@('result','payload','files','completion','events','origins','terminal_copy','raw_copy','event_raw_copy','line','path',
        'writer_position','source_position','stopped_clock','expired','record_budget','code_unit_budget','scope','coordinate','hash','borrower','origin')
    foreach($control in $post) {
        $r=$result; $restore={}; $target=$null; $field=$null; $value=$null
        switch($control) {
            'result' {$r=$clone.Invoke($r,@());$r.Dispose()}
            'payload' {$target=$Payload;$field=Field $target 'disposed';$value=1}
            'files' {$target=$files;$field=Field $target 'disposed';$value=1}
            'completion' {$target=$Completion;$field=Field $target 'payload'}
            'events' {$prior=$index.AllEvents;$index.AllEvents=$prior.Clone();$restore={$index.AllEvents=$prior}}
            'origins' {$prior=$Context.FullOrigins;$Context.FullOrigins=$prior.Clone();$restore={$Context.FullOrigins=$prior}}
            'terminal_copy' {$prior=$index.AllEvents[$at];$index.AllEvents[$at]=$row.PSObject.Copy();$restore={$index.AllEvents[$at]=$prior}}
            'raw_copy' {$prior=$Context.FullLines[$at];$Context.FullLines[$at]=[string]::new($prior.ToCharArray());$restore={$Context.FullLines[$at]=$prior}}
            'event_raw_copy' {$prior=$row.RawLine;$row.RawLine=[string]::new($prior.ToCharArray());$restore={$row.RawLine=$prior}}
            'line' {$prior=$row.LineNumber;$row.LineNumber++;$restore={$row.LineNumber=$prior}}
            'path' {$prior=$row.FilePath;$row.FilePath='foreign';$restore={$row.FilePath=$prior}}
            'writer_position' {$map=(Field $member 'collectorWriterPositions').GetValue($member);$prior=$map[$at];$map[$at]++;$restore={$map[$at]=$prior}}
            'source_position' {$prior=$Context.FullOrigins[$at];$Context.FullOrigins[$at]++;$restore={$Context.FullOrigins[$at]=$prior}}
            'stopped_clock' {$clock.Stop();$restore={$clock.Start()}}
            'expired' {$target=$clock;$field=@($clock.GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic')|Where-Object {$_.Name -cin @('_elapsed','elapsed')})[0];$value=21L*[Diagnostics.Stopwatch]::Frequency}
            'record_budget' {$target=$member;$field=Field $target 'borrowerPriorRecords';$value=4096L}
            'code_unit_budget' {$target=$member;$field=Field $target 'borrowerPriorUnits';$value=[NLink.Tools.LocalPublicationInputLimits]::ProducerCodeUnits}
            'scope' {$target=$binding;$field=Field $target 'Transfer';$value='fte1-'+('0'*24)}
            'coordinate' {$target=$binding;$field=Field $target 'InboundLine';$value=4096}
            'hash' {$target=(Field $native 'measurements').GetValue($native)[2];$field=Field $target '<Sha256Base64>k__BackingField';$value='foreign'}
            'borrower' {$target=$Borrower;$field=Field $target 'disposed';$value=1}
            'origin' {$target=(Field $Payload 'origin').GetValue($Payload);$field=Field $target 'disposed';$value=1}
        }
        if($null -ne $field) {$prior=$field.GetValue($target);$field.SetValue($target,$value);$restore={$field.SetValue($target,$prior)}}
        try {if($r.IsCurrent) {throw ('selected_terminal_postcontrol:'+ $control)}} finally {& $restore}
        if(-not $result.IsCurrent) {throw ('selected_terminal_original_lost:'+ $control)}
    }
    # Scalar parser controls are fault-injection checks, not a second join or
    # acceptance pass. The consumer parses only its two owned original strings.
    $check=$Borrower.GetType().GetMethod('CompletedTerminal',[Reflection.BindingFlags]'Static,NonPublic')
    $raw=$row.RawLine; $direction=if($raw.Contains('direction=inbound')) {'inbound'} else {'outbound'}
    $session=(Field $binding 'Session').GetValue($binding);$transfer=(Field $binding 'Transfer').GetValue($binding)
    if(-not $check.Invoke($null,@($raw,$direction,$session,$transfer))) {throw 'selected_terminal_parser_original'}
    $bad=@($raw.Replace('error_code=(none);',''),($raw+'; error_code=(none)'),
        $raw.Replace('event=transfer_terminal','event=other'),$raw.Replace('direction='+$direction,'direction=other'),
        $raw.Replace('terminal_session_id='+$session,'terminal_session_id=foreign'),$raw.Replace('terminal_transfer_id='+$transfer,'terminal_transfer_id=foreign'),
        $raw.Replace('error_code=(none)','error_code=canceled'),$raw.Replace('reason=Transfer complete.','reason=Canceled.'),
        $raw.Replace('file_size_bytes=262144','file_size_bytes=1'),$raw.Replace('bytes_transferred=262144','bytes_transferred=1'),
        ($raw+'; event=transfer_terminal'),($raw+(';'*65)))
    foreach($changed in $bad) {
        if($changed -ceq $raw -or $check.Invoke($null,@($changed,$direction,$session,$transfer))) {throw ('selected_terminal_parser_control:'+ $parserControls)}
        $parserControls++
    }
    if($case -ceq 'dispose') {$result.Dispose();if($result.IsCurrent) {throw 'selected_terminal_disposal'}}
}
$visits=$Borrower.CumulativeRecordVisits-$beforeVisits; $units=$Borrower.CumulativeCodeUnits-$beforeUnits
if($null -ne $Borrower.TryJoinSelectedTerminalsForTest($Payload,$terminals) -or $visits -lt 1 -or
    ($joined -and $visits -ne $Context.FullLines.Length+2) -or $Borrower.CumulativeRecordVisits -gt 4096 -or
    $Borrower.CumulativeCodeUnits -gt [NLink.Tools.LocalPublicationInputLimits]::ProducerCodeUnits -or
    $Context.WriterReader.LinesRead -ne $beforeReads -or -not $Borrower.IsCurrent -or -not $Payload.IsCurrent -or
    -not $files.IsCurrent -or -not $Custody.OwnsPublication($Completion)) {throw 'selected_terminal_owner_or_budget_changed'}
[IO.File]::WriteAllText((Join-Path $ArtifactDir 'selected-terminal-join.json'),([ordered]@{
    Joined=$joined;Case=$case;PreControls=$pre.Count;PostControls=$post.Count;ParserControls=$parserControls
    InboundOrigin=$inbound;OutboundOrigin=$outbound;RepeatedJoinRejected=$true;ResultDisposed=($case -ceq 'dispose')
    RecordVisits=$visits;CodeUnits=$units;CumulativeRecordVisits=$Borrower.CumulativeRecordVisits;CumulativeCodeUnits=$Borrower.CumulativeCodeUnits
    AdditionalWriterReads=0;AdditionalPayloadHashes=0;AcceptanceChanged=$false;Live=$false
}|ConvertTo-Json -Depth 4 -Compress))
