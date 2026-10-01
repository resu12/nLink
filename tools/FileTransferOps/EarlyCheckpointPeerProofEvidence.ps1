Set-StrictMode -Version Latest

$script:FileTransferCoordinatorEarlyCheckpointProofCatalogVersion =
    'snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1-fallback-bootstrap-initiation-v1-source-callback-v1-repair-owner-v1-activation-retry-v1-receiver-pre-admission-v1-initial-data-session-v1-early-checkpoint-proof-v1'
$script:FileTransferCoordinatorEarlyCheckpointProofCatalogHash = 'a5589d2ced4470105f2643a73921f2f9918e97804043581b9b10ef2c367c5be4'
$script:FileTransferCoordinatorEarlyCheckpointProofCatalog = [ordered]@{
    version=1; family='fallback_tail_yield'; operationKind='PeerProof'
    custodyRole='peer_proof_awaiting_checkpoint'; promotedRole='peer_proof_pending_initial'
    retainDecision='ApplyFallbackTailPeerProofReceipt'; promoteDecision='FallbackCheckpointAccepted'
    binding='exact-original-observation-checkpoint-acceptance-same-generation-contiguous-history-v1'
    routeAuthority=$false; retryAuthority=$false; copiedObservationAllowed=$false
}

function Get-FileTransferCoordinatorEarlyCheckpointProofCatalog {
    $parent = Get-FileTransferCoordinatorInitialDataSessionCatalog
    $json = ConvertTo-Json -InputObject $script:FileTransferCoordinatorEarlyCheckpointProofCatalog -Depth 16 -Compress
    $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([Text.UTF8Encoding]::new($false).GetBytes(
        "parentHash=$($parent.Hash)`nEarlyCheckpointProofCatalog=$json"))
    if ($hash -cne $script:FileTransferCoordinatorEarlyCheckpointProofCatalogHash) { throw "early_checkpoint_proof_catalog_hash_invalid:$hash" }
    $result = [ordered]@{}
    foreach ($property in $parent.PSObject.Properties) { $result[$property.Name] = $property.Value }
    $result.Version = $script:FileTransferCoordinatorEarlyCheckpointProofCatalogVersion
    $result.Hash = $hash; $result.ParentCatalogHash = $parent.Hash
    $result.EarlyCheckpointProofCatalog = $script:FileTransferCoordinatorEarlyCheckpointProofCatalog
    [pscustomobject]$result
}

