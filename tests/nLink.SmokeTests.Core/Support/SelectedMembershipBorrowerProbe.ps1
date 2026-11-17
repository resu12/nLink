param($Context,$Custody,$Pair,$Index,$Failure,$Witness,$Prepared,$Completion,[string]$ArtifactDir,[int]$Evaluations,[scriptblock]$OriginProbe=$null)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
if($null -eq $Context -or $null -eq $Context.WriterReader -or $null -eq $Prepared -or $null -eq $Completion) {
    [IO.File]::WriteAllText((Join-Path $ArtifactDir 'membership-borrower.json'),'{"Status":"unavailable","AcceptanceChanged":false}')
    return
}
$reader=$Context.WriterReader; $owner=$Context.SourceOwner; $clock=$Context.Work
$beforeReads=$reader.LinesRead; $beforeFull=$Context.FullView|ConvertTo-Json -Depth 64 -Compress
$beforeCompact=$Context.CompactView|ConvertTo-Json -Depth 64 -Compress
$beforePrepared=$Prepared|ConvertTo-Json -Depth 64 -Compress
$clone=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic')
$selected=[Array]::BinarySearch($Context.FullOrigins,[int]$Witness.SourceLine)
if($env:NLINK_TEST_SELECTED_NATIVE_WRITER -ceq '1') {
    $clock.Start()
    $files=$owner.CollectorOutputsForTest
    if($null -eq $files -or ($null -ne $owner.TerminalFilesForTest) -ne ($env:NLINK_TEST_AUTHENTICATED_PAYLOAD_HANDOFF -ceq '1') -or $selected -lt 1 -or
        $null -ne $owner.TryOpenNativeWitnessWriterForTest($files,$Context.SourceReader,$Context,$clock) -or
        $null -ne $owner.TryOpenWriter()) {throw 'selected_native_original_reader_scope'}
    $member=$owner.GetType().GetField('borrowerMembership',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($owner)
    $map=$member.GetType().GetField('collectorWriterPositions',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($member)
    $borrowerMap=$member.GetType().GetField('borrowerPositions',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($member)
    $origin=$files.DescribeLineOriginForTest($Context.SourceReader,[int]$Witness.SourceLine)
    if(-not [object]::ReferenceEquals($map,$borrowerMap) -or $null -eq $origin -or $map[$selected] -ne $origin.WriterLine -or
        $beforeReads -ne $owner.WriterContext.Outputs[1].LineCount) {throw 'selected_native_membership_map'}
    [IO.File]::WriteAllText((Join-Path $ArtifactDir 'selected-native-witness.json'),([ordered]@{
        SelectedIndex=$selected;WriterRecordsRead=$beforeReads;SameMembershipMap=$true;SelectedWriterLine=$origin.WriterLine
        SourceLines=$owner.SourceLines;OriginalWitnessAndCompletion=$true;NoTerminalOwner=($null -eq $owner.TerminalFilesForTest);AdditionalWriterReads=0
        OriginalEvaluations=$Evaluations;AcceptanceChanged=$false;Live=$false}|ConvertTo-Json -Compress))
}
function Borrow($o,$r,$c,$p,$ctx,$idx,$f,$full,$compact,$fv,$cv,$w) {
    $o.TryBorrowMembershipForTest($r,$c,$p,$ctx,$idx,$f,$full,$compact,$fv,$cv,$w)
}
$borrower=$null; $clock.Start()
try {
    $preCases=@('owner','reader','custody','pair','context','index','failure','full','compact','full_view','compact_view','clock')
    foreach($case in $preCases) {
        $o=$owner; $r=$reader; $c=$Custody; $p=$Pair; $ctx=$Context; $idx=$Index; $f=$Failure
        $full=$Context.FullLines; $compact=$Context.CompactLines; $fv=$Context.FullView; $cv=$Context.CompactView; $w=$clock
        switch($case) {
            'owner' {$o=$clone.Invoke($o,@())} 'reader' {$r=$clone.Invoke($r,@())}
            'custody' {$c=$clone.Invoke($c,@())} 'pair' {$p=$clone.Invoke($p,@())}
            'context' {$ctx=$Context.PSObject.Copy()} 'index' {$idx=$Index.PSObject.Copy()}
            'failure' {$f=$Failure.PSObject.Copy()} 'full' {$full=$full.Clone()}
            'compact' {$compact=$compact.Clone()} 'full_view' {$fv=$null}
            'compact_view' {$cv=$null} 'clock' {$w=[Diagnostics.Stopwatch]::StartNew()}
        }
        try {if($null -ne (Borrow $o $r $c $p $ctx $idx $f $full $compact $fv $cv $w)) {throw "borrower_precontrol:$case"}}
        finally {if($case -ceq 'clock') {$w.Stop()}}
    }
    $borrower=Borrow $owner $reader $Custody $Pair $Context $Index $Failure $Context.FullLines $Context.CompactLines $Context.FullView $Context.CompactView $clock
    if($null -eq $borrower) {
        if(-not $reader.IsVerified -or $reader.LinesRead -ne $beforeReads -or -not $Custody.OwnsPublication($Completion)) {throw 'borrower_omission_changed_owner'}
        if($null -ne $OriginProbe) {& $OriginProbe -Borrower $null -Context $Context -Completion $Completion -Custody $Custody -ArtifactDir $ArtifactDir}
        [IO.File]::WriteAllText((Join-Path $ArtifactDir 'membership-borrower.json'),([ordered]@{
            Status='unavailable';OriginalCompletionCurrent=$true;OwnerStillVerified=$true;OriginalEvaluations=$Evaluations
            AdditionalWriterReads=$reader.LinesRead-$beforeReads;AcceptanceChanged=$false;Live=$false}|ConvertTo-Json -Compress))
        return
    }
    if($null -ne (Borrow $owner $reader $Custody $Pair $Context $Index $Failure $Context.FullLines $Context.CompactLines $Context.FullView $Context.CompactView $clock)) {throw 'borrower_issued_twice'}
    function Consume($b,$o,$r,$c,$p,$ctx,$idx,$f,$full,$compact,$fv,$cv,$w,$at,$writer,$seq) {
        $b.TryConsume($o,$r,$c,$p,$ctx,$idx,$f,$full,$compact,$fv,$cv,$w,$at,$writer,$seq)
    }
    $postCases=@('result')+$preCases+@('selected','writer','sequence','stopped_clock','raw_reference','event_copy','expired','record_budget','code_unit_budget')
    $memberField=$borrower.GetType().GetField('membership',[Reflection.BindingFlags]'Instance,NonPublic')
    $member=$memberField.GetValue($borrower)
    if($env:NLINK_TEST_SELECTED_NATIVE_WRITER -ceq '1') {
        # Predicate control only: restore the real adopted owner before continuing.
        $field=$owner.GetType().GetField('collectorPrimaryFiles',[Reflection.BindingFlags]'Instance,NonPublic')
        try {
            $field.SetValue($owner,$clone.Invoke($files,@()))
            if($borrower.IsCurrent) {throw 'selected_native_borrower_accepted_copied_collector'}
        } finally {$field.SetValue($owner,$files)}
        if(-not $borrower.IsCurrent) {throw 'selected_native_borrower_lost_original_collector'}
    }
    foreach($case in $postCases) {
        $b=$borrower; $o=$owner; $r=$reader; $c=$Custody; $p=$Pair; $ctx=$Context; $idx=$Index; $f=$Failure
        $full=$Context.FullLines; $compact=$Context.CompactLines; $fv=$Context.FullView; $cv=$Context.CompactView; $w=$clock
        $at=$selected; $writer=$Witness.Writer; $seq=$Witness.WriterSequence; $restoreField=$null; $restoreValue=$null
        $raw=$full[$selected]; $event=$Failure.Event
        switch($case) {
            'result' {$b=$clone.Invoke($b,@());$b.Dispose()}
            'owner' {$o=$clone.Invoke($o,@())} 'reader' {$r=$clone.Invoke($r,@())}
            'custody' {$c=$clone.Invoke($c,@())} 'pair' {$p=$clone.Invoke($p,@())}
            'context' {$ctx=$Context.PSObject.Copy()} 'index' {$idx=$Index.PSObject.Copy()}
            'failure' {$f=$Failure.PSObject.Copy()} 'full' {$full=$full.Clone()}
            'compact' {$compact=$compact.Clone()} 'full_view' {$fv=$null} 'compact_view' {$cv=$null}
            'clock' {$w=[Diagnostics.Stopwatch]::StartNew()} 'selected' {$at++}
            'writer' {$writer='foreign'} 'sequence' {$seq++} 'stopped_clock' {$clock.Stop()}
            'raw_reference' {$full[$selected]=[string]::new($raw.ToCharArray())}
            'event_copy' {$Failure.Event=$event.PSObject.Copy()}
            'expired' {
                $restoreField=$clock.GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic')|Where-Object {$_.Name -cin @('_elapsed','elapsed')}
                if($null -eq $restoreField) {throw 'borrower_clock_control_field'}
                $restoreValue=$restoreField.GetValue($clock);$restoreField.SetValue($clock,([long][Diagnostics.Stopwatch]::Frequency*21))
            }
            'record_budget' {$restoreField=$member.GetType().GetField('borrowerPriorRecords',[Reflection.BindingFlags]'Instance,NonPublic');$restoreValue=$restoreField.GetValue($member);$restoreField.SetValue($member,4096L)}
            'code_unit_budget' {$restoreField=$member.GetType().GetField('borrowerPriorUnits',[Reflection.BindingFlags]'Instance,NonPublic');$restoreValue=$restoreField.GetValue($member);$restoreField.SetValue($member,[NLink.Tools.LocalPublicationInputLimits]::ProducerCodeUnits)}
        }
        try {if(Consume $b $o $r $c $p $ctx $idx $f $full $compact $fv $cv $w $at $writer $seq) {throw "borrower_postcontrol:$case"}}
        finally {
            $Context.FullLines[$selected]=$raw; $Failure.Event=$event
            if($null -ne $restoreField) {$target=if($case -ceq 'expired'){$clock}else{$member};$restoreField.SetValue($target,$restoreValue)}
            if($case -ceq 'stopped_clock') {$clock.Start()};if($case -ceq 'clock') {$w.Stop()}
        }
    }
    if(-not (Consume $borrower $owner $reader $Custody $Pair $Context $Index $Failure $Context.FullLines $Context.CompactLines $Context.FullView $Context.CompactView $clock $selected $Witness.Writer $Witness.WriterSequence)) {throw 'borrower_original_consume'}
    if(Consume $borrower $owner $reader $Custody $Pair $Context $Index $Failure $Context.FullLines $Context.CompactLines $Context.FullView $Context.CompactView $clock $selected $Witness.Writer $Witness.WriterSequence) {throw 'borrower_consumed_twice'}
    if($null -ne $OriginProbe) {& $OriginProbe -Borrower $borrower -Context $Context -Completion $Completion -Custody $Custody -ArtifactDir $ArtifactDir}
    $visits=$borrower.CumulativeRecordVisits; $units=$borrower.CumulativeCodeUnits
    if($env:NLINK_TEST_MEMBERSHIP_BORROWER_CASE -ceq 'dispose') {
        $borrower.Dispose()
        if($borrower.IsCurrent -or (Consume $borrower $owner $reader $Custody $Pair $Context $Index $Failure $Context.FullLines $Context.CompactLines $Context.FullView $Context.CompactView $clock $selected $Witness.Writer $Witness.WriterSequence)) {throw 'borrower_disposed_result_revived'}
        if($null -ne $OriginProbe -and -not $owner.SelectedOriginRetiredForTest) {throw 'selected_origin_survived_borrower_disposal'}
    } elseif(-not $borrower.IsCurrent) {throw 'borrower_lost_current_owner'}
    if(-not $reader.IsVerified -or $reader.LinesRead -ne $beforeReads -or $beforeReads -ne $owner.WriterContext.Outputs[1].LineCount -or
        -not $Custody.Owns($Pair,$Context,$Context.FullLines,$Context.CompactLines,$Context.FullView,$Context.CompactView) -or
        -not $Custody.OwnsPublication($Completion) -or $Evaluations -ne 3 -or
        ($Context.FullView|ConvertTo-Json -Depth 64 -Compress) -cne $beforeFull -or
        ($Context.CompactView|ConvertTo-Json -Depth 64 -Compress) -cne $beforeCompact -or
        ($Prepared|ConvertTo-Json -Depth 64 -Compress) -cne $beforePrepared) {throw 'borrower_changed_original_owner_or_outcome'}
} finally {$clock.Stop()}
[IO.File]::WriteAllText((Join-Path $ArtifactDir 'membership-borrower.json'),([ordered]@{
    Status='verified_offline_selected_membership_reuse_only';PreControls=$preCases.Count;PostControls=$postCases.Count
    SelectedIndex=$selected;WriterRecordsRead=$beforeReads;AdditionalWriterReads=$reader.LinesRead-$beforeReads
    CumulativeRecordVisits=$visits;CumulativeCodeUnits=$units;WorkMilliseconds=$clock.ElapsedMilliseconds
    OriginalEvaluations=$Evaluations;OriginalViewsUnchanged=$true;OriginalWitnessUnchanged=$true;OriginalCompletionCurrent=$true
    BorrowerDisposed=($env:NLINK_TEST_MEMBERSHIP_BORROWER_CASE -ceq 'dispose');OwnerStillVerified=$reader.IsVerified
    RepeatedBorrowRejected=$true;RepeatedConsumptionRejected=$true;AcceptanceChanged=$false;Live=$false
}|ConvertTo-Json -Compress))
