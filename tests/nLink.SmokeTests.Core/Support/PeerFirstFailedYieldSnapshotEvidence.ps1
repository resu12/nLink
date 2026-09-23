param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')

$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne
    '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') {
    throw 'Frozen catalog changed.'
}
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
if ($envelopes.Count -ne 4) {
    throw "Unexpected fixture envelope count: $($envelopes.Count)."
}
$origin = $envelopes[0]
$accepted = $envelopes[1]
$idempotent = $envelopes[2]
$rejected = $envelopes[3]

function Copy-Value($value) {
    $value | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json
}
function Subject($envelope, [string]$role) {
    $subjects = @($envelope.aggregate.operations | Where-Object {
            [string]$_.operationRole -ceq $role
        })
    if ($subjects.Count -ne 1) { throw "Fixture subject not unique: $role" }
    return $subjects[0]
}

foreach ($envelope in @($accepted, $idempotent, $rejected)) {
    if (-not (Test-FileTransferCoordinatorSnapshotFallbackTailFailedEnvelope `
            $envelope)) {
        throw "Valid Failed boundary was rejected: $($envelope.decisionStatus)"
    }
    foreach ($operation in @($envelope.aggregate.operations)) {
        if (-not (Test-FileTransferCoordinatorSnapshotFamily4OperationCatalog `
                $envelope $operation)) {
            throw "Valid Failed operation was rejected: $($envelope.decisionStatus)"
        }
    }
}

$acceptedCurrent = Subject $accepted 'yield_current'
$acceptedPending = Subject $accepted 'peer_proof_pending_initial'
if ([string]$acceptedCurrent.operationKind -cne 'Failed' -or
    [string]$acceptedCurrent.beforeState -cne 'None' -or
    [string]$acceptedCurrent.afterState -cne 'PeerProofObserved' -or
    [string]$acceptedPending.beforeState -cne 'Pending' -or
    [string]$acceptedPending.afterState -cne 'None') {
    throw 'Production fixture did not contain the peer-first Failed shape.'
}

foreach ($mutation in @(
    { param($e,$p,$y) $e.decisionStatus = 'Idempotent' },
    { param($e,$p,$y) $e.aggregate.afterStateGeneration++ },
    { param($e,$p,$y) $e.aggregate.afterStateFingerprint = $e.aggregate.beforeStateFingerprint },
    { param($e,$p,$y) $e.aggregate.operations = @($y) },
    { param($e,$p,$y) $e.aggregate.operations += Copy-Value $p },
    { param($e,$p,$y) $p.operationKey += '-wrong' },
    { param($e,$p,$y) $p.authorityId = $y.authorityId },
    { param($e,$p,$y) $p.fallbackTailYield.ownerKey = 'wrong' },
    { param($e,$p,$y) $p.fallbackTailYield.peerProofAuthorityId = 'wrong'; $y.fallbackTailYield.peerProofAuthorityId = 'wrong' },
    { param($e,$p,$y) $p.fallbackTailYield.peerProofYieldGeneration++; $y.fallbackTailYield.peerProofYieldGeneration++ },
    { param($e,$p,$y) $p.fallbackTailYield.peerProofIdentity = 'wrong'; $y.fallbackTailYield.peerProofIdentity = 'wrong' },
    { param($e,$p,$y) $p.fallbackTailYield.peerProofFingerprint = 'wrong' },
    { param($e,$p,$y) $p.fallbackTailYield.receipts = @() },
    { param($e,$p,$y) $y.fallbackTailYield.receipts = @($y.fallbackTailYield.receipts | Where-Object stage -CNE 'peer_proof') },
    { param($e,$p,$y) $y.fallbackTailYield.sourceBoundary.dataSessionGeneration++ },
    { param($e,$p,$y) $y.fallbackTailYield.pairedReleaseAuthorityId = 'forged' },
    { param($e,$p,$y) $y.fallbackTailYield.failedSupersessionCarriedPeerProof = $true }
)) {
    $bad = Copy-Value $accepted
    & $mutation $bad (Subject $bad 'peer_proof_pending_initial') (Subject $bad 'yield_current')
    if (Test-FileTransferCoordinatorSnapshotFallbackTailFailedEnvelope $bad) {
        throw "Malformed peer-first Failed boundary was admitted: $mutation"
    }
}

