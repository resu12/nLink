# Runs after the original shutdown/adoption and report drain, before the
# scenario's owner disposal. No path-based reopen or copied log is consumed.
if(-not $FrontierOwner.IsCurrent -or -not $FrontierOwner.AllFileHandoffsAcknowledged -or
    -not $FrontierOwner.AllAppRequestsCompletedSuccessfully -or -not $FrontierOwner.AllAppOutputsDrainedSuccessfully) {throw 'Original Core writer shutdown incomplete'}
$private:clone=[object].GetMethod('MemberwiseClone',$flags)
$private:adoption=$FrontierOwner.GetType().GetField('fileAdoption',$flags).GetValue($FrontierOwner)
[NLink.Tests.AttemptWorkProfileForTest]::Start($adoption.GetType().GetField('work',$flags).GetValue($adoption))
[NLink.Tests.AttemptWorkProfileForTest]::Mark('before_seal')
$private:items=$adoption.GetType().GetField('adopted',$flags).GetValue($adoption)
if($items.Count -ne 2) {throw 'Core writer contributions missing'}
if($attemptCase -ceq 'writer-helper-first') {
    $private:first=$items[0].GetType().GetField('Manifest',$flags).GetValue($items[0])
    if($first.GetType().GetField('AppId',$flags).GetValue($first) -ne $Context.HelperProc.Id) {throw 'Reverse-adoption control did not run'}
}
$private:proof=$FrontierOwner.TrySealFiles()
[NLink.Tests.AttemptWorkProfileForTest]::Mark('after_seal')
if($null -eq $proof) {throw 'Original Core source seal missing'}
$private:finalizer=[NLink.Tools.LocalGuiWriterRequest]::TryAttach($env:NLINK_GUI_WRITER_REQUEST,[guid]$env:NLINK_GUI_WRITER_REQUEST_ID,
    $env:NLINK_FILETRANSFER_ACCEPTANCE_RUN_ID,$env:NLINK_FILETRANSFER_SCENARIO_RUN_ID,$Directory)
