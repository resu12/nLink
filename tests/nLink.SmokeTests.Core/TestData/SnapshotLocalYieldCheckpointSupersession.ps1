# The reducer consumes already-validated snapshot records; malformed snapshot
# envelopes/receipt custody remain covered by the snapshot-validator tests.
$source.checkpointGeneration = 1L
$nextScope = Copy-Value $source
$nextScope.checkpointGeneration = 2L
$failedAuthority = 'failed-authority'
$failedTyped = Copy-Value $carriedTyped
$failedTyped.pairingState = 'YieldIssued'
$failedTyped.sourceBoundary = $nextScope
$failedTyped.peerProofAuthorityId = '(none)'
$failedTyped.peerProofIdentity = '(none)'
$failedTyped.peerProofFingerprint = '(none)'
$failedTyped.peerProofYieldGeneration = 0L
$failedTyped | Add-Member failedSupersessionCarriedPeerProof $false
$failedOp = New-YieldOperation 'yield_current' 'failed-key' $failedAuthority 1 'None' 'YieldIssued' $failedTyped
$failedOp.operationKind = 'Failed'
$retirement = New-Boundary 'retired' 50 2 @($retiredOperation,$failedOp)
$retirement | Add-Member decisionKind 'FallbackTailFailed'
$retirement | Add-Member decisionStatus 'Accepted'

$fresh = Copy-Value $pairedBinding
$fresh.SourceBoundary = $nextScope
$fresh.EffectiveBoundary = $nextScope
$fresh.TargetBoundary = $nextScope
$fresh.PeerProofAuthorityId = 'fta2-fallback_tail_yield-bbbbbbbbbbbbbbbbbbbbbbbb'
$fresh.PeerProofIdentity = 'fresh-peer-proof'
$fresh.PeerProofFingerprint = 'fresh-peer-fingerprint'
$fresh.PeerProofYieldGeneration = 2L
$fresh.PeerProofReceipt.receiptIdentity = 'fresh-peer-receipt'
$fresh.PeerProofEffect.WriterSequence = 53L
$fresh.PairedReleaseAuthorityId = $currentAuthority
$fresh.PairedReleaseGeneration = 2L
$successorTyped = Copy-Value $carriedTyped
$successorTyped.sourceBoundary = $nextScope
foreach ($field in @('PeerProofAuthorityId','PeerProofIdentity','PeerProofFingerprint','PeerProofYieldGeneration')) {
    $successorTyped.$field = $fresh.$field
}
$successorTyped | Add-Member failedPredecessorAuthorityId $failedAuthority
$successorTyped | Add-Member failedSupersessionCarriedPeerProof $true
$pairedOperation.fallbackTailYield = Copy-Value $successorTyped
$pairedOperation.fallbackTailYield.pairingState = 'Paired'
$successor = New-Boundary 'successor' 55 3 @(
    (New-YieldOperation 'yield_current' $currentKey $currentAuthority 2 'None' 'PeerProofObserved' $successorTyped),
    (New-YieldOperation 'yield_current' 'failed-key' $failedAuthority 1 'PeerProofObserved' 'None' $failedTyped))