$local = Copy-Value $accepted
$localCurrent = Subject $local 'yield_current'
$local.aggregate.operations = @($localCurrent)
$localCurrent.afterState = 'YieldIssued'
$localCurrent.fallbackTailYield.pairingState = 'YieldIssued'
$localCurrent.fallbackTailYield.peerProofAuthorityId = '(none)'
$localCurrent.fallbackTailYield.peerProofYieldGeneration = 0
$localCurrent.fallbackTailYield.peerProofIdentity = '(none)'
$localCurrent.fallbackTailYield.peerProofFingerprint = '(none)'
$localCurrent.fallbackTailYield.receipts = @(
    $localCurrent.fallbackTailYield.receipts | Where-Object {
        [string]$_.stage -ceq 'yield'
    })
$localReceipt = $localCurrent.fallbackTailYield.receipts[0]
foreach ($field in @(
        'commandIdentity', 'commandGeneration', 'commandFingerprint',
        'receiptIdentity', 'receiptGeneration', 'receiptStatus',
        'receiptFingerprint', 'receiptValidated')) {
    $localCurrent.$field = $localReceipt.$field
}
if (-not (Test-FileTransferCoordinatorSnapshotFallbackTailFailedEnvelope $local)) {
    throw 'Valid local Failed boundary was rejected.'
}
foreach ($field in @('ownerKey', 'pairKey')) {
    $bad = Copy-Value $local
    $bad.aggregate.operations[0].fallbackTailYield.$field = '(none)'
    if (Test-FileTransferCoordinatorSnapshotFallbackTailFailedEnvelope $bad) {
        throw "Local Failed boundary admitted sentinel $field."
    }
}

foreach ($neutral in @($idempotent, $rejected)) {
    if (@($neutral.aggregate.changedFamilies).Count -ne 1 -or
        [string]$neutral.aggregate.changedFamilies[0] -cne
            'fallback_tail_yield') {
        throw 'State-neutral Failed attempt did not declare its projected family.'
    }
    foreach ($mutation in @(
        { param($e,$o) $e.aggregate.afterStateGeneration++ },
        { param($e,$o) $e.aggregate.afterStateFingerprint = 'changed' },
        { param($e,$o) $e.aggregate.changedFamilies = @() },
        { param($e,$o) $o.operationRole = 'yield_current' },
        { param($e,$o) $o.operationKind = 'FallbackTailQuiescent' },
        { param($e,$o) $o.operationKey += '-wrong' },
        { param($e,$o) $o.stateApplied = $true },
        { param($e,$o) $o.fallbackTailYield.role = 'yield_current' },
        { param($e,$o) $o.fallbackTailYield.receipts[0].stage = 'yield' },
        { param($e,$o) $o.fallbackTailYield.receipts[0].receiptStatus = 'Accepted' },
        { param($e,$o) $o.fallbackTailYield.sourceBoundary.dataSessionGeneration++ }
    )) {
        $bad = Copy-Value $neutral
        & $mutation $bad $bad.aggregate.operations[0]
        if (Test-FileTransferCoordinatorSnapshotFallbackTailFailedEnvelope $bad) {
            throw "Malformed $($neutral.decisionStatus) attempt was admitted: $mutation"
        }
    }
}

