param([string]$RepoRoot, [string]$FixturePath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne
    '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') {
    throw 'Frozen catalog changed.'
}
$envelope = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
$first = $envelope
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$families = $catalog.RequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
function Copy-Value($value) {
    $value | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json
}
function Responder-Operations($value) {
    @($value.aggregate.operations | Where-Object {
            $_.family -ceq 'responder_probe_handoff'
        })
}
function Current($value) {
    @(Responder-Operations $value | Where-Object {
            $_.operationRole -ceq 'handoff_current'
        })[0]
}
function Terminal($value) {
    @(Responder-Operations $value | Where-Object {
            $_.operationRole -ceq 'handoff_terminal'
        })[0]
}
function Set-Both($value, [string]$field, $replacement) {
    foreach ($operation in @(Responder-Operations $value)) {
        $operation.responderProbeHandoff.$field = $replacement
    }
}
function Assert-Rejected($value, [string]$name) {
    if (Test-FileTransferCoordinatorSnapshotRepairReplayResponderScopeInvalidation `
            -Envelope $value) {
        throw "Malformed RepairReplay responder invalidation passed: $name"
    }
    $operations = @(Responder-Operations $value)
    if (@($operations | Where-Object {
                -not (Test-FileTransferCoordinatorSnapshotFamily6OperationCatalog `
                    -Envelope $value -Operation $_)
            }).Count -eq 0) {
        throw "Malformed RepairReplay responder catalog passed: $name"
    }
    $expectedVariant = if ($value.decisionKind -ceq 'RepairReplay') {
        'family6_catalog_omitted_atomic_repair_replay_scope_invalidation'
    }
    else { '(none)' }
    if ((Get-FileTransferCoordinatorSnapshotFamily6CatalogVariant `
            -Envelope $value -Operation $operations[0]) -cne
        $expectedVariant) {
        throw "Malformed RepairReplay variant was imprecise: $name"
    }
}

if ($script:FileTransferCoordinatorSnapshotFamily6DecisionKinds -contains
        'RepairReplay' -or
    -not (Test-FileTransferCoordinatorSnapshotRepairReplayResponderScopeInvalidation `
        -Envelope $envelope)) {
    throw 'Exact RepairReplay responder scope invalidation was not admitted narrowly.'
}
foreach ($operation in $envelope.aggregate.operations) {
    if (-not (Test-FileTransferCoordinatorSnapshotFamily5OperationCatalog `
            -Envelope $envelope -Operation $operation) -or
        -not (Test-FileTransferCoordinatorSnapshotFamily6OperationCatalog `
            -Envelope $envelope -Operation $operation)) {
        throw "Production operation rejected: $($operation.family)/$($operation.operationRole)"
    }
}

