Set-StrictMode -Version Latest

$script:FileTransferCoordinatorFallbackRepairHandoffCatalogVersion =
    'snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1-fallback-bootstrap-initiation-v1-source-callback-v1-repair-owner-v1-activation-retry-v1-receiver-pre-admission-v1-initial-data-session-v1-early-checkpoint-proof-v1-fallback-repair-handoff-v1'
$script:FileTransferCoordinatorFallbackRepairHandoffCatalogHash = '47e13f6036f632533f75d505eb7502e949129fce0133080e1e319214c3ea6bf0'
$script:FileTransferCoordinatorFallbackRepairHandoffCatalog = [ordered]@{
    version=1; family='fallback_tail_yield'; operationKind='SparseRepairHandoff'
    operationRole='repair_handoff'; attemptKind='RepairHandoffAttempt'; attemptRole='repair_handoff_attempt'
    payloadName='fallbackRepairHandoff'; states=@('Prepared','Captured','Drained','Restored','Adopted')
    payloadFields=@('sourceBoundary','peerProofIdentity','peerProofFingerprint','pauseInstance','capturedWrites','capturedFeedback',
        'settledWrites','settledFeedback','captureCheckpoint','capturedGaps','drainCheckpoint','remainingGaps',
        'restoreCheckpoint','restoredGaps','dispositionKind','transitionAuthorityId','transitionGeneration','dispositionFingerprint','receipts')
    decisions=@('PrepareInboundFallbackRepairHandoff','CaptureInboundFallbackRepairHandoff',
        'DrainInboundFallbackRepairHandoff','RestoreInboundFallbackRepairHandoff','CommitFileTransferRouteTransition')
    binding='exact-peer-source-capture-settled-ids-retained-gap-map-coordinator-disposition-v1'
    effectKinds=@(); routeAuthority=$false; retryAuthority=$false
}

function Get-FileTransferCoordinatorFallbackRepairHandoffCatalog {
    $parent=Get-FileTransferCoordinatorEarlyCheckpointProofCatalog
    $json=ConvertTo-Json -InputObject $script:FileTransferCoordinatorFallbackRepairHandoffCatalog -Depth 16 -Compress
    $hash=Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([Text.UTF8Encoding]::new($false).GetBytes(
        "parentHash=$($parent.Hash)`nFallbackRepairHandoffCatalog=$json"))
    if ($hash -cne $script:FileTransferCoordinatorFallbackRepairHandoffCatalogHash) { throw "fallback_repair_handoff_catalog_hash_invalid:$hash" }
    $result=[ordered]@{}
    foreach ($property in $parent.PSObject.Properties) { $result[$property.Name]=$property.Value }
    $result.Version=$script:FileTransferCoordinatorFallbackRepairHandoffCatalogVersion
    $result.Hash=$hash; $result.ParentCatalogHash=$parent.Hash
    $result.FallbackRepairHandoffCatalog=$script:FileTransferCoordinatorFallbackRepairHandoffCatalog
    [pscustomobject]$result
}

function Test-FileTransferFallbackRepairHandoffMarker {
    param($Operation)
    return $null -ne $Operation -and (
        $Operation.PSObject.Properties.Name -contains 'fallbackRepairHandoff' -or
        (Get-FileTransferCoordinatorSnapshotObjectProperty $Operation 'operationKind' '') -cin @('SparseRepairHandoff','RepairHandoffAttempt') -or
        (Get-FileTransferCoordinatorSnapshotObjectProperty $Operation 'operationRole' '') -cin @('repair_handoff','repair_handoff_attempt'))
}

function Test-FileTransferRepairHandoffIds {
    param($Ids)
    if ($null -eq $Ids) { return $false }
    $previous=0L
    foreach ($id in @($Ids)) {
        if ($id -isnot [long] -and $id -isnot [int]) { return $false }
        if ($id -le $previous) { return $false }; $previous=[long]$id
    }
    return $true
}

