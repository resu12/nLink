# Mutations of copies of fresh, already evaluated boundaries; no reevaluation.
param($Evaluation)
$original = ConvertTo-Json -InputObject $Evaluation.Boundaries -Depth 64 -Compress
$script:staleControlCount = 0
function Assert-StaleConsumed([string]$name, [scriptblock]$mutate, [bool]$expected = $false) {
    $b = $original | ConvertFrom-Json
    $r = @($b | Where-Object decisionReason -CEQ 'data_session_availability_stale')[0]
    $o = $r.operations[0]
    $a = @($b | Where-Object {
        $_.decisionKind -ceq 'ObserveRuntimeUnlockResponderPeerProbeHandoff' -and $_.decisionStatus -ceq 'Idempotent'
    })[0]
    $b = @(& $mutate $b $r $o $a)
    $history = @(foreach ($boundary in $b) {
        foreach ($operation in $boundary.operations) {
            if ($operation.family -ceq 'responder_probe_handoff' -and $null -ne $operation.responderProbeHandoff) {
                [pscustomobject]@{Boundary=$boundary;Operation=$operation}
            }
        }
    })
    $index = New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries @(foreach ($boundary in $b) {
        [pscustomobject]@{Boundary=$boundary;BeforeBoundary=$boundary.beforeBoundary;AfterBoundary=$boundary.afterBoundary
            SessionId=$boundary.sessionId;TransferId=$boundary.transferId;Direction=$boundary.direction}
    })
    $actual = Test-FileTransferCoordinatorResponderProbeHandoffObservationConsumed -History $history `
        -Candidate ([pscustomobject]@{Boundary=$r;Operation=$o}) -AggregateIndex $index
    if ($actual -ne $expected) { throw "Stale responder control ${name}: expected=$expected; actual=$actual" }
    $script:staleControlCount++
}
Assert-StaleConsumed 'exact_completed' {param($b) $b} $true
Assert-StaleConsumed 'commit_with_applied_pressure_settlement' {
    param($b)
    $c=@($b | Where-Object decisionKind -CEQ 'CommitFileTransferRouteTransition')[0]
    $terminal=@($c.operations | Where-Object { $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'terminal' })[0]
    # The descriptor projects its latest typed receipt, not necessarily commit.
    $settlement=$terminal.routeTransition.receipts[-1] | Select-Object *
    $settlement.stage='pressure_settlement'
    $settlement.receiptStatus='Applied'
    $terminal.routeTransition.receipts+=@($settlement)
    $terminal.receiptStatus='Applied'
    $b
} $true
Assert-StaleConsumed 'missing_commit_receipt' {
    param($b)
    $c=@($b | Where-Object decisionKind -CEQ 'CommitFileTransferRouteTransition')[0]
    $terminal=@($c.operations | Where-Object { $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'terminal' })[0]
    $terminal.routeTransition.receipts=@($terminal.routeTransition.receipts | Where-Object stage -CNE 'commit')
    $b
}
Assert-StaleConsumed 'duplicate_commit_receipt' {
    param($b)
    $c=@($b | Where-Object decisionKind -CEQ 'CommitFileTransferRouteTransition')[0]
    $terminal=@($c.operations | Where-Object { $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'terminal' })[0]
    $terminal.routeTransition.receipts+=@($terminal.routeTransition.receipts | Where-Object stage -CEQ 'commit')
    $b
}
foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity','receiptGeneration',
        'receiptStatus','receiptFingerprint','receiptValidated','stage')) {
    Assert-StaleConsumed "wrong_commit_receipt_$field" {
        param($b)
        $c=@($b | Where-Object decisionKind -CEQ 'CommitFileTransferRouteTransition')[0]
        $terminal=@($c.operations | Where-Object { $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'terminal' })[0]
        $receipt=@($terminal.routeTransition.receipts | Where-Object stage -CEQ 'commit')[0]
        if ($receipt.$field -is [bool]) { $receipt.$field=$false }
        elseif ($field -in @('commandFingerprint','receiptFingerprint')) { $receipt.$field='(none)' }
        elseif ($receipt.$field -is [string]) { $receipt.$field='different' }
        else { $receipt.$field++ }
        $b
    }
}
foreach ($kind in @('ObserveRuntimeUnlockResponderPeerProbeHandoff','PrepareFileTransferRouteTransition',
        'BeginFileTransferRouteTransitionPhysicalDrain','CommitFileTransferRouteTransition','ApplyFileTransferRouteTransitionDispositionReceipt')) {
    Assert-StaleConsumed "missing_$kind" {param($b,$r) $b | Where-Object { $_.decisionKind -cne $kind -or $_.boundaryId -ceq $r.boundaryId }}
    Assert-StaleConsumed "duplicate_$kind" {
        param($b,$r) $b; $b | Where-Object { $_.decisionKind -ceq $kind -and $_.boundaryId -cne $r.boundaryId }
    }
}
foreach ($field in @('coordinatorInstanceId','writer','sessionId','transferId','direction')) {
    Assert-StaleConsumed "wrong_acceptance_$field" {param($b,$r,$o,$a) $a.$field='different'; $b}
    Assert-StaleConsumed "wrong_candidate_$field" {param($b,$r) $r.$field='different'; $b}
}
foreach ($field in @('authorityId','operationGeneration','commandFingerprint','commandGeneration','receiptGeneration','commandIdentity','receiptIdentity')) {
    Assert-StaleConsumed "wrong_acceptance_$field" {
        param($b,$r,$o,$a)
        $op=@($a.operations | Where-Object operationRole -CEQ 'peer_acceptance_attempt')[0]
        if ($op.$field -is [string]) { $op.$field='different' } else { $op.$field++ }; $b
    }
}
foreach ($field in @('probeId','ackMessageId','ackSourceFingerprint','peerProbeFrameFingerprint','probeState','probeTransport','lanes',
        'tunaPathLeaseListenerRunId','transitionMode','pendingAnswerAckGeneration','probeAckedUtcMs','admissionGeneration',
        'transactionGeneration','offerGeneration','launchGeneration','tunaPathLeaseGeneration','payerDecisionId','pendingPreCommitGeneration')) {
    Assert-StaleConsumed "wrong_observation_$field" {
        param($b,$r,$o)
        if ($o.responderProbeHandoff.$field -is [string]) { $o.responderProbeHandoff.$field='different' }
        else { $o.responderProbeHandoff.$field++ }; $b
    }
}
foreach ($scopeName in @('sourceBoundary','effectiveBoundary','targetBoundary')) {
    foreach ($field in @('route','protocolVersion','runtimeGeneration','routePublicationGeneration','routeCommitGeneration',
            'legGeneration','liveRouteEpoch','terminalGeneration','transportEpoch','bridgeRecoveryGeneration',
            'checkpointGeneration','checkpointRequestId','dataSessionIdentity','dataSessionGeneration')) {
        Assert-StaleConsumed "wrong_${scopeName}_$field" {
            param($b,$r,$o)
            $scope=$o.responderProbeHandoff.$scopeName
            if ($scope.$field -is [string]) { $scope.$field='different' } else { $scope.$field++ }; $b
        }
    }
}
foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity','receiptGeneration',
        'receiptStatus','receiptFingerprint','receiptValidated','receiptReason','stage')) {
    Assert-StaleConsumed "wrong_typed_receipt_$field" {
        param($b,$r,$o)
        $receipt=$o.responderProbeHandoff.receipts[0]
        if ($receipt.$field -is [bool]) { $receipt.$field=$false }
        elseif ($receipt.$field -is [string]) { $receipt.$field='different' }
        else { $receipt.$field++ }; $b
    }
}
Assert-StaleConsumed 'missing_receipt' {param($b,$r,$o) $o.responderProbeHandoff.receipts=@(); $b}
Assert-StaleConsumed 'duplicate_receipt' {param($b,$r,$o) $o.responderProbeHandoff.receipts=@($o.responderProbeHandoff.receipts)*2; $b}
Assert-StaleConsumed 'unknown_reason' {param($b,$r) $r.decisionReason='unknown_stale'; $b}
Assert-StaleConsumed 'payload_reason' {param($b,$r,$o) $o.responderProbeHandoff.decisionReason='accepted'; $b}
Assert-StaleConsumed 'payload_status' {param($b,$r,$o) $o.responderProbeHandoff.decisionStatus='Accepted'; $b}
Assert-StaleConsumed 'post_fallback_reason_not_added' {param($b,$r,$o) $o.responderProbeHandoff.transitionMode='post_fallback_v6_paired'; $b}
Assert-StaleConsumed 'applied_attempt' {param($b,$r,$o) $o.stateApplied=$true; $b}
Assert-StaleConsumed 'state_generation_mutation' {param($b,$r) $r.afterStateGeneration++; $b}
Assert-StaleConsumed 'state_hash_mutation' {param($b,$r) $r.afterStateHash='different'; $b}
Assert-StaleConsumed 'post_disposition_hash_gap' {param($b,$r) $r.beforeStateHash='gap'; $r.afterStateHash='gap'; $b}
Assert-StaleConsumed 'before_disposition' {param($b,$r) $r.beforeStateGeneration-=2; $r.afterStateGeneration-=2; $b}
Assert-StaleConsumed 'missing_publication' {
    param($b)
    $c=@($b | Where-Object decisionKind -CEQ 'CommitFileTransferRouteTransition')[0]
    $c.operations=@($c.operations | Where-Object operationKind -CNE 'RouteCommitPublication'); $b
}
Assert-StaleConsumed 'duplicate_publication' {
    param($b)
    $c=@($b | Where-Object decisionKind -CEQ 'CommitFileTransferRouteTransition')[0]
    $c.operations+=@($c.operations | Where-Object operationKind -CEQ 'RouteCommitPublication'); $b
}
Assert-StaleConsumed 'unapplied_disposition' {
    param($b)
    $d=@($b | Where-Object decisionKind -CEQ 'ApplyFileTransferRouteTransitionDispositionReceipt')[0]
    foreach ($o in @($d.operations | Where-Object family -CEQ 'route_transition')) { $o.receiptValidated=$false }; $b
}
if ((ConvertTo-Json -InputObject $Evaluation.Boundaries -Depth 64 -Compress) -cne $original) { throw 'Controls changed original evaluation.' }
Write-Output "initial-stale-controls=pass; controls=$script:staleControlCount; additional_authority_evaluations=0"