$mutations = [ordered]@{
    unrelated_decision = { param($e) $e.decisionKind =
        'StageRegularV4FrontierRepairPressureBinding' }
    wrong_envelope_reason = { param($e) $e.decisionReason = 'other' }
    rejected_decision = { param($e) $e.decisionStatus = 'Rejected' }
    unchanged_generation = { param($e)
        $e.aggregate.afterStateGeneration = $e.aggregate.beforeStateGeneration }
    unchanged_fingerprint = { param($e)
        $e.aggregate.afterStateFingerprint = $e.aggregate.beforeStateFingerprint }
    boundary_mutation = { param($e) $e.aggregate.afterBoundary.runtimeGeneration++ }
    missing_family5_subject = { param($e)
        $e.aggregate.operations = @($e.aggregate.operations | Where-Object {
                $_.operationRole -cne 'repair_authority_current'
            }) }
    zero_family5_operations = { param($e)
        $e.aggregate.operations = @($e.aggregate.operations | Where-Object {
                $_.family -cne 'regular_v4_frontier_repair_pressure'
            }) }
    non_v4_aggregate = { param($e)
        foreach ($boundary in @(
                $e.aggregate.beforeBoundary,
                $e.aggregate.afterBoundary)) {
            $boundary.route = 'post_tuna_fallback_v6'
            $boundary.protocolVersion = 6
            $boundary.routeCommitGeneration = 0
        } }
    family5_joint_scope_mutation = { param($e)
        foreach ($operation in @($e.aggregate.operations | Where-Object {
                    $_.family -ceq
                        'regular_v4_frontier_repair_pressure'
                })) {
            foreach ($boundary in @(
                    $operation.regularV4FrontierRepairPressure.sourceBoundary,
                    $operation.regularV4FrontierRepairPressure.effectiveBoundary)) {
                $boundary.route = 'regular_nkn_v4_fast'
            }
            $operation.regularV4FrontierRepairPressure.targetTransport =
                'RegularNkn'
        } }
    foreign_family = { param($e)
        $copy = Copy-Value (Current $e)
        $copy.family = 'receive_recovery'
        $e.aggregate.operations += $copy }
    wrong_current_state = { param($e) (Current $e).beforeState = 'None' }
    wrong_terminal_state = { param($e) (Terminal $e).afterState = 'Accepted' }
    wrong_authority = { param($e) (Terminal $e).authorityId = 'fte1-wrong' }
    wrong_generation = { param($e) (Terminal $e).operationGeneration++ }
    wrong_operation_key = { param($e) (Terminal $e).operationKey += '-wrong' }
    wrong_invalidation_reason = { param($e)
        (Terminal $e).responderProbeHandoff.decisionReason = 'wrong' }
    wrong_receipt_stage = { param($e)
        (Terminal $e).responderProbeHandoff.receipts[0].stage = 'handoff_bind' }
    wrong_receipt_status = { param($e)
        (Terminal $e).responderProbeHandoff.receipts[0].receiptStatus = 'Accepted' }
    unvalidated_receipt = { param($e)
        (Terminal $e).responderProbeHandoff.receipts[0].receiptValidated = $false }
    wrong_command = { param($e) (Terminal $e).commandFingerprint = 'fte1-wrong' }
    source_mutation = { param($e)
        (Terminal $e).responderProbeHandoff.sourceBoundary.checkpointGeneration++ }
    effective_mutation = { param($e)
        (Terminal $e).responderProbeHandoff.effectiveBoundary.runtimeGeneration++ }
    target_mutation = { param($e)
        (Terminal $e).responderProbeHandoff.targetBoundary.route =
            'post_tuna_fallback_v6' }
    data_session_mutation = { param($e)
        (Terminal $e).responderProbeHandoff.sourceBoundary.dataSessionGeneration++ }
    pair_mutation = { param($e)
        (Terminal $e).responderProbeHandoff.pairKey += '-wrong' }
    zero_transaction = { param($e) Set-Both $e transactionGeneration 0 }
    zero_offer = { param($e) Set-Both $e offerGeneration 0 }
    zero_pending_ack = { param($e) Set-Both $e pendingAnswerAckGeneration 0 }
    zero_payer = { param($e) Set-Both $e payerDecisionId 0 }
    zero_launch = { param($e) Set-Both $e launchGeneration 0 }
    zero_lease = { param($e) Set-Both $e tunaPathLeaseGeneration 0 }
    zero_probe_time = { param($e) Set-Both $e probeAckedUtcMs 0 }
    zero_admission = { param($e) Set-Both $e admissionGeneration 0 }
    zero_precommit = { param($e) Set-Both $e pendingPreCommitGeneration 0 }
    missing_listener = { param($e)
        Set-Both $e tunaPathLeaseListenerRunId '(none)' }
    missing_ack = { param($e) Set-Both $e ackMessageId '(none)' }
    missing_ack_fingerprint = { param($e)
        Set-Both $e ackSourceFingerprint '(none)' }
    missing_probe_fingerprint = { param($e)
        Set-Both $e peerProbeFrameFingerprint '(none)' }
    missing_probe = { param($e) Set-Both $e probeId '(none)' }
    missing_owner = { param($e) Set-Both $e ownerKey '(none)' }
    wrong_lanes = { param($e) Set-Both $e lanes 'screen' }
    wrong_probe_state = { param($e) Set-Both $e probeState 'Pending' }
    wrong_probe_transport = { param($e) Set-Both $e probeTransport 'RegularNkn' }
    initial_mode = { param($e)
        Set-Both $e transitionMode 'initial_regular_v4'
        Set-Both $e ownerKey '(none)'
        Set-Both $e pairKey '(none)'
        Set-Both $e pairedReleaseAuthorityId '(none)'
        Set-Both $e pairedReleaseGeneration 0
        Set-Both $e pairingGeneration 0
        Set-Both $e localProofIdentity '(none)'
        Set-Both $e localProofFingerprint '(none)'
        Set-Both $e peerProofIdentity '(none)'
        Set-Both $e peerProofFingerprint '(none)' }
    missing_pair = { param($e) Set-Both $e pairKey '(none)' }
    missing_release = { param($e)
        Set-Both $e pairedReleaseAuthorityId '(none)' }
    zero_release_generation = { param($e)
        Set-Both $e pairedReleaseGeneration 0 }
    zero_pairing_generation = { param($e) Set-Both $e pairingGeneration 0 }
    missing_local_proof = { param($e)
        Set-Both $e localProofIdentity '(none)' }
    missing_local_proof_fingerprint = { param($e)
        Set-Both $e localProofFingerprint '(none)' }
    missing_peer_proof = { param($e)
        Set-Both $e peerProofIdentity '(none)' }
    missing_peer_proof_fingerprint = { param($e)
        Set-Both $e peerProofFingerprint '(none)' }
    linked_none_with_generation = { param($e)
        Set-Both $e linkedTransitionGeneration 1 }
    linked_authority_without_generation = { param($e)
        Set-Both $e linkedTransitionAuthorityId 'fte1-linked' }
    duplicate_subject = { param($e)
        $e.aggregate.operations += Copy-Value (Terminal $e) }
}
foreach ($entry in $mutations.GetEnumerator()) {
    $value = Copy-Value $envelope
    & $entry.Value $value
    Assert-Rejected $value $entry.Key
}

