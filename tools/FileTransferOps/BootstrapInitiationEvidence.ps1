Set-StrictMode -Version Latest

$script:FileTransferCoordinatorBootstrapInitiationCatalogVersion =
    'snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1-fallback-bootstrap-initiation-v1'
$script:FileTransferCoordinatorBootstrapInitiationCatalogHash = '40a8177eae51664f06bc2bc7aaa4dc2fb62983fc2262c3c2f99d030401efd7db'
$script:FileTransferCoordinatorBootstrapInitiationCatalog = [ordered]@{
    version=1; family='route_transition'; operationKind='FallbackBootstrapSourceHold'
    operationRole='fallback_bootstrap_source_hold'; payloadName='bootstrapInitiation'
    decisionKinds=@('PrepareFallbackBootstrapSourceHold','ApplyFallbackBootstrapSourceHoldCapture',
        'ApplyFallbackBootstrapSourceHoldReceipt','InvalidateFallbackBootstrapSourceHold')
    states=@('Prepared','Applied','Drained','Invalidated')
    transitions=[ordered]@{
        'None:Prepared'='PrepareFallbackBootstrapSourceHold'
        'Prepared:Applied'='ApplyFallbackBootstrapSourceHoldCapture'
        'Applied:Drained'='ApplyFallbackBootstrapSourceHoldReceipt'
        'Prepared:Invalidated'='InvalidateFallbackBootstrapSourceHold'
        'Applied:Invalidated'='InvalidateFallbackBootstrapSourceHold'
        'Drained:Invalidated'='InvalidateFallbackBootstrapSourceHold'
    }
    payloadFields=[ordered]@{
        kind='string'; source='string'; proofKind='string'; observationFingerprint='string'
        localStopFingerprint='string'; peerWakeFingerprint='string'; sourceFingerprint='string'
        sourceBoundary='FileTransferCoordinatorStateBoundarySnapshot'; agreementFingerprint='string'
        holdIdentity='string'; holdGeneration='int64'; holdCommandFingerprint='string'
        captureFingerprint='string'; pauseInstance='int64'; capturedWorkFingerprint='string'
        drainReceiptFingerprint='string'; settledWorkFingerprint='string'; frozenCheckpointFingerprint='string'
        baseCommandIdentity='string'; baseCommandGeneration='int64'; baseCommandFingerprint='string'
    }
    immutableFields=@('kind','source','proofKind','observationFingerprint','localStopFingerprint','peerWakeFingerprint',
        'sourceFingerprint','agreementFingerprint','holdIdentity','holdGeneration','holdCommandFingerprint')
    receiptStage='bootstrap_source_hold_drain'; effectKinds=@()
    history='enabled-agreement-prepared-captured-drained-frozen-base-command-v1'
    retirement='exact-next-generation-new-source-preparation-v1'
}

function Get-FileTransferCoordinatorBootstrapInitiationCatalog {
    $parent = Get-FileTransferCoordinatorBootstrapCorrelationCatalog
    $json = ConvertTo-Json -InputObject $script:FileTransferCoordinatorBootstrapInitiationCatalog -Depth 16 -Compress
    $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes (
        [Text.UTF8Encoding]::new($false).GetBytes("parentHash=$($parent.Hash)`nBootstrapInitiationCatalog=$json"))
    if ($hash -cne $script:FileTransferCoordinatorBootstrapInitiationCatalogHash) {
        throw "coordinator_snapshot_bootstrap_initiation_catalog_hash_invalid:$hash"
    }
    $extended = [ordered]@{}
    foreach ($property in $parent.PSObject.Properties) { $extended[$property.Name] = $property.Value }
    $extended.Version=$script:FileTransferCoordinatorBootstrapInitiationCatalogVersion
    $extended.Hash=$hash; $extended.ParentCatalogHash=$parent.Hash
    $extended.BootstrapInitiationCatalog=$script:FileTransferCoordinatorBootstrapInitiationCatalog
    [pscustomobject]$extended
}

