param([string]$RepoRoot,[string]$Name,[guid]$RequestId,[string]$SourcePath,[string]$Case)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$script:stageWatch=[Diagnostics.Stopwatch]::StartNew();$script:stageCount=0
function Write-ContentNativeStage([string]$Stage) {
    # Fixture observations only: no source, token, identity, or custody reads.
    if($script:stageCount -ge 32) {return}
    $script:stageCount++
    [Console]::Out.WriteLine('content-native-stage='+([ordered]@{DiagnosticOnly=$true;Sequence=$script:stageCount;
        Stage=$Stage;ElapsedMilliseconds=$script:stageWatch.ElapsedMilliseconds} | ConvertTo-Json -Compress))
    [Console]::Out.Flush()
}
Write-ContentNativeStage 'load_started'
$readProfile=$Case.StartsWith('content-native-read-profile-',[StringComparison]::Ordinal)
$terminalSelection=$Case.StartsWith('content-native-terminal-',[StringComparison]::Ordinal)
$selectionComponentProfile=$terminalSelection -or $Case.StartsWith('content-native-selection-component-profile-',[StringComparison]::Ordinal) -or $Case -ceq 'content-native-scale-profile'
$selectionProfile=$Case.StartsWith('content-native-selection-profile-',[StringComparison]::Ordinal) -or $selectionComponentProfile
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentAcquisitionLoader.ps1') $RepoRoot -ReadProfile:$readProfile -SelectionProfile:$selectionProfile -SelectionComponentProfile:$selectionComponentProfile -TerminalSelection:$terminalSelection
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentSelectionOutput.ps1')
if($readProfile) {. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentReadProfile.ps1')}
Write-ContentNativeStage 'load_returned'
$mode=$Case.Substring('content-native-'.Length);$flags=[Reflection.BindingFlags]'Instance,NonPublic'
if($mode.StartsWith('read-profile-',[StringComparison]::Ordinal)) {$mode=$mode.Substring('read-profile-'.Length)}
if($mode.StartsWith('selection-profile-',[StringComparison]::Ordinal)) {$mode=$mode.Substring('selection-profile-'.Length)}
if($mode.StartsWith('selection-component-profile-',[StringComparison]::Ordinal)) {$mode=$mode.Substring('selection-component-profile-'.Length)}
$signals=@{};$worker=$null;$reader=$null;$custody=$null
try {
    foreach($part in @('begin','attached','release')) {$signals[$part]=[Threading.EventWaitHandle]::OpenExisting($Name+'.test.worker.'+$part)}
    Write-ContentNativeStage 'begin_wait_started'
    if(-not $signals.begin.WaitOne(30000)) {throw 'Content native begin deadline'}
    Write-ContentNativeStage 'begin_wait_returned'
    Write-ContentNativeStage 'attach_started'
    $worker=[NLink.Tools.LocalRetainedSourceRequest]::TryAttachFrontier($Name,$RequestId,$SourcePath,'frontier-gui-run','frontier-gui-scenario')
    Write-ContentNativeStage 'attach_returned'
    if($null -eq $worker) {throw 'Content native original worker missing'}
    Write-ContentNativeStage 'open_source_started'
    $owned=$worker.FrontierInputs;$reader=$worker.TryOpenSource()
    Write-ContentNativeStage 'open_source_returned'
    if($mode -ceq 'trace-timeout') {
        Write-ContentNativeStage 'timeout_control_started'
        [Console]::Error.WriteLine('content-native-timeout-control');[Console]::Error.Flush()
        # The parent retains its original 30-second barrier and releases cleanup.
        [void]$signals.release.WaitOne()
        throw 'Content native timeout control released'
    }
    Write-ContentNativeStage 'source_read_started'
    if($readProfile) {[NLink.Tests.NativeContentReadProfileForTest]::Begin($owned)}
    try {$events=[object[]]@(Read-FileTransferLogEvents -LogFiles @($SourcePath) -OwnedSource $reader)}
    finally {if($readProfile) {[NLink.Tests.NativeContentReadProfileForTest]::Observe('source_read_finally')}}
    Write-ContentNativeStage 'source_read_returned'
    if(-not $reader.InputReadComplete -or $events.Count -lt 3) {throw 'Content native original source incomplete'}
    Write-ContentNativeStage 'materialize_started'
    [string[]]$full=@($events | ForEach-Object RawLine);[int[]]$origins=@($events | ForEach-Object LineNumber)
    [string[]]$compact=@($full[-1]);[int[]]$compactOrigins=@($full.Length)
    $context=[object]::new();$fullView=[pscustomobject]@{Fixture='native-input-identity-only'};$compactView=[pscustomobject]@{Fixture='native-input-identity-only-compact'}
    Write-ContentNativeStage 'materialize_returned'
    Write-ContentNativeStage 'evaluations_started'
    $custody=$worker.TryBeginFrontierPrimary($reader,$context,$full,$origins,$compact,$compactOrigins)
    if($null -eq $custody -or -not $custody.RecordFull($full,$fullView) -or -not $custody.RecordCompact($compact,$compactView)) {throw 'Content native original evaluations missing'}
    Write-ContentNativeStage 'evaluations_returned'
    Write-ContentNativeStage 'take_started'
    $pair=$custody.Take($context,$full,$compact,$fullView,$compactView)
    Write-ContentNativeStage 'take_returned'
    if($null -eq $pair) {throw 'Content native original pair missing'}
    if($terminalSelection) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentTerminalSelection.ps1')
        return
    }
    $selected=$events[-1];$script:callbacks=0;$script:retainedCursor=$null
    $select=[Func[NLink.Tools.LocalRetainedSourceRequest+FrontierWorkerInputs+EvaluationCustody+ContentInputCursorForTest,int[]]]{
        param($cursor)
        $script:callbacks++;$script:retainedCursor=$cursor
        # The original primary chooses exact ordinals. This is an identity-only
        # fixture, not a content or route-authority query.
        if($mode -ceq 'over-witness') {return [int[]](1..65)}
        if($mode -ceq 'omit-selected') {return [int[]]@(1)}
        if($mode -ceq 'mutate-during') {$full[0]=[string]::new($full[0].ToCharArray())}
        return [int[]]@(1,$cursor.Count)
    }
    $beforeVisits=$owned.GetType().GetField('evaluationVisits',$flags).GetValue($owned)
    Write-ContentNativeStage 'default_off_started'
    $watch=$owned.GetType().GetField('work',$flags).GetValue($owned);$beforeTicks=$watch.ElapsedTicks
    $off=$custody.TryAcquireContentInputsForTest($false,$pair,$context,$full,$compact,$fullView,$compactView,$events,$selected,$select)
    Write-ContentNativeStage 'default_off_returned'
    if($null -ne $off -or $script:callbacks -ne 0 -or $watch.ElapsedTicks -ne $beforeTicks -or $owned.GetType().GetField('evaluationVisits',$flags).GetValue($owned) -ne $beforeVisits) {throw 'Default-off content adapter changed native work'}
    $candidateFull=$full;$candidateView=$fullView;$candidateSelected=$selected
    switch($mode) {
        'copied-array' {$candidateFull=[string[]]$full.Clone()}
        'copied-view' {$candidateView=$fullView.PSObject.Copy()}
        'copied-event' {$candidateSelected=$selected.PSObject.Copy()}
        'record-budget' {$owned.GetType().GetField('evaluationVisits',$flags).SetValue($owned,[long][NLink.Tools.LocalPublicationInputLimits]::ProducerRecords)}
        'code-unit-budget' {$owned.GetType().GetField('sourceUnits',$flags).SetValue($owned,[long]([NLink.Tools.LocalPublicationInputLimits]::ProducerCodeUnits+1))}
        'stale-owner' {$reader.Dispose()}
    }
    if($mode -cin @('time-budget','retained-extended','retained-expired','retained-scope')) {
        # Small fault controls only; the real scale clock is never injected.
        $elapsed=@($watch.GetType().GetFields($flags) | Where-Object {$_.Name -match '^_?elapsed$'})[0]
        $seconds=if($mode -cin @('retained-extended','retained-scope')) {70} elseif($mode -ceq 'retained-expired') {181} else {151}
        $elapsed.SetValue($watch,[long]([Diagnostics.Stopwatch]::Frequency*$seconds))
    }
    if($mode -ceq 'retained-scope') {
        $scope=$owned.GetType().GetField('scope',$flags).GetValue($owned)
        $header=$scope.GetType().GetField('header',$flags).GetValue($scope);$header[0]=$header[0] -bxor 1
    }
    $entryTicks=$watch.ElapsedTicks
    Write-ContentNativeStage 'content_selection_started'
    if($selectionProfile) {[NLink.Tests.NativeContentSelectionProfileForTest]::Begin($owned)}
    $result=$custody.TryAcquireContentInputsForTest($true,$pair,$context,$candidateFull,$compact,$candidateView,$compactView,$events,$candidateSelected,$select)
    if($selectionProfile) {[NLink.Tests.NativeContentSelectionProfileForTest]::End()}
    Write-ContentNativeStage 'content_selection_returned'
    Write-NativeContentSelectionOutput $result $script:callbacks
    Write-ContentNativeStage 'assertions_started'
    $nullResult=$mode -cin @('copied-array','copied-view','stale-owner')
    $reason=@{'copied-event'='content_selected_event_ambiguous';'over-witness'='content_witness_limit';'omit-selected'='content_selected_event_omitted';
        'record-budget'='content_input_budget';'time-budget'='content_input_budget';'code-unit-budget'='content_input_budget';
        'retained-expired'='content_input_budget';'retained-scope'='content_native_custody';'stale-owner'='content_native_custody';'mutate-during'='content_original_index'}
    if($nullResult) {if($null -ne $result -or $script:callbacks -ne 0) {throw 'Foreign content input acquired custody'}}
    elseif($mode -cnotin @('valid','scale','scale-profile','retained-extended')) {
        if($result.Status -cne 'unproven' -or $result.FirstMissingEdge -cne $reason[$mode] -or $result.GetWitnesses().Length) {throw ('Content rejection differs: '+($result | ConvertTo-Json -Compress))}
    } else {
        if($mode -cin @('scale','scale-profile') -and ($full.Length -lt 102215 -or $worker.SourceBytes -lt 173102783)) {throw 'Content native scale shape insufficient'}
        if($result.Status -cne 'prepared_native_input_selection' -or $result.GetWitnesses().Length -ne 2 -or $script:callbacks -ne 1) {throw 'Content original input selection missing'}
        $rows=$result.GetWitnesses();$rows[0]=$null
        if($null -eq $result.GetWitnesses()[0]) {throw 'Detached row array changed stored input selection'}
        foreach($row in $result.GetWitnesses()) {
            $physical=$custody.GetPhysicalOrigin($pair,$full,$fullView,$row.FullOrdinal)
            if($row.PhysicalLine -ne $physical -or $row.PhysicalLine -eq $row.FullOrdinal) {throw 'Content native physical mapping lost'}
        }
        $failed=$false;try {$script:retainedCursor.Line(1) | Out-Null} catch {$failed=$_.Exception.Message -match 'content_cursor_ended'}
        if(-not $failed) {throw 'Ended content cursor remained readable'}
    }
    if($null -ne $result -and ($result.ContentCoverageProven -or $result.ContentCoverageMissingEdge -cne 'content_family_coverage_unavailable')) {throw 'Input selection acquired content authority'}
    if($mode -cnotin @('stale-owner','mutate-during','retained-expired','retained-scope')) {
        if(-not $custody.Owns($pair,$context,$full,$compact,$fullView,$compactView)) {throw 'Content omission retired original custody'}
        if($null -ne $custody.GetType().GetField('witnessBatch',$flags).GetValue($custody) -or
            $custody.GetType().GetField('witnessesAttempted',$flags).GetValue($custody) -or
            $custody.GetType().GetField('resolutionAttempted',$flags).GetValue($custody)) {throw 'Content adapter consumed frontier witness operation'}
        if(-not $worker.IsCurrent -or -not $owned.IsCurrent -or -not $reader.InputReadComplete) {throw 'Optional selection lost retained source custody'}
        $sourceOrigins=$reader.GetFrontierPhysicalOrigins(([int[]]@(1,$full.Length)),([string[]]@($full[0],$full[-1])))
        if($null -eq $sourceOrigins -or $sourceOrigins[0] -ne $reader.GetFrontierPhysicalOrigin(1,$full[0]) -or
            $sourceOrigins[1] -ne $custody.GetPhysicalOrigin($pair,$full,$fullView,$full.Length)) {throw 'Retained source/evaluation origins differ'}
        if($mode -cin @('time-budget','retained-extended')) {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/NativeContentRetainedChecks.ps1')
        }
        if($watch.IsRunning -or $watch.ElapsedTicks -lt $entryTicks -or $watch.ElapsedMilliseconds -gt 180000) {throw 'Retained checks reset or exceeded original cumulative clock'}
        [Console]::Out.WriteLine('content-native-retained='+([ordered]@{DiagnosticOnly=$true;CumulativeMilliseconds=$watch.ElapsedMilliseconds;
            WorkRunning=$watch.IsRunning;EvaluationVisits=$owned.GetType().GetField('evaluationVisits',$flags).GetValue($owned);
            ContentCoverageProven=$false;AddedAuthorityEvaluations=0} | ConvertTo-Json -Compress))
    } elseif($mode -cin @('retained-expired','retained-scope')) {
        if($custody.Owns($pair,$context,$full,$compact,$fullView,$compactView) -or $worker.IsCurrent -or $owned.IsCurrent -or
            -not $owned.GetType().GetField('closed',$flags).GetValue($owned) -or
            $null -ne $custody.GetType().GetField('full',$flags).GetValue($custody) -or
            $watch.IsRunning -or $watch.ElapsedTicks -lt $entryTicks) {throw 'Expired retained custody reopened or reset clock'}
    }
    Write-ContentNativeStage 'assertions_returned'
    [void]$signals.attached.Set();Write-ContentNativeStage 'release_wait_started'
    if(-not $signals.release.WaitOne(30000)) {throw 'Content native release deadline'}
    Write-ContentNativeStage 'release_wait_returned'
    [ordered]@{Mode=$mode;IdentityOnly=$true;AddedAuthorityEvaluations=0;ContentCoverageProven=$false;
        SourceRecords=$full.Length;Result=$result;CallbackCount=$script:callbacks;AcceptanceChanged=$false} | ConvertTo-Json -Depth 6 -Compress
    'content-native-acquisition='+$mode
    'frontier-primary-worker=content-native-acquisition'
} finally {
    Write-ContentNativeStage 'cleanup_started'
    if($signals.ContainsKey('attached')) {[void]$signals.attached.Set()}
    if($null -ne $custody) {$custody.Dispose()};if($null -ne $reader) {$reader.Dispose()};if($null -ne $worker) {$worker.Dispose()}
    foreach($signal in $signals.Values) {$signal.Dispose()}
    Write-ContentNativeStage 'cleanup_returned'
}
