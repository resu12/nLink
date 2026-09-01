param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') { throw 'Frozen catalog changed.' }
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
foreach ($envelope in $envelopes) { $envelope.snapshotSelectionMode = 'SnapshotV1Authoritative' }
function Copy-Value($value) { $value | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json }
function Subject($envelope) {
    $subjects = @($envelope.aggregate.operations | Where-Object { $_.operationRole -ceq 'scope_invalidation' })
    if ($subjects.Count -ne 1) { throw 'Fixture subject not unique.' }
    $subjects[0]
}
$prepared = $envelopes[0]
$pending = Subject $prepared
if (-not (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog $prepared $pending)) { throw 'Production preparation rejected.' }
if ($pending.runtimeUnlockRetry.receipts[0].receiptReason -cne 'retry_dispatch_runtime_scope_replaced') { throw 'Command reason lost.' }
# The known duplicate-decision generation-zero defect is NOT covered by the pending exemption.
$duplicate = $envelopes[1]
if ((Subject $duplicate).receiptGeneration -ne 0 -or
    (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog $duplicate (Subject $duplicate))) {
    throw 'Malformed validated duplicate decision was unexpectedly admitted.'
}
if (-not (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog $envelopes[2] (Subject $envelopes[2]))) {
    throw 'The exact Applied scope-invalidation lifecycle was rejected.'
}
foreach ($mutation in @(
    { param($e,$o) $o.operationRole = 'attempt_pending' },
    { param($e,$o) $o.operationKind = 'RetryAttempt' },
    { param($e,$o) $o.runtimeUnlockRetry.role = 'attempt_installed' },
    { param($e,$o) $o.operationKey += '-wrong' },
    { param($e,$o) $o.authorityId = '(none)' },
    { param($e,$o) $o.operationGeneration++ },
    { param($e,$o) $o.commandIdentity = 'wrong' },
    { param($e,$o) $o.commandGeneration++ },
    { param($e,$o) $o.commandFingerprint = 'wrong' },
    { param($e,$o) $o.receiptIdentity = 'wrong' },
    { param($e,$o) $o.receiptGeneration++ },
    { param($e,$o) $o.receiptFingerprint = 'wrong' },
    { param($e,$o) $o.receiptValidated = $false },
    { param($e,$o) $o.receiptValidated = 'True' },
    { param($e,$o) $o.stateApplied = $false },
    { param($e,$o) $o.runtimeUnlockRetry.receipts[0].stage = 'install' },
    { param($e,$o) $o.runtimeUnlockRetry.receipts[0].receiptStatus = 'Pending' },
    { param($e,$o) $o.runtimeUnlockRetry.receipts[0].receiptReason = '' },
    { param($e,$o) $o.runtimeUnlockRetry.receipts[0].receiptValidated = $false },
    { param($e,$o) $o.runtimeUnlockRetry.receipts[0].commandGeneration++ },
    { param($e,$o) $o.runtimeUnlockRetry.receipts[0].receiptIdentity = 'wrong' },
    { param($e,$o) $o.runtimeUnlockRetry.receipts = @() },
    { param($e,$o) $o.runtimeUnlockRetry.receipts += Copy-Value $o.runtimeUnlockRetry.receipts[0] },
    { param($e,$o) $o.runtimeUnlockRetry.sourceBoundary.dataSessionIdentity = '' },
    { param($e,$o) $e.aggregate.operations += Copy-Value $o },
    { param($e,$o) $e.decisionKind = 'ObserveRuntimeUnlockRetryDispatchStage' },
    { param($e,$o) $e.decisionStatus = 'Rejected' },
    { param($e,$o) $e.aggregate.afterBoundary.dataSessionGeneration++ },
    { param($e,$o) $e.aggregate.afterStateGeneration++ },
    { param($e,$o) $e.aggregate.afterStateFingerprint = $e.aggregate.beforeStateFingerprint }
)) {
    $bad = Copy-Value $envelopes[2]; $operation = Subject $bad
    & $mutation $bad $operation
    if (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog $bad $operation) { throw "Malformed Applied stage passed: $mutation" }
}
$separate = Copy-Value $prepared
$subject = Subject $separate
$subject.operationGeneration = 7L; $subject.commandGeneration = 7L
$subject.operationKey = "runtime_unlock_retry_lifecycle|scope_invalidation|$($subject.authorityId)|7|ScopeInvalidation"
$subject.runtimeUnlockRetry.receipts[0].commandGeneration = 7L
$attempt = @($separate.aggregate.operations | Where-Object operationRole -CEQ 'attempted_decision')[0]
$attempt.operationGeneration = 7L; $attempt.commandGeneration = 7L; $attempt.receiptGeneration = 7L
$attempt.runtimeUnlockRetry.receipts[0].commandGeneration = 7L
$attempt.runtimeUnlockRetry.receipts[0].receiptGeneration = 7L
$attempt.operationKey = "runtime_unlock_retry_lifecycle|attempted_decision|$($attempt.authorityId)|7|$($attempt.operationKind)"
$subject.runtimeUnlockRetry.authorityGeneration = 3L
$subject.runtimeUnlockRetry.dispatchAttemptGeneration = 2L
$subject.runtimeUnlockRetry.recoveryContractGeneration = 5L
$subject.runtimeUnlockRetry.sourceBoundary.transportEpoch = 9L
$subject.runtimeUnlockRetry.sourceBoundary.bridgeRecoveryGeneration = 8L
if (-not (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog $separate $subject)) { throw 'Generation domains or historical source conflated.' }
$appliedDomains = Copy-Value $envelopes[2]; $subject = Subject $appliedDomains
$subject.operationGeneration = 7L; $subject.commandGeneration = 7L; $subject.receiptGeneration = 7L
$subject.operationKey = "runtime_unlock_retry_lifecycle|scope_invalidation|$($subject.authorityId)|7|ScopeInvalidation"
$subject.runtimeUnlockRetry.receipts[0].commandGeneration = 7L; $subject.runtimeUnlockRetry.receipts[0].receiptGeneration = 7L
$subject.runtimeUnlockRetry.authorityGeneration = 3L; $subject.runtimeUnlockRetry.dispatchAttemptGeneration = 2L
$subject.runtimeUnlockRetry.sourceBoundary.transportEpoch = 9L
if (-not (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog $appliedDomains $subject)) { throw 'Applied command generation or historical source conflated with authority/aggregate scope.' }

