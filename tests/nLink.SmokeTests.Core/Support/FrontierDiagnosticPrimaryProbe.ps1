param([ValidateSet('Capture','Closed')][string]$Stage)
$diagFlags=[Reflection.BindingFlags]'Instance,NonPublic'
function Read-DiagnosticField($Value,[string]$Name) {return $Value.GetType().GetField($Name,$diagFlags).GetValue($Value)}
function Get-DiagnosticHash([byte[]]$Bytes) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try {return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose()}
}
if($Stage -ceq 'Capture') {
    if($null -eq $nativePrimaryCustody -or $null -eq $nativePrimaryPair -or $null -eq $nativePrimaryPayloads -or
        -not $nativePrimaryCustody.Owns($nativePrimaryPair,$nativePrimaryContext,$nativePrimaryFull,$nativePrimaryCompact,$fullSnapshotEvaluation,$compactSnapshotEvaluation)) {
        throw 'Original native diagnostic evaluation/payload catalog missing'
    }
    $diagRows=@($nativePrimaryPayloads.GetPayloads())
    if($diagRows.Count -ne 1 -or -not $nativePrimaryPayloads.Owns($diagRows[0])) {throw 'Expected one original native receipt'}
    $script:diagnosticPayload=$diagRows[0]
    $diagPayload=$script:diagnosticPayload; $diagReceipt=$diagPayload.Text | ConvertFrom-Json
    $diagBytes=[Text.UTF8Encoding]::new($false,$true).GetBytes($diagPayload.Text)
    if($diagBytes.Length -ne $diagPayload.ByteCount -or (Get-DiagnosticHash $diagBytes) -cne $diagPayload.Sha256 -or
        [guid]$diagReceipt.ObservationId -ne $diagPayload.ObservationId -or
        $diagReceipt.Format -cne 'nlink-frontier-boundary-producer-v1' -or
        $diagReceipt.HashDomain -cne 'sha256-utf8-frontier-boundary-producer-v1' -or
        $diagReceipt.ClockDomain -cne 'frontier_publication_boundary_utc_qpc_v1' -or
        $diagReceipt.Outcome -cne 'PublishedBeforeOriginalDeadline' -or $diagReceipt.LiveProofEligible -or
        $diagReceipt.NativeWriterCustodyEstablished -or $diagReceipt.PublicationCompletionIssued) {throw 'Original diagnostic receipt identity/schema changed'}
    $diagInputs=Read-DiagnosticField $nativePrimaryCustody 'inputs'
    $script:diagnosticPins=@(Read-DiagnosticField (Read-DiagnosticField $diagInputs 'pins') 'Handles')
    if($script:diagnosticPins.Count -ne 11 -or @($script:diagnosticPins | Where-Object IsClosed).Count) {throw 'Original diagnostic native pins missing'}
    $diagFacts=Read-DiagnosticField $diagInputs 'facts'
    $diagArgs=[object[]]@([int]0,$null)
    $diagFact=$diagFacts.GetType().GetMethod('PayloadAt',$diagFlags).Invoke($diagFacts,$diagArgs)
    if($diagArgs[1] -cne $diagPayload.WriterId -or (Read-DiagnosticField $diagFact 'Observation') -ne $diagPayload.ObservationId) {
        throw 'Receipt disconnected from original writer manifest'
    }
    $diagOrigins=@{}; $diagRanges=@{}
    foreach($diagName in @('Completion','Marker')) {
        $diagRange=Read-DiagnosticField $diagFact $diagName; $diagRanges[$diagName]=$diagRange
        $diagOffset=[long](Read-DiagnosticField $diagRange 'Offset'); $diagLength=[long](Read-DiagnosticField $diagRange 'Length')
        $diagFirst=$diagFacts.GetType().GetMethod('LineAt',$diagFlags).Invoke($diagFacts,[object[]]@($diagOffset))
        $diagLast=$diagFacts.GetType().GetMethod('RangeLastLine',$diagFlags).Invoke($diagFacts,[object[]]@([long]($diagOffset+$diagLength+2)))
        if($diagFirst -lt 1 -or $diagFirst -ne $diagLast -or
            (Read-DiagnosticField $diagRange 'First') -ne (Read-DiagnosticField $diagRange 'Last')) {throw 'Native diagnostic range lost exact origin'}
        $diagOrigins[$diagName]=[int]$diagFirst
    }
    if($diagOrigins.Marker -le $diagOrigins.Completion) {throw 'Native diagnostic marker preceded completion'}
    # The normal semantic catalog need not include the marker. Its physical
    # origin above comes from the original native manifest, not a new witness.
    foreach($diagView in @($fullSnapshotEvaluation,$compactSnapshotEvaluation)) {
        if($diagView.Mode -cne 'SnapshotV1Authoritative' -or -not $diagView.StreamComplete -or $diagView.ArtifactEvidenceVerdict -cne 'pass') {
            throw 'Original diagnostic coordinator evidence incomplete'
        }
        $diagBoundary=@($diagView.Boundaries | Where-Object {$_.coordinatorInstanceId -ceq $diagReceipt.Completion.CoordinatorInstanceId -and
            $_.boundaryId -ceq $diagReceipt.Completion.TransitionId})
        if($diagBoundary.Count -ne 1) {throw 'Diagnostic completion boundary missing/ambiguous'}
        $diagBoundary=$diagBoundary[0]
        $diagLines=$nativePrimaryFull
        if([object]::ReferenceEquals($diagView,$compactSnapshotEvaluation)) {$diagLines=$nativePrimaryCompact}
        $diagIndex=[int]$diagBoundary.evidenceLineIndex
        if($diagIndex -lt 0 -or $diagIndex -ge $diagLines.Length -or $diagBoundary.verdict -cne 'pass' -or
            $diagBoundary.decisionKind -cne 'FallbackControlPlaneSendCompleted' -or
            $diagBoundary.decisionKind -cne $diagReceipt.Completion.DecisionKind -or
            $diagBoundary.decisionStatus -cne $diagReceipt.Completion.DecisionStatus -or
            $diagBoundary.decisionStatus -cnotin @('Accepted','Idempotent') -or
            $diagBoundary.boundaryHash -cne $diagReceipt.Completion.SnapshotSha256 -or
            $diagBoundary.writer -cne $diagPayload.WriterId -or
            $diagBoundary.writerSequence -ne (Read-DiagnosticField $diagRanges.Completion 'First') -or
            $nativePrimaryCustody.GetPhysicalOrigin($nativePrimaryPair,$diagLines,$diagView,$diagIndex+1) -ne $diagOrigins.Completion) {
            throw 'Diagnostic completion lost original evaluation/native origin'
        }
        $diagHash=Get-DiagnosticHash ([Text.Encoding]::UTF8.GetBytes($diagLines[$diagIndex]))
        if($diagHash -cne [BitConverter]::ToString((Read-DiagnosticField $diagRanges.Completion 'RawHash')).Replace('-','').ToLowerInvariant()) {
            throw 'Diagnostic completion differs from native writer receipt'
        }
    }
    $script:diagnosticObservation=[ordered]@{ObservationId=$diagPayload.ObservationId.ToString('N');WriterId=$diagPayload.WriterId;
        ByteCount=$diagPayload.ByteCount;Sha256=$diagPayload.Sha256;Outcome=$diagReceipt.Outcome;
        SessionId=$diagReceipt.Source.SessionId;TransferId=$diagReceipt.Source.TransferId;
        CompletionPhysicalLine=$diagOrigins.Completion;MarkerPhysicalLine=$diagOrigins.Marker;
        CompletionRawSha256=$diagHash;NativePins=$script:diagnosticPins.Count;LiveProofEligible=$false}
    return
}
if($null -eq $script:diagnosticObservation) {throw 'Diagnostic receipt capture did not complete'}
$diagSelected=if($null -eq $firstCompactProofChainFailure) {'(none)'} else {$firstCompactProofChainFailure.Class}
$diagResolution=[bool](Read-DiagnosticField $nativePrimaryCustody 'resolutionAttempted')
$diagWitness=[bool](Read-DiagnosticField $nativePrimaryCustody 'witnessesAttempted')
$diagOpen=@($script:diagnosticPins | Where-Object {-not $_.IsClosed}).Count
$script:diagnosticObservation.SelectedFailure=$diagSelected
$script:diagnosticObservation.PrimaryVerdict=$verdict
$script:diagnosticObservation.GateStatus=$gateStatus
$script:diagnosticObservation.ResolverCalls=$script:diagnosticResolverCalls
$script:diagnosticObservation.ResolutionAttempted=$diagResolution
$script:diagnosticObservation.WitnessesAttempted=$diagWitness
$script:diagnosticObservation.OpenPinsAfterCleanup=$diagOpen
$script:diagnosticObservation.PayloadTextCleared=$null -eq $script:diagnosticPayload.Text
[IO.File]::WriteAllText((Join-Path $ArtifactDir 'diagnostic-primary-observation.json'),
    ($script:diagnosticObservation | ConvertTo-Json -Depth 5))
if($diagOpen -or $null -ne $script:diagnosticPayload.Text) {throw 'Diagnostic primary retained native handles or payload text'}
if($diagSelected -ceq 'filetransfer_fallback_frontier_recent_progress_grace_recheck_missing') {
    throw 'Positive diagnostic receipt selected an unresolved primary failure; preserve original result for review'
}
if($script:diagnosticResolverCalls -ne 0 -or $diagResolution -or $diagWitness) {throw 'Ineligible primary failure invoked diagnostic witness selection'}
$script:diagnosticChecked=$true
Write-Host ('attempt-diagnostic-primary; selected_failure='+$diagSelected+'; diagnostic_witness=unavailable')