function Test-FileTransferRepairHandoffMap {
    param($Checkpoint, $Gaps, $Source)
    try {
        foreach ($name in @('stateEpoch','contiguousCommittedChunkIndex','durableHighestChunkIndex','chunkCount','checkpointGeneration')) {
            if ($Checkpoint.$name -isnot [int] -and $Checkpoint.$name -isnot [long]) { return $false }
        }
        if (@($Checkpoint.PSObject.Properties.Name).Count -ne 6) { return $false }
        if ($null -eq $Checkpoint -or $null -eq $Gaps -or $Checkpoint.stateEpoch -lt 0 -or
            $Checkpoint.chunkCount -le 0 -or $Checkpoint.contiguousCommittedChunkIndex -lt 0 -or
            $Checkpoint.contiguousCommittedChunkIndex -gt $Checkpoint.chunkCount -or
            $Checkpoint.durableHighestChunkIndex -lt ($Checkpoint.contiguousCommittedChunkIndex - 1) -or
            $Checkpoint.durableHighestChunkIndex -ge $Checkpoint.chunkCount -or
            $Checkpoint.checkpointGeneration -ne $Source.checkpointGeneration -or
            $Checkpoint.fingerprint -cnotmatch '^fte1-[0-9a-f]{24}$') { return $false }
        $ranges=@($Gaps)
        if ($ranges.Count -eq 0) { return $Checkpoint.contiguousCommittedChunkIndex -eq $Checkpoint.chunkCount }
        if ($ranges[0].startChunkIndex -ne $Checkpoint.contiguousCommittedChunkIndex) { return $false }
        $end=-1L
        foreach ($range in $ranges) {
            if (@($range.PSObject.Properties.Name).Count -ne 2 -or
                ($range.startChunkIndex -isnot [int] -and $range.startChunkIndex -isnot [long]) -or
                ($range.chunkCount -isnot [int] -and $range.chunkCount -isnot [long])) { return $false }
            if ($range.startChunkIndex -le $end -or $range.chunkCount -le 0 -or
                ([long]$range.startChunkIndex + $range.chunkCount) -gt $Checkpoint.chunkCount) { return $false }
            $end=[long]$range.startChunkIndex + $range.chunkCount
        }
        $highest=if ($end -eq $Checkpoint.chunkCount) { $ranges[-1].startChunkIndex - 1 } else { $Checkpoint.chunkCount - 1 }
        return $Checkpoint.durableHighestChunkIndex -eq $highest
    } catch { return $false }
}

