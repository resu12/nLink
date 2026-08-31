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
if (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog $envelopes[2] (Subject $envelopes[2])) {
    throw 'The independent Applied lifecycle catalog control was relaxed.'
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
function New-Lines([object[]]$values, [bool]$forgedEffect = $false) {
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
        throw "Unexpected shared result: valid=$valid; expected=$reason; actual=$($full.ArtifactEvidenceFailureReason); parity=$((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact) | ConvertTo-Json -Compress)"
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
Assert-Parity (New-Lines @($prepared,$envelopes[2])) $false 'snapshot_operation_catalog_mismatch'
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