foreach ($mutation in @(
    { param($e,$o) $o.operationRole = 'attempt_pending' },
    { param($e,$o) $o.operationKind = 'RetryAttempt' },
    { param($e,$o) $o.runtimeUnlockRetry.role = 'attempt_pending' },
    { param($e,$o) $o.operationKey += '-wrong' },
    { param($e,$o) $o.authorityId = '(none)' },
    { param($e,$o) $o.operationGeneration++ },
    { param($e,$o) $o.commandIdentity = 'wrong' },
    { param($e,$o) $o.commandFingerprint = 'wrong' },
    { param($e,$o) $o.runtimeUnlockRetry.receipts[0].commandGeneration++ },
    { param($e,$o) $o.runtimeUnlockRetry.receipts[0].commandIdentity = '(none)' },
    { param($e,$o) $o.runtimeUnlockRetry.receipts[0].commandFingerprint = '' },
    { param($e,$o) $o.runtimeUnlockRetry.receipts[0].receiptReason = '' },
    { param($e,$o) $o.runtimeUnlockRetry.receipts[0].receiptIdentity = 'forged' },
    { param($e,$o) $o.runtimeUnlockRetry.receipts[0].receiptGeneration = 1L },
    { param($e,$o) $o.runtimeUnlockRetry.receipts[0].receiptFingerprint = 'forged' },
    { param($e,$o) $o.runtimeUnlockRetry.receipts[0].receiptStatus = 'Applied' },
    { param($e,$o) $o.runtimeUnlockRetry.receipts[0].receiptValidated = $true },
    { param($e,$o) $o.runtimeUnlockRetry.receipts[0].stage = 'install' },
    { param($e,$o) $o.runtimeUnlockRetry.receipts = @() },
    { param($e,$o) $o.runtimeUnlockRetry.receipts += Copy-Value $o.runtimeUnlockRetry.receipts[0] },
    { param($e,$o) $o.receiptValidated = $true },
    { param($e,$o) $o.receiptValidated = 'False' },
    { param($e,$o) $o.stateApplied = 'True' },
    { param($e,$o) $o.receiptGeneration = 1L },
    { param($e,$o) $o.runtimeUnlockRetry.sourceBoundary.dataSessionIdentity = '' },
    { param($e,$o) $e.aggregate.operations += Copy-Value $o },
    { param($e,$o) $e.aggregate.operations = @($o) },
    { param($e,$o) @($e.aggregate.operations | Where-Object operationRole -CEQ 'attempted_decision')[0].authorityId = 'unrelated' },
    { param($e,$o) @($e.aggregate.operations | Where-Object operationRole -CEQ 'attempted_decision')[0].receiptGeneration = 0L },
    { param($e,$o) $e.decisionStatus = 'Rejected' },
    { param($e,$o) $e.decisionKind = 'CommitRuntimeUnlockRetryLifecycleScopeInvalidation' },
    { param($e,$o) $e.decisionKind = 'IssueRuntimeUnlockRetryDispatchInstallReceipt' },
    { param($e,$o) $e.aggregate.afterStateGeneration++ },
    { param($e,$o) $e.aggregate.afterStateFingerprint = $e.aggregate.beforeStateFingerprint }
)) {
    $bad = Copy-Value $prepared; $operation = Subject $bad
    & $mutation $bad $operation
    if (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog $bad $operation) { throw "Malformed pending stage passed: $mutation" }
}