function Test-FileTransferEarlyCheckpointProofDescriptor {
    param($Operation, [string]$Role)
    try {
        $p = $Operation.fallbackTailYield; $receipts = @($p.receipts)
        if ($Operation.family -cne 'fallback_tail_yield' -or $Operation.operationRole -cne $Role -or
            $Operation.operationKind -cne 'PeerProof' -or $Operation.predecessorAuthorityId -cne '(none)' -or
            $Operation.operationGeneration -le 0 -or
            $Operation.operationKey -cne "fallback_tail_yield|$Role|$($Operation.authorityId)|$($Operation.operationGeneration)|PeerProof" -or
            $p.role -cne $Role -or $p.yieldKind -cne 'PeerProof' -or $p.pairingState -cne 'Pending' -or
            $p.peerProofYieldGeneration -ne $Operation.operationGeneration -or
            $p.peerProofAuthorityId -cnotmatch '^fta2-fallback_tail_yield-[0-9a-f]{24}$' -or
            (ConvertTo-FileTransferCoordinatorEvidenceToken $p.peerProofAuthorityId) -cne $Operation.authorityId -or
            $receipts.Count -ne 1 -or $receipts[0].stage -cne 'peer_proof' -or
            $Operation.receiptStatus -cne 'Observed' -or $Operation.receiptValidated -isnot [bool] -or
            -not $Operation.receiptValidated -or $receipts[0].receiptValidated -isnot [bool] -or
            -not $receipts[0].receiptValidated -or $receipts[0].receiptReason -cne 'fallback_tail_peer_proof_retained') { return $false }
        foreach ($field in @('commandIdentity','commandFingerprint','receiptIdentity','receiptFingerprint')) {
            if ([string]$Operation.$field -in @('', '(none)') -or $Operation.$field -cne $receipts[0].$field) { return $false }
        }
        foreach ($field in @('commandGeneration','receiptGeneration')) {
            if ($Operation.$field -ne $Operation.operationGeneration -or $receipts[0].$field -ne $Operation.$field) { return $false }
        }
        if ($receipts[0].receiptStatus -cne $Operation.receiptStatus -or
            $p.peerProofIdentity -cne $Operation.receiptIdentity -or $p.peerProofFingerprint -in @('', '(none)')) { return $false }
        foreach ($field in @('localProofIdentity','localProofFingerprint','pairedReleaseAuthorityId','pairedReleaseOutcome',
                'reservedRetryAuthorityId','failedPredecessorAuthorityId','invalidationReason')) {
            if ($p.$field -cne '(none)') { return $false }
        }
        foreach ($field in @('pairingGeneration','pairedReleaseGeneration','reservationGeneration','consumingInstallGeneration',
                'reauthorizationGeneration','retryHoldGeneration','retryRecheckGeneration')) {
            if ($p.$field -ne 0) { return $false }
        }
        foreach ($scope in @($p.sourceBoundary,$p.effectiveBoundary,$p.targetBoundary)) {
            if (-not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $scope) -or
                -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $scope $p.sourceBoundary) -or
                $scope.route -cne 'post_tuna_fallback_v6' -or $scope.protocolVersion -ne 6 -or
                $scope.terminalGeneration -ne 0 -or $scope.transportEpoch -le 0 -or
                $scope.checkpointGeneration -le 0 -or $scope.dataSessionGeneration -le 0) { return $false }
        }
        return $p.ownerKey -notin @('', '(none)') -and $p.pairKey -notin @('', '(none)')
    }
    catch { return $false }
}

