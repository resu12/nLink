param($Context,$Custody,$Evaluations,$Files,$Terminals,[string]$Mutation='')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
if($Mutation -cnotin @('','token','swapped','coordinate','duplicate','direction','budget','expired','foreign','origin')) {throw 'terminal_payload_mutation'}
$owned=$Context.SourceOwner.CollectorMembershipResultForTest
$clone=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic')
function Join-Payload($Result,$Owner,$F,$Reader,$C,$Cust,$Pair,$Full,$Compact,$FullView,$CompactView,$Rows,$Clock) {
    return $Result.TryJoinTerminalPayloadForTest($Owner,$F,$Reader,$Cust,$Pair,$C,$Full,$Compact,$FullView,$CompactView,$Rows,$Clock)
}
$cases=@('result','owner','files','reader','context','custody','pair','full','compact','view','compact_view','clock')
foreach($case in $cases) {
    $result=$owned; $o=$Context.SourceOwner; $f=$Files; $r=$Context.SourceReader; $c=$Context; $cust=$Custody; $p=$Evaluations
    $full=$Context.FullLines; $compact=$Context.CompactLines; $view=$Context.FullView; $compactView=$Context.CompactView; $clock=$Context.Work
    switch($case) {
        'result' {$result=$clone.Invoke($result,@()); $result.Dispose()}
        'owner' {$o=$clone.Invoke($o,@())}
        'files' {$f=$clone.Invoke($f,@()); $f.Dispose()}
        'reader' {$r=$clone.Invoke($r,@())}
        'context' {$c=$Context.PSObject.Copy()}
        'custody' {$cust=$clone.Invoke($cust,@())}
        'pair' {$p=$null}
        'full' {$full=$full.Clone()}
        'compact' {$compact=$compact.Clone()}
        'view' {$view=$null}
        'compact_view' {$compactView=$null}
        'clock' {$clock=[Diagnostics.Stopwatch]::StartNew()}
    }
    try {if(Join-Payload $result $o $f $r $c $cust $p $full $compact $view $compactView $Terminals $clock) {throw "terminal_payload_owner:$case"}}
    finally {if($case -ceq 'clock') {$clock.Stop()}}
}
if($owned.TerminalPayloadRecordVisitsForTest -ne 0 -or $owned.TerminalPayloadJoinedForTest) {throw 'terminal_payload_precontrols_consumed'}
$reads=$Context.SourceOwner.CollectorWriterRecordsReadForTest
$candidate=$Terminals; $field=$null; $target=$null; $saved=$null; $savedSecond=$null
try {
    if($Mutation -cin @('token','swapped','coordinate')) {
        $native=$Files.GetType().GetField('files',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($Files)
        $target=$native.GetType().GetField('transfer',[Reflection.BindingFlags]'Instance,NonPublic').GetValue($native)
        $name=if($Mutation -ceq 'token') {'Transfer'} else {'InboundLine'}
        $field=$target.GetType().GetField($name,[Reflection.BindingFlags]'Instance,NonPublic'); $saved=$field.GetValue($target)
        if($Mutation -ceq 'token') {$field.SetValue($target,'fte1-'+('a'*24))}
        elseif($Mutation -ceq 'coordinate') {$field.SetValue($target,4096)}
        else {
            $second=$target.GetType().GetField('OutboundLine',[Reflection.BindingFlags]'Instance,NonPublic'); $savedSecond=$second.GetValue($target)
            $field.SetValue($target,$savedSecond); $second.SetValue($target,$saved)
        }
    }
    if($Mutation -ceq 'duplicate') {$candidate=@($Terminals[0],$Terminals[0])}
    if($Mutation -ceq 'direction') {
        $copy=$Terminals[0].PSObject.Copy()
        $copy.RawLine=if($copy.RawLine.Contains('direction=inbound')) {$copy.RawLine.Replace('direction=inbound','direction=outbound')} else {$copy.RawLine.Replace('direction=outbound','direction=inbound')}
        $candidate=@($copy,$Terminals[1])
    }
    if($Mutation -ceq 'foreign') {$candidate=@($Terminals | Select-Object -Last 2)}
    if($Mutation -ceq 'budget') {
        $target=$owned; $field=$owned.GetType().GetField('originJoinVisits',[Reflection.BindingFlags]'Instance,NonPublic')
        $saved=$field.GetValue($target); $field.SetValue($target,4096L)
    }
    if($Mutation -ceq 'expired') {
        $target=$Context.Work
        $field=@($target.GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic') | Where-Object {$_.Name -cin @('_elapsed','elapsed')})[0]
        $saved=$field.GetValue($target); $field.SetValue($target,21L*[Diagnostics.Stopwatch]::Frequency)
    }
    $joined=Join-Payload $owned $Context.SourceOwner $Files $Context.SourceReader $Context $Custody $Evaluations `
        $Context.FullLines $Context.CompactLines $Context.FullView $Context.CompactView $candidate $Context.Work
    if($joined -ne (-not $Mutation) -or $owned.TerminalPayloadJoinedForTest -ne $joined) {throw 'terminal_payload_outcome'}
    if(Join-Payload $owned $Context.SourceOwner $Files $Context.SourceReader $Context $Custody $Evaluations `
        $Context.FullLines $Context.CompactLines $Context.FullView $Context.CompactView $candidate $Context.Work) {throw 'terminal_payload_repeated'}
} finally {
    if($null -ne $field) {$field.SetValue($target,$saved)}
    if($null -ne $savedSecond) {$second.SetValue($target,$savedSecond)}
}
if($Mutation -cnotin @('','origin','expired') -and (Join-Payload $owned $Context.SourceOwner $Files $Context.SourceReader $Context $Custody $Evaluations `
    $Context.FullLines $Context.CompactLines $Context.FullView $Context.CompactView @($Terminals | Select-Object -First 2) $Context.Work)) {throw 'terminal_payload_corrected_retry'}
if($Context.SourceOwner.CollectorWriterRecordsReadForTest -ne $reads) {throw 'terminal_payload_reread'}
$copied=$clone.Invoke($owned,@()); $copied.Dispose()
if($copied.TerminalPayloadJoinedForTest -or $owned.TerminalPayloadJoinedForTest -ne $joined) {throw 'terminal_payload_copied_result'}
[pscustomobject]@{Joined=$joined;Mutation=$Mutation;RejectedOwnerControls=$cases.Count;RepeatedJoinRejected=$true;
    RecordVisits=$owned.TerminalPayloadRecordVisitsForTest;CodeUnitVisits=$owned.TerminalPayloadCodeUnitsForTest;
    CumulativeRecordVisits=7L*$Context.FullLines.Length+4L*$Context.CompactLines.Length+$owned.RecordVisits+$owned.OriginJoinRecordVisitsForTest+$owned.TerminalPayloadRecordVisitsForTest;
    AdditionalWriterReads=$Context.SourceOwner.CollectorWriterRecordsReadForTest-$reads;PrimaryPayloadHashPasses=0;
    CumulativeMilliseconds=$Context.Work.ElapsedMilliseconds;AcceptanceChanged=$false}