function Test-FileTransferCoordinatorBootstrapCorrelationCatalogCapability {
    param([string]$Version)
    $Version -cin @($script:FileTransferCoordinatorBootstrapCorrelationCatalogVersion,
        $script:FileTransferCoordinatorBootstrapInitiationCatalogVersion, $script:FileTransferCoordinatorBootstrapCallbackCatalogVersion,
        $script:FileTransferCoordinatorRepairOwnerCatalogVersion, $script:FileTransferCoordinatorActivationRetryCatalogVersion, $script:FileTransferCoordinatorReceiverPreAdmissionCatalogVersion)
}

function Test-FileTransferCoordinatorBootstrapInitiationMarker {
    param($Operation)
    if ($null -eq $Operation) { return $false }
    ($Operation.PSObject.Properties.Name -contains 'operationRole' -and $Operation.operationRole -ceq 'fallback_bootstrap_source_hold') -or
        ($Operation.PSObject.Properties.Name -contains 'operationKind' -and $Operation.operationKind -ceq 'FallbackBootstrapSourceHold') -or
        ($Operation.PSObject.Properties.Name -contains 'routeTransition' -and $null -ne $Operation.routeTransition -and
            $Operation.routeTransition.PSObject.Properties.Name -contains 'bootstrapInitiation')
}