$syntheticRejected = Copy-Value $rejected
$syntheticAttempt = $syntheticRejected.aggregate.operations[0]
$syntheticAttempt.beforeState = 'None'
$syntheticAttempt.afterState = 'None'
$syntheticAttempt.fallbackTailYield.yieldKind = 'Attempt'
$syntheticAttempt.fallbackTailYield.pairingState = 'Rejected'
foreach ($field in @(
        'ownerKey', 'pairKey', 'localProofIdentity',
        'localProofFingerprint', 'peerProofAuthorityId',
        'peerProofIdentity', 'peerProofFingerprint',
        'pairedReleaseAuthorityId', 'pairedReleaseOutcome',
        'failedPredecessorAuthorityId', 'reservedRetryAuthorityId')) {
    $syntheticAttempt.fallbackTailYield.$field = '(none)'
}
foreach ($field in @(
        'peerProofYieldGeneration', 'pairingGeneration',
        'pairedReleaseGeneration', 'reauthorizationGeneration',
        'reservationGeneration', 'consumingInstallGeneration',
        'retryHoldGeneration', 'retryRecheckGeneration')) {
    $syntheticAttempt.fallbackTailYield.$field = 0
}
$syntheticAttempt.fallbackTailYield.reauthorizationSourceTransportEpoch =
    $syntheticAttempt.fallbackTailYield.sourceBoundary.transportEpoch
$syntheticAttempt.fallbackTailYield.reauthorizationTargetTransportEpoch =
    $syntheticAttempt.fallbackTailYield.sourceBoundary.transportEpoch
$syntheticAttempt.fallbackTailYield.invalidationReason =
    $syntheticRejected.decisionReason
