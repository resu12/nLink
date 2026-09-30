param($Context,$Custody,$Evaluations,$Frame,$Gui,[string]$ReadStatus,[string]$ArtifactDir,[int]$EvaluationCount)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'TerminalPreVerdictInventory.ps1')
function Get-FileTransferCoordinatorSnapshotEvaluation {throw 'Integrity shadow cannot evaluate authority'}
$Context.Work.Start()
try {
    foreach($name in @('filetransfer-operator-verdict.txt','transfer-terminal-summary.txt','filetransfer-retained-compact-summary.json')) {
        if([IO.File]::Exists((Join-Path $ArtifactDir $name))) {throw 'integrity_shadow_after_report'}
    }
    $owned=$Context.SourceOwner.CollectorMembershipResultForTest
    $priorVisits=7L*$Context.FullLines.Length+4L*$Context.CompactLines.Length+$owned.RecordVisits+$owned.OriginJoinRecordVisitsForTest+$owned.TerminalPayloadRecordVisitsForTest
    $inventory=Get-TerminalPreVerdictInventory $Frame -AvailableVisits (4096L-$priorVisits) `
        -AvailableUnits (1073741824L-$owned.CodeUnitVisits-$owned.TerminalPayloadCodeUnitsForTest)
    $files=$Context.SourceOwner.TerminalFilesForTest
    $reads=$Context.SourceOwner.CollectorWriterRecordsReadForTest
    function Consume($Result,$Owner,$Files,$Reader,$C,$Cust,$Pair,$Full,$Compact,$FullView,$CompactView,$Clock,$Visits,$Units) {
        return $Result.TryConsumeTerminalIntegrityForTest($Owner,$Files,$Reader,$Cust,$Pair,$C,$Full,$Compact,$FullView,$CompactView,$Clock,$Visits,$Units)
    }
    $clone=[object].GetMethod('MemberwiseClone',[Reflection.BindingFlags]'Instance,NonPublic')
    $cases=@('result','owner','files','reader','context','custody','pair','full','compact','view','compact_view','clock')
    foreach($case in $cases) {
        $r=$owned; $o=$Context.SourceOwner; $f=$files; $reader=$Context.SourceReader; $c=$Context; $cust=$Custody; $pair=$Evaluations
        $full=$Context.FullLines; $compact=$Context.CompactLines; $view=$Context.FullView; $compactView=$Context.CompactView; $clock=$Context.Work
        switch($case) {
            'result' {$r=$clone.Invoke($r,@()); $r.Dispose()}
            'owner' {$o=$clone.Invoke($o,@())}
            'files' {$f=$clone.Invoke($f,@()); $f.Dispose()}
            'reader' {$reader=$clone.Invoke($reader,@())}
            'context' {$c=$Context.PSObject.Copy()}
            'custody' {$cust=$clone.Invoke($cust,@())}
            'pair' {$pair=$null}
            'full' {$full=$full.Clone()}
            'compact' {$compact=$compact.Clone()}
            'view' {$view=$null}
            'compact_view' {$compactView=$null}
            'clock' {$clock=[Diagnostics.Stopwatch]::StartNew()}
        }
        try {
            if(Consume $r $o $f $reader $c $cust $pair $full $compact $view $compactView $clock $inventory.RecordVisits $inventory.CodeUnitVisits) {throw "integrity_shadow_owner:$case"}
        } finally {if($case -ceq 'clock') {$clock.Stop()}}
    }
    if($owned.TerminalIntegrityCurrentForTest) {throw 'integrity_shadow_precontrols_consumed'}
    $mode=[string]$env:NLINK_TEST_PRE_VERDICT_CASE
    $visits=$inventory.RecordVisits; $units=$inventory.CodeUnitVisits; $saved=$null; $field=$null
    try {
        if($mode -ceq 'budget') {$visits=4096L}
        if($mode -ceq 'units') {$units=1073741824L}
        if($mode -ceq 'expired') {
            $field=@($Context.Work.GetType().GetFields([Reflection.BindingFlags]'Instance,NonPublic') | Where-Object {$_.Name -cin @('_elapsed','elapsed')})[0]
            $saved=$field.GetValue($Context.Work); $field.SetValue($Context.Work,21L*[Diagnostics.Stopwatch]::Frequency)
        }
        if($mode -ceq 'disposed') {$owned.Dispose()}
        if($mode -ceq 'stopped') {$Context.Work.Stop()}
        $consumed=Consume $owned $Context.SourceOwner $files $Context.SourceReader $Context $Custody $Evaluations `
            $Context.FullLines $Context.CompactLines $Context.FullView $Context.CompactView $Context.Work $visits $units
        if(Consume $owned $Context.SourceOwner $files $Context.SourceReader $Context $Custody $Evaluations `
            $Context.FullLines $Context.CompactLines $Context.FullView $Context.CompactView $Context.Work $inventory.RecordVisits $inventory.CodeUnitVisits) {throw 'integrity_shadow_repeated'}
    } finally {if($null -ne $field) {$field.SetValue($Context.Work,$saved)}}
    if($Context.SourceOwner.CollectorWriterRecordsReadForTest -ne $reads) {throw 'integrity_shadow_extra_writer_read'}
    if($mode -ceq 'unknown') {$ReadStatus='unknown'}
    $state=Get-TerminalIntegrityShadowState $ReadStatus $Gui $consumed
    if($Context.Work.ElapsedMilliseconds -gt 20000) {throw 'integrity_shadow_work_limit'}
    [pscustomobject]@{Schema='nlink.test-pre-verdict-integrity/v1';State=$state;Consumed=$consumed;GuiReadStatus=$ReadStatus;
        BeforeDecisionReports=$true;Evaluations=$EvaluationCount;RejectedOwnerControls=$cases.Count;
        RepeatedConsumptionRejected=$true;AdditionalWriterReads=0;PrimaryPayloadHashPasses=0;Inventory=$inventory;
        CumulativeRecordVisits=7L*$Context.FullLines.Length+4L*$Context.CompactLines.Length+$owned.RecordVisits+$owned.OriginJoinRecordVisitsForTest+$owned.TerminalPayloadRecordVisitsForTest+$inventory.RecordVisits;
        CumulativeMilliseconds=$Context.Work.ElapsedMilliseconds;AcceptanceChanged=$false;CanonicalScenarioProven=$false}
} finally {$Context.Work.Stop()}