$successor | Add-Member decisionKind 'FallbackTailQuiescent'
$successor | Add-Member decisionStatus 'Accepted'
$consumedTyped = Copy-Value $successorTyped
$consumedTyped.pairingState = 'ReleaseConsumed'
$consumedTyped | Add-Member pairedReleaseGeneration 2L
$consumedTyped | Add-Member pairedReleaseOutcome 'Quiescent'
$consumedTyped.pairedReleaseAuthorityId = $currentAuthority
$consumedTyped.localProofIdentity = $fresh.LocalProofIdentity
$consumedTyped | Add-Member localProofFingerprint $fresh.LocalProofFingerprint
$consumedTyped | Add-Member pairingGeneration 1L
$consumedTyped | Add-Member consumingInstallGeneration 1L
$rawRetry = 'fta2-runtime_unlock_retry_dispatch-cccccccccccccccccccccccc'
$consumedTyped | Add-Member reservedRetryAuthorityId $rawRetry
$consumed = New-YieldOperation 'release_reservation_consumed' 'consumed-key' $currentAuthority 1 'None' 'Consumed' $consumedTyped
$consumed | Add-Member receiptValidated $true
$consumed | Add-Member receiptGeneration 1L
$consumed | Add-Member receiptFingerprint 'consumption-fingerprint'
$installed = [pscustomobject]@{
    family='runtime_unlock_retry_lifecycle'; operationRole='attempt_installed'
    beforeState='None'; afterState='Installed'; stateApplied=$true; receiptValidated=$true
    authorityId=(ConvertTo-FileTransferCoordinatorEvidenceToken $rawRetry)
    operationGeneration=1L; receiptGeneration=1L; receiptFingerprint='consumption-fingerprint'
    runtimeUnlockRetry=[pscustomobject]@{
        releaseKind='FallbackPairedTailYield'; releaseAuthorityId=$currentAuthority
        releaseGeneration=2L; releaseOutcome='Quiescent'
    }
}
$consumption = New-Boundary 'consumption' 80 5 @($consumed,$installed)
$consumption | Add-Member decisionKind 'CommitRuntimeUnlockRetryDispatchInstall'
$consumption | Add-Member decisionStatus 'Accepted'
$checkpointScope = Copy-Value $aggregate
$checkpointScope.checkpointGeneration = 2L
$checkpointScope.checkpointRequestId = 'checkpoint-request'
$requested = New-Boundary 'checkpoint-requested' 45 2 @()
$requested.afterBoundary = $checkpointScope
$requested | Add-Member decisionKind 'FallbackCheckpointRequested'
$requested | Add-Member decisionStatus 'Accepted'
$inFlight = New-Boundary 'checkpoint-send' 46 3 @()
$inFlight.beforeBoundary = $checkpointScope
$inFlight.afterBoundary = $checkpointScope
$inFlight | Add-Member decisionKind 'FallbackControlPlaneSendAuthorized'
$inFlight | Add-Member decisionStatus 'Accepted'
$accepted = New-Boundary 'checkpoint-accepted' 48 4 @()
$accepted.beforeBoundary = $checkpointScope
$accepted | Add-Member decisionKind 'FallbackCheckpointAccepted'
$accepted | Add-Member decisionStatus 'Accepted'
$base = [pscustomobject]@{
    Evaluation=(New-Evaluation @($pendingBoundary,$retirement,$successor,$pairedBoundary,$consumption,$requested,$inFlight,$accepted) @($pendingBinding,$fresh))
    Events=@($receipt) + $blocked
}
$base.Evaluation.AuthoritativeFamilies += 'runtime_unlock_retry_lifecycle'
function Check-Supersession($scenario, [int]$recovered) {
    $before = $scenario | ConvertTo-Json -Depth 32 -Compress
    $proof = Get-FileTransferFallbackTailSnapshotLocalYieldMissingCorrelation -Events $scenario.Events -SnapshotEvaluation $scenario.Evaluation
    Assert-Quartet $proof 4 1 $recovered (1 - $recovered)
    if (($scenario | ConvertTo-Json -Depth 32 -Compress) -cne $before) { throw 'Supersession mutated source evidence' }
}
Check-Supersession $base 1
$resolution = Get-FileTransferFallbackTailSnapshotCheckpointSupersessionRecovery `
    -PendingBinding $pendingBinding `
    -PendingRetirement ([pscustomobject]@{ Boundary=$retirement; Operation=$retiredOperation }) `
    -SnapshotEvaluation $base.Evaluation -Bindings $base.Evaluation.__FallbackTailPeerProofBindings
if ($resolution.Resolution -cne 'checkpoint_stale_pending_proof_safely_superseded' -or
    $resolution.TransitionSequence -ne 80) { throw 'Supersession was not tied to release consumption' }
function Copy-Supersession {
    [pscustomobject]@{
        Evaluation=(Copy-Value $base.Evaluation)
        Events=@($base.Events | ForEach-Object { Copy-Event $_ })
    }
}
$full = Copy-Supersession
$full.Events += $unrelated
[array]::Reverse($full.Events)
Check-Supersession $full 1

foreach ($mutation in @(
    { param($s) $s.Evaluation.__FallbackTailPeerProofBindings = @($s.Evaluation.__FallbackTailPeerProofBindings[0]) },
    { param($s) $s.Evaluation.__FallbackTailPeerProofBindings += Copy-Value $s.Evaluation.__FallbackTailPeerProofBindings[1] },
    { param($s) $s.Evaluation.__FallbackTailPeerProofBindings[1].PeerProofIdentity = 'peer-proof' },
    { param($s) $s.Evaluation.__FallbackTailPeerProofBindings[1].PeerProofAuthorityId = $rawPeerAuthority },
    { param($s) $s.Evaluation.__FallbackTailPeerProofBindings[1].SourceBoundary.dataSessionIdentity = 'foreign' },
    { param($s) $s.Evaluation.__FallbackTailPeerProofBindings[1].Writer = 'foreign' },
    { param($s) $s.Evaluation.__FallbackTailPeerProofBindings[1].LocalProofEffects = @() },
    { param($s) $s.Evaluation.__FallbackTailPeerProofBindings[1].PairedReleaseEffect = $null },
    { param($s) $s.Evaluation.AuthoritativeFamilies = @('fallback_tail_yield') },
    { param($s) $s.Evaluation.Boundaries[1].operations[1].fallbackTailYield.peerProofIdentity = 'peer-proof' },
    { param($s) $s.Evaluation.Boundaries[1].operations[1].fallbackTailYield.sourceBoundary.checkpointGeneration = 3L },
    { param($s) $s.Evaluation.Boundaries[2].operations[0].fallbackTailYield.failedPredecessorAuthorityId = 'foreign' },
    { param($s) $s.Evaluation.Boundaries = @($s.Evaluation.Boundaries | Where-Object boundaryId -CNE 'successor') },
    { param($s) $s.Evaluation.Boundaries = @($s.Evaluation.Boundaries | Where-Object boundaryId -CNE 'consumption') },
    { param($s) $s.Evaluation.Boundaries[4].operations = @($s.Evaluation.Boundaries[4].operations[0]) },
    { param($s) $s.Evaluation.Boundaries[4].operations[1].runtimeUnlockRetry.releaseAuthorityId = 'foreign' },
    { param($s) $s.Evaluation.Boundaries[4].operations[1].receiptFingerprint = 'foreign' },
    { param($s) $s.Evaluation.Boundaries[4].operations[0].fallbackTailYield.localProofIdentity = 'foreign' },
    { param($s) $s.Evaluation.Boundaries[4].writer = 'foreign' },
    { param($s) $s.Evaluation.Boundaries[3].operations[0].fallbackTailYield.peerProofAuthorityId = $rawPeerAuthority },
    { param($s) $s.Evaluation.Boundaries[2].afterBoundary.routePublicationGeneration++ },
    { param($s) $s.Events += New-Event 'filetransfer_data_session_recreated' 70 },
    { param($s) $s.Events += New-Event 'transfer_terminal' 70 },
    { param($s) $s.Evaluation.Boundaries = @($s.Evaluation.Boundaries | Where-Object boundaryId -CNE 'checkpoint-requested') },
    { param($s) $s.Evaluation.Boundaries = @($s.Evaluation.Boundaries | Where-Object boundaryId -CNE 'checkpoint-accepted') },
    { param($s) $s.Evaluation.Boundaries[7].beforeBoundary.checkpointRequestId = 'foreign' },
    { param($s) $s.Evaluation.Boundaries[6].afterBoundary.checkpointGeneration++ },
    { param($s) $s.Evaluation.Boundaries[7].afterBoundary.checkpointGeneration = 2L }
)) {
    $bad = Copy-Supersession
    & $mutation $bad
    try { Check-Supersession $bad 0 } catch { throw "Negative failed: $mutation : $_" }
}
