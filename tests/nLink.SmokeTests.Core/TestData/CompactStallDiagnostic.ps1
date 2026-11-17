param([string]$RepoRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
function Parse([string]$Path) {
    $tokens=$null; $errors=$null
    $tree=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot $Path),[ref]$tokens,[ref]$errors)
    if ($errors.Count) { throw "Parse failed: $Path" }
    return $tree
}
$ast=Parse 'tools/FileTransferOps/AnalyzerOrchestration.ps1'
$functions=@($ast.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $n.Name -ceq 'Write-FileTransferCompactRetainedAnalysisArtifacts'},$true))
if ($functions.Count -ne 1) { throw 'Expected one compact writer' }
$body=$functions[0].Body
function Assignments([string]$Name) {
    return @($body.FindAll({param($n)
        $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq ('$'+$Name)
    },$true))
}
$loops=@($body.FindAll({param($n) $n -is [Management.Automation.Language.ForEachStatementAst] -and
    $n.Extent.Text.StartsWith('foreach ($candidateRecord in @($fileTransferFallbackFrontierStallGraceRecheckProof.UnresolvedCandidateRecords))')},$true))
if ($loops.Count -ne 1) { throw 'Expected one stall projection' }
$parsing=Parse 'tools/FileTransferSoak/LogParsing.ps1'
foreach ($name in @('Get-FileTransferEventField','Get-FileTransferEventInt64Field')) {
    $function=@($parsing.FindAll({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name},$true))
    if ($function.Count -ne 1) { throw "Expected field reader: $name" }
    . ([scriptblock]::Create($function[0].Extent.Text))
}
function Record([long]$Sequence) {
    return [pscustomobject]@{
        Event=[pscustomobject]@{Sequence=$Sequence;LineNumber=[int]$Sequence;FilePath='synthetic-only';
            EventName='filetransfer_v6_frontier_request_deferred';Fields=@{log_writer_sequence=[string]($Sequence+100)}}
        Reason='synthetic-reason';Variant='settled_frontier_progress_successor_not_rechecked_after_stall_grace'
    }
}
$snapshotFallbackTailYieldAuthoritative=$false
$snapshotReceiveRecoveryEvidenceSelected=$false
# Exercise only DTO, selection and formatting expressions on invented objects.
# Never invoke a report entry point, correlator, or authority evaluator.
foreach ($mode in @('present','null','absent','earlier_other','no_failure')) {
    $first=Record 10; $later=Record 20
    $diagnostic=[pscustomobject]@{Kind='DispatchedStalled';WakeWriterSequence=111L;SourceDisposition='Unproven';
        ObligationDisposition='Unresolved';SupersedingTransitionId='quoted"value\path'}
    if ($mode -cne 'absent') {
        $first | Add-Member UnresolvedDiagnostic $(if ($mode -ceq 'null') {$null} else {$diagnostic})
    }
    $later | Add-Member UnresolvedDiagnostic ([pscustomobject]@{Kind='later-must-not-be-borrowed'})
    $records=@(if ($mode -cne 'no_failure') { $later; $first })
    $fileTransferFallbackFrontierStallGraceRecheckProof=[pscustomobject]@{
        UnresolvedCandidateRecords=$records;AccountingVersion=2;RawCount=5;DistinctCount=5;
        RecoveredCount=3;SupersededCount=0;UnresolvedCount=2;Verdict='fail'
    }
    $before=$fileTransferFallbackFrontierStallGraceRecheckProof | ConvertTo-Json -Depth 8 -Compress
    $compactProofChainCandidates=[Collections.Generic.List[object]]::new()
    . ([scriptblock]::Create($loops[0].Extent.Text))
    if ($mode -cne 'no_failure') {
        $projected=$compactProofChainCandidates[1]
        if ($null -eq $projected.PSObject.Properties['UnresolvedDiagnostic']) { throw 'Compact candidate dropped diagnostic property' }
        $expected=if ($mode -cin @('null','absent')) {$null} else {$diagnostic}
        if (-not [object]::ReferenceEquals($projected.UnresolvedDiagnostic,$expected)) { throw 'Compact candidate changed diagnostic' }
    }
    if ($mode -ceq 'earlier_other') {
        $other=Record 1
        $compactProofChainCandidates.Add([pscustomobject]@{Class='filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing';
            Event=$other.Event;Reason=$other.Reason;UnresolvedDiagnostic=$diagnostic})
    }
    $compactProofChainCandidateArray=$compactProofChainCandidates.ToArray()
    $selectors=@(Assignments 'firstCompactProofChainFailure')
    if ($selectors.Count -ne 2) { throw 'Expected ordered first-failure selection and unwrap' }
    foreach ($assignment in $selectors) { . ([scriptblock]::Create($assignment.Extent.Text)) }
    $verdict='FAIL_PROTOCOL_OR_INTEGRITY'; $gateStatus='fail'; $acceptanceFailureClass='route_runtime'
    foreach ($name in @('firstProofChainFailure','firstProofChainFailureClass','firstProofChainFailureEvent',
            'firstProofChainFailureReason','failureVariant','firstProofChainFailureSequence','firstProofChainFailureSourcePath',
            'firstProofChainFailureLineNumber','firstProofChainFailureWriterSequence',
            'fallbackFrontierStallDiagnostic','fallbackFrontierStallDiagnosticJson')) {
        $assignments=@(Assignments $name)
        if ($assignments.Count -ne 1) { throw "Expected one selection/format assignment: $name" }
        . ([scriptblock]::Create($assignments[0].Extent.Text))
    }
    $expectedJson=if ($mode -ceq 'present') {ConvertTo-Json $diagnostic -Depth 4 -Compress} else {'null'}
    if ($fallbackFrontierStallDiagnosticJson -cne $expectedJson) { throw "Selected wrong diagnostic: $mode" }
    $expectedSequence=if ($mode -ceq 'no_failure') {-1L} elseif ($mode -ceq 'earlier_other') {1L} else {10L}
    $expectedClass=if ($mode -ceq 'no_failure') {'(none)'} elseif ($mode -ceq 'earlier_other') {
        'filetransfer_fallback_frontier_checkpoint_acceptance_recheck_missing'
    } else {'filetransfer_fallback_frontier_stall_grace_recheck_missing'}
    $expectedVariant=if ($mode -cin @('no_failure','earlier_other')) {'(none)'} else {$first.Variant}
    if ($firstProofChainFailureSequence -ne $expectedSequence -or $firstProofChainFailureClass -cne $expectedClass -or
        $failureVariant -cne $expectedVariant) { throw "Failure selection changed: $mode" }
    if ($mode -cne 'no_failure' -and ($firstProofChainFailureLineNumber -ne $expectedSequence -or
        $firstProofChainFailureSourcePath -cne 'synthetic-only' -or $firstProofChainFailureWriterSequence -ne ($expectedSequence+100))) {
        throw 'Failure source coordinates changed'
    }
    $exports=@($body.FindAll({param($n) $n -is [Management.Automation.Language.BinaryExpressionAst] -and
        $n.Operator -eq 'Format' -and $n.Left -is [Management.Automation.Language.StringConstantExpressionAst] -and
        $n.Left.Value -ceq 'fallback_frontier_stall_diagnostic={0}'},$true))
    if ($exports.Count -ne 4) { throw 'Expected diagnostic in four compact text reports' }
    foreach ($export in $exports) {
        $line=Invoke-Expression $export.Extent.Text
        if ($line -cne ('fallback_frontier_stall_diagnostic='+$expectedJson)) { throw 'Text diagnostic changed' }
    }
    $jsonValues=@(foreach ($table in $body.FindAll({param($n) $n -is [Management.Automation.Language.HashtableAst]},$true)) {
        foreach ($pair in $table.KeyValuePairs) {
            if ($pair.Item1.Extent.Text -ceq 'fallback_frontier_stall_diagnostic') { Invoke-Expression $pair.Item2.Extent.Text }
        }
    })
    if ($jsonValues.Count -ne 1 -or $jsonValues[0] -cne $expectedJson) { throw 'Compact JSON diagnostic missing/changed' }
    $roundtrip=([pscustomobject]@{fallback_frontier_stall_diagnostic=$jsonValues[0]} | ConvertTo-Json -Depth 4 | ConvertFrom-Json)
    if ($roundtrip.fallback_frontier_stall_diagnostic -cne $expectedJson) { throw 'JSON escaping changed diagnostic' }
    if ($before -cne ($fileTransferFallbackFrontierStallGraceRecheckProof | ConvertTo-Json -Depth 8 -Compress) -or
        $verdict -cne 'FAIL_PROTOCOL_OR_INTEGRITY' -or $gateStatus -cne 'fail' -or $acceptanceFailureClass -cne 'route_runtime') {
        throw 'Diagnostic reporting mutated proof or verdict'
    }
    Write-Output "pass $mode"
}
Write-Output 'compact-stall-diagnostic-exports-ok'