function Test-FileTransferFallbackRepairHandoffOperation {
    param($Envelope, $Operation, $Catalog)
    try {
        if ($Catalog.Version -cne $script:FileTransferCoordinatorFallbackRepairHandoffCatalogVersion -or
            $Envelope.direction -cne 'inbound' -or
            $Operation.family -cne 'fallback_tail_yield' -or $Operation.operationGeneration -le 0 -or
            $Operation.operationKey -cne "fallback_tail_yield|$($Operation.operationRole)|$($Operation.authorityId)|$($Operation.operationGeneration)|$($Operation.operationKind)") { return $false }
        foreach ($name in @('routeRuntime','routeTransition','runtimeUnlockRetry','receiveRecovery','fallbackTailYield',
                'regularV4FrontierRepairPressure','responderProbeHandoff','receiverBarrier','checkpointRepairContinuity','repairOwner','initialDataSessionBinding')) {
            if ($null -ne (Get-FileTransferCoordinatorSnapshotObjectProperty $Operation $name)) { return $false }
        }
        if ($Operation.operationRole -ceq 'repair_handoff_attempt') {
            return $Operation.operationKind -ceq 'RepairHandoffAttempt' -and $Operation.operationGeneration -eq 1 -and
                $Operation.predecessorAuthorityId -ceq '(none)' -and $Operation.beforeState -ceq 'None' -and
                $Operation.afterState -ceq 'Rejected' -and $Operation.commandIdentity -ceq '(none)' -and
                $Operation.commandGeneration -eq 0 -and $Operation.receiptIdentity -ceq '(none)' -and
                $Operation.receiptGeneration -eq 0 -and $Operation.receiptStatus -ceq 'Rejected' -and
                $Operation.receiptFingerprint -ceq '(none)' -and $Operation.receiptValidated -is [bool] -and
                -not $Operation.receiptValidated -and $Operation.stateApplied -is [bool] -and -not $Operation.stateApplied -and
                $Envelope.decisionStatus -ceq 'Rejected' -and
                $Envelope.decisionKind -cin $script:FileTransferCoordinatorFallbackRepairHandoffCatalog.decisions[0..3] -and
                $null -eq (Get-FileTransferCoordinatorSnapshotObjectProperty $Operation 'fallbackRepairHandoff') -and
                $Operation.authorityId -cmatch '^fte1-[0-9a-f]{24}$' -and $Operation.commandFingerprint -cmatch '^fte1-[0-9a-f]{24}$'
        }
        $p=$Operation.fallbackRepairHandoff; $source=$p.sourceBoundary
        if (@($p.PSObject.Properties.Name).Count -ne $script:FileTransferCoordinatorFallbackRepairHandoffCatalog.payloadFields.Count) { return $false }
        foreach ($name in $script:FileTransferCoordinatorFallbackRepairHandoffCatalog.payloadFields) {
            if ($p.PSObject.Properties.Name -cnotcontains $name) { return $false }
        }
        $state=if ($Operation.afterState -ceq 'None') { $Operation.beforeState } else { $Operation.afterState }
        $states=@('Prepared','Captured','Drained','Restored','Adopted'); $stages=@('hold','capture','drain','restore','adopt')
        if ($Operation.operationRole -cne 'repair_handoff' -or $Operation.operationKind -cne 'SparseRepairHandoff' -or
            $state -cnotin $states -or $Operation.beforeState -cnotin (@('None') + $states) -or
            $Operation.authorityId -cnotmatch '^fte1-[0-9a-f]{24}$' -or $Operation.commandIdentity -cne $Operation.authorityId -or
            $Operation.predecessorAuthorityId -cnotmatch '^fte1-[0-9a-f]{24}$' -or
            $Operation.commandGeneration -ne $Operation.operationGeneration -or $Operation.receiptGeneration -ne $Operation.operationGeneration -or
            $Operation.receiptValidated -isnot [bool] -or -not $Operation.receiptValidated -or
            $Operation.stateApplied -isnot [bool] -or $Operation.stateApplied -ne ($Operation.beforeState -cne $Operation.afterState) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $source) -or
            $source.route -cne 'post_tuna_fallback_v6' -or $source.protocolVersion -ne 6 -or $source.terminalGeneration -ne 0 -or
            $source.checkpointGeneration -le 0 -or $source.checkpointRequestId -cne '(none)' -or
            $source.dataSessionGeneration -le 0 -or $source.dataSessionIdentity -cin @('', '(none)') -or
            $p.peerProofIdentity -cnotmatch '^fte1-[0-9a-f]{24}$' -or $p.peerProofFingerprint -cnotmatch '^fte1-[0-9a-f]{24}$') { return $false }
        $receipts=@($p.receipts); $count=if ($state -cin @('Restored','Adopted')) { 4 } else { [array]::IndexOf($states,$state)+1 }
        if ($receipts.Count -ne $count -or $Operation.receiptStatus -cne $state) { return $false }
        for ($i=0; $i -lt $count; $i++) {
            $expected=if ($i -eq 3) { $state } else { $states[$i] }
            $stage=if ($i -eq 3 -and $state -ceq 'Adopted') { 'adopt' } else { $stages[$i] }
            $r=$receipts[$i]
            if ($r.stage -cne $stage -or $r.receiptStatus -cne $expected -or $r.receiptReason -cne "fallback_repair_handoff_$stage" -or
                $r.receiptValidated -isnot [bool] -or -not $r.receiptValidated -or
                $r.commandIdentity -cne $Operation.commandIdentity -or $r.commandGeneration -ne $Operation.commandGeneration -or
                $r.commandFingerprint -cne $Operation.commandFingerprint -or $r.receiptGeneration -ne $Operation.operationGeneration -or
                $r.receiptIdentity -cnotmatch '^fte1-[0-9a-f]{24}$' -or $r.receiptFingerprint -cnotmatch '^fte1-[0-9a-f]{24}$') { return $false }
        }
        foreach ($name in @('receiptIdentity','receiptFingerprint','receiptStatus','receiptGeneration','receiptValidated')) {
            if ([string]$Operation.$name -cne [string]$receipts[-1].$name) { return $false }
        }
        if ($receipts[0].receiptFingerprint -cne $Operation.commandFingerprint) { return $false }
        foreach ($ids in @('capturedWrites','capturedFeedback','settledWrites','settledFeedback')) {
            if (-not (Test-FileTransferRepairHandoffIds $p.$ids)) { return $false }
        }
        if ($count -eq 1) {
            if ($p.pauseInstance -ne 0 -or $null -ne $p.captureCheckpoint -or @($p.capturedGaps).Count -ne 0 -or
                @($p.capturedWrites).Count -ne 0 -or @($p.capturedFeedback).Count -ne 0) { return $false }
        } elseif ($p.pauseInstance -le 0 -or @($p.capturedGaps).Count -eq 0 -or
            -not (Test-FileTransferRepairHandoffMap $p.captureCheckpoint $p.capturedGaps $source)) { return $false }
        if ($count -lt 3) {
            if ($null -ne $p.drainCheckpoint -or @($p.remainingGaps).Count -ne 0 -or
                @($p.settledWrites).Count -ne 0 -or @($p.settledFeedback).Count -ne 0) { return $false }
        } else {
            if (-not (Test-FileTransferRepairHandoffMap $p.drainCheckpoint $p.remainingGaps $source) -or
                (ConvertTo-Json @($p.capturedWrites) -Compress) -cne (ConvertTo-Json @($p.settledWrites) -Compress) -or
                (ConvertTo-Json @($p.capturedFeedback) -Compress) -cne (ConvertTo-Json @($p.settledFeedback) -Compress) -or
                $p.drainCheckpoint.chunkCount -ne $p.captureCheckpoint.chunkCount -or
                $p.drainCheckpoint.stateEpoch -lt $p.captureCheckpoint.stateEpoch -or
                $p.drainCheckpoint.contiguousCommittedChunkIndex -lt $p.captureCheckpoint.contiguousCommittedChunkIndex -or
                $p.drainCheckpoint.durableHighestChunkIndex -lt $p.captureCheckpoint.durableHighestChunkIndex) { return $false }
            foreach ($gap in @($p.remainingGaps)) {
                if (@($p.capturedGaps | Where-Object {
                    $_.startChunkIndex -le $gap.startChunkIndex -and
                    ([long]$_.startChunkIndex + $_.chunkCount) -ge ([long]$gap.startChunkIndex + $gap.chunkCount)
                }).Count -ne 1) { return $false }
            }
            if (@($p.capturedWrites).Count -eq 0 -and (
                $p.drainCheckpoint.contiguousCommittedChunkIndex -ne $p.captureCheckpoint.contiguousCommittedChunkIndex -or
                $p.drainCheckpoint.durableHighestChunkIndex -ne $p.captureCheckpoint.durableHighestChunkIndex -or
                (ConvertTo-Json @($p.remainingGaps) -Compress) -cne (ConvertTo-Json @($p.capturedGaps) -Compress))) { return $false }
        }
        if ($state -ceq 'Restored') {
            if ((ConvertTo-Json $p.restoreCheckpoint -Compress) -cne (ConvertTo-Json $p.drainCheckpoint -Compress) -or
                (ConvertTo-Json @($p.restoredGaps) -Compress) -cne (ConvertTo-Json @($p.remainingGaps) -Compress)) { return $false }
        } elseif ($null -ne $p.restoreCheckpoint -or @($p.restoredGaps).Count -ne 0) { return $false }
        if ($p.dispositionKind -ceq '(none)') {
            if ($state -ceq 'Adopted' -or $p.transitionAuthorityId -cne '(none)' -or $p.transitionGeneration -ne 0 -or
                $p.dispositionFingerprint -cne '(none)') { return $false }
        } elseif ($p.dispositionKind -cne $(if ($state -ceq 'Adopted') { 'Committed' } elseif ($state -ceq 'Restored') { 'RolledBack' } else { '(invalid)' }) -or
            $p.transitionAuthorityId -cnotmatch '^fte1-[0-9a-f]{24}$' -or $p.transitionGeneration -le 0 -or
            $p.dispositionFingerprint -cnotmatch '^fte1-[0-9a-f]{24}$') { return $false }
        if ($Operation.beforeState -cne $Operation.afterState -and $Operation.afterState -cne 'None') {
            $expectedBefore=@{Prepared='None';Captured='Prepared';Drained='Captured';Restored='Drained';Adopted='Drained'}[$state]
            $decision=@{Prepared='PrepareInboundFallbackRepairHandoff';Captured='CaptureInboundFallbackRepairHandoff';
                Drained='DrainInboundFallbackRepairHandoff';Restored='RestoreInboundFallbackRepairHandoff';Adopted='CommitFileTransferRouteTransition'}[$state]
            if ($Operation.beforeState -cne $expectedBefore -or $Envelope.decisionKind -cne $decision -or
                $Envelope.decisionStatus -cne $(if ($state -ceq 'Adopted') { 'Committed' } else { 'Accepted' }) -or
                -not (Test-FileTransferRepairHandoffOwnerBoundary $source $Envelope.aggregate.beforeBoundary)) { return $false }
            if ($state -ceq 'Adopted') {
                if ($Envelope.aggregate.afterBoundary.route -cne 'file_tuna_v4' -or $Envelope.aggregate.afterBoundary.protocolVersion -ne 4 -or
                    @($p.remainingGaps).Count -eq 0) { return $false }
            } elseif (-not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $Envelope.aggregate.beforeBoundary $Envelope.aggregate.afterBoundary)) { return $false }
        }
        return $true
    } catch { return $false }
}

