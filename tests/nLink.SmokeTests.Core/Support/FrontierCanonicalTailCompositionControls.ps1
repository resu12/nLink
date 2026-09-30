param([string]$RepoRoot,[string]$Directory,[ValidateSet('receipt','proof','handoff')][string]$Mode)
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatchProjection.ps1')
if($Mode -ceq 'receipt') {
    $ast=Read-AttemptAst (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierLatePublicationPrimaryProbe.ps1')
    Invoke-Expression (Get-AttemptFunction $ast 'Test-LateReceipt')
    function Clock($sequence,$utc,$counter) {[pscustomobject]@{ProcessId=1;ProcessCreationFileTime=1;ObservationId='o';ClockInstanceId='c';
        Frequency=1000;CaptureSequence=$sequence;UtcTicks=$utc;CounterBefore=$counter;CounterAfter=$counter+1}}
    foreach($fault in @('valid','default','small','frontier','highest','bytes','before','after','count','write-highest','paused','terminal',
        'early-completion','late-write','late-completion','clock','authority')) {
        # Values only: not a native receipt or custody source.
        $r=[pscustomobject]@{Outcome='PublishedWithinObservationBound';LiveProofEligible=$false;NativeWriterCustodyEstablished=$false;PublicationCompletionIssued=$false;
            RootGeneration=1;RecheckedGeneration=1;FirstWriteGeneration=1;ProcessId=1;ProcessCreationFileTime=1;ObservationId='o';ClockInstanceId='c';Frequency=1000;
            OriginalDeadlineUtcTicks=8500000L;PublicationDeadlineUtcTicks=14000000L;
            Source=[pscustomobject]@{Route='post_tuna_fallback_v6';ProtocolVersion=6;TransportEpoch=2};
            Frame=[pscustomobject]@{ContiguousCommittedChunkIndex=12480;DurableReceivedHighestChunkIndex=12483;BytesCommitted=268369920L;TransferPaused=$false;TerminalReady=$false;TransportEpoch=2;MissingRanges=@()};
            Completion=[pscustomobject]@{DecisionKind='FallbackControlPlaneSendCompleted';DecisionStatus='Accepted'};
            Lifecycle=@([pscustomobject]@{Kind='Write';Clock=(Clock 1 4000000 400);Binding=[pscustomobject]@{Before=12477;After=12480;Count=3;Highest=12483}},
                [pscustomobject]@{Kind='AcceptedCompletion';Clock=(Clock 2 9000000 900);Binding=$null})}
        switch($fault) {
            'small' {$r.Frame.ContiguousCommittedChunkIndex=4;$r.Frame.DurableReceivedHighestChunkIndex=7;$r.Frame.BytesCommitted=86016L;$r.Lifecycle[0].Binding.Before=1;$r.Lifecycle[0].Binding.After=4;$r.Lifecycle[0].Binding.Highest=7}
            'frontier' {$r.Frame.ContiguousCommittedChunkIndex++}
            'highest' {$r.Frame.DurableReceivedHighestChunkIndex--}
            'bytes' {$r.Frame.BytesCommitted++}
            'before' {$r.Lifecycle[0].Binding.Before++}
            'after' {$r.Lifecycle[0].Binding.After++}
            'count' {$r.Lifecycle[0].Binding.Count++}
            'write-highest' {$r.Lifecycle[0].Binding.Highest--}
            'paused' {$r.Frame.TransferPaused=$true}
            'terminal' {$r.Frame.TerminalReady=$true}
            'early-completion' {$r.Lifecycle[1].Clock.UtcTicks=8499999}
            'late-write' {$r.Lifecycle[0].Clock.UtcTicks=8500000}
            'late-completion' {$r.Lifecycle[1].Clock.UtcTicks=14000000}
            'clock' {$r.Lifecycle[1].Clock.ClockInstanceId='other'}
            'authority' {$r.PublicationCompletionIssued=$true}
        }
        $rejected=$false
        try {if($fault -ceq 'default') {Test-LateReceipt $r} else {Test-LateReceipt $r -CanonicalTail $true}} catch {$rejected=$true}
        if($rejected -ne ($fault -cne 'valid')) {throw ('Canonical receipt gate: '+$fault)}
    }
    'canonical-tail-receipt-controls-ok';return
}
if($Mode -ceq 'handoff') {
    $caseName='writer-source-read-cross-process-carrier-full-canonical-tail-late-publication'
    $ast=Read-AttemptAst (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDispatch.ps1')
    $admission=@($ast.EndBlock.Statements | Where-Object {$_.Extent.Text.StartsWith('$canonicalTailProfile=') -or $_.Extent.Text.Contains("throw 'Canonical tail requires the exact actual-pair composite profile'")})
    if($admission.Count -ne 2) {throw 'Exact composite admission missing'}
    foreach($fault in @('valid','wrapper','parent','primary','small-diagnostic','foreign-case','ordinary')) {
        $Case=$caseName;$PayloadWrapperCase='writer-valid';$ParentPayloadCase='valid';$PrimaryPayloadCase='canonical-tail-late-publication';$DiagnosticCustodyCase=''
        switch($fault) {'wrapper' {$PayloadWrapperCase='valid'} 'parent' {$ParentPayloadCase=''} 'primary' {$PrimaryPayloadCase='canonical-fallback'}
            'small-diagnostic' {$DiagnosticCustodyCase='frontier-late-publication'} 'foreign-case' {$Case='writer-valid'}
            'ordinary' {$Case='writer-valid';$PayloadWrapperCase='';$ParentPayloadCase='';$PrimaryPayloadCase=''}}
        $rejected=$false;try {foreach($statement in $admission) {Invoke-Expression $statement.Extent.Text}} catch {$rejected=$true}
        if($rejected -ne ($fault -cnotin @('valid','ordinary'))) {throw ('Composite admission outcome: '+$fault)}
    }
    foreach($name in @('FrontierAttemptSenderReadGui.ps1','FrontierAttemptSourceJoin.ps1','FrontierAttemptDestinationJoin.ps1')) {
        $a=Read-AttemptAst (Join-Path $RepoRoot ('tests/nLink.SmokeTests.Core/Support/'+$name))
        $conditions=@($a.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
            $n.Clauses[0].Item1.Extent.Text.Contains($caseName) -and
            $n.Clauses[0].Item1.Extent.Text.Contains("'writer-source-read-cross-process-gui-valid'") -and
            -not $n.Clauses[0].Item1.Extent.Text.Contains(' -and')},$true))
        if($conditions.Count -ne 1) {throw ('Canonical handoff branch missing: '+$name)}
        foreach($attemptCase in @($caseName,'writer-source-read-cross-process-carrier-full-gui-valid','writer-source-read-cross-process-gui-valid','writer-source-read-cross-process-carrier-frontier-late-publication')) {
            if([bool](Invoke-Expression $conditions[0].Clauses[0].Item1.Extent.Text) -ne ($attemptCase -cne 'writer-source-read-cross-process-carrier-frontier-late-publication')) {throw ('Handoff profile broadened/lost: '+$name)}
        }
    }
    foreach($name in @('FrontierAttemptDestinationJoin.ps1','FrontierAttemptPayloadParentProbe.ps1')) {
        $a=Read-AttemptAst (Join-Path $RepoRoot ('tests/nLink.SmokeTests.Core/Support/'+$name))
        $assignment=@($a.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$private:receiveRoot'},$true))
        if($assignment.Count -ne 1) {throw 'Receive cleanup root ambiguous'}
        $Case=$caseName;$attemptCase=$caseName
        if((Invoke-Expression $assignment[0].Right.Extent.Text) -cne 'native-carrier-received') {throw 'Canonical destination lost its native cleanup inventory'}
    }
    $a=Read-AttemptAst (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierRelayAnalysis.ps1')
    $selection=@($a.FindAll({param($n) $n -is [Management.Automation.Language.AssignmentStatementAst] -and $n.Left.Extent.Text -ceq '$worker'},$true))
    $env:NLINK_TEST_PAYLOAD_PRIMARY_CASE='canonical-tail-late-publication';$env:NLINK_TEST_DIAGNOSTIC_CUSTODY=''
    if($selection.Count -ne 1 -or (Invoke-Expression $selection[0].Right.Extent.Text) -cne 'FrontierCanonicalTailPrimaryWorker.ps1') {throw 'Combined worker selection missing'}
    $a=Read-AttemptAst (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptWritersJoin.ps1')
    $semantic=@($a.FindAll({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and
        $n.Clauses[0].Item1.Extent.Text.Contains("'canonical-tail-late-publication'")},$true))
    if($semantic.Count -ne 1 -or -not (Invoke-Expression $semantic[0].Clauses[0].Item1.Extent.Text) -or
        -not $semantic[0].Clauses[0].Item2.Extent.Text.Contains('Get-GuiSmokeSemanticEvidenceEventNames')) {throw 'Canonical primary lost the original semantic catalog'}
    $destination=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDestinationJoin.ps1'))
    $finalizer='foreach($nativeWriterStep in @(''copy'',''persist'',''prepare'',''complete''))'
    if([regex]::Matches($destination,[regex]::Escape($finalizer)).Count -ne 1) {throw 'Original finalization sequence duplicated or changed'}
    $source=[IO.File]::ReadAllText((Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptSourceJoin.ps1'))
    if($source.IndexOf('FrontierAttemptDestinationJoin.ps1') -ge $source.IndexOf('$join.Dispose()') -or
        [regex]::Matches($source,'FrontierAttemptDestinationJoin.ps1').Count -ne 1 -or
        -not $source.Contains("`$env:NLINK_TEST_DIAGNOSTIC_CUSTODY -cin @('frontier-schedule','frontier-late-publication')")) {throw 'Actual-pair finalizer order/exclusion changed'}
    'canonical-tail-handoff-controls-ok';return
}
$ast=Read-AttemptAst (Join-Path $RepoRoot 'tools/Run-FileTransferRouteAcceptance.ps1')
Invoke-Expression (Get-AttemptFunction $ast 'Read-RouteAcceptanceKeyValueArtifact')
foreach($fault in @('valid-incomplete','valid-completed','status','classification','failure','mode','exit','timeout','input','full','compact','parity',
    'verdict','gate','route','profile','join','outcome','selected','calls','witness-empty','witness-cap','output','pins','text','authority','reports')) {
    $target=Join-Path $Directory $fault;[void][IO.Directory]::CreateDirectory($target)
    $failure='FileTransfer-Ops retained analysis exited successfully without complete proof artifacts for '+$target
    $receipt=[pscustomobject]@{status='proof_incomplete';completedProofArtifacts=$false;coordinatorSnapshotMode='SnapshotV1Authoritative';exitCode=0;exitCodeAvailable=$true;timedOut=$false;analyzerInputUnchanged=$true}
    $o=[ordered]@{CanonicalTailProfile='canonical-tail-late-publication';SamePairJoined=$true;Outcome='PublishedWithinObservationBound';
        SelectedFailure='filetransfer_fallback_frontier_recent_progress_grace_recheck_missing';PrimaryVerdict='FAIL_PROTOCOL_OR_INTEGRITY';GateStatus='fail';
        ResolverCalls=1;WitnessCount=41;OriginalOutputCommitted=$true;OpenPinsAfterCleanup=0;TextCleared=$true;
        LiveProofEligible=$false;NativeWriterCustodyEstablished=$false;PublicationCompletionIssued=$false;ReportsBefore='unchanged';ReportsAfter='unchanged'}
    switch($fault) {
        'valid-completed' {$failure='';$receipt.status='completed';$receipt.completedProofArtifacts=$true}
        'status' {$receipt.status='completed'} 'classification' {$receipt.completedProofArtifacts=$true}
        'failure' {$failure='other failure'} 'mode' {$receipt.coordinatorSnapshotMode='LegacyV0'}
        'exit' {$receipt.exitCode=1} 'timeout' {$receipt.timedOut=$true} 'input' {$receipt.analyzerInputUnchanged=$false}
        'profile' {$o.CanonicalTailProfile='canonical-fallback'} 'join' {$o.SamePairJoined=$false} 'outcome' {$o.Outcome='other'}
        'selected' {$o.SelectedFailure='other'} 'calls' {$o.ResolverCalls=2} 'witness-empty' {$o.WitnessCount=0} 'witness-cap' {$o.WitnessCount=65}
        'output' {$o.OriginalOutputCommitted=$false} 'pins' {$o.OpenPinsAfterCleanup=1} 'text' {$o.TextCleared=$false}
        'authority' {$o.PublicationCompletionIssued=$true} 'reports' {$o.ReportsAfter='changed'}
    }
    foreach($name in @('full','compact')) {
        $view=@{Mode='SnapshotV1Authoritative';StreamComplete=$true;ArtifactEvidenceVerdict=$(if($fault -ceq $name){'fail'}else{'pass'})}
        [IO.File]::WriteAllText((Join-Path $target ('filetransfer-coordinator-snapshot-'+$name+'-summary.json')),($view | ConvertTo-Json -Compress))
    }
    [IO.File]::WriteAllText((Join-Path $target 'filetransfer-coordinator-snapshot-parity-summary.json'),(@{Verdict=$(if($fault -ceq 'parity'){'fail'}else{'pass'})} | ConvertTo-Json -Compress))
    [IO.File]::WriteAllText((Join-Path $target 'filetransfer-operator-verdict.txt'),('verdict='+$(if($fault -ceq 'verdict'){'INCONCLUSIVE'}else{'FAIL_PROTOCOL_OR_INTEGRITY'})+"`ngate_status="+$(if($fault -ceq 'gate'){'pass'}else{'fail'})))
    [IO.File]::WriteAllText((Join-Path $target 'filetransfer-route-consistency-summary.txt'),('live_route_epoch_proof_verdict='+$(if($fault -ceq 'route'){'pass'}else{'fail'})))
    [IO.File]::WriteAllText((Join-Path $target 'late-publication-primary-observation.json'),($o | ConvertTo-Json -Compress))
    $rejected=$false;try {& (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierCanonicalTailProof.ps1') -Directory $target -Receipt $receipt -AnalysisFailure $failure} catch {$rejected=$true}
    if($rejected -ne ($fault -cnotin @('valid-incomplete','valid-completed'))) {throw ('Canonical parent proof gate: '+$fault)}
}
'canonical-tail-proof-controls-ok'
