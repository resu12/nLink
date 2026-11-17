param([string]$Directory,$Receipt,[string]$AnalysisFailure)
# Completed analysis artifacts do not imply accepted route proof.
if($null -eq $Receipt -or $Receipt.status -cne 'completed' -or -not $Receipt.completedProofArtifacts -or
    $Receipt.coordinatorSnapshotMode -cne 'SnapshotV1Authoritative' -or $AnalysisFailure -or
    -not $Receipt.exitCodeAvailable -or $Receipt.exitCode -ne 0 -or $Receipt.timedOut -or -not $Receipt.analyzerInputUnchanged) {
    throw 'Canonical fallback analysis completion changed'
}
foreach($name in @('full','compact')) {
    $view=Get-Content -LiteralPath (Join-Path $Directory ('filetransfer-coordinator-snapshot-'+$name+'-summary.json')) -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if($view.Mode -cne 'SnapshotV1Authoritative' -or -not $view.StreamComplete -or $view.ArtifactEvidenceVerdict -cne 'pass') {
        throw 'Canonical fallback snapshot evidence changed'
    }
}
$parity=Get-Content -LiteralPath (Join-Path $Directory 'filetransfer-coordinator-snapshot-parity-summary.json') -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
if($parity.Verdict -cne 'pass') {throw 'Canonical fallback snapshot parity changed'}
$operator=Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $Directory 'filetransfer-operator-verdict.txt')
$route=Read-RouteAcceptanceKeyValueArtifact -Path (Join-Path $Directory 'filetransfer-route-consistency-summary.txt')
if($operator['verdict'] -cne 'INCONCLUSIVE' -or $operator['gate_status'] -cne 'fail' -or
    $operator['acceptance_failure_class'] -cne 'incomplete_proof' -or $operator['runtime_correctness_verdict'] -cne 'incomplete_proof' -or
    $operator['hard_failure_count'] -cne '0' -or $route['live_route_epoch_proof_verdict'] -cne 'fail') {
    throw 'Canonical fallback incomplete route proof changed'
}
