param([string]$RepoRoot,[string]$Directory)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tools/FileTransferOps/AnalyzerOrchestration.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferOps/LocalPublicationFailureWitness.ps1')
$path=Join-Path $Directory 'input.log'
# Synthetic selector/primary-preservation fixture, not generated publication proof.
$raw='[2026-09-14T12:00:00Z] [INFO] [Fixture] event=runtime_unlock_retry_dispatch_authority_rejected; decision_reason=retry_dispatch_successor_attempt_mismatch; session_id=s; transfer_id=t; log_writer_id=writer; log_writer_sequence=2; publication_use_kind=v4_pre_pump_request; publication_evidence_version=1'
[IO.File]::WriteAllLines($path,[string[]]@('non-event preamble',$raw),[Text.UTF8Encoding]::new($false))
$events=@(Read-FileTransferLogEvents -LogFiles @($path) -TailMinutes 0)
$index=New-FileTransferRetainedEventIndex -Events $events -LogFiles @($path)
$failure=[pscustomobject]@{Class='runtime_unlock_retry_dispatch_successor_attempt_mismatch';Reason='retry_dispatch_successor_attempt_mismatch';Event=$events[0]}
$baseline=Resolve-FileTransferPrimaryFailureWitness $index $failure
if($null -eq $baseline -or $baseline.SourceLine -ne 2 -or $baseline.WriterSequence -ne 2 -or
    $baseline.IndexRecordVisits -ne 1 -or $baseline.Writer -cne 'writer') { throw 'Exact physical/writer selection unavailable' }