# Exercise the complete shared evaluator without changing the frozen catalog.
$definitions = @('New-Effect', 'New-Lines')
$source = Join-Path $RepoRoot `
    'tests/nLink.SmokeTests.Core/Support/FileTunaPressureSettlementSnapshotEvidence.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $source, [ref]$null, [ref]$null)
foreach ($name in $definitions) {
    $matches = @($ast.FindAll({
                param($node)
                $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -ceq $name
            }, $true))
    if ($matches.Count -ne 1) { throw "Function not unique: $name" }
    . ([scriptblock]::Create(
            ($matches[0].Extent.Text -replace
                "function $name", "function script:$name")))
}
$lines = @(New-Lines @($envelope))
$full = Get-FileTransferCoordinatorSnapshotEvaluation `
    -Lines (@('event=unrelated') + $lines)
$compactLines = @($lines | Where-Object {
        $event = Get-FileTransferCoordinatorSnapshotEventName $_
        $catalog.SnapshotAuthorityV1 -contains $event -or
            $catalog.GenerationStampedEffects -contains $event
    })
$compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
if ((Compare-FileTransferCoordinatorSnapshotEvaluations `
        -Full $full -Compact $compact).Verdict -cne 'pass' -or
    $full.ArtifactEvidenceFailureReason -like
        '*snapshot_operation_catalog_mismatch*') {
    throw "Shared evaluator rejected the exact catalog shape: $($full.ArtifactEvidenceFailureReason)"
}

$malformed = Copy-Value $envelope
(Terminal $malformed).responderProbeHandoff.decisionReason = 'wrong'
$malformedResult = Get-FileTransferCoordinatorSnapshotEvaluation `
    -Lines (New-Lines @($malformed))
if ($malformedResult.ArtifactEvidenceFailureReason -notlike
        '*snapshot_operation_catalog_mismatch*' -or
    $malformedResult.FirstUnresolvedBoundary.variant -cne
        'family6_catalog_omitted_atomic_repair_replay_scope_invalidation') {
    throw "Malformed shared boundary did not expose the precise variant: $($malformedResult.ArtifactEvidenceFailureReason)"
}

'RepairReplay responder scope-replacement catalog controls passed.'