function Get-FileTransferEarlyCheckpointProofShape {
    param($Envelope)
    try {
        $ops = @($Envelope.aggregate.operations | Where-Object family -ceq 'fallback_tail_yield')
        $held = @($ops | Where-Object operationRole -ceq 'peer_proof_awaiting_checkpoint')
        if ($held.Count -ne 1 -or -not (Test-FileTransferEarlyCheckpointProofDescriptor $held[0] 'peer_proof_awaiting_checkpoint')) { return $null }
        $held = $held[0]; $scope = $held.fallbackTailYield.sourceBoundary
        $before = $Envelope.aggregate.beforeBoundary; $after = $Envelope.aggregate.afterBoundary
        if ($scope.checkpointRequestId -in @('', '(none)') -or $scope.checkpointRequestId -cne $before.checkpointRequestId -or
            $scope.checkpointGeneration -ne $before.checkpointGeneration) { return $null }
        if ($Envelope.decisionStatus -cnotin @('Accepted','Invalidated') -or
            $Envelope.aggregate.afterStateGeneration -ne ($Envelope.aggregate.beforeStateGeneration + 1) -or
            $Envelope.aggregate.afterStateFingerprint -ceq $Envelope.aggregate.beforeStateFingerprint -or
            $held.stateApplied -isnot [bool] -or -not $held.stateApplied) { return $null }
        foreach ($field in @('route','protocolVersion','liveRouteEpoch','routePublicationGeneration','routeCommitGeneration',
                'runtimeGeneration','terminalGeneration','legGeneration','dataSessionIdentity','dataSessionGeneration')) {
            if ([string]$scope.$field -cne [string]$before.$field) { return $null }
        }
        if ($Envelope.decisionKind -ceq 'ApplyFallbackTailPeerProofReceipt' -and
            $Envelope.decisionStatus -ceq 'Accepted' -and
            $Envelope.decisionReason -ceq 'fallback_tail_checkpoint_pending_peer_proof_retained' -and
            $held.beforeState -ceq 'None' -and $held.afterState -ceq 'Pending' -and
            (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $before $after) -and
            @($ops | Where-Object operationRole -cne 'attempted_decision').Count -eq 1) {
            return @{ Kind='Retain'; Held=$held }
        }
        $pending = @($ops | Where-Object operationRole -ceq 'peer_proof_pending_initial')
        if ($held.beforeState -ceq 'Pending' -and $held.afterState -ceq 'None' -and $pending.Count -eq 0 -and
            ($Envelope.decisionKind -cin @('FallbackCheckpointRetired','FallbackCheckpointRequested','FallbackTailQuiescent') -or
             -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $before $after))) {
            return @{ Kind='Discard'; Held=$held }
        }
        if ($Envelope.decisionKind -cne 'FallbackCheckpointAccepted' -or
            -not (Test-FileTransferCoordinatorSnapshotFallbackCheckpointScopeMutation $Envelope) -or
            $held.beforeState -cne 'Pending' -or $held.afterState -cne 'None' -or $ops.Count -ne 2) { return $null }
        $promoted = @($ops | Where-Object operationRole -ceq 'peer_proof_pending_initial')
        if ($promoted.Count -ne 1 -or -not (Test-FileTransferEarlyCheckpointProofDescriptor $promoted[0] 'peer_proof_pending_initial')) { return $null }
        $promoted = $promoted[0]; $target = $promoted.fallbackTailYield.sourceBoundary
        if ($promoted.beforeState -cne 'None' -or $promoted.afterState -cne 'Pending' -or
            $promoted.stateApplied -isnot [bool] -or -not $promoted.stateApplied -or
            $target.checkpointRequestId -cne '(none)' -or $target.checkpointGeneration -ne $scope.checkpointGeneration -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint $scope $target)) { return $null }
        foreach ($field in @('authorityId','operationGeneration','commandIdentity','commandGeneration','commandFingerprint',
                'receiptIdentity','receiptGeneration','receiptStatus')) {
            if ([string]$held.$field -cne [string]$promoted.$field) { return $null }
        }
        foreach ($field in @('peerProofAuthorityId','peerProofYieldGeneration','peerProofIdentity','peerProofFingerprint')) {
            if ([string]$held.fallbackTailYield.$field -cne [string]$promoted.fallbackTailYield.$field) { return $null }
        }
        if ($held.receiptFingerprint -ceq $promoted.receiptFingerprint -or
            $held.fallbackTailYield.ownerKey -ceq $promoted.fallbackTailYield.ownerKey -or
            $held.fallbackTailYield.pairKey -ceq $promoted.fallbackTailYield.pairKey) { return $null }
        return @{ Kind='Promote'; Held=$held; Promoted=$promoted }
    }
    catch { return $null }
}

