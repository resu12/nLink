param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') { throw 'Frozen catalog changed.' }
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
$first = $envelopes[0]
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$families = $catalog.RequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
function New-Effect($envelope, $operation, [string]$kind, [long]$sequence, $receipt = $null) {
    $scope = switch -Exact ($operation.family) {
        'regular_v4_frontier_repair_pressure' { $operation.regularV4FrontierRepairPressure.sourceBoundary }
        'responder_probe_handoff' { $operation.responderProbeHandoff.sourceBoundary }
        'route_transition' { $operation.routeTransition.sourceBoundary }
        'route_runtime' { $operation.routeRuntime.boundary }
        'receive_recovery' { $operation.receiveRecovery.sourceBoundary }
        'runtime_unlock_retry_lifecycle' { $operation.runtimeUnlockRetry.sourceBoundary }
        'fallback_tail_yield' { $operation.fallbackTailYield.sourceBoundary }
    }
    if ($null -eq $receipt) { $receipt = $operation }
    "event=filetransfer_coordinator_authority_effect; $prefix; transition_id=$($envelope.transitionId); coordinator_state_generation=$($envelope.aggregate.afterStateGeneration); operation_family=$($operation.family); operation_key=$($operation.operationKey); operation_authority_id=$($operation.authorityId); operation_generation=$($operation.operationGeneration); effect_kind=$kind; effect_identity=$($operation.operationKey):$kind; effect_generation=$($receipt.receiptGeneration); receipt_identity=$($receipt.receiptIdentity); receipt_generation=$($receipt.receiptGeneration); receipt_status=$($receipt.receiptStatus); receipt_fingerprint=$($receipt.receiptFingerprint); route=$($scope.route); protocol_version=$($scope.protocolVersion); live_route_epoch=$($scope.liveRouteEpoch); route_publication_generation=$($scope.routePublicationGeneration); route_commit_generation=$($scope.routeCommitGeneration); runtime_generation=$($scope.runtimeGeneration); terminal_generation=$($scope.terminalGeneration); leg_generation=$($scope.legGeneration); transport_epoch=$($scope.transportEpoch); bridge_recovery_generation=$($scope.bridgeRecoveryGeneration); checkpoint_generation=$($scope.checkpointGeneration); checkpoint_request_id=$($scope.checkpointRequestId); data_session_identity=$($scope.dataSessionIdentity); data_session_generation=$($scope.dataSessionGeneration); authority_effect=applied; log_writer_id=fixture-writer; log_writer_sequence=$sequence"
}
function New-Lines([object[]]$envelopes) {
$lines = [Collections.Generic.List[string]]::new()
$lines.Add("event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=$($first.aggregate.beforeStateGeneration); initial_state_hash=$($first.aggregate.beforeStateFingerprint); $coverage; authority_effect=none")
for ($i=0; $i -lt $envelopes.Count; $i++) {
    $envelope = $envelopes[$i]
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($envelope | ConvertTo-Json -Depth 64 -Compress))
    $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
    $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
    $sequence = 20L*($i+1)
    $lines.Add("event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=$($envelope.transitionId); before_state_generation=$($envelope.aggregate.beforeStateGeneration); after_state_generation=$($envelope.aggregate.afterStateGeneration); snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none; log_writer_id=fixture-writer; log_writer_sequence=$sequence")
    $role = $null
    $kinds = @()
    switch -Exact ($envelope.decisionKind) {
        'StageRegularV4FrontierRepairPressureBinding' { $role='attempted_decision'; $kinds=@('pressure_install_staged') }
        'CommitRegularV4FrontierRepairPressureBinding' { $role='pressure_binding_installed'; $kinds=@('pressure_install_bridge_result','repair_contract_bound') }
        'PrepareRegularV4FrontierRepairPressureSettlement' { $role='pressure_settlement_prepared'; $kinds=@('pressure_settlement_prepared') }
        'CommitRegularV4FrontierRepairPressureSettlement' { $role='pressure_settlement_terminal'; $kinds=@('pressure_settlement_bridge_applied','local_contract_settled','local_contract_reference_cleared') }
        'PrepareRegularV4FrontierRepairPressureRecoveryHandoff' { $role='recovery_handoff_prepared'; $kinds=@('recovery_handoff_prepared') }
        'ObserveReceiveRecoveryExecutionStart' { $role='operation_current'; $kinds=@('execution_started') }
        'FallbackCheckpointRequested' { $role='repair_authority_terminal'; $kinds=@('regular_v4_pressure_decision_adopted') }
        'FallbackCheckpointAccepted' { $role='repair_authority_terminal'; $kinds=@('regular_v4_pressure_decision_adopted') }
    }
    if ($envelope.decisionKind -in @('FallbackCheckpointRequested','FallbackCheckpointAccepted') -and
        @($envelope.aggregate.operations | Where-Object family -CEQ 'regular_v4_frontier_repair_pressure').Count -eq 0) { $role = $null }
    if ($null -ne $role) {
        $matches = @($envelope.aggregate.operations | Where-Object { $_.operationRole -ceq $role -and $_.afterState -cne 'None' })
        if ($matches.Count -ne 1 -or -not $matches[0].receiptValidated) { throw "Effect subject not exact: $role; $($envelope.aggregate.operations | ConvertTo-Json -Depth 16 -Compress)" }
        foreach ($kind in $kinds) { $sequence++; $lines.Add((New-Effect $envelope $matches[0] $kind $sequence)) }
    }
    foreach ($operation in $envelope.aggregate.operations) {
        $kinds = @()
        if ($envelope.decisionKind -ceq 'CommitRegularV4FrontierRepairPressureRecoveryHandoff' -and
            $operation.family -ceq 'regular_v4_frontier_repair_pressure' -and $operation.beforeState -ceq 'None') {
            if ($operation.operationRole -ceq 'pressure_settlement_terminal') { $kinds=@('pressure_settlement_bridge_applied','local_contract_settled','local_contract_reference_cleared') }
            elseif ($operation.operationRole -ceq 'recovery_handoff_terminal') { $kinds=@('recovery_handoff_committed','projection_detached') }
        }
        elseif ($operation.family -ceq 'regular_v4_frontier_repair_pressure' -and
            $envelope.decisionKind -ceq 'CommitFileTransferRouteTransition' -and
            $operation.operationRole -ceq 'repair_authority_terminal' -and $operation.afterState -ceq 'Applied') {
            $kinds = @('route_commit_local_lifecycle_adopted')
        }
        elseif ($operation.family -ceq 'responder_probe_handoff') {
            $kinds = @(if ($operation.operationRole -ceq 'route_transition_handoff_pending') { 'route_transition_handoff_supplied' }
                elseif ($operation.receiptStatus -ceq 'Invalidated') { 'handoff_invalidated' } else { 'convergence_observed' })
        }
        elseif ($operation.family -ceq 'route_runtime' -and $operation.operationRole -ceq 'route_publication' -and $operation.afterState -ceq 'Published') {
            $kinds = @('route_publication_forwarded')
        }
        elseif ($operation.family -ceq 'route_runtime' -and $operation.operationRole -ceq 'route_commit' -and $operation.afterState -ceq 'Committed') {
            $kinds = @('route_commit_adopted','target_runtime_leg_installed')
        }
        elseif ($operation.family -ceq 'runtime_unlock_retry_lifecycle' -and
            $operation.operationRole -ceq 'scope_invalidation' -and $operation.afterState -ceq 'Applied') {
            $kinds = @('scope_invalidation_applied')
        }
        elseif ($operation.family -ceq 'runtime_unlock_retry_lifecycle' -and
            $envelope.decisionKind -ceq 'ObserveRuntimeUnlockRetryDispatchStage' -and
            $operation.operationRole -ceq 'attempt_pending') {
            $receipt = @($operation.runtimeUnlockRetry.receipts | Where-Object stage -CEQ 'stage')
            if ($receipt.Count -eq 1) {
                $sequence++
                $lines.Add((New-Effect $envelope $operation 'dispatch_stage_observed' $sequence $receipt[0]))
            }
        }
        elseif ($operation.family -ceq 'runtime_unlock_retry_lifecycle' -and
            $envelope.decisionKind -ceq 'CommitRuntimeUnlockRetryDispatchInstall' -and
            $operation.operationRole -ceq 'attempt_installed' -and $operation.afterState -ceq 'Installed') {
            $receipt = @($operation.runtimeUnlockRetry.receipts | Where-Object stage -CEQ 'install')
            if ($receipt.Count -eq 1) {
                $sequence++
                $lines.Add((New-Effect $envelope $operation 'install_committed' $sequence $receipt[0]))
            }
        }
        elseif ($operation.family -ceq 'fallback_tail_yield' -and
            $operation.operationRole -ceq 'yield_current') {
            $stageKinds = switch -Exact ($envelope.decisionKind) {
                'FallbackTailQuiescent' { @{ yield = @('recovery_owner_retired_after_yield') } }
                'ApplyFallbackTailLocalProofReceipt' { @{ local_proof = @('local_proof_sent','local_proof_applied') } }
                'ApplyFallbackTailPeerProofReceipt' { @{ peer_proof = @('peer_proof_observed'); paired_release = @('paired_release_published') } }
            }
            if ($null -ne $stageKinds) {
                foreach ($stage in $stageKinds.Keys) {
                    $receipt = @($operation.fallbackTailYield.receipts | Where-Object stage -CEQ $stage)
                    if ($receipt.Count -eq 1) {
                        foreach ($kind in $stageKinds[$stage]) {
                            $sequence++
                            $lines.Add((New-Effect $envelope $operation $kind $sequence $receipt[0]))
                        }
                    }
                }
            }
        }
        elseif ($operation.family -ceq 'route_transition') {
            if ($envelope.decisionKind -ceq 'BeginFileTransferRouteTransitionPhysicalDrain' -and $operation.operationRole -ceq 'current') { $kinds=@('pause_installed') }
            elseif ($envelope.decisionKind -ceq 'CommitFileTransferRouteTransition') {
                if ($operation.operationRole -ceq 'terminal' -and $operation.beforeState -ceq 'None' -and $operation.afterState -ceq 'Committed') { $kinds=@('route_commit_adopted','target_runtime_leg_installed') }
                elseif ($operation.operationRole -ceq $envelope.direction) { $kinds=@('completed_drain_receipt') }
            }
            elseif ($envelope.decisionKind -ceq 'ApplyFileTransferRouteTransitionDispositionReceipt' -and $operation.operationRole -ceq $envelope.direction) {
                $kinds=@('route_transition_disposition_applied','transition_pause_released','recovered_epoch_publication')
            }
            elseif ($envelope.decisionKind -ceq 'ApplyFileTransferRouteTransitionRetainedFrameReceipt' -and $operation.operationRole -ceq $envelope.direction) {
                $kinds=@('retained_frame_response','retained_frame_applied')
            }
        }
        foreach ($kind in $kinds) { $sequence++; $lines.Add((New-Effect $envelope $operation $kind $sequence)) }
    }
}
$last = $envelopes[-1]
$lines.Add("event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=$($last.aggregate.afterStateGeneration); final_state_hash=$($last.aggregate.afterStateFingerprint); transition_count=$($envelopes.Count); $coverage; authority_effect=none")
$lines.ToArray()
}
$lines = @(New-Lines $envelopes)
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$null,[ref]$null)
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification','Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definitions = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if ($definitions.Count -ne 1) { throw "GUI function not unique: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
function Assert-Parity([string[]]$inputLines, [bool]$missingClosure) {
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated_diagnostic')+$inputLines)
    $compactLines = @($inputLines | Where-Object {
        $name = Get-FileTransferCoordinatorSnapshotEventName -Line $_
        $catalog.SnapshotAuthorityV1 -contains $name -or $catalog.GenerationStampedEffects -contains $name
    })
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact).Verdict -cne 'pass') { throw 'Full/compact parity failed.' }
    if ($full.LegacySelectionCount -ne 0 -or
        ($null -ne $full.FirstUnresolvedBoundary) -ne $missingClosure -or
        (-not $missingClosure -and $full.ArtifactEvidenceVerdict -cne 'pass')) {
        $catalogFailures = @($full.ArtifactEvidenceFailureReason -split ';' | Where-Object { $_ -match 'snapshot_operation_catalog_mismatch:(\d+)$' } | ForEach-Object {
            $index = [int]($_ -replace '^.*:', '')
            $badLine = (@('event=unrelated_diagnostic') + $inputLines)[$index]
            $e = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField -Line $badLine -Name snapshot_payload)).Value
            foreach ($op in $e.aggregate.operations) {
                foreach ($family in 1..6) {
                    if (-not (& "Test-FileTransferCoordinatorSnapshotFamily${family}OperationCatalog" -Envelope $e -Operation $op)) {
                        "$($e.decisionKind):$($op.operationRole):$($op.beforeState)->$($op.afterState)"
                    }
                }
            }
        })
        throw "Tuna settlement contract failed: $($full.ArtifactEvidenceFailureReason); catalog=$($catalogFailures -join ','); first=$($full.FirstUnresolvedBoundary | Select-Object * -ExcludeProperty operations | ConvertTo-Json -Depth 4 -Compress)"
    }
    $path = Join-Path $OutputRoot 'gui-input.log'
    [IO.File]::WriteAllLines($path,$compactLines)
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
    if ($missingClosure) {
        $boundary = $full.FirstUnresolvedBoundary
        if ($gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or
            $gui.CoordinatorWriter -cne $boundary.writer -or $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or
            $gui.CoordinatorRawCount -ne $boundary.rawCount -or $gui.CoordinatorUnresolvedCount -ne $boundary.unresolvedCount) { throw 'GUI boundary differs.' }
    }
    elseif ($null -ne $gui) { throw 'GUI rejected the exact settlement.' }
}
$retainedHandoffFixture = @($envelopes | Where-Object decisionKind -ceq 'CommitRegularV4FrontierRepairPressureRecoveryHandoff').Count -gt 0
Assert-Parity $lines $false
Assert-Parity (@($lines[0]) + $lines[1..($lines.Count-2)] + $lines[1..($lines.Count-2)] + @($lines[-1])) $false
Assert-Parity @($lines | Where-Object { $_ -notmatch 'effect_kind=local_contract_reference_cleared;' }) $true
if (@($envelopes | Where-Object decisionKind -ceq 'CommitRegularV4FrontierRepairPressureSettlement').Count -eq 2) {
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/CrossRoutePressureTerminalRolloverControls.ps1')
}
if ($retainedHandoffFixture) {
    . (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/RetainedHandoffReceiptControls.ps1')
}
'File-Tuna Coordinator/projector/full/compact/GUI settlement contract passed.'
