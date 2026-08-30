Set-StrictMode -Version Latest

$script:FileTransferCoordinatorSnapshotContractVersion = 1
$script:FileTransferCoordinatorSnapshotCatalogVersion = 'snapshot-authority-v1'
$script:FileTransferCoordinatorSnapshotCatalogHash = '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2'
$script:FileTransferLegacyV0CatalogVersion = 'legacy-v0-pinned-20260825'
$script:FileTransferLegacyV0CatalogHash = 'a2ad4a9853c5a9551c98e59c11c5d71e1a35b0b5b0ff3229da1f4d6f295083ac'
$script:FileTransferCoordinatorEvidenceCatalogCache = $null
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
    param()

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
            'responderProbeHandoff')) {
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

function Test-FileTransferCoordinatorSnapshotFamily1OperationCatalog {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        [Parameter(Mandatory = $true)]$Operation
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
         -not $isExactFallbackCheckpointOperation) -or
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
        [string]$Envelope.decisionKind -notin
            $script:FileTransferCoordinatorSnapshotFamily2DecisionKinds -or
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
    if ([string]$Operation.beforeState -notin $states -or
        [string]$Operation.afterState -notin $states) {
        return $false
    }
    foreach ($receipt in @($Operation.runtimeUnlockRetry.receipts)) {
        $stage = [string]$receipt.stage
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

    if ([string]$yield.afterState -ceq 'YieldIssued') {
        return $family4Operations.Count -eq 1 -and
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

function Get-FileTransferCoordinatorSnapshotFamily4CatalogVariant {
    param([Parameter(Mandatory = $true)]$Envelope)

    if ([string]$Envelope.decisionKind -ceq 'FallbackTailQuiescent') {
        return 'fallback_tail_quiescent_event_omitted_from_family4_catalog'
    }
    return '(none)'
}

function Test-FileTransferCoordinatorSnapshotFallbackTailPeerProofEffect {
    param(
        [Parameter(Mandatory = $true)]$Entry,
        [Parameter(Mandatory = $true)]$OperationHistory,
        [Parameter(Mandatory = $true)]$Effects
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
        [string]$Entry.Boundary.decisionKind -ceq
            'FallbackTailQuiescent' -and
        [string]$Entry.Boundary.decisionStatus -ceq 'Accepted' -and
        [string]$operation.operationRole -ceq 'yield_current' -and
        [string]$operation.operationKind -ceq 'Quiescent' -and
        [string]$operation.beforeState -ceq 'None' -and
        [string]$operation.afterState -ceq 'PeerProofObserved'
    if (-not $isPeerFirstCarry) {
        return Test-FileTransferCoordinatorSnapshotExactEffectBinding `
            -Boundary $Entry.Boundary `
            -Operation $operation `
            -Effect $matchingEffects[0]
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
    foreach ($receipt in @($Operation.fallbackTailYield.receipts)) {
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
                -Left $retiredSettlement.regularV4FrontierRepairPressure.sourceBoundary `
                -Right $payload.sourceBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual `
                -Left $retiredSettlement.regularV4FrontierRepairPressure.effectiveBoundary `
                -Right $payload.effectiveBoundary)) {
            return ('snapshot_regular_v4_pressure_settlement_transition_invalid|' +
                'pressure_settlement_terminal_rollover_invalid')
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
        return ''
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
    return ''
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
        [string]$transition.retainedFrameFingerprint,
        [string]$transition.retainedFrameType,
        [string]$transition.targetBoundary.dataSessionIdentity,
        [long]$transition.targetBoundary.dataSessionGeneration,
        [long]$transition.targetBoundary.transportEpoch,
        $source) -join '|'
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
        $committedOperation = $commitEntries[0].Operation
        $retainedFingerprint =
            [string]$committedOperation.routeTransition.retainedFrameFingerprint
        $retainedFrameType =
            [string]$committedOperation.routeTransition.retainedFrameType
        if ($retainedFingerprint -cne '(none)' -and
            (-not (& $hasReceipt 'retained_frame' 'Applied') -or
             -not (& $hasEffect 'retained_frame_applied'))) {
            $successClosed = $false
        }
        if ($retainedFrameType -ceq 'filetransfer.frontier_request.v6' -and
            -not (& $hasEffect 'retained_frame_response')) {
            $successClosed = $false
        }
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
        [Parameter(Mandatory = $true)][object[]]$History
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
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Lines)

    $catalog = Get-FileTransferCoordinatorEvidenceCatalog
    $records = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        $eventName = Get-FileTransferCoordinatorSnapshotEventName -Line ([string]$Lines[$index])
        if ($catalog.SnapshotAuthorityV1 -notcontains $eventName -and
            $catalog.GenerationStampedEffects -notcontains $eventName) {
            continue
        }
        $records.Add([pscustomobject]@{
                LineIndex = $index
                Line = [string]$Lines[$index]
                EventName = $eventName
                CoordinatorInstanceId = Get-FileTransferCoordinatorSnapshotLineField -Line ([string]$Lines[$index]) -Name 'coordinator_instance_id'
            })
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
    $boundaries = [System.Collections.Generic.List[object]]::new()
    $effects = [System.Collections.Generic.List[object]]::new()
    $seenTransitions = @{}
    $seenEffects = @{}
    $seenOneShotEffectIdentities = @{}
    $streams = @{}
    $selectionModes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

    foreach ($record in @($records)) {
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
        if ($catalogVersion -ne $catalog.Version -or $catalogHash -ne $catalog.Hash) {
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
                if ([string]$envelope.decisionKind -ceq
                        'FallbackTailQuiescent' -and
                    [string]$envelope.decisionStatus -ceq 'Accepted' -and
                    -not (Test-FileTransferCoordinatorSnapshotFallbackTailQuiescentEnvelope `
                        -Envelope $envelope)) {
                    $errors.Add(
                        'snapshot_operation_catalog_mismatch|' +
                        'fallback_tail_quiescent_event_omitted_from_family4_catalog:' +
                        $record.LineIndex)
                    break
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
                    if (-not (Test-FileTransferCoordinatorSnapshotFamily1OperationCatalog `
                            -Envelope $envelope `
                            -Operation $operation)) {
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
                        $reason = 'snapshot_operation_catalog_mismatch'
                        if ($attemptVariant -cne '(none)') {
                            $reason = "$reason|$attemptVariant"
                        }
                        $errors.Add("$reason`:$($record.LineIndex)")
                        break
                    }
                    if (-not (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog `
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
                                -Envelope $envelope
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
                        $errors.Add("snapshot_operation_catalog_mismatch:$($record.LineIndex)")
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
                        foreach ($receipt in @($operation.fallbackTailYield.receipts)) {
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
                if (-not [string]::IsNullOrWhiteSpace(
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
                    evidenceLineIndex = [int]$record.LineIndex
                    writer = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'log_writer_id'
                    writerSequence = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'log_writer_sequence'
                    decisionUtcMilliseconds = $decisionUtcMilliseconds
                    beforeStateGeneration = $beforeGeneration
                    afterStateGeneration = $afterGeneration
                    beforeStateHash = $beforeHash
                    afterStateHash = $afterHash
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
                if ($effect.Family -ceq
                        'regular_v4_frontier_repair_pressure' -and
                    $effect.EffectKind -in @(
                        'pressure_settlement_bridge_applied',
                        'local_contract_settled',
                        'local_contract_reference_cleared')) {
                    $oneShotIdentity =
                        "$instanceId|$($effect.Family)|$($effect.EffectIdentity)"
                    if ($seenOneShotEffectIdentities.ContainsKey(
                            $oneShotIdentity)) {
                        $priorEffect = $seenOneShotEffectIdentities[
                            $oneShotIdentity]
                        if ([string]$priorEffect.TransitionId -cne
                            [string]$effect.TransitionId) {
                            $errors.Add(
                                'snapshot_authority_effect_identity_reused_across_transitions|' +
                                'route_commit_reemitted_already_applied_pressure_settlement_lifecycle:' +
                                $record.LineIndex)
                        }
                    }
                    else {
                        $seenOneShotEffectIdentities[$oneShotIdentity] =
                            $effect
                    }
                }
                $effects.Add($effect) | Out-Null
            }
        }
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
                [string]$effect.EffectKind) {
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
        if ($matches.Count -ne 1) {
            $errors.Add("snapshot_authority_effect_unbound:$($effect.LineIndex)")
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

    $operationHistory = @(
        foreach ($boundary in @($boundaries)) {
            foreach ($operation in @($boundary.operations)) {
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
        if ($family -ceq 'route_transition') {
            $isBootstrap = [string]$latest.Operation.operationRole -ceq
                'fallback_v6_bootstrap'
            if ($isBootstrap) {
                $bootstrapLifecycle =
                Get-FileTransferCoordinatorFallbackV6BootstrapLifecycle `
                    -History $history
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
                if ($liveRouteEpoch -le 0 -or
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

    $responderEntries = @($operationHistory | Where-Object {
            [string]$_.Operation.family -ceq 'responder_probe_handoff'
        })
    foreach ($lineageGroup in @($responderEntries | Group-Object {
                $payload = $_.Operation.responderProbeHandoff
                $source = ConvertTo-Json -InputObject $payload.sourceBoundary `
                    -Depth 16 -Compress
                @(
                    [string]$_.Boundary.coordinatorInstanceId,
                    [string]$payload.transitionMode,
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
                    [string]$payload.pairKey,
                    [string]$payload.pairedReleaseAuthorityId,
                    $source) -join '|'
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
            if ($valid -and $hasPeer) {
                $valid =
                    Test-FileTransferCoordinatorSnapshotFallbackTailPeerProofEffect `
                        -Entry $entry `
                        -OperationHistory @($operationHistory) `
                        -Effects @($effects)
                if (-not $valid -and
                    [string]$entry.Boundary.decisionKind -ceq
                        'FallbackTailQuiescent' -and
                    [string]$operation.afterState -ceq
                        'PeerProofObserved') {
                    $failureVariant =
                        'fallback_tail_quiescent_peer_proof_carry_invalid'
                }
            }
            if ($valid -and $hasPair) {
                $valid = & $hasReceiptEffect 'paired_release' 'paired_release_published'
            }
            if ($valid -and [string]$typed.yieldKind -ceq 'Quiescent') {
                if ([string]$entry.Boundary.decisionKind -ceq
                    'FallbackTailQuiescent') {
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
        $sourceBoundary = $history[0].Operation.runtimeUnlockRetry.sourceBoundary
        $exactEffects = @($effects | Where-Object {
                [string]$_.CoordinatorInstanceId -ceq $instanceId -and
                [string]$_.AuthorityId -ceq $authorityId
            })
        $missingRequiredReceiptEffect = $null
        foreach ($entry in $history) {
            foreach ($receipt in @($entry.Operation.runtimeUnlockRetry.receipts)) {
                if (-not [bool]$receipt.receiptValidated) {
                    continue
                }
                $requiredEffectKind = switch -Exact ([string]$receipt.stage) {
                    'stage' { 'dispatch_stage_observed'; break }
                    'install' { 'install_committed'; break }
                    'execution' {
                        if ([bool]$entry.Operation.runtimeUnlockRetry.dispatchObserved) {
                            'execution_observed'
                        }
                        break
                    }
                    'settlement' { 'settlement_observed'; break }
                    default { '' }
                }
                if ([string]::IsNullOrWhiteSpace($requiredEffectKind)) {
                    continue
                }
                $matchingEffects = @($exactEffects | Where-Object {
                        [string]$_.EffectKind -ceq $requiredEffectKind -and
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
            $candidate.Boundary.taxonomyClass =
                if ($missingRequiredReceiptEffect.Stage -in @('stage', 'install')) {
                    'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete'
                }
                else {
                    'runtime_unlock_retry_dispatch_contract_unsettled'
                }
            $candidate.Boundary.domain = 'route_runtime'
            $candidate.Boundary.phase = 'runtime_unlock_retry'
            $candidate.Boundary.rawCount = 1
            $candidate.Boundary.distinctCount = 1
            $candidate.Boundary.recoveredCount = 0
            $candidate.Boundary.unresolvedCount = 1
            $candidate.Boundary.verdict = 'fail'
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

    $receiveRecoveryEntries = @($operationHistory | Where-Object {
            [string]$_.Operation.family -ceq 'receive_recovery' -and
            [string]$_.Operation.operationRole -ne 'attempted_decision'
        })
    foreach ($authorityGroup in @($receiveRecoveryEntries | Group-Object {
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

        $operationEntries = @($history | Where-Object {
                [string]$_.Operation.operationRole -in @(
                    'operation_current', 'operation_terminal')
            })
        $latestOperation = $operationEntries | Select-Object -Last 1
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
                        [string]$_.Operation.afterState -in @(
                            'Issued', 'Running', 'Completed')
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
                        $typedCancellation = @($history | Where-Object {
                                [string]$_.Boundary.boundaryId -ceq
                                    [string]$closure.Boundary.boundaryId -and
                                [string]$_.Operation.operationRole -in @(
                                    'operation_current', 'operation_terminal') -and
                                [string]$_.Operation.authorityId -ceq
                                    [string]$arm.Operation.authorityId -and
                                [long]$_.Operation.operationGeneration -eq
                                    [long]$arm.Operation.operationGeneration -and
                                (([string]$_.Operation.receiveRecovery.proofIdentity -notin
                                      @('', '(none)')) -or
                                 ([string]$_.Operation.afterState -like
                                      'Invalidated*')) -and
                                (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                                    -Left $_.Operation.receiveRecovery.sourceBoundary `
                                    -Right $arm.Operation.receiveRecovery.sourceBoundary) -and
                                (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                                    -Left $_.Operation.receiveRecovery.effectiveBoundary `
                                    -Right $arm.Operation.receiveRecovery.effectiveBoundary)
                            })
                        $closed = $typedCancellation.Count -eq 1
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
            families = if ([string]$firstFailure.Family -in @('', '(none)')) {
                @()
            }
            else {
                @([string]$firstFailure.Family)
            }
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

function Compare-FileTransferCoordinatorSnapshotEvaluations {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Full,
        [Parameter(Mandatory = $true)]$Compact
    )

    $reasons = [System.Collections.Generic.List[string]]::new()
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
    }
}
