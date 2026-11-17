# Dot-sourced only after the original worker's source/evaluation/pair bootstrap.
# The caller returns immediately after this script, then runs its normal finally.
$terminalMode=$mode.Substring('terminal-'.Length)
if($terminalMode -cnotin @('valid','inherited-time','time-budget','record-budget','code-unit-budget','copied-array','copied-view',
    'copied-event','mutate-during','remaining-consumer','frontier-slot','over-witness','omit-selected','stale-owner','scale')) {throw 'Unknown terminal selection fixture'}
Write-ContentNativeStage 'terminal_preentry_started'
if(-not $custody.Owns($pair,$context,$full,$compact,$fullView,$compactView)) {throw 'Terminal pre-entry custody missing'}
$oracle=@{}
foreach($ordinal in @(1,$full.Length)) {
    $oracle[$ordinal]=$custody.GetPhysicalOrigin($pair,$full,$fullView,$ordinal)
    if($oracle[$ordinal] -le 0) {throw 'Terminal pre-entry origin missing'}
}
Write-ContentNativeStage 'terminal_preentry_returned'
$selected=$events[-1];$script:callbacks=0;$script:retainedCursor=$null
$select=[Func[NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+EvaluationCustody+ContentInputCursorForTest,int[]]]{
    param($cursor)
    $script:callbacks++;$script:retainedCursor=$cursor
    if($terminalMode -ceq 'over-witness') {return [int[]](1..65)}
    if($terminalMode -ceq 'omit-selected') {return [int[]]@(1)}
    if($terminalMode -ceq 'mutate-during') {$full[0]=[string]::new($full[0].ToCharArray())}
    return [int[]]@(1,$cursor.Count)
}
$watch=$owned.GetType().GetField('work',$flags).GetValue($owned)
$visitsField=$owned.GetType().GetField('evaluationVisits',$flags)
$beforeVisits=$visitsField.GetValue($owned);$beforeTicks=$watch.ElapsedTicks
$off=$custody.TryAcquireTerminalContentInputsForTest($false,$true,$pair,$context,$full,$compact,$fullView,$compactView,$events,$selected,$select)
if($null -ne $off -or $script:callbacks -ne 0 -or $visitsField.GetValue($owned) -ne $beforeVisits -or $watch.ElapsedTicks -ne $beforeTicks) {throw 'Default-off terminal probe changed work'}
$candidateFull=$full;$candidateView=$fullView;$candidateSelected=$selected
switch($terminalMode) {
    'copied-array' {$candidateFull=[string[]]$full.Clone()}
    'copied-view' {$candidateView=$fullView.PSObject.Copy()}
    'copied-event' {$candidateSelected=$selected.PSObject.Copy()}
    'record-budget' {$visitsField.SetValue($owned,[long][NLink.Tools.LocalPublicationInputLimits]::ProducerRecords)}
    'code-unit-budget' {$owned.GetType().GetField('sourceUnits',$flags).SetValue($owned,[long][NLink.Tools.LocalPublicationInputLimits]::ProducerCodeUnits)}
    'frontier-slot' {$custody.GetType().GetField('resolutionAttempted',$flags).SetValue($custody,$true)}
    'stale-owner' {$reader.Dispose()}
}
if($terminalMode -cin @('inherited-time','time-budget')) {
    # Fault injection in small controls only; the actual scale clock is untouched.
    $elapsed=@($watch.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
    $seconds=if($terminalMode -ceq 'inherited-time') {70} else {151}
    $elapsed.SetValue($watch,[long]([Diagnostics.Stopwatch]::Frequency*$seconds))
}
$entryTicks=$watch.ElapsedTicks;$entryVisits=$visitsField.GetValue($owned)
Write-ContentNativeStage 'content_selection_started'
[NLink.Tests.NativeContentSelectionProfileForTest]::Begin($owned)
$result=$custody.TryAcquireTerminalContentInputsForTest($true,($terminalMode -cne 'remaining-consumer'),$pair,$context,$candidateFull,$compact,$candidateView,$compactView,$events,$candidateSelected,$select)
[NLink.Tests.NativeContentSelectionProfileForTest]::End()
Write-ContentNativeStage 'content_selection_returned'
Write-NativeContentSelectionOutput $result $script:callbacks
# Everything below is detached result validation or passive bookkeeping.
$nullResult=$terminalMode -cin @('copied-array','copied-view','stale-owner','remaining-consumer','frontier-slot')
$success=$terminalMode -cin @('valid','inherited-time','scale')
if($nullResult) {
    if($null -ne $result -or $script:callbacks -ne 0 -or $visitsField.GetValue($owned) -ne $entryVisits) {throw 'Ineligible terminal fixture acquired inputs'}
} elseif($success) {
    if($terminalMode -ceq 'scale' -and ($full.Length -lt 102215 -or $worker.SourceBytes -lt 173102783)) {throw 'Terminal native scale shape insufficient'}
    if($result.Status -cne 'prepared_native_input_selection' -or $result.GetWitnesses().Length -ne 2 -or $script:callbacks -ne 1) {throw 'Terminal original input selection missing'}
    foreach($row in $result.GetWitnesses()) {
        if($row.PhysicalLine -ne $oracle[$row.FullOrdinal] -or $row.PhysicalLine -eq $row.FullOrdinal) {throw 'Terminal physical mapping differs from pre-entry oracle'}
    }
    $rows=$result.GetWitnesses();$rows[0]=$null
    if($null -eq $result.GetWitnesses()[0]) {throw 'Terminal detached row array changed selection'}
} else {
    $reason=@{'copied-event'='content_selected_event_ambiguous';'mutate-during'='content_original_index';'time-budget'='content_input_budget';
        'record-budget'='content_input_budget';'code-unit-budget'='content_input_budget';'over-witness'='content_witness_limit';'omit-selected'='content_selected_event_omitted'}
    if($result.Status -cne 'unproven' -or $result.FirstMissingEdge -cne $reason[$terminalMode] -or $result.GetWitnesses().Length) {throw 'Terminal rejection differs'}
}
if($null -ne $script:retainedCursor) {
    $ended=$false;try {$script:retainedCursor.Line(1) | Out-Null} catch {$ended=$_.Exception.Message -match 'content_cursor_ended'}
    if(-not $ended) {throw 'Terminal cursor remained readable'}
}
if($null -ne $result -and ($result.ContentCoverageProven -or $result.ContentCoverageMissingEdge -cne 'content_family_coverage_unavailable')) {throw 'Terminal selection acquired content authority'}
if($watch.IsRunning -or $watch.ElapsedTicks -lt $entryTicks -or $custody.GetType().GetField('terminalContentAcquisitionForTest',$flags).GetValue($custody)) {throw 'Terminal clock or active window differs'}
if($null -ne $custody.GetType().GetField('witnessBatch',$flags).GetValue($custody) -or
    $custody.GetType().GetField('witnessesAttempted',$flags).GetValue($custody) -or
    $custody.GetType().GetField('resolutionAttempted',$flags).GetValue($custody) -ne ($terminalMode -ceq 'frontier-slot') -or
    $custody.GetType().GetField('payloadAttempted',$flags).GetValue($custody)) {throw 'Terminal selection changed frontier slots'}
$closed=$owned.GetType().GetField('closed',$flags).GetValue($owned)
if($closed -ne ($terminalMode -ceq 'stale-owner')) {throw 'Terminal omission retired inputs'}
[Console]::Out.WriteLine('content-native-terminal='+([ordered]@{DiagnosticOnly=$true;TerminalOnly=$true;Mode=$terminalMode;
    EntryWorkTicks=$entryTicks;ReturnWorkTicks=$watch.ElapsedTicks;WorkRunning=$watch.IsRunning;TerminalActive=$false;Closed=$closed;
    SourceRecords=$full.Length;ContentCoverageProven=$false;NormalScaleGateReplaced=$false;PostReturnCustodyUsabilityProven=$false} | ConvertTo-Json -Compress))
[void]$signals.attached.Set()
if(-not $signals.release.WaitOne(30000)) {throw 'Terminal release deadline'}
'content-native-acquisition=terminal-'+$terminalMode
'frontier-primary-worker=content-native-acquisition'