function Test-FileTransferCoordinatorBootstrapInitiationOperation {
    param($Envelope, $Operation, $Catalog)
    try {
        $definition=$script:FileTransferCoordinatorBootstrapInitiationCatalog
        if ($Catalog.Version -cnotin @($script:FileTransferCoordinatorBootstrapInitiationCatalogVersion, $script:FileTransferCoordinatorBootstrapCallbackCatalogVersion, $script:FileTransferCoordinatorRepairOwnerCatalogVersion, $script:FileTransferCoordinatorActivationRetryCatalogVersion, $script:FileTransferCoordinatorReceiverPreAdmissionCatalogVersion) -or
            $Operation.family -cne 'route_transition' -or $Operation.operationRole -cnotin @(
                'fallback_bootstrap_source_hold','fallback_bootstrap_exchange','fallback_bootstrap_phase') -or
            $Operation.stateApplied -isnot [bool] -or $Operation.receiptValidated -isnot [bool] -or -not $Operation.stateApplied) { return $false }
        $typed=$Operation.routeTransition; $p=$typed.bootstrapInitiation
        if ($typed.role -cne $Operation.operationRole -or -not (Test-FileTransferBootstrapEvidenceFields $p $definition.payloadFields) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $p.sourceBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $p.sourceBoundary $typed.sourceBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $typed.sourceBoundary $typed.targetBoundary) -or
            $p.sourceBoundary.route -cne 'file_tuna_v4' -or $p.sourceBoundary.protocolVersion -ne 4 -or
            $p.holdGeneration -le 0 -or $p.sourceBoundary.dataSessionGeneration -le 0 -or
            $typed.durableCheckpointFingerprint -cne $p.frozenCheckpointFingerprint) { return $false }
        foreach ($field in $definition.payloadFields.Keys) {
            if ($field -cmatch '(Fingerprint|Identity)$' -and $p.$field -cnotmatch '^(?:[a-f0-9]{64}|fte1-[a-f0-9]{24}|\(none\))$') { return $false }
        }
        foreach ($field in @('observationFingerprint','sourceFingerprint','agreementFingerprint','holdIdentity','holdCommandFingerprint')) {
            if ($p.$field -ceq '(none)') { return $false }
        }
        if ($p.kind -ceq 'LocalRuntimeDisable') {
            if ($p.source -cne 'runtime_disabled' -or $p.proofKind -cne 'local_runtime_disabled' -or $p.localStopFingerprint -ceq '(none)') { return $false }
        } elseif ($p.kind -ceq 'PeerEvidence') {
            $proofKind=if ($Envelope.direction -ceq 'outbound') { 'file_transfer_v6_state_frame_received' } else { 'file_transfer_v6_frontier_request_frame_received' }
            if ($p.localStopFingerprint -cne '(none)' -or $p.peerWakeFingerprint -ceq '(none)' -or
                $p.source -cne 'correlated_bootstrap_peer_frame' -or $p.proofKind -cne $proofKind) { return $false }
        } else { return $false }
        if ($Operation.operationRole -cne 'fallback_bootstrap_source_hold') {
            return $p.baseCommandIdentity -cne '(none)' -and $p.baseCommandGeneration -eq $Operation.operationGeneration -and
                $p.baseCommandFingerprint -cne '(none)' -and $p.frozenCheckpointFingerprint -cne '(none)' -and
                $p.captureFingerprint -ceq '(none)' -and $p.pauseInstance -eq 0 -and $p.capturedWorkFingerprint -ceq '(none)' -and
                $p.drainReceiptFingerprint -ceq '(none)' -and $p.settledWorkFingerprint -ceq '(none)'
        }
        if ($Operation.operationKind -cne $definition.operationKind -or
            $Operation.operationKey -cne "route_transition|fallback_bootstrap_source_hold|$($p.holdIdentity)|$($p.holdGeneration)|FallbackBootstrapSourceHold" -or
            $Operation.authorityId -cne $p.holdIdentity -or $Operation.commandIdentity -cne $p.holdIdentity -or
            $Operation.operationGeneration -ne $p.holdGeneration -or $Operation.commandGeneration -ne $p.holdGeneration -or
            $Operation.commandFingerprint -cne $p.holdCommandFingerprint -or $typed.proofBoundaryFingerprint -cne $p.holdCommandFingerprint -or
            $Operation.beforeState -cnotin (@('None') + $definition.states) -or $Operation.afterState -cnotin (@('None') + $definition.states) -or
            $typed.transitionKind -cne 'TunaV4ToPostTunaFallbackV6' -or $typed.sourceTransport -cne 'Tuna' -or
            $typed.proofBoundaryKind -cne 'fallback-bootstrap-correlation-v1' -or $typed.proofAdmissionMode -cne 'fallback-bootstrap-correlation-v1' -or
            $typed.targetRoute -cne 'post_tuna_fallback_v6' -or $typed.targetProtocolVersion -ne 6 -or
            $typed.targetTransport -cne 'RegularNkn' -or $typed.handoffKind -cne 'TunaToNormalFallback' -or
            $p.baseCommandIdentity -cne '(none)' -or $p.baseCommandGeneration -ne 0 -or $p.baseCommandFingerprint -cne '(none)') { return $false }
        foreach ($name in @('routeRuntime','runtimeUnlockRetry','receiveRecovery','fallbackTailYield',
            'regularV4FrontierRepairPressure','responderProbeHandoff','receiverBarrier','checkpointRepairContinuity')) {
            if ($Operation.PSObject.Properties.Name -contains $name -and $null -ne $Operation.$name) { return $false }
        }
        if ($typed.PSObject.Properties.Name -contains 'bootstrapCorrelation' -or
            $typed.retainedFrameType -cne '(none)' -or $typed.retainedFrameFingerprint -cne '(none)') { return $false }
        $captured=$p.captureFingerprint -cne '(none)'; $drained=$p.drainReceiptFingerprint -cne '(none)'
        if (($captured -and ($p.pauseInstance -le 0 -or $p.capturedWorkFingerprint -ceq '(none)')) -or
            (-not $captured -and ($p.pauseInstance -ne 0 -or $p.capturedWorkFingerprint -cne '(none)')) -or
            ($drained -and (-not $captured -or $p.settledWorkFingerprint -cne $p.capturedWorkFingerprint -or $p.frozenCheckpointFingerprint -ceq '(none)')) -or
            (-not $drained -and ($p.settledWorkFingerprint -cne '(none)' -or $p.frozenCheckpointFingerprint -cne '(none)'))) { return $false }
        if (($Operation.afterState -ceq 'Prepared' -and ($captured -or $drained)) -or
            ($Operation.afterState -ceq 'Applied' -and (-not $captured -or $drained)) -or
            ($Operation.afterState -ceq 'Drained' -and -not $drained)) { return $false }
        $receipts=@($typed.receipts)
        if (-not $drained) {
            return $receipts.Count -eq 0 -and -not $Operation.receiptValidated -and $Operation.receiptIdentity -ceq '(none)' -and
                $Operation.receiptGeneration -eq 0 -and $Operation.receiptStatus -ceq '(none)' -and $Operation.receiptFingerprint -ceq '(none)'
        }
        if ($receipts.Count -ne 1) { return $false }
        $r=$receipts[0]
        if (-not (Test-FileTransferBootstrapEvidenceFields $r $script:FileTransferCoordinatorBootstrapCorrelationCatalog.receiptFields) -or
            $r.stage -cne $definition.receiptStage -or $r.receiptStatus -cne 'Drained' -or $r.receiptReason -cne 'bootstrap_source_hold_drained' -or
            -not $r.receiptValidated -or $r.receiptGeneration -ne $p.holdGeneration -or $r.receiptIdentity -ceq '(none)' -or
            $r.receiptFingerprint -cne $p.drainReceiptFingerprint -or -not $Operation.receiptValidated) { return $false }
        foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity','receiptGeneration','receiptStatus','receiptFingerprint')) {
            if ([string]$Operation.$field -cne [string]$r.$field) { return $false }
        }
        return $true
    } catch { return $false }
}

