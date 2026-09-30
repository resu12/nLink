[NLink.Tests.AttemptWorkProfileForTest]::Mark('before_destination_bind_controls')
$private:attemptDestination=$attemptTicket.GetDestinationForTest()
if($null -eq $attemptDestination -or -not $attemptDestination.IsCurrent -or
    $attemptDestination.TryBindClosedWriters($clone.Invoke($join,$null)) -or
    -not $attemptDestination.TryBindClosedWriters($join) -or
    $attemptDestination.TryBindClosedWriters($join)) {throw 'Destination closed-writer join missing/repeated'}
[NLink.Tests.AttemptWorkProfileForTest]::Mark('after_destination_bind_controls')
if($attemptCase -cin @('writer-source-read-cross-process-gui-valid','writer-source-read-cross-process-carrier-full-gui-valid','writer-source-read-cross-process-carrier-full-canonical-tail-late-publication') -and
    (-not $attemptSource.IsCurrent -or -not $attemptSource.ClosedWritersBound -or -not $join.IsCurrent)) {
    throw 'Source/destination writer custody did not coexist'
}
[NLink.Tests.AttemptWorkProfileForTest]::Mark('after_coexistence')
if($env:NLINK_TEST_PAYLOAD_WRAPPER_CASE) {
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierAttemptPayloadWrapperProbe.ps1') -Stage GuiOffer
}
[NLink.Tests.AttemptWorkProfileForTest]::Mark('after_payload_offer')
if($env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -like 'writer-*') {
    foreach($nativeWriterStep in @('copy','persist','prepare','complete')) {
        $private:accepted=switch($nativeWriterStep) {
            'copy' {$null -ne $binding.TryCopySemanticEvidence()}
            'persist' {$null -ne $binding.TryPersistEvidence()}
            'prepare' {$binding.TryPrepareParentHandoffWhenReady()}
            'complete' {$binding.TryCompleteParentHandoff()}
        }
        $private:activeMs=$adoption.GetType().GetProperty('ActiveMilliseconds',$flags).GetValue($adoption)
        [Console]::Error.WriteLine('payload-writer-finalization; stage='+$nativeWriterStep+'; accepted='+$accepted+'; active_ms='+$activeMs)
        if(-not $accepted) {
            foreach($fieldName in @('parentHandoffAttempted','parentPreparationAttempted','parentSubmissionAttempted','parentFactsAttempted','parentAcknowledgmentAttempted','parentCompletionAttempted')) {
                [Console]::Error.WriteLine('payload-writer-finalization; '+$fieldName+'='+$binding.GetType().GetField($fieldName,$flags).GetValue($binding))
            }
            throw ('Original writer parent handoff incomplete: '+$nativeWriterStep)
        }
    }
    if($binding.IsCurrent -or $join.IsCurrent -or $capture.IsCurrent) {throw 'Completed native writer handoff retained GUI custody'}
    if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE) {
        # Restore the ordinary artifact copy. The existing native duplicates
        # retain their original deny-write share; the new reader only shares
        # that file object's creation access. This grants no native ownership.
        $private:ordinary=Copy-GuiSmokeFileAtomicCreateNew -SourcePath $capture.Semantic.CreationPath `
            -DestinationPath (Join-Path $Directory 'filetransfer-retained-log-slice.log') -AllowSourceWrite
        if($ordinary.Sha256 -cne $capture.Semantic.Sha256 -or $ordinary.ByteCount -ne $capture.Semantic.ByteCount -or
            $ordinary.LineCount -ne $capture.Semantic.LineCount) {throw 'Original ordinary source copy differs'}
        $private:writeProbe=$null
        try {$writeProbe=[IO.File]::Open($capture.Semantic.CreationPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::ReadWrite)} catch [IO.IOException] {}
        if($null -ne $writeProbe) {$writeProbe.Dispose();throw 'Ordinary copy weakened native log protection'}
    }
    'attempt-payload-native-writer-handoff-ok'
}
if($env:NLINK_TEST_DESTINATION_RETIRE) {
    if($attemptCase -cne 'writer-source-read-cross-process-gui-valid' -or $env:NLINK_TEST_PAYLOAD_WRAPPER_CASE) {
        throw 'Destination retirement requires the isolated offline profile'
    }
    if($env:NLINK_TEST_DESTINATION_RETIRE -ceq 'source') {$attemptSource.Dispose()}
    elseif($env:NLINK_TEST_DESTINATION_RETIRE -ceq 'attempt') {$attemptTicket.Dispose()}
    else {throw 'Unknown destination retirement'}
    if($attemptDestination.IsCurrent -or $attemptDestination.ByteCount -ne 0 -or
        $null -ne $attemptDestination.FileIdentity -or $null -ne $attemptDestination.Sha256Base64) {throw 'Retired custody left destination current'}
    'canonical-destination-retired='+$env:NLINK_TEST_DESTINATION_RETIRE
}
$attemptDestination.Dispose()
if($attemptDestination.IsCurrent) {throw 'Retired destination current'}
$private:destinationPath=Join-Path $Directory 'native-destination/native-receive.bin'
if($attemptCase -cin @('writer-source-read-cross-process-gui-valid','writer-source-read-cross-process-carrier-full-gui-valid','writer-source-read-cross-process-carrier-full-canonical-tail-late-publication')) {
    $private:receiveRoot=if($attemptCase -cin @('writer-source-read-cross-process-carrier-full-gui-valid','writer-source-read-cross-process-carrier-full-canonical-tail-late-publication')) {'native-carrier-received'} else {'native-received'}
    $private:files=@(Get-ChildItem -LiteralPath (Join-Path $Directory $receiveRoot) -File -Recurse -Filter '*.bin')
    if($files.Count -ne 1) {throw 'Unexpected received-file cleanup inventory'}
    $destinationPath=$files[0].FullName
}
if($env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -and $payloadOffered) {Assert-SourceProtected $destinationPath}
else {
    $private:exclusive=[IO.File]::Open($destinationPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $exclusive.Dispose()
}
'attempt-destination-closed-writers-ok'
