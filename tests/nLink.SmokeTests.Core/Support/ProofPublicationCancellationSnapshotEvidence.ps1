param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') {
    throw 'Frozen Snapshot V1 catalog changed.'
}
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
function Copy-Value($value) { $value | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json }
function Find-Operation($envelope, [string]$role) {
    $matches = @($envelope.aggregate.operations | Where-Object { $_.operationRole -ceq $role })
    if ($matches.Count -ne 1) { throw "Fixture subject not unique: $role" }
    $matches[0]
}
$first = $envelopes[0]
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$families = $catalog.RequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
function New-Effect($envelope, $operation, [string]$kind, [long]$sequence) {
    $scope = $operation.receiveRecovery.sourceBoundary
    "event=filetransfer_coordinator_authority_effect; $prefix; transition_id=$($envelope.transitionId); coordinator_state_generation=$($envelope.aggregate.afterStateGeneration); operation_family=$($operation.family); operation_key=$($operation.operationKey); operation_authority_id=$($operation.authorityId); operation_generation=$($operation.operationGeneration); effect_kind=$kind; effect_identity=$kind-$($envelope.transitionId); effect_generation=$($operation.receiptGeneration); receipt_identity=$($operation.receiptIdentity); receipt_generation=$($operation.receiptGeneration); receipt_status=$($operation.receiptStatus); receipt_fingerprint=$($operation.receiptFingerprint); route=$($scope.route); protocol_version=$($scope.protocolVersion); live_route_epoch=$($scope.liveRouteEpoch); route_publication_generation=$($scope.routePublicationGeneration); route_commit_generation=$($scope.routeCommitGeneration); runtime_generation=$($scope.runtimeGeneration); terminal_generation=$($scope.terminalGeneration); leg_generation=$($scope.legGeneration); transport_epoch=$($scope.transportEpoch); bridge_recovery_generation=$($scope.bridgeRecoveryGeneration); checkpoint_generation=$($scope.checkpointGeneration); checkpoint_request_id=$($scope.checkpointRequestId); data_session_identity=$($scope.dataSessionIdentity); data_session_generation=$($scope.dataSessionGeneration); authority_effect=applied; log_writer_id=fixture-writer; log_writer_sequence=$sequence"
}
function New-Lines([object[]]$records, [bool]$includeProofEffect = $true) {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=$($first.aggregate.beforeStateGeneration); initial_state_hash=$($first.aggregate.beforeStateFingerprint); $coverage; authority_effect=none")
    for ($i = 0; $i -lt $records.Count; $i++) {
        $envelope = $records[$i]
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($envelope | ConvertTo-Json -Depth 64 -Compress))
        $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
        $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
        $sequence = 10L * ($i + 1)
        $lines.Add("event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=$($envelope.transitionId); before_state_generation=$($envelope.aggregate.beforeStateGeneration); after_state_generation=$($envelope.aggregate.afterStateGeneration); snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none; log_writer_id=fixture-writer; log_writer_sequence=$sequence")
        $kind = switch -Exact ($envelope.decisionKind) {
            'ObserveReceiveRecoveryExecutionStart' { 'execution_started' }
            'ObserveReceiveRecoveryExecutionReceipt' { 'execution_receipt_observed' }
            'ApplyReceiveRecoveryProofStatePublicationReceipt' { 'proof_publication_delivered' }
            'ObserveReceiveRecoveryProof' { if ($includeProofEffect) { 'receive_proof_observed' } }
        }
        if ($kind) {
            $role = if ($kind -ceq 'proof_publication_delivered') { 'proof_publication_terminal' } else { 'operation_current' }
            $lines.Add((New-Effect $envelope (Find-Operation $envelope $role) $kind ($sequence + 1)))
        }
    }
    $last = $records[$records.Count - 1]
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=$($last.aggregate.afterStateGeneration); final_state_hash=$($last.aggregate.afterStateFingerprint); transition_count=$($records.Count); $coverage; authority_effect=none")
    $lines.ToArray()
}
function Test-Open($evaluation) {
    @($evaluation.Boundaries | Where-Object {
        $_.taxonomyClass -ceq 'filetransfer_receive_recovery_completed_retained_proof_state_recheck_heartbeat_suppressed' -and
        $_.unresolvedCount -eq 1
    }).Count -eq 1
}
$lines = @(New-Lines $envelopes)
$full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated') + $lines)
$compactLines = @($lines | Where-Object {
    $eventName = Get-FileTransferCoordinatorSnapshotEventName -Line $_
    $catalog.SnapshotAuthorityV1 -contains $eventName -or $catalog.GenerationStampedEffects -contains $eventName
})
$compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
if ($full.ArtifactEvidenceVerdict -cne 'pass' -or $full.LegacySelectionCount -ne 0 -or
    $null -ne $full.FirstUnresolvedBoundary -or
    (Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact).Verdict -cne 'pass') {
    throw "Exact parent proof cancellation failed: $($full | Select-Object * -ExcludeProperty Boundaries | ConvertTo-Json -Depth 16 -Compress)"
}
$openLines = @(New-Lines $envelopes[0..4])
$open = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $openLines
if ($open.ArtifactEvidenceVerdict -cne 'pass' -or -not (Test-Open $open)) { throw 'Unclosed arm was hidden.' }
$missingEffect = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (New-Lines $envelopes $false)
if ($missingEffect.ArtifactEvidenceVerdict -cne 'fail') { throw 'Missing proof effect passed.' }

