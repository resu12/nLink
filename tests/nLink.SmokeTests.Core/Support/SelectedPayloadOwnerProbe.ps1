param($Borrower,$Context,$Completion,$Custody,[string]$ArtifactDir)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
$case=$env:NLINK_TEST_PAYLOAD_OWNER_CASE
if($case -cnotin @('active','dispose','scope') -or $null -eq $Borrower) {throw 'selected_payload_case'}
$owner=$Context.SourceOwner; $reader=$Context.WriterReader; $files=$owner.TerminalFilesForTest; $clock=$Context.Work
$beforeReads=$reader.LinesRead; $beforeVisits=$Borrower.CumulativeRecordVisits; $beforeUnits=$Borrower.CumulativeCodeUnits
$origin=$Borrower.TryJoinSelectedOriginForTest($owner.CollectorOutputsForTest,$Completion)
if($null -eq $origin -or $null -eq $files) {throw 'selected_payload_origin_or_files'}
$clone=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic')
$member=$Borrower.GetType().GetField('membership',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($Borrower)
$pre=@('borrower','files','origin','completion','missing_files','stopped_clock','record_budget','code_unit_budget')
foreach($control in $pre) {
    $b=$Borrower; $f=$files; $o=$origin; $c=$Completion; $field=$null; $prior=$null
    switch($control) {
        'borrower' {$b=$clone.Invoke($b,@())} 'files' {$f=$clone.Invoke($f,@())}
        'origin' {$o=$clone.Invoke($o,@())} 'completion' {$c=$clone.Invoke($c,@())}
        'missing_files' {$f=$null} 'stopped_clock' {$clock.Stop()}
        'record_budget' {$field=$member.GetType().GetField('borrowerPriorRecords',[Reflection.BindingFlags]'Instance,NonPublic');$prior=$field.GetValue($member);$field.SetValue($member,4096L)}
        'code_unit_budget' {$field=$member.GetType().GetField('borrowerPriorUnits',[Reflection.BindingFlags]'Instance,NonPublic');$prior=$field.GetValue($member);$field.SetValue($member,[NLink.Tools.LocalPublicationInputLimits]::ProducerCodeUnits)}
    }
    try {if($null -ne $b.TryBindSelectedPayloadForTest($f,$o,$c)) {throw ('selected_payload_precontrol:'+ $control)}}
    finally {if($null -ne $field) {$field.SetValue($member,$prior)};if($control -ceq 'stopped_clock') {$clock.Start()}}
}
if($Borrower.CumulativeRecordVisits -ne $beforeVisits+1) {throw 'selected_payload_rejection_spent_attempt'}
$result=$null; $post=@()
if($case -ceq 'scope') {
    # Predicate fault only; restore the original immutable binding afterward.
    # The rejected admitted attempt must remain spent.
    $native=$files.GetType().GetField('files',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($files)
    $binding=$native.GetType().GetField('transfer',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($native)
    $field=$binding.GetType().GetField('Transfer',[Reflection.BindingFlags]'Instance,NonPublic'); $prior=$field.GetValue($binding)
    try {
        $field.SetValue($binding,'fte1-'+('0'*24))
        if($null -ne $Borrower.TryBindSelectedPayloadForTest($files,$origin,$Completion)) {throw 'selected_payload_foreign_scope'}
    } finally {$field.SetValue($binding,$prior)}
} else {
    $result=$Borrower.TryBindSelectedPayloadForTest($files,$origin,$Completion)
    if($null -eq $result -or -not $result.Owns($owner,$Borrower,$files,$origin,$Completion)) {throw 'selected_payload_original'}
    $post=@('result','owner','borrower','files','origin','completion','closed_files','retired_completion')
    foreach($control in $post) {
        $r=$result; $o=$owner; $b=$Borrower; $f=$files; $g=$origin; $c=$Completion; $field=$null; $target=$null; $prior=$null
        switch($control) {
            'result' {$r=$clone.Invoke($r,@());$r.Dispose()} 'owner' {$o=$clone.Invoke($o,@())}
            'borrower' {$b=$clone.Invoke($b,@())} 'files' {$f=$clone.Invoke($f,@())}
            'origin' {$g=$clone.Invoke($g,@())} 'completion' {$c=$clone.Invoke($c,@())}
            'closed_files' {$target=$files;$field=$target.GetType().GetField('disposed',[Reflection.BindingFlags]'Instance,NonPublic');$prior=$field.GetValue($target);$field.SetValue($target,1)}
            'retired_completion' {$target=$Completion;$field=$target.GetType().GetField('payload',[Reflection.BindingFlags]'Instance,NonPublic');$prior=$field.GetValue($target);$field.SetValue($target,$null)}
        }
        try {if($r.Owns($o,$b,$f,$g,$c)) {throw ('selected_payload_postcontrol:'+ $control)}}
        finally {if($null -ne $field) {$field.SetValue($target,$prior)}}
    }
    if(-not $result.IsCurrent) {throw 'selected_payload_original_lost'}
    if($case -ceq 'dispose') {$result.Dispose();if($result.IsCurrent) {throw 'selected_payload_disposed_current'}}
}
if($null -ne $Borrower.TryBindSelectedPayloadForTest($files,$origin,$Completion) -or
    -not $Borrower.IsCurrent -or -not $origin.IsCurrent -or -not $files.IsCurrent -or -not $reader.IsVerified -or
    -not $Custody.OwnsPublication($Completion) -or $reader.LinesRead -ne $beforeReads -or
    $Borrower.CumulativeRecordVisits -ne $beforeVisits+2 -or $Borrower.CumulativeRecordVisits -gt 4096 -or
    $Borrower.CumulativeCodeUnits -ne $beforeUnits+4L*$Context.FullLines[$origin.SelectedIndex].Length) {throw 'selected_payload_original_or_budget_changed'}
[IO.File]::WriteAllText((Join-Path $ArtifactDir 'selected-payload-owner.json'),([ordered]@{
    Status=$(if($case -ceq 'scope') {'foreign_scope_rejected'} else {'verified_offline_selected_payload_owner_only'})
    Case=$case;PreControls=$pre.Count;PostControls=$post.Count;SourceLine=$origin.SourceLine;ProducerLine=$origin.ProducerLine
    ResultDisposed=($case -ceq 'dispose');RepeatedBindingRejected=$true;OriginalCompletionCurrent=$true
    CumulativeRecordVisits=$Borrower.CumulativeRecordVisits;CumulativeCodeUnits=$Borrower.CumulativeCodeUnits
    AdditionalWriterReads=0;AdditionalPayloadHashes=0;TerminalIntegrityEvaluated=$false;AcceptanceChanged=$false;Live=$false
}|ConvertTo-Json -Compress))
if($env:NLINK_TEST_SELECTED_TERMINAL_CASE) {
    if($case -cne 'active' -or $null -eq $result) {throw 'selected_terminal_payload_scope'}
    & (Join-Path $repoRoot 'tests/nLink.SmokeTests.Core/Support/SelectedTerminalJoinProbe.ps1') -Borrower $Borrower -Payload $result -Context $Context `
        -Completion $Completion -Custody $Custody -ArtifactDir $ArtifactDir
}
