param([string]$RepoRoot)
$ErrorActionPreference='Stop'; Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalInputReadLease.ps1')
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
$ast=Read-AttemptAst (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierDiagnosticState.ps1')
Invoke-Expression (Get-AttemptFunction $ast 'Fixture')
$flags=[Reflection.BindingFlags]'Instance,NonPublic'
$type=[NLink.Tools.LocalRetainedSourceRequest].Assembly.GetType('NLink.Tools.LocalFrontierDiagnosticState')
$capture=$type.GetMethod('Capture',[Reflection.BindingFlags]'Static,NonPublic')
$current=$type.GetMethod('IsCurrent',$flags)
$inputsType=[NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs]
function Meter([switch]$Evaluation) {
    # Inert budget receiver only: no ownership, handles, source, or authority.
    # Exercise the actual cumulative VisitEvaluation method, not a substitute.
    $inputs=[Runtime.Serialization.FormatterServices]::GetUninitializedObject($inputsType)
    $watch=[Diagnostics.Stopwatch]::new()
    $inputsType.GetField('work',$flags).SetValue($inputs,$watch)
    if($Evaluation) {
        $marker=[Runtime.Serialization.FormatterServices]::GetUninitializedObject([NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+EvaluationCustody])
        $inputsType.GetField('evaluation',$flags).SetValue($inputs,$marker)
    }
    return @{Inputs=$inputs;Watch=$watch;Visits=$inputsType.GetField('evaluationVisits',$flags);
        Action=[Delegate]::CreateDelegate([Action],$inputs,$inputsType.GetMethod('VisitEvaluation',$flags))}
}
function Capture($x,[int]$Count,$meter) {
    return $capture.Invoke($null,[object[]]@($x.Failure,$x.Candidates,$x.Proof,$x.Index,$x.Full,$x.Compact,$Count,$meter.Action))
}
function Reason([scriptblock]$Action) {
    try {[void](& $Action);return ''} catch {
        $caught=$_.Exception;while($null -ne $caught.InnerException){$caught=$caught.InnerException};return $caught.Message
    }
}
$maximum=[NLink.Tools.LocalPublicationInputLimits]::InputRecords
$evaluationLimit=[NLink.Tools.LocalPublicationInputLimits]::EvaluationVisits
if($evaluationLimit -ne 1073741824 -or [NLink.Tools.LocalPublicationInputLimits]::EvaluationMilliseconds -ne 60000 -or
    [NLink.Tools.LocalPublicationInputLimits]::ProducerRecords -ne 2097152 -or
    [NLink.Tools.LocalPublicationInputLimits]::ProducerMilliseconds -ne 20000 -or $maximum -ne 262144) {throw 'Evaluation limit changed an independent admission/time bound'}
$observations=@(foreach($count in @(0,1,73885,$maximum)) {
    $x=Fixture; $x.Index.AllEvents=[object[]](@($x.Failure.Event)*$count); $meter=Meter
    $guard=Capture $x $count $meter; $captured=[long]$meter.Visits.GetValue($meter.Inputs)
    $cells=[int]$type.GetField('cells',$flags).GetValue($guard)
    if($cells -ne 61 -or $captured -ne 61+3L*$count -or -not $current.Invoke($guard,$null)) {throw 'Event admission lost metadata or visit accounting'}
    $validated=[long]$meter.Visits.GetValue($meter.Inputs)-$captured
    if($validated -ne 69+3L*$count) {throw 'Event revalidation lost charged checks'}
    if($count -eq $maximum) {
        # The actual per-pass charge is checked above. Move only this inert
        # test meter near the cap; never execute a billion visits to exhaust it.
        $meter.Visits.SetValue($meter.Inputs,[long]($evaluationLimit-$validated))
        if(-not $current.Invoke($guard,$null) -or [long]$meter.Visits.GetValue($meter.Inputs) -ne $evaluationLimit) {throw 'Exact final in-budget validation rejected'}
        if((Reason {$current.Invoke($guard,$null)}) -cne 'frontier_evaluation_budget' -or
            [long]$meter.Visits.GetValue($meter.Inputs) -ne $evaluationLimit+1) {throw 'Repeated maximum validation escaped cumulative visit limit'}
    }
    [pscustomobject]@{Events=$count;MetadataCells=$cells;CaptureVisits=$captured;ValidationVisits=$validated}
})
foreach($fault in @('input-over','count-mismatch','lower-bound','null-event','raw-script','path-script','path-scalar','path-text')) {
    $x=Fixture;$second=$x.Failure.Event.PSObject.Copy();$x.Index.AllEvents=[object[]]@($x.Failure.Event,$second);$count=2;$expected='frontier_diagnostic_collection';$script:getters=0
    switch($fault) {
        'input-over' {$count=$maximum+1;$x.Index.AllEvents=[object[]](@($second)*$count)}
        'count-mismatch' {$count=1;$expected='frontier_diagnostic_index'}
        'lower-bound' {$x.Index.AllEvents=[Array]::CreateInstance([object],[int[]]@(2),[int[]]@(1))}
        'null-event' {$x.Index.AllEvents[1]=$null;$expected='frontier_diagnostic_owner'}
        'raw-script' {$second.PSObject.Properties.Remove('RawLine');$second | Add-Member ScriptProperty RawLine {$script:getters++;'original'};$expected='frontier_diagnostic_note:RawLine'}
        'path-script' {$second.PSObject.Properties.Remove('FilePath');$second | Add-Member ScriptProperty FilePath {$script:getters++;'source'};$expected='frontier_diagnostic_note:FilePath'}
        'path-scalar' {$second.FilePath=[object]::new();$expected='frontier_diagnostic_scalar'}
        'path-text' {$second.FilePath='x'*262145;$expected='frontier_diagnostic_text_capacity'}
    }
    $meter=Meter
    if((Reason {Capture $x $count $meter}) -cne $expected -or $script:getters -ne 0) {throw ('Input guard changed: '+$fault)}
}
# Event capacity must not absorb metadata cells, and neither storage category
# escapes the shared scalar text limit or the cumulative evaluation/work cap.
$x=Fixture;$meter=Meter;$guard=Capture $x 1 $meter
$cell=$type.GetMethod('Cell',$flags);$cells=$type.GetField('cells',$flags)
$cells.SetValue($guard,65535);$null=$cell.Invoke($guard,$null)
if($cells.GetValue($guard) -ne 65536 -or (Reason {$cell.Invoke($guard,$null)}) -cne 'frontier_diagnostic_state_capacity') {throw 'Metadata cell limit changed'}
$units=$type.GetField('units',$flags);$scalar=$type.GetMethod('Scalar',$flags)
$units.SetValue($guard,[long]([NLink.Tools.LocalPublicationInputLimits]::ProducerCodeUnits-1))
$null=$scalar.Invoke($guard,[object[]]@('x'))
if((Reason {$scalar.Invoke($guard,[object[]]@('x'))}) -cne 'frontier_diagnostic_text_capacity') {throw 'Shared scalar text limit changed'}
$meter=Meter;$meter.Visits.SetValue($meter.Inputs,[long][NLink.Tools.LocalPublicationInputLimits]::ProducerRecords)
$meter.Action.Invoke()
if([long]$meter.Visits.GetValue($meter.Inputs) -ne 2097153L) {throw 'Old producer cap still constrains evaluation visits'}
$meter=Meter;$meter.Visits.SetValue($meter.Inputs,[long]($evaluationLimit-1))
$meter.Action.Invoke()
if([long]$meter.Visits.GetValue($meter.Inputs) -ne $evaluationLimit -or
    (Reason {$meter.Action.Invoke()}) -cne 'frontier_evaluation_budget' -or
    [long]$meter.Visits.GetValue($meter.Inputs) -ne $evaluationLimit+1) {throw 'Exact cumulative visit boundary changed'}
$meter=Meter;$elapsed=@($meter.Watch.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
$elapsed.SetValue($meter.Watch,[long]([Diagnostics.Stopwatch]::Frequency*20));$meter.Action.Invoke()
$elapsed.SetValue($meter.Watch,[long]([Diagnostics.Stopwatch]::Frequency*20001/1000))
if((Reason {$meter.Action.Invoke()}) -cne 'frontier_evaluation_budget') {throw 'Exact cumulative work boundary changed'}
# Build the real split representation, then exhaust work before currentness.
$meter=Meter;$guard=Capture (Fixture) 1 $meter
$elapsed.SetValue($meter.Watch,[long]([Diagnostics.Stopwatch]::Frequency*21))
if((Reason {$current.Invoke($guard,$null)}) -cne 'frontier_evaluation_budget') {throw 'State currentness bypassed cumulative work'}
foreach($milliseconds in @(21000,60000,70000,180000,180001)) {
    $meter=Meter -Evaluation
    $elapsed=@($meter.Watch.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
    $elapsed.SetValue($meter.Watch,[long]([Diagnostics.Stopwatch]::Frequency*$milliseconds/1000))
    $reason=Reason {$meter.Action.Invoke()}
    if(($reason -ceq '') -ne ($milliseconds -le 180000) -or
        ($milliseconds -gt 180000 -and $reason -cne 'frontier_evaluation_budget') -or
        $meter.Watch.ElapsedMilliseconds -ne $milliseconds -or $meter.Watch.IsRunning -or
        [long]$meter.Visits.GetValue($meter.Inputs) -ne 1) {throw 'Evaluation work boundary or cumulative accounting changed'}
}
$observations | ConvertTo-Json -Compress
'frontier-diagnostic-split-capacity=verified'
