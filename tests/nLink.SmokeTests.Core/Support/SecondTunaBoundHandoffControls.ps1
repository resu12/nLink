# Direct reducer controls over copies of the already evaluated fresh service
# boundaries. No additional authority evaluation and no rewritten input log.
param($Evaluation)
$original = @($Evaluation.Boundaries | Where-Object {
    @($_.operations | Where-Object {
        $null -ne $_.responderProbeHandoff -and
        $_.responderProbeHandoff.transitionMode -ceq 'post_fallback_v6_paired'
    }).Count -gt 0
})
$originalJson = ConvertTo-Json -InputObject $original -Depth 64 -Compress
$script:secondTunaControlCount = 0
function Assert-SecondTunaConsumed([string]$name, [scriptblock]$mutate, [bool]$expected = $false) {
    $boundaries = $originalJson | ConvertFrom-Json
    $binding = @($boundaries | Where-Object decisionKind -CEQ 'BindRuntimeUnlockResponderPeerProbeRouteTransitionHandoff')[0]
    $attempt = @($binding.operations | Where-Object operationRole -CEQ 'peer_acceptance_attempt')[0]
    $installed = @($binding.operations | Where-Object operationRole -CEQ 'handoff_current')[0]
    $rejections = @($boundaries | Where-Object decisionKind -CEQ 'ObserveRuntimeUnlockResponderPeerProbeHandoff')
    $boundaries = @(& $mutate $boundaries $binding $attempt $installed $rejections)
    $history = @(foreach ($boundary in $boundaries) {
        foreach ($operation in $boundary.operations) {
            if ($operation.family -ceq 'responder_probe_handoff' -and $null -ne $operation.responderProbeHandoff) {
                [pscustomobject]@{ Boundary=$boundary; Operation=$operation }
            }
        }
    })
    $index = New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries @(foreach ($boundary in $boundaries) {
        [pscustomobject]@{ Boundary=$boundary; BeforeBoundary=$boundary.beforeBoundary; AfterBoundary=$boundary.afterBoundary
            SessionId=$boundary.sessionId; TransferId=$boundary.transferId; Direction=$boundary.direction }
    })
    $candidates = @($history | Where-Object { $_.Boundary.decisionKind -ceq 'ObserveRuntimeUnlockResponderPeerProbeHandoff' })
    if ($candidates.Count -ne 2) { throw "Control $name lost an original rejection." }
    foreach ($candidate in $candidates) {
        $actual = Test-FileTransferCoordinatorResponderProbeHandoffObservationConsumed -History $history -Candidate $candidate -AggregateIndex $index
        if ($actual -ne $expected) { throw "Bound handoff control ${name}: expected=$expected; actual=$actual" }
    }
    $script:secondTunaControlCount++
}
Assert-SecondTunaConsumed 'original' { param($b) $b } $true
foreach ($kind in @('BindRuntimeUnlockResponderPeerProbeRouteTransitionHandoff','PrepareFileTransferRouteTransition',
        'BeginFileTransferRouteTransitionPhysicalDrain','CommitFileTransferRouteTransition','ApplyFileTransferRouteTransitionDispositionReceipt')) {
    Assert-SecondTunaConsumed "missing_$kind" { param($b) $b | Where-Object decisionKind -CNE $kind }
}
Assert-SecondTunaConsumed 'duplicate_binding' { param($b,$bind) $b; $bind }
Assert-SecondTunaConsumed 'missing_installed_authority' { param($b,$bind) $bind.operations=@($bind.operations | Where-Object operationRole -CNE 'handoff_current'); $b }
Assert-SecondTunaConsumed 'unapplied_installation' { param($b,$bind,$a,$current) $current.stateApplied=$false; $b }
Assert-SecondTunaConsumed 'unvalidated_binding' { param($b,$bind,$a) $a.receiptValidated=$false; $b }
Assert-SecondTunaConsumed 'missing_binding_receipt' { param($b,$bind,$a) $a.responderProbeHandoff.receipts=@(); $b }
Assert-SecondTunaConsumed 'missing_installed_receipt' { param($b,$bind,$a,$current) $current.responderProbeHandoff.receipts=@(); $b }
Assert-SecondTunaConsumed 'wrong_binding_receipt' { param($b,$bind,$a) $a.responderProbeHandoff.receipts[0].receiptIdentity='different'; $b }
Assert-SecondTunaConsumed 'wrong_observation_fingerprint' { param($b,$bind,$a) $a.commandFingerprint='different'; $b }
Assert-SecondTunaConsumed 'nonadjacent_binding' { param($b,$bind) $bind.afterStateGeneration--; $b }
Assert-SecondTunaConsumed 'wrong_binding_hash' { param($b,$bind) $bind.afterStateHash='different'; $b }
foreach ($field in @('coordinatorInstanceId','writer','sessionId','transferId','direction')) {
    Assert-SecondTunaConsumed "wrong_binding_$field" { param($b,$bind) $bind.$field='different'; $b }
}
foreach ($field in @('commandGeneration','receiptGeneration','operationGeneration')) {
    Assert-SecondTunaConsumed "wrong_binding_$field" { param($b,$bind,$a) $a.$field++; $b }
}
foreach ($field in @('ownerKey','pairKey','localProofIdentity','localProofFingerprint','peerProofIdentity',
        'peerProofFingerprint','pairedReleaseAuthorityId','probeId','ackMessageId','ackSourceFingerprint',
        'handoffAuthorityId','probeState','probeTransport','lanes')) {
    Assert-SecondTunaConsumed "wrong_candidate_$field" {
        param($b,$bind,$a,$current,$rejected)
        foreach ($r in $rejected) { $r.operations[0].responderProbeHandoff.$field='different' }; $b
    }
}
foreach ($field in @('pendingPreCommitGeneration','probeAckedUtcMs','handoffGeneration')) {
    Assert-SecondTunaConsumed "wrong_candidate_$field" {
        param($b,$bind,$a,$current,$rejected)
        foreach ($r in $rejected) { $r.operations[0].responderProbeHandoff.$field++ }; $b
    }
}
foreach ($field in @('route','protocolVersion','liveRouteEpoch','routePublicationGeneration','routeCommitGeneration',
        'runtimeGeneration','terminalGeneration','legGeneration','transportEpoch','bridgeRecoveryGeneration',
        'checkpointGeneration','checkpointRequestId','dataSessionIdentity','dataSessionGeneration')) {
    Assert-SecondTunaConsumed "wrong_candidate_source_$field" {
        param($b,$bind,$a,$current,$rejected)
        foreach ($r in $rejected) {
            $scope=$r.operations[0].responderProbeHandoff.sourceBoundary
            if ($scope.$field -is [string]) { $scope.$field='different' } else { $scope.$field++ }
        }; $b
    }
}
Assert-SecondTunaConsumed 'missing_rejected_receipt' {
    param($b,$bind,$a,$current,$rejected)
    foreach ($r in $rejected) { $r.operations[0].responderProbeHandoff.receipts=@() }; $b
}
Assert-SecondTunaConsumed 'rejection_mutates_state' {
    param($b,$bind,$a,$current,$rejected)
    foreach ($r in $rejected) { $r.operations[0].stateApplied=$true }; $b
}
Assert-SecondTunaConsumed 'missing_publication' {
    param($b)
    $commit=@($b | Where-Object decisionKind -CEQ 'CommitFileTransferRouteTransition')[0]
    $commit.operations=@($commit.operations | Where-Object operationKind -CNE 'RouteCommitPublication'); $b
}
Assert-SecondTunaConsumed 'unapplied_disposition' {
    param($b)
    $d=@($b | Where-Object decisionKind -CEQ 'ApplyFileTransferRouteTransitionDispositionReceipt')[0]
    foreach ($o in @($d.operations | Where-Object family -CEQ 'route_transition')) { $o.receiptValidated=$false }; $b
}
if ((ConvertTo-Json -InputObject $original -Depth 64 -Compress) -cne $originalJson) {
    throw 'Reducer controls mutated the primary evaluated boundary objects.'
}
Write-Output "second-tuna-bound-handoff=pass; controls=$script:secondTunaControlCount; additional_authority_evaluations=0"
