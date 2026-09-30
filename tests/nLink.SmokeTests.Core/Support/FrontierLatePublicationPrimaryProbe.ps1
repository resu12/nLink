param([ValidateSet('Capture','BeforeResolve','AfterResolve','AfterOptional','Closed')][string]$Stage,[switch]$CanonicalTail)
$lateFlags=[Reflection.BindingFlags]'Instance,NonPublic'
function Read-LatePublicationField($Value,[string]$Name) {return $Value.GetType().GetField($Name,$lateFlags).GetValue($Value)}
function Get-LatePublicationHash([byte[]]$Bytes) {
    $sha=[Security.Cryptography.SHA256]::Create()
    try {return [BitConverter]::ToString($sha.ComputeHash($Bytes)).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose()}
}
function Test-LateReceipt($Receipt,[bool]$CanonicalTail=$false) {
    $first=if($CanonicalTail) {12476} else {0}
    $committed=if($CanonicalTail) {268369920L} else {86016L}
    if($Receipt.Outcome -cne 'PublishedWithinObservationBound' -or $Receipt.LiveProofEligible -or
        $Receipt.NativeWriterCustodyEstablished -or $Receipt.PublicationCompletionIssued -or
        $Receipt.RootGeneration -ne $Receipt.RecheckedGeneration -or $Receipt.RootGeneration -ne $Receipt.FirstWriteGeneration -or
        $Receipt.Source.Route -cne 'post_tuna_fallback_v6' -or $Receipt.Source.ProtocolVersion -ne 6 -or
        $Receipt.Frame.ContiguousCommittedChunkIndex -ne ($first+4) -or $Receipt.Frame.DurableReceivedHighestChunkIndex -ne ($first+7) -or
        $Receipt.Frame.BytesCommitted -ne $committed -or $Receipt.Frame.TransferPaused -or $Receipt.Frame.TerminalReady -or
        $Receipt.Frame.TransportEpoch -ne $Receipt.Source.TransportEpoch -or $Receipt.Frame.MissingRanges.Count -gt 8 -or
        $Receipt.Completion.DecisionKind -cne 'FallbackControlPlaneSendCompleted' -or
        $Receipt.Completion.DecisionStatus -cnotin @('Accepted','Idempotent') -or
        @($Receipt.Lifecycle | Where-Object Kind -ceq 'StallSuccessor').Count) {throw 'Late native receipt shape or root changed'}
    $write=@($Receipt.Lifecycle | Where-Object Kind -ceq 'Write')
    $completion=@($Receipt.Lifecycle | Where-Object Kind -ceq 'AcceptedCompletion')
    if($write.Count -ne 1 -or $completion.Count -ne 1 -or $write[0].Binding.Before -ne ($first+1) -or
        $write[0].Binding.After -ne ($first+4) -or $write[0].Binding.Count -ne 3 -or $write[0].Binding.Highest -ne ($first+7) -or
        $write[0].Clock.UtcTicks -ge $Receipt.OriginalDeadlineUtcTicks -or
        $completion[0].Clock.UtcTicks -lt $Receipt.OriginalDeadlineUtcTicks -or
        $completion[0].Clock.UtcTicks -ge $Receipt.PublicationDeadlineUtcTicks -or
        $Receipt.PublicationDeadlineUtcTicks -ne $write[0].Clock.UtcTicks+10000000) {throw 'Late native write/completion window missing'}
    # The producer's original outcome is required above. These identity/order
    # checks do not call its timing evaluator or issue another result.
    $sequence=0L; $counter=0L
    foreach($entry in $Receipt.Lifecycle) {
        if($null -eq $entry.Clock) {continue}
        $clock=$entry.Clock
        if($clock.ProcessId -ne $Receipt.ProcessId -or $clock.ProcessCreationFileTime -ne $Receipt.ProcessCreationFileTime -or
            $clock.ObservationId -cne $Receipt.ObservationId -or $clock.ClockInstanceId -cne $Receipt.ClockInstanceId -or
            $clock.Frequency -ne $Receipt.Frequency -or $clock.CaptureSequence -le $sequence -or
            $clock.CounterBefore -lt $counter -or $clock.CounterAfter -lt $clock.CounterBefore) {throw 'Late native clock identity/order changed'}
        $sequence=$clock.CaptureSequence; $counter=$clock.CounterAfter
    }
}
function Get-LateReports {
    $hashes=[ordered]@{}
    foreach($name in @('filetransfer-operator-verdict.txt','filetransfer-route-consistency-summary.txt',
        'transfer-terminal-summary.txt','stability-gates-summary.txt','phase0-invariant-ledger.txt','filetransfer-invariant-summary.txt',
        'filetransfer-retained-compact-summary.json','filetransfer-coordinator-snapshot-compact-summary.json',
        'filetransfer-coordinator-snapshot-full-summary.json','filetransfer-coordinator-snapshot-parity-summary.json')) {
        $hashes[$name]=(Get-FileHash -LiteralPath (Join-Path $ArtifactDir $name) -Algorithm SHA256).Hash
    }
    return ($hashes | ConvertTo-Json -Compress)
}
function Get-LateState {
    $views=@($fullSnapshotEvaluation,$compactSnapshotEvaluation) | ForEach-Object {
        $_ | Select-Object * -ExcludeProperty __HeldPublicationProofClosures,__FallbackTailPeerProofBindings,__RoutePublicationEffects,__CheckpointRepairContinuityEvidence
    }
    return ([ordered]@{Views=$views;Verdict=$verdict;GateStatus=$gateStatus;HardFailures=$hardFailureCount;
        Candidates=$compactProofChainCandidateArray.Count;Failure=$firstCompactProofChainFailure;
        Unresolved=$fileTransferFallbackFrontierRecentProgressGraceRecheckProof.UnresolvedCount;
        Recovered=$fileTransferFallbackFrontierRecentProgressGraceRecheckProof.RecoveredCount} | ConvertTo-Json -Depth 64 -Compress)
}
function Assert-LateStateUnchanged {
    if((Get-LateState) -cne $script:latePublicationState -or
        -not [object]::ReferenceEquals($script:latePublicationFailure,$firstCompactProofChainFailure) -or
        -not [object]::ReferenceEquals($script:latePublicationFull,$fullSnapshotEvaluation) -or
        -not [object]::ReferenceEquals($script:latePublicationCompact,$compactSnapshotEvaluation) -or
        $compactProofChainCandidateArray.Count -ne $script:latePublicationCandidates.Count) {throw 'Original primary state changed around optional resolver'}
    for($i=0;$i -lt $compactProofChainCandidateArray.Count;$i++) {
        if(-not [object]::ReferenceEquals($compactProofChainCandidateArray[$i],$script:latePublicationCandidates[$i])) {throw 'Original candidate identity changed'}
    }
}
function Test-LateOutput($Custody,$Preparation) {
    if($null -eq $Preparation -or $Preparation.LiveProofEligible -or
        $Preparation.NativeWriterCustodyEstablished -or $Preparation.PublicationCompletionIssued) {
        throw 'Original diagnostic preparation unavailable'
    }
    $inputs=Read-LatePublicationField $Custody 'inputs'
    $channel=Read-LatePublicationField $inputs 'diagnosticOutput'
    if(-not (Read-LatePublicationField $Custody 'diagnosticOffered') -or
        -not (Read-LatePublicationField $Custody 'diagnosticWaitAttempted') -or
        -not (Read-LatePublicationField $Custody 'diagnosticCommitAttempted') -or
        $null -eq $channel -or -not (Read-LatePublicationField $channel 'committed')) {throw 'Original diagnostic output was not adopted and committed'}
}
if($Stage -ceq 'Capture') {
    if($null -eq $nativePrimaryCustody -or $null -eq $nativePrimaryPair -or $null -eq $nativePrimaryPayloads -or
        -not $nativePrimaryCustody.Owns($nativePrimaryPair,$nativePrimaryContext,$nativePrimaryFull,$nativePrimaryCompact,$fullSnapshotEvaluation,$compactSnapshotEvaluation)) {
        throw 'Original native latePublication evaluation/payload catalog missing'
    }
    $lateRows=@($nativePrimaryPayloads.GetPayloads())
    if($lateRows.Count -ne 1 -or -not $nativePrimaryPayloads.Owns($lateRows[0])) {throw 'Expected one original native receipt'}
    $script:latePublicationPayload=$lateRows[0]
    $latePayload=$script:latePublicationPayload; $lateReceipt=$latePayload.Text | ConvertFrom-Json
    $lateBytes=[Text.UTF8Encoding]::new($false,$true).GetBytes($latePayload.Text)
    if($lateBytes.Length -ne $latePayload.ByteCount -or (Get-LatePublicationHash $lateBytes) -cne $latePayload.Sha256 -or
        [guid]$lateReceipt.ObservationId -ne $latePayload.ObservationId -or
        $lateReceipt.Format -cne 'nlink-frontier-boundary-producer-v1' -or
        $lateReceipt.HashDomain -cne 'sha256-utf8-frontier-boundary-producer-v1' -or
        $lateReceipt.ClockDomain -cne 'frontier_publication_boundary_utc_qpc_v1' -or
        $lateReceipt.Outcome -cne 'PublishedWithinObservationBound' -or $lateReceipt.LiveProofEligible -or
        $lateReceipt.NativeWriterCustodyEstablished -or $lateReceipt.PublicationCompletionIssued) {throw 'Original latePublication receipt identity/schema changed'}
    $lateInputs=Read-LatePublicationField $nativePrimaryCustody 'inputs'
    $script:latePublicationPins=@(Read-LatePublicationField (Read-LatePublicationField $lateInputs 'pins') 'Handles')
    if($script:latePublicationPins.Count -ne 11 -or @($script:latePublicationPins | Where-Object IsClosed).Count) {throw 'Original latePublication native pins missing'}
    $lateFacts=Read-LatePublicationField $lateInputs 'facts'
    $lateArgs=[object[]]@([int]0,$null)
    $lateFact=$lateFacts.GetType().GetMethod('PayloadAt',$lateFlags).Invoke($lateFacts,$lateArgs)
    if($lateArgs[1] -cne $latePayload.WriterId -or (Read-LatePublicationField $lateFact 'Observation') -ne $latePayload.ObservationId) {
        throw 'Receipt disconnected from original writer manifest'
    }
    $lateOrigins=@{}; $lateRanges=@{}
    foreach($lateName in @('Completion','Marker')) {
        $lateRange=Read-LatePublicationField $lateFact $lateName; $lateRanges[$lateName]=$lateRange
        $lateOffset=[long](Read-LatePublicationField $lateRange 'Offset'); $lateLength=[long](Read-LatePublicationField $lateRange 'Length')
        $lateFirst=$lateFacts.GetType().GetMethod('LineAt',$lateFlags).Invoke($lateFacts,[object[]]@($lateOffset))
        $lateLast=$lateFacts.GetType().GetMethod('RangeLastLine',$lateFlags).Invoke($lateFacts,[object[]]@([long]($lateOffset+$lateLength+2)))
        if($lateFirst -lt 1 -or $lateFirst -ne $lateLast -or
            (Read-LatePublicationField $lateRange 'First') -ne (Read-LatePublicationField $lateRange 'Last')) {throw 'Native latePublication range lost exact origin'}
        $lateOrigins[$lateName]=[int]$lateFirst
    }
    if($lateOrigins.Marker -le $lateOrigins.Completion) {throw 'Native latePublication marker preceded completion'}
    # The normal semantic catalog need not include the marker. Its physical
    # origin above comes from the original native manifest, not a new witness.
    foreach($lateView in @($fullSnapshotEvaluation,$compactSnapshotEvaluation)) {
        if($lateView.Mode -cne 'SnapshotV1Authoritative' -or -not $lateView.StreamComplete -or $lateView.ArtifactEvidenceVerdict -cne 'pass') {
            throw 'Original latePublication coordinator evidence incomplete'
        }
        $lateBoundary=@($lateView.Boundaries | Where-Object {$_.coordinatorInstanceId -ceq $lateReceipt.Completion.CoordinatorInstanceId -and
            $_.boundaryId -ceq $lateReceipt.Completion.TransitionId})
        if($lateBoundary.Count -ne 1) {throw 'LatePublication completion boundary missing/ambiguous'}
        $lateBoundary=$lateBoundary[0]
        $lateLines=$nativePrimaryFull
        if([object]::ReferenceEquals($lateView,$compactSnapshotEvaluation)) {$lateLines=$nativePrimaryCompact}
        $lateIndex=[int]$lateBoundary.evidenceLineIndex
        if($lateIndex -lt 0 -or $lateIndex -ge $lateLines.Length -or $lateBoundary.verdict -cne 'pass' -or
            $lateBoundary.decisionKind -cne 'FallbackControlPlaneSendCompleted' -or
            $lateBoundary.decisionKind -cne $lateReceipt.Completion.DecisionKind -or
            $lateBoundary.decisionStatus -cne $lateReceipt.Completion.DecisionStatus -or
            $lateBoundary.decisionStatus -cnotin @('Accepted','Idempotent') -or
            $lateBoundary.boundaryHash -cne $lateReceipt.Completion.SnapshotSha256 -or
            $lateBoundary.writer -cne $latePayload.WriterId -or
            $lateBoundary.writerSequence -ne (Read-LatePublicationField $lateRanges.Completion 'First') -or
            $nativePrimaryCustody.GetPhysicalOrigin($nativePrimaryPair,$lateLines,$lateView,$lateIndex+1) -ne $lateOrigins.Completion) {
            throw 'LatePublication completion lost original evaluation/native origin'
        }
        $lateHash=Get-LatePublicationHash ([Text.Encoding]::UTF8.GetBytes($lateLines[$lateIndex]))
        if($lateHash -cne [BitConverter]::ToString((Read-LatePublicationField $lateRanges.Completion 'RawHash')).Replace('-','').ToLowerInvariant()) {
            throw 'LatePublication completion differs from native writer receipt'
        }
    }
    Test-LateReceipt $lateReceipt -CanonicalTail ([bool]$CanonicalTail)
    $script:latePublicationObservation=[ordered]@{ObservationId=$latePayload.ObservationId.ToString('N');WriterId=$latePayload.WriterId;
        ByteCount=$latePayload.ByteCount;Sha256=$latePayload.Sha256;Outcome=$lateReceipt.Outcome;
        SessionId=$lateReceipt.Source.SessionId;TransferId=$lateReceipt.Source.TransferId;
        CompletionPhysicalLine=$lateOrigins.Completion;MarkerPhysicalLine=$lateOrigins.Marker;
        CompletionRawSha256=$lateHash;NativePins=$script:latePublicationPins.Count;LiveProofEligible=$false;
        NativeWriterCustodyEstablished=$false;PublicationCompletionIssued=$false}
    return
}
if($Stage -ceq 'BeforeResolve') {
    $script:latePublicationOmissionReason=$null
    $script:latePublicationResolverCalls++
    $script:latePublicationFailure=$firstCompactProofChainFailure
    $script:latePublicationFull=$fullSnapshotEvaluation; $script:latePublicationCompact=$compactSnapshotEvaluation
    $script:latePublicationCandidates=@($compactProofChainCandidateArray)
    $script:latePublicationState=Get-LateState
    $script:latePublicationReportsBefore=Get-LateReports
    return
}
if($Stage -ceq 'AfterResolve') {
    if($null -eq $nativeWitness) {
        [Console]::Error.WriteLine('late-publication-resolver-omission; reason_json='+
            (ConvertTo-Json -InputObject $script:latePublicationOmissionReason -Compress))
    }
    $script:latePublicationReportsAfter=Get-LateReports
    if($script:latePublicationReportsAfter -cne $script:latePublicationReportsBefore) {throw 'Original reports changed during optional resolver'}
    Assert-LateStateUnchanged
    if($null -eq $nativeWitness) {throw 'Original optional resolver produced no witness'}
    $batch=$nativeWitness.Batch
    if($nativeWitness.Status -cne 'matched_native_frontier_diagnostic' -or
        $nativeWitness.DiagnosticOutcome -cne 'PublishedWithinObservationBound' -or
        $nativeWitness.LiveProofEligible -or $nativeWitness.NativeWriterCustodyEstablished -or $nativeWitness.PublicationCompletionIssued -or
        $batch.LiveProofEligible -or $batch.NativeWriterCustodyEstablished -or $batch.PublicationCompletionIssued -or
        $nativeWitness.OriginalSnapshot -cne $batch.DiagnosticSnapshot -or
        -not [object]::ReferenceEquals($batch.Payload,$script:latePublicationPayload)) {throw 'Original witness receipt or authority flags changed'}
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FrontierWitnessVerificationDiagnostics.ps1')
    $script:latePublicationWitnesses=@($batch.GetWitnesses())
    if(Test-LateWitnessRejectedForTest 'capacity' $null {$script:latePublicationWitnesses.Count -lt 1 -or $script:latePublicationWitnesses.Count -gt 64} $nativePrimaryCustody $nativePrimaryFull $nativePrimaryCompact $nativePrimaryPair) {throw 'Original witness count outside bound'}
    $seen=[Collections.Generic.HashSet[int]]::new()
    foreach($row in $script:latePublicationWitnesses) {
        if((Test-LateWitnessRejectedForTest 'ownership' $row {-not $batch.Owns($row)} $nativePrimaryCustody $nativePrimaryFull $nativePrimaryCompact $nativePrimaryPair) -or
            (Test-LateWitnessRejectedForTest 'unique-index' $row {-not $seen.Add($row.FullIndex)} $nativePrimaryCustody $nativePrimaryFull $nativePrimaryCompact $nativePrimaryPair) -or
            (Test-LateWitnessRejectedForTest 'full-lower-bound' $row {$row.FullIndex -lt 1} $nativePrimaryCustody $nativePrimaryFull $nativePrimaryCompact $nativePrimaryPair) -or
            (Test-LateWitnessRejectedForTest 'full-upper-bound' $row {$row.FullIndex -gt $nativePrimaryFull.Length} $nativePrimaryCustody $nativePrimaryFull $nativePrimaryCompact $nativePrimaryPair) -or
            (Test-LateWitnessRejectedForTest 'full-text' $row {$row.Text -cne $nativePrimaryFull[$row.FullIndex-1]} $nativePrimaryCustody $nativePrimaryFull $nativePrimaryCompact $nativePrimaryPair) -or
            (Test-LateWitnessRejectedForTest 'line-hash' $row {(Get-LatePublicationHash ([Text.Encoding]::UTF8.GetBytes($row.Text))) -cne $row.LineSha256} $nativePrimaryCustody $nativePrimaryFull $nativePrimaryCompact $nativePrimaryPair) -or
            (Test-LateWitnessRejectedForTest 'full-origin' $row {$nativePrimaryCustody.GetPhysicalOrigin($nativePrimaryPair,$nativePrimaryFull,$fullSnapshotEvaluation,$row.FullIndex) -ne $row.SourceLine} $nativePrimaryCustody $nativePrimaryFull $nativePrimaryCompact $nativePrimaryPair)) {
            throw 'Witness lost original exact physical source line'
        }
        if($row.CompactIndex -gt 0 -and ((Test-LateWitnessRejectedForTest 'compact-upper-bound' $row {$row.CompactIndex -gt $nativePrimaryCompact.Length} $nativePrimaryCustody $nativePrimaryFull $nativePrimaryCompact $nativePrimaryPair) -or
            (Test-LateWitnessRejectedForTest 'compact-text' $row {$row.Text -cne $nativePrimaryCompact[$row.CompactIndex-1]} $nativePrimaryCustody $nativePrimaryFull $nativePrimaryCompact $nativePrimaryPair) -or
            (Test-LateWitnessRejectedForTest 'compact-origin' $row {$nativePrimaryCustody.GetPhysicalOrigin($nativePrimaryPair,$nativePrimaryCompact,$compactSnapshotEvaluation,$row.CompactIndex) -ne $row.SourceLine} $nativePrimaryCustody $nativePrimaryFull $nativePrimaryCompact $nativePrimaryPair))) {
            throw 'Witness lost original compact physical source line'
        }
    }
    $failureRows=@($script:latePublicationWitnesses | Where-Object FullIndex -eq $nativeWitness.FailureFullIndex)
    if($failureRows.Count -ne 1 -or $failureRows[0].SemanticIndex -ne $firstCompactProofChainFailure.Event.LineNumber -or
        $failureRows[0].SemanticIndex -ne $firstCompactProofChainFailure.Event.Sequence -or
        -not [object]::ReferenceEquals($nativePrimaryFull[$failureRows[0].FullIndex-1],$firstCompactProofChainFailure.Event.RawLine)) {
        throw 'Witness lost the original selected failure identity'
    }
    if($batch.Completion.FirstSourceLine -ne $script:latePublicationObservation.CompletionPhysicalLine -or
        $batch.Completion.LastSourceLine -ne $batch.Completion.FirstSourceLine -or
        $batch.Marker.FirstSourceLine -ne $script:latePublicationObservation.MarkerPhysicalLine -or
        $batch.Marker.LastSourceLine -ne $batch.Marker.FirstSourceLine) {throw 'Original witness native ranges changed'}
    $script:latePublicationResolved=$true
    return
}
if($Stage -ceq 'AfterOptional') {
    if($script:latePublicationResolverCalls -ne 1 -or $null -eq $script:latePublicationReportsBefore) {
        throw 'Original primary did not select the supported unresolved failure'
    }
    if((Get-LateReports) -cne $script:latePublicationReportsBefore) {throw 'Original reports changed during optional output'}
    Assert-LateStateUnchanged
    if(-not $script:latePublicationResolved) {throw 'Original diagnostic witness unavailable'}
    Test-LateOutput $nativePrimaryCustody $nativePreparation
    $script:latePublicationOutput=$true
    return
}
if($null -eq $script:latePublicationObservation) {throw 'Late publication receipt capture did not complete'}
$selected=if($null -eq $firstCompactProofChainFailure) {'(none)'} else {$firstCompactProofChainFailure.Class}
$open=@($script:latePublicationPins | Where-Object {-not $_.IsClosed}).Count
$cleared=$null -eq $script:latePublicationPayload.Text -and @($script:latePublicationWitnesses | Where-Object {$null -ne $_.Text}).Count -eq 0
$script:latePublicationObservation.SelectedFailure=$selected
$script:latePublicationObservation.PrimaryVerdict=$verdict; $script:latePublicationObservation.GateStatus=$gateStatus
$script:latePublicationObservation.ResolverCalls=$script:latePublicationResolverCalls
$script:latePublicationObservation.ResolverOmissionReason=$script:latePublicationOmissionReason
$script:latePublicationObservation.WitnessCount=$script:latePublicationWitnesses.Count
$script:latePublicationObservation.OriginalOutputCommitted=$script:latePublicationOutput
$script:latePublicationObservation.OpenPinsAfterCleanup=$open; $script:latePublicationObservation.TextCleared=$cleared
$script:latePublicationObservation.ReportsBefore=$script:latePublicationReportsBefore
$script:latePublicationObservation.ReportsAfter=$script:latePublicationReportsAfter
[IO.File]::WriteAllText((Join-Path $ArtifactDir 'late-publication-primary-observation.json'),($script:latePublicationObservation | ConvertTo-Json -Depth 5))
$script:latePublicationState=$null; $script:latePublicationFailure=$null
$script:latePublicationFull=$null; $script:latePublicationCompact=$null; $script:latePublicationCandidates=@()
$script:latePublicationWitnesses=@(); $script:latePublicationPayload=$null; $script:latePublicationPins=@()
$script:latePublicationOmissionReason=$null
if($open -or -not $cleared) {throw 'Late publication primary retained handles or text'}
if($selected -cne 'filetransfer_fallback_frontier_recent_progress_grace_recheck_missing' -or
    $script:latePublicationResolverCalls -ne 1 -or -not $script:latePublicationResolved -or -not $script:latePublicationOutput) {
    throw 'Late publication witness milestone remains unmet'
}
$script:latePublicationChecked=$true