function Test-FileTransferRepairHandoffOwnerBoundary {
    param($Source, $Boundary)
    # Aggregate ownership omits a retired checkpoint lease. The complete leg/checkpoint
    # tuple is bound separately to the accepted peer proof, never inferred from zeros.
    foreach ($name in @('route','protocolVersion','liveRouteEpoch','routePublicationGeneration','routeCommitGeneration',
            'runtimeGeneration','terminalGeneration','legGeneration','dataSessionIdentity','dataSessionGeneration')) {
        if ([string]$Source.$name -cne [string]$Boundary.$name) { return $false }
    }
    return $true
}

function Get-FileTransferFallbackRepairHandoffHistoryErrors {
    param([AllowEmptyCollection()][object[]]$AggregateEntries)
    $errors=[System.Collections.Generic.List[string]]::new(); $holds=@{}; $transitions=@{}; $peers=@{}; $openTransitions=@{}
    foreach ($entry in $AggregateEntries) {
        $b=$entry.Boundary; $stream="$($b.coordinatorInstanceId)|$($entry.SessionId)|$($entry.TransferId)|$($entry.Direction)"
        foreach ($t in @($b.operations | Where-Object { $_.family -ceq 'route_transition' -and $_.afterState -cne 'None' })) {
            if (@($t.routeTransition.receipts | Where-Object { $_.stage -ceq 'disposition' -and $_.receiptValidated -and
                    $_.receiptStatus -cin @('Applied','Idempotent') }).Count -eq 1) { $transitions["$stream|$($t.authorityId)"]=$t }
        }
    }
    foreach ($entry in @($AggregateEntries | Sort-Object { [long]$_.Boundary.afterStateGeneration })) {
        $b=$entry.Boundary; $stream="$($b.coordinatorInstanceId)|$($entry.SessionId)|$($entry.TransferId)|$($entry.Direction)"
        foreach ($t in @($b.operations | Where-Object { $_.family -ceq 'route_transition' -and $_.operationRole -cin @('current','terminal') })) {
            $transitionKey="$stream|$($t.authorityId)"
            if ($t.afterState -cin @('Prepared','Draining')) { $openTransitions[$transitionKey]=$t }
            elseif ($t.afterState -ceq 'Committed' -or @($t.routeTransition.receipts | Where-Object {
                    $_.stage -ceq 'disposition' -and $_.receiptStatus -cin @('Applied','Idempotent') -and $_.receiptValidated }).Count -eq 1) {
                $openTransitions.Remove($transitionKey)
            }
        }
        foreach ($peer in @($b.operations | Where-Object { $_.family -ceq 'fallback_tail_yield' -and
                $_.operationKind -ceq 'PeerProof' -and $_.afterState -ceq 'Pending' -and $_.receiptValidated })) {
            $peers["$stream|$($peer.authorityId)"]=$peer
        }
        foreach ($o in @($b.operations | Where-Object operationRole -ceq 'repair_handoff')) {
            $key="$stream|$($o.operationKey)"; $p=$o.fallbackRepairHandoff; $prior=$holds[$key]
            if ($o.beforeState -ceq 'None' -and $o.afterState -ceq 'Prepared') {
                $peer=$peers["$stream|$($o.predecessorAuthorityId)"]
                if ($null -eq $peer -or $peer.receiptIdentity -cne $p.peerProofIdentity -or
                    $peer.receiptFingerprint -cne $p.peerProofFingerprint -or
                    -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $peer.fallbackTailYield.sourceBoundary $p.sourceBoundary)) {
                    $errors.Add("snapshot_fallback_repair_handoff_peer_source_unproven:$($b.evidenceLineIndex)")
                }
            }
            if ($o.beforeState -cne 'None' -and $null -eq $prior) { $errors.Add("snapshot_fallback_repair_handoff_source_history_missing:$($b.evidenceLineIndex)") }
            if ($null -ne $prior) {
                foreach ($field in @('sourceBoundary','peerProofIdentity','peerProofFingerprint')) {
                    if ((ConvertTo-Json $p.$field -Depth 16 -Compress) -cne (ConvertTo-Json $prior.fallbackRepairHandoff.$field -Depth 16 -Compress)) {
                        $errors.Add("snapshot_fallback_repair_handoff_source_changed:$($b.evidenceLineIndex)")
                    }
                }
                if ($prior.afterState -cin @('Captured','Drained','Restored','Adopted')) {
                    foreach ($field in @('pauseInstance','capturedWrites','capturedFeedback','captureCheckpoint','capturedGaps')) {
                        if ((ConvertTo-Json $p.$field -Depth 16 -Compress) -cne (ConvertTo-Json $prior.fallbackRepairHandoff.$field -Depth 16 -Compress)) {
                            $errors.Add("snapshot_fallback_repair_handoff_capture_changed:$($b.evidenceLineIndex)")
                        }
                    }
                }
                if ($prior.afterState -cin @('Drained','Restored','Adopted')) {
                    foreach ($field in @('settledWrites','settledFeedback','drainCheckpoint','remainingGaps')) {
                        if ((ConvertTo-Json $p.$field -Depth 16 -Compress) -cne (ConvertTo-Json $prior.fallbackRepairHandoff.$field -Depth 16 -Compress)) {
                            $errors.Add("snapshot_fallback_repair_handoff_drain_changed:$($b.evidenceLineIndex)")
                        }
                    }
                }
                if ($prior.afterState -cin @('Restored','Adopted')) {
                    foreach ($field in @('restoreCheckpoint','restoredGaps','dispositionKind','transitionAuthorityId','transitionGeneration','dispositionFingerprint')) {
                        if ((ConvertTo-Json $p.$field -Depth 16 -Compress) -cne (ConvertTo-Json $prior.fallbackRepairHandoff.$field -Depth 16 -Compress)) {
                            $errors.Add("snapshot_fallback_repair_handoff_terminal_changed:$($b.evidenceLineIndex)")
                        }
                    }
                }
                if ($o.beforeState -cne $prior.afterState -or $o.commandFingerprint -cne $prior.commandFingerprint) {
                    $errors.Add("snapshot_fallback_repair_handoff_state_history_invalid:$($b.evidenceLineIndex)")
                }
                for ($i=0; $i -lt @($prior.fallbackRepairHandoff.receipts).Count; $i++) {
                    if ((ConvertTo-Json $p.receipts[$i] -Compress) -cne (ConvertTo-Json $prior.fallbackRepairHandoff.receipts[$i] -Compress)) {
                        $errors.Add("snapshot_fallback_repair_handoff_receipt_changed:$($b.evidenceLineIndex)")
                    }
                }
            }
            if ($o.beforeState -cne $o.afterState -and $o.afterState -cin @('Restored','Adopted') -and $p.dispositionKind -cne '(none)') {
                $t=$transitions["$stream|$($p.transitionAuthorityId)"]
                $expectedState=if ($o.afterState -ceq 'Adopted') { 'Committed' } else { 'Invalidated' }
                if ($null -eq $t -or $t.operationGeneration -ne $p.transitionGeneration -or $t.afterState -cne $expectedState -or
                    $t.routeTransition.transitionKind -cne 'PostTunaFallbackV6ToTunaV4' -or
                    $t.routeTransition.durableCheckpointFingerprint -cne $p.drainCheckpoint.fingerprint -or
                    $t.routeTransition.dispositionKind -cne $p.dispositionKind -or
                    @($t.routeTransition.receipts | Where-Object {
                        $_.stage -ceq 'disposition' -and $_.commandFingerprint -ceq $p.dispositionFingerprint -and
                        $_.receiptStatus -cin @('Applied','Idempotent') -and $_.receiptValidated
                    }).Count -ne 1) {
                    $errors.Add("snapshot_fallback_repair_handoff_disposition_unproven:$($b.evidenceLineIndex)")
                }
            }
            if ($o.beforeState -cne $o.afterState -and $o.afterState -ceq 'Restored' -and $p.dispositionKind -ceq '(none)' -and
                @($openTransitions.Keys | Where-Object { $_.StartsWith($stream + '|', [StringComparison]::Ordinal) }).Count -gt 0) {
                $errors.Add("snapshot_fallback_repair_handoff_restore_negotiation_open:$($b.evidenceLineIndex)")
            }
            $holds[$key]=$o
        }
    }
    return @($errors)
}
