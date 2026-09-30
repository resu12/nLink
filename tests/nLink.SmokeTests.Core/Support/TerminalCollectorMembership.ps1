param($Context,$Custody,$Evaluations,$Files,[string]$Mutation='')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
if($Mutation -cnotin @('','foreign-member','partial-read','origin-coordinate') -or $null -ne $Context.WriterReader) {throw 'collector_membership_scope'}
$membershipMutation=if($Mutation -ceq 'origin-coordinate'){''}else{$Mutation}
# Ordinary first-record membership lookup, never a failure-witness selection.
$lookup=ConvertFrom-GuiSmokeLogLedgerStructuredFields $Context.FullLines[0]
$clone=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic')
function Verify($Owner,$F,$Reader,$C,$Cust,$Pair,$Full,$Compact,$FullView,$CompactView) {
    return $Owner.TryVerifyCollectorMembershipForTest($F,$Reader,$Cust,$Pair,$C,$Full,$Compact,$FullView,$CompactView,
        $Context.FullOriginal,[string]$lookup.log_writer_id,[long]$lookup.log_writer_sequence,
        $Context.NativeOwner.CodeUnitVisits,$Context.Work,$membershipMutation)
}
$cases=@('owner','files','reader','context','custody','pair','full','compact','view')
foreach($case in $cases) {
    $o=$Context.SourceOwner; $f=$Files; $r=$Context.SourceReader; $c=$Context; $cust=$Custody; $p=$Evaluations
    $full=$Context.FullLines; $compact=$Context.CompactLines; $view=$Context.FullView
    switch($case) {
        'owner' {$o=$clone.Invoke($o,@())}
        'files' {$f=$clone.Invoke($f,@())}
        'reader' {$r=$clone.Invoke($r,@())}
        'context' {$c=$Context.PSObject.Copy()}
        'custody' {$cust=$clone.Invoke($cust,@())}
        'pair' {$p=$clone.Invoke($p,@())}
        'full' {$full=$full.Clone()}
        'compact' {$compact=$compact.Clone()}
        'view' {$view=$null}
    }
    if($null -ne (Verify $o $f $r $c $cust $p $full $compact $view $Context.CompactView)) {throw "collector_membership_precontrol:$case"}
}
foreach($case in @('original_array','code_units','clock')) {
    $original=$Context.FullOriginal; $units=$Context.NativeOwner.CodeUnitVisits; $clock=$Context.Work
    switch($case) {
        'original_array' {$original=$original.Clone()}
        'code_units' {$units++}
        'clock' {$clock=[Diagnostics.Stopwatch]::StartNew()}
    }
    try {
        if($null -ne $Context.SourceOwner.TryVerifyCollectorMembershipForTest($Files,$Context.SourceReader,$Custody,$Evaluations,
            $Context,$Context.FullLines,$Context.CompactLines,$Context.FullView,$Context.CompactView,$original,
            [string]$lookup.log_writer_id,[long]$lookup.log_writer_sequence,$units,$clock,$membershipMutation)) {throw "collector_membership_accounting:$case"}
    } finally {if($case -ceq 'clock') {$clock.Stop()}}
}
if($Context.SourceOwner.CollectorWriterRecordsReadForTest -ne 0) {throw 'collector_membership_precontrols_read_writer'}
$owned=Verify $Context.SourceOwner $Files $Context.SourceReader $Context $Custody $Evaluations $Context.FullLines $Context.CompactLines $Context.FullView $Context.CompactView
$expected=-not $membershipMutation
if(($null -ne $owned) -ne $expected -or $Context.SourceOwner.CollectorWriterVerifiedForTest -ne $expected) {throw 'collector_membership_outcome'}
if($null -ne (Verify $Context.SourceOwner $Files $Context.SourceReader $Context $Custody $Evaluations $Context.FullLines $Context.CompactLines $Context.FullView $Context.CompactView) -or
    $null -ne $Context.SourceOwner.TryOpenWriter()) {throw 'collector_membership_one_shot'}
