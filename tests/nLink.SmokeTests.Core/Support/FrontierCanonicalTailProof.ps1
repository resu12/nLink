param([string]$Directory,$Receipt,[string]$AnalysisFailure)
# Preserve the original parent's classification of the failed route proof.
# A successful diagnostic join never promotes that classification or verdict.
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
if($operator['verdict'] -cne 'FAIL_PROTOCOL_OR_INTEGRITY' -or $operator['gate_status'] -cne 'fail' -or
    $route['live_route_epoch_proof_verdict'] -cne 'fail') {throw 'Canonical tail original failed route verdict changed'}
$observation=Get-Content -LiteralPath (Join-Path $Directory 'late-publication-primary-observation.json') -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
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
