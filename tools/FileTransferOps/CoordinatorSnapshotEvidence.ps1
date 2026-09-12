Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'CheckpointRepairContinuityEvidence.ps1')
. (Join-Path $PSScriptRoot 'BootstrapCorrelationEvidence.ps1')
. (Join-Path $PSScriptRoot 'BootstrapInitiationEvidence.ps1')
. (Join-Path $PSScriptRoot 'BootstrapCallbackEvidence.ps1')

$script:FileTransferCoordinatorSnapshotContractVersion = 1
$script:FileTransferCoordinatorSnapshotCatalogVersion = 'snapshot-authority-v1'
$script:FileTransferCoordinatorSnapshotCatalogHash = '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2'
$script:FileTransferLegacyV0CatalogVersion = 'legacy-v0-pinned-20260825'
$script:FileTransferLegacyV0CatalogHash = 'a2ad4a9853c5a9551c98e59c11c5d71e1a35b0b5b0ff3229da1f4d6f295083ac'
$script:FileTransferCoordinatorEvidenceCatalogCache = $null
$script:FileTransferCoordinatorReceiverBarrierCatalogVersion = 'snapshot-authority-v1-receiver-barrier-v1'
$script:FileTransferCoordinatorReceiverBarrierCatalogHash = 'e507b186e037924e5785e563742fedd9111552684fc8cc2f46eef38225b0bb94'
$script:FileTransferCoordinatorReceiverBarrierCatalog = [ordered]@{
    version = 1
    family = 'responder_probe_handoff'
    operationKind = 'InitialActivationReceiverBarrier'
    operationRoles = @('receiver_barrier_current', 'receiver_barrier_terminal', 'receiver_barrier_attempt')
    decisionKinds = @(
        'BindInitialActivationReceiverBarrier',
        'AttachInitialActivationReceiverBarrierAdmission',
        'PrepareInitialActivationReceiverBarrierClose',
        'CommitInitialActivationReceiverBarrierClose',
        'InvalidateInitialActivationReceiverBarrier')
    states = @('Bound', 'AdmissionAttached', 'ClosePrepared', 'Closed', 'Invalidated')
    receiptStages = @('barrier_bind', 'barrier_admission', 'barrier_close')
    effectKinds = @('receiver_barrier_close_applied')
    payloadName = 'receiverBarrier'
    payloadFields = [ordered]@{
        role = 'string'
        sourceBoundary = 'FileTransferCoordinatorStateBoundarySnapshot'
        effectiveBoundary = 'FileTransferCoordinatorStateBoundarySnapshot'
        pauseGeneration = 'int64'
        negotiationFingerprint = 'string'
        admissionFingerprint = 'string'
        failureOriginFingerprint = 'string'
        invalidatingAdmissionFingerprint = 'string'
        replacementPauseGeneration = 'int64'
        receipts = 'FileTransferCoordinatorReceiptTransitionSnapshot[]'
    }
}
$script:FileTransferCoordinatorSnapshotRequiredFamilies = @(
    'route_runtime',
    'route_transition',
    'runtime_unlock_retry_lifecycle',
    'receive_recovery',
    'fallback_tail_yield',
    'regular_v4_frontier_repair_pressure',
    'responder_probe_handoff'
)
$script:FileTransferCoordinatorSnapshotFamily1 = @(
    'route_runtime',
    'route_transition'
)
$script:FileTransferCoordinatorSnapshotFamily2 = @(
    'runtime_unlock_retry_lifecycle'
)
$script:FileTransferCoordinatorSnapshotFamily3 = @(
    'receive_recovery'
)
$script:FileTransferCoordinatorSnapshotFamily4 = @(
    'fallback_tail_yield'
)
$script:FileTransferCoordinatorSnapshotFamily5 = @(
    'regular_v4_frontier_repair_pressure'
)
$script:FileTransferCoordinatorSnapshotFamily6 = @(
    'responder_probe_handoff'
)
$script:FileTransferCoordinatorSnapshotFamily6DecisionKinds = @(
    'ObserveRuntimeUnlockResponderPeerProbeHandoff',
    'BindRuntimeUnlockResponderPeerProbeRouteTransitionHandoff',
    'HoldRuntimeUnlockResponderPeerProbeRouteTransitionHandoffForOwnerWork',
    'InvalidateRuntimeUnlockResponderPeerProbeRouteTransitionHandoff'
)
$script:FileTransferCoordinatorSnapshotFamily6OperationRoles = @(
    'admission_attempt',
    'precommit_current',
    'precommit_terminal',
    'peer_acceptance_attempt',
    'convergence_current',
    'convergence_terminal',
    'probe_adoption_pending',
    'probe_adoption_terminal',
    'owner_work_hold',
    'handoff_current',
    'handoff_terminal',
    'route_transition_handoff_pending',
    'attempted_decision'
)
$script:FileTransferCoordinatorSnapshotFamily6EffectKinds = @(
    'answer_acceptance_observed',
    'semantic_answer_ack_retained',
    'peer_probe_received',
    'peer_probe_ack_delivered',
    'convergence_observed',
    'convergence_invalidated',
    'probe_adoption_requested',
    'probe_adoption_applied',
    'paired_offer_binding_applied',
    'responder_transaction_lease_installed',
    'handoff_bound',
    'handoff_held',
    'handoff_rechecked',
    'handoff_invalidated',
    'route_transition_handoff_supplied',
    'precommit_sidecar_failure_observed',
    'precommit_sidecar_rollback_processed'
)
$script:FileTransferCoordinatorSnapshotFamily6ReceiptStages = [ordered]@{
    attempted_decision = @(
        'Accepted', 'Idempotent', 'Held', 'Rejected', 'Invalidated',
        'Prepared', 'Draining', 'Committed')
    handoff_bind = @('Accepted', 'Idempotent')
    handoff_hold = @('Held')
    handoff_invalidation = @('Invalidated')
    route_transition_prepare_handoff = @(
        'Prepared', 'Idempotent', 'Rejected', 'Invalidated')
}
$script:FileTransferCoordinatorSnapshotFamily5DecisionKinds = @(
    'StageRegularV4FrontierRepairPressureBinding',
    'CommitRegularV4FrontierRepairPressureBinding',
    'PrepareRegularV4FrontierRepairPressureSettlement',
    'CommitRegularV4FrontierRepairPressureSettlement',
    'PrepareRegularV4FrontierRepairPressureRecoveryHandoff',
    'CommitRegularV4FrontierRepairPressureRecoveryHandoff'
)
$script:FileTransferCoordinatorSnapshotFamily5OperationRoles = @(
    'repair_authority_current',
    'repair_authority_terminal',
    'pressure_binding_pending',
    'pressure_binding_installed',
    'pressure_binding_retained_predecessor',
    'pressure_settlement_prepared',
    'pressure_settlement_terminal',
    'recovery_handoff_prepared',
    'recovery_handoff_terminal',
    'attempted_decision'
)
$script:FileTransferCoordinatorSnapshotFamily5EffectKinds = @(
    'pressure_observed',
    'pressure_forwarded',
    'pressure_install_staged',
    'pressure_install_bridge_result',
    'repair_contract_bound',
    'repair_contract_queued',
    'repair_contract_reserved',
    'repair_contract_dispatched',
    'repair_contract_dispatch_observed',
    'physical_send_lease_registered',
    'physical_send_submitted',
    'physical_send_completed',
    'physical_send_lease_retired',
    'pressure_settlement_prepared',
    'pressure_settlement_bridge_applied',
    'local_contract_settled',
    'local_contract_reference_cleared',
    'predecessor_superseded',
    'successor_dispatched',
    'route_commit_local_lifecycle_adopted',
    'recovery_handoff_prepared',
    'recovery_handoff_committed',
    'projection_detached',
    'projection_retained',
    'projection_published',
    'projection_cleared',
    'projection_rejected',
    'regular_v4_pressure_decision_adopted'
)
$script:FileTransferCoordinatorSnapshotFamily4DecisionKinds = @(
    'ApplyFallbackTailLocalProofReceipt',
    'ApplyFallbackTailPeerProofReceipt',
    'CarryForwardFallbackTailPairedReleaseToTransportEpoch',
    'InvalidateFallbackTailYieldTransportEpochReauthorization',
    'ReserveFallbackTailPairedRelease',
    'ConsumeFallbackTailPairedRelease',
    'SupersedeFallbackTailPairedYieldAfterRejectedProbe',
    'BindFallbackTailYieldRetryDispatchHold',
    'RebindFallbackTailYieldRetryDispatchHoldToRepairSuccessor',
    'RejectFallbackTailYieldRetryDispatchHoldSuccessor',
    'IssueFallbackTailYieldRetryDispatchRecheck',
    'ApplyFallbackTailYieldRetryDispatchRecheckReceipt',
    'RebindFallbackTailYieldTransportEpochReauthorization',
    'FallbackTailYieldTransition'
)
$script:FileTransferCoordinatorSnapshotFamily4OperationRoles = @(
    'yield_current',
    'yield_terminal',
    'yield_retained_predecessor',
    'peer_proof_pending_initial',
    'peer_proof_pending_reauthorization',
    'peer_proof_pending_retry_successor',
    'reauthorization_pending',
    'reauthorization_terminal',
    'retry_request_pending',
    'retry_hold_current',
    'retry_recheck_pending',
    'retry_recheck_terminal',
    'release_reservation_current',
    'release_reservation_consumed',
    'attempted_decision'
)
$script:FileTransferCoordinatorSnapshotFamily4EffectKinds = @(
    'local_proof_sent',
    'local_proof_applied',
    'peer_proof_observed',
    'paired_release_published',
    'recovery_owner_retired_after_yield',
    'transport_epoch_reauthorization_handoff',
    'retry_dispatch_hold_observed',
    'retry_dispatch_recheck_observed'
)
$script:FileTransferCoordinatorSnapshotFamily3DecisionKinds = @(
    'IssueReceiveRecoveryOperation',
    'ClassifyPostTunaReceiveRecoveryEpochRestart',
    'ClassifyReceiveRecoverySubmissionFailure',
    'ObserveReceiveRecoveryExecutionStart',
    'ObserveReceiveRecoveryExecutionReceipt',
    'ObserveReceiveRecoveryPeerReachability',
    'ApplyReceiveRecoveryProofStatePublicationReceipt',
    'ObserveReceiveRecoveryProofStatePublicationRecheckEvidence',
    'ObserveReceiveRecoveryProof',
    'ReserveReceiveRecoveryClosure',
    'ConsumeReceiveRecoveryClosure',
    'InvalidateReceiveRecoveryOperation',
    'EvaluateRuntimeUnlockInitialActivationReceiveRecoveryHold',
    'InvalidateRuntimeUnlockInitialActivationReceiveRecoveryHold',
    'CommitRuntimeUnlockInitialActivationOfferEnvelopeRefresh',
    'ApplyReceiveRecoveryFallbackCheckpointBinding',
    'ApplyReceiveRecoveryFallbackCheckpointDeliveryReceipt',
    'RecheckReceiveRecoveryFallbackCheckpointDelivery',
    'ReserveReceiveRecoveryFallbackCheckpointDeliveryResume',
    'ObserveReceiveRecoveryFallbackCheckpointFrontierRequest',
    'ApplyReceiveRecoveryFallbackCheckpointFrontierRequestRecheckReceipt'
)
$script:FileTransferCoordinatorSnapshotFamily3OperationRoles = @(
    'operation_current',
    'operation_terminal',
    'proof_publication_pending',
    'proof_publication_terminal',
    'proof_publication_recheck',
    'initial_activation_hold_current',
    'initial_activation_hold_terminal',
    'initial_activation_envelope_refresh',
    'checkpoint_binding_current',
    'checkpoint_binding_terminal',
    'checkpoint_delivery_pending',
    'checkpoint_delivery_terminal',
    'checkpoint_resume',
    'checkpoint_frontier_hold',
    'checkpoint_frontier_recheck',
    'attempted_decision'
)
$script:FileTransferCoordinatorSnapshotFamily3EffectKinds = @(
    'execution_started',
    'execution_receipt_observed',
    'receive_proof_observed',
    'payload_hold_bound',
    'payload_resumed',
    'proof_publication_delivered',
    'checkpoint_delivery_observed',
    'checkpoint_resume_observed',
    'checkpoint_frontier_recheck_observed',
    'initial_activation_hold_observed',
    'initial_activation_envelope_refresh_observed'
)
$script:FileTransferCoordinatorSnapshotFamily2EffectKinds = @(
    'dispatch_stage_observed',
    'install_committed',
    'execution_observed',
    'settlement_observed',
    'scope_invalidation_applied'
)
$script:FileTransferCoordinatorSnapshotFamily2DecisionKinds = @(
    'AuthorizeRuntimeUnlockRetryDispatch',
    'ObserveRuntimeUnlockRetryDispatchStage',
    'IssueRuntimeUnlockRetryDispatchInstallReceipt',
    'CommitRuntimeUnlockRetryDispatchInstall',
    'EvaluateRuntimeUnlockRetryDispatchDeadline',
    'ObserveRuntimeUnlockRetryDispatchExecutionState',
    'ObserveRuntimeUnlockRetryDispatchSettlement',
    'PrepareRuntimeUnlockRetryDispatchSuccessor',
    'CloseRuntimeUnlockRetryDispatchChain',
    'PrepareRuntimeUnlockRetryDispatchStageExpiry',
    'InvalidateRuntimeUnlockRetryDispatchPendingAttempt',
    'PrepareRuntimeUnlockRetryLifecycleScopeInvalidation',
    'CommitRuntimeUnlockRetryLifecycleScopeInvalidation'
)
$script:FileTransferCoordinatorSnapshotFamily2OperationRoles = @(
    'attempt_pending',
    'attempt_installed',
    'attempt_retained_predecessor',
    'attempt_terminal',
    'bounded_closure',
    'scope_invalidation',
    'attempted_decision'
)
$script:FileTransferCoordinatorSnapshotFamily2ReceiptStages = [ordered]@{
    stage = @('Staged', 'Idempotent', 'Rejected')
    install = @(
        'ReceiptIssued', 'Installed', 'AliasBound', 'SendInFlight',
        'Settled', 'Invalidated', 'Expired', 'Closed')
    execution = @('AliasBound', 'SendInFlight', 'Settled')
    settlement = @(
        'DispatchObserved', 'MissingDispatch', 'Rejected', 'Invalidated',
        'Expired', 'StaleRejected')
    bounded_closure = @('Closed')
    scope_invalidation = @(
        'Pending', 'Accepted', 'Idempotent', 'Rejected', 'Applied')
    decision = @(
        'Accepted', 'Idempotent', 'Rejected', 'Expired', 'Granted', 'Held',
        'Requested', 'ChainClosed')
}
$script:FileTransferCoordinatorSnapshotFamily1EffectKinds = @(
    'pause_installed',
    'physical_send_lease_registered',
    'physical_send_lease_retired',
    'completed_drain_receipt',
    'route_commit_adopted',
    'target_runtime_leg_installed',
    'route_publication_forwarded',
    'retained_frame_response',
    'retained_frame_applied',
    'route_transition_disposition_applied',
    'recovered_epoch_publication',
    'transition_pause_released',
    'terminalized_while_draining'
)
$script:FileTransferCoordinatorSnapshotFamily1RouteTransitionFields = @(
    'role',
    'transitionKind',
    'sourceBoundary',
    'targetBoundary',
    'sourceTransport',
    'proofBoundaryKind',
    'proofAdmissionMode',
    'proofBoundaryFingerprint',
    'durableCheckpointFingerprint',
    'targetRoute',
    'targetProtocolVersion',
    'targetTransport',
    'handoffKind',
    'dispositionKind',
    'retainedFrameType',
    'retainedFrameFingerprint',
    'receipts'
)
$script:FileTransferCoordinatorSnapshotFamily1ReceiptStages = [ordered]@{
    pause_bind = @('Installed', 'Idempotent', 'Rejected')
    pause_rebind = @('Installed', 'Idempotent', 'Rejected')
    drain = @('Completed')
    commit = @('Committed')
    retained_frame = @('Applied', 'Idempotent', 'Rejected')
    disposition = @('Applied', 'Idempotent', 'Rejected')
    pressure_settlement = @('Applied', 'Idempotent', 'Rejected')
    fallback_v6_bootstrap = @('Delivered')
}
$script:FileTransferCoordinatorSnapshotDecisionCatalog = [ordered]@{
    route_transition_incomplete = [ordered]@{
        family = 'route_transition'
        class = 'filetransfer_route_transition_incomplete'
        domain = 'route_runtime'
        phase = 'route_transition'
    }
    route_transition_bootstrap = [ordered]@{
        family = 'route_transition'
        class = 'filetransfer_route_transition_tuna_v4_to_post_tuna_fallback_v6_proof_bootstrap_missing'
        domain = 'route_runtime'
        phase = 'route_transition'
    }
    route_transition_draining_terminal = [ordered]@{
        family = 'route_transition'
        class = 'filetransfer_route_transition_draining_transport_grace_terminalization'
        domain = 'route_runtime'
        phase = 'route_transition'
    }
    route_publication = [ordered]@{
        family = 'route_runtime'
        class = 'publication_authority_missing'
        domain = 'route_runtime'
        phase = 'route_runtime'
    }
    retry_missing_dispatch_successor = [ordered]@{
        family = 'runtime_unlock_retry_lifecycle'
        class = 'runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch'
        domain = 'route_runtime'
        phase = 'runtime_unlock_retry'
    }
    retry_install_incomplete = [ordered]@{
        family = 'runtime_unlock_retry_lifecycle'
        class = 'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete'
        domain = 'route_runtime'
        phase = 'runtime_unlock_retry'
    }
    retry_contract_unsettled = [ordered]@{
        family = 'runtime_unlock_retry_lifecycle'
        class = 'runtime_unlock_retry_dispatch_contract_unsettled'
        domain = 'route_runtime'
        phase = 'runtime_unlock_retry'
    }
    retry_chain_closure = [ordered]@{
        family = 'runtime_unlock_retry_lifecycle'
        class = 'runtime_unlock_retry_dispatch_chain_closure_incomplete'
        domain = 'route_runtime'
        phase = 'runtime_unlock_retry'
    }
    receive_recovery_operation = [ordered]@{
        family = 'receive_recovery'
        class = 'filetransfer_receive_recovery_operation_incomplete'
        domain = 'route_runtime'
        phase = 'bridge_recovery_execution'
    }
    receive_recovery_completed_unproven_successor = [ordered]@{
        family = 'receive_recovery'
        class = 'filetransfer_receive_recovery_completed_unproven_successor_missing'
        domain = 'route_runtime'
        phase = 'bridge_recovery_execution'
    }
    receive_recovery_completed_proven_successor = [ordered]@{
        family = 'receive_recovery'
        class = 'filetransfer_receive_recovery_completed_proven_successor_missing'
        domain = 'route_runtime'
        phase = 'bridge_recovery_execution'
    }
    receive_recovery_payload_hold = [ordered]@{
        family = 'receive_recovery'
        class = 'filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved'
        domain = 'route_runtime'
        phase = 'bridge_recovery_execution'
    }
    receive_recovery_proof_publication = [ordered]@{
        family = 'receive_recovery'
        class = 'filetransfer_receive_recovery_completed_retained_proof_state_publication_missing'
        domain = 'route_runtime'
        phase = 'bridge_recovery_execution'
    }
    receive_recovery_checkpoint = [ordered]@{
        family = 'receive_recovery'
        class = 'filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing'
        domain = 'route_runtime'
        phase = 'bridge_recovery_execution'
    }
    receive_recovery_post_tuna_liveness = [ordered]@{
        family = 'receive_recovery'
        class = 'filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing'
        domain = 'route_runtime'
        phase = 'bridge_recovery_execution'
    }
    receive_recovery_retry_hold = [ordered]@{
        family = 'receive_recovery'
        class = 'runtime_unlock_retry_dispatch_post_fallback_receive_recovery_hold_missing'
        domain = 'route_runtime'
        phase = 'runtime_unlock_retry'
    }
    receive_recovery_initial_activation = [ordered]@{
        family = 'receive_recovery'
        class = 'runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing'
        domain = 'route_runtime'
        phase = 'runtime_unlock_initial_activation'
    }
    responder_probe_handoff = [ordered]@{
        family = 'responder_probe_handoff'
        class = 'runtime_unlock_responder_acked_probe_route_transition_handoff_missing'
        domain = 'route_runtime'
        phase = 'runtime_unlock_retry'
    }
}
$script:FileTransferCoordinatorSnapshotFamily1DecisionKinds = @(
    'StartTransfer',
    'RuntimeUnlockCommitRequested',
    'RoutePublication',
    'RouteCommitPublication',
    'Terminalized',
    'PrepareFileTransferRouteTransition',
    'BeginFileTransferRouteTransitionPhysicalDrain',
    'StageFileTransferRouteTransitionSupersession',
    'CommitFileTransferRouteTransitionSupersession',
    'CommitFileTransferRouteTransition',
    'ApplyFileTransferRouteTransitionRetainedFrameReceipt',
    'InvalidateFileTransferRouteTransition',
    'ApplyFileTransferRouteTransitionDispositionReceipt',
    'IssueFileTransferFallbackV6ProofBootstrap',
    'ApplyFileTransferFallbackV6ProofBootstrapReceipt'
)
$script:FileTransferCoordinatorSnapshotFamily1OperationKinds = [ordered]@{
    runtime_owner = @('RuntimeOwner', 'StartTransfer')
    route_commit = @('RouteCommit', 'RuntimeUnlockCommitRequested')
    route_publication = @('RoutePublication', 'RouteCommitPublication')
    current = @(
        'RegularV4ToTunaV4',
        'TunaV4ToPostTunaFallbackV6',
        'PostTunaFallbackV6ToTunaV4',
        'UncommittedTunaToPostTunaFallbackV6')
    terminal = @(
        'RegularV4ToTunaV4',
        'TunaV4ToPostTunaFallbackV6',
        'PostTunaFallbackV6ToTunaV4',
        'UncommittedTunaToPostTunaFallbackV6')
    supersession = @('Supersession')
    fallback_v6_bootstrap = @('FallbackV6ProofBootstrap')
    inbound = @(
        'PrepareFileTransferRouteTransition',
        'BeginFileTransferRouteTransitionPhysicalDrain',
        'StageFileTransferRouteTransitionSupersession',
        'CommitFileTransferRouteTransitionSupersession',
        'CommitFileTransferRouteTransition',
        'ApplyFileTransferRouteTransitionRetainedFrameReceipt',
        'InvalidateFileTransferRouteTransition',
        'ApplyFileTransferRouteTransitionDispositionReceipt')
    outbound = @(
        'PrepareFileTransferRouteTransition',
        'BeginFileTransferRouteTransitionPhysicalDrain',
        'StageFileTransferRouteTransitionSupersession',
        'CommitFileTransferRouteTransitionSupersession',
        'CommitFileTransferRouteTransition',
        'ApplyFileTransferRouteTransitionRetainedFrameReceipt',
        'InvalidateFileTransferRouteTransition',
        'ApplyFileTransferRouteTransitionDispositionReceipt')
    attempt = @(
        'PrepareFileTransferRouteTransition',
        'BeginFileTransferRouteTransitionPhysicalDrain',
        'StageFileTransferRouteTransitionSupersession',
        'CommitFileTransferRouteTransitionSupersession',
        'CommitFileTransferRouteTransition',
        'ApplyFileTransferRouteTransitionRetainedFrameReceipt',
        'InvalidateFileTransferRouteTransition',
        'ApplyFileTransferRouteTransitionDispositionReceipt')
}

function Get-FileTransferCoordinatorEvidenceCatalog {
    [CmdletBinding()]
    param([string]$ProfileVersion = $script:FileTransferCoordinatorSnapshotCatalogVersion)

    if ($ProfileVersion -ceq $script:FileTransferCoordinatorBootstrapCallbackCatalogVersion) {
        return Get-FileTransferCoordinatorBootstrapCallbackCatalog
    }
    if ($ProfileVersion -ceq $script:FileTransferCoordinatorBootstrapInitiationCatalogVersion) {
        return Get-FileTransferCoordinatorBootstrapInitiationCatalog
    }
    if ($ProfileVersion -ceq $script:FileTransferCoordinatorBootstrapCorrelationCatalogVersion) {
        return Get-FileTransferCoordinatorBootstrapCorrelationCatalog
    }
    if ($ProfileVersion -ceq $script:FileTransferCoordinatorCheckpointRepairContinuityCatalogVersion) {
        return Get-FileTransferCoordinatorCheckpointRepairContinuityCatalog
    }
    if ($ProfileVersion -cne $script:FileTransferCoordinatorSnapshotCatalogVersion) {
        if ($ProfileVersion -cne $script:FileTransferCoordinatorReceiverBarrierCatalogVersion) {
            throw "coordinator_snapshot_catalog_version_unknown:$ProfileVersion"
        }
        $baseCatalog = Get-FileTransferCoordinatorEvidenceCatalog
        $extensionJson = ConvertTo-Json -InputObject $script:FileTransferCoordinatorReceiverBarrierCatalog -Depth 16 -Compress
        $canonical = "baseHash=$($baseCatalog.Hash)`nReceiverBarrierCatalog=$extensionJson"
        $actualHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes (
            [System.Text.UTF8Encoding]::new($false).GetBytes($canonical))
        if ($actualHash -cne $script:FileTransferCoordinatorReceiverBarrierCatalogHash) {
            throw "coordinator_snapshot_receiver_barrier_catalog_hash_invalid:$actualHash"
        }
        $extended = [ordered]@{}
        foreach ($property in $baseCatalog.PSObject.Properties) {
            $extended[$property.Name] = $property.Value
        }
        $extended.Version = $ProfileVersion
        $extended.Hash = $actualHash
        $extended.BaseCatalogHash = $baseCatalog.Hash
        $extended.ReceiverBarrierCatalog = $script:FileTransferCoordinatorReceiverBarrierCatalog
        return [pscustomobject]$extended
    }

    if ($null -ne $script:FileTransferCoordinatorEvidenceCatalogCache) {
        return $script:FileTransferCoordinatorEvidenceCatalogCache
    }

    $snapshotEvents = @(
        'filetransfer_coordinator_snapshot_stream_started',
        'filetransfer_coordinator_transition_snapshot',
        'filetransfer_coordinator_snapshot_stream_completed'
    )
    $generationStampedEffects = @('filetransfer_coordinator_authority_effect')
    $nonAuthorityEvidence = @(
        'filetransfer_transfer_terminal',
        'filetransfer_live_route_epoch_terminal',
        'integrity_verify_passed',
        'temp_finalize_succeeded'
    )
    $canonical = @(
        "contract=$script:FileTransferCoordinatorSnapshotContractVersion"
        "SnapshotAuthorityV1=$($snapshotEvents -join ',')"
        "GenerationStampedEffects=$($generationStampedEffects -join ',')"
        "NonAuthorityEvidence=$($nonAuthorityEvidence -join ',')"
        "RequiredFamilies=$($script:FileTransferCoordinatorSnapshotRequiredFamilies -join ',')"
        "Family1=$($script:FileTransferCoordinatorSnapshotFamily1 -join ',')"
        "Family1Effects=$($script:FileTransferCoordinatorSnapshotFamily1EffectKinds -join ',')"
        "Family1RouteTransitionFields=$($script:FileTransferCoordinatorSnapshotFamily1RouteTransitionFields -join ',')"
        "Family1ReceiptStages=$((@($script:FileTransferCoordinatorSnapshotFamily1ReceiptStages.Keys | ForEach-Object { '{0}:{1}' -f $_, ($script:FileTransferCoordinatorSnapshotFamily1ReceiptStages[$_] -join '|') })) -join ',')"
        "Family1DecisionKinds=$($script:FileTransferCoordinatorSnapshotFamily1DecisionKinds -join ',')"
        "Family1OperationKinds=$((@($script:FileTransferCoordinatorSnapshotFamily1OperationKinds.Keys | ForEach-Object { '{0}:{1}' -f $_, ($script:FileTransferCoordinatorSnapshotFamily1OperationKinds[$_] -join '|') })) -join ',')"
        "Family2=$($script:FileTransferCoordinatorSnapshotFamily2 -join ',')"
        "Family2Effects=$($script:FileTransferCoordinatorSnapshotFamily2EffectKinds -join ',')"
        "Family2DecisionKinds=$($script:FileTransferCoordinatorSnapshotFamily2DecisionKinds -join ',')"
        "Family2OperationRoles=$($script:FileTransferCoordinatorSnapshotFamily2OperationRoles -join ',')"
        "Family2ReceiptStages=$((@($script:FileTransferCoordinatorSnapshotFamily2ReceiptStages.Keys | ForEach-Object { '{0}:{1}' -f $_, ($script:FileTransferCoordinatorSnapshotFamily2ReceiptStages[$_] -join '|') })) -join ',')"
        "Family3=$($script:FileTransferCoordinatorSnapshotFamily3 -join ',')"
        "Family3Effects=$($script:FileTransferCoordinatorSnapshotFamily3EffectKinds -join ',')"
        "Family3DecisionKinds=$($script:FileTransferCoordinatorSnapshotFamily3DecisionKinds -join ',')"
        "Family3OperationRoles=$($script:FileTransferCoordinatorSnapshotFamily3OperationRoles -join ',')"
        "Family4=$($script:FileTransferCoordinatorSnapshotFamily4 -join ',')"
        "Family4Effects=$($script:FileTransferCoordinatorSnapshotFamily4EffectKinds -join ',')"
        "Family4DecisionKinds=$($script:FileTransferCoordinatorSnapshotFamily4DecisionKinds -join ',')"
        "Family4OperationRoles=$($script:FileTransferCoordinatorSnapshotFamily4OperationRoles -join ',')"
        "Family5=$($script:FileTransferCoordinatorSnapshotFamily5 -join ',')"
        "Family5Effects=$($script:FileTransferCoordinatorSnapshotFamily5EffectKinds -join ',')"
        "Family5DecisionKinds=$($script:FileTransferCoordinatorSnapshotFamily5DecisionKinds -join ',')"
        "Family5OperationRoles=$($script:FileTransferCoordinatorSnapshotFamily5OperationRoles -join ',')"
        "Family6=$($script:FileTransferCoordinatorSnapshotFamily6 -join ',')"
        "Family6Effects=$($script:FileTransferCoordinatorSnapshotFamily6EffectKinds -join ',')"
        "Family6DecisionKinds=$($script:FileTransferCoordinatorSnapshotFamily6DecisionKinds -join ',')"
        "Family6OperationRoles=$($script:FileTransferCoordinatorSnapshotFamily6OperationRoles -join ',')"
        "Family6ReceiptStages=$((@($script:FileTransferCoordinatorSnapshotFamily6ReceiptStages.Keys | ForEach-Object { '{0}:{1}' -f $_, ($script:FileTransferCoordinatorSnapshotFamily6ReceiptStages[$_] -join '|') })) -join ',')"
        "DecisionCatalog=$((@($script:FileTransferCoordinatorSnapshotDecisionCatalog.Keys | ForEach-Object { $entry = $script:FileTransferCoordinatorSnapshotDecisionCatalog[$_]; '{0}|{1}|{2}|{3}|{4}' -f $_, $entry.family, $entry.class, $entry.domain, $entry.phase })) -join ',')"
    ) -join "`n"
    $actualHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes (
        [System.Text.UTF8Encoding]::new($false).GetBytes($canonical))
    if ($actualHash -ne $script:FileTransferCoordinatorSnapshotCatalogHash) {
        throw "coordinator_snapshot_catalog_hash_invalid:$actualHash"
    }

    $legacyCatalogPath = Join-Path $PSScriptRoot 'LegacyV0EvidenceCatalog.json'
    if (-not (Test-Path -LiteralPath $legacyCatalogPath -PathType Leaf)) {
        throw "legacy_v0_evidence_catalog_missing:$legacyCatalogPath"
    }
    $legacyBytes = [System.IO.File]::ReadAllBytes($legacyCatalogPath)
    $legacyHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $legacyBytes
    if ($legacyHash -ne $script:FileTransferLegacyV0CatalogHash) {
        throw "legacy_v0_evidence_catalog_hash_invalid:$legacyHash"
    }
    $legacyCatalog = (
        [System.Text.UTF8Encoding]::new($false, $true).GetString($legacyBytes)) |
        ConvertFrom-Json
    if ([string]$legacyCatalog.version -ne $script:FileTransferLegacyV0CatalogVersion) {
        throw "legacy_v0_evidence_catalog_version_unknown:$($legacyCatalog.version)"
    }

    $result = [pscustomobject]@{
        Version = $script:FileTransferCoordinatorSnapshotCatalogVersion
        Hash = $script:FileTransferCoordinatorSnapshotCatalogHash
        SnapshotContractVersion = $script:FileTransferCoordinatorSnapshotContractVersion
        SnapshotAuthorityV1 = $snapshotEvents
        GenerationStampedEffects = $generationStampedEffects
        NonAuthorityEvidence = $nonAuthorityEvidence
        RequiredFamilies = $script:FileTransferCoordinatorSnapshotRequiredFamilies
        Family1 = $script:FileTransferCoordinatorSnapshotFamily1
        Family1EffectKinds = $script:FileTransferCoordinatorSnapshotFamily1EffectKinds
        Family1RouteTransitionFields = $script:FileTransferCoordinatorSnapshotFamily1RouteTransitionFields
        Family1ReceiptStages = $script:FileTransferCoordinatorSnapshotFamily1ReceiptStages
        Family1DecisionKinds = $script:FileTransferCoordinatorSnapshotFamily1DecisionKinds
        Family1OperationKinds = $script:FileTransferCoordinatorSnapshotFamily1OperationKinds
        Family2 = $script:FileTransferCoordinatorSnapshotFamily2
        Family2EffectKinds = $script:FileTransferCoordinatorSnapshotFamily2EffectKinds
        Family2DecisionKinds = $script:FileTransferCoordinatorSnapshotFamily2DecisionKinds
        Family2OperationRoles = $script:FileTransferCoordinatorSnapshotFamily2OperationRoles
        Family2ReceiptStages = $script:FileTransferCoordinatorSnapshotFamily2ReceiptStages
        Family3 = $script:FileTransferCoordinatorSnapshotFamily3
        Family3EffectKinds = $script:FileTransferCoordinatorSnapshotFamily3EffectKinds
        Family3DecisionKinds = $script:FileTransferCoordinatorSnapshotFamily3DecisionKinds
        Family3OperationRoles = $script:FileTransferCoordinatorSnapshotFamily3OperationRoles
        Family4 = $script:FileTransferCoordinatorSnapshotFamily4
        Family4EffectKinds = $script:FileTransferCoordinatorSnapshotFamily4EffectKinds
        Family4DecisionKinds = $script:FileTransferCoordinatorSnapshotFamily4DecisionKinds
        Family4OperationRoles = $script:FileTransferCoordinatorSnapshotFamily4OperationRoles
        Family5 = $script:FileTransferCoordinatorSnapshotFamily5
        Family5EffectKinds = $script:FileTransferCoordinatorSnapshotFamily5EffectKinds
        Family5DecisionKinds = $script:FileTransferCoordinatorSnapshotFamily5DecisionKinds
        Family5OperationRoles = $script:FileTransferCoordinatorSnapshotFamily5OperationRoles
        Family6 = $script:FileTransferCoordinatorSnapshotFamily6
        Family6EffectKinds = $script:FileTransferCoordinatorSnapshotFamily6EffectKinds
        Family6DecisionKinds = $script:FileTransferCoordinatorSnapshotFamily6DecisionKinds
        Family6OperationRoles = $script:FileTransferCoordinatorSnapshotFamily6OperationRoles
        Family6ReceiptStages = $script:FileTransferCoordinatorSnapshotFamily6ReceiptStages
        DecisionCatalog = $script:FileTransferCoordinatorSnapshotDecisionCatalog
        LegacyV0 = [pscustomobject]@{
            Version = $script:FileTransferLegacyV0CatalogVersion
            Hash = $script:FileTransferLegacyV0CatalogHash
            EventCount = @($legacyCatalog.events).Count
            CorrelatorCount = @($legacyCatalog.correlators).Count
        }
    }
    $script:FileTransferCoordinatorEvidenceCatalogCache = $result
    return $result
}

function Get-FileTransferCoordinatorSnapshotLineField {
    param(
        [Parameter(Mandatory = $true)][string]$Line,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Default = ''
    )

    $match = [regex]::Match(
        $Line,
        '(?:^|;\s*)' + [regex]::Escape($Name) + '=([^;\r\n]*)',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        return $Default
    }
    return $match.Groups[1].Value.Trim()
}

function Get-FileTransferCoordinatorSnapshotEventName {
    param([Parameter(Mandatory = $true)][string]$Line)

    $match = [regex]::Match(
        $Line,
        '(?:^|[;\s])event=([a-z0-9_]+)',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        return ''
    }
    return $match.Groups[1].Value
}

function Get-FileTransferCoordinatorSnapshotLineTimestampMilliseconds {
    param([Parameter(Mandatory = $true)][string]$Line)

    $match = [regex]::Match(
        $Line,
        '^\[(?<timestamp>[^\]]+)\]',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        return [long]::MaxValue
    }
    try {
        return [DateTimeOffset]::Parse(
            $match.Groups['timestamp'].Value,
            [System.Globalization.CultureInfo]::InvariantCulture,
            [System.Globalization.DateTimeStyles]::AssumeUniversal
        ).ToUnixTimeMilliseconds()
    }
    catch {
        return [long]::MaxValue
    }
}

function ConvertTo-FileTransferCoordinatorSnapshotValidationFailures {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Errors,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Records,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Boundaries
    )

    $recordsByLineIndex = @{}
    foreach ($record in @($Records)) {
        if ($null -eq $record -or
            $record.PSObject.Properties.Name -notcontains 'LineIndex') {
            continue
        }
        $recordsByLineIndex[[long]$record.LineIndex] = $record
    }
    $boundariesById = @{}
    foreach ($boundary in @($Boundaries)) {
        if ($null -eq $boundary -or
            $boundary.PSObject.Properties.Name -notcontains 'boundaryId' -or
            [string]::IsNullOrWhiteSpace([string]$boundary.boundaryId)) {
            continue
        }
        $boundariesById[[string]$boundary.boundaryId] = $boundary
    }

    $failures = [System.Collections.Generic.List[object]]::new()
    foreach ($errorText in @($Errors)) {
        $parts = @([string]$errorText -split ':')
        $lineIndex = -1L
        $lineMatch = [regex]::Match([string]$errorText, ':(?<line>[0-9]+)$')
        if ($lineMatch.Success) {
            [void][long]::TryParse($lineMatch.Groups['line'].Value, [ref]$lineIndex)
        }
        $record = if ($recordsByLineIndex.ContainsKey($lineIndex)) {
            $recordsByLineIndex[$lineIndex]
        }
        else {
            $null
        }
        $line = if ($null -ne $record) { [string]$record.Line } else { '' }
        $transitionId = if ($null -ne $record) {
            Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'transition_id'
        }
        else {
            @($parts | Where-Object {
                    -not [string]::IsNullOrWhiteSpace([string]$_) -and
                    $boundariesById.ContainsKey([string]$_)
                } |
                Select-Object -First 1) | Select-Object -First 1
        }
        $boundary = if (-not [string]::IsNullOrWhiteSpace($transitionId) -and
            $boundariesById.ContainsKey($transitionId)) {
            $boundariesById[$transitionId]
        }
        else {
            $null
        }
        $instanceId = if ($null -ne $record) {
            [string]$record.CoordinatorInstanceId
        }
        elseif ($null -ne $boundary) {
            [string]$boundary.coordinatorInstanceId
        }
        else {
            '(artifact)'
        }
        $writer = if ($null -ne $record) {
            Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'log_writer_id' -Default '(unknown)'
        }
        elseif ($null -ne $boundary) { [string]$boundary.writer }
        else { '(unknown)' }
        $writerSequence = 0L
        if ($null -ne $record) {
            [void][long]::TryParse(
                (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'log_writer_sequence' -Default '0'),
                [ref]$writerSequence)
        }
        elseif ($null -ne $boundary) {
            $writerSequence = [long]$boundary.writerSequence
        }
        $decisionUtcMilliseconds = if ($null -ne $boundary) {
            [long]$boundary.decisionUtcMilliseconds
        }
        elseif ($null -ne $record) {
            Get-FileTransferCoordinatorSnapshotLineTimestampMilliseconds -Line $line
        }
        else { [long]::MaxValue }
        $stateGeneration = 0L
        if ($null -ne $record) {
            [void][long]::TryParse(
                (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'coordinator_state_generation' -Default '0'),
                [ref]$stateGeneration)
        }
        elseif ($null -ne $boundary) {
            $stateGeneration = [long]$boundary.afterStateGeneration
        }
        $eventName = if ($null -ne $record) { [string]$record.EventName }
            elseif ($null -ne $boundary) { 'filetransfer_coordinator_transition_snapshot' }
            else { '(artifact)' }
        $effectIdentity = if ($null -ne $record) {
            Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'effect_identity' -Default '(none)'
        }
        else { '(none)' }
        $operationKey = if ($null -ne $record) {
            Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'operation_key' -Default '(none)'
        }
        else { '(none)' }
        $authorityId = if ($null -ne $record) {
            Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'operation_authority_id' -Default '(none)'
        }
        else { '(none)' }
        $operationGeneration = 0L
        if ($null -ne $record) {
            [void][long]::TryParse(
                (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'operation_generation' -Default '0'),
                [ref]$operationGeneration)
        }
        $receiptIdentity = if ($null -ne $record) {
            Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'receipt_identity' -Default '(none)'
        }
        else { '(none)' }
        $family = if ($null -ne $record) {
            Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'operation_family' -Default '(none)'
        }
        else { '(none)' }
        $reasonParts = @([string]$parts[0] -split '\|', 2)
        $reason = [string]$reasonParts[0]
        $variant = if ($reasonParts.Count -eq 2 -and
            -not [string]::IsNullOrWhiteSpace([string]$reasonParts[1])) {
            [string]$reasonParts[1]
        }
        else {
            '(none)'
        }
        $identity = "$reason|$variant|$instanceId|$transitionId|$eventName|$effectIdentity|$operationKey"
        $failureText = if ($variant -cne '(none)' -and $lineIndex -ge 0) {
            "$reason`:$lineIndex"
        }
        else {
            [string]$errorText
        }
        $failures.Add([pscustomobject][ordered]@{
                FailureText = $failureText
                Reason = $reason
                Variant = $variant
                EventName = $eventName
                LineIndex = $lineIndex
                Writer = $writer
                WriterSequence = $writerSequence
                DecisionUtcMilliseconds = $decisionUtcMilliseconds
                CoordinatorInstanceId = $instanceId
                TransitionId = if ([string]::IsNullOrWhiteSpace($transitionId)) {
                    '(none)'
                } else { $transitionId }
                EffectIdentity = $effectIdentity
                OperationKey = $operationKey
                AuthorityId = $authorityId
                OperationGeneration = $operationGeneration
                ReceiptIdentity = $receiptIdentity
                Family = $family
                StateGeneration = $stateGeneration
                Boundary = $boundary
                Identity = $identity
            }) | Out-Null
    }

    return @($failures | Sort-Object `
            DecisionUtcMilliseconds,
            Writer,
            WriterSequence,
            LineIndex,
            Identity)
}

function Get-FileTransferCoordinatorSnapshotObjectProperty {
    param(
        $InputObject,
        [Parameter(Mandatory = $true)][string]$Name,
        $Default = $null
    )

    if ($null -eq $InputObject -or
        $InputObject.PSObject.Properties.Name -notcontains $Name) {
        return $Default
    }
    return $InputObject.$Name
}

function Get-FileTransferCoordinatorSnapshotSha256 {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Test-FileTransferCoordinatorSnapshotEffectScope {
    param(
        [Parameter(Mandatory = $true)]$Operation,
        [Parameter(Mandatory = $true)]$Effect
    )

    if ((Test-FileTransferCoordinatorCheckpointRepairContinuityMarker $Operation) -or
        (Test-FileTransferCoordinatorBootstrapCorrelationMarker $Operation) -or
        (Test-FileTransferCoordinatorBootstrapInitiationMarker $Operation) -or
        (Test-FileTransferCoordinatorBootstrapCallbackMarker $Operation)) { return $false }
    $scope = if ($Operation.PSObject.Properties.Name -contains 'routeRuntime' -and
        $null -ne $Operation.routeRuntime) {
        $Operation.routeRuntime.boundary
    }
    elseif ($Operation.PSObject.Properties.Name -contains 'routeTransition' -and
        $null -ne $Operation.routeTransition) {
        $Operation.routeTransition.sourceBoundary
    }
    elseif ($Operation.PSObject.Properties.Name -contains 'receiveRecovery' -and
        $null -ne $Operation.receiveRecovery) {
        $Operation.receiveRecovery.sourceBoundary
    }
    elseif ($Operation.PSObject.Properties.Name -contains 'fallbackTailYield' -and
        $null -ne $Operation.fallbackTailYield) {
        $Operation.fallbackTailYield.sourceBoundary
    }
    elseif ($Operation.PSObject.Properties.Name -contains
            'regularV4FrontierRepairPressure' -and
        $null -ne $Operation.regularV4FrontierRepairPressure) {
        $Operation.regularV4FrontierRepairPressure.sourceBoundary
    }
    elseif ($Operation.PSObject.Properties.Name -contains
            'responderProbeHandoff' -and
        $null -ne $Operation.responderProbeHandoff) {
        $Operation.responderProbeHandoff.sourceBoundary
    }
    elseif ($Operation.PSObject.Properties.Name -contains 'receiverBarrier' -and
        $null -ne $Operation.receiverBarrier) {
        $Operation.receiverBarrier.sourceBoundary
    }
    else {
        $Operation.runtimeUnlockRetry.sourceBoundary
    }
    if ($null -eq $scope) {
        return $false
    }

    foreach ($pair in @(
            @('route', 'Route'),
            @('protocolVersion', 'ProtocolVersion'),
            @('liveRouteEpoch', 'LiveRouteEpoch'),
            @('routePublicationGeneration', 'RoutePublicationGeneration'),
            @('routeCommitGeneration', 'RouteCommitGeneration'),
            @('runtimeGeneration', 'RuntimeGeneration'),
            @('terminalGeneration', 'TerminalGeneration'),
            @('legGeneration', 'LegGeneration'),
            @('transportEpoch', 'TransportEpoch'),
            @('bridgeRecoveryGeneration', 'BridgeRecoveryGeneration'),
            @('checkpointGeneration', 'CheckpointGeneration'),
            @('checkpointRequestId', 'CheckpointRequestId'),
            @('dataSessionIdentity', 'DataSessionIdentity'),
            @('dataSessionGeneration', 'DataSessionGeneration'))) {
        $expected = [string](
            Get-FileTransferCoordinatorSnapshotObjectProperty `
                -InputObject $scope `
                -Name $pair[0] `
                -Default '')
        if ([string]::IsNullOrWhiteSpace($expected) -or
            [string]::IsNullOrWhiteSpace([string]$Effect.($pair[1])) -or
            $expected -cne [string]$Effect.($pair[1])) {
            return $false
        }
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotEffectReceipt {
    param(
        [Parameter(Mandatory = $true)]$Operation,
        [Parameter(Mandatory = $true)]$Effect
    )

    if ((Test-FileTransferCoordinatorCheckpointRepairContinuityMarker $Operation) -or
        (Test-FileTransferCoordinatorBootstrapCorrelationMarker $Operation) -or
        (Test-FileTransferCoordinatorBootstrapInitiationMarker $Operation) -or
        (Test-FileTransferCoordinatorBootstrapCallbackMarker $Operation)) { return $false }
    if ([bool]$Operation.receiptValidated -and
        [string]$Operation.receiptIdentity -ceq
            [string]$Effect.ReceiptIdentity -and
        [long]$Operation.receiptGeneration -eq
            [long]$Effect.ReceiptGeneration -and
        [string]$Operation.receiptStatus -ceq
            [string]$Effect.ReceiptStatus -and
        [string]$Operation.receiptFingerprint -ceq
            [string]$Effect.ReceiptFingerprint) {
        return $true
    }

    foreach ($propertyName in @(
            'routeTransition',
            'runtimeUnlockRetry',
            'receiveRecovery',
            'fallbackTailYield',
            'regularV4FrontierRepairPressure',
            'responderProbeHandoff',
            'receiverBarrier')) {
        if ($Operation.PSObject.Properties.Name -notcontains $propertyName -or
            $null -eq $Operation.$propertyName) {
            continue
        }
        $matches = @($Operation.$propertyName.receipts | Where-Object {
                [bool]$_.receiptValidated -and
                [string]$_.receiptIdentity -ceq
                    [string]$Effect.ReceiptIdentity -and
                [long]$_.receiptGeneration -eq
                    [long]$Effect.ReceiptGeneration -and
                [string]$_.receiptStatus -ceq
                    [string]$Effect.ReceiptStatus -and
                [string]$_.receiptFingerprint -ceq
                    [string]$Effect.ReceiptFingerprint
            })
        if ($matches.Count -eq 1) {
            return $true
        }
        if ($matches.Count -gt 1) {
            return $false
        }
    }
    return $false
}

function Test-FileTransferCoordinatorSnapshotExactEffectBinding {
    param(
        [Parameter(Mandatory = $true)]$Boundary,
        [Parameter(Mandatory = $true)]$Operation,
        [Parameter(Mandatory = $true)]$Effect
    )

    return [string]$Effect.CoordinatorInstanceId -ceq
            [string]$Boundary.coordinatorInstanceId -and
        [string]$Effect.TransitionId -ceq [string]$Boundary.boundaryId -and
        [long]$Effect.StateGeneration -eq
            [long]$Boundary.afterStateGeneration -and
        [string]$Effect.Family -ceq [string]$Operation.family -and
        [string]$Effect.OperationKey -ceq [string]$Operation.operationKey -and
        [string]$Effect.AuthorityId -ceq [string]$Operation.authorityId -and
        [long]$Effect.OperationGeneration -eq
            [long]$Operation.operationGeneration -and
        (Test-FileTransferCoordinatorSnapshotEffectScope `
            -Operation $Operation -Effect $Effect) -and
        (Test-FileTransferCoordinatorSnapshotEffectReceipt `
            -Operation $Operation -Effect $Effect)
}

function Test-FileTransferCoordinatorSnapshotBoundaryComplete {
    param([Parameter(Mandatory = $true)]$Boundary)

    foreach ($name in @(
            'route',
            'protocolVersion',
            'liveRouteEpoch',
            'routePublicationGeneration',
            'routeCommitGeneration',
            'runtimeGeneration',
            'terminalGeneration',
            'legGeneration',
            'transportEpoch',
            'bridgeRecoveryGeneration',
            'checkpointGeneration',
            'checkpointRequestId',
            'dataSessionIdentity',
            'dataSessionGeneration')) {
        if ($Boundary.PSObject.Properties.Name -notcontains $name -or
            [string]::IsNullOrWhiteSpace([string]$Boundary.$name)) {
            return $false
        }
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotBoundaryEqual {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Left,
        [Parameter(Mandatory = $true)]$Right
    )

    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Left) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Right)) {
        return $false
    }

    return (ConvertTo-Json -InputObject $Left -Depth 16 -Compress) -ceq
        (ConvertTo-Json -InputObject $Right -Depth 16 -Compress)
}

function Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual {
    param(
        [Parameter(Mandatory = $true)]$Left,
        [Parameter(Mandatory = $true)]$Right
    )

    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Left) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Right)) {
        return $false
    }
    foreach ($name in @(
            'route', 'protocolVersion', 'liveRouteEpoch',
            'routePublicationGeneration', 'routeCommitGeneration',
            'runtimeGeneration', 'terminalGeneration', 'legGeneration',
            'transportEpoch', 'bridgeRecoveryGeneration',
            'checkpointGeneration', 'checkpointRequestId',
            'dataSessionIdentity', 'dataSessionGeneration')) {
        $leftValue = Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $Left -Name $name -Default $null
        $rightValue = Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $Right -Name $name -Default $null
        if ([string]$leftValue -cne [string]$rightValue) {
            return $false
        }
    }
    return $true
}

function Test-FileTransferCoordinatorReceiverBarrierMarker {
    param($Operation)
    return $Operation.PSObject.Properties.Name -contains 'receiverBarrier' -or
        [string]$Operation.operationKind -ceq 'InitialActivationReceiverBarrier' -or
        [string]$Operation.operationRole -in $script:FileTransferCoordinatorReceiverBarrierCatalog.operationRoles
}

function Test-FileTransferCoordinatorReceiverBarrierSourceProjection {
    param($Source, $Aggregate, [AllowEmptyCollection()][object[]]$RecoveryOperations = @(), [switch]$StructureOnly)

    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $Source) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $Aggregate)) { return $false }
    if (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $Source $Aggregate) { return $true }
    if ($Source.route -cne 'regular_nkn_v4_fast' -or [int]$Source.protocolVersion -ne 4 -or
        [long]$Source.bridgeRecoveryGeneration -ne 0 -or [long]$Aggregate.bridgeRecoveryGeneration -le 0) { return $false }

    # The barrier owns the leg's bridge generation. The aggregate projects
    # CurrentOperation.Authority.BridgeRecoveryDecision.BridgeDecisionGeneration.
    # Only this directional projection may differ; no other scope field is optional.
    $projected = $Source | Select-Object *
    $projected.bridgeRecoveryGeneration = $Aggregate.bridgeRecoveryGeneration
    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $projected $Aggregate)) { return $false }
    # Parsing admits the shape provisionally. The validated history reducer
    # below must establish its exact current recovery authority before acceptance.
    if ($StructureOnly) { return $true }
    $matches = @($RecoveryOperations | Where-Object {
        $null -ne $_ -and $_.family -ceq 'receive_recovery' -and $_.operationRole -ceq 'operation_current' -and
        $_.operationKind -ceq 'BridgeReceiveStallRecovery' -and $_.afterState -cne 'None' -and
        $_.stateApplied -is [bool] -and $_.stateApplied -and $_.authorityId -cnotin @('', '(none)') -and
        [long]$_.operationGeneration -gt 0 -and $null -ne $_.receiveRecovery -and
        $_.receiveRecovery.role -ceq 'operation_current' -and
        $_.receiveRecovery.recoveryKind -ceq 'BridgeReceiveStallRecovery' -and
        [long]$_.receiveRecovery.bridgeDecisionGeneration -eq [long]$Aggregate.bridgeRecoveryGeneration -and
        (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $_.receiveRecovery.sourceBoundary $Source) -and
        (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $_.receiveRecovery.effectiveBoundary $Source)
    })
    return @($RecoveryOperations).Count -eq 1 -and $matches.Count -eq 1
}

function Test-FileTransferCoordinatorReceiverBarrierOperation {
    param($Envelope, $Operation, $Catalog)
    try {
        if (-not (Test-FileTransferCoordinatorReceiverBarrierCatalogCapability $Catalog) -or
            $Operation.family -cne 'responder_probe_handoff' -or
            $Operation.operationKind -cne 'InitialActivationReceiverBarrier' -or
            $Operation.operationRole -cnotin $script:FileTransferCoordinatorReceiverBarrierCatalog.operationRoles -or
            $Operation.PSObject.Properties.Name -notcontains 'receiverBarrier' -or
            $null -eq $Operation.receiverBarrier) { return $false }
        foreach ($other in @('routeRuntime', 'routeTransition', 'runtimeUnlockRetry', 'receiveRecovery',
                'fallbackTailYield', 'regularV4FrontierRepairPressure', 'responderProbeHandoff')) {
            if ($Operation.PSObject.Properties.Name -contains $other -and $null -ne $Operation.$other) { return $false }
        }
        $payload = $Operation.receiverBarrier
        $fields = @($script:FileTransferCoordinatorReceiverBarrierCatalog.payloadFields.Keys)
        if (@($payload.PSObject.Properties).Count -ne $fields.Count -or
            @($fields | Where-Object { $payload.PSObject.Properties.Name -cnotcontains $_ }).Count -ne 0 -or
            $payload.role -cne $Operation.operationRole -or
            $Operation.operationKey -cne "responder_probe_handoff|$($Operation.operationRole)|$($Operation.authorityId)|$($Operation.operationGeneration)|InitialActivationReceiverBarrier" -or
            @($Envelope.aggregate.operations | Where-Object {
                $_.family -ceq 'responder_probe_handoff' -and $_.operationKey -ceq $Operation.operationKey
            }).Count -ne 1 -or
            $Operation.predecessorAuthorityId -cne '(none)' -or
            [long]$Operation.operationGeneration -le 0 -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $payload.sourceBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $payload.effectiveBoundary) -or
            [string]$payload.negotiationFingerprint -cnotmatch '^[0-9a-f]{64}$' -or
            @($payload.receipts).Count -ne 1) { return $false }
        $attempt = $Operation.operationRole -ceq 'receiver_barrier_attempt'
        $state = if ($Operation.afterState -ceq 'None') { [string]$Operation.beforeState } else { [string]$Operation.afterState }
        $receipt = @($payload.receipts)[0]
        if ($Operation.stateApplied -isnot [bool] -or $Operation.receiptValidated -isnot [bool] -or
            $receipt.receiptValidated -isnot [bool] -or
            @($receipt.PSObject.Properties).Count -ne 10 -or
            @(@('stage','commandIdentity','commandGeneration','commandFingerprint','receiptIdentity','receiptGeneration',
                'receiptStatus','receiptReason','receiptFingerprint','receiptValidated') | Where-Object {
                    $receipt.PSObject.Properties.Name -cnotcontains $_ }).Count -ne 0) { return $false }
        foreach ($value in @($Operation.operationGeneration, $Operation.commandGeneration, $Operation.receiptGeneration,
                $payload.pauseGeneration, $payload.replacementPauseGeneration, $receipt.commandGeneration, $receipt.receiptGeneration)) {
            if ($value -isnot [int] -and $value -isnot [long]) { return $false }
        }
        foreach ($scope in @($payload.sourceBoundary, $payload.effectiveBoundary)) {
            if (@($scope.PSObject.Properties).Count -ne 14) { return $false }
            foreach ($field in @('protocolVersion','liveRouteEpoch','routePublicationGeneration','routeCommitGeneration',
                    'runtimeGeneration','terminalGeneration','legGeneration','transportEpoch','bridgeRecoveryGeneration',
                    'checkpointGeneration','dataSessionGeneration')) {
                if (($scope.$field -isnot [int] -and $scope.$field -isnot [long]) -or [long]$scope.$field -lt 0) { return $false }
            }
        }
        if ($attempt) {
            if ($Envelope.decisionKind -cnotin $script:FileTransferCoordinatorReceiverBarrierCatalog.decisionKinds -or
                $state -cnotin @('Accepted','Idempotent','Rejected','Stale','Invalidated') -or
                $state -cne $Envelope.decisionStatus -or $Operation.beforeState -cne 'None' -or
                [bool]$Operation.stateApplied -or $receipt.receiptStatus -cne $state -or
                $receipt.receiptReason -cne $Envelope.decisionReason) { return $false }
            $stage = switch -CaseSensitive ($Envelope.decisionKind) {
                'BindInitialActivationReceiverBarrier' { 'barrier_bind' }
                'AttachInitialActivationReceiverBarrierAdmission' { 'barrier_admission' }
                default { 'barrier_close' }
            }
        }
        else {
            if ($state -cnotin $script:FileTransferCoordinatorReceiverBarrierCatalog.states -or
                -not [bool]$Operation.stateApplied -or [string]$Operation.authorityId -in @('','(none)') -or
                [long]$payload.pauseGeneration -le 0 -or
                ($Operation.operationRole -ceq 'receiver_barrier_current' -and $state -cnotin @('Bound','AdmissionAttached','ClosePrepared')) -or
                ($Operation.operationRole -ceq 'receiver_barrier_terminal' -and $state -cnotin @('Closed','Invalidated'))) { return $false }
            $stage = switch -CaseSensitive ($state) { 'Bound' { 'barrier_bind' }; 'AdmissionAttached' { 'barrier_admission' }; default { 'barrier_close' } }
            $statuses = switch -CaseSensitive ($state) { 'Closed' { @('Applied','Idempotent') }; 'Invalidated' { @('Invalidated') }; default { @('Accepted') } }
            if ($receipt.receiptStatus -cnotin $statuses) { return $false }
            if ($state -ceq 'Bound' -and ($payload.admissionFingerprint -cne '(none)' -or $payload.failureOriginFingerprint -cne '(none)')) { return $false }
            if ($state -in @('AdmissionAttached','ClosePrepared','Closed') -and
                $payload.admissionFingerprint -cnotmatch '^[0-9a-f]{64}$') { return $false }
            if ($state -ceq 'AdmissionAttached' -and $payload.failureOriginFingerprint -cne '(none)') { return $false }
            if ($state -in @('ClosePrepared','Closed') -and $payload.failureOriginFingerprint -cnotmatch '^[0-9a-f]{64}$') { return $false }
        }
        foreach ($field in @('admissionFingerprint','failureOriginFingerprint','invalidatingAdmissionFingerprint')) {
            if ($payload.$field -cne '(none)' -and $payload.$field -cnotmatch '^[0-9a-f]{64}$') { return $false }
        }
        if ([long]$payload.replacementPauseGeneration -lt 0 -or
            (-not $attempt -and $state -cne 'Invalidated' -and
             ($payload.invalidatingAdmissionFingerprint -cne '(none)' -or [long]$payload.replacementPauseGeneration -ne 0))) { return $false }
        if (-not $attempt -or $state -in @('Accepted','Idempotent','Invalidated')) {
            $source = $payload.sourceBoundary
            if ($source.route -cne 'regular_nkn_v4_fast' -or [int]$source.protocolVersion -ne 4 -or
                [int]$source.liveRouteEpoch -ne 0 -or [long]$source.transportEpoch -ne 0 -or
                [int]$source.bridgeRecoveryGeneration -ne 0 -or [int]$source.checkpointGeneration -ne 0 -or
                $source.checkpointRequestId -cne '(none)' -or [int]$source.routeCommitGeneration -ne 0 -or
                [int]$source.routePublicationGeneration -le 0 -or [int]$source.runtimeGeneration -le 0 -or
                [int]$source.legGeneration -le 0 -or [long]$source.dataSessionGeneration -le 0 -or
                $source.dataSessionIdentity -in @('','(none)') -or $Envelope.direction -cne 'inbound') { return $false }
        }
        $effective = if (-not $attempt -and $Operation.operationRole -ceq 'receiver_barrier_terminal' -and $state -ceq 'Closed') {
            $payload.sourceBoundary
        } elseif (-not $attempt -and $Operation.afterState -ceq 'None') {
            $Envelope.aggregate.beforeBoundary
        } else {
            $Envelope.aggregate.afterBoundary
        }
        if (-not $attempt -and $Operation.operationRole -ceq 'receiver_barrier_current' -and $Operation.afterState -cne 'None' -and
            -not (Test-FileTransferCoordinatorReceiverBarrierSourceProjection $payload.sourceBoundary $Envelope.aggregate.afterBoundary -StructureOnly)) { return $false }
        if (-not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $payload.effectiveBoundary $effective) -or
            $receipt.stage -cne $stage -or [string]::IsNullOrWhiteSpace($receipt.receiptReason) -or
            [long]$receipt.commandGeneration -ne [long]$Operation.operationGeneration -or
            [long]$receipt.receiptGeneration -ne [long]$Operation.operationGeneration -or
            $receipt.commandIdentity -cne $Operation.authorityId -or $receipt.receiptIdentity -cne $Operation.authorityId -or
            [bool]$receipt.receiptValidated -ne ($receipt.receiptStatus -cin @('Accepted','Applied','Idempotent','Invalidated'))) { return $false }
        foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity','receiptGeneration','receiptStatus','receiptFingerprint','receiptValidated')) {
            if ([string]$receipt.$field -cne [string]$Operation.$field) { return $false }
        }
        foreach ($field in @('commandFingerprint','receiptFingerprint')) {
            if ($receipt.$field -cnotmatch '^(?:[0-9a-f]{64}|fte1-[0-9a-f]{24})$') { return $false }
        }
        return $true
    }
    catch { return $false }
}

function Test-FileTransferCoordinatorReceiverBarrierRoutePreparation {
    param($Boundary, $Source, [AllowEmptyCollection()][object[]]$BeforeRecoveryOperations = @(),
        [AllowEmptyCollection()][object[]]$AfterRecoveryOperations = @())

    if ($Boundary.decisionKind -cne 'PrepareFileTransferRouteTransition' -or
        $Boundary.decisionStatus -cne 'Prepared' -or
        [long]$Boundary.afterStateGeneration -ne ([long]$Boundary.beforeStateGeneration + 1L) -or
        $Boundary.beforeStateHash -ceq $Boundary.afterStateHash -or
        -not (Test-FileTransferCoordinatorReceiverBarrierSourceProjection $Source $Boundary.beforeBoundary $BeforeRecoveryOperations) -or
        -not (Test-FileTransferCoordinatorReceiverBarrierSourceProjection $Source $Boundary.afterBoundary $AfterRecoveryOperations)) { return $false }

    $current = @($Boundary.operations | Where-Object {
        $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'current' -and
            $_.stateApplied -is [bool] -and $_.stateApplied
    })
    if ($current.Count -ne 1) { return $false }
    $operation = $current[0]
    $transition = $operation.routeTransition
    if ($operation.operationKind -cne 'RegularV4ToTunaV4' -or
        $operation.beforeState -cne 'None' -or $operation.afterState -cne 'Prepared' -or
        $operation.authorityId -in @('', '(none)') -or [long]$operation.operationGeneration -le 0 -or
        $operation.operationKey -cne "route_transition|current|$($operation.authorityId)|$($operation.operationGeneration)|RegularV4ToTunaV4" -or
        $operation.commandIdentity -cne $operation.authorityId -or
        [long]$operation.commandGeneration -ne [long]$operation.operationGeneration -or
        $operation.commandFingerprint -cnotmatch '^(?:[0-9a-f]{64}|fte1-[0-9a-f]{24})$' -or
        $null -eq $transition) { return $false }

    # Prepared authors a command, not a pause/drain application receipt.
    if ($operation.receiptIdentity -cne '(none)' -or [long]$operation.receiptGeneration -ne 0 -or
        $operation.receiptStatus -cne '(none)' -or $operation.receiptFingerprint -cne '(none)' -or
        [bool]$operation.receiptValidated -or @($transition.receipts).Count -ne 0 -or
        $transition.role -cne 'current' -or $transition.transitionKind -cne 'RegularV4ToTunaV4' -or
        $transition.sourceTransport -cne 'RegularNkn' -or $transition.targetTransport -cne 'Tuna' -or
        $transition.targetRoute -cne 'file_tuna_v4' -or [int]$transition.targetProtocolVersion -ne 4 -or
        $transition.targetBoundary.route -cne 'file_tuna_v4' -or [int]$transition.targetBoundary.protocolVersion -ne 4 -or
        $transition.handoffKind -cne 'NormalToTunaActivation' -or
        $transition.proofBoundaryKind -cne 'FileTransferTunaActivationTransitionProofBoundary' -or
        $transition.proofAdmissionMode -cne 'runtime_unlock_precommit' -or
        $transition.proofBoundaryFingerprint -cnotmatch '^(?:[0-9a-f]{64}|fte1-[0-9a-f]{24})$' -or
        $transition.durableCheckpointFingerprint -cnotmatch '^(?:[0-9a-f]{64}|fte1-[0-9a-f]{24})$') { return $false }

    # Its durable checkpoint is distinct from the receiver barrier's prior
    # checkpoint lease. Keep every other initial source field exact.
    $captured = $transition.sourceBoundary
    $checkpoint = [long]$captured.checkpointGeneration
    $checkpointIdentity = [string]$captured.checkpointRequestId
    $checkpointCommandIdentity = "checkpoint:$checkpoint"
    # The existing service projector transforms the source identity only;
    # the typed target retains the canonical command identity.
    $projectedCheckpointIdentity = 'fte1-' + (Get-FileTransferCoordinatorSnapshotSha256 `
        -Bytes ([Text.Encoding]::UTF8.GetBytes($checkpointCommandIdentity))).Substring(0, 24)
    return (Test-FileTransferCoordinatorSnapshotRouteTransitionSourceCurrent $captured $Source) -and
        [long]$captured.transportEpoch -eq 0 -and [long]$captured.bridgeRecoveryGeneration -eq 0 -and
        $checkpoint -gt 0 -and [long]$transition.targetBoundary.checkpointGeneration -eq $checkpoint -and
        $transition.targetBoundary.checkpointRequestId -ceq $checkpointCommandIdentity -and
        ($checkpointIdentity -ceq $checkpointCommandIdentity -or $checkpointIdentity -ceq $projectedCheckpointIdentity)
}

function Get-FileTransferCoordinatorReceiverBarrierFailures {
    param([AllowEmptyCollection()][object[]]$Boundaries, [AllowEmptyCollection()][object[]]$Effects,
        [AllowEmptyCollection()][object[]]$AggregateEntries)
    $valid = @{}
    foreach ($entry in $AggregateEntries) { $valid[[string]$entry.Boundary.boundaryId] = $true }
    $streams = @{}
    foreach ($boundary in $Boundaries) {
        $streamKey = "$($boundary.coordinatorInstanceId)|$($boundary.writer)"
        if (-not $streams.ContainsKey($streamKey)) {
            $streams[$streamKey] = @{ Current = $null; Known = @{}; Recovery = @{}; RecoverySeen = @{};
                RecoveryValid = $true; LastGeneration = 0L; LastPause = 0L; Hash = $boundary.beforeStateHash }
        }
        $stream = $streams[$streamKey]
        $operations = @($boundary.operations | Where-Object { Test-FileTransferCoordinatorReceiverBarrierMarker $_ })
        if (-not $valid.ContainsKey([string]$boundary.boundaryId) -or $stream.Hash -cne $boundary.beforeStateHash) {
            if ($operations.Count -gt 0) { "snapshot_receiver_barrier_history_invalid:$($boundary.evidenceLineIndex)" }
            continue
        }
        $beforeRecoveryOperations = @(if ($stream.RecoveryValid) { $stream.Recovery.Values })
        $nextRecovery = $stream.Recovery.Clone()
        $nextRecoverySeen = $stream.RecoverySeen.Clone()
        $recoveryValid = $stream.RecoveryValid
        $changedRecoveryKeys = @{}
        foreach ($operation in $boundary.operations) {
            if ($operation.family -cne 'receive_recovery' -or $operation.operationRole -cne 'operation_current') { continue }
            if ($operation.stateApplied -isnot [bool]) { $recoveryValid = $false; continue }
            if (-not $operation.stateApplied) { continue }
            $recoveryKey = [string]$operation.operationKey
            if ($changedRecoveryKeys.ContainsKey($recoveryKey)) { $recoveryValid = $false }
            $changedRecoveryKeys[$recoveryKey] = $true
            if ($operation.beforeState -ceq 'None') {
                if ($operation.afterState -cne 'Issued:None' -or
                    $boundary.decisionStatus -cnotin @('Accepted', 'Committed') -or
                    $nextRecoverySeen.ContainsKey([string]$operation.authorityId)) { $recoveryValid = $false }
                $nextRecoverySeen[[string]$operation.authorityId] = $true
            }
            elseif (-not $stream.Recovery.ContainsKey($recoveryKey)) { $recoveryValid = $false }
            else {
                $priorRecovery = $stream.Recovery[$recoveryKey]
                if ($operation.beforeState -cne $priorRecovery.afterState -or
                    [long]$operation.receiveRecovery.bridgeDecisionGeneration -ne [long]$priorRecovery.receiveRecovery.bridgeDecisionGeneration -or
                    -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $operation.receiveRecovery.sourceBoundary $priorRecovery.receiveRecovery.sourceBoundary) -or
                    -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $operation.receiveRecovery.effectiveBoundary $priorRecovery.receiveRecovery.effectiveBoundary)) { $recoveryValid = $false }
                foreach ($field in @('authorityId','operationGeneration','operationKind','predecessorAuthorityId',
                        'commandIdentity','commandGeneration','commandFingerprint')) {
                    if ([string]$operation.$field -cne [string]$priorRecovery.$field) { $recoveryValid = $false }
                }
            }
            if ($operation.afterState -ceq 'None') { $nextRecovery.Remove($recoveryKey) }
            else { $nextRecovery[$recoveryKey] = $operation }
        }
        if ($nextRecovery.Count -gt 1) { $recoveryValid = $false }
        $afterRecoveryOperations = @(if ($recoveryValid) { $nextRecovery.Values })
        $priorCurrent = $stream.Current
        $projectionInvalid = $false
        foreach ($operation in @($operations) + @($priorCurrent)) {
            if ($null -eq $operation) { continue }
            $source = $operation.receiverBarrier.sourceBoundary
            foreach ($side in @('before', 'after')) {
                $scope = if ($side -ceq 'before') { $boundary.beforeBoundary } else { $boundary.afterBoundary }
                $recovery = if ($side -ceq 'before') { $beforeRecoveryOperations } else { $afterRecoveryOperations }
                if ((Test-FileTransferCoordinatorReceiverBarrierSourceProjection $source $scope -StructureOnly) -and
                    -not (Test-FileTransferCoordinatorReceiverBarrierSourceProjection $source $scope @($recovery))) {
                    $projectionInvalid = $true
                }
            }
        }
        if ($projectionInvalid) {
            'snapshot_receiver_barrier_history_invalid|receiver_barrier_leg_bridge_generation_compared_with_recovery_decision_generation:' + $boundary.evidenceLineIndex
            continue
        }
        $stream.Hash = $boundary.afterStateHash
        $stream.Recovery = $nextRecovery
        $stream.RecoverySeen = $nextRecoverySeen
        $stream.RecoveryValid = $recoveryValid
        $currentRemoval = @($operations | Where-Object { $_.operationRole -ceq 'receiver_barrier_current' -and $_.afterState -ceq 'None' })
        $terminalEntry = @($operations | Where-Object { $_.operationRole -ceq 'receiver_barrier_terminal' -and $_.beforeState -ceq 'None' })
        if ($null -ne $priorCurrent -and
            -not (Test-FileTransferCoordinatorReceiverBarrierSourceProjection $priorCurrent.receiverBarrier.sourceBoundary $boundary.afterBoundary $afterRecoveryOperations) -and
            ($currentRemoval.Count -ne 1 -or $terminalEntry.Count -ne 1 -or $terminalEntry[0].afterState -cne 'Invalidated')) {
            "snapshot_receiver_barrier_source_replacement_without_invalidation:$($boundary.evidenceLineIndex)"
        }
        foreach ($operation in $operations) {
            $payload = $operation.receiverBarrier
            $role = [string]$operation.operationRole
            $key = "$($operation.authorityId)|$($operation.operationGeneration)"
            $failure = $false
            if ($role -ceq 'receiver_barrier_attempt') {
                if ($boundary.decisionStatus -cin @('Rejected','Stale','Idempotent') -and
                    ($boundary.beforeStateHash -cne $boundary.afterStateHash -or
                     @($operations | Where-Object { [bool]$_.stateApplied }).Count -ne 0)) { $failure = $true }
                if (@($Effects | Where-Object { $_.TransitionId -ceq $boundary.boundaryId -and $_.OperationKey -ceq $operation.operationKey }).Count -ne 0) { $failure = $true }
                if ($boundary.decisionStatus -cin @('Accepted','Invalidated') -and
                    @($operations | Where-Object { [bool]$_.stateApplied -and $_.authorityId -ceq $operation.authorityId -and $_.operationGeneration -eq $operation.operationGeneration }).Count -eq 0) { $failure = $true }
                if ($boundary.decisionStatus -ceq 'Idempotent' -and -not $stream.Known.ContainsKey($key)) { $failure = $true }
            }
            elseif ($role -ceq 'receiver_barrier_current' -and $operation.beforeState -ceq 'None') {
                $failure = $null -ne $priorCurrent -or $stream.Known.ContainsKey($key) -or
                    $operation.afterState -cne 'Bound' -or $boundary.decisionKind -cne 'BindInitialActivationReceiverBarrier' -or
                    $boundary.decisionStatus -cne 'Accepted' -or [long]$operation.operationGeneration -ne ($stream.LastGeneration + 1L) -or
                    [long]$payload.pauseGeneration -le $stream.LastPause -or
                    -not (Test-FileTransferCoordinatorReceiverBarrierSourceProjection $payload.sourceBoundary $boundary.beforeBoundary $beforeRecoveryOperations) -or
                    -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $boundary.beforeBoundary $boundary.afterBoundary)
                if (-not $failure) { $stream.LastGeneration = [long]$operation.operationGeneration; $stream.LastPause = [long]$payload.pauseGeneration }
            }
            elseif ($role -ceq 'receiver_barrier_terminal' -and $operation.beforeState -ceq 'None') {
                $failure = $null -eq $priorCurrent -or $currentRemoval.Count -ne 1 -or $terminalEntry.Count -ne 1
                if (-not $failure) {
                    $prior = $priorCurrent
                    $failure = $prior.authorityId -cne $operation.authorityId -or $prior.operationGeneration -ne $operation.operationGeneration
                    foreach ($field in @('pauseGeneration','negotiationFingerprint','admissionFingerprint','failureOriginFingerprint')) {
                        if ([string]$prior.receiverBarrier.$field -cne [string]$payload.$field) { $failure = $true }
                    }
                    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $prior.receiverBarrier.sourceBoundary $payload.sourceBoundary)) { $failure = $true }
                    if ($operation.afterState -ceq 'Closed') {
                        if ($prior.afterState -cne 'ClosePrepared' -or $boundary.decisionKind -cne 'CommitInitialActivationReceiverBarrierClose' -or
                            $boundary.decisionStatus -cne 'Accepted' -or $prior.commandFingerprint -cne $operation.commandFingerprint -or
                            -not (Test-FileTransferCoordinatorReceiverBarrierSourceProjection $payload.sourceBoundary $boundary.afterBoundary $afterRecoveryOperations)) { $failure = $true }
                    }
                    else {
                        $explicit = $boundary.decisionKind -ceq 'InvalidateInitialActivationReceiverBarrier' -and $boundary.decisionStatus -ceq 'Invalidated'
                        $replacement = -not (Test-FileTransferCoordinatorReceiverBarrierSourceProjection $payload.sourceBoundary $boundary.afterBoundary $afterRecoveryOperations)
                        $prepared = $operation.receiverBarrier.receipts[0].receiptReason -ceq 'receiver_barrier_source_boundary_invalidated' -and
                            (Test-FileTransferCoordinatorReceiverBarrierRoutePreparation $boundary $payload.sourceBoundary $beforeRecoveryOperations $afterRecoveryOperations)
                        $admissionReplaced = $operation.receiverBarrier.receipts[0].receiptReason -ceq 'receiver_barrier_admission_replaced' -and
                            $payload.admissionFingerprint -cne '(none)' -and $payload.invalidatingAdmissionFingerprint -cne $payload.admissionFingerprint
                        $pauseReplaced = $operation.receiverBarrier.receipts[0].receiptReason -ceq 'receiver_barrier_pause_replaced' -and
                            [long]$payload.replacementPauseGeneration -gt [long]$payload.pauseGeneration
                        if (($payload.invalidatingAdmissionFingerprint -cne '(none)' -and -not $admissionReplaced) -or
                            ([long]$payload.replacementPauseGeneration -ne 0 -and -not $pauseReplaced) -or
                            ($operation.receiverBarrier.receipts[0].receiptReason -ceq 'receiver_barrier_admission_replaced' -and -not $admissionReplaced) -or
                            ($operation.receiverBarrier.receipts[0].receiptReason -ceq 'receiver_barrier_pause_replaced' -and -not $pauseReplaced) -or
                            (-not $explicit -and -not ($boundary.decisionStatus -cin @('Accepted','Prepared','Committed') -and
                                ($replacement -or $prepared -or $admissionReplaced -or $pauseReplaced)))) { $failure = $true }
                    }
                }
            }
            else {
                $prior = if ($stream.Known.ContainsKey("$role|$key")) { $stream.Known["$role|$key"] } else { $null }
                if ($null -eq $prior -or $prior.afterState -cne $operation.beforeState) { $failure = $true }
                else {
                    foreach ($field in @('pauseGeneration','failureOriginFingerprint','invalidatingAdmissionFingerprint','replacementPauseGeneration')) {
                        if ([string]$prior.receiverBarrier.$field -cne [string]$payload.$field -and
                            -not ($field -ceq 'failureOriginFingerprint' -and $operation.beforeState -ceq 'AdmissionAttached' -and $operation.afterState -ceq 'ClosePrepared')) { $failure = $true }
                    }
                    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $prior.receiverBarrier.sourceBoundary $payload.sourceBoundary)) { $failure = $true }
                    $attach = $role -ceq 'receiver_barrier_current' -and $operation.afterState -ceq 'AdmissionAttached' -and $operation.beforeState -cin @('Bound','AdmissionAttached') -and $boundary.decisionKind -ceq 'AttachInitialActivationReceiverBarrierAdmission' -and $boundary.decisionStatus -ceq 'Accepted'
                    foreach ($field in @('negotiationFingerprint','admissionFingerprint')) {
                        if ([string]$prior.receiverBarrier.$field -cne [string]$payload.$field -and -not $attach) { $failure = $true }
                    }
                    if ($attach -and $prior.receiverBarrier.admissionFingerprint -cne '(none)' -and
                        $prior.receiverBarrier.admissionFingerprint -cne $payload.admissionFingerprint) { $failure = $true }
                    $prepare = $role -ceq 'receiver_barrier_current' -and $operation.beforeState -ceq 'AdmissionAttached' -and $operation.afterState -ceq 'ClosePrepared' -and $boundary.decisionKind -ceq 'PrepareInitialActivationReceiverBarrierClose' -and $boundary.decisionStatus -ceq 'Accepted'
                    $remove = $operation.afterState -ceq 'None' -and ($role -ceq 'receiver_barrier_terminal' -or $terminalEntry.Count -eq 1)
                    $carry = $operation.beforeState -ceq $operation.afterState
                    if (-not ($attach -or $prepare -or $remove -or $carry)) { $failure = $true }
                    if (-not ($attach -or $prepare)) {
                        foreach ($field in @('commandFingerprint','receiptFingerprint','receiptStatus')) {
                            if ([string]$prior.$field -cne [string]$operation.$field) { $failure = $true }
                        }
                    }
                }
            }
            if ($failure) {
                $reason = 'snapshot_receiver_barrier_lifecycle_invalid'
                if ($role -ceq 'receiver_barrier_terminal' -and $operation.beforeState -ceq 'None' -and
                    $operation.afterState -ceq 'Invalidated' -and $boundary.decisionKind -ceq 'PrepareFileTransferRouteTransition') {
                    $reason += '|receiver_barrier_route_preparation_invalidation_binding_invalid'
                }
                "$reason`:$($boundary.evidenceLineIndex)"
                continue
            }
            if ($role -cne 'receiver_barrier_attempt') {
                $stream.Known[$key] = $true
                $stream.Known["$role|$key"] = $operation
                if ($role -ceq 'receiver_barrier_current') { $stream.Current = if ($operation.afterState -ceq 'None') { $null } else { $operation } }
            }
            $matchingEffects = @($Effects | Where-Object { $_.TransitionId -ceq $boundary.boundaryId -and $_.OperationKey -ceq $operation.operationKey -and $_.CoordinatorInstanceId -ceq $boundary.coordinatorInstanceId })
            $needsEffect = $role -ceq 'receiver_barrier_terminal' -and $operation.beforeState -ceq 'None' -and $operation.afterState -ceq 'Closed'
            if (($needsEffect -and ($matchingEffects.Count -ne 1 -or $matchingEffects[0].EffectKind -cne 'receiver_barrier_close_applied' -or
                    -not (Test-FileTransferCoordinatorSnapshotExactEffectBinding $boundary $operation $matchingEffects[0]))) -or
                (-not $needsEffect -and $matchingEffects.Count -ne 0)) { "snapshot_receiver_barrier_effect_invalid:$($boundary.evidenceLineIndex)" }
        }
    }
}

function Test-FileTransferCoordinatorSnapshotProofPublicationTerminalTransition {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()][object[]]$PublicationOperations,
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()][object[]]$DeliveryEffects,
        [Parameter(Mandatory = $true)]$Boundary,
        [Parameter(Mandatory = $true)]
        [ValidateSet('Delivered', 'Rejected')][string]$Outcome
    )

    $receiptValidated = $Outcome -ceq 'Delivered'
    $terminalEntrants = @($PublicationOperations | Where-Object {
            [string]$_.operationRole -ceq 'proof_publication_terminal' -and
            [string]$_.beforeState -ceq 'None' -and
            [string]$_.afterState -ceq $Outcome -and
            [bool]$_.stateApplied -and
            [bool]$_.receiptValidated -eq $receiptValidated -and
            [string]$_.receiptStatus -ceq $Outcome
        })
    $pendingRetirements = @($PublicationOperations | Where-Object {
            [string]$_.operationRole -ceq 'proof_publication_pending' -and
            [string]$_.beforeState -in @('Pending', 'Held') -and
            [string]$_.afterState -ceq 'None' -and
            [bool]$_.stateApplied
        })
    $terminalRetirements = @($PublicationOperations | Where-Object {
            [string]$_.operationRole -ceq 'proof_publication_terminal' -and
            [string]$_.beforeState -in @('Delivered', 'Rejected') -and
            [string]$_.afterState -ceq 'None' -and
            [bool]$_.stateApplied
        })
    if ($terminalEntrants.Count -ne 1 -or
        $pendingRetirements.Count -ne 1 -or
        $terminalRetirements.Count -gt 1 -or
        $PublicationOperations.Count -ne
            (2 + $terminalRetirements.Count)) {
        return $false
    }

    $entrant = $terminalEntrants[0]
    $pending = $pendingRetirements[0]
    $entrantReceipts = @($entrant.receiveRecovery.receipts | Where-Object {
            [string]$_.stage -ceq 'proof_publication_terminal' -and
            [string]$_.commandIdentity -ceq
                [string]$entrant.commandIdentity -and
            [long]$_.commandGeneration -eq
                [long]$entrant.commandGeneration -and
            [string]$_.commandFingerprint -ceq
                [string]$entrant.commandFingerprint -and
            [string]$_.receiptIdentity -ceq
                [string]$entrant.receiptIdentity -and
            [long]$_.receiptGeneration -eq
                [long]$entrant.receiptGeneration -and
            [string]$_.receiptStatus -ceq
                [string]$entrant.receiptStatus -and
            [string]$_.receiptFingerprint -ceq
                [string]$entrant.receiptFingerprint -and
            [bool]$_.receiptValidated -eq
                [bool]$entrant.receiptValidated
        })
    if ([string]$pending.authorityId -cne [string]$entrant.authorityId -or
        [long]$pending.operationGeneration -ne
            [long]$entrant.operationGeneration -or
        [long]$entrant.commandGeneration -ne
            [long]$entrant.operationGeneration -or
        [long]$entrant.receiptGeneration -ne
            [long]$entrant.operationGeneration -or
        [long]$pending.commandGeneration -ne
            [long]$entrant.commandGeneration -or
        [string]$pending.commandFingerprint -cne
            [string]$entrant.commandFingerprint -or
        @($pending.receiveRecovery.receipts).Count -ne 0 -or
        $entrantReceipts.Count -ne 1 -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
            -Left $pending.receiveRecovery.sourceBoundary `
            -Right $entrant.receiveRecovery.sourceBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
            -Left $pending.receiveRecovery.effectiveBoundary `
            -Right $entrant.receiveRecovery.effectiveBoundary)) {
        return $false
    }

    if ($terminalRetirements.Count -eq 1) {
        $retired = $terminalRetirements[0]
        $retiredReceiptValidated =
            [string]$retired.beforeState -ceq 'Delivered'
        $retiredReceipts = @($retired.receiveRecovery.receipts | Where-Object {
                [string]$_.stage -ceq 'proof_publication_terminal' -and
                [string]$_.commandIdentity -ceq
                    [string]$retired.commandIdentity -and
                [long]$_.commandGeneration -eq
                    [long]$retired.commandGeneration -and
                [string]$_.commandFingerprint -ceq
                    [string]$retired.commandFingerprint -and
                [string]$_.receiptIdentity -ceq
                    [string]$retired.receiptIdentity -and
                [long]$_.receiptGeneration -eq
                    [long]$retired.receiptGeneration -and
                [string]$_.receiptStatus -ceq
                    [string]$retired.receiptStatus -and
                [string]$_.receiptFingerprint -ceq
                    [string]$retired.receiptFingerprint -and
                [bool]$_.receiptValidated -eq
                    [bool]$retired.receiptValidated
            })
        if ([string]$retired.authorityId -cne
                [string]$entrant.authorityId -or
            [long]$retired.operationGeneration + 1L -ne
                [long]$entrant.operationGeneration -or
            [long]$retired.commandGeneration -ne
                [long]$retired.operationGeneration -or
            [long]$retired.receiptGeneration -ne
                [long]$retired.operationGeneration -or
            [string]$retired.receiptStatus -cne
                [string]$retired.beforeState -or
            [bool]$retired.receiptValidated -ne
                $retiredReceiptValidated -or
            $retiredReceipts.Count -ne 1 -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                -Left $retired.receiveRecovery.sourceBoundary `
                -Right $entrant.receiveRecovery.sourceBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                -Left $retired.receiveRecovery.effectiveBoundary `
                -Right $entrant.receiveRecovery.effectiveBoundary)) {
            return $false
        }
    }

    if ($Outcome -ceq 'Rejected') {
        return $DeliveryEffects.Count -eq 0
    }

    $matchingDeliveryEffects = @($DeliveryEffects | Where-Object {
            [long]$_.StateGeneration -eq
                [long]$Boundary.afterStateGeneration -and
            [string]$_.OperationKey -ceq [string]$entrant.operationKey -and
            [string]$_.AuthorityId -ceq [string]$entrant.authorityId -and
            [long]$_.OperationGeneration -eq
                [long]$entrant.operationGeneration -and
            [string]$_.ReceiptIdentity -ceq
                [string]$entrant.receiptIdentity -and
            [long]$_.ReceiptGeneration -eq
                [long]$entrant.receiptGeneration -and
            [string]$_.ReceiptStatus -ceq
                [string]$entrant.receiptStatus -and
            [string]$_.ReceiptFingerprint -ceq
                [string]$entrant.receiptFingerprint -and
            (Test-FileTransferCoordinatorSnapshotEffectScope `
                -Operation $entrant -Effect $_)
        })
    return $DeliveryEffects.Count -eq 1 -and
        $matchingDeliveryEffects.Count -eq 1
}

function Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint {
    param(
        [Parameter(Mandatory = $true)]$Left,
        [Parameter(Mandatory = $true)]$Right
    )

    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Left) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Right)) {
        return $false
    }
    foreach ($name in @(
            'route', 'protocolVersion', 'liveRouteEpoch',
            'routePublicationGeneration', 'routeCommitGeneration',
            'runtimeGeneration', 'terminalGeneration', 'legGeneration',
            'transportEpoch', 'bridgeRecoveryGeneration',
            'dataSessionIdentity', 'dataSessionGeneration')) {
        $leftValue = Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $Left -Name $name -Default $null
        $rightValue = Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $Right -Name $name -Default $null
        if ([string]$leftValue -cne [string]$rightValue) {
            return $false
        }
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotFallbackCheckpointScopeMutation {
    param([Parameter(Mandatory = $true)]$Envelope)

    $decisionKind = [string]$Envelope.decisionKind
    if ($decisionKind -notin @(
            'FallbackCheckpointRequested',
            'FallbackCheckpointAccepted') -or
        [string]$Envelope.decisionStatus -cne 'Accepted' -or
        [string]::IsNullOrWhiteSpace([string]$Envelope.decisionReason) -or
        $null -eq $Envelope.aggregate) {
        return $false
    }

    $aggregate = $Envelope.aggregate
    $beforeBoundary = $aggregate.beforeBoundary
    $afterBoundary = $aggregate.afterBoundary
    if ([long]$aggregate.afterStateGeneration -ne
            [long]$aggregate.beforeStateGeneration + 1L -or
        [string]::IsNullOrWhiteSpace(
            [string]$aggregate.beforeStateFingerprint) -or
        [string]::IsNullOrWhiteSpace(
            [string]$aggregate.afterStateFingerprint) -or
        [string]$aggregate.beforeStateFingerprint -ceq
            [string]$aggregate.afterStateFingerprint -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint `
            -Left $beforeBoundary -Right $afterBoundary) -or
        [string]$beforeBoundary.route -cne 'post_tuna_fallback_v6' -or
        [int]$beforeBoundary.protocolVersion -ne 6) {
        return $false
    }

    $beforeCheckpointId = [string]$beforeBoundary.checkpointRequestId
    $afterCheckpointId = [string]$afterBoundary.checkpointRequestId
    $beforeCheckpointGeneration =
        [long]$beforeBoundary.checkpointGeneration
    $afterCheckpointGeneration =
        [long]$afterBoundary.checkpointGeneration
    if ($decisionKind -ceq 'FallbackCheckpointRequested') {
        return $beforeCheckpointGeneration -eq 0 -and
            $beforeCheckpointId -ceq '(none)' -and
            $afterCheckpointGeneration -gt 0 -and
            $afterCheckpointId -notin @('', '(none)')
    }

    return $beforeCheckpointGeneration -gt 0 -and
        $beforeCheckpointId -notin @('', '(none)') -and
        $afterCheckpointGeneration -eq 0 -and
        $afterCheckpointId -ceq '(none)'
}

function Test-FileTransferCoordinatorSnapshotCheckpointResponderCarry {
    param($Envelope, $Operation)

    if (-not (Test-FileTransferCoordinatorSnapshotFallbackCheckpointScopeMutation $Envelope) -or
        $Operation.family -cne 'responder_probe_handoff' -or $Operation.operationRole -cne 'handoff_terminal' -or
        $Operation.operationKind -cne 'ResponderPeerProbeRouteTransitionHandoff' -or
        $Operation.beforeState -cne 'Invalidated' -or $Operation.afterState -cne 'Invalidated' -or
        $Operation.stateApplied -isnot [bool] -or -not $Operation.stateApplied -or
        $Operation.receiptValidated -isnot [bool] -or -not $Operation.receiptValidated -or
        $Operation.receiptStatus -cne 'Invalidated' -or [long]$Operation.operationGeneration -le 0 -or
        [string]$Operation.authorityId -in @('', '(none)') -or
        $Operation.operationKey -cne "responder_probe_handoff|handoff_terminal|$($Operation.authorityId)|$($Operation.operationGeneration)|ResponderPeerProbeRouteTransitionHandoff" -or
        $null -eq $Operation.responderProbeHandoff) { return $false }
    $payload = $Operation.responderProbeHandoff
    foreach ($name in @('receipts','role','transitionMode','handoffAuthorityId','handoffGeneration',
            'linkedTransitionAuthorityId','linkedTransitionGeneration','probeState','probeTransport','decisionStatus','decisionReason',
            'sourceBoundary','effectiveBoundary','targetBoundary','transactionGeneration','offerGeneration','pendingAnswerAckGeneration',
            'payerDecisionId','launchGeneration','tunaPathLeaseGeneration','probeAckedUtcMs','admissionGeneration',
            'pendingPreCommitGeneration','pairedReleaseGeneration','pairingGeneration','tunaPathLeaseListenerRunId',
            'ackMessageId','ackSourceFingerprint','peerProbeFrameFingerprint','probeId','ownerKey','pairKey',
            'pairedReleaseAuthorityId','localProofIdentity','localProofFingerprint','peerProofIdentity','peerProofFingerprint')) {
        if ($payload.PSObject.Properties.Name -notcontains $name) { return $false }
    }
    $receipts = @($payload.receipts)
    if (@($Envelope.aggregate.operations | Where-Object {
            $_.family -ceq 'responder_probe_handoff' -and
            $_.operationRole -ceq 'handoff_terminal' -and
            $_.operationKind -ceq 'ResponderPeerProbeRouteTransitionHandoff'
        }).Count -ne 1 -or
        $payload.role -cne 'handoff_terminal' -or $payload.transitionMode -cne 'post_fallback_v6_paired' -or
        $payload.handoffAuthorityId -cne $Operation.authorityId -or
        [long]$payload.handoffGeneration -ne [long]$Operation.operationGeneration -or
        $payload.linkedTransitionAuthorityId -cne '(none)' -or [long]$payload.linkedTransitionGeneration -ne 0 -or
        $payload.probeState -cne 'Acked' -or $payload.probeTransport -cne 'Tuna' -or
        $payload.decisionStatus -cne 'Invalidated' -or $receipts.Count -ne 1 -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $payload.sourceBoundary) -or
        $payload.sourceBoundary.route -cne 'post_tuna_fallback_v6' -or [int]$payload.sourceBoundary.protocolVersion -ne 6 -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $payload.effectiveBoundary $Envelope.aggregate.afterBoundary) -or
        $payload.sourceBoundary.dataSessionIdentity -cne $payload.effectiveBoundary.dataSessionIdentity -or
        [long]$payload.sourceBoundary.dataSessionGeneration -ne [long]$payload.effectiveBoundary.dataSessionGeneration) { return $false }
    $target = $payload.effectiveBoundary | Select-Object *
    $target.route = 'file_tuna_v4'; $target.protocolVersion = 4
    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $payload.targetBoundary $target)) { return $false }
    foreach ($field in @('transactionGeneration','offerGeneration','pendingAnswerAckGeneration','payerDecisionId',
            'launchGeneration','tunaPathLeaseGeneration','probeAckedUtcMs','admissionGeneration','pendingPreCommitGeneration',
            'pairedReleaseGeneration','pairingGeneration')) {
        if ([long]$payload.$field -le 0) { return $false }
    }
    foreach ($field in @('tunaPathLeaseListenerRunId','ackMessageId','ackSourceFingerprint','peerProbeFrameFingerprint',
            'probeId','ownerKey','pairKey','pairedReleaseAuthorityId','localProofIdentity','localProofFingerprint',
            'peerProofIdentity','peerProofFingerprint')) {
        if ([string]::IsNullOrWhiteSpace([string]$payload.$field) -or $payload.$field -ceq '(none)') { return $false }
    }
    $receipt = $receipts[0]
    foreach ($name in @('stage','commandIdentity','commandGeneration','commandFingerprint','receiptIdentity',
            'receiptGeneration','receiptStatus','receiptFingerprint','receiptValidated','receiptReason')) {
        if ($receipt.PSObject.Properties.Name -notcontains $name) { return $false }
    }
    if ($receipt.stage -cne 'handoff_invalidation' -or $receipt.receiptStatus -cne 'Invalidated' -or
        $receipt.receiptValidated -isnot [bool] -or -not $receipt.receiptValidated -or
        $receipt.receiptReason -cne $payload.decisionReason -or
        [string]::IsNullOrWhiteSpace([string]$receipt.receiptReason) -or
        [long]$receipt.commandGeneration -ne [long]$Operation.operationGeneration -or
        [long]$receipt.receiptGeneration -ne [long]$Operation.operationGeneration) { return $false }
    foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity',
            'receiptGeneration','receiptStatus','receiptFingerprint','receiptValidated')) {
        if ([string]$Operation.$field -cne [string]$receipt.$field -or
            [string]::IsNullOrWhiteSpace([string]$receipt.$field) -or [string]$receipt.$field -ceq '(none)') { return $false }
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotFailedCheckpointYieldCandidate {
    param([Parameter(Mandatory = $true)]$Envelope)

    if ([string]$Envelope.decisionKind -cne 'FallbackCheckpointRequested' -or
        -not (Test-FileTransferCoordinatorSnapshotFallbackCheckpointScopeMutation $Envelope)) { return $false }
    $operations = @($Envelope.aggregate.operations | Where-Object family -CEQ 'fallback_tail_yield')
    foreach ($operation in $operations) {
        foreach ($field in @('operationRole','operationKind','beforeState','afterState','authorityId','operationGeneration',
                'operationKey','predecessorAuthorityId','stateApplied','commandIdentity','commandGeneration','commandFingerprint',
                'receiptIdentity','receiptGeneration','receiptStatus','receiptFingerprint','receiptValidated','fallbackTailYield')) {
            if ($operation.PSObject.Properties.Name -notcontains $field) { return $false }
        }
    }
    $current = @($operations | Where-Object {
            $_.operationRole -ceq 'yield_current' -and $_.operationKind -ceq 'Failed' -and
            $_.beforeState -ceq 'PeerProofObserved' -and $_.afterState -ceq 'None'
        })
    $entered = @($operations | Where-Object {
            $_.operationRole -ceq 'yield_terminal' -and $_.operationKind -ceq 'Failed' -and
            $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Invalidated'
        })
    $retired = @($operations | Where-Object {
            $_.operationRole -ceq 'yield_terminal' -and $_.operationKind -ceq 'Quiescent' -and
            $_.beforeState -ceq 'Invalidated' -and $_.afterState -ceq 'None'
        })
    if ($current.Count -ne 1 -or $entered.Count -ne 1 -or $retired.Count -gt 1 -or
        $operations.Count -ne (2 + $retired.Count)) { return $false }
    $yield = $current[0]; $terminal = $entered[0]
    if ($yield.authorityId -cne $terminal.authorityId -or
        [long]$yield.operationGeneration -ne [long]$terminal.operationGeneration) { return $false }
    foreach ($operation in $operations) {
        $typed = $operation.fallbackTailYield
        if ($null -eq $typed) { return $false }
        foreach ($field in @('role','yieldKind','pairingState','ownerKey','pairKey','localProofIdentity','localProofFingerprint',
                'peerProofAuthorityId','peerProofIdentity','peerProofFingerprint','peerProofYieldGeneration',
                'pairedReleaseAuthorityId','pairedReleaseGeneration','pairedReleaseOutcome','pairingGeneration',
                'failedPredecessorAuthorityId','failedSupersessionCarriedPeerProof','invalidationReason',
                'reservedRetryAuthorityId','reservationGeneration','consumingInstallGeneration','retryHoldGeneration',
                'retryRecheckGeneration','reauthorizationGeneration','reauthorizationSourceTransportEpoch',
                'reauthorizationTargetTransportEpoch','sourceBoundary','effectiveBoundary','targetBoundary','receipts')) {
            if ($typed.PSObject.Properties.Name -notcontains $field) { return $false }
        }
        if ($null -eq $typed.sourceBoundary -or $null -eq $typed.effectiveBoundary -or
            $null -eq $typed.targetBoundary) { return $false }
        if ($null -eq $typed -or $operation.stateApplied -isnot [bool] -or -not $operation.stateApplied -or
            $operation.receiptValidated -isnot [bool] -or -not $operation.receiptValidated -or
            [string]$operation.authorityId -in @('', '(none)') -or [long]$operation.operationGeneration -le 0 -or
            $operation.operationKey -cne "fallback_tail_yield|$($operation.operationRole)|$($operation.authorityId)|$($operation.operationGeneration)|$($operation.operationKind)" -or
            $typed.role -cne $operation.operationRole -or $typed.yieldKind -cne $operation.operationKind -or
            -not (Test-FileTransferCoordinatorSnapshotFallbackTailOperationScope $Envelope $operation)) { return $false }
        foreach ($receipt in @($typed.receipts)) {
            foreach ($field in @('stage','commandIdentity','commandGeneration','commandFingerprint','receiptIdentity',
                    'receiptGeneration','receiptStatus','receiptReason','receiptFingerprint','receiptValidated')) {
                if ($null -eq $receipt -or $receipt.PSObject.Properties.Name -notcontains $field -or
                    [string]::IsNullOrWhiteSpace([string]$receipt.$field) -or [string]$receipt.$field -ceq '(none)') { return $false }
            }
            if ($receipt.receiptValidated -isnot [bool] -or -not $receipt.receiptValidated -or
                [long]$receipt.commandGeneration -le 0 -or [long]$receipt.receiptGeneration -le 0) { return $false }
        }
        if (@($typed.receipts).Count -eq 0 -or
            @($typed.receipts | Group-Object stage | Where-Object Count -GT 1).Count -ne 0) { return $false }
    }
    foreach ($operation in @($yield, $terminal)) {
        $typed = $operation.fallbackTailYield
        if ($operation.predecessorAuthorityId -cne '(none)' -or
            $typed.failedSupersessionCarriedPeerProof -isnot [bool] -or $typed.failedSupersessionCarriedPeerProof) { return $false }
        foreach ($field in @('localProofIdentity','localProofFingerprint','pairedReleaseAuthorityId',
                'pairedReleaseOutcome','reservedRetryAuthorityId','failedPredecessorAuthorityId')) {
            if ([string]$typed.$field -cne '(none)') { return $false }
        }
        foreach ($field in @('pairingGeneration','pairedReleaseGeneration','reauthorizationGeneration',
                'reservationGeneration','consumingInstallGeneration','retryHoldGeneration','retryRecheckGeneration')) {
            if ($typed.PSObject.Properties.Name -notcontains $field -or [long]$typed.$field -ne 0) { return $false }
        }
        foreach ($field in @('ownerKey','pairKey','peerProofAuthorityId','peerProofIdentity','peerProofFingerprint')) {
            if ([string]::IsNullOrWhiteSpace([string]$typed.$field) -or [string]$typed.$field -ceq '(none)') { return $false }
        }
        if ([long]$typed.peerProofYieldGeneration -le 0 -or
            [long]$typed.reauthorizationSourceTransportEpoch -ne [long]$typed.sourceBoundary.transportEpoch -or
            [long]$typed.reauthorizationTargetTransportEpoch -ne [long]$typed.sourceBoundary.transportEpoch) { return $false }
    }
    $currentTyped = $yield.fallbackTailYield; $terminalTyped = $terminal.fallbackTailYield
    if ($currentTyped.pairingState -cne 'PeerProofObserved' -or $currentTyped.invalidationReason -cne '(none)' -or
        $terminalTyped.pairingState -cne 'Invalidated' -or $terminalTyped.invalidationReason -cne 'fallback_checkpoint_requested' -or
        @($currentTyped.receipts).Count -ne 2 -or @($terminalTyped.receipts).Count -ne 3 -or
        ($currentTyped | Select-Object * -ExcludeProperty role,pairingState,invalidationReason,receipts | ConvertTo-Json -Depth 64 -Compress) -cne
        ($terminalTyped | Select-Object * -ExcludeProperty role,pairingState,invalidationReason,receipts | ConvertTo-Json -Depth 64 -Compress)) { return $false }
    foreach ($stage in @('yield', 'peer_proof')) {
        $before = @($currentTyped.receipts | Where-Object stage -CEQ $stage)
        $after = @($terminalTyped.receipts | Where-Object stage -CEQ $stage)
        if ($before.Count -ne 1 -or $after.Count -ne 1 -or
            ($before[0] | ConvertTo-Json -Depth 16 -Compress) -cne ($after[0] | ConvertTo-Json -Depth 16 -Compress)) { return $false }
        $generation = if ($stage -ceq 'yield') { [long]$yield.operationGeneration } else { [long]$currentTyped.peerProofYieldGeneration }
        $status = if ($stage -ceq 'yield') { 'Accepted' } else { 'Observed' }
        if ([long]$before[0].commandGeneration -ne $generation -or [long]$before[0].receiptGeneration -ne $generation -or
            $before[0].receiptStatus -cne $status) { return $false }
        if ($stage -ceq 'peer_proof' -and
            ($before[0].receiptIdentity -cne $currentTyped.peerProofIdentity -or
             $before[0].receiptReason -cne 'fallback_tail_peer_proof_observed')) { return $false }
    }
    foreach ($operation in @($yield, $terminal) + @($retired)) {
        $stage = if ($operation.operationRole -ceq 'yield_current') { 'peer_proof' } else { 'invalidation' }
        $receipts = @($operation.fallbackTailYield.receipts | Where-Object stage -CEQ $stage)
        if ($receipts.Count -ne 1) { return $false }
        foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity',
                'receiptGeneration','receiptStatus','receiptFingerprint','receiptValidated')) {
            if ([string]$operation.$field -cne [string]$receipts[0].$field) { return $false }
        }
        if ($stage -ceq 'invalidation' -and
            ($receipts[0].receiptStatus -cne 'Invalidated' -or
             $receipts[0].receiptReason -cne $operation.fallbackTailYield.invalidationReason -or
             [long]$receipts[0].commandGeneration -ne [long]$operation.operationGeneration -or
             [long]$receipts[0].receiptGeneration -ne [long]$operation.operationGeneration)) { return $false }
    }
    return $retired.Count -eq 0 -or
        ($retired[0].authorityId -cne $yield.authorityId -and
         [long]$retired[0].operationGeneration -lt [long]$yield.operationGeneration -and
         $retired[0].fallbackTailYield.pairingState -ceq 'Invalidated')
}

function Test-FileTransferCoordinatorSnapshotFallbackCheckpointOperation {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        [Parameter(Mandatory = $true)]$Operation
    )

    if (-not (Test-FileTransferCoordinatorSnapshotFallbackCheckpointScopeMutation `
            -Envelope $Envelope) -or
        -not [bool]($Operation.stateApplied)) {
        return $false
    }

    $family = [string]$Operation.family
    $role = [string]$Operation.operationRole
    $kind = [string]$Operation.operationKind
    $beforeState = [string]$Operation.beforeState
    $afterState = [string]$Operation.afterState
    switch ($family) {
        'responder_probe_handoff' {
            return Test-FileTransferCoordinatorSnapshotCheckpointResponderCarry $Envelope $Operation
        }
        'route_transition' {
            return [bool]($Operation.receiptValidated) -and
                ((($role -ceq 'terminal') -and
                  ($kind -ceq 'TunaV4ToPostTunaFallbackV6') -and
                  ($beforeState -ceq 'Committed') -and
                  ($afterState -ceq 'Committed')) -or
                 (($role -ceq 'fallback_v6_bootstrap') -and
                  ($kind -ceq 'FallbackV6ProofBootstrap') -and
                  ($beforeState -ceq 'Delivered') -and
                  ($afterState -ceq 'Delivered')))
        }
        'receive_recovery' {
            if ($role -ceq 'initial_activation_envelope_refresh') {
                if ($kind -cne 'InitialActivationEnvelopeRefresh') {
                    return $false
                }
                $refreshOperations = @(
                    $Envelope.aggregate.operations | Where-Object {
                        [string]$_.family -ceq 'receive_recovery' -and
                        [string]$_.operationRole -ceq
                            'initial_activation_envelope_refresh'
                    })
                if ($refreshOperations.Count -ne 1) {
                    return $false
                }
                if (($beforeState -ceq 'Pending') -and
                    ($afterState -ceq 'Pending')) {
                    return ([string]$Operation.receiptStatus -ceq 'Pending') -and
                        -not [bool]($Operation.receiptValidated)
                }
                if (($beforeState -cne 'Committed') -or
                    ($afterState -cne 'Committed') -or
                    ([string]$Operation.receiptStatus -cne 'Accepted') -or
                    -not [bool]($Operation.receiptValidated) -or
                    $null -eq $Operation.receiveRecovery) {
                    return $false
                }

                $receipts = @($Operation.receiveRecovery.receipts)
                if ($receipts.Count -ne 1) {
                    return $false
                }
                $receipt = $receipts[0]
                $sourceBoundary = $Operation.receiveRecovery.sourceBoundary
                $effectiveBoundary =
                    $Operation.receiveRecovery.effectiveBoundary
                $aggregateBoundary = $Envelope.aggregate.afterBoundary
                return [string]$Operation.receiveRecovery.role -ceq $role -and
                    [long]$Operation.receiveRecovery.envelopeRefreshGeneration `
                        -eq [long]$Operation.operationGeneration -and
                    [long]$Operation.commandGeneration -eq
                        [long]$Operation.operationGeneration -and
                    [long]$Operation.receiptGeneration -eq
                        [long]$Operation.operationGeneration -and
                    [string]$receipt.stage -ceq
                        'initial_activation_envelope_refresh' -and
                    [string]$receipt.commandIdentity -ceq
                        [string]$Operation.commandIdentity -and
                    [long]$receipt.commandGeneration -eq
                        [long]$Operation.commandGeneration -and
                    [string]$receipt.commandFingerprint -ceq
                        [string]$Operation.commandFingerprint -and
                    [string]$receipt.receiptIdentity -ceq
                        [string]$Operation.receiptIdentity -and
                    [long]$receipt.receiptGeneration -eq
                        [long]$Operation.receiptGeneration -and
                    [string]$receipt.receiptStatus -ceq 'Accepted' -and
                    [string]$receipt.receiptStatus -ceq
                        [string]$Operation.receiptStatus -and
                    [string]$receipt.receiptFingerprint -ceq
                        [string]$Operation.receiptFingerprint -and
                    [bool]$receipt.receiptValidated -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                        -Boundary $sourceBoundary) -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                        -Left $sourceBoundary -Right $effectiveBoundary) -and
                    [string]$sourceBoundary.dataSessionIdentity -ceq
                        [string]$aggregateBoundary.dataSessionIdentity -and
                    [long]$sourceBoundary.dataSessionGeneration -eq
                        [long]$aggregateBoundary.dataSessionGeneration
            }
            return [bool]($Operation.receiptValidated) -and
                ((($role -ceq 'initial_activation_hold_terminal') -and
                  ($kind -ceq 'InitialActivationHold') -and
                  ($beforeState -ceq 'Released') -and
                  ($afterState -ceq 'Released')) -or
                 (($role -ceq 'operation_terminal') -and
                  ($kind -ceq 'BridgeReceiveStallRecovery') -and
                  ($beforeState -ceq 'Invalidated:None') -and
                  ($afterState -ceq 'Invalidated:None')))
        }
        'fallback_tail_yield' {
            if (Test-FileTransferCoordinatorSnapshotFailedCheckpointYieldCandidate $Envelope) { return $true }
            $related = @($Envelope.aggregate.operations | Where-Object {
                    [string]$_.family -ceq 'fallback_tail_yield' -and
                    [string]$_.authorityId -ceq
                        [string]$Operation.authorityId -and
                    [long]$_.operationGeneration -eq
                        [long]$Operation.operationGeneration -and
                    [string]$_.operationKind -ceq $kind
                })
            if ($kind -cne 'Quiescent' -or $related.Count -ne 2) {
                return $false
            }
            $current = @($related | Where-Object {
                    [string]$_.operationRole -ceq 'yield_current' -and
                    [string]$_.beforeState -ceq 'LocalProofObserved' -and
                    [string]$_.afterState -ceq 'None' -and
                    [bool]($_.receiptValidated)
                })
            $terminal = @($related | Where-Object {
                    [string]$_.operationRole -ceq 'yield_terminal' -and
                    [string]$_.beforeState -ceq 'None' -and
                    [string]$_.afterState -ceq 'Invalidated' -and
                    [bool]($_.receiptValidated) -and
                    [string]$_.fallbackTailYield.invalidationReason -ceq
                        'fallback_checkpoint_requested'
                })
            return $current.Count -eq 1 -and $terminal.Count -eq 1
        }
        'regular_v4_frontier_repair_pressure' {
            return [bool]($Operation.receiptValidated) -and
                ((($role -ceq 'pressure_settlement_terminal') -and
                  ($kind -ceq 'PressureSettlement') -and
                  ($beforeState -ceq 'Applied') -and
                  ($afterState -ceq 'Applied')) -or
                 (($role -ceq 'repair_authority_terminal') -and
                  ($kind -ceq 'PressureSettlement') -and
                  ($beforeState -ceq 'Applied') -and
                  ($afterState -ceq 'Applied')) -or
                 (($role -ceq 'recovery_handoff_terminal') -and
                  ($kind -ceq 'PressureRecoveryHandoff') -and
                  ($beforeState -ceq 'Committed') -and
                  ($afterState -ceq 'Committed')))
        }
        default { return $false }
    }
}

function Get-FileTransferCoordinatorSnapshotCheckpointCatalogVariant {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        $Operation = $null
    )

    if ([string]$Envelope.decisionKind -in @(
            'FallbackCheckpointRequested',
            'FallbackCheckpointAccepted')) {
        if ($null -ne $Operation -and $Operation.family -ceq 'fallback_tail_yield' -and
            @($Envelope.aggregate.operations | Where-Object {
                    $_.family -ceq 'fallback_tail_yield' -and $_.operationKind -ceq 'Failed'
                }).Count -gt 0) {
            return 'checkpoint_failed_peer_first_invalidation_terminal_rollover_omitted_from_catalog'
        }
        if ($null -ne $Operation -and $Operation.family -ceq 'responder_probe_handoff') {
            return 'checkpoint_scope_catalog_omitted_retained_invalidated_responder_handoff'
        }
        if ($null -ne $Operation -and
            (Test-FileTransferCoordinatorSnapshotFallbackCheckpointScopeMutation `
                -Envelope $Envelope) -and
            [string]$Operation.family -ceq 'receive_recovery' -and
            [string]$Operation.operationRole -ceq
                'initial_activation_envelope_refresh' -and
            [string]$Operation.operationKind -ceq
                'InitialActivationEnvelopeRefresh' -and
            ([string]$Operation.beforeState -ceq 'Committed' -or
             [string]$Operation.afterState -ceq 'Committed')) {
            return 'fallback_checkpoint_committed_initial_activation_envelope_refresh_carry_invalid'
        }
        return 'fallback_checkpoint_scope_transition_omitted_from_cross_family_catalog'
    }
    return '(none)'
}

function Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptBridgeRecovery {
    param($Left, $Right)

    if ($null -eq $Left -or $null -eq $Right -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $Left) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $Right)) { return $false }
    $comparison = $Right | Select-Object *
    $comparison.bridgeRecoveryGeneration = $Left.bridgeRecoveryGeneration
    return Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $Left $comparison
}

function Test-FileTransferCoordinatorSnapshotReceiveRecoveryRouteCarry {
    param($Envelope, $Operation)

    $decision = [string]$Envelope.decisionKind
    $aggregate = $Envelope.aggregate
    $before = $aggregate.beforeBoundary; $after = $aggregate.afterBoundary
    if ($decision -notin @('IssueReceiveRecoveryOperation', 'ObserveReceiveRecoveryProof') -or
        [string]$Envelope.decisionStatus -cne 'Accepted' -or
        [long]$aggregate.afterStateGeneration -ne ([long]$aggregate.beforeStateGeneration + 1L) -or
        [string]$aggregate.beforeStateFingerprint -ceq [string]$aggregate.afterStateFingerprint -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptBridgeRecovery $before $after) -or
        [string]$after.route -cne 'post_tuna_fallback_v6' -or [long]$after.protocolVersion -ne 6 -or
        [long]$after.terminalGeneration -ne 0 -or
        [string]$Operation.family -cne 'route_transition') { return $false }

    $routes = @($aggregate.operations | Where-Object {
        $_.family -in @('route_runtime', 'route_transition') -and
        -not (Test-FileTransferCoordinatorCheckpointRepairContinuityMarker $_)
    })
    if ($routes.Count -ne 2 -or
        @($routes | Where-Object operationRole -CEQ 'terminal').Count -ne 1 -or
        @($routes | Where-Object operationRole -CEQ 'fallback_v6_bootstrap').Count -ne 1) { return $false }
    foreach ($route in $routes) {
        $typed = $route.routeTransition
        $bootstrap = [string]$route.operationRole -ceq 'fallback_v6_bootstrap'
        $kind = if ($bootstrap) { 'FallbackV6ProofBootstrap' } else { 'TunaV4ToPostTunaFallbackV6' }
        $state = if ($bootstrap) { 'Delivered' } else { 'Committed' }
        $stage = if ($bootstrap) { 'fallback_v6_bootstrap' } else { 'disposition' }
        if ($route.family -cne 'route_transition' -or $null -eq $typed -or
            $typed.role -cne $route.operationRole -or $route.operationKind -cne $kind -or
            $route.beforeState -cne $state -or $route.afterState -cne $state -or
            $route.stateApplied -isnot [bool] -or -not $route.stateApplied -or
            $route.receiptValidated -isnot [bool] -or -not $route.receiptValidated -or
            $route.authorityId -in @('', '(none)') -or [long]$route.operationGeneration -le 0 -or
            $route.operationKey -cne "route_transition|$($route.operationRole)|$($route.authorityId)|$($route.operationGeneration)|$kind" -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $typed.sourceBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $typed.targetBoundary) -or
            $typed.transitionKind -cne 'TunaV4ToPostTunaFallbackV6' -or
            $typed.targetRoute -cne 'post_tuna_fallback_v6' -or [long]$typed.targetProtocolVersion -ne 6 -or
            $typed.targetTransport -cne 'RegularNkn' -or $typed.sourceTransport -cne 'Tuna' -or
            $typed.targetBoundary.route -cne 'post_tuna_fallback_v6' -or [long]$typed.targetBoundary.protocolVersion -ne 6 -or
            [long]$typed.targetBoundary.bridgeRecoveryGeneration -ne [long]$after.bridgeRecoveryGeneration) { return $false }
        if ($bootstrap) {
            if (@($typed.receipts).Count -ne 1 -or
                -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $typed.sourceBoundary $after)) { return $false }
        }
        # The retained target has its own exact identity representation. Its
        # complete tuple is checked against indexed target history below; never
        # alias that identity to the aggregate's evidence projection.
        if (@($typed.receipts).Count -eq 0 -or
            @($typed.receipts | ForEach-Object { $_.stage } | Sort-Object -Unique).Count -ne @($typed.receipts).Count) { return $false }
        foreach ($receipt in @($typed.receipts)) {
            if ($receipt.receiptValidated -isnot [bool] -or -not $receipt.receiptValidated -or
                [long]$receipt.commandGeneration -ne [long]$route.operationGeneration -or
                [long]$receipt.receiptGeneration -ne [long]$route.operationGeneration) { return $false }
            foreach ($field in @('commandIdentity','commandFingerprint','receiptIdentity','receiptFingerprint')) {
                if ([string]$receipt.$field -in @('', '(none)')) { return $false }
            }
        }
        $receipts = @($typed.receipts | Where-Object stage -CEQ $stage)
        if ($receipts.Count -ne 1 -or -not $receipts[0].receiptValidated -or
            $receipts[0].receiptStatus -notin $(if ($bootstrap) { @('Delivered') } else { @('Applied', 'Idempotent') })) { return $false }
        foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity',
                'receiptGeneration','receiptStatus','receiptFingerprint')) {
            if ([string]$route.$field -cne [string]$receipts[0].$field -or
                [string]$route.$field -in @('', '(none)')) { return $false }
        }
        if ([long]$route.commandGeneration -le 0 -or [long]$route.receiptGeneration -le 0) { return $false }
    }
    if (@($routes | Where-Object operationKey -CEQ $Operation.operationKey).Count -ne 1) { return $false }

    $recovery = @($aggregate.operations | Where-Object {
        $_.family -ceq 'receive_recovery' -and $_.operationRole -in @('operation_current', 'operation_terminal')
    })
    $current = @($recovery | Where-Object operationRole -CEQ 'operation_current')
    if ($current.Count -ne 1 -or $null -eq $current[0].receiveRecovery) { return $false }
    $subject = $current[0]; $typed = $subject.receiveRecovery
    if ($subject.operationKind -cne 'BridgeReceiveStallRecovery' -or
        $typed.recoveryKind -cne 'BridgeReceiveStallRecovery' -or $typed.role -cne 'operation_current' -or
        $subject.stateApplied -isnot [bool] -or -not $subject.stateApplied -or $subject.authorityId -in @('', '(none)') -or
        [long]$subject.operationGeneration -le 0 -or [long]$typed.bridgeDecisionGeneration -le 0 -or
        $subject.commandIdentity -cne $subject.authorityId -or
        [long]$subject.commandGeneration -ne [long]$subject.operationGeneration -or
        $subject.commandFingerprint -in @('', '(none)') -or
        $subject.operationKey -cne "receive_recovery|operation_current|$($subject.authorityId)|$($subject.operationGeneration)|BridgeReceiveStallRecovery" -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $typed.sourceBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $typed.effectiveBoundary)) { return $false }
    # Recovery authority retains its own transport/checkpoint scope, not the
    # aggregate's currently installed transport or cleared checkpoint lease.
    foreach ($scope in @($typed.sourceBoundary, $typed.effectiveBoundary)) {
        foreach ($field in @('route','protocolVersion','liveRouteEpoch','routePublicationGeneration',
                'routeCommitGeneration','runtimeGeneration','terminalGeneration','legGeneration',
                'dataSessionIdentity','dataSessionGeneration')) {
            if ([string]$scope.$field -cne [string]$after.$field) { return $false }
        }
    }
    if ($decision -ceq 'IssueReceiveRecoveryOperation') {
        $attempts = @($aggregate.operations | Where-Object {
            $_.family -ceq 'receive_recovery' -and $_.operationRole -ceq 'attempted_decision'
        })
        if ($Envelope.decisionReason -cne 'filetransfer_receive_recovery_operation_issued' -or
            $recovery.Count -ne 1 -or $attempts.Count -ne 1 -or
            [long]$before.bridgeRecoveryGeneration -ne 0 -or
            [long]$after.bridgeRecoveryGeneration -ne [long]$typed.bridgeDecisionGeneration -or
            $subject.beforeState -cne 'None' -or $subject.afterState -cne 'Issued:None' -or
            $typed.executionState -cne 'Issued' -or $typed.closureState -cne 'None' -or
            $subject.receiptStatus -cne 'Pending' -or $subject.receiptIdentity -cne '(none)' -or
            [long]$subject.receiptGeneration -ne 0 -or $subject.receiptFingerprint -cne '(none)' -or
            $subject.receiptValidated -isnot [bool] -or $subject.receiptValidated -or @($typed.receipts).Count -ne 0) { return $false }
        $attempt = $attempts[0]; $attemptTyped = $attempt.receiveRecovery
        if ($null -eq $attemptTyped -or $attempt.operationKind -cne $decision -or
            $attempt.authorityId -cne $subject.authorityId -or
            [long]$attempt.operationGeneration -ne [long]$subject.operationGeneration -or
            $attempt.stateApplied -isnot [bool] -or $attempt.stateApplied -or $attempt.beforeState -cne 'None' -or $attempt.afterState -cne 'None' -or
            $attemptTyped.role -cne 'attempted_decision' -or $attemptTyped.recoveryKind -cne $typed.recoveryKind -or
            [long]$attemptTyped.bridgeDecisionGeneration -ne [long]$typed.bridgeDecisionGeneration -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $attemptTyped.sourceBoundary $typed.sourceBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $attemptTyped.effectiveBoundary $typed.effectiveBoundary)) { return $false }
        $receipts = @($attemptTyped.receipts)
        if ($receipts.Count -ne 1 -or $receipts[0].stage -cne 'attempted_decision' -or
            $receipts[0].receiptReason -cne $Envelope.decisionReason -or
            $receipts[0].receiptStatus -cne 'Accepted' -or $receipts[0].receiptValidated -isnot [bool] -or -not $receipts[0].receiptValidated -or
            $attempt.receiptValidated -isnot [bool] -or -not $attempt.receiptValidated) { return $false }
        if ([long]$attempt.commandGeneration -ne [long]$subject.operationGeneration -or
            [long]$attempt.receiptGeneration -ne [long]$subject.operationGeneration) { return $false }
        foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity',
                'receiptGeneration','receiptStatus','receiptFingerprint')) {
            if ([string]$attempt.$field -cne [string]$receipts[0].$field -or
                [string]$attempt.$field -in @('', '(none)')) { return $false }
        }
        return [long]$attempt.commandGeneration -gt 0 -and [long]$attempt.receiptGeneration -gt 0
    }

    $terminals = @($recovery | Where-Object operationRole -CEQ 'operation_terminal')
    $terminalEntrants = @($terminals | Where-Object {
            $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Completed:None'
        })
    $terminalRetirements = @($terminals | Where-Object {
            $_.afterState -ceq 'None'
        })
    if ($Envelope.decisionReason -cne 'filetransfer_receive_recovery_proof_observed' -or
        $terminalEntrants.Count -ne 1 -or $terminalRetirements.Count -gt 1 -or
        $terminals.Count -ne ($terminalEntrants.Count + $terminalRetirements.Count) -or
        $recovery.Count -ne (2 + $terminalRetirements.Count) -or
        [long]$before.bridgeRecoveryGeneration -ne [long]$typed.bridgeDecisionGeneration -or
        [long]$after.bridgeRecoveryGeneration -ne 0 -or
        $subject.beforeState -cne 'Completed:None' -or $subject.afterState -cne 'None' -or
        $typed.executionState -cne 'Completed' -or $typed.closureState -cne 'None' -or
        $typed.proofIdentity -cne '(none)' -or [long]$typed.proofObservedUtcMs -ne 0 -or
        [long]$typed.closureGeneration -ne 0 -or [long]$typed.recoveryLivenessLeaseGeneration -ne 0) { return $false }
    $terminal = $terminalEntrants[0]; $terminalTyped = $terminal.receiveRecovery
    if ($null -eq $terminalTyped -or $terminal.beforeState -cne 'None' -or
        $terminal.afterState -cne 'Completed:None' -or $terminal.stateApplied -isnot [bool] -or -not $terminal.stateApplied -or
        $terminalTyped.role -cne 'operation_terminal' -or $terminalTyped.recoveryKind -cne $typed.recoveryKind -or
        $terminalTyped.executionState -cne 'Completed' -or
        $terminalTyped.closureState -cne 'None' -or [long]$terminalTyped.closureGeneration -ne 0 -or
        $terminalTyped.proofIdentity -in @('', '(none)') -or
        [long]$terminalTyped.proofObservedUtcMs -le 0 -or
        [long]$terminalTyped.bridgeDecisionGeneration -ne [long]$typed.bridgeDecisionGeneration -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $terminalTyped.sourceBoundary $typed.sourceBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $terminalTyped.effectiveBoundary $typed.effectiveBoundary)) { return $false }
    foreach ($field in @('authorityId','predecessorAuthorityId','operationKind','operationGeneration',
            'commandIdentity','commandGeneration','commandFingerprint','receiptIdentity','receiptGeneration')) {
        if ([string]$terminal.$field -cne [string]$subject.$field) { return $false }
    }
    $currentPayload = $typed | Select-Object * -ExcludeProperty role,proofIdentity,proofObservedUtcMs,receipts | ConvertTo-Json -Depth 64 -Compress
    $terminalPayload = $terminalTyped | Select-Object * -ExcludeProperty role,proofIdentity,proofObservedUtcMs,receipts | ConvertTo-Json -Depth 64 -Compress
    if ($currentPayload -cne $terminalPayload) { return $false }
    foreach ($pair in @(@{ Operation=$subject; Status='Completed' }, @{ Operation=$terminal; Status='Adopted' })) {
        $parent = $pair.Operation; $receipts = @($parent.receiveRecovery.receipts)
        if ($receipts.Count -ne 1 -or $receipts[0].stage -cne 'operation' -or
            [long]$parent.receiptGeneration -ne [long]$parent.operationGeneration -or
            $parent.receiptStatus -cne $pair.Status -or $parent.receiptValidated -isnot [bool] -or -not $parent.receiptValidated -or
            $receipts[0].receiptValidated -isnot [bool] -or -not $receipts[0].receiptValidated) { return $false }
        foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity',
                'receiptGeneration','receiptStatus','receiptFingerprint')) {
            if ([string]$parent.$field -cne [string]$receipts[0].$field -or
                [string]$parent.$field -in @('', '(none)')) { return $false }
        }
    }
    if ($terminalRetirements.Count -eq 1) {
        $retirement = $terminalRetirements[0]
        $retirementTyped = $retirement.receiveRecovery
        if ($null -eq $retirementTyped -or
            $retirement.beforeState -notin @('Completed:None', 'Invalidated:None') -or
            $retirement.stateApplied -isnot [bool] -or -not $retirement.stateApplied -or
            $retirement.operationKind -cne 'BridgeReceiveStallRecovery' -or
            $retirementTyped.role -cne 'operation_terminal' -or
            $retirementTyped.recoveryKind -cne 'BridgeReceiveStallRecovery' -or
            $retirement.authorityId -in @('', '(none)') -or
            $retirement.authorityId -ceq $terminal.authorityId -or
            [long]$retirement.operationGeneration -le 0 -or
            [long]$retirement.operationGeneration -ge [long]$terminal.operationGeneration -or
            $retirement.operationKey -cne "receive_recovery|operation_terminal|$($retirement.authorityId)|$($retirement.operationGeneration)|BridgeReceiveStallRecovery" -or
            $retirement.commandIdentity -cne $retirement.authorityId -or
            [long]$retirement.commandGeneration -ne [long]$retirement.operationGeneration -or
            [long]$retirement.receiptGeneration -ne [long]$retirement.operationGeneration -or
            $retirement.receiptStatus -cne 'Adopted' -or
            $retirement.receiptValidated -isnot [bool] -or -not $retirement.receiptValidated -or
            "$($retirementTyped.executionState):$($retirementTyped.closureState)" -cne $retirement.beforeState -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $retirementTyped.sourceBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $retirementTyped.effectiveBoundary)) {
            return $false
        }
        $retirementReceipts = @($retirementTyped.receipts)
        if ($retirementReceipts.Count -ne 1 -or
            $retirementReceipts[0].stage -cne 'operation' -or
            $retirementReceipts[0].receiptValidated -isnot [bool] -or
            -not $retirementReceipts[0].receiptValidated) {
            return $false
        }
        foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity',
                'receiptGeneration','receiptStatus','receiptFingerprint')) {
            if ([string]$retirement.$field -cne [string]$retirementReceipts[0].$field -or
                [string]$retirement.$field -in @('', '(none)')) {
                return $false
            }
        }
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotFamily1OperationCatalog {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        [Parameter(Mandatory = $true)]$Operation,
        $Catalog = $null
    )

    if ([string]$Operation.family -notin
        $script:FileTransferCoordinatorSnapshotFamily1) {
        return $true
    }
    $expectedOperationKey = @(
        [string]$Operation.family,
        [string]$Operation.operationRole,
        [string]$Operation.authorityId,
        ([long]$Operation.operationGeneration).ToString(
            [System.Globalization.CultureInfo]::InvariantCulture),
        [string]$Operation.operationKind) -join '|'
    if ([string]$Operation.operationKey -cne $expectedOperationKey) {
        return $false
    }
    $isExactFallbackCheckpointOperation =
        Test-FileTransferCoordinatorSnapshotFallbackCheckpointOperation `
            -Envelope $Envelope -Operation $Operation
    if (([string]$Envelope.decisionKind -notin
            $script:FileTransferCoordinatorSnapshotFamily1DecisionKinds -and
         -not $isExactFallbackCheckpointOperation -and
         -not (Test-FileTransferBootstrapCorrelationBaseRecord $Envelope $Operation $Catalog) -and
         -not (Test-FileTransferCoordinatorSnapshotReceiveRecoveryRouteCarry $Envelope $Operation)) -or
        [string]$Envelope.decisionStatus -notin @(
            'Accepted', 'Idempotent', 'Rejected', 'Prepared', 'Draining',
            'Committed', 'Invalidated', 'SupersessionStaged') -or
        [string]::IsNullOrWhiteSpace([string]$Envelope.decisionReason)) {
        return $false
    }

    $role = [string]$Operation.operationRole
    if (-not $script:FileTransferCoordinatorSnapshotFamily1OperationKinds.Contains($role) -or
        [string]$Operation.operationKind -notin
            @($script:FileTransferCoordinatorSnapshotFamily1OperationKinds[$role])) {
        return $false
    }
    if ([string]$Operation.family -ceq 'route_runtime') {
        return [string]$Operation.beforeState -in @(
            'None', 'Attempted', 'Current', 'Committed', 'Published', 'Terminal') -and
            [string]$Operation.afterState -in @(
                'None', 'Attempted', 'Current', 'Committed', 'Published', 'Terminal')
    }

    foreach ($name in @(
            'role', 'transitionKind', 'sourceTransport', 'proofBoundaryKind',
            'proofAdmissionMode',
            'proofBoundaryFingerprint', 'durableCheckpointFingerprint',
            'targetRoute', 'targetProtocolVersion', 'targetTransport',
            'handoffKind', 'dispositionKind', 'retainedFrameType',
            'retainedFrameFingerprint')) {
        if ($Operation.routeTransition.PSObject.Properties.Name -notcontains $name -or
            [string]::IsNullOrWhiteSpace([string]$Operation.routeTransition.$name)) {
            return $false
        }
    }
    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Operation.routeTransition.targetBoundary)) {
        return $false
    }

    if ([string]$Operation.beforeState -notin @(
            'None', 'Unknown', 'Current', 'Prepared', 'Draining', 'Committed',
            'Invalidated', 'Staged', 'Rebound', 'Pending', 'Delivered',
            'Idempotent', 'Rejected') -or
        [string]$Operation.afterState -notin @(
            'None', 'Unknown', 'Current', 'Prepared', 'Draining', 'Committed',
            'Invalidated', 'Staged', 'Rebound', 'Pending', 'Delivered',
            'Idempotent', 'Rejected')) {
        return $false
    }
    foreach ($receipt in @($Operation.routeTransition.receipts)) {
        $stage = [string]$receipt.stage
        if (-not $script:FileTransferCoordinatorSnapshotFamily1ReceiptStages.Contains($stage)) {
            return $false
        }
        $allowed = @($script:FileTransferCoordinatorSnapshotFamily1ReceiptStages[$stage])
        if ([string]$receipt.receiptStatus -notin $allowed) {
            return $false
        }
    }
    if (-not (Test-FileTransferCoordinatorSnapshotFamily1AttemptOperation `
            -Envelope $Envelope `
            -Operation $Operation)) {
        return $false
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotFamily1AttemptOperation {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        [Parameter(Mandatory = $true)]$Operation
    )

    if ([string]$Operation.family -cne 'route_transition' -or
        [bool]$Operation.stateApplied -or
        [string]$Operation.operationRole -notin @(
            'inbound', 'outbound', 'attempt')) {
        return $true
    }
    if ([string]$Operation.operationKind -cne
            [string]$Envelope.decisionKind -or
        [string]$Operation.routeTransition.role -cne
            [string]$Operation.operationRole) {
        return $false
    }
    if ([string]$Operation.operationRole -in @('inbound', 'outbound') -and
        [string]$Operation.operationRole -cne
            [string]$Envelope.direction) {
        return $false
    }

    $expectedStage = switch ([string]$Envelope.decisionKind) {
        'BeginFileTransferRouteTransitionPhysicalDrain' { 'pause_bind' }
        'CommitFileTransferRouteTransitionSupersession' { 'pause_rebind' }
        'CommitFileTransferRouteTransition' { 'drain' }
        'ApplyFileTransferRouteTransitionRetainedFrameReceipt' {
            'retained_frame'
        }
        'ApplyFileTransferRouteTransitionDispositionReceipt' {
            'disposition'
        }
        default { '(none)' }
    }
    $receipts = @($Operation.routeTransition.receipts)
    if ($expectedStage -ceq '(none)') {
        return $receipts.Count -eq 0 -and
            [string]$Operation.receiptIdentity -ceq '(none)' -and
            [long]$Operation.receiptGeneration -eq 0 -and
            [string]$Operation.receiptStatus -ceq '(none)' -and
            [string]$Operation.receiptFingerprint -ceq '(none)' -and
            -not [bool]$Operation.receiptValidated
    }
    if ($receipts.Count -ne 1) {
        return $false
    }

    $receipt = $receipts[0]
    return [string]$receipt.stage -ceq $expectedStage -and
        -not [string]::IsNullOrWhiteSpace(
            [string]$receipt.commandIdentity) -and
        [string]$receipt.commandIdentity -cne '(none)' -and
        [long]$receipt.commandGeneration -eq
            [long]$Operation.operationGeneration -and
        [string]$receipt.commandIdentity -ceq
            [string]$Operation.commandIdentity -and
        [long]$receipt.commandGeneration -eq
            [long]$Operation.commandGeneration -and
        [string]$receipt.commandFingerprint -ceq
            [string]$Operation.commandFingerprint -and
        [string]$receipt.receiptIdentity -ceq
            [string]$receipt.commandIdentity -and
        [long]$receipt.receiptGeneration -eq
            [long]$Operation.operationGeneration -and
        [string]$receipt.receiptIdentity -ceq
            [string]$Operation.receiptIdentity -and
        [long]$receipt.receiptGeneration -eq
            [long]$Operation.receiptGeneration -and
        [string]$receipt.receiptStatus -ceq
            [string]$Operation.receiptStatus -and
        [string]$receipt.receiptFingerprint -ceq
            [string]$Operation.receiptFingerprint -and
        [bool]$receipt.receiptValidated -eq
            [bool]$Operation.receiptValidated
}

function Get-FileTransferCoordinatorSnapshotFamily1AttemptReceiptStageVariant {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        [Parameter(Mandatory = $true)]$Operation
    )

    if ([string]$Operation.family -cne 'route_transition' -or
        [bool]$Operation.stateApplied -or
        [string]$Operation.operationRole -notin @(
            'inbound', 'outbound', 'attempt')) {
        return '(none)'
    }
    $expectedStage = switch ([string]$Envelope.decisionKind) {
        'BeginFileTransferRouteTransitionPhysicalDrain' { 'pause_bind' }
        'CommitFileTransferRouteTransitionSupersession' { 'pause_rebind' }
        'CommitFileTransferRouteTransition' { 'drain' }
        'ApplyFileTransferRouteTransitionRetainedFrameReceipt' {
            'retained_frame'
        }
        'ApplyFileTransferRouteTransitionDispositionReceipt' {
            'disposition'
        }
        default { '(none)' }
    }
    $receipts = @($Operation.routeTransition.receipts)
    if ($expectedStage -cne '(none)' -and
        $receipts.Count -eq 1 -and
        [string]$receipts[0].stage -ceq [string]$Envelope.decisionKind) {
        return 'route_transition_attempt_receipt_stage_projected_as_decision_kind_instead_of_typed_stage'
    }
    return '(none)'
}

function Test-FileTransferCoordinatorSnapshotStagedRetryInstallReceipt {
    param([Parameter(Mandatory = $true)]$Operation)

    if ([string]$Operation.family -cne 'runtime_unlock_retry_lifecycle' -or
        [string]$Operation.operationRole -cne 'attempt_pending' -or
        [string]$Operation.operationKind -cne 'RetryAttempt' -or
        [string]$Operation.beforeState -cne 'Staged' -or
        [string]$Operation.afterState -notin @('Staged', 'None') -or
        $null -eq $Operation.runtimeUnlockRetry) { return $false }
    $retry = $Operation.runtimeUnlockRetry
    if ([string]$retry.role -cne 'attempt_pending' -or
        [long]$Operation.operationGeneration -le 0 -or
        [long]$Operation.operationGeneration -ne [long]$retry.authorityGeneration -or
        [string]::IsNullOrWhiteSpace([string]$Operation.authorityId) -or
        [string]$Operation.authorityId -ceq '(none)' -or
        [string]$Operation.operationKey -cne
            "runtime_unlock_retry_lifecycle|attempt_pending|$($Operation.authorityId)|$($Operation.operationGeneration)|RetryAttempt" -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete -Boundary $retry.sourceBoundary)) {
        return $false
    }
    $receipts = @($retry.receipts)
    $staging = @($receipts | Where-Object { [string]$_.stage -ceq 'stage' })
    $install = @($receipts | Where-Object { [string]$_.stage -ceq 'install' })
    if ($receipts.Count -ne 2 -or $staging.Count -ne 1 -or $install.Count -ne 1) {
        return $false
    }
    foreach ($receipt in $receipts) {
        foreach ($field in @('commandIdentity', 'commandFingerprint', 'receiptIdentity', 'receiptFingerprint')) {
            if ([string]::IsNullOrWhiteSpace([string]$receipt.$field) -or
                [string]$receipt.$field -ceq '(none)') { return $false }
        }
        if (-not [bool]$receipt.receiptValidated -or
            [long]$receipt.commandGeneration -le 0 -or
            [long]$receipt.commandGeneration -ne [long]$receipt.receiptGeneration -or
            [string]$receipt.commandIdentity -cne [string]$receipt.receiptIdentity) {
            return $false
        }
    }
    if ([string]$staging[0].receiptStatus -notin @('Staged', 'Idempotent') -or
        [long]$staging[0].commandGeneration -ne [long]$retry.dispatchAttemptGeneration -or
        [string]$install[0].receiptStatus -cne 'Staged' -or
        [string]$staging[0].commandFingerprint -cne [string]$install[0].commandFingerprint) {
        return $false
    }
    if ([bool]$Operation.stateApplied) {
        foreach ($field in @('commandIdentity', 'commandGeneration', 'commandFingerprint',
                'receiptIdentity', 'receiptGeneration', 'receiptStatus', 'receiptFingerprint', 'receiptValidated')) {
            if ([string]$Operation.$field -cne [string]$install[0].$field) { return $false }
        }
    }
    else {
        # Unchanged attempts carry a decision receipt above the retained install receipt.
        if ([string]$Operation.afterState -cne 'Staged' -or
            [string]$Operation.receiptStatus -notin @('Idempotent', 'Rejected') -or
            -not [bool]$Operation.receiptValidated -or
            [long]$Operation.commandGeneration -le 0 -or
            [long]$Operation.receiptGeneration -le 0) { return $false }
        foreach ($field in @('commandIdentity', 'commandFingerprint', 'receiptIdentity', 'receiptFingerprint')) {
            if ([string]::IsNullOrWhiteSpace([string]$Operation.$field) -or
                [string]$Operation.$field -ceq '(none)') { return $false }
        }
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotStagedRetryInstallBoundary {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        [Parameter(Mandatory = $true)]$Operation
    )
    if (-not (Test-FileTransferCoordinatorSnapshotStagedRetryInstallReceipt -Operation $Operation)) {
        return $false
    }
    $aggregate = $Envelope.aggregate
    if (@($aggregate.operations | Where-Object {
            [string]$_.operationKey -ceq [string]$Operation.operationKey
        }).Count -ne 1) { return $false }
    if (-not [bool]$Operation.stateApplied) {
        return [string]$Envelope.decisionStatus -ceq [string]$Operation.receiptStatus -and
            [long]$aggregate.beforeStateGeneration -eq [long]$aggregate.afterStateGeneration -and
            [string]$aggregate.beforeStateFingerprint -ceq [string]$aggregate.afterStateFingerprint
    }
    if ([string]$Envelope.decisionStatus -cne 'Accepted' -or
        [long]$aggregate.afterStateGeneration -ne ([long]$aggregate.beforeStateGeneration + 1) -or
        [string]$aggregate.beforeStateFingerprint -ceq [string]$aggregate.afterStateFingerprint) {
        return $false
    }
    if ([string]$Operation.afterState -ceq 'Staged') {
        return [string]$Envelope.decisionKind -ceq 'IssueRuntimeUnlockRetryDispatchInstallReceipt'
    }
    if ([string]$Envelope.decisionKind -cne 'CommitRuntimeUnlockRetryDispatchInstall') { return $false }
    $entrants = @($aggregate.operations | Where-Object {
            [string]$_.family -ceq 'runtime_unlock_retry_lifecycle' -and
            [string]$_.operationRole -ceq 'attempt_installed' -and
            [string]$_.authorityId -ceq [string]$Operation.authorityId
        })
    if ($entrants.Count -ne 1) { return $false }
    $entrant = $entrants[0]
    if ([string]$entrant.operationKind -cne 'RetryAttempt' -or
        [string]$entrant.beforeState -cne 'None' -or [string]$entrant.afterState -cne 'Installed' -or
        -not [bool]$entrant.stateApplied -or
        [long]$entrant.operationGeneration -ne [long]$Operation.operationGeneration -or
        $null -eq $entrant.runtimeUnlockRetry -or
        [string]$entrant.runtimeUnlockRetry.role -cne 'attempt_installed' -or
        [long]$entrant.runtimeUnlockRetry.authorityGeneration -ne [long]$Operation.operationGeneration -or
        [long]$entrant.runtimeUnlockRetry.dispatchAttemptGeneration -ne [long]$Operation.runtimeUnlockRetry.dispatchAttemptGeneration -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            -Left $Operation.runtimeUnlockRetry.sourceBoundary -Right $entrant.runtimeUnlockRetry.sourceBoundary)) {
        return $false
    }
    $pendingInstall = @($Operation.runtimeUnlockRetry.receipts | Where-Object { [string]$_.stage -ceq 'install' })[0]
    $installedReceipts = @($entrant.runtimeUnlockRetry.receipts | Where-Object { [string]$_.stage -ceq 'install' })
    if ($installedReceipts.Count -ne 1 -or
        [string]$installedReceipts[0].receiptStatus -cne 'Installed') { return $false }
    foreach ($field in @('commandIdentity', 'commandGeneration', 'commandFingerprint',
            'receiptIdentity', 'receiptGeneration', 'receiptFingerprint', 'receiptValidated')) {
        if ([string]$pendingInstall.$field -cne [string]$installedReceipts[0].$field -or
            [string]$entrant.$field -cne [string]$installedReceipts[0].$field) { return $false }
    }
    return [string]$entrant.receiptStatus -ceq 'Installed'
}

function Test-FileTransferCoordinatorSnapshotPendingScopeInvalidationStage {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        [Parameter(Mandatory = $true)]$Operation
    )

    if ([string]$Operation.family -cne 'runtime_unlock_retry_lifecycle' -or
        [string]$Operation.operationRole -cne 'scope_invalidation' -or
        [string]$Operation.operationKind -cne 'ScopeInvalidation' -or
        $Operation.stateApplied -isnot [bool] -or $Operation.receiptValidated -isnot [bool] -or
        [string]$Operation.beforeState -notin @('None', 'Pending') -or
        [string]$Operation.afterState -notin @('Pending', 'None') -or
        ([string]$Operation.beforeState -ceq 'None' -and [string]$Operation.afterState -ceq 'None') -or
        $null -eq $Operation.runtimeUnlockRetry) { return $false }
    $retry = $Operation.runtimeUnlockRetry
    $receipts = @($retry.receipts)
    if ([string]$retry.role -cne 'scope_invalidation' -or
        $receipts.Count -ne 1 -or [string]$receipts[0].stage -cne 'scope_invalidation' -or
        [long]$Operation.operationGeneration -le 0 -or
        [string]::IsNullOrWhiteSpace([string]$Operation.authorityId) -or
        [string]$Operation.authorityId -ceq '(none)' -or
        [string]$Operation.operationKey -cne
            "runtime_unlock_retry_lifecycle|scope_invalidation|$($Operation.authorityId)|$($Operation.operationGeneration)|ScopeInvalidation" -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete -Boundary $retry.sourceBoundary)) {
        return $false
    }
    $receipt = $receipts[0]
    foreach ($field in @('commandIdentity', 'commandFingerprint', 'receiptReason')) {
        if ([string]::IsNullOrWhiteSpace([string]$receipt.$field) -or
            [string]$receipt.$field -ceq '(none)') { return $false }
    }
    # Invalidation generation is independent of retry-authority/dispatch/install generations.
    if ([long]$receipt.commandGeneration -ne [long]$Operation.operationGeneration -or
        [string]$receipt.receiptIdentity -cne '(none)' -or
        [long]$receipt.receiptGeneration -ne 0 -or
        [string]$receipt.receiptFingerprint -cne '(none)' -or
        [string]$receipt.receiptStatus -cne 'Pending' -or
        $receipt.receiptValidated -isnot [bool] -or $receipt.receiptValidated) { return $false }
    $aggregate = $Envelope.aggregate
    if (@($aggregate.operations | Where-Object {
            [string]$_.operationKey -ceq [string]$Operation.operationKey
        }).Count -ne 1) { return $false }
    if ([bool]$Operation.stateApplied) {
        if ([string]$Envelope.decisionKind -ceq 'CommitRuntimeUnlockRetryLifecycleScopeInvalidation') { return $false }
        foreach ($field in @('commandIdentity', 'commandGeneration', 'commandFingerprint',
                'receiptIdentity', 'receiptGeneration', 'receiptStatus', 'receiptFingerprint', 'receiptValidated')) {
            if ([string]$Operation.$field -cne [string]$receipt.$field) { return $false }
        }
        if ([string]$Operation.beforeState -ceq 'None') {
            if ([string]$Envelope.decisionKind -notin @('PrepareRuntimeUnlockRetryLifecycleScopeInvalidation',
                    'PrepareRuntimeUnlockRetryDispatchStageExpiry')) { return $false }
            $attempts = @($aggregate.operations | Where-Object {
                [string]$_.family -ceq 'runtime_unlock_retry_lifecycle' -and
                [string]$_.operationRole -ceq 'attempted_decision'
            })
            if ($attempts.Count -ne 1) { return $false }
            $attempt = $attempts[0]
            if ([string]$attempt.authorityId -cne [string]$receipt.commandIdentity -or
                [long]$attempt.operationGeneration -ne [long]$receipt.commandGeneration -or
                [string]$attempt.commandFingerprint -cne [string]$receipt.commandFingerprint -or
                [string]$attempt.operationKind -cne [string]$Envelope.decisionKind -or
                [bool]$attempt.stateApplied -or -not [bool]$attempt.receiptValidated -or
                [string]$attempt.receiptStatus -cne 'Accepted' -or [long]$attempt.receiptGeneration -le 0 -or
                $null -eq $attempt.runtimeUnlockRetry) { return $false }
            $decisions = @($attempt.runtimeUnlockRetry.receipts)
            if ($decisions.Count -ne 1 -or [string]$decisions[0].stage -cne 'decision') { return $false }
            foreach ($field in @('commandIdentity', 'commandGeneration', 'commandFingerprint',
                    'receiptIdentity', 'receiptGeneration', 'receiptStatus', 'receiptFingerprint', 'receiptValidated')) {
                if ([string]$attempt.$field -cne [string]$decisions[0].$field) { return $false }
            }
        }
        return [string]$Envelope.decisionStatus -ceq 'Accepted' -and
            [long]$aggregate.afterStateGeneration -eq ([long]$aggregate.beforeStateGeneration + 1) -and
            [string]$aggregate.beforeStateFingerprint -cne [string]$aggregate.afterStateFingerprint
    }
    # Unchanged attempts have their own decision receipt, not a fabricated command receipt.
    if ([string]$Operation.beforeState -cne 'Pending' -or [string]$Operation.afterState -cne 'Pending' -or
        [string]$Operation.receiptStatus -notin @('Idempotent', 'Rejected') -or
        [string]$Operation.receiptStatus -cne [string]$Envelope.decisionStatus -or
        -not [bool]$Operation.receiptValidated -or [long]$Operation.receiptGeneration -le 0 -or
        [long]$Operation.commandGeneration -ne [long]$receipt.commandGeneration -or
        [string]$Operation.commandFingerprint -cne [string]$receipt.commandFingerprint -or
        [long]$aggregate.beforeStateGeneration -ne [long]$aggregate.afterStateGeneration -or
        [string]$aggregate.beforeStateFingerprint -cne [string]$aggregate.afterStateFingerprint) { return $false }
    foreach ($field in @('commandIdentity', 'receiptIdentity', 'receiptFingerprint')) {
        if ([string]::IsNullOrWhiteSpace([string]$Operation.$field) -or
            [string]$Operation.$field -ceq '(none)') { return $false }
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotAppliedScopeInvalidationStage {
    param($Envelope, $Operation)

    if ($Operation.family -cne 'runtime_unlock_retry_lifecycle' -or
        $Operation.operationRole -cne 'scope_invalidation' -or $Operation.operationKind -cne 'ScopeInvalidation' -or
        $Operation.stateApplied -isnot [bool] -or $Operation.receiptValidated -isnot [bool] -or
        -not $Operation.receiptValidated -or $null -eq $Operation.runtimeUnlockRetry -or
        $Operation.runtimeUnlockRetry.role -cne 'scope_invalidation' -or
        [string]$Operation.authorityId -in @('', '(none)') -or [long]$Operation.operationGeneration -le 0 -or
        $Operation.operationKey -cne "runtime_unlock_retry_lifecycle|scope_invalidation|$($Operation.authorityId)|$($Operation.operationGeneration)|ScopeInvalidation" -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $Operation.runtimeUnlockRetry.sourceBoundary)) { return $false }
    $receipts = @($Operation.runtimeUnlockRetry.receipts)
    if ($receipts.Count -ne 1) { return $false }
    $receipt = $receipts[0]
    if ($receipt.stage -cne 'scope_invalidation' -or $receipt.receiptStatus -cne 'Applied' -or
        $receipt.receiptValidated -isnot [bool] -or -not $receipt.receiptValidated -or
        [long]$receipt.commandGeneration -ne [long]$Operation.operationGeneration -or
        [long]$receipt.receiptGeneration -ne [long]$receipt.commandGeneration -or
        $receipt.commandIdentity -cne $receipt.receiptIdentity) { return $false }
    foreach ($field in @('commandIdentity', 'commandFingerprint', 'receiptIdentity', 'receiptFingerprint', 'receiptReason')) {
        if ([string]::IsNullOrWhiteSpace([string]$receipt.$field) -or [string]$receipt.$field -ceq '(none)') { return $false }
    }
    $aggregate = $Envelope.aggregate
    if (@($aggregate.operations | Where-Object { $_.operationKey -ceq $Operation.operationKey }).Count -ne 1) { return $false }
    $application = $Operation.beforeState -ceq 'Pending' -and $Operation.afterState -ceq 'Applied'
    if (-not $application -and -not ($Operation.beforeState -ceq 'Applied' -and $Operation.afterState -in @('Applied', 'None'))) { return $false }
    if (($application -or -not $Operation.stateApplied) -and
        @($aggregate.operations | Where-Object { $_.family -ceq 'runtime_unlock_retry_lifecycle' -and $_.operationRole -ceq 'scope_invalidation' }).Count -ne 1) { return $false }
    if ($Operation.stateApplied) {
        if ($application -and ($Envelope.decisionKind -cne 'CommitRuntimeUnlockRetryLifecycleScopeInvalidation' -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $aggregate.beforeBoundary $aggregate.afterBoundary))) { return $false }
        foreach ($field in @('commandIdentity', 'commandGeneration', 'commandFingerprint',
                'receiptIdentity', 'receiptGeneration', 'receiptStatus', 'receiptFingerprint', 'receiptValidated')) {
            if ([string]$Operation.$field -cne [string]$receipt.$field) { return $false }
        }
        return $Envelope.decisionStatus -ceq 'Accepted' -and
            [long]$aggregate.afterStateGeneration -eq ([long]$aggregate.beforeStateGeneration + 1L) -and
            [string]$aggregate.beforeStateFingerprint -cne [string]$aggregate.afterStateFingerprint
    }
    # A duplicate decision keeps its own receipt; the typed payload retains the
    # immutable Applied receipt, not a second physical application.
    if ($Operation.beforeState -cne 'Applied' -or $Operation.afterState -cne 'Applied' -or
        $Envelope.decisionKind -cne 'CommitRuntimeUnlockRetryLifecycleScopeInvalidation' -or
        $Envelope.decisionStatus -notin @('Idempotent', 'Rejected') -or
        $Operation.receiptStatus -cne $Envelope.decisionStatus -or
        [long]$Operation.commandGeneration -ne [long]$receipt.commandGeneration -or
        [long]$Operation.receiptGeneration -ne [long]$receipt.receiptGeneration -or
        [long]$aggregate.beforeStateGeneration -ne [long]$aggregate.afterStateGeneration -or
        $aggregate.beforeStateFingerprint -cne $aggregate.afterStateFingerprint) { return $false }
    foreach ($field in @('commandIdentity', 'commandFingerprint', 'receiptIdentity', 'receiptFingerprint')) {
        if ([string]::IsNullOrWhiteSpace([string]$Operation.$field) -or [string]$Operation.$field -ceq '(none)') { return $false }
    }
    return $Envelope.decisionStatus -ceq 'Rejected' -or $Operation.commandFingerprint -ceq $receipt.receiptFingerprint
}

function Test-FileTransferCoordinatorSnapshotProvisionalRetrySupersession {
    param([Parameter(Mandatory = $true)]$Envelope, [Parameter(Mandatory = $true)]$Operation)

    if ($Operation.family -cne 'runtime_unlock_retry_lifecycle' -or
        $Operation.operationRole -cne 'attempt_retained_predecessor' -or
        $Operation.operationKind -cne 'RetryAttempt' -or $null -eq $Operation.runtimeUnlockRetry) { return $false }
    $aggregate = $Envelope.aggregate
    $operations = @($aggregate.operations)
    $subjects = @($operations | Where-Object operationKey -CEQ $Operation.operationKey)
    if ($subjects.Count -ne 1 -or
        ($subjects[0] | ConvertTo-Json -Depth 32 -Compress) -cne ($Operation | ConvertTo-Json -Depth 32 -Compress) -or
        $Envelope.decisionStatus -cne 'Accepted' -or
        [long]$aggregate.afterStateGeneration -ne ([long]$aggregate.beforeStateGeneration + 1L) -or
        $aggregate.beforeStateFingerprint -ceq $aggregate.afterStateFingerprint -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $aggregate.beforeBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $aggregate.afterBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $aggregate.beforeBoundary $aggregate.afterBoundary)) { return $false }
    $pending = $null
    if ($Envelope.decisionKind -ceq 'CommitRuntimeUnlockRetryDispatchInstall') {
        if ($Envelope.decisionReason -cne 'retry_dispatch_install_committed' -or $operations.Count -ne 4 -or
            $Operation.beforeState -cne 'None' -or $Operation.afterState -cne 'Settled') { return $false }
        $originals = @($operations | Where-Object {
            $_.operationRole -ceq 'attempt_installed' -and $_.beforeState -ceq 'AliasBound' -and $_.afterState -ceq 'None'
        })
        $successors = @($operations | Where-Object {
            $_.operationRole -ceq 'attempt_installed' -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Installed'
        })
        $pendingOperations = @($operations | Where-Object {
            $_.operationRole -ceq 'attempt_pending' -and $_.beforeState -ceq 'Staged' -and $_.afterState -ceq 'None'
        })
        if ($originals.Count -ne 1 -or $successors.Count -ne 1 -or $pendingOperations.Count -ne 1) { return $false }
        $original = $originals[0]; $successor = $successors[0]; $pending = $pendingOperations[0]
        if (-not (Test-FileTransferCoordinatorSnapshotStagedRetryInstallBoundary $Envelope $pending)) { return $false }
    }
    elseif ($Envelope.decisionKind -ceq 'CompensateRejectedRuntimeUnlockRetryDispatchConfirmation') {
        $shape = Get-FileTransferCoordinatorRetryConfirmationCompensationShape $Envelope
        if ($null -eq $shape -or -not $shape.Applied -or $shape.Displaced.Count -ne 1 -or $shape.Restored.Count -ne 1 -or
            $shape.Displaced[0].operationKey -cne $Operation.operationKey -or
            $shape.Restored[0].afterState -cne 'AliasBound') { return $false }
        $original = $shape.Restored[0]; $successor = $shape.Failed
        # Local shape admission is not history proof. The existing compensation
        # history validator also requires the exact previously admitted commit.
    }
    else { return $false }
    foreach ($subject in $operations) {
        if ($subject.family -cne 'runtime_unlock_retry_lifecycle' -or $subject.operationKind -cne 'RetryAttempt' -or
            $subject.stateApplied -isnot [bool] -or -not $subject.stateApplied -or
            $subject.receiptValidated -isnot [bool] -or $null -eq $subject.runtimeUnlockRetry -or
            $subject.runtimeUnlockRetry.requestActivePredecessorSupersession -isnot [bool] -or
            $subject.runtimeUnlockRetry.dispatchObserved -isnot [bool] -or
            $subject.runtimeUnlockRetry.role -cne $subject.operationRole -or
            [long]$subject.operationGeneration -le 0 -or
            [long]$subject.operationGeneration -ne [long]$subject.runtimeUnlockRetry.authorityGeneration -or
            [string]::IsNullOrWhiteSpace([string]$subject.authorityId) -or $subject.authorityId -ceq '(none)' -or
            $subject.operationKey -cne "runtime_unlock_retry_lifecycle|$($subject.operationRole)|$($subject.authorityId)|$($subject.operationGeneration)|RetryAttempt" -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $subject.runtimeUnlockRetry.sourceBoundary)) { return $false }
        $receipts = @($subject.runtimeUnlockRetry.receipts)
        if ($receipts.Count -lt 2) { return $false }
        foreach ($receipt in $receipts) {
            if ($receipt.receiptValidated -isnot [bool] -or -not $receipt.receiptValidated -or [long]$receipt.commandGeneration -le 0 -or
                [long]$receipt.commandGeneration -ne [long]$receipt.receiptGeneration -or
                ($receipt.stage -in @('stage', 'install') -and $receipt.commandIdentity -cne $receipt.receiptIdentity)) { return $false }
            foreach ($field in @('commandIdentity', 'commandFingerprint', 'receiptIdentity', 'receiptFingerprint')) {
                if ([string]::IsNullOrWhiteSpace([string]$receipt.$field) -or $receipt.$field -ceq '(none)') { return $false }
            }
        }
        foreach ($field in @('commandIdentity', 'commandGeneration', 'commandFingerprint', 'receiptIdentity',
                'receiptGeneration', 'receiptStatus', 'receiptFingerprint', 'receiptValidated')) {
            if ([string]$subject.$field -cne [string]$receipts[-1].$field) { return $false }
        }
    }
    $a = $original.runtimeUnlockRetry; $retained = $Operation.runtimeUnlockRetry; $b = $successor.runtimeUnlockRetry
    if ($original.authorityId -cne $Operation.authorityId -or $original.predecessorAuthorityId -cne $Operation.predecessorAuthorityId -or
        $original.authorityId -ceq $successor.authorityId -or $successor.predecessorAuthorityId -cne $original.authorityId -or
        [long]$original.operationGeneration -ne [long]$Operation.operationGeneration -or
        [long]$successor.operationGeneration -le [long]$original.operationGeneration -or
        $retained.successorAuthorityId -cne $successor.authorityId -or $a.successorAuthorityId -cne '(none)' -or
        $b.successorAuthorityId -cne '(none)' -or -not [bool]$b.requestActivePredecessorSupersession -or
        [bool]$a.dispatchObserved -or [bool]$retained.dispatchObserved -or [bool]$b.dispatchObserved -or
        [long]$a.offerGeneration -le 0 -or [string]::IsNullOrWhiteSpace([string]$a.offerNonceFingerprint) -or
        $a.offerNonceFingerprint -ceq '(none)' -or
        [long]$a.dispatchAttemptGeneration -le 0 -or [long]$a.recoveryContractGeneration -le 0 -or
        [long]$a.releaseGeneration -le 0 -or [string]::IsNullOrWhiteSpace([string]$a.releaseAuthorityId) -or
        $a.releaseAuthorityId -ceq '(none)' -or
        [long]$b.offerGeneration -ne 0 -or $b.offerNonceFingerprint -cne '(none)' -or
        [long]$b.retiredOfferGeneration -ne [long]$a.offerGeneration -or
        [long]$b.dispatchAttemptGeneration -ne ([long]$a.dispatchAttemptGeneration + 1L) -or
        $a.listenerRearmRequirement -notin @('Required', 'RequiredBeforeDispatch') -or
        $a.listenerExecutionMode -notin @('Default', 'AdoptCurrentCoordinatorScoped') -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $a.sourceBoundary $b.sourceBoundary)) { return $false }
    $releaseOutcomeValid = switch -Exact ([string]$a.releaseKind) {
        'RegularV4RepairSettlement' { $a.releaseOutcome -in @('ProgressObserved', 'MissingDispatch', 'SettledStalled'); break }
        'ReceiveRecoveryLeaseClosure' { $a.releaseOutcome -ceq 'Retired'; break }
        'FallbackPairedTailYield' { $a.releaseOutcome -ceq 'Quiescent'; break }
        default { $false }
    }
    if (-not $releaseOutcomeValid) { return $false }
    foreach ($field in @('recoveryContractGeneration', 'missingDispatchSuccessorGeneration', 'releaseKind', 'releaseAuthorityId',
            'releaseGeneration', 'releaseOutcome', 'listenerRearmRequirement', 'listenerExecutionMode')) {
        if ([string]$a.$field -cne [string]$b.$field) { return $false }
    }
    $aReceipts = @($a.receipts); $retainedReceipts = @($retained.receipts)
    if ($aReceipts.Count -ne 3 -or $retainedReceipts.Count -ne 4 -or
        $aReceipts[0].stage -cne 'stage' -or $aReceipts[1].stage -cne 'install' -or $aReceipts[2].stage -cne 'execution' -or
        $aReceipts[0].receiptStatus -notin @('Staged', 'Idempotent') -or
        $aReceipts[1].receiptStatus -cne 'AliasBound' -or $aReceipts[2].receiptStatus -cne 'AliasBound' -or
        [long]$aReceipts[0].receiptGeneration -ne [long]$a.dispatchAttemptGeneration -or
        [long]$aReceipts[2].receiptGeneration -ne [long]$a.offerGeneration -or
        $aReceipts[0].commandFingerprint -cne $aReceipts[1].commandFingerprint -or
        $aReceipts[2].commandFingerprint -cne $aReceipts[1].receiptFingerprint -or
        $aReceipts[2].receiptReason -cne 'offer_bound' -or
        $retainedReceipts[1].receiptStatus -cne 'Settled' -or $retainedReceipts[2].receiptStatus -cne 'Settled' -or
        $retainedReceipts[2].receiptFingerprint -ceq $aReceipts[2].receiptFingerprint) { return $false }
    $settlement = $retainedReceipts[3]
    if ($settlement.stage -cne 'settlement' -or $settlement.receiptStatus -cne 'Superseded' -or
        $settlement.receiptReason -cne 'coordinator_successor_installed' -or
        [long]$settlement.receiptGeneration -ne [long]$a.offerGeneration -or
        $settlement.commandIdentity -cne $settlement.receiptIdentity) { return $false }
    # Stage/install are immutable. Execution's status AND fingerprint project
    # AliasBound -> Settled; its raw nonce cannot be recovered from evidence.
    $normalized = $retained | ConvertTo-Json -Depth 32 -Compress | ConvertFrom-Json
    $normalized.role = 'attempt_installed'; $normalized.successorAuthorityId = '(none)'
    $normalized.receipts = @($normalized.receipts[0..2])
    $normalized.receipts[1].receiptStatus = 'AliasBound'
    $normalized.receipts[2].receiptStatus = 'AliasBound'
    $normalized.receipts[2].receiptFingerprint = $aReceipts[2].receiptFingerprint
    if (($normalized | ConvertTo-Json -Depth 32 -Compress) -cne ($a | ConvertTo-Json -Depth 32 -Compress)) { return $false }
    if ($null -ne $pending) {
        if ($pending.predecessorAuthorityId -cne $original.authorityId -or $pending.authorityId -cne $successor.authorityId) { return $false }
        $normalizedPending = $pending.runtimeUnlockRetry | ConvertTo-Json -Depth 32 -Compress | ConvertFrom-Json
        $normalizedPending.role = 'attempt_installed'; $normalizedPending.receipts[1].receiptStatus = 'Installed'
        if (($normalizedPending | ConvertTo-Json -Depth 32 -Compress) -cne ($b | ConvertTo-Json -Depth 32 -Compress)) { return $false }
    }
    $bInstall = @($b.receipts | Where-Object stage -CEQ 'install')
    return $bInstall.Count -eq 1 -and [long]$bInstall[0].receiptGeneration -gt [long]$aReceipts[1].receiptGeneration
}

function Get-FileTransferCoordinatorSuccessfulRetryRetirementShape {
    param([Parameter(Mandatory = $true)]$Envelope)

    $aggregate = $Envelope.aggregate
    $operations = @($aggregate.operations)
    if ($Envelope.decisionKind -cne 'ObserveRuntimeUnlockRetryDispatchSettlement' -or
        $Envelope.decisionStatus -cne 'Accepted' -or
        $Envelope.decisionReason -cne 'retry_dispatch_settlement_superseded_predecessor' -or
        $operations.Count -ne 2 -or
        [long]$aggregate.afterStateGeneration -ne ([long]$aggregate.beforeStateGeneration + 1L) -or
        $aggregate.beforeStateFingerprint -ceq $aggregate.afterStateFingerprint -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $aggregate.beforeBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $aggregate.afterBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $aggregate.beforeBoundary $aggregate.afterBoundary)) { return $null }
    $retained = @($operations | Where-Object {
        $_.operationRole -ceq 'attempt_retained_predecessor' -and $_.beforeState -ceq 'Settled' -and $_.afterState -ceq 'None'
    })
    $terminal = @($operations | Where-Object {
        $_.operationRole -ceq 'attempt_terminal' -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Settled'
    })
    if ($retained.Count -ne 1 -or $terminal.Count -ne 1) { return $null }
    foreach ($operation in $operations) {
        $payload = $operation.runtimeUnlockRetry
        if ($operation.family -cne 'runtime_unlock_retry_lifecycle' -or $operation.operationKind -cne 'RetryAttempt' -or
            $operation.stateApplied -isnot [bool] -or -not $operation.stateApplied -or
            $operation.receiptValidated -isnot [bool] -or -not $operation.receiptValidated -or $null -eq $payload -or
            $payload.role -cne $operation.operationRole -or $payload.dispatchObserved -isnot [bool] -or $payload.dispatchObserved -or
            $payload.requestActivePredecessorSupersession -isnot [bool] -or
            [long]$operation.operationGeneration -le 0 -or [long]$operation.operationGeneration -ne [long]$payload.authorityGeneration -or
            $operation.operationKey -cne "runtime_unlock_retry_lifecycle|$($operation.operationRole)|$($operation.authorityId)|$($operation.operationGeneration)|RetryAttempt" -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $payload.sourceBoundary) -or
            [long]$payload.offerGeneration -le 0 -or $payload.successorAuthorityId -in @('', '(none)', $operation.authorityId)) { return $null }
        $receipts = @($payload.receipts)
        if ($receipts.Count -ne 4 -or ($receipts.stage -join '|') -cne 'stage|install|execution|settlement' -or
            $receipts[1].receiptStatus -cne 'Settled' -or $receipts[2].receiptStatus -cne 'Settled' -or
            $receipts[3].receiptStatus -cne 'Superseded' -or $receipts[3].receiptReason -cne 'coordinator_successor_installed' -or
            $receipts[3].commandIdentity -cne $receipts[3].receiptIdentity -or
            [long]$receipts[3].receiptGeneration -ne [long]$payload.offerGeneration) { return $null }
        foreach ($receipt in $receipts) {
            if ($receipt.receiptValidated -isnot [bool] -or -not $receipt.receiptValidated -or
                [long]$receipt.commandGeneration -le 0 -or [long]$receipt.commandGeneration -ne [long]$receipt.receiptGeneration) { return $null }
            foreach ($field in @('commandIdentity', 'commandFingerprint', 'receiptIdentity', 'receiptFingerprint')) {
                if ([string]::IsNullOrWhiteSpace([string]$receipt.$field) -or $receipt.$field -ceq '(none)') { return $null }
            }
        }
        foreach ($field in @('commandIdentity', 'commandGeneration', 'commandFingerprint', 'receiptIdentity',
                'receiptGeneration', 'receiptStatus', 'receiptFingerprint', 'receiptValidated')) {
            if ([string]$operation.$field -cne [string]$receipts[-1].$field) { return $null }
        }
    }
    $normalized = $terminal[0] | ConvertTo-Json -Depth 32 -Compress | ConvertFrom-Json
    $normalized.operationRole = $retained[0].operationRole; $normalized.operationKey = $retained[0].operationKey
    $normalized.beforeState = 'Settled'; $normalized.afterState = 'None'
    $normalized.runtimeUnlockRetry.role = $retained[0].operationRole
    if (($normalized | ConvertTo-Json -Depth 32 -Compress) -cne ($retained[0] | ConvertTo-Json -Depth 32 -Compress)) { return $null }
    return [pscustomobject]@{ Retained=$retained[0]; Terminal=$terminal[0] }
}

function Get-FileTransferCoordinatorSuccessfulRetrySuccessorSettlementShape {
    param([Parameter(Mandatory = $true)]$Envelope)

    $aggregate = $Envelope.aggregate
    $operations = @($aggregate.operations)
    if ($Envelope.decisionKind -cne 'ObserveRuntimeUnlockRetryDispatchSettlement' -or
        $Envelope.decisionStatus -cne 'Accepted' -or $Envelope.decisionReason -cne 'retry_dispatch_settlement_accepted' -or
        $operations.Count -ne 3 -or [long]$aggregate.afterStateGeneration -ne ([long]$aggregate.beforeStateGeneration + 1L) -or
        $aggregate.beforeStateFingerprint -ceq $aggregate.afterStateFingerprint -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $aggregate.beforeBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $aggregate.afterBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $aggregate.beforeBoundary $aggregate.afterBoundary)) { return $null }
    $removed = @($operations | Where-Object {
        $_.operationRole -ceq 'attempt_terminal' -and $_.beforeState -ceq 'Settled' -and $_.afterState -ceq 'None'
    })
    $installed = @($operations | Where-Object {
        $_.operationRole -ceq 'attempt_installed' -and $_.beforeState -ceq 'SendInFlight' -and $_.afterState -ceq 'None'
    })
    $terminal = @($operations | Where-Object {
        $_.operationRole -ceq 'attempt_terminal' -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Settled'
    })
    if ($removed.Count -ne 1 -or $installed.Count -ne 1 -or $terminal.Count -ne 1) { return $null }
    foreach ($operation in $operations) {
        $payload = $operation.runtimeUnlockRetry
        if ($operation.family -cne 'runtime_unlock_retry_lifecycle' -or $operation.operationKind -cne 'RetryAttempt' -or
            $operation.stateApplied -isnot [bool] -or -not $operation.stateApplied -or
            $operation.receiptValidated -isnot [bool] -or -not $operation.receiptValidated -or $null -eq $payload -or
            $payload.role -cne $operation.operationRole -or $operation.authorityId -in @('', '(none)') -or
            [long]$operation.operationGeneration -le 0 -or [long]$operation.operationGeneration -ne [long]$payload.authorityGeneration -or
            $operation.operationKey -cne "runtime_unlock_retry_lifecycle|$($operation.operationRole)|$($operation.authorityId)|$($operation.operationGeneration)|RetryAttempt" -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $payload.sourceBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $payload.sourceBoundary $removed[0].runtimeUnlockRetry.sourceBoundary)) { return $null }
        $receipts = @($payload.receipts)
        if ($receipts.Count -lt 3) { return $null }
        foreach ($receipt in $receipts) {
            if ($receipt.receiptValidated -isnot [bool] -or -not $receipt.receiptValidated -or
                [long]$receipt.commandGeneration -le 0 -or [long]$receipt.commandGeneration -ne [long]$receipt.receiptGeneration) { return $null }
            foreach ($field in @('commandIdentity','commandFingerprint','receiptIdentity','receiptFingerprint')) {
                if ([string]::IsNullOrWhiteSpace([string]$receipt.$field) -or $receipt.$field -ceq '(none)') { return $null }
            }
        }
        foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity',
                'receiptGeneration','receiptStatus','receiptFingerprint','receiptValidated')) {
            if ([string]$operation.$field -cne [string]$receipts[-1].$field) { return $null }
        }
    }
    $a = $removed[0]; $b = $terminal[0]; $current = $installed[0]
    $aReceipts = @($a.runtimeUnlockRetry.receipts); $bReceipts = @($b.runtimeUnlockRetry.receipts)
    $currentReceipts = @($current.runtimeUnlockRetry.receipts)
    if ($a.authorityId -ceq $b.authorityId -or [long]$a.operationGeneration -ge [long]$b.operationGeneration -or
        $a.runtimeUnlockRetry.successorAuthorityId -cne $b.authorityId -or $b.predecessorAuthorityId -cne $a.authorityId -or
        $current.authorityId -cne $b.authorityId -or $current.predecessorAuthorityId -cne $a.authorityId -or
        [long]$current.operationGeneration -ne [long]$b.operationGeneration -or
        $a.runtimeUnlockRetry.dispatchObserved -isnot [bool] -or $a.runtimeUnlockRetry.dispatchObserved -or
        $b.runtimeUnlockRetry.dispatchObserved -isnot [bool] -or -not $b.runtimeUnlockRetry.dispatchObserved -or
        $current.runtimeUnlockRetry.dispatchObserved -isnot [bool] -or $current.runtimeUnlockRetry.dispatchObserved -or
        ($aReceipts.stage -join '|') -cne 'stage|install|execution|settlement' -or
        ($bReceipts.stage -join '|') -cne 'stage|install|execution|settlement' -or
        ($currentReceipts.stage -join '|') -cne 'stage|install|execution' -or
        $currentReceipts[1].receiptStatus -cne 'SendInFlight' -or $currentReceipts[2].receiptStatus -cne 'SendInFlight' -or
        $bReceipts[1].receiptStatus -cne 'Settled' -or $bReceipts[2].receiptStatus -cne 'Settled' -or
        $bReceipts[3].commandIdentity -cne $bReceipts[3].receiptIdentity -or
        $aReceipts[1].receiptStatus -cne 'Settled' -or $aReceipts[2].receiptStatus -cne 'Settled' -or
        $aReceipts[3].receiptStatus -cne 'Superseded' -or $aReceipts[3].receiptReason -cne 'coordinator_successor_installed' -or
        [long]$aReceipts[3].receiptGeneration -ne [long]$a.runtimeUnlockRetry.offerGeneration -or
        $bReceipts[3].receiptStatus -cne 'DispatchObserved' -or $bReceipts[3].receiptReason -cne 'dispatch_observed' -or
        [long]$bReceipts[3].receiptGeneration -ne [long]$b.runtimeUnlockRetry.offerGeneration -or
        [long]$b.runtimeUnlockRetry.offerGeneration -le 0 -or
        ($b.runtimeUnlockRetry | Select-Object * -ExcludeProperty role,receipts,dispatchObserved | ConvertTo-Json -Depth 32 -Compress) -cne
        ($current.runtimeUnlockRetry | Select-Object * -ExcludeProperty role,receipts,dispatchObserved | ConvertTo-Json -Depth 32 -Compress)) { return $null }
    foreach ($subject in @($current, $b)) {
        $receipts = @($subject.runtimeUnlockRetry.receipts)
        $execution = $receipts[2]
        if ([long]$execution.commandGeneration -ne [long]$b.runtimeUnlockRetry.offerGeneration -or
            [long]$execution.receiptGeneration -ne [long]$b.runtimeUnlockRetry.offerGeneration -or
            $execution.commandFingerprint -cne $receipts[1].receiptFingerprint) { return $null }
        foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity','receiptGeneration')) {
            if ([string]$execution.$field -cne [string]$currentReceipts[2].$field) { return $null }
        }
    }
    return [pscustomobject]@{ Removed=$a; Installed=$current; Terminal=$b }
}

function Test-FileTransferCoordinatorSuccessfulRetryRetirementObservation {
    param([Parameter(Mandatory = $true)]$Terminal, [AllowEmptyCollection()][object[]]$Effects)
    $receipt = @($Terminal.Operation.runtimeUnlockRetry.receipts | Where-Object stage -CEQ 'settlement')[0]
    $observations = @($Effects | Where-Object {
        $_.EffectKind -ceq 'settlement_observed' -and $_.Writer -ceq $Terminal.Boundary.writer -and
        $_.ReceiptIdentity -ceq $receipt.receiptIdentity -and [long]$_.ReceiptGeneration -eq [long]$receipt.receiptGeneration -and
        $_.ReceiptStatus -ceq 'Superseded' -and $_.ReceiptFingerprint -ceq $receipt.receiptFingerprint -and
        (Test-FileTransferCoordinatorSnapshotExactEffectBinding -Boundary $Terminal.Boundary -Operation $Terminal.Operation -Effect $_)
    })
    return $observations.Count -eq 1
}

function Get-FileTransferCoordinatorSuccessfulRetryRetirementAttempt {
    param([Parameter(Mandatory = $true)]$Envelope)
    $aggregate = $Envelope.aggregate; $operations = @($aggregate.operations)
    if ($Envelope.decisionKind -cne 'ObserveRuntimeUnlockRetryDispatchSettlement' -or
        -not (($Envelope.decisionStatus -ceq 'Idempotent' -and $Envelope.decisionReason -ceq 'retry_dispatch_settlement_duplicate') -or
              ($Envelope.decisionStatus -ceq 'Rejected' -and $Envelope.decisionReason -ceq 'retry_dispatch_settlement_execution_proof_mismatch')) -or
        $operations.Count -ne 1 -or [long]$aggregate.beforeStateGeneration -ne [long]$aggregate.afterStateGeneration -or
        $aggregate.beforeStateFingerprint -cne $aggregate.afterStateFingerprint -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $aggregate.beforeBoundary $aggregate.afterBoundary)) { return $null }
    $operation = $operations[0]
    if ($operation.family -cne 'runtime_unlock_retry_lifecycle' -or $operation.operationRole -cne 'attempt_terminal' -or
        $operation.operationKind -cne 'RetryAttempt' -or $operation.beforeState -cne 'Settled' -or $operation.afterState -cne 'Settled' -or
        $operation.stateApplied -isnot [bool] -or $operation.stateApplied -or
        $operation.receiptValidated -isnot [bool] -or -not $operation.receiptValidated -or
        $operation.receiptStatus -cne $Envelope.decisionStatus -or $null -eq $operation.runtimeUnlockRetry -or
        [long]$operation.commandGeneration -ne [long]$operation.runtimeUnlockRetry.offerGeneration -or
        [long]$operation.receiptGeneration -ne [long]$operation.commandGeneration -or [long]$operation.commandGeneration -le 0 -or
        @($operation.runtimeUnlockRetry.receipts | Where-Object { $_.stage -ceq 'settlement' -and $_.receiptStatus -ceq 'Superseded' }).Count -ne 1) { return $null }
    foreach ($field in @('commandIdentity', 'commandFingerprint', 'receiptIdentity', 'receiptFingerprint')) {
        if ([string]::IsNullOrWhiteSpace([string]$operation.$field) -or $operation.$field -ceq '(none)') { return $null }
    }
    return $operation
}

function Get-FileTransferCoordinatorRetryConfirmationCompensationShape {
    param([Parameter(Mandatory = $true)]$Envelope)

    if ($Envelope.decisionKind -cne 'CompensateRejectedRuntimeUnlockRetryDispatchConfirmation') { return $null }
    $aggregate = $Envelope.aggregate
    $operations = @($aggregate.operations)
    if ($operations.Count -eq 0 -or @($operations | Where-Object {
            $_.family -cne 'runtime_unlock_retry_lifecycle' -or $null -eq $_.runtimeUnlockRetry
        }).Count -gt 0 -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $aggregate.beforeBoundary $aggregate.afterBoundary)) { return $null }
    if ($Envelope.decisionStatus -in @('Rejected', 'Idempotent')) {
        $reasonValid = if ($Envelope.decisionStatus -ceq 'Idempotent') {
            $Envelope.decisionReason -ceq 'retry_dispatch_confirmation_already_compensated'
        } else {
            $Envelope.decisionReason -in @('retry_dispatch_confirmation_compensation_scope_mismatch',
                'retry_dispatch_confirmation_compensation_install_mismatch',
                'retry_dispatch_confirmation_compensation_predecessor_mismatch')
        }
        if (-not $reasonValid -or $operations.Count -ne 1 -or
            [long]$aggregate.beforeStateGeneration -ne [long]$aggregate.afterStateGeneration -or
            $aggregate.beforeStateFingerprint -cne $aggregate.afterStateFingerprint -or
            [bool]$operations[0].stateApplied -or $operations[0].beforeState -cne $operations[0].afterState -or
            $operations[0].receiptStatus -cne $Envelope.decisionStatus) { return $null }
        return [pscustomobject]@{ Applied = $false; Operations = $operations }
    }
    if ($Envelope.decisionStatus -cne 'Accepted' -or
        $Envelope.decisionReason -cne 'retry_dispatch_install_confirmation_rejected' -or
        [long]$aggregate.afterStateGeneration -ne ([long]$aggregate.beforeStateGeneration + 1L) -or
        $aggregate.afterStateFingerprint -ceq $aggregate.beforeStateFingerprint -or
        $operations.Count -notin @(2, 4) -or @($operations | Where-Object {
            -not [bool]$_.stateApplied -or $_.operationKind -cne 'RetryAttempt' -or
            $_.runtimeUnlockRetry.role -cne $_.operationRole -or
            [long]$_.operationGeneration -ne [long]$_.runtimeUnlockRetry.authorityGeneration
        }).Count -gt 0) { return $null }
    $failed = @($operations | Where-Object {
        $_.operationRole -ceq 'attempt_installed' -and $_.beforeState -ceq 'Installed' -and $_.afterState -ceq 'None'
    })
    $terminal = @($operations | Where-Object {
        $_.operationRole -ceq 'attempt_terminal' -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Invalidated'
    })
    if ($failed.Count -ne 1 -or $terminal.Count -ne 1 -or
        $failed[0].authorityId -cne $terminal[0].authorityId -or
        $failed[0].predecessorAuthorityId -cne $terminal[0].predecessorAuthorityId -or
        [long]$failed[0].operationGeneration -ne [long]$terminal[0].operationGeneration) { return $null }
    foreach ($operation in $operations) {
        $receipts = @($operation.runtimeUnlockRetry.receipts)
        if ($receipts.Count -lt 2 -or @($receipts | Where-Object { -not [bool]$_.receiptValidated }).Count -gt 0) { return $null }
        $last = $receipts[-1]
        foreach ($field in @('commandIdentity', 'commandGeneration', 'commandFingerprint', 'receiptIdentity',
                'receiptGeneration', 'receiptStatus', 'receiptFingerprint', 'receiptValidated')) {
            if ([string]$operation.$field -cne [string]$last.$field) { return $null }
        }
    }
    $failedReceipts = @($failed[0].runtimeUnlockRetry.receipts)
    $terminalReceipts = @($terminal[0].runtimeUnlockRetry.receipts)
    if ($failedReceipts.Count -ne 2 -or $terminalReceipts.Count -ne 2 -or
        $failedReceipts[0].stage -cne 'stage' -or $failedReceipts[1].stage -cne 'install' -or
        $failedReceipts[1].receiptStatus -cne 'Installed' -or
        $terminalReceipts[1].stage -cne 'install' -or $terminalReceipts[1].receiptStatus -cne 'Invalidated') { return $null }
    $normalizedTerminal = $terminal[0].runtimeUnlockRetry | ConvertTo-Json -Depth 32 -Compress | ConvertFrom-Json
    $normalizedTerminal.role = 'attempt_installed'
    $normalizedTerminal.receipts[1].receiptStatus = 'Installed'
    if (($normalizedTerminal | ConvertTo-Json -Depth 32 -Compress) -cne
        ($failed[0].runtimeUnlockRetry | ConvertTo-Json -Depth 32 -Compress)) { return $null }
    $restored = @($operations | Where-Object {
        $_.operationRole -ceq 'attempt_installed' -and $_.beforeState -ceq 'None' -and
        $_.afterState -in @('Installed', 'AliasBound', 'SendInFlight', 'Settled')
    })
    $displaced = @($operations | Where-Object {
        $_.operationRole -in @('attempt_terminal', 'attempt_retained_predecessor') -and
        $_.beforeState -ceq 'Settled' -and $_.afterState -ceq 'None'
    })
    if (($operations.Count -eq 2 -and ($restored.Count -ne 0 -or $displaced.Count -ne 0)) -or
        ($operations.Count -eq 4 -and ($restored.Count -ne 1 -or $displaced.Count -ne 1 -or
            $restored[0].authorityId -ceq $failed[0].authorityId -or
            $restored[0].authorityId -cne $displaced[0].authorityId -or
            [long]$restored[0].operationGeneration -ne [long]$displaced[0].operationGeneration))) { return $null }
    return [pscustomobject]@{ Applied = $true; Failed = $failed[0]; Terminal = $terminal[0];
        Restored = $restored; Displaced = $displaced; Operations = $operations }
}

function Test-FileTransferCoordinatorRetryConfirmationCompensationHistory {
    param([Parameter(Mandatory = $true)]$Envelope, [Parameter(Mandatory = $true)]$Boundary,
        [AllowEmptyCollection()][object[]]$PriorEntries)

    $shape = Get-FileTransferCoordinatorRetryConfirmationCompensationShape $Envelope
    if ($null -eq $shape) { return $false }
    if (-not $shape.Applied) { return $true }
    $commits = @($PriorEntries | Where-Object {
        $_.Boundary.coordinatorInstanceId -ceq $Boundary.coordinatorInstanceId -and
        $_.Boundary.writer -ceq $Boundary.writer -and $_.SessionId -ceq $Boundary.sessionId -and
        $_.TransferId -ceq $Boundary.transferId -and $_.Direction -ceq $Boundary.direction -and
        $_.Boundary.decisionKind -ceq 'CommitRuntimeUnlockRetryDispatchInstall' -and
        $_.Boundary.decisionStatus -ceq 'Accepted' -and
        $_.Boundary.decisionReason -ceq 'retry_dispatch_install_committed' -and
        [long]$_.Boundary.afterStateGeneration -eq [long]$Boundary.beforeStateGeneration -and
        $_.Boundary.afterStateHash -ceq $Boundary.beforeStateHash -and
        (Test-FileTransferCoordinatorSnapshotBoundaryEqual $_.AfterBoundary $Boundary.beforeBoundary)
    })
    if ($commits.Count -ne 1) { return $false }
    $commit = $commits[0].Boundary
    foreach ($operation in @($shape.Failed) + @($shape.Restored) + @($shape.Displaced | Where-Object operationRole -CEQ 'attempt_retained_predecessor')) {
        $old = @($commit.operations | Where-Object {
            $_.operationKey -ceq $operation.operationKey -and [bool]$_.stateApplied -and
            $_.beforeState -ceq $operation.afterState -and $_.afterState -ceq $operation.beforeState -and
            ($_.runtimeUnlockRetry | ConvertTo-Json -Depth 32 -Compress) -ceq
                ($operation.runtimeUnlockRetry | ConvertTo-Json -Depth 32 -Compress)
        })
        if ($old.Count -ne 1) { return $false }
    }
    if ($shape.Displaced.Count -eq 1 -and $shape.Displaced[0].operationRole -ceq 'attempt_retained_predecessor') {
        $commitEnvelope = [pscustomobject]@{ decisionKind=$commit.decisionKind; decisionStatus=$commit.decisionStatus;
            decisionReason=$commit.decisionReason; aggregate=[pscustomobject]@{
                beforeStateGeneration=$commit.beforeStateGeneration; afterStateGeneration=$commit.afterStateGeneration;
                beforeStateFingerprint=$commit.beforeStateHash; afterStateFingerprint=$commit.afterStateHash;
                beforeBoundary=$commit.beforeBoundary; afterBoundary=$commits[0].AfterBoundary; operations=$commit.operations
            } }
        $retainedAtCommit = @($commit.operations | Where-Object operationKey -CEQ $shape.Displaced[0].operationKey)
        if ($retainedAtCommit.Count -ne 1 -or -not (Test-FileTransferCoordinatorSnapshotProvisionalRetrySupersession `
                -Envelope $commitEnvelope -Operation $retainedAtCommit[0])) { return $false }
    }
    if ($shape.Displaced.Count -eq 1 -and $shape.Displaced[0].operationRole -ceq 'attempt_terminal') {
        # MissingDispatch already owns the terminal slot before the successor
        # commit. That unchanged descriptor is not re-emitted by the commit.
        $displaced = $shape.Displaced[0]
        $terminalHistory = @($PriorEntries | Where-Object {
            $_.Boundary.coordinatorInstanceId -ceq $Boundary.coordinatorInstanceId -and
            $_.Boundary.writer -ceq $Boundary.writer -and $_.SessionId -ceq $Boundary.sessionId -and
            $_.TransferId -ceq $Boundary.transferId -and $_.Direction -ceq $Boundary.direction -and
            [long]$_.Boundary.afterStateGeneration -le [long]$commit.beforeStateGeneration
        } | ForEach-Object {
            $priorBoundary = $_.Boundary
            foreach ($operation in @($priorBoundary.operations | Where-Object {
                $_.operationKey -ceq $displaced.operationKey -and [bool]$_.stateApplied
            })) { [pscustomobject]@{ Boundary=$priorBoundary; Operation=$operation } }
        })
        # Exactly one None -> Settled establishes this operation key. Any later
        # applied mutation/removal/restore before B commits poisons the carry.
        if ($terminalHistory.Count -ne 1) { return $false }
        $origin = $terminalHistory[0]
        if ($origin.Operation.beforeState -cne 'None' -or $origin.Operation.afterState -cne 'Settled' -or
            $origin.Boundary.decisionKind -notin @('EvaluateRuntimeUnlockRetryDispatchDeadline', 'ObserveRuntimeUnlockRetryDispatchSettlement') -or
            $origin.Boundary.decisionStatus -cne 'Accepted' -or
            $origin.Boundary.decisionReason -cne 'retry_dispatch_settlement_accepted' -or
            ($origin.Operation.runtimeUnlockRetry | ConvertTo-Json -Depth 32 -Compress) -cne
                ($displaced.runtimeUnlockRetry | ConvertTo-Json -Depth 32 -Compress) -or
            @($displaced.runtimeUnlockRetry.receipts | Where-Object {
                $_.stage -ceq 'settlement' -and $_.receiptStatus -ceq 'MissingDispatch' -and [bool]$_.receiptValidated
            }).Count -ne 1) { return $false }
        $restoredPayload = $shape.Restored[0].runtimeUnlockRetry | ConvertTo-Json -Depth 32 -Compress | ConvertFrom-Json
        $restoredPayload.role = 'attempt_terminal'
        if (($restoredPayload | ConvertTo-Json -Depth 32 -Compress) -cne
            ($displaced.runtimeUnlockRetry | ConvertTo-Json -Depth 32 -Compress)) { return $false }
    }
    return $true
}

function Get-FileTransferCoordinatorProvisionalRetryScopeKey {
    param($Boundary)
    return @($Boundary.coordinatorInstanceId, $Boundary.writer, $Boundary.sessionId,
        $Boundary.transferId, $Boundary.direction) -join [char]31
}

function Get-FileTransferCoordinatorProvisionalRetryReduction {
    param([object[]]$AggregateEntries, [object[]]$OperationHistory, [switch]$DiagnosticOnly)

    $result = [pscustomobject]@{ Participants=@{}; CancelledReceipts=@{}; SuccessfulRetirements=@{}; TerminalEvictions=@{}; InvalidScopes=@{};
        Findings=[Collections.Generic.List[object]]::new(); Errors=[Collections.Generic.List[string]]::new() }
    $scopes = @{}; $admittedCommits = @{}
    foreach ($entry in $AggregateEntries) {
        $boundary = $entry.Boundary
        if ($boundary.decisionKind -cne 'CommitRuntimeUnlockRetryDispatchInstall' -or $boundary.decisionStatus -cne 'Accepted') { continue }
        $retained = @($boundary.operations | Where-Object {
            $_.operationRole -ceq 'attempt_retained_predecessor' -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Settled'
        })
        if ($retained.Count -ne 1) { continue }
        $envelope = [pscustomobject]@{ decisionKind=$boundary.decisionKind; decisionStatus=$boundary.decisionStatus;
            decisionReason=$boundary.decisionReason; aggregate=[pscustomobject]@{
                beforeStateGeneration=$boundary.beforeStateGeneration; afterStateGeneration=$boundary.afterStateGeneration;
                beforeStateFingerprint=$boundary.beforeStateHash; afterStateFingerprint=$boundary.afterStateHash;
                beforeBoundary=$entry.BeforeBoundary; afterBoundary=$entry.AfterBoundary; operations=$boundary.operations
            } }
        if (-not (Test-FileTransferCoordinatorSnapshotProvisionalRetrySupersession $envelope $retained[0])) { continue }
        $scopeKey = Get-FileTransferCoordinatorProvisionalRetryScopeKey $boundary
        $admittedCommits["$scopeKey$([char]31)$($boundary.boundaryId)"] = $true
        if (-not $scopes.ContainsKey($scopeKey)) { $scopes[$scopeKey] = [Collections.Generic.List[object]]::new() }
        $scopes[$scopeKey].Add([pscustomobject]@{ Entry=$entry; Retained=$retained[0] })
    }
    if ($DiagnosticOnly) {
        # A rejected active-commit catalog claim is not a substitute role history.
        # Keep its artifact error, without falling back to descriptor-order inference.
        foreach ($row in @($OperationHistory | Where-Object {
            $_.Boundary.decisionKind -ceq 'CommitRuntimeUnlockRetryDispatchInstall' -and
            $_.Boundary.decisionStatus -ceq 'Accepted' -and $_.Operation.family -ceq 'runtime_unlock_retry_lifecycle' -and
            $_.Operation.operationRole -ceq 'attempt_retained_predecessor' -and
            $_.Operation.beforeState -ceq 'None' -and $_.Operation.afterState -ceq 'Settled' -and
            $null -ne $_.Operation.runtimeUnlockRetry -and
            @($_.Operation.runtimeUnlockRetry.receipts | Where-Object {
                $_.stage -ceq 'settlement' -and $_.receiptStatus -ceq 'Superseded'
            }).Count -eq 1
        })) {
            $scopeKey = Get-FileTransferCoordinatorProvisionalRetryScopeKey $row.Boundary
            if (-not $admittedCommits.ContainsKey("$scopeKey$([char]31)$($row.Boundary.boundaryId)")) {
                $result.InvalidScopes[$scopeKey] = $true
            }
        }
    }
    foreach ($scopeKey in @($scopes.Keys)) {
        if ($result.InvalidScopes.ContainsKey($scopeKey)) { continue }
        $commits = @($scopes[$scopeKey])
        $scopeEntries = @($AggregateEntries | Where-Object {
            (Get-FileTransferCoordinatorProvisionalRetryScopeKey $_.Boundary) -ceq $scopeKey
        })
        $members = @{}; $reopens = @{}; $commitIds = @{}; $inverseIds = @{}
        $cancellations = [Collections.Generic.List[object]]::new()
        $terminalOrigins = @{}; $terminalOwner = $null; $successfulTerminalOwner = $null
        $evictions = [Collections.Generic.List[object]]::new(); $successfulRetirements = [Collections.Generic.List[object]]::new()
        $successfulEvictions = [Collections.Generic.List[object]]::new()
        $poison = $null
        foreach ($commit in $commits) {
            $commitIds[[string]$commit.Entry.Boundary.boundaryId] = $true
            foreach ($operation in @($commit.Entry.Boundary.operations | Where-Object operationRole -CEQ 'attempt_installed')) {
                $authority = [string]$operation.authorityId
                $immutable = $operation.runtimeUnlockRetry | Select-Object * -ExcludeProperty role, receipts,
                    dispatchDeadlineUtcMs, offerGeneration, offerNonceFingerprint, dispatchObserved, successorAuthorityId |
                    ConvertTo-Json -Depth 32 -Compress
                if ($members.ContainsKey($authority)) {
                    if ($members[$authority].Generation -ne $operation.operationGeneration -or
                        $members[$authority].Immutable -cne $immutable) { $poison = $commit.Entry.Boundary }
                } else {
                    $members[$authority] = [pscustomobject]@{ Generation=$operation.operationGeneration; Immutable=$immutable;
                        Slots=@{}; Stage=$null; Install=$null; Offer=$null; OpenRoles=@() }
                }
            }
        }
        foreach ($entry in $scopeEntries) {
            $boundary = $entry.Boundary
            if ($boundary.decisionKind -cne 'CompensateRejectedRuntimeUnlockRetryDispatchConfirmation' -or
                $boundary.decisionStatus -cne 'Accepted') { continue }
            $envelope = [pscustomobject]@{ decisionKind=$boundary.decisionKind; decisionStatus=$boundary.decisionStatus;
                decisionReason=$boundary.decisionReason; aggregate=[pscustomobject]@{
                    beforeStateGeneration=$boundary.beforeStateGeneration; afterStateGeneration=$boundary.afterStateGeneration;
                    beforeStateFingerprint=$boundary.beforeStateHash; afterStateFingerprint=$boundary.afterStateHash;
                    beforeBoundary=$entry.BeforeBoundary; afterBoundary=$entry.AfterBoundary; operations=$boundary.operations
                } }
            $shape = Get-FileTransferCoordinatorRetryConfirmationCompensationShape $envelope
            if ($null -eq $shape -or -not $shape.Applied -or $shape.Displaced.Count -ne 1 -or
                $shape.Displaced[0].operationRole -cne 'attempt_retained_predecessor' -or
                -not (Test-FileTransferCoordinatorRetryConfirmationCompensationHistory $envelope $boundary $scopeEntries)) { continue }
            $origins = @($commits | Where-Object {
                $_.Entry.Boundary.afterStateGeneration -eq $boundary.beforeStateGeneration -and
                $_.Entry.Boundary.afterStateHash -ceq $boundary.beforeStateHash -and
                $_.Retained.operationKey -ceq $shape.Displaced[0].operationKey
            })
            if ($origins.Count -ne 1) { $poison = $boundary; continue }
            $reopens["$($boundary.boundaryId)|$($shape.Restored[0].operationKey)"] = $true
            $inverseIds[[string]$boundary.boundaryId] = $true
            $cancellations.Add($origins[0])
            $installed = @($origins[0].Entry.Boundary.operations | Where-Object {
                $_.operationKey -ceq $shape.Failed.operationKey -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Installed'
            })
            if ($installed.Count -ne 1) { $poison = $boundary; continue }
            $terminalOrigins[[string]$boundary.boundaryId] = [pscustomobject]@{
                Commit=$origins[0].Entry.Boundary; Installed=$installed[0]; Compensation=$boundary;
                Terminal=$shape.Terminal; Restored=$shape.Restored[0];
                Identity=($shape.Terminal | Select-Object * -ExcludeProperty beforeState,afterState,stateApplied | ConvertTo-Json -Depth 32 -Compress)
            }
        }
        $rowsByBoundary = @{}
        foreach ($row in @($OperationHistory | Where-Object {
            (Get-FileTransferCoordinatorProvisionalRetryScopeKey $_.Boundary) -ceq $scopeKey -and
            $_.Operation.family -ceq 'runtime_unlock_retry_lifecycle' -and $_.Operation.operationKind -ceq 'RetryAttempt' -and
            ($members.ContainsKey([string]$_.Operation.authorityId) -or $_.Operation.operationRole -ceq 'attempt_terminal') -and
            [bool]$_.Operation.stateApplied
        })) {
            $id = [string]$row.Boundary.boundaryId
            if (-not $rowsByBoundary.ContainsKey($id)) { $rowsByBoundary[$id] = [Collections.Generic.List[object]]::new() }
            $rowsByBoundary[$id].Add($row)
        }
        $live = $false; $guardActive = $false; $liveScope = $null; $liveGeneration = 0L; $liveHash = $null
        foreach ($aggregateEntry in @($scopeEntries | Sort-Object {
                    [long]$_.Boundary.beforeStateGeneration
                }, { [long]$_.Boundary.afterStateGeneration }, { [string]$_.Boundary.boundaryId })) {
            $boundary = $aggregateEntry.Boundary; $id = [string]$boundary.boundaryId
            $allDeltas = @(if ($rowsByBoundary.ContainsKey($id)) { $rowsByBoundary[$id] })
            $deltas = @($allDeltas | Where-Object { $members.ContainsKey([string]$_.Operation.authorityId) })
            $terminalDeltas = @($allDeltas | Where-Object { $_.Operation.operationRole -ceq 'attempt_terminal' })
            # Initial installation may adopt its authority's bridge scope. This
            # guard owns only the admitted provisional supersession and its live inverse.
            if (-not $guardActive -and $commitIds.ContainsKey($id)) {
                $guardActive = $true; $liveScope = $aggregateEntry.BeforeBoundary
                $liveGeneration = [long]$boundary.beforeStateGeneration; $liveHash = [string]$boundary.beforeStateHash
            }
            if ($guardActive) {
                if ([long]$boundary.beforeStateGeneration -ne $liveGeneration -or
                    $boundary.beforeStateHash -cne $liveHash -or
                    -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $aggregateEntry.BeforeBoundary $liveScope) -or
                    -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $aggregateEntry.AfterBoundary $liveScope)) {
                    $poison = $boundary; break
                }
                $liveGeneration = [long]$boundary.afterStateGeneration; $liveHash = [string]$boundary.afterStateHash
            }
            if ($boundary.decisionKind -ceq 'ObserveRuntimeUnlockRetryDispatchSettlement' -and
                $boundary.decisionStatus -in @('Idempotent', 'Rejected') -and
                @($boundary.operations | Where-Object {
                    $_.operationRole -ceq 'attempt_terminal' -and $null -ne $_.runtimeUnlockRetry -and
                    @($_.runtimeUnlockRetry.receipts | Where-Object { $_.stage -ceq 'settlement' -and $_.receiptStatus -ceq 'Superseded' }).Count -gt 0
                }).Count -gt 0) {
                $attemptEnvelope = [pscustomobject]@{ decisionKind=$boundary.decisionKind; decisionStatus=$boundary.decisionStatus;
                    decisionReason=$boundary.decisionReason; aggregate=[pscustomobject]@{
                        beforeStateGeneration=$boundary.beforeStateGeneration; afterStateGeneration=$boundary.afterStateGeneration;
                        beforeStateFingerprint=$boundary.beforeStateHash; afterStateFingerprint=$boundary.afterStateHash;
                        beforeBoundary=$aggregateEntry.BeforeBoundary; afterBoundary=$aggregateEntry.AfterBoundary; operations=$boundary.operations
                    } }
                $attempt = Get-FileTransferCoordinatorSuccessfulRetryRetirementAttempt $attemptEnvelope
                if ($null -eq $attempt -or $null -eq $successfulTerminalOwner -or
                    $attempt.operationKey -cne $successfulTerminalOwner.Operation.operationKey -or
                    $attempt.predecessorAuthorityId -cne $successfulTerminalOwner.Operation.predecessorAuthorityId -or
                    ($attempt.runtimeUnlockRetry | ConvertTo-Json -Depth 32 -Compress) -cne
                    ($successfulTerminalOwner.Operation.runtimeUnlockRetry | ConvertTo-Json -Depth 32 -Compress)) {
                    $poison = $boundary; break
                }
            }
            if ($deltas.Count -eq 0 -and $terminalDeltas.Count -eq 0) { continue }
            if (@($deltas | Group-Object { $_.Operation.operationKey } | Where-Object Count -NE 1).Count -gt 0) {
                $poison = $boundary; break
            }
            # A restored installation closes only through its own accepted physical
            # dispatch settlement, never through a terminal-looking state name.
            $ownClosures = @{}
            if ($boundary.decisionKind -ceq 'ObserveRuntimeUnlockRetryDispatchSettlement' -and
                $boundary.decisionStatus -ceq 'Accepted' -and $boundary.decisionReason -ceq 'retry_dispatch_settlement_accepted') {
                foreach ($terminal in @($deltas | Where-Object {
                    $_.Operation.operationRole -ceq 'attempt_terminal' -and $_.Operation.beforeState -ceq 'None' -and
                    $_.Operation.afterState -ceq 'Settled'
                })) {
                    $operation = $terminal.Operation; $payload = $operation.runtimeUnlockRetry
                    $retirements = @($deltas | Where-Object {
                        $_.Operation.operationRole -ceq 'attempt_installed' -and
                        $_.Operation.authorityId -ceq $operation.authorityId -and
                        $_.Operation.beforeState -ceq 'SendInFlight' -and $_.Operation.afterState -ceq 'None'
                    })
                    $settlement = @($payload.receipts | Where-Object {
                        $_.stage -ceq 'settlement' -and $_.receiptStatus -ceq 'DispatchObserved' -and
                        $_.receiptReason -ceq 'dispatch_observed' -and $_.receiptValidated -and
                        $_.receiptGeneration -eq $payload.offerGeneration
                    })
                    if ($retirements.Count -eq 1 -and $settlement.Count -eq 1 -and $payload.dispatchObserved -and
                        [long]$payload.offerGeneration -gt 0 -and
                        (($payload | Select-Object * -ExcludeProperty role,receipts,dispatchObserved | ConvertTo-Json -Depth 32 -Compress) -ceq
                         ($retirements[0].Operation.runtimeUnlockRetry | Select-Object * -ExcludeProperty role,receipts,dispatchObserved | ConvertTo-Json -Depth 32 -Compress))) {
                        $ownClosures[[string]$operation.authorityId] = $terminal
                    }
                }
            }
            $successfulShape = $null; $successfulOrigin = $null
            if ($boundary.decisionKind -ceq 'ObserveRuntimeUnlockRetryDispatchSettlement' -and
                $boundary.decisionReason -ceq 'retry_dispatch_settlement_superseded_predecessor') {
                $successfulEnvelope = [pscustomobject]@{ decisionKind=$boundary.decisionKind; decisionStatus=$boundary.decisionStatus;
                    decisionReason=$boundary.decisionReason; aggregate=[pscustomobject]@{
                        beforeStateGeneration=$boundary.beforeStateGeneration; afterStateGeneration=$boundary.afterStateGeneration;
                        beforeStateFingerprint=$boundary.beforeStateHash; afterStateFingerprint=$boundary.afterStateHash;
                        beforeBoundary=$aggregateEntry.BeforeBoundary; afterBoundary=$aggregateEntry.AfterBoundary; operations=$boundary.operations
                    } }
                $successfulShape = Get-FileTransferCoordinatorSuccessfulRetryRetirementShape $successfulEnvelope
                if ($null -eq $successfulShape) { $poison = $boundary; break }
                $origins = @($commits | Where-Object {
                    ($_.Retained | Select-Object * -ExcludeProperty beforeState,afterState | ConvertTo-Json -Depth 32 -Compress) -ceq
                    ($successfulShape.Retained | Select-Object * -ExcludeProperty beforeState,afterState | ConvertTo-Json -Depth 32 -Compress)
                })
                $successorId = [string]$successfulShape.Retained.runtimeUnlockRetry.successorAuthorityId
                $successorSlots = @(if ($members.ContainsKey($successorId)) { $members[$successorId].Slots.Values | Where-Object {
                    $_.State -ceq 'Installed' -and $_.Entry.Operation.operationRole -ceq 'attempt_installed'
                } })
                if ($origins.Count -ne 1 -or $successorSlots.Count -ne 1 -or
                    $successorSlots[0].Entry.Boundary.boundaryId -cne $origins[0].Entry.Boundary.boundaryId -or
                    $null -ne $terminalOwner -or $null -ne $successfulTerminalOwner -or $terminalDeltas.Count -ne 1) {
                    $poison = $boundary; break
                }
                $successfulOrigin = $origins[0]
            }
            $successfulEviction = $null
            if ($null -ne $successfulTerminalOwner -and $terminalDeltas.Count -gt 0) {
                $owned = @($terminalDeltas | Where-Object {
                    ($_.Operation | Select-Object * -ExcludeProperty beforeState,afterState,stateApplied | ConvertTo-Json -Depth 32 -Compress) -ceq
                    ($successfulTerminalOwner.Operation | Select-Object * -ExcludeProperty beforeState,afterState,stateApplied | ConvertTo-Json -Depth 32 -Compress)
                })
                if ($terminalDeltas.Count -eq 1 -and $owned.Count -eq 1 -and
                    $owned[0].Operation.beforeState -ceq 'Settled' -and $owned[0].Operation.afterState -ceq 'Settled') {
                    # The consumed predecessor still occupies the terminal slot.
                }
                else {
                    $closureEnvelope = [pscustomobject]@{ decisionKind=$boundary.decisionKind; decisionStatus=$boundary.decisionStatus;
                        decisionReason=$boundary.decisionReason; aggregate=[pscustomobject]@{
                            beforeStateGeneration=$boundary.beforeStateGeneration; afterStateGeneration=$boundary.afterStateGeneration;
                            beforeStateFingerprint=$boundary.beforeStateHash; afterStateFingerprint=$boundary.afterStateHash;
                            beforeBoundary=$aggregateEntry.BeforeBoundary; afterBoundary=$aggregateEntry.AfterBoundary; operations=$boundary.operations
                        } }
                    $closureShape = Get-FileTransferCoordinatorSuccessfulRetrySuccessorSettlementShape $closureEnvelope
                    $successorId = [string]$successfulTerminalOwner.Operation.runtimeUnlockRetry.successorAuthorityId
                    if ($null -eq $closureShape -or $owned.Count -ne 1 -or $terminalDeltas.Count -ne 2 -or
                        $closureShape.Removed.operationKey -cne $owned[0].Operation.operationKey -or
                        $ownClosures.Count -ne 1 -or -not $ownClosures.ContainsKey($successorId) -or
                        $ownClosures[$successorId].Operation.operationKey -cne $closureShape.Terminal.operationKey) {
                        $poison = $boundary; break
                    }
                    $successfulEviction = [pscustomobject]@{ Entry=$owned[0]; Terminal=$successfulTerminalOwner;
                        Successor=$ownClosures[$successorId] }
                }
            }
            if ($guardActive -and $terminalDeltas.Count -gt 0 -and $null -eq $terminalOwner -and
                $null -eq $successfulTerminalOwner -and $null -eq $successfulShape -and
                -not $inverseIds.ContainsKey($id) -and $ownClosures.Count -eq 0) { $poison = $boundary; break }
            $nextTerminalOwner = $terminalOwner
            if ($null -ne $terminalOwner -and $terminalDeltas.Count -gt 0) {
                $owned = @($terminalDeltas | Where-Object {
                    $_.Operation.operationKey -ceq $terminalOwner.Terminal.operationKey -and
                    ($_.Operation | Select-Object * -ExcludeProperty beforeState,afterState,stateApplied | ConvertTo-Json -Depth 32 -Compress) -ceq $terminalOwner.Identity
                })
                $restoredId = [string]$terminalOwner.Restored.authorityId
                if ($terminalDeltas.Count -eq 1 -and $owned.Count -eq 1 -and
                    $owned[0].Operation.beforeState -ceq 'Invalidated' -and $owned[0].Operation.afterState -ceq 'Invalidated') {
                    # Exact unchanged carry retains the compensation's terminal owner.
                }
                elseif ($terminalDeltas.Count -eq 2 -and $owned.Count -eq 1 -and
                    $owned[0].Operation.beforeState -ceq 'Invalidated' -and $owned[0].Operation.afterState -ceq 'None' -and
                    $ownClosures.Count -eq 1 -and $ownClosures.ContainsKey($restoredId) -and
                    $ownClosures[$restoredId].Operation.operationGeneration -eq $terminalOwner.Restored.operationGeneration) {
                    $evictions.Add([pscustomobject]@{ Entry=$owned[0]; Origin=$terminalOwner })
                    $nextTerminalOwner = $null
                }
                else { $poison = $boundary; break }
            }
            if ($terminalOrigins.ContainsKey($id)) {
                $origin = $terminalOrigins[$id]
                if ($null -ne $terminalOwner -or $terminalDeltas.Count -ne 1 -or
                    ($terminalDeltas[0].Operation | ConvertTo-Json -Depth 32 -Compress) -cne
                    ($origin.Terminal | ConvertTo-Json -Depth 32 -Compress)) { $poison = $boundary; break }
                $nextTerminalOwner = $origin
            }
            $updates = [Collections.Generic.List[object]]::new()
            foreach ($row in $deltas) {
                $operation = $row.Operation; $member = $members[[string]$operation.authorityId]
                $payload = $operation.runtimeUnlockRetry
                $immutable = $payload | Select-Object * -ExcludeProperty role, receipts,
                    dispatchDeadlineUtcMs, offerGeneration, offerNonceFingerprint, dispatchObserved, successorAuthorityId |
                    ConvertTo-Json -Depth 32 -Compress
                if ($operation.operationGeneration -ne $member.Generation -or $immutable -cne $member.Immutable) { $poison = $row.Boundary; break }
                $slot = if ($member.Slots.ContainsKey($operation.operationKey)) { $member.Slots[$operation.operationKey] } else { $null }
                $beforeState = if ($null -eq $slot) { 'None' } else { [string]$slot.State }
                $payloadJson = $payload | ConvertTo-Json -Depth 32 -Compress
                $role = [string]$operation.operationRole
                $allowedRole = switch -Exact ($role) {
                    'attempt_pending' { $operation.afterState -in @('Authorized','Staged','ReceiptIssued','None'); break }
                    'attempt_installed' {
                        if ($operation.afterState -ceq 'None') {
                            $commitIds.ContainsKey($id) -or $inverseIds.ContainsKey($id) -or $ownClosures.ContainsKey([string]$operation.authorityId)
                        } elseif ($operation.afterState -in @('AliasBound','SendInFlight') -and $operation.beforeState -cne 'None') {
                            $boundary.decisionKind -ceq 'ObserveRuntimeUnlockRetryDispatchExecutionState' -and
                            $boundary.decisionStatus -ceq 'Accepted' -and
                            (($operation.beforeState -ceq 'Installed' -and $operation.afterState -ceq 'AliasBound') -or
                             ($operation.beforeState -ceq 'AliasBound' -and $operation.afterState -ceq 'SendInFlight'))
                        } else {
                            ($operation.beforeState -ceq 'None' -and $operation.afterState -ceq 'Installed' -and
                             $boundary.decisionKind -ceq 'CommitRuntimeUnlockRetryDispatchInstall' -and $boundary.decisionStatus -ceq 'Accepted') -or
                            $reopens.ContainsKey("$id|$($operation.operationKey)")
                        }
                        break
                    }
                    'attempt_retained_predecessor' { $commitIds.ContainsKey($id) -or $inverseIds.ContainsKey($id) -or $null -ne $successfulShape; break }
                    'attempt_terminal' {
                        $inverseIds.ContainsKey($id) -or $null -ne $successfulShape -or
                        ($null -ne $slot -and $operation.beforeState -ceq $operation.afterState -and
                         $operation.afterState -in @('Settled','Invalidated') -and $slot.Payload -ceq $payloadJson) -or
                        ($ownClosures.Count -eq 1 -and ($ownClosures.ContainsKey([string]$operation.authorityId) -or
                         ($operation.beforeState -ceq 'Invalidated' -and $operation.afterState -ceq 'None'))) -or
                        ($null -ne $successfulEviction -and $operation.operationKey -ceq $successfulEviction.Entry.Operation.operationKey)
                        break
                    }
                    default { $false }
                }
                if ($operation.stateApplied -isnot [bool] -or -not $allowedRole -or $operation.beforeState -cne $beforeState -or
                    ($null -ne $slot -and $beforeState -ceq 'None' -and $operation.afterState -cne 'None' -and
                     -not $reopens.ContainsKey("$($row.Boundary.boundaryId)|$($operation.operationKey)")) -or
                    ($operation.afterState -ceq 'None' -and ($null -eq $slot -or $slot.Payload -cne $payloadJson))) {
                    $poison = $row.Boundary; break
                }
                foreach ($receipt in @($payload.receipts | Where-Object { $_.stage -in @('stage', 'install') })) {
                    $signature = if ($receipt.stage -ceq 'install') {
                        $receipt | Select-Object * -ExcludeProperty receiptStatus | ConvertTo-Json -Depth 16 -Compress
                    } else { $receipt | ConvertTo-Json -Depth 16 -Compress }
                    $property = if ($receipt.stage -ceq 'install') { 'Install' } else { 'Stage' }
                    if ($null -ne $member.$property -and $member.$property -cne $signature) { $poison = $row.Boundary; break }
                    $member.$property = $signature
                }
                if ($null -ne $poison) { break }
                if ([long]$payload.offerGeneration -gt 0) {
                    $offer = "$($payload.offerGeneration)|$($payload.offerNonceFingerprint)"
                    if ($null -ne $member.Offer -and $member.Offer -cne $offer) { $poison = $row.Boundary; break }
                    $member.Offer = $offer
                }
                $updates.Add([pscustomobject]@{ Member=$member; Key=$operation.operationKey;
                    Slot=[pscustomobject]@{ State=$operation.afterState; Payload=$payloadJson; Entry=$row } })
            }
            if ($null -ne $poison) { break }
            foreach ($update in $updates) { $update.Member.Slots[$update.Key] = $update.Slot }
            if ($null -ne $successfulEviction) {
                $successfulEvictions.Add($successfulEviction)
                $successfulTerminalOwner = $null
            }
            if ($null -ne $successfulShape) {
                $successfulTerminalOwner = @($deltas | Where-Object { $_.Operation.operationKey -ceq $successfulShape.Terminal.operationKey })[0]
                $successfulRetirements.Add([pscustomobject]@{ Origin=$successfulOrigin; Terminal=$successfulTerminalOwner;
                    Retained=@($deltas | Where-Object { $_.Operation.operationKey -ceq $successfulShape.Retained.operationKey })[0] })
            }
            $terminalOwner = $nextTerminalOwner
            $live = @($members.Values | ForEach-Object { $_.Slots.Values } | Where-Object {
                $_.State -in @('Authorized','Staged','ReceiptIssued','Installed','AliasBound','SendInFlight') -or
                ($_.State -ceq 'Settled' -and $_.Entry.Operation.operationRole -ceq 'attempt_retained_predecessor')
            }).Count -gt 0
            $guardActive = $guardActive -and $live
        }
        if ($null -ne $poison) {
            $result.Errors.Add("snapshot_operation_receipt_incomplete|provisional_retry_role_history_invalid:$($poison.evidenceLineIndex)")
            $result.InvalidScopes[$scopeKey] = $true
            continue
        }
        foreach ($authority in @($members.Keys)) {
            $member = $members[$authority]
            $member.OpenRoles = @($member.Slots.Values | Where-Object {
                $_.State -in @('Authorized', 'Staged', 'ReceiptIssued', 'Installed', 'AliasBound', 'SendInFlight') -or
                ($_.State -ceq 'Settled' -and $_.Entry.Operation.operationRole -ceq 'attempt_retained_predecessor')
            } | ForEach-Object { $_.Entry })
            $result.Participants["$scopeKey$([char]31)$authority$([char]31)$($member.Generation)"] = $member
        }
        foreach ($cancellation in $cancellations) {
            if ($DiagnosticOnly) { continue }
            $operation = $cancellation.Retained
            $receipt = @($operation.runtimeUnlockRetry.receipts | Where-Object stage -CEQ 'settlement')[0]
            $key = "$scopeKey$([char]31)$($cancellation.Entry.Boundary.boundaryId)$([char]31)$($operation.operationKey)"
            $result.CancelledReceipts[$key] = [pscustomobject]@{
                Operation=($operation | ConvertTo-Json -Depth 32 -Compress); Receipt=($receipt | ConvertTo-Json -Depth 16 -Compress)
            }
        }
        if (-not $DiagnosticOnly) {
            foreach ($retirement in $successfulRetirements) {
                foreach ($subject in @([pscustomobject]@{ Boundary=$retirement.Origin.Entry.Boundary; Operation=$retirement.Origin.Retained },
                        $retirement.Retained, $retirement.Terminal)) {
                    $key = "$scopeKey$([char]31)$($subject.Boundary.boundaryId)$([char]31)$($subject.Operation.operationKey)"
                    $result.SuccessfulRetirements[$key] = [pscustomobject]@{ Entry=$subject; Terminal=$retirement.Terminal; TerminalRemoval=$false;
                        Operation=($subject.Operation | ConvertTo-Json -Depth 32 -Compress) }
                }
            }
            foreach ($eviction in $successfulEvictions) {
                $subject = $eviction.Entry
                $key = "$scopeKey$([char]31)$($subject.Boundary.boundaryId)$([char]31)$($subject.Operation.operationKey)"
                $result.SuccessfulRetirements[$key] = [pscustomobject]@{ Entry=$subject; Terminal=$eviction.Terminal; TerminalRemoval=$true;
                    Successor=$eviction.Successor;
                    Operation=($subject.Operation | ConvertTo-Json -Depth 32 -Compress) }
            }
            foreach ($eviction in $evictions) {
                $operation = $eviction.Entry.Operation
                $key = "$scopeKey$([char]31)$($eviction.Entry.Boundary.boundaryId)$([char]31)$($operation.operationKey)$([char]31)$($operation.operationGeneration)"
                $result.TerminalEvictions[$key] = [pscustomobject]@{ Entry=$eviction.Entry; Origin=$eviction.Origin;
                    Operation=($operation | ConvertTo-Json -Depth 32 -Compress) }
            }
        }
    }
    foreach ($entry in $AggregateEntries) {
        if ($entry.Boundary.decisionKind -ceq 'ObserveRuntimeUnlockRetryDispatchSettlement' -and
            (($entry.Boundary.decisionStatus -ceq 'Accepted' -and
              $entry.Boundary.decisionReason -ceq 'retry_dispatch_settlement_superseded_predecessor') -or
             ($entry.Boundary.decisionStatus -in @('Idempotent', 'Rejected') -and
              @($entry.Boundary.operations | Where-Object {
                  $_.family -ceq 'runtime_unlock_retry_lifecycle' -and $_.operationRole -ceq 'attempt_terminal' -and
                  $null -ne $_.runtimeUnlockRetry -and @($_.runtimeUnlockRetry.receipts | Where-Object {
                      $_.stage -ceq 'settlement' -and $_.receiptStatus -ceq 'Superseded'
                  }).Count -gt 0
              }).Count -gt 0)) -and
            -not $scopes.ContainsKey((Get-FileTransferCoordinatorProvisionalRetryScopeKey $entry.Boundary))) {
            $result.Errors.Add("snapshot_operation_receipt_incomplete|successful_retry_retirement_origin_missing:$($entry.Boundary.evidenceLineIndex)")
        }
    }
    if ($result.Errors.Count -gt 0) {
        $result.CancelledReceipts.Clear(); $result.SuccessfulRetirements.Clear(); $result.TerminalEvictions.Clear()
    }
    return $result
}

function Add-FileTransferCoordinatorProvisionalRetryFinding {
    param($Reduction, $Entry, [string]$Taxonomy, [string]$Rule, $Receipt = $null)
    $scope = Get-FileTransferCoordinatorProvisionalRetryScopeKey $Entry.Boundary
    $Reduction.Findings.Add([pscustomobject]@{
        BoundaryKey="$scope$([char]31)$($Entry.Boundary.boundaryId)"; Entry=$Entry; Taxonomy=$Taxonomy;
        Priority=$(if ($Taxonomy -ceq 'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete') { 0 } else { 1 });
        TieKey=(@($Entry.Operation.operationKey, ($Receipt | ConvertTo-Json -Depth 16 -Compress), $Rule) -join [char]31)
    })
}

function Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        [Parameter(Mandatory = $true)]$Operation
    )

    if ([string]$Operation.family -notin
        $script:FileTransferCoordinatorSnapshotFamily2) {
        return $true
    }
    $expectedOperationKey = @(
        [string]$Operation.family,
        [string]$Operation.operationRole,
        [string]$Operation.authorityId,
        ([long]$Operation.operationGeneration).ToString(
            [System.Globalization.CultureInfo]::InvariantCulture),
        [string]$Operation.operationKind) -join '|'
    if ([string]$Operation.operationKey -cne $expectedOperationKey -or
        ([string]$Envelope.decisionKind -notin
            $script:FileTransferCoordinatorSnapshotFamily2DecisionKinds -and
         $null -eq (Get-FileTransferCoordinatorRetryConfirmationCompensationShape $Envelope)) -or
        [string]$Operation.operationRole -notin
            $script:FileTransferCoordinatorSnapshotFamily2OperationRoles -or
        $null -eq $Operation.runtimeUnlockRetry -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Operation.runtimeUnlockRetry.sourceBoundary)) {
        return $false
    }

    $role = [string]$Operation.operationRole
    $kind = [string]$Operation.operationKind
    if (($role -like 'attempt_*' -and
         $role -cne 'attempted_decision' -and
         $kind -cne 'RetryAttempt') -or
        ($role -ceq 'bounded_closure' -and $kind -cne 'BoundedClosure') -or
        ($role -ceq 'scope_invalidation' -and $kind -cne 'ScopeInvalidation') -or
        ($role -ceq 'attempted_decision' -and
         $kind -cne [string]$Envelope.decisionKind)) {
        return $false
    }

    $states = @(
        'None', 'Authorized', 'Staged', 'ReceiptIssued', 'Installed',
        'AliasBound', 'SendInFlight', 'Settled', 'Invalidated', 'Expired',
        'Closed', 'Pending', 'Accepted', 'Idempotent', 'Rejected')
    if ([string]$Operation.beforeState -ceq 'Applied' -or [string]$Operation.afterState -ceq 'Applied') {
        return Test-FileTransferCoordinatorSnapshotAppliedScopeInvalidationStage $Envelope $Operation
    }
    if ([string]$Operation.beforeState -notin $states -or
        [string]$Operation.afterState -notin $states) {
        return $false
    }
    if (($kind -ceq 'ScopeInvalidation' -and
            ([string]$Operation.afterState -ceq 'Pending' -or
             ([string]$Operation.beforeState -ceq 'Pending' -and [string]$Operation.afterState -ceq 'None'))) -or
        @($Operation.runtimeUnlockRetry.receipts | Where-Object {
            [string]$_.stage -ceq 'scope_invalidation' -and [string]$_.receiptStatus -ceq 'Pending'
        }).Count -gt 0) {
        return Test-FileTransferCoordinatorSnapshotPendingScopeInvalidationStage -Envelope $Envelope -Operation $Operation
    }
    foreach ($receipt in @($Operation.runtimeUnlockRetry.receipts)) {
        $stage = [string]$receipt.stage
        if ($stage -ceq 'settlement' -and [string]$receipt.receiptStatus -ceq 'Superseded') {
            if (-not (Test-FileTransferCoordinatorSnapshotProvisionalRetrySupersession -Envelope $Envelope -Operation $Operation)) {
                $successful = Get-FileTransferCoordinatorSuccessfulRetryRetirementShape $Envelope
                $attempt = Get-FileTransferCoordinatorSuccessfulRetryRetirementAttempt $Envelope
                $rollover = Get-FileTransferCoordinatorSuccessfulRetrySuccessorSettlementShape $Envelope
                if (($null -eq $successful -or @(@($successful.Retained, $successful.Terminal) | Where-Object {
                    ($_ | ConvertTo-Json -Depth 32 -Compress) -ceq ($Operation | ConvertTo-Json -Depth 32 -Compress)
                }).Count -ne 1) -and ($null -eq $attempt -or
                    ($attempt | ConvertTo-Json -Depth 32 -Compress) -cne ($Operation | ConvertTo-Json -Depth 32 -Compress)) -and
                    ($null -eq $rollover -or ($rollover.Removed | ConvertTo-Json -Depth 32 -Compress) -cne
                        ($Operation | ConvertTo-Json -Depth 32 -Compress))) { return $false }
            }
            continue
        }
        if ($stage -ceq 'install' -and [string]$receipt.receiptStatus -ceq 'Staged') {
            if (-not (Test-FileTransferCoordinatorSnapshotStagedRetryInstallBoundary `
                    -Envelope $Envelope -Operation $Operation)) { return $false }
            continue
        }
        if (-not $script:FileTransferCoordinatorSnapshotFamily2ReceiptStages.Contains($stage) -or
            [string]$receipt.receiptStatus -notin
                @($script:FileTransferCoordinatorSnapshotFamily2ReceiptStages[$stage])) {
            return $false
        }
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotFamily3OperationCatalog {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        [Parameter(Mandatory = $true)]$Operation
    )

    if ([string]$Operation.family -notin
        $script:FileTransferCoordinatorSnapshotFamily3) {
        return $true
    }
    $expectedOperationKey = @(
        [string]$Operation.family,
        [string]$Operation.operationRole,
        [string]$Operation.authorityId,
        ([long]$Operation.operationGeneration).ToString(
            [System.Globalization.CultureInfo]::InvariantCulture),
        [string]$Operation.operationKind) -join '|'
    $decisionKind = [string]$Envelope.decisionKind
    $isExactPressureRecoveryHandoffCommit =
        $decisionKind -ceq
            'CommitRegularV4FrontierRepairPressureRecoveryHandoff'
    $knownAtomicDecision =
        $decisionKind -in $script:FileTransferCoordinatorSnapshotFamily3DecisionKinds -or
        $decisionKind -in $script:FileTransferCoordinatorSnapshotFamily2DecisionKinds -or
        $decisionKind -in $script:FileTransferCoordinatorSnapshotFamily1DecisionKinds -or
        $isExactPressureRecoveryHandoffCommit -or
        (Test-FileTransferCoordinatorSnapshotFallbackCheckpointOperation `
            -Envelope $Envelope -Operation $Operation)
    if ([string]$Operation.operationKey -cne $expectedOperationKey -or
        -not $knownAtomicDecision -or
        [string]$Operation.operationRole -notin
            $script:FileTransferCoordinatorSnapshotFamily3OperationRoles -or
        $null -eq $Operation.receiveRecovery -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Operation.receiveRecovery.sourceBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Operation.receiveRecovery.effectiveBoundary)) {
        return $false
    }

    foreach ($name in @(
            'role', 'recoveryKind', 'executionState', 'closureState',
            'proofIdentity', 'heldRetryAuthorityId',
            'reservedRetryAuthorityId', 'receipts')) {
        if ($Operation.receiveRecovery.PSObject.Properties.Name -notcontains
            $name) {
            return $false
        }
    }
    foreach ($receipt in @($Operation.receiveRecovery.receipts)) {
        if ([string]::IsNullOrWhiteSpace([string]$receipt.stage) -or
            [string]::IsNullOrWhiteSpace([string]$receipt.commandIdentity) -or
            [long]$receipt.commandGeneration -le 0 -or
            [string]::IsNullOrWhiteSpace([string]$receipt.commandFingerprint) -or
            [string]::IsNullOrWhiteSpace([string]$receipt.receiptIdentity) -or
            [long]$receipt.receiptGeneration -le 0 -or
            [string]::IsNullOrWhiteSpace([string]$receipt.receiptStatus) -or
            [string]::IsNullOrWhiteSpace([string]$receipt.receiptFingerprint)) {
            return $false
        }
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotFallbackTailReceiptEqual {
    param(
        [Parameter(Mandatory = $true)]$Left,
        [Parameter(Mandatory = $true)]$Right
    )

    foreach ($name in @(
            'commandIdentity', 'commandGeneration', 'commandFingerprint',
            'receiptIdentity', 'receiptGeneration', 'receiptStatus',
            'receiptFingerprint', 'receiptValidated', 'stage')) {
        if ([string]$Left.$name -cne [string]$Right.$name) {
            return $false
        }
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotFallbackTailOperationScope {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        [Parameter(Mandatory = $true)]$Operation
    )

    if ($null -eq $Operation.fallbackTailYield) {
        return $false
    }
    $payload = $Operation.fallbackTailYield
    foreach ($boundary in @(
            $payload.sourceBoundary,
            $payload.effectiveBoundary,
            $payload.targetBoundary)) {
        if (-not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                -Boundary $boundary) -or
            [string]$boundary.route -cne 'post_tuna_fallback_v6' -or
            [int]$boundary.protocolVersion -ne 6 -or
            [string]$boundary.dataSessionIdentity -cne
                [string]$Envelope.aggregate.afterBoundary.dataSessionIdentity -or
            [long]$boundary.dataSessionGeneration -ne
                [long]$Envelope.aggregate.afterBoundary.dataSessionGeneration) {
            return $false
        }
    }
    return (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            -Left $payload.sourceBoundary `
            -Right $payload.effectiveBoundary) -and
        (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            -Left $payload.sourceBoundary `
            -Right $payload.targetBoundary)
}

function Get-FileTransferCoordinatorSnapshotFallbackTailFailedSupersessionShape {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [object[]]$Operations
    )

    if ($Operations.Count -eq 4) {
        foreach ($operation in $Operations) {
            foreach ($name in @('family', 'operationRole', 'operationKind', 'operationKey', 'authorityId',
                    'operationGeneration', 'beforeState', 'afterState', 'stateApplied', 'fallbackTailYield',
                    'commandIdentity', 'commandGeneration', 'commandFingerprint', 'receiptIdentity',
                    'receiptGeneration', 'receiptStatus', 'receiptFingerprint', 'receiptValidated')) {
                if ($null -eq $operation -or $operation.PSObject.Properties.Name -notcontains $name) { return $null }
            }
        }
    }
    $family4Operations = @($Operations | Where-Object {
            [string]$_.family -ceq 'fallback_tail_yield'
        })
    if ($Operations.Count -notin @(3, 4) -or $family4Operations.Count -ne $Operations.Count) {
        return $null
    }

    $failedCurrent = @($family4Operations | Where-Object {
            [string]$_.operationRole -ceq 'yield_current' -and
            [string]$_.operationKind -ceq 'Failed' -and
            [string]$_.beforeState -ceq 'PeerProofObserved' -and
            [string]$_.afterState -ceq 'None' -and
            [bool]$_.stateApplied
        })
    $quiescentCurrent = @($family4Operations | Where-Object {
            [string]$_.operationRole -ceq 'yield_current' -and
            [string]$_.operationKind -ceq 'Quiescent' -and
            [string]$_.beforeState -ceq 'None' -and
            [string]$_.afterState -ceq 'PeerProofObserved' -and
            [bool]$_.stateApplied
        })
    $failedTerminal = @($family4Operations | Where-Object {
            [string]$_.operationRole -ceq 'yield_terminal' -and
            [string]$_.operationKind -ceq 'Failed' -and
            [string]$_.beforeState -ceq 'None' -and
            [string]$_.afterState -ceq 'Invalidated' -and
            [bool]$_.stateApplied
        })
    if ($failedCurrent.Count -ne 1 -or $quiescentCurrent.Count -ne 1 -or
        $failedTerminal.Count -ne 1) {
        return $null
    }

    $prior = $failedCurrent[0]
    $successor = $quiescentCurrent[0]
    $terminal = $failedTerminal[0]
    $displaced = $null
    if ($Operations.Count -eq 4) {
        $retirements = @($family4Operations | Where-Object {
                $_.operationRole -ceq 'yield_terminal' -and $_.operationKind -ceq 'Quiescent' -and
                $_.beforeState -ceq 'Invalidated' -and $_.afterState -ceq 'None' -and $_.stateApplied
            })
        if ($retirements.Count -ne 1) { return $null }
        $displaced = $retirements[0]
        $typed = $displaced.fallbackTailYield
        foreach ($name in @('role', 'yieldKind', 'pairingState', 'sourceBoundary', 'effectiveBoundary', 'targetBoundary', 'receipts')) {
            if ($null -eq $typed -or $typed.PSObject.Properties.Name -notcontains $name) { return $null }
        }
        $successorScope = Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $successor.fallbackTailYield -Name 'sourceBoundary' -Default $null
        if ($null -eq $successorScope -or -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $successorScope)) { return $null }
        foreach ($receipt in @($typed.receipts)) {
            foreach ($name in @('stage', 'commandIdentity', 'commandGeneration', 'commandFingerprint',
                    'receiptIdentity', 'receiptGeneration', 'receiptStatus', 'receiptFingerprint', 'receiptValidated')) {
                if ($null -eq $receipt -or $receipt.PSObject.Properties.Name -notcontains $name) { return $null }
            }
        }
        if ($displaced.stateApplied -isnot [bool] -or $displaced.receiptValidated -isnot [bool] -or
            -not $displaced.receiptValidated -or $typed.role -cne 'yield_terminal' -or
            $typed.yieldKind -cne 'Quiescent' -or $typed.pairingState -cne 'Invalidated' -or
            $displaced.authorityId -in @('', '(none)', $prior.authorityId, $successor.authorityId) -or
            [long]$displaced.operationGeneration -le 0 -or
            [long]$displaced.operationGeneration -ge [long]$prior.operationGeneration -or
            $displaced.operationKey -cne ('fallback_tail_yield|yield_terminal|' + $displaced.authorityId + '|' +
                ([long]$displaced.operationGeneration).ToString([Globalization.CultureInfo]::InvariantCulture) + '|Quiescent') -or
            -not (Test-FileTransferCoordinatorSnapshotFamily4StageEvidence $displaced)) { return $null }
        foreach ($scope in @($typed.sourceBoundary, $typed.effectiveBoundary, $typed.targetBoundary)) {
            if ($null -eq $scope -or -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $scope) -or
                $scope.dataSessionIdentity -cne $successorScope.dataSessionIdentity -or
                [long]$scope.dataSessionGeneration -ne [long]$successorScope.dataSessionGeneration) {
                return $null
            }
        }
        $retainedReceipt = @($typed.receipts | Where-Object {
                $_.stage -ceq 'invalidation' -and $_.receiptStatus -ceq 'Invalidated' -and
                $_.receiptValidated -is [bool] -and $_.receiptValidated
            })
        if ($retainedReceipt.Count -ne 1 -or
            [long]$retainedReceipt[0].commandGeneration -ne [long]$displaced.operationGeneration -or
            [long]$retainedReceipt[0].receiptGeneration -ne [long]$displaced.operationGeneration) { return $null }
        foreach ($field in @('commandIdentity', 'commandGeneration', 'commandFingerprint',
                'receiptIdentity', 'receiptGeneration', 'receiptStatus', 'receiptFingerprint', 'receiptValidated')) {
            if ([string]$displaced.$field -cne [string]$retainedReceipt[0].$field) { return $null }
        }
    }
    foreach ($operation in @($prior, $successor, $terminal)) {
        $typed = $operation.fallbackTailYield
        $expectedKey = @(
            'fallback_tail_yield',
            [string]$operation.operationRole,
            [string]$operation.authorityId,
            ([long]$operation.operationGeneration).ToString(
                [System.Globalization.CultureInfo]::InvariantCulture),
            [string]$operation.operationKind) -join '|'
        if ($null -eq $typed -or
            [string]$operation.operationKey -cne $expectedKey -or
            [string]$operation.authorityId -in @('', '(none)') -or
            [long]$operation.operationGeneration -le 0 -or
            [string]$operation.predecessorAuthorityId -cne '(none)' -or
            [string]$typed.role -cne [string]$operation.operationRole -or
            [string]$typed.ownerKey -in @('', '(none)') -or
            [string]$typed.pairKey -in @('', '(none)') -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                -Boundary $typed.sourceBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                -Left $typed.sourceBoundary -Right $typed.effectiveBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                -Left $typed.sourceBoundary -Right $typed.targetBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotFamily4StageEvidence `
                -Operation $operation)) {
            return $null
        }
    }

    if ([string]$prior.authorityId -ceq [string]$successor.authorityId -or
        [string]$terminal.authorityId -cne [string]$prior.authorityId -or
        [long]$terminal.operationGeneration -ne
            [long]$prior.operationGeneration -or
        [long]$successor.operationGeneration -le
            [long]$prior.operationGeneration -or
        [string]$prior.fallbackTailYield.yieldKind -cne 'Failed' -or
        [string]$prior.fallbackTailYield.pairingState -cne
            'PeerProofObserved' -or
        [string]$successor.fallbackTailYield.yieldKind -cne 'Quiescent' -or
        [string]$successor.fallbackTailYield.pairingState -cne
            'PeerProofObserved' -or
        [string]$terminal.fallbackTailYield.yieldKind -cne 'Failed' -or
        [string]$terminal.fallbackTailYield.pairingState -cne 'Invalidated' -or
        [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $successor.fallbackTailYield `
            -Name 'failedPredecessorAuthorityId' -Default '(none)') -cne
            [string]$prior.authorityId -or
        -not [bool](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $successor.fallbackTailYield `
            -Name 'failedSupersessionCarriedPeerProof' -Default $false) -or
        [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $prior.fallbackTailYield `
            -Name 'failedPredecessorAuthorityId' -Default '(none)') -cne
            '(none)' -or
        [bool](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $prior.fallbackTailYield `
            -Name 'failedSupersessionCarriedPeerProof' -Default $false) -or
        [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $terminal.fallbackTailYield `
            -Name 'failedPredecessorAuthorityId' -Default '(none)') -cne
            '(none)' -or
        [bool](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $terminal.fallbackTailYield `
            -Name 'failedSupersessionCarriedPeerProof' -Default $false) -or
        [string]$terminal.fallbackTailYield.invalidationReason -cne
            'failed_fallback_tail_yield_superseded_by_quiescence') {
        return $null
    }

    foreach ($field in @('ownerKey', 'pairKey', 'peerProofAuthorityId',
            'peerProofIdentity', 'peerProofFingerprint',
            'peerProofYieldGeneration')) {
        $expected = [string]$prior.fallbackTailYield.$field
        if ($expected -in @('', '(none)', '0') -or
            [string]$successor.fallbackTailYield.$field -cne $expected -or
            [string]$terminal.fallbackTailYield.$field -cne $expected) {
            return $null
        }
    }
    foreach ($operation in @($prior, $successor, $terminal)) {
        foreach ($field in @('localProofIdentity', 'localProofFingerprint',
                'pairedReleaseAuthorityId', 'pairedReleaseOutcome',
                'reservedRetryAuthorityId')) {
            if ([string]$operation.fallbackTailYield.$field -cne '(none)') {
                return $null
            }
        }
        foreach ($field in @('pairingGeneration', 'pairedReleaseGeneration',
                'reservationGeneration', 'consumingInstallGeneration')) {
            if ([long]$operation.fallbackTailYield.$field -ne 0L) {
                return $null
            }
        }
        if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                -Left $prior.fallbackTailYield.sourceBoundary `
                -Right $operation.fallbackTailYield.sourceBoundary)) {
            return $null
        }
    }

    $priorYield = @($prior.fallbackTailYield.receipts | Where-Object {
            [string]$_.stage -ceq 'yield' -and
            [string]$_.receiptStatus -ceq 'Accepted' -and
            [bool]$_.receiptValidated
        })
    $priorPeer = @($prior.fallbackTailYield.receipts | Where-Object {
            [string]$_.stage -ceq 'peer_proof' -and
            [string]$_.receiptStatus -ceq 'Observed' -and
            [bool]$_.receiptValidated
        })
    $successorYield = @($successor.fallbackTailYield.receipts | Where-Object {
            [string]$_.stage -ceq 'yield' -and
            [string]$_.receiptStatus -ceq 'Accepted' -and
            [bool]$_.receiptValidated
        })
    $successorPeer = @($successor.fallbackTailYield.receipts | Where-Object {
            [string]$_.stage -ceq 'peer_proof' -and
            [string]$_.receiptStatus -ceq 'Observed' -and
            [bool]$_.receiptValidated
        })
    $supersession = @($successor.fallbackTailYield.receipts | Where-Object {
            [string]$_.stage -ceq 'failed_supersession' -and
            [string]$_.receiptStatus -ceq 'Superseded' -and
            [bool]$_.receiptValidated
        })
    $terminalYield = @($terminal.fallbackTailYield.receipts | Where-Object {
            [string]$_.stage -ceq 'yield' -and
            [string]$_.receiptStatus -ceq 'Accepted' -and
            [bool]$_.receiptValidated
        })
    $terminalPeer = @($terminal.fallbackTailYield.receipts | Where-Object {
            [string]$_.stage -ceq 'peer_proof' -and
            [string]$_.receiptStatus -ceq 'Observed' -and
            [bool]$_.receiptValidated
        })
    $invalidation = @($terminal.fallbackTailYield.receipts | Where-Object {
            [string]$_.stage -ceq 'invalidation' -and
            [string]$_.receiptStatus -ceq 'Invalidated' -and
            [bool]$_.receiptValidated
        })
    if (@($prior.fallbackTailYield.receipts).Count -ne 2 -or
        @($successor.fallbackTailYield.receipts).Count -ne 3 -or
        @($terminal.fallbackTailYield.receipts).Count -ne 3 -or
        $priorYield.Count -ne 1 -or $priorPeer.Count -ne 1 -or
        $successorYield.Count -ne 1 -or $successorPeer.Count -ne 1 -or
        $supersession.Count -ne 1 -or $terminalYield.Count -ne 1 -or
        $terminalPeer.Count -ne 1 -or $invalidation.Count -ne 1 -or
        -not (Test-FileTransferCoordinatorSnapshotFallbackTailReceiptEqual `
            -Left $priorYield[0] -Right $terminalYield[0]) -or
        -not (Test-FileTransferCoordinatorSnapshotFallbackTailReceiptEqual `
            -Left $priorPeer[0] -Right $successorPeer[0]) -or
        -not (Test-FileTransferCoordinatorSnapshotFallbackTailReceiptEqual `
            -Left $priorPeer[0] -Right $terminalPeer[0]) -or
        [long]$priorYield[0].commandGeneration -ne
            [long]$prior.operationGeneration -or
        [long]$priorYield[0].receiptGeneration -ne
            [long]$prior.operationGeneration -or
        [long]$successorYield[0].commandGeneration -ne
            [long]$successor.operationGeneration -or
        [long]$successorYield[0].receiptGeneration -ne
            [long]$successor.operationGeneration -or
        [long]$supersession[0].commandGeneration -ne
            [long]$prior.operationGeneration -or
        [long]$supersession[0].receiptGeneration -ne
            [long]$successor.operationGeneration -or
        [long]$invalidation[0].commandGeneration -ne
            [long]$prior.operationGeneration -or
        [long]$invalidation[0].receiptGeneration -ne
            [long]$prior.operationGeneration) {
        return $null
    }

    foreach ($pair in @(
            @($prior, $priorPeer[0]),
            @($successor, $supersession[0]),
            @($terminal, $invalidation[0]))) {
        foreach ($field in @('commandIdentity', 'commandGeneration',
                'commandFingerprint', 'receiptIdentity', 'receiptGeneration',
                'receiptStatus', 'receiptFingerprint', 'receiptValidated')) {
            if ([string]$pair[0].$field -cne [string]$pair[1].$field) {
                return $null
            }
        }
    }

    return [pscustomobject]@{
        FailedCurrent = $prior
        QuiescentCurrent = $successor
        FailedTerminal = $terminal
        DisplacedTerminal = $displaced
        PeerReceipt = $priorPeer[0]
        YieldReceipt = $successorYield[0]
        SupersessionReceipt = $supersession[0]
        InvalidationReceipt = $invalidation[0]
    }
}

function Test-FileTransferCoordinatorSnapshotCheckpointStalePeerProofRetirement {
    param([Parameter(Mandatory = $true)]$Envelope)

    $operations = @($Envelope.aggregate.operations | Where-Object { $_.family -ceq 'fallback_tail_yield' })
    $pending = @($operations | Where-Object { $_.operationRole -ceq 'peer_proof_pending_initial' })
    $current = @($operations | Where-Object { $_.operationRole -ceq 'yield_current' })
    if (@($Envelope.aggregate.operations).Count -ne 2 -or $operations.Count -ne 2 -or
        $pending.Count -ne 1 -or $current.Count -ne 1) { return $false }
    $old = $pending[0]; $new = $current[0]
    if ($old.operationKind -cne 'PeerProof' -or $old.beforeState -cne 'Pending' -or $old.afterState -cne 'None' -or
        $new.operationKind -cne 'Quiescent' -or $new.beforeState -cne 'None' -or $new.afterState -cne 'YieldIssued' -or
        $old.authorityId -ceq $new.authorityId) { return $false }
    foreach ($operation in @($old, $new)) {
        if ($operation.stateApplied -isnot [bool] -or -not $operation.stateApplied -or
            $operation.receiptValidated -isnot [bool] -or -not $operation.receiptValidated -or
            [string]$operation.authorityId -in @('', '(none)') -or [long]$operation.operationGeneration -le 0 -or
            [string]$operation.operationKey -cne
                "fallback_tail_yield|$($operation.operationRole)|$($operation.authorityId)|$($operation.operationGeneration)|$($operation.operationKind)" -or
            $null -eq $operation.fallbackTailYield -or
            [string]$operation.fallbackTailYield.role -cne [string]$operation.operationRole -or
            -not (Test-FileTransferCoordinatorSnapshotFallbackTailOperationScope $Envelope $operation)) { return $false }
        $receipts = @($operation.fallbackTailYield.receipts)
        if ($receipts.Count -ne 1 -or -not (Test-FileTransferCoordinatorSnapshotFamily4StageEvidence $operation)) { return $false }
        foreach ($field in @('commandIdentity', 'commandGeneration', 'commandFingerprint', 'receiptIdentity',
                'receiptGeneration', 'receiptStatus', 'receiptFingerprint', 'receiptValidated')) {
            if ([string]$operation.$field -cne [string]$receipts[0].$field) { return $false }
        }
    }
    $prior = $old.fallbackTailYield; $yield = $new.fallbackTailYield
    if ($prior.receipts[0].stage -cne 'peer_proof' -or $prior.receipts[0].receiptStatus -cne 'Observed' -or
        $prior.pairingState -cne 'Pending' -or $yield.receipts[0].stage -cne 'yield' -or
        $yield.receipts[0].receiptStatus -cne 'Accepted' -or $yield.pairingState -cne 'YieldIssued' -or
        $yield.yieldKind -cne 'Quiescent' -or
        [long]$prior.sourceBoundary.checkpointGeneration -le 0 -or
        [long]$prior.sourceBoundary.checkpointGeneration -ge [long]$yield.sourceBoundary.checkpointGeneration -or
        $prior.sourceBoundary.checkpointRequestId -cne '(none)' -or $yield.sourceBoundary.checkpointRequestId -cne '(none)' -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint $prior.sourceBoundary $yield.sourceBoundary)) { return $false }
    foreach ($field in @('ownerKey', 'pairKey')) {
        if ([string]$prior.$field -in @('', '(none)') -or [string]$prior.$field -cne [string]$yield.$field) { return $false }
    }
    foreach ($field in @('peerProofAuthorityId', 'peerProofIdentity', 'peerProofFingerprint')) {
        if ([string]$prior.$field -in @('', '(none)') -or [string]$yield.$field -cne '(none)') { return $false }
    }
    foreach ($field in @('localProofIdentity', 'localProofFingerprint', 'pairedReleaseAuthorityId', 'reservedRetryAuthorityId')) {
        if ([string]$yield.$field -cne '(none)') { return $false }
    }
    return [long]$prior.peerProofYieldGeneration -gt 0 -and [long]$yield.peerProofYieldGeneration -eq 0 -and
        [long]$yield.pairingGeneration -eq 0 -and [long]$yield.pairedReleaseGeneration -eq 0 -and
        [long]$yield.reservationGeneration -eq 0 -and [long]$yield.consumingInstallGeneration -eq 0
}

function Test-FileTransferCoordinatorSnapshotCheckpointPeerProofSuccessor {
    param([Parameter(Mandatory = $true)]$Envelope)

    if ([string]$Envelope.decisionKind -cne 'ApplyFallbackTailPeerProofReceipt' -or
        [string]$Envelope.decisionStatus -cne 'Accepted' -or
        [string]$Envelope.decisionReason -cne
            'fallback_tail_initial_peer_proof_receipt_superseded_after_checkpoint_advance' -or
        [long]$Envelope.aggregate.afterStateGeneration -ne
            ([long]$Envelope.aggregate.beforeStateGeneration + 1L) -or
        [string]$Envelope.aggregate.beforeStateFingerprint -ceq
            [string]$Envelope.aggregate.afterStateFingerprint -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            $Envelope.aggregate.beforeBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            $Envelope.aggregate.beforeBoundary $Envelope.aggregate.afterBoundary) -or
        @($Envelope.aggregate.changedFamilies).Count -ne 1 -or
        [string]$Envelope.aggregate.changedFamilies[0] -cne
            'fallback_tail_yield') {
        return $false
    }

    $operations = @($Envelope.aggregate.operations)
    $pending = @($operations | Where-Object {
            [string]$_.family -ceq 'fallback_tail_yield' -and
            [string]$_.operationRole -ceq 'peer_proof_pending_initial' -and
            [string]$_.operationKind -ceq 'PeerProof'
        })
    $attempted = @($operations | Where-Object {
            [string]$_.family -ceq 'fallback_tail_yield' -and
            [string]$_.operationRole -ceq 'attempted_decision' -and
            [string]$_.operationKind -ceq 'ApplyFallbackTailPeerProofReceipt'
        })
    if ($operations.Count -ne 3 -or $pending.Count -ne 2 -or
        $attempted.Count -ne 1) {
        return $false
    }
    $retired = @($pending | Where-Object {
            [string]$_.beforeState -ceq 'Pending' -and
            [string]$_.afterState -ceq 'None' -and [bool]$_.stateApplied
        })
    $entered = @($pending | Where-Object {
            [string]$_.beforeState -ceq 'None' -and
            [string]$_.afterState -ceq 'Pending' -and [bool]$_.stateApplied
        })
    if ($retired.Count -ne 1 -or $entered.Count -ne 1 -or
        [bool]$attempted[0].stateApplied -or
        [string]$retired[0].authorityId -ceq [string]$entered[0].authorityId -or
        [long]$entered[0].operationGeneration -le
            [long]$retired[0].operationGeneration) {
        return $false
    }

    foreach ($operation in @($retired[0], $entered[0], $attempted[0])) {
        $expectedKey = @(
            'fallback_tail_yield',
            [string]$operation.operationRole,
            [string]$operation.authorityId,
            ([long]$operation.operationGeneration).ToString(
                [Globalization.CultureInfo]::InvariantCulture),
            [string]$operation.operationKind) -join '|'
        if ([string]$operation.authorityId -in @('', '(none)') -or
            [long]$operation.operationGeneration -le 0 -or
            [string]$operation.operationKey -cne $expectedKey -or
            [string]$operation.predecessorAuthorityId -cne '(none)' -or
            [string]$operation.fallbackTailYield.role -cne
                [string]$operation.operationRole -or
            -not (Test-FileTransferCoordinatorSnapshotFamily4StageEvidence `
                $operation) -or
            -not (Test-FileTransferCoordinatorSnapshotFallbackTailOperationScope `
                $Envelope $operation)) {
            return $false
        }
    }
    $old = $retired[0].fallbackTailYield
    $new = $entered[0].fallbackTailYield
    $attempt = $attempted[0].fallbackTailYield
    if ([long]$old.sourceBoundary.checkpointGeneration -le 0 -or
        [long]$new.sourceBoundary.checkpointGeneration -le
            [long]$old.sourceBoundary.checkpointGeneration -or
        [string]$old.sourceBoundary.checkpointRequestId -cne '(none)' -or
        [string]$new.sourceBoundary.checkpointRequestId -cne '(none)' -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint `
            $old.sourceBoundary $new.sourceBoundary)) {
        return $false
    }
    foreach ($field in @('ownerKey', 'pairKey')) {
        if ([string]$old.$field -in @('', '(none)') -or
            [string]$old.$field -cne [string]$new.$field) {
            return $false
        }
    }
    foreach ($operation in @($retired[0], $entered[0])) {
        $typed = $operation.fallbackTailYield
        $receipts = @($typed.receipts | Where-Object {
                [string]$_.stage -ceq 'peer_proof' -and
                [string]$_.receiptStatus -ceq 'Observed' -and
                [bool]$_.receiptValidated
        })
        if ($receipts.Count -ne 1 -or
            [string]$typed.peerProofAuthorityId -in @('', '(none)') -or
            [long]$typed.peerProofYieldGeneration -ne
                [long]$operation.operationGeneration -or
            [string]$typed.peerProofIdentity -in @('', '(none)') -or
            [string]$typed.peerProofFingerprint -in @('', '(none)') -or
            [string]$typed.yieldKind -cne 'PeerProof' -or
            [string]$typed.pairingState -cne 'Pending') {
            return $false
        }
        foreach ($field in @(
                'localProofIdentity', 'localProofFingerprint',
                'pairedReleaseAuthorityId', 'reservedRetryAuthorityId')) {
            if ([string]$typed.$field -cne '(none)') { return $false }
        }
    }
    return [string]$old.peerProofAuthorityId -cne
            [string]$new.peerProofAuthorityId -and
        [long]$new.peerProofYieldGeneration -gt
            [long]$old.peerProofYieldGeneration -and
        [string]$attempted[0].beforeState -ceq 'Pending' -and
        [string]$attempted[0].afterState -ceq 'Pending' -and
        [string]$attempted[0].authorityId -ceq
            [string]$entered[0].authorityId -and
        [long]$attempted[0].operationGeneration -eq
            [long]$entered[0].operationGeneration -and
        [string]$attempt.peerProofIdentity -ceq
            [string]$new.peerProofIdentity -and
        [string]$attempt.peerProofFingerprint -ceq
            [string]$new.peerProofFingerprint -and
        (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            $attempt.sourceBoundary $new.sourceBoundary)
}

function Test-FileTransferCoordinatorSnapshotFallbackTailQuiescentEnvelope {
    param([Parameter(Mandatory = $true)]$Envelope)

    if ([string]$Envelope.decisionKind -cne 'FallbackTailQuiescent' -or
        [string]$Envelope.decisionStatus -cne 'Accepted' -or
        [string]::IsNullOrWhiteSpace([string]$Envelope.decisionReason) -or
        $null -eq $Envelope.aggregate -or
        [long]$Envelope.aggregate.afterStateGeneration -ne
            [long]$Envelope.aggregate.beforeStateGeneration + 1L -or
        [string]::IsNullOrWhiteSpace(
            [string]$Envelope.aggregate.beforeStateFingerprint) -or
        [string]::IsNullOrWhiteSpace(
            [string]$Envelope.aggregate.afterStateFingerprint) -or
        [string]$Envelope.aggregate.beforeStateFingerprint -ceq
            [string]$Envelope.aggregate.afterStateFingerprint -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Envelope.aggregate.beforeBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            -Left $Envelope.aggregate.beforeBoundary `
            -Right $Envelope.aggregate.afterBoundary) -or
        [string]$Envelope.aggregate.afterBoundary.route -cne
            'post_tuna_fallback_v6' -or
        [int]$Envelope.aggregate.afterBoundary.protocolVersion -ne 6) {
        return $false
    }

    $changedFamilies = @($Envelope.aggregate.changedFamilies)
    $operations = @($Envelope.aggregate.operations)
    $family4Operations = @($operations | Where-Object {
            [string]$_.family -ceq 'fallback_tail_yield'
        })
    if ($changedFamilies.Count -ne 1 -or
        [string]$changedFamilies[0] -cne 'fallback_tail_yield') {
        return $false
    }

    $current = @($family4Operations | Where-Object {
            [string]$_.operationRole -ceq 'yield_current' -and
            [string]$_.operationKind -ceq 'Quiescent' -and
            [string]$_.beforeState -ceq 'None' -and
            [string]$_.afterState -in @('YieldIssued', 'PeerProofObserved') -and
            [bool]$_.stateApplied
        })
    if ($current.Count -ne 1 -or
        [string]$current[0].authorityId -in @('', '(none)') -or
        [long]$current[0].operationGeneration -le 0 -or
        [string]$current[0].predecessorAuthorityId -cne '(none)' -or
        -not (Test-FileTransferCoordinatorSnapshotFallbackTailOperationScope `
            -Envelope $Envelope -Operation $current[0])) {
        return $false
    }

    $yield = $current[0]
    $yieldReceipts = @($yield.fallbackTailYield.receipts | Where-Object {
            [string]$_.stage -ceq 'yield' -and
            [string]$_.receiptStatus -ceq 'Accepted' -and
            [bool]$_.receiptValidated
        })
    if ($yieldReceipts.Count -ne 1 -or
        [string]$yield.fallbackTailYield.role -cne 'yield_current' -or
        [string]$yield.fallbackTailYield.yieldKind -cne 'Quiescent' -or
        [string]$yield.fallbackTailYield.ownerKey -in @('', '(none)') -or
        [string]$yield.fallbackTailYield.pairKey -in @('', '(none)')) {
        return $false
    }

    $failedSupersession =
        Get-FileTransferCoordinatorSnapshotFallbackTailFailedSupersessionShape `
            -Operations $operations
    if ($null -ne $failedSupersession) {
        return [string]$yield.operationKey -ceq
                [string]$failedSupersession.QuiescentCurrent.operationKey -and
            [string]$yieldReceipts[0].receiptIdentity -ceq
                [string]$failedSupersession.YieldReceipt.receiptIdentity -and
            [long]$yieldReceipts[0].receiptGeneration -eq
                [long]$failedSupersession.YieldReceipt.receiptGeneration -and
            [string]$yieldReceipts[0].receiptFingerprint -ceq
                [string]$failedSupersession.YieldReceipt.receiptFingerprint
    }

    if ([string]$yield.afterState -ceq 'YieldIssued') {
        return ($family4Operations.Count -eq 1 -or
            (Test-FileTransferCoordinatorSnapshotCheckpointStalePeerProofRetirement $Envelope)) -and
            [string]$yield.fallbackTailYield.pairingState -ceq 'YieldIssued' -and
            [string]$yield.receiptIdentity -ceq
                [string]$yieldReceipts[0].receiptIdentity -and
            [long]$yield.receiptGeneration -eq
                [long]$yieldReceipts[0].receiptGeneration -and
            [string]$yield.receiptStatus -ceq
                [string]$yieldReceipts[0].receiptStatus -and
            [string]$yield.receiptFingerprint -ceq
                [string]$yieldReceipts[0].receiptFingerprint -and
            [string]$yield.commandIdentity -ceq
                [string]$yieldReceipts[0].commandIdentity -and
            [long]$yield.commandGeneration -eq
                [long]$yieldReceipts[0].commandGeneration -and
            [string]$yield.commandFingerprint -ceq
                [string]$yieldReceipts[0].commandFingerprint
    }

    $pending = @($family4Operations | Where-Object {
            [string]$_.operationRole -ceq 'peer_proof_pending_initial' -and
            [string]$_.operationKind -ceq 'PeerProof' -and
            [string]$_.beforeState -ceq 'Pending' -and
            [string]$_.afterState -ceq 'None' -and
            [bool]$_.stateApplied
        })
    if ($family4Operations.Count -ne 2 -or $pending.Count -ne 1 -or
        [string]$yield.fallbackTailYield.pairingState -cne
            'PeerProofObserved' -or
        [string]$pending[0].authorityId -in @('', '(none)') -or
        [string]$pending[0].authorityId -ceq [string]$yield.authorityId -or
        [long]$pending[0].operationGeneration -le 0 -or
        -not (Test-FileTransferCoordinatorSnapshotFallbackTailOperationScope `
            -Envelope $Envelope -Operation $pending[0])) {
        return $false
    }

    $peerReceipts = @($pending[0].fallbackTailYield.receipts | Where-Object {
            [string]$_.stage -ceq 'peer_proof' -and
            [string]$_.receiptStatus -ceq 'Observed' -and
            [bool]$_.receiptValidated
        })
    $carriedPeerReceipts = @(
        $yield.fallbackTailYield.receipts | Where-Object {
            [string]$_.stage -ceq 'peer_proof' -and
            [string]$_.receiptStatus -ceq 'Observed' -and
            [bool]$_.receiptValidated
        })
    if ($peerReceipts.Count -ne 1 -or $carriedPeerReceipts.Count -ne 1 -or
        -not (Test-FileTransferCoordinatorSnapshotFallbackTailReceiptEqual `
            -Left $peerReceipts[0] -Right $carriedPeerReceipts[0])) {
        return $false
    }

    foreach ($name in @(
            'ownerKey', 'pairKey', 'peerProofAuthorityId',
            'peerProofIdentity', 'peerProofFingerprint',
            'peerProofYieldGeneration')) {
        if ([string]$pending[0].fallbackTailYield.$name -cne
            [string]$yield.fallbackTailYield.$name) {
            return $false
        }
    }
    return (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            -Left $pending[0].fallbackTailYield.sourceBoundary `
            -Right $yield.fallbackTailYield.sourceBoundary) -and
        [string]$pending[0].receiptIdentity -ceq
            [string]$peerReceipts[0].receiptIdentity -and
        [long]$pending[0].receiptGeneration -eq
            [long]$peerReceipts[0].receiptGeneration -and
        [string]$pending[0].receiptFingerprint -ceq
            [string]$peerReceipts[0].receiptFingerprint -and
        [string]$yield.receiptIdentity -ceq
            [string]$carriedPeerReceipts[0].receiptIdentity -and
        [long]$yield.receiptGeneration -eq
            [long]$carriedPeerReceipts[0].receiptGeneration -and
        [string]$yield.receiptFingerprint -ceq
            [string]$carriedPeerReceipts[0].receiptFingerprint
}

function Test-FileTransferCoordinatorSnapshotFallbackTailFailedEnvelope {
    param($Envelope)

    if ($Envelope.decisionKind -cne 'FallbackTailFailed' -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $Envelope.aggregate.beforeBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $Envelope.aggregate.beforeBoundary $Envelope.aggregate.afterBoundary) -or
        $Envelope.aggregate.afterBoundary.route -cne 'post_tuna_fallback_v6' -or
        [int]$Envelope.aggregate.afterBoundary.protocolVersion -ne 6) { return $false }

    $operations = @($Envelope.aggregate.operations)
    if ([string]$Envelope.decisionStatus -in @('Idempotent', 'Rejected')) {
        if ([long]$Envelope.aggregate.afterStateGeneration -ne
                [long]$Envelope.aggregate.beforeStateGeneration -or
            [string]$Envelope.aggregate.beforeStateFingerprint -cne
                [string]$Envelope.aggregate.afterStateFingerprint -or
            @($Envelope.aggregate.changedFamilies).Count -ne 1 -or
            [string]$Envelope.aggregate.changedFamilies[0] -cne
                'fallback_tail_yield' -or
            $operations.Count -ne 1) {
            return $false
        }

        $attempt = $operations[0]
        $typed = $attempt.fallbackTailYield
        $expectedKey = @(
            'fallback_tail_yield', 'attempted_decision',
            [string]$attempt.authorityId,
            ([long]$attempt.operationGeneration).ToString(
                [System.Globalization.CultureInfo]::InvariantCulture),
            'FallbackTailFailed') -join '|'
        if ([string]$attempt.family -cne 'fallback_tail_yield' -or
            [string]$attempt.operationRole -cne 'attempted_decision' -or
            [string]$attempt.operationKind -cne 'FallbackTailFailed' -or
            [string]$attempt.operationKey -cne $expectedKey -or
            [string]$attempt.authorityId -in @('', '(none)') -or
            [long]$attempt.operationGeneration -le 0 -or
            [string]$attempt.predecessorAuthorityId -cne '(none)' -or
            [string]$attempt.beforeState -cne [string]$attempt.afterState -or
            [bool]$attempt.stateApplied -or $null -eq $typed -or
            [string]$typed.role -cne 'attempted_decision' -or
            -not (Test-FileTransferCoordinatorSnapshotFallbackTailOperationScope `
                $Envelope $attempt) -or
            -not (Test-FileTransferCoordinatorSnapshotFamily4StageEvidence `
                $attempt)) {
            return $false
        }

        $receipts = @($typed.receipts)
        if ($receipts.Count -ne 1 -or
            [string]$receipts[0].stage -cne 'attempted_decision' -or
            [string]$receipts[0].receiptStatus -cne
                [string]$Envelope.decisionStatus -or
            [string]$receipts[0].receiptReason -cne
                [string]$Envelope.decisionReason -or
            -not [bool]$receipts[0].receiptValidated) {
            return $false
        }
        foreach ($field in @(
                'commandIdentity', 'commandGeneration', 'commandFingerprint',
                'receiptIdentity', 'receiptGeneration', 'receiptStatus',
                'receiptFingerprint', 'receiptValidated')) {
            if ([string]$attempt.$field -cne [string]$receipts[0].$field) {
                return $false
            }
        }
        if ([long]$receipts[0].commandGeneration -ne
                [long]$attempt.operationGeneration -or
            [long]$receipts[0].receiptGeneration -ne
                [long]$attempt.operationGeneration) {
            return $false
        }

        if ([string]$Envelope.decisionStatus -ceq 'Idempotent') {
            return [string]$typed.yieldKind -ceq 'Failed' -and
                [string]$typed.pairingState -ceq [string]$attempt.afterState -and
                [string]$typed.ownerKey -notin @('', '(none)') -and
                [string]$typed.pairKey -notin @('', '(none)')
        }

        if ([string]$typed.yieldKind -ceq 'Attempt') {
            if ([string]$attempt.beforeState -cne 'None' -or
                [string]$typed.pairingState -cne 'Rejected' -or
                [string]$typed.invalidationReason -cne
                    [string]$Envelope.decisionReason -or
                [bool]$typed.failedSupersessionCarriedPeerProof) {
                return $false
            }
            foreach ($field in @(
                    'ownerKey', 'pairKey', 'localProofIdentity',
                    'localProofFingerprint', 'peerProofAuthorityId',
                    'peerProofIdentity', 'peerProofFingerprint',
                    'pairedReleaseAuthorityId', 'pairedReleaseOutcome',
                    'failedPredecessorAuthorityId',
                    'reservedRetryAuthorityId')) {
                if ([string]$typed.$field -cne '(none)') { return $false }
            }
            foreach ($field in @(
                    'peerProofYieldGeneration', 'pairingGeneration',
                    'pairedReleaseGeneration', 'reauthorizationGeneration',
                    'reservationGeneration', 'consumingInstallGeneration',
                    'retryHoldGeneration', 'retryRecheckGeneration')) {
                if ([long]$typed.$field -ne 0) { return $false }
            }
            return [long]$typed.reauthorizationSourceTransportEpoch -eq
                    [long]$typed.sourceBoundary.transportEpoch -and
                [long]$typed.reauthorizationTargetTransportEpoch -eq
                    [long]$typed.sourceBoundary.transportEpoch
        }
        return [string]$typed.yieldKind -ceq 'Failed' -and
            [string]$typed.pairingState -ceq [string]$attempt.afterState -and
            [string]$typed.ownerKey -notin @('', '(none)') -and
            [string]$typed.pairKey -notin @('', '(none)')
    }

    if ([string]$Envelope.decisionStatus -cne 'Accepted' -or
        [long]$Envelope.aggregate.afterStateGeneration -ne
            ([long]$Envelope.aggregate.beforeStateGeneration + 1L) -or
        $Envelope.aggregate.beforeStateFingerprint -ceq
            $Envelope.aggregate.afterStateFingerprint -or
        @($Envelope.aggregate.changedFamilies).Count -ne 1 -or
        $Envelope.aggregate.changedFamilies[0] -cne 'fallback_tail_yield') {
        return $false
    }

    $current = @($operations | Where-Object {
            [string]$_.family -ceq 'fallback_tail_yield' -and
            [string]$_.operationRole -ceq 'yield_current' -and
            [string]$_.operationKind -ceq 'Failed' -and
            [string]$_.beforeState -ceq 'None' -and
            [string]$_.afterState -in @('YieldIssued', 'PeerProofObserved') -and
            [bool]$_.stateApplied
        })
    if ($current.Count -ne 1) { return $false }
    $operation = $current[0]; $typed = $operation.fallbackTailYield
    $expectedOperationKey = @(
        'fallback_tail_yield', 'yield_current', [string]$operation.authorityId,
        ([long]$operation.operationGeneration).ToString(
            [System.Globalization.CultureInfo]::InvariantCulture), 'Failed') -join '|'
    if ($operation.family -cne 'fallback_tail_yield' -or $operation.operationRole -cne 'yield_current' -or
        $operation.operationKey -cne $expectedOperationKey -or
        $operation.operationKind -cne 'Failed' -or $operation.beforeState -cne 'None' -or
        $operation.afterState -notin @('YieldIssued', 'PeerProofObserved') -or
        -not $operation.stateApplied -or
        $operation.authorityId -in @('', '(none)') -or [long]$operation.operationGeneration -le 0 -or
        $operation.predecessorAuthorityId -cne '(none)' -or $null -eq $typed -or
        $typed.role -cne 'yield_current' -or $typed.yieldKind -cne 'Failed' -or
        $typed.pairingState -cne $operation.afterState -or
        [string]$typed.ownerKey -in @('', '(none)') -or
        [string]$typed.pairKey -in @('', '(none)') -or
        -not (Test-FileTransferCoordinatorSnapshotFallbackTailOperationScope $Envelope $operation) -or
        -not (Test-FileTransferCoordinatorSnapshotFamily4StageEvidence $operation)) { return $false }
    foreach ($field in @('localProofIdentity','localProofFingerprint',
            'pairedReleaseAuthorityId','pairedReleaseOutcome','reservedRetryAuthorityId',
            'failedPredecessorAuthorityId','invalidationReason')) {
        if ([string]$typed.$field -cne '(none)') { return $false }
    }
    if ([bool]$typed.failedSupersessionCarriedPeerProof -or
        $typed.PSObject.Properties.Name -notcontains 'retryRecheckGeneration') { return $false }
    foreach ($field in @('pairingGeneration','pairedReleaseGeneration',
            'reservationGeneration','consumingInstallGeneration','retryRecheckGeneration')) {
        if ([long]$typed.$field -ne 0) { return $false }
    }

    $yieldReceipts = @($typed.receipts | Where-Object {
            [string]$_.stage -ceq 'yield' -and
            [string]$_.receiptStatus -ceq 'Accepted' -and
            [bool]$_.receiptValidated
        })
    if ($yieldReceipts.Count -ne 1 -or
        [long]$yieldReceipts[0].commandGeneration -ne
            [long]$operation.operationGeneration -or
        [long]$yieldReceipts[0].receiptGeneration -ne
            [long]$operation.operationGeneration) {
        return $false
    }

    if ([string]$operation.afterState -ceq 'YieldIssued') {
        if ($operations.Count -ne 1 -or @($typed.receipts).Count -ne 1) {
            return $false
        }
        foreach ($field in @('peerProofAuthorityId','peerProofIdentity',
                'peerProofFingerprint')) {
            if ([string]$typed.$field -cne '(none)') { return $false }
        }
        if ([long]$typed.peerProofYieldGeneration -ne 0) { return $false }
        foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint',
                'receiptIdentity','receiptGeneration','receiptStatus','receiptFingerprint','receiptValidated')) {
            if ([string]$operation.$field -cne [string]$yieldReceipts[0].$field) {
                return $false
            }
        }
        return $true
    }

    $pending = @($operations | Where-Object {
            [string]$_.family -ceq 'fallback_tail_yield' -and
            [string]$_.operationRole -ceq 'peer_proof_pending_initial' -and
            [string]$_.operationKind -ceq 'PeerProof' -and
            [string]$_.beforeState -ceq 'Pending' -and
            [string]$_.afterState -ceq 'None' -and
            [bool]$_.stateApplied
        })
    if ($operations.Count -ne 2 -or $pending.Count -ne 1) { return $false }
    $pendingOperation = $pending[0]
    $pendingTyped = $pendingOperation.fallbackTailYield
    $expectedPendingKey = @(
        'fallback_tail_yield', 'peer_proof_pending_initial',
        [string]$pendingOperation.authorityId,
        ([long]$pendingOperation.operationGeneration).ToString(
            [System.Globalization.CultureInfo]::InvariantCulture), 'PeerProof') -join '|'
    if ([string]$pendingOperation.operationKey -cne $expectedPendingKey -or
        [string]$pendingOperation.authorityId -in @('', '(none)') -or
        [string]$pendingOperation.authorityId -ceq [string]$operation.authorityId -or
        [long]$pendingOperation.operationGeneration -le 0 -or
        [string]$pendingOperation.predecessorAuthorityId -cne '(none)' -or
        $null -eq $pendingTyped -or
        [string]$pendingTyped.role -cne 'peer_proof_pending_initial' -or
        [string]$pendingTyped.yieldKind -cne 'PeerProof' -or
        [string]$pendingTyped.pairingState -cne 'Pending' -or
        -not (Test-FileTransferCoordinatorSnapshotFallbackTailOperationScope `
            $Envelope $pendingOperation) -or
        -not (Test-FileTransferCoordinatorSnapshotFamily4StageEvidence `
            $pendingOperation)) {
        return $false
    }

    $pendingPeerReceipts = @($pendingTyped.receipts | Where-Object {
            [string]$_.stage -ceq 'peer_proof' -and
            [string]$_.receiptStatus -ceq 'Observed' -and
            [bool]$_.receiptValidated
        })
    $carriedPeerReceipts = @($typed.receipts | Where-Object {
            [string]$_.stage -ceq 'peer_proof' -and
            [string]$_.receiptStatus -ceq 'Observed' -and
            [bool]$_.receiptValidated
        })
    if (@($pendingTyped.receipts).Count -ne 1 -or
        @($typed.receipts).Count -ne 2 -or
        $pendingPeerReceipts.Count -ne 1 -or
        $carriedPeerReceipts.Count -ne 1 -or
        -not (Test-FileTransferCoordinatorSnapshotFallbackTailReceiptEqual `
            $pendingPeerReceipts[0] $carriedPeerReceipts[0])) {
        return $false
    }
    $peerAuthorityHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes (
        [System.Text.UTF8Encoding]::new($false).GetBytes(
            [string]$pendingTyped.peerProofAuthorityId))
    $expectedPendingAuthority = 'fte1-' + $peerAuthorityHash.Substring(0, 24)
    if ([string]$pendingOperation.authorityId -cne
            $expectedPendingAuthority -or
        [long]$pendingTyped.peerProofYieldGeneration -ne
            [long]$pendingOperation.operationGeneration -or
        [string]$pendingTyped.peerProofIdentity -cne
            [string]$pendingPeerReceipts[0].receiptIdentity) {
        return $false
    }
    foreach ($field in @(
            'ownerKey', 'pairKey', 'peerProofAuthorityId',
            'peerProofIdentity', 'peerProofFingerprint',
            'peerProofYieldGeneration')) {
        if ([string]$pendingTyped.$field -in @('', '(none)', '0') -or
            [string]$pendingTyped.$field -cne [string]$typed.$field) {
            return $false
        }
    }
    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            $pendingTyped.sourceBoundary $typed.sourceBoundary)) {
        return $false
    }
    foreach ($subject in @($pendingOperation, $operation)) {
        foreach ($field in @(
                'commandIdentity', 'commandGeneration', 'commandFingerprint',
                'receiptIdentity', 'receiptGeneration', 'receiptStatus',
                'receiptFingerprint', 'receiptValidated')) {
            if ([string]$subject.$field -cne
                [string]$pendingPeerReceipts[0].$field) {
                return $false
            }
        }
    }
    foreach ($peerTyped in @($pendingTyped, $typed)) {
        foreach ($field in @(
                'localProofIdentity', 'localProofFingerprint',
                'pairedReleaseAuthorityId', 'pairedReleaseOutcome',
                'reservedRetryAuthorityId', 'failedPredecessorAuthorityId',
                'invalidationReason')) {
            if ([string]$peerTyped.$field -cne '(none)') { return $false }
        }
        if ([bool]$peerTyped.failedSupersessionCarriedPeerProof) {
            return $false
        }
        foreach ($field in @(
                'pairingGeneration', 'pairedReleaseGeneration',
                'reservationGeneration', 'consumingInstallGeneration')) {
            if ([long]$peerTyped.$field -ne 0) { return $false }
        }
    }
    return [long]$pendingPeerReceipts[0].commandGeneration -eq
            [long]$pendingOperation.operationGeneration -and
        [long]$pendingPeerReceipts[0].receiptGeneration -eq
            [long]$pendingOperation.operationGeneration
}

function Get-FileTransferCoordinatorSnapshotFamily4CatalogVariant {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        $Operation = $null
    )

    if ([string]$Envelope.decisionKind -ceq 'FallbackTailFailed') {
        $operations = @($Envelope.aggregate.operations)
        if ([string]$Envelope.decisionStatus -in @('Idempotent', 'Rejected')) {
            if ($operations.Count -eq 0) {
                return 'fallback_tail_failed_attempted_decision_missing'
            }
            return 'fallback_tail_failed_attempted_decision_invalid'
        }
        $freshFailed = @($operations | Where-Object {
                [string]$_.family -ceq 'fallback_tail_yield' -and
                [string]$_.operationRole -ceq 'yield_current' -and
                [string]$_.operationKind -ceq 'Failed' -and
                [string]$_.beforeState -ceq 'None' -and
                [string]$_.afterState -in @('YieldIssued', 'PeerProofObserved')
            })
        $freshRecheckGeneration = 0L
        if ($freshFailed.Count -eq 1) {
            $freshTyped = Get-FileTransferCoordinatorSnapshotObjectProperty `
                -InputObject $freshFailed[0] -Name 'fallbackTailYield'
            $freshRecheckGeneration = [long](Get-FileTransferCoordinatorSnapshotObjectProperty `
                -InputObject $freshTyped -Name 'retryRecheckGeneration' -Default 0L)
        }
        if ([string]$Envelope.decisionStatus -ceq 'Accepted' -and
            $freshFailed.Count -eq 1 -and
            ($freshRecheckGeneration -ne 0 -or
             @($operations | Where-Object {
                    [string]$_.family -ceq 'fallback_tail_yield' -and
                    [string]$_.operationRole -ceq 'retry_recheck_terminal' -and
                    [string]$_.operationKind -ceq 'RetryDispatchRecheck'
                }).Count -gt 0)) {
            return 'fresh_failed_yield_rebound_unrelated_terminal_retry_recheck_receipt'
        }
        if (@($operations | Where-Object {
                    [string]$_.operationRole -ceq
                        'peer_proof_pending_initial'
                }).Count -gt 0 -or
            @($operations | Where-Object {
                    [string]$_.operationRole -ceq 'yield_current' -and
                    [string]$_.afterState -ceq 'PeerProofObserved'
                }).Count -gt 0) {
            return 'peer_first_failed_yield_omitted_from_family4_catalog'
        }
        return 'fresh_failed_yield_rebound_unrelated_last_consumed_reservation_and_install_receipt'
    }
    if ($null -ne $Operation -and
        [string]$Operation.operationRole -in @(
            'retry_request_pending', 'retry_recheck_pending')) {
        return 'family4_pending_retry_command_validated_as_completed_receipt'
    }
    if ([string]$Envelope.decisionKind -ceq
            'ApplyFallbackTailPeerProofReceipt' -and
        [string]$Envelope.decisionReason -ceq
            'fallback_tail_initial_peer_proof_receipt_superseded_after_checkpoint_advance') {
        return 'current_checkpoint_peer_proof_successor_transition_invalid'
    }
    if ([string]$Envelope.decisionKind -ceq 'FallbackTailQuiescent') {
        if (@($Envelope.aggregate.operations | Where-Object {
                    $_.family -ceq 'fallback_tail_yield' -and $_.operationRole -ceq 'yield_terminal' -and
                    $_.operationKind -ceq 'Quiescent'
                }).Count -gt 0) {
            return 'failed_to_quiescent_terminal_slot_rollover_rejected_as_extra_operation'
        }
        $failedSupersession =
            Get-FileTransferCoordinatorSnapshotFallbackTailFailedSupersessionShape `
                -Operations @($Envelope.aggregate.operations)
        if ($null -ne $failedSupersession -or
            @($Envelope.aggregate.operations | Where-Object {
                    [string]$_.operationRole -ceq 'yield_terminal' -and
                    [string]$_.operationKind -ceq 'Failed'
                }).Count -gt 0 -or
            @($Envelope.aggregate.operations | Where-Object {
                    [bool](Get-FileTransferCoordinatorSnapshotObjectProperty `
                        -InputObject $_.fallbackTailYield `
                        -Name 'failedSupersessionCarriedPeerProof' `
                        -Default $false)
                }).Count -gt 0) {
            return 'failed_to_quiescent_carried_peer_proof_supersession_omitted_from_fallback_tail_quiescent_catalog'
        }
        if (@($Envelope.aggregate.operations | Where-Object { $_.operationRole -ceq 'peer_proof_pending_initial' }).Count -gt 0 -and
            @($Envelope.aggregate.operations | Where-Object { $_.operationRole -ceq 'yield_current' -and $_.afterState -ceq 'YieldIssued' }).Count -gt 0) {
            return 'checkpoint_stale_pending_peer_proof_retirement_rejected_by_quiescent_shape_catalog'
        }
        return 'fallback_tail_quiescent_event_omitted_from_family4_catalog'
    }
    return '(none)'
}

function Get-FileTransferCoordinatorSnapshotPendingPeerProofBindings {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()]
        [object[]]$AggregateEntries,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()]
        [object[]]$Effects
    )

    $bindings = [Collections.Generic.List[object]]::new()
    $effectIndex = @{}
    foreach ($effect in $Effects) {
        if ([string]$effect.EffectKind -cne 'peer_proof_observed') {
            continue
        }
        $key = "$($effect.CoordinatorInstanceId)|$($effect.TransitionId)|$($effect.OperationKey)"
        if (-not $effectIndex.ContainsKey($key)) {
            $effectIndex[$key] = [Collections.Generic.List[object]]::new()
        }
        $effectIndex[$key].Add($effect) | Out-Null
    }

    foreach ($entry in @($aggregateEntries | Where-Object {
                [string]$_.Boundary.decisionKind -ceq
                    'ApplyFallbackTailPeerProofReceipt' -and
                [string]$_.Boundary.decisionStatus -ceq 'Accepted' -and
                [string]$_.Boundary.decisionReason -ceq
                    'fallback_tail_initial_peer_proof_receipt_retained'
            })) {
        $pending = @($entry.Boundary.operations | Where-Object {
                [string]$_.family -ceq 'fallback_tail_yield' -and
                [string]$_.operationRole -ceq
                    'peer_proof_pending_initial' -and
                [string]$_.operationKind -ceq 'PeerProof' -and
                [string]$_.beforeState -ceq 'None' -and
                [string]$_.afterState -ceq 'Pending' -and
                [bool]$_.stateApplied
            })
        if ($pending.Count -ne 1) {
            continue
        }

        $operation = $pending[0]
        $typed = $operation.fallbackTailYield
        $receipts = @($typed.receipts | Where-Object {
                [string]$_.stage -ceq 'peer_proof' -and
                [string]$_.receiptStatus -ceq 'Observed' -and
                [bool]$_.receiptValidated
            })
        $expectedOperationKey = @(
            'fallback_tail_yield',
            'peer_proof_pending_initial',
            [string]$operation.authorityId,
            ([long]$operation.operationGeneration).ToString(
                [Globalization.CultureInfo]::InvariantCulture),
            'PeerProof') -join '|'
        $rawPeerAuthority = [string](
            Get-FileTransferCoordinatorSnapshotObjectProperty `
                -InputObject $typed `
                -Name 'peerProofAuthorityId' `
                -Default '(none)')
        $scopeEnvelope = [pscustomobject]@{
            aggregate = [pscustomobject]@{
                afterBoundary = $entry.AfterBoundary
            }
        }
        if ($null -eq $typed -or
            $receipts.Count -ne 1 -or
            [string]$operation.operationKey -cne $expectedOperationKey -or
            [string]$operation.predecessorAuthorityId -cne '(none)' -or
            [long]$operation.operationGeneration -le 0L -or
            $rawPeerAuthority -notmatch
                '^fta2-fallback_tail_yield-[0-9a-f]{24}$' -or
            (ConvertTo-FileTransferCoordinatorEvidenceToken `
                $rawPeerAuthority) -cne [string]$operation.authorityId -or
            [string]$typed.role -cne 'peer_proof_pending_initial' -or
            [string]$typed.yieldKind -cne 'PeerProof' -or
            [string]$typed.pairingState -cne 'Pending' -or
            [long]$typed.peerProofYieldGeneration -ne
                [long]$operation.operationGeneration -or
            [string]$typed.peerProofIdentity -in @('', '(none)') -or
            [string]$typed.peerProofFingerprint -in @('', '(none)') -or
            [string]$typed.ownerKey -in @('', '(none)') -or
            [string]$typed.pairKey -in @('', '(none)') -or
            -not (Test-FileTransferCoordinatorSnapshotFamily4StageEvidence `
                -Operation $operation) -or
            -not (Test-FileTransferCoordinatorSnapshotFallbackTailOperationScope `
                -Envelope $scopeEnvelope `
                -Operation $operation)) {
            continue
        }

        $receipt = $receipts[0]
        if ([string]$operation.commandIdentity -cne
                [string]$receipt.commandIdentity -or
            [long]$operation.commandGeneration -ne
                [long]$receipt.commandGeneration -or
            [string]$operation.commandFingerprint -cne
                [string]$receipt.commandFingerprint -or
            [string]$operation.receiptIdentity -cne
                [string]$receipt.receiptIdentity -or
            [long]$operation.receiptGeneration -ne
                [long]$receipt.receiptGeneration -or
            [string]$operation.receiptStatus -cne
                [string]$receipt.receiptStatus -or
            [string]$operation.receiptFingerprint -cne
                [string]$receipt.receiptFingerprint -or
            -not [bool]$operation.receiptValidated) {
            continue
        }

        $effectKey = "$($entry.Boundary.coordinatorInstanceId)|$($entry.Boundary.boundaryId)|$($operation.operationKey)"
        $matchingEffects = @($effectIndex[$effectKey] | Where-Object {
                $null -ne $_ -and [string]$_.EffectKind -ceq 'peer_proof_observed' -and
                (Test-FileTransferCoordinatorSnapshotExactEffectBinding `
                    -Boundary $entry.Boundary `
                    -Operation $operation `
                    -Effect $_)
            })
        if ($matchingEffects.Count -ne 1 -or
            [string]$matchingEffects[0].Writer -cne
                [string]$entry.Boundary.writer) {
            continue
        }

        $effect = $matchingEffects[0]
        $bindings.Add([pscustomobject]@{
                BindingKind = 'pending_peer_proof'
                CoordinatorInstanceId =
                    [string]$entry.Boundary.coordinatorInstanceId
                Writer = [string]$entry.Boundary.writer
                TransitionId = [string]$entry.Boundary.boundaryId
                TransitionSequence = [long]$entry.Boundary.writerSequence
                StateGeneration = [long]$entry.Boundary.afterStateGeneration
                SessionId = [string]$entry.SessionId
                TransferId = [string]$entry.TransferId
                Direction = [string]$entry.Direction
                OperationKey = [string]$operation.operationKey
                OperationAuthorityId = [string]$operation.authorityId
                OperationGeneration = [long]$operation.operationGeneration
                OperationRole = [string]$operation.operationRole
                BeforeState = [string]$operation.beforeState
                AfterState = [string]$operation.afterState
                OwnerKey = [string]$typed.ownerKey
                PairKey = [string]$typed.pairKey
                PeerProofAuthorityId = $rawPeerAuthority
                PeerProofGeneration = [long](
                    Get-FileTransferCoordinatorSnapshotObjectProperty `
                        -InputObject $typed `
                        -Name 'peerProofGeneration' `
                        -Default 0L)
                PeerProofYieldGeneration =
                    [long]$typed.peerProofYieldGeneration
                PeerProofIdentity = [string]$typed.peerProofIdentity
                PeerProofFingerprint = [string]$typed.peerProofFingerprint
                PairingState = [string]$typed.pairingState
                PairingGeneration = [long]$typed.pairingGeneration
                LocalProofIdentity = [string]$typed.localProofIdentity
                LocalProofFingerprint = [string]$typed.localProofFingerprint
                PairedReleaseAuthorityId =
                    [string]$typed.pairedReleaseAuthorityId
                PairedReleaseGeneration =
                    [long]$typed.pairedReleaseGeneration
                PairedReleaseOutcome =
                    [string]$typed.pairedReleaseOutcome
                AggregateBoundary = $entry.AfterBoundary
                SourceBoundary = $typed.sourceBoundary
                EffectiveBoundary = $typed.effectiveBoundary
                TargetBoundary = $typed.targetBoundary
                PeerProofReceipt = $receipt
                PeerProofEffect = $effect
                LocalProofReceipt = $null
                LocalProofEffects = @()
                PairedReleaseReceipt = $null
                PairedReleaseEffect = $null
            }) | Out-Null
    }

    return $bindings.ToArray()
}

function Test-FileTransferCoordinatorSnapshotFallbackTailPeerProofEffect {
    param(
        [Parameter(Mandatory = $true)]$Entry,
        [Parameter(Mandatory = $true)]$OperationHistory,
        [Parameter(Mandatory = $true)]$Effects,
        $SupersessionPeerProofOrigins = @{}
    )

    $operation = $Entry.Operation
    $typed = $operation.fallbackTailYield
    $receipt = @($typed.receipts | Where-Object {
            [string]$_.stage -ceq 'peer_proof' -and
            [bool]$_.receiptValidated
        })
    if ($receipt.Count -ne 1) {
        return $false
    }

    $matchingEffects = @($Effects | Where-Object {
            [string]$_.CoordinatorInstanceId -ceq
                [string]$Entry.Boundary.coordinatorInstanceId -and
            [string]$_.Family -ceq 'fallback_tail_yield' -and
            [string]$_.EffectKind -ceq 'peer_proof_observed' -and
            [string]$_.ReceiptIdentity -ceq
                [string]$receipt[0].receiptIdentity -and
            [long]$_.ReceiptGeneration -eq
                [long]$receipt[0].receiptGeneration -and
            [string]$_.ReceiptStatus -ceq
                [string]$receipt[0].receiptStatus -and
            [string]$_.ReceiptFingerprint -ceq
                [string]$receipt[0].receiptFingerprint
        })
    if ($matchingEffects.Count -ne 1) {
        return $false
    }

    $isPeerFirstCarry =
        [string]$Entry.Boundary.decisionKind -in @(
            'FallbackTailQuiescent', 'FallbackTailFailed') -and
        [string]$Entry.Boundary.decisionStatus -ceq 'Accepted' -and
        [string]$operation.operationRole -ceq 'yield_current' -and
        [string]$operation.operationKind -ceq
            ([string]$Entry.Boundary.decisionKind).Substring(
                'FallbackTail'.Length) -and
        [string]$operation.beforeState -ceq 'None' -and
        [string]$operation.afterState -ceq 'PeerProofObserved'
    if (-not $isPeerFirstCarry) {
        return Test-FileTransferCoordinatorSnapshotExactEffectBinding `
            -Boundary $Entry.Boundary `
            -Operation $operation `
            -Effect $matchingEffects[0]
    }

    $failedSupersession =
        Get-FileTransferCoordinatorSnapshotFallbackTailFailedSupersessionShape `
            -Operations @($Entry.Boundary.operations)
    if ($null -ne $failedSupersession -and
        [string]$operation.operationKey -ceq
            [string]$failedSupersession.QuiescentCurrent.operationKey) {
        $prior = $failedSupersession.FailedCurrent
        $entryKey = Get-FileTransferCoordinatorSnapshotFallbackTailYieldEntryKey -Entry $Entry
        if ($SupersessionPeerProofOrigins.ContainsKey($entryKey)) {
            $provenance = $SupersessionPeerProofOrigins[$entryKey]
            $origin = $provenance.Origin
            # Physical observation stays on the pending receipt; only the
            # validated two-hop slot history can carry it to this successor.
            $sameBoundary = $true
            foreach ($field in @('coordinatorInstanceId','writer','sessionId','transferId','direction',
                    'boundaryId','beforeStateGeneration','afterStateGeneration','beforeStateHash','afterStateHash')) {
                if ([string]$Entry.Boundary.$field -cne [string]$provenance.Boundary.$field) { $sameBoundary = $false }
            }
            return $sameBoundary -and
                (Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofSignature -Operation $operation) -ceq $provenance.Signature -and
                [string]$matchingEffects[0].Writer -ceq [string]$origin.Boundary.writer -and
                (Test-FileTransferCoordinatorSnapshotExactEffectBinding `
                    -Boundary $origin.Boundary -Operation $origin.Operation -Effect $matchingEffects[0])
        }
        if ([string]$matchingEffects[0].OperationKey -cne
                [string]$prior.operationKey -or
            [string]$matchingEffects[0].AuthorityId -cne
                [string]$prior.authorityId -or
            [long]$matchingEffects[0].OperationGeneration -ne
                [long]$prior.operationGeneration -or
            [string]$prior.fallbackTailYield.ownerKey -cne
                [string]$typed.ownerKey -or
            [string]$prior.fallbackTailYield.pairKey -cne
                [string]$typed.pairKey -or
            [string]$prior.fallbackTailYield.peerProofAuthorityId -cne
                [string]$typed.peerProofAuthorityId -or
            [string]$prior.fallbackTailYield.peerProofIdentity -cne
                [string]$typed.peerProofIdentity -or
            [string]$prior.fallbackTailYield.peerProofFingerprint -cne
                [string]$typed.peerProofFingerprint -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                -Left $prior.fallbackTailYield.sourceBoundary `
                -Right $typed.sourceBoundary)) {
            return $false
        }

        $priorSignature =
            Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofSignature `
                -Operation $prior
        $effectSubjects = @($OperationHistory | Where-Object {
                [string]$_.Boundary.coordinatorInstanceId -ceq
                    [string]$Entry.Boundary.coordinatorInstanceId -and
                [string]$_.Boundary.boundaryId -ceq
                    [string]$matchingEffects[0].TransitionId -and
                [long]$_.Boundary.afterStateGeneration -eq
                    [long]$matchingEffects[0].StateGeneration -and
                [string]$_.Operation.operationKey -ceq
                    [string]$prior.operationKey -and
                [string]$_.Operation.operationRole -ceq 'yield_current' -and
                [string]$_.Operation.operationKind -ceq 'Failed' -and
                [string]$_.Operation.afterState -ceq 'PeerProofObserved' -and
                [string]$_.Boundary.decisionKind -ceq 'ApplyFallbackTailPeerProofReceipt' -and
                [string]$_.Boundary.decisionStatus -ceq 'Accepted' -and
                (Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofSignature `
                    -Operation $_.Operation) -ceq $priorSignature -and
                (Test-FileTransferCoordinatorSnapshotExactEffectBinding `
                    -Boundary $_.Boundary `
                    -Operation $_.Operation `
                    -Effect $matchingEffects[0])
            })
        return $effectSubjects.Count -eq 1
    }

    $pending = @($Entry.Boundary.operations | Where-Object {
            [string]$_.family -ceq 'fallback_tail_yield' -and
            [string]$_.operationRole -ceq 'peer_proof_pending_initial' -and
            [string]$_.operationKind -ceq 'PeerProof' -and
            [string]$_.beforeState -ceq 'Pending' -and
            [string]$_.afterState -ceq 'None' -and
            [bool]$_.stateApplied
        })
    if ($pending.Count -ne 1 -or
        [string]$matchingEffects[0].OperationKey -cne
            [string]$pending[0].operationKey -or
        [string]$matchingEffects[0].AuthorityId -cne
            [string]$pending[0].authorityId -or
        [long]$matchingEffects[0].OperationGeneration -ne
            [long]$pending[0].operationGeneration -or
        [string]$pending[0].fallbackTailYield.ownerKey -cne
            [string]$typed.ownerKey -or
        [string]$pending[0].fallbackTailYield.pairKey -cne
            [string]$typed.pairKey -or
        [string]$pending[0].fallbackTailYield.peerProofIdentity -cne
            [string]$typed.peerProofIdentity -or
        [string]$pending[0].fallbackTailYield.peerProofFingerprint -cne
            [string]$typed.peerProofFingerprint -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            -Left $pending[0].fallbackTailYield.sourceBoundary `
            -Right $typed.sourceBoundary)) {
        return $false
    }

    $effectSubjects = @($OperationHistory | Where-Object {
            [string]$_.Boundary.coordinatorInstanceId -ceq
                [string]$Entry.Boundary.coordinatorInstanceId -and
            [string]$_.Boundary.boundaryId -ceq
                [string]$matchingEffects[0].TransitionId -and
            [long]$_.Boundary.afterStateGeneration -eq
                [long]$matchingEffects[0].StateGeneration -and
            [string]$_.Operation.operationKey -ceq
                [string]$pending[0].operationKey -and
            [string]$_.Operation.operationRole -ceq
                'peer_proof_pending_initial' -and
            [string]$_.Operation.afterState -ceq 'Pending' -and
            (Test-FileTransferCoordinatorSnapshotExactEffectBinding `
                -Boundary $_.Boundary `
                -Operation $_.Operation `
                -Effect $matchingEffects[0])
        })
    return $effectSubjects.Count -eq 1
}

function Get-FileTransferCoordinatorSnapshotFallbackTailYieldEntryKey {
    param([Parameter(Mandatory = $true)]$Entry)

    return @(
        [string]$Entry.Boundary.boundaryId,
        [string]$Entry.Operation.operationKey,
        [string]$Entry.Operation.operationRole,
        [string]$Entry.Operation.beforeState,
        [string]$Entry.Operation.afterState) -join '|'
}

function Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofSignature {
    param([Parameter(Mandatory = $true)]$Operation)

    $typed = $Operation.fallbackTailYield
    if ($null -eq $typed) {
        return ''
    }
    $receipts = @($typed.receipts | Where-Object {
            [string]$_.stage -ceq 'peer_proof' -and
            [bool]$_.receiptValidated
        })
    if ($receipts.Count -ne 1) {
        return ''
    }
    $receipt = $receipts[0]
    return [ordered]@{
        AuthorityId = [string]$Operation.authorityId
        OperationGeneration = [long]$Operation.operationGeneration
        OperationKind = [string]$Operation.operationKind
        OwnerKey = [string]$typed.ownerKey
        PairKey = [string]$typed.pairKey
        PeerProofAuthorityId = [string](
            Get-FileTransferCoordinatorSnapshotObjectProperty `
                -InputObject $typed -Name 'peerProofAuthorityId' -Default '(none)')
        PeerProofGeneration = [long](
            Get-FileTransferCoordinatorSnapshotObjectProperty `
                -InputObject $typed -Name 'peerProofGeneration' -Default 0L)
        PeerProofYieldGeneration = [long](
            Get-FileTransferCoordinatorSnapshotObjectProperty `
                -InputObject $typed -Name 'peerProofYieldGeneration' -Default 0L)
        PeerProofIdentity = [string]$typed.peerProofIdentity
        PeerProofFingerprint = [string]$typed.peerProofFingerprint
        Receipt = [ordered]@{
            CommandIdentity = [string]$receipt.commandIdentity
            CommandGeneration = [long]$receipt.commandGeneration
            CommandFingerprint = [string]$receipt.commandFingerprint
            ReceiptIdentity = [string]$receipt.receiptIdentity
            ReceiptGeneration = [long]$receipt.receiptGeneration
            ReceiptStatus = [string]$receipt.receiptStatus
            ReceiptReason = [string]$receipt.receiptReason
            ReceiptFingerprint = [string]$receipt.receiptFingerprint
        }
        SourceBoundary = $typed.sourceBoundary
        EffectiveBoundary = $typed.effectiveBoundary
        TargetBoundary = $typed.targetBoundary
    } | ConvertTo-Json -Depth 64 -Compress
}

function Test-FileTransferCoordinatorSnapshotFallbackTailScopeInvalidationRollback {
    param([Parameter(Mandatory = $true)]$Entry)

    $boundary = $Entry.Boundary
    $current = $Entry.Operation
    if ([string]$boundary.decisionKind -cne
            'CommitRuntimeUnlockRetryLifecycleScopeInvalidation' -or
        [string]$boundary.decisionStatus -cne 'Accepted' -or
        [string]$boundary.decisionReason -cne
            'retry_dispatch_scope_invalidated' -or
        [string]$current.operationRole -cne 'yield_current' -or
        [string]$current.beforeState -cne 'ReleaseReserved' -or
        [string]$current.afterState -cne 'Paired' -or
        -not [bool]$current.stateApplied) {
        return $false
    }

    $family4 = @($boundary.operations | Where-Object {
            [string]$_.family -ceq 'fallback_tail_yield'
        })
    $reservation = @($family4 | Where-Object {
            [string]$_.operationRole -ceq
                'release_reservation_current' -and
            [string]$_.operationKind -ceq
                'PairedReleaseReservation' -and
            [string]$_.beforeState -ceq 'Reserved' -and
            [string]$_.afterState -ceq 'None' -and
            [bool]$_.stateApplied
        })
    $pendingRequest = @($family4 | Where-Object {
            [string]$_.operationRole -ceq 'retry_request_pending' -and
            [string]$_.operationKind -ceq 'RetryRequest' -and
            [string]$_.beforeState -ceq 'Pending' -and
            [string]$_.afterState -ceq 'Pending'
        })
    if ($family4.Count -ne 3 -or $reservation.Count -ne 1 -or
        $pendingRequest.Count -ne 1) {
        return $false
    }
    foreach ($operation in @($reservation[0], $pendingRequest[0])) {
        if ([string]$operation.authorityId -cne
                [string]$current.authorityId -or
            [long]$operation.operationGeneration -ne
                [long]$current.operationGeneration -or
            [string]$operation.fallbackTailYield.ownerKey -cne
                [string]$current.fallbackTailYield.ownerKey -or
            [string]$operation.fallbackTailYield.pairKey -cne
                [string]$current.fallbackTailYield.pairKey -or
            [string]$operation.fallbackTailYield.peerProofFingerprint -cne
                [string]$current.fallbackTailYield.peerProofFingerprint) {
            return $false
        }
    }

    $family2 = @($boundary.operations | Where-Object {
            [string]$_.family -ceq 'runtime_unlock_retry_lifecycle'
        })
    $pendingAttempt = @($family2 | Where-Object {
            [string]$_.operationRole -ceq 'attempt_pending' -and
            [string]$_.operationKind -ceq 'RetryAttempt' -and
            [string]$_.beforeState -ceq 'Staged' -and
            [string]$_.afterState -ceq 'None' -and
            [bool]$_.stateApplied
        })
    $terminalAttempt = @($family2 | Where-Object {
            [string]$_.operationRole -ceq 'attempt_terminal' -and
            [string]$_.operationKind -ceq 'RetryAttempt' -and
            [string]$_.beforeState -ceq 'None' -and
            [string]$_.afterState -ceq 'Invalidated' -and
            [bool]$_.stateApplied
        })
    $scopeInvalidation = @($family2 | Where-Object {
            [string]$_.operationRole -ceq 'scope_invalidation' -and
            [string]$_.operationKind -ceq 'ScopeInvalidation' -and
            [string]$_.beforeState -ceq 'Pending' -and
            [string]$_.afterState -ceq 'Applied' -and
            [bool]$_.stateApplied -and [bool]$_.receiptValidated -and
            [string]$_.receiptStatus -ceq 'Applied'
        })
    if ($family2.Count -ne 3 -or $pendingAttempt.Count -ne 1 -or
        $terminalAttempt.Count -ne 1 -or $scopeInvalidation.Count -ne 1) {
        return $false
    }
    return [string]$pendingAttempt[0].authorityId -ceq
            [string]$terminalAttempt[0].authorityId -and
        [string]$pendingAttempt[0].authorityId -ceq
            [string]$scopeInvalidation[0].authorityId -and
        [long]$pendingAttempt[0].operationGeneration -eq
            [long]$terminalAttempt[0].operationGeneration -and
        [long]$pendingAttempt[0].operationGeneration -eq
            [long]$scopeInvalidation[0].operationGeneration
}

function Test-FileTransferCoordinatorSnapshotFallbackTailCurrentPeerProofSuccessor {
    param($Prior, $Current)

    if ($null -eq $Prior -or $null -eq $Current -or
        [string]$Prior.Operation.operationRole -cne 'yield_current' -or
        [string]$Current.Operation.operationRole -cne 'yield_current' -or
        [string]$Current.Operation.beforeState -cne
            [string]$Prior.Operation.afterState) {
        return $false
    }
    $before = [string]$Current.Operation.beforeState
    $after = [string]$Current.Operation.afterState
    if ($before -ceq $after) {
        return $true
    }
    if ($after -ceq 'None') {
        return $before -in @(
            'PeerProofObserved', 'Paired', 'ReleaseReserved',
            'ReleaseConsumed')
    }
    if ($before -ceq 'ReleaseReserved' -and $after -ceq 'Paired') {
        return Test-FileTransferCoordinatorSnapshotFallbackTailScopeInvalidationRollback `
            -Entry $Current
    }
    $rank = @{
        YieldIssued = 0
        LocalProofObserved = 1
        PeerProofObserved = 1
        Paired = 2
        ReleaseReserved = 3
        ReleaseConsumed = 4
    }
    return $rank.ContainsKey($before) -and $rank.ContainsKey($after) -and
        [int]$rank[$after] -ge [int]$rank[$before]
}

function Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofLifecycle {
    param(
        [Parameter(Mandatory = $true)]$YieldHistory,
        [Parameter(Mandatory = $true)]$OperationHistory,
        [Parameter(Mandatory = $true)]$Effects,
        $SupersessionPeerProofOrigins = @{}
    )

    $results = @{}
    $ordered = @($YieldHistory | Sort-Object {
            [long]$_.Boundary.decisionUtcMilliseconds
        }, {
            [long]$_.Boundary.afterStateGeneration
        }, {
            [string]$_.Boundary.boundaryId
        }, {
            switch ([string]$_.Operation.operationRole) {
                'yield_current' { 0 }
                'yield_terminal' { 1 }
                'yield_retained_predecessor' { 2 }
                default { 3 }
            }
        })
    $originSignature = ''
    $originKind = ''
    $lastCurrent = $null
    $lastTerminal = $null

    foreach ($entry in $ordered) {
        $operation = $entry.Operation
        $role = [string]$operation.operationRole
        $entryKey = Get-FileTransferCoordinatorSnapshotFallbackTailYieldEntryKey `
            -Entry $entry
        $signature =
            Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofSignature `
                -Operation $operation
        $valid = $true
        $variant = '(none)'

        if ([string]::IsNullOrWhiteSpace($originSignature)) {
            if (-not [string]::IsNullOrWhiteSpace($signature)) {
                $valid = $role -ceq 'yield_current' -and
                    (Test-FileTransferCoordinatorSnapshotFallbackTailPeerProofEffect `
                        -Entry $entry `
                        -OperationHistory $OperationHistory `
                        -Effects $Effects -SupersessionPeerProofOrigins $SupersessionPeerProofOrigins)
                if ($valid -and $null -ne $lastCurrent) {
                    $valid =
                        Test-FileTransferCoordinatorSnapshotFallbackTailCurrentPeerProofSuccessor `
                            -Prior $lastCurrent -Current $entry
                }
                if ($valid) {
                    $originSignature = $signature
                    $failedSupersession =
                        Get-FileTransferCoordinatorSnapshotFallbackTailFailedSupersessionShape `
                            -Operations @($entry.Boundary.operations)
                    $originKind = if ($null -ne $failedSupersession -and
                        [string]$operation.operationKey -ceq
                            [string]$failedSupersession.QuiescentCurrent.operationKey) {
                        'FailedSupersession'
                    }
                    elseif (
                        [string]$entry.Boundary.decisionKind -in @(
                            'FallbackTailQuiescent', 'FallbackTailFailed') -and
                        [string]$operation.beforeState -ceq 'None' -and
                        [string]$operation.afterState -ceq
                            'PeerProofObserved') {
                        if ([string]$entry.Boundary.decisionKind -ceq
                                'FallbackTailFailed') {
                            'PeerFirstFailedPending'
                        }
                        else {
                            'PeerFirstPending'
                        }
                    }
                    else {
                        'DirectCurrent'
                    }
                }
                else {
                    $variant = if ($null -ne (
                        Get-FileTransferCoordinatorSnapshotFallbackTailFailedSupersessionShape `
                            -Operations @($entry.Boundary.operations))) {
                        'failed_to_quiescent_carried_peer_proof_provenance_invalid'
                    }
                    else {
                        if ([string]$entry.Boundary.decisionKind -ceq
                                'FallbackTailFailed') {
                            'peer_first_failed_yield_peer_proof_provenance_invalid'
                        }
                        else {
                            'fallback_tail_quiescent_peer_proof_carry_invalid'
                        }
                    }
                }
            }
        }
        else {
            if ([string]::IsNullOrWhiteSpace($signature) -or
                $signature -cne $originSignature) {
                $valid = $false
            }
            elseif ($role -ceq 'yield_current') {
                $valid =
                    Test-FileTransferCoordinatorSnapshotFallbackTailCurrentPeerProofSuccessor `
                        -Prior $lastCurrent -Current $entry
            }
            elseif ($role -ceq 'yield_terminal') {
                if ($null -ne $lastTerminal) {
                    $valid = [string]$operation.beforeState -ceq
                        [string]$lastTerminal.Operation.afterState
                }
                else {
                    $currentRetirements = @($ordered | Where-Object {
                            [string]$_.Boundary.boundaryId -ceq
                                [string]$entry.Boundary.boundaryId -and
                            [string]$_.Operation.operationRole -ceq
                                'yield_current' -and
                            [string]$_.Operation.afterState -ceq 'None' -and
                            (Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofSignature `
                                -Operation $_.Operation) -ceq $originSignature -and
                            [bool]$results[(
                                Get-FileTransferCoordinatorSnapshotFallbackTailYieldEntryKey `
                                    -Entry $_)].Valid
                        })
                    $valid = $currentRetirements.Count -eq 1 -and
                        [string]$operation.beforeState -ceq 'None' -and
                        [string]$operation.afterState -ceq 'Invalidated'
                }
            }
            elseif ($role -ceq 'yield_retained_predecessor') {
                $terminalProjections = @($ordered | Where-Object {
                        [string]$_.Boundary.boundaryId -ceq
                            [string]$entry.Boundary.boundaryId -and
                        [string]$_.Operation.operationRole -ceq
                            'yield_terminal' -and
                        (Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofSignature `
                            -Operation $_.Operation) -ceq $originSignature -and
                        [bool]$results[(
                            Get-FileTransferCoordinatorSnapshotFallbackTailYieldEntryKey `
                                -Entry $_)].Valid
                    })
                $valid = $terminalProjections.Count -eq 1 -or
                    ($null -ne $lastTerminal -and
                     [string]$operation.beforeState -ceq
                        [string]$lastTerminal.Operation.afterState)
            }
            else {
                $valid = $false
            }
            if (-not $valid -and $originKind -in @(
                    'PeerFirstPending', 'PeerFirstFailedPending') -and
                $role -ceq 'yield_current' -and
                [string]$operation.beforeState -ceq 'PeerProofObserved' -and
                [string]$operation.afterState -ceq 'Paired') {
                $variant =
                    'peer_first_proof_effect_rechecked_against_later_paired_current_transition'
            }
            elseif (-not $valid) {
                $variant =
                    'fallback_tail_peer_proof_provenance_invalid'
            }
        }

        $results[$entryKey] = [pscustomobject]@{
            Valid = $valid
            Variant = $variant
            OriginKind = $originKind
        }
        if ($role -ceq 'yield_current') {
            $lastCurrent = $entry
        }
        elseif ($role -ceq 'yield_terminal') {
            $lastTerminal = $entry
        }
    }
    return $results
}

function Test-FileTransferCoordinatorSnapshotFamily4StageEvidence {
    param([Parameter(Mandatory = $true)]$Operation)

    $typed = $Operation.fallbackTailYield
    $receipts = @($typed.receipts)
    $role = [string]$Operation.operationRole
    if ($role -in @('retry_request_pending', 'retry_recheck_pending')) {
        $kind = if ($role -ceq 'retry_request_pending') {
            'RetryRequest'
        }
        else {
            'RetryDispatchRecheck'
        }
        $expectedKey = @(
            'fallback_tail_yield', $role, [string]$Operation.authorityId,
            ([long]$Operation.operationGeneration).ToString(
                [System.Globalization.CultureInfo]::InvariantCulture), $kind) -join '|'
        if ($receipts.Count -ne 1 -or
            $role -cnotin @('retry_request_pending', 'retry_recheck_pending') -or
            [string]$Operation.family -cne 'fallback_tail_yield' -or
            [string]$typed.role -cne $role -or
            [string]$Operation.operationKind -cne $kind -or
            [string]$Operation.operationKey -cne $expectedKey -or
            [string]::IsNullOrWhiteSpace([string]$Operation.authorityId) -or
            [string]$Operation.authorityId -ceq '(none)' -or
            [long]$Operation.operationGeneration -le 0 -or
            [long]$Operation.commandGeneration -ne
                [long]$Operation.operationGeneration -or
            "$($Operation.beforeState)->$($Operation.afterState)" -cnotin @(
                'None->Pending', 'Pending->Pending', 'Pending->None')) {
            return $false
        }
        $receipt = $receipts[0]
        foreach ($name in @(
                'stage', 'commandIdentity', 'commandGeneration',
                'commandFingerprint', 'receiptIdentity', 'receiptGeneration',
                'receiptStatus', 'receiptReason', 'receiptFingerprint',
                'receiptValidated')) {
            if ($null -eq $receipt -or
                $receipt.PSObject.Properties.Name -notcontains $name) {
                return $false
            }
        }
        if ([string]$receipt.stage -cne $kind -or
            [string]$receipt.receiptReason -cne 'Pending') {
            return $false
        }
        foreach ($name in @('commandIdentity', 'commandFingerprint')) {
            if ([string]::IsNullOrWhiteSpace([string]$Operation.$name) -or
                [string]$Operation.$name -ceq '(none)' -or
                [string]$Operation.$name -cne [string]$receipt.$name) {
                return $false
            }
        }
        if ([long]$receipt.commandGeneration -ne
            [long]$Operation.commandGeneration) {
            return $false
        }
        foreach ($subject in @($Operation, $receipt)) {
            if ([string]$subject.receiptIdentity -cne '(none)' -or
                [long]$subject.receiptGeneration -ne 0 -or
                [string]$subject.receiptFingerprint -cne '(none)' -or
                [string]$subject.receiptStatus -cne 'Pending' -or
                $subject.receiptValidated -isnot [bool] -or
                [bool]$subject.receiptValidated) {
                return $false
            }
        }
        return $true
    }

    foreach ($receipt in $receipts) {
        if ([string]::IsNullOrWhiteSpace([string]$receipt.stage) -or
            [string]::IsNullOrWhiteSpace([string]$receipt.commandIdentity) -or
            [long]$receipt.commandGeneration -le 0 -or
            [string]::IsNullOrWhiteSpace([string]$receipt.commandFingerprint) -or
            [string]::IsNullOrWhiteSpace([string]$receipt.receiptIdentity) -or
            [long]$receipt.receiptGeneration -le 0 -or
            [string]::IsNullOrWhiteSpace([string]$receipt.receiptStatus) -or
            [string]::IsNullOrWhiteSpace([string]$receipt.receiptFingerprint)) {
            return $false
        }
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotFamily4OperationCatalog {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        [Parameter(Mandatory = $true)]$Operation
    )

    if ([string]$Operation.family -notin
        $script:FileTransferCoordinatorSnapshotFamily4) {
        return $true
    }
    $expectedOperationKey = @(
        [string]$Operation.family,
        [string]$Operation.operationRole,
        [string]$Operation.authorityId,
        ([long]$Operation.operationGeneration).ToString(
            [System.Globalization.CultureInfo]::InvariantCulture),
        [string]$Operation.operationKind) -join '|'
    $decisionKind = [string]$Envelope.decisionKind
    $isExactFallbackTailQuiescent =
        Test-FileTransferCoordinatorSnapshotFallbackTailQuiescentEnvelope `
            -Envelope $Envelope
    $knownAtomicDecision =
        $decisionKind -in $script:FileTransferCoordinatorSnapshotFamily4DecisionKinds -or
        $isExactFallbackTailQuiescent -or
        (Test-FileTransferCoordinatorSnapshotFallbackTailFailedEnvelope $Envelope) -or
        $decisionKind -in $script:FileTransferCoordinatorSnapshotFamily3DecisionKinds -or
        $decisionKind -in $script:FileTransferCoordinatorSnapshotFamily2DecisionKinds -or
        $decisionKind -in $script:FileTransferCoordinatorSnapshotFamily1DecisionKinds -or
        (Test-FileTransferCoordinatorSnapshotFallbackCheckpointOperation `
            -Envelope $Envelope -Operation $Operation) -or
        $decisionKind -in @(
            'StartTransfer', 'RoutePublication', 'RouteCommitPublication',
            'Terminalized', 'LegacyFamilyCoordinatorDecision',
            'LegacyFamilyCoordinatorActionDecision')
    if ([string]$Operation.operationKey -cne $expectedOperationKey -or
        -not $knownAtomicDecision -or
        [string]$Operation.operationRole -notin
            $script:FileTransferCoordinatorSnapshotFamily4OperationRoles -or
        $null -eq $Operation.fallbackTailYield -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Operation.fallbackTailYield.sourceBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Operation.fallbackTailYield.effectiveBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Operation.fallbackTailYield.targetBoundary)) {
        return $false
    }

    foreach ($name in @(
            'role', 'yieldKind', 'pairingState', 'ownerKey', 'pairKey',
            'localProofIdentity', 'localProofFingerprint',
            'peerProofAuthorityId', 'peerProofIdentity',
            'peerProofFingerprint', 'pairedReleaseAuthorityId',
            'failedPredecessorAuthorityId', 'invalidationReason',
            'receipts')) {
        if ($Operation.fallbackTailYield.PSObject.Properties.Name -notcontains
            $name) {
            return $false
        }
    }
    return Test-FileTransferCoordinatorSnapshotFamily4StageEvidence `
        -Operation $Operation
}

function Test-FileTransferCoordinatorSnapshotFamily5OperationCatalog {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        [Parameter(Mandatory = $true)]$Operation
    )

    if ([string]$Operation.family -notin
        $script:FileTransferCoordinatorSnapshotFamily5) {
        return $true
    }
    $expectedOperationKey = @(
        [string]$Operation.family,
        [string]$Operation.operationRole,
        [string]$Operation.authorityId,
        ([long]$Operation.operationGeneration).ToString(
            [System.Globalization.CultureInfo]::InvariantCulture),
        [string]$Operation.operationKind) -join '|'
    $decisionKind = [string]$Envelope.decisionKind
    $knownAtomicDecision =
        $decisionKind -in $script:FileTransferCoordinatorSnapshotFamily5DecisionKinds -or
        $decisionKind -ceq 'RepairReplay' -or
        $decisionKind -in $script:FileTransferCoordinatorSnapshotFamily3DecisionKinds -or
        $decisionKind -in $script:FileTransferCoordinatorSnapshotFamily2DecisionKinds -or
        $decisionKind -in $script:FileTransferCoordinatorSnapshotFamily1DecisionKinds -or
        (Test-FileTransferCoordinatorSnapshotFallbackCheckpointOperation `
            -Envelope $Envelope -Operation $Operation) -or
        $decisionKind -in @(
            'StartTransfer', 'RoutePublication', 'RouteCommitPublication',
            'Terminalized', 'LegacyFamilyCoordinatorDecision',
            'LegacyFamilyCoordinatorActionDecision')
    if ([string]$Operation.operationKey -cne $expectedOperationKey -or
        -not $knownAtomicDecision -or
        [string]$Operation.operationRole -notin
            $script:FileTransferCoordinatorSnapshotFamily5OperationRoles -or
        $null -eq $Operation.regularV4FrontierRepairPressure -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Operation.regularV4FrontierRepairPressure.sourceBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Operation.regularV4FrontierRepairPressure.effectiveBoundary)) {
        return $false
    }

    foreach ($name in @(
            'role', 'repairKind', 'repairKey', 'targetTransport',
            'pressureReason', 'bindingState', 'settlementOutcome',
            'settlementReason', 'successorAuthorityId',
            'recoveryAuthorityId', 'recoveryKind')) {
        if ($Operation.regularV4FrontierRepairPressure.PSObject.Properties.Name `
                -notcontains $name -or
            [string]::IsNullOrWhiteSpace(
                [string]$Operation.regularV4FrontierRepairPressure.$name)) {
            return $false
        }
    }
    if ($Operation.regularV4FrontierRepairPressure.PSObject.Properties.Name `
            -notcontains 'receipts') {
        return $false
    }
    foreach ($receipt in @(
            $Operation.regularV4FrontierRepairPressure.receipts)) {
        if ([string]::IsNullOrWhiteSpace([string]$receipt.stage) -or
            [string]::IsNullOrWhiteSpace([string]$receipt.commandIdentity) -or
            [long]$receipt.commandGeneration -le 0 -or
            [string]::IsNullOrWhiteSpace([string]$receipt.commandFingerprint) -or
            [string]::IsNullOrWhiteSpace([string]$receipt.receiptIdentity) -or
            [long]$receipt.receiptGeneration -le 0 -or
            [string]::IsNullOrWhiteSpace([string]$receipt.receiptStatus) -or
            [string]::IsNullOrWhiteSpace([string]$receipt.receiptFingerprint)) {
            return $false
        }
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotRecoveryHandoffTerminalReceipt {
    param([Parameter(Mandatory = $true)]$Operation)

    foreach ($name in @('family', 'operationRole', 'operationKind', 'operationKey', 'authorityId',
            'operationGeneration', 'commandIdentity', 'commandGeneration', 'commandFingerprint',
            'receiptIdentity', 'receiptGeneration', 'receiptStatus', 'receiptFingerprint', 'receiptValidated')) {
        if ($Operation.PSObject.Properties.Name -notcontains $name) { return $false }
    }
    $payload = Get-FileTransferCoordinatorSnapshotObjectProperty -InputObject $Operation `
        -Name 'regularV4FrontierRepairPressure' -Default $null
    if ($null -eq $payload) { return $false }
    foreach ($name in @('role', 'bindingState', 'receipts', 'decisionGeneration', 'pressureInstallGeneration',
            'recoveryOperationGeneration', 'recoveryKind', 'recoveryAuthorityId', 'currentAuthorityCleared',
            'retainedPredecessorCleared', 'sourceBoundary', 'effectiveBoundary')) {
        if ($payload.PSObject.Properties.Name -notcontains $name) { return $false }
    }
    if ([string]$Operation.family -cne 'regular_v4_frontier_repair_pressure' -or
        [string]$Operation.operationRole -cne 'recovery_handoff_terminal' -or
        [string]$Operation.operationKind -cne 'PressureRecoveryHandoff' -or
        $null -eq $payload -or
        [string]$payload.role -cne 'recovery_handoff_terminal' -or
        [string]$payload.bindingState -cne 'Committed' -or
        @($payload.receipts).Count -ne 1 -or
        [long]$payload.decisionGeneration -le 0 -or
        [long]$payload.pressureInstallGeneration -le 0 -or
        [long]$Operation.operationGeneration -le 0 -or
        [long]$Operation.commandGeneration -ne [long]$Operation.operationGeneration -or
        [long]$payload.recoveryOperationGeneration -ne [long]$Operation.operationGeneration -or
        [string]$payload.recoveryKind -cne 'BridgeReceiveStallRecovery' -or
        -not [bool]$payload.currentAuthorityCleared -or
        [bool]$payload.retainedPredecessorCleared -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete -Boundary $payload.sourceBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete -Boundary $payload.effectiveBoundary) -or
        [string]$payload.sourceBoundary.dataSessionIdentity -cne [string]$payload.effectiveBoundary.dataSessionIdentity -or
        [long]$payload.sourceBoundary.dataSessionGeneration -ne [long]$payload.effectiveBoundary.dataSessionGeneration) {
        return $false
    }
    $key = @('regular_v4_frontier_repair_pressure', 'recovery_handoff_terminal',
        [string]$Operation.authorityId, [string]$Operation.operationGeneration,
        'PressureRecoveryHandoff') -join '|'
    $receipt = $payload.receipts[0]
    if ($null -eq $receipt) { return $false }
    foreach ($name in @('stage', 'commandIdentity', 'commandGeneration', 'commandFingerprint',
            'receiptIdentity', 'receiptGeneration', 'receiptStatus', 'receiptReason',
            'receiptFingerprint', 'receiptValidated')) {
        if ($receipt.PSObject.Properties.Name -notcontains $name) { return $false }
    }
    if ([string]$Operation.operationKey -cne $key -or
        [string]$receipt.stage -cne 'pressure_settlement' -or
        [long]$receipt.commandGeneration -ne [long]$payload.decisionGeneration -or
        [long]$receipt.receiptGeneration -ne [long]$payload.pressureInstallGeneration -or
        [string]$receipt.receiptStatus -notin @('Applied', 'Idempotent') -or
        -not [bool]$receipt.receiptValidated -or -not [bool]$Operation.receiptValidated) {
        return $false
    }
    foreach ($value in @($Operation.authorityId, $Operation.commandIdentity,
            $Operation.commandFingerprint, $payload.recoveryAuthorityId,
            $receipt.commandIdentity, $receipt.commandFingerprint,
            $receipt.receiptIdentity, $receipt.receiptFingerprint, $receipt.receiptReason)) {
        if ([string]::IsNullOrWhiteSpace([string]$value) -or [string]$value -ceq '(none)') {
            return $false
        }
    }
    foreach ($name in @('receiptIdentity', 'receiptGeneration', 'receiptStatus', 'receiptFingerprint')) {
        if ([string]$Operation.$name -cne [string]$receipt.$name) { return $false }
    }
    return $true
}

function Get-FileTransferCoordinatorSnapshotRecoveryHandoffReceiptSignature {
    param([Parameter(Mandatory = $true)]$Operation)

    $payload = $Operation.regularV4FrontierRepairPressure
    [ordered]@{
        authority = $Operation.authorityId
        predecessor = $Operation.predecessorAuthorityId
        generation = $Operation.operationGeneration
        commandIdentity = $Operation.commandIdentity
        commandGeneration = $Operation.commandGeneration
        commandFingerprint = $Operation.commandFingerprint
        decisionGeneration = $payload.decisionGeneration
        installGeneration = $payload.pressureInstallGeneration
        settlementOutcome = $payload.settlementOutcome
        settlementReason = $payload.settlementReason
        currentCleared = $payload.currentAuthorityCleared
        predecessorCleared = $payload.retainedPredecessorCleared
        successor = $payload.successorAuthorityId
        recoveryAuthority = $payload.recoveryAuthorityId
        recoveryGeneration = $payload.recoveryOperationGeneration
        recoveryKind = $payload.recoveryKind
        dataSessionIdentity = $payload.sourceBoundary.dataSessionIdentity
        dataSessionGeneration = $payload.sourceBoundary.dataSessionGeneration
        receipt = $payload.receipts[0]
    } | ConvertTo-Json -Depth 8 -Compress
}

function Test-FileTransferCoordinatorSnapshotFamily5RepairReplayScope {
    param([Parameter(Mandatory = $true)]$Operation)

    if ($null -eq $Operation.regularV4FrontierRepairPressure) {
        return $false
    }
    $payload = $Operation.regularV4FrontierRepairPressure
    $source = $payload.sourceBoundary
    $effective = $payload.effectiveBoundary
    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $source) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $effective) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            -Left $source -Right $effective) -or
        [int]$source.protocolVersion -ne 4 -or
        [long]$source.routePublicationGeneration -le 0) {
        return $false
    }

    switch ([string]$source.route) {
        'regular_nkn_v4_fast' {
            return [string]$payload.targetTransport -ceq 'RegularNkn'
        }
        'file_tuna_v4' {
            return [long]$source.routeCommitGeneration -gt 0 -and
                [string]$payload.targetTransport -ceq 'Tuna'
        }
        default {
            return $false
        }
    }
}

function Get-FileTransferCoordinatorSnapshotFamily5RepairReplayFailure {
    param([Parameter(Mandatory = $true)]$Envelope)

    if ([string]$Envelope.decisionKind -cne 'RepairReplay') {
        return ''
    }

    $operations = @($Envelope.aggregate.operations | Where-Object {
            [string]$_.family -ceq
                'regular_v4_frontier_repair_pressure'
        })
    $beforeBoundary = $Envelope.aggregate.beforeBoundary
    $afterBoundary = $Envelope.aggregate.afterBoundary
    $v4Boundary =
        (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $beforeBoundary) -and
        (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $afterBoundary) -and
        [int]$beforeBoundary.protocolVersion -eq 4 -and
        [int]$afterBoundary.protocolVersion -eq 4 -and
        [string]$beforeBoundary.route -ceq [string]$afterBoundary.route -and
        [string]$afterBoundary.route -in @(
            'regular_nkn_v4_fast', 'file_tuna_v4') -and
        [long]$afterBoundary.routePublicationGeneration -gt 0 -and
        ([string]$afterBoundary.route -cne 'file_tuna_v4' -or
         [long]$afterBoundary.routeCommitGeneration -gt 0)
    if (-not $v4Boundary -and $operations.Count -eq 0) {
        return ''
    }
    $attempts = @($operations | Where-Object {
            [string]$_.operationRole -ceq 'attempted_decision'
        })
    $decisionStatus = [string]$Envelope.decisionStatus
    $stateChanged = [string]$Envelope.aggregate.beforeStateFingerprint -cne
        [string]$Envelope.aggregate.afterStateFingerprint
    $attemptValid = $attempts.Count -eq 1 -and
        [string]$attempts[0].operationKind -ceq 'RepairReplay' -and
        -not [bool]$attempts[0].stateApplied -and
        [bool]$attempts[0].receiptValidated -and
        [string]$attempts[0].receiptStatus -ceq $decisionStatus -and
        [string]$attempts[0].regularV4FrontierRepairPressure.role -ceq
            'attempted_decision' -and
        @($attempts[0].regularV4FrontierRepairPressure.receipts).Count -eq 1 -and
        [string]$attempts[0].regularV4FrontierRepairPressure.receipts[0].stage `
            -ceq 'attempted_decision'

    if ($decisionStatus -in @('Idempotent', 'Rejected')) {
        if ($stateChanged -or
            [long]$Envelope.aggregate.beforeStateGeneration -ne
                [long]$Envelope.aggregate.afterStateGeneration -or
            -not $attemptValid -or
            @($operations | Where-Object { [bool]$_.stateApplied }).Count -ne
                0) {
            return 'snapshot_regular_v4_repair_replay_attempt_invalid'
        }
        return ''
    }

    if ($decisionStatus -cne 'Accepted' -or -not $stateChanged -or
        [long]$Envelope.aggregate.afterStateGeneration -ne
            ([long]$Envelope.aggregate.beforeStateGeneration + 1L) -or
        -not $attemptValid) {
        return 'snapshot_regular_v4_repair_replay_authority_transition_missing'
    }

    $successors = @($operations | Where-Object {
            [string]$_.operationRole -ceq 'repair_authority_current' -and
            [bool]$_.stateApplied -and
            [string]$_.beforeState -ceq 'None' -and
            [string]$_.afterState -ceq 'Authorized'
        })
    if ($successors.Count -ne 1) {
        return 'snapshot_regular_v4_repair_replay_authority_transition_missing'
    }
    $successor = $successors[0]
    if ([string]$successor.operationKind -notin @(
            'V4Repair', 'PeerSilenceReplay',
            'TransportRebindSafetyReplay') -or
        [long]$successor.operationGeneration -le 0 -or
        [string]::IsNullOrWhiteSpace([string]$successor.authorityId) -or
        [string]$successor.authorityId -ceq '(none)' -or
        -not (Test-FileTransferCoordinatorSnapshotFamily5RepairReplayScope `
            -Operation $successor) -or
        [string]$successor.regularV4FrontierRepairPressure.bindingState -cne
            'Authorized' -or
        [long]$successor.regularV4FrontierRepairPressure.pressureInstallGeneration `
            -ne 0 -or
        [bool]$successor.receiptValidated -or
        [string]$successor.receiptIdentity -cne '(none)') {
        return 'snapshot_regular_v4_repair_replay_authority_transition_invalid'
    }

    $retired = @($operations | Where-Object {
            [string]$_.operationRole -ceq 'repair_authority_current' -and
            [bool]$_.stateApplied -and
            [string]$_.beforeState -ceq 'Authorized' -and
            [string]$_.afterState -ceq 'None'
        })
    $predecessorId = [string]$successor.predecessorAuthorityId
    if ($predecessorId -ceq '(none)') {
        if ($retired.Count -ne 0) {
            return 'snapshot_regular_v4_repair_replay_authority_transition_invalid'
        }
    }
    else {
        $matchingRetired = @($retired | Where-Object {
                [string]$_.authorityId -ceq $predecessorId
            })
        if ($retired.Count -ne 1 -or $matchingRetired.Count -ne 1) {
            return 'snapshot_regular_v4_repair_replay_authority_transition_missing'
        }
        $predecessor = $matchingRetired[0]
        if ([string]$predecessor.authorityId -ceq
                [string]$successor.authorityId -or
            [long]$successor.operationGeneration -ne
                ([long]$predecessor.operationGeneration + 1L) -or
            [string]$predecessor.operationKind -cne
                [string]$successor.operationKind -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                -Left $predecessor.regularV4FrontierRepairPressure.sourceBoundary `
                -Right $successor.regularV4FrontierRepairPressure.sourceBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                -Left $predecessor.regularV4FrontierRepairPressure.effectiveBoundary `
                -Right $successor.regularV4FrontierRepairPressure.effectiveBoundary)) {
            return 'snapshot_regular_v4_repair_replay_authority_transition_invalid'
        }
    }

    $allowedOperationCount = 1 + $retired.Count + $attempts.Count
    if ($operations.Count -ne $allowedOperationCount) {
        return 'snapshot_regular_v4_repair_replay_authority_transition_invalid'
    }
    return ''
}

function Get-FileTransferCoordinatorSnapshotFamily5PressureSettlementFailure {
    param([Parameter(Mandatory = $true)]$Envelope)

    $crossSourceFailure = ''

    if ([string]$Envelope.decisionKind -cne
            'CommitRegularV4FrontierRepairPressureSettlement' -or
        [string]$Envelope.decisionStatus -cne 'Accepted') {
        return ''
    }

    $operations = @($Envelope.aggregate.operations | Where-Object {
            [string]$_.family -ceq
                'regular_v4_frontier_repair_pressure'
        })
    $settlementTerminals = @($operations | Where-Object {
            [string]$_.operationRole -ceq 'pressure_settlement_terminal' -and
            [string]$_.operationKind -ceq 'PressureSettlement' -and
            [bool]$_.stateApplied -and
            [bool]$_.receiptValidated -and
            $null -ne $_.regularV4FrontierRepairPressure
        })
    $enteringSettlements = @($settlementTerminals | Where-Object {
            [string]$_.beforeState -ceq 'None' -and
            [string]$_.afterState -ceq 'Applied'
        })
    $retiredSettlements = @($settlementTerminals | Where-Object {
            [string]$_.beforeState -ceq 'Applied' -and
            [string]$_.afterState -ceq 'None'
        })
    if ($enteringSettlements.Count -ne 1 -or
        $retiredSettlements.Count -gt 1 -or
        $settlementTerminals.Count -ne
            ($enteringSettlements.Count + $retiredSettlements.Count)) {
        return ('snapshot_regular_v4_pressure_settlement_transition_invalid|' +
            'pressure_settlement_terminal_partition_invalid')
    }

    $settlement = $enteringSettlements[0]
    $payload = $settlement.regularV4FrontierRepairPressure
    if ([string]$payload.role -cne 'pressure_settlement_terminal' -or
        [string]$settlement.receiptStatus -notin @('Applied', 'Idempotent') -or
        [string]$settlement.receiptIdentity -in @('', '(none)') -or
        [long]$settlement.receiptGeneration -le 0 -or
        [string]$settlement.receiptFingerprint -in @('', '(none)')) {
        return ('snapshot_regular_v4_pressure_settlement_transition_invalid|' +
            'pressure_settlement_entering_terminal_receipt_invalid')
    }
    $currentCleared = [bool]$payload.currentAuthorityCleared
    $retainedCleared = [bool]$payload.retainedPredecessorCleared
    if ($currentCleared -eq $retainedCleared) {
        return 'snapshot_regular_v4_pressure_settlement_transition_invalid'
    }

    $candidateAttempts = @($operations | Where-Object {
            [string]$_.operationRole -ceq 'attempted_decision' -and
            [string]$_.operationKind -ceq
                'CommitRegularV4FrontierRepairPressureSettlement' -and
            -not [bool]$_.stateApplied -and
            [bool]$_.receiptValidated -and
            [string]$_.receiptStatus -ceq 'Accepted' -and
            $null -ne $_.regularV4FrontierRepairPressure -and
            [string]$_.regularV4FrontierRepairPressure.role -ceq
                'attempted_decision'
        })
    if ($retainedCleared) {
        $successorAuthorityId = [string]$payload.successorAuthorityId
        $pressureInstallGeneration =
            [long]$payload.pressureInstallGeneration
        $attempts = @($candidateAttempts | Where-Object {
                [string]$_.authorityId -ceq $successorAuthorityId -and
                [string]$_.predecessorAuthorityId -ceq
                    [string]$settlement.authorityId -and
                [long]$_.operationGeneration -eq
                    [long]$settlement.receiptGeneration -and
                [long]$_.operationGeneration -eq
                    $pressureInstallGeneration -and
                [long]$_.commandGeneration -eq
                    $pressureInstallGeneration -and
                [long]$_.receiptGeneration -eq
                    $pressureInstallGeneration -and
                [long]($_.regularV4FrontierRepairPressure.
                    decisionGeneration) -eq
                    $pressureInstallGeneration -and
                [long]($_.regularV4FrontierRepairPressure.
                    pressureInstallGeneration) -eq
                    $pressureInstallGeneration -and
                [string]$_.commandFingerprint -ceq
                    [string]$settlement.receiptFingerprint -and
                (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                    -Left $_.regularV4FrontierRepairPressure.sourceBoundary `
                    -Right $payload.sourceBoundary) -and
                (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                    -Left $_.regularV4FrontierRepairPressure.effectiveBoundary `
                    -Right $payload.effectiveBoundary)
            })
        if ($successorAuthorityId -in @('', '(none)') -or
            $pressureInstallGeneration -le 0 -or
            [long]$settlement.operationGeneration -ge
                $pressureInstallGeneration) {
            return ('snapshot_regular_v4_pressure_settlement_transition_invalid|' +
                'pressure_settlement_attempt_binding_invalid')
        }
    }
    else {
        $attempts = @($candidateAttempts | Where-Object {
                [string]$_.authorityId -ceq
                    [string]$settlement.authorityId -and
                [long]$_.operationGeneration -eq
                    [long]$settlement.operationGeneration
            })
    }
    if ($attempts.Count -ne 1) {
        return ('snapshot_regular_v4_pressure_settlement_transition_invalid|' +
            'pressure_settlement_attempt_binding_invalid')
    }

    $terminalMirrors = @($operations | Where-Object {
            [string]$_.operationRole -ceq 'repair_authority_terminal' -and
            [string]$_.operationKind -ceq 'PressureSettlement' -and
            [bool]$_.stateApplied -and
            [bool]$_.receiptValidated -and
            $null -ne $_.regularV4FrontierRepairPressure
        })
    $enteringMirrors = @($terminalMirrors | Where-Object {
            [string]$_.authorityId -ceq [string]$settlement.authorityId -and
            [long]$_.operationGeneration -eq
                [long]$settlement.operationGeneration -and
            [string]$_.beforeState -ceq 'None' -and
            [string]$_.afterState -ceq 'Applied'
        })
    if ($enteringMirrors.Count -ne 1 -or
        [string]$enteringMirrors[0].regularV4FrontierRepairPressure.role `
            -cne 'repair_authority_terminal' -or
        [string]$enteringMirrors[0].receiptIdentity -cne
            [string]$settlement.receiptIdentity -or
        [long]$enteringMirrors[0].receiptGeneration -ne
            [long]$settlement.receiptGeneration -or
        [string]$enteringMirrors[0].receiptStatus -cne
            [string]$settlement.receiptStatus -or
        [string]$enteringMirrors[0].receiptFingerprint -cne
            [string]$settlement.receiptFingerprint -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
            -Left $enteringMirrors[0].regularV4FrontierRepairPressure.sourceBoundary `
            -Right $payload.sourceBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
            -Left $enteringMirrors[0].regularV4FrontierRepairPressure.effectiveBoundary `
            -Right $payload.effectiveBoundary)) {
        return ('snapshot_regular_v4_pressure_settlement_transition_invalid|' +
            'pressure_settlement_entering_terminal_mirror_invalid')
    }

    if ($retiredSettlements.Count -eq 1) {
        $retiredSettlement = $retiredSettlements[0]
        if ([string]$retiredSettlement.authorityId -ceq
                [string]$settlement.authorityId -or
            [long]$retiredSettlement.operationGeneration -ge
                [long]$settlement.operationGeneration -or
            [string]$retiredSettlement.regularV4FrontierRepairPressure.role `
                -cne 'pressure_settlement_terminal' -or
            [string]$retiredSettlement.receiptIdentity -ceq
                [string]$settlement.receiptIdentity -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                -Left $retiredSettlement.regularV4FrontierRepairPressure.effectiveBoundary `
                -Right $payload.effectiveBoundary)) {
            return ('snapshot_regular_v4_pressure_settlement_transition_invalid|' +
                'pressure_settlement_terminal_rollover_invalid')
        }
        if (-not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                -Left $retiredSettlement.regularV4FrontierRepairPressure.sourceBoundary `
                -Right $payload.sourceBoundary)) {
            $crossSourceFailure = ('snapshot_regular_v4_pressure_settlement_transition_invalid|' +
                'terminal_settlement_rollover_compared_historical_source_with_current_route_source')
            if (-not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                    -Left $payload.effectiveBoundary -Right $Envelope.aggregate.beforeBoundary) -or
                -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                    -Left $payload.effectiveBoundary -Right $Envelope.aggregate.afterBoundary)) {
                return 'snapshot_regular_v4_pressure_settlement_transition_invalid|pressure_settlement_terminal_rollover_invalid'
            }
        }
        $retiredMirrors = @($terminalMirrors | Where-Object {
                [string]$_.authorityId -ceq
                    [string]$retiredSettlement.authorityId -and
                [long]$_.operationGeneration -eq
                    [long]$retiredSettlement.operationGeneration -and
                [string]$_.beforeState -ceq 'Applied' -and
                [string]$_.afterState -ceq 'None'
            })
        if ($retiredMirrors.Count -ne 1 -or
            [string]$retiredMirrors[0].regularV4FrontierRepairPressure.role `
                -cne 'repair_authority_terminal' -or
            [string]$retiredMirrors[0].receiptIdentity -cne
                [string]$retiredSettlement.receiptIdentity -or
            [long]$retiredMirrors[0].receiptGeneration -ne
                [long]$retiredSettlement.receiptGeneration -or
            [string]$retiredMirrors[0].receiptStatus -cne
                [string]$retiredSettlement.receiptStatus -or
            [string]$retiredMirrors[0].receiptFingerprint -cne
                [string]$retiredSettlement.receiptFingerprint -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                -Left $retiredMirrors[0].regularV4FrontierRepairPressure.sourceBoundary `
                -Right $retiredSettlement.regularV4FrontierRepairPressure.sourceBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                -Left $retiredMirrors[0].regularV4FrontierRepairPressure.effectiveBoundary `
                -Right $retiredSettlement.regularV4FrontierRepairPressure.effectiveBoundary)) {
            return ('snapshot_regular_v4_pressure_settlement_transition_invalid|' +
                'pressure_settlement_retired_terminal_mirror_invalid')
        }
    }
    if ($terminalMirrors.Count -ne (1 + $retiredSettlements.Count)) {
        return ('snapshot_regular_v4_pressure_settlement_transition_invalid|' +
            'pressure_settlement_terminal_mirror_count_invalid')
    }

    $currentRetirements = @($operations | Where-Object {
            [string]$_.operationRole -ceq 'repair_authority_current' -and
            (@('V4Repair', 'PeerSilenceReplay',
                'TransportRebindSafetyReplay') -ccontains
                    [string]$_.operationKind) -and
            [bool]$_.stateApplied -and
            [string]$_.beforeState -ceq 'Authorized' -and
            [string]$_.afterState -ceq 'None'
        })
    if ($retainedCleared) {
        $retainedPredecessorRetirements = @($operations | Where-Object {
                [string]$_.operationRole -ceq
                    'pressure_binding_retained_predecessor' -and
                [string]$_.authorityId -ceq
                    [string]$settlement.authorityId -and
                [long]$_.operationGeneration -eq
                    [long]$settlement.operationGeneration -and
                [bool]$_.stateApplied -and
                [string]$_.beforeState -ceq 'RetainedPredecessor' -and
                [string]$_.afterState -ceq 'None' -and
                $null -ne $_.regularV4FrontierRepairPressure -and
                (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                    -Left $_.regularV4FrontierRepairPressure.sourceBoundary `
                    -Right $payload.sourceBoundary) -and
                (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                    -Left $_.regularV4FrontierRepairPressure.effectiveBoundary `
                    -Right $payload.effectiveBoundary)
            })
        $successorMutations = @($operations | Where-Object {
                [string]$_.authorityId -ceq
                    [string]$payload.successorAuthorityId -and
                [bool]$_.stateApplied
            })
        if ($currentRetirements.Count -ne 0 -or
            $retainedPredecessorRetirements.Count -ne 1 -or
            $successorMutations.Count -ne 0) {
            return 'snapshot_regular_v4_pressure_settlement_transition_invalid'
        }
        return $crossSourceFailure
    }

    $matchingRetirements = @($currentRetirements | Where-Object {
            [string]$_.authorityId -ceq [string]$settlement.authorityId -and
            [long]$_.operationGeneration -eq
                [long]$settlement.operationGeneration
        })
    if ($currentRetirements.Count -ne 1 -or
        $matchingRetirements.Count -ne 1) {
        return ('snapshot_regular_v4_repair_replay_authority_transition_missing|' +
            'accepted_pressure_settlement_declared_current_authority_clear_without_repair_authority_retirement')
    }
    $retirement = $matchingRetirements[0]
    if ($null -eq $retirement.regularV4FrontierRepairPressure -or
        [string]$retirement.regularV4FrontierRepairPressure.role -cne
            'repair_authority_current') {
        return 'snapshot_regular_v4_pressure_settlement_transition_invalid'
    }

    $successors = @($operations | Where-Object {
            [string]$_.operationRole -ceq 'repair_authority_current' -and
            [bool]$_.stateApplied -and
            [string]$_.beforeState -ceq 'None' -and
            [string]$_.afterState -ceq 'Authorized'
        })
    if ($successors.Count -ne 0) {
        return 'snapshot_regular_v4_pressure_settlement_transition_invalid'
    }
    return $crossSourceFailure
}

function Test-FileTransferCoordinatorSnapshotRepairReplayResponderScopeInvalidation {
    param([Parameter(Mandatory = $true)]$Envelope)

    if ([string]$Envelope.decisionKind -cne 'RepairReplay' -or
        [string]$Envelope.decisionStatus -cne 'Accepted' -or
        [string]$Envelope.decisionReason -cne 'accepted' -or
        $null -eq $Envelope.aggregate) {
        return $false
    }

    $aggregate = $Envelope.aggregate
    if ([long]$aggregate.afterStateGeneration -ne
            ([long]$aggregate.beforeStateGeneration + 1L) -or
        [string]::IsNullOrWhiteSpace(
            [string]$aggregate.beforeStateFingerprint) -or
        [string]::IsNullOrWhiteSpace(
            [string]$aggregate.afterStateFingerprint) -or
        [string]$aggregate.beforeStateFingerprint -ceq
            [string]$aggregate.afterStateFingerprint -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            -Left $aggregate.beforeBoundary `
            -Right $aggregate.afterBoundary) -or
        [int]$aggregate.afterBoundary.protocolVersion -ne 4 -or
        [string]$aggregate.afterBoundary.route -notin @(
            'regular_nkn_v4_fast', 'file_tuna_v4') -or
        [long]$aggregate.afterBoundary.routePublicationGeneration -le 0 -or
        ([string]$aggregate.afterBoundary.route -ceq 'file_tuna_v4' -and
         [long]$aggregate.afterBoundary.routeCommitGeneration -le 0) -or
        (@($aggregate.changedFamilies | Sort-Object) -join '|') -cne
            'regular_v4_frontier_repair_pressure|responder_probe_handoff' -or
        (Get-FileTransferCoordinatorSnapshotFamily5RepairReplayFailure `
            -Envelope $Envelope) -cne '') {
        return $false
    }

    $operations = @($aggregate.operations)
    if (@($operations | Where-Object {
                [string]$_.family -notin @(
                    'regular_v4_frontier_repair_pressure',
                    'responder_probe_handoff')
            }).Count -ne 0) {
        return $false
    }
    $family5Operations = @($operations | Where-Object {
            [string]$_.family -ceq
                'regular_v4_frontier_repair_pressure'
        })
    $family5Attempts = @($family5Operations | Where-Object {
            [string]$_.operationRole -ceq 'attempted_decision' -and
            [string]$_.operationKind -ceq 'RepairReplay' -and
            -not [bool]$_.stateApplied
        })
    $family5Entrants = @($family5Operations | Where-Object {
            [string]$_.operationRole -ceq 'repair_authority_current' -and
            [string]$_.beforeState -ceq 'None' -and
            [string]$_.afterState -ceq 'Authorized' -and
            [bool]$_.stateApplied
        })
    $family5Retirements = @($family5Operations | Where-Object {
            [string]$_.operationRole -ceq 'repair_authority_current' -and
            [string]$_.beforeState -ceq 'Authorized' -and
            [string]$_.afterState -ceq 'None' -and
            [bool]$_.stateApplied
        })
    if ($family5Attempts.Count -ne 1 -or
        $family5Entrants.Count -ne 1 -or
        $family5Retirements.Count -gt 1 -or
        $family5Operations.Count -ne
            (2 + $family5Retirements.Count) -or
        -not (Test-FileTransferCoordinatorSnapshotFamily5RepairReplayScope `
            -Operation $family5Entrants[0])) {
        return $false
    }
    foreach ($family5Subject in @(
            $family5Entrants[0], $family5Attempts[0])) {
        $family5Payload =
            $family5Subject.regularV4FrontierRepairPressure
        if ($null -eq $family5Payload -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                -Left $family5Payload.sourceBoundary `
                -Right $aggregate.afterBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                -Left $family5Payload.effectiveBoundary `
                -Right $aggregate.afterBoundary)) {
            return $false
        }
    }
    $responderOperations = @($operations | Where-Object {
            [string]$_.family -ceq 'responder_probe_handoff'
        })
    $currentOperations = @($responderOperations | Where-Object {
            [string]$_.operationRole -ceq 'handoff_current' -and
            [string]$_.operationKind -ceq
                'ResponderPeerProbeRouteTransitionHandoff' -and
            [string]$_.beforeState -ceq 'Accepted' -and
            [string]$_.afterState -ceq 'None' -and
            [bool]$_.stateApplied
        })
    $terminalOperations = @($responderOperations | Where-Object {
            [string]$_.operationRole -ceq 'handoff_terminal' -and
            [string]$_.operationKind -ceq
                'ResponderPeerProbeRouteTransitionHandoff' -and
            [string]$_.beforeState -ceq 'None' -and
            [string]$_.afterState -ceq 'Invalidated' -and
            [bool]$_.stateApplied
        })
    if ($responderOperations.Count -ne 2 -or
        $currentOperations.Count -ne 1 -or
        $terminalOperations.Count -ne 1) {
        return $false
    }

    $current = $currentOperations[0]
    $terminal = $terminalOperations[0]
    if ([string]$current.authorityId -in @('', '(none)') -or
        [string]$terminal.authorityId -cne [string]$current.authorityId -or
        [long]$current.operationGeneration -le 0 -or
        [long]$terminal.operationGeneration -ne
            [long]$current.operationGeneration -or
        [string]$current.predecessorAuthorityId -cne '(none)' -or
        [string]$terminal.predecessorAuthorityId -cne '(none)' -or
        [string]$current.operationKey -cne
            "responder_probe_handoff|handoff_current|$($current.authorityId)|$($current.operationGeneration)|ResponderPeerProbeRouteTransitionHandoff" -or
        [string]$terminal.operationKey -cne
            "responder_probe_handoff|handoff_terminal|$($terminal.authorityId)|$($terminal.operationGeneration)|ResponderPeerProbeRouteTransitionHandoff" -or
        $null -eq $current.responderProbeHandoff -or
        $null -eq $terminal.responderProbeHandoff) {
        return $false
    }

    $currentPayload = $current.responderProbeHandoff
    $terminalPayload = $terminal.responderProbeHandoff
    if ([string]$currentPayload.role -cne 'handoff_current' -or
        [string]$terminalPayload.role -cne 'handoff_terminal' -or
        [string]$currentPayload.decisionStatus -cne 'Accepted' -or
        [string]$currentPayload.decisionReason -cne
            'runtime_unlock_responder_peer_probe_handoff_bound' -or
        [string]$terminalPayload.decisionStatus -cne 'Invalidated' -or
        [string]$terminalPayload.decisionReason -cne
            'runtime_unlock_responder_peer_probe_handoff_scope_replaced' -or
        [string]$currentPayload.transitionMode -cne
            'post_fallback_v6_paired' -or
        [string]$terminalPayload.transitionMode -cne
            [string]$currentPayload.transitionMode -or
        [string]$currentPayload.handoffAuthorityId -cne
            [string]$current.authorityId -or
        [string]$terminalPayload.handoffAuthorityId -cne
            [string]$current.authorityId -or
        [long]$currentPayload.handoffGeneration -ne
            [long]$current.operationGeneration -or
        [long]$terminalPayload.handoffGeneration -ne
            [long]$current.operationGeneration) {
        return $false
    }

    foreach ($field in @(
            'transactionGeneration', 'offerGeneration',
            'pendingAnswerAckGeneration', 'payerDecisionId',
            'launchGeneration', 'tunaPathLeaseGeneration',
            'tunaPathLeaseListenerRunId', 'ackMessageId',
            'ackSourceFingerprint', 'peerProbeFrameFingerprint', 'probeId',
            'probeState', 'probeTransport', 'probeAckedUtcMs',
            'admissionGeneration', 'pendingPreCommitGeneration', 'ownerKey',
            'pairKey', 'pairedReleaseAuthorityId',
            'pairedReleaseGeneration', 'pairingGeneration',
            'localProofIdentity', 'localProofFingerprint',
            'peerProofIdentity', 'peerProofFingerprint', 'lanes',
            'linkedTransitionAuthorityId', 'linkedTransitionGeneration')) {
        if ([string]$currentPayload.$field -cne
            [string]$terminalPayload.$field) {
            return $false
        }
    }
    foreach ($field in @(
            'transactionGeneration', 'offerGeneration',
            'pendingAnswerAckGeneration', 'payerDecisionId',
            'launchGeneration', 'tunaPathLeaseGeneration',
            'probeAckedUtcMs', 'admissionGeneration',
            'pendingPreCommitGeneration')) {
        if ([long]$currentPayload.$field -le 0) {
            return $false
        }
    }
    foreach ($field in @(
            'tunaPathLeaseListenerRunId', 'ackMessageId',
            'ackSourceFingerprint', 'peerProbeFrameFingerprint', 'probeId',
            'ownerKey')) {
        if ([string]$currentPayload.$field -in @('', '(none)')) {
            return $false
        }
    }
    if ([string]$currentPayload.lanes -cne 'file' -or
        [string]$currentPayload.probeState -cne 'Acked' -or
        [string]$currentPayload.probeTransport -cne 'Tuna') {
        return $false
    }
    $linkedTransitionAuthorityId =
        [string]$currentPayload.linkedTransitionAuthorityId
    $linkedTransitionGeneration =
        [long]$currentPayload.linkedTransitionGeneration
    if (($linkedTransitionAuthorityId -ceq '(none)' -and
            $linkedTransitionGeneration -ne 0) -or
        ($linkedTransitionAuthorityId -cne '(none)' -and
            ([string]::IsNullOrWhiteSpace($linkedTransitionAuthorityId) -or
             $linkedTransitionGeneration -le 0))) {
        return $false
    }
    foreach ($field in @('commandIdentity', 'commandGeneration',
            'commandFingerprint', 'receiptIdentity', 'receiptGeneration')) {
        if ([string]$current.$field -cne [string]$terminal.$field) {
            return $false
        }
    }
    foreach ($boundaryName in @(
            'sourceBoundary', 'effectiveBoundary', 'targetBoundary')) {
        if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                -Left $currentPayload.$boundaryName `
                -Right $terminalPayload.$boundaryName)) {
            return $false
        }
    }
    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            -Left $currentPayload.effectiveBoundary `
            -Right $aggregate.afterBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            -Left $currentPayload.targetBoundary `
            -Right $aggregate.afterBoundary) -or
        [string]$currentPayload.sourceBoundary.dataSessionIdentity -cne
            [string]$aggregate.afterBoundary.dataSessionIdentity -or
        [long]$currentPayload.sourceBoundary.dataSessionGeneration -ne
            [long]$aggregate.afterBoundary.dataSessionGeneration) {
        return $false
    }

    if ([string]$currentPayload.sourceBoundary.route -cne
            'post_tuna_fallback_v6' -or
        [int]$currentPayload.sourceBoundary.protocolVersion -ne 6 -or
        [string]$currentPayload.pairedReleaseAuthorityId -in @('', '(none)') -or
        [long]$currentPayload.pairedReleaseGeneration -le 0 -or
        [long]$currentPayload.pairingGeneration -le 0 -or
        [string]$currentPayload.pairKey -in @('', '(none)') -or
        [string]$currentPayload.localProofIdentity -in @('', '(none)') -or
        [string]$currentPayload.localProofFingerprint -in @('', '(none)') -or
        [string]$currentPayload.peerProofIdentity -in @('', '(none)') -or
        [string]$currentPayload.peerProofFingerprint -in @('', '(none)')) {
        return $false
    }

    $currentReceipts = @($currentPayload.receipts)
    $terminalReceipts = @($terminalPayload.receipts)
    if ($currentReceipts.Count -ne 1 -or $terminalReceipts.Count -ne 1) {
        return $false
    }
    $currentReceipt = $currentReceipts[0]
    $terminalReceipt = $terminalReceipts[0]
    if ([string]$currentReceipt.stage -cne 'handoff_bind' -or
        [string]$currentReceipt.receiptStatus -cne 'Accepted' -or
        [string]$currentReceipt.receiptReason -cne
            [string]$currentPayload.decisionReason -or
        -not [bool]$currentReceipt.receiptValidated -or
        [string]$terminalReceipt.stage -cne 'handoff_invalidation' -or
        [string]$terminalReceipt.receiptStatus -cne 'Invalidated' -or
        [string]$terminalReceipt.receiptReason -cne
            [string]$terminalPayload.decisionReason -or
        -not [bool]$terminalReceipt.receiptValidated) {
        return $false
    }
    foreach ($pair in @(
            @($current, $currentReceipt), @($terminal, $terminalReceipt))) {
        $operation = $pair[0]
        $receipt = $pair[1]
        foreach ($field in @(
                'commandIdentity', 'commandGeneration', 'commandFingerprint',
                'receiptIdentity', 'receiptGeneration', 'receiptStatus',
                'receiptFingerprint', 'receiptValidated')) {
            if ([string]$operation.$field -cne [string]$receipt.$field) {
                return $false
            }
        }
        foreach ($field in @(
                'commandIdentity', 'commandFingerprint', 'receiptIdentity',
                'receiptFingerprint')) {
            if ([string]$receipt.$field -in @('', '(none)')) {
                return $false
            }
        }
        if ([long]$receipt.commandGeneration -ne
                [long]$current.operationGeneration -or
            [long]$receipt.receiptGeneration -ne
                [long]$current.operationGeneration) {
            return $false
        }
    }
    return $true
}

function Get-FileTransferCoordinatorSnapshotFamily6CatalogVariant {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        $Operation = $null
    )

    if ([string]$Envelope.decisionKind -ceq 'RepairReplay' -and
        $null -ne $Operation -and
        [string]$Operation.family -ceq 'responder_probe_handoff') {
        return 'family6_catalog_omitted_atomic_repair_replay_scope_invalidation'
    }
    return Get-FileTransferCoordinatorSnapshotCheckpointCatalogVariant `
        -Envelope $Envelope -Operation $Operation
}

function Test-FileTransferCoordinatorSnapshotFamily6OperationCatalog {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        [Parameter(Mandatory = $true)]$Operation
    )

    if ([string]$Operation.family -notin
        $script:FileTransferCoordinatorSnapshotFamily6) {
        return $true
    }
    $expectedOperationKey = @(
        [string]$Operation.family,
        [string]$Operation.operationRole,
        [string]$Operation.authorityId,
        ([long]$Operation.operationGeneration).ToString(
            [System.Globalization.CultureInfo]::InvariantCulture),
        [string]$Operation.operationKind) -join '|'
    $knownAtomicDecision =
        [string]$Envelope.decisionKind -in
            $script:FileTransferCoordinatorSnapshotFamily6DecisionKinds -or
        (Test-FileTransferCoordinatorSnapshotRepairReplayResponderScopeInvalidation `
            -Envelope $Envelope) -or
        (Test-FileTransferCoordinatorSnapshotCheckpointResponderCarry $Envelope $Operation) -or
        [string]$Envelope.decisionKind -in
            $script:FileTransferCoordinatorSnapshotFamily1DecisionKinds -or
        [string]$Envelope.decisionKind -in
            $script:FileTransferCoordinatorSnapshotFamily2DecisionKinds -or
        [string]$Envelope.decisionKind -in
            $script:FileTransferCoordinatorSnapshotFamily4DecisionKinds -or
        [string]$Envelope.decisionKind -in @(
            'StartTransfer', 'RoutePublication', 'RouteCommitPublication',
            'Terminalized', 'CoordinatorDecision', 'CoordinatorAction',
            'LegacyFamilyCoordinatorDecision',
            'LegacyFamilyCoordinatorActionDecision')
    if ([string]$Operation.operationKey -cne $expectedOperationKey -or
        -not $knownAtomicDecision -or
        [string]$Operation.operationRole -notin
            $script:FileTransferCoordinatorSnapshotFamily6OperationRoles -or
        $null -eq $Operation.responderProbeHandoff) {
        return $false
    }

    $payload = $Operation.responderProbeHandoff
    if ([string]$payload.transitionMode -notin @(
            'initial_regular_v4', 'post_fallback_v6_paired') -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $payload.sourceBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $payload.effectiveBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $payload.targetBoundary)) {
        return $false
    }
    foreach ($name in @(
            'role', 'ackMessageId', 'ackSourceFingerprint',
            'peerProbeFrameFingerprint', 'probeId', 'probeState',
            'probeTransport', 'ownerKey', 'pairKey',
            'pairedReleaseAuthorityId', 'handoffAuthorityId',
            'linkedTransitionAuthorityId', 'decisionStatus',
            'decisionReason')) {
        if ($payload.PSObject.Properties.Name -notcontains $name -or
            [string]::IsNullOrWhiteSpace([string]$payload.$name)) {
            return $false
        }
    }
    if ($payload.PSObject.Properties.Name -notcontains 'receipts') {
        return $false
    }
    if ([string]$payload.transitionMode -ceq 'initial_regular_v4') {
        if ([long]$payload.sourceBoundary.transportEpoch -ne 0 -or
            [int]$payload.sourceBoundary.bridgeRecoveryGeneration -ne 0 -or
            [int]$payload.sourceBoundary.checkpointGeneration -ne 0 -or
            [string]$payload.pairedReleaseAuthorityId -cne '(none)') {
            return $false
        }
    }
    elseif ([string]$Operation.receiptStatus -notin @(
            'Rejected', 'Invalidated') -and
        ([string]$payload.pairedReleaseAuthorityId -ceq '(none)' -or
         [int]$payload.pairedReleaseGeneration -le 0 -or
         [long]$payload.pairingGeneration -le 0 -or
         [string]$payload.pairKey -ceq '(none)')) {
        return $false
    }
    foreach ($receipt in @($payload.receipts)) {
        $stage = [string]$receipt.stage
        if (-not $script:FileTransferCoordinatorSnapshotFamily6ReceiptStages.Contains($stage) -or
            [string]$receipt.receiptStatus -notin
                @($script:FileTransferCoordinatorSnapshotFamily6ReceiptStages[$stage]) -or
            -not [bool]$receipt.receiptValidated) {
            return $false
        }
    }
    return $true
}

function ConvertFrom-FileTransferCoordinatorSnapshotPayload {
    param([Parameter(Mandatory = $true)][string]$Payload)

    $base64 = $Payload.Replace('-', '+').Replace('_', '/')
    switch ($base64.Length % 4) {
        2 { $base64 += '==' }
        3 { $base64 += '=' }
        1 { throw 'snapshot_payload_invalid_base64url_length' }
    }
    $bytes = [Convert]::FromBase64String($base64)
    $json = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    [pscustomobject]@{
        Bytes = $bytes
        Json = $json
        Value = ($json | ConvertFrom-Json)
    }
}

function Test-FileTransferCoordinatorReceiveRecoveryRecheckCancellation {
    param(
        [Parameter(Mandatory = $true)]$Arm,
        [Parameter(Mandatory = $true)]$Closure,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$ParentHistory,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Effects
    )

    if ([string]$Closure.Boundary.decisionStatus -cne 'Accepted' -or
        -not [bool]$Closure.Operation.stateApplied -or
        [string]$Closure.Boundary.coordinatorInstanceId -cne [string]$Arm.Boundary.coordinatorInstanceId -or
        [string]$Closure.Boundary.writer -cne [string]$Arm.Boundary.writer -or
        [string]$Closure.Operation.predecessorAuthorityId -cne [string]$Arm.Operation.predecessorAuthorityId -or
        [string]$Closure.Operation.receiveRecovery.recoveryKind -cne [string]$Arm.Operation.receiveRecovery.recoveryKind -or
        [long]$Closure.Operation.receiveRecovery.bridgeDecisionGeneration -ne [long]$Arm.Operation.receiveRecovery.bridgeDecisionGeneration -or
        [long]$Closure.Operation.receiveRecovery.recoveryLivenessLeaseGeneration -ne [long]$Arm.Operation.receiveRecovery.recoveryLivenessLeaseGeneration -or
        [long]$Closure.Operation.receiveRecovery.closureGeneration -ne [long]$Arm.Operation.receiveRecovery.closureGeneration) {
        return $false
    }

    # The child key uses PublicationGeneration. Resolve the independent recovery
    # generation from its exact parent state, never from a child/lease counter.
    $parents = @($ParentHistory | Where-Object {
            [bool]$_.Operation.stateApplied -and
            [long]$_.Boundary.afterStateGeneration -le
                [long]$Closure.Boundary.afterStateGeneration
        } | Sort-Object { [long]$_.Boundary.afterStateGeneration }, {
            [string]$_.Boundary.boundaryId
        })
    $generations = @($parents | ForEach-Object {
            [long]$_.Operation.operationGeneration
        } | Sort-Object -Unique)
    if ($generations.Count -ne 1 -or $generations[0] -le 0) {
        return $false
    }

    $states = @{}
    $parentAtArm = @()
    foreach ($entry in $parents) {
        $operation = $entry.Operation
        $typed = $operation.receiveRecovery
        if ([string]$entry.Boundary.coordinatorInstanceId -cne
                [string]$Arm.Boundary.coordinatorInstanceId -or
            [string]$entry.Boundary.writer -cne [string]$Arm.Boundary.writer -or
            [string]$operation.authorityId -cne [string]$Arm.Operation.authorityId -or
            [string]$operation.predecessorAuthorityId -cne
                [string]$Arm.Operation.predecessorAuthorityId -or
            [string]$operation.operationKind -cne
                [string]$Arm.Operation.receiveRecovery.recoveryKind -or
            [string]$typed.recoveryKind -cne
                [string]$Arm.Operation.receiveRecovery.recoveryKind -or
            [long]$typed.bridgeDecisionGeneration -ne
                [long]$Arm.Operation.receiveRecovery.bridgeDecisionGeneration -or
            [long]$typed.recoveryLivenessLeaseGeneration -ne
                [long]$Arm.Operation.receiveRecovery.recoveryLivenessLeaseGeneration -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                -Left $typed.sourceBoundary `
                -Right $Arm.Operation.receiveRecovery.sourceBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                -Left $typed.effectiveBoundary `
                -Right $Arm.Operation.receiveRecovery.effectiveBoundary)) {
            return $false
        }

        $key = [string]$operation.operationKey
        if ($states.ContainsKey($key)) {
            # A retired key cannot be revived, even if a later tuple is equal.
            if ([string]$states[$key].afterState -ceq 'None' -or
                [string]$states[$key].afterState -cne [string]$operation.beforeState) {
                return $false
            }
        }
        elseif ([string]$operation.beforeState -cne 'None') {
            return $false
        }
        $states[$key] = $operation
        if ([long]$entry.Boundary.afterStateGeneration -le
                [long]$Arm.Boundary.afterStateGeneration) {
            $parentAtArm = @($states.Values | Where-Object {
                    [string]$_.afterState -cne 'None'
                })
        }
    }
    if ($parentAtArm.Count -ne 1 -or
        [long]$parentAtArm[0].receiveRecovery.closureGeneration -ne
            [long]$Arm.Operation.receiveRecovery.closureGeneration -or
        [string]$parentAtArm[0].receiveRecovery.proofIdentity -notin @('', '(none)')) {
        return $false
    }

    $cancellations = @($parents | Where-Object {
            [string]$_.Boundary.boundaryId -ceq [string]$Closure.Boundary.boundaryId -and
            [string]$_.Operation.afterState -cne 'None' -and
            [long]$_.Operation.receiveRecovery.closureGeneration -eq
                [long]$Arm.Operation.receiveRecovery.closureGeneration -and
            (([string]$_.Operation.receiveRecovery.proofIdentity -notin @('', '(none)')) -or
             ([string]$_.Operation.receiveRecovery.executionState -ceq 'Invalidated'))
        })
    if ($cancellations.Count -ne 1) { return $false }
    $cancellation = $cancellations[0]
    if ([string]$Closure.Boundary.decisionKind -cne 'ObserveReceiveRecoveryProof') {
        return [string]$cancellation.Operation.receiveRecovery.executionState -ceq 'Invalidated'
    }
    if (-not [bool]$cancellation.Operation.receiptValidated -or
        [string]$cancellation.Operation.receiveRecovery.proofIdentity -in @('', '(none)') -or
        [long]$cancellation.Operation.receiveRecovery.proofObservedUtcMs -le 0) {
        return $false
    }
    $proofEffects = @($Effects | Where-Object {
            [string]$_.EffectKind -ceq 'receive_proof_observed' -and
            [string]$_.ReceiptIdentity -ceq [string]$cancellation.Operation.receiptIdentity -and
            [long]$_.ReceiptGeneration -eq [long]$cancellation.Operation.receiptGeneration -and
            [string]$_.ReceiptStatus -ceq [string]$cancellation.Operation.receiptStatus -and
            [string]$_.ReceiptFingerprint -ceq [string]$cancellation.Operation.receiptFingerprint -and
            (Test-FileTransferCoordinatorSnapshotExactEffectBinding `
                -Boundary $cancellation.Boundary -Operation $cancellation.Operation `
                -Effect $_)
        })
    return $proofEffects.Count -eq 1
}

function Get-FileTransferCoordinatorRouteTransitionLifecycleKey {
    param(
        [Parameter(Mandatory = $true)]$Boundary,
        [Parameter(Mandatory = $true)]$Operation
    )

    $transition = $Operation.routeTransition
    if ($null -eq $transition) {
        return "$($Boundary.coordinatorInstanceId)|$($Operation.operationKey)"
    }
    $source = ConvertTo-Json -InputObject $transition.sourceBoundary `
        -Depth 16 -Compress
    $targetTransportEpochKey = if ([string]$transition.transitionKind -ceq
            'PostTunaFallbackV6ToTunaV4') {
        0L
    }
    else {
        [long]$transition.targetBoundary.transportEpoch
    }
    return @(
        [string]$Boundary.coordinatorInstanceId,
        [string]$Boundary.writer,
        [string]$Operation.authorityId,
        [long]$Operation.operationGeneration,
        [string]$transition.transitionKind,
        [string]$transition.sourceTransport,
        [string]$transition.proofBoundaryKind,
        [string]$transition.proofBoundaryFingerprint,
        [string]$transition.durableCheckpointFingerprint,
        [string]$transition.targetRoute,
        [int]$transition.targetProtocolVersion,
        [string]$transition.targetTransport,
        [string]$transition.handoffKind,
        [string]$transition.targetBoundary.dataSessionIdentity,
        [long]$transition.targetBoundary.dataSessionGeneration,
        $targetTransportEpochKey,
        $source) -join '|'
}

function Get-FileTransferCoordinatorResponderProbeHandoffLifecycleKey {
    param(
        [Parameter(Mandatory = $true)]$Boundary,
        [Parameter(Mandatory = $true)]$Operation
    )

    $payload = $Operation.responderProbeHandoff
    if ($null -eq $payload) {
        return "$($Boundary.coordinatorInstanceId)|$($Operation.operationKey)"
    }
    $source = ConvertTo-Json -InputObject $payload.sourceBoundary `
        -Depth 16 -Compress
    return @(
        [string]$Boundary.coordinatorInstanceId,
        [string]$Boundary.writer,
        [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $Boundary -Name 'sessionId' -Default ''),
        [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $Boundary -Name 'transferId' -Default ''),
        [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $Boundary -Name 'direction' -Default ''),
        [string]$Operation.authorityId,
        [long]$Operation.operationGeneration,
        [string]$payload.transitionMode,
        [long]$payload.admissionGeneration,
        [long]$payload.transactionGeneration,
        [long]$payload.offerGeneration,
        [long]$payload.pendingAnswerAckGeneration,
        [string]$payload.payerDecisionId,
        [long]$payload.launchGeneration,
        [long]$payload.tunaPathLeaseGeneration,
        [string]$payload.tunaPathLeaseListenerRunId,
        [string]$payload.ackMessageId,
        [string]$payload.ackSourceFingerprint,
        [string]$payload.peerProbeFrameFingerprint,
        [string]$payload.probeId,
        [string]$payload.ownerKey,
        [string]$payload.pairKey,
        [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $payload -Name 'localProofIdentity' -Default ''),
        [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $payload -Name 'localProofFingerprint' -Default ''),
        [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $payload -Name 'peerProofIdentity' -Default ''),
        [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $payload -Name 'peerProofFingerprint' -Default ''),
        [string]$payload.pairedReleaseAuthorityId,
        [long]$payload.pairedReleaseGeneration,
        [long]$payload.pairingGeneration,
        $source) -join '|'
}

function Get-FileTransferCoordinatorResponderProbeHandoffObservationKey {
    param([Parameter(Mandatory = $true)]$Entry)

    $boundary = $Entry.Boundary
    $operation = $Entry.Operation
    $payload = $operation.responderProbeHandoff
    return @(
        [string]$boundary.decisionKind,
        [string]$boundary.decisionStatus,
        [string]$boundary.decisionReason,
        [long]$boundary.beforeStateGeneration,
        [long]$boundary.afterStateGeneration,
        [string]$boundary.beforeStateHash,
        [string]$boundary.afterStateHash,
        [string]$operation.operationRole,
        [string]$operation.beforeState,
        [string]$operation.afterState,
        [string]$operation.commandIdentity,
        [long]$operation.commandGeneration,
        [string]$operation.commandFingerprint,
        [string]$operation.receiptIdentity,
        [long]$operation.receiptGeneration,
        [string]$operation.receiptStatus,
        [string]$operation.receiptFingerprint,
        [bool]$operation.receiptValidated,
        [bool]$operation.stateApplied,
        (ConvertTo-Json -InputObject $payload.effectiveBoundary -Depth 16 -Compress),
        (ConvertTo-Json -InputObject $payload.targetBoundary -Depth 16 -Compress)) -join '|'
}

function New-FileTransferCoordinatorSnapshotAggregateIntervalIndex {
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Entries)

    $index = @{}
    foreach ($entry in $Entries) {
        $boundary = $entry.Boundary
        $key = "$($boundary.coordinatorInstanceId)|$($boundary.writer)"
        if (-not $index.ContainsKey($key)) {
            $index[$key] = @{ Boundaries = @{}; Mutations = @{} }
        }
        $stream = $index[$key]
        $stream.Boundaries[[string]$boundary.boundaryId] = $entry
        if ([long]$boundary.beforeStateGeneration -eq
            [long]$boundary.afterStateGeneration) {
            continue
        }
        $generation = [long]$boundary.beforeStateGeneration
        if (-not $stream.Mutations.ContainsKey($generation)) {
            $stream.Mutations[$generation] =
                [System.Collections.Generic.List[object]]::new()
        }
        $stream.Mutations[$generation].Add($entry)
    }
    return $index
}

function Get-FileTransferCoordinatorSnapshotInitialRegularPublications {
    param($AggregateIndex, [AllowEmptyCollection()][object[]]$Effects)

    $result = @{}
    $effectsByBoundary = @{}
    foreach ($effect in $Effects) {
        if ($effect.EffectKind -cne 'route_publication_forwarded') { continue }
        $key = "$($effect.CoordinatorInstanceId)|$($effect.Writer)|$($effect.TransitionId)"
        if (-not $effectsByBoundary.ContainsKey($key)) {
            $effectsByBoundary[$key] = [System.Collections.Generic.List[object]]::new()
        }
        $effectsByBoundary[$key].Add($effect)
    }
    foreach ($stream in $AggregateIndex.Values) {
        $owner = $null; $publication = $null; $started = $false; $poisoned = $false
        $publicationAuthorities = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $scope = $null; $hash = $null; $generation = 0L; $identity = $null
        foreach ($entry in @($stream.Boundaries.Values | Sort-Object {
                    [long]$_.Boundary.beforeStateGeneration
                }, { [long]$_.Boundary.afterStateGeneration }, { [string]$_.Boundary.boundaryId })) {
            $boundary = $entry.Boundary
            # Reduced route fixtures and malformed inputs cannot establish
            # an initial-owner exemption; leave their existing validation live.
            foreach ($field in @('beforeBoundary','afterBoundary','beforeStateHash','afterStateHash',
                    'sessionId','transferId','direction','decisionKind','decisionStatus')) {
                if ($null -eq $boundary.PSObject.Properties[$field]) { $poisoned = $true }
            }
            if ($poisoned) { continue }
            $before = $boundary.beforeBoundary; $after = $boundary.afterBoundary
            $boundaryIdentity = "$($boundary.sessionId)|$($boundary.transferId)|$($boundary.direction)"
            if ($null -eq $before -or $null -eq $after -or
                [string]::IsNullOrWhiteSpace([string]$boundary.beforeStateHash) -or
                [string]::IsNullOrWhiteSpace([string]$boundary.afterStateHash) -or
                -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $before) -or
                -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $after) -or
                [long]$boundary.beforeStateGeneration -ne $generation -or
                [long]$boundary.afterStateGeneration -notin @($generation, ($generation + 1L)) -or
                (([long]$boundary.afterStateGeneration -eq $generation) -ne
                    ($boundary.beforeStateHash -ceq $boundary.afterStateHash)) -or
                ([long]$boundary.afterStateGeneration -eq $generation -and
                    -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $before $after)) -or
                ($null -ne $hash -and ($boundary.beforeStateHash -cne $hash -or
                    $boundaryIdentity -cne $identity -or
                    -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $before $scope)))) {
                $poisoned = $true
            }
            $generation = [long]$boundary.afterStateGeneration
            $hash = [string]$boundary.afterStateHash; $scope = $after; $identity = $boundaryIdentity
            if ($poisoned) { continue }
            $regular = $after.route -ceq 'regular_nkn_v4_fast' -and
                [long]$after.protocolVersion -eq 4L -and [long]$after.liveRouteEpoch -eq 0L -and
                [long]$after.routeCommitGeneration -eq 0L -and [long]$after.terminalGeneration -eq 0L
            $runtime = @($boundary.operations | Where-Object { $_.family -ceq 'route_runtime' -and $_.stateApplied })
            if (-not $started) {
                $births = @($runtime | Where-Object {
                    $_.operationRole -ceq 'runtime_owner' -and $_.operationKind -ceq 'RuntimeOwner' -and
                    $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Current'
                })
                if ($births.Count -eq 0) { continue }
                $started = $true
                if ($births.Count -ne 1 -or $runtime.Count -ne 1 -or -not $regular -or
                    $boundary.decisionKind -cne 'StartTransfer' -or $boundary.decisionStatus -cne 'Accepted' -or
                    [long]$boundary.afterStateGeneration -ne ([long]$boundary.beforeStateGeneration + 1L) -or
                    $boundary.beforeStateHash -ceq $boundary.afterStateHash -or
                    [long]$before.runtimeGeneration -ne 0L -or [long]$before.legGeneration -ne 0L -or
                    [long]$before.routePublicationGeneration -ne 0L -or [long]$after.routePublicationGeneration -ne 0L -or
                    [long]$after.runtimeGeneration -le 0L -or [long]$after.legGeneration -le 0L -or
                    [long]$births[0].operationGeneration -ne [long]$after.runtimeGeneration -or
                    -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $births[0].routeRuntime.boundary $after)) {
                    $poisoned = $true; continue
                }
                $owner = $births[0]
                foreach ($field in @('route','protocolVersion','liveRouteEpoch','routeCommitGeneration','terminalGeneration',
                        'transportEpoch','bridgeRecoveryGeneration','checkpointGeneration','checkpointRequestId',
                        'dataSessionIdentity','dataSessionGeneration')) {
                    if ([string]$before.$field -cne [string]$after.$field) { $poisoned = $true }
                }
                if ([string]::IsNullOrWhiteSpace([string]$owner.authorityId) -or $owner.authorityId -ceq '(none)' -or
                    $owner.operationKey -cne "route_runtime|runtime_owner|$($owner.authorityId)|$($owner.operationGeneration)|RuntimeOwner" -or
                    $owner.predecessorAuthorityId -cne '(none)' -or $owner.routeRuntime.role -cne 'runtime_owner' -or
                    $owner.commandIdentity -cne $owner.authorityId -or
                    [long]$owner.commandGeneration -ne [long]$owner.operationGeneration -or
                    [string]::IsNullOrWhiteSpace([string]$owner.commandFingerprint) -or $owner.commandFingerprint -ceq '(none)' -or
                    $owner.routeRuntime.tokenFingerprint -cne $owner.commandFingerprint -or
                    $owner.receiptValidated -or $owner.receiptIdentity -cne '(none)' -or
                    [long]$owner.receiptGeneration -ne 0L -or $owner.receiptStatus -cne '(none)' -or
                    $owner.receiptFingerprint -cne '(none)') { $poisoned = $true }
                continue
            }
            if (-not $regular -or
                -not (Test-FileTransferCoordinatorSnapshotRouteOwnerLineageEqual $before $after) -or
                [long]$before.routeCommitGeneration -ne [long]$after.routeCommitGeneration -or
                -not (Test-FileTransferCoordinatorSnapshotRouteOwnerLineageEqual $owner.routeRuntime.boundary $after)) {
                $poisoned = $true; continue
            }
            if ($runtime.Count -eq 0) {
                if ([long]$before.routePublicationGeneration -ne [long]$after.routePublicationGeneration) { $poisoned = $true }
                continue
            }
            $entrants = @($runtime | Where-Object {
                $_.operationRole -ceq 'route_publication' -and $_.operationKind -ceq 'RoutePublication' -and
                $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Published'
            })
            $retirements = @($runtime | Where-Object {
                $_.operationRole -ceq 'route_publication' -and $_.operationKind -ceq 'RoutePublication' -and
                $_.beforeState -ceq 'Published' -and $_.afterState -ceq 'None'
            })
            $expectedRetirements = if ($null -eq $publication) { 0 } else { 1 }
            if ($runtime.Count -ne (1 + $expectedRetirements) -or $entrants.Count -ne 1 -or
                $retirements.Count -ne $expectedRetirements -or
                $boundary.decisionKind -cne 'RoutePublication' -or $boundary.decisionStatus -cne 'Accepted' -or
                [long]$boundary.afterStateGeneration -ne ([long]$boundary.beforeStateGeneration + 1L) -or
                $boundary.beforeStateHash -ceq $boundary.afterStateHash -or
                [long]$before.routePublicationGeneration -ne $(if ($null -eq $publication) { 0L } else { [long]$publication.operationGeneration }) -or
                [long]$after.routePublicationGeneration -ne ([long]$before.routePublicationGeneration + 1L) -or
                @($boundary.operations | Where-Object { $_.family -ceq 'route_transition' -and $_.stateApplied }).Count -ne 0) {
                $poisoned = $true; continue
            }
            if ($null -ne $publication) {
                $retirement = $retirements[0]
                foreach ($field in @('operationKey','authorityId','operationGeneration','predecessorAuthorityId',
                        'commandIdentity','commandGeneration','commandFingerprint','receiptIdentity',
                        'receiptGeneration','receiptFingerprint','receiptStatus','receiptValidated')) {
                    if ([string]$retirement.$field -cne [string]$publication.$field) { $poisoned = $true }
                }
                if ($retirement.routeRuntime.role -cne 'route_publication' -or
                    $retirement.routeRuntime.tokenFingerprint -cne $publication.routeRuntime.tokenFingerprint -or
                    -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $retirement.routeRuntime.boundary $before)) {
                    $poisoned = $true
                }
                if ($poisoned) { continue }
            }
            $operation = $entrants[0]
            if ([string]::IsNullOrWhiteSpace([string]$operation.authorityId) -or $operation.authorityId -ceq '(none)' -or
                [string]::IsNullOrWhiteSpace([string]$operation.commandFingerprint) -or $operation.commandFingerprint -ceq '(none)' -or
                $publicationAuthorities.Contains([string]$operation.authorityId) -or
                $operation.operationKey -cne "route_runtime|route_publication|$($operation.authorityId)|$($operation.operationGeneration)|RoutePublication" -or
                $operation.predecessorAuthorityId -cne '(none)' -or $operation.routeRuntime.role -cne 'route_publication' -or
                [long]$operation.operationGeneration -ne [long]$after.routePublicationGeneration -or
                $operation.commandIdentity -cne $operation.authorityId -or $operation.receiptIdentity -cne $operation.authorityId -or
                [long]$operation.commandGeneration -ne [long]$operation.operationGeneration -or
                [long]$operation.receiptGeneration -ne [long]$operation.operationGeneration -or
                $operation.receiptStatus -cne 'Accepted' -or -not $operation.receiptValidated -or
                $operation.commandFingerprint -cne $operation.receiptFingerprint -or
                $operation.routeRuntime.tokenFingerprint -cne $operation.receiptFingerprint -or
                -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $operation.routeRuntime.boundary $after)) {
                $poisoned = $true; continue
            }
            $key = "$($boundary.coordinatorInstanceId)|$($boundary.writer)|$($boundary.boundaryId)"
            $boundEffects = @(if ($effectsByBoundary.ContainsKey($key)) { $effectsByBoundary[$key].ToArray() })
            if ($boundEffects.Count -ne 1) { $poisoned = $true; continue }
            $effect = $boundEffects[0]
            if ($effect.Family -cne 'route_runtime' -or $effect.OperationKey -cne $operation.operationKey -or
                $effect.AuthorityId -cne $operation.authorityId -or
                [long]$effect.OperationGeneration -ne [long]$operation.operationGeneration -or
                [long]$effect.StateGeneration -ne [long]$boundary.afterStateGeneration -or
                $effect.ReceiptIdentity -cne $operation.receiptIdentity -or
                [long]$effect.ReceiptGeneration -ne [long]$operation.receiptGeneration -or
                $effect.ReceiptStatus -cne $operation.receiptStatus -or
                $effect.ReceiptFingerprint -cne $operation.receiptFingerprint -or
                -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $effect $after)) {
                $poisoned = $true; continue
            }
            $result["$key|$($operation.operationKey)"] = [pscustomobject]@{
                RuntimeOwner = $owner
                PreviousPublicationKey = if ($null -eq $publication) { '(none)' } else { [string]$publication.operationKey }
            }
            [void]$publicationAuthorities.Add([string]$operation.authorityId)
            $publication = $operation
        }
    }
    return $result
}

function Get-FileTransferCoordinatorReceiveRecoveryRootLifecycle {
    param([AllowEmptyCollection()][object[]]$Entries, $AggregateIndex, $ValidatedHandoffSuccessors)

    # A current retirement and terminal installation are one state boundary,
    # not two chronologically ordered observations. Never select a retired
    # descriptor's historical payload as the current execution state.
    $slots = @{}; $retired = @{}; $latest = $null; $poisoned = $null; $poisonBoundary = $null
    $failures = [System.Collections.Generic.List[string]]::new()
    $groups = @($Entries | Where-Object { $_.Operation.stateApplied } |
        Group-Object { [string]$_.Boundary.boundaryId } |
        Sort-Object { [long]$_.Group[0].Boundary.afterStateGeneration })
    foreach ($group in $groups) {
        $entriesAtBoundary = @($group.Group | Where-Object { $_.Operation.stateApplied })
        if ($entriesAtBoundary.Count -eq 0) { continue }
        $boundary = $entriesAtBoundary[0].Boundary
        $aggregateEntry = $null
        $streamKey = "$($boundary.coordinatorInstanceId)|$($boundary.writer)"
        if ($null -ne $AggregateIndex -and $AggregateIndex.ContainsKey($streamKey)) {
            $aggregateEntry = $AggregateIndex[$streamKey].Boundaries[[string]$boundary.boundaryId]
        }
        $live = @($entriesAtBoundary | Where-Object { $_.Operation.afterState -cne 'None' })
        $leaving = @($entriesAtBoundary | Where-Object { $_.Operation.afterState -ceq 'None' })
        $valid = $live.Count -le 1 -and $leaving.Count -le 1 -and
            @($entriesAtBoundary | Group-Object { $_.Operation.operationRole } |
                Where-Object Count -GT 1).Count -eq 0
        foreach ($entry in $entriesAtBoundary) {
            $operation = $entry.Operation
            $role = [string]$operation.operationRole
            $key = [string]$operation.operationKey
            if ($operation.beforeState -ceq 'None') {
                $valid = $valid -and -not $slots.ContainsKey($role) -and -not $retired.ContainsKey($key)
                continue
            }
            if (-not $slots.ContainsKey($role)) { $valid = $false; continue }
            $prior = $slots[$role].Operation
            $valid = $valid -and $operation.beforeState -ceq $prior.afterState
            foreach ($field in @('operationKey','authorityId','operationGeneration','operationKind',
                    'predecessorAuthorityId','commandIdentity','commandGeneration','commandFingerprint')) {
                $valid = $valid -and [string]$operation.$field -ceq [string]$prior.$field
            }
            $sameSource = Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $operation.receiveRecovery.sourceBoundary $prior.receiveRecovery.sourceBoundary
            if (-not $sameSource -and $null -ne $aggregateEntry -and
                $boundary.decisionKind -in @('FallbackCheckpointRequested','FallbackCheckpointAccepted') -and
                $role -ceq 'operation_terminal' -and $operation.beforeState -ceq $operation.afterState) {
                # The existing checkpoint catalog permits retained root scope
                # to carry the exact pre-decision checkpoint, without changing receipt.
                $sameSource = (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint $operation.receiveRecovery.sourceBoundary $prior.receiveRecovery.sourceBoundary) -and
                    [long]$operation.receiveRecovery.sourceBoundary.checkpointGeneration -eq [long]$aggregateEntry.BeforeBoundary.checkpointGeneration -and
                    $operation.receiveRecovery.sourceBoundary.checkpointRequestId -ceq '(none)' -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $operation.receiveRecovery.sourceBoundary $operation.receiveRecovery.effectiveBoundary)
            }
            if (-not $sameSource -and $role -ceq 'operation_terminal' -and
                $operation.beforeState -ceq $operation.afterState) {
                $sameSource = Test-FileTransferCoordinatorReceiveRecoveryTerminalSlotCarry `
                    -Prior $prior -Operation $operation -Boundary $boundary `
                    -AggregateEntry $aggregateEntry
            }
            $valid = $valid -and $sameSource -and
                $operation.receiveRecovery.recoveryKind -ceq $prior.receiveRecovery.recoveryKind -and
                $operation.receiveRecovery.effectiveBoundary.dataSessionIdentity -ceq $prior.receiveRecovery.effectiveBoundary.dataSessionIdentity -and
                [long]$operation.receiveRecovery.effectiveBoundary.dataSessionGeneration -eq [long]$prior.receiveRecovery.effectiveBoundary.dataSessionGeneration
            if ($operation.afterState -ceq 'None') {
                # Retirement preserves the previous operation, including its
                # receipt. A new proof belongs to the terminal entrant instead.
                foreach ($field in @('receiptIdentity','receiptGeneration','receiptStatus','receiptFingerprint','receiptValidated')) {
                    $valid = $valid -and [string]$operation.$field -ceq [string]$prior.$field
                }
                $valid = $valid -and
                    $operation.receiveRecovery.proofIdentity -ceq $prior.receiveRecovery.proofIdentity -and
                    [long]$operation.receiveRecovery.proofObservedUtcMs -eq [long]$prior.receiveRecovery.proofObservedUtcMs
            }
        }
        if ($live.Count -eq 1 -and $leaving.Count -eq 1) {
            $entrant = $live[0].Operation; $retirement = $leaving[0].Operation
            $valid = $valid -and $retirement.operationRole -ceq 'operation_current' -and
                $entrant.operationRole -ceq 'operation_terminal' -and $entrant.beforeState -ceq 'None'
            foreach ($field in @('authorityId','operationGeneration','operationKind','predecessorAuthorityId',
                    'commandIdentity','commandGeneration','commandFingerprint')) {
                $valid = $valid -and [string]$entrant.$field -ceq [string]$retirement.$field
            }
            $valid = $valid -and
                (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $entrant.receiveRecovery.sourceBoundary $retirement.receiveRecovery.sourceBoundary) -and
                (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $entrant.receiveRecovery.effectiveBoundary $retirement.receiveRecovery.effectiveBoundary)
            if ($entrant.receiveRecovery.executionState -ceq 'Invalidated' -and
                $entrant.receiveRecovery.proofIdentity -ceq '(none)') {
                foreach ($field in @('receiptIdentity','receiptGeneration','receiptStatus','receiptFingerprint','receiptValidated')) {
                    $valid = $valid -and [string]$entrant.$field -ceq [string]$retirement.$field
                }
            }
        }
        elseif ($live.Count -eq 1 -and $slots.Count -gt 0 -and
            -not $slots.ContainsKey([string]$live[0].Operation.operationRole)) {
            $valid = $false
        }
        if (-not $valid) {
            $failures.Add("snapshot_receive_recovery_root_lifecycle_invalid:$($boundary.evidenceLineIndex)")
            # Invalid evidence cannot establish state for a later callback.
            break
        }
        foreach ($entry in $leaving) {
            $role = [string]$entry.Operation.operationRole
            $prior = $slots[$role]
            $successors = @()
            $handoffKey = "$($boundary.coordinatorInstanceId)|$($boundary.writer)|$($boundary.boundaryId)"
            $handoffSuccessor = if ($null -ne $ValidatedHandoffSuccessors -and
                $ValidatedHandoffSuccessors.ContainsKey($handoffKey)) { $ValidatedHandoffSuccessors[$handoffKey] } else { $null }
            $directSuccessor = $boundary.decisionKind -ceq 'IssueReceiveRecoveryOperation' -and
                $boundary.decisionStatus -ceq 'Accepted' -and $boundary.decisionReason -ceq 'filetransfer_receive_recovery_operation_successor_issued'
            if ($role -ceq 'operation_current' -and ($directSuccessor -or $null -ne $handoffSuccessor)) {
                $successors = @($boundary.operations | Where-Object {
                    $_.family -ceq 'receive_recovery' -and $_.operationRole -ceq 'operation_current' -and $_.stateApplied -and
                    $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Issued:None' -and
                    ($directSuccessor -or $_.operationKey -ceq $handoffSuccessor.operationKey) -and
                    $_.authorityId -cne $prior.Operation.authorityId -and $_.predecessorAuthorityId -ceq $prior.Operation.authorityId -and
                    [long]$_.operationGeneration -eq ([long]$prior.Operation.operationGeneration + 1L) -and
                    $_.operationKind -ceq $prior.Operation.operationKind -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $_.receiveRecovery.sourceBoundary $prior.Operation.receiveRecovery.sourceBoundary) -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $_.receiveRecovery.effectiveBoundary $prior.Operation.receiveRecovery.effectiveBoundary)
                })
            }
            if ($null -eq $poisoned -and $prior.Operation.receiveRecovery.executionState -ceq 'Completed' -and
                $successors.Count -ne 1 -and
                ($live.Count -eq 0 -or ($live[0].Operation.receiveRecovery.executionState -ceq 'Invalidated' -and
                    $live[0].Operation.receiveRecovery.proofIdentity -ceq '(none)'))) {
                # Cancellation is not recovery. Preserve an already established
                # Completed obligation at its pre-retirement boundary.
                $poisoned = $prior
                $poisonBoundary = $boundary
            }
            [void]$slots.Remove($role)
            $retired[[string]$entry.Operation.operationKey] = $true
        }
        if ($live.Count -eq 1) {
            $latest = $live[0]
            $slots[[string]$latest.Operation.operationRole] = $latest
        }
        else { $latest = $null }
    }
    [pscustomobject]@{ Latest = $latest; Poisoned = $poisoned; PoisonBoundary = $poisonBoundary; Errors = @($failures) }
}

function Get-FileTransferCoordinatorReceiveRecoveryRouteCarryIdentity {
    param($Operation)

    [ordered]@{
        Operation = $Operation | Select-Object * -ExcludeProperty beforeState,afterState,stateApplied,routeTransition
        Route = $Operation.routeTransition | Select-Object * -ExcludeProperty sourceBoundary,targetBoundary
    } | ConvertTo-Json -Depth 64 -Compress
}

function Get-FileTransferCoordinatorReceiveRecoveryTerminalSlotIdentity {
    param($Operation)

    $globalChildFields = @(
        'role', 'sourceBoundary', 'effectiveBoundary',
        'initialActivationHoldGeneration', 'envelopeRefreshGeneration',
        'checkpointBindingGeneration', 'checkpointDeliveryGeneration',
        'checkpointResumeGeneration', 'checkpointFrontierHoldGeneration',
        'checkpointFrontierRecheckGeneration')
    [ordered]@{
        Operation = $Operation | Select-Object * -ExcludeProperty beforeState,afterState,stateApplied,receiveRecovery
        Recovery = $Operation.receiveRecovery | Select-Object * -ExcludeProperty $globalChildFields
    } | ConvertTo-Json -Depth 64 -Compress
}

function Test-FileTransferCoordinatorReceiveRecoveryTerminalSlotCarry {
    param($Prior, $Operation, $Boundary, $AggregateEntry)

    if ($null -eq $Prior -or $null -eq $Operation -or
        [string]$Operation.beforeState -cne [string]$Prior.afterState -or
        (Get-FileTransferCoordinatorReceiveRecoveryTerminalSlotIdentity $Operation) -cne
            (Get-FileTransferCoordinatorReceiveRecoveryTerminalSlotIdentity $Prior)) {
        return $false
    }

    $sameSource = Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
        $Operation.receiveRecovery.sourceBoundary $Prior.receiveRecovery.sourceBoundary
    $sameEffective = Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
        $Operation.receiveRecovery.effectiveBoundary $Prior.receiveRecovery.effectiveBoundary
    if (-not $sameSource -or -not $sameEffective) {
        # A retained terminal may adopt only the exact checkpoint scope already
        # admitted by the checkpoint catalog. Its authority and immutable
        # receipt remain unchanged; no route or data-session mutation is
        # inferred here.
        $checkpointCarry = $null -ne $Boundary -and $null -ne $AggregateEntry -and
            [string]$Boundary.decisionKind -in @('FallbackCheckpointRequested','FallbackCheckpointAccepted') -and
            [string]$Operation.beforeState -ceq [string]$Operation.afterState -and
            (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint `
                $Operation.receiveRecovery.sourceBoundary $Prior.receiveRecovery.sourceBoundary) -and
            (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint `
                $Operation.receiveRecovery.effectiveBoundary $Prior.receiveRecovery.effectiveBoundary) -and
            [long]$Operation.receiveRecovery.sourceBoundary.checkpointGeneration -eq
                [long]$AggregateEntry.BeforeBoundary.checkpointGeneration -and
            [long]$Operation.receiveRecovery.effectiveBoundary.checkpointGeneration -eq
                [long]$AggregateEntry.BeforeBoundary.checkpointGeneration -and
            [string]$Operation.receiveRecovery.sourceBoundary.checkpointRequestId -ceq '(none)' -and
            [string]$Operation.receiveRecovery.effectiveBoundary.checkpointRequestId -ceq '(none)' -and
            (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                $Operation.receiveRecovery.sourceBoundary $Operation.receiveRecovery.effectiveBoundary)
        $routeCommitCheckpointConvergence =
            $null -ne $Boundary -and $null -ne $AggregateEntry -and
            [string]$Boundary.decisionKind -ceq 'CommitFileTransferRouteTransition' -and
            [string]$Boundary.decisionStatus -ceq 'Committed' -and
            [string]$Operation.beforeState -ceq [string]$Operation.afterState -and
            (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint `
                $Operation.receiveRecovery.sourceBoundary $Prior.receiveRecovery.sourceBoundary) -and
            (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint `
                $Operation.receiveRecovery.effectiveBoundary $Prior.receiveRecovery.effectiveBoundary) -and
            [long]$Prior.receiveRecovery.sourceBoundary.checkpointGeneration -gt 0 -and
            [long]$Prior.receiveRecovery.effectiveBoundary.checkpointGeneration -eq
                [long]$Prior.receiveRecovery.sourceBoundary.checkpointGeneration -and
            [string]$Prior.receiveRecovery.sourceBoundary.checkpointRequestId -ceq '(none)' -and
            [string]$Prior.receiveRecovery.effectiveBoundary.checkpointRequestId -ceq '(none)' -and
            [long]$Operation.receiveRecovery.sourceBoundary.checkpointGeneration -eq 0 -and
            [long]$Operation.receiveRecovery.effectiveBoundary.checkpointGeneration -eq 0 -and
            [string]$Operation.receiveRecovery.sourceBoundary.checkpointRequestId -ceq '(none)' -and
            [string]$Operation.receiveRecovery.effectiveBoundary.checkpointRequestId -ceq '(none)' -and
            [long]$AggregateEntry.BeforeBoundary.checkpointGeneration -eq 0 -and
            [string]$AggregateEntry.BeforeBoundary.checkpointRequestId -ceq '(none)' -and
            [long]$AggregateEntry.AfterBoundary.checkpointGeneration -eq 0 -and
            [string]$AggregateEntry.AfterBoundary.checkpointRequestId -ceq '(none)' -and
            (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                $Operation.receiveRecovery.sourceBoundary `
                $Operation.receiveRecovery.effectiveBoundary)
        if (-not $checkpointCarry -and -not $routeCommitCheckpointConvergence) {
            return $false
        }
    }
    return $true
}

function Test-FileTransferCoordinatorReceiveRecoveryTerminalSlotEntrant {
    param($Operation, $Boundary)

    if ($null -eq $Operation -or $null -eq $Operation.receiveRecovery -or
        [string]$Operation.family -cne 'receive_recovery' -or
        [string]$Operation.operationRole -cne 'operation_terminal' -or
        [string]$Operation.operationKind -cne 'BridgeReceiveStallRecovery' -or
        [string]$Operation.beforeState -cne 'None' -or
        [string]$Operation.afterState -notin @('Completed:None','Invalidated:None') -or
        $Operation.stateApplied -isnot [bool] -or -not $Operation.stateApplied -or
        [string]$Operation.authorityId -in @('', '(none)') -or
        [long]$Operation.operationGeneration -le 0 -or
        [string]$Operation.operationKey -cne
            "receive_recovery|operation_terminal|$($Operation.authorityId)|$($Operation.operationGeneration)|BridgeReceiveStallRecovery" -or
        [string]$Operation.commandIdentity -cne [string]$Operation.authorityId -or
        [long]$Operation.commandGeneration -ne [long]$Operation.operationGeneration -or
        [long]$Operation.receiptGeneration -ne [long]$Operation.operationGeneration -or
        $Operation.receiptValidated -isnot [bool] -or -not $Operation.receiptValidated -or
        [string]$Operation.receiveRecovery.role -cne 'operation_terminal' -or
        [string]$Operation.receiveRecovery.recoveryKind -cne 'BridgeReceiveStallRecovery' -or
        "$($Operation.receiveRecovery.executionState):$($Operation.receiveRecovery.closureState)" -cne
            [string]$Operation.afterState -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            $Operation.receiveRecovery.sourceBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            $Operation.receiveRecovery.effectiveBoundary)) {
        return $false
    }
    if ([string]$Operation.afterState -ceq 'Completed:None' -and
        ([string]$Operation.receiveRecovery.proofIdentity -in @('', '(none)') -or
         [long]$Operation.receiveRecovery.proofObservedUtcMs -le 0)) {
        return $false
    }
    $adoptedTerminal = [string]$Operation.receiptStatus -ceq 'Adopted'
    $completedUnprovenInvalidation =
        [string]$Operation.afterState -ceq 'Invalidated:None' -and
        [string]$Operation.receiptStatus -ceq 'Completed' -and
        [string]$Operation.receiveRecovery.proofIdentity -ceq '(none)' -and
        [long]$Operation.receiveRecovery.proofObservedUtcMs -eq 0 -and
        $null -ne $Boundary -and
        [string]$Boundary.decisionKind -ceq 'IssueReceiveRecoveryOperation' -and
        [string]$Boundary.decisionStatus -ceq 'Accepted' -and
        [string]$Boundary.decisionReason -ceq
            'filetransfer_receive_recovery_operation_successor_issued'
    if (-not $adoptedTerminal -and -not $completedUnprovenInvalidation) {
        return $false
    }
    $receipts = @($Operation.receiveRecovery.receipts)
    if ($receipts.Count -ne 1 -or [string]$receipts[0].stage -cne 'operation' -or
        $receipts[0].receiptValidated -isnot [bool] -or -not $receipts[0].receiptValidated) {
        return $false
    }
    if ($completedUnprovenInvalidation -and
        [string]$receipts[0].receiptReason -cne
            'completed_unproven_receive_recovery_superseded_by_newer_health_observation') {
        return $false
    }
    foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity',
            'receiptGeneration','receiptStatus','receiptFingerprint')) {
        if ([string]$Operation.$field -cne [string]$receipts[0].$field -or
            [string]$Operation.$field -in @('', '(none)')) {
            return $false
        }
    }
    return $true
}

function Test-FileTransferCoordinatorReceiveRecoveryRouteTargetCarry {
    param($PreviousTarget, $Target, $BeforeBoundary, $AfterBoundary, [string]$Role, [string]$DecisionKind)

    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $PreviousTarget) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $Target)) { return $false }
    foreach ($field in $PreviousTarget.PSObject.Properties.Name) {
        if ([string]$PreviousTarget.$field -ceq [string]$Target.$field) { continue }
        if ($Role -ceq 'terminal' -and $field -ceq 'checkpointRequestId' -and
            [long]$PreviousTarget.checkpointGeneration -eq [long]$BeforeBoundary.checkpointGeneration -and
            [long]$Target.checkpointGeneration -eq [long]$AfterBoundary.checkpointGeneration) {
            # Request identities, like data-session identities, have separate
            # target and aggregate projections. Preserve the target's own exact
            # request until its validated checkpoint acceptance clears it.
            if (($DecisionKind -ceq 'FallbackCheckpointRequested' -and
                    [long]$PreviousTarget.checkpointGeneration -eq 0 -and $PreviousTarget.checkpointRequestId -ceq '(none)' -and
                    [long]$Target.checkpointGeneration -gt 0 -and $Target.checkpointRequestId -notin @('', '(none)')) -or
                ($DecisionKind -ceq 'FallbackCheckpointAccepted' -and
                    [long]$PreviousTarget.checkpointGeneration -gt 0 -and $PreviousTarget.checkpointRequestId -notin @('', '(none)') -and
                    [long]$Target.checkpointGeneration -eq 0 -and $Target.checkpointRequestId -ceq '(none)')) { continue }
        }
        # A retained target identity never changes representation. Other scope
        # changes must be the exact aggregate-authored delta, not a new target.
        if (($Role -ceq 'fallback_v6_bootstrap' -and $field -notin @('transportEpoch', 'bridgeRecoveryGeneration')) -or
            $field -in @('dataSessionIdentity', 'dataSessionGeneration') -or
            [string]$PreviousTarget.$field -cne [string]$BeforeBoundary.$field -or
            [string]$Target.$field -cne [string]$AfterBoundary.$field) { return $false }
    }
    return $true
}

function Resolve-FileTransferCoordinatorReceiveRecoveryRouteCarries {
    param($AggregateIndex, $Candidates, $InvalidBoundaries, [object[]]$Effects)

    $routeEffects = @{}
    foreach ($effect in $Effects) {
        if ($effect.Family -ceq 'route_transition') {
            $routeEffects["$($effect.CoordinatorInstanceId)|$($effect.TransitionId)"] = $true
        }
    }
    $resolved = @{}
    foreach ($stream in $AggregateIndex.Values) {
        $slots = @{}; $seen = @{}; $previous = $null; $currentRecovery = $null; $seenRecovery = @{}
        $terminalRecovery = $null; $seenTerminalRecovery = @{}
        foreach ($generation in @($stream.Mutations.Keys | Sort-Object)) {
            $entries = $stream.Mutations[$generation]
            if ($entries.Count -ne 1) {
                $slots.Clear(); $previous = $null; $currentRecovery = $null; $terminalRecovery = $null
                continue
            }
            $entry = $entries[0]; $boundary = $entry.Boundary; $id = [string]$boundary.boundaryId
            $valid = -not $InvalidBoundaries.ContainsKey($id) -and
                [long]$boundary.afterStateGeneration -eq ([long]$generation + 1L) -and
                [string]$boundary.beforeStateHash -cne [string]$boundary.afterStateHash -and
                [long]$entry.AfterBoundary.terminalGeneration -eq 0 -and
                [string]$entry.BeforeBoundary.dataSessionIdentity -ceq [string]$entry.AfterBoundary.dataSessionIdentity -and
                [long]$entry.BeforeBoundary.dataSessionGeneration -eq [long]$entry.AfterBoundary.dataSessionGeneration
            if ($null -ne $previous) {
                $valid = $valid -and [long]$previous.Boundary.afterStateGeneration -eq [long]$generation -and
                    [string]$previous.Boundary.afterStateHash -ceq [string]$boundary.beforeStateHash -and
                    [string]$previous.SessionId -ceq [string]$entry.SessionId -and
                    [string]$previous.TransferId -ceq [string]$entry.TransferId -and
                    [string]$previous.Direction -ceq [string]$entry.Direction -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $previous.AfterBoundary $entry.BeforeBoundary)
            }
            if (-not $valid) { $slots.Clear(); $currentRecovery = $null; $terminalRecovery = $null }
            $operations = @($boundary.operations | Where-Object {
                $_.family -ceq 'route_transition' -and $_.stateApplied -and
                $_.operationRole -in @('terminal', 'fallback_v6_bootstrap')
            })
            if ($Candidates.ContainsKey($id)) {
                $resolved[$id] = $true
                if ($boundary.decisionKind -ceq 'ObserveReceiveRecoveryProof') {
                    $retirements = @($boundary.operations | Where-Object {
                        $_.family -ceq 'receive_recovery' -and $_.operationRole -ceq 'operation_current' -and $_.stateApplied
                    })
                    $valid = $valid -and $null -ne $currentRecovery -and $retirements.Count -eq 1
                    if ($null -ne $currentRecovery -and $retirements.Count -eq 1) {
                        $retiredIdentity = $retirements[0] | Select-Object * -ExcludeProperty beforeState,afterState,stateApplied | ConvertTo-Json -Depth 64 -Compress
                        $currentIdentity = $currentRecovery | Select-Object * -ExcludeProperty beforeState,afterState,stateApplied | ConvertTo-Json -Depth 64 -Compress
                        $valid = $valid -and $retirements[0].beforeState -ceq $currentRecovery.afterState -and
                            $retiredIdentity -ceq $currentIdentity
                    }
                    $terminalChanges = @($boundary.operations | Where-Object {
                        $_.family -ceq 'receive_recovery' -and $_.operationRole -ceq 'operation_terminal' -and $_.stateApplied
                    })
                    $terminalEntrants = @($terminalChanges | Where-Object {
                        $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Completed:None'
                    })
                    $terminalRetirements = @($terminalChanges | Where-Object afterState -CEQ 'None')
                    $valid = $valid -and $terminalEntrants.Count -eq 1 -and
                        $terminalRetirements.Count -le 1 -and
                        $terminalChanges.Count -eq ($terminalEntrants.Count + $terminalRetirements.Count)
                    if ($terminalRetirements.Count -eq 1) {
                        $valid = $valid -and $null -ne $terminalRecovery -and
                            (Test-FileTransferCoordinatorReceiveRecoveryTerminalSlotCarry `
                                $terminalRecovery $terminalRetirements[0] $boundary $entry) -and
                            $terminalRetirements[0].authorityId -cne $terminalEntrants[0].authorityId -and
                            [long]$terminalRetirements[0].operationGeneration -lt
                                [long]$terminalEntrants[0].operationGeneration
                    }
                    else {
                        $valid = $valid -and $null -eq $terminalRecovery
                    }
                }
                foreach ($operation in $operations) {
                    $role = [string]$operation.operationRole
                    if (-not $slots.ContainsKey($role)) { $valid = $false; continue }
                    $prior = $slots[$role]
                    $valid = $valid -and $operation.operationKey -ceq $prior.operationKey -and
                        $operation.beforeState -ceq $prior.afterState -and
                        (Get-FileTransferCoordinatorReceiveRecoveryRouteCarryIdentity $operation) -ceq
                            (Get-FileTransferCoordinatorReceiveRecoveryRouteCarryIdentity $prior) -and
                        [long]$prior.routeTransition.targetBoundary.bridgeRecoveryGeneration -eq
                            [long]$entry.BeforeBoundary.bridgeRecoveryGeneration -and
                        (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptBridgeRecovery `
                            $prior.routeTransition.targetBoundary $operation.routeTransition.targetBoundary)
                    if ($role -ceq 'terminal') {
                        $valid = $valid -and
                            (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                                $prior.routeTransition.sourceBoundary $operation.routeTransition.sourceBoundary)
                    }
                    else {
                        $valid = $valid -and
                            (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $prior.routeTransition.sourceBoundary $entry.BeforeBoundary) -and
                            (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptBridgeRecovery `
                                $prior.routeTransition.sourceBoundary $operation.routeTransition.sourceBoundary)
                    }
                }
                $valid = $valid -and $operations.Count -eq 2
                $variant = 'receive_recovery_route_terminal_carry_history_invalid'
                if ($routeEffects.ContainsKey("$($boundary.coordinatorInstanceId)|$id")) {
                    $valid = $false
                    $variant = 'receive_recovery_route_terminal_carry_effect_invalid'
                }
                if (-not $valid) {
                    $InvalidBoundaries[$id] = $true
                    [void]$stream.Boundaries.Remove($id); [void]$stream.Mutations.Remove($generation)
                    "snapshot_operation_catalog_mismatch|$variant`:$($boundary.evidenceLineIndex)"
                    $slots.Clear(); $currentRecovery = $null; $terminalRecovery = $null
                }
            }
            if ($valid) {
                $recoveries = @($boundary.operations | Where-Object {
                    $_.family -ceq 'receive_recovery' -and $_.operationRole -ceq 'operation_current' -and $_.stateApplied
                })
                $retiringRecovery = @($recoveries | Where-Object afterState -CEQ 'None')
                $remainingRecovery = @($recoveries | Where-Object afterState -CNE 'None')
                if ($recoveries.Count -gt 0) {
                    $recoveryValid = $retiringRecovery.Count -le 1 -and $remainingRecovery.Count -le 1
                    foreach ($recovery in $recoveries) {
                        if ($recovery.beforeState -ceq 'None') {
                            $recoveryValid = $recoveryValid -and -not $seenRecovery.ContainsKey([string]$recovery.operationKey) -and
                                ($null -eq $currentRecovery -or $retiringRecovery.Count -eq 1)
                            $seenRecovery[[string]$recovery.operationKey] = $true
                            continue
                        }
                        $recoveryValid = $recoveryValid -and $null -ne $currentRecovery
                        if ($null -ne $currentRecovery) {
                            foreach ($field in @('operationKey','commandIdentity','commandGeneration','commandFingerprint')) {
                                $recoveryValid = $recoveryValid -and [string]$recovery.$field -ceq [string]$currentRecovery.$field
                            }
                            $recoveryValid = $recoveryValid -and $recovery.beforeState -ceq $currentRecovery.afterState -and
                                (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $recovery.receiveRecovery.sourceBoundary $currentRecovery.receiveRecovery.sourceBoundary)
                        }
                    }
                    if ($recoveryValid -and $remainingRecovery.Count -eq 1) { $currentRecovery = $remainingRecovery[0] }
                    else { $currentRecovery = $null }
                }
                $terminalChanges = @($boundary.operations | Where-Object {
                    $_.family -ceq 'receive_recovery' -and $_.operationRole -ceq 'operation_terminal' -and $_.stateApplied
                })
                $retiringTerminal = @($terminalChanges | Where-Object afterState -CEQ 'None')
                $remainingTerminal = @($terminalChanges | Where-Object afterState -CNE 'None')
                if ($terminalChanges.Count -gt 0) {
                    $terminalValid = $retiringTerminal.Count -le 1 -and $remainingTerminal.Count -le 1 -and
                        $terminalChanges.Count -eq ($retiringTerminal.Count + $remainingTerminal.Count)
                    if ($retiringTerminal.Count -eq 1) {
                        $terminalValid = $terminalValid -and $null -ne $terminalRecovery -and
                            (Test-FileTransferCoordinatorReceiveRecoveryTerminalSlotCarry `
                                $terminalRecovery $retiringTerminal[0] $boundary $entry)
                    }
                    if ($remainingTerminal.Count -eq 1) {
                        $nextTerminal = $remainingTerminal[0]
                        if ($nextTerminal.beforeState -ceq 'None') {
                            $terminalValid = $terminalValid -and
                                (Test-FileTransferCoordinatorReceiveRecoveryTerminalSlotEntrant `
                                    $nextTerminal $boundary) -and
                                ($null -eq $terminalRecovery -or $retiringTerminal.Count -eq 1) -and
                                -not $seenTerminalRecovery.ContainsKey([string]$nextTerminal.operationKey)
                            $seenTerminalRecovery[[string]$nextTerminal.operationKey] = $true
                        }
                        else {
                            $terminalValid = $terminalValid -and $null -ne $terminalRecovery -and
                                (Test-FileTransferCoordinatorReceiveRecoveryTerminalSlotCarry `
                                    $terminalRecovery $nextTerminal $boundary $entry)
                        }
                    }
                    if (-not $terminalValid) { $terminalRecovery = $null }
                    elseif ($remainingTerminal.Count -eq 1) { $terminalRecovery = $remainingTerminal[0] }
                    elseif ($retiringTerminal.Count -eq 1) { $terminalRecovery = $null }
                }
                foreach ($role in @('terminal', 'fallback_v6_bootstrap')) {
                    $changes = @($operations | Where-Object operationRole -CEQ $role)
                    if ($changes.Count -eq 0) {
                        if (-not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $entry.BeforeBoundary $entry.AfterBoundary)) {
                            [void]$slots.Remove($role)
                        }
                        continue
                    }
                    $retired = @($changes | Where-Object afterState -CEQ 'None')
                    $remaining = @($changes | Where-Object afterState -CNE 'None')
                    if ($retired.Count -gt 1 -or $remaining.Count -gt 1) { [void]$slots.Remove($role); continue }
                    if ($retired.Count -eq 1) {
                        if (-not $slots.ContainsKey($role) -or $slots[$role].operationKey -cne $retired[0].operationKey) {
                            [void]$slots.Remove($role); continue
                        }
                        [void]$slots.Remove($role)
                    }
                    if ($remaining.Count -eq 0) { continue }
                    $operation = $remaining[0]; $key = [string]$operation.operationKey
                    if ($operation.beforeState -ceq 'None') {
                        if ($slots.ContainsKey($role) -or $seen.ContainsKey($key)) { [void]$slots.Remove($role); continue }
                        $seen[$key] = $true
                    }
                    elseif (-not $slots.ContainsKey($role) -or $slots[$role].operationKey -cne $key -or
                        $slots[$role].afterState -cne $operation.beforeState) {
                        [void]$slots.Remove($role); continue
                    }
                    elseif ($role -ceq 'terminal' -and
                        -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                            $slots[$role].routeTransition.sourceBoundary $operation.routeTransition.sourceBoundary)) {
                        [void]$slots.Remove($role); continue
                    }
                    elseif (-not (Test-FileTransferCoordinatorReceiveRecoveryRouteTargetCarry `
                            $slots[$role].routeTransition.targetBoundary $operation.routeTransition.targetBoundary `
                            $entry.BeforeBoundary $entry.AfterBoundary $role $boundary.decisionKind)) {
                        [void]$slots.Remove($role); continue
                    }
                    elseif ((($role -ceq 'fallback_v6_bootstrap' -and $slots[$role].afterState -ceq 'Delivered') -or
                            ($role -ceq 'terminal' -and @($slots[$role].routeTransition.receipts | Where-Object {
                                $_.stage -ceq 'disposition' -and $_.receiptValidated -and $_.receiptStatus -in @('Applied', 'Idempotent')
                            }).Count -eq 1)) -and
                        (Get-FileTransferCoordinatorReceiveRecoveryRouteCarryIdentity $slots[$role]) -cne
                            (Get-FileTransferCoordinatorReceiveRecoveryRouteCarryIdentity $operation)) {
                        [void]$slots.Remove($role); continue
                    }
                    $slots[$role] = $operation
                }
            }
            $previous = $entry
        }
    }
    foreach ($id in $Candidates.Keys) {
        if (-not $resolved.ContainsKey($id)) {
            "snapshot_operation_catalog_mismatch|receive_recovery_route_terminal_carry_history_invalid:$($Candidates[$id].Line)"
        }
    }
}

function Test-FileTransferCoordinatorCheckpointYieldTerminalBirth {
    param($Current, $Terminal)

    if ($null -eq $Current -or $null -eq $Terminal -or
        $null -eq $Current.fallbackTailYield -or $null -eq $Terminal.fallbackTailYield -or
        $Current.operationRole -cne 'yield_current' -or $Current.afterState -cne 'None' -or
        $Terminal.operationRole -cne 'yield_terminal' -or $Terminal.beforeState -cne 'None' -or
        $Terminal.afterState -cne 'Invalidated') { return $false }
    foreach ($field in @('authorityId','operationGeneration','operationKind','predecessorAuthorityId')) {
        if ([string]$Current.$field -cne [string]$Terminal.$field) { return $false }
    }
    $before = $Current.fallbackTailYield; $after = $Terminal.fallbackTailYield
    foreach ($typed in @($before, $after)) {
        foreach ($field in @('role','pairingState','invalidationReason','receipts','sourceBoundary','effectiveBoundary','targetBoundary')) {
            if ($typed.PSObject.Properties.Name -notcontains $field) { return $false }
        }
        foreach ($scope in @($typed.sourceBoundary, $typed.effectiveBoundary, $typed.targetBoundary)) {
            if ($null -eq $scope -or -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $scope)) { return $false }
        }
    }
    if ($before.role -cne 'yield_current' -or $after.role -cne 'yield_terminal' -or
        $before.invalidationReason -cne '(none)' -or $after.pairingState -cne 'Invalidated' -or
        [string]::IsNullOrWhiteSpace([string]$after.invalidationReason) -or $after.invalidationReason -ceq '(none)' -or
        ($before | Select-Object * -ExcludeProperty role,pairingState,invalidationReason,receipts | ConvertTo-Json -Depth 64 -Compress) -cne
        ($after | Select-Object * -ExcludeProperty role,pairingState,invalidationReason,receipts | ConvertTo-Json -Depth 64 -Compress)) { return $false }
    $oldReceipts = @($before.receipts); $newReceipts = @($after.receipts)
    if ($oldReceipts.Count -eq 0 -or $newReceipts.Count -ne ($oldReceipts.Count + 1) -or
        @($oldReceipts | Where-Object stage -CEQ 'invalidation').Count -ne 0) { return $false }
    for ($index = 0; $index -lt $oldReceipts.Count; $index++) {
        if (($oldReceipts[$index] | ConvertTo-Json -Depth 16 -Compress) -cne
            ($newReceipts[$index] | ConvertTo-Json -Depth 16 -Compress)) { return $false }
    }
    $receipt = $newReceipts[-1]
    foreach ($field in @('stage','commandIdentity','commandGeneration','commandFingerprint','receiptIdentity',
            'receiptGeneration','receiptStatus','receiptReason','receiptFingerprint','receiptValidated')) {
        if ($null -eq $receipt -or $receipt.PSObject.Properties.Name -notcontains $field -or
            [string]::IsNullOrWhiteSpace([string]$receipt.$field) -or [string]$receipt.$field -ceq '(none)') { return $false }
    }
    if ($receipt.stage -cne 'invalidation' -or $receipt.receiptStatus -cne 'Invalidated' -or
        $receipt.receiptValidated -isnot [bool] -or -not $receipt.receiptValidated -or
        $receipt.receiptReason -cne $after.invalidationReason -or
        [long]$receipt.commandGeneration -ne [long]$Terminal.operationGeneration -or
        [long]$receipt.receiptGeneration -ne [long]$Terminal.operationGeneration) { return $false }
    foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity',
            'receiptGeneration','receiptStatus','receiptFingerprint','receiptValidated')) {
        if ([string]$Terminal.$field -cne [string]$receipt.$field) { return $false }
    }
    return $true
}

function Resolve-FileTransferCoordinatorFailedCheckpointYieldCandidates {
    param($AggregateIndex, $Candidates, $InvalidBoundaries, [object[]]$Effects, [object[]]$AllBoundaries,
        $SupersessionCandidates = @{}, $RejectedSupersessions = @{},
        [object[]]$PendingPeerProofBindings = @(), $SupersessionPeerProofOrigins = @{},
        $ValidatedDisplacedTerminals = @{})

    # Index effects and rejected observations once, then reduce the Coordinator's
    # current/LastTerminalOperation slots in generation order. Retained terminal
    # scopes are immutable history, not the aggregate's currently installed scope.
    $forbiddenEffects = @{}; $invalidGenerations = @{}; $resolved = @{}; $effectIndex = @{}
    foreach ($effect in $Effects) {
        if ($effect.Family -ceq 'fallback_tail_yield') {
            $forbiddenEffects["$($effect.CoordinatorInstanceId)|$($effect.TransitionId)"] = $true
            $effectKey = "$($effect.CoordinatorInstanceId)|$($effect.TransitionId)|$($effect.OperationKey)"
            if (-not $effectIndex.ContainsKey($effectKey)) { $effectIndex[$effectKey] = [Collections.Generic.List[object]]::new() }
            $effectIndex[$effectKey].Add($effect)
        }
    }
    $neutralProofObservations = @{}
    foreach ($boundary in $AllBoundaries) {
        if ($InvalidBoundaries.ContainsKey([string]$boundary.boundaryId)) {
            $invalidGenerations["$($boundary.coordinatorInstanceId)|$($boundary.writer)|$($boundary.beforeStateGeneration)"] = $true
        }
        if ([long]$boundary.beforeStateGeneration -eq [long](Get-FileTransferCoordinatorSnapshotObjectProperty $boundary 'afterStateGeneration' (-1)) -and
            @($boundary.operations | Where-Object { $_.family -ceq 'fallback_tail_yield' -and
                    $_.operationRole -in @('peer_proof_pending_initial','yield_current','yield_terminal') }).Count -gt 0) {
            $key = "$($boundary.coordinatorInstanceId)|$($boundary.writer)|$($boundary.beforeStateGeneration)"
            if (-not $neutralProofObservations.ContainsKey($key)) { $neutralProofObservations[$key] = [Collections.Generic.List[object]]::new() }
            $neutralProofObservations[$key].Add($boundary)
        }
    }
    $pendingOrigins = @{}
    foreach ($binding in $PendingPeerProofBindings) {
        $key = "$($binding.CoordinatorInstanceId)|$($binding.Writer)|$($binding.TransitionId)|$($binding.OperationKey)"
        if (-not $pendingOrigins.ContainsKey($key)) { $pendingOrigins[$key] = [Collections.Generic.List[object]]::new() }
        $pendingOrigins[$key].Add($binding)
    }
    foreach ($streamKey in $AggregateIndex.Keys) {
        $stream = $AggregateIndex[$streamKey]
        $slots = @{}; $seen = @{}; $previous = $null
        $pendingProof = $null; $seenPendingProofs = @{}
        foreach ($generation in @($stream.Mutations.Keys | Sort-Object)) {
            $entries = $stream.Mutations[$generation]
            if ($entries.Count -ne 1) {
                foreach ($conflict in $entries) {
                    $id = [string]$conflict.Boundary.boundaryId
                    $InvalidBoundaries[$id] = $true
                    [void]$stream.Boundaries.Remove($id)
                }
                [void]$stream.Mutations.Remove($generation)
                $slots = @{}; $previous = $null; $pendingProof = $null
                continue
            }
            $entry = $entries[0]; $boundary = $entry.Boundary; $id = [string]$boundary.boundaryId
            $valid = -not $InvalidBoundaries.ContainsKey($id) -and
                -not $invalidGenerations.ContainsKey("$streamKey|$generation") -and
                [long]$boundary.afterStateGeneration -eq ([long]$generation + 1L) -and
                [string]$boundary.beforeStateHash -cne [string]$boundary.afterStateHash -and
                [long]$entry.BeforeBoundary.terminalGeneration -eq 0 -and
                [long]$entry.AfterBoundary.terminalGeneration -eq 0 -and
                [string]$entry.BeforeBoundary.dataSessionIdentity -ceq [string]$entry.AfterBoundary.dataSessionIdentity -and
                [long]$entry.BeforeBoundary.dataSessionGeneration -eq [long]$entry.AfterBoundary.dataSessionGeneration
            if ($null -ne $previous) {
                $valid = $valid -and [long]$previous.Boundary.afterStateGeneration -eq [long]$generation -and
                    [string]$previous.Boundary.afterStateHash -ceq [string]$boundary.beforeStateHash -and
                    [string]$previous.SessionId -ceq [string]$entry.SessionId -and
                    [string]$previous.TransferId -ceq [string]$entry.TransferId -and
                    [string]$previous.Direction -ceq [string]$entry.Direction -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $previous.AfterBoundary $entry.BeforeBoundary)
            }
            if (-not $valid) { $slots = @{}; $pendingProof = $null }
            # State-neutral carried descriptors cannot mutate and restore an
            # origin between mutations. Attempted/rejected observations are
            # separate diagnostic roles and do not authorize or poison a carry.
            foreach ($observation in @($neutralProofObservations["$streamKey|$generation"] | Where-Object { $null -ne $_ })) {
                $observationAnchorValid = $observation.beforeStateHash -ceq $boundary.beforeStateHash -and
                    $observation.afterStateHash -ceq $boundary.beforeStateHash
                $originObservationValid = $observationAnchorValid
                foreach ($operation in @($observation.operations | Where-Object {
                        $_.family -ceq 'fallback_tail_yield' -and $_.operationRole -in @('peer_proof_pending_initial','yield_current','yield_terminal') })) {
                    $subject = if ($operation.operationRole -ceq 'peer_proof_pending_initial') { $pendingProof }
                        elseif ($slots.ContainsKey([string]$operation.operationRole)) { $slots[[string]$operation.operationRole] } else { $null }
                    $identity = $operation | Select-Object * -ExcludeProperty beforeState,afterState,stateApplied | ConvertTo-Json -Depth 64 -Compress
                    $subjectObservationValid = $observationAnchorValid -and $null -ne $subject -and
                        $operation.beforeState -ceq $operation.afterState -and -not $operation.stateApplied
                    if ($null -ne $subject) {
                        $subjectObservationValid = $subjectObservationValid -and $identity -ceq $subject.Identity -and
                            $operation.beforeState -ceq $subject.Operation.afterState
                    }
                    if (-not $subjectObservationValid -and $operation.operationRole -ceq 'yield_terminal' -and
                        $slots.ContainsKey('yield_terminal')) {
                        $slots.yield_terminal.OriginProven = $false
                    }
                    $originObservationValid = $originObservationValid -and $subjectObservationValid
                }
                if (-not $originObservationValid) {
                    $pendingProof = $null
                    if ($slots.ContainsKey('yield_current')) { $slots.yield_current.PendingProofOrigin = $null }
                }
            }
            $supersessionProof = $null; $retiredPendingProof = $null; $displacedTerminal = $null
            $pendingNext = $pendingProof
            $pendingOperations = @($boundary.operations | Where-Object {
                    $_.family -ceq 'fallback_tail_yield' -and $_.operationRole -ceq 'peer_proof_pending_initial' -and $_.stateApplied
                })
            if ($pendingOperations.Count -gt 0) {
                $pendingExisting = @($pendingOperations | Where-Object beforeState -CNE 'None')
                $pendingEntering = @($pendingOperations | Where-Object { $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Pending' })
                $pendingValid = $valid -and $pendingExisting.Count -le 1 -and $pendingEntering.Count -le 1 -and
                    $pendingOperations.Count -eq ($pendingExisting.Count + $pendingEntering.Count)
                if ($pendingExisting.Count -eq 1) {
                    $pendingOperation = $pendingExisting[0]
                    $pendingIdentity = $pendingOperation | Select-Object * -ExcludeProperty beforeState,afterState,stateApplied | ConvertTo-Json -Depth 64 -Compress
                    $pendingValid = $pendingValid -and $null -ne $pendingProof -and $pendingOperation.beforeState -ceq 'Pending' -and
                        $pendingOperation.afterState -in @('Pending','None')
                    if ($null -ne $pendingProof) { $pendingValid = $pendingValid -and $pendingIdentity -ceq $pendingProof.Identity }
                    if ($pendingOperation.afterState -ceq 'None') {
                        if ($pendingValid) { $retiredPendingProof = $pendingProof }
                        $pendingNext = $null
                    }
                }
                if ($pendingEntering.Count -eq 1) {
                    $pendingOperation = $pendingEntering[0]
                    $pendingKey = [string]$pendingOperation.operationKey
                    $originKey = "$($boundary.coordinatorInstanceId)|$($boundary.writer)|$id|$pendingKey"
                    $origins = @($pendingOrigins[$originKey] | Where-Object { $null -ne $_ })
                    $pendingValid = $pendingValid -and $null -eq $pendingNext -and -not $seenPendingProofs.ContainsKey($pendingKey) -and $origins.Count -eq 1
                    $seenPendingProofs[$pendingKey] = $true
                    if ($origins.Count -eq 1) {
                        $binding = $origins[0]
                        $pendingValid = $pendingValid -and $binding.SessionId -ceq $entry.SessionId -and $binding.TransferId -ceq $entry.TransferId -and
                            $binding.Direction -ceq $entry.Direction -and [long]$binding.StateGeneration -eq [long]$boundary.afterStateGeneration -and
                            $binding.PeerProofReceipt.receiptReason -ceq 'fallback_tail_peer_proof_retained'
                        if ($pendingValid) {
                            $pendingNext = @{ Binding = $binding; Operation = $pendingOperation; Boundary = $boundary
                                Identity = ($pendingOperation | Select-Object * -ExcludeProperty beforeState,afterState,stateApplied | ConvertTo-Json -Depth 64 -Compress) }
                        }
                    }
                }
                if (-not $pendingValid) { $pendingNext = $null; $retiredPendingProof = $null }
            }
            $operations = @($boundary.operations | Where-Object {
                    $_.family -ceq 'fallback_tail_yield' -and
                    $_.operationRole -in @('yield_current', 'yield_terminal') -and $_.stateApplied
                })
            $next = @{} + $slots
            foreach ($role in @('yield_current', 'yield_terminal')) {
                $atSlot = @($operations | Where-Object operationRole -CEQ $role)
                if ($atSlot.Count -eq 0) { continue }
                $entering = @($atSlot | Where-Object { $_.beforeState -ceq 'None' -and $_.afterState -cne 'None' })
                $existing = @($atSlot | Where-Object { $_.beforeState -cne 'None' })
                $valid = $valid -and $entering.Count -le 1 -and $existing.Count -le 1 -and
                    $atSlot.Count -eq ($entering.Count + $existing.Count)
                if ($existing.Count -eq 1) {
                    $operation = $existing[0]
                    $valid = $valid -and $slots.ContainsKey($role)
                    if ($slots.ContainsKey($role)) {
                        $prior = $slots[$role].Operation
                        $valid = $valid -and $operation.operationKey -ceq $prior.operationKey -and
                            $operation.beforeState -ceq $prior.afterState
                        # A terminal cannot be overwritten and later restored;
                        # every retained receipt and historical scope stays exact.
                        # Current retirement must likewise match the owning state.
                        if ($role -ceq 'yield_terminal' -or $operation.afterState -ceq 'None') {
                            $identity = $operation | Select-Object * -ExcludeProperty beforeState,afterState,stateApplied | ConvertTo-Json -Depth 64 -Compress
                            $valid = $valid -and $identity -ceq $slots[$role].Identity
                        }
                    }
                    if ($operation.afterState -ceq 'None') { [void]$next.Remove($role) }
                    else {
                        $identity = $operation | Select-Object * -ExcludeProperty beforeState,afterState,stateApplied | ConvertTo-Json -Depth 64 -Compress
                        $next[$role] = @{ Operation = $operation
                            PendingProofOrigin = $(if ($slots.ContainsKey($role) -and $slots[$role].Identity -ceq $identity) { $slots[$role].PendingProofOrigin } else { $null })
                            OriginProven = ($slots.ContainsKey($role) -and $slots[$role].OriginProven)
                            SupersessionIdentity = $(if ($slots.ContainsKey($role) -and
                                    ($slots[$role].SupersessionIdentity -ceq $identity -or
                                     ($role -ceq 'yield_current' -and $operation.operationKind -ceq 'Failed' -and
                                      $null -ne $slots[$role].SupersessionIdentity -and
                                      $slots[$role].SupersessionIdentity -ceq $slots[$role].Identity -and
                                      $slots[$role].Operation.afterState -ceq 'YieldIssued' -and
                                      $operation.afterState -ceq 'PeerProofObserved' -and
                                      $boundary.decisionKind -ceq 'ApplyFallbackTailPeerProofReceipt' -and
                                      $boundary.decisionStatus -ceq 'Accepted'))) { $identity } else { $null })
                            CheckpointIdentity = $(if ($slots.ContainsKey($role) -and
                                    $slots[$role].CheckpointIdentity -ceq $identity) { $identity } else { $null })
                            Identity = $identity }
                    }
                }
                if ($entering.Count -eq 1) {
                    $operation = $entering[0]; $key = [string]$operation.operationKey
                    $valid = $valid -and -not $next.ContainsKey($role) -and -not $seen.ContainsKey($key)
                    if ($role -ceq 'yield_terminal') {
                        $currentRetirement = @($operations | Where-Object {
                                $_.operationRole -ceq 'yield_current' -and $_.afterState -ceq 'None' -and
                                $_.authorityId -ceq $operation.authorityId -and
                                [long]$_.operationGeneration -eq [long]$operation.operationGeneration -and
                                $_.operationKind -ceq $operation.operationKind
                            })
                        $valid = $valid -and $currentRetirement.Count -eq 1
                        if ($currentRetirement.Count -eq 1 -and $operation.afterState -ceq 'Invalidated') {
                            $valid = $valid -and (Test-FileTransferCoordinatorCheckpointYieldTerminalBirth `
                                -Current $currentRetirement[0] -Terminal $operation)
                        }
                    }
                    $seen[$key] = $true
                    $identity = $operation | Select-Object * -ExcludeProperty beforeState,afterState,stateApplied | ConvertTo-Json -Depth 64 -Compress
                    $next[$role] = @{ Operation = $operation
                        PendingProofOrigin = $null
                        Identity = $identity
                        OriginProven = $(if ($role -ceq 'yield_current') {
                                $operation.operationKind -ceq 'Quiescent' -and
                                $operation.afterState -in @('YieldIssued', 'PeerProofObserved') -and
                                $boundary.decisionKind -ceq 'FallbackTailQuiescent' -and $boundary.decisionStatus -ceq 'Accepted'
                            } else { $slots.ContainsKey('yield_current') -and $slots.yield_current.OriginProven })
                        SupersessionIdentity = $(if ($role -ceq 'yield_current' -and $operation.operationKind -ceq 'Failed' -and
                                $operation.afterState -in @('YieldIssued', 'PeerProofObserved') -and
                                $boundary.decisionKind -ceq 'FallbackTailFailed' -and $boundary.decisionStatus -ceq 'Accepted') {
                                $identity
                            } else { $null })
                        CheckpointIdentity = $(if ($role -ceq 'yield_current' -and $operation.operationKind -ceq 'Failed' -and
                                $operation.afterState -ceq 'PeerProofObserved' -and $boundary.decisionKind -ceq 'FallbackTailFailed' -and
                                $boundary.decisionStatus -ceq 'Accepted') { $identity } else { $null }) }
                }
            }
            if ($valid -and $null -ne $retiredPendingProof -and $next.ContainsKey('yield_current') -and
                $boundary.decisionKind -ceq 'FallbackTailFailed' -and $boundary.decisionStatus -ceq 'Accepted') {
                $current = $next.yield_current.Operation
                $envelope = [pscustomobject]@{
                    decisionKind=$boundary.decisionKind; decisionStatus=$boundary.decisionStatus; decisionReason=$boundary.decisionReason
                    aggregate=[pscustomobject]@{
                        beforeStateGeneration=$boundary.beforeStateGeneration; afterStateGeneration=$boundary.afterStateGeneration
                        beforeStateFingerprint=$boundary.beforeStateHash; afterStateFingerprint=$boundary.afterStateHash
                        beforeBoundary=$entry.BeforeBoundary; afterBoundary=$entry.AfterBoundary; operations=$boundary.operations
                        changedFamilies=@(Get-FileTransferCoordinatorSnapshotObjectProperty $boundary 'families' @())
                    }
                }
                $originMatches = Test-FileTransferCoordinatorSnapshotFallbackTailFailedEnvelope $envelope
                $binding = $retiredPendingProof.Binding; $typed = $current.fallbackTailYield
                foreach ($field in @('ownerKey','pairKey','peerProofAuthorityId','peerProofYieldGeneration','peerProofIdentity','peerProofFingerprint')) {
                    if ([string]$typed.$field -cne [string]$binding.$field) { $originMatches = $false }
                }
                foreach ($scope in @('sourceBoundary','effectiveBoundary','targetBoundary')) {
                    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $typed.$scope $binding.$scope)) { $originMatches = $false }
                }
                $peer = @($typed.receipts | Where-Object stage -CEQ 'peer_proof')
                $originMatches = $originMatches -and $peer.Count -eq 1
                if ($peer.Count -eq 1) {
                    $originMatches = $originMatches -and $peer[0].receiptReason -ceq 'fallback_tail_peer_proof_observed' -and
                        (Test-FileTransferCoordinatorSnapshotFallbackTailReceiptEqual $peer[0] $binding.PeerProofReceipt)
                }
                if ($originMatches) { $next.yield_current.PendingProofOrigin = $retiredPendingProof }
            }
            if ($Candidates.ContainsKey($id)) {
                $resolved[$id] = $true
                $envelope = [pscustomobject]@{
                    decisionKind = $boundary.decisionKind; decisionStatus = $boundary.decisionStatus; decisionReason = $boundary.decisionReason
                    aggregate = [pscustomobject]@{
                        beforeStateGeneration = $boundary.beforeStateGeneration; afterStateGeneration = $boundary.afterStateGeneration
                        beforeStateFingerprint = $boundary.beforeStateHash; afterStateFingerprint = $boundary.afterStateHash
                        beforeBoundary = $entry.BeforeBoundary; afterBoundary = $entry.AfterBoundary; operations = $boundary.operations
                    }
                }
                $valid = $valid -and (Test-FileTransferCoordinatorSnapshotFailedCheckpointYieldCandidate $envelope)
                $valid = $valid -and $slots.ContainsKey('yield_current')
                if ($slots.ContainsKey('yield_current')) {
                    $valid = $valid -and $null -ne $slots.yield_current.CheckpointIdentity -and
                        $slots.yield_current.Identity -ceq $slots.yield_current.CheckpointIdentity
                }
                $forbidden = $forbiddenEffects.ContainsKey("$($boundary.coordinatorInstanceId)|$id")
                if (-not $valid -or $forbidden) {
                    $variant = if ($forbidden) { 'checkpoint_failed_peer_first_invalidation_terminal_rollover_forbidden_effect' }
                        else { 'checkpoint_failed_peer_first_invalidation_terminal_rollover_history_unproven' }
                    $InvalidBoundaries[$id] = $true
                    [void]$stream.Boundaries.Remove($id); [void]$stream.Mutations.Remove($generation)
                    "snapshot_operation_catalog_mismatch|$variant`:$($boundary.evidenceLineIndex)"
                    $valid = $false
                }
            }
            if ($SupersessionCandidates.ContainsKey($id)) {
                $resolved[$id] = $true
                if ($InvalidBoundaries.ContainsKey($id)) {
                    # Keep an already precise receipt/effect failure primary;
                    # this rejected boundary cannot contribute slot history.
                    [void]$stream.Boundaries.Remove($id); [void]$stream.Mutations.Remove($generation)
                    $valid = $false
                }
                else {
                    $shape = Get-FileTransferCoordinatorSnapshotFallbackTailFailedSupersessionShape -Operations @($boundary.operations)
                    $rolloverValid = $null -ne $shape
                    $forbidden = $false
                    if ($rolloverValid -and $null -eq $shape.DisplacedTerminal) {
                        # Preserve the existing three-operation contract, but it may
                        # not silently overwrite a terminal retained by this stream.
                        $rolloverValid = -not $slots.ContainsKey('yield_terminal')
                    }
                    elseif ($rolloverValid) {
                        $rolloverValid = $valid -and $slots.ContainsKey('yield_terminal') -and $slots.yield_terminal.OriginProven -and
                            $slots.ContainsKey('yield_current') -and $null -ne $slots.yield_current.SupersessionIdentity -and
                            $slots.yield_current.Identity -ceq $slots.yield_current.SupersessionIdentity
                        if ($rolloverValid) {
                            $identity = $shape.DisplacedTerminal | Select-Object * -ExcludeProperty beforeState,afterState,stateApplied |
                                ConvertTo-Json -Depth 64 -Compress
                            $rolloverValid = $identity -ceq $slots.yield_terminal.Identity
                        }
                        $effectKey = "$($boundary.coordinatorInstanceId)|$id|$($shape.QuiescentCurrent.operationKey)"
                        $yieldEffects = @($effectIndex[$effectKey] | Where-Object {
                                $null -ne $_ -and $_.EffectKind -ceq 'recovery_owner_retired_after_yield' -and
                                $_.Writer -ceq $boundary.writer
                            })
                        $rolloverValid = $rolloverValid -and $yieldEffects.Count -eq 1
                        if ($yieldEffects.Count -eq 1) {
                            $rolloverValid = $rolloverValid -and
                                (Test-FileTransferCoordinatorSnapshotExactEffectBinding $boundary $shape.QuiescentCurrent $yieldEffects[0])
                        }
                        $forbidden = $effectIndex.ContainsKey("$($boundary.coordinatorInstanceId)|$id|$($shape.DisplacedTerminal.operationKey)")
                    }
                    if (-not $rolloverValid -or $forbidden) {
                        $variant = if ($forbidden) { 'failed_to_quiescent_terminal_slot_rollover_forbidden_effect' }
                            else { 'failed_to_quiescent_terminal_slot_rollover_history_unproven' }
                        $InvalidBoundaries[$id] = $true
                        $RejectedSupersessions[$id] = $true
                        [void]$stream.Boundaries.Remove($id); [void]$stream.Mutations.Remove($generation)
                        "snapshot_operation_catalog_mismatch|$variant`:$($boundary.evidenceLineIndex)"
                        $valid = $false
                    }
                    elseif ($valid -and $slots.ContainsKey('yield_current') -and $null -ne $slots.yield_current.PendingProofOrigin) {
                        $successorPeer = @($shape.QuiescentCurrent.fallbackTailYield.receipts | Where-Object stage -CEQ 'peer_proof')
                        if ($successorPeer.Count -eq 1 -and $successorPeer[0].receiptReason -ceq 'fallback_tail_peer_proof_observed') {
                            $supersessionProof = @{ Origin = $slots.yield_current.PendingProofOrigin; Boundary = $boundary
                                Signature = (Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofSignature -Operation $shape.QuiescentCurrent) }
                        }
                    }
                    if ($valid -and $rolloverValid -and -not $forbidden -and $null -ne $shape.DisplacedTerminal -and
                        $boundary.decisionKind -ceq 'FallbackTailQuiescent' -and $boundary.decisionStatus -ceq 'Accepted') {
                        $displacedTerminal = $shape.DisplacedTerminal
                    }
                }
            }
            if ($valid) {
                $slots = $next; $pendingProof = $pendingNext
                if ($null -ne $supersessionProof) {
                    $key = Get-FileTransferCoordinatorSnapshotFallbackTailYieldEntryKey `
                        -Entry ([pscustomobject]@{ Boundary=$boundary; Operation=$shape.QuiescentCurrent })
                    $SupersessionPeerProofOrigins[$key] = $supersessionProof
                }
                if ($null -ne $displacedTerminal) {
                    $key = Get-FileTransferCoordinatorSnapshotFallbackTailYieldEntryKey `
                        -Entry ([pscustomobject]@{ Boundary=$boundary; Operation=$displacedTerminal })
                    $ValidatedDisplacedTerminals["$($boundary.coordinatorInstanceId)|$($boundary.writer)|$key"] = $true
                }
            }
            else { $slots = @{}; $pendingProof = $null }
            $previous = $entry
        }
    }
    foreach ($id in $Candidates.Keys) {
        if (-not $resolved.ContainsKey($id)) {
            $InvalidBoundaries[$id] = $true
            "snapshot_operation_catalog_mismatch|checkpoint_failed_peer_first_invalidation_terminal_rollover_history_unproven:$($Candidates[$id].Line)"
        }
    }
    foreach ($id in $SupersessionCandidates.Keys) {
        if (-not $resolved.ContainsKey($id)) {
            $InvalidBoundaries[$id] = $true
            $RejectedSupersessions[$id] = $true
            "snapshot_operation_catalog_mismatch|failed_to_quiescent_terminal_slot_rollover_history_unproven:$($SupersessionCandidates[$id].Line)"
        }
    }
}

function Resolve-FileTransferCoordinatorCheckpointStalePeerProofRetirements {
    param($AggregateIndex, $Candidates, $InvalidBoundaries, [object[]]$Effects)

    # Reduce the pending slot once per writer. Checkpoint history is authority
    # evidence; physical record order and the aggregate's cleared lease are not.
    $effectIndex = @{}
    foreach ($effect in $Effects) {
        $key = "$($effect.CoordinatorInstanceId)|$($effect.TransitionId)|$($effect.OperationKey)"
        if (-not $effectIndex.ContainsKey($key)) { $effectIndex[$key] = [Collections.Generic.List[object]]::new() }
        $effectIndex[$key].Add($effect)
    }
    $resolved = @{}
    foreach ($stream in $AggregateIndex.Values) {
        $slot = $null; $previous = $null; $seen = @{}; $checkpoints = @{}
        foreach ($generation in @($stream.Mutations.Keys | Sort-Object)) {
            $entries = $stream.Mutations[$generation]
            if ($entries.Count -ne 1) { $slot = $null; $previous = $null; $checkpoints = @{}; continue }
            $entry = $entries[0]; $boundary = $entry.Boundary; $id = [string]$boundary.boundaryId
            $valid = -not $InvalidBoundaries.ContainsKey($id) -and
                [long]$boundary.afterStateGeneration -eq ([long]$generation + 1L) -and
                [string]$boundary.beforeStateHash -cne [string]$boundary.afterStateHash
            if ($null -ne $previous) {
                $valid = $valid -and [long]$previous.Boundary.afterStateGeneration -eq [long]$generation -and
                    [string]$previous.Boundary.afterStateHash -ceq [string]$boundary.beforeStateHash -and
                    [string]$previous.SessionId -ceq [string]$entry.SessionId -and
                    [string]$previous.TransferId -ceq [string]$entry.TransferId -and
                    [string]$previous.Direction -ceq [string]$entry.Direction -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $previous.AfterBoundary $entry.BeforeBoundary)
            }
            if (-not $valid -or [long]$entry.AfterBoundary.terminalGeneration -ne 0 -or
                -not (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint $entry.BeforeBoundary $entry.AfterBoundary)) {
                $slot = $null; $checkpoints = @{}
            }
            if ($null -ne $slot -and -not
                (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint $slot.Entry.AfterBoundary $entry.AfterBoundary)) {
                $slot = $null; $checkpoints = @{}
            }
            if ($null -ne $slot -and $boundary.decisionKind -in @('FallbackCheckpointRequested', 'FallbackCheckpointAccepted')) {
                $checkpointEnvelope = [pscustomobject]@{
                    decisionKind = $boundary.decisionKind; decisionStatus = $boundary.decisionStatus; decisionReason = $boundary.decisionReason
                    aggregate = [pscustomobject]@{
                        beforeStateGeneration = $boundary.beforeStateGeneration; afterStateGeneration = $boundary.afterStateGeneration
                        beforeStateFingerprint = $boundary.beforeStateHash; afterStateFingerprint = $boundary.afterStateHash
                        beforeBoundary = $entry.BeforeBoundary; afterBoundary = $entry.AfterBoundary
                    }
                }
                if (-not (Test-FileTransferCoordinatorSnapshotFallbackCheckpointScopeMutation $checkpointEnvelope)) {
                    $slot = $null; $checkpoints = @{}
                }
                elseif ($boundary.decisionKind -ceq 'FallbackCheckpointRequested') {
                    $checkpoint = [long]$entry.AfterBoundary.checkpointGeneration
                    if ($checkpoints.ContainsKey($checkpoint) -or $checkpoint -le $slot.Checkpoint) { $slot = $null; $checkpoints = @{} }
                    else {
                        $slot.Checkpoint = $checkpoint
                        $checkpoints[$checkpoint] = @{ Scope = $entry.AfterBoundary; Accepted = $false }
                    }
                }
                else {
                    $checkpoint = [long]$entry.BeforeBoundary.checkpointGeneration
                    if (-not $checkpoints.ContainsKey($checkpoint) -or $checkpoints[$checkpoint].Accepted -or
                        -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $checkpoints[$checkpoint].Scope $entry.BeforeBoundary)) {
                        $slot = $null; $checkpoints = @{}
                    }
                    else { $checkpoints[$checkpoint].Accepted = $true }
                }
            }
            $pending = @($boundary.operations | Where-Object {
                $_.family -ceq 'fallback_tail_yield' -and $_.operationRole -ceq 'peer_proof_pending_initial'
            })
            if ($pending.Count -gt 1) { $slot = $null; $checkpoints = @{} }
            if ($pending.Count -eq 1) {
                $operation = $pending[0]
                $identity = $operation | Select-Object * -ExcludeProperty beforeState,afterState,stateApplied | ConvertTo-Json -Depth 64 -Compress
                if ($operation.beforeState -ceq 'None' -and $operation.afterState -ceq 'Pending') {
                    $key = [string]$operation.operationKey
                    if ($valid -and $null -eq $slot -and -not $seen.ContainsKey($key)) {
                        $slot = @{ Entry = $entry; Operation = $operation; Identity = $identity
                            Checkpoint = [long]$operation.fallbackTailYield.sourceBoundary.checkpointGeneration }
                        $checkpoints = @{}
                    }
                    else { $slot = $null; $checkpoints = @{} }
                    $seen[$key] = $true
                }
                elseif ($operation.beforeState -cne 'Pending' -or $operation.afterState -notin @('Pending', 'None') -or
                    $null -eq $slot -or $slot.Identity -cne $identity) { $slot = $null; $checkpoints = @{} }
            }
            if ($Candidates.ContainsKey($id)) {
                $resolved[$id] = $true
                $candidate = $Candidates[$id]; $yield = $candidate.Yield
                $checkpoint = [long]$yield.fallbackTailYield.sourceBoundary.checkpointGeneration
                $valid = $valid -and $null -ne $slot -and $pending.Count -eq 1 -and
                    $checkpoint -eq $slot.Checkpoint -and $checkpoints.ContainsKey($checkpoint) -and $checkpoints[$checkpoint].Accepted
                if ($valid) {
                    foreach ($field in @('route', 'protocolVersion', 'liveRouteEpoch', 'routePublicationGeneration',
                            'routeCommitGeneration', 'runtimeGeneration', 'terminalGeneration', 'legGeneration',
                            'dataSessionIdentity', 'dataSessionGeneration')) {
                        $valid = $valid -and [string]$checkpoints[$checkpoint].Scope.$field -ceq
                            [string]$yield.fallbackTailYield.sourceBoundary.$field
                    }
                }
                if ($valid) {
                    $originalKey = "$($boundary.coordinatorInstanceId)|$($slot.Entry.Boundary.boundaryId)|$($slot.Operation.operationKey)"
                    $yieldKey = "$($boundary.coordinatorInstanceId)|$id|$($yield.operationKey)"
                    $originalEffects = @($effectIndex[$originalKey] | Where-Object { $null -ne $_ -and $_.EffectKind -ceq 'peer_proof_observed' })
                    $yieldEffects = @($effectIndex[$yieldKey] | Where-Object { $null -ne $_ -and $_.EffectKind -ceq 'recovery_owner_retired_after_yield' })
                    $copied = @($effectIndex[$yieldKey] | Where-Object { $null -ne $_ -and $_.EffectKind -ceq 'peer_proof_observed' })
                    $retirementKey = "$($boundary.coordinatorInstanceId)|$id|$($pending[0].operationKey)"
                    $copied += @($effectIndex[$retirementKey] | Where-Object { $null -ne $_ -and $_.EffectKind -ceq 'peer_proof_observed' })
                    $valid = $originalEffects.Count -eq 1 -and $yieldEffects.Count -eq 1 -and $copied.Count -eq 0 -and
                        (Test-FileTransferCoordinatorSnapshotExactEffectBinding $slot.Entry.Boundary $slot.Operation $originalEffects[0]) -and
                        (Test-FileTransferCoordinatorSnapshotExactEffectBinding $boundary $yield $yieldEffects[0])
                }
                if (-not $valid) {
                    $InvalidBoundaries[$id] = $true
                    [void]$stream.Boundaries.Remove($id); [void]$stream.Mutations.Remove($generation)
                    "snapshot_operation_catalog_mismatch|checkpoint_stale_pending_peer_proof_retirement_rejected_by_quiescent_shape_catalog:$($boundary.evidenceLineIndex)"
                    $slot = $null; $checkpoints = @{}
                }
            }
            if ($pending.Count -eq 1 -and $pending[0].afterState -ceq 'None') { $slot = $null; $checkpoints = @{} }
            $previous = $entry
        }
    }
    foreach ($id in $Candidates.Keys) {
        if (-not $resolved.ContainsKey($id)) {
            "snapshot_operation_catalog_mismatch|checkpoint_stale_pending_peer_proof_retirement_rejected_by_quiescent_shape_catalog:$($Candidates[$id].Line)"
        }
    }
}

function Resolve-FileTransferCoordinatorCheckpointPeerProofSuccessors {
    param(
        $AggregateIndex,
        $Candidates,
        $InvalidBoundaries,
        [object[]]$OperationHistory,
        [object[]]$Effects)

    $operationIndex = @{}
    $operationBoundaryIndex = @{}
    foreach ($entry in $OperationHistory) {
        $key = "$($entry.Boundary.coordinatorInstanceId)|$($entry.Boundary.writer)|$($entry.Operation.operationKey)"
        if (-not $operationIndex.ContainsKey($key)) {
            $operationIndex[$key] = [Collections.Generic.List[object]]::new()
        }
        $operationIndex[$key].Add($entry)
        $boundaryKey = "$($entry.Boundary.coordinatorInstanceId)|$($entry.Boundary.writer)|$($entry.Boundary.boundaryId)|$($entry.Operation.operationKey)"
        if (-not $operationBoundaryIndex.ContainsKey($boundaryKey)) {
            $operationBoundaryIndex[$boundaryKey] = [Collections.Generic.List[object]]::new()
        }
        $operationBoundaryIndex[$boundaryKey].Add($entry)
    }
    $effectIndex = @{}
    foreach ($effect in $Effects) {
        $key = "$($effect.CoordinatorInstanceId)|$($effect.Writer)|$($effect.TransitionId)|$($effect.OperationKey)"
        if (-not $effectIndex.ContainsKey($key)) {
            $effectIndex[$key] = [Collections.Generic.List[object]]::new()
        }
        $effectIndex[$key].Add($effect)
    }

    $candidateEntries = [Collections.Generic.List[object]]::new()
    foreach ($candidateId in @($Candidates.Keys)) {
        $candidate = $Candidates[$candidateId]
        $boundaryKey = "$($candidate.CoordinatorInstanceId)|$($candidate.Writer)|$candidateId|$($candidate.Entered.operationKey)"
        $candidateEntry = @(if ($operationBoundaryIndex.ContainsKey($boundaryKey)) {
            @($operationBoundaryIndex[$boundaryKey])
        }
        else { @() })
        $valid = $candidateEntry.Count -eq 1
        if (-not $valid) {
            "snapshot_operation_catalog_mismatch|current_checkpoint_peer_proof_successor_history_invalid:$($candidate.Line)"
            continue
        }
        [void]$candidateEntries.Add([pscustomobject]@{
                CandidateId = [string]$candidateId
                Candidate = $candidate
                Entry = $candidateEntry[0]
            })
    }

    foreach ($candidateEntry in @($candidateEntries | Sort-Object `
                @{ Expression = { [string]$_.Entry.Boundary.coordinatorInstanceId } }, `
                @{ Expression = { [string]$_.Entry.Boundary.writer } }, `
                @{ Expression = { [long]$_.Entry.Boundary.beforeStateGeneration } }, `
                @{ Expression = { [long]$_.Entry.Boundary.decisionUtcMilliseconds } }, `
                @{ Expression = { [string]$_.CandidateId } })) {
        $candidateId = [string]$candidateEntry.CandidateId
        $candidate = $candidateEntry.Candidate
        $entry = $candidateEntry.Entry
        $boundary = $entry.Boundary
        $valid = $true
        $streamKey = "$($boundary.coordinatorInstanceId)|$($boundary.writer)"
        $stream = if ($AggregateIndex.ContainsKey($streamKey)) {
            $AggregateIndex[$streamKey]
        }
        else { $null }
        $retiredKey = "$($boundary.coordinatorInstanceId)|$($boundary.writer)|$($candidate.Retired.operationKey)"
        $enteredKey = "$($boundary.coordinatorInstanceId)|$($boundary.writer)|$($candidate.Entered.operationKey)"
        $origins = @(if ($operationIndex.ContainsKey($retiredKey)) {
            @($operationIndex[$retiredKey] | Where-Object {
                    [string]$_.Operation.beforeState -ceq 'None' -and
                    [string]$_.Operation.afterState -ceq 'Pending' -and
                    [long]$_.Boundary.afterStateGeneration -le
                        [long]$boundary.beforeStateGeneration
                })
        }
        else { @() })
        $preexistingEntered = @(if ($operationIndex.ContainsKey($enteredKey)) {
            @($operationIndex[$enteredKey] | Where-Object {
                    [string]$_.Boundary.boundaryId -cne [string]$candidateId -and
                    [long]$_.Boundary.afterStateGeneration -le
                        [long]$boundary.beforeStateGeneration
                })
        }
        else { @() })
        $valid = $valid -and $null -ne $stream -and $origins.Count -eq 1 -and
            $preexistingEntered.Count -eq 0 -and
            (Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofSignature `
                $origins[0].Operation) -ceq
            (Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofSignature `
                $candidate.Retired)

        $requestGeneration = -1L
        $acceptGeneration = -1L
        if ($valid) {
            $from = [long]$origins[0].Boundary.afterStateGeneration
            $to = [long]$boundary.beforeStateGeneration
            $previousHash = [string]$origins[0].Boundary.afterStateHash
            for ($generation = $from; $generation -lt $to; $generation++) {
                $items = @(if ($stream.Mutations.ContainsKey($generation)) {
                    @($stream.Mutations[$generation])
                }
                else { @() })
                if ($items.Count -ne 1 -or
                    $InvalidBoundaries.ContainsKey(
                        [string]$items[0].Boundary.boundaryId) -or
                    [string]$items[0].Boundary.beforeStateHash -cne $previousHash) {
                    $valid = $false
                    break
                }
                $current = $items[0]
                $previousHash = [string]$current.Boundary.afterStateHash
                $checkpoint = [long]$candidate.Entered.fallbackTailYield.sourceBoundary.checkpointGeneration
                if ([string]$current.Boundary.decisionKind -ceq
                        'FallbackCheckpointRequested' -and
                    [long]$current.AfterBoundary.checkpointGeneration -eq $checkpoint) {
                    $requestGeneration = $generation
                }
                elseif ([string]$current.Boundary.decisionKind -ceq
                        'FallbackCheckpointAccepted' -and
                    [long]$current.BeforeBoundary.checkpointGeneration -eq $checkpoint) {
                    $acceptGeneration = $generation
                }
            }
            $valid = $valid -and $requestGeneration -ge $from -and
                $acceptGeneration -gt $requestGeneration -and
                $acceptGeneration -lt $to -and
                $previousHash -ceq [string]$boundary.beforeStateHash
        }

        if ($valid) {
            $originEffectKey = "$($boundary.coordinatorInstanceId)|$($origins[0].Boundary.writer)|$($origins[0].Boundary.boundaryId)|$($candidate.Retired.operationKey)"
            $retiredEffectKey = "$($boundary.coordinatorInstanceId)|$($boundary.writer)|$candidateId|$($candidate.Retired.operationKey)"
            $enteredEffectKey = "$($boundary.coordinatorInstanceId)|$($boundary.writer)|$candidateId|$($candidate.Entered.operationKey)"
            $originEffects = @($effectIndex[$originEffectKey] | Where-Object {
                    $null -ne $_ -and [string]$_.EffectKind -ceq 'peer_proof_observed'
                })
            $retiredEffects = @($effectIndex[$retiredEffectKey] | Where-Object {
                    $null -ne $_ -and [string]$_.EffectKind -ceq 'peer_proof_observed'
                })
            $enteredEffects = @($effectIndex[$enteredEffectKey] | Where-Object {
                    $null -ne $_ -and [string]$_.EffectKind -ceq 'peer_proof_observed'
                })
            $valid = $originEffects.Count -eq 1 -and
                $retiredEffects.Count -eq 0 -and $enteredEffects.Count -eq 1 -and
                (Test-FileTransferCoordinatorSnapshotExactEffectBinding `
                    $origins[0].Boundary $origins[0].Operation $originEffects[0]) -and
                (Test-FileTransferCoordinatorSnapshotExactEffectBinding `
                    $boundary $candidate.Entered $enteredEffects[0])
        }

        if (-not $valid) {
            $InvalidBoundaries[[string]$candidateId] = $true
            if ($null -ne $stream) {
                [void]$stream.Boundaries.Remove([string]$candidateId)
                [void]$stream.Mutations.Remove(
                    [long]$boundary.beforeStateGeneration)
            }
            "snapshot_operation_catalog_mismatch|current_checkpoint_peer_proof_successor_history_invalid:$($candidate.Line)"
        }
    }
}

function Resolve-FileTransferCoordinatorConsumedReleaseHistory {
    param($AggregateIndex, $InvalidBoundaries)

    foreach ($stream in $AggregateIndex.Values) {
        $reservations = @{}; $consumedReceipts = @{}; $seen = @{}; $previous = $null
        foreach ($generation in @($stream.Mutations.Keys | Sort-Object)) {
            $entries = $stream.Mutations[$generation]
            if ($entries.Count -ne 1) { $reservations.Clear(); $previous = $null; continue }
            $entry = $entries[0]; $boundary = $entry.Boundary
            if ($InvalidBoundaries.ContainsKey([string]$boundary.boundaryId) -or
                ($null -ne $previous -and ($previous.afterStateGeneration -ne $boundary.beforeStateGeneration -or
                    $previous.afterStateHash -cne $boundary.beforeStateHash))) {
                $reservations.Clear(); $previous = $boundary; continue
            }
            $operations = @($boundary.operations)
            $retired = @{}
            foreach ($operation in @($operations | Where-Object operationRole -CEQ 'release_reservation_current')) {
                $key = [string]$operation.operationKey
                $signature = [ordered]@{
                    authority = $operation.authorityId; generation = $operation.operationGeneration
                    command = $operation.commandIdentity; commandGeneration = $operation.commandGeneration
                    fingerprint = $operation.commandFingerprint
                    payload = $operation.fallbackTailYield | Select-Object * -ExcludeProperty pairingState,consumingInstallGeneration
                } | ConvertTo-Json -Depth 64 -Compress
                if ($operation.beforeState -ceq 'None' -and $operation.afterState -ceq 'Reserved' -and -not $seen.ContainsKey($key)) {
                    $reservations[$key] = $signature; $seen[$key] = $true
                }
                elseif ($reservations.ContainsKey($key) -and $reservations[$key] -ceq $signature -and
                    $operation.beforeState -ceq 'Reserved') {
                    if ($operation.afterState -ceq 'None') { $retired[$key] = $operation; $reservations.Remove($key) }
                }
                else { $reservations.Remove($key) }
            }
            foreach ($operation in @($operations | Where-Object {
                    $_.operationRole -ceq 'release_reservation_consumed' -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Consumed'
                })) {
                $typed = $operation.fallbackTailYield
                $key = "fallback_tail_yield|release_reservation_current|$($operation.authorityId)|$($operation.operationGeneration)|PairedReleaseReservation"
                $valid = $retired.ContainsKey($key) -and $typed.yieldKind -ceq 'Quiescent' -and
                    $typed.pairingState -ceq 'ReleaseConsumed' -and $typed.pairedReleaseAuthorityId -ceq $operation.authorityId -and
                    [long]$typed.pairedReleaseGeneration -gt 0 -and [long]$typed.pairingGeneration -gt 0 -and
                    [long]$typed.consumingInstallGeneration -eq [long]$operation.receiptGeneration
                if ($valid) {
                    $prior = $retired[$key]
                    foreach ($field in @('authorityId','operationGeneration','commandIdentity','commandGeneration','commandFingerprint')) {
                        $valid = $valid -and [string]$prior.$field -ceq [string]$operation.$field
                    }
                    foreach ($field in @('ownerKey','pairKey','localProofIdentity','localProofFingerprint','peerProofAuthorityId',
                            'peerProofIdentity','peerProofFingerprint','peerProofYieldGeneration','pairedReleaseAuthorityId',
                            'pairedReleaseGeneration','pairingGeneration','reservationGeneration','reservedRetryAuthorityId')) {
                        $valid = $valid -and [string]$prior.fallbackTailYield.$field -ceq [string]$typed.$field
                    }
                    foreach ($field in @('sourceBoundary','effectiveBoundary','targetBoundary')) {
                        $valid = $valid -and (Test-FileTransferCoordinatorSnapshotBoundaryEqual $prior.fallbackTailYield.$field $typed.$field)
                    }
                }
                $installs = @($operations | Where-Object {
                    $_.family -ceq 'runtime_unlock_retry_lifecycle' -and $_.operationRole -ceq 'attempt_installed' -and
                    $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Installed' -and
                    $_.runtimeUnlockRetry.releaseKind -ceq 'FallbackPairedTailYield' -and
                    $_.runtimeUnlockRetry.releaseAuthorityId -ceq $operation.authorityId -and
                    [long]$_.runtimeUnlockRetry.releaseGeneration -eq [long]$typed.pairedReleaseGeneration -and
                    @($_.runtimeUnlockRetry.receipts | Where-Object {
                        $_.stage -ceq 'install' -and $_.receiptStatus -ceq 'Installed' -and $_.receiptValidated -and
                        $_.receiptFingerprint -ceq $operation.receiptFingerprint -and
                        [long]$_.receiptGeneration -eq [long]$operation.receiptGeneration
                    }).Count -eq 1
                })
                $valid = $valid -and $installs.Count -eq 1
                if ($valid) {
                    # Retry authority has its own checkpoint domain; all other
                    # execution fields must equal the release's effective scope.
                    foreach ($field in @('route','protocolVersion','liveRouteEpoch','routePublicationGeneration',
                            'routeCommitGeneration','runtimeGeneration','terminalGeneration','legGeneration',
                            'transportEpoch','bridgeRecoveryGeneration','dataSessionIdentity','dataSessionGeneration')) {
                        $valid = $valid -and [string]$installs[0].runtimeUnlockRetry.sourceBoundary.$field -ceq
                            [string]$typed.effectiveBoundary.$field
                    }
                }
                $receiptKey = "$($operation.receiptIdentity)|$($operation.receiptGeneration)|$($operation.receiptFingerprint)"
                if ($consumedReceipts.ContainsKey($receiptKey)) { $valid = $false }
                if (-not $valid) {
                    "snapshot_fallback_tail_consumed_release_ownership_invalid|fresh_failed_yield_rebound_unrelated_last_consumed_reservation_and_install_receipt:$($boundary.lineIndex)"
                    $InvalidBoundaries[[string]$boundary.boundaryId] = $true
                }
                else { $consumedReceipts[$receiptKey] = [string]$operation.operationKey }
            }
            if ($entry.BeforeBoundary.dataSessionIdentity -cne $entry.AfterBoundary.dataSessionIdentity -or
                $entry.BeforeBoundary.dataSessionGeneration -ne $entry.AfterBoundary.dataSessionGeneration -or
                [long]$entry.AfterBoundary.terminalGeneration -gt 0) { $reservations.Clear() }
            $previous = $boundary
        }
    }
}

function Resolve-FileTransferCoordinatorAppliedScopeInvalidationHistory {
    param($History, $AggregateIndex, [object[]]$Effects)

    $effectIndex = @{}
    foreach ($effect in $Effects) {
        if ($effect.EffectKind -cne 'scope_invalidation_applied') { continue }
        $key = "$($effect.CoordinatorInstanceId)|$($effect.TransitionId)|$($effect.OperationKey)"
        if (-not $effectIndex.ContainsKey($key)) { $effectIndex[$key] = [Collections.Generic.List[object]]::new() }
        $effectIndex[$key].Add($effect)
    }
    foreach ($group in @($History | Where-Object {
                $_.Operation.family -ceq 'runtime_unlock_retry_lifecycle' -and $_.Operation.operationRole -ceq 'scope_invalidation'
            } | Group-Object Key)) {
        if (@($group.Group | Where-Object { $_.Operation.beforeState -ceq 'Applied' -or $_.Operation.afterState -ceq 'Applied' }).Count -eq 0) { continue }
        $state = 'None'; $command = $null; $appliedReceipt = $null; $previous = $null
        foreach ($entry in @($group.Group | Sort-Object { [long]$_.Boundary.beforeStateGeneration },
                { [long]$_.Boundary.afterStateGeneration }, { [string]$_.Boundary.boundaryId })) {
            $boundary = $entry.Boundary; $operation = $entry.Operation
            $streamKey = "$($boundary.coordinatorInstanceId)|$($boundary.writer)"
            if (-not $AggregateIndex.ContainsKey($streamKey) -or
                -not $AggregateIndex[$streamKey].Boundaries.ContainsKey([string]$boundary.boundaryId)) {
                $state = 'Invalid'; continue # Existing validation owns the malformed boundary.
            }
            $stream = $AggregateIndex[$streamKey]
            $receipts = @($operation.runtimeUnlockRetry.receipts)
            if ($receipts.Count -ne 1 -or $receipts[0].stage -cne 'scope_invalidation') { $state = 'Invalid'; continue }
            $receipt = $receipts[0]
            $signature = [ordered]@{
                authority = $operation.authorityId; predecessor = $operation.predecessorAuthorityId
                generation = $operation.operationGeneration; command = $receipt.commandIdentity
                commandGeneration = $receipt.commandGeneration; fingerprint = $receipt.commandFingerprint
                capturedAttempt = $operation.runtimeUnlockRetry | Select-Object * -ExcludeProperty receipts
            } | ConvertTo-Json -Depth 64 -Compress
            $valid = $state -ceq $operation.beforeState -and ($null -eq $command -or $signature -ceq $command)
            if ($null -ne $previous) {
                $valid = $valid -and $previous.writer -ceq $boundary.writer
                $generation = [long]$previous.afterStateGeneration; $hash = [string]$previous.afterStateHash
                while ($valid -and $generation -lt [long]$boundary.beforeStateGeneration) {
                    if (-not $stream.Mutations.ContainsKey($generation) -or $stream.Mutations[$generation].Count -ne 1) { $valid = $false; break }
                    $step = $stream.Mutations[$generation][0]
                    if ($step.Boundary.beforeStateHash -cne $hash -or
                        [long]$step.Boundary.afterStateGeneration -ne ($generation + 1L) -or
                        ($state -ceq 'Pending' -and
                         ([long]$step.AfterBoundary.terminalGeneration -ne 0 -or
                          $step.BeforeBoundary.dataSessionIdentity -cne $step.AfterBoundary.dataSessionIdentity -or
                          [long]$step.BeforeBoundary.dataSessionGeneration -ne [long]$step.AfterBoundary.dataSessionGeneration))) {
                        $valid = $false; break
                    }
                    $hash = [string]$step.Boundary.afterStateHash; $generation++
                }
                $valid = $valid -and $generation -eq [long]$boundary.beforeStateGeneration -and $hash -ceq $boundary.beforeStateHash
            }
            if ($receipt.receiptStatus -ceq 'Applied') {
                $receiptSignature = $receipt | ConvertTo-Json -Depth 16 -Compress
                $valid = $valid -and ($null -eq $appliedReceipt -or $receiptSignature -ceq $appliedReceipt)
                $effectValid = $true
                if ($operation.beforeState -ceq 'Pending' -and $operation.afterState -ceq 'Applied') {
                    $key = "$($boundary.coordinatorInstanceId)|$($boundary.boundaryId)|$($operation.operationKey)"
                    $matches = @($effectIndex[$key] | Where-Object { $null -ne $_ })
                    if ($matches.Count -ne 1 -or -not
                        (Test-FileTransferCoordinatorSnapshotExactEffectBinding $boundary $operation $matches[0])) {
                        "snapshot_scope_invalidation_required_effect_missing:$($boundary.evidenceLineIndex)"
                        $effectValid = $false
                    }
                }
                elseif ($null -eq $appliedReceipt) { $valid = $false }
                if (-not $valid) {
                    "snapshot_operation_receipt_incomplete|family2_scope_invalidation_application_without_exact_pending_history:$($boundary.evidenceLineIndex)"
                }
                $valid = $valid -and $effectValid
                if ($valid) { $appliedReceipt = $receiptSignature }
            }
            if ($valid) {
                $command = $signature
                $state = if ($operation.afterState -ceq 'None') { 'Retired' } else { [string]$operation.afterState }
            }
            else { $state = 'Invalid' }
            $previous = $boundary
        }
    }
}

function Resolve-FileTransferCoordinatorCheckpointResponderCarries {
    param($AggregateIndex, $InvalidBoundaries)

    foreach ($stream in $AggregateIndex.Values) {
        $entries = @($stream.Boundaries.Values | Sort-Object { [long]$_.Boundary.beforeStateGeneration },
            { [long]$_.Boundary.afterStateGeneration }, { [string]$_.Boundary.boundaryId })
        $candidates = @($entries | Where-Object {
            $_.Boundary.decisionKind -in @('FallbackCheckpointRequested','FallbackCheckpointAccepted') -and
            @($_.Boundary.operations | Where-Object family -CEQ 'responder_probe_handoff').Count -gt 0
        })
        if ($candidates.Count -eq 0) { continue }
        $lastGeneration = [long]$candidates[-1].Boundary.afterStateGeneration
        $slot = $null; $retired = @{}; $previous = $null; $previousEntry = $null
        foreach ($entry in $entries) {
            $boundary = $entry.Boundary
            if ([long]$boundary.beforeStateGeneration -gt $lastGeneration) { break }
            $valid = -not $InvalidBoundaries.ContainsKey([string]$boundary.boundaryId)
            if ($null -ne $previous) {
                $valid = $valid -and [long]$previous.afterStateGeneration -eq [long]$boundary.beforeStateGeneration -and
                    $previous.afterStateHash -ceq $boundary.beforeStateHash -and
                    $previousEntry.SessionId -ceq $entry.SessionId -and $previousEntry.TransferId -ceq $entry.TransferId -and
                    $previousEntry.Direction -ceq $entry.Direction
            }
            if (-not $valid -or [long]$entry.AfterBoundary.terminalGeneration -ne 0 -or
                $entry.BeforeBoundary.dataSessionIdentity -cne $entry.AfterBoundary.dataSessionIdentity -or
                [long]$entry.BeforeBoundary.dataSessionGeneration -ne [long]$entry.AfterBoundary.dataSessionGeneration) {
                if ($null -ne $slot) { $retired[$slot.Key] = $true }; $slot = $null
                $valid = $false
            }
            $terminals = @($boundary.operations | Where-Object {
                $_.family -ceq 'responder_probe_handoff' -and $_.operationRole -ceq 'handoff_terminal' -and
                $_.operationKind -ceq 'ResponderPeerProbeRouteTransitionHandoff'
            })
            foreach ($operation in @($terminals | Sort-Object { if ($_.afterState -ceq 'None') { 0 } else { 1 } })) {
                $payload = $operation.responderProbeHandoff
                # Effective/target and linked-transition projection follow current
                # aggregate state. The retained observation and receipt do not.
                $identity = $operation | Select-Object * -ExcludeProperty beforeState,afterState,stateApplied,responderProbeHandoff
                $identity | Add-Member -NotePropertyName responderProbeHandoff -NotePropertyValue (
                    $payload | Select-Object * -ExcludeProperty effectiveBoundary,targetBoundary,linkedTransitionAuthorityId,linkedTransitionGeneration)
                $signature = $identity | ConvertTo-Json -Depth 32 -Compress
                $key = [string]$operation.operationKey
                if ($operation.beforeState -ceq 'None' -and $operation.afterState -ceq 'Invalidated' -and $valid -and
                    -not $retired.ContainsKey($key) -and $null -eq $slot) {
                    $slot = @{ Key=$key; Signature=$signature }
                }
                else {
                    $valid = $valid -and $null -ne $slot -and $slot.Key -ceq $key -and $slot.Signature -ceq $signature -and
                        $operation.beforeState -ceq 'Invalidated' -and $operation.afterState -in @('Invalidated','None')
                }
                if ($operation.afterState -ceq 'None' -or -not $valid) {
                    if ($null -ne $slot) { $retired[$slot.Key] = $true }
                    $retired[$key] = $true; $slot = $null
                }
            }
            if ($null -ne $slot -and @($boundary.operations | Where-Object {
                $_.family -ceq 'responder_probe_handoff' -and $_.operationRole -in @('handoff_current','owner_work_hold') -and
                $_.beforeState -ceq 'None' -and $_.afterState -cne 'None'
            }).Count -gt 0) { $retired[$slot.Key] = $true; $slot = $null; $valid = $false }
            if ($boundary.decisionKind -in @('FallbackCheckpointRequested','FallbackCheckpointAccepted') -and
                $terminals.Count -gt 0 -and (-not $valid -or $null -eq $slot -or $terminals.Count -ne 1)) {
                "snapshot_operation_catalog_mismatch|checkpoint_responder_terminal_carry_history_invalid:$($boundary.evidenceLineIndex)"
            }
            $previous = $boundary
            $previousEntry = $entry
        }
    }
}

function Get-FileTransferCoordinatorPressureTerminalIdentity {
    param($Operation, [switch]$IgnoreRole, [switch]$IgnoreSourceBoundary)

    $excluded = @('beforeState', 'afterState', 'stateApplied', 'regularV4FrontierRepairPressure')
    if ($IgnoreRole) { $excluded += @('operationRole', 'operationKey') }
    $identity = $Operation | Select-Object * -ExcludeProperty $excluded
    $payloadExcluded = @('effectiveBoundary')
    if ($IgnoreSourceBoundary) { $payloadExcluded += 'sourceBoundary' }
    if ($IgnoreRole) { $payloadExcluded += 'role' }
    $identity | Add-Member -NotePropertyName regularV4FrontierRepairPressure -NotePropertyValue (
        $Operation.regularV4FrontierRepairPressure | Select-Object * -ExcludeProperty $payloadExcluded)
    return ConvertTo-Json -InputObject $identity -Depth 64 -Compress
}

function Test-FileTransferCoordinatorPressureTerminalSourceCarry {
    param(
        [Parameter(Mandatory = $true)]$PreviousSource,
        [Parameter(Mandatory = $true)]$CurrentSource,
        [Parameter(Mandatory = $true)]$Boundary,
        [Parameter(Mandatory = $true)]$Entry
    )

    if (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
            -Left $PreviousSource -Right $CurrentSource) {
        return $true
    }
    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint `
            -Left $PreviousSource -Right $CurrentSource) -or
        [long]$PreviousSource.checkpointGeneration -ne
            [long]$Entry.BeforeBoundary.checkpointGeneration -or
        [string]$PreviousSource.checkpointRequestId -cne
            [string]$Entry.BeforeBoundary.checkpointRequestId -or
        [long]$CurrentSource.checkpointGeneration -ne
            [long]$Entry.AfterBoundary.checkpointGeneration -or
        [string]$CurrentSource.checkpointRequestId -cne
            [string]$Entry.AfterBoundary.checkpointRequestId) {
        return $false
    }

    $routeCommitCheckpointBootstrap =
        [string]$Boundary.decisionKind -ceq
            'CommitFileTransferRouteTransition' -and
        [string]$Boundary.decisionStatus -ceq 'Committed' -and
        [string]$Entry.BeforeBoundary.route -ceq 'file_tuna_v4' -and
        [int]$Entry.BeforeBoundary.protocolVersion -eq 4 -and
        [long]$Entry.BeforeBoundary.checkpointGeneration -eq 0 -and
        [string]$Entry.BeforeBoundary.checkpointRequestId -ceq '(none)' -and
        [string]$Entry.AfterBoundary.route -ceq
            'post_tuna_fallback_v6' -and
        [int]$Entry.AfterBoundary.protocolVersion -eq 6 -and
        [long]$Entry.AfterBoundary.checkpointGeneration -gt 0 -and
        [string]$Entry.AfterBoundary.checkpointRequestId -notin @(
            '', '(none)')
    if ($routeCommitCheckpointBootstrap) {
        return $true
    }

    if ([string]$Boundary.decisionKind -notin @(
            'FallbackCheckpointRequested', 'FallbackCheckpointAccepted') -or
        [string]$Boundary.decisionStatus -cne 'Accepted') {
        return $false
    }

    $checkpointEnvelope = [pscustomobject]@{
        decisionKind = [string]$Boundary.decisionKind
        decisionStatus = [string]$Boundary.decisionStatus
        decisionReason = [string]$Boundary.decisionReason
        aggregate = [pscustomobject]@{
            beforeStateGeneration = [long]$Boundary.beforeStateGeneration
            afterStateGeneration = [long]$Boundary.afterStateGeneration
            beforeStateFingerprint = [string]$Boundary.beforeStateHash
            afterStateFingerprint = [string]$Boundary.afterStateHash
            beforeBoundary = $Entry.BeforeBoundary
            afterBoundary = $Entry.AfterBoundary
        }
    }
    return Test-FileTransferCoordinatorSnapshotFallbackCheckpointScopeMutation `
        -Envelope $checkpointEnvelope
}

function Resolve-FileTransferCoordinatorPressureTerminalRollovers {
    param(
        [Parameter(Mandatory = $true)]$AggregateIndex,
        [Parameter(Mandatory = $true)]$Candidates,
        [Parameter(Mandatory = $true)]$InvalidBoundaries
    )

    # Reduce the single LastSettlement slot once per stream. No callback performs
    # a latest-prior search, and a rejected boundary cannot seed a later slot.
    $resolved = @{}
    foreach ($stream in $AggregateIndex.Values) {
        $slot = $null
        $previous = $null
        $seen = @{}
        foreach ($generation in @($stream.Mutations.Keys | Sort-Object)) {
            $entries = $stream.Mutations[$generation]
            if ($entries.Count -ne 1) {
                foreach ($conflict in $entries) {
                    $id = [string]$conflict.Boundary.boundaryId
                    $InvalidBoundaries[$id] = $true
                    [void]$stream.Boundaries.Remove($id)
                    if ($Candidates.ContainsKey($id) -and -not $resolved.ContainsKey($id)) {
                        $resolved[$id] = $true
                        "$($Candidates[$id].Failure):$($conflict.Boundary.evidenceLineIndex)"
                    }
                }
                [void]$stream.Mutations.Remove($generation)
                $slot = $null; $previous = $null
                continue
            }
            $entry = $entries[0]
            $boundary = $entry.Boundary
            $id = [string]$boundary.boundaryId
            $valid = -not $InvalidBoundaries.ContainsKey($id) -and
                [long]$boundary.afterStateGeneration -eq ([long]$generation + 1L) -and
                [string]$boundary.beforeStateHash -cne [string]$boundary.afterStateHash
            if ($null -ne $previous) {
                $valid = $valid -and
                    [long]$previous.Boundary.afterStateGeneration -eq [long]$generation -and
                    [string]$previous.Boundary.afterStateHash -ceq [string]$boundary.beforeStateHash -and
                    [string]$previous.SessionId -ceq [string]$entry.SessionId -and
                    [string]$previous.TransferId -ceq [string]$entry.TransferId -and
                    [string]$previous.Direction -ceq [string]$entry.Direction -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                        -Left $previous.AfterBoundary -Right $entry.BeforeBoundary)
            }
            if (-not $valid -or
                [long]$entry.BeforeBoundary.terminalGeneration -ne 0 -or
                [long]$entry.AfterBoundary.terminalGeneration -ne 0 -or
                [string]$entry.BeforeBoundary.dataSessionIdentity -cne [string]$entry.AfterBoundary.dataSessionIdentity -or
                [long]$entry.BeforeBoundary.dataSessionGeneration -ne [long]$entry.AfterBoundary.dataSessionGeneration) {
                $slot = $null
            }

            $terminals = @($boundary.operations | Where-Object {
                    [string]$_.family -ceq 'regular_v4_frontier_repair_pressure' -and
                    [string]$_.operationRole -ceq 'pressure_settlement_terminal' -and
                    [bool]$_.stateApplied
                })
            $mirrors = @($boundary.operations | Where-Object {
                    [string]$_.family -ceq 'regular_v4_frontier_repair_pressure' -and
                    [string]$_.operationRole -ceq 'repair_authority_terminal' -and
                    [string]$_.operationKind -ceq 'PressureSettlement' -and
                    [bool]$_.stateApplied
                })
            $entering = @($terminals | Where-Object { $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Applied' })
            $retiring = @($terminals | Where-Object { $_.beforeState -ceq 'Applied' -and $_.afterState -ceq 'None' })
            $carrying = @($terminals | Where-Object { $_.beforeState -ceq 'Applied' -and $_.afterState -ceq 'Applied' })
            $valid = $valid -and $entering.Count -le 1 -and $retiring.Count -le 1 -and $carrying.Count -le 1 -and
                $terminals.Count -eq ($entering.Count + $retiring.Count + $carrying.Count) -and
                $mirrors.Count -eq $terminals.Count
            foreach ($terminal in $terminals) {
                $matchingMirrors = @($mirrors | Where-Object {
                        [string]$_.authorityId -ceq [string]$terminal.authorityId -and
                        [long]$_.operationGeneration -eq [long]$terminal.operationGeneration -and
                        [string]$_.beforeState -ceq [string]$terminal.beforeState -and
                        [string]$_.afterState -ceq [string]$terminal.afterState
                    })
                $valid = $valid -and $matchingMirrors.Count -eq 1 -and [bool]$terminal.receiptValidated
                if ($matchingMirrors.Count -eq 1) {
                    $valid = $valid -and [bool]$matchingMirrors[0].receiptValidated -and
                        (Get-FileTransferCoordinatorPressureTerminalIdentity $terminal -IgnoreRole) -ceq
                            (Get-FileTransferCoordinatorPressureTerminalIdentity $matchingMirrors[0] -IgnoreRole) -and
                        (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                            -Left $terminal.regularV4FrontierRepairPressure.effectiveBoundary `
                            -Right $matchingMirrors[0].regularV4FrontierRepairPressure.effectiveBoundary)
                }
            }

            if ($retiring.Count + $carrying.Count -gt 0) {
                $existing = @($retiring) + @($carrying)
                $valid = $valid -and $existing.Count -eq 1 -and $null -ne $slot
                if ($existing.Count -eq 1 -and $null -ne $slot) {
                    $valid = $valid -and $slot.Identity -ceq
                        (Get-FileTransferCoordinatorPressureTerminalIdentity `
                            $existing[0] -IgnoreSourceBoundary) -and
                        (Test-FileTransferCoordinatorPressureTerminalSourceCarry `
                            -PreviousSource $slot.Source `
                            -CurrentSource $existing[0].regularV4FrontierRepairPressure.sourceBoundary `
                            -Boundary $boundary -Entry $entry) -and
                        (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                            -Left $slot.Effective -Right $entry.BeforeBoundary) -and
                        (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                            -Left $existing[0].regularV4FrontierRepairPressure.effectiveBoundary `
                            -Right $(if ($retiring.Count -eq 1) { $entry.BeforeBoundary } else { $entry.AfterBoundary }))
                }
            }
            elseif ($null -ne $slot) {
                $valid = $valid -and $entering.Count -eq 0 -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual -Left $slot.Effective -Right $entry.AfterBoundary)
            }
            if ($entering.Count -eq 1) {
                $key = [string]$entering[0].operationKey
                $valid = $valid -and -not $seen.ContainsKey($key) -and $carrying.Count -eq 0 -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                        -Left $entering[0].regularV4FrontierRepairPressure.effectiveBoundary -Right $entry.AfterBoundary)
                $seen[$key] = $true
            }
            if ($Candidates.ContainsKey($id)) {
                $resolved[$id] = $true
                if (-not $valid -or $retiring.Count -ne 1 -or $entering.Count -ne 1) {
                    $InvalidBoundaries[$id] = $true
                    # Remove provisional evidence before another family uses the index.
                    [void]$stream.Boundaries.Remove($id)
                    [void]$stream.Mutations.Remove($generation)
                    $candidate = $Candidates[$id]
                    "$($candidate.Failure):$($boundary.evidenceLineIndex)"
                    $valid = $false
                }
            }
            if (-not $valid) { $slot = $null }
            elseif ($entering.Count -eq 1 -or $carrying.Count -eq 1) {
                $terminal = if ($entering.Count -eq 1) { $entering[0] } else { $carrying[0] }
                $slot = @{ Identity = Get-FileTransferCoordinatorPressureTerminalIdentity `
                        $terminal -IgnoreSourceBoundary
                    Source = $terminal.regularV4FrontierRepairPressure.sourceBoundary
                    Effective = $terminal.regularV4FrontierRepairPressure.effectiveBoundary }
            }
            elseif ($retiring.Count -eq 1) { $slot = $null }
            $previous = $entry
        }
    }
    foreach ($id in $Candidates.Keys) {
        if (-not $resolved.ContainsKey($id)) {
            "$($Candidates[$id].Failure):$($Candidates[$id].LineIndex)"
        }
    }
}

function Test-FileTransferCoordinatorResponderHandoffConsumedInterval {
    param(
        [Parameter(Mandatory = $true)]$Disposition,
        [Parameter(Mandatory = $true)]$Candidate,
        [Parameter(Mandatory = $true)][string]$LinkedAuthority,
        [Parameter(Mandatory = $true)][long]$LinkedGeneration,
        $AggregateIndex = $null
    )

    $start = $Disposition.Boundary
    $end = $Candidate.Boundary
    $generation = [long]$start.afterStateGeneration
    $hash = [string]$start.afterStateHash
    if ($generation -eq [long]$end.beforeStateGeneration) {
        return $hash -ceq [string]$end.beforeStateHash
    }
    $key = "$($end.coordinatorInstanceId)|$($end.writer)"
    if ($generation -gt [long]$end.beforeStateGeneration -or
        $null -eq $AggregateIndex -or -not $AggregateIndex.ContainsKey($key) -or
        [string]$start.coordinatorInstanceId -cne [string]$end.coordinatorInstanceId -or
        [string]$start.writer -cne [string]$end.writer) {
        return $false
    }
    $stream = $AggregateIndex[$key]
    if (-not $stream.Boundaries.ContainsKey([string]$start.boundaryId) -or
        -not $stream.Boundaries.ContainsKey([string]$end.boundaryId)) {
        return $false
    }
    $startEntry = $stream.Boundaries[[string]$start.boundaryId]
    $endEntry = $stream.Boundaries[[string]$end.boundaryId]
    $target = $Candidate.Operation.responderProbeHandoff.targetBoundary
    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryComplete -Boundary $target) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual -Left $startEntry.AfterBoundary -Right $target) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual -Left $endEntry.BeforeBoundary -Right $target) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual -Left $endEntry.AfterBoundary -Right $target)) {
        return $false
    }

    $retainedOperations = @{}
    foreach ($operation in @($start.operations)) {
        if ([string]$operation.family -ceq 'responder_probe_handoff' -or
            ([string]$operation.family -ceq 'route_transition' -and
             [string]$operation.authorityId -ceq $LinkedAuthority -and
             [long]$operation.operationGeneration -eq $LinkedGeneration)) {
            $retainedOperations[[string]$operation.operationKey] = ConvertTo-Json `
                -InputObject ($operation | Select-Object * -ExcludeProperty beforeState, stateApplied) `
                -Depth 64 -Compress
        }
    }
    while ($generation -lt [long]$end.beforeStateGeneration) {
        if (-not $stream.Mutations.ContainsKey($generation) -or
            $stream.Mutations[$generation].Count -ne 1) {
            return $false
        }
        $entry = $stream.Mutations[$generation][0]
        $boundary = $entry.Boundary
        if ([long]$boundary.afterStateGeneration -ne ($generation + 1L) -or
            [string]$boundary.beforeStateHash -cne $hash -or
            [string]$boundary.beforeStateHash -ceq [string]$boundary.afterStateHash -or
            [string]$entry.SessionId -cne [string]$startEntry.SessionId -or
            [string]$entry.TransferId -cne [string]$startEntry.TransferId -or
            [string]$entry.Direction -cne [string]$startEntry.Direction -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual -Left $entry.BeforeBoundary -Right $target) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual -Left $entry.AfterBoundary -Right $target)) {
            return $false
        }
        foreach ($operation in @($boundary.operations)) {
            if ([string]$operation.family -cne 'responder_probe_handoff' -and
                -not ([string]$operation.family -ceq 'route_transition' -and
                      [string]$operation.authorityId -ceq $LinkedAuthority)) {
                continue
            }
            $operationKey = [string]$operation.operationKey
            if ([string]$operation.beforeState -cne [string]$operation.afterState -or
                -not $retainedOperations.ContainsKey($operationKey) -or
                $retainedOperations[$operationKey] -cne (ConvertTo-Json `
                    -InputObject ($operation | Select-Object * -ExcludeProperty beforeState, stateApplied) `
                    -Depth 64 -Compress)) {
                return $false
            }
        }
        $generation = [long]$boundary.afterStateGeneration
        $hash = [string]$boundary.afterStateHash
    }
    return $hash -ceq [string]$end.beforeStateHash -and
        [string]$endEntry.SessionId -ceq [string]$startEntry.SessionId -and
        [string]$endEntry.TransferId -ceq [string]$startEntry.TransferId -and
        [string]$endEntry.Direction -ceq [string]$startEntry.Direction
}

function Test-FileTransferCoordinatorResponderPreparationFirstAcceptance {
    param(
        [Parameter(Mandatory = $true)]$Prepared,
        [Parameter(Mandatory = $true)]$Draining,
        [Parameter(Mandatory = $true)]$Committed,
        [Parameter(Mandatory = $true)]$Acceptance,
        $AggregateIndex = $null
    )

    $start = $Prepared.Boundary
    $end = $Acceptance.Boundary
    $operation = $Acceptance.Operation
    $payload = $operation.responderProbeHandoff
    $preparedPayload = $Prepared.Operation.responderProbeHandoff
    $key = "$($start.coordinatorInstanceId)|$($start.writer)"
    if ([string]$end.decisionStatus -cne 'Idempotent' -or
        [long]$end.beforeStateGeneration -ne [long]$end.afterStateGeneration -or
        [string]$end.beforeStateHash -cne [string]$end.afterStateHash -or
        [bool]$operation.stateApplied -or
        [string]$operation.beforeState -cne 'None' -or
        [string]$operation.afterState -cne 'Accepted' -or
        [string]$operation.receiptStatus -cne 'Accepted' -or
        -not [bool]$operation.receiptValidated -or
        [long]$end.afterStateGeneration -ge [long]$Committed.Boundary.afterStateGeneration -or
        [string]$payload.linkedTransitionAuthorityId -cne [string]$preparedPayload.linkedTransitionAuthorityId -or
        [long]$payload.linkedTransitionGeneration -ne [long]$preparedPayload.linkedTransitionGeneration -or
        [long]$payload.pendingPreCommitGeneration -le 0 -or
        [long]$payload.pendingPreCommitGeneration -ne [long]$preparedPayload.pendingPreCommitGeneration -or
        [string]$operation.commandFingerprint -cne [string]$Prepared.Operation.commandFingerprint -or
        (Get-FileTransferCoordinatorResponderProbeHandoffLifecycleKey -Boundary $end -Operation $operation) -cne
            (Get-FileTransferCoordinatorResponderProbeHandoffLifecycleKey -Boundary $start -Operation $Prepared.Operation) -or
        $null -eq $AggregateIndex -or -not $AggregateIndex.ContainsKey($key)) {
        return $false
    }
    $stream = $AggregateIndex[$key]
    if (-not $stream.Boundaries.ContainsKey([string]$start.boundaryId) -or
        -not $stream.Boundaries.ContainsKey([string]$end.boundaryId)) { return $false }
    $startEntry = $stream.Boundaries[[string]$start.boundaryId]
    $endEntry = $stream.Boundaries[[string]$end.boundaryId]
    $scope = $startEntry.AfterBoundary
    $generation = [long]$start.afterStateGeneration
    $hash = [string]$start.afterStateHash
    $active = $Prepared
    $retained = @{}
    foreach ($item in @($start.operations)) {
        if ([string]$item.family -ceq 'responder_probe_handoff' -or
            ([string]$item.family -ceq 'route_transition' -and
             [string]$item.authorityId -ceq [string]$payload.linkedTransitionAuthorityId)) {
            $retained[[string]$item.operationKey] = ConvertTo-Json -InputObject (
                $item | Select-Object * -ExcludeProperty beforeState, stateApplied) -Depth 64 -Compress
        }
    }
    # Follow validated aggregate generations, not log order or a latest-prior lookup.
    while ($generation -lt [long]$end.beforeStateGeneration) {
        if (-not $stream.Mutations.ContainsKey($generation) -or
            $stream.Mutations[$generation].Count -ne 1) { return $false }
        $entry = $stream.Mutations[$generation][0]
        $boundary = $entry.Boundary
        if ([long]$boundary.afterStateGeneration -ne ($generation + 1L) -or
            [string]$boundary.beforeStateHash -cne $hash -or
            [string]$boundary.beforeStateHash -ceq [string]$boundary.afterStateHash -or
            [string]$entry.SessionId -cne [string]$startEntry.SessionId -or
            [string]$entry.TransferId -cne [string]$startEntry.TransferId -or
            [string]$entry.Direction -cne [string]$startEntry.Direction -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual -Left $entry.BeforeBoundary -Right $scope) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual -Left $entry.AfterBoundary -Right $scope)) {
            return $false
        }
        $isDrain = [string]$boundary.boundaryId -ceq [string]$Draining.Boundary.boundaryId
        foreach ($item in @($boundary.operations)) {
            if ([string]$item.family -cne 'responder_probe_handoff' -and
                -not ([string]$item.family -ceq 'route_transition' -and
                      [string]$item.authorityId -ceq [string]$payload.linkedTransitionAuthorityId)) { continue }
            $itemKey = [string]$item.operationKey
            $value = ConvertTo-Json -InputObject (
                $item | Select-Object * -ExcludeProperty beforeState, stateApplied) -Depth 64 -Compress
            if ($isDrain) {
                if ([string]$item.family -ceq 'responder_probe_handoff') {
                    if ($itemKey -cne [string]$Draining.Operation.operationKey -or
                        [string]$item.commandFingerprint -cne [string]$Prepared.Operation.commandFingerprint -or
                        [long]$item.responderProbeHandoff.pendingPreCommitGeneration -ne [long]$payload.pendingPreCommitGeneration -or
                        (Get-FileTransferCoordinatorResponderProbeHandoffLifecycleKey -Boundary $boundary -Operation $item) -cne
                            (Get-FileTransferCoordinatorResponderProbeHandoffLifecycleKey -Boundary $start -Operation $Prepared.Operation)) { return $false }
                }
                elseif ([long]$item.operationGeneration -ne [long]$payload.linkedTransitionGeneration -or
                    [string]$item.operationRole -notin @('current','inbound','outbound') -or
                    [string]$item.afterState -cne 'Draining') { return $false }
            }
            if (-not $isDrain -and
                ([string]$item.beforeState -cne [string]$item.afterState -or
                 -not $retained.ContainsKey($itemKey) -or $retained[$itemKey] -cne $value)) { return $false }
            $retained[$itemKey] = $value
        }
        if ($isDrain) { $active = $Draining }
        $generation = [long]$boundary.afterStateGeneration
        $hash = [string]$boundary.afterStateHash
    }
    return $generation -eq [long]$end.beforeStateGeneration -and
        $hash -ceq [string]$end.beforeStateHash -and
        [string]$endEntry.SessionId -ceq [string]$startEntry.SessionId -and
        [string]$endEntry.TransferId -ceq [string]$startEntry.TransferId -and
        [string]$endEntry.Direction -ceq [string]$startEntry.Direction -and
        (Test-FileTransferCoordinatorSnapshotBoundaryEqual -Left $endEntry.BeforeBoundary -Right $scope) -and
        (Test-FileTransferCoordinatorSnapshotBoundaryEqual -Left $endEntry.AfterBoundary -Right $scope) -and
        (Test-FileTransferCoordinatorSnapshotBoundaryEqual -Left $payload.effectiveBoundary -Right $active.Operation.responderProbeHandoff.effectiveBoundary) -and
        (Test-FileTransferCoordinatorSnapshotBoundaryEqual -Left $payload.targetBoundary -Right $active.Operation.responderProbeHandoff.targetBoundary)
}

function Get-FileTransferCoordinatorResponderAcceptanceSubject {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Accepted,
        [Parameter(Mandatory = $true)]$Prepared
    )

    if ($Accepted.Count -eq 1) {
        return $Accepted[0]
    }
    if ($Accepted.Count -ne 2) {
        return $null
    }

    $preparedBoundary = $Prepared.Boundary
    $preparedPayload = $Prepared.Operation.responderProbeHandoff
    $linkedAuthority = [string]$preparedPayload.linkedTransitionAuthorityId
    $linkedGeneration = [long]$preparedPayload.linkedTransitionGeneration
    $unlinked = @($Accepted | Where-Object {
            [string]$_.Operation.responderProbeHandoff.linkedTransitionAuthorityId -ceq '(none)' -and
            [long]$_.Operation.responderProbeHandoff.linkedTransitionGeneration -eq 0
        })
    $linked = @($Accepted | Where-Object {
            [string]$_.Operation.responderProbeHandoff.linkedTransitionAuthorityId -ceq
                $linkedAuthority -and
            [long]$_.Operation.responderProbeHandoff.linkedTransitionGeneration -eq
                $linkedGeneration
        })
    if ($unlinked.Count -ne 1 -or $linked.Count -ne 1) {
        return $null
    }

    $start = $unlinked[0]
    $end = $linked[0]
    if ([string]$start.Boundary.decisionStatus -cne 'Idempotent' -or
        [string]$end.Boundary.decisionStatus -cne 'Idempotent' -or
        [long]$start.Boundary.beforeStateGeneration -ne
            [long]$start.Boundary.afterStateGeneration -or
        [string]$start.Boundary.beforeStateHash -cne
            [string]$start.Boundary.afterStateHash -or
        [long]$end.Boundary.beforeStateGeneration -ne
            [long]$end.Boundary.afterStateGeneration -or
        [string]$end.Boundary.beforeStateHash -cne
            [string]$end.Boundary.afterStateHash -or
        [long]$start.Boundary.afterStateGeneration -ne
            [long]$preparedBoundary.beforeStateGeneration -or
        [string]$start.Boundary.afterStateHash -cne
            [string]$preparedBoundary.beforeStateHash -or
        [long]$end.Boundary.afterStateGeneration -lt
            [long]$preparedBoundary.afterStateGeneration -or
        (Get-FileTransferCoordinatorResponderProbeHandoffLifecycleKey `
            -Boundary $start.Boundary -Operation $start.Operation) -cne
            (Get-FileTransferCoordinatorResponderProbeHandoffLifecycleKey `
                -Boundary $end.Boundary -Operation $end.Operation)) {
        return $null
    }

    $startOperation = ConvertTo-Json -InputObject (
        $start.Operation | Select-Object * -ExcludeProperty responderProbeHandoff) `
        -Depth 32 -Compress
    $endOperation = ConvertTo-Json -InputObject (
        $end.Operation | Select-Object * -ExcludeProperty responderProbeHandoff) `
        -Depth 32 -Compress
    $startPayload = ConvertTo-Json -InputObject (
        $start.Operation.responderProbeHandoff |
            Select-Object * -ExcludeProperty linkedTransitionAuthorityId,
                linkedTransitionGeneration) -Depth 32 -Compress
    $endPayload = ConvertTo-Json -InputObject (
        $end.Operation.responderProbeHandoff |
            Select-Object * -ExcludeProperty linkedTransitionAuthorityId,
                linkedTransitionGeneration) -Depth 32 -Compress
    if ($startOperation -cne $endOperation -or $startPayload -cne $endPayload) {
        return $null
    }
    return $end
}

function Test-FileTransferCoordinatorResponderProbeHandoffObservationConsumed {
    param(
        [Parameter(Mandatory = $true)][object[]]$History,
        [Parameter(Mandatory = $true)]$Candidate,
        $AggregateIndex = $null
    )

    $candidateBoundary = $Candidate.Boundary
    $candidateOperation = $Candidate.Operation
    $candidatePayload = $candidateOperation.responderProbeHandoff
    if ($null -eq $candidatePayload -or
        [string]$candidateBoundary.decisionKind -cne
            'ObserveRuntimeUnlockResponderPeerProbeHandoff' -or
        [string]$candidateBoundary.decisionStatus -cne 'Rejected' -or
        [string]$candidateBoundary.decisionReason -cne 'accepted' -or
        [string]$candidateOperation.operationRole -cne 'attempted_decision' -or
        [string]$candidateOperation.beforeState -cne 'None' -or
        [string]$candidateOperation.afterState -cne 'Rejected' -or
        [string]$candidateOperation.receiptStatus -cne 'Rejected' -or
        -not [bool]$candidateOperation.receiptValidated -or
        [bool]$candidateOperation.stateApplied -or
        [long]$candidateBoundary.beforeStateGeneration -ne
            [long]$candidateBoundary.afterStateGeneration -or
        [string]$candidateBoundary.beforeStateHash -cne
            [string]$candidateBoundary.afterStateHash) {
        return $false
    }

    $candidateGeneration = [long]$candidateBoundary.afterStateGeneration
    $accepted = @($History | Where-Object {
            [string]$_.Boundary.decisionKind -ceq
                'ObserveRuntimeUnlockResponderPeerProbeHandoff' -and
            [string]$_.Operation.operationRole -ceq
                'peer_acceptance_attempt' -and
            [string]$_.Operation.receiptStatus -ceq 'Accepted' -and
            [bool]$_.Operation.receiptValidated -and
            -not [bool]$_.Operation.stateApplied -and
            [long]$_.Boundary.afterStateGeneration -lt $candidateGeneration -and
            [string]$_.Operation.commandIdentity -ceq
                [string]$candidateOperation.commandIdentity -and
            [long]$_.Operation.commandGeneration -eq
                [long]$candidateOperation.commandGeneration -and
            [string]$_.Operation.commandFingerprint -ceq
                [string]$candidateOperation.commandFingerprint -and
            [string]$_.Operation.receiptIdentity -ceq
                [string]$candidateOperation.receiptIdentity -and
            [long]$_.Operation.receiptGeneration -eq
                [long]$candidateOperation.receiptGeneration -and
            (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                -Left $_.Operation.responderProbeHandoff.sourceBoundary `
                -Right $candidatePayload.sourceBoundary)
        })
    $prepared = @($History | Where-Object {
            [string]$_.Boundary.decisionKind -ceq
                'PrepareFileTransferRouteTransition' -and
            [string]$_.Operation.operationRole -ceq
                'route_transition_handoff_pending' -and
            [string]$_.Operation.receiptStatus -ceq 'Prepared' -and
            [bool]$_.Operation.receiptValidated -and
            [long]$_.Boundary.afterStateGeneration -lt $candidateGeneration
        })
    if ($prepared.Count -ne 1) {
        return $false
    }
    $preparedEntry = $prepared[0]
    $preparedPayload = $preparedEntry.Operation.responderProbeHandoff
    $linkedAuthority = [string]$preparedPayload.linkedTransitionAuthorityId
    $linkedGeneration = [long]$preparedPayload.linkedTransitionGeneration
    if ($linkedAuthority -in @('', '(none)') -or $linkedGeneration -le 0 -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            -Left $preparedPayload.sourceBoundary `
            -Right $candidatePayload.sourceBoundary)) {
        return $false
    }
    $acceptance = Get-FileTransferCoordinatorResponderAcceptanceSubject `
        -Accepted @($accepted) -Prepared $preparedEntry
    if ($null -eq $acceptance) {
        return $false
    }
    $acceptedGeneration = [long]$acceptance.Boundary.afterStateGeneration

    $preparedGeneration = [long]$preparedEntry.Boundary.afterStateGeneration
    $draining = @($History | Where-Object {
            [string]$_.Boundary.decisionKind -ceq
                'BeginFileTransferRouteTransitionPhysicalDrain' -and
            [string]$_.Operation.operationRole -ceq
                'route_transition_handoff_pending' -and
            [string]$_.Operation.receiptStatus -ceq 'Draining' -and
            [bool]$_.Operation.receiptValidated -and
            [long]$_.Boundary.afterStateGeneration -gt $preparedGeneration -and
            [long]$_.Boundary.afterStateGeneration -lt $candidateGeneration
        })
    if ($draining.Count -ne 1) {
        return $false
    }
    $drainingEntry = $draining[0]
    $drainingGeneration = [long]$drainingEntry.Boundary.afterStateGeneration
    $committed = @($History | Where-Object {
            [string]$_.Boundary.decisionKind -ceq
                'CommitFileTransferRouteTransition' -and
            [string]$_.Operation.operationRole -ceq
                'route_transition_handoff_pending' -and
            [string]$_.Operation.receiptStatus -ceq 'Committed' -and
            [bool]$_.Operation.receiptValidated -and
            [long]$_.Boundary.afterStateGeneration -gt $drainingGeneration -and
            [long]$_.Boundary.afterStateGeneration -lt $candidateGeneration
        })
    if ($committed.Count -ne 1) {
        return $false
    }
    $committedEntry = $committed[0]
    $committedGeneration = [long]$committedEntry.Boundary.afterStateGeneration
    $dispositions = @($History | Where-Object {
            [string]$_.Boundary.decisionKind -ceq
                'ApplyFileTransferRouteTransitionDispositionReceipt' -and
            [string]$_.Operation.operationRole -ceq
                'route_transition_handoff_pending' -and
            [string]$_.Operation.receiptStatus -ceq 'Committed' -and
            [bool]$_.Operation.receiptValidated -and
            [long]$_.Boundary.afterStateGeneration -gt $committedGeneration -and
            [long]$_.Boundary.afterStateGeneration -le $candidateGeneration
        })
    if ($dispositions.Count -ne 1) {
        return $false
    }
    $dispositionEntry = $dispositions[0]
    $dispositionPayload = $dispositionEntry.Operation.responderProbeHandoff

    foreach ($entry in @($drainingEntry, $committedEntry, $dispositionEntry)) {
        $payload = $entry.Operation.responderProbeHandoff
        if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                -Left $payload.sourceBoundary `
                -Right $candidatePayload.sourceBoundary)) {
            return $false
        }
    }
    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            -Left $dispositionPayload.effectiveBoundary `
            -Right $candidatePayload.effectiveBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            -Left $dispositionPayload.targetBoundary `
            -Right $candidatePayload.targetBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            -Left $candidatePayload.effectiveBoundary `
            -Right $candidatePayload.targetBoundary)) {
        return $false
    }

    $preparedRoute = @($preparedEntry.Boundary.operations | Where-Object {
            [string]$_.family -ceq 'route_transition' -and
            [string]$_.operationRole -ceq 'current' -and
            [string]$_.authorityId -ceq $linkedAuthority -and
            [long]$_.operationGeneration -eq $linkedGeneration -and
            [string]$_.beforeState -ceq 'None' -and
            [string]$_.afterState -ceq 'Prepared'
        })
    $drainingRoute = @($drainingEntry.Boundary.operations | Where-Object {
            [string]$_.family -ceq 'route_transition' -and
            [string]$_.operationRole -ceq 'current' -and
            [string]$_.authorityId -ceq $linkedAuthority -and
            [long]$_.operationGeneration -eq $linkedGeneration -and
            [string]$_.beforeState -ceq 'Prepared' -and
            [string]$_.afterState -ceq 'Draining' -and
            [bool]$_.receiptValidated
        })
    $committedRoute = @($committedEntry.Boundary.operations | Where-Object {
            [string]$_.family -ceq 'route_transition' -and
            [string]$_.operationRole -ceq 'terminal' -and
            [string]$_.authorityId -ceq $linkedAuthority -and
            [long]$_.operationGeneration -eq $linkedGeneration -and
            [string]$_.beforeState -ceq 'None' -and
            [string]$_.afterState -ceq 'Committed' -and
            [string]$_.receiptStatus -ceq 'Committed' -and
            [bool]$_.receiptValidated
        })
    $dispositionRoute = @($dispositionEntry.Boundary.operations | Where-Object {
            [string]$_.family -ceq 'route_transition' -and
            [string]$_.operationRole -ceq 'terminal' -and
            [string]$_.authorityId -ceq $linkedAuthority -and
            [long]$_.operationGeneration -eq $linkedGeneration -and
            [string]$_.beforeState -ceq 'Committed' -and
            [string]$_.afterState -ceq 'Committed' -and
            [string]$_.receiptStatus -ceq 'Applied' -and
            [bool]$_.receiptValidated
        })
    $publication = @($committedEntry.Boundary.operations | Where-Object {
            [string]$_.family -ceq 'route_runtime' -and
            [string]$_.operationRole -ceq 'route_publication' -and
            [string]$_.operationKind -ceq 'RouteCommitPublication' -and
            [string]$_.beforeState -ceq 'None' -and
            [string]$_.afterState -ceq 'Published' -and
            [bool]$_.receiptValidated
        })
    if ($preparedRoute.Count -ne 1 -or $drainingRoute.Count -ne 1 -or
        $committedRoute.Count -ne 1 -or $dispositionRoute.Count -ne 1 -or
        $publication.Count -ne 1) {
        return $false
    }
    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            -Left $publication[0].routeRuntime.boundary `
            -Right $candidatePayload.targetBoundary)) {
        return $false
    }
    if ($preparedGeneration -le $acceptedGeneration -and
        -not (Test-FileTransferCoordinatorResponderPreparationFirstAcceptance `
            -Prepared $preparedEntry -Draining $drainingEntry -Committed $committedEntry `
            -Acceptance $acceptance -AggregateIndex $AggregateIndex)) {
        return $false
    }
    return Test-FileTransferCoordinatorResponderHandoffConsumedInterval `
        -Disposition $dispositionEntry -Candidate $Candidate `
        -LinkedAuthority $linkedAuthority -LinkedGeneration $linkedGeneration `
        -AggregateIndex $AggregateIndex
}

function Test-FileTransferCoordinatorRouteTransitionRetainedFrameLifecycle {
    param(
        [Parameter(Mandatory = $true)]$Commit,
        [Parameter(Mandatory = $true)][object[]]$History,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Effects
    )

    # A retained frame is attached by commit, not part of the immutable route
    # authority. Disposition-only attempts do not return that frame command.
    $frame = $Commit.Operation.routeTransition
    $fingerprint = [string]$frame.retainedFrameFingerprint
    $type = [string]$frame.retainedFrameType
    $hasFrame = $fingerprint -cne '(none)'
    if ([string]::IsNullOrWhiteSpace($fingerprint) -or
        [string]::IsNullOrWhiteSpace($type) -or
        $hasFrame -ne ($type -cne '(none)') -or
        (-not $hasFrame -and [string]$frame.proofBoundaryKind -ceq
            'FileTransferFallbackTransitionProofBoundary')) {
        return $false
    }
    $key = Get-FileTransferCoordinatorRouteTransitionLifecycleKey `
        -Boundary $Commit.Boundary -Operation $Commit.Operation
    $retirements = @($History | Where-Object {
            [bool]$_.Operation.stateApplied -and
            [string]$_.Operation.operationRole -ceq 'terminal' -and
            [string]$_.Operation.beforeState -ceq 'Committed' -and
            [string]$_.Operation.afterState -ceq 'None'
        })
    if ($retirements.Count -gt 1) { return $false }
    $applied = [System.Collections.Generic.List[object]]::new()
    $receiptKeys = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($entry in $History) {
        $operation = $entry.Operation
        $transition = $operation.routeTransition
        if ((Get-FileTransferCoordinatorRouteTransitionLifecycleKey `
                -Boundary $entry.Boundary -Operation $operation) -cne $key) {
            return $false
        }
        $empty = [string]$transition.retainedFrameFingerprint -ceq '(none)' -and
            [string]$transition.retainedFrameType -ceq '(none)'
        $matches = $hasFrame -and
            [string]$transition.retainedFrameFingerprint -ceq $fingerprint -and
            [string]$transition.retainedFrameType -ceq $type
        if (-not $empty -and -not $matches) { return $false }

        if ([bool]$operation.stateApplied) {
            if ($retirements.Count -eq 1 -and
                [long]$entry.Boundary.afterStateGeneration -gt
                    [long]$retirements[0].Boundary.afterStateGeneration) {
                return $false
            }
            $committedState = [string]$operation.operationRole -ceq 'terminal' -or
                ([string]$operation.beforeState -ceq 'Draining' -and
                 [string]$operation.afterState -ceq 'Committed')
            if ($committedState -and
                [long]$entry.Boundary.afterStateGeneration -ge
                    [long]$Commit.Boundary.afterStateGeneration) {
                if ($hasFrame -and -not $matches) { return $false }
            }
            elseif (-not $empty) { return $false }
        }
        elseif (-not $empty -and
            [long]$entry.Boundary.afterStateGeneration -lt
                [long]$Commit.Boundary.afterStateGeneration) {
            return $false
        }

        $receipts = @($transition.receipts | Where-Object {
                [string]$_.stage -ceq 'retained_frame' -and
                [string]$_.receiptStatus -ceq 'Applied' -and
                [bool]$_.receiptValidated
            })
        if ($receipts.Count -gt 1 -or ($receipts.Count -gt 0 -and -not $matches)) {
            return $false
        }
        if ($receipts.Count -eq 1) {
            $receipt = $receipts[0]
            [void]$receiptKeys.Add(@(
                    [string]$receipt.commandIdentity,
                    [long]$receipt.commandGeneration,
                    [string]$receipt.commandFingerprint,
                    [string]$receipt.receiptIdentity,
                    [long]$receipt.receiptGeneration,
                    [string]$receipt.receiptFingerprint) -join '|')
            $applied.Add([pscustomobject]@{ Entry = $entry; Receipt = $receipt })
        }
    }

    $frameEffects = @($Effects | Where-Object {
            [string]$_.EffectKind -in @('retained_frame_applied', 'retained_frame_response')
        })
    if (-not $hasFrame) { return $frameEffects.Count -eq 0 }
    if ($receiptKeys.Count -ne 1 -or
        @($frameEffects | Where-Object EffectKind -CEQ 'retained_frame_applied').Count -ne 1 -or
        @($frameEffects | Where-Object EffectKind -CEQ 'retained_frame_response').Count -gt 1 -or
        ($type -ceq 'filetransfer.frontier_request.v6' -and
         @($frameEffects | Where-Object EffectKind -CEQ 'retained_frame_response').Count -ne 1)) {
        return $false
    }
    foreach ($effect in $frameEffects) {
        $bindings = @($applied | Where-Object {
                [string]$effect.ReceiptIdentity -ceq [string]$_.Receipt.receiptIdentity -and
                [long]$effect.ReceiptGeneration -eq [long]$_.Receipt.receiptGeneration -and
                [string]$effect.ReceiptStatus -ceq 'Applied' -and
                [string]$effect.ReceiptFingerprint -ceq [string]$_.Receipt.receiptFingerprint -and
                (Test-FileTransferCoordinatorSnapshotExactEffectBinding `
                    -Boundary $_.Entry.Boundary -Operation $_.Entry.Operation -Effect $effect)
            })
        if ($bindings.Count -ne 1) { return $false }
    }
    return $true
}

function Get-FileTransferCoordinatorRouteTransitionLifecycleResult {
    param(
        [Parameter(Mandatory = $true)][object[]]$History,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Effects
    )

    $ordered = @($History | Sort-Object {
            [long]$_.Boundary.decisionUtcMilliseconds
        }, {
            [long]$_.Boundary.afterStateGeneration
        }, {
            [string]$_.Boundary.boundaryId
        })
    if ($ordered.Count -eq 0) {
        return [pscustomobject]@{
            HasCandidate = $false
            Candidate = $null
            RawCount = 0
            RecoveredCount = 0
            UnresolvedCount = 0
            FailureClass = 'filetransfer_route_transition_incomplete'
        }
    }

    $stateEntries = @($ordered | Where-Object {
            [bool]$_.Operation.stateApplied -and
            [string]$_.Operation.operationRole -in @('current', 'terminal')
        })
    $attemptEntries = @($ordered | Where-Object {
            -not [bool]$_.Operation.stateApplied -and
            [string]$_.Operation.operationRole -in @(
                'inbound', 'outbound', 'attempt') -and
            [string]$_.Operation.operationKind -ceq
                [string]$_.Boundary.decisionKind
        })

    $bindingKeys = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    foreach ($entry in $ordered) {
        [void]$bindingKeys.Add(@(
                [string]$entry.Boundary.coordinatorInstanceId,
                [string]$entry.Boundary.boundaryId,
                [string]$entry.Operation.operationKey) -join '|')
    }
    $lifecycleEffects = @($Effects | Where-Object {
            $bindingKeys.Contains(@(
                    [string]$_.CoordinatorInstanceId,
                    [string]$_.TransitionId,
                    [string]$_.OperationKey) -join '|')
        })

    $receipts = @(
        foreach ($entry in $ordered) {
            foreach ($receipt in @($entry.Operation.routeTransition.receipts)) {
                $receipt
            }
        }
    )
    $hasReceipt = {
        param([string]$Stage, [string]$Status)
        $stageReceipts = @($receipts | Where-Object {
                [string]$_.stage -ceq $Stage -and
                [string]$_.receiptStatus -ceq $Status -and
                [bool]$_.receiptValidated
            })
        $identities = @($stageReceipts | ForEach-Object {
                @(
                    [string]$_.commandIdentity,
                    [long]$_.commandGeneration,
                    [string]$_.commandFingerprint,
                    [string]$_.receiptIdentity,
                    [long]$_.receiptGeneration,
                    [string]$_.receiptStatus,
                    [string]$_.receiptFingerprint) -join '|'
            } | Sort-Object -Unique)
        return $identities.Count -eq 1
    }
    $hasEffect = {
        param([string]$Kind)
        return @($lifecycleEffects | Where-Object {
                [string]$_.EffectKind -ceq $Kind
            }).Count -eq 1
    }

    $atomicCommits = [System.Collections.Generic.List[object]]::new()
    foreach ($boundaryGroup in @($stateEntries | Group-Object {
                [string]$_.Boundary.boundaryId
            })) {
        $currentRetirements = @($boundaryGroup.Group | Where-Object {
                [string]$_.Operation.operationRole -ceq 'current' -and
                [string]$_.Operation.beforeState -ceq 'Draining' -and
                [string]$_.Operation.afterState -ceq 'None'
            })
        $terminalEntrants = @($boundaryGroup.Group | Where-Object {
                [string]$_.Operation.operationRole -ceq 'terminal' -and
                [string]$_.Operation.beforeState -ceq 'None' -and
                [string]$_.Operation.afterState -ceq 'Committed'
            })
        if ($currentRetirements.Count -eq 1 -and
            $terminalEntrants.Count -eq 1) {
            $atomicCommits.Add($terminalEntrants[0])
        }
    }
    $directCommits = @($stateEntries | Where-Object {
            [string]$_.Operation.beforeState -ceq 'Draining' -and
            [string]$_.Operation.afterState -ceq 'Committed'
        })
    $commitEntries = @(@($atomicCommits) + $directCommits)
    $successClosed =
        $commitEntries.Count -eq 1 -and
        (& $hasReceipt 'pause_bind' 'Installed') -and
        (& $hasReceipt 'drain' 'Completed') -and
        (& $hasReceipt 'commit' 'Committed') -and
        (& $hasReceipt 'disposition' 'Applied') -and
        (& $hasEffect 'pause_installed') -and
        (& $hasEffect 'completed_drain_receipt') -and
        (& $hasEffect 'route_commit_adopted') -and
        (& $hasEffect 'target_runtime_leg_installed') -and
        (& $hasEffect 'route_transition_disposition_applied') -and
        (& $hasEffect 'transition_pause_released') -and
        (& $hasEffect 'recovered_epoch_publication')

    if ($successClosed) {
        $successClosed = Test-FileTransferCoordinatorRouteTransitionRetainedFrameLifecycle `
            -Commit $commitEntries[0] -History $ordered -Effects $lifecycleEffects
    }

    $terminalizedWhileDraining =
        @($lifecycleEffects | Where-Object {
                [string]$_.EffectKind -ceq 'terminalized_while_draining'
            }).Count -gt 0
    $atomicInvalidations = [System.Collections.Generic.List[object]]::new()
    foreach ($boundaryGroup in @($stateEntries | Group-Object {
                [string]$_.Boundary.boundaryId
            })) {
        $currentRetirements = @($boundaryGroup.Group | Where-Object {
                [string]$_.Operation.operationRole -ceq 'current' -and
                [string]$_.Operation.beforeState -in @(
                    'Prepared', 'Draining') -and
                [string]$_.Operation.afterState -ceq 'None'
            })
        $terminalEntrants = @($boundaryGroup.Group | Where-Object {
                [string]$_.Operation.operationRole -ceq 'terminal' -and
                [string]$_.Operation.beforeState -ceq 'None' -and
                [string]$_.Operation.afterState -ceq 'Invalidated'
            })
        if ($currentRetirements.Count -eq 1 -and
            $terminalEntrants.Count -eq 1) {
            $atomicInvalidations.Add($terminalEntrants[0])
        }
    }
    $cleanInvalidation =
        $atomicInvalidations.Count -eq 1 -and
        (& $hasReceipt 'disposition' 'Applied') -and
        (& $hasEffect 'route_transition_disposition_applied') -and
        (& $hasEffect 'transition_pause_released') -and
        -not $terminalizedWhileDraining

    $rejected = @($attemptEntries | Where-Object {
            [string]$_.Boundary.decisionStatus -ceq 'Rejected' -or
            @($_.Operation.routeTransition.receipts | Where-Object {
                    [string]$_.receiptStatus -ceq 'Rejected' -and
                    -not [bool]$_.receiptValidated
                }).Count -gt 0
        })
    if ($rejected.Count -gt 0) {
        $candidate = $rejected[0]
        $candidateTime = [long]$candidate.Boundary.decisionUtcMilliseconds
        $commitTime = if ($commitEntries.Count -eq 1) {
            [long]$commitEntries[0].Boundary.decisionUtcMilliseconds
        }
        else {
            0L
        }
        $recovered = $successClosed -and $commitTime -gt $candidateTime
        return [pscustomobject]@{
            HasCandidate = $true
            Candidate = $candidate
            RawCount = $rejected.Count
            RecoveredCount = if ($recovered) { 1 } else { 0 }
            UnresolvedCount = if ($recovered) { 0 } else { 1 }
            FailureClass = 'filetransfer_route_transition_incomplete'
        }
    }

    if ($successClosed -or $cleanInvalidation) {
        return [pscustomobject]@{
            HasCandidate = $false
            Candidate = $null
            RawCount = 0
            RecoveredCount = 0
            UnresolvedCount = 0
            FailureClass = 'filetransfer_route_transition_incomplete'
        }
    }

    $candidateEntries = @($stateEntries | Where-Object {
            [string]$_.Operation.afterState -in @(
                'Prepared', 'Draining', 'Committed', 'Invalidated')
        })
    $candidate = if ($candidateEntries.Count -gt 0) {
        $candidateEntries[0]
    }
    else {
        $ordered[0]
    }
    return [pscustomobject]@{
        HasCandidate = $true
        Candidate = $candidate
        RawCount = 1
        RecoveredCount = 0
        UnresolvedCount = 1
        FailureClass = if ($terminalizedWhileDraining) {
            'filetransfer_route_transition_draining_transport_grace_terminalization'
        }
        else {
            'filetransfer_route_transition_incomplete'
        }
    }
}

function Test-FileTransferCoordinatorFallbackV6BootstrapLineageEqual {
    param(
        [Parameter(Mandatory = $true)]$Left,
        [Parameter(Mandatory = $true)]$Right
    )

    if ($null -eq $Left.routeTransition -or
        $null -eq $Right.routeTransition -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
            -Left $Left.routeTransition.sourceBoundary `
            -Right $Right.routeTransition.sourceBoundary)) {
        return $false
    }

    foreach ($name in @(
            'transitionKind', 'sourceTransport', 'proofAdmissionMode',
            'targetRoute', 'targetProtocolVersion', 'targetTransport',
            'handoffKind')) {
        if ([string]$Left.routeTransition.$name -cne
            [string]$Right.routeTransition.$name) {
            return $false
        }
    }
    return $true
}

function Get-FileTransferCoordinatorFallbackV6BootstrapLifecycle {
    param(
        [Parameter(Mandatory = $true)][object[]]$History,
        [hashtable]$CorrelatedRetirements
    )

    $ordered = @($History | Sort-Object {
            [long]$_.Boundary.decisionUtcMilliseconds
        }, {
            [long]$_.Boundary.afterStateGeneration
        }, {
            [string]$_.Boundary.boundaryId
        })
    if ($ordered.Count -eq 0) {
        return [pscustomobject]@{
            Closed = $false
            Candidate = $null
            StructuralFailure = 'history_missing'
        }
    }

    $latest = $ordered[-1]
    $latestState = [string]$latest.Operation.afterState
    $hasValidatedDelivery = @(
        $latest.Operation.routeTransition.receipts | Where-Object {
            [string]$_.stage -ceq 'fallback_v6_bootstrap' -and
            [string]$_.receiptStatus -ceq 'Delivered' -and
            [bool]$_.receiptValidated
        }).Count -eq 1

    if ($latestState -ceq 'Delivered') {
        return [pscustomobject]@{
            Closed = $hasValidatedDelivery
            Candidate = if ($hasValidatedDelivery) { $null } else { $latest }
            StructuralFailure = if ($hasValidatedDelivery) {
                ''
            }
            else {
                'delivered_receipt_missing'
            }
        }
    }
    if ($latestState -ceq 'Pending') {
        $candidate = @($ordered | Where-Object {
                [string]$_.Operation.afterState -ceq 'Pending'
            } | Select-Object -First 1)
        return [pscustomobject]@{
            Closed = $false
            Candidate = if ($candidate.Count -eq 1) {
                $candidate[0]
            }
            else {
                $latest
            }
            StructuralFailure = ''
        }
    }
    if ($latestState -cne 'None') {
        return [pscustomobject]@{
            Closed = $false
            Candidate = $latest
            StructuralFailure = 'state_invalid'
        }
    }

    $explicitInvalidation =
        [string]$latest.Boundary.decisionKind -ceq
            'InvalidateFileTransferRouteTransition' -and
        [string]$latest.Boundary.decisionStatus -ceq 'Invalidated'
    if ($explicitInvalidation) {
        return [pscustomobject]@{
            Closed = $true
            Candidate = $null
            StructuralFailure = ''
        }
    }

    if ([string]$latest.Operation.beforeState -cne 'Delivered' -or
        -not $hasValidatedDelivery) {
        return [pscustomobject]@{
            Closed = $false
            Candidate = $latest
            StructuralFailure = 'retirement_without_delivery'
        }
    }

    if ($null -ne $CorrelatedRetirements -and $latest.Boundary.decisionKind -ceq 'StartFallbackBootstrapExchange') {
        $key = "$($latest.Boundary.coordinatorInstanceId)|$($latest.Boundary.boundaryId)|$($latest.Operation.operationKey)"
        $validated = $CorrelatedRetirements.ContainsKey($key)
        return [pscustomobject]@{
            Closed = $validated
            Candidate = if ($validated) { $null } else { $latest }
            StructuralFailure = if ($validated) { '' } else { 'correlated_retirement_history_missing' }
        }
    }

    $successors = @($latest.Boundary.operations | Where-Object {
            [string]$_.family -ceq 'route_transition' -and
            [string]$_.operationRole -ceq 'fallback_v6_bootstrap' -and
            [string]$_.operationKind -ceq 'FallbackV6ProofBootstrap' -and
            [string]$_.authorityId -cne
                [string]$latest.Operation.authorityId -and
            [long]$_.operationGeneration -gt
                [long]$latest.Operation.operationGeneration -and
            [string]$_.beforeState -ceq 'None' -and
            [string]$_.afterState -ceq 'Pending' -and
            (Test-FileTransferCoordinatorFallbackV6BootstrapLineageEqual `
                -Left $latest.Operation `
                -Right $_)
        })
    if ($successors.Count -eq 1) {
        return [pscustomobject]@{
            Closed = $true
            Candidate = $null
            StructuralFailure = ''
        }
    }

    return [pscustomobject]@{
        Closed = $false
        Candidate = $latest
        StructuralFailure = if ($successors.Count -eq 0) {
            'successor_missing_or_mutated'
        }
        else {
            'successor_ambiguous'
        }
    }
}

function Get-FileTransferCoordinatorSnapshotEvaluation {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, ParameterSetName = 'Lines')]
        [AllowEmptyCollection()][string[]]$Lines,
        [Parameter(Mandatory = $true, ParameterSetName = 'SnapshotRecords')]
        [AllowEmptyCollection()][object[]]$SnapshotRecords
    )

    $catalog = Get-FileTransferCoordinatorEvidenceCatalog
    $records = [System.Collections.Generic.List[object]]::new()
    if ($PSCmdlet.ParameterSetName -ceq 'SnapshotRecords') {
        foreach ($candidate in @($SnapshotRecords)) {
            $line = [string]$candidate.Line
            $lineIndex = [long]$candidate.LineIndex
            $eventName = Get-FileTransferCoordinatorSnapshotEventName -Line $line
            if ($catalog.SnapshotAuthorityV1 -notcontains $eventName -and
                $catalog.GenerationStampedEffects -notcontains $eventName) {
                continue
            }
            $records.Add([pscustomobject]@{
                    LineIndex = $lineIndex
                    Line = $line
                    EventName = $eventName
                    CoordinatorInstanceId = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'coordinator_instance_id'
                })
        }
    }
    else {
        for ($index = 0; $index -lt $Lines.Count; $index++) {
            $line = [string]$Lines[$index]
            $eventName = Get-FileTransferCoordinatorSnapshotEventName -Line $line
            if ($catalog.SnapshotAuthorityV1 -notcontains $eventName -and
                $catalog.GenerationStampedEffects -notcontains $eventName) {
                continue
            }
            $records.Add([pscustomobject]@{
                    LineIndex = $index
                    Line = $line
                    EventName = $eventName
                    CoordinatorInstanceId = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'coordinator_instance_id'
                })
        }
    }

    if ($records.Count -eq 0) {
        return [pscustomobject]@{
            Mode = 'LegacyV0'
            ContractVersion = 0
            CatalogVersion = $catalog.LegacyV0.Version
            CatalogHash = $catalog.LegacyV0.Hash
            StreamComplete = $false
            ArtifactEvidenceVerdict = 'pass'
            ArtifactEvidenceFailureReason = ''
            TransitionCount = 0
            EffectCount = 0
            BoundaryCount = 0
            BoundaryHash = ''
            Boundaries = @()
            FirstUnresolvedBoundary = $null
            LegacySelectionCount = 1
            RouteFamilyLegacySelectionCount = 2
            RuntimeUnlockRetryFamilyLegacySelectionCount = 1
            ReceiveRecoveryFamilyLegacySelectionCount = 1
            FallbackTailYieldFamilyLegacySelectionCount = 1
            RegularV4FrontierRepairPressureFamilyLegacySelectionCount = 1
            ResponderProbeHandoffFamilyLegacySelectionCount = 1
        }
    }

    $errors = [System.Collections.Generic.List[string]]::new()
    $selectedCatalogIdentity = $null
    foreach ($record in $records) {
        $profileVersion = Get-FileTransferCoordinatorSnapshotLineField -Line $record.Line -Name 'snapshot_catalog_version'
        $profileHash = Get-FileTransferCoordinatorSnapshotLineField -Line $record.Line -Name 'snapshot_catalog_hash'
        $profileIdentity = "$profileVersion|$profileHash"
        if ($null -eq $selectedCatalogIdentity) {
            $selectedCatalogIdentity = $profileIdentity
            if ($profileVersion -cin @($script:FileTransferCoordinatorReceiverBarrierCatalogVersion,
                    $script:FileTransferCoordinatorCheckpointRepairContinuityCatalogVersion,
                    $script:FileTransferCoordinatorBootstrapCorrelationCatalogVersion,
                    $script:FileTransferCoordinatorBootstrapInitiationCatalogVersion, $script:FileTransferCoordinatorBootstrapCallbackCatalogVersion)) {
                $catalog = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $profileVersion
            }
        }
        elseif ($selectedCatalogIdentity -cne $profileIdentity) {
            $errors.Add("snapshot_catalog_profile_conflict:$($record.LineIndex)")
        }
    }
    $boundaries = [System.Collections.Generic.List[object]]::new()
    $aggregateEntries = [System.Collections.Generic.List[object]]::new()
    $retryConfirmationCompensations = @{}
    $pressureTerminalRolloverCandidates = @{}
    $checkpointStalePeerProofCandidates = @{}
    $checkpointPeerProofSuccessorCandidates = @{}
    $failedCheckpointYieldCandidates = @{}
    $failedQuiescentSupersessionCandidates = @{}
    $rejectedFailedQuiescentSupersessions = @{}
    $failedCheckpointYieldHistoryObservations = [System.Collections.Generic.List[object]]::new()
    $receiveRecoveryRouteCarryCandidates = @{}
    $effects = [System.Collections.Generic.List[object]]::new()
    $seenTransitions = @{}
    $seenEffects = @{}
    $seenOneShotEffectIdentities = @{}
    $streams = @{}
    $selectionModes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

    foreach ($record in @($records)) {
        $transitionErrorCount = $errors.Count
        $line = $record.Line
        $instanceId = $record.CoordinatorInstanceId
        if ([string]::IsNullOrWhiteSpace($instanceId)) {
            $errors.Add("snapshot_instance_missing:$($record.LineIndex)")
            continue
        }
        if (-not $streams.ContainsKey($instanceId)) {
            $streams[$instanceId] = [pscustomobject]@{
                Started = $false
                Completed = $false
                InitialGeneration = 0L
                InitialHash = ''
                CurrentGeneration = 0L
                CurrentHash = ''
                TransitionCount = 0L
                EmittedFamilies = @()
                ShadowFamilies = @()
                AuthoritativeFamilies = @()
                SessionId = ''
                TransferId = ''
                Direction = ''
            }
        }
        $stream = $streams[$instanceId]
        $recordSessionId = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'session_id'
        $recordTransferId = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'transfer_id'
        $recordDirection = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'direction'
        if ([string]::IsNullOrWhiteSpace($recordSessionId) -or
            [string]::IsNullOrWhiteSpace($recordTransferId) -or
            [string]::IsNullOrWhiteSpace($recordDirection)) {
            $errors.Add("snapshot_stream_scope_missing:$($record.LineIndex)")
        }
        elseif ([string]::IsNullOrWhiteSpace($stream.SessionId)) {
            $stream.SessionId = $recordSessionId
            $stream.TransferId = $recordTransferId
            $stream.Direction = $recordDirection
        }
        elseif ($stream.SessionId -cne $recordSessionId -or
            $stream.TransferId -cne $recordTransferId -or
            $stream.Direction -cne $recordDirection) {
            $errors.Add("snapshot_stream_scope_conflict:$($record.LineIndex)")
        }
        $contractVersion = 0
        if (-not [int]::TryParse(
                (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'snapshot_contract_version'),
                [ref]$contractVersion) -or
            $contractVersion -ne $script:FileTransferCoordinatorSnapshotContractVersion) {
            $errors.Add("snapshot_contract_version_unknown:$($record.LineIndex)")
            continue
        }
        $catalogVersion = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'snapshot_catalog_version'
        $catalogHash = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'snapshot_catalog_hash'
        if ($catalogVersion -cne $catalog.Version -or $catalogHash -cne $catalog.Hash) {
            $errors.Add("snapshot_catalog_mismatch:$($record.LineIndex)")
        }
        $selectionMode = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'snapshot_selection_mode'
        if ($selectionMode -notin @(
                'SnapshotV1Shadow',
                'SnapshotV1Authoritative')) {
            $errors.Add("snapshot_selection_mode_unknown:$($record.LineIndex)")
        }
        else {
            [void]$selectionModes.Add($selectionMode)
        }

        switch ($record.EventName) {
            'filetransfer_coordinator_snapshot_stream_started' {
                if ($stream.Started) {
                    $duplicateGeneration = 0L
                    [void][long]::TryParse(
                        (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'initial_state_generation'),
                        [ref]$duplicateGeneration)
                    $duplicateHash = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'initial_state_hash'
                    $duplicateEmitted = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'emitted_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                    $duplicateShadow = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'shadow_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                    $duplicateAuthoritative = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'authoritative_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                    if ($duplicateGeneration -ne $stream.InitialGeneration -or
                        $duplicateHash -cne $stream.InitialHash -or
                        (@($duplicateEmitted | Sort-Object) -join ',') -cne (@($stream.EmittedFamilies | Sort-Object) -join ',') -or
                        (@($duplicateShadow | Sort-Object) -join ',') -cne (@($stream.ShadowFamilies | Sort-Object) -join ',') -or
                        (@($duplicateAuthoritative | Sort-Object) -join ',') -cne (@($stream.AuthoritativeFamilies | Sort-Object) -join ',')) {
                        $errors.Add("snapshot_stream_start_conflicting_duplicate:$instanceId")
                    }
                    break
                }
                $generation = 0L
                if (-not [long]::TryParse(
                        (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'initial_state_generation'),
                        [ref]$generation)) {
                    $errors.Add("snapshot_initial_generation_invalid:$instanceId")
                    break
                }
                $stream.Started = $true
                $stream.InitialGeneration = $generation
                $stream.CurrentGeneration = $generation
                $stream.InitialHash = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'initial_state_hash'
                $stream.CurrentHash = $stream.InitialHash
                $stream.EmittedFamilies = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'emitted_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                $stream.ShadowFamilies = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'shadow_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                $stream.AuthoritativeFamilies = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'authoritative_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                if ([string]::IsNullOrWhiteSpace($stream.InitialHash)) {
                    $errors.Add("snapshot_initial_hash_missing:$instanceId")
                }
                $distinctFamilies = @($stream.EmittedFamilies | Sort-Object -Unique)
                $unknownFamilies = @(
                    $distinctFamilies | Where-Object {
                        $script:FileTransferCoordinatorSnapshotRequiredFamilies -notcontains $_
                    })
                $invalidShadow = @($stream.ShadowFamilies | Where-Object { $stream.EmittedFamilies -notcontains $_ })
                $invalidAuthoritative = @($stream.AuthoritativeFamilies | Where-Object { $stream.EmittedFamilies -notcontains $_ })
                $overlappingCoverage = @(
                    $stream.ShadowFamilies | Where-Object {
                        $stream.AuthoritativeFamilies -contains $_
                    })
                if ($stream.EmittedFamilies.Count -eq 0 -or
                    $distinctFamilies.Count -ne $stream.EmittedFamilies.Count -or
                    $unknownFamilies.Count -gt 0 -or
                    $invalidShadow.Count -gt 0 -or
                    $invalidAuthoritative.Count -gt 0 -or
                    $overlappingCoverage.Count -gt 0) {
                    $errors.Add("snapshot_declared_coverage_invalid:$instanceId")
                }
            }
            'filetransfer_coordinator_transition_snapshot' {
                if (-not $stream.Started -or $stream.Completed) {
                    $errors.Add("snapshot_transition_outside_stream:$($record.LineIndex)")
                    break
                }
                $transitionId = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'transition_id'
                $payload = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'snapshot_payload'
                $declaredHash = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'snapshot_sha256'
                if ([string]::IsNullOrWhiteSpace($transitionId) -or
                    [string]::IsNullOrWhiteSpace($payload) -or
                    [string]::IsNullOrWhiteSpace($declaredHash)) {
                    $errors.Add("snapshot_transition_identity_incomplete:$($record.LineIndex)")
                    break
                }
                try {
                    $decoded = ConvertFrom-FileTransferCoordinatorSnapshotPayload -Payload $payload
                }
                catch {
                    $errors.Add("snapshot_payload_invalid:$($record.LineIndex)")
                    break
                }
                $actualHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $decoded.Bytes
                if ($actualHash -ne $declaredHash) {
                    $errors.Add("snapshot_payload_hash_mismatch:$($record.LineIndex)")
                    break
                }
                $envelope = $decoded.Value
                if ([int]$envelope.snapshotContractVersion -ne $contractVersion -or
                    [string]$envelope.coordinatorInstanceId -cne $instanceId -or
                    [string]$envelope.transitionId -ne $transitionId -or
                    [string]$envelope.snapshotSelectionMode -cne $selectionMode -or
                    [string]$envelope.sessionId -cne $recordSessionId -or
                    [string]$envelope.transferId -cne $recordTransferId -or
                    [string]$envelope.direction -cne $recordDirection) {
                    $errors.Add("snapshot_header_payload_identity_mismatch:$($record.LineIndex)")
                    break
                }
                $beforeGeneration = [long]$envelope.aggregate.beforeStateGeneration
                $afterGeneration = [long]$envelope.aggregate.afterStateGeneration
                $beforeHash = [string]$envelope.aggregate.beforeStateFingerprint
                $afterHash = [string]$envelope.aggregate.afterStateFingerprint
                $headerBeforeGeneration = 0L
                $headerAfterGeneration = 0L
                if (-not [long]::TryParse(
                        (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'before_state_generation'),
                        [ref]$headerBeforeGeneration) -or
                    -not [long]::TryParse(
                        (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'after_state_generation'),
                        [ref]$headerAfterGeneration) -or
                    $headerBeforeGeneration -ne $beforeGeneration -or
                    $headerAfterGeneration -ne $afterGeneration) {
                    $errors.Add("snapshot_header_payload_generation_mismatch:$($record.LineIndex)")
                    break
                }
                $failedCheckpointYieldHistoryObservations.Add([pscustomobject]@{
                    boundaryId = $transitionId; coordinatorInstanceId = $instanceId
                    writer = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'log_writer_id'
                    beforeStateGeneration = $beforeGeneration
                })
                $transitionKey = "$instanceId|$transitionId"
                if ($seenTransitions.ContainsKey($transitionKey)) {
                    if ($seenTransitions[$transitionKey] -ne $actualHash) {
                        $errors.Add("snapshot_transition_conflicting_duplicate:$transitionId")
                    }
                    break
                }
                $seenTransitions[$transitionKey] = $actualHash
                if ($beforeGeneration -ne $stream.CurrentGeneration -or $beforeHash -ne $stream.CurrentHash) {
                    $errors.Add("snapshot_generation_or_hash_gap:$($record.LineIndex)")
                    break
                }
                $stateChanged = $beforeHash -ne $afterHash
                if (($stateChanged -and $afterGeneration -ne ($beforeGeneration + 1L)) -or
                    (-not $stateChanged -and $afterGeneration -ne $beforeGeneration)) {
                    $errors.Add("snapshot_generation_transition_invalid:$($record.LineIndex)")
                    break
                }
                $decisionStatus = [string]$envelope.decisionStatus
                if ([string]::IsNullOrWhiteSpace($decisionStatus) -or
                    ($stateChanged -and $decisionStatus -in @('Idempotent', 'Rejected')) -or
                    (-not $stateChanged -and $decisionStatus -eq 'Accepted')) {
                    $errors.Add("snapshot_decision_state_transition_invalid:$($record.LineIndex)")
                    break
                }
                if ([string]$envelope.decisionKind -ceq 'FallbackTailFailed' -and
                    @($envelope.aggregate.operations).Count -eq 0) {
                    $errors.Add('snapshot_operation_catalog_mismatch|' +
                        (Get-FileTransferCoordinatorSnapshotFamily4CatalogVariant $envelope) + ':' + $record.LineIndex)
                }
                if ([string]$envelope.decisionKind -ceq
                        'FallbackTailQuiescent' -and
                    [string]$envelope.decisionStatus -ceq 'Accepted' -and
                    -not (Test-FileTransferCoordinatorSnapshotFallbackTailQuiescentEnvelope `
                        -Envelope $envelope)) {
                    $errors.Add(
                        'snapshot_operation_catalog_mismatch|' +
                        (Get-FileTransferCoordinatorSnapshotFamily4CatalogVariant $envelope) + ':' +
                        $record.LineIndex)
                    break
                }
                if ([string]$envelope.decisionKind -ceq
                        'ApplyFallbackTailPeerProofReceipt' -and
                    [string]$envelope.decisionReason -ceq
                        'fallback_tail_initial_peer_proof_receipt_superseded_after_checkpoint_advance') {
                    if (-not (Test-FileTransferCoordinatorSnapshotCheckpointPeerProofSuccessor `
                            -Envelope $envelope)) {
                        $errors.Add(
                            'snapshot_operation_catalog_mismatch|' +
                            'current_checkpoint_peer_proof_successor_transition_invalid:' +
                            $record.LineIndex)
                        break
                    }
                    $successorOperations = @($envelope.aggregate.operations | Where-Object {
                            [string]$_.family -ceq 'fallback_tail_yield' -and
                            [string]$_.operationRole -ceq 'peer_proof_pending_initial'
                        })
                    $checkpointPeerProofSuccessorCandidates[$transitionId] = @{
                        CoordinatorInstanceId = [string]$envelope.coordinatorInstanceId
                        Writer = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'log_writer_id'
                        Retired = @($successorOperations | Where-Object {
                                [string]$_.beforeState -ceq 'Pending' -and
                                [string]$_.afterState -ceq 'None'
                            })[0]
                        Entered = @($successorOperations | Where-Object {
                                [string]$_.beforeState -ceq 'None' -and
                                [string]$_.afterState -ceq 'Pending'
                            })[0]
                        Line = $record.LineIndex
                    }
                }
                if ([string]$envelope.decisionKind -ceq 'FallbackTailQuiescent' -and
                    (Test-FileTransferCoordinatorSnapshotCheckpointStalePeerProofRetirement $envelope)) {
                    $checkpointStalePeerProofCandidates[$transitionId] = @{
                        Yield = @($envelope.aggregate.operations | Where-Object { $_.operationRole -ceq 'yield_current' })[0]
                        Line = $record.LineIndex
                    }
                }
                if (Test-FileTransferCoordinatorSnapshotFailedCheckpointYieldCandidate $envelope) {
                    $failedCheckpointYieldCandidates[$transitionId] = @{ Line = $record.LineIndex }
                }
                if ([string]$envelope.decisionKind -ceq 'FallbackTailQuiescent' -and
                    [string]$envelope.decisionStatus -ceq 'Accepted' -and
                    $null -ne (Get-FileTransferCoordinatorSnapshotFallbackTailFailedSupersessionShape `
                        -Operations @($envelope.aggregate.operations))) {
                    $failedQuiescentSupersessionCandidates[$transitionId] = @{ Line = $record.LineIndex }
                }
                foreach ($operation in @($envelope.aggregate.operations)) {
                    if ([string]$operation.family -ceq 'regular_v4_frontier_repair_pressure' -and
                        [string]$operation.operationRole -ceq 'recovery_handoff_terminal' -and
                        ([string]$operation.beforeState -ceq 'Committed' -or
                         [string]$operation.afterState -ceq 'Committed') -and
                        -not (Test-FileTransferCoordinatorSnapshotRecoveryHandoffTerminalReceipt -Operation $operation)) {
                        $errors.Add(
                            "snapshot_regular_v4_pressure_recovery_handoff_terminal_receipt_invalid|retained_recovery_handoff_receipt_replaced_by_unrelated_last_settlement:$($record.LineIndex)")
                    }
                }
                foreach ($operation in @($envelope.aggregate.operations)) {
                    if ([string]::IsNullOrWhiteSpace([string]$operation.family) -or
                        [string]::IsNullOrWhiteSpace([string]$operation.operationRole) -or
                        [string]::IsNullOrWhiteSpace([string]$operation.operationKey) -or
                        [string]::IsNullOrWhiteSpace([string]$operation.operationKind) -or
                        [string]::IsNullOrWhiteSpace([string]$operation.authorityId) -or
                        [long]$operation.operationGeneration -le 0 -or
                        [string]::IsNullOrWhiteSpace([string]$operation.commandIdentity) -or
                        [long]$operation.commandGeneration -le 0 -or
                        [string]::IsNullOrWhiteSpace([string]$operation.commandFingerprint) -or
                        [string]::IsNullOrWhiteSpace([string]$operation.receiptIdentity) -or
                        [string]::IsNullOrWhiteSpace([string]$operation.receiptStatus) -or
                        [string]::IsNullOrWhiteSpace([string]$operation.receiptFingerprint)) {
                        $errors.Add("snapshot_operation_identity_incomplete:$($record.LineIndex)")
                        break
                    }
                    if ((Test-FileTransferCoordinatorBootstrapCallbackMarker $operation) -or
                        ($catalog.Version -ceq $script:FileTransferCoordinatorBootstrapCallbackCatalogVersion -and $operation.operationRole -ceq 'fallback_bootstrap_source_hold')) {
                        if (-not (Test-FileTransferCoordinatorBootstrapCallbackOperation $envelope $operation $catalog)) {
                            $errors.Add("snapshot_bootstrap_callback_operation_invalid:$($record.LineIndex)")
                            break
                        }
                        if ($operation.operationRole -ceq 'fallback_bootstrap_source_callback') { continue }
                    }
                    if (Test-FileTransferCoordinatorBootstrapInitiationMarker $operation) {
                        if (-not (Test-FileTransferCoordinatorBootstrapInitiationOperation $envelope $operation $catalog)) {
                            $errors.Add("snapshot_bootstrap_initiation_operation_invalid:$($record.LineIndex)")
                            break
                        }
                        if ($operation.operationRole -ceq 'fallback_bootstrap_source_hold') { continue }
                    }
                    if (Test-FileTransferCoordinatorBootstrapCorrelationMarker $operation) {
                        if (-not (Test-FileTransferCoordinatorBootstrapCorrelationOperation $envelope $operation $catalog)) {
                            $errors.Add("snapshot_bootstrap_correlation_operation_invalid:$($record.LineIndex)")
                            break
                        }
                        continue
                    }
                    if (Test-FileTransferCoordinatorCheckpointRepairContinuityMarker $operation) {
                        if (-not (Test-FileTransferCoordinatorCheckpointRepairContinuityOperation $envelope $operation $catalog)) {
                            $continuityReason = 'snapshot_checkpoint_repair_continuity_operation_invalid'
                            if ($envelope.decisionKind -ceq 'CommitFileTransferRouteTransition' -and
                                $envelope.decisionStatus -ceq 'Committed' -and
                                $operation.operationRole -ceq 'checkpoint_acceptance_retained') {
                                $continuityReason += '|committed_route_transition_checkpoint_continuity_retirement_invalid'
                            }
                            $errors.Add("$continuityReason`:$($record.LineIndex)")
                            break
                        }
                        continue
                    }
                    if (Test-FileTransferCoordinatorReceiverBarrierMarker $operation) {
                        if (-not (Test-FileTransferCoordinatorReceiverBarrierOperation $envelope $operation $catalog)) {
                            $errors.Add("snapshot_receiver_barrier_operation_invalid:$($record.LineIndex)")
                            break
                        }
                        continue
                    }
                    if (-not (Test-FileTransferCoordinatorSnapshotFamily1OperationCatalog `
                            -Envelope $envelope `
                            -Operation $operation -Catalog $catalog)) {
                        $attemptVariant =
                            Get-FileTransferCoordinatorSnapshotFamily1AttemptReceiptStageVariant `
                                -Envelope $envelope `
                                -Operation $operation
                        if ($attemptVariant -ceq '(none)') {
                            $attemptVariant =
                                Get-FileTransferCoordinatorSnapshotCheckpointCatalogVariant `
                                    -Envelope $envelope `
                                    -Operation $operation
                        }
                        if ([string]$envelope.decisionKind -in @('IssueReceiveRecoveryOperation', 'ObserveReceiveRecoveryProof') -and
                            [string]$operation.family -ceq 'route_transition') {
                            $attemptVariant = 'receive_recovery_bridge_scope_carry_omitted_from_family1_catalog'
                        }
                        $reason = 'snapshot_operation_catalog_mismatch'
                        if ($attemptVariant -cne '(none)') {
                            $reason = "$reason|$attemptVariant"
                        }
                        $errors.Add("$reason`:$($record.LineIndex)")
                        break
                    }
                    if ([string]$envelope.decisionKind -in @('IssueReceiveRecoveryOperation', 'ObserveReceiveRecoveryProof') -and
                        [string]$operation.family -ceq 'route_transition') {
                        $receiveRecoveryRouteCarryCandidates[$transitionId] = @{ Line = $record.LineIndex }
                    }
                    if (-not (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog `
                            -Envelope $envelope `
                            -Operation $operation)) {
                        $variant =
                            Get-FileTransferCoordinatorSnapshotCheckpointCatalogVariant `
                                -Envelope $envelope `
                                -Operation $operation
                        if ($null -ne $operation.runtimeUnlockRetry -and
                            @($operation.runtimeUnlockRetry.receipts | Where-Object {
                                [string]$_.stage -ceq 'install' -and
                                [string]$_.receiptStatus -ceq 'Staged'
                            }).Count -gt 0) {
                            $variant = 'family2_staged_install_receipt_omitted_from_catalog'
                        }
                        if ($null -ne $operation.runtimeUnlockRetry -and
                            (([string]$operation.operationKind -ceq 'ScopeInvalidation' -and
                              [string]$operation.afterState -ceq 'Pending') -or
                             @($operation.runtimeUnlockRetry.receipts | Where-Object {
                                 [string]$_.stage -ceq 'scope_invalidation' -and [string]$_.receiptStatus -ceq 'Pending'
                             }).Count -gt 0)) {
                            $variant = 'family2_pending_scope_invalidation_command_validated_as_completed_receipt'
                        }
                        elseif ([string]$operation.operationRole -ceq 'scope_invalidation' -and
                            ([string]$operation.beforeState -ceq 'Applied' -or [string]$operation.afterState -ceq 'Applied')) {
                            $variant = 'family2_applied_scope_invalidation_state_omitted_from_lifecycle_catalog'
                        }
                        if ($envelope.decisionKind -cin @('CommitRuntimeUnlockRetryDispatchInstall',
                                'CompensateRejectedRuntimeUnlockRetryDispatchConfirmation') -and
                            @($envelope.aggregate.operations | Where-Object {
                            $_.family -ceq 'runtime_unlock_retry_lifecycle' -and
                            $_.operationRole -ceq 'attempt_retained_predecessor' -and
                            $_.operationKind -ceq 'RetryAttempt' -and $null -ne $_.runtimeUnlockRetry -and
                            @($_.runtimeUnlockRetry.receipts | Where-Object {
                                $_.stage -ceq 'settlement' -and $_.receiptStatus -ceq 'Superseded'
                            }).Count -gt 0
                        }).Count -gt 0) {
                            $variant = 'provisional_active_predecessor_superseded_receipt_omitted_from_family2_catalog'
                        }
                        $reason = 'snapshot_operation_catalog_mismatch'
                        if ($variant -cne '(none)') {
                            $reason = "$reason|$variant"
                        }
                        $errors.Add("$reason`:$($record.LineIndex)")
                        break
                    }
                    if (-not (Test-FileTransferCoordinatorSnapshotFamily3OperationCatalog `
                            -Envelope $envelope `
                            -Operation $operation)) {
                        $variant =
                            Get-FileTransferCoordinatorSnapshotCheckpointCatalogVariant `
                                -Envelope $envelope `
                                -Operation $operation
                        $reason = 'snapshot_operation_catalog_mismatch'
                        if ($variant -cne '(none)') {
                            $reason = "$reason|$variant"
                        }
                        $errors.Add("$reason`:$($record.LineIndex)")
                        break
                    }
                    if (-not (Test-FileTransferCoordinatorSnapshotFamily4OperationCatalog `
                            -Envelope $envelope `
                            -Operation $operation)) {
                        $variant =
                            Get-FileTransferCoordinatorSnapshotFamily4CatalogVariant `
                                -Envelope $envelope -Operation $operation
                        if ($variant -ceq '(none)') {
                            $variant =
                                Get-FileTransferCoordinatorSnapshotCheckpointCatalogVariant `
                                    -Envelope $envelope `
                                    -Operation $operation
                        }
                        $reason = 'snapshot_operation_catalog_mismatch'
                        if ($variant -cne '(none)') {
                            $reason = "$reason|$variant"
                        }
                        $errors.Add("$reason`:$($record.LineIndex)")
                        break
                    }
                    if (-not (Test-FileTransferCoordinatorSnapshotFamily5OperationCatalog `
                            -Envelope $envelope `
                            -Operation $operation)) {
                        $variant =
                            Get-FileTransferCoordinatorSnapshotCheckpointCatalogVariant `
                                -Envelope $envelope `
                                -Operation $operation
                        $reason = 'snapshot_operation_catalog_mismatch'
                        if ($variant -cne '(none)') {
                            $reason = "$reason|$variant"
                        }
                        $errors.Add("$reason`:$($record.LineIndex)")
                        break
                    }
                    if (-not (Test-FileTransferCoordinatorSnapshotFamily6OperationCatalog `
                            -Envelope $envelope `
                            -Operation $operation)) {
                        $variant = Get-FileTransferCoordinatorSnapshotFamily6CatalogVariant `
                            -Envelope $envelope -Operation $operation
                        $reason = 'snapshot_operation_catalog_mismatch'
                        if ($variant -cne '(none)') { $reason += "|$variant" }
                        $errors.Add("$reason`:$($record.LineIndex)")
                        break
                    }
                    if ([string]$operation.family -ceq 'route_runtime' -and
                        ($null -eq $operation.routeRuntime -or
                         -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                            -Boundary $operation.routeRuntime.boundary)) -or
                        [string]$operation.family -ceq 'route_transition' -and
                        ($null -eq $operation.routeTransition -or
                         -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                            -Boundary $operation.routeTransition.sourceBoundary)) -or
                        [string]$operation.family -ceq 'runtime_unlock_retry_lifecycle' -and
                        ($null -eq $operation.runtimeUnlockRetry -or
                         -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                            -Boundary $operation.runtimeUnlockRetry.sourceBoundary)) -or
                        [string]$operation.family -ceq 'receive_recovery' -and
                        ($null -eq $operation.receiveRecovery -or
                         -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                            -Boundary $operation.receiveRecovery.sourceBoundary) -or
                         -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                            -Boundary $operation.receiveRecovery.effectiveBoundary)) -or
                        [string]$operation.family -ceq 'fallback_tail_yield' -and
                        ($null -eq $operation.fallbackTailYield -or
                         -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                            -Boundary $operation.fallbackTailYield.sourceBoundary) -or
                         -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                            -Boundary $operation.fallbackTailYield.effectiveBoundary) -or
                         -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                            -Boundary $operation.fallbackTailYield.targetBoundary)) -or
                        [string]$operation.family -ceq
                            'regular_v4_frontier_repair_pressure' -and
                        ($null -eq $operation.regularV4FrontierRepairPressure -or
                         -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                            -Boundary $operation.regularV4FrontierRepairPressure.sourceBoundary) -or
                         -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                            -Boundary $operation.regularV4FrontierRepairPressure.effectiveBoundary)) -or
                        [string]$operation.family -ceq
                            'responder_probe_handoff' -and
                        ($null -eq $operation.responderProbeHandoff -or
                         -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                            -Boundary $operation.responderProbeHandoff.sourceBoundary) -or
                         -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                            -Boundary $operation.responderProbeHandoff.effectiveBoundary) -or
                         -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                            -Boundary $operation.responderProbeHandoff.targetBoundary))) {
                        $errors.Add("snapshot_operation_scope_incomplete:$($record.LineIndex)")
                        break
                    }
                    if ([string]$operation.family -ceq 'route_transition') {
                        foreach ($receipt in @($operation.routeTransition.receipts)) {
                            if ([string]::IsNullOrWhiteSpace([string]$receipt.stage) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.commandIdentity) -or
                                [long]$receipt.commandGeneration -le 0 -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.commandFingerprint) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptIdentity) -or
                                [long]$receipt.receiptGeneration -le 0 -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptStatus) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptFingerprint)) {
                                $errors.Add("snapshot_operation_receipt_incomplete:$($record.LineIndex)")
                                break
                            }
                        }
                    }
                    if ([string]$operation.family -ceq
                        'runtime_unlock_retry_lifecycle') {
                        foreach ($receipt in @($operation.runtimeUnlockRetry.receipts)) {
                            if ([string]$receipt.stage -ceq 'scope_invalidation' -and
                                [string]$receipt.receiptStatus -ceq 'Pending' -and
                                (Test-FileTransferCoordinatorSnapshotPendingScopeInvalidationStage `
                                    -Envelope $envelope -Operation $operation)) { continue }
                            if ([string]::IsNullOrWhiteSpace([string]$receipt.stage) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.commandIdentity) -or
                                [long]$receipt.commandGeneration -le 0 -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.commandFingerprint) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptIdentity) -or
                                [long]$receipt.receiptGeneration -le 0 -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptStatus) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptFingerprint)) {
                                $errors.Add("snapshot_operation_receipt_incomplete:$($record.LineIndex)")
                                break
                            }
                        }
                    }
                    if ([string]$operation.family -ceq 'receive_recovery') {
                        foreach ($receipt in @($operation.receiveRecovery.receipts)) {
                            if ([string]::IsNullOrWhiteSpace([string]$receipt.stage) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.commandIdentity) -or
                                [long]$receipt.commandGeneration -le 0 -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.commandFingerprint) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptIdentity) -or
                                [long]$receipt.receiptGeneration -le 0 -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptStatus) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptFingerprint)) {
                                $errors.Add("snapshot_operation_receipt_incomplete:$($record.LineIndex)")
                                break
                            }
                        }
                    }
                    if ([string]$operation.family -ceq 'fallback_tail_yield') {
                        if (-not (Test-FileTransferCoordinatorSnapshotFamily4StageEvidence `
                                -Operation $operation)) {
                            $errors.Add("snapshot_operation_receipt_incomplete:$($record.LineIndex)")
                        }
                    }
                    if ([string]$operation.family -ceq
                        'regular_v4_frontier_repair_pressure') {
                        foreach ($receipt in @(
                                $operation.regularV4FrontierRepairPressure.receipts)) {
                            if ([string]::IsNullOrWhiteSpace([string]$receipt.stage) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.commandIdentity) -or
                                [long]$receipt.commandGeneration -le 0 -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.commandFingerprint) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptIdentity) -or
                                [long]$receipt.receiptGeneration -le 0 -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptStatus) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptFingerprint)) {
                                $errors.Add("snapshot_operation_receipt_incomplete:$($record.LineIndex)")
                                break
                            }
                        }
                    }
                    if ([bool]$operation.receiptValidated -and
                        ([string]$operation.receiptIdentity -ceq '(none)' -or
                         [string]$operation.receiptStatus -ceq '(none)' -or
                         [string]$operation.receiptFingerprint -ceq '(none)' -or
                         [long]$operation.receiptGeneration -le 0)) {
                        $errors.Add("snapshot_operation_validated_receipt_incomplete:$($record.LineIndex)")
                        break
                    }
                }
                $repairReplayFailure =
                    Get-FileTransferCoordinatorSnapshotFamily5RepairReplayFailure `
                        -Envelope $envelope
                if (-not [string]::IsNullOrWhiteSpace($repairReplayFailure)) {
                    $errors.Add("$repairReplayFailure`:$($record.LineIndex)")
                }
                $pressureSettlementFailure =
                    Get-FileTransferCoordinatorSnapshotFamily5PressureSettlementFailure `
                        -Envelope $envelope
                if ($pressureSettlementFailure -ceq
                    ('snapshot_regular_v4_pressure_settlement_transition_invalid|' +
                     'terminal_settlement_rollover_compared_historical_source_with_current_route_source')) {
                    $pressureTerminalRolloverCandidates[$transitionId] = @{
                        Failure = $pressureSettlementFailure; LineIndex = $record.LineIndex }
                }
                elseif (-not [string]::IsNullOrWhiteSpace(
                        $pressureSettlementFailure)) {
                    $errors.Add(
                        "$pressureSettlementFailure`:$($record.LineIndex)")
                }
                $decisionUtcMilliseconds = 0L
                if ($envelope.PSObject.Properties.Name -contains 'decisionUtcMilliseconds') {
                    $decisionUtcMilliseconds = [long]$envelope.decisionUtcMilliseconds
                }
                $boundary = [ordered]@{
                    boundaryId = $transitionId
                    boundaryHash = $actualHash
                    coordinatorInstanceId = $instanceId
                    sessionId = [string]$envelope.sessionId
                    transferId = [string]$envelope.transferId
                    direction = [string]$envelope.direction
                    evidenceLineIndex = [int]$record.LineIndex
                    writer = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'log_writer_id'
                    writerSequence = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'log_writer_sequence'
                    decisionUtcMilliseconds = $decisionUtcMilliseconds
                    beforeStateGeneration = $beforeGeneration
                    afterStateGeneration = $afterGeneration
                    beforeStateHash = $beforeHash
                    afterStateHash = $afterHash
                    beforeBoundary = $envelope.aggregate.beforeBoundary
                    afterBoundary = $envelope.aggregate.afterBoundary
                    decisionKind = [string]$envelope.decisionKind
                    decisionStatus = $decisionStatus
                    decisionReason = [string]$envelope.decisionReason
                    families = @($envelope.aggregate.changedFamilies)
                    operations = @($envelope.aggregate.operations)
                    verdict = 'pass'
                    taxonomyClass = '(none)'
                    domain = 'route_runtime'
                    phase = 'coordinator_transition'
                    rawCount = 0
                    distinctCount = 0
                    recoveredCount = 0
                    unresolvedCount = 0
                }
                $boundaries.Add([pscustomobject]$boundary)
                if ($envelope.decisionKind -ceq 'CompensateRejectedRuntimeUnlockRetryDispatchConfirmation') {
                    if (-not (Test-FileTransferCoordinatorRetryConfirmationCompensationHistory `
                            -Envelope $envelope -Boundary ([pscustomobject]$boundary) -PriorEntries @($aggregateEntries))) {
                        $errors.Add("snapshot_retry_confirmation_compensation_invalid:$($record.LineIndex)")
                    }
                    elseif ($errors.Count -eq $transitionErrorCount) {
                        $retryConfirmationCompensations["$instanceId|$transitionId"] = $true
                    }
                }
                if ($errors.Count -eq $transitionErrorCount -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryComplete -Boundary $envelope.aggregate.beforeBoundary) -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryComplete -Boundary $envelope.aggregate.afterBoundary)) {
                    $aggregateEntries.Add([pscustomobject]@{
                        Boundary = [pscustomobject]$boundary
                        BeforeBoundary = $envelope.aggregate.beforeBoundary
                        AfterBoundary = $envelope.aggregate.afterBoundary
                        SessionId = [string]$envelope.sessionId
                        TransferId = [string]$envelope.transferId
                        Direction = [string]$envelope.direction
                    })
                }
                $stream.CurrentGeneration = $afterGeneration
                $stream.CurrentHash = $afterHash
                $stream.TransitionCount++
            }
            'filetransfer_coordinator_snapshot_stream_completed' {
                if (-not $stream.Started) {
                    $errors.Add("snapshot_stream_completion_invalid:$instanceId")
                    break
                }
                $finalGeneration = 0L
                $transitionCount = 0L
                [void][long]::TryParse((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'final_state_generation'), [ref]$finalGeneration)
                [void][long]::TryParse((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'transition_count'), [ref]$transitionCount)
                $finalHash = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'final_state_hash'
                $completionEmitted = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'emitted_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                $completionShadow = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'shadow_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                $completionAuthoritative = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'authoritative_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                if ($finalGeneration -ne $stream.CurrentGeneration -or
                    $finalHash -ne $stream.CurrentHash -or
                    $transitionCount -ne $stream.TransitionCount -or
                    (@($completionEmitted | Sort-Object) -join ',') -cne (@($stream.EmittedFamilies | Sort-Object) -join ',') -or
                    (@($completionShadow | Sort-Object) -join ',') -cne (@($stream.ShadowFamilies | Sort-Object) -join ',') -or
                    (@($completionAuthoritative | Sort-Object) -join ',') -cne (@($stream.AuthoritativeFamilies | Sort-Object) -join ',')) {
                    $errors.Add("snapshot_stream_completion_mismatch:$instanceId")
                    break
                }
                $stream.Completed = $true
            }
            'filetransfer_coordinator_authority_effect' {
                if (-not $stream.Started -or $stream.Completed) {
                    $errors.Add("snapshot_authority_effect_outside_stream:$($record.LineIndex)")
                    break
                }
                $stateGeneration = 0L
                $operationGeneration = 0L
                $receiptGeneration = 0L
                if (-not [long]::TryParse(
                        (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'coordinator_state_generation'),
                        [ref]$stateGeneration) -or
                    -not [long]::TryParse(
                        (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'operation_generation'),
                        [ref]$operationGeneration) -or
                    -not [long]::TryParse(
                        (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'receipt_generation'),
                        [ref]$receiptGeneration)) {
                    $errors.Add("snapshot_authority_effect_generation_invalid:$($record.LineIndex)")
                    break
                }
                $effect = [pscustomobject]@{
                        LineIndex = $record.LineIndex
                        Writer = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'log_writer_id'
                        WriterSequence = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'log_writer_sequence'
                        CoordinatorInstanceId = $instanceId
                        TransitionId = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'transition_id'
                        StateGeneration = $stateGeneration
                        Family = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'operation_family'
                        OperationKey = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'operation_key'
                        AuthorityId = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'operation_authority_id'
                        OperationGeneration = $operationGeneration
                        EffectKind = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'effect_kind'
                        EffectIdentity = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'effect_identity'
                        ReceiptIdentity = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'receipt_identity'
                        ReceiptGeneration = $receiptGeneration
                        ReceiptStatus = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'receipt_status'
                        ReceiptFingerprint = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'receipt_fingerprint'
                        Route = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'route'
                        ProtocolVersion = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'protocol_version'
                        LiveRouteEpoch = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'live_route_epoch'
                        RoutePublicationGeneration = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'route_publication_generation'
                        RouteCommitGeneration = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'route_commit_generation'
                        RuntimeGeneration = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'runtime_generation'
                        TerminalGeneration = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'terminal_generation'
                        LegGeneration = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'leg_generation'
                        TransportEpoch = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'transport_epoch'
                        BridgeRecoveryGeneration = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'bridge_recovery_generation'
                        CheckpointGeneration = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'checkpoint_generation'
                        CheckpointRequestId = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'checkpoint_request_id'
                        DataSessionIdentity = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'data_session_identity'
                        DataSessionGeneration = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'data_session_generation'
                    }
                $effectIdentity = "$instanceId|$($effect.TransitionId)|$stateGeneration|$($effect.Family)|$($effect.OperationKey)|$($effect.AuthorityId)|$operationGeneration|$($effect.EffectKind)|$($effect.EffectIdentity)|$($effect.ReceiptIdentity)|$receiptGeneration"
                $effectFingerprint = [string]$line
                if ($seenEffects.ContainsKey($effectIdentity)) {
                    if ($seenEffects[$effectIdentity] -cne $effectFingerprint) {
                        $errors.Add("snapshot_authority_effect_conflicting_duplicate:$($record.LineIndex)")
                    }
                    break
                }
                $seenEffects[$effectIdentity] = $effectFingerprint
                $isPressureSettlementOneShotEffect =
                    $effect.Family -ceq
                        'regular_v4_frontier_repair_pressure' -and
                    $effect.EffectKind -in @(
                        'pressure_settlement_bridge_applied',
                        'local_contract_settled',
                        'local_contract_reference_cleared')
                $isFallbackTailRecoveryOwnerRetirementEffect =
                    $effect.Family -ceq 'fallback_tail_yield' -and
                    $effect.EffectKind -ceq
                        'recovery_owner_retired_after_yield'
                $oneShotEffectReuseDetected = $false
                if ($isPressureSettlementOneShotEffect -or
                    $isFallbackTailRecoveryOwnerRetirementEffect) {
                    $oneShotIdentity =
                        "$instanceId|$($effect.Family)|$($effect.EffectIdentity)"
                    if ($seenOneShotEffectIdentities.ContainsKey(
                            $oneShotIdentity)) {
                        $priorEffect = $seenOneShotEffectIdentities[
                            $oneShotIdentity]
                        if ([string]$priorEffect.TransitionId -cne
                            [string]$effect.TransitionId) {
                            $variant = if (
                                $isFallbackTailRecoveryOwnerRetirementEffect) {
                                'terminal_cleanup_reemitted_already_applied_fallback_tail_recovery_owner_retirement'
                            }
                            else {
                                'route_commit_reemitted_already_applied_pressure_settlement_lifecycle'
                            }
                            $errors.Add(
                                'snapshot_authority_effect_identity_reused_across_transitions|' +
                                $variant + ':' + $record.LineIndex)
                            $oneShotEffectReuseDetected = $true
                        }
                    }
                    else {
                        $seenOneShotEffectIdentities[$oneShotIdentity] =
                            $effect
                    }
                }
                if ($oneShotEffectReuseDetected) {
                    break
                }
                $effects.Add($effect) | Out-Null
            }
        }
    }

    if ((Test-FileTransferCoordinatorBootstrapCorrelationCatalogCapability $catalog.Version) -and $errors.Count -eq 0) {
        foreach ($error in @(Get-FileTransferBootstrapCorrelationHistoryErrors -Boundaries @($boundaries))) { $errors.Add($error) }
    }
    if ($catalog.Version -cin @($script:FileTransferCoordinatorBootstrapInitiationCatalogVersion, $script:FileTransferCoordinatorBootstrapCallbackCatalogVersion) -and $errors.Count -eq 0) {
        foreach ($error in @(Get-FileTransferBootstrapInitiationHistoryErrors -Boundaries @($boundaries))) { $errors.Add($error) }
    }
    if ($catalog.Version -ceq $script:FileTransferCoordinatorBootstrapCallbackCatalogVersion -and $errors.Count -eq 0) {
        foreach ($error in @(Get-FileTransferBootstrapCallbackHistoryErrors -Boundaries @($boundaries))) { $errors.Add($error) }
    }

    $operationIndex = @{}
    $operationIndexSeparator = [char]31
    foreach ($boundary in $boundaries) {
        foreach ($operation in @($boundary.operations)) {
            $operationIndexKey = @(
                [string]$boundary.coordinatorInstanceId,
                [string]$boundary.boundaryId,
                [long]$boundary.afterStateGeneration,
                [string]$operation.family,
                [string]$operation.operationKey,
                [string]$operation.authorityId,
                [long]$operation.operationGeneration) -join
                $operationIndexSeparator
            if (-not $operationIndex.ContainsKey($operationIndexKey)) {
                $operationIndex[$operationIndexKey] =
                    [System.Collections.Generic.List[object]]::new()
            }
            $operationIndex[$operationIndexKey].Add([pscustomobject]@{
                    Boundary = $boundary
                    Operation = $operation
                })
        }
    }

    foreach ($effect in $effects) {
        if ([string]::IsNullOrWhiteSpace($effect.Family) -or
            [string]::IsNullOrWhiteSpace($effect.TransitionId) -or
            [string]::IsNullOrWhiteSpace($effect.OperationKey) -or
            [string]::IsNullOrWhiteSpace($effect.AuthorityId) -or
            [string]::IsNullOrWhiteSpace($effect.EffectKind) -or
            [string]::IsNullOrWhiteSpace($effect.EffectIdentity) -or
            [string]::IsNullOrWhiteSpace($effect.ReceiptIdentity) -or
            [string]::IsNullOrWhiteSpace($effect.ReceiptStatus) -or
            [string]::IsNullOrWhiteSpace($effect.ReceiptFingerprint)) {
            $errors.Add("snapshot_authority_effect_identity_incomplete:$($effect.LineIndex)")
            continue
        }
        if ($effect.Family -in $script:FileTransferCoordinatorSnapshotFamily1 -and
            $script:FileTransferCoordinatorSnapshotFamily1EffectKinds -notcontains
                [string]$effect.EffectKind) {
            $errors.Add("snapshot_authority_effect_kind_unknown:$($effect.LineIndex)")
            continue
        }
        if ($effect.Family -in $script:FileTransferCoordinatorSnapshotFamily2 -and
            $script:FileTransferCoordinatorSnapshotFamily2EffectKinds -notcontains
                [string]$effect.EffectKind) {
            $errors.Add("snapshot_authority_effect_kind_unknown:$($effect.LineIndex)")
            continue
        }
        if ($effect.Family -in $script:FileTransferCoordinatorSnapshotFamily3 -and
            $script:FileTransferCoordinatorSnapshotFamily3EffectKinds -notcontains
                [string]$effect.EffectKind) {
            $errors.Add("snapshot_authority_effect_kind_unknown:$($effect.LineIndex)")
            continue
        }
        if ($effect.Family -in $script:FileTransferCoordinatorSnapshotFamily4 -and
            $script:FileTransferCoordinatorSnapshotFamily4EffectKinds -notcontains
                [string]$effect.EffectKind) {
            $errors.Add("snapshot_authority_effect_kind_unknown:$($effect.LineIndex)")
            continue
        }
        if ($effect.Family -in $script:FileTransferCoordinatorSnapshotFamily5 -and
            $script:FileTransferCoordinatorSnapshotFamily5EffectKinds -notcontains
                [string]$effect.EffectKind) {
            $errors.Add("snapshot_authority_effect_kind_unknown:$($effect.LineIndex)")
            continue
        }
        if ($effect.Family -in $script:FileTransferCoordinatorSnapshotFamily6 -and
            $script:FileTransferCoordinatorSnapshotFamily6EffectKinds -notcontains
                [string]$effect.EffectKind -and
            -not ((Test-FileTransferCoordinatorReceiverBarrierCatalogCapability $catalog) -and
                $effect.EffectKind -ceq 'receiver_barrier_close_applied')) {
            $errors.Add("snapshot_authority_effect_kind_unknown:$($effect.LineIndex)")
            continue
        }
        $operationIndexKey = @(
            [string]$effect.CoordinatorInstanceId,
            [string]$effect.TransitionId,
            [long]$effect.StateGeneration,
            [string]$effect.Family,
            [string]$effect.OperationKey,
            [string]$effect.AuthorityId,
            [long]$effect.OperationGeneration) -join $operationIndexSeparator
        $candidates = if ($operationIndex.ContainsKey($operationIndexKey)) {
            @($operationIndex[$operationIndexKey])
        }
        else {
            @()
        }
        if (@($candidates | Where-Object {
                (Test-FileTransferCoordinatorBootstrapCorrelationMarker $_.Operation) -or
                (Test-FileTransferCoordinatorBootstrapInitiationMarker $_.Operation) -or
                (Test-FileTransferCoordinatorBootstrapCallbackMarker $_.Operation) }).Count -gt 0) {
            $errors.Add("snapshot_bootstrap_correlation_authority_effect_forbidden:$($effect.LineIndex)")
            continue
        }
        if (@($candidates | Where-Object {
                Test-FileTransferCoordinatorCheckpointRepairContinuityMarker $_.Operation }).Count -gt 0) {
            $errors.Add("snapshot_checkpoint_repair_continuity_authority_effect_forbidden:$($effect.LineIndex)")
            continue
        }
        $matches = @($candidates | Where-Object {
                [string]$_.Boundary.coordinatorInstanceId -ceq
                    [string]$effect.CoordinatorInstanceId -and
                [string]$_.Boundary.boundaryId -ceq
                    [string]$effect.TransitionId -and
                [long]$_.Boundary.afterStateGeneration -eq
                    [long]$effect.StateGeneration -and
                [string]$_.Operation.family -ceq [string]$effect.Family -and
                [string]$_.Operation.operationKey -ceq
                    [string]$effect.OperationKey -and
                [string]$_.Operation.authorityId -ceq
                    [string]$effect.AuthorityId -and
                [long]$_.Operation.operationGeneration -eq
                    [long]$effect.OperationGeneration -and
                (Test-FileTransferCoordinatorSnapshotEffectScope `
                    -Operation $_.Operation `
                    -Effect $effect) -and
                (Test-FileTransferCoordinatorSnapshotEffectReceipt `
                    -Operation $_.Operation `
                    -Effect $effect)
            })
        if (@($candidates | Where-Object {
                $_.Boundary.decisionKind -ceq 'CompensateRejectedRuntimeUnlockRetryDispatchConfirmation'
            }).Count -gt 0) {
            $errors.Add("snapshot_retry_confirmation_compensation_effect_forbidden:$($effect.LineIndex)")
            continue
        }
        if ($matches.Count -ne 1) {
            $errors.Add("snapshot_authority_effect_unbound:$($effect.LineIndex)")
        }
        elseif ($effect.EffectKind -ceq 'receiver_barrier_close_applied' -and
            (-not (Test-FileTransferCoordinatorReceiverBarrierMarker $matches[0].Operation) -or
             $matches[0].Operation.operationRole -cne 'receiver_barrier_terminal' -or
             $matches[0].Operation.beforeState -cne 'None' -or $matches[0].Operation.afterState -cne 'Closed' -or
             -not [bool]$matches[0].Operation.stateApplied -or
             $matches[0].Boundary.decisionKind -cne 'CommitInitialActivationReceiverBarrierClose' -or
             $matches[0].Boundary.decisionStatus -cne 'Accepted')) {
            $errors.Add("snapshot_receiver_barrier_effect_without_close:$($effect.LineIndex)")
        }
        elseif ($effect.Family -ceq 'runtime_unlock_retry_lifecycle' -and
            $effect.EffectKind -ceq 'install_committed' -and
            [string]$matches[0].Operation.operationRole -ceq 'attempt_pending') {
            $errors.Add("snapshot_authority_effect_install_before_commit:$($effect.LineIndex)")
        }
        elseif ($effect.Family -ceq 'runtime_unlock_retry_lifecycle' -and $effect.EffectKind -ceq 'scope_invalidation_applied' -and
            ($matches[0].Operation.operationRole -cne 'scope_invalidation' -or
             $matches[0].Operation.beforeState -cne 'Pending' -or $matches[0].Operation.afterState -cne 'Applied' -or
             -not [bool]$matches[0].Operation.stateApplied -or
             $matches[0].Boundary.decisionKind -cne 'CommitRuntimeUnlockRetryLifecycleScopeInvalidation' -or
             $matches[0].Boundary.decisionStatus -cne 'Accepted')) {
            $errors.Add("snapshot_authority_effect_scope_invalidation_without_application:$($effect.LineIndex)")
        }
    }

    foreach ($failure in @(Get-FileTransferCoordinatorReceiverBarrierFailures `
            -Boundaries @($boundaries) -Effects @($effects) -AggregateEntries @($aggregateEntries))) {
        $errors.Add([string]$failure)
    }

    foreach ($boundary in @($boundaries | Where-Object {
                [string]$_.decisionKind -ceq 'FallbackTailQuiescent' -and
                [string]$_.decisionStatus -ceq 'Accepted'
            })) {
        $failedSupersession =
            Get-FileTransferCoordinatorSnapshotFallbackTailFailedSupersessionShape `
                -Operations @($boundary.operations)
        if ($null -eq $failedSupersession) {
            continue
        }
        $successor = $failedSupersession.QuiescentCurrent
        $retirementEffects = @($effects | Where-Object {
                [string]$_.EffectKind -ceq
                    'recovery_owner_retired_after_yield' -and
                (Test-FileTransferCoordinatorSnapshotExactEffectBinding `
                    -Boundary $boundary `
                    -Operation $successor `
                    -Effect $_)
            })
        if ($retirementEffects.Count -ne 1) {
            $errors.Add(
                'snapshot_fallback_tail_recovery_owner_retirement_effect_missing|' +
                'failed_to_quiescent_supersession_ambiguous_yield_receipts_suppressed_exact_successor_effect:' +
                [string]$boundary.evidenceLineIndex)
        }
    }

    foreach ($boundary in @($boundaries | Where-Object {
                [string]$_.decisionKind -ceq 'FallbackTailFailed' -and
                [string]$_.decisionStatus -in @('Idempotent', 'Rejected')
            })) {
        $attemptEffects = @($effects | Where-Object {
                [string]$_.CoordinatorInstanceId -ceq
                    [string]$boundary.coordinatorInstanceId -and
                [string]$_.TransitionId -ceq [string]$boundary.boundaryId -and
                [string]$_.Family -ceq 'fallback_tail_yield'
            })
        if ($attemptEffects.Count -gt 0) {
            $errors.Add(
                'snapshot_fallback_tail_failed_attempt_effect_invalid|' +
                'fallback_tail_failed_state_neutral_attempt_emitted_authority_effect:' +
                [string]$attemptEffects[0].LineIndex)
        }
    }

    foreach ($boundary in @($boundaries | Where-Object {
                [string]$_.decisionKind -ceq 'FallbackTailFailed' -and
                [string]$_.decisionStatus -ceq 'Accepted' -and
                @($_.operations | Where-Object {
                        [string]$_.operationRole -ceq
                            'peer_proof_pending_initial'
                    }).Count -eq 1 -and
                @($_.operations | Where-Object {
                        [string]$_.operationRole -ceq 'yield_current' -and
                        [string]$_.operationKind -ceq 'Failed' -and
                        [string]$_.afterState -ceq 'PeerProofObserved'
                    }).Count -eq 1
            })) {
        $newEffects = @($effects | Where-Object {
                [string]$_.CoordinatorInstanceId -ceq
                    [string]$boundary.coordinatorInstanceId -and
                [string]$_.TransitionId -ceq [string]$boundary.boundaryId -and
                [string]$_.Family -ceq 'fallback_tail_yield'
            })
        if ($newEffects.Count -gt 0) {
            $errors.Add(
                'snapshot_fallback_tail_failed_peer_first_effect_invalid|' +
                'peer_first_failed_yield_emitted_copied_authority_effect:' +
                [string]$newEffects[0].LineIndex)
        }
    }

    $receiveRecoveryRequiredEffects = [ordered]@{
        ObserveReceiveRecoveryExecutionStart = 'execution_started'
        ObserveReceiveRecoveryExecutionReceipt = 'execution_receipt_observed'
        ApplyReceiveRecoveryFallbackCheckpointDeliveryReceipt =
            'checkpoint_delivery_observed'
        ReserveReceiveRecoveryFallbackCheckpointDeliveryResume =
            'checkpoint_resume_observed'
        ApplyReceiveRecoveryFallbackCheckpointFrontierRequestRecheckReceipt =
            'checkpoint_frontier_recheck_observed'
        EvaluateRuntimeUnlockInitialActivationReceiveRecoveryHold =
            'initial_activation_hold_observed'
        InvalidateRuntimeUnlockInitialActivationReceiveRecoveryHold =
            'initial_activation_hold_observed'
        CommitRuntimeUnlockInitialActivationOfferEnvelopeRefresh =
            'initial_activation_envelope_refresh_observed'
    }
    foreach ($boundary in @($boundaries | Where-Object {
                $streams[[string]$_.coordinatorInstanceId].AuthoritativeFamilies -contains
                    'receive_recovery' -and
                [string]$_.decisionKind -ceq
                    'ApplyReceiveRecoveryProofStatePublicationReceipt' -and
                [string]$_.decisionStatus -in @('Accepted', 'Idempotent')
            })) {
        $publicationOperations = @($boundary.operations | Where-Object {
                [string]$_.family -ceq 'receive_recovery' -and
                [string]$_.operationKind -ceq 'ProofPublication'
            })
        $pendingOperations = @($publicationOperations | Where-Object {
                [string]$_.operationRole -ceq 'proof_publication_pending'
            })
        $terminalOperations = @($publicationOperations | Where-Object {
                [string]$_.operationRole -ceq 'proof_publication_terminal'
            })
        $attemptedOperations = @($publicationOperations | Where-Object {
                @($_.receiveRecovery.receipts | Where-Object {
                        [string]$_.stage -ceq 'attempted_decision'
                    }).Count -eq 1
            })
        $deliveryEffects = @($effects | Where-Object {
                $_.CoordinatorInstanceId -ceq
                    [string]$boundary.coordinatorInstanceId -and
                $_.TransitionId -ceq [string]$boundary.boundaryId -and
                $_.Family -ceq 'receive_recovery' -and
                $_.EffectKind -ceq 'proof_publication_delivered'
            })
        $reason = [string]$boundary.decisionReason
        $status = [string]$boundary.decisionStatus
        $validPublicationTransition = $false
        $failureVariant =
            'proof_publication_outcome_state_or_effect_binding_invalid'

        if ($reason -ceq
            'filetransfer_receive_recovery_proof_state_publication_held') {
            $heldTransitions = @($pendingOperations | Where-Object {
                    [string]$_.beforeState -ceq 'Pending' -and
                    [string]$_.afterState -ceq 'Held' -and
                    [bool]$_.stateApplied -and
                    [bool]$_.receiptValidated -and
                    [string]$_.receiptStatus -ceq 'Held' -and
                    @($_.receiveRecovery.receipts | Where-Object {
                            [string]$_.stage -ceq
                                'proof_publication_pending' -and
                            [string]$_.receiptStatus -ceq 'Held' -and
                            [bool]$_.receiptValidated -and
                            [long]$_.commandGeneration -eq
                                [long]$_.receiptGeneration
                        }).Count -eq 1
                })
            $validPublicationTransition =
                $status -ceq 'Accepted' -and
                $heldTransitions.Count -eq 1 -and
                $publicationOperations.Count -eq 1 -and
                $deliveryEffects.Count -eq 0
            $failureVariant =
                'held_receipt_omitted_from_pending_publication_state'
        }
        elseif ($reason -ceq
            'filetransfer_receive_recovery_proof_state_publication_held_receipt_duplicate') {
            $duplicateHeld = @($pendingOperations | Where-Object {
                    [string]$_.beforeState -ceq 'Held' -and
                    [string]$_.afterState -ceq 'Held' -and
                    -not [bool]$_.stateApplied -and
                    [string]$_.receiptStatus -ceq 'Idempotent'
                })
            $validPublicationTransition =
                $status -ceq 'Idempotent' -and
                $duplicateHeld.Count -eq 1 -and
                $attemptedOperations.Count -eq 1 -and
                $publicationOperations.Count -eq 1 -and
                $deliveryEffects.Count -eq 0
            $failureVariant =
                'held_duplicate_attempt_binding_invalid'
        }
        elseif ($reason -ceq
            'filetransfer_receive_recovery_proof_state_publication_delivered') {
            $isTerminalRollover = @($terminalOperations | Where-Object {
                    [string]$_.beforeState -in @('Delivered', 'Rejected') -and
                    [string]$_.afterState -ceq 'None'
                }).Count -gt 0
            $validPublicationTransition =
                $status -ceq 'Accepted' -and
                (Test-FileTransferCoordinatorSnapshotProofPublicationTerminalTransition `
                    -PublicationOperations $publicationOperations `
                    -DeliveryEffects $deliveryEffects `
                    -Boundary $boundary `
                    -Outcome 'Delivered')
            $failureVariant =
                if ($isTerminalRollover) {
                    'valid_terminal_proof_publication_rollover_rejected_as_extra_operation'
                }
                else { 'delivered_publication_transition_or_effect_invalid' }
        }
        elseif ($reason -ceq
            'filetransfer_receive_recovery_proof_state_publication_rejected') {
            $isTerminalRollover = @($terminalOperations | Where-Object {
                    [string]$_.beforeState -in @('Delivered', 'Rejected') -and
                    [string]$_.afterState -ceq 'None'
                }).Count -gt 0
            $validPublicationTransition =
                $status -ceq 'Accepted' -and
                (Test-FileTransferCoordinatorSnapshotProofPublicationTerminalTransition `
                    -PublicationOperations $publicationOperations `
                    -DeliveryEffects $deliveryEffects `
                    -Boundary $boundary `
                    -Outcome 'Rejected')
            $failureVariant =
                if ($isTerminalRollover) {
                    'valid_terminal_proof_publication_rollover_rejected_as_extra_operation'
                }
                else { 'rejected_publication_transition_or_effect_invalid' }
        }
        elseif ($reason -in @(
                'filetransfer_receive_recovery_proof_state_publication_receipt_duplicate',
                'filetransfer_receive_recovery_proof_state_publication_predecessor_receipt_duplicate')) {
            $validPublicationTransition =
                $status -ceq 'Idempotent' -and
                $attemptedOperations.Count -eq 1 -and
                $publicationOperations.Count -eq 1 -and
                $deliveryEffects.Count -eq 0
            $failureVariant =
                'publication_duplicate_attempt_binding_invalid'
        }

        if (-not $validPublicationTransition) {
            $errors.Add(
                "snapshot_receive_recovery_proof_publication_transition_invalid|$failureVariant`:$($boundary.evidenceLineIndex)")
        }
    }
    foreach ($boundary in @($boundaries | Where-Object {
                $streams[[string]$_.coordinatorInstanceId].AuthoritativeFamilies -contains
                    'receive_recovery' -and
                [string]$_.decisionKind -ceq
                    'ObserveReceiveRecoveryProof'
            })) {
        $proofOperations = @($boundary.operations | Where-Object {
                [string]$_.family -ceq 'receive_recovery' -and
                [string]$_.operationRole -in @(
                    'operation_current',
                    'operation_terminal') -and
                $null -ne $_.receiveRecovery
            })
        $proofEffects = @($effects | Where-Object {
                $_.CoordinatorInstanceId -ceq
                    [string]$boundary.coordinatorInstanceId -and
                $_.TransitionId -ceq [string]$boundary.boundaryId -and
                $_.Family -ceq 'receive_recovery' -and
                $_.EffectKind -ceq 'receive_proof_observed'
            })
        $status = [string]$boundary.decisionStatus
        $validProofTransition = $false
        $failureVariant =
            'receive_proof_observation_subject_or_effect_invalid'

        if ($status -in @('Accepted', 'Idempotent')) {
            $effect = if ($proofEffects.Count -eq 1) {
                $proofEffects[0]
            }
            else {
                $null
            }
            $effectSubjects = @()
            if ($null -ne $effect) {
                $effectSubjects = @($proofOperations | Where-Object {
                    [string]$_.operationKey -ceq
                        [string]$effect.OperationKey -and
                    [string]$_.authorityId -ceq
                        [string]$effect.AuthorityId -and
                    [long]$_.operationGeneration -eq
                        [long]$effect.OperationGeneration
                })
            }
            $subject = if ($effectSubjects.Count -eq 1) {
                $effectSubjects[0]
            }
            else {
                $null
            }
            $effectReceipts = @()
            if ($null -ne $subject -and $null -ne $effect) {
                $effectReceipts = @(
                    $subject.receiveRecovery.receipts | Where-Object {
                        [bool]$_.receiptValidated -and
                        [string]$_.receiptIdentity -ceq
                            [string]$effect.ReceiptIdentity -and
                        [long]$_.receiptGeneration -eq
                            [long]$effect.ReceiptGeneration -and
                        [string]$_.receiptStatus -ceq
                            [string]$effect.ReceiptStatus -and
                        [string]$_.receiptFingerprint -ceq
                            [string]$effect.ReceiptFingerprint
                    })
            }
            $expectedReceiptStage = if ($status -ceq 'Accepted') {
                'operation'
            }
            else {
                'attempted_decision'
            }
            $validProofTransition =
                $null -ne $subject -and
                -not [string]::IsNullOrWhiteSpace(
                    [string]$subject.receiveRecovery.proofIdentity) -and
                [string]$subject.receiveRecovery.proofIdentity -cne '(none)' -and
                [long]$subject.receiveRecovery.proofObservedUtcMs -gt 0 -and
                (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                    -Boundary $subject.receiveRecovery.sourceBoundary) -and
                (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                    -Boundary $subject.receiveRecovery.effectiveBoundary) -and
                $effectReceipts.Count -eq 1 -and
                [string]$effectReceipts[0].stage -ceq
                    $expectedReceiptStage -and
                (($status -ceq 'Accepted' -and
                  [long]$boundary.afterStateGeneration -eq
                    [long]$boundary.beforeStateGeneration + 1L -and
                  [string]$boundary.afterStateHash -cne
                    [string]$boundary.beforeStateHash -and
                  [bool]$subject.stateApplied) -or
                 ($status -ceq 'Idempotent' -and
                  [long]$boundary.afterStateGeneration -eq
                    [long]$boundary.beforeStateGeneration -and
                  [string]$boundary.afterStateHash -ceq
                    [string]$boundary.beforeStateHash -and
                  -not [bool]$subject.stateApplied))
            if ($status -ceq 'Idempotent' -and
                $proofOperations.Count -eq 0 -and
                @($boundary.operations | Where-Object {
                        [string]$_.family -ceq 'receive_recovery' -and
                        [string]$_.operationRole -ceq
                            'initial_activation_envelope_refresh'
                    }).Count -gt 0) {
                $failureVariant =
                    'initial_activation_envelope_refresh_lexically_selected_over_current_receive_recovery_operation'
            }
        }
        elseif ($status -ceq 'Rejected') {
            $validProofTransition = $proofEffects.Count -eq 0
            $failureVariant = 'rejected_receive_proof_emitted_authority_effect'
        }

        if (-not $validProofTransition) {
            $errors.Add(
                "snapshot_receive_recovery_required_effect_missing|$failureVariant`:$($boundary.evidenceLineIndex)")
        }
    }
    foreach ($boundary in @($boundaries | Where-Object {
                $streams[[string]$_.coordinatorInstanceId].AuthoritativeFamilies -contains
                    'receive_recovery' -and
                $receiveRecoveryRequiredEffects.Contains(
                    [string]$_.decisionKind) -and
                [string]$_.decisionStatus -in @('Accepted', 'Idempotent')
            })) {
        $requiredEffect = [string]$receiveRecoveryRequiredEffects[
            [string]$boundary.decisionKind]
        $matchingEffects = @($effects | Where-Object {
                $_.CoordinatorInstanceId -ceq
                    [string]$boundary.coordinatorInstanceId -and
                $_.TransitionId -ceq [string]$boundary.boundaryId -and
                $_.Family -ceq 'receive_recovery' -and
                $_.EffectKind -ceq $requiredEffect
            })
        if ($matchingEffects.Count -ne 1) {
            $errors.Add(
                "snapshot_receive_recovery_required_effect_missing:$($boundary.boundaryId):$requiredEffect")
        }
    }

    $regularV4PressureRequiredEffects = [ordered]@{
        StageRegularV4FrontierRepairPressureBinding =
            'pressure_install_staged'
        CommitRegularV4FrontierRepairPressureBinding =
            'pressure_install_bridge_result'
        PrepareRegularV4FrontierRepairPressureSettlement =
            'pressure_settlement_prepared'
        CommitRegularV4FrontierRepairPressureSettlement =
            'pressure_settlement_bridge_applied'
        PrepareRegularV4FrontierRepairPressureRecoveryHandoff =
            'recovery_handoff_prepared'
    }
    foreach ($boundary in @($boundaries | Where-Object {
                $streams[[string]$_.coordinatorInstanceId].AuthoritativeFamilies `
                    -contains 'regular_v4_frontier_repair_pressure' -and
                $regularV4PressureRequiredEffects.Contains(
                    [string]$_.decisionKind) -and
                [string]$_.decisionStatus -in @(
                    'Accepted', 'Idempotent', 'Committed')
            })) {
        $requiredEffect = [string]$regularV4PressureRequiredEffects[
            [string]$boundary.decisionKind]
        $matchingEffects = @($effects | Where-Object {
                $_.CoordinatorInstanceId -ceq
                    [string]$boundary.coordinatorInstanceId -and
                $_.TransitionId -ceq [string]$boundary.boundaryId -and
                $_.Family -ceq
                    'regular_v4_frontier_repair_pressure' -and
                $_.EffectKind -ceq $requiredEffect
            })
        if ($matchingEffects.Count -ne 1) {
            $errors.Add(
                "snapshot_regular_v4_pressure_required_effect_missing:$($boundary.boundaryId):$requiredEffect")
        }
    }

    $validatedHandoffSuccessors = @{}
    $recoveryHandoffEffectKinds = @(
        'pressure_settlement_bridge_applied',
        'local_contract_settled',
        'local_contract_reference_cleared',
        'recovery_handoff_committed',
        'projection_detached')
    foreach ($boundary in @($boundaries | Where-Object {
                $streams[[string]$_.coordinatorInstanceId].AuthoritativeFamilies `
                    -contains 'regular_v4_frontier_repair_pressure' -and
                [string]$_.decisionKind -ceq
                    'CommitRegularV4FrontierRepairPressureRecoveryHandoff'
            })) {
        $boundaryEffects = @($effects | Where-Object {
                [string]$_.CoordinatorInstanceId -ceq
                    [string]$boundary.coordinatorInstanceId -and
                [string]$_.TransitionId -ceq [string]$boundary.boundaryId -and
                [string]$_.Family -ceq
                    'regular_v4_frontier_repair_pressure' -and
                [string]$_.EffectKind -in $recoveryHandoffEffectKinds
            })
        if ([string]$boundary.decisionStatus -cne 'Committed') {
            foreach ($unexpectedEffect in $boundaryEffects) {
                $errors.Add(
                    "snapshot_regular_v4_pressure_recovery_handoff_effect_binding_invalid|idempotent_or_rejected_handoff_emitted_one_shot_effect:$($unexpectedEffect.LineIndex)")
            }
            continue
        }

        $settlements = @($boundary.operations | Where-Object {
                [string]$_.family -ceq
                    'regular_v4_frontier_repair_pressure' -and
                [string]$_.operationRole -ceq
                    'pressure_settlement_terminal' -and
                [string]$_.operationKind -ceq 'PressureSettlement' -and
                [string]$_.beforeState -ceq 'None' -and
                [string]$_.afterState -ceq 'Applied' -and
                [bool]$_.stateApplied -and [bool]$_.receiptValidated -and
                [string]$_.receiptStatus -ceq 'Applied'
            })
        $handoffs = @($boundary.operations | Where-Object {
                [string]$_.family -ceq
                    'regular_v4_frontier_repair_pressure' -and
                [string]$_.operationRole -ceq
                    'recovery_handoff_terminal' -and
                [string]$_.operationKind -ceq 'PressureRecoveryHandoff' -and
                [string]$_.beforeState -ceq 'None' -and
                [string]$_.afterState -ceq 'Committed' -and
                [bool]$_.stateApplied -and [bool]$_.receiptValidated
            })
        $shapeValid = $settlements.Count -eq 1 -and $handoffs.Count -eq 1
        if ($shapeValid) {
            $settlement = $settlements[0]
            $handoff = $handoffs[0]
            $settlementPayload = $settlement.regularV4FrontierRepairPressure
            $handoffPayload = $handoff.regularV4FrontierRepairPressure
            $bindingRetirements = @($boundary.operations | Where-Object {
                    [string]$_.family -ceq
                        'regular_v4_frontier_repair_pressure' -and
                    [string]$_.operationRole -ceq
                        'pressure_binding_installed' -and
                    [string]$_.beforeState -ceq 'Installed' -and
                    [string]$_.afterState -ceq 'None' -and
                    [bool]$_.stateApplied -and
                    [string]$_.authorityId -ceq
                        [string]$settlement.authorityId -and
                    [long]$_.operationGeneration -eq
                        [long]$settlement.operationGeneration
                })
            $authorityRetirements = @($boundary.operations | Where-Object {
                    [string]$_.family -ceq
                        'regular_v4_frontier_repair_pressure' -and
                    [string]$_.operationRole -ceq
                        'repair_authority_current' -and
                    [string]$_.beforeState -ceq 'Authorized' -and
                    [string]$_.afterState -ceq 'None' -and
                    [bool]$_.stateApplied -and
                    [string]$_.authorityId -ceq
                        [string]$settlement.authorityId -and
                    [long]$_.operationGeneration -eq
                        [long]$settlement.operationGeneration
                })
            $settlementMirrors = @($boundary.operations | Where-Object {
                    [string]$_.family -ceq
                        'regular_v4_frontier_repair_pressure' -and
                    [string]$_.operationRole -ceq
                        'repair_authority_terminal' -and
                    [string]$_.operationKind -ceq 'PressureSettlement' -and
                    [string]$_.beforeState -ceq 'None' -and
                    [string]$_.afterState -ceq 'Applied' -and
                    [bool]$_.stateApplied -and [bool]$_.receiptValidated -and
                    [string]$_.authorityId -ceq
                        [string]$settlement.authorityId -and
                    [long]$_.operationGeneration -eq
                        [long]$settlement.operationGeneration -and
                    [string]$_.receiptIdentity -ceq
                        [string]$settlement.receiptIdentity -and
                    [long]$_.receiptGeneration -eq
                        [long]$settlement.receiptGeneration -and
                    [string]$_.receiptStatus -ceq
                        [string]$settlement.receiptStatus -and
                    [string]$_.receiptFingerprint -ceq
                        [string]$settlement.receiptFingerprint
                })
            $attempts = @($boundary.operations | Where-Object {
                    [string]$_.family -ceq
                        'regular_v4_frontier_repair_pressure' -and
                    [string]$_.operationRole -ceq 'attempted_decision' -and
                    [string]$_.operationKind -ceq
                        'CommitRegularV4FrontierRepairPressureRecoveryHandoff' -and
                    -not [bool]$_.stateApplied -and
                    [bool]$_.receiptValidated -and
                    [string]$_.receiptStatus -ceq 'Committed' -and
                    [string]$_.authorityId -ceq
                        [string]$settlement.authorityId -and
                    [long]$_.operationGeneration -eq
                        [long]$settlement.operationGeneration
                })
            $recoveries = @($boundary.operations | Where-Object {
                    [string]$_.family -ceq 'receive_recovery' -and
                    [string]$_.operationRole -ceq 'operation_current' -and
                    [string]$_.beforeState -ceq 'None' -and
                    [string]$_.afterState -like 'Issued*' -and
                    [bool]$_.stateApplied -and
                    $null -ne $_.receiveRecovery -and
                    [string]$_.authorityId -ceq
                        [string]$handoffPayload.recoveryAuthorityId -and
                    [long]$_.operationGeneration -eq
                        [long]$handoffPayload.recoveryOperationGeneration -and
                    [long]$_.operationGeneration -eq
                        [long]$handoff.operationGeneration -and
                    [string]$_.operationKind -ceq
                        [string]$handoffPayload.recoveryKind
                })
            $shapeValid =
                [string]$handoff.authorityId -ceq
                    [string]$settlement.authorityId -and
                [string]$handoff.receiptIdentity -ceq
                    [string]$settlement.receiptIdentity -and
                [long]$handoff.receiptGeneration -eq
                    [long]$settlement.receiptGeneration -and
                [string]$handoff.receiptStatus -ceq
                    [string]$settlement.receiptStatus -and
                [string]$handoff.receiptFingerprint -ceq
                    [string]$settlement.receiptFingerprint -and
                [bool]$settlementPayload.currentAuthorityCleared -and
                -not [bool]$settlementPayload.retainedPredecessorCleared -and
                [bool]$handoffPayload.currentAuthorityCleared -and
                -not [bool]$handoffPayload.retainedPredecessorCleared -and
                [long]$handoffPayload.decisionGeneration -eq
                    [long]$settlementPayload.decisionGeneration -and
                [long]$handoffPayload.pressureInstallGeneration -eq
                    [long]$settlementPayload.pressureInstallGeneration -and
                (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                    -Left $handoffPayload.sourceBoundary `
                    -Right $settlementPayload.sourceBoundary) -and
                (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                    -Left $handoffPayload.effectiveBoundary `
                    -Right $settlementPayload.effectiveBoundary) -and
                $bindingRetirements.Count -eq 1 -and
                $authorityRetirements.Count -eq 1 -and
                $settlementMirrors.Count -eq 1 -and
                $attempts.Count -eq 1 -and $recoveries.Count -eq 1 -and
                (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                    -Left $recoveries[0].receiveRecovery.sourceBoundary `
                    -Right $settlementPayload.sourceBoundary)
        }
        if (-not $shapeValid) {
            $errors.Add(
                "snapshot_regular_v4_pressure_recovery_handoff_effect_binding_invalid|recovery_handoff_commit_operation_shape_invalid:$($boundary.evidenceLineIndex)")
            continue
        }

        $handoffEffectsValid = $true
        foreach ($effectKind in $recoveryHandoffEffectKinds) {
            $subject = if ($effectKind -in @(
                    'recovery_handoff_committed',
                    'projection_detached')) {
                $handoff
            }
            else {
                $settlement
            }
            $kindEffects = @($boundaryEffects | Where-Object {
                    [string]$_.EffectKind -ceq $effectKind
                })
            $exactEffects = @($kindEffects | Where-Object {
                    Test-FileTransferCoordinatorSnapshotExactEffectBinding `
                        -Boundary $boundary -Operation $subject -Effect $_
                })
            if ($kindEffects.Count -eq 1 -and $exactEffects.Count -eq 1) {
                continue
            }
            $handoffEffectsValid = $false
            if ($kindEffects.Count -eq 0) {
                $errors.Add(
                    "snapshot_regular_v4_pressure_recovery_handoff_effect_binding_invalid|recovery_handoff_commit_required_effect_missing:$($boundary.evidenceLineIndex)")
                continue
            }
            foreach ($invalidEffect in @($kindEffects | Where-Object {
                        -not (Test-FileTransferCoordinatorSnapshotExactEffectBinding `
                            -Boundary $boundary -Operation $subject -Effect $_)
                    })) {
                $errors.Add(
                    "snapshot_regular_v4_pressure_recovery_handoff_effect_binding_invalid|recovery_handoff_commit_effects_bound_to_retired_pressure_install_receipt:$($invalidEffect.LineIndex)")
            }
            if ($exactEffects.Count -gt 1) {
                foreach ($duplicateEffect in $exactEffects | Select-Object -Skip 1) {
                    $errors.Add(
                        "snapshot_regular_v4_pressure_recovery_handoff_effect_binding_invalid|recovery_handoff_commit_effect_duplicated:$($duplicateEffect.LineIndex)")
                }
            }
        }
        if ($handoffEffectsValid) {
            # This atomic Family-3/5 boundary installs recovery authority just
            # as direct issuance does, but only after its state and five exact
            # physical effects have passed the existing handoff validation.
            $handoffKey = "$($boundary.coordinatorInstanceId)|$($boundary.writer)|$($boundary.boundaryId)"
            $validatedHandoffSuccessors[$handoffKey] = $recoveries[0]
        }
    }

    foreach ($boundary in @($boundaries | Where-Object {
                $streams[[string]$_.coordinatorInstanceId].AuthoritativeFamilies `
                    -contains 'receive_recovery' -and
                [string]$_.decisionKind -in @(
                    'FallbackCheckpointRequested',
                    'FallbackCheckpointAccepted') -and
                [string]$_.decisionStatus -ceq 'Accepted'
            })) {
        $committedRefreshes = @($boundary.operations | Where-Object {
                [string]$_.family -ceq 'receive_recovery' -and
                [string]$_.operationRole -ceq
                    'initial_activation_envelope_refresh' -and
                [string]$_.operationKind -ceq
                    'InitialActivationEnvelopeRefresh' -and
                [string]$_.beforeState -ceq 'Committed' -and
                [string]$_.afterState -ceq 'Committed'
            })
        if ($committedRefreshes.Count -ne 1) {
            continue
        }
        $refresh = $committedRefreshes[0]
        foreach ($unexpectedEffect in @($effects | Where-Object {
                    [string]$_.CoordinatorInstanceId -ceq
                        [string]$boundary.coordinatorInstanceId -and
                    [string]$_.TransitionId -ceq
                        [string]$boundary.boundaryId -and
                    [string]$_.Family -ceq 'receive_recovery' -and
                    [string]$_.EffectKind -ceq
                        'initial_activation_envelope_refresh_observed' -and
                    [string]$_.OperationKey -ceq
                        [string]$refresh.operationKey -and
                    [string]$_.AuthorityId -ceq
                        [string]$refresh.authorityId -and
                    [long]$_.OperationGeneration -eq
                        [long]$refresh.operationGeneration
                })) {
            $errors.Add(
                "snapshot_receive_recovery_checkpoint_refresh_carry_effect_invalid:$($unexpectedEffect.LineIndex)")
        }
    }

    foreach ($boundary in @($boundaries | Where-Object {
                $streams[[string]$_.coordinatorInstanceId].AuthoritativeFamilies `
                    -contains 'regular_v4_frontier_repair_pressure' -and
                [string]$_.decisionKind -ceq
                    'CommitFileTransferRouteTransition' -and
                [string]$_.decisionStatus -ceq 'Committed' -and
                @($_.operations | Where-Object {
                        [string]$_.family -ceq
                            'regular_v4_frontier_repair_pressure' -and
                        [string]$_.operationRole -ceq
                            'pressure_settlement_terminal'
                    }).Count -gt 0
            })) {
        $terminalOperations = @($boundary.operations | Where-Object {
                [string]$_.family -ceq
                    'regular_v4_frontier_repair_pressure' -and
                [string]$_.operationRole -ceq
                    'pressure_settlement_terminal' -and
                [string]$_.operationKind -ceq 'PressureSettlement' -and
                [bool]$_.receiptValidated
            })
        $enteringOperations = @($terminalOperations | Where-Object {
                [string]$_.beforeState -ceq 'None' -and
                [string]$_.afterState -ceq 'Applied'
            })
        $carriedOperations = @($terminalOperations | Where-Object {
                [string]$_.beforeState -ceq 'Applied' -and
                [string]$_.afterState -ceq 'Applied'
            })
        $outgoingOperations = @($terminalOperations | Where-Object {
                [string]$_.beforeState -ceq 'Applied' -and
                [string]$_.afterState -ceq 'None'
            })
        $settlementOperation = $null
        $expectedEffectKinds = @()
        if ($enteringOperations.Count -eq 1 -and
            $carriedOperations.Count -eq 0 -and
            $terminalOperations.Count -eq
                (1 + $outgoingOperations.Count)) {
            $settlementOperation = $enteringOperations[0]
            $expectedEffectKinds = @(
                'pressure_settlement_bridge_applied',
                'local_contract_settled',
                'local_contract_reference_cleared',
                'route_commit_local_lifecycle_adopted')
        }
        elseif ($enteringOperations.Count -eq 0 -and
            $carriedOperations.Count -eq 1 -and
            $terminalOperations.Count -eq 1) {
            $settlementOperation = $carriedOperations[0]
            $expectedEffectKinds = @(
                'route_commit_local_lifecycle_adopted')
        }
        else {
            $errors.Add(
                "snapshot_regular_v4_pressure_route_commit_effect_invalid:$($boundary.boundaryId)")
            continue
        }

        $effectOperations = @($boundary.operations | Where-Object {
                [string]$_.family -ceq
                    'regular_v4_frontier_repair_pressure' -and
                [string]$_.operationRole -ceq
                    'repair_authority_terminal' -and
                [string]$_.operationKind -ceq 'PressureSettlement' -and
                [string]$_.beforeState -ceq
                    [string]$settlementOperation.beforeState -and
                [string]$_.afterState -ceq
                    [string]$settlementOperation.afterState -and
                [string]$_.authorityId -ceq
                    [string]$settlementOperation.authorityId -and
                [long]$_.operationGeneration -eq
                    [long]$settlementOperation.operationGeneration -and
                [string]$_.receiptIdentity -ceq
                    [string]$settlementOperation.receiptIdentity -and
                [long]$_.receiptGeneration -eq
                    [long]$settlementOperation.receiptGeneration -and
                [string]$_.receiptStatus -ceq
                    [string]$settlementOperation.receiptStatus -and
                [string]$_.receiptFingerprint -ceq
                    [string]$settlementOperation.receiptFingerprint -and
                [bool]$_.receiptValidated
            })
        if ($effectOperations.Count -ne 1) {
            $errors.Add(
                "snapshot_regular_v4_pressure_route_commit_effect_invalid:$($boundary.boundaryId)")
            continue
        }
        $selectedOperation = $effectOperations[0]

        $exactEffects = @($effects | Where-Object {
                [string]$_.CoordinatorInstanceId -ceq
                    [string]$boundary.coordinatorInstanceId -and
                [string]$_.TransitionId -ceq
                    [string]$boundary.boundaryId -and
                [long]$_.StateGeneration -eq
                    [long]$boundary.afterStateGeneration -and
                [string]$_.Family -ceq
                    'regular_v4_frontier_repair_pressure' -and
                [string]$_.OperationKey -ceq
                    [string]$selectedOperation.operationKey -and
                [string]$_.AuthorityId -ceq
                    [string]$selectedOperation.authorityId -and
                [long]$_.OperationGeneration -eq
                    [long]$selectedOperation.operationGeneration -and
                [string]$_.ReceiptIdentity -ceq
                    [string]$selectedOperation.receiptIdentity -and
                [long]$_.ReceiptGeneration -eq
                    [long]$selectedOperation.receiptGeneration -and
                [string]$_.ReceiptStatus -ceq
                    [string]$selectedOperation.receiptStatus -and
                [string]$_.ReceiptFingerprint -ceq
                    [string]$selectedOperation.receiptFingerprint -and
                (Test-FileTransferCoordinatorSnapshotEffectScope `
                    -Operation $selectedOperation `
                    -Effect $_)
            })
        foreach ($effectKind in $expectedEffectKinds) {
            if (@($exactEffects | Where-Object {
                        [string]$_.EffectKind -ceq $effectKind
                    }).Count -ne 1) {
                $errors.Add(
                    "snapshot_regular_v4_pressure_required_effect_missing:$($boundary.boundaryId):$effectKind")
            }
        }
        $unexpectedEffects = @($effects | Where-Object {
                [string]$_.CoordinatorInstanceId -ceq
                    [string]$boundary.coordinatorInstanceId -and
                [string]$_.TransitionId -ceq
                    [string]$boundary.boundaryId -and
                [string]$_.Family -ceq
                    'regular_v4_frontier_repair_pressure' -and
                [string]$_.EffectKind -notin $expectedEffectKinds
            })
        foreach ($unexpectedEffect in $unexpectedEffects) {
            $errors.Add(
                "snapshot_regular_v4_pressure_route_commit_effect_invalid:$($unexpectedEffect.LineIndex)")
        }
    }

    foreach ($boundary in @($boundaries | Where-Object {
                $streams[[string]$_.coordinatorInstanceId].AuthoritativeFamilies `
                    -contains 'regular_v4_frontier_repair_pressure' -and
                [string]$_.decisionKind -in @(
                    'FallbackCheckpointRequested',
                    'FallbackCheckpointAccepted') -and
                [string]$_.decisionStatus -ceq 'Accepted' -and
                @($_.operations | Where-Object {
                        [string]$_.family -ceq
                            'regular_v4_frontier_repair_pressure'
                    }).Count -gt 0
            })) {
        $adoptionOperations = @($boundary.operations | Where-Object {
                [string]$_.family -ceq
                    'regular_v4_frontier_repair_pressure' -and
                [string]$_.operationRole -ceq
                    'repair_authority_terminal' -and
                [string]$_.operationKind -ceq 'PressureSettlement' -and
                [bool]($_.receiptValidated)
            })
        $matchingEffects = @(if ($adoptionOperations.Count -eq 1) {
            $adoption = $adoptionOperations[0]
            $effects | Where-Object {
                    [string]$_.CoordinatorInstanceId -ceq
                        [string]$boundary.coordinatorInstanceId -and
                    [string]$_.TransitionId -ceq
                        [string]$boundary.boundaryId -and
                    [string]$_.Family -ceq
                        'regular_v4_frontier_repair_pressure' -and
                    [string]$_.OperationKey -ceq
                        [string]$adoption.operationKey -and
                    [string]$_.AuthorityId -ceq
                        [string]$adoption.authorityId -and
                    [long]$_.OperationGeneration -eq
                        [long]$adoption.operationGeneration -and
                    [string]$_.EffectKind -ceq
                        'regular_v4_pressure_decision_adopted'
                }
        })
        if ($adoptionOperations.Count -ne 1 -or
            $matchingEffects.Count -ne 1) {
            $errors.Add(
                "snapshot_regular_v4_pressure_required_effect_missing:$($boundary.boundaryId):regular_v4_pressure_decision_adopted")
        }
    }

    foreach ($boundary in @($boundaries | Where-Object {
                $streams[[string]$_.coordinatorInstanceId].AuthoritativeFamilies `
                    -contains 'responder_probe_handoff' -and
                @($_.operations | Where-Object {
                        [string]$_.family -ceq 'responder_probe_handoff'
                    }).Count -gt 0
            })) {
        foreach ($operation in @($boundary.operations | Where-Object {
                    [string]$_.family -ceq 'responder_probe_handoff' -and
                    -not (Test-FileTransferCoordinatorReceiverBarrierMarker $_) -and
                    [bool]$_.receiptValidated
                })) {
            $requiredEffect = if (
                [string]$operation.operationRole -ceq
                    'route_transition_handoff_pending') {
                'route_transition_handoff_supplied'
            }
            elseif ([string]$operation.receiptStatus -ceq 'Invalidated') {
                'handoff_invalidated'
            }
            elseif ([string]$boundary.decisionKind -ceq
                'BindRuntimeUnlockResponderPeerProbeRouteTransitionHandoff') {
                'handoff_bound'
            }
            elseif ([string]$boundary.decisionKind -ceq
                'HoldRuntimeUnlockResponderPeerProbeRouteTransitionHandoffForOwnerWork') {
                'handoff_held'
            }
            else {
                'convergence_observed'
            }
            $matchingEffects = @($effects | Where-Object {
                    [string]$_.CoordinatorInstanceId -ceq
                        [string]$boundary.coordinatorInstanceId -and
                    [string]$_.TransitionId -ceq
                        [string]$boundary.boundaryId -and
                    [string]$_.Family -ceq 'responder_probe_handoff' -and
                    [string]$_.OperationKey -ceq
                        [string]$operation.operationKey -and
                    [string]$_.EffectKind -ceq $requiredEffect
                })
            if ($matchingEffects.Count -ne 1) {
                $errors.Add(
                    "snapshot_responder_probe_handoff_required_effect_missing:$($boundary.boundaryId):$requiredEffect")
            }
        }
    }

    $correlatedRetirements = $null
    if (Test-FileTransferCoordinatorBootstrapCorrelationCatalogCapability $catalog.Version) {
        $retirementEvidence = Get-FileTransferBootstrapCorrelationRetirementEvidence -Boundaries @($boundaries) -Catalog $catalog
        $correlatedRetirements = $retirementEvidence.Validated
        foreach ($error in $retirementEvidence.Errors) { $errors.Add($error) }
    }
    $operationHistory = @(
        foreach ($boundary in @($boundaries)) {
            foreach ($operation in @($boundary.operations)) {
                if ((Test-FileTransferCoordinatorCheckpointRepairContinuityMarker $operation) -or
                    (Test-FileTransferCoordinatorBootstrapCorrelationMarker $operation) -or
                    (Test-FileTransferCoordinatorBootstrapInitiationMarker $operation) -or
                    (Test-FileTransferCoordinatorBootstrapCallbackMarker $operation)) { continue }
                if ([string]$operation.family -in
                    (@($script:FileTransferCoordinatorSnapshotFamily1) +
                     @($script:FileTransferCoordinatorSnapshotFamily2) +
                     @($script:FileTransferCoordinatorSnapshotFamily3) +
                     @($script:FileTransferCoordinatorSnapshotFamily4) +
                     @($script:FileTransferCoordinatorSnapshotFamily5) +
                     @($script:FileTransferCoordinatorSnapshotFamily6))) {
                    [pscustomobject]@{
                        Boundary = $boundary
                        Operation = $operation
                        Key = if (
                            [string]$operation.family -ceq
                                'route_transition' -and
                            [string]$operation.operationRole -cne
                                'fallback_v6_bootstrap') {
                            Get-FileTransferCoordinatorRouteTransitionLifecycleKey `
                                -Boundary $boundary `
                                -Operation $operation
                        }
                        else {
                            "$($boundary.coordinatorInstanceId)|$([string]$operation.operationKey)"
                        }
                    }
                }
            }
        }
    )
    $aggregateBoundaryIndex = New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries @($aggregateEntries)
    $initialRegularPublications = Get-FileTransferCoordinatorSnapshotInitialRegularPublications `
        -AggregateIndex $aggregateBoundaryIndex -Effects @($effects)
    foreach ($group in @($operationHistory | Group-Object Key)) {
        $history = @($group.Group | Sort-Object {
                [long]$_.Boundary.decisionUtcMilliseconds
            }, {
                [long]$_.Boundary.afterStateGeneration
            }, {
                [string]$_.Boundary.boundaryId
            })
        if ($history.Count -eq 0) {
            continue
        }
        $latest = $history[-1]
        $family = [string]$latest.Operation.family
        if ($family -ceq 'runtime_unlock_retry_lifecycle' -and
            [string]$latest.Operation.operationRole -ceq 'scope_invalidation') {
            $pendingSignature = $null
            $retired = $false
            foreach ($entry in @($history | Sort-Object {
                        [long]$_.Boundary.afterStateGeneration
                    }, { [long]$_.Boundary.beforeStateGeneration }, {
                        [string]$_.Boundary.boundaryId
                    })) {
                $operation = $entry.Operation
                $pendingStages = @($operation.runtimeUnlockRetry.receipts | Where-Object {
                    [string]$_.stage -ceq 'scope_invalidation' -and [string]$_.receiptStatus -ceq 'Pending'
                })
                if ($pendingStages.Count -ne 1) { $retired = $true; continue }
                # The pending command captures this immutable attempt and historical source.
                $signature = $operation.runtimeUnlockRetry | ConvertTo-Json -Depth 64 -Compress
                if ($retired -or ($null -ne $pendingSignature -and $signature -cne $pendingSignature)) {
                    $errors.Add(
                        "snapshot_operation_receipt_incomplete|family2_pending_scope_invalidation_command_mutated_or_reused:$($entry.Boundary.evidenceLineIndex)")
                    continue
                }
                $pendingSignature = $signature
                $retired = [string]$operation.afterState -ceq 'None'
            }
        }
        if ($family -ceq 'regular_v4_frontier_repair_pressure' -and
            [string]$latest.Operation.operationRole -ceq 'recovery_handoff_terminal') {
            $receiptSignature = $null
            $retired = $false
            foreach ($entry in @($history | Sort-Object {
                        [long]$_.Boundary.afterStateGeneration
                    }, { [long]$_.Boundary.beforeStateGeneration }, {
                        [string]$_.Boundary.boundaryId
                    })) {
                $operation = $entry.Operation
                if (-not (Test-FileTransferCoordinatorSnapshotRecoveryHandoffTerminalReceipt -Operation $operation)) {
                    continue # The per-operation validator already reports this exact location.
                }
                $signature = Get-FileTransferCoordinatorSnapshotRecoveryHandoffReceiptSignature -Operation $operation
                if ($retired -or ($null -ne $receiptSignature -and $signature -cne $receiptSignature)) {
                    $errors.Add(
                        "snapshot_regular_v4_pressure_recovery_handoff_terminal_receipt_invalid|retained_recovery_handoff_receipt_mutated_or_reused:$($entry.Boundary.evidenceLineIndex)")
                    continue
                }
                $receiptSignature = $signature
                $retired = [string]$operation.afterState -ceq 'None'
            }
        }
        if ($family -ceq 'route_transition') {
            $isBootstrap = [string]$latest.Operation.operationRole -ceq
                'fallback_v6_bootstrap'
            if ($isBootstrap) {
                $bootstrapLifecycle =
                Get-FileTransferCoordinatorFallbackV6BootstrapLifecycle `
                    -History $history -CorrelatedRetirements $correlatedRetirements
                if (-not [bool]$bootstrapLifecycle.Closed) {
                    $candidate = $bootstrapLifecycle.Candidate
                    if (-not [string]::IsNullOrWhiteSpace(
                            [string]$bootstrapLifecycle.StructuralFailure)) {
                        $errors.Add(
                            "snapshot_fallback_v6_bootstrap_$($bootstrapLifecycle.StructuralFailure):$($candidate.Boundary.boundaryId)")
                    }
                    if ($null -ne $candidate) {
                        $candidate.Boundary.taxonomyClass =
                            'filetransfer_route_transition_tuna_v4_to_post_tuna_fallback_v6_proof_bootstrap_missing'
                        $candidate.Boundary.domain = 'route_runtime'
                        $candidate.Boundary.phase = 'route_transition'
                        $candidate.Boundary.rawCount = 1
                        $candidate.Boundary.distinctCount = 1
                        $candidate.Boundary.recoveredCount = 0
                        $candidate.Boundary.unresolvedCount = 1
                        $candidate.Boundary.verdict = 'fail'
                    }
                }
                continue
            }

            $lifecycle =
                Get-FileTransferCoordinatorRouteTransitionLifecycleResult `
                    -History $history `
                    -Effects @($effects)
            if ([bool]$lifecycle.HasCandidate) {
                $candidate = $lifecycle.Candidate
                if ($null -eq $candidate) {
                    $errors.Add(
                        "snapshot_route_transition_candidate_missing:$($latest.Boundary.boundaryId)")
                    $candidate = $latest
                }
                if ($null -ne $candidate) {
                    $candidate.Boundary.taxonomyClass =
                        [string]$lifecycle.FailureClass
                    $candidate.Boundary.domain = 'route_runtime'
                    $candidate.Boundary.phase = 'route_transition'
                    $candidate.Boundary.rawCount = [int]$lifecycle.RawCount
                    $candidate.Boundary.distinctCount = 1
                    $candidate.Boundary.recoveredCount =
                        [int]$lifecycle.RecoveredCount
                    $candidate.Boundary.unresolvedCount =
                        [int]$lifecycle.UnresolvedCount
                    $candidate.Boundary.verdict = if (
                        [int]$lifecycle.UnresolvedCount -eq 0) {
                        'pass'
                    }
                    else {
                        'fail'
                    }
                }
            }
        }
        elseif ($family -ceq 'route_runtime' -and
            [string]$latest.Operation.operationRole -ceq 'route_publication' -and
            [string]$latest.Operation.afterState -ceq 'Published' -and
            [bool]$latest.Operation.receiptValidated) {
            $publicationEffects = @($effects | Where-Object {
                    [string]$_.OperationKey -ceq [string]$latest.Operation.operationKey -and
                    [string]$_.EffectKind -ceq 'route_publication_forwarded'
                })
            if ($publicationEffects.Count -eq 0) {
                $latest.Boundary.taxonomyClass = 'publication_authority_missing'
                $latest.Boundary.domain = 'route_runtime'
                $latest.Boundary.phase = 'route_runtime'
                $latest.Boundary.rawCount = 1
                $latest.Boundary.distinctCount = 1
                $latest.Boundary.recoveredCount = 0
                $latest.Boundary.unresolvedCount = 1
                $latest.Boundary.verdict = 'fail'
            }
            else {
                $runtimeBoundary = $latest.Operation.routeRuntime.boundary
                $liveRouteEpoch = [long](
                    Get-FileTransferCoordinatorSnapshotObjectProperty `
                        -InputObject $runtimeBoundary `
                        -Name 'liveRouteEpoch' `
                        -Default 0L)
                $runtimeGeneration = [long](
                    Get-FileTransferCoordinatorSnapshotObjectProperty `
                        -InputObject $runtimeBoundary `
                        -Name 'runtimeGeneration' `
                        -Default 0L)
                $legGeneration = [long](
                    Get-FileTransferCoordinatorSnapshotObjectProperty `
                        -InputObject $runtimeBoundary `
                        -Name 'legGeneration' `
                        -Default 0L)
                $initialRegularKey = "$($latest.Boundary.coordinatorInstanceId)|$($latest.Boundary.writer)|$($latest.Boundary.boundaryId)|$($latest.Operation.operationKey)"
                if (($liveRouteEpoch -le 0 -and -not $initialRegularPublications.ContainsKey($initialRegularKey)) -or
                    $runtimeGeneration -le 0 -or
                    $legGeneration -le 0) {
                $latest.Boundary.taxonomyClass =
                    'filetransfer_live_route_epoch_projection_missing'
                $latest.Boundary.domain = 'route_runtime'
                $latest.Boundary.phase = 'route_runtime'
                $latest.Boundary.rawCount = 1
                $latest.Boundary.distinctCount = 1
                $latest.Boundary.recoveredCount = 0
                $latest.Boundary.unresolvedCount = 1
                $latest.Boundary.verdict = 'fail'
                }
            }
        }
    }

    $invalidConsumedReleaseBoundaries = @{}
    foreach ($failure in @(ConvertTo-FileTransferCoordinatorSnapshotValidationFailures `
            -Errors @($errors) -Records @($records) -Boundaries @($boundaries))) {
        $invalidConsumedReleaseBoundaries[[string]$failure.TransitionId] = $true
    }
    foreach ($failure in @(Resolve-FileTransferCoordinatorConsumedReleaseHistory `
            -AggregateIndex $aggregateBoundaryIndex -InvalidBoundaries $invalidConsumedReleaseBoundaries)) {
        $errors.Add([string]$failure)
    }
    foreach ($failure in @(Resolve-FileTransferCoordinatorAppliedScopeInvalidationHistory `
            -History $operationHistory -AggregateIndex $aggregateBoundaryIndex -Effects @($effects))) {
        $errors.Add([string]$failure)
    }
    if ($pressureTerminalRolloverCandidates.Count -gt 0) {
        $invalidAggregateBoundaries = @{}
        foreach ($failure in @(ConvertTo-FileTransferCoordinatorSnapshotValidationFailures `
                -Errors @($errors) -Records @($records) -Boundaries @($boundaries))) {
            $invalidAggregateBoundaries[[string]$failure.TransitionId] = $true
        }
        foreach ($failure in @(Resolve-FileTransferCoordinatorPressureTerminalRollovers `
                -AggregateIndex $aggregateBoundaryIndex -Candidates $pressureTerminalRolloverCandidates `
                -InvalidBoundaries $invalidAggregateBoundaries)) {
            $errors.Add([string]$failure)
        }
    }
    if ($checkpointStalePeerProofCandidates.Count -gt 0) {
        $invalidAggregateBoundaries = @{}
        foreach ($failure in @(ConvertTo-FileTransferCoordinatorSnapshotValidationFailures `
                -Errors @($errors) -Records @($records) -Boundaries @($boundaries))) {
            $invalidAggregateBoundaries[[string]$failure.TransitionId] = $true
        }
        foreach ($failure in @(Resolve-FileTransferCoordinatorCheckpointStalePeerProofRetirements `
                -AggregateIndex $aggregateBoundaryIndex -Candidates $checkpointStalePeerProofCandidates `
                -InvalidBoundaries $invalidAggregateBoundaries -Effects @($effects))) {
            $errors.Add([string]$failure)
        }
    }
    if ($checkpointPeerProofSuccessorCandidates.Count -gt 0) {
        $invalidAggregateBoundaries = @{}
        foreach ($failure in @(ConvertTo-FileTransferCoordinatorSnapshotValidationFailures `
                -Errors @($errors) -Records @($records) -Boundaries @($boundaries))) {
            $invalidAggregateBoundaries[[string]$failure.TransitionId] = $true
        }
        foreach ($failure in @(Resolve-FileTransferCoordinatorCheckpointPeerProofSuccessors `
                -AggregateIndex $aggregateBoundaryIndex `
                -Candidates $checkpointPeerProofSuccessorCandidates `
                -InvalidBoundaries $invalidAggregateBoundaries `
                -OperationHistory @($operationHistory) `
                -Effects @($effects))) {
            $errors.Add([string]$failure)
        }
    }
    if ($receiveRecoveryRouteCarryCandidates.Count -gt 0) {
        $invalidAggregateBoundaries = @{}
        foreach ($failure in @(ConvertTo-FileTransferCoordinatorSnapshotValidationFailures `
                -Errors @($errors) -Records @($records) -Boundaries @($boundaries))) {
            $invalidAggregateBoundaries[[string]$failure.TransitionId] = $true
        }
        foreach ($failure in @(Resolve-FileTransferCoordinatorReceiveRecoveryRouteCarries `
                -AggregateIndex $aggregateBoundaryIndex -Candidates $receiveRecoveryRouteCarryCandidates `
                -InvalidBoundaries $invalidAggregateBoundaries -Effects @($effects))) {
            $errors.Add([string]$failure)
        }
    }
    $responderEntries = @($operationHistory | Where-Object {
            [string]$_.Operation.family -ceq 'responder_probe_handoff' -and
            -not (Test-FileTransferCoordinatorReceiverBarrierMarker $_.Operation)
        })
    $invalidResponderCarryBoundaries = @{}
    foreach ($failure in @(ConvertTo-FileTransferCoordinatorSnapshotValidationFailures `
            -Errors @($errors) -Records @($records) -Boundaries @($boundaries))) {
        $invalidResponderCarryBoundaries[[string]$failure.TransitionId] = $true
    }
    foreach ($failure in @(Resolve-FileTransferCoordinatorCheckpointResponderCarries `
            -AggregateIndex $aggregateBoundaryIndex -InvalidBoundaries $invalidResponderCarryBoundaries)) {
        $errors.Add([string]$failure)
    }
    $pendingPeerProofBindings = @(Get-FileTransferCoordinatorSnapshotPendingPeerProofBindings -AggregateEntries @($aggregateEntries) -Effects @($effects))
    $supersessionPeerProofOrigins = @{}
    $validatedDisplacedTerminals = @{}
    if ($failedCheckpointYieldCandidates.Count -gt 0 -or $failedQuiescentSupersessionCandidates.Count -gt 0) {
        $invalidAggregateBoundaries = @{}
        foreach ($failure in @(ConvertTo-FileTransferCoordinatorSnapshotValidationFailures `
                -Errors @($errors) -Records @($records) -Boundaries @($boundaries))) {
            $invalidAggregateBoundaries[[string]$failure.TransitionId] = $true
        }
        foreach ($failure in @(Resolve-FileTransferCoordinatorFailedCheckpointYieldCandidates `
                -AggregateIndex $aggregateBoundaryIndex -Candidates $failedCheckpointYieldCandidates `
                -SupersessionCandidates $failedQuiescentSupersessionCandidates `
                -RejectedSupersessions $rejectedFailedQuiescentSupersessions `
                -PendingPeerProofBindings $pendingPeerProofBindings -SupersessionPeerProofOrigins $supersessionPeerProofOrigins `
                -ValidatedDisplacedTerminals $validatedDisplacedTerminals `
                -InvalidBoundaries $invalidAggregateBoundaries -Effects @($effects) `
                -AllBoundaries (@($boundaries) + @($failedCheckpointYieldHistoryObservations)))) {
            $errors.Add([string]$failure)
        }
    }
    if ($rejectedFailedQuiescentSupersessions.Count -gt 0) {
        # Keep rejected snapshots and their strict failures in the result, but
        # do not let their unproven operations establish downstream authority.
        $operationHistory = @($operationHistory | Where-Object {
                -not $rejectedFailedQuiescentSupersessions.ContainsKey([string]$_.Boundary.boundaryId)
            })
    }
    foreach ($lineageGroup in @($responderEntries | Group-Object {
                Get-FileTransferCoordinatorResponderProbeHandoffLifecycleKey `
                    -Boundary $_.Boundary `
                    -Operation $_.Operation
            })) {
        $history = @($lineageGroup.Group | Sort-Object {
                [long]$_.Boundary.decisionUtcMilliseconds
            }, {
                [long]$_.Boundary.afterStateGeneration
            }, {
                [string]$_.Boundary.boundaryId
            })
        if ($history.Count -eq 0) {
            continue
        }

        $prepared = @($history | Where-Object {
                [string]$_.Operation.operationRole -ceq
                    'route_transition_handoff_pending' -and
                [string]$_.Operation.receiptStatus -in @(
                    'Prepared', 'Idempotent', 'Committed') -and
                [bool]$_.Operation.receiptValidated -and
                [string]$_.Operation.responderProbeHandoff.linkedTransitionAuthorityId `
                    -notin @('', '(none)') -and
                [long]$_.Operation.responderProbeHandoff.linkedTransitionGeneration `
                    -gt 0
            })
        $incidentEntries = @($history | Where-Object {
                [string]$_.Operation.receiptStatus -in @(
                    'Rejected', 'Held')
            })
        if ($incidentEntries.Count -gt 0) {
            $openIncidentEntries = New-Object System.Collections.Generic.List[object]
            foreach ($observationGroup in @($incidentEntries | Group-Object {
                        Get-FileTransferCoordinatorResponderProbeHandoffObservationKey `
                            -Entry $_
                    })) {
                $observations = @($observationGroup.Group)
                if (-not (
                        Test-FileTransferCoordinatorResponderProbeHandoffObservationConsumed `
                            -History $history `
                            -Candidate $observations[0] `
                            -AggregateIndex $aggregateBoundaryIndex)) {
                    foreach ($observation in $observations) {
                        $openIncidentEntries.Add($observation) | Out-Null
                    }
                }
            }
            $incidentEntries = @($openIncidentEntries.ToArray())
        }
        if ($incidentEntries.Count -eq 0 -and $prepared.Count -eq 0) {
            $incidentEntries = @($history | Where-Object {
                    [string]$_.Boundary.decisionKind -ceq
                        'BindRuntimeUnlockResponderPeerProbeRouteTransitionHandoff' -and
                    [string]$_.Operation.receiptStatus -in @(
                        'Accepted', 'Idempotent')
                })
        }
        if ($incidentEntries.Count -eq 0) {
            continue
        }

        $candidate = $incidentEntries[0]
        $candidateTime =
            [long]$candidate.Boundary.decisionUtcMilliseconds
        $poison = @($history | Where-Object {
                [long]$_.Boundary.decisionUtcMilliseconds -gt $candidateTime -and
                [string]$_.Operation.receiptStatus -ceq 'Invalidated'
            } | Select-Object -First 1) | Select-Object -First 1
        $recovery = @($prepared | Where-Object {
                [long]$_.Boundary.decisionUtcMilliseconds -ge $candidateTime -and
                ($null -eq $poison -or
                 [long]$_.Boundary.decisionUtcMilliseconds -lt
                    [long]$poison.Boundary.decisionUtcMilliseconds)
            } | Select-Object -First 1) | Select-Object -First 1
        $recovered = $null -ne $recovery
        $reason = [string]$candidate.Boundary.decisionReason
        $variant = if ($reason -like '*initial*scope*' -or
            $reason -like '*initial*peer*acceptance*') {
            'initial_activation_peer_acceptance_scope_rejected'
        }
        elseif ($reason -like '*ack*before*probe*' -or
            $reason -like '*adoption*recheck*') {
            'ack_before_probe_adoption_recheck_missing'
        }
        elseif ($reason -like '*binding*' -or
            $reason -like '*probe_mismatch*' -or
            $reason -like '*admission*') {
            'binding_missing'
        }
        else {
            'after_bound_handoff_missing'
        }
        $candidate.Boundary.taxonomyClass =
            'runtime_unlock_responder_acked_probe_route_transition_handoff_missing'
        $candidate.Boundary | Add-Member -NotePropertyName variant `
            -NotePropertyValue $variant -Force
        $candidate.Boundary.domain = 'route_runtime'
        $candidate.Boundary.phase = 'runtime_unlock_retry'
        $candidate.Boundary.rawCount = $incidentEntries.Count
        $candidate.Boundary.distinctCount = 1
        $candidate.Boundary.recoveredCount = if ($recovered) { 1 } else { 0 }
        $candidate.Boundary.unresolvedCount = if ($recovered) { 0 } else { 1 }
        $candidate.Boundary.verdict = if ($recovered) { 'pass' } else { 'fail' }
    }

    $regularV4PressureEntries = @($operationHistory | Where-Object {
            [string]$_.Operation.family -ceq
                'regular_v4_frontier_repair_pressure'
        })
    foreach ($authorityGroup in @($regularV4PressureEntries | Group-Object {
                "$($_.Boundary.coordinatorInstanceId)|$([string]$_.Operation.authorityId)"
            })) {
        $history = @($authorityGroup.Group | Sort-Object {
                [long]$_.Boundary.decisionUtcMilliseconds
            }, {
                [long]$_.Boundary.afterStateGeneration
            }, {
                [string]$_.Boundary.boundaryId
            })
        if ($history.Count -eq 0) {
            continue
        }

        $enteringTerminalSettlements = @($history | Where-Object {
                [string]$_.Operation.operationRole -ceq
                    'pressure_settlement_terminal' -and
                [string]$_.Operation.beforeState -ceq 'None' -and
                [string]$_.Operation.afterState -ceq 'Applied' -and
                [bool]$_.Operation.receiptValidated -and
                [string]$_.Operation.receiptStatus -in @(
                    'Applied', 'Idempotent')
            })
        $terminalSettlement = if (
            $enteringTerminalSettlements.Count -eq 1) {
            $enteringTerminalSettlements[0]
        }
        else {
            $null
        }
        $terminalHandoff = @($history | Where-Object {
                [string]$_.Operation.operationRole -ceq
                    'recovery_handoff_terminal' -and
                [string]$_.Operation.afterState -ceq 'Committed' -and
                [bool]$_.Operation.receiptValidated
            } | Select-Object -Last 1) | Select-Object -First 1
        $settlementClosed = $false
        if ($null -ne $terminalSettlement) {
            $settlementEffectOperations = @(if (
                [string]$terminalSettlement.Boundary.decisionKind -ceq
                    'CommitFileTransferRouteTransition') {
                @($terminalSettlement.Boundary.operations | Where-Object {
                        [string]$_.family -ceq
                            'regular_v4_frontier_repair_pressure' -and
                        [string]$_.operationRole -ceq
                            'repair_authority_terminal' -and
                        [string]$_.operationKind -ceq 'PressureSettlement' -and
                        [string]$_.beforeState -ceq 'None' -and
                        [string]$_.afterState -ceq 'Applied' -and
                        [string]$_.authorityId -ceq
                            [string]$terminalSettlement.Operation.authorityId -and
                        [long]$_.operationGeneration -eq
                            [long]$terminalSettlement.Operation.operationGeneration -and
                        [string]$_.receiptIdentity -ceq
                            [string]$terminalSettlement.Operation.receiptIdentity -and
                        [long]$_.receiptGeneration -eq
                            [long]$terminalSettlement.Operation.receiptGeneration -and
                        [string]$_.receiptStatus -ceq
                            [string]$terminalSettlement.Operation.receiptStatus -and
                        [string]$_.receiptFingerprint -ceq
                            [string]$terminalSettlement.Operation.receiptFingerprint -and
                        [bool]$_.receiptValidated
                    })
            }
            else {
                @($terminalSettlement.Operation)
            })
            $settlementEffectOperation = if (
                $settlementEffectOperations.Count -eq 1) {
                $settlementEffectOperations[0]
            }
            else {
                $null
            }
            $settlementEffects = @($effects | Where-Object {
                    $null -ne $settlementEffectOperation -and
                    [string]$_.CoordinatorInstanceId -ceq
                        [string]$terminalSettlement.Boundary.coordinatorInstanceId -and
                    [string]$_.TransitionId -ceq
                        [string]$terminalSettlement.Boundary.boundaryId -and
                    [long]$_.StateGeneration -eq
                        [long]$terminalSettlement.Boundary.afterStateGeneration -and
                    [string]$_.Family -ceq
                        'regular_v4_frontier_repair_pressure' -and
                    [string]$_.OperationKey -ceq
                        [string]$settlementEffectOperation.operationKey -and
                    [string]$_.AuthorityId -ceq
                        [string]$settlementEffectOperation.authorityId -and
                    [long]$_.OperationGeneration -eq
                        [long]$settlementEffectOperation.operationGeneration -and
                    [string]$_.ReceiptIdentity -ceq
                        [string]$settlementEffectOperation.receiptIdentity -and
                    [long]$_.ReceiptGeneration -eq
                        [long]$settlementEffectOperation.receiptGeneration -and
                    [string]$_.ReceiptStatus -ceq
                        [string]$settlementEffectOperation.receiptStatus -and
                    [string]$_.ReceiptFingerprint -ceq
                        [string]$settlementEffectOperation.receiptFingerprint -and
                    (Test-FileTransferCoordinatorSnapshotEffectScope `
                        -Operation $settlementEffectOperation `
                        -Effect $_)
                })
            $settlementClosed =
                @($settlementEffects | Where-Object {
                    [string]$_.EffectKind -ceq
                        'pressure_settlement_bridge_applied'
                }).Count -eq 1 -and
                @($settlementEffects | Where-Object {
                    [string]$_.EffectKind -ceq 'local_contract_settled'
                }).Count -eq 1 -and
                @($settlementEffects | Where-Object {
                    [string]$_.EffectKind -ceq
                        'local_contract_reference_cleared'
                }).Count -eq 1
        }

        if (-not $settlementClosed -and
            $null -ne $terminalSettlement -and
            $null -ne $terminalHandoff -and
            [string]$terminalHandoff.Boundary.boundaryId -ceq
                [string]$terminalSettlement.Boundary.boundaryId -and
            [string]$terminalHandoff.Operation.authorityId -ceq
                [string]$terminalSettlement.Operation.authorityId -and
            [string]$terminalHandoff.Operation.receiptIdentity -ceq
                [string]$terminalSettlement.Operation.receiptIdentity -and
            [long]$terminalHandoff.Operation.receiptGeneration -eq
                [long]$terminalSettlement.Operation.receiptGeneration -and
            [string]$terminalHandoff.Operation.receiptStatus -ceq
                [string]$terminalSettlement.Operation.receiptStatus -and
            [string]$terminalHandoff.Operation.receiptFingerprint -ceq
                [string]$terminalSettlement.Operation.receiptFingerprint -and
            (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                -Left $terminalHandoff.Operation.regularV4FrontierRepairPressure.sourceBoundary `
                -Right $terminalSettlement.Operation.regularV4FrontierRepairPressure.sourceBoundary) -and
            (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                -Left $terminalHandoff.Operation.regularV4FrontierRepairPressure.effectiveBoundary `
                -Right $terminalSettlement.Operation.regularV4FrontierRepairPressure.effectiveBoundary)) {
            # The typed Coordinator commit closes runtime authority. Malformed
            # effect bindings remain a separate artifact-evidence failure.
            $settlementClosed = $true
        }

        $pending = @($history | Where-Object {
                [string]$_.Operation.operationRole -ceq
                    'pressure_binding_pending' -and
                [string]$_.Operation.afterState -ceq 'Pending'
            } | Select-Object -Last 1) | Select-Object -First 1
        $installed = @($history | Where-Object {
                [string]$_.Operation.operationRole -in @(
                    'pressure_binding_installed',
                    'pressure_binding_retained_predecessor') -and
                [string]$_.Operation.afterState -in @(
                    'Installed', 'RetainedPredecessor')
            } | Select-Object -Last 1) | Select-Object -First 1
        $preparedHandoff = @($history | Where-Object {
                [string]$_.Boundary.decisionKind -ceq
                    'PrepareRegularV4FrontierRepairPressureRecoveryHandoff' -and
                [string]$_.Boundary.decisionStatus -ceq 'Prepared'
            } | Select-Object -Last 1) | Select-Object -First 1
        $rejectedSettlement = @($history | Where-Object {
                [string]$_.Boundary.decisionKind -in @(
                    'PrepareRegularV4FrontierRepairPressureSettlement',
                    'CommitRegularV4FrontierRepairPressureSettlement') -and
                [string]$_.Boundary.decisionStatus -ceq 'Rejected'
            } | Select-Object -First 1) | Select-Object -First 1

        $candidate = $null
        $class = ''
        if ($null -ne $rejectedSettlement -and -not $settlementClosed) {
            $candidate = $rejectedSettlement
            $class = 'regular_v4_frontier_repair_settlement_disposition_unverifiable'
        }
        elseif ($null -ne $preparedHandoff -and $null -eq $terminalHandoff) {
            $candidate = $preparedHandoff
            $class = 'regular_v4_frontier_repair_pressure_recovery_handoff_missing'
        }
        elseif ($null -ne $pending -and
            @($history | Where-Object {
                    [string]$_.Operation.operationRole -ceq
                        'pressure_binding_installed' -and
                    [long]$_.Boundary.decisionUtcMilliseconds -ge
                        [long]$pending.Boundary.decisionUtcMilliseconds
                }).Count -eq 0) {
            $candidate = $pending
            $class = if ([string]$pending.Operation.predecessorAuthorityId -in
                @('', '(none)')) {
                'regular_v4_frontier_repair_initial_pressure_install_missing'
            }
            else {
                'regular_v4_frontier_repair_successor_pressure_install_missing'
            }
        }
        elseif ($null -ne $installed -and -not $settlementClosed -and
            $null -eq $terminalHandoff) {
            $candidate = $installed
            $class =
                'regular_v4_frontier_repair_pressure_authority_lifecycle_split'
        }
        elseif ($null -ne $terminalSettlement -and -not $settlementClosed) {
            $candidate = $terminalSettlement
            $class =
                'regular_v4_frontier_repair_pressure_authority_lifecycle_split'
        }

        if ($null -ne $candidate -and
            -not [string]::IsNullOrWhiteSpace($class)) {
            $candidate.Boundary.taxonomyClass = $class
            $candidate.Boundary.domain = 'route_runtime'
            $candidate.Boundary.phase = 'fallback_frontier_repair'
            $candidate.Boundary.rawCount = 1
            $candidate.Boundary.distinctCount = 1
            $candidate.Boundary.recoveredCount = 0
            $candidate.Boundary.unresolvedCount = 1
            $candidate.Boundary.verdict = 'fail'
        }
    }

    $fallbackTailPeerProofBindings =
        [Collections.Generic.List[object]]::new()
    $fallbackTailEntries = @($operationHistory | Where-Object {
            [string]$_.Operation.family -ceq 'fallback_tail_yield'
        })
    foreach ($authorityGroup in @($fallbackTailEntries | Group-Object {
                "$($_.Boundary.coordinatorInstanceId)|$([string]$_.Operation.authorityId)"
            })) {
        $history = @($authorityGroup.Group | Sort-Object {
                [long]$_.Boundary.decisionUtcMilliseconds
            }, {
                [long]$_.Boundary.afterStateGeneration
            }, {
                [string]$_.Boundary.boundaryId
            })
        $coordinatorInstanceId =
            [string]$history[0].Boundary.coordinatorInstanceId
        $authorityId = [string]$history[0].Operation.authorityId
        $exactEffects = @($effects | Where-Object {
                [string]$_.CoordinatorInstanceId -ceq
                    $coordinatorInstanceId -and
                [string]$_.Family -ceq 'fallback_tail_yield' -and
                [string]$_.AuthorityId -ceq $authorityId
            })
        $yieldHistory = @($history | Where-Object {
                [string]$_.Operation.operationRole -in @(
                    'yield_current', 'yield_terminal',
                    'yield_retained_predecessor')
            })
        $peerProofLifecycle =
            Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofLifecycle `
                -YieldHistory $yieldHistory `
                -OperationHistory @($operationHistory) `
                -Effects @($effects) -SupersessionPeerProofOrigins $supersessionPeerProofOrigins
        foreach ($entry in $yieldHistory) {
            $operation = $entry.Operation
            $state = [string]$operation.afterState
            $typed = $operation.fallbackTailYield
            $failureVariant = '(none)'
            $hasLocal = @($typed.receipts | Where-Object {
                    [string]$_.stage -ceq 'local_proof' -and
                    [bool]$_.receiptValidated
                }).Count -eq 1
            $hasPeer = @($typed.receipts | Where-Object {
                    [string]$_.stage -ceq 'peer_proof' -and
                    [bool]$_.receiptValidated
                }).Count -eq 1
            $hasPair = @($typed.receipts | Where-Object {
                    [string]$_.stage -ceq 'paired_release' -and
                    [bool]$_.receiptValidated
                }).Count -eq 1
            $hasReceiptEffect = {
                param([string]$Stage, [string]$Kind)
                $receipt = @($typed.receipts | Where-Object {
                        [string]$_.stage -ceq $Stage -and
                        [bool]$_.receiptValidated
                    })
                if ($receipt.Count -ne 1) {
                    return $false
                }
                return @($exactEffects | Where-Object {
                        [string]$_.EffectKind -ceq $Kind -and
                        [string]$_.ReceiptIdentity -ceq
                            [string]$receipt[0].receiptIdentity -and
                        [long]$_.ReceiptGeneration -eq
                            [long]$receipt[0].receiptGeneration -and
                        [string]$_.ReceiptStatus -ceq
                            [string]$receipt[0].receiptStatus -and
                        [string]$_.ReceiptFingerprint -ceq
                            [string]$receipt[0].receiptFingerprint
                    }).Count -eq 1
            }
            $advanced = $state -in @(
                'Paired', 'ReleaseReserved', 'ReleaseConsumed')
            $valid = -not $advanced -or ($hasLocal -and $hasPeer -and $hasPair)
            if ($valid -and $hasLocal) {
                $valid = (& $hasReceiptEffect 'local_proof' 'local_proof_sent') -and
                    (& $hasReceiptEffect 'local_proof' 'local_proof_applied')
            }
            $peerProofEntryKey =
                Get-FileTransferCoordinatorSnapshotFallbackTailYieldEntryKey `
                    -Entry $entry
            $peerProofState = $peerProofLifecycle[$peerProofEntryKey]
            if ($valid -and $null -ne $peerProofState -and
                -not [bool]$peerProofState.Valid) {
                $valid = $false
                $failureVariant = [string]$peerProofState.Variant
            }
            if ($valid -and $hasPair) {
                $valid = & $hasReceiptEffect 'paired_release' 'paired_release_published'
            }
            if ($valid -and [string]$typed.yieldKind -ceq 'Quiescent') {
                $displacementKey = "$($entry.Boundary.coordinatorInstanceId)|$($entry.Boundary.writer)|$peerProofEntryKey"
                if ([string]$entry.Boundary.decisionKind -ceq
                    'FallbackTailQuiescent' -and -not $validatedDisplacedTerminals.ContainsKey($displacementKey)) {
                    $yieldEffects = @($effects | Where-Object {
                            [string]$_.EffectKind -ceq
                                'recovery_owner_retired_after_yield' -and
                            (Test-FileTransferCoordinatorSnapshotExactEffectBinding `
                                -Boundary $entry.Boundary `
                                -Operation $operation `
                                -Effect $_)
                        })
                    $valid = $yieldEffects.Count -eq 1
                }
                else {
                    # Exact terminal-slot displacement retains the original
                    # yield's one-shot effect; it does not issue that yield again.
                    $valid = & $hasReceiptEffect `
                        'yield' 'recovery_owner_retired_after_yield'
                }
            }
            if ($valid -and [bool]$typed.failedSupersessionCarriedPeerProof) {
                $valid = [string]$typed.failedPredecessorAuthorityId -notin
                    @('', '(none)') -and $hasPeer
            }
            if ($valid -and $state -ceq 'ReleaseReserved') {
                $valid = @($history | Where-Object {
                        [string]$_.Operation.operationRole -ceq
                            'release_reservation_current' -and
                        [string]$_.Operation.afterState -ceq 'Reserved' -and
                        [long]$_.Operation.fallbackTailYield.reservationGeneration -gt 0
                    }).Count -eq 1
            }
            if ($valid -and $state -ceq 'ReleaseConsumed') {
                $valid = @($history | Where-Object {
                        [string]$_.Operation.operationRole -ceq
                            'release_reservation_consumed' -and
                        [string]$_.Operation.afterState -ceq 'Consumed' -and
                        [bool]$_.Operation.receiptValidated -and
                        [long]$_.Operation.fallbackTailYield.consumingInstallGeneration -gt 0
                    }).Count -eq 1
            }
            if ($valid -and
                [string]$operation.operationRole -ceq 'yield_current' -and
                [string]$operation.afterState -ceq 'Paired' -and
                [string]$operation.beforeState -cne 'Paired' -and
                [bool]$operation.stateApplied) {
                $peerReceipt = @($typed.receipts | Where-Object {
                        [string]$_.stage -ceq 'peer_proof' -and
                        [string]$_.receiptStatus -ceq 'Observed' -and
                        [bool]$_.receiptValidated
                    })
                $localReceipt = @($typed.receipts | Where-Object {
                        [string]$_.stage -ceq 'local_proof' -and
                        [bool]$_.receiptValidated
                    })
                $releaseReceipt = @($typed.receipts | Where-Object {
                        [string]$_.stage -ceq 'paired_release' -and
                        [bool]$_.receiptValidated
                    })
                $peerEffects = @($effects | Where-Object {
                        [string]$_.CoordinatorInstanceId -ceq
                            [string]$entry.Boundary.coordinatorInstanceId -and
                        [string]$_.Family -ceq 'fallback_tail_yield' -and
                        [string]$_.EffectKind -ceq 'peer_proof_observed' -and
                        $peerReceipt.Count -eq 1 -and
                        [string]$_.ReceiptIdentity -ceq
                            [string]$peerReceipt[0].receiptIdentity -and
                        [long]$_.ReceiptGeneration -eq
                            [long]$peerReceipt[0].receiptGeneration -and
                        [string]$_.ReceiptStatus -ceq
                            [string]$peerReceipt[0].receiptStatus -and
                        [string]$_.ReceiptFingerprint -ceq
                            [string]$peerReceipt[0].receiptFingerprint
                    })
                $localEffects = @($effects | Where-Object {
                        [string]$_.CoordinatorInstanceId -ceq
                            [string]$entry.Boundary.coordinatorInstanceId -and
                        [string]$_.Family -ceq 'fallback_tail_yield' -and
                        [string]$_.OperationKey -ceq
                            [string]$operation.operationKey -and
                        [string]$_.AuthorityId -ceq
                            [string]$operation.authorityId -and
                        [long]$_.OperationGeneration -eq
                            [long]$operation.operationGeneration -and
                        [string]$_.EffectKind -in @(
                            'local_proof_sent', 'local_proof_applied') -and
                        $localReceipt.Count -eq 1 -and
                        [string]$_.ReceiptIdentity -ceq
                            [string]$localReceipt[0].receiptIdentity -and
                        [long]$_.ReceiptGeneration -eq
                            [long]$localReceipt[0].receiptGeneration -and
                        [string]$_.ReceiptStatus -ceq
                            [string]$localReceipt[0].receiptStatus -and
                        [string]$_.ReceiptFingerprint -ceq
                            [string]$localReceipt[0].receiptFingerprint
                    })
                $releaseEffects = @($effects | Where-Object {
                        [string]$_.CoordinatorInstanceId -ceq
                            [string]$entry.Boundary.coordinatorInstanceId -and
                        [string]$_.Family -ceq 'fallback_tail_yield' -and
                        [string]$_.OperationKey -ceq
                            [string]$operation.operationKey -and
                        [string]$_.AuthorityId -ceq
                            [string]$operation.authorityId -and
                        [long]$_.OperationGeneration -eq
                            [long]$operation.operationGeneration -and
                        [string]$_.EffectKind -ceq
                            'paired_release_published' -and
                        $releaseReceipt.Count -eq 1 -and
                        [string]$_.ReceiptIdentity -ceq
                            [string]$releaseReceipt[0].receiptIdentity -and
                        [long]$_.ReceiptGeneration -eq
                            [long]$releaseReceipt[0].receiptGeneration -and
                        [string]$_.ReceiptStatus -ceq
                            [string]$releaseReceipt[0].receiptStatus -and
                        [string]$_.ReceiptFingerprint -ceq
                            [string]$releaseReceipt[0].receiptFingerprint
                    })
                if ($peerReceipt.Count -eq 1 -and
                    $localReceipt.Count -eq 1 -and
                    $releaseReceipt.Count -eq 1 -and
                    $peerEffects.Count -eq 1 -and
                    @($localEffects | Where-Object {
                            [string]$_.EffectKind -ceq 'local_proof_sent'
                        }).Count -eq 1 -and
                    @($localEffects | Where-Object {
                            [string]$_.EffectKind -ceq 'local_proof_applied'
                        }).Count -eq 1 -and
                    $releaseEffects.Count -eq 1 -and
                    [string]$typed.peerProofAuthorityId -match
                        '^fta2-fallback_tail_yield-[0-9a-f]{24}$') {
                    $fallbackTailPeerProofBindings.Add([pscustomobject]@{
                            BindingKind = 'paired_yield'
                            CoordinatorInstanceId =
                                [string]$entry.Boundary.coordinatorInstanceId
                            Writer = [string]$entry.Boundary.writer
                            TransitionId = [string]$entry.Boundary.boundaryId
                            TransitionSequence =
                                [long]$entry.Boundary.writerSequence
                            StateGeneration =
                                [long]$entry.Boundary.afterStateGeneration
                            SessionId = [string]$entry.Boundary.sessionId
                            TransferId = [string]$entry.Boundary.transferId
                            Direction = [string]$entry.Boundary.direction
                            OperationKey = [string]$operation.operationKey
                            OperationAuthorityId =
                                [string]$operation.authorityId
                            OperationGeneration =
                                [long]$operation.operationGeneration
                            OperationRole =
                                [string]$operation.operationRole
                            BeforeState = [string]$operation.beforeState
                            AfterState = [string]$operation.afterState
                            OwnerKey = [string]$typed.ownerKey
                            PairKey = [string]$typed.pairKey
                            PeerProofAuthorityId =
                                [string]$typed.peerProofAuthorityId
                            PeerProofGeneration = [long](
                                Get-FileTransferCoordinatorSnapshotObjectProperty `
                                    -InputObject $typed `
                                    -Name 'peerProofGeneration' `
                                    -Default 0L)
                            PeerProofYieldGeneration =
                                [long]$typed.peerProofYieldGeneration
                            PeerProofIdentity =
                                [string]$typed.peerProofIdentity
                            PeerProofFingerprint =
                                [string]$typed.peerProofFingerprint
                            PairingState = [string]$typed.pairingState
                            PairingGeneration = [long]$typed.pairingGeneration
                            LocalProofIdentity =
                                [string]$typed.localProofIdentity
                            LocalProofFingerprint =
                                [string]$typed.localProofFingerprint
                            PairedReleaseAuthorityId =
                                [string]$typed.pairedReleaseAuthorityId
                            PairedReleaseGeneration =
                                [long]$typed.pairedReleaseGeneration
                            PairedReleaseOutcome =
                                [string]$typed.pairedReleaseOutcome
                            AggregateBoundary = $entry.Boundary.afterBoundary
                            SourceBoundary = $typed.sourceBoundary
                            EffectiveBoundary = $typed.effectiveBoundary
                            TargetBoundary = $typed.targetBoundary
                            PeerProofReceipt = $peerReceipt[0]
                            PeerProofEffect = $peerEffects[0]
                            LocalProofReceipt = $localReceipt[0]
                            LocalProofEffects = @($localEffects | Sort-Object {
                                    [string]$_.EffectKind
                                }, {
                                    [string]$_.EffectIdentity
                                }, {
                                    [string]$_.ReceiptIdentity
                                })
                            PairedReleaseReceipt = $releaseReceipt[0]
                            PairedReleaseEffect = $releaseEffects[0]
                        }) | Out-Null
                }
            }
            if (-not $valid) {
                $entry.Boundary.taxonomyClass =
                    'fallback_tail_yield_authority_invalid'
                if ($failureVariant -cne '(none)') {
                    $entry.Boundary | Add-Member `
                        -NotePropertyName variant `
                        -NotePropertyValue $failureVariant `
                        -Force
                }
                $entry.Boundary.domain = 'route_runtime'
                $entry.Boundary.phase = 'fallback_tail_yield'
                $entry.Boundary.rawCount = 1
                $entry.Boundary.distinctCount = 1
                $entry.Boundary.recoveredCount = 0
                $entry.Boundary.unresolvedCount = 1
                $entry.Boundary.verdict = 'fail'
            }
        }

        $pendingReauthorization = @($history | Where-Object {
                [string]$_.Operation.operationRole -ceq
                    'reauthorization_pending' -and
                [string]$_.Operation.afterState -ceq 'Pending'
            } | Select-Object -Last 1) | Select-Object -First 1
        if ($null -ne $pendingReauthorization) {
            $closedReauthorization = @($history | Where-Object {
                    [long]$_.Boundary.decisionUtcMilliseconds -ge
                        [long]$pendingReauthorization.Boundary.decisionUtcMilliseconds -and
                    (([string]$_.Operation.operationRole -ceq
                        'reauthorization_terminal' -and
                      [string]$_.Operation.afterState -ceq 'Invalidated') -or
                     ([string]$_.Operation.operationRole -ceq 'yield_current' -and
                      [long]$_.Operation.fallbackTailYield.reauthorizationGeneration -eq
                        [long]$pendingReauthorization.Operation.operationGeneration -and
                      [long]$_.Operation.fallbackTailYield.reauthorizationTargetTransportEpoch -gt
                        [long]$pendingReauthorization.Operation.fallbackTailYield.reauthorizationSourceTransportEpoch))
                })
            if ($closedReauthorization.Count -eq 0) {
                $pendingReauthorization.Boundary.taxonomyClass =
                    'post_tuna_fallback_paired_yield_transport_epoch_reauthorization_missing'
                $pendingReauthorization.Boundary.domain = 'route_runtime'
                $pendingReauthorization.Boundary.phase = 'fallback_tail_yield'
                $pendingReauthorization.Boundary.rawCount = 1
                $pendingReauthorization.Boundary.distinctCount = 1
                $pendingReauthorization.Boundary.recoveredCount = 0
                $pendingReauthorization.Boundary.unresolvedCount = 1
                $pendingReauthorization.Boundary.verdict = 'fail'
            }
        }
    }

    $retryEntries = @($operationHistory | Where-Object {
            [string]$_.Operation.family -ceq
                'runtime_unlock_retry_lifecycle' -and
            [string]$_.Operation.operationRole -ne 'attempted_decision'
        })
    $retryAttempts = @($retryEntries | Where-Object {
            [string]$_.Operation.operationKind -ceq 'RetryAttempt'
        })
    # Failed artifacts may still report exact negative role findings consistently;
    # they cannot establish any cancellation of a receipt-effect obligation.
    $provisionalRetryReduction = Get-FileTransferCoordinatorProvisionalRetryReduction `
        -AggregateEntries @($aggregateEntries) -OperationHistory $operationHistory -DiagnosticOnly:($errors.Count -gt 0)
    foreach ($failure in $provisionalRetryReduction.Errors) { $errors.Add([string]$failure) }
    foreach ($effect in @($effects | Where-Object {
        $_.EffectKind -ceq 'settlement_observed' -and $_.ReceiptStatus -ceq 'Superseded' -and
        $_.OperationKey -clike 'runtime_unlock_retry_lifecycle|attempt_retained_predecessor|*'
    })) {
        $errors.Add("snapshot_provisional_retry_retained_settlement_effect_forbidden:$($effect.LineIndex)")
    }
    $neutralRetirements = @{}
    foreach ($entry in @($aggregateEntries | Where-Object {
        $_.Boundary.decisionKind -ceq 'ObserveRuntimeUnlockRetryDispatchSettlement' -and
        $_.Boundary.decisionStatus -in @('Rejected', 'Idempotent') -and
        @($_.Boundary.operations | Where-Object {
            $_.operationRole -ceq 'attempt_terminal' -and $null -ne $_.runtimeUnlockRetry -and
            @($_.runtimeUnlockRetry.receipts | Where-Object { $_.stage -ceq 'settlement' -and $_.receiptStatus -ceq 'Superseded' }).Count -gt 0
        }).Count -gt 0
    })) { $neutralRetirements["$($entry.Boundary.coordinatorInstanceId)|$($entry.Boundary.writer)|$($entry.Boundary.boundaryId)"] = $true }
    foreach ($effect in $effects) {
        if ($neutralRetirements.ContainsKey("$($effect.CoordinatorInstanceId)|$($effect.Writer)|$($effect.TransitionId)")) {
            $errors.Add("snapshot_successful_retry_retirement_attempt_effect_forbidden:$($effect.LineIndex)")
        }
    }
    $successfulTerminalRemovals = @{}; $successfulSuccessorTerminals = @{}
    foreach ($witness in $provisionalRetryReduction.SuccessfulRetirements.Values) {
        if (-not $witness.TerminalRemoval) { continue }
        $boundary = $witness.Entry.Boundary
        $key = @($boundary.coordinatorInstanceId, $boundary.writer, $boundary.boundaryId,
            $witness.Entry.Operation.operationKey) -join [char]31
        $successfulTerminalRemovals[$key] = $true
        $successor = $witness.Successor
        $key = @($successor.Boundary.coordinatorInstanceId, $successor.Boundary.writer, $successor.Boundary.boundaryId,
            $successor.Operation.operationKey) -join [char]31
        $successfulSuccessorTerminals[$key] = $true
    }
    foreach ($effect in $effects) {
        $key = @($effect.CoordinatorInstanceId, $effect.Writer, $effect.TransitionId, $effect.OperationKey) -join [char]31
        if ($successfulTerminalRemovals.ContainsKey($key)) {
            $errors.Add("snapshot_successful_retry_terminal_removal_effect_forbidden:$($effect.LineIndex)")
        }
        if ($successfulSuccessorTerminals.ContainsKey($key) -and $effect.EffectKind -cne 'settlement_observed') {
            # B's terminal closes its already executed installation; a new
            # terminal execution effect cannot replace the original send proof.
            $errors.Add("snapshot_successful_retry_successor_terminal_effect_forbidden:$($effect.LineIndex)")
        }
    }
    if ($errors.Count -gt 0) {
        $provisionalRetryReduction.SuccessfulRetirements.Clear(); $provisionalRetryReduction.CancelledReceipts.Clear()
        $provisionalRetryReduction.TerminalEvictions.Clear()
    }
    $evictionBoundaries = @{}
    foreach ($witness in $provisionalRetryReduction.TerminalEvictions.Values) {
        $boundary = $witness.Entry.Boundary
        $evictionBoundaries["$($boundary.coordinatorInstanceId)|$($boundary.writer)|$($boundary.boundaryId)"] = $true
    }
    $forbiddenEvictionEffect = $false
    foreach ($effect in @($effects | Where-Object { $_.EffectKind -ceq 'install_committed' })) {
        if ($evictionBoundaries.ContainsKey("$($effect.CoordinatorInstanceId)|$($effect.Writer)|$($effect.TransitionId)")) {
            $errors.Add("snapshot_retry_confirmation_eviction_install_effect_forbidden:$($effect.LineIndex)")
            $forbiddenEvictionEffect = $true
        }
    }
    if ($forbiddenEvictionEffect) {
        $provisionalRetryReduction.TerminalEvictions.Clear(); $provisionalRetryReduction.CancelledReceipts.Clear()
        $provisionalRetryReduction.SuccessfulRetirements.Clear()
    }
    foreach ($authorityGroup in @($retryAttempts | Group-Object {
                "$($_.Boundary.coordinatorInstanceId)|$([string]$_.Operation.authorityId)"
            })) {
        $history = @($authorityGroup.Group | Sort-Object {
                [long]$_.Boundary.decisionUtcMilliseconds
            }, {
                [long]$_.Boundary.afterStateGeneration
            }, {
                [string]$_.Boundary.boundaryId
            })
        if ($history.Count -eq 0) {
            continue
        }

        $authorityId = [string]$history[0].Operation.authorityId
        $instanceId = [string]$history[0].Boundary.coordinatorInstanceId
        $provisionalParticipant = $null
        if ($null -ne $provisionalRetryReduction) {
            $scopeKeys = @($history | ForEach-Object {
                Get-FileTransferCoordinatorProvisionalRetryScopeKey $_.Boundary
            } | Select-Object -Unique)
            if ($scopeKeys.Count -eq 1 -and $provisionalRetryReduction.InvalidScopes.ContainsKey($scopeKeys[0])) { continue }
            $participantKeys = @($history | ForEach-Object {
                "$(Get-FileTransferCoordinatorProvisionalRetryScopeKey $_.Boundary)$([char]31)$($_.Operation.authorityId)$([char]31)$($_.Operation.operationGeneration)"
            } | Select-Object -Unique)
            if ($participantKeys.Count -eq 1 -and $provisionalRetryReduction.Participants.ContainsKey($participantKeys[0])) {
                $provisionalParticipant = $provisionalRetryReduction.Participants[$participantKeys[0]]
            }
        }
        $sourceBoundary = $history[0].Operation.runtimeUnlockRetry.sourceBoundary
        $exactEffects = @($effects | Where-Object {
                [string]$_.CoordinatorInstanceId -ceq $instanceId -and
                [string]$_.AuthorityId -ceq $authorityId -and
                ($null -eq $provisionalParticipant -or
                 ([string]$_.Writer -ceq [string]$history[0].Boundary.writer -and
                  [long]$_.OperationGeneration -eq [long]$provisionalParticipant.Generation))
            })
        $committedInstallProofs = [Collections.Generic.List[object]]::new()
        foreach ($installEntry in @($history | Where-Object {
                    [string]$_.Operation.operationRole -ceq
                        'attempt_installed' -and
                    [string]$_.Operation.operationKind -ceq 'RetryAttempt' -and
                    [string]$_.Operation.beforeState -ceq 'None' -and
                    [string]$_.Operation.afterState -ceq 'Installed' -and
                    [bool]$_.Operation.stateApplied
                })) {
            $installReceipts = @(
                $installEntry.Operation.runtimeUnlockRetry.receipts |
                    Where-Object {
                        [string]$_.stage -ceq 'install' -and
                        [string]$_.receiptStatus -ceq 'Installed' -and
                        [bool]$_.receiptValidated
                    })
            if ($installReceipts.Count -ne 1) {
                continue
            }
            $installReceipt = $installReceipts[0]
            $installEffects = @($exactEffects | Where-Object {
                    [string]$_.EffectKind -ceq 'install_committed' -and
                    [string]$_.TransitionId -ceq
                        [string]$installEntry.Boundary.boundaryId -and
                    [string]$_.OperationKey -ceq
                        [string]$installEntry.Operation.operationKey -and
                    [long]$_.StateGeneration -eq
                        [long]$installEntry.Boundary.afterStateGeneration -and
                    [string]$_.ReceiptIdentity -ceq
                        [string]$installReceipt.receiptIdentity -and
                    [long]$_.ReceiptGeneration -eq
                        [long]$installReceipt.receiptGeneration -and
                    [string]$_.ReceiptStatus -ceq 'Installed' -and
                    [string]$_.ReceiptFingerprint -ceq
                        [string]$installReceipt.receiptFingerprint -and
                    (Test-FileTransferCoordinatorSnapshotExactEffectBinding `
                        -Boundary $installEntry.Boundary `
                        -Operation $installEntry.Operation `
                        -Effect $_)
                })
            if ($installEffects.Count -eq 1) {
                $committedInstallProofs.Add([pscustomobject]@{
                    Entry = $installEntry
                    Receipt = $installReceipt
                })
            }
        }
        $missingRequiredReceiptEffect = $null
        foreach ($entry in $history) {
            # Exact compensation only reverses its immediately preceding commit.
            # Original stage/install/settlement entries still require their own
            # effects; this boundary neither applies nor republishes an effect.
            if ($retryConfirmationCompensations.ContainsKey("$($entry.Boundary.coordinatorInstanceId)|$($entry.Boundary.boundaryId)")) { continue }
            $successfulKey = "$(Get-FileTransferCoordinatorProvisionalRetryScopeKey $entry.Boundary)$([char]31)$($entry.Boundary.boundaryId)$([char]31)$($entry.Operation.operationKey)"
            if ($null -ne $provisionalParticipant -and $provisionalRetryReduction.SuccessfulRetirements.ContainsKey($successfulKey)) {
                $retirement = $provisionalRetryReduction.SuccessfulRetirements[$successfulKey]
                if ($retirement.TerminalRemoval) {
                    # Only this validated slot eviction reuses the original A
                    # observation. Original A evidence and B's own closure remain
                    # independent obligations in their respective histories.
                    if ($retirement.Operation -cne ($entry.Operation | ConvertTo-Json -Depth 32 -Compress) -or
                        -not (Test-FileTransferCoordinatorSuccessfulRetryRetirementObservation -Terminal $retirement.Terminal -Effects $exactEffects)) {
                        $missingRequiredReceiptEffect = [pscustomobject]@{ Entry=$entry; Stage='settlement';
                            Receipt=@($entry.Operation.runtimeUnlockRetry.receipts | Where-Object stage -CEQ 'settlement')[0] }
                        break
                    }
                    continue
                }
            }
            if (-not [bool]$entry.Operation.stateApplied -and
                [string]$entry.Operation.beforeState -ceq
                    [string]$entry.Operation.afterState -and
                [string]$entry.Operation.beforeState -cne 'None') {
                $receiptSignature = $entry.Operation.runtimeUnlockRetry.receipts |
                    ConvertTo-Json -Depth 32 -Compress
                $priorAppliedHistory = @($history | Where-Object {
                        [bool]$_.Operation.stateApplied -and
                        [string]$_.Operation.operationRole -ceq
                            [string]$entry.Operation.operationRole -and
                        [string]$_.Operation.authorityId -ceq
                            [string]$entry.Operation.authorityId -and
                        [long]$_.Operation.operationGeneration -eq
                            [long]$entry.Operation.operationGeneration -and
                        [long]$_.Boundary.afterStateGeneration -le
                            [long]$entry.Boundary.beforeStateGeneration
                    } | Sort-Object {
                        [long]$_.Boundary.afterStateGeneration
                    }, {
                        [long]$_.Boundary.decisionUtcMilliseconds
                    }, {
                        [string]$_.Boundary.boundaryId
                    })
                $latestPrior = if ($priorAppliedHistory.Count -gt 0) {
                    $priorAppliedHistory[-1]
                }
                else {
                    $null
                }
                if ($null -ne $latestPrior -and
                    [string]$latestPrior.Operation.afterState -ceq
                        [string]$entry.Operation.beforeState -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                        -Left $latestPrior.Operation.runtimeUnlockRetry.sourceBoundary `
                        -Right $entry.Operation.runtimeUnlockRetry.sourceBoundary) -and
                    ($latestPrior.Operation.runtimeUnlockRetry.receipts |
                        ConvertTo-Json -Depth 32 -Compress) -ceq
                        $receiptSignature) {
                    continue
                }
            }
            foreach ($receipt in @($entry.Operation.runtimeUnlockRetry.receipts)) {
                if (-not [bool]$receipt.receiptValidated) {
                    continue
                }
                $requiredEffectKind = switch -Exact ([string]$receipt.stage) {
                    'stage' { 'dispatch_stage_observed'; break }
                    'install' {
                        if (Test-FileTransferCoordinatorSnapshotStagedRetryInstallReceipt `
                                -Operation $entry.Operation) {
                            break
                        }
                        if ([string]$entry.Operation.operationRole -ceq
                                'attempt_installed' -and
                            [string]$entry.Operation.beforeState -ceq 'None' -and
                            [string]$entry.Operation.afterState -ceq 'Installed') {
                            'install_committed'
                            break
                        }
                        if ([string]$entry.Operation.operationRole -ceq
                                'attempt_terminal' -and
                            [string]$entry.Operation.beforeState -cne 'None') {
                            $evictionKey = "$(Get-FileTransferCoordinatorProvisionalRetryScopeKey $entry.Boundary)$([char]31)$($entry.Boundary.boundaryId)$([char]31)$($entry.Operation.operationKey)$([char]31)$($entry.Operation.operationGeneration)"
                            if ($null -ne $provisionalParticipant -and $entry.Operation.beforeState -ceq 'Invalidated' -and
                                $entry.Operation.afterState -ceq 'None' -and $receipt.receiptStatus -ceq 'Invalidated' -and
                                $provisionalRetryReduction.TerminalEvictions.ContainsKey($evictionKey)) {
                                $witness = $provisionalRetryReduction.TerminalEvictions[$evictionKey]
                                $origin = $witness.Origin
                                $originalReceipt = @($origin.Installed.runtimeUnlockRetry.receipts | Where-Object stage -CEQ 'install')[0]
                                $terminalReceipt = @($origin.Terminal.runtimeUnlockRetry.receipts | Where-Object stage -CEQ 'install')[0]
                                $originProofs = @($committedInstallProofs | Where-Object {
                                    $_.Entry.Boundary.boundaryId -ceq $origin.Commit.boundaryId -and
                                    $_.Entry.Boundary.afterStateGeneration -eq $origin.Commit.afterStateGeneration -and
                                    $_.Entry.Boundary.afterStateHash -ceq $origin.Commit.afterStateHash -and
                                    (Get-FileTransferCoordinatorProvisionalRetryScopeKey $_.Entry.Boundary) -ceq
                                        (Get-FileTransferCoordinatorProvisionalRetryScopeKey $origin.Commit) -and
                                    ($_.Entry.Operation | ConvertTo-Json -Depth 32 -Compress) -ceq
                                        ($origin.Installed | ConvertTo-Json -Depth 32 -Compress) -and
                                    ($_.Receipt | ConvertTo-Json -Depth 16 -Compress) -ceq
                                        ($originalReceipt | ConvertTo-Json -Depth 16 -Compress)
                                })
                                if ($originProofs.Count -ne 1 -or
                                    $witness.Operation -cne ($entry.Operation | ConvertTo-Json -Depth 32 -Compress) -or
                                    ($receipt | ConvertTo-Json -Depth 16 -Compress) -cne ($terminalReceipt | ConvertTo-Json -Depth 16 -Compress)) {
                                    $missingRequiredReceiptEffect = [pscustomobject]@{ Entry=$entry; Stage='install'; Receipt=$receipt }
                                }
                                break
                            }
                            'install_committed'
                            break
                        }
                        $priorInstallProofs = @($committedInstallProofs |
                            Where-Object {
                                [string]$_.Entry.Operation.operationRole -ceq
                                    'attempt_installed' -and
                                [string]$_.Entry.Operation.authorityId -ceq
                                    [string]$entry.Operation.authorityId -and
                                [long]$_.Entry.Operation.operationGeneration -eq
                                    [long]$entry.Operation.operationGeneration -and
                                [long]$_.Entry.Boundary.afterStateGeneration -le
                                    [long]$entry.Boundary.beforeStateGeneration -and
                                [string]$_.Receipt.receiptIdentity -ceq
                                    [string]$receipt.receiptIdentity -and
                                [long]$_.Receipt.receiptGeneration -eq
                                    [long]$receipt.receiptGeneration -and
                                [string]$_.Receipt.receiptFingerprint -ceq
                                    [string]$receipt.receiptFingerprint -and
                                (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                                    -Left $_.Entry.Operation.runtimeUnlockRetry.sourceBoundary `
                                    -Right $entry.Operation.runtimeUnlockRetry.sourceBoundary)
                            })
                        if ($priorInstallProofs.Count -ne 1) {
                            $missingRequiredReceiptEffect = [pscustomobject]@{
                                Entry = $entry
                                Stage = [string]$receipt.stage
                                Receipt = $receipt
                            }
                        }
                        break
                    }
                    'execution' {
                        if ([bool]$entry.Operation.runtimeUnlockRetry.dispatchObserved) {
                            if ([string]$entry.Operation.operationRole -ceq
                                    'attempt_terminal' -and
                                [string]$entry.Operation.beforeState -ceq 'None' -and
                                [string]$entry.Operation.afterState -ceq 'Settled') {
                                $retiredInstalled = @($history | Where-Object {
                                        [string]$_.Boundary.boundaryId -ceq
                                            [string]$entry.Boundary.boundaryId -and
                                        [string]$_.Operation.operationRole -ceq
                                            'attempt_installed' -and
                                        [string]$_.Operation.authorityId -ceq
                                            [string]$entry.Operation.authorityId -and
                                        [long]$_.Operation.operationGeneration -eq
                                            [long]$entry.Operation.operationGeneration -and
                                        [string]$_.Operation.afterState -ceq 'None' -and
                                        [bool]$_.Operation.stateApplied -and
                                        (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                                            -Left $_.Operation.runtimeUnlockRetry.sourceBoundary `
                                            -Right $entry.Operation.runtimeUnlockRetry.sourceBoundary)
                                    })
                                $retiredInstalledState = if ($retiredInstalled.Count -eq 1) {
                                    [string]$retiredInstalled[0].Operation.beforeState
                                }
                                else {
                                    ''
                                }
                                $priorExecutionProofs = @($history | Where-Object {
                                        [string]$_.Operation.operationRole -ceq
                                            'attempt_installed' -and
                                        [string]$_.Operation.authorityId -ceq
                                            [string]$entry.Operation.authorityId -and
                                        [long]$_.Operation.operationGeneration -eq
                                            [long]$entry.Operation.operationGeneration -and
                                        [string]$_.Operation.afterState -ceq $retiredInstalledState -and
                                        [long]$_.Boundary.afterStateGeneration -le
                                            [long]$entry.Boundary.beforeStateGeneration -and
                                        (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                                            -Left $_.Operation.runtimeUnlockRetry.sourceBoundary `
                                            -Right $entry.Operation.runtimeUnlockRetry.sourceBoundary) -and
                                        @($_.Operation.runtimeUnlockRetry.receipts | Where-Object {
                                                [string]$_.stage -ceq 'execution' -and
                                                [bool]$_.receiptValidated -and
                                                [string]$_.commandIdentity -ceq
                                                    [string]$receipt.commandIdentity -and
                                                [long]$_.commandGeneration -eq
                                                    [long]$receipt.commandGeneration -and
                                                [string]$_.commandFingerprint -ceq
                                                    [string]$receipt.commandFingerprint -and
                                                [string]$_.receiptIdentity -ceq
                                                    [string]$receipt.receiptIdentity -and
                                                [long]$_.receiptGeneration -eq
                                                    [long]$receipt.receiptGeneration -and
                                                [string]$_.receiptStatus -ceq
                                                    $retiredInstalledState
                                            }).Count -eq 1
                                    })
                                $boundPriorExecutionProofs = @($priorExecutionProofs | Where-Object {
                                        $priorEntry = $_
                                        $priorReceipt = @($priorEntry.Operation.runtimeUnlockRetry.receipts |
                                            Where-Object {
                                                [string]$_.stage -ceq 'execution' -and
                                                [string]$_.receiptStatus -ceq
                                                    $retiredInstalledState
                                            })[0]
                                        @($exactEffects | Where-Object {
                                                [string]$_.EffectKind -ceq 'execution_observed' -and
                                                [string]$_.TransitionId -ceq
                                                    [string]$priorEntry.Boundary.boundaryId -and
                                                [string]$_.OperationKey -ceq
                                                    [string]$priorEntry.Operation.operationKey -and
                                                [long]$_.StateGeneration -eq
                                                    [long]$priorEntry.Boundary.afterStateGeneration -and
                                                [string]$_.ReceiptIdentity -ceq
                                                    [string]$priorReceipt.receiptIdentity -and
                                                [long]$_.ReceiptGeneration -eq
                                                    [long]$priorReceipt.receiptGeneration -and
                                                [string]$_.ReceiptStatus -ceq
                                                    [string]$priorReceipt.receiptStatus -and
                                                [string]$_.ReceiptFingerprint -ceq
                                                    [string]$priorReceipt.receiptFingerprint -and
                                                (Test-FileTransferCoordinatorSnapshotExactEffectBinding `
                                                    -Boundary $priorEntry.Boundary `
                                                    -Operation $priorEntry.Operation `
                                                    -Effect $_)
                                            }).Count -eq 1
                                    })
                                if ($boundPriorExecutionProofs.Count -eq 1) {
                                    break
                                }
                            }
                            'execution_observed'
                        }
                        break
                    }
                    'settlement' {
                        if ($null -ne $provisionalParticipant) {
                            $cancelledKey = "$(Get-FileTransferCoordinatorProvisionalRetryScopeKey $entry.Boundary)$([char]31)$($entry.Boundary.boundaryId)$([char]31)$($entry.Operation.operationKey)"
                            if ($provisionalRetryReduction.CancelledReceipts.ContainsKey($cancelledKey)) {
                                $cancelled = $provisionalRetryReduction.CancelledReceipts[$cancelledKey]
                                if ($cancelled.Operation -ceq ($entry.Operation | ConvertTo-Json -Depth 32 -Compress) -and
                                    $cancelled.Receipt -ceq ($receipt | ConvertTo-Json -Depth 16 -Compress)) { break }
                            }
                            if ($provisionalRetryReduction.SuccessfulRetirements.ContainsKey($cancelledKey)) {
                                # Successful cleanup is observed, never cancelled. Both
                                # retained descriptors resolve only to the entering terminal.
                                $retirement = $provisionalRetryReduction.SuccessfulRetirements[$cancelledKey]
                                $terminal = $retirement.Terminal
                                $terminalReceipt = @($terminal.Operation.runtimeUnlockRetry.receipts | Where-Object stage -CEQ 'settlement')[0]
                                if ($retirement.Operation -cne ($entry.Operation | ConvertTo-Json -Depth 32 -Compress) -or
                                    ($receipt | ConvertTo-Json -Depth 16 -Compress) -cne ($terminalReceipt | ConvertTo-Json -Depth 16 -Compress) -or
                                    -not (Test-FileTransferCoordinatorSuccessfulRetryRetirementObservation -Terminal $terminal -Effects $exactEffects)) {
                                    $missingRequiredReceiptEffect = [pscustomobject]@{ Entry=$entry; Stage='settlement'; Receipt=$receipt }
                                }
                                break
                            }
                        }
                        'settlement_observed'; break
                    }
                    default { '' }
                }
                if ([string]::IsNullOrWhiteSpace($requiredEffectKind)) {
                    if ($null -ne $missingRequiredReceiptEffect) {
                        break
                    }
                    continue
                }
                $matchingEffects = @($exactEffects | Where-Object {
                        [string]$_.EffectKind -ceq $requiredEffectKind -and
                        ($requiredEffectKind -cne 'install_committed' -or
                         [string]$entry.Operation.beforeState -cne 'None' -or
                         [string]$entry.Operation.afterState -cne 'Installed' -or
                         ([string]$_.TransitionId -ceq [string]$entry.Boundary.boundaryId -and
                          [string]$_.OperationKey -ceq [string]$entry.Operation.operationKey -and
                          [long]$_.StateGeneration -eq [long]$entry.Boundary.afterStateGeneration)) -and
                        [string]$_.ReceiptIdentity -ceq
                            [string]$receipt.receiptIdentity -and
                        [long]$_.ReceiptGeneration -eq
                            [long]$receipt.receiptGeneration -and
                        [string]$_.ReceiptStatus -ceq
                            [string]$receipt.receiptStatus -and
                        [string]$_.ReceiptFingerprint -ceq
                            [string]$receipt.receiptFingerprint
                    })
                if ($matchingEffects.Count -ne 1) {
                    $missingRequiredReceiptEffect = [pscustomobject]@{
                        Entry = $entry
                        Stage = [string]$receipt.stage
                        Receipt = $receipt
                    }
                    break
                }
            }
            if ($null -ne $missingRequiredReceiptEffect) {
                break
            }
        }
        if ($null -ne $missingRequiredReceiptEffect) {
            $candidate = $missingRequiredReceiptEffect.Entry
            $missingTaxonomy =
                if ($missingRequiredReceiptEffect.Stage -in @('stage', 'install')) {
                    'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete'
                }
                else {
                    'runtime_unlock_retry_dispatch_contract_unsettled'
                }
            if ($null -ne $provisionalParticipant) {
                Add-FileTransferCoordinatorProvisionalRetryFinding $provisionalRetryReduction $candidate `
                    $missingTaxonomy 'required_receipt_effect' $missingRequiredReceiptEffect.Receipt
            }
            else {
                $candidate.Boundary.taxonomyClass = $missingTaxonomy
                $candidate.Boundary.domain = 'route_runtime'
                $candidate.Boundary.phase = 'runtime_unlock_retry'
                $candidate.Boundary.rawCount = 1
                $candidate.Boundary.distinctCount = 1
                $candidate.Boundary.recoveredCount = 0
                $candidate.Boundary.unresolvedCount = 1
                $candidate.Boundary.verdict = 'fail'
            }
        }
        $missingDispatch = @($history | Where-Object {
                @($_.Operation.runtimeUnlockRetry.receipts | Where-Object {
                        [string]$_.stage -ceq 'settlement' -and
                        [string]$_.receiptStatus -ceq 'MissingDispatch' -and
                        [bool]$_.receiptValidated
                    }).Count -eq 1
            } | Select-Object -First 1) | Select-Object -First 1
        $closureRejections = @(
            $history | Where-Object {
                [string]$_.Boundary.decisionKind -ceq
                    'PrepareRuntimeUnlockRetryDispatchSuccessor' -and
                [string]$_.Boundary.decisionStatus -ceq 'Rejected' -and
                [string]$_.Boundary.decisionReason -ceq
                    'retry_dispatch_missing_successor_limit_exhausted' -and
                (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                    -Left $_.Operation.runtimeUnlockRetry.sourceBoundary `
                    -Right $sourceBoundary)
            } | Group-Object {
                [string]$_.Boundary.boundaryId
            } | ForEach-Object { $_.Group | Select-Object -First 1 }
        )
        foreach ($closureRejection in $closureRejections) {
            $matchingClosures = @($retryEntries | Where-Object {
                    [string]$_.Boundary.coordinatorInstanceId -ceq $instanceId -and
                    [string]$_.Operation.operationRole -ceq 'bounded_closure' -and
                    [string]$_.Operation.authorityId -ceq $authorityId -and
                    [string]$_.Operation.afterState -ceq 'Closed' -and
                    [bool]$_.Operation.receiptValidated -and
                    [long]$_.Boundary.decisionUtcMilliseconds -ge
                        [long]$closureRejection.Boundary.decisionUtcMilliseconds -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                        -Left $_.Operation.runtimeUnlockRetry.sourceBoundary `
                        -Right $sourceBoundary) -and
                    @($_.Operation.runtimeUnlockRetry.receipts | Where-Object {
                            [string]$_.stage -ceq 'bounded_closure' -and
                            [string]$_.receiptStatus -ceq 'Closed' -and
                            [bool]$_.receiptValidated
                        }).Count -eq 1
                })
            $closureRejection.Boundary.taxonomyClass =
                'runtime_unlock_retry_dispatch_chain_closure_incomplete'
            $closureRejection.Boundary.domain = 'route_runtime'
            $closureRejection.Boundary.phase = 'runtime_unlock_retry'
            $closureRejection.Boundary.rawCount = 1
            $closureRejection.Boundary.distinctCount = 1
            $closureRejection.Boundary.recoveredCount =
                if ($matchingClosures.Count -eq 1) { 1 } else { 0 }
            $closureRejection.Boundary.unresolvedCount =
                if ($matchingClosures.Count -eq 1) { 0 } else { 1 }
            $closureRejection.Boundary.verdict =
                if ($matchingClosures.Count -eq 1) { 'pass' } else { 'fail' }
        }
        if ($null -ne $missingDispatch) {
            $successors = @($retryAttempts | Where-Object {
                    [string]$_.Boundary.coordinatorInstanceId -ceq $instanceId -and
                    [string]$_.Operation.predecessorAuthorityId -ceq $authorityId -and
                    [string]$_.Operation.authorityId -cne $authorityId -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                        -Left $_.Operation.runtimeUnlockRetry.sourceBoundary `
                        -Right $sourceBoundary) -and
                    [string]$_.Operation.runtimeUnlockRetry.releaseKind -ceq
                        [string]$history[0].Operation.runtimeUnlockRetry.releaseKind -and
                    [string]$_.Operation.runtimeUnlockRetry.releaseAuthorityId -ceq
                        [string]$history[0].Operation.runtimeUnlockRetry.releaseAuthorityId -and
                    [long]$_.Operation.runtimeUnlockRetry.releaseGeneration -eq
                        [long]$history[0].Operation.runtimeUnlockRetry.releaseGeneration -and
                    [long]$_.Boundary.decisionUtcMilliseconds -ge
                        [long]$missingDispatch.Boundary.decisionUtcMilliseconds
                })
            $successorRecovered = $false
            foreach ($successorGroup in @($successors | Group-Object {
                        [string]$_.Operation.authorityId
                    })) {
                $successorHistory = @($successorGroup.Group)
                $install = @($successorHistory | Where-Object {
                        @($_.Operation.runtimeUnlockRetry.receipts | Where-Object {
                                [string]$_.stage -ceq 'install' -and
                                [bool]$_.receiptValidated
                            }).Count -eq 1
                    })
                $dispatch = @($successorHistory | Where-Object {
                        [bool]$_.Operation.runtimeUnlockRetry.dispatchObserved -and
                        @($_.Operation.runtimeUnlockRetry.receipts | Where-Object {
                                [string]$_.stage -ceq 'execution' -and
                                [string]$_.receiptReason -ceq 'dispatch_observed' -and
                                [bool]$_.receiptValidated
                            }).Count -eq 1
                    })
                $dispatchEffects = @($effects | Where-Object {
                        [string]$_.CoordinatorInstanceId -ceq $instanceId -and
                        [string]$_.AuthorityId -ceq
                            [string]$successorHistory[0].Operation.authorityId -and
                        [string]$_.EffectKind -ceq 'execution_observed'
                    })
                if ($install.Count -gt 0 -and $dispatch.Count -gt 0 -and
                    $dispatchEffects.Count -eq 1) {
                    $successorRecovered = $true
                    break
                }
            }
            $closed = @($retryEntries | Where-Object {
                    [string]$_.Boundary.coordinatorInstanceId -ceq $instanceId -and
                    [string]$_.Operation.operationRole -ceq 'bounded_closure' -and
                    [string]$_.Operation.authorityId -ceq $authorityId -and
                    [string]$_.Operation.afterState -ceq 'Closed' -and
                    [bool]$_.Operation.receiptValidated
                }).Count -gt 0
            if (-not $closed -and $closureRejections.Count -eq 0) {
                $missingDispatch.Boundary.taxonomyClass =
                    'runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch'
                $missingDispatch.Boundary.domain = 'route_runtime'
                $missingDispatch.Boundary.phase = 'runtime_unlock_retry'
                $missingDispatch.Boundary.rawCount = 1
                $missingDispatch.Boundary.distinctCount = 1
                $missingDispatch.Boundary.recoveredCount =
                    if ($successorRecovered) { 1 } else { 0 }
                $missingDispatch.Boundary.unresolvedCount =
                    if ($successorRecovered) { 0 } else { 1 }
                $missingDispatch.Boundary.verdict =
                    if ($successorRecovered) { 'pass' } else { 'fail' }
            }
        }

        if ($null -ne $provisionalParticipant) {
            foreach ($openRole in $provisionalParticipant.OpenRoles) {
                $hasInstall = @($openRole.Operation.runtimeUnlockRetry.receipts | Where-Object {
                    $_.stage -ceq 'install' -and $_.receiptValidated
                }).Count -eq 1
                $class = if ($hasInstall) { 'runtime_unlock_retry_dispatch_contract_unsettled' }
                    else { 'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete' }
                Add-FileTransferCoordinatorProvisionalRetryFinding $provisionalRetryReduction $openRole $class 'current_role_open'
            }
            continue
        }
        $latest = $history[-1]
        $latestState = [string]$latest.Operation.afterState
        if ($latestState -in @(
                'Authorized', 'Staged', 'ReceiptIssued', 'Installed',
                'AliasBound', 'SendInFlight')) {
            $hasInstall = @($history | Where-Object {
                    @($_.Operation.runtimeUnlockRetry.receipts | Where-Object {
                            [string]$_.stage -ceq 'install' -and
                            [bool]$_.receiptValidated
                        }).Count -eq 1
                }).Count -gt 0
            $latest.Boundary.taxonomyClass = if ($hasInstall) {
                'runtime_unlock_retry_dispatch_contract_unsettled'
            }
            else {
                'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete'
            }
            $latest.Boundary.domain = 'route_runtime'
            $latest.Boundary.phase = 'runtime_unlock_retry'
            $latest.Boundary.rawCount = 1
            $latest.Boundary.distinctCount = 1
            $latest.Boundary.recoveredCount = 0
            $latest.Boundary.unresolvedCount = 1
            $latest.Boundary.verdict = 'fail'
        }
    }

    if ($null -ne $provisionalRetryReduction) {
        foreach ($group in @($provisionalRetryReduction.Findings | Group-Object BoundaryKey)) {
            $selected = @($group.Group | Sort-Object Priority, TieKey)[0]
            $boundary = $selected.Entry.Boundary
            $boundary.taxonomyClass = $selected.Taxonomy
            $boundary.domain = 'route_runtime'; $boundary.phase = 'runtime_unlock_retry'
            $boundary.rawCount = 1; $boundary.distinctCount = 1
            $boundary.recoveredCount = 0; $boundary.unresolvedCount = 1; $boundary.verdict = 'fail'
        }
    }

    $heldPublicationProofClosures = [Collections.Generic.List[object]]::new()
    $receiveRecoveryEntries = @($operationHistory | Where-Object {
            [string]$_.Operation.family -ceq 'receive_recovery' -and
            [string]$_.Operation.operationRole -ne 'attempted_decision'
        })
    $validateRecoveryRoots = $errors.Count -eq 0
    foreach ($authorityGroup in @($receiveRecoveryEntries | Group-Object {
                "$($_.Boundary.coordinatorInstanceId)|$($_.Boundary.writer)|$([string]$_.Operation.authorityId)"
            })) {
        $history = @($authorityGroup.Group | Sort-Object {
                [long]$_.Boundary.decisionUtcMilliseconds
            }, {
                [long]$_.Boundary.afterStateGeneration
            }, {
                [string]$_.Boundary.boundaryId
            })
        if ($history.Count -eq 0) {
            continue
        }

        $operationEntries = @($history | Where-Object {
                [string]$_.Operation.operationRole -in @(
                    'operation_current', 'operation_terminal')
            })
        $rootLifecycle = Get-FileTransferCoordinatorReceiveRecoveryRootLifecycle -Entries $operationEntries -AggregateIndex $aggregateBoundaryIndex -ValidatedHandoffSuccessors $validatedHandoffSuccessors
        if ($validateRecoveryRoots) {
            foreach ($failure in $rootLifecycle.Errors) { $errors.Add([string]$failure) }
        }
        $latestOperation = if ($null -ne $rootLifecycle.Poisoned) { $rootLifecycle.Poisoned } else { $rootLifecycle.Latest }
        $candidate = $null
        $class = ''
        $phase = 'bridge_recovery_execution'
        $instanceId = [string]$history[0].Boundary.coordinatorInstanceId
        $authorityId = [string]$history[0].Operation.authorityId
        $authorityEffects = @($effects | Where-Object {
                $_.CoordinatorInstanceId -ceq $instanceId -and
                $_.Family -ceq 'receive_recovery' -and
                $_.AuthorityId -ceq $authorityId
            })
        foreach ($publicationGroup in @($history | Where-Object {
                $_.Operation.operationRole -ceq 'proof_publication_pending' -and
                $_.Operation.stateApplied
            } | Group-Object { [string]$_.Operation.operationKey })) {
            $publicationHistory = @($publicationGroup.Group | Sort-Object {
                    [long]$_.Boundary.afterStateGeneration
                })
            $origin = $publicationHistory[0]
            $retired = $publicationHistory[-1]
            if ($origin.Boundary.decisionKind -cne 'ObserveReceiveRecoveryProofStatePublicationRecheckEvidence' -or
                $origin.Operation.beforeState -cne 'None' -or $origin.Operation.afterState -cne 'Pending' -or
                $retired.Boundary.decisionKind -cne 'ObserveReceiveRecoveryProof' -or
                $retired.Operation.beforeState -cne 'Held' -or $retired.Operation.afterState -cne 'None') { continue }
            $held = @($publicationHistory | Where-Object {
                    $_.Operation.beforeState -ceq 'Pending' -and $_.Operation.afterState -ceq 'Held'
                })
            if ($held.Count -ne 1 -or $held[0].Operation.receiptStatus -cne 'Held' -or
                -not $held[0].Operation.receiptValidated) { continue }
            $valid = $true
            $state = 'None'
            foreach ($entry in $publicationHistory) {
                $operation = $entry.Operation
                if ($operation.beforeState -cne $state -or
                    ($state -ceq 'None' -and $entry -ne $origin)) { $valid = $false; break }
                foreach ($field in @('operationKey','authorityId','operationGeneration',
                        'commandIdentity','commandGeneration','commandFingerprint')) {
                    if ([string]$operation.$field -cne [string]$origin.Operation.$field) { $valid = $false }
                }
                foreach ($scope in @('sourceBoundary','effectiveBoundary')) {
                    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                            $operation.receiveRecovery.$scope $origin.Operation.receiveRecovery.$scope)) { $valid = $false }
                }
                if ($operation.beforeState -ceq 'Held') {
                    foreach ($field in @('receiptIdentity','receiptGeneration','receiptStatus','receiptFingerprint','receiptValidated')) {
                        if ([string]$operation.$field -cne [string]$held[0].Operation.$field) { $valid = $false }
                    }
                }
                $state = [string]$operation.afterState
            }
            if (-not $valid -or -not (Test-FileTransferCoordinatorReceiveRecoveryRecheckCancellation `
                    -Arm $held[0] -Closure $retired -ParentHistory $operationEntries -Effects $authorityEffects)) { continue }
            $parent = @($operationEntries | Where-Object {
                    $_.Boundary.boundaryId -ceq $retired.Boundary.boundaryId -and
                    $_.Operation.afterState -ceq 'Completed:Adopted'
                })
            if ($parent.Count -ne 1) { continue }
            $heldPublicationProofClosures.Add([pscustomobject]@{
                    Writer = [string]$retired.Boundary.writer
                    AuthorityId = $authorityId
                    OperationGeneration = [long]$parent[0].Operation.operationGeneration
                    ClosureGeneration = [long]$parent[0].Operation.receiveRecovery.closureGeneration
                    PublicationGeneration = [long]$retired.Operation.operationGeneration
                    ProofIdentity = [string]$parent[0].Operation.receiveRecovery.proofIdentity
                    ProofObservedUtcMs = [long]$parent[0].Operation.receiveRecovery.proofObservedUtcMs
                    SourceBoundary = $parent[0].Operation.receiveRecovery.sourceBoundary
                    Direction = [string]$streams[$instanceId].Direction
                    OriginSequence = [long]$origin.Boundary.writerSequence
                    HeldSequence = [long]$held[0].Boundary.writerSequence
                    ClosureSequence = [long]$retired.Boundary.writerSequence
                    TransitionId = [string]$retired.Boundary.boundaryId
                })
        }
        if ($null -ne $latestOperation) {
            $executionState = [string]$latestOperation.Operation.receiveRecovery.executionState
            $closureState = [string]$latestOperation.Operation.receiveRecovery.closureState
            $proofIdentity = [string]$latestOperation.Operation.receiveRecovery.proofIdentity
            if ($executionState -in @('Issued', 'Running')) {
                $candidate = $latestOperation
                $class = 'filetransfer_receive_recovery_operation_incomplete'
            }
            elseif ($executionState -ceq 'Completed' -and
                $proofIdentity -ceq '(none)' -and
                $closureState -in @('None', 'Retained')) {
                $candidate = $latestOperation
                $class = 'filetransfer_receive_recovery_completed_unproven_successor_missing'
            }
            elseif ($executionState -ceq 'Completed' -and
                $proofIdentity -notin @('', '(none)') -and
                $closureState -ceq 'Adopted') {
                $successors = @($receiveRecoveryEntries | Where-Object {
                        [string]$_.Boundary.coordinatorInstanceId -ceq $instanceId -and
                        [string]$_.Operation.predecessorAuthorityId -ceq
                            $authorityId -and
                        [string]$_.Operation.operationRole -ceq
                            'operation_current' -and
                        [string]$_.Operation.receiveRecovery.executionState -in @(
                            'Issued', 'Running', 'Completed') -and
                        [string]$_.Operation.afterState -cne 'None' -and
                        ($null -eq $rootLifecycle.Poisoned -or
                            [long]$_.Boundary.afterStateGeneration -lt [long]$rootLifecycle.PoisonBoundary.afterStateGeneration)
                    })
                $rejectedSuccessors = @($operationHistory | Where-Object {
                        [string]$_.Boundary.coordinatorInstanceId -ceq
                            $instanceId -and
                        [string]$_.Boundary.decisionKind -ceq
                            'IssueReceiveRecoveryOperation' -and
                        [string]$_.Boundary.decisionStatus -ceq 'Rejected' -and
                        [string]$_.Boundary.decisionReason -ceq
                            'filetransfer_receive_recovery_operation_conflict' -and
                        [long]$_.Boundary.decisionUtcMilliseconds -ge
                            [long]$latestOperation.Boundary.decisionUtcMilliseconds -and
                        ($null -eq $rootLifecycle.PoisonBoundary -or
                            [long]$_.Boundary.afterStateGeneration -lt [long]$rootLifecycle.PoisonBoundary.afterStateGeneration) -and
                        (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                            -Left $_.Operation.receiveRecovery.sourceBoundary `
                            -Right $latestOperation.Operation.receiveRecovery.sourceBoundary)
                    })
                if ($successors.Count -eq 0 -and
                    $rejectedSuccessors.Count -gt 0) {
                    $candidate = $latestOperation
                    $class =
                        'filetransfer_receive_recovery_completed_proven_successor_missing'
                }
            }
        }

        $invalidPayloadResume = @($authorityEffects | Where-Object {
                $_.EffectKind -ceq 'payload_resumed'
            } | ForEach-Object {
                $effect = $_
                @($history | Where-Object {
                        [string]$_.Boundary.boundaryId -ceq
                            [string]$effect.TransitionId -and
                        [string]$_.Operation.operationKey -ceq
                            [string]$effect.OperationKey -and
                        [string]$_.Operation.receiveRecovery.proofIdentity -in
                            @('', '(none)')
                    })
            } | Select-Object -First 1)
        if ($invalidPayloadResume.Count -eq 1) {
            $candidate = $invalidPayloadResume[0]
            $class =
                'filetransfer_receive_recovery_completed_unproven_payload_hold_not_preserved'
        }

        $pendingProof = @($history | Where-Object {
                [string]$_.Operation.operationRole -ceq
                    'proof_publication_pending'
            } | Group-Object {
                [string]$_.Operation.operationKey
            } | ForEach-Object {
                $publicationHistory = @($_.Group | Sort-Object {
                        [long]$_.Boundary.afterStateGeneration
                    }, {
                        [long]$_.Boundary.decisionUtcMilliseconds
                    }, {
                        [string]$_.Boundary.boundaryId
                    })
                $latestPublication = $publicationHistory | Select-Object -Last 1
                if ($null -eq $latestPublication -or
                    [string]$latestPublication.Operation.afterState -notin
                        @('Pending', 'Held')) {
                    return
                }

                $terminalProof = @($history | Where-Object {
                        [string]$_.Operation.operationRole -ceq
                            'proof_publication_terminal' -and
                        [string]$_.Operation.authorityId -ceq
                            [string]$latestPublication.Operation.authorityId -and
                        [long]$_.Operation.operationGeneration -eq
                            [long]$latestPublication.Operation.operationGeneration -and
                        [long]$_.Boundary.afterStateGeneration -ge
                            [long]$latestPublication.Boundary.afterStateGeneration -and
                        ((([string]$_.Operation.afterState -ceq 'Delivered') -and
                          [bool]$_.Operation.receiptValidated) -or
                         (([string]$_.Operation.afterState -ceq 'Rejected') -and
                          [string]$_.Operation.receiptStatus -ceq 'Rejected' -and
                          [string]$_.Operation.receiptIdentity -notin
                              @('', '(none)') -and
                          [string]$_.Operation.receiptFingerprint -notin
                              @('', '(none)'))) -and
                        (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                            -Left $_.Operation.receiveRecovery.sourceBoundary `
                            -Right $latestPublication.Operation.receiveRecovery.sourceBoundary) -and
                        (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                            -Left $_.Operation.receiveRecovery.effectiveBoundary `
                            -Right $latestPublication.Operation.receiveRecovery.effectiveBoundary)
                    })
                if ($terminalProof.Count -eq 0) {
                    $latestPublication
                }
            } | Sort-Object {
                [long]$_.Boundary.afterStateGeneration
            }, {
                [long]$_.Boundary.decisionUtcMilliseconds
            }, {
                [string]$_.Boundary.boundaryId
            } | Select-Object -Last 1) | Select-Object -First 1
        if ($null -ne $pendingProof) {
            $candidate = $pendingProof
            $class = 'filetransfer_receive_recovery_completed_retained_proof_state_publication_missing'
        }

        $pendingProofRecheck = @($history | Where-Object {
                [string]$_.Operation.operationRole -ceq
                    'proof_publication_recheck'
            } | Group-Object {
                [string]$_.Operation.operationKey
            } | ForEach-Object {
                $recheckHistory = @($_.Group | Sort-Object {
                        [long]$_.Boundary.afterStateGeneration
                    }, {
                        [long]$_.Boundary.decisionUtcMilliseconds
                    }, {
                        [string]$_.Boundary.boundaryId
                })
                $latestRecheck = $recheckHistory | Select-Object -Last 1
                if ($null -eq $latestRecheck -or
                    [bool]$latestRecheck.Operation.receiptValidated) {
                    return
                }

                $arms = @($recheckHistory | Where-Object {
                        [string]$_.Operation.beforeState -ceq 'None' -and
                        [string]$_.Operation.afterState -ceq 'Armed'
                    })
                if ($arms.Count -eq 0) {
                    $latestRecheck
                    return
                }
                if ($arms.Count -gt 1) {
                    $arms | Select-Object -Last 1
                    return
                }

                if ([string]$latestRecheck.Operation.afterState -ceq 'Armed') {
                    $latestRecheck
                    return
                }
                if ([string]$latestRecheck.Operation.afterState -cne 'None') {
                    return
                }

                $arm = $arms[0]
                if ($null -eq $arm) {
                    $latestRecheck
                    return
                }

                $closures = @($recheckHistory | Where-Object {
                        [long]$_.Boundary.afterStateGeneration -gt
                            [long]$arm.Boundary.afterStateGeneration -and
                        [string]$_.Operation.beforeState -ceq 'Armed' -and
                        [string]$_.Operation.afterState -ceq 'None' -and
                        [string]$_.Operation.commandIdentity -ceq
                            [string]$arm.Operation.commandIdentity -and
                        [long]$_.Operation.commandGeneration -eq
                            [long]$arm.Operation.commandGeneration -and
                        [string]$_.Operation.commandFingerprint -ceq
                            [string]$arm.Operation.commandFingerprint -and
                        (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                            -Left $_.Operation.receiveRecovery.sourceBoundary `
                            -Right $arm.Operation.receiveRecovery.sourceBoundary) -and
                        (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                            -Left $_.Operation.receiveRecovery.effectiveBoundary `
                            -Right $arm.Operation.receiveRecovery.effectiveBoundary)
                    })
                $closed = $false
                if ($closures.Count -eq 1) {
                    $closure = $closures[0]
                    if ([string]$closure.Boundary.decisionStatus -ceq 'Accepted' -and
                        [string]$closure.Boundary.decisionKind -ceq
                            'ObserveReceiveRecoveryProofStatePublicationRecheckEvidence') {
                        $successors = @($history | Where-Object {
                                [string]$_.Boundary.boundaryId -ceq
                                    [string]$closure.Boundary.boundaryId -and
                                [string]$_.Operation.operationRole -ceq
                                    'proof_publication_pending' -and
                                [string]$_.Operation.operationKind -ceq
                                    'ProofPublication' -and
                                [string]$_.Operation.beforeState -ceq 'None' -and
                                [string]$_.Operation.afterState -ceq 'Pending' -and
                                [string]$_.Operation.authorityId -ceq
                                    [string]$arm.Operation.authorityId -and
                                [long]$_.Operation.operationGeneration -eq
                                    ([long]$arm.Operation.operationGeneration + 1) -and
                                [long]$_.Operation.commandGeneration -eq
                                    ([long]$arm.Operation.commandGeneration + 1) -and
                                (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                                    -Left $_.Operation.receiveRecovery.sourceBoundary `
                                    -Right $arm.Operation.receiveRecovery.sourceBoundary) -and
                                (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                                    -Left $_.Operation.receiveRecovery.effectiveBoundary `
                                    -Right $arm.Operation.receiveRecovery.effectiveBoundary)
                            })
                        $closed = $successors.Count -eq 1
                    }
                    elseif ([string]$closure.Boundary.decisionStatus -ceq
                            'Accepted') {
                        $closed = Test-FileTransferCoordinatorReceiveRecoveryRecheckCancellation `
                            -Arm $arm -Closure $closure `
                            -ParentHistory $operationEntries -Effects $authorityEffects
                    }
                }

                if (-not $closed) {
                    $arm
                }
            } | Sort-Object {
                [long]$_.Boundary.afterStateGeneration
            }, {
                [long]$_.Boundary.decisionUtcMilliseconds
            }, {
                [string]$_.Boundary.boundaryId
            } | Select-Object -Last 1) | Select-Object -First 1
        if ($null -ne $pendingProofRecheck) {
            $candidate = $pendingProofRecheck
            $class =
                'filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed'
        }

        $pendingCheckpoint = @($history | Where-Object {
                [string]$_.Operation.operationRole -in @(
                    'checkpoint_binding_current',
                    'checkpoint_delivery_pending',
                    'checkpoint_frontier_hold',
                    'checkpoint_frontier_recheck') -and
                [string]$_.Operation.afterState -in @('Pending', 'Held')
            } | Select-Object -Last 1) | Select-Object -First 1
        $closedCheckpoint = @($history | Where-Object {
                [string]$_.Operation.operationRole -in @(
                    'checkpoint_binding_terminal',
                    'checkpoint_delivery_terminal',
                    'checkpoint_resume',
                    'checkpoint_frontier_recheck') -and
                [bool]$_.Operation.receiptValidated -and
                [string]$_.Operation.afterState -notin @('Pending', 'Held')
            })
        if ($null -ne $pendingCheckpoint -and $closedCheckpoint.Count -eq 0) {
            $candidate = $pendingCheckpoint
            $class = 'filetransfer_receive_recovery_fallback_checkpoint_scope_handoff_missing'
        }

        $pendingHold = @($history | Where-Object {
                [string]$_.Operation.operationRole -in @(
                    'initial_activation_hold_current',
                    'initial_activation_envelope_refresh') -and
                [string]$_.Operation.afterState -in @('Held', 'Pending')
            } | Select-Object -Last 1) | Select-Object -First 1
        $closedHold = @($history | Where-Object {
                [string]$_.Operation.operationRole -in @(
                    'initial_activation_hold_terminal',
                    'initial_activation_envelope_refresh') -and
                [bool]$_.Operation.receiptValidated -and
                [string]$_.Operation.afterState -notin @('Held', 'Pending')
            })
        if ($null -ne $pendingHold -and $closedHold.Count -eq 0) {
            $candidate = $pendingHold
            $class = 'runtime_unlock_initial_activation_receive_recovery_hold_release_delivery_missing'
            $phase = 'runtime_unlock_initial_activation'
        }

        $runtimeScopeInvalidation = @($history | Where-Object {
                [string]$_.Operation.operationRole -ceq 'operation_terminal' -and
                [string]$_.Operation.afterState -ceq
                    'Invalidated:runtime_scope_replaced' -and
                [string]$_.Operation.receiveRecovery.sourceBoundary.route -ceq
                    'post_tuna_fallback_v6'
            } | Select-Object -Last 1) | Select-Object -First 1
        if ($null -ne $runtimeScopeInvalidation) {
            $holdClassifications = @($history | Where-Object {
                    [string]$_.Boundary.decisionKind -ceq
                        'ClassifyPostTunaReceiveRecoveryEpochRestart' -and
                    [string]$_.Boundary.decisionStatus -in @(
                        'ActiveHold',
                        'CompletedAwaitingProofHold',
                        'CompletedProvenIgnore') -and
                    [long]$_.Boundary.decisionUtcMilliseconds -le
                        [long]$runtimeScopeInvalidation.Boundary.decisionUtcMilliseconds
                })
            if ($holdClassifications.Count -eq 0) {
                $candidate = $runtimeScopeInvalidation
                $class =
                    'filetransfer_receive_recovery_post_tuna_runtime_successor_liveness_handoff_missing'
                $phase = 'bridge_recovery_execution'
            }
        }

        if ($null -ne $candidate -and
            -not [string]::IsNullOrWhiteSpace($class)) {
            $candidate.Boundary.taxonomyClass = $class
            $candidate.Boundary.domain = 'route_runtime'
            $candidate.Boundary.phase = $phase
            $candidate.Boundary.rawCount = 1
            $candidate.Boundary.distinctCount = 1
            $candidate.Boundary.recoveredCount = 0
            $candidate.Boundary.unresolvedCount = 1
            $candidate.Boundary.verdict = 'fail'
        }
    }

    $receiveRecoveryRetryHolds = @($operationHistory | Where-Object {
            [string]$_.Operation.family -ceq 'receive_recovery' -and
            [string]$_.Boundary.decisionKind -ceq
                'ClassifyReceiveRecoverySubmissionFailure' -and
            [string]$_.Boundary.decisionStatus -in @(
                'ActiveHold', 'CompletedAwaitingProofHold')
        })
    foreach ($hold in $receiveRecoveryRetryHolds) {
        $sourceBoundary = $hold.Operation.receiveRecovery.sourceBoundary
        $retryEntries = @($operationHistory | Where-Object {
                [string]$_.Boundary.coordinatorInstanceId -ceq
                    [string]$hold.Boundary.coordinatorInstanceId -and
                [string]$_.Operation.family -ceq
                    'runtime_unlock_retry_lifecycle' -and
                [long]$_.Boundary.decisionUtcMilliseconds -gt
                    [long]$hold.Boundary.decisionUtcMilliseconds -and
                (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                    -Left $_.Operation.runtimeUnlockRetry.sourceBoundary `
                    -Right $sourceBoundary)
            })
        $candidate = @($retryEntries | Where-Object {
                [string]$_.Operation.afterState -in @(
                    'Authorized', 'Staged', 'ReceiptIssued', 'Installed')
            } | Select-Object -First 1) | Select-Object -First 1
        if ($null -eq $candidate) {
            continue
        }

        $proof = @($operationHistory | Where-Object {
                [string]$_.Boundary.coordinatorInstanceId -ceq
                    [string]$hold.Boundary.coordinatorInstanceId -and
                [string]$_.Operation.family -ceq 'receive_recovery' -and
                [string]$_.Operation.authorityId -ceq
                    [string]$hold.Operation.authorityId -and
                [string]$_.Operation.receiveRecovery.proofIdentity -notin
                    @('', '(none)') -and
                [long]$_.Boundary.decisionUtcMilliseconds -gt
                    [long]$hold.Boundary.decisionUtcMilliseconds
            } | Select-Object -First 1) | Select-Object -First 1
        $installedBeforeProof = @($retryEntries | Where-Object {
                [string]$_.Operation.authorityId -ceq
                    [string]$candidate.Operation.authorityId -and
                [string]$_.Operation.afterState -eq 'Installed' -and
                ($null -eq $proof -or
                 [long]$_.Boundary.decisionUtcMilliseconds -lt
                    [long]$proof.Boundary.decisionUtcMilliseconds)
            }).Count -gt 0
        $releasedBeforeProof = $null -ne $proof -and @($retryEntries |
            Where-Object {
                [string]$_.Operation.authorityId -ceq
                    [string]$candidate.Operation.authorityId -and
                [string]$_.Operation.afterState -in @('Settled', 'Closed') -and
                [long]$_.Boundary.decisionUtcMilliseconds -lt
                    [long]$proof.Boundary.decisionUtcMilliseconds
            }).Count -gt 0
        $postProofDispatch = $null -ne $proof -and @($retryEntries |
            Where-Object {
                [string]$_.Operation.authorityId -ceq
                    [string]$candidate.Operation.authorityId -and
                [bool]$_.Operation.runtimeUnlockRetry.dispatchObserved -and
                [long]$_.Boundary.decisionUtcMilliseconds -gt
                    [long]$proof.Boundary.decisionUtcMilliseconds
            }).Count -gt 0
        $recovered = -not $installedBeforeProof -and
            $releasedBeforeProof -and $postProofDispatch
        $candidate.Boundary.taxonomyClass =
            'runtime_unlock_retry_dispatch_post_fallback_receive_recovery_hold_missing'
        $candidate.Boundary.domain = 'route_runtime'
        $candidate.Boundary.phase = 'runtime_unlock_retry'
        $candidate.Boundary.rawCount = 1
        $candidate.Boundary.distinctCount = 1
        $candidate.Boundary.recoveredCount = if ($recovered) { 1 } else { 0 }
        $candidate.Boundary.unresolvedCount = if ($recovered) { 0 } else { 1 }
        $candidate.Boundary.verdict = if ($recovered) { 'pass' } else { 'fail' }
    }

    $committedRouteRuntimeOperations = @(
        $operationHistory | Where-Object {
            [string]$_.Operation.family -ceq 'route_runtime' -and
            [string]$_.Operation.operationRole -ceq 'route_commit' -and
            [string]$_.Operation.afterState -ceq 'Committed' -and
            [bool]$_.Operation.receiptValidated
        })
    foreach ($commitEntry in $committedRouteRuntimeOperations) {
        $commit = $commitEntry.Operation
        $commitBoundary = $commitEntry.Boundary
        $publicationMatches = @(
            $operationHistory | Where-Object {
                $_.Boundary.coordinatorInstanceId -ceq
                    $commitBoundary.coordinatorInstanceId -and
                [string]$_.Operation.family -ceq 'route_runtime' -and
                [string]$_.Operation.operationRole -ceq 'route_publication' -and
                [string]$_.Operation.predecessorAuthorityId -ceq
                    [string]$commit.authorityId -and
                [string]$_.Operation.afterState -ceq 'Published' -and
                [bool]$_.Operation.receiptValidated -and
                [long]$_.Operation.routeRuntime.boundary.routeCommitGeneration -eq
                    [long]$commit.operationGeneration -and
                [long]$_.Boundary.decisionUtcMilliseconds -ge
                    [long]$commitBoundary.decisionUtcMilliseconds
            })
        $commitEffects = @(
            $effects | Where-Object {
                $_.CoordinatorInstanceId -ceq
                    $commitBoundary.coordinatorInstanceId -and
                [string]$_.ReceiptIdentity -ceq
                    [string]$commit.receiptIdentity -and
                [long]$_.ReceiptGeneration -eq
                    [long]$commit.receiptGeneration -and
                [string]$_.ReceiptStatus -ceq
                    [string]$commit.receiptStatus -and
                [string]$_.ReceiptFingerprint -ceq
                    [string]$commit.receiptFingerprint
            })
        $hasCommitAdoption = @($commitEffects | Where-Object {
                [string]$_.EffectKind -ceq 'route_commit_adopted'
            }).Count -eq 1
        $hasRuntimeLegInstall = @($commitEffects | Where-Object {
                [string]$_.EffectKind -ceq 'target_runtime_leg_installed'
            }).Count -eq 1
        if ($publicationMatches.Count -ne 1 -or -not $hasCommitAdoption) {
            $commitBoundary.taxonomyClass = 'publication_authority_missing'
            $commitBoundary.domain = 'route_runtime'
            $commitBoundary.phase = 'route_runtime'
            $commitBoundary.rawCount = 1
            $commitBoundary.distinctCount = 1
            $commitBoundary.recoveredCount = 0
            $commitBoundary.unresolvedCount = 1
            $commitBoundary.verdict = 'fail'
        }
        elseif (-not $hasRuntimeLegInstall) {
            $commitBoundary.taxonomyClass =
                'filetransfer_live_route_epoch_projection_missing'
            $commitBoundary.domain = 'route_runtime'
            $commitBoundary.phase = 'route_runtime'
            $commitBoundary.rawCount = 1
            $commitBoundary.distinctCount = 1
            $commitBoundary.recoveredCount = 0
            $commitBoundary.unresolvedCount = 1
            $commitBoundary.verdict = 'fail'
        }
    }

    foreach ($binding in $pendingPeerProofBindings) {
        $fallbackTailPeerProofBindings.Add($binding) | Out-Null
    }

    if ($selectionModes.Count -ne 1) {
        $errors.Add('snapshot_selection_mode_conflict')
    }
    $mode = if ($selectionModes.Contains('SnapshotV1Authoritative')) {
        'SnapshotV1Authoritative'
    }
    else {
        'SnapshotV1Shadow'
    }
    foreach ($entry in $streams.GetEnumerator()) {
        if (-not $entry.Value.Completed) {
            $errors.Add("snapshot_stream_incomplete:$($entry.Key)")
        }
        if ($mode -eq 'SnapshotV1Authoritative') {
            $missingFamilies = @($script:FileTransferCoordinatorSnapshotRequiredFamilies | Where-Object { $entry.Value.AuthoritativeFamilies -notcontains $_ })
            if ($missingFamilies.Count -gt 0) {
                $errors.Add("snapshot_declared_coverage_incomplete:$($entry.Key):$($missingFamilies -join ',')")
            }
        }
    }

    $checkpointRepairContinuityEvidence = Get-FileTransferCheckpointRepairContinuityEvidence `
        -Boundaries @($aggregateEntries | ForEach-Object { $_.Boundary }) -Catalog $catalog
    foreach ($failure in @($checkpointRepairContinuityEvidence.Errors)) { $errors.Add([string]$failure) }
    $validationFailures = @(
        ConvertTo-FileTransferCoordinatorSnapshotValidationFailures `
            -Errors @($errors) `
            -Records @($records) `
            -Boundaries @($boundaries))
    $orderedErrorTexts = @($validationFailures | ForEach-Object {
            [string]$_.FailureText
        })

    $firstUnresolvedBoundary = $null
    if ($validationFailures.Count -gt 0) {
        $firstFailure = $validationFailures[0]
        $normalizedFailureReason = [string]$firstFailure.Reason
        $failureIdentity = "coordinator_snapshot_evidence|$mode|$($firstFailure.Identity)"
        $failureHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes (
            [System.Text.UTF8Encoding]::new($false).GetBytes($failureIdentity))
        $sourceBoundary = $firstFailure.Boundary
        $matchingFailures = @($validationFailures | Where-Object {
                [string]$_.Reason -ceq $normalizedFailureReason -and
                [string]$_.Variant -ceq [string]$firstFailure.Variant
            })
        $matchingFailureCount = $matchingFailures.Count
        $matchingDistinctCount = 1
        $matchingUnresolvedCount = 1
        if ($normalizedFailureReason -ceq
            'snapshot_authority_effect_identity_reused_across_transitions') {
            $matchingDistinctCount = @($matchingFailures | Group-Object {
                    "$($_.CoordinatorInstanceId)|$($_.AuthorityId)|$($_.OperationGeneration)|$($_.ReceiptIdentity)"
                }).Count
            $matchingUnresolvedCount = $matchingDistinctCount
        }
        $family5RepairReplayOperations = if ($null -ne $sourceBoundary -and
            [string]$sourceBoundary.decisionKind -ceq 'RepairReplay') {
            @($sourceBoundary.operations | Where-Object {
                    [string]$_.family -ceq
                        'regular_v4_frontier_repair_pressure' -and
                    [string]$_.operationRole -ceq
                        'repair_authority_current' -and
                    [bool]$_.stateApplied
                })
        }
        else {
            @()
        }
        $hasUnlinkedRepairReplaySuccessor = @(
            $family5RepairReplayOperations | Where-Object {
                [string]$_.beforeState -ceq 'None' -and
                [string]$_.afterState -ceq 'Authorized' -and
                [string]$_.predecessorAuthorityId -ceq '(none)'
            }).Count -eq 1
        $hasRepeatedRepairReplayRetirement = @(
            $family5RepairReplayOperations | Where-Object {
                [string]$_.beforeState -ceq 'Authorized' -and
                [string]$_.afterState -ceq 'None'
            }).Count -eq 1
        $failureVariant = if ([string]$firstFailure.Variant -cne '(none)') {
            [string]$firstFailure.Variant
        }
        elseif (
            $normalizedFailureReason -ceq 'snapshot_authority_effect_unbound' -and
            [string]$firstFailure.OperationKey -cmatch '\|\[redacted\]$') {
            'structural_operation_key_tail_redacted_by_generic_long_token_filter'
        }
        elseif ($normalizedFailureReason -ceq
            'snapshot_regular_v4_repair_replay_authority_transition_missing') {
            'accepted_v4_repair_replay_changed_state_without_atomic_successor_operation'
        }
        elseif ($normalizedFailureReason -ceq
                'snapshot_regular_v4_repair_replay_authority_transition_invalid' -and
            $hasUnlinkedRepairReplaySuccessor -and
            $hasRepeatedRepairReplayRetirement) {
            'specialized_family5_adoption_resurrected_settled_repair_authority'
        }
        else {
            '(none)'
        }
        $firstUnresolvedBoundary = [pscustomobject][ordered]@{
            boundaryId = $failureHash
            boundaryHash = $failureHash
            coordinatorInstanceId = [string]$firstFailure.CoordinatorInstanceId
            evidenceLineIndex = [long]$firstFailure.LineIndex
            writer = [string]$firstFailure.Writer
            writerSequence = [long]$firstFailure.WriterSequence
            decisionUtcMilliseconds = [long]$firstFailure.DecisionUtcMilliseconds
            beforeStateGeneration = if ($null -ne $sourceBoundary) {
                [long]$sourceBoundary.beforeStateGeneration
            }
            else {
                0L
            }
            afterStateGeneration = if ($null -ne $sourceBoundary) {
                [long]$sourceBoundary.afterStateGeneration
            }
            else {
                [long]$firstFailure.StateGeneration
            }
            beforeStateHash = if ($null -ne $sourceBoundary) {
                [string]$sourceBoundary.beforeStateHash
            }
            else {
                '(none)'
            }
            afterStateHash = if ($null -ne $sourceBoundary) {
                [string]$sourceBoundary.afterStateHash
            }
            else {
                '(none)'
            }
            decisionKind = 'ArtifactEvidenceValidation'
            decisionStatus = 'Rejected'
            decisionReason = $normalizedFailureReason
            eventName = [string]$firstFailure.EventName
            variant = $failureVariant
            referencedTransitionId = [string]$firstFailure.TransitionId
            effectIdentity = [string]$firstFailure.EffectIdentity
            families = @(if ([string]$firstFailure.Family -notin @('', '(none)')) {
                [string]$firstFailure.Family
            })
            operations = @()
            verdict = 'fail'
            taxonomyClass = 'filetransfer_coordinator_snapshot_evidence_invalid'
            domain = 'evidence_harness'
            phase = 'evidence_finalization'
            rawCount = $matchingFailureCount
            distinctCount = $matchingDistinctCount
            recoveredCount = 0
            unresolvedCount = $matchingUnresolvedCount
        }
        $boundaries.Add($firstUnresolvedBoundary) | Out-Null
    }
    if ($null -eq $firstUnresolvedBoundary) {
        $firstUnresolvedBoundary = @(
            $boundaries | Where-Object { $_.verdict -ceq 'fail' } |
                Sort-Object decisionUtcMilliseconds, writer, writerSequence, boundaryId |
                Select-Object -First 1
        ) | Select-Object -First 1
    }

    $normalizedBoundaries = @(
        $boundaries |
            Sort-Object coordinatorInstanceId, beforeStateGeneration, afterStateGeneration, boundaryId |
            ForEach-Object {
                [ordered]@{
                    boundaryId = $_.boundaryId
                    boundaryHash = $_.boundaryHash
                    coordinatorInstanceId = $_.coordinatorInstanceId
                    writer = $_.writer
                    writerSequence = $_.writerSequence
                    decisionUtcMilliseconds = $_.decisionUtcMilliseconds
                    beforeStateGeneration = $_.beforeStateGeneration
                    afterStateGeneration = $_.afterStateGeneration
                    beforeStateHash = $_.beforeStateHash
                    afterStateHash = $_.afterStateHash
                    decisionKind = $_.decisionKind
                    decisionStatus = $_.decisionStatus
                    decisionReason = $_.decisionReason
                    families = @($_.families)
                    operations = @($_.operations)
                    verdict = $_.verdict
                    taxonomyClass = $_.taxonomyClass
                    domain = $_.domain
                    phase = $_.phase
                    rawCount = $_.rawCount
                    distinctCount = $_.distinctCount
                    recoveredCount = $_.recoveredCount
                    unresolvedCount = $_.unresolvedCount
                }
            })
    $normalizedBoundaryJson = ConvertTo-Json -InputObject $normalizedBoundaries -Depth 64 -Compress
    $boundaryHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([System.Text.UTF8Encoding]::new($false).GetBytes($normalizedBoundaryJson))
    $artifactVerdict = if ($errors.Count -eq 0) { 'pass' } else { 'fail' }
    $orderedFallbackTailPeerProofBindings = @(
        $fallbackTailPeerProofBindings.ToArray() |
            Where-Object { $artifactVerdict -ceq 'pass' } |
            Sort-Object {
                [string]$_.BindingKind
            }, {
                [string]$_.CoordinatorInstanceId
            }, {
                [string]$_.Writer
            }, {
                [string]$_.SessionId
            }, {
                [string]$_.TransferId
            }, {
                [string]$_.Direction
            }, {
                [long]$_.StateGeneration
            }, {
                [string]$_.TransitionId
            }, {
                [string]$_.OperationKey
            }, {
                [string]$_.PeerProofAuthorityId
            }, {
                [long]$_.PeerProofGeneration
            })
    [pscustomobject]@{
        Mode = $mode
        ContractVersion = $script:FileTransferCoordinatorSnapshotContractVersion
        CatalogVersion = $catalog.Version
        CatalogHash = $catalog.Hash
        StreamComplete = @($streams.Values | Where-Object { -not $_.Completed }).Count -eq 0
        ArtifactEvidenceVerdict = $artifactVerdict
        ArtifactEvidenceFailureReason = ($orderedErrorTexts -join ';')
        TransitionCount = $seenTransitions.Count
        EffectCount = $effects.Count
        BoundaryCount = $boundaries.Count
        BoundaryHash = $boundaryHash
        Boundaries = @($boundaries)
        # Internal correlation evidence; excluded from normalized boundary JSON/hash.
        __CheckpointRepairContinuityEvidence = if ($artifactVerdict -ceq 'pass') { $checkpointRepairContinuityEvidence } else { $null }
        __HeldPublicationProofClosures = @($heldPublicationProofClosures.ToArray() | Where-Object { $artifactVerdict -ceq 'pass' })
        __FallbackTailPeerProofBindings =
            @($orderedFallbackTailPeerProofBindings)
        __RoutePublicationEffects = @($effects | Where-Object {
            $artifactVerdict -ceq 'pass' -and $_.EffectKind -ceq 'route_publication_forwarded'
        })
        FirstUnresolvedBoundary = $firstUnresolvedBoundary
        AuthoritativeFamilies = @($streams.Values | ForEach-Object { $_.AuthoritativeFamilies } | Sort-Object -Unique)
        ShadowFamilies = @($streams.Values | ForEach-Object { $_.ShadowFamilies } | Sort-Object -Unique)
        RouteFamilyLegacySelectionCount = @(
            $script:FileTransferCoordinatorSnapshotFamily1 | Where-Object {
                $authoritativeFamilies = @($streams.Values |
                    ForEach-Object { $_.AuthoritativeFamilies } |
                    Sort-Object -Unique)
                $authoritativeFamilies -notcontains $_
            }).Count
        RuntimeUnlockRetryFamilyLegacySelectionCount = if (
            @($streams.Values | ForEach-Object {
                    $_.AuthoritativeFamilies
                } | Sort-Object -Unique) -contains
                'runtime_unlock_retry_lifecycle') {
            0
        }
        else {
            1
        }
        ReceiveRecoveryFamilyLegacySelectionCount = if (
            @($streams.Values | ForEach-Object {
                    $_.AuthoritativeFamilies
                } | Sort-Object -Unique) -contains 'receive_recovery') {
            0
        }
        else {
            1
        }
        FallbackTailYieldFamilyLegacySelectionCount = if (
            @($streams.Values | ForEach-Object {
                    $_.AuthoritativeFamilies
                } | Sort-Object -Unique) -contains 'fallback_tail_yield') {
            0
        }
        else {
            1
        }
        RegularV4FrontierRepairPressureFamilyLegacySelectionCount = if (
            @($streams.Values | ForEach-Object {
                    $_.AuthoritativeFamilies
                } | Sort-Object -Unique) -contains
                'regular_v4_frontier_repair_pressure') {
            0
        }
        else {
            1
        }
        ResponderProbeHandoffFamilyLegacySelectionCount = if (
            @($streams.Values | ForEach-Object {
                    $_.AuthoritativeFamilies
                } | Sort-Object -Unique) -contains
                'responder_probe_handoff') {
            0
        }
        else {
            1
        }
        LegacySelectionCount = if ($mode -eq 'SnapshotV1Authoritative') {
            0
        }
        else {
            @($script:FileTransferCoordinatorSnapshotRequiredFamilies |
                Where-Object {
                    $authoritativeFamilies = @($streams.Values |
                        ForEach-Object { $_.AuthoritativeFamilies } |
                        Sort-Object -Unique)
                    $authoritativeFamilies -notcontains $_
                }).Count
        }
    }
}

function Get-FileTransferCoordinatorSnapshotRouteExpectation {
    param([Parameter(Mandatory = $true)][string]$Route)

    switch ($Route) {
        'regular_nkn_v4_fast' {
            return [pscustomobject]@{
                ProtocolVersion = 4L
                RuntimeProfile = 'regular_nkn_v4_fast'
                BridgeRecoveryPolicy = 'regular_nkn_v4_fast'
                TargetTransport = 'RegularNkn'
                ProjectedTargetTransport = 'regular_nkn'
            }
        }
        'file_tuna_v4' {
            return [pscustomobject]@{
                ProtocolVersion = 4L
                RuntimeProfile = 'file_tuna_v4'
                BridgeRecoveryPolicy = 'tuna_strict'
                TargetTransport = 'Tuna'
                ProjectedTargetTransport = 'tuna'
            }
        }
        'post_tuna_fallback_v6' {
            return [pscustomobject]@{
                ProtocolVersion = 6L
                RuntimeProfile = 'default_v6'
                BridgeRecoveryPolicy = 'post_tuna_fallback_strict'
                TargetTransport = 'RegularNkn'
                ProjectedTargetTransport = 'regular_nkn'
            }
        }
        default { return $null }
    }
}

function New-FileTransferCoordinatorSnapshotRouteProjectionEvent {
    param(
        [Parameter(Mandatory = $true)]$Boundary,
        [Parameter(Mandatory = $true)]$Operation,
        [Parameter(Mandatory = $true)]$Expectation,
        [Parameter(Mandatory = $true)][long]$Sequence
    )

    $routeBoundary = $Operation.routeRuntime.boundary
    $fields = [ordered]@{
        session_id = [string]$Boundary.sessionId
        transfer_id = [string]$Boundary.transferId
        direction = [string]$Boundary.direction
        role = if ([string]$Boundary.direction -ceq 'outbound') {
            'sender'
        }
        else {
            'receiver'
        }
        route = [string]$routeBoundary.route
        protocol_version = [string]$routeBoundary.protocolVersion
        runtime_profile = [string]$Expectation.RuntimeProfile
        bridge_recovery_policy = [string]$Expectation.BridgeRecoveryPolicy
        target_transport = [string]$Expectation.ProjectedTargetTransport
        selection_reason = 'coordinator_snapshot_route_publication'
        live_route_epoch = [string]$routeBoundary.liveRouteEpoch
        route_publication_generation =
            [string]$routeBoundary.routePublicationGeneration
        route_commit_generation = [string]$routeBoundary.routeCommitGeneration
        runtime_generation = [string]$routeBoundary.runtimeGeneration
        terminal_generation = [string]$routeBoundary.terminalGeneration
        leg_generation = [string]$routeBoundary.legGeneration
        transport_epoch = [string]$routeBoundary.transportEpoch
        bridge_recovery_generation =
            [string]$routeBoundary.bridgeRecoveryGeneration
        checkpoint_generation = [string]$routeBoundary.checkpointGeneration
        checkpoint_request_id = [string]$routeBoundary.checkpointRequestId
        data_session_identity = [string]$routeBoundary.dataSessionIdentity
        data_session_generation = [string]$routeBoundary.dataSessionGeneration
        coordinator_instance_id = [string]$Boundary.coordinatorInstanceId
        coordinator_state_generation = [string]$Boundary.afterStateGeneration
        transition_id = [string]$Boundary.boundaryId
        operation_key = [string]$Operation.operationKey
        operation_authority_id = [string]$Operation.authorityId
        operation_generation = [string]$Operation.operationGeneration
        log_writer_id = [string]$Boundary.writer
        log_writer_sequence = [string]$Boundary.writerSequence
    }
    $message = @(
        'event=filetransfer_route_selected'
        "session_id=$($fields.session_id)"
        "transfer_id=$($fields.transfer_id)"
        "direction=$($fields.direction)"
        "route=$($fields.route)"
        "protocol_version=$($fields.protocol_version)"
        "runtime_profile=$($fields.runtime_profile)"
        "bridge_recovery_policy=$($fields.bridge_recovery_policy)"
        "selection_reason=$($fields.selection_reason)"
        "route_publication_generation=$($fields.route_publication_generation)"
        "transition_id=$($fields.transition_id)"
        "log_writer_id=$($fields.log_writer_id)"
        "log_writer_sequence=$($fields.log_writer_sequence)"
    ) -join '; '
    [pscustomobject]@{
        EventName = 'filetransfer_route_selected'
        Sequence = $Sequence
        LineNumber = [long]$Boundary.evidenceLineIndex + 1L
        FileName = '(coordinator_snapshot)'
        FilePath = '(coordinator_snapshot)'
        SourcePath = '(coordinator_snapshot)'
        Message = $message
        RawLine = $message
        Fields = $fields
        SessionId = [string]$Boundary.sessionId
        TransferId = [string]$Boundary.transferId
        Direction = [string]$Boundary.direction
        Writer = [string]$Boundary.writer
        WriterSequence = [long]$Boundary.writerSequence
        BoundaryId = [string]$Boundary.boundaryId
        BoundaryHash = [string]$Boundary.boundaryHash
    }
}

function Test-FileTransferCoordinatorSnapshotRouteLineageEqual {
    param(
        [Parameter(Mandatory = $true)]$Left,
        [Parameter(Mandatory = $true)]$Right
    )

    if ($null -eq $Left -or $null -eq $Right) { return $false }
    foreach ($field in @(
            'route', 'protocolVersion', 'liveRouteEpoch',
            'routePublicationGeneration', 'routeCommitGeneration',
            'runtimeGeneration', 'terminalGeneration', 'legGeneration')) {
        if ([string]$Left.$field -cne [string]$Right.$field) {
            return $false
        }
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotRouteOwnerLineageEqual {
    param(
        [Parameter(Mandatory = $true)]$Left,
        [Parameter(Mandatory = $true)]$Right
    )

    if ($null -eq $Left -or $null -eq $Right) { return $false }
    # Route publication is a later mutation than the initial runtime-owner
    # installation.  Every other scope component must remain exact.
    foreach ($field in @(
            'route', 'protocolVersion', 'liveRouteEpoch',
            'runtimeGeneration', 'terminalGeneration', 'legGeneration',
            'transportEpoch', 'bridgeRecoveryGeneration',
            'checkpointGeneration', 'checkpointRequestId',
            'dataSessionIdentity', 'dataSessionGeneration')) {
        if ([string]$Left.$field -cne [string]$Right.$field) {
            return $false
        }
    }
    return $true
}

function Get-FileTransferCoordinatorSnapshotRouteTransitionExpectation {
    param(
        [Parameter(Mandatory = $true)][string]$SourceRoute,
        [Parameter(Mandatory = $true)][string]$TargetRoute
    )

    switch ("$SourceRoute|$TargetRoute") {
        'regular_nkn_v4_fast|file_tuna_v4' {
            return [pscustomobject]@{
                TransitionKind = 'RegularV4ToTunaV4'
                SourceTransport = 'RegularNkn'
                TargetTransport = 'Tuna'
            }
        }
        'file_tuna_v4|post_tuna_fallback_v6' {
            return [pscustomobject]@{
                TransitionKind = 'TunaV4ToPostTunaFallbackV6'
                SourceTransport = 'Tuna'
                TargetTransport = 'RegularNkn'
            }
        }
        'post_tuna_fallback_v6|file_tuna_v4' {
            return [pscustomobject]@{
                TransitionKind = 'PostTunaFallbackV6ToTunaV4'
                SourceTransport = 'RegularNkn'
                TargetTransport = 'Tuna'
            }
        }
        default { return $null }
    }
}

function Test-FileTransferCoordinatorSnapshotRouteTransitionSourceCurrent {
    param(
        [Parameter(Mandatory = $true)]$Source,
        [Parameter(Mandatory = $true)]$Current
    )

    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryComplete -Boundary $Source) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete -Boundary $Current)) {
        return $false
    }
    # Pause/drain may consume the aggregate transport, bridge, and checkpoint
    # projections before commit.  Those fields remain exact across the typed
    # current/terminal/attempted transition operations; the aggregate join uses
    # the stable route and data-session lineage.
    foreach ($field in @(
            'route', 'protocolVersion', 'liveRouteEpoch',
            'routePublicationGeneration', 'routeCommitGeneration',
            'runtimeGeneration', 'terminalGeneration', 'legGeneration',
            'dataSessionIdentity', 'dataSessionGeneration')) {
        if ([string]$Source.$field -cne [string]$Current.$field) {
            return $false
        }
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotRouteTransitionTargetCurrent {
    param(
        [Parameter(Mandatory = $true)]$Target,
        [Parameter(Mandatory = $true)]$Current
    )

    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryComplete -Boundary $Target) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete -Boundary $Current)) {
        return $false
    }
    # The transition's target data-session identity is transport-authored and
    # is deliberately distinct from the aggregate projection.  Its generation
    # and every other target field are exact.
    foreach ($field in @(
            'route', 'protocolVersion', 'liveRouteEpoch',
            'routePublicationGeneration', 'routeCommitGeneration',
            'runtimeGeneration', 'terminalGeneration', 'legGeneration',
            'transportEpoch', 'bridgeRecoveryGeneration',
            'checkpointGeneration',
            'dataSessionGeneration')) {
        if ([string]$Target.$field -cne [string]$Current.$field) {
            return $false
        }
    }
    $targetCheckpointRequest = [string]$Target.checkpointRequestId
    $currentCheckpointRequest = [string]$Current.checkpointRequestId
    $checkpointIdentityValid = if ([long]$Target.checkpointGeneration -eq 0L) {
        $targetCheckpointRequest -ceq '(none)' -and
            $currentCheckpointRequest -ceq '(none)'
    }
    else {
        -not [string]::IsNullOrWhiteSpace($targetCheckpointRequest) -and
            $targetCheckpointRequest -cne '(none)' -and
            -not [string]::IsNullOrWhiteSpace($currentCheckpointRequest) -and
            $currentCheckpointRequest -cne '(none)'
    }
    return $checkpointIdentityValid -and
        -not [string]::IsNullOrWhiteSpace([string]$Target.dataSessionIdentity) -and
        [string]$Target.dataSessionIdentity -cne '(none)'
}

function ConvertTo-FileTransferCoordinatorSnapshotRouteFailureSemanticBoundary {
    param($Boundary)

    if ($null -eq $Boundary) { return $null }
    $semantic = [ordered]@{}
    foreach ($property in $Boundary.PSObject.Properties) {
        if ($property.Name -cne 'evidenceLineIndex') {
            $semantic[$property.Name] = $property.Value
        }
    }
    return [pscustomobject]$semantic
}

function Test-FileTransferCoordinatorSnapshotFallbackOwnerPublication {
    param($Entry, $Previous, [object[]]$RetiredPublications, [object[]]$RetiredOwners,
        [object[]]$RuntimeOperations, [object[]]$PublicationOperations, [object[]]$Effects, $AggregateIndex)

    $boundary = $Entry.Boundary
    $before = $boundary.beforeBoundary
    $after = $boundary.afterBoundary
    if (-not $Previous.Valid -or $null -eq $Previous.LastTransition -or $null -eq $Entry.RuntimeOwner -or
        $RetiredPublications.Count -ne 1 -or $RetiredOwners.Count -ne 1 -or
        $RuntimeOperations.Count -ne 3 -or $PublicationOperations.Count -ne 2 -or
        [string]$boundary.decisionKind -cne 'StartTransfer' -or
        [string]$boundary.decisionStatus -cne 'Accepted' -or
        [string]$boundary.decisionReason -cne 'state_refresh_received' -or
        [long]$boundary.afterStateGeneration -ne ([long]$boundary.beforeStateGeneration + 1L) -or
        [string]$boundary.beforeStateHash -ceq [string]$boundary.afterStateHash -or
        [string]$before.route -cne 'post_tuna_fallback_v6' -or [long]$before.protocolVersion -ne 6L -or
        [long]$before.terminalGeneration -ne 0L -or [long]$before.routeCommitGeneration -ne 0L) {
        return $false
    }
    # The publication token stays immutable while an exact checkpoint lease may
    # be requested/accepted. Reduce that interval, rather than equating its birth
    # scope with the current aggregate or treating missing history as a wildcard.
    $streamKey = "$($boundary.coordinatorInstanceId)|$($boundary.writer)"
    if (-not $AggregateIndex.ContainsKey($streamKey)) { return $false }
    $stream = $AggregateIndex[$streamKey]
    $generation = [long]$Previous.Boundary.afterStateGeneration
    $hash = [string]$Previous.Boundary.afterStateHash
    $scope = $Previous.RouteBoundary
    while ($generation -lt [long]$boundary.beforeStateGeneration) {
        if (-not $stream.Mutations.ContainsKey($generation) -or $stream.Mutations[$generation].Count -ne 1) { return $false }
        $step = $stream.Mutations[$generation][0].Boundary
        if ([long]$step.afterStateGeneration -ne ($generation + 1L) -or $step.beforeStateHash -cne $hash -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $step.beforeBoundary $scope)) { return $false }
        if (-not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $step.beforeBoundary $step.afterBoundary)) {
            $checkpoint = [pscustomobject]@{
                decisionKind=$step.decisionKind; decisionStatus=$step.decisionStatus; decisionReason=$step.decisionReason
                aggregate=[pscustomobject]@{
                    beforeBoundary=$step.beforeBoundary; afterBoundary=$step.afterBoundary
                    beforeStateGeneration=$step.beforeStateGeneration; afterStateGeneration=$step.afterStateGeneration
                    beforeStateFingerprint=$step.beforeStateHash; afterStateFingerprint=$step.afterStateHash
                }
            }
            if (-not (Test-FileTransferCoordinatorSnapshotFallbackCheckpointScopeMutation $checkpoint)) { return $false }
        }
        foreach ($operation in @($step.operations | Where-Object {
                $_.stateApplied -and $_.family -ceq 'route_runtime' -and
                -not (Test-FileTransferCoordinatorCheckpointRepairContinuityMarker $_) })) {
            $prior = if ($operation.operationKey -ceq $Previous.Operation.operationKey) { $Previous.Operation }
                elseif ($operation.operationKey -ceq $Previous.RuntimeOwner.operationKey) { $Previous.RuntimeOwner } else { $null }
            if ($null -eq $prior -or $operation.beforeState -cne $prior.afterState -or $operation.afterState -cne $prior.afterState) { return $false }
            foreach ($field in @('authorityId','operationGeneration','commandIdentity','commandGeneration','commandFingerprint',
                    'receiptIdentity','receiptGeneration','receiptStatus','receiptFingerprint','receiptValidated')) {
                if ([string]$operation.$field -cne [string]$prior.$field) { return $false }
            }
        }
        $scope=$step.afterBoundary; $hash=[string]$step.afterStateHash; $generation=[long]$step.afterStateGeneration
    }
    if ($generation -ne [long]$boundary.beforeStateGeneration -or $hash -cne $boundary.beforeStateHash -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $scope $before)) { return $false }
    foreach ($field in @('route', 'protocolVersion', 'liveRouteEpoch', 'routeCommitGeneration',
            'terminalGeneration', 'transportEpoch', 'bridgeRecoveryGeneration', 'checkpointGeneration',
            'checkpointRequestId', 'dataSessionIdentity', 'dataSessionGeneration')) {
        if ([string]$before.$field -cne [string]$after.$field) { return $false }
    }
    foreach ($field in @('routePublicationGeneration', 'runtimeGeneration', 'legGeneration')) {
        if ([long]$after.$field -ne ([long]$before.$field + 1L)) { return $false }
    }
    $attempts = @($RuntimeOperations | Where-Object {
        $_.operationKind -ceq 'StartTransfer' -and $_.beforeState -ceq 'Attempted' -and
        $_.afterState -ceq 'Attempted' -and -not $_.stateApplied -and
        $_.authorityId -ceq $Entry.RuntimeOwner.authorityId -and
        [long]$_.operationGeneration -eq [long]$Entry.RuntimeOwner.operationGeneration -and
        (Test-FileTransferCoordinatorSnapshotBoundaryEqual -Left $_.routeRuntime.boundary -Right $before)
    })
    if ($attempts.Count -ne 1) { return $false }
    foreach ($operation in $RuntimeOperations) {
        if ([string]$operation.operationKey -cne
                "route_runtime|runtime_owner|$($operation.authorityId)|$($operation.operationGeneration)|$($operation.operationKind)" -or
            $operation.routeRuntime.role -cne 'runtime_owner' -or
            $operation.receiptValidated -or [long]$operation.receiptGeneration -ne 0L -or
            $operation.receiptIdentity -cne '(none)' -or $operation.receiptStatus -cne '(none)' -or
            $operation.receiptFingerprint -cne '(none)' -or
            $operation.commandIdentity -cne $operation.authorityId -or
            [long]$operation.commandGeneration -ne [long]$operation.operationGeneration -or
            [string]::IsNullOrWhiteSpace([string]$operation.commandFingerprint) -or
            $operation.commandFingerprint -ceq '(none)' -or
            $operation.routeRuntime.tokenFingerprint -cne $operation.commandFingerprint) { return $false }
    }
    # Retirement preserves the exact old tokens; the successor is not a repair
    # predecessor relationship and deliberately has no predecessor authority.
    foreach ($pair in @(@($RetiredPublications[0], $Previous.Operation), @($RetiredOwners[0], $Previous.RuntimeOwner))) {
        foreach ($field in @('authorityId', 'operationKey', 'operationGeneration', 'commandIdentity',
                'commandGeneration', 'commandFingerprint', 'receiptIdentity', 'receiptGeneration',
                'receiptStatus', 'receiptFingerprint', 'receiptValidated')) {
            if ([string]$pair[0].$field -cne [string]$pair[1].$field) { return $false }
        }
    }
    foreach ($publication in $PublicationOperations) {
        if ($publication.operationKind -cne 'RoutePublication' -or
            $publication.predecessorAuthorityId -cne '(none)' -or -not $publication.receiptValidated -or
            $publication.receiptStatus -cne 'Accepted' -or
            [long]$publication.commandGeneration -ne [long]$publication.operationGeneration -or
            [long]$publication.receiptGeneration -ne [long]$publication.operationGeneration -or
            $publication.commandIdentity -cne $publication.authorityId -or
            $publication.receiptIdentity -cne $publication.authorityId -or
            $publication.commandFingerprint -cne $publication.receiptFingerprint -or
            $publication.routeRuntime.tokenFingerprint -cne $publication.receiptFingerprint) { return $false }
    }
    $carries = @($boundary.operations | Where-Object { $_.family -ceq 'route_transition' })
    $terminal = @($carries | Where-Object {
        $_.operationRole -ceq 'terminal' -and $_.operationKind -ceq 'TunaV4ToPostTunaFallbackV6' -and
        $_.beforeState -ceq 'Committed' -and $_.afterState -ceq 'Committed' -and $_.receiptValidated -and
        $_.authorityId -ceq $Previous.LastTransition.authorityId -and
        [long]$_.operationGeneration -eq [long]$Previous.LastTransition.operationGeneration
    })
    $bootstrap = @($carries | Where-Object {
        $_.operationRole -ceq 'fallback_v6_bootstrap' -and $_.operationKind -ceq 'FallbackV6ProofBootstrap' -and
        $_.beforeState -ceq 'Delivered' -and $_.afterState -ceq 'Delivered' -and $_.receiptValidated
    })
    if ($null -eq $Previous.LastTransition -or $carries.Count -ne 2 -or
        $terminal.Count -ne 1 -or $bootstrap.Count -ne 1 -or
        @($boundary.operations | Where-Object {
            $_.family -ceq 'route_runtime' -and $_.operationRole -ceq 'route_commit'
        }).Count -ne 0) { return $false }

    $operation = $Entry.Operation
    $boundEffects = @($Effects | Where-Object {
        $_.CoordinatorInstanceId -ceq $boundary.coordinatorInstanceId -and $_.Writer -ceq $boundary.writer -and
        $_.TransitionId -ceq $boundary.boundaryId
    })
    if ($boundEffects.Count -ne 1) { return $false }
    $effect = $boundEffects[0]
    if ($effect.EffectKind -cne 'route_publication_forwarded' -or $effect.Family -cne 'route_runtime' -or
        $effect.OperationKey -cne $operation.operationKey -or $effect.AuthorityId -cne $operation.authorityId -or
        [long]$effect.OperationGeneration -ne [long]$operation.operationGeneration -or
        [long]$effect.StateGeneration -ne [long]$boundary.afterStateGeneration -or
        $effect.ReceiptIdentity -cne $operation.receiptIdentity -or
        [long]$effect.ReceiptGeneration -ne [long]$operation.receiptGeneration -or
        $effect.ReceiptStatus -cne $operation.receiptStatus -or
        $effect.ReceiptFingerprint -cne $operation.receiptFingerprint) { return $false }
    return Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual -Left $effect -Right $after
}

function Get-FileTransferCoordinatorSnapshotRouteConsistency {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Evaluation)

    if ($null -eq $Evaluation -or
        [string]$Evaluation.Mode -ceq 'LegacyV0') {
        return $null
    }
    $authoritativeFamilies = @(
        Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $Evaluation -Name 'AuthoritativeFamilies' -Default @())
    $routeFamiliesAuthoritative =
        $authoritativeFamilies -contains 'route_runtime' -and
        $authoritativeFamilies -contains 'route_transition'
    if (-not $routeFamiliesAuthoritative -and
        [string]$Evaluation.Mode -cne 'SnapshotV1Authoritative') {
        return $null
    }

    $findings = [System.Collections.Generic.List[string]]::new()
    $evidenceEvents = [System.Collections.Generic.List[object]]::new()
    $findingIdentities = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)
    $addFinding = {
        param([string]$Identity, [string]$Finding, $Event)
        if ($findingIdentities.Add($Identity)) {
            $findings.Add($Finding)
            if ($null -ne $Event) {
                $evidenceEvents.Add($Event)
            }
        }
    }
    $newBoundaryEvidence = {
        param($Boundary)
        if ($null -eq $Boundary) { return $null }
        $lineNumber = [long](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $Boundary -Name 'evidenceLineIndex' -Default -1L) + 1L
        $writer = [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $Boundary -Name 'writer' -Default '(unknown)')
        $writerSequence = [long](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $Boundary -Name 'writerSequence' -Default 0L)
        $boundaryId = [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $Boundary -Name 'boundaryId' -Default '(none)')
        $message = @(
            'event=filetransfer_coordinator_transition_snapshot'
            "transition_id=$boundaryId"
            "log_writer_id=$writer"
            "log_writer_sequence=$writerSequence"
        ) -join '; '
        return [pscustomobject]@{
            EventName = 'filetransfer_coordinator_transition_snapshot'
            Sequence = $writerSequence
            LineNumber = $lineNumber
            FileName = '(coordinator_snapshot)'
            FilePath = '(coordinator_snapshot)'
            SourcePath = '(coordinator_snapshot)'
            Message = $message
            RawLine = $message
            Fields = [ordered]@{
                transition_id = $boundaryId
                log_writer_id = $writer
                log_writer_sequence = [string]$writerSequence
            }
            SessionId = [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
                -InputObject $Boundary -Name 'sessionId' -Default '(none)')
            TransferId = [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
                -InputObject $Boundary -Name 'transferId' -Default '(none)')
            Direction = [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
                -InputObject $Boundary -Name 'direction' -Default '(none)')
            Writer = $writer
            WriterSequence = $writerSequence
            BoundaryId = $boundaryId
            BoundaryHash = [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
                -InputObject $Boundary -Name 'boundaryHash' -Default '(none)')
        }
    }

    $streamComplete = [bool](Get-FileTransferCoordinatorSnapshotObjectProperty `
        -InputObject $Evaluation -Name 'StreamComplete' -Default $false)
    $artifactVerdict = [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
        -InputObject $Evaluation -Name 'ArtifactEvidenceVerdict' -Default 'fail')
    if (-not $routeFamiliesAuthoritative -or
        -not $streamComplete -or $artifactVerdict -cne 'pass') {
        $failure = Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $Evaluation -Name 'FirstUnresolvedBoundary' -Default $null
        $event = & $newBoundaryEvidence $failure
        $failureReason = if (-not $routeFamiliesAuthoritative) {
            'snapshot_authoritative_route_family_coverage_incomplete'
        }
        elseif ($null -ne $failure) {
            [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
                -InputObject $failure -Name 'decisionReason' `
                -Default 'snapshot_authoritative_route_evidence_invalid')
        }
        else {
            'snapshot_authoritative_route_evidence_invalid'
        }
        & $addFinding "artifact|$failureReason" `
            "snapshot authoritative route evidence invalid: reason=$failureReason" `
            $event
        $normalized = [ordered]@{
            source = 'SnapshotAuthorityV1'
            verdict = 'fail'
            routeSelections = @()
            selectedRouteChanges = @()
            findings = @($findings)
            firstUnresolvedBoundary =
                ConvertTo-FileTransferCoordinatorSnapshotRouteFailureSemanticBoundary $failure
        }
        $normalizedJson = ConvertTo-Json -InputObject $normalized `
            -Depth 16 -Compress
        $normalizedHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes (
            [System.Text.UTF8Encoding]::new($false).GetBytes($normalizedJson))
        return [pscustomobject]@{
            Verdict = 'fail'
            RouteSelectedEvents = @()
            RouteAwareEvents = @()
            Findings = @($findings)
            EvidenceEvents = @($evidenceEvents)
            SelectedRouteChanges = @()
            NormalizedBoundaryJson = $normalizedJson
            BoundaryHash = $normalizedHash
            NormalizedJson = $normalizedJson
            NormalizedHash = $normalizedHash
            FirstUnresolvedBoundary = $failure
            Source = 'SnapshotAuthorityV1'
        }
    }

    $boundaries = @(
        Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $Evaluation -Name 'Boundaries' -Default @())
    $routeAggregateIndex = New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries @(
        $boundaries | ForEach-Object { [pscustomobject]@{ Boundary=$_ } })
    foreach ($boundary in @($boundaries | Where-Object {
                [string]$_.verdict -ceq 'fail' -and
                [string]$_.taxonomyClass -in @(
                    'filetransfer_route_transition_incomplete',
                    'filetransfer_route_transition_tuna_v4_to_post_tuna_fallback_v6_proof_bootstrap_missing',
                    'filetransfer_route_transition_draining_transport_grace_terminalization',
                    'filetransfer_live_route_epoch_projection_missing',
                    'publication_authority_missing')
            })) {
        $event = & $newBoundaryEvidence $boundary
        & $addFinding "route-boundary|$($boundary.boundaryId)|$($boundary.taxonomyClass)" `
            "snapshot route authority boundary is unresolved: class=$($boundary.taxonomyClass); transition_id=$($boundary.boundaryId)" `
            $event
    }
    $publicationEntries = [System.Collections.Generic.List[object]]::new()
    $publicationEffectsByBoundary = @{}
    $publicationEffects = @(Get-FileTransferCoordinatorSnapshotObjectProperty -InputObject $Evaluation `
            -Name '__RoutePublicationEffects' -Default @())
    foreach ($effect in $publicationEffects) {
        $key = "$($effect.CoordinatorInstanceId)|$($effect.Writer)|$($effect.TransitionId)"
        if (-not $publicationEffectsByBoundary.ContainsKey($key)) {
            $publicationEffectsByBoundary[$key] = [System.Collections.Generic.List[object]]::new()
        }
        $publicationEffectsByBoundary[$key].Add($effect)
    }
    $initialRegularPublications = Get-FileTransferCoordinatorSnapshotInitialRegularPublications `
        -AggregateIndex $routeAggregateIndex -Effects $publicationEffects
    $transitionEntries = [System.Collections.Generic.List[object]]::new()
    foreach ($boundary in @($boundaries | Sort-Object {
                [long]$_.decisionUtcMilliseconds
            }, {
                [string]$_.writer
            }, {
                [long]$_.writerSequence
            }, {
                [string]$_.boundaryId
            })) {
        $sessionId = [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $boundary -Name 'sessionId' -Default '')
        $transferId = [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $boundary -Name 'transferId' -Default '')
        $direction = [string](Get-FileTransferCoordinatorSnapshotObjectProperty `
            -InputObject $boundary -Name 'direction' -Default '')
        if ([string]::IsNullOrWhiteSpace($sessionId) -or
            [string]::IsNullOrWhiteSpace($transferId) -or
            $direction -notin @('inbound', 'outbound')) {
            $routeOperations = @($boundary.operations | Where-Object {
                    [string]$_.family -in @('route_runtime', 'route_transition')
                })
            if ($routeOperations.Count -gt 0) {
                $event = & $newBoundaryEvidence $boundary
                & $addFinding "scope|$($boundary.boundaryId)" `
                    'snapshot route boundary scope is incomplete' $event
            }
            continue
        }

        foreach ($operation in @($boundary.operations | Where-Object {
                    [string]$_.family -ceq 'route_runtime' -and
                    [string]$_.operationRole -ceq 'route_publication' -and
                    [string]$_.beforeState -ceq 'None' -and
                    [string]$_.afterState -ceq 'Published' -and
                    [bool]$_.stateApplied
                })) {
            $routeBoundary = $operation.routeRuntime.boundary
            $route = [string]$routeBoundary.route
            $expectation = Get-FileTransferCoordinatorSnapshotRouteExpectation `
                -Route $route
            $entry = [pscustomobject]@{
                Boundary = $boundary
                Operation = $operation
                RouteBoundary = $routeBoundary
                Expectation = $expectation
                RuntimeOwner = $null
                RouteCommit = $null
                LastTransition = $null
                SameRouteOwnerSuccessor = $false
                SameOwnerRegularRepublication = $false
                Valid = $false
            }
            $publicationEntries.Add($entry)
            $entryEvent = New-FileTransferCoordinatorSnapshotRouteProjectionEvent `
                -Boundary $boundary -Operation $operation `
                -Expectation $(if ($null -ne $expectation) {
                    $expectation
                }
                else {
                    [pscustomobject]@{
                        RuntimeProfile = '(unknown)'
                        BridgeRecoveryPolicy = '(unknown)'
                        ProjectedTargetTransport = '(unknown)'
                    }
                }) -Sequence 0L
            if ($null -eq $expectation) {
                & $addFinding "route|$($boundary.boundaryId)|$route" `
                    "unknown snapshot route publication: route=$route" $entryEvent
                continue
            }
            $validPublication =
                [bool]$operation.receiptValidated -and
                [long]$operation.operationGeneration -gt 0 -and
                [string]$operation.operationKind -in @(
                    'RoutePublication', 'RouteCommitPublication') -and
                [long]$routeBoundary.routePublicationGeneration -eq
                    [long]$operation.operationGeneration -and
                [long]$routeBoundary.protocolVersion -eq
                    [long]$expectation.ProtocolVersion -and
                [long]$routeBoundary.runtimeGeneration -gt 0 -and
                [long]$routeBoundary.legGeneration -gt 0 -and
                (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                    -Boundary $routeBoundary) -and
                (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                    -Left $routeBoundary -Right $boundary.afterBoundary)
            if (-not $validPublication) {
                & $addFinding "publication|$($boundary.boundaryId)|$($operation.operationKey)" `
                    "snapshot route publication is invalid: route=$route; operation_key=$($operation.operationKey)" `
                    $entryEvent
            }
            $entry.Valid = $validPublication

        }
    }

    $projectionSequence = 0L
    $routeSelectedEvents = [System.Collections.Generic.List[object]]::new()
    foreach ($entry in @($publicationEntries | Sort-Object {
                [long]$_.Boundary.decisionUtcMilliseconds
            }, {
                [string]$_.Boundary.writer
            }, {
                [long]$_.Boundary.writerSequence
            }, {
                [string]$_.Boundary.boundaryId
            })) {
        $projectionSequence++
        $routeSelectedEvents.Add(
            (New-FileTransferCoordinatorSnapshotRouteProjectionEvent `
                -Boundary $entry.Boundary -Operation $entry.Operation `
                -Expectation $(if ($null -ne $entry.Expectation) {
                    $entry.Expectation
                }
                else {
                    [pscustomobject]@{
                        RuntimeProfile = '(unknown)'
                        BridgeRecoveryPolicy = '(unknown)'
                        ProjectedTargetTransport = '(unknown)'
                    }
                }) -Sequence $projectionSequence))
    }

    foreach ($directionGroup in @($publicationEntries | Group-Object {
                "$($_.Boundary.coordinatorInstanceId)|$($_.Boundary.writer)|$($_.Boundary.sessionId)|$($_.Boundary.transferId)|$($_.Boundary.direction)"
            } | Sort-Object Name)) {
        $priorPublicationAuthorities = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $priorRuntimeAuthorities = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $ordered = @($directionGroup.Group | Sort-Object {
                [long]$_.Operation.operationGeneration
            }, {
                [long]$_.Boundary.afterStateGeneration
            }, {
                [string]$_.Boundary.boundaryId
            })
        for ($index = 0; $index -lt $ordered.Count; $index++) {
            $entry = $ordered[$index]
            $event = @($routeSelectedEvents | Where-Object {
                    [string]$_.BoundaryId -ceq
                        [string]$entry.Boundary.boundaryId
                }) | Select-Object -First 1
            if ([long]$entry.Operation.operationGeneration -ne ($index + 1L)) {
                & $addFinding "generation|$($directionGroup.Name)|$($entry.Operation.operationGeneration)" `
                    "snapshot route publication generation is not contiguous: scope=$($directionGroup.Name); generation=$($entry.Operation.operationGeneration)" `
                    $event
            }
            $runtimeEntrants = @($entry.Boundary.operations | Where-Object {
                    [string]$_.family -ceq 'route_runtime' -and
                    [string]$_.operationRole -ceq 'runtime_owner' -and
                    [string]$_.operationKind -ceq 'RuntimeOwner' -and
                    [string]$_.beforeState -ceq 'None' -and
                    [string]$_.afterState -ceq 'Current' -and
                    [bool]$_.stateApplied -and
                    [long]$_.operationGeneration -eq
                        [long]$entry.RouteBoundary.runtimeGeneration -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                        -Left $_.routeRuntime.boundary -Right $entry.RouteBoundary)
                })
            if ($index -eq 0) {
                if ([long]$entry.RouteBoundary.liveRouteEpoch -eq 0L) {
                    $initialKey = "$($entry.Boundary.coordinatorInstanceId)|$($entry.Boundary.writer)|$($entry.Boundary.boundaryId)|$($entry.Operation.operationKey)"
                    $runtimeEntrants = @(if ($initialRegularPublications.ContainsKey($initialKey) -and
                            $initialRegularPublications[$initialKey].PreviousPublicationKey -ceq '(none)') {
                        $initialRegularPublications[$initialKey].RuntimeOwner
                    })
                }
                elseif ($runtimeEntrants.Count -eq 0) {
                    $runtimeBirths = @(
                        foreach ($candidateBoundary in @($boundaries | Where-Object {
                                     [string]$_.sessionId -ceq
                                         [string]$entry.Boundary.sessionId -and
                                     [string]$_.transferId -ceq
                                         [string]$entry.Boundary.transferId -and
                                     [string]$_.direction -ceq
                                         [string]$entry.Boundary.direction -and
                                     [string]$_.coordinatorInstanceId -ceq
                                         [string]$entry.Boundary.coordinatorInstanceId -and
                                     [string]$_.writer -ceq
                                         [string]$entry.Boundary.writer -and
                                     [long]$_.afterStateGeneration -le
                                         [long]$entry.Boundary.beforeStateGeneration
                                 })) {
                            foreach ($candidateOperation in @(
                                    $candidateBoundary.operations | Where-Object {
                                         [string]$_.family -ceq 'route_runtime' -and
                                         [string]$_.operationRole -ceq 'runtime_owner' -and
                                         [string]$_.operationKind -ceq 'RuntimeOwner' -and
                                         [string]$_.beforeState -ceq 'None' -and
                                        [string]$_.afterState -ceq 'Current' -and
                                        [bool]$_.stateApplied -and
                                        [long]$_.operationGeneration -eq
                                            [long]$entry.Operation.operationGeneration -and
                                        (Test-FileTransferCoordinatorSnapshotRouteOwnerLineageEqual `
                                            -Left $_.routeRuntime.boundary `
                                            -Right $entry.RouteBoundary)
                                    })) {
                                [pscustomobject]@{
                                    Boundary = $candidateBoundary
                                    Operation = $candidateOperation
                                }
                            }
                        })
                    $runtimeEntrants = @($runtimeBirths | Where-Object {
                            $birth = $_
                            @($boundaries | Where-Object {
                                     [string]$_.sessionId -ceq
                                         [string]$entry.Boundary.sessionId -and
                                     [string]$_.transferId -ceq
                                         [string]$entry.Boundary.transferId -and
                                     [string]$_.direction -ceq
                                         [string]$entry.Boundary.direction -and
                                     [string]$_.coordinatorInstanceId -ceq
                                         [string]$entry.Boundary.coordinatorInstanceId -and
                                     [string]$_.writer -ceq
                                         [string]$entry.Boundary.writer -and
                                     [long]$_.afterStateGeneration -gt
                                        [long]$birth.Boundary.afterStateGeneration -and
                                    [long]$_.afterStateGeneration -le
                                        [long]$entry.Boundary.beforeStateGeneration -and
                                    @($_.operations | Where-Object {
                                            [string]$_.operationKey -ceq
                                                [string]$birth.Operation.operationKey -and
                                            [string]$_.beforeState -ceq 'Current' -and
                                            [string]$_.afterState -ceq 'None' -and
                                            [bool]$_.stateApplied
                                        }).Count -gt 0
                                }).Count -eq 0
                        })
                }
                if ($runtimeEntrants.Count -ne 1) {
                    $entry.Valid = $false
                    & $addFinding "runtime|$($entry.Boundary.boundaryId)|$($entry.Operation.operationKey)" `
                        "snapshot route publication has no unique current runtime owner: route=$($entry.RouteBoundary.route)" `
                        $event
                }
                else {
                    $entry.RuntimeOwner = if ($runtimeEntrants[0].PSObject.Properties['Operation']) {
                        $runtimeEntrants[0].Operation
                    }
                    else {
                        $runtimeEntrants[0]
                    }
                }
                if ([string]$entry.Operation.operationKind -cne 'RoutePublication' -or
                    [string]$entry.Operation.predecessorAuthorityId -cne '(none)') {
                    $entry.Valid = $false
                    & $addFinding "initial-publication|$($entry.Boundary.boundaryId)|$($entry.Operation.operationKey)" `
                        'snapshot initial route publication has invalid kind or predecessor' `
                        $event
                }
                continue
            }
            $previous = $ordered[$index - 1]
            [void]$priorPublicationAuthorities.Add([string]$previous.Operation.authorityId)
            if ($null -ne $previous.RuntimeOwner) { [void]$priorRuntimeAuthorities.Add([string]$previous.RuntimeOwner.authorityId) }
            $entry.LastTransition = $previous.LastTransition
            $regularKey = "$($entry.Boundary.coordinatorInstanceId)|$($entry.Boundary.writer)|$($entry.Boundary.boundaryId)|$($entry.Operation.operationKey)"
            if ($entry.Valid -and $previous.Valid -and $null -ne $previous.RuntimeOwner -and
                -not $priorPublicationAuthorities.Contains([string]$entry.Operation.authorityId) -and
                $initialRegularPublications.ContainsKey($regularKey) -and
                $initialRegularPublications[$regularKey].PreviousPublicationKey -ceq $previous.Operation.operationKey -and
                $initialRegularPublications[$regularKey].RuntimeOwner.operationKey -ceq $previous.RuntimeOwner.operationKey) {
                $entry.RuntimeOwner = $previous.RuntimeOwner
                $entry.RouteCommit = $previous.RouteCommit
                $entry.SameOwnerRegularRepublication = $true
                continue
            }
            if ($runtimeEntrants.Count -ne 1) {
                & $addFinding "runtime|$($entry.Boundary.boundaryId)|$($entry.Operation.operationKey)" `
                    "snapshot route publication has no unique runtime owner entrant: route=$($entry.RouteBoundary.route)" `
                    $event
            }
            else {
                $entry.RuntimeOwner = $runtimeEntrants[0]
            }
            $retiredPublications = @($entry.Boundary.operations | Where-Object {
                    [string]$_.family -ceq 'route_runtime' -and
                    [string]$_.operationRole -ceq 'route_publication' -and
                    [string]$_.operationKind -ceq
                        [string]$previous.Operation.operationKind -and
                    [string]$_.operationKey -ceq
                        [string]$previous.Operation.operationKey -and
                    [string]$_.beforeState -ceq 'Published' -and
                    [string]$_.afterState -ceq 'None' -and
                    [bool]$_.stateApplied -and
                    [string]$_.authorityId -ceq
                        [string]$previous.Operation.authorityId -and
                    [long]$_.operationGeneration -eq
                        [long]$previous.Operation.operationGeneration -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                        -Left $_.routeRuntime.boundary `
                        -Right $entry.Boundary.beforeBoundary)
                })
            $allPublicationOperations = @($entry.Boundary.operations | Where-Object {
                    [string]$_.family -ceq 'route_runtime' -and
                    [string]$_.operationRole -ceq 'route_publication'
                })
            $retiredRuntimeOwners = @(if ($null -eq $previous.RuntimeOwner) {
                    @()
                }
                else {
                    @($entry.Boundary.operations | Where-Object {
                        [string]$_.family -ceq 'route_runtime' -and
                        [string]$_.operationRole -ceq 'runtime_owner' -and
                        [string]$_.operationKind -ceq 'RuntimeOwner' -and
                        [string]$_.operationKey -ceq
                            [string]$previous.RuntimeOwner.operationKey -and
                        [string]$_.authorityId -ceq
                            [string]$previous.RuntimeOwner.authorityId -and
                        [long]$_.operationGeneration -eq
                            [long]$previous.RuntimeOwner.operationGeneration -and
                        [string]$_.beforeState -ceq 'Current' -and
                        [string]$_.afterState -ceq 'None' -and
                        [bool]$_.stateApplied -and
                        (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                            -Left $_.routeRuntime.boundary `
                            -Right $entry.Boundary.beforeBoundary)
                        })
                })
            $routeCommits = @($entry.Boundary.operations | Where-Object {
                    [string]$_.family -ceq 'route_runtime' -and
                    [string]$_.operationRole -ceq 'route_commit' -and
                    [string]$_.operationKind -ceq 'RouteCommit' -and
                    [string]$_.beforeState -ceq 'None' -and
                    [string]$_.afterState -ceq 'Committed' -and
                    [bool]$_.stateApplied -and
                    [bool]$_.receiptValidated -and
                    [long]$_.operationGeneration -eq
                        [long]$entry.RouteBoundary.routeCommitGeneration -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                        -Left $_.routeRuntime.boundary -Right $entry.RouteBoundary)
                })
            $allRuntimeOwnerOperations = @($entry.Boundary.operations | Where-Object {
                    [string]$_.family -ceq 'route_runtime' -and
                    [string]$_.operationRole -ceq 'runtime_owner'
                })
            $retiredRouteCommits = @(if ($null -eq $previous.RouteCommit) {
                    @()
                }
                else {
                    @($entry.Boundary.operations | Where-Object {
                            [string]$_.family -ceq 'route_runtime' -and
                            [string]$_.operationRole -ceq 'route_commit' -and
                            [string]$_.operationKind -ceq 'RouteCommit' -and
                            [string]$_.operationKey -ceq
                                [string]$previous.RouteCommit.operationKey -and
                            [string]$_.authorityId -ceq
                                [string]$previous.RouteCommit.authorityId -and
                            [long]$_.operationGeneration -eq
                                [long]$previous.RouteCommit.operationGeneration -and
                            [string]$_.beforeState -ceq 'Committed' -and
                            [string]$_.afterState -ceq 'None' -and
                            [bool]$_.stateApplied -and
                            [bool]$_.receiptValidated -and
                            (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                                -Left $_.routeRuntime.boundary `
                                -Right $entry.Boundary.beforeBoundary)
                        })
                })
            $allRouteCommitOperations = @($entry.Boundary.operations | Where-Object {
                    [string]$_.family -ceq 'route_runtime' -and
                    [string]$_.operationRole -ceq 'route_commit'
                })
            if ([string]$previous.RouteBoundary.route -ceq [string]$entry.RouteBoundary.route) {
                $effectKey = "$($entry.Boundary.coordinatorInstanceId)|$($entry.Boundary.writer)|$($entry.Boundary.boundaryId)"
                $effectEvidence = @(if ($publicationEffectsByBoundary.ContainsKey($effectKey)) {
                    $publicationEffectsByBoundary[$effectKey].ToArray()
                })
                $entry.SameRouteOwnerSuccessor = $entry.Valid -and $null -ne $entry.RuntimeOwner -and
                    -not $priorPublicationAuthorities.Contains([string]$entry.Operation.authorityId) -and
                    -not $priorRuntimeAuthorities.Contains([string]$entry.RuntimeOwner.authorityId) -and
                    (Test-FileTransferCoordinatorSnapshotFallbackOwnerPublication -Entry $entry -Previous $previous `
                        -RetiredPublications $retiredPublications -RetiredOwners $retiredRuntimeOwners `
                        -RuntimeOperations $allRuntimeOwnerOperations -PublicationOperations $allPublicationOperations `
                        -Effects $effectEvidence -AggregateIndex $routeAggregateIndex)
                $entry.Valid = $entry.SameRouteOwnerSuccessor
                if (-not $entry.Valid) {
                    & $addFinding "owner-successor|$($entry.Boundary.boundaryId)|$($entry.Operation.operationKey)" `
                        'snapshot same-route publication has no exact fallback owner successor contract' $event
                }
                continue
            }
            $transitionExpectation =
                Get-FileTransferCoordinatorSnapshotRouteTransitionExpectation `
                    -SourceRoute ([string]$previous.RouteBoundary.route) `
                    -TargetRoute ([string]$entry.RouteBoundary.route)
            $terminalTransitions = @($entry.Boundary.operations | Where-Object {
                    [string]$_.family -ceq 'route_transition' -and
                    [string]$_.operationRole -ceq 'terminal' -and
                    $null -ne $transitionExpectation -and
                    [string]$_.operationKind -ceq
                        [string]$transitionExpectation.TransitionKind -and
                    [string]$_.beforeState -ceq 'None' -and
                    [string]$_.afterState -ceq 'Committed' -and
                    [bool]$_.stateApplied -and
                    [bool]$_.receiptValidated -and
                    [long]$_.operationGeneration -gt 0L -and
                    [string]$_.routeTransition.transitionKind -ceq
                        [string]$transitionExpectation.TransitionKind -and
                    [string]$_.routeTransition.sourceTransport -ceq
                        [string]$transitionExpectation.SourceTransport -and
                    [string]$_.routeTransition.targetTransport -ceq
                        [string]$transitionExpectation.TargetTransport -and
                    (Test-FileTransferCoordinatorSnapshotRouteTransitionSourceCurrent `
                        -Source $_.routeTransition.sourceBoundary `
                        -Current $entry.Boundary.beforeBoundary) -and
                    (Test-FileTransferCoordinatorSnapshotRouteTransitionTargetCurrent `
                        -Target $_.routeTransition.targetBoundary `
                        -Current $entry.RouteBoundary)
                })
            $terminalTransition = if ($terminalTransitions.Count -eq 1) {
                $terminalTransitions[0]
            }
            else {
                $null
            }
            $currentTransitions = @(if ($null -eq $terminalTransition) {
                    @()
                }
                else {
                    @($entry.Boundary.operations | Where-Object {
                        [string]$_.family -ceq 'route_transition' -and
                        [string]$_.operationRole -ceq 'current' -and
                        [string]$_.operationKind -ceq
                            [string]$transitionExpectation.TransitionKind -and
                        [string]$_.authorityId -ceq
                            [string]$terminalTransition.authorityId -and
                        [long]$_.operationGeneration -eq
                            [long]$terminalTransition.operationGeneration -and
                        [string]$_.beforeState -ceq 'Draining' -and
                        [string]$_.afterState -ceq 'None' -and
                        [bool]$_.stateApplied -and
                        [string]$_.routeTransition.transitionKind -ceq
                            [string]$transitionExpectation.TransitionKind -and
                        [string]$_.routeTransition.sourceTransport -ceq
                            [string]$transitionExpectation.SourceTransport -and
                        [string]$_.routeTransition.targetTransport -ceq
                            [string]$transitionExpectation.TargetTransport -and
                        (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                            -Left $_.routeTransition.sourceBoundary `
                            -Right $terminalTransition.routeTransition.sourceBoundary) -and
                        [string]$_.routeTransition.targetBoundary.route -ceq
                            [string]$terminalTransition.routeTransition.targetBoundary.route -and
                        [long]$_.routeTransition.targetBoundary.protocolVersion -eq
                            [long]$terminalTransition.routeTransition.targetBoundary.protocolVersion -and
                        [string]$_.routeTransition.targetBoundary.dataSessionIdentity -ceq
                            [string]$terminalTransition.routeTransition.targetBoundary.dataSessionIdentity -and
                        [long]$_.routeTransition.targetBoundary.dataSessionGeneration -eq
                            [long]$terminalTransition.routeTransition.targetBoundary.dataSessionGeneration
                        })
                })
            $attemptedTransitions = @(if ($null -eq $terminalTransition) {
                    @()
                }
                else {
                    @($entry.Boundary.operations | Where-Object {
                        [string]$_.family -ceq 'route_transition' -and
                        [string]$_.operationRole -ceq [string]$entry.Boundary.direction -and
                        [string]$_.operationKind -ceq 'CommitFileTransferRouteTransition' -and
                        [string]$_.authorityId -ceq
                            [string]$terminalTransition.authorityId -and
                        [long]$_.operationGeneration -eq
                            [long]$terminalTransition.operationGeneration -and
                        [string]$_.beforeState -ceq 'Current' -and
                        [string]$_.afterState -ceq 'Committed' -and
                        -not [bool]$_.stateApplied -and
                        [bool]$_.receiptValidated -and
                        [string]$_.routeTransition.transitionKind -ceq
                            [string]$transitionExpectation.TransitionKind -and
                        [string]$_.routeTransition.sourceTransport -ceq
                            [string]$transitionExpectation.SourceTransport -and
                        [string]$_.routeTransition.targetTransport -ceq
                            [string]$transitionExpectation.TargetTransport -and
                        (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                            -Left $_.routeTransition.sourceBoundary `
                            -Right $terminalTransition.routeTransition.sourceBoundary) -and
                        (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                            -Left $_.routeTransition.targetBoundary `
                            -Right $terminalTransition.routeTransition.targetBoundary)
                        })
                })
            $targetTransportValid =
                $terminalTransitions.Count -eq 1 -and
                $null -ne $entry.Expectation -and
                [string]$terminalTransitions[0].routeTransition.targetRoute -ceq
                    [string]$entry.RouteBoundary.route -and
                [long]$terminalTransitions[0].routeTransition.targetProtocolVersion -eq
                    [long]$entry.RouteBoundary.protocolVersion -and
                [string]$terminalTransitions[0].routeTransition.targetTransport -ceq
                    [string]$entry.Expectation.TargetTransport
            $publishesTuna =
                [string]$entry.RouteBoundary.route -ceq 'file_tuna_v4'
            $routeCommitLinked = if ($publishesTuna) {
                $routeCommits.Count -eq 1 -and
                    $retiredRouteCommits.Count -eq 0 -and
                    $allRouteCommitOperations.Count -eq 1 -and
                    [long]$entry.RouteBoundary.routeCommitGeneration -gt 0 -and
                    [string]$entry.Operation.operationKind -ceq
                        'RouteCommitPublication' -and
                    [string]$entry.Operation.predecessorAuthorityId -ceq
                        [string]$routeCommits[0].authorityId
            }
            else {
                [string]$entry.RouteBoundary.route -ceq
                    'post_tuna_fallback_v6' -and
                    $routeCommits.Count -eq 0 -and
                    $retiredRouteCommits.Count -eq 1 -and
                    $allRouteCommitOperations.Count -eq 1 -and
                    [long]$entry.RouteBoundary.routeCommitGeneration -eq 0L -and
                    [string]$entry.Operation.operationKind -ceq
                        'RoutePublication' -and
                    [string]$entry.Operation.predecessorAuthorityId -ceq '(none)'
            }
            if ($retiredPublications.Count -ne 1 -or
                $allPublicationOperations.Count -ne 2 -or
                $retiredRuntimeOwners.Count -ne 1 -or
                $allRuntimeOwnerOperations.Count -ne 2 -or
                $runtimeEntrants.Count -ne 1 -or
                -not $routeCommitLinked -or
                -not $targetTransportValid) {
                $entry.Valid = $false
                & $addFinding "transition|$($entry.Boundary.boundaryId)|$($entry.Operation.operationKey)" `
                    "snapshot route publication has no exact committed predecessor transition: route=$($entry.RouteBoundary.route); operation_key=$($entry.Operation.operationKey)" `
                    $event
            }
            elseif ($currentTransitions.Count -ne 1 -or
                $attemptedTransitions.Count -ne 1) {
                $entry.Valid = $false
                & $addFinding "transition-operations|$($entry.Boundary.boundaryId)|$($entry.Operation.operationKey)" `
                    "snapshot route commit does not contain the exact transition retirement and attempted decision: route=$($entry.RouteBoundary.route); operation_key=$($entry.Operation.operationKey)" `
                    $event
            }
            else {
                $entry.LastTransition = $terminalTransitions[0]
                if ($publishesTuna) {
                    $entry.RouteCommit = $routeCommits[0]
                }
                $transitionEntries.Add([pscustomobject]@{
                        Boundary = $entry.Boundary
                        Operation = $terminalTransitions[0]
                        Expectation = $entry.Expectation
                        Sequence = [long]$event.Sequence
                    })
            }
        }
    }

    $selectedRouteChanges = [System.Collections.Generic.List[string]]::new()
    foreach ($transferGroup in @($publicationEntries | Group-Object {
                "$($_.Boundary.sessionId)|$($_.Boundary.transferId)"
            } | Sort-Object Name)) {
        $directionHistories = @($transferGroup.Group | Group-Object {
                [string]$_.Boundary.direction
            } | Sort-Object Name)
        $reference = @()
        if ($directionHistories.Count -gt 0) {
            $reference = @($directionHistories[0].Group | Where-Object { -not $_.SameRouteOwnerSuccessor -and -not $_.SameOwnerRegularRepublication } | Sort-Object {
                    [long]$_.Operation.operationGeneration
                } | ForEach-Object {
                    "$($_.RouteBoundary.liveRouteEpoch)|$($_.RouteBoundary.route)|$($_.RouteBoundary.protocolVersion)"
                })
        }
        foreach ($directionHistory in @($directionHistories | Select-Object -Skip 1)) {
            $candidate = @($directionHistory.Group | Where-Object { -not $_.SameRouteOwnerSuccessor -and -not $_.SameOwnerRegularRepublication } | Sort-Object {
                    [long]$_.Operation.operationGeneration
                } | ForEach-Object {
                    "$($_.RouteBoundary.liveRouteEpoch)|$($_.RouteBoundary.route)|$($_.RouteBoundary.protocolVersion)"
                })
            if (($reference -join ';') -cne ($candidate -join ';')) {
                $event = @($routeSelectedEvents | Where-Object {
                        [string]$_.SessionId -ceq
                            [string]$transferGroup.Group[0].Boundary.sessionId -and
                        [string]$_.TransferId -ceq
                            [string]$transferGroup.Group[0].Boundary.transferId
                    } | Select-Object -First 1) | Select-Object -First 1
                & $addFinding "direction|$($transferGroup.Name)" `
                    "snapshot route publication histories diverge by direction: scope=$($transferGroup.Name)" `
                    $event
            }
        }
        foreach ($item in $reference) {
            $route = @($item -split '\|')[1]
            if ($selectedRouteChanges.Count -eq 0 -or
                [string]$selectedRouteChanges[$selectedRouteChanges.Count - 1] -cne
                    $route) {
                $selectedRouteChanges.Add($route)
            }
        }
    }

    if ($publicationEntries.Count -eq 0) {
        & $addFinding 'publication|missing' `
            'snapshot authoritative route stream contains no route publications' `
            (& $newBoundaryEvidence ($boundaries | Select-Object -First 1))
    }

    $routeAwareEvents = @($routeSelectedEvents) + @(
        $transitionEntries | Sort-Object {
            [long]$_.Sequence
        }, {
            [string]$_.Boundary.writer
        } | ForEach-Object {
            & $newBoundaryEvidence $_.Boundary
        })
    $verdict = if ($findings.Count -eq 0) { 'pass' } else { 'fail' }
    $firstUnresolvedBoundary = $null
    if ($verdict -ceq 'fail') {
        $firstEvidence = @($evidenceEvents | Select-Object -First 1)
        $firstEvent = if ($firstEvidence.Count -gt 0) {
            $firstEvidence[0]
        }
        else {
            $null
        }
        $firstBoundary = if ($null -ne $firstEvent) {
            @($boundaries | Where-Object {
                    [string]$_.boundaryId -ceq [string]$firstEvent.BoundaryId
                } | Select-Object -First 1) | Select-Object -First 1
        }
        else {
            $null
        }
        $firstUnresolvedBoundary = [pscustomobject][ordered]@{
            boundaryId = if ($null -ne $firstBoundary) {
                [string]$firstBoundary.boundaryId
            }
            else { '(none)' }
            boundaryHash = if ($null -ne $firstBoundary) {
                [string]$firstBoundary.boundaryHash
            }
            else { '(none)' }
            taxonomyClass = 'filetransfer_route_consistency_analysis_parity_invalid'
            decisionReason = 'snapshot_authoritative_route_consistency_invalid'
            variant = 'snapshot_authoritative_route_boundary_invalid'
            domain = 'route_runtime'
            phase = 'evidence_finalization'
            rawCount = 1
            distinctCount = 1
            recoveredCount = 0
            unresolvedCount = 1
            writer = if ($null -ne $firstBoundary) {
                [string]$firstBoundary.writer
            }
            else { '(unknown)' }
            writerSequence = if ($null -ne $firstBoundary) {
                [long]$firstBoundary.writerSequence
            }
            else { 0L }
            evidenceLineIndex = if ($null -ne $firstBoundary) {
                [long]$firstBoundary.evidenceLineIndex
            }
            else { -1L }
            sessionId = if ($null -ne $firstBoundary) {
                [string]$firstBoundary.sessionId
            }
            else { '(none)' }
            transferId = if ($null -ne $firstBoundary) {
                [string]$firstBoundary.transferId
            }
            else { '(none)' }
            direction = if ($null -ne $firstBoundary) {
                [string]$firstBoundary.direction
            }
            else { '(none)' }
        }
    }
    $normalized = [ordered]@{
        source = 'SnapshotAuthorityV1'
        verdict = $verdict
        routeSelections = @($routeSelectedEvents | ForEach-Object {
                [ordered]@{
                    boundaryId = [string]$_.BoundaryId
                    boundaryHash = [string]$_.BoundaryHash
                    writer = [string]$_.Writer
                    writerSequence = [long]$_.WriterSequence
                    sequence = [long]$_.Sequence
                    sessionId = [string]$_.SessionId
                    transferId = [string]$_.TransferId
                    direction = [string]$_.Direction
                    route = [string]$_.Fields.route
                    protocolVersion = [long]$_.Fields.protocol_version
                    runtimeProfile = [string]$_.Fields.runtime_profile
                    bridgeRecoveryPolicy =
                        [string]$_.Fields.bridge_recovery_policy
                    targetTransport = [string]$_.Fields.target_transport
                    publicationGeneration =
                        [long]$_.Fields.route_publication_generation
                    coordinatorStateGeneration =
                        [long]$_.Fields.coordinator_state_generation
                    operationKey = [string]$_.Fields.operation_key
                }
            })
        routeTransitions = @($transitionEntries | Sort-Object {
                    [long]$_.Sequence
                }, {
                    [string]$_.Boundary.writer
                } | ForEach-Object {
                    [ordered]@{
                        boundaryId = [string]$_.Boundary.boundaryId
                        boundaryHash = [string]$_.Boundary.boundaryHash
                        writer = [string]$_.Boundary.writer
                        writerSequence = [long]$_.Boundary.writerSequence
                        sessionId = [string]$_.Boundary.sessionId
                        transferId = [string]$_.Boundary.transferId
                        direction = [string]$_.Boundary.direction
                        authorityId = [string]$_.Operation.authorityId
                        operationGeneration =
                            [long]$_.Operation.operationGeneration
                        sourceRoute =
                            [string]$_.Operation.routeTransition.sourceBoundary.route
                        targetRoute =
                            [string]$_.Operation.routeTransition.targetRoute
                    }
                })
        selectedRouteChanges = @($selectedRouteChanges)
        findings = @($findings)
    }
    if ($null -ne $firstUnresolvedBoundary) {
        $normalized['firstUnresolvedBoundary'] =
            ConvertTo-FileTransferCoordinatorSnapshotRouteFailureSemanticBoundary $firstUnresolvedBoundary
    }
    $normalizedJson = ConvertTo-Json -InputObject $normalized -Depth 32 -Compress
    $normalizedHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes (
        [System.Text.UTF8Encoding]::new($false).GetBytes($normalizedJson))
    return [pscustomobject]@{
        Verdict = $verdict
        RouteSelectedEvents = @($routeSelectedEvents)
        RouteAwareEvents = @($routeAwareEvents)
        Findings = @($findings)
        EvidenceEvents = @($evidenceEvents)
        SelectedRouteChanges = @($selectedRouteChanges)
        NormalizedBoundaryJson = $normalizedJson
        BoundaryHash = $normalizedHash
        NormalizedJson = $normalizedJson
        NormalizedHash = $normalizedHash
        FirstUnresolvedBoundary = $firstUnresolvedBoundary
        Source = 'SnapshotAuthorityV1'
    }
}

function Compare-FileTransferCoordinatorSnapshotRouteConsistency {
    [CmdletBinding()]
    param($Full, $Compact)

    $fullHash = if ($null -ne $Full -and
        $null -ne $Full.PSObject.Properties['BoundaryHash']) {
        [string]$Full.BoundaryHash
    }
    else { '(none)' }
    $compactHash = if ($null -ne $Compact -and
        $null -ne $Compact.PSObject.Properties['BoundaryHash']) {
        [string]$Compact.BoundaryHash
    }
    else { '(none)' }

    if ($null -eq $Full -and $null -eq $Compact) {
        return [pscustomobject]@{
            Verdict = 'pass'
            FailureReason = '(none)'
            FullBoundaryHash = $fullHash
            CompactBoundaryHash = $compactHash
        }
    }
    $failureReason = '(none)'
    if ($null -eq $Full -or $null -eq $Compact) {
        $failureReason = 'snapshot_route_consistency_mode_mismatch'
    }
    elseif ($null -eq $Full.PSObject.Properties['Verdict'] -or
        $null -eq $Compact.PSObject.Properties['Verdict'] -or
        $null -eq $Full.PSObject.Properties['NormalizedHash'] -or
        $null -eq $Compact.PSObject.Properties['NormalizedHash'] -or
        $null -eq $Full.PSObject.Properties['NormalizedJson'] -or
        $null -eq $Compact.PSObject.Properties['NormalizedJson']) {
        $failureReason = 'snapshot_route_consistency_result_incomplete'
    }
    elseif ([string]$Full.Verdict -cne [string]$Compact.Verdict -or
        [string]::IsNullOrWhiteSpace([string]$Full.NormalizedHash) -or
        [string]::IsNullOrWhiteSpace([string]$Compact.NormalizedHash) -or
        [string]::IsNullOrWhiteSpace([string]$Full.NormalizedJson) -or
        [string]::IsNullOrWhiteSpace([string]$Compact.NormalizedJson) -or
        [string]$Full.NormalizedHash -cne [string]$Compact.NormalizedHash -or
        [string]$Full.NormalizedJson -cne [string]$Compact.NormalizedJson) {
        $failureReason = 'snapshot_route_consistency_full_compact_mismatch'
    }
    return [pscustomobject]@{
        Verdict = if ($failureReason -ceq '(none)') { 'pass' } else { 'fail' }
        FailureReason = $failureReason
        FullBoundaryHash = $fullHash
        CompactBoundaryHash = $compactHash
    }
}

function Test-FileTransferCoordinatorSnapshotFamilyAuthoritative {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Evaluation,
        [Parameter(Mandatory = $true)][string]$Family
    )

    return $Evaluation.Mode -ne 'LegacyV0' -and
        $Evaluation.ArtifactEvidenceVerdict -ceq 'pass' -and
        @($Evaluation.AuthoritativeFamilies) -contains $Family
}

function ConvertTo-FileTransferCoordinatorEvidenceToken {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -ceq '(none)') { return '(none)' }
    'fte1-' + (Get-FileTransferCoordinatorSnapshotSha256 `
        -Bytes ([Text.Encoding]::UTF8.GetBytes($Value))).Substring(0, 24)
}

function New-FileTransferHeldPublicationNaturalProofRecoveryIndex {
    param($Evaluation, [AllowEmptyCollection()][string[]]$Lines = @())

    $result = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if ($null -eq $Evaluation -or $Evaluation.Mode -cne 'SnapshotV1Authoritative' -or
        $Evaluation.ArtifactEvidenceVerdict -cne 'pass' -or -not $Evaluation.StreamComplete -or
        $null -eq $Evaluation.PSObject.Properties['__HeldPublicationProofClosures']) { return ,$result }

    $publicationEvents = @(
        'filetransfer_receive_recovery_proof_state_publication_recheck_decided',
        'filetransfer_receive_recovery_proof_state_publication_receipt_applied',
        'filetransfer_receive_recovery_proof_state_publication_invalidated')
    $proofEvents = @('filetransfer_receive_recovery_operation_proof_observed',
        'filetransfer_receive_recovery_operation_closure_adopted')
    $buckets = @{}
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in $Lines) {
        $eventName = Get-FileTransferCoordinatorSnapshotEventName -Line $line
        if (($publicationEvents -notcontains $eventName -and $proofEvents -notcontains $eventName) -or
            -not $seen.Add($line)) { continue }
        $fields = @{}
        $conflictingFields = $false
        foreach ($match in [regex]::Matches($line, '(?:^|;\s*)([a-z0-9_]+)=([^;\r\n]*)')) {
            $name = $match.Groups[1].Value
            $value = $match.Groups[2].Value.Trim()
            if ($fields.ContainsKey($name) -and $fields[$name] -cne $value) { $conflictingFields = $true }
            $fields[$name] = $value
        }
        $writerSequence = 0L
        if ($conflictingFields -or -not [long]::TryParse([string]$fields['log_writer_sequence'], [ref]$writerSequence) -or
            $writerSequence -le 0) { continue }
        foreach ($name in @('log_writer_id','publication_generation','receive_recovery_operation_authority_id',
                'receive_recovery_operation_generation','direction','publication_attempt','decision_status',
                'recovery_operation_generation','closure_generation','predecessor_publication_generation',
                'proof_kind','publication_outcome','publication_reason','invalidation_target','decision_reason',
                'closure_state','proof_observed_utc_ms','proof_identity')) {
            if (-not $fields.ContainsKey($name)) { $fields[$name] = '' }
        }
        $key = if ($publicationEvents -contains $eventName) {
            "$($fields.log_writer_id)|$($fields.publication_generation)|$eventName"
        } else {
            $authority = ConvertTo-FileTransferCoordinatorEvidenceToken $fields.receive_recovery_operation_authority_id
            "$($fields.log_writer_id)|$authority|$($fields.receive_recovery_operation_generation)|$eventName"
        }
        if (-not $buckets.ContainsKey($key)) { $buckets[$key] = [Collections.Generic.List[object]]::new() }
        $buckets[$key].Add([pscustomobject]@{ Line = $line; Fields = $fields })
    }

    function Get-ClosureRecords([string]$key) {
        if ($buckets.ContainsKey($key)) { $buckets[$key].ToArray() }
    }
    function Test-DiagnosticScope($record, $closure) {
        if ($record.Fields.direction -cne $closure.Direction) { return $false }
        # Physical diagnostics do not serialize checkpoint generation or an unredacted
        # data-session identity. Those are bound by the exact parent/child V1 history.
        foreach ($mapping in @(
                @('route','route'), @('protocol_version','protocolVersion'),
                @('live_route_epoch','liveRouteEpoch'), @('route_publication_generation','routePublicationGeneration'),
                @('route_commit_generation','routeCommitGeneration'), @('runtime_generation','runtimeGeneration'),
                @('terminal_generation','terminalGeneration'), @('leg_generation','legGeneration'),
                @('transport_epoch','transportEpoch'), @('bridge_recovery_generation','bridgeRecoveryGeneration'),
                @('checkpoint_request_id','checkpointRequestId'), @('data_session_generation','dataSessionGeneration'))) {
            if ([string]$record.Fields[$mapping[0]] -cne [string]$closure.SourceBoundary.($mapping[1])) { return $false }
        }
        return $true
    }
    function Test-DiagnosticIdentity($left, $right) {
        foreach ($field in @('log_writer_id','session_id','transfer_id','direction','data_session_identity',
                'data_session_generation','closure_generation')) {
            if ([string]::IsNullOrWhiteSpace([string]$left.Fields[$field]) -or
                [string]$left.Fields[$field] -cne [string]$right.Fields[$field]) { return $false }
        }
        return $true
    }
    function Test-PublicationTuple($recheck, $record) {
        foreach ($field in @('recovery_operation_generation','closure_generation','publication_generation',
                'predecessor_publication_generation','predecessor_frame_fingerprint','predecessor_receipt_observed_utc_ms')) {
            if ([string]::IsNullOrWhiteSpace([string]$recheck.Fields[$field]) -or
                [string]$recheck.Fields[$field] -cne [string]$record.Fields[$field]) { return $false }
        }
        foreach ($mapping in @(@('heartbeat_generation','recheck_heartbeat_generation'),
                @('heartbeat_sequence','recheck_heartbeat_sequence'), @('proof_kind','recheck_proof_kind'), @('lane','recheck_lane'))) {
            if ([string]::IsNullOrWhiteSpace([string]$recheck.Fields[$mapping[0]]) -or
                [string]$recheck.Fields[$mapping[0]] -cne [string]$record.Fields[$mapping[1]]) { return $false }
        }
        return $record.Fields.publication_attempt -ceq 'post_delivery_recheck'
    }

    foreach ($closure in $Evaluation.__HeldPublicationProofClosures) {
        $publicationKey = "$($closure.Writer)|$($closure.PublicationGeneration)"
        $parentKey = "$($closure.Writer)|$($closure.AuthorityId)|$($closure.OperationGeneration)"
        $rechecks = @(Get-ClosureRecords "$publicationKey|$($publicationEvents[0])" | Where-Object {
                $_.Fields.decision_status -ceq 'Accepted' -and
                $_.Fields.recovery_operation_generation -ceq [string]$closure.OperationGeneration -and
                $_.Fields.closure_generation -ceq [string]$closure.ClosureGeneration -and
                $_.Fields.predecessor_publication_generation -ceq [string]($closure.PublicationGeneration - 1) -and
                $_.Fields.proof_kind -ceq 'heartbeat_received' -and
                [long]$_.Fields.log_writer_sequence -gt $closure.OriginSequence -and
                [long]$_.Fields.log_writer_sequence -lt $closure.HeldSequence -and
                (Test-DiagnosticScope $_ $closure)
            })
        $held = @(Get-ClosureRecords "$publicationKey|$($publicationEvents[1])" | Where-Object {
                $_.Fields.publication_outcome -ceq 'Held' -and
                $_.Fields.decision_status -in @('Accepted','Idempotent') -and
                $_.Fields.publication_reason -ceq 'filetransfer_receive_recovery_proof_state_held_for_tuna_activation_pause' -and
                [long]$_.Fields.log_writer_sequence -gt $closure.HeldSequence -and
                [long]$_.Fields.log_writer_sequence -lt $closure.ClosureSequence -and
                (Test-DiagnosticScope $_ $closure)
            })
        $invalidations = @(Get-ClosureRecords "$publicationKey|$($publicationEvents[2])" | Where-Object {
                $_.Fields.invalidation_target -ceq 'pending_command' -and
                $_.Fields.decision_reason -ceq 'coordinator_receive_recovery_transition' -and
                [long]$_.Fields.log_writer_sequence -gt $closure.ClosureSequence -and
                (Test-DiagnosticScope $_ $closure)
            })
        $proof = @(Get-ClosureRecords "$parentKey|$($proofEvents[0])" | Where-Object {
                $_.Fields.closure_state -ceq 'Adopted' -and
                $_.Fields.proof_observed_utc_ms -ceq [string]$closure.ProofObservedUtcMs -and
                (ConvertTo-FileTransferCoordinatorEvidenceToken $_.Fields.proof_identity) -ceq $closure.ProofIdentity -and
                [long]$_.Fields.log_writer_sequence -gt $closure.ClosureSequence -and
                (Test-DiagnosticScope $_ $closure)
            })
        $adopted = @(Get-ClosureRecords "$parentKey|$($proofEvents[1])" | Where-Object {
                $_.Fields.closure_state -ceq 'Adopted' -and
                $_.Fields.proof_observed_utc_ms -ceq [string]$closure.ProofObservedUtcMs -and
                (ConvertTo-FileTransferCoordinatorEvidenceToken $_.Fields.proof_identity) -ceq $closure.ProofIdentity -and
                [long]$_.Fields.log_writer_sequence -gt $closure.ClosureSequence -and
                (Test-DiagnosticScope $_ $closure)
            })
        if ($rechecks.Count -ne 1 -or $held.Count -ne 1 -or $invalidations.Count -ne 1 -or
            $proof.Count -ne 1 -or $adopted.Count -ne 1) { continue }
        $valid = $true
        foreach ($record in @($held[0], $invalidations[0], $proof[0], $adopted[0])) {
            $valid = $valid -and (Test-DiagnosticIdentity $rechecks[0] $record)
        }
        foreach ($record in @($held[0], $invalidations[0])) {
            $valid = $valid -and (Test-PublicationTuple $rechecks[0] $record)
        }
        if ($valid) { [void]$result.Add("$($closure.AuthorityId)|$($rechecks[0].Line)") }
    }
    return ,$result
}

function Test-FileTransferHeldPublicationNaturalProofRecovery {
    param($Index, [string]$AuthorityId, [string]$RecheckLine)
    $null -ne $Index -and $Index.Contains(
        "$(ConvertTo-FileTransferCoordinatorEvidenceToken $AuthorityId)|$RecheckLine")
}

function Get-FileTransferCoordinatorSnapshotCorrelationResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Evaluation,
        [Parameter(Mandatory = $true)][string]$Family,
        [string[]]$TaxonomyClasses = @()
    )

    [object[]]$boundaries = @($Evaluation.Boundaries | Where-Object {
            @($_.families) -contains $Family -and
            ($TaxonomyClasses.Count -eq 0 -or
             $TaxonomyClasses -contains [string]$_.taxonomyClass) -and
            ([int]$_.rawCount -gt 0 -or [string]$_.verdict -ceq 'fail')
        })
    [object[]]$records = @(
        foreach ($boundary in $boundaries) {
            $sequence = 0L
            [void][long]::TryParse([string]$boundary.writerSequence, [ref]$sequence)
            if ($sequence -le 0) {
                $sequence = [long]$boundary.evidenceLineIndex + 1L
            }
            $event = [pscustomobject]@{
                EventName = 'filetransfer_coordinator_transition_snapshot'
                Sequence = $sequence
                LineNumber = [int]$boundary.evidenceLineIndex + 1
                FilePath = '(coordinator_snapshot)'
                RawLine = "event=filetransfer_coordinator_transition_snapshot; decision_reason=$($boundary.decisionReason); log_writer_id=$($boundary.writer); log_writer_sequence=$sequence"
            }
            [pscustomobject]@{
                Event = $event
                Class = [string]$boundary.taxonomyClass
                Reason = [string]$boundary.decisionReason
                Boundary = $boundary
                Recovered = [int]$boundary.recoveredCount -gt 0 -and
                    [int]$boundary.unresolvedCount -eq 0
            }
        })
    [object[]]$unresolved = @($records | Where-Object {
            [int]$_.Boundary.unresolvedCount -gt 0 })
    [object[]]$recovered = @($records | Where-Object {
            [int]$_.Boundary.recoveredCount -gt 0 })
    $rawCount = 0
    $distinctCount = 0
    $recoveredCount = 0
    $unresolvedCount = 0
    foreach ($boundary in $boundaries) {
        $rawCount += [int]$boundary.rawCount
        $distinctCount += [int]$boundary.distinctCount
        $recoveredCount += [int]$boundary.recoveredCount
        $unresolvedCount += [int]$boundary.unresolvedCount
    }
    [object[]]$initialResponderRecords = @($records | Where-Object {
            $_.Boundary.PSObject.Properties.Name -contains 'variant' -and
            [string]$_.Boundary.variant -ceq
                'initial_activation_peer_acceptance_scope_rejected'
        })
    [object[]]$ackBeforeProbeRecords = @($records | Where-Object {
            $_.Boundary.PSObject.Properties.Name -contains 'variant' -and
            [string]$_.Boundary.variant -ceq
                'ack_before_probe_adoption_recheck_missing'
        })
    [object[]]$bindingMissingRecords = @($records | Where-Object {
            $_.Boundary.PSObject.Properties.Name -contains 'variant' -and
            [string]$_.Boundary.variant -ceq 'binding_missing'
        })
    [object[]]$afterBoundRecords = @($records | Where-Object {
            $_.Boundary.PSObject.Properties.Name -contains 'variant' -and
            [string]$_.Boundary.variant -ceq 'after_bound_handoff_missing'
        })
    function Get-SnapshotCorrelationVariantCount {
        param(
            [object[]]$VariantRecords,
            [ValidateSet('raw', 'distinct', 'recovered', 'unresolved')]
            [string]$Kind)
        $total = 0
        foreach ($record in @($VariantRecords)) {
            $total += switch ($Kind) {
                'raw' { [int]$record.Boundary.rawCount }
                'distinct' { [int]$record.Boundary.distinctCount }
                'recovered' { [int]$record.Boundary.recoveredCount }
                'unresolved' { [int]$record.Boundary.unresolvedCount }
            }
        }
        return $total
    }
    [pscustomobject]@{
        RawCount = $rawCount
        DistinctCount = $distinctCount
        RecoveredCount = $recoveredCount
        UnresolvedCount = $unresolvedCount
        Verdict = if ($unresolved.Count -gt 0) { 'fail' } else { 'pass' }
        CandidateRecords = $records
        RecoveredCandidateRecords = $recovered
        UnresolvedCandidateRecords = $unresolved
        CandidateEvents = @($records | ForEach-Object { $_.Event })
        RecoveredCandidateEvents = @($recovered | ForEach-Object { $_.Event })
        UnresolvedCandidateEvents = @($unresolved | ForEach-Object { $_.Event })
        AuthoritativeRecords = @($records | ForEach-Object { $_.Event })
        InitialActivationPeerAcceptanceScopeRejectedRawCount =
            Get-SnapshotCorrelationVariantCount $initialResponderRecords raw
        InitialActivationPeerAcceptanceScopeRejectedDistinctCount =
            Get-SnapshotCorrelationVariantCount $initialResponderRecords distinct
        InitialActivationPeerAcceptanceScopeRejectedRecoveredCount =
            Get-SnapshotCorrelationVariantCount $initialResponderRecords recovered
        InitialActivationPeerAcceptanceScopeRejectedUnresolvedCount =
            Get-SnapshotCorrelationVariantCount $initialResponderRecords unresolved
        InitialActivationPeerAcceptanceScopeRejectedVerdict = if (
            (Get-SnapshotCorrelationVariantCount $initialResponderRecords unresolved) -gt 0) { 'fail' } else { 'pass' }
        AckBeforeProbeAdoptionRecheckMissingRawCount =
            Get-SnapshotCorrelationVariantCount $ackBeforeProbeRecords raw
        AckBeforeProbeAdoptionRecheckMissingDistinctCount =
            Get-SnapshotCorrelationVariantCount $ackBeforeProbeRecords distinct
        AckBeforeProbeAdoptionRecheckMissingRecoveredCount =
            Get-SnapshotCorrelationVariantCount $ackBeforeProbeRecords recovered
        AckBeforeProbeAdoptionRecheckMissingUnresolvedCount =
            Get-SnapshotCorrelationVariantCount $ackBeforeProbeRecords unresolved
        AckBeforeProbeAdoptionRecheckMissingVerdict = if (
            (Get-SnapshotCorrelationVariantCount $ackBeforeProbeRecords unresolved) -gt 0) { 'fail' } else { 'pass' }
        BindingMissingRawCount =
            Get-SnapshotCorrelationVariantCount $bindingMissingRecords raw
        BindingMissingDistinctCount =
            Get-SnapshotCorrelationVariantCount $bindingMissingRecords distinct
        BindingMissingRecoveredCount =
            Get-SnapshotCorrelationVariantCount $bindingMissingRecords recovered
        BindingMissingUnresolvedCount =
            Get-SnapshotCorrelationVariantCount $bindingMissingRecords unresolved
        BindingMissingVerdict = if (
            (Get-SnapshotCorrelationVariantCount $bindingMissingRecords unresolved) -gt 0) { 'fail' } else { 'pass' }
        AfterBoundHandoffMissingRawCount =
            Get-SnapshotCorrelationVariantCount $afterBoundRecords raw
        AfterBoundHandoffMissingDistinctCount =
            Get-SnapshotCorrelationVariantCount $afterBoundRecords distinct
        AfterBoundHandoffMissingRecoveredCount =
            Get-SnapshotCorrelationVariantCount $afterBoundRecords recovered
        AfterBoundHandoffMissingUnresolvedCount =
            Get-SnapshotCorrelationVariantCount $afterBoundRecords unresolved
        AfterBoundHandoffMissingVerdict = if (
            (Get-SnapshotCorrelationVariantCount $afterBoundRecords unresolved) -gt 0) { 'fail' } else { 'pass' }
        Source = 'SnapshotAuthorityV1'
    }
}

function ConvertTo-FileTransferCoordinatorSnapshotBindingSemanticValue {
    param($Value)

    if ($null -eq $Value -or $Value -is [string] -or
        $Value.GetType().IsValueType) {
        return $Value
    }
    if ($Value -is [Collections.IDictionary]) {
        $result = [ordered]@{}
        foreach ($key in @($Value.Keys | Sort-Object)) {
            if ([string]$key -in @('LineIndex', 'EvidenceLineIndex')) {
                continue
            }
            $result[[string]$key] =
                ConvertTo-FileTransferCoordinatorSnapshotBindingSemanticValue `
                    -Value $Value[$key]
        }
        return $result
    }
    if ($Value -is [Collections.IEnumerable]) {
        $items = @($Value | ForEach-Object {
                ConvertTo-FileTransferCoordinatorSnapshotBindingSemanticValue `
                    -Value $_
            })
        # Preserve plain arrays; Write-Output -NoEnumerate adds PS5.1 serializer metadata.
        return ,$items
    }

    $result = [ordered]@{}
    foreach ($property in @($Value.PSObject.Properties | Sort-Object Name)) {
        if ([string]$property.Name -in @('LineIndex', 'EvidenceLineIndex')) {
            continue
        }
        $result[[string]$property.Name] =
            ConvertTo-FileTransferCoordinatorSnapshotBindingSemanticValue `
                -Value $property.Value
    }
    return $result
}

function Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofBindingSemanticSummary {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Evaluation)

    $bindings = if ($null -ne $Evaluation.PSObject.Properties[
            '__FallbackTailPeerProofBindings']) {
        @($Evaluation.__FallbackTailPeerProofBindings)
    }
    else {
        @()
    }
    $normalized = @($bindings | Sort-Object {
                [string]$_.BindingKind
            }, {
                [string]$_.CoordinatorInstanceId
            }, {
                [string]$_.Writer
            }, {
                [string]$_.SessionId
            }, {
                [string]$_.TransferId
            }, {
                [string]$_.Direction
            }, {
                [long]$_.StateGeneration
            }, {
                [string]$_.TransitionId
            }, {
                [string]$_.OperationKey
            }, {
                [string]$_.PeerProofAuthorityId
            }, {
                [long]$_.PeerProofGeneration
            } | ForEach-Object {
                ConvertTo-FileTransferCoordinatorSnapshotBindingSemanticValue `
                    -Value $_
            })
    $json = ConvertTo-Json -InputObject $normalized -Depth 32 -Compress
    [pscustomobject]@{
        Count = $normalized.Count
        Hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes (
            [Text.UTF8Encoding]::new($false).GetBytes($json))
    }
}

function Compare-FileTransferCoordinatorSnapshotEvaluations {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Full,
        [Parameter(Mandatory = $true)]$Compact
    )

    $reasons = [System.Collections.Generic.List[string]]::new()
    $fullFallbackTailBindingSummary =
        Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofBindingSemanticSummary `
            -Evaluation $Full
    $compactFallbackTailBindingSummary =
        Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofBindingSemanticSummary `
            -Evaluation $Compact
    foreach ($field in @(
            'Mode',
            'ContractVersion',
            'CatalogVersion',
            'CatalogHash',
            'StreamComplete',
            'ArtifactEvidenceVerdict',
            'TransitionCount',
            'EffectCount',
            'BoundaryCount',
            'BoundaryHash',
            'LegacySelectionCount',
            'RouteFamilyLegacySelectionCount',
            'RuntimeUnlockRetryFamilyLegacySelectionCount',
            'ReceiveRecoveryFamilyLegacySelectionCount',
            'FallbackTailYieldFamilyLegacySelectionCount',
            'RegularV4FrontierRepairPressureFamilyLegacySelectionCount',
            'ResponderProbeHandoffFamilyLegacySelectionCount')) {
        if ([string]$Full.$field -cne [string]$Compact.$field) {
            $reasons.Add("snapshot_full_compact_$($field.ToLowerInvariant())_mismatch")
        }
    }
    if ([long]$fullFallbackTailBindingSummary.Count -ne
        [long]$compactFallbackTailBindingSummary.Count) {
        $reasons.Add(
            'snapshot_full_compact_fallback_tail_peer_proof_binding_count_mismatch')
    }
    elseif ([string]$fullFallbackTailBindingSummary.Hash -cne
        [string]$compactFallbackTailBindingSummary.Hash) {
        $reasons.Add(
            'snapshot_full_compact_fallback_tail_peer_proof_binding_hash_mismatch')
    }

    [pscustomobject]@{
        Verdict = if ($reasons.Count -eq 0) { 'pass' } else { 'fail' }
        FailureReason = $reasons -join ';'
        FullMode = $Full.Mode
        CompactMode = $Compact.Mode
        FullBoundaryHash = $Full.BoundaryHash
        CompactBoundaryHash = $Compact.BoundaryHash
        FullBoundaryCount = $Full.BoundaryCount
        CompactBoundaryCount = $Compact.BoundaryCount
        FullTransitionCount = $Full.TransitionCount
        CompactTransitionCount = $Compact.TransitionCount
        FullEffectCount = $Full.EffectCount
        CompactEffectCount = $Compact.EffectCount
        FullFallbackTailPeerProofBindingCount =
            [long]$fullFallbackTailBindingSummary.Count
        CompactFallbackTailPeerProofBindingCount =
            [long]$compactFallbackTailBindingSummary.Count
        FullFallbackTailPeerProofBindingHash =
            [string]$fullFallbackTailBindingSummary.Hash
        CompactFallbackTailPeerProofBindingHash =
            [string]$compactFallbackTailBindingSummary.Hash
    }
}