$before=ConvertTo-Json -InputObject $events -Depth 10 -Compress
$cases=[Collections.Generic.List[string]]::new()
# A full admitted bucket has exactly one matching original event. The other
# entries are selector-only fixtures, never publication evidence.
$largeIndex=New-FileTransferRetainedEventIndex -Events $events -LogFiles @($path)
$other=[pscustomobject]@{FilePath=$path;LineNumber=3}
for($i=1;$i -lt 16384;$i++) { $largeIndex.EventsByName[$events[0].EventName].Add($other) }
$large=Resolve-FileTransferPrimaryFailureWitness $largeIndex $failure
if($null -eq $large -or $large.IndexRecordVisits -ne 16384 -or $large.SourceLine -ne 2) { throw 'Exact failure bucket cap rejected' }
$largeIndex.EventsByName[$events[0].EventName].Add($other)
if($null -ne (Resolve-FileTransferPrimaryFailureWitness $largeIndex $failure)) { throw 'Failure bucket cap+1 admitted' }
$cases.Add('exact-bucket-cap-and-cap-plus-one')
foreach($case in @('no-failure','cloned-event','wrong-source','relative-source','synthetic','duplicate-member','wrong-writer','noncanonical-sequence',
    'duplicate-raw-field','unsupported-use','missing-use','oversized-raw','oversized-bucket','too-many-fields','multiple-sources','changed-event-name')) {
    $localEvents=@(Read-FileTransferLogEvents -LogFiles @($path) -TailMinutes 0)
    $localIndex=New-FileTransferRetainedEventIndex -Events $localEvents -LogFiles @($path)
    $candidate=[pscustomobject]@{Class=$failure.Class;Reason=$failure.Reason;Event=$localEvents[0]}
    switch($case) {
        'no-failure' { $candidate=$null }
        'cloned-event' { $candidate.Event=$candidate.Event | ConvertTo-Json -Depth 8 | ConvertFrom-Json }
        'wrong-source' { $candidate.Event.FilePath=Join-Path $Directory 'foreign.log' }
        'relative-source' { $localIndex.LogFiles=@('input.log'); $candidate.Event.FilePath='input.log' }
        'synthetic' { $candidate.Event.LineNumber=0 }
        'duplicate-member' { $localIndex.EventsByName[$candidate.Event.EventName].Add($candidate.Event) }
        'wrong-writer' { $candidate.Event.Fields.log_writer_id='foreign' }
        'noncanonical-sequence' { $candidate.Event.RawLine=$raw.Replace('log_writer_sequence=2','log_writer_sequence=02') }
        'duplicate-raw-field' { $candidate.Event.RawLine=$raw+'; publication_use_kind=v4_pre_pump_request' }
        'unsupported-use' { $candidate.Event.RawLine=$raw.Replace('v4_pre_pump_request','future_kind') }
        'missing-use' { $candidate.Event.RawLine=$raw.Replace('; publication_use_kind=v4_pre_pump_request','') }
        'oversized-raw' { $candidate.Event.RawLine=$raw+('X'*262144) }
        'oversized-bucket' { for($i=1;$i -lt 16385;$i++) { $localIndex.EventsByName[$candidate.Event.EventName].Add($candidate.Event) } }
        'too-many-fields' { $candidate.Event.RawLine=$raw+((1..256 | ForEach-Object { '; extra'+$_+'=x' }) -join '') }
        'multiple-sources' { $localIndex.LogFiles=@($path,$path) }
        'changed-event-name' { $candidate.Event.RawLine=$raw.Replace('event=runtime_unlock_retry_dispatch_authority_rejected','event=other') }
    }
    if($null -ne (Resolve-FileTransferPrimaryFailureWitness $localIndex $candidate)) { throw "Unsafe witness selected: $case" }
    $cases.Add($case)
}
if((ConvertTo-Json -InputObject $events -Depth 10 -Compress) -cne $before) { throw 'Selection mutated original inputs' }
if($PSVersionTable.PSVersion.Major -gt 5) {
    # This legacy synthetic primary-preservation lane runs on Windows PowerShell.
    # Selection runs on both hosts; the genuine primary integration separately
    # covers both PS5 and PS7 (including the corrected stage writer).
    'primary-failure-witness='+(ConvertTo-Json -InputObject ([ordered]@{NegativeControls=$cases.ToArray();PrimaryAttempts=0
        PrimaryHost='Windows PowerShell only';ParentWorkerBindingVerified=$false;PublicationCompletionIssued=$false;LiveParentCaller=$false}) -Depth 4 -Compress)
    return
}
# Execute the complete primary compact workflow, not a replacement classifier.
# Compare optional-on/off attempts and count original authority invocations.
$realEvaluator=(Get-Command Get-FileTransferCoordinatorSnapshotEvaluation).ScriptBlock
$realWriter=(Get-Command Write-FileTransferCompactRetainedAnalysisArtifacts).ScriptBlock
$realEfficiencyWriter=(Get-Command Write-FileTransferEfficiencySummaryArtifacts).ScriptBlock
$script:evaluations=0
$script:evaluationCallers=[Collections.Generic.List[string]]::new()
$script:injectedSelectionFailures=0
function Get-FileTransferCoordinatorSnapshotEvaluation {
    param([AllowEmptyCollection()][string[]]$Lines)
    $script:evaluations++
    $script:evaluationCallers.Add([string](Get-PSCallStack)[1].FunctionName)
    & $realEvaluator -Lines $Lines
}
$env:NLINK_FILETRANSFER_RETAINED_ANALYSIS_COMPACT='1'
$primary=@{}
foreach($mode in @('baseline','witness','selection-fails','assigned-reference','primary-report-fails','incompatible-reference')) {
    $output=Join-Path $Directory $mode
    $selection=$null
    [uri]$incompatible=$null
    if($mode -ceq 'assigned-reference') { $selection='existing-owner' }
    $args=@{RepoRoot=$RepoRoot;ArtifactDir=$output;LogPath=@($path);TailMinutes=0}
    if($mode -cne 'baseline') { $args.LocalFailureWitness=[ref]$selection }
    if($mode -ceq 'incompatible-reference') { $args.LocalFailureWitness=[ref]$incompatible }
    if($mode -ceq 'selection-fails') {
        $needle='$preparedLocalFailureWitness = Resolve-FileTransferPrimaryFailureWitness'
        if($realWriter.ToString().Split([string[]]@($needle),[StringSplitOptions]::None).Length -ne 2) { throw 'Selection injection seam changed' }
        Set-Item Function:Write-FileTransferCompactRetainedAnalysisArtifacts -Value ([scriptblock]::Create(
            $realWriter.ToString().Replace($needle,"`$script:injectedSelectionFailures++; throw 'injected optional selector failure'; "+$needle).Replace(
                "Join-Path `$PSScriptRoot 'LocalPublicationFailureWitness.ps1'", "Join-Path `$RepoRoot 'tools/FileTransferOps/LocalPublicationFailureWitness.ps1'")))
    }
    if($mode -ceq 'primary-report-fails') {
        function Write-FileTransferEfficiencySummaryArtifacts { throw 'injected primary report failure' }
    }
    $startCount=$script:evaluations
    $failureMessage=''
    try { $result=Invoke-FileTransferRetainedAnalysis @args }
    catch { $failureMessage=$_.Exception.Message }
    if($mode -ceq 'primary-report-fails') {
        if($failureMessage -cne 'injected primary report failure') { throw 'Primary failure was lost or replaced' }
    }
    elseif($failureMessage) { throw $failureMessage }
    # This synthetic LegacyV0 primary fixture already evaluates three times.
    # Pin the count and caller sequence; optional selection must add none.
    if($script:evaluations-$startCount -ne 3) { throw "Authority invocation count changed: $mode actual=$($script:evaluations-$startCount)" }
    $callers=($script:evaluationCallers.GetRange($startCount,3) -join '|')
    if($mode -ceq 'baseline') {
        $baselineCallers='Write-FileTransferCompactRetainedAnalysisArtifacts|Write-FileTransferCompactRetainedAnalysisArtifacts|Get-FileTransferRecoveryFailureClassification'
    }
    # The explicitly rewritten fault-injection function has anonymous frames;
    # retain the exact original caller sequence for every unmodified attempt.
    $expectedCallers=if($mode -ceq 'selection-fails') {
        '<ScriptBlock>|<ScriptBlock>|Get-FileTransferRecoveryFailureClassification'
    } else { $baselineCallers }
    if($callers -cne $expectedCallers) { throw "Authority caller sequence changed: $mode; expected=$expectedCallers; actual=$callers" }
    if($mode -ceq 'witness') {
        if($null -eq $selection -or $selection.SourceLine -ne 2 -or $selection.ReportClass -cne $failure.Class -or
            $selection.ReportReason -cne $failure.Reason -or $selection.ContentSha256 -cne $baseline.ContentSha256) {
            throw 'Actual primary did not preserve its exact first failure witness'
        }
    }
    elseif($mode -ceq 'assigned-reference') { if($selection -cne 'existing-owner') { throw 'Assigned output owner overwritten' } }
    elseif($null -ne $selection) { throw 'Omitted witness was published' }
    if($null -ne $incompatible) { throw 'Incompatible output reference mutated' }
    if($mode -ceq 'selection-fails' -and $script:injectedSelectionFailures -ne 1) { throw 'Selector fault injection was not reached' }
    $primary[$mode]=@{}
    foreach($name in @('filetransfer-operator-verdict.txt','filetransfer-retained-compact-summary.json',
        'filetransfer-coordinator-snapshot-full-summary.json','filetransfer-coordinator-snapshot-compact-summary.json',
        'filetransfer-coordinator-snapshot-parity-summary.json')) {
        $primary[$mode][$name]=[IO.File]::ReadAllText((Join-Path $output $name))
        if($name -ceq 'filetransfer-retained-compact-summary.json') {
            # Only these existing elapsed measurements vary between attempts.
            $primary[$mode][$name]=$primary[$mode][$name] -replace '("data_path_correlation_(index|total)_ms"\s*:\s*)[0-9]+','${1}0'
        }
        if($mode -cne 'baseline' -and $primary[$mode][$name] -cne $primary.baseline[$name]) { throw "Primary artifact changed: $mode/$name" }
    }
    Set-Item Function:Write-FileTransferCompactRetainedAnalysisArtifacts -Value $realWriter
    Set-Item Function:Write-FileTransferEfficiencySummaryArtifacts -Value $realEfficiencyWriter
}
if($primary.baseline['filetransfer-operator-verdict.txt'] -notmatch 'first_proof_chain_failure_line_number=2') { throw 'Fixture did not report exact first failure' }
'primary-failure-witness='+(ConvertTo-Json -InputObject ([ordered]@{NegativeControls=$cases.ToArray();PrimaryAttempts=6
    AuthorityEvaluationsPerAttempt=3;AdditionalAuthorityEvaluations=0;PrimaryArtifactsEqualExceptExistingTimings=$true
    EvaluationCallers=$baselineCallers
    ParentWorkerBindingVerified=$false;PublicationCompletionIssued=$false;LiveParentCaller=$false}) -Depth 4 -Compress)