$postControls=0
if($expected) {
    foreach($case in @('result','owner','reader','context','custody','pair','full','compact','view')) {
        $result=$owned; $o=$Context.SourceOwner; $r=$Context.SourceReader; $c=$Context; $cust=$Custody; $p=$Evaluations
        $full=$Context.FullLines; $compact=$Context.CompactLines; $view=$Context.FullView
        switch($case) {
            'result' {$result=$clone.Invoke($result,@()); $result.Dispose()}
            'owner' {$o=$clone.Invoke($o,@())}
            'reader' {$r=$clone.Invoke($r,@())}
            'context' {$c=$Context.PSObject.Copy()}
            'custody' {$cust=$clone.Invoke($cust,@())}
            'pair' {$p=$clone.Invoke($p,@())}
            'full' {$full=$full.Clone()}
            'compact' {$compact=$compact.Clone()}
            'view' {$view=$null}
        }
        if($result.Owns($o,$r,$cust,$p,$c,$full,$compact,$view,$Context.CompactView)) {throw "collector_membership_postcontrol:$case"}
        $postControls++
    }
    if(-not $owned.Owns($Context.SourceOwner,$Context.SourceReader,$Custody,$Evaluations,$Context,
        $Context.FullLines,$Context.CompactLines,$Context.FullView,$Context.CompactView) -or $owned.SelectedIndex -ne 0 -or
        $owned.RecordVisits -ne 2*$Context.SourceOwner.WriterContext.Outputs[1].LineCount+$Context.FullLines.Length) {throw 'collector_membership_original_lost'}
}
if($Mutation -ceq 'partial-read' -and $Context.SourceOwner.CollectorWriterRecordsReadForTest -ne 1) {throw 'collector_membership_partial_count'}
if($Mutation -cne 'partial-read' -and $Context.SourceOwner.CollectorWriterRecordsReadForTest -ne $Context.SourceOwner.WriterContext.Outputs[1].LineCount) {throw 'collector_membership_eof_count'}
$join=$null
if($expected) {
    function Join-Origins($Result,$Owner,$F,$Reader,$C,$Cust,$Pair,$Full,$Compact,$FullView,$CompactView,$Origins,$Clock) {
        return $Result.TryJoinOriginsForTest($Owner,$F,$Reader,$Cust,$Pair,$C,$Full,$Compact,$FullView,$CompactView,$Origins,$Clock)
    }
    $joinCases=@('result','owner','files','reader','context','custody','pair','full','compact','view','compact_view','origins','clock','original_snapshot')
    foreach($case in $joinCases) {
        $result=$owned; $o=$Context.SourceOwner; $f=$Files; $r=$Context.SourceReader; $c=$Context; $cust=$Custody; $p=$Evaluations
        $full=$Context.FullLines; $compact=$Context.CompactLines; $view=$Context.FullView; $compactView=$Context.CompactView
        $origins=$Context.FullOrigins; $clock=$Context.Work; $snapshot=$Context.FullOriginal
        switch($case) {
            'result' {$result=$clone.Invoke($result,@()); $result.Dispose()}
            'owner' {$o=$clone.Invoke($o,@())}
            'files' {$f=$clone.Invoke($f,@())}
            'reader' {$r=$clone.Invoke($r,@())}
            'context' {$c=$Context.PSObject.Copy()}
            'custody' {$cust=$clone.Invoke($cust,@())}
            'pair' {$p=$clone.Invoke($p,@())}
            'full' {$full=$full.Clone()}
            'compact' {$compact=$compact.Clone()}
            'view' {$view=$null}
            'compact_view' {$compactView=$null}
            'origins' {$origins=$origins.Clone()}
            'clock' {$clock=[Diagnostics.Stopwatch]::StartNew()}
            'original_snapshot' {$Context.FullOriginal=$snapshot.Clone()}
        }
        try {
            if(Join-Origins $result $o $f $r $c $cust $p $full $compact $view $compactView $origins $clock) {throw "collector_origin_join_owner:$case"}
        } finally {$Context.FullOriginal=$snapshot; if($case -ceq 'clock') {$clock.Stop()}}
    }
    if($owned.OriginJoinRecordVisitsForTest -ne 0 -or $owned.OriginsJoinedForTest) {throw 'collector_origin_join_precontrols_consumed'}
    $writerReadsBefore=$Context.SourceOwner.CollectorWriterRecordsReadForTest
    $maps=$null; $savedOrigin=0
    try {
        if($Mutation -ceq 'origin-coordinate') {
            # Corrupt a live, originally owned map with a different valid writer
            # coordinate. No changed file bytes or replacement receipt are used.
            $originOwner=$Files.GetType().GetField('origins',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($Files)
            $maps=$originOwner.GetType().GetField('origins',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($originOwner)
            $savedOrigin=$maps[2][0]
            if($savedOrigin -le 1 -or $maps[1][0] -ne 1) {throw 'collector_origin_join_control_requires_gap'}
            $maps[2][0]=1
        }
        $joined=Join-Origins $owned $Context.SourceOwner $Files $Context.SourceReader $Context $Custody $Evaluations `
            $Context.FullLines $Context.CompactLines $Context.FullView $Context.CompactView $Context.FullOrigins $Context.Work
    } finally {if($null -ne $maps) {$maps[2][0]=$savedOrigin}}
    $joinExpected=$Mutation -cne 'origin-coordinate'
    if($joined -ne $joinExpected -or $owned.OriginsJoinedForTest -ne $joinExpected) {throw 'collector_origin_join_outcome'}
    if(Join-Origins $owned $Context.SourceOwner $Files $Context.SourceReader $Context $Custody $Evaluations `
        $Context.FullLines $Context.CompactLines $Context.FullView $Context.CompactView $Context.FullOrigins $Context.Work) {throw 'collector_origin_join_repeated'}
    if($Context.SourceOwner.CollectorWriterRecordsReadForTest -ne $writerReadsBefore -or -not $owned.IsCurrent) {throw 'collector_origin_join_changed_membership'}
    $copiedJoin=$clone.Invoke($owned,@()); $copiedJoin.Dispose()
    if($copiedJoin.OriginsJoinedForTest -or $null -ne $copiedJoin.JoinedOriginsForTest -or $owned.OriginsJoinedForTest -ne $joinExpected) {
        throw 'collector_origin_join_copied_result'
    }
    [object[]]$joinedRows=@()
    if($joinExpected) {$joinedRows=@($owned.JoinedOriginsForTest)}
    $join=[pscustomobject]@{Joined=$joined;RejectedOwnerControls=$joinCases.Count;RepeatedJoinRejected=$true;CopiedJoinRejected=$true;
        RecordVisits=$owned.OriginJoinRecordVisitsForTest;CumulativeRecordVisits=7L*$Context.FullLines.Length+4L*$Context.CompactLines.Length+$owned.RecordVisits+$owned.OriginJoinRecordVisitsForTest;
        AdditionalWriterReads=$Context.SourceOwner.CollectorWriterRecordsReadForTest-$writerReadsBefore;
        Rows=$joinedRows;AcceptanceChanged=$false}
}
[pscustomobject]@{Verified=$expected;Mutation=$Mutation;PreReadControls=$cases.Count+3;PostReadControls=$postControls;
    WriterRecordsRead=$Context.SourceOwner.CollectorWriterRecordsReadForTest;
    RecordVisits=if($expected){$owned.RecordVisits}else{0};CodeUnitVisits=if($expected){$owned.CodeUnitVisits}else{0};
    SelectedIndex=if($expected){$owned.SelectedIndex}else{-1};LookupIsFailureWitness=$false;
    RepeatedVerificationRejected=$true;LegacyReopenRejected=$true;PathReopened=$false;AcceptanceChanged=$false;OriginJoin=$join}
