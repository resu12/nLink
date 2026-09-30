param($Context,$Custody,$Evaluations,$Files,[string]$Mutation='')
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
if($Mutation -or $null -ne $Context.WriterReader -or $Context.FullLines.Length -lt 2) {throw 'native_writer_adapter_probe_scope'}
$owner=$Context.SourceOwner; $source=$Context.SourceReader; $clock=$Context.Work
$clone=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic')
$cases=@('worker','files','source','context','clock','stopped','expired','frontier','full','compact','view','native','custody')
foreach($case in $cases) {
    $o=$owner; $f=$Files; $s=$source; $c=$Context; $w=$clock; $field=$null; $saved=$null; $property=''; $value=$null
    switch($case) {
        'worker' {$o=$clone.Invoke($o,@())} 'files' {$f=$clone.Invoke($f,@())}
        'source' {$s=$clone.Invoke($s,@())} 'context' {$c=$Context.PSObject.Copy()}
        'clock' {$w=[Diagnostics.Stopwatch]::StartNew()} 'stopped' {$clock.Stop()}
        'expired' {$field=$clock.GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic')|Where-Object {$_.Name -cin @('_elapsed','elapsed')}; $saved=$field.GetValue($clock); $field.SetValue($clock,([long][Diagnostics.Stopwatch]::Frequency*21))}
        'frontier' {$field=$owner.GetType().GetField('frontierRequested',[Reflection.BindingFlags]'Instance,NonPublic'); $saved=$field.GetValue($owner); $field.SetValue($owner,$true)}
        'full' {$property='FullLines'} 'compact' {$property='CompactLines'} 'view' {$property='FullView'} 'native' {$property='NativeOwner'}
        'custody' {$field=$owner.GetType().GetField('primary',[Reflection.BindingFlags]'Instance,NonPublic'); $saved=$field.GetValue($owner); $field.SetValue($owner,$null)}
    }
    if($property) {$value=$Context.$property; $Context.$property=if($value -is [array]) {$value.Clone()} else {$null}}
    try {
        if($null -ne $o.TryOpenNativeWitnessWriterForTest($f,$s,$c,$w)) {throw "native_writer_adapter_control:$case"}
    } finally {
        if($property) {$Context.$property=$value}
        if($null -ne $field) {$field.SetValue($(if($case -ceq 'expired'){$clock}else{$owner}),$saved)}
        if($case -ceq 'clock') {$w.Stop()}
        if($case -ceq 'stopped') {$clock.Start()}
    }
}
if($owner.CollectorWriterRecordsReadForTest -ne 0) {throw 'native_writer_adapter_precontrols_read'}
$reader=$owner.TryOpenNativeWitnessWriterForTest($Files,$source,$Context,$clock)
if($null -eq $reader) {throw 'native_writer_adapter_missing'}
try {
    if($reader.IsVerified -or $reader.LinesRead -ne 0) {throw 'native_writer_adapter_preverified'}
    if($null -ne $owner.TryOpenNativeWitnessWriterForTest($Files,$source,$Context,$clock) -or $null -ne $owner.TryOpenWriter()) {throw 'native_writer_adapter_reopened'}
    # Actual row at index one, not a fabricated failure and not the old index-zero lookup.
    $selected=1
    $fields=ConvertFrom-GuiSmokeLogLedgerStructuredFields $Context.FullLines[$selected]
    $membership=$reader.TryVerifyMembership($Context.FullLines,$Context.FullOriginal,[string]$fields.log_writer_id,
        [long]$fields.log_writer_sequence,$Context.NativeOwner.CodeUnitVisits,$clock)
    if($null -eq $membership -or $membership.SelectedIndex -ne $selected -or -not $reader.IsVerified -or
        $reader.LinesRead -ne $owner.WriterContext.Outputs[1].LineCount -or
        $membership.RecordVisits -ne 2*$reader.LinesRead+$Context.FullLines.Length) {throw 'native_writer_adapter_membership'}
    $visits=7L*$Context.FullLines.Length+4L*$Context.CompactLines.Length+$membership.RecordVisits
    if($visits -gt 4096 -or $clock.ElapsedMilliseconds -gt 20000) {throw 'native_writer_adapter_budget'}
    $result=[ordered]@{Status='verified_native_writer_acquisition_only';Verified=$true;SelectedIndex=$selected;
        PreReadControls=$cases.Count;WriterRecordsRead=$reader.LinesRead;RecordVisits=$membership.RecordVisits;
        CumulativeRecordVisits=$visits;CodeUnitVisits=$membership.CodeUnitVisits;PathReopened=$false;
        PreAcquisitionReads=0;RepeatedOpenRejected=$true;SelectedFailureProven=$false;AcceptanceChanged=$false}
} finally {$reader.Dispose()}
if($reader.IsVerified -or $null -ne $owner.TryOpenNativeWitnessWriterForTest($Files,$source,$Context,$clock) -or
    $null -ne $Context.WriterReader -or -not $Files.IsCurrent -or
    -not $Custody.Owns($Evaluations,$Context,$Context.FullLines,$Context.CompactLines,$Context.FullView,$Context.CompactView)) {
    throw 'native_writer_adapter_disposal'
}
$result.ReaderRetired=$true; $result.PrimaryAndCollectorSurvive=$true
[pscustomobject]$result