if($null -eq $finalizer) {throw 'Original role writer finalizer missing'}
$private:binding=$finalizer.TryTakeFrontierBinding()
$private:reader=$null; $private:capture=$null; $private:join=$null
$private:completeNativeWriters=$env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -like 'writer-*' -or $env:NLINK_TEST_DIAGNOSTIC_CUSTODY -cin @('frontier-schedule','frontier-late-publication')
try {
    [NLink.Tests.AttemptWorkProfileForTest]::Mark('binding_ready')
    if($null -eq $binding) {throw 'Role writer source binding missing'}
    if($completeNativeWriters) {
        $private:semanticEvents=[string[]]@('operational_log_writer_drained')
        if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE -cin @('canonical-source','canonical-fallback','canonical-tail-late-publication') -or $env:NLINK_TEST_DIAGNOSTIC_CUSTODY -cin @('frontier-schedule','frontier-late-publication')) {
            $semanticEvents=[string[]](Get-GuiSmokeSemanticEvidenceEventNames)
        }
        [NLink.Tests.AttemptWorkProfileForTest]::Mark('before_processing')
        $private:result=$binding.TryProcessObservedLedger($semanticEvents)
        [NLink.Tests.AttemptWorkProfileForTest]::Mark('after_processing')
        if($null -eq $result) {
            $private:activeMs=$adoption.GetType().GetProperty('ActiveMilliseconds',$flags).GetValue($adoption)
            $private:readBytes=$adoption.GetType().GetField('sourceReadPosition',$flags).GetValue($adoption)
            [Console]::Error.WriteLine('payload-writer-processing-failed; active_ms='+$activeMs+'; source_bytes_read='+$readBytes)
            throw 'Original writer cursor/recovery processing missing'
        }
        $capture=$result.StreamCapture
        $private:match=$binding.WriterMatch
        if($null -eq $match -or -not $match.MatchesResult($result) -or $null -eq $binding.CursorMatch -or
            $null -eq $binding.RecoveryMatch) {throw 'Original writer cursor/recovery match missing'}
    } else {
    [NLink.Tests.AttemptWorkProfileForTest]::Mark('before_processing')
    $reader=$binding.TryOpenSource()
    if($attemptCase -ceq 'writer-profile' -and $null -ne $reader) {
        [NLink.Tests.OwnedSourceCheckProfile]::Run($reader,$binding,$FrontierOwner,$adoption)
    }
    if($null -eq $reader -or -not $reader.TryBeginProcessingWork()) {throw 'Role writer read interval missing'}
    $private:observation=$binding.TryCaptureLedgerFinal()
    if($null -eq $observation) {throw 'Original final cursor missing'}
    $private:sourceBytes=$reader.Length
    try {
        $private:result=[NLink.Tools.FrontierLedger.GuiSmokeLogLedgerStreamingProcessor]::ProcessFromStreamWithRetainedOutputs(
            $reader,[long]$observation.LineCount,[long]$observation.Bookmark,(Join-Path $Directory 'role-retained.tmp'),
            (Join-Path $Directory 'role-writer.tmp'),$binding.AcceptanceRunId,$binding.ScenarioRunId,[string[]]@(),
            (Join-Path $Directory 'role-semantic.tmp'),[string[]]@('operational_log_writer_drained'),$reader.ProcessingLifetime)
    } catch {
        # Diagnostic only: observe the original cumulative budget without
        # resetting it or converting a rejected processing attempt to success.
        $private:activeMs=$adoption.GetType().GetProperty('ActiveMilliseconds',$flags).GetValue($adoption)
        [Console]::Error.WriteLine('role-writer-processing-failed; active_ms='+$activeMs+'; source_bytes='+$sourceBytes+'; lines='+$observation.LineCount)
        throw
    }
    $capture=$result.StreamCapture
    $private:match=$binding.TryMatchProcessedWriters($result)
    if($null -eq $match -or -not $reader.ReachedEnd -or -not $reader.TryFinishProcessingWork() -or
        -not $match.IsCurrent -or -not $match.MatchesResult($result)) {throw 'Original Core writer closure match missing'}
    [NLink.Tests.AttemptWorkProfileForTest]::Mark('after_processing')
    }
    if($env:NLINK_TEST_GUI_CLOSED_PAYLOAD_HANDOFF -ceq '1') {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiClosedPayloadOffer.ps1')
        return
    }
    [NLink.Tests.AttemptWorkProfileForTest]::Mark('before_role_probes')
    if($null -ne $attemptTicket.TryBindRoleWriters($clone.Invoke($binding,$null),$match) -or
        $null -ne $attemptTicket.TryBindRoleWriters($binding,$clone.Invoke($match,$null)) -or
        $null -ne $clone.Invoke($attemptTicket,$null).TryBindRoleWriters($binding,$match)) {throw 'Copied source/match/attempt admitted'}
    $private:manifestField=$items[0].GetType().GetField('Manifest',$flags)
    $private:originalManifest=$manifestField.GetValue($items[0])
    foreach($fieldName in @('AppRequest','AppCreation')) {
        $private:changed=$clone.Invoke($originalManifest,$null)
        $private:value=if($fieldName -ceq 'AppRequest') {[guid]::NewGuid()} else {[long]1}
        $changed.GetType().GetField($fieldName,$flags).SetValue($changed,$value)
        try {
            $manifestField.SetValue($items[0],$changed)
            if($null -ne $attemptTicket.TryBindRoleWriters($binding,$match)) {throw 'Foreign request/creation joined writer'}
        } finally {$manifestField.SetValue($items[0],$originalManifest)}
    }
    if(-not $match.IsCurrent -or -not $attemptTicket.IsCurrent) {throw 'Foreign role facts retired original ownership'}
    if([NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptWriters].GetConstructors().Length -or
        @([NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptWriters].GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) {throw 'Role writer join publicly constructible/mutable'}
    # Corrupt only the retained test-side manifest after the genuine writer
    # match. This isolates the new role join from earlier native validation.
    if($attemptCase -cin @('writer-app-mismatch','writer-sequence-mismatch')) {
        $private:field=$items[0].GetType().GetField('Manifest',$flags)
        $private:changed=$clone.Invoke($field.GetValue($items[0]),$null)
        if($attemptCase -ceq 'writer-app-mismatch') {$changed.GetType().GetField('AppId',$flags).SetValue($changed,[int]$PID)}
        else {
            $private:drainField=$changed.GetType().GetField('Drain',$flags)
            $private:drain=$clone.Invoke($drainField.GetValue($changed),$null)
            $private:last=$drain.GetType().GetField('Last',$flags)
            $last.SetValue($drain,[long]($last.GetValue($drain)+1)); $drainField.SetValue($changed,$drain)
        }
        $field.SetValue($items[0],$changed)
    }
    [NLink.Tests.AttemptWorkProfileForTest]::Mark('after_role_probes')
    $join=$attemptTicket.TryBindRoleWriters($binding,$match)
    [NLink.Tests.AttemptWorkProfileForTest]::Mark('after_role_bind')
    $rejected=$attemptCase -cin @('writer-app-mismatch','writer-sequence-mismatch')
    if(($null -eq $join) -ne $rejected) {throw 'Role manifest join result changed'}
    if($rejected) {
        if(-not $match.IsCurrent -or -not $binding.IsCurrent) {throw 'Rejected role join retired original source'}
    } else {
        if(-not $join.IsCurrent -or $join.HelpeeWriterId -ceq $join.HelperWriterId -or
            -not $join.MatchesRole('helpee',$frontierHelpeeRequest,$join.HelpeeWriterId) -or
            -not $join.MatchesRole('helper',$frontierHelperRequest,$join.HelperWriterId) -or
            $join.MatchesRole('helpee',$frontierHelperRequest,$join.HelpeeWriterId) -or
            $join.MatchesRole('helpee',$frontierHelpeeRequest,$join.HelperWriterId) -or
            $join.MatchesRole('helper',$clone.Invoke($frontierHelperRequest,$null),$join.HelperWriterId) -or
            $join.LiveProofEligible -or $join.NativeWriterCustodyEstablished -or $join.PublicationCompletionIssued) {throw 'Role writer identity or authority mismatch'}
        $private:copy=$clone.Invoke($join,$null)
        if($copy.IsCurrent) {throw 'Copied role writer join current'}
        $copy.Dispose()
        if($attemptCase -ceq 'writer-join-spent-budget') {
            $private:work=$adoption.GetType().GetField('work',$flags).GetValue($adoption)
            $private:wait=[Threading.ManualResetEvent]::new($false)
            $work.Start()
            try {[void]$wait.WaitOne(10050)} finally {$work.Stop();$wait.Dispose()}
        }
        $private:joinCurrent=$join.IsCurrent
        $private:joinRepeat=$null
        # Preserve the original short-circuit and its single authority evaluation.
        if($joinCurrent) {$joinRepeat=$attemptTicket.TryBindRoleWriters($binding,$match)}
        if(-not $joinCurrent -or $null -ne $joinRepeat) {
            [Console]::Error.WriteLine('role-writer-copy-repeat-failed; original_current='+$joinCurrent+
                '; repeat_evaluated='+$joinCurrent+'; repeat_returned_join='+($null -ne $joinRepeat)+
                '; active_ms='+$adoption.GetType().GetProperty('ActiveMilliseconds',$flags).GetValue($adoption))
            if($attemptCase -cne 'writer-join-spent-budget') {throw 'Copy/repeat retired or reissued role join'}
        }
        if($attemptCase -ceq 'writer-join-spent-budget') {
            if($joinCurrent -or $null -ne $joinRepeat -or $work.ElapsedMilliseconds -le 10000) {throw 'Exhausted original role-writer clock accepted/reset'}
            'attempt-role-writer-spent-budget-rejected'
        }
        [NLink.Tests.AttemptWorkProfileForTest]::Mark('after_role_checks')
        if($attemptCase.StartsWith('writer-source-')) {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptSourceJoin.ps1')
        }
        if($attemptCase -ceq 'writer-destination-gui-valid') {
            . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDestinationJoin.ps1')
        }
        if($attemptCase -ceq 'writer-valid') {
            $manifestField.SetValue($items[0],$clone.Invoke($originalManifest,$null))
            if($join.IsCurrent) {throw 'Copied manifest retained role writer join'}
            $manifestField.SetValue($items[0],$originalManifest)
        } elseif($attemptCase -ceq 'writer-parent-cancelled') {
            $private:closed=$attemptTicket.GetType().GetField('originClosed',$flags).GetValue($attemptTicket)
            [void]$closed.Set(); if($join.IsCurrent) {throw 'Cancelled role writer join current'}; [void]$closed.Reset()
        } elseif($attemptCase -ceq 'writer-closed-output') {
            $private:outputs=$capture.GetType().GetField('outputs',$flags).GetValue($capture)
            $outputs.GetType().GetField('files',$flags).GetValue($outputs)[0].Dispose()
        } else {$join.Dispose()}
        if($join.IsCurrent -or $null -ne $join.HelpeeWriterId -or $null -ne $join.HelperWriterId -or
            $null -ne $attemptTicket.TryBindRoleWriters($binding,$match)) {throw 'Retired role writer join revived'}
    }
    'attempt-role-writers-ok'
} finally {
    [NLink.Tests.AttemptWorkProfileForTest]::Mark('before_cleanup')
    if($null -ne $join) {$join.Dispose()}
    if($null -ne $capture) {$capture.Dispose()}
    if($null -ne $reader) {$reader.Dispose()}
    if($null -ne $binding) {$binding.Dispose()}
    $finalizer.Dispose()
    [NLink.Tests.AttemptWorkProfileForTest]::WriteAfterCleanup()
}
if($proof.IsCurrent -or $match.IsCurrent -or $capture.IsCurrent -or $attemptTicket.IsCurrent) {throw 'Writer fixture retained custody after finalizer cleanup'}
