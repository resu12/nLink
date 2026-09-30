# Real original Core writers and native source/destination, with the existing
# augmented offline fixture. No manual role/payload joins on this path.
if($attemptCase -cne 'writer-source-read-cross-process-gui-valid' -or
    $env:NLINK_TEST_PAYLOAD_WRAPPER_CASE -cne 'writer-valid') {throw 'Unexpected closed-payload fixture profile'}
if($attemptTicket.TryOfferClosedPayloadsToWrapper($null) -or
    $attemptTicket.TryOfferClosedPayloadsToWrapper($clone.Invoke($binding,$null)) -or
    $clone.Invoke($attemptTicket,$null).TryOfferClosedPayloadsToWrapper($binding)) {throw 'Unowned payload composition admitted'}
foreach($name in @('source','destination')) {
    $field=$attemptTicket.GetType().GetField($name,$flags)
    $original=$field.GetValue($attemptTicket)
    if($null -eq $original -or -not $original.IsCurrent) {throw ('Original payload missing: '+$name)}
    try {
        $field.SetValue($attemptTicket,$null)
        if($attemptTicket.TryOfferClosedPayloadsToWrapper($binding)) {throw 'Missing payload composed'}
    } finally {$field.SetValue($attemptTicket,$original)}
}
if(-not $binding.IsCurrent -or -not $attemptTicket.IsCurrent -or
    $null -ne $attemptTicket.GetType().GetField('roleWriters',$flags).GetValue($attemptTicket)) {throw 'Rejected composition spent original writer scope'}
if($null -eq $binding.TryCopySemanticEvidence() -or $null -eq $binding.TryPersistEvidence()) {throw 'Original finalizer evidence missing'}
$private:CanonicalAttempt=$attemptTicket
$private:localFrontierBinding=$binding
$private:localWriterIssuer=$finalizer
. (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierGuiClosedPayloadHandoff.ps1') -RepoRoot $RepoRoot -Mode Offer
if(-not $frontierHandoffPrepared -or $binding.IsCurrent -or $capture.IsCurrent -or
    $attemptTicket.GetType().GetField('payloadOffered',$flags).GetValue($attemptTicket) -ne 1 -or
    $attemptTicket.TryOfferClosedPayloadsToWrapper($binding)) {throw 'Production closed-payload completion missing or repeated'}
if($env:NLINK_TEST_PAYLOAD_PRIMARY_CASE) {
    # The primary still requires the separate ordinary artifact, as on the
    # existing component path. This copy grants no native custody.
    $private:ordinary=Copy-GuiSmokeFileAtomicCreateNew -SourcePath $capture.Semantic.CreationPath `
        -DestinationPath (Join-Path $Directory 'filetransfer-retained-log-slice.log') -AllowSourceWrite
    if($ordinary.Sha256 -cne $capture.Semantic.Sha256 -or $ordinary.ByteCount -ne $capture.Semantic.ByteCount -or
        $ordinary.LineCount -ne $capture.Semantic.LineCount) {throw 'Original ordinary source copy differs'}
    $private:writeProbe=$null
    try {$writeProbe=[IO.File]::Open($capture.Semantic.CreationPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::ReadWrite)} catch [IO.IOException] {}
    if($null -ne $writeProbe) {$writeProbe.Dispose();throw 'Ordinary copy weakened native log protection'}
}
'attempt-payload-native-writer-handoff-ok'
'gui-original-closed-payload-handoff-ok'