# Mutate only the cancellation, retaining the exact earlier parent/arm evidence.
foreach ($mutation in @(
    { param($e,$p,$r) $p.operationGeneration++; $p.operationKey = $p.operationKey.Replace("|$($p.operationGeneration - 1)|", "|$($p.operationGeneration)|") },
    { param($e,$p,$r) $p.authorityId = 'foreign-authority' },
    { param($e,$p,$r) $p.predecessorAuthorityId = 'foreign-predecessor' },
    { param($e,$p,$r) $p.receiveRecovery.recoveryKind = 'foreign-kind' },
    { param($e,$p,$r) $p.receiveRecovery.sourceBoundary.checkpointGeneration++ },
    { param($e,$p,$r) $p.receiveRecovery.effectiveBoundary.dataSessionGeneration++ },
    { param($e,$p,$r) $p.receiveRecovery.recoveryLivenessLeaseGeneration++ },
    { param($e,$p,$r) $p.receiveRecovery.proofIdentity = '(none)' },
    { param($e,$p,$r) $p.receiveRecovery.proofObservedUtcMs = 0 },
    { param($e,$p,$r) $p.receiptFingerprint = 'wrong-receipt' },
    { param($e,$p,$r) $p.receiptValidated = $false },
    { param($e,$p,$r) $p.stateApplied = $false },
    { param($e,$p,$r) $p.beforeState = 'None' },
    { param($e,$p,$r) $r.stateApplied = $false },
    { param($e,$p,$r) $r.predecessorAuthorityId = 'foreign-predecessor' },
    { param($e,$p,$r) $r.receiveRecovery.recoveryKind = 'foreign-kind' },
    { param($e,$p,$r) $r.receiveRecovery.closureGeneration++ },
    { param($e,$p,$r) $r.commandFingerprint = 'wrong-command' },
    { param($e,$p,$r) $r.commandGeneration++ },
    { param($e,$p,$r) $r.receiveRecovery.effectiveBoundary.transportEpoch++ },
    { param($e,$p,$r) $e.aggregate.operations += Copy-Value $p },
    { param($e,$p,$r) $e.aggregate.operations += Copy-Value $r },
    { param($e,$p,$r) $e.decisionStatus = 'Rejected' }
)) {
    $bad = Copy-Value $envelopes
    $cancellation = $bad[5]
    & $mutation $cancellation (Find-Operation $cancellation 'operation_current') (Find-Operation $cancellation 'proof_publication_recheck')
    # Keep effects immutable: a modified receipt/operation cannot manufacture a matching effect.
    $badLines = @(New-Lines $bad $false) | Where-Object { -not $_.Contains('snapshot_stream_completed') }
    $badLines += @($lines | Where-Object { $_.Contains('effect_kind=receive_proof_observed;') -or $_.Contains('snapshot_stream_completed') })
    $result = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $badLines
    if ($result.ArtifactEvidenceVerdict -cne 'fail' -and -not (Test-Open $result)) { throw "Malformed cancellation passed: $mutation" }
}
# Parent-state absence is checked directly, avoiding fabricated execution effects.
$armBoundary = @($full.Boundaries | Where-Object { $_.boundaryId -ceq $envelopes[4].transitionId })[0]
$closureBoundary = @($full.Boundaries | Where-Object { $_.boundaryId -ceq $envelopes[5].transitionId })[0]
$armEntry = [pscustomobject]@{ Boundary=$armBoundary; Operation=(Find-Operation $envelopes[4] 'proof_publication_recheck') }
$closureEntry = [pscustomobject]@{ Boundary=$closureBoundary; Operation=(Find-Operation $envelopes[5] 'proof_publication_recheck') }
if (Test-FileTransferCoordinatorReceiveRecoveryRecheckCancellation -Arm $armEntry -Closure $closureEntry -ParentHistory @() -Effects @()) {
    throw 'Missing parent lineage passed.'
}
$parents = @($full.Boundaries | ForEach-Object {
    $boundary = $_
    foreach ($operation in $boundary.operations) {
        if ($operation.operationRole -in @('operation_current','operation_terminal')) {
            [pscustomobject]@{Boundary=$boundary; Operation=$operation}
        }
    }
})
function Proof-Effect($entry) {
    $op = $entry.Operation
    $effect = [ordered]@{
        CoordinatorInstanceId=$entry.Boundary.coordinatorInstanceId
        TransitionId=$entry.Boundary.boundaryId; StateGeneration=$entry.Boundary.afterStateGeneration
        Family=$op.family; OperationKey=$op.operationKey; AuthorityId=$op.authorityId
        OperationGeneration=$op.operationGeneration; EffectKind='receive_proof_observed'
        ReceiptIdentity=$op.receiptIdentity; ReceiptGeneration=$op.receiptGeneration
        ReceiptStatus=$op.receiptStatus; ReceiptFingerprint=$op.receiptFingerprint
    }
    foreach ($property in $op.receiveRecovery.sourceBoundary.PSObject.Properties) {
        $effect[$property.Name.Substring(0,1).ToUpperInvariant() + $property.Name.Substring(1)] = $property.Value
    }
    [pscustomobject]$effect
}
foreach ($scopeName in @('sourceBoundary','effectiveBoundary')) {
    foreach ($field in $parents[-1].Operation.receiveRecovery.$scopeName.PSObject.Properties.Name) {
        $mutated = Copy-Value $parents
        $scope = $mutated[-1].Operation.receiveRecovery.$scopeName
        $scope.$field = if ($scope.$field -is [string]) { 'mutated-scope' } else { [long]$scope.$field + 1 }
        if (Test-FileTransferCoordinatorReceiveRecoveryRecheckCancellation -Arm $armEntry -Closure $closureEntry `
            -ParentHistory $mutated -Effects @((Proof-Effect $parents[-1]))) {
            throw "Parent boundary mutation passed: $scopeName/$field"
        }
    }
}
# Moving current to terminal is one parent lineage, not a second generation.
$terminalParents = Copy-Value $parents
$entering = $terminalParents[-1]
$retiring = Copy-Value $entering
$retiring.Operation.afterState = 'None'
$retiring.Operation.receiveRecovery.proofIdentity = '(none)'
$entering.Operation.operationRole = 'operation_terminal'
$entering.Operation.operationKey = $entering.Operation.operationKey.Replace('|operation_current|','|operation_terminal|')
$entering.Operation.receiveRecovery.role = 'operation_terminal'
$entering.Operation.beforeState = 'None'
$terminalParents = @($terminalParents[0..($terminalParents.Count - 2)]) + @($retiring,$entering)
if (-not (Test-FileTransferCoordinatorReceiveRecoveryRecheckCancellation -Arm $armEntry -Closure $closureEntry `
    -ParentHistory $terminalParents -Effects @((Proof-Effect $entering)))) { throw 'Exact terminal parent cancellation failed.' }

$invalidatedParents = Copy-Value $parents
$invalidated = $invalidatedParents[-1]
$invalidated.Boundary.decisionKind = 'InvalidateReceiveRecoveryOperation'
$invalidated.Operation.afterState = 'Invalidated:None'
$invalidated.Operation.receiveRecovery.executionState = 'Invalidated'
$invalidated.Operation.receiveRecovery.proofIdentity = '(none)'
$invalidationClosure = Copy-Value $closureEntry
$invalidationClosure.Boundary.decisionKind = 'InvalidateReceiveRecoveryOperation'
if (-not (Test-FileTransferCoordinatorReceiveRecoveryRecheckCancellation -Arm $armEntry -Closure $invalidationClosure `
    -ParentHistory $invalidatedParents -Effects @())) { throw 'Exact typed invalidation failed.' }

# An intervening retirement and restoration cannot revive the old parent key.
$abaParents = Copy-Value $parents
$retired = Copy-Value $abaParents[-2]
$retired.Boundary.boundaryId = 'retired-parent'
$retired.Operation.stateApplied = $true
$retired.Operation.beforeState = $retired.Operation.afterState
$retired.Operation.afterState = 'None'
$restored = Copy-Value $retired
$restored.Boundary.boundaryId = 'restored-parent'
$restored.Operation.beforeState = 'None'
$restored.Operation.afterState = $retired.Operation.beforeState
$abaParents = @($abaParents[0..($abaParents.Count - 2)]) + @($retired,$restored,$abaParents[-1])
if (Test-FileTransferCoordinatorReceiveRecoveryRecheckCancellation -Arm $armEntry -Closure $closureEntry `
    -ParentHistory $abaParents -Effects @((Proof-Effect $parents[-1]))) { throw 'Retired parent ABA passed.' }

$duplicates = @($lines | ForEach-Object { $_; if ($_ -match 'event=filetransfer_coordinator_(transition_snapshot|authority_effect);') { $_ } })
if ((Get-FileTransferCoordinatorSnapshotEvaluation -Lines $duplicates).BoundaryHash -cne $full.BoundaryHash) {
    throw 'Byte-identical duplicates changed boundaries.'
}
$conflicts = @($lines | ForEach-Object {
    $_; if ($_.Contains('effect_kind=receive_proof_observed;')) { $_.Replace('receipt_status=Adopted;', 'receipt_status=Rejected;') }
})
if ((Get-FileTransferCoordinatorSnapshotEvaluation -Lines $conflicts).ArtifactEvidenceVerdict -cne 'fail') {
    throw 'Conflicting proof effects passed.'
}

$guiAst = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$null, [ref]$null)
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult',
        'Get-TunaGuiExplicitFailureClassification', 'Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definitions = @($guiAst.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $true))
    if ($definitions.Count -ne 1) { throw "GUI function not unique: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
foreach ($case in @(@{Lines=$compactLines; Evaluation=$compact}, @{Lines=$openLines; Evaluation=$open})) {
    $path = Join-Path $OutputRoot 'gui-evidence.log'
    [IO.File]::WriteAllLines($path, [string[]]$case.Lines)
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'diagnostic evidence evaluation' `
        -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
    $boundary = $case.Evaluation.FirstUnresolvedBoundary
    if ($null -eq $boundary) {
        if ($null -ne $gui) { throw 'GUI reopened cancelled arm.' }
    }
    elseif ($gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or
        $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or
        $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or $gui.CoordinatorRawCount -ne 1) {
        throw "GUI boundary diverged: $($gui | ConvertTo-Json -Depth 8 -Compress)"
    }
}
Write-Output 'Production proof cancellation, generation isolation, malformed controls, and full/compact/GUI parity passed.'
