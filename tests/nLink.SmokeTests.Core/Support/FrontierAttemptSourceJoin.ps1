if($attemptCase -cin @('writer-source-size','writer-source-open-writer')) {
    if($null -ne $attemptSource -or -not $join.IsCurrent) {throw 'Source refusal retired original writers'}
    'attempt-source-refusal-ok'
} else {
    [NLink.Tests.AttemptWorkProfileForTest]::Mark('before_source_bind_controls')
    if(-not $attemptSource.IsCurrent -or $attemptSource.ClosedWritersBound -or
        $attemptSource.TryBindClosedWriters($clone.Invoke($join,$null)) -or
        -not $attemptSource.TryBindClosedWriters($join) -or -not $attemptSource.ClosedWritersBound -or
        $attemptSource.TryBindClosedWriters($join) -or $attemptSource.LiveProofEligible -or
        $attemptSource.TransferIntegrityEstablished -or $attemptSource.PublicationCompletionIssued) {throw 'Closed writer/source association failed'}
    [NLink.Tests.AttemptWorkProfileForTest]::Mark('after_source_bind_controls')
    if([NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptSource].GetConstructors().Length -or
        @([NLink.Tools.LocalGuiWriterRequest+CanonicalAttemptSource].GetProperties() | Where-Object {$null -ne $_.GetSetMethod()}).Count) {
        throw 'Source owner publicly constructible/mutable'
    }
    Assert-SourceProtected $autopickPath
    [NLink.Tests.AttemptWorkProfileForTest]::Mark('after_source_protection_check')
    if($env:NLINK_TEST_DIAGNOSTIC_CUSTODY -cin @('frontier-schedule','frontier-late-publication')) {
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierDiagnosticGuiHandoff.ps1')
    }
    if($attemptCase -cin @('writer-source-read-cross-process-gui-valid','writer-source-read-cross-process-carrier-full-gui-valid','writer-source-read-cross-process-carrier-full-canonical-tail-late-publication')) {
        # Both source and destination must join before this fixture deliberately
        # retires the shared writer owner below. Never reopen that one-shot join.
        . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptDestinationJoin.ps1')
    }
    if($attemptCase -ceq 'writer-source-cancel') {
        $private:closed=$attemptTicket.GetType().GetField('originClosed',$flags).GetValue($attemptTicket)
        [void]$closed.Set()
        if($attemptSource.IsCurrent) {throw 'Cancelled source current'}
        [void]$closed.Reset()
    } elseif($attemptCase -ceq 'writer-source-closed') {
        $private:held=$attemptSource.GetType().GetField('Held',$flags).GetValue($attemptSource)
        $held.GetType().GetField('anchor',$flags).GetValue($held).Dispose()
    } elseif($attemptCase -ceq 'writer-source-dispose') {
        $private:ticketCopy=$clone.Invoke($attemptTicket,$null)
        $ticketCopy.Dispose()
        if(-not $attemptSource.IsCurrent) {throw 'Copied attempt disposed source'}
        $attemptTicket.Dispose()
    } else {
        $join.Dispose()
    }
    if($attemptSource.IsCurrent -or $attemptSource.ClosedWritersBound -or $null -ne $attemptSource.Sha256 -or
        $null -ne $attemptSource.FileIdentity -or $attemptSource.ByteCount -ne 0 -or
        $attemptSource.TryBindClosedWriters($join) -or
        $null -ne $attemptTicket.TryHoldSourceForTest($frontierHelpeeRequest,$autopickPath)) {throw 'Retired source revived'}
    $attemptSource.Dispose()
    if($env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -and $payloadOffered) {Assert-SourceProtected $autopickPath}
    else {
        $private:exclusive=[IO.File]::Open($autopickPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $exclusive.Dispose()
        [IO.File]::Move($autopickPath,$autopickPath+'.moved')
        [IO.File]::Move($autopickPath+'.moved',$autopickPath)
    }
    'attempt-source-custody-ok'
}