function Resolve-FileTransferEarlyCheckpointProofHistory {
    param([object[]]$AggregateEntries, [object[]]$Effects, $Origins)
    foreach ($group in @($AggregateEntries | Group-Object { "$($_.Boundary.coordinatorInstanceId)|$($_.Boundary.writer)" })) {
        $held = $null; $promoted = $null; $previous = $null; $seen = @{}
        foreach ($entry in @($group.Group | Sort-Object { [long]$_.Boundary.writerSequence })) {
            $b = $entry.Boundary
            $continuous = $null -eq $previous -or (
                [long]$previous.Boundary.writerSequence -lt [long]$b.writerSequence -and
                $previous.Boundary.afterStateGeneration -eq $b.beforeStateGeneration -and
                $previous.Boundary.afterStateHash -ceq $b.beforeStateHash -and
                (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $previous.AfterBoundary $entry.BeforeBoundary))
            if (-not $continuous) { $held = $null; $promoted = $null }
            $envelope = [pscustomobject]@{ decisionKind=$b.decisionKind; decisionStatus=$b.decisionStatus; decisionReason=$b.decisionReason
                aggregate=[pscustomobject]@{ operations=$b.operations; beforeBoundary=$entry.BeforeBoundary; afterBoundary=$entry.AfterBoundary
                    beforeStateGeneration=$b.beforeStateGeneration; afterStateGeneration=$b.afterStateGeneration
                    beforeStateFingerprint=$b.beforeStateHash; afterStateFingerprint=$b.afterStateHash } }
            $shape = Get-FileTransferEarlyCheckpointProofShape $envelope
            $valid = $true
            if ($null -ne $shape -and $shape.Kind -ceq 'Retain') {
                $key = [string]$shape.Held.operationKey
                $matches = @($Effects | Where-Object { $_.EffectKind -ceq 'peer_proof_observed' -and
                    $_.CoordinatorInstanceId -ceq $b.coordinatorInstanceId -and $_.ReceiptIdentity -ceq $shape.Held.receiptIdentity })
                $valid = $b.writer -notin @('', '(none)') -and [long]$b.writerSequence -gt 0 -and
                    $null -eq $held -and -not $seen.ContainsKey($key) -and $matches.Count -eq 1 -and
                    (Test-FileTransferCoordinatorSnapshotExactEffectBinding $b $shape.Held $matches[0]) -and
                    $matches[0].Writer -ceq $b.writer -and [long]$matches[0].WriterSequence -gt [long]$b.writerSequence
                $seen[$key] = $true
                if ($valid) { $held = @{ Entry=$entry; Operation=$shape.Held; Effect=$matches[0] }; $promoted = $null }
            }
            elseif ($null -ne $shape -and $shape.Kind -ceq 'Promote') {
                $valid = $null -ne $held -and
                    (Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofSignature $shape.Held) -ceq
                    (Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofSignature $held.Operation) -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $held.Entry.AfterBoundary $entry.BeforeBoundary)
                if ($valid) { $promoted = @{ Operation=$shape.Promoted; Origin=$held; Entry=$entry }; $held = $null }
            }
            elseif ($null -ne $shape -and $shape.Kind -ceq 'Discard') {
                $valid = $null -ne $held -and
                    (Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofSignature $shape.Held) -ceq
                    (Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofSignature $held.Operation)
                $held = $null; $promoted = $null
            }
            elseif ($null -ne $promoted -and $b.decisionKind -ceq 'FallbackTailQuiescent') {
                $pending = @($b.operations | Where-Object operationRole -ceq 'peer_proof_pending_initial')
                $yield = @($b.operations | Where-Object operationRole -ceq 'yield_current')
                $valid = $pending.Count -eq 1 -and $yield.Count -eq 1 -and
                    $yield[0].afterState -ceq 'PeerProofObserved' -and
                    (Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofSignature $pending[0]) -ceq
                    (Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofSignature $promoted.Operation) -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $promoted.Entry.AfterBoundary $entry.BeforeBoundary)
                if ($valid) {
                    $yieldEntry = [pscustomobject]@{ Boundary=$b; Operation=$yield[0] }
                    $Origins[(Get-FileTransferCoordinatorSnapshotFallbackTailYieldEntryKey $yieldEntry)] = @{
                        Kind='CheckpointPromotion'; Signature=(Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofSignature $yield[0])
                        Origin=$promoted.Origin; Boundary=$b
                    }
                }
                $promoted = $null
            }
            elseif (-not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $entry.BeforeBoundary $entry.AfterBoundary)) {
                $held = $null; $promoted = $null
            }
            if (-not $valid) {
                $held = $null; $promoted = $null
                "snapshot_operation_catalog_mismatch|early_checkpoint_peer_proof_history_invalid:$($b.evidenceLineIndex)"
            }
            $previous = $entry
        }
    }
}