function Get-FileTransferBootstrapInitiationHistoryErrors {
    param([AllowEmptyCollection()][object[]]$Boundaries)
    $definition=$script:FileTransferCoordinatorBootstrapInitiationCatalog
    foreach ($stream in @($Boundaries | Group-Object coordinatorInstanceId)) {
        $holds=@{}; $exchanges=@{}; $agreement=$null
        foreach ($boundary in @($stream.Group | Sort-Object evidenceLineIndex)) {
            foreach ($op in @($boundary.operations | Where-Object operationRole -CEQ 'fallback_bootstrap_agreement')) { $agreement=$op }
            foreach ($op in @($boundary.operations | Where-Object operationRole -CEQ 'fallback_bootstrap_source_hold')) {
                $p=$op.routeTransition.bootstrapInitiation; $id=[string]$p.holdIdentity
                $prior=if ($holds.ContainsKey($id)) { $holds[$id] } else { $null }
                $before=if ($null -eq $prior) { 'None' } else { $prior.afterState }
                $invalid=$op.beforeState -cne $before -or $null -eq $agreement -or $agreement.afterState -cne 'Enabled' -or
                    $p.agreementFingerprint -cne $agreement.routeTransition.bootstrapCorrelation.agreementFingerprint
                if ($null -ne $prior) {
                    $old=$prior.routeTransition.bootstrapInitiation
                    foreach ($field in $definition.immutableFields) { if ([string]$p.$field -cne [string]$old.$field) { $invalid=$true } }
                    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $p.sourceBoundary $old.sourceBoundary)) { $invalid=$true }
                    foreach ($field in @('captureFingerprint','capturedWorkFingerprint','drainReceiptFingerprint','settledWorkFingerprint','frozenCheckpointFingerprint')) {
                        if ($old.$field -cne '(none)' -and $p.$field -cne $old.$field) { $invalid=$true }
                    }
                    if ($old.pauseInstance -gt 0 -and $p.pauseInstance -ne $old.pauseInstance) { $invalid=$true }
                    if ($op.afterState -ceq $before -and (ConvertTo-Json $p -Depth 16 -Compress) -cne (ConvertTo-Json $old -Depth 16 -Compress)) { $invalid=$true }
                } else {
                    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $p.sourceBoundary $boundary.beforeBoundary)) { $invalid=$true }
                    $generation=if ($holds.Count -eq 0) { 1 } else { 1 + ($holds.Values | Measure-Object operationGeneration -Maximum).Maximum }
                    if ($p.holdGeneration -ne $generation) { $invalid=$true }
                }
                if ($op.afterState -cne $before -and $op.afterState -cne 'None') {
                    $key="$before`:$($op.afterState)"
                    if ($key -cnotin @($definition.transitions.Keys) -or $definition.transitions[$key] -cne $boundary.decisionKind -or
                        $boundary.decisionStatus -cne 'Accepted') { $invalid=$true }
                }
                if ($op.afterState -ceq 'None') {
                    $replacement=@($boundary.operations | Where-Object { $_.operationRole -ceq 'fallback_bootstrap_source_hold' -and
                        $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Prepared' -and $_.operationGeneration -eq ($op.operationGeneration + 1) -and
                        $_.authorityId -cne $op.authorityId -and $_.routeTransition.bootstrapInitiation.sourceFingerprint -cne $p.sourceFingerprint })
                    if ($null -eq $prior -or $before -ceq 'None' -or $replacement.Count -ne 1 -or
                        $boundary.decisionKind -cne 'PrepareFallbackBootstrapSourceHold' -or $boundary.decisionStatus -cne 'Accepted') { $invalid=$true }
                }
                if ($invalid) { "snapshot_bootstrap_initiation_hold_history_invalid:$($boundary.evidenceLineIndex)" }
                $holds[$id]=$op
            }
            foreach ($op in @($boundary.operations | Where-Object { $_.operationRole -cin @('fallback_bootstrap_exchange','fallback_bootstrap_phase') })) {
                if ($op.routeTransition.PSObject.Properties.Name -cnotcontains 'bootstrapInitiation') {
                    "snapshot_bootstrap_initiation_link_missing:$($boundary.evidenceLineIndex)"; continue
                }
                $p=$op.routeTransition.bootstrapInitiation; $id=[string]$p.holdIdentity
                if (-not $holds.ContainsKey($id)) { "snapshot_bootstrap_initiation_hold_missing:$($boundary.evidenceLineIndex)"; continue }
                $hold=$holds[$id]; $h=$hold.routeTransition.bootstrapInitiation
                $invalid=$h.drainReceiptFingerprint -ceq '(none)' -or $p.frozenCheckpointFingerprint -cne $h.frozenCheckpointFingerprint -or
                    -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $p.sourceBoundary $h.sourceBoundary)
                foreach ($field in $definition.immutableFields) { if ([string]$p.$field -cne [string]$h.$field) { $invalid=$true } }
                if ($hold.afterState -cne 'Drained' -and $op.afterState -cnotin @('None','Invalidated') -and
                    ($op.beforeState -cne $op.afterState -or ($boundary.decisionKind -ceq 'ApplyFallbackBootstrapPhaseReceipt' -and
                        $boundary.decisionStatus -ceq 'Accepted'))) { $invalid=$true }
                if ($op.operationRole -ceq 'fallback_bootstrap_exchange') {
                    if ($p.baseCommandIdentity -cne $op.authorityId -or $p.baseCommandGeneration -ne $op.operationGeneration) { $invalid=$true }
                    if ($op.beforeState -ceq 'None' -and $op.afterState -ceq 'Pending') {
                        if ($hold.afterState -cne 'Drained' -or $boundary.decisionKind -cne 'StartFallbackBootstrapExchange' -or
                            $boundary.decisionStatus -cne 'Accepted' -or $exchanges.ContainsKey($id)) { $invalid=$true }
                        $base=@($boundary.operations | Where-Object { $_.operationRole -ceq 'fallback_v6_bootstrap' -and $_.authorityId -ceq $p.baseCommandIdentity })
                        if ($base.Count -ne 1 -or $base[0].commandFingerprint -cne $p.baseCommandFingerprint -or
                            $base[0].commandGeneration -ne $p.baseCommandGeneration -or $base[0].beforeState -cne 'None' -or $base[0].afterState -cne 'Pending' -or
                            $base[0].routeTransition.durableCheckpointFingerprint -cne $p.frozenCheckpointFingerprint -or
                            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $base[0].routeTransition.sourceBoundary $p.sourceBoundary)) { $invalid=$true }
                        $exchanges[$id]=$p
                    }
                }
                if (-not $exchanges.ContainsKey($id)) { $invalid=$true }
                else {
                    foreach ($field in @('baseCommandIdentity','baseCommandGeneration','baseCommandFingerprint','frozenCheckpointFingerprint')) {
                        if ([string]$p.$field -cne [string]$exchanges[$id].$field) { $invalid=$true }
                    }
                }
                if ($invalid) { "snapshot_bootstrap_initiation_exchange_link_invalid:$($boundary.evidenceLineIndex)" }
            }
        }
    }
}