if (-not (Test-FileTransferCoordinatorSnapshotFallbackTailFailedEnvelope `
        $syntheticRejected)) {
    throw 'Valid synthetic rejected Failed attempt was rejected.'
}
foreach ($mutation in @(
    { param($o) $o.fallbackTailYield.peerProofIdentity = 'forged' },
    { param($o) $o.fallbackTailYield.pairedReleaseGeneration = 1 },
    { param($o) $o.fallbackTailYield.invalidationReason = 'wrong' }
)) {
    $bad = Copy-Value $syntheticRejected
    & $mutation $bad.aggregate.operations[0]
    if (Test-FileTransferCoordinatorSnapshotFallbackTailFailedEnvelope $bad) {
        throw "Malformed synthetic rejected Failed attempt was admitted: $mutation"
    }
}

$first = $origin
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$families = $catalog.RequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
function New-Effect($envelope, $operation, [string]$kind, [long]$sequence) {
    $scope = $operation.fallbackTailYield.sourceBoundary
    "event=filetransfer_coordinator_authority_effect; $prefix; transition_id=$($envelope.transitionId); coordinator_state_generation=$($envelope.aggregate.afterStateGeneration); operation_family=$($operation.family); operation_key=$($operation.operationKey); operation_authority_id=$($operation.authorityId); operation_generation=$($operation.operationGeneration); effect_kind=$kind; effect_identity=$kind-$sequence; effect_generation=$($operation.receiptGeneration); receipt_identity=$($operation.receiptIdentity); receipt_generation=$($operation.receiptGeneration); receipt_status=$($operation.receiptStatus); receipt_fingerprint=$($operation.receiptFingerprint); route=$($scope.route); protocol_version=$($scope.protocolVersion); live_route_epoch=$($scope.liveRouteEpoch); route_publication_generation=$($scope.routePublicationGeneration); route_commit_generation=$($scope.routeCommitGeneration); runtime_generation=$($scope.runtimeGeneration); terminal_generation=$($scope.terminalGeneration); leg_generation=$($scope.legGeneration); transport_epoch=$($scope.transportEpoch); bridge_recovery_generation=$($scope.bridgeRecoveryGeneration); checkpoint_generation=$($scope.checkpointGeneration); checkpoint_request_id=$($scope.checkpointRequestId); data_session_identity=$($scope.dataSessionIdentity); data_session_generation=$($scope.dataSessionGeneration); authority_effect=applied; log_writer_id=fixture-writer; log_writer_sequence=$sequence"
}
function Convert-Envelope($envelope, [long]$sequence) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(
        ($envelope | ConvertTo-Json -Depth 64 -Compress))
    $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
    $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
    "event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=$($envelope.transitionId); before_state_generation=$($envelope.aggregate.beforeStateGeneration); after_state_generation=$($envelope.aggregate.afterStateGeneration); snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none; log_writer_id=fixture-writer; log_writer_sequence=$sequence"
}
function New-Lines(
    [object[]]$values,
    [string]$effectCase = '') {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=$($values[0].aggregate.beforeStateGeneration); initial_state_hash=$($values[0].aggregate.beforeStateFingerprint); $coverage; authority_effect=none; log_writer_id=fixture-writer; log_writer_sequence=1")
    for ($index = 0; $index -lt $values.Count; $index++) {
        $envelope = $values[$index]
        $sequence = 10L * ($index + 1L)
        $lines.Add((Convert-Envelope $envelope $sequence))
        if ($index -eq 0 -and
            [string]$envelope.decisionKind -ceq
                'ApplyFallbackTailPeerProofReceipt' -and
            $effectCase -cne 'missing-origin') {
            $lines.Add((New-Effect $envelope (Subject $envelope 'peer_proof_pending_initial') 'peer_proof_observed' ($sequence + 1L)))
        }
        if ($index -eq 1 -and $effectCase -ceq 'copied-peer') {
            $lines.Add((New-Effect $envelope (Subject $envelope 'yield_current') 'peer_proof_observed' ($sequence + 1L)))
        }
        if ($index -eq 1 -and $effectCase -ceq 'copied-recovery') {
            $lines.Add((New-Effect $envelope (Subject $envelope 'yield_current') 'recovery_owner_retired_after_yield' ($sequence + 1L)))
        }
        if ($index -eq ($values.Count - 1) -and
            $effectCase -ceq 'neutral-effect') {
            $lines.Add((New-Effect $envelope $envelope.aggregate.operations[0] 'peer_proof_observed' ($sequence + 1L)))
        }
    }
    $last = $values[-1]
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=$($last.aggregate.afterStateGeneration); final_state_hash=$($last.aggregate.afterStateFingerprint); transition_count=$($values.Count); $coverage; authority_effect=none; log_writer_id=fixture-writer; log_writer_sequence=99")
    return $lines.ToArray()
}

$guiAst = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$null,[ref]$null)
foreach ($name in @(
        'New-TunaGuiFileTransferFailureClassificationResult',
        'Get-TunaGuiExplicitFailureClassification',
        'Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definitions = @($guiAst.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -ceq $name
    }, $true))
    if ($definitions.Count -ne 1) { throw "GUI function not unique: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
function Assert-Parity(
    [string[]]$inputLines,
    [bool]$valid,
    [string]$expectedVariant = '') {
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (
        @('event=unrelated') + $inputLines)
    $compactLines = @($inputLines | Where-Object {
        $name = Get-FileTransferCoordinatorSnapshotEventName -Line $_
        $catalog.SnapshotAuthorityV1 -contains $name -or
            $catalog.GenerationStampedEffects -contains $name
    })
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
    $actualValid = $full.ArtifactEvidenceVerdict -ceq 'pass' -and
        $null -eq $full.FirstUnresolvedBoundary
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        $full.BoundaryHash -cne $compact.BoundaryHash -or
        $actualValid -ne $valid) {
        throw "Unexpected shared result: valid=$valid; reason=$($full.ArtifactEvidenceFailureReason); boundary=$($full.FirstUnresolvedBoundary | ConvertTo-Json -Depth 10 -Compress)"
    }
    if (-not $valid -and $expectedVariant -cne '' -and
        [string]$full.FirstUnresolvedBoundary.variant -cne $expectedVariant) {
        throw "Unexpected failure variant: expected=$expectedVariant; actual=$($full.FirstUnresolvedBoundary.variant)"
    }
    $path = Join-Path $OutputRoot 'compact.log'
    [IO.File]::WriteAllLines($path, $compactLines)
    $gui = Get-TunaGuiFileTransferSetupFailureClassification `
        -Bookmark 0 -ErrorMessage 'fixture' `
        -RouteMode 'regular-v4-live-activation-off-on-off-256mb' `
        -ImmutableEvidencePaths @($path)
    if ($full.ArtifactEvidenceVerdict -ceq 'pass') {
        # These family fixtures intentionally omit earlier route publications.
        # GUI route checks precede semantic findings, which remain pinned above.
        $route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
        $compactRoute = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $compact
        $boundary = $route.FirstUnresolvedBoundary
        if ($route.Verdict -cne 'fail' -or @($route.Findings).Count -ne 1 -or
            $route.Findings[0] -cne 'snapshot authoritative route stream contains no route publications' -or
            (Compare-FileTransferCoordinatorSnapshotRouteConsistency $route $compactRoute).Verdict -cne 'pass' -or
            $route.BoundaryHash -cne $compactRoute.BoundaryHash -or
            $boundary.boundaryId -cne $full.Boundaries[0].boundaryId -or
            $boundary.boundaryHash -cne $full.Boundaries[0].boundaryHash -or
            $gui.ClassificationSource -cne 'coordinator_snapshot_authority_v1_route_consistency' -or
            $gui.FailureClass -cne 'filetransfer_route_consistency_analysis_parity_invalid' -or
            $gui.FailureVariant -cne 'snapshot_authoritative_route_boundary_invalid' -or
            $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or $gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or
            $gui.CoordinatorWriter -cne $boundary.writer -or $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or
            $gui.CoordinatorRawCount -ne 1 -or $gui.CoordinatorDistinctCount -ne 1 -or
            $gui.CoordinatorRecoveredCount -ne 0 -or $gui.CoordinatorUnresolvedCount -ne 1) {
            throw 'Peer-first Failed partial-route rejection or exact GUI boundary changed.'
        }
    }
    else {
        $boundary = $full.FirstUnresolvedBoundary
        if ($gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or
            $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or
            $gui.CoordinatorWriter -cne $boundary.writer -or
            $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or
            $gui.FailureVariant -cne $boundary.variant -or
            $gui.CoordinatorRawCount -ne $boundary.rawCount -or
            $gui.CoordinatorDistinctCount -ne $boundary.distinctCount -or
            $gui.CoordinatorRecoveredCount -ne $boundary.recoveredCount -or
            $gui.CoordinatorUnresolvedCount -ne $boundary.unresolvedCount) {
            throw 'GUI boundary diverged from the shared evaluator.'
        }
    }
}

$idempotentChain = @($origin, $accepted, $idempotent)
Assert-Parity (New-Lines $idempotentChain) $true
Assert-Parity (New-Lines @($rejected)) $true
Assert-Parity (New-Lines $idempotentChain 'missing-origin') $false `
    'peer_first_failed_yield_peer_proof_provenance_invalid'
Assert-Parity (New-Lines $idempotentChain 'copied-peer') $false `
    'peer_first_failed_yield_emitted_copied_authority_effect'
Assert-Parity (New-Lines $idempotentChain 'copied-recovery') $false `
    'peer_first_failed_yield_emitted_copied_authority_effect'
Assert-Parity (New-Lines $idempotentChain 'neutral-effect') $false `
    'fallback_tail_failed_state_neutral_attempt_emitted_authority_effect'

$emptyAttempt = @($idempotentChain | ForEach-Object { Copy-Value $_ })
$emptyAttempt[-1].aggregate.operations = @()
$emptyLines = New-Lines $emptyAttempt
$emptyEvaluation = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $emptyLines
if ($emptyEvaluation.ArtifactEvidenceVerdict -cne 'fail' -or
    $emptyEvaluation.FirstUnresolvedBoundary.variant -cne
        'fallback_tail_failed_attempted_decision_missing') {
    throw 'Empty Failed attempt did not retain its precise fail-closed variant.'
}

$duplicateLines = @(New-Lines $idempotentChain)
Assert-Parity (@($duplicateLines[0], $duplicateLines[1], $duplicateLines[1]) +
    $duplicateLines[2..($duplicateLines.Count - 1)]) $true
Write-Output 'Peer-first Failed yield: strict catalog, provenance, attempts, effects, and full/compact/GUI parity passed.'
