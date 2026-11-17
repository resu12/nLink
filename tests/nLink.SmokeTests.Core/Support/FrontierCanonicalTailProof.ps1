param([string]$Directory,$Receipt,[string]$AnalysisFailure,[string]$RouteCycleProfile='',[string]$WorkerConfiguration='')
# Only the explicit Debug cycle expects successful route proof; preserve the
# original parent's operator failure and the legacy failed-route requirement.
# A successful diagnostic join never promotes that classification or verdict.
if($RouteCycleProfile -cnotin @('','canonical-cycle-v1','canonical-ineligible-v1') -or ($RouteCycleProfile -and $WorkerConfiguration -cne 'Debug')) {
    throw 'Canonical tail proof requires the exact Debug cycle profile or legacy default'
}
$expectedRouteVerdict=if($RouteCycleProfile -ceq 'canonical-cycle-v1') {'pass'} else {'fail'}
$expectedStatus=if($AnalysisFailure) {'proof_incomplete'} else {'completed'}
if($null -eq $Receipt -or $Receipt.status -cne $expectedStatus -or
    [bool]$Receipt.completedProofArtifacts -eq [bool]$AnalysisFailure -or
    $Receipt.coordinatorSnapshotMode -cne 'SnapshotV1Authoritative' -or
    -not $Receipt.exitCodeAvailable -or $Receipt.exitCode -ne 0 -or $Receipt.timedOut -or -not $Receipt.analyzerInputUnchanged -or
    ($AnalysisFailure -and -not $AnalysisFailure.StartsWith('FileTransfer-Ops retained analysis exited successfully without complete proof artifacts for '))) {
    throw 'Canonical tail original analysis completion changed'
}
foreach($name in @('full','compact')) {
    $view=Get-Content -LiteralPath (Join-Path $Directory ('filetransfer-coordinator-snapshot-'+$name+'-summary.json')) -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if($view.Mode -cne 'SnapshotV1Authoritative' -or -not $view.StreamComplete -or $view.ArtifactEvidenceVerdict -cne 'pass') {
        throw 'Canonical tail snapshot evidence changed'
    }
}
$parity=Get-Content -LiteralPath (Join-Path $Directory 'filetransfer-coordinator-snapshot-parity-summary.json') -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
if($parity.Verdict -cne 'pass') {throw 'Canonical tail snapshot parity changed'}
$operator=Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $Directory 'filetransfer-operator-verdict.txt')
$route=Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $Directory 'filetransfer-route-consistency-summary.txt')
$ineligible=$RouteCycleProfile -ceq 'canonical-ineligible-v1'
$expectedPrimaryVerdict=if($ineligible) {'INCONCLUSIVE'} else {'FAIL_PROTOCOL_OR_INTEGRITY'}
if($operator['verdict'] -cne $expectedPrimaryVerdict -or $operator['gate_status'] -cne 'fail' -or
    $route['live_route_epoch_proof_verdict'] -cne $expectedRouteVerdict) {throw 'Canonical tail original failed route verdict changed'}
if($RouteCycleProfile -ceq 'canonical-cycle-v1') {
    foreach($key in @('file_tuna_route_cycle_delivery_verdict','file_tuna_activation_1_delivery_verdict',
        'file_tuna_activation_2_delivery_verdict','file_tuna_route_cycle_full_compact_parity_verdict')) {
        if($operator[$key] -cne 'pass') {throw ('Canonical cycle delivery proof changed: '+$key)}
    }
}
$observation=Get-Content -LiteralPath (Join-Path $Directory 'late-publication-primary-observation.json') -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
if($ineligible) {
    $cleanup=Get-Content -LiteralPath (Join-Path $Directory 'canonical-tail-ineligible-cleanup.json') -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if($observation.CanonicalTailProfile -cne 'canonical-tail-late-publication' -or $observation.SamePairJoined -or
        -not $observation.CanonicalTailIneligibleChecked -or $observation.TransferBindingAttempted -or
        $observation.Outcome -cne 'PublishedWithinObservationBound' -or
        $observation.SelectedFailure -cne 'filetransfer_tuna_activation_delivery_unproven' -or
        $observation.PrimaryVerdict -cne 'INCONCLUSIVE' -or $observation.GateStatus -cne 'fail' -or
        $observation.ResolverCalls -ne 0 -or $observation.WitnessCount -ne 0 -or $observation.OriginalOutputCommitted -or
        $observation.OpenPinsAfterCleanup -ne 0 -or -not $observation.TextCleared -or
        $observation.LiveProofEligible -or $observation.NativeWriterCustodyEstablished -or $observation.PublicationCompletionIssued -or
        [string]::IsNullOrWhiteSpace($observation.ReportsBefore) -or $observation.ReportsBefore -cne $observation.ReportsAfter -or
        $cleanup.Profile -cne 'canonical-ineligible-v1' -or $cleanup.HandleCount -ne 2 -or $cleanup.OpenHandles -ne 0 -or
        $cleanup.EvaluationCurrent -or $cleanup.TransferBound) {throw 'Canonical tail ineligibility or actual-handle cleanup evidence missing'}
    return
}
if($observation.CanonicalTailProfile -cne 'canonical-tail-late-publication' -or -not $observation.SamePairJoined -or
    $observation.Outcome -cne 'PublishedWithinObservationBound' -or
    $observation.SelectedFailure -cne 'filetransfer_fallback_frontier_recent_progress_grace_recheck_missing' -or
    $observation.PrimaryVerdict -cne 'FAIL_PROTOCOL_OR_INTEGRITY' -or $observation.GateStatus -cne 'fail' -or
    $observation.ResolverCalls -ne 1 -or $observation.WitnessCount -lt 1 -or $observation.WitnessCount -gt 64 -or
    -not $observation.OriginalOutputCommitted -or $observation.OpenPinsAfterCleanup -ne 0 -or -not $observation.TextCleared -or
    $observation.LiveProofEligible -or $observation.NativeWriterCustodyEstablished -or $observation.PublicationCompletionIssued -or
    [string]::IsNullOrWhiteSpace($observation.ReportsBefore) -or $observation.ReportsBefore -cne $observation.ReportsAfter) {
    throw 'Canonical tail same-pair witness/output/cleanup evidence missing'
}
