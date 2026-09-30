param($Borrower,$Context,$Completion,$Custody,[string]$ArtifactDir)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$case=$env:NLINK_TEST_SELECTED_ORIGIN_CASE
$owner=$Context.SourceOwner; $reader=$Context.WriterReader; $files=$owner.CollectorOutputsForTest; $clock=$Context.Work
$beforeReads=$reader.LinesRead
if($null -eq $Borrower) {
    if($case -cne 'unavailable' -or $owner.SelectedOriginIssuedForTest -or -not $Custody.OwnsPublication($Completion)) {throw 'selected_origin_missing_borrower'}
    [IO.File]::WriteAllText((Join-Path $ArtifactDir 'selected-origin.json'),
        '{"Status":"unavailable","OriginalCompletionCurrent":true,"AdditionalWriterReads":0,"AcceptanceChanged":false}')
    return
}
$beforeVisits=$Borrower.CumulativeRecordVisits; $beforeUnits=$Borrower.CumulativeCodeUnits
$clone=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic')
$member=$Borrower.GetType().GetField('membership',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($Borrower)
$pre=@('borrower','files','completion','missing_completion','unconsumed','stopped_clock','expired','record_budget','code_unit_budget')
foreach($control in $pre) {
    $b=$Borrower; $f=$files; $c=$Completion; $field=$null; $target=$null; $prior=$null
    switch($control) {
        'borrower' {$b=$clone.Invoke($b,@())} 'files' {$f=$clone.Invoke($f,@())}
        'completion' {$c=$clone.Invoke($c,@())} 'missing_completion' {$c=$null}
        'stopped_clock' {$clock.Stop()}
        'unconsumed' {$target=$Borrower;$field=$target.GetType().GetField('consumed',[Reflection.BindingFlags]'Instance,NonPublic');$prior=$field.GetValue($target);$field.SetValue($target,0)}
        'expired' {$target=$clock;$field=$target.GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic')|Where-Object {$_.Name -cin @('_elapsed','elapsed')};$prior=$field.GetValue($target);$field.SetValue($target,([long][Diagnostics.Stopwatch]::Frequency*21))}
        'record_budget' {$target=$member;$field=$target.GetType().GetField('borrowerPriorRecords',[Reflection.BindingFlags]'Instance,NonPublic');$prior=$field.GetValue($target);$field.SetValue($target,4095L)}
        'code_unit_budget' {$target=$member;$field=$target.GetType().GetField('borrowerPriorUnits',[Reflection.BindingFlags]'Instance,NonPublic');$prior=$field.GetValue($target);$field.SetValue($target,[NLink.Tools.LocalPublicationInputLimits]::ProducerCodeUnits)}
    }
    try {if($null -ne $b.TryJoinSelectedOriginForTest($f,$c)) {throw ('selected_origin_precontrol:'+ $control)}}
    finally {if($null -ne $field) {$field.SetValue($target,$prior)};if($control -ceq 'stopped_clock') {$clock.Start()}}
}
if($Borrower.CumulativeRecordVisits -ne $beforeVisits -or $owner.SelectedOriginIssuedForTest) {throw 'selected_origin_rejected_input_spent_join'}
if($case -ceq 'invalid-map') {
    # Predicate control, not a fabricated collector history. The one admitted
    # join attempt stays spent even after the original map is restored.
    $map=$member.GetType().GetField('collectorWriterPositions',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($member)
    $selected=$member.SelectedIndex; $saved=$map[$selected]
    try {$map[$selected]=$saved+1;if($null -ne $Borrower.TryJoinSelectedOriginForTest($files,$Completion)) {throw 'selected_origin_invalid_map_admitted'}}
    finally {$map[$selected]=$saved}
    if($null -ne $Borrower.TryJoinSelectedOriginForTest($files,$Completion) -or $owner.SelectedOriginIssuedForTest -or
        -not $Borrower.IsCurrent -or -not $Custody.OwnsPublication($Completion) -or $reader.LinesRead -ne $beforeReads -or
        $Borrower.CumulativeRecordVisits -ne $beforeVisits+1) {throw 'selected_origin_failed_attempt_reopened'}
    [IO.File]::WriteAllText((Join-Path $ArtifactDir 'selected-origin.json'),
        '{"Status":"invalid_map_rejected","OriginalCompletionCurrent":true,"JoinRecordVisits":1,"RepeatedJoinRejected":true,"AdditionalWriterReads":0,"AcceptanceChanged":false}')
    return
}
$result=$Borrower.TryJoinSelectedOriginForTest($files,$Completion)
if($null -eq $result -or -not $result.Owns($owner,$Borrower,$files,$Completion) -or
    $null -ne $Borrower.TryJoinSelectedOriginForTest($files,$Completion)) {throw 'selected_origin_original_join'}
$post=@('result','owner','borrower','files','completion','raw_reference','event_copy','closed_collector','retired_completion','expired',
    'source_line','writer_line','producer_line')
$failure=$member.GetType().GetField('borrowerFailure',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($member)
foreach($control in $post) {
    $r=$result; $o=$owner; $b=$Borrower; $f=$files; $c=$Completion; $field=$null; $target=$null; $prior=$null
    $raw=$Context.FullLines[$result.SelectedIndex];$event=$failure.Event
    switch($control) {
        'result' {$r=$clone.Invoke($r,@());$r.Dispose()}
        'owner' {$o=$clone.Invoke($o,@())} 'borrower' {$b=$clone.Invoke($b,@())}
        'files' {$f=$clone.Invoke($f,@())} 'completion' {$c=$clone.Invoke($c,@())}
        'raw_reference' {$Context.FullLines[$result.SelectedIndex]=[string]::new($raw.ToCharArray())}
        'event_copy' {$failure.Event=$event.PSObject.Copy()}
        'closed_collector' {$target=$files;$field=$target.GetType().GetField('disposed',[Reflection.BindingFlags]'Instance,NonPublic');$prior=$field.GetValue($target);$field.SetValue($target,$true)}
        'retired_completion' {$target=$Completion;$field=$target.GetType().GetField('payload',[Reflection.BindingFlags]'Instance,NonPublic');$prior=$field.GetValue($target);$field.SetValue($target,$null)}
        'expired' {$target=$clock;$field=$target.GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic')|Where-Object {$_.Name -cin @('_elapsed','elapsed')};$prior=$field.GetValue($target);$field.SetValue($target,([long][Diagnostics.Stopwatch]::Frequency*21))}
        'source_line' {$target=$member;$field=$target.GetType().GetField('borrowerSourceLine',[Reflection.BindingFlags]'Instance,NonPublic');$prior=$field.GetValue($target);$field.SetValue($target,([int]$prior+1))}
        'writer_line' {
            $map=$member.GetType().GetField('collectorWriterPositions',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($member)
            $savedWriterLine=$map[$result.SelectedIndex];$map[$result.SelectedIndex]=$savedWriterLine+1
        }
        'producer_line' {
            $origins=$files.GetType().GetField('origins',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($files)
            $maps=$origins.GetType().GetField('origins',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($origins)
            $savedProducerLine=$maps[2][$result.SourceLine-1];$maps[2][$result.SourceLine-1]=$savedProducerLine+1
        }
    }
    try {if($r.Owns($o,$b,$f,$c)) {throw ('selected_origin_postcontrol:'+ $control)}}
    finally {
        if($null -ne $field) {$field.SetValue($target,$prior)}
        if($control -ceq 'writer_line') {$map[$result.SelectedIndex]=$savedWriterLine}
        if($control -ceq 'producer_line') {$maps[2][$result.SourceLine-1]=$savedProducerLine}
        $Context.FullLines[$result.SelectedIndex]=$raw;$failure.Event=$event
    }
}
if(-not $result.IsCurrent -or -not $Borrower.IsCurrent -or -not $reader.IsVerified -or
    -not $Custody.OwnsPublication($Completion) -or $reader.LinesRead -ne $beforeReads -or
    $Borrower.CumulativeRecordVisits -ne $beforeVisits+1 -or $Borrower.CumulativeCodeUnits -ne $beforeUnits) {throw 'selected_origin_changed_original'}
if($case -ceq 'result-dispose') {
    $result.Dispose()
    if($result.IsCurrent -or -not $Borrower.IsCurrent -or -not $files.IsCurrent -or -not $Custody.OwnsPublication($Completion) -or
        $null -ne $Borrower.TryJoinSelectedOriginForTest($files,$Completion)) {throw 'selected_origin_disposal_owned_originals'}
}
[IO.File]::WriteAllText((Join-Path $ArtifactDir 'selected-origin.json'),([ordered]@{
    Status='verified_offline_selected_collector_origin_only';PreControls=$pre.Count;PostControls=$post.Count
    SelectedIndex=$result.SelectedIndex;SourceLine=$result.SourceLine;WriterLine=$result.WriterLine
    ProducerLine=$result.ProducerLine;RetainedLine=$result.RetainedLine;JoinRecordVisits=1
    CumulativeRecordVisits=$Borrower.CumulativeRecordVisits;CumulativeCodeUnits=$Borrower.CumulativeCodeUnits
    WorkMilliseconds=$clock.ElapsedMilliseconds;AdditionalWriterReads=$reader.LinesRead-$beforeReads
    OriginalCompletionCurrent=$Custody.OwnsPublication($Completion);BorrowerCurrent=$Borrower.IsCurrent
    ResultDisposed=($case -ceq 'result-dispose');RepeatedJoinRejected=$true;AcceptanceChanged=$false;Live=$false
}|ConvertTo-Json -Compress))