$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($prepared.coordinatorInstanceId); session_id=$($prepared.sessionId); transfer_id=$($prepared.transferId); direction=$($prepared.direction)"
$families = $script:FileTransferCoordinatorSnapshotRequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
function New-AppliedEffect($envelope, $op, $receipt, [string]$kind, [int]$sequence) {
    $scope = if ($op.family -ceq 'fallback_tail_yield') { $op.fallbackTailYield.sourceBoundary } else { $op.runtimeUnlockRetry.sourceBoundary }
    "event=filetransfer_coordinator_authority_effect; $prefix; transition_id=$($envelope.transitionId); coordinator_state_generation=$($envelope.aggregate.afterStateGeneration); operation_family=$($op.family); operation_key=$($op.operationKey); operation_authority_id=$($op.authorityId); operation_generation=$($op.operationGeneration); effect_kind=$kind; effect_identity=$kind-$sequence; effect_generation=$($receipt.receiptGeneration); receipt_identity=$($receipt.receiptIdentity); receipt_generation=$($receipt.receiptGeneration); receipt_status=$($receipt.receiptStatus); receipt_fingerprint=$($receipt.receiptFingerprint); route=$($scope.route); protocol_version=$($scope.protocolVersion); live_route_epoch=$($scope.liveRouteEpoch); route_publication_generation=$($scope.routePublicationGeneration); route_commit_generation=$($scope.routeCommitGeneration); runtime_generation=$($scope.runtimeGeneration); terminal_generation=$($scope.terminalGeneration); leg_generation=$($scope.legGeneration); transport_epoch=$($scope.transportEpoch); bridge_recovery_generation=$($scope.bridgeRecoveryGeneration); checkpoint_generation=$($scope.checkpointGeneration); checkpoint_request_id=$($scope.checkpointRequestId); data_session_identity=$($scope.dataSessionIdentity); data_session_generation=$($scope.dataSessionGeneration); authority_effect=applied; log_writer_id=fixture-writer; log_writer_sequence=$sequence"
}
function New-Lines([object[]]$values, [bool]$forgedEffect = $false, [string]$effectCase = '') {
    $first = $values[0]; $last = $values[-1]
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=$($first.aggregate.beforeStateGeneration); initial_state_hash=$($first.aggregate.beforeStateFingerprint); $coverage; authority_effect=none")
    for ($i=0; $i -lt $values.Count; $i++) {
        $envelope = $values[$i]
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($envelope | ConvertTo-Json -Depth 64 -Compress))
        $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
        $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
        $sequence = 10 * ($i+1)
        $lines.Add("event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=$($envelope.transitionId); before_state_generation=$($envelope.aggregate.beforeStateGeneration); after_state_generation=$($envelope.aggregate.afterStateGeneration); snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none; log_writer_id=fixture-writer; log_writer_sequence=$sequence")
        $proofStage = switch -Exact ($envelope.decisionKind) {
            'FallbackTailQuiescent' { 'yield' }
            'ApplyFallbackTailLocalProofReceipt' { 'local_proof' }
            'ApplyFallbackTailPeerProofReceipt' { 'peer_proof' }
        }
        if ($null -ne $proofStage) {
            $op = @($envelope.aggregate.operations | Where-Object operationRole -CEQ 'yield_current')[0]
            $receipt = @($op.fallbackTailYield.receipts | Where-Object stage -CEQ $proofStage)[0]
            $kinds = switch -Exact ($proofStage) {
                'yield' { @('recovery_owner_retired_after_yield') }
                'local_proof' { @('local_proof_sent','local_proof_applied') }
                'peer_proof' { @('peer_proof_observed') }
            }
            $offset = 1
            foreach ($kind in $kinds) { $lines.Add((New-AppliedEffect $envelope $op $receipt $kind ($sequence+$offset))); $offset++ }
            if ($proofStage -ceq 'peer_proof') {
                $pair = @($op.fallbackTailYield.receipts | Where-Object stage -CEQ 'paired_release')[0]
                $lines.Add((New-AppliedEffect $envelope $op $pair 'paired_release_published' ($sequence+$offset)))
            }
        }
        if ($envelope.decisionKind -ceq 'ObserveRuntimeUnlockRetryDispatchStage') {
            $op = @($envelope.aggregate.operations | Where-Object operationRole -CEQ 'attempt_pending')[0]
            $receipt = @($op.runtimeUnlockRetry.receipts | Where-Object stage -CEQ 'stage')[0]
            $lines.Add((New-AppliedEffect $envelope $op $receipt 'dispatch_stage_observed' ($sequence+1)))
        }
        if ($envelope.decisionKind -ceq 'CommitRuntimeUnlockRetryLifecycleScopeInvalidation' -and
            (($envelope.decisionStatus -ceq 'Accepted' -and $effectCase -cne 'missing') -or $effectCase -ceq 'duplicate-application')) {
            $op = Subject $envelope
            $receipt = $op.runtimeUnlockRetry.receipts[0]
            $lines.Add((New-AppliedEffect $envelope $op $receipt 'scope_invalidation_applied' ($sequence+1)))
        }
        if ($forgedEffect -and $i -eq 0) {
            $op = Subject $envelope; $scope = $op.runtimeUnlockRetry.sourceBoundary
            $lines.Add("event=filetransfer_coordinator_authority_effect; $prefix; transition_id=$($envelope.transitionId); coordinator_state_generation=$($envelope.aggregate.afterStateGeneration); operation_family=$($op.family); operation_key=$($op.operationKey); operation_authority_id=$($op.authorityId); operation_generation=$($op.operationGeneration); effect_kind=scope_invalidation_applied; effect_identity=forged; effect_generation=1; receipt_identity=forged; receipt_generation=1; receipt_status=Applied; receipt_fingerprint=forged; route=$($scope.route); protocol_version=$($scope.protocolVersion); live_route_epoch=$($scope.liveRouteEpoch); route_publication_generation=$($scope.routePublicationGeneration); route_commit_generation=$($scope.routeCommitGeneration); runtime_generation=$($scope.runtimeGeneration); terminal_generation=$($scope.terminalGeneration); leg_generation=$($scope.legGeneration); transport_epoch=$($scope.transportEpoch); bridge_recovery_generation=$($scope.bridgeRecoveryGeneration); checkpoint_generation=$($scope.checkpointGeneration); checkpoint_request_id=$($scope.checkpointRequestId); data_session_identity=$($scope.dataSessionIdentity); data_session_generation=$($scope.dataSessionGeneration); authority_effect=applied; log_writer_id=fixture-writer; log_writer_sequence=$($sequence+1)")
        }
    }
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=$($last.aggregate.afterStateGeneration); final_state_hash=$($last.aggregate.afterStateFingerprint); transition_count=$($values.Count); $coverage; authority_effect=none")
    $lines.ToArray()
}
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$null,[ref]$null)
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification','Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definitions = @($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if ($definitions.Count -ne 1) { throw "GUI function not unique: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
function Assert-Parity([string[]]$inputLines, [bool]$valid, [string]$reason = '') {
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated') + $inputLines)
    $compactLines = @($inputLines | Where-Object {
        $name = Get-FileTransferCoordinatorSnapshotEventName -Line $_
        $catalog.SnapshotAuthorityV1 -contains $name -or $catalog.GenerationStampedEffects -contains $name
    })
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        ($full.ArtifactEvidenceVerdict -ceq 'pass') -ne $valid -or
        ($valid -and $null -ne $full.FirstUnresolvedBoundary) -or
        (-not $valid -and $full.ArtifactEvidenceFailureReason -notlike "*$reason*" -and
            $full.FirstUnresolvedBoundary.variant -cne $reason)) {
        throw "Unexpected shared result: valid=$valid; expected=$reason; actual=$($full.ArtifactEvidenceFailureReason); boundary=$($full.FirstUnresolvedBoundary | Select-Object decisionKind,taxonomyClass,decisionReason,variant | ConvertTo-Json -Compress); parity=$((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact) | ConvertTo-Json -Compress)"
    }
    $path = Join-Path $OutputRoot 'compact.log'; [IO.File]::WriteAllLines($path, $compactLines)
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
    if ($valid) { if ($null -ne $gui) { throw 'GUI rejected valid preparation.' } }
    else {
        $boundary = $full.FirstUnresolvedBoundary
        if ($gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or
            $gui.CoordinatorWriter -cne $boundary.writer -or $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or
            $gui.CoordinatorRawCount -ne $boundary.rawCount -or $gui.CoordinatorDistinctCount -ne $boundary.distinctCount -or
            $gui.CoordinatorRecoveredCount -ne $boundary.recoveredCount -or $gui.CoordinatorUnresolvedCount -ne $boundary.unresolvedCount) { throw 'GUI boundary diverged.' }
    }
}
Assert-Parity (New-Lines @($prepared)) $true
Assert-Parity (New-Lines @($separate)) $true
Assert-Parity (New-Lines @($prepared) $true) $false 'snapshot_authority_effect_unbound'
Assert-Parity (New-Lines @($prepared,$duplicate)) $false 'family2_pending_scope_invalidation_command_validated_as_completed_receipt'
foreach ($status in @('Idempotent', 'Rejected')) {
    # Contract-valid decision evidence; the actual generation-zero producer control stays above.
    $attempt = Copy-Value $duplicate
    $attempt.decisionStatus = $status
    (Subject $attempt).receiptStatus = $status
    (Subject $attempt).receiptGeneration = (Subject $attempt).commandGeneration
    Assert-Parity (New-Lines @($prepared,$attempt)) $true
}
$application = @($envelopes[3],$prepared,$envelopes[2])
if ($envelopes.Count -gt 6) { $application = @($envelopes[6..($envelopes.Count-1)]) + $application }
foreach ($item in $application) {
    if ($item.decisionKind -ceq 'FallbackTailQuiescent' -and -not (Test-FileTransferCoordinatorSnapshotFallbackTailQuiescentEnvelope $item)) {
        throw 'Production fixture must start after checkpoint acceptance, with an unchanged Quiescent aggregate boundary.'
    }
}
if ($envelopes.Count -gt 6) {
    $rollback = $envelopes[2]
    $rollbackYield = @($rollback.aggregate.operations | Where-Object {
            $_.family -ceq 'fallback_tail_yield' -and
            $_.operationRole -ceq 'yield_current'
        })[0]
    $rollbackEntry = [pscustomobject]@{
        Boundary = [pscustomobject]@{
            decisionKind = $rollback.decisionKind
            decisionStatus = $rollback.decisionStatus
            decisionReason = $rollback.decisionReason
            operations = $rollback.aggregate.operations
        }
        Operation = $rollbackYield
    }
    if (-not (Test-FileTransferCoordinatorSnapshotFallbackTailScopeInvalidationRollback $rollbackEntry)) {
        throw 'Exact fallback reservation rollback was rejected.'
    }
    foreach ($mutation in @(
        { param($e) $e.Boundary.decisionReason = 'unrelated' },
        { param($e) @($e.Boundary.operations | Where-Object operationRole -CEQ 'release_reservation_current')[0].beforeState = 'Consumed' },
        { param($e) @($e.Boundary.operations | Where-Object operationRole -CEQ 'attempt_terminal')[0].afterState = 'Installed' },
        { param($e) @($e.Boundary.operations | Where-Object operationRole -CEQ 'scope_invalidation')[0].receiptValidated = $false }
    )) {
        $badEntry = Copy-Value $rollbackEntry
        & $mutation $badEntry
        if (Test-FileTransferCoordinatorSnapshotFallbackTailScopeInvalidationRollback $badEntry) {
            throw "Malformed fallback reservation rollback passed: $mutation"
        }
    }
}
Assert-Parity (New-Lines $application) $true
Assert-Parity (New-Lines (@($application) + @($envelopes[4],$envelopes[5]))) $true
Assert-Parity (New-Lines $application $false 'missing') $false 'snapshot_scope_invalidation_required_effect_missing'
Assert-Parity (New-Lines (@($application) + @($envelopes[4])) $false 'duplicate-application') $false 'snapshot_authority_effect_scope_invalidation_without_application'
Assert-Parity (New-Lines @($envelopes[2])) $false 'family2_scope_invalidation_application_without_exact_pending_history'
foreach ($mutation in @(
    { param($o) $o.runtimeUnlockRetry.sourceBoundary.dataSessionGeneration++ },
    { param($o) $o.runtimeUnlockRetry.sourceBoundary.transportEpoch++ },
    { param($o) $o.commandFingerprint = 'changed'; $o.runtimeUnlockRetry.receipts[0].commandFingerprint = 'changed' }
)) {
    $bad = Copy-Value $envelopes[2]; & $mutation (Subject $bad)
    Assert-Parity (New-Lines (@($application[0..($application.Count-2)]) + @($bad))) $false 'family2_scope_invalidation_application_without_exact_pending_history'
}
$appliedLines = @(New-Lines $application)
$effectLine = $appliedLines[-2]
Assert-Parity (@($appliedLines[0..($appliedLines.Count-2)]) + @($effectLine,$appliedLines[-1])) $true
$conflictingEffect = $effectLine -replace 'receipt_fingerprint=[^;]+', 'receipt_fingerprint=wrong'
Assert-Parity (@($appliedLines[0..($appliedLines.Count-2)]) + @($conflictingEffect,$appliedLines[-1])) $false 'snapshot_authority_effect_conflicting_duplicate'
$receiptMutation = Copy-Value $envelopes[4]
(Subject $receiptMutation).runtimeUnlockRetry.receipts[0].receiptFingerprint = 'mutated-applied-receipt'
(Subject $receiptMutation).commandFingerprint = 'mutated-applied-receipt'
Assert-Parity (New-Lines (@($application) + @($receiptMutation))) $false 'family2_scope_invalidation_application_without_exact_pending_history'
$gap = Copy-Value $envelopes[2]; $gap.aggregate.beforeStateGeneration++; $gap.aggregate.afterStateGeneration++
Assert-Parity (New-Lines (@($application[0..($application.Count-2)]) + @($gap))) $false 'snapshot_generation_or_hash_gap'
$malformed = Copy-Value $prepared; (Subject $malformed).runtimeUnlockRetry.receipts[0].receiptGeneration = 1L
Assert-Parity (New-Lines @($malformed)) $false 'family2_pending_scope_invalidation_command_validated_as_completed_receipt'
$lines = @(New-Lines @($prepared))
Assert-Parity @($lines[0],$lines[1],$lines[1],$lines[2]) $true
$conflict = $lines[1] -replace 'snapshot_sha256=[^;]+', 'snapshot_sha256=conflict'
Assert-Parity @($lines[0],$lines[1],$conflict,$lines[2]) $false 'snapshot_payload_hash_mismatch'

# Same-key history must retain the immutable captured command through carries and retirement.
$carry = Copy-Value $prepared
$carry.transitionId += '-carry'; $carry.decisionUtcMilliseconds++
$carry.aggregate.beforeStateGeneration = $prepared.aggregate.afterStateGeneration
$carry.aggregate.afterStateGeneration = $carry.aggregate.beforeStateGeneration + 1
$carry.aggregate.beforeStateFingerprint = $prepared.aggregate.afterStateFingerprint
$carry.aggregate.afterStateFingerprint = 'carry-hash'
$carry.aggregate.operations = @((Subject $carry)); (Subject $carry).beforeState = 'Pending'
Assert-Parity (New-Lines @($prepared,$carry)) $true
foreach ($mutation in @(
    { param($o) $o.runtimeUnlockRetry.sourceBoundary.dataSessionGeneration++ },
    { param($o) $o.runtimeUnlockRetry.sourceBoundary.transportEpoch++ },
    { param($o) $o.runtimeUnlockRetry.receipts[0].receiptReason = 'mutated' },
    { param($o) $o.commandFingerprint = 'changed'; $o.runtimeUnlockRetry.receipts[0].commandFingerprint = 'changed' }
)) {
    $bad = Copy-Value $carry; & $mutation (Subject $bad)
    Assert-Parity (New-Lines @($prepared,$bad)) $false 'family2_pending_scope_invalidation_command_mutated_or_reused'
}
$retired = Copy-Value $carry; (Subject $retired).afterState = 'None'
Assert-Parity (New-Lines @($prepared,$retired)) $true
$restored = Copy-Value $prepared; $restored.transitionId += '-restored'; $restored.decisionUtcMilliseconds += 2
$restored.aggregate.beforeStateGeneration = $retired.aggregate.afterStateGeneration
$restored.aggregate.afterStateGeneration = $restored.aggregate.beforeStateGeneration + 1
$restored.aggregate.beforeStateFingerprint = $retired.aggregate.afterStateFingerprint
$restored.aggregate.afterStateFingerprint = 'restored-hash'
Assert-Parity (New-Lines @($prepared,$retired,$restored)) $false 'family2_pending_scope_invalidation_command_mutated_or_reused'
Write-Output 'Pending scope invalidation, strict receipt/effect controls, immutable history, and full/compact/GUI parity passed.'
