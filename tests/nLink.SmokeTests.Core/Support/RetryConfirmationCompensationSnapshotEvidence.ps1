param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot, [string]$Mode = 'baseline')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferAuthorityMatching.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferFallbackV6BootstrapDirectionCorrelation.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferFrontierGraceHandoff.ps1')
. (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') {
    throw 'Frozen Snapshot V1 catalog changed.'
}
$kind = 'CompensateRejectedRuntimeUnlockRetryDispatchConfirmation'
if ($script:FileTransferCoordinatorSnapshotFamily2DecisionKinds -contains $kind) { throw 'Compensation broadened the frozen decision catalog.' }
if ($script:FileTransferCoordinatorSnapshotFamily2ReceiptStages['settlement'] -contains 'Superseded') {
    throw 'Provisional supersession broadened the frozen settlement catalog.'
}
$active = $Mode -in @('active', 'active_settled')
$settled = $Mode -ceq 'active_settled'
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
# This is the production-projected Family-2 graph with receipt-backed effect
# fixtures, not a claim that physical callbacks ran or all other families passed.
# Start at the first real retry transition, retaining its exact declared initial
# generation/hash and every following envelope; no prior unrelated family is
# represented by an empty, fabricated transition.
$firstRetryIndex = -1
for ($i = 0; $i -lt $envelopes.Count; $i++) {
    if (@($envelopes[$i].aggregate.operations | Where-Object family -CEQ 'runtime_unlock_retry_lifecycle').Count -gt 0) {
        $firstRetryIndex = $i; break
    }
}
if ($firstRetryIndex -lt 0) { throw 'Fixture contains no retry transition.' }
$envelopes = @($envelopes[$firstRetryIndex..($envelopes.Count - 1)])
foreach ($envelope in $envelopes) {
    $envelope.snapshotSelectionMode = 'SnapshotV1Shadow'
    $envelope.aggregate.operations = @($envelope.aggregate.operations | Where-Object family -CEQ 'runtime_unlock_retry_lifecycle')
    $envelope.aggregate.changedFamilies = @('runtime_unlock_retry_lifecycle')
}
function Copy-Value($value) { $value | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json }
$accepted = @($envelopes | Where-Object { $_.decisionKind -ceq $kind -and $_.decisionStatus -ceq 'Accepted' })
if ($accepted.Count -ne 1) { throw 'Expected exactly one real accepted compensation.' }
$compensation = $accepted[0]
$shape = Get-FileTransferCoordinatorRetryConfirmationCompensationShape $compensation
if ($null -eq $shape) { throw 'Production compensation shape rejected.' }
$prior = @($envelopes | Where-Object {
    $_.decisionKind -ceq 'CommitRuntimeUnlockRetryDispatchInstall' -and
    $_.aggregate.afterStateGeneration -eq $compensation.aggregate.beforeStateGeneration
})
if ($prior.Count -ne 1) { throw 'Compensation predecessor commit not unique.' }
$commit = $prior[0]
$ownSendEnvelope = $null
$ownSettlementEnvelope = $null
$evictedTerminal = $null
if ($settled) {
    $sends = @($envelopes | Where-Object {
        $_.decisionKind -ceq 'ObserveRuntimeUnlockRetryDispatchExecutionState' -and $_.decisionStatus -ceq 'Accepted' -and
        @($_.aggregate.operations | Where-Object {
            $_.authorityId -ceq $shape.Restored[0].authorityId -and $_.operationRole -ceq 'attempt_installed' -and
            $_.beforeState -ceq 'AliasBound' -and $_.afterState -ceq 'SendInFlight'
        }).Count -eq 1
    })
    $settlements = @($envelopes | Where-Object {
        $_.decisionKind -ceq 'ObserveRuntimeUnlockRetryDispatchSettlement' -and $_.decisionStatus -ceq 'Accepted' -and
        @($_.aggregate.operations | Where-Object {
            $_.authorityId -ceq $shape.Restored[0].authorityId -and $_.operationRole -ceq 'attempt_terminal' -and
            $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Settled' -and $_.receiptStatus -ceq 'DispatchObserved'
        }).Count -eq 1
    })
    if ($sends.Count -ne 1 -or $settlements.Count -ne 1) { throw 'Genuine A continuation is not uniquely identified.' }
    $ownSendEnvelope = $sends[0]; $ownSettlementEnvelope = $settlements[0]
    $evictions = @($ownSettlementEnvelope.aggregate.operations | Where-Object {
        $_.authorityId -ceq $shape.Failed.authorityId -and $_.operationRole -ceq 'attempt_terminal' -and
        $_.beforeState -ceq 'Invalidated' -and $_.afterState -ceq 'None'
    })
    $retirements = @($ownSettlementEnvelope.aggregate.operations | Where-Object {
        $_.authorityId -ceq $shape.Restored[0].authorityId -and $_.operationRole -ceq 'attempt_installed' -and
        $_.beforeState -ceq 'SendInFlight' -and $_.afterState -ceq 'None'
    })
    if ($evictions.Count -ne 1 -or $retirements.Count -ne 1 -or $ownSettlementEnvelope.aggregate.operations.Count -ne 3 -or
        $evictions[0].operationKey -cne $shape.Terminal.operationKey -or
        ($evictions[0].runtimeUnlockRetry | ConvertTo-Json -Depth 32 -Compress) -cne
            ($shape.Terminal.runtimeUnlockRetry | ConvertTo-Json -Depth 32 -Compress)) {
        throw 'A settlement must evict only the exact unchanged B terminal born in compensation.'
    }
    $evictedTerminal = $evictions[0]
    $generationDomains = @([long]$shape.Restored[0].operationGeneration, [long]$shape.Failed.operationGeneration,
        [long]$shape.Restored[0].runtimeUnlockRetry.receipts[1].receiptGeneration,
        [long]$shape.Failed.runtimeUnlockRetry.receipts[1].receiptGeneration,
        [long]$shape.Restored[0].runtimeUnlockRetry.offerGeneration,
        [long]$ownSettlementEnvelope.aggregate.afterStateGeneration)
    if (@($generationDomains | Select-Object -Unique).Count -ne $generationDomains.Count) {
        throw 'Continuation fixture must distinguish A/B authority, install, A offer, and aggregate generation domains.'
    }
}
function Boundary($envelope) {
    [pscustomobject]@{ coordinatorInstanceId=$envelope.coordinatorInstanceId; writer='fixture-writer';
        sessionId=$envelope.sessionId; transferId=$envelope.transferId; direction=$envelope.direction;
        beforeStateGeneration=$envelope.aggregate.beforeStateGeneration; afterStateGeneration=$envelope.aggregate.afterStateGeneration;
        beforeStateHash=$envelope.aggregate.beforeStateFingerprint; afterStateHash=$envelope.aggregate.afterStateFingerprint;
        beforeBoundary=$envelope.aggregate.beforeBoundary; decisionKind=$envelope.decisionKind;
        decisionStatus=$envelope.decisionStatus; decisionReason=$envelope.decisionReason; operations=$envelope.aggregate.operations }
}
function Prior-Entry($envelope) {
    [pscustomobject]@{ Boundary=(Boundary $envelope); AfterBoundary=$envelope.aggregate.afterBoundary;
        SessionId=$envelope.sessionId; TransferId=$envelope.transferId; Direction=$envelope.direction }
}
$priorEntries = @($envelopes | Where-Object {
    $_.aggregate.afterStateGeneration -le $compensation.aggregate.beforeStateGeneration
} | ForEach-Object { Prior-Entry $_ })
if (-not (Test-FileTransferCoordinatorRetryConfirmationCompensationHistory $compensation (Boundary $compensation) $priorEntries)) {
    throw 'Production compensation history rejected; the wrapper retains the original envelopes on failure.'
}
if ($shape.Displaced.Count -eq 1 -and $shape.Displaced[0].operationRole -ceq 'attempt_terminal') {
    $terminalKey = $shape.Displaced[0].operationKey
    foreach ($case in @('missing_terminal', 'changed_terminal', 'terminal_remove_restore')) {
        $badHistory = Copy-Value $priorEntries
        $terminalEntries = @($badHistory | Where-Object {
            @($_.Boundary.operations | Where-Object { $_.operationKey -ceq $terminalKey -and $_.stateApplied }).Count -gt 0
        })
        if ($terminalEntries.Count -ne 1) { throw 'Original predecessor terminal not uniquely established.' }
        $entry = $terminalEntries[0]
        $operation = @($entry.Boundary.operations | Where-Object operationKey -CEQ $terminalKey)[0]
        switch ($case) {
            'missing_terminal' { $entry.Boundary.operations = @($entry.Boundary.operations | Where-Object operationKey -CNE $terminalKey) }
            'changed_terminal' { $operation.runtimeUnlockRetry.receipts[-1].receiptFingerprint = 'changed-original-terminal' }
            'terminal_remove_restore' {
                $removed = Copy-Value $entry
                $removed.Boundary.afterStateGeneration++
                $removed.Boundary.operations = @((Copy-Value $operation))
                $removed.Boundary.operations[0].beforeState = 'Settled'
                $removed.Boundary.operations[0].afterState = 'None'
                $restored = Copy-Value $entry
                $restored.Boundary.afterStateGeneration += 2L
                $restored.Boundary.operations = @((Copy-Value $operation))
                $badHistory = @($badHistory) + @($removed, $restored)
            }
        }
        if (Test-FileTransferCoordinatorRetryConfirmationCompensationHistory $compensation (Boundary $compensation) $badHistory) {
            throw "Retained predecessor terminal mutation accepted: $case"
        }
    }
}
foreach ($mutation in @(
    { param($e) $e.decisionReason = 'other' },
    { param($e) $e.decisionStatus = 'Committed' },
    { param($e) $e.aggregate.afterStateGeneration++ },
    { param($e) $e.aggregate.afterStateFingerprint = $e.aggregate.beforeStateFingerprint },
    { param($e) $e.aggregate.afterBoundary.dataSessionGeneration++ },
    { param($e) $e.aggregate.operations += Copy-Value $e.aggregate.operations[0] },
    { param($e) $e.aggregate.operations = @($e.aggregate.operations | Where-Object operationRole -CNE 'attempt_terminal') },
    { param($e) $e.aggregate.operations[0].runtimeUnlockRetry.sourceBoundary.runtimeGeneration++ },
    { param($e) $e.aggregate.operations[0].runtimeUnlockRetry.receipts[1].receiptValidated = $false }
)) {
    $bad = Copy-Value $compensation
    & $mutation $bad
    if ((Test-FileTransferCoordinatorRetryConfirmationCompensationHistory $bad (Boundary $bad) $priorEntries)) {
        throw "Malformed compensation accepted: $mutation"
    }
}
foreach ($field in @('commandIdentity', 'commandFingerprint', 'receiptIdentity', 'receiptFingerprint')) {
    $bad = Copy-Value $compensation
    foreach ($operation in $bad.aggregate.operations | Where-Object authorityId -CEQ $shape.Failed.authorityId) {
        $operation.runtimeUnlockRetry.receipts[1].$field = 'consistently-forged'
        $operation.$field = 'consistently-forged'
    }
    if (Test-FileTransferCoordinatorRetryConfirmationCompensationHistory $bad (Boundary $bad) $priorEntries) {
        throw "Consistently forged receipt escaped commit history: $field"
    }
}
foreach ($mutatePrior in @(
    { param($p) $p.Boundary.writer = 'other-writer' },
    { param($p) $p.Boundary.coordinatorInstanceId = 'other-coordinator' },
    { param($p) $p.Boundary.afterStateGeneration-- },
    { param($p) $p.Boundary.afterStateHash = 'old-hash' },
    { param($p) $p.Boundary.decisionReason = 'unrelated' }
)) {
    $badHistory = Copy-Value $priorEntries
    $badPrior = @($badHistory | Where-Object { $_.Boundary.afterStateGeneration -eq $commit.aggregate.afterStateGeneration })
    if ($badPrior.Count -ne 1) { throw 'Mutation commit not unique.' }
    & $mutatePrior $badPrior[0]
    if (Test-FileTransferCoordinatorRetryConfirmationCompensationHistory $compensation (Boundary $compensation) $badHistory) {
        throw "Unbound prior commit accepted: $mutatePrior"
    }
}
if ((Test-FileTransferCoordinatorRetryConfirmationCompensationHistory $compensation (Boundary $compensation) @()) -or
    (Test-FileTransferCoordinatorRetryConfirmationCompensationHistory $compensation (Boundary $compensation) @($priorEntries + (Prior-Entry $commit)))) {
    throw 'Absent or ambiguous commit history accepted.'
}

function Active-Parts($envelope) {
    $operations = @($envelope.aggregate.operations)
    $original = @($operations | Where-Object { $_.operationRole -ceq 'attempt_installed' -and $_.beforeState -ceq 'AliasBound' -and $_.afterState -ceq 'None' })
    $successor = @($operations | Where-Object { $_.operationRole -ceq 'attempt_installed' -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Installed' })
    $pending = @($operations | Where-Object operationRole -CEQ 'attempt_pending')
    $retained = @($operations | Where-Object operationRole -CEQ 'attempt_retained_predecessor')
    if ($original.Count -ne 1 -or $successor.Count -ne 1 -or $pending.Count -ne 1 -or $retained.Count -ne 1) { return $null }
    [pscustomobject]@{ Original=$original[0]; Successor=$successor[0]; Pending=$pending[0]; Retained=$retained[0] }
}
function Test-ActiveCommit($envelope) {
    $parts = Active-Parts $envelope
    if ($null -eq $parts) { return $false }
    Test-FileTransferCoordinatorSnapshotProvisionalRetrySupersession -Envelope $envelope -Operation $parts.Retained
}
$activeMutationCount = 0
if ($active) {
    $parts = Active-Parts $commit
    if ($null -eq $parts -or -not (Test-ActiveCommit $commit) -or
        -not (Test-FileTransferCoordinatorSnapshotProvisionalRetrySupersession -Envelope $compensation -Operation $shape.Displaced[0])) {
        throw 'Production active-predecessor four-role commit or exact inverse was rejected.'
    }
    if ($parts.Original.operationGeneration -eq $parts.Original.runtimeUnlockRetry.receipts[1].receiptGeneration -or
        $parts.Original.operationGeneration -eq $parts.Original.runtimeUnlockRetry.offerGeneration -or
        $parts.Original.runtimeUnlockRetry.receipts[1].receiptGeneration -eq $parts.Original.runtimeUnlockRetry.offerGeneration) {
        throw 'Active fixture must keep authority, install-receipt, and offer generation domains distinct.'
    }
    $mutations = [ordered]@{
        missing_sibling = { param($e,$p) $e.aggregate.operations = @($e.aggregate.operations | Where-Object operationKey -CNE $p.Original.operationKey) }
        duplicate_sibling = { param($e,$p) $e.aggregate.operations += Copy-Value $p.Pending }
        foreign_sibling = { param($e,$p) $p.Pending.authorityId = 'foreign-authority' }
        wrong_kind = { param($e,$p) $e.decisionKind = 'ObserveRuntimeUnlockRetryDispatchSettlement' }
        wrong_reason = { param($e,$p) $e.decisionReason = 'other' }
        wrong_status = { param($e,$p) $e.decisionStatus = 'Idempotent' }
        wrong_generation = { param($e,$p) $e.aggregate.afterStateGeneration++ }
        unchanged_hash = { param($e,$p) $e.aggregate.afterStateFingerprint = $e.aggregate.beforeStateFingerprint }
        changed_scope = { param($e,$p) $e.aggregate.afterBoundary.runtimeGeneration++ }
        wrong_source = { param($e,$p) $p.Successor.runtimeUnlockRetry.sourceBoundary.dataSessionGeneration++ }
        wrong_predecessor = { param($e,$p) $p.Successor.predecessorAuthorityId = 'foreign-predecessor' }
        wrong_successor = { param($e,$p) $p.Retained.runtimeUnlockRetry.successorAuthorityId = 'foreign-successor' }
        wrong_authority_generation = { param($e,$p) $p.Successor.operationGeneration = $p.Original.operationGeneration }
        wrong_dispatch_generation = { param($e,$p) $p.Successor.runtimeUnlockRetry.dispatchAttemptGeneration++ }
        wrong_offer_generation = { param($e,$p) $p.Successor.runtimeUnlockRetry.retiredOfferGeneration++ }
        wrong_missing_dispatch_generation = { param($e,$p) $p.Successor.runtimeUnlockRetry.missingDispatchSuccessorGeneration++ }
        active_flag_absent = { param($e,$p) $p.Successor.runtimeUnlockRetry.requestActivePredecessorSupersession = $false }
        string_active_flag = { param($e,$p) $p.Pending.runtimeUnlockRetry.requestActivePredecessorSupersession = 'false'; $p.Successor.runtimeUnlockRetry.requestActivePredecessorSupersession = 'false' }
        string_state_applied_false = { param($e,$p) $p.Retained.stateApplied = 'false' }
        string_state_applied_true = { param($e,$p) $p.Retained.stateApplied = 'True' }
        already_dispatched = { param($e,$p) $p.Original.runtimeUnlockRetry.dispatchObserved = $true }
        no_rearm = { param($e,$p) $p.Original.runtimeUnlockRetry.listenerRearmRequirement = 'None' }
        unknown_rearm = { param($e,$p) foreach ($o in $e.aggregate.operations) { $o.runtimeUnlockRetry.listenerRearmRequirement = 'Unknown' } }
        unknown_execution_mode = { param($e,$p) foreach ($o in $e.aggregate.operations) { $o.runtimeUnlockRetry.listenerExecutionMode = 'Unknown' } }
        zero_recovery_lineage = { param($e,$p) foreach ($o in $e.aggregate.operations) { $o.runtimeUnlockRetry.recoveryContractGeneration = 0 } }
        zero_release_lineage = { param($e,$p) foreach ($o in $e.aggregate.operations) { $o.runtimeUnlockRetry.releaseGeneration = 0 } }
        wrong_stage_identity = { param($e,$p) $p.Original.runtimeUnlockRetry.receipts[0].commandIdentity = 'foreign-command'; $p.Retained.runtimeUnlockRetry.receipts[0].commandIdentity = 'foreign-command' }
        foreign_release = { param($e,$p) $p.Successor.runtimeUnlockRetry.releaseAuthorityId = 'foreign-release' }
        unvalidated_receipt = { param($e,$p) $p.Retained.runtimeUnlockRetry.receipts[-1].receiptValidated = $false; $p.Retained.receiptValidated = $false }
        string_receipt_validated_false = { param($e,$p) $p.Retained.runtimeUnlockRetry.receipts[-1].receiptValidated = 'false'; $p.Retained.receiptValidated = 'false' }
        string_receipt_validated_true = { param($e,$p) $p.Retained.runtimeUnlockRetry.receipts[-1].receiptValidated = 'True'; $p.Retained.receiptValidated = 'True' }
        wrong_settlement_status = { param($e,$p) $p.Retained.runtimeUnlockRetry.receipts[-1].receiptStatus = 'MissingDispatch'; $p.Retained.receiptStatus = 'MissingDispatch' }
        wrong_settlement_reason = { param($e,$p) $p.Retained.runtimeUnlockRetry.receipts[-1].receiptReason = 'other' }
        wrong_settlement_generation = { param($e,$p) $p.Retained.runtimeUnlockRetry.receipts[-1].receiptGeneration++; $p.Retained.receiptGeneration++ }
        changed_stage_fingerprint = { param($e,$p) $p.Retained.runtimeUnlockRetry.receipts[0].receiptFingerprint = 'changed-stage' }
        changed_install_fingerprint = { param($e,$p) $p.Retained.runtimeUnlockRetry.receipts[1].receiptFingerprint = 'changed-install' }
        copied_alias_bound_execution_fingerprint = { param($e,$p) $p.Retained.runtimeUnlockRetry.receipts[2].receiptFingerprint = $p.Original.runtimeUnlockRetry.receipts[2].receiptFingerprint }
    }
    foreach ($entry in $mutations.GetEnumerator()) {
        $bad = Copy-Value $commit
        & $entry.Value $bad (Active-Parts $bad)
        if (Test-ActiveCommit $bad) { throw "Malformed provisional active supersession accepted: $($entry.Key)" }
        $activeMutationCount++
    }
    # The new execution fingerprint is opaque after projection. Authenticate its
    # exact carry through the prior commit, not by equating it to AliasBound A.
    foreach ($receiptIndex in @(2, 3)) {
        $bad = Copy-Value $compensation
        $retained = @($bad.aggregate.operations | Where-Object operationRole -CEQ 'attempt_retained_predecessor')[0]
        $retained.runtimeUnlockRetry.receipts[$receiptIndex].receiptFingerprint = 'consistently-changed-retained-fingerprint'
        if ($receiptIndex -eq 3) { $retained.receiptFingerprint = $retained.runtimeUnlockRetry.receipts[$receiptIndex].receiptFingerprint }
        if (Test-FileTransferCoordinatorRetryConfirmationCompensationHistory $bad (Boundary $bad) $priorEntries) {
            throw "Compensation borrowed changed retained receipt $receiptIndex instead of its exact commit."
        }
        $activeMutationCount++
    }
    foreach ($field in @('sessionId', 'transferId', 'direction')) {
        $bad = Copy-Value $compensation
        $bad.$field = if ($field -ceq 'direction') { 'Inbound' } else { 'foreign-scope' }
        if (Test-FileTransferCoordinatorRetryConfirmationCompensationHistory $bad (Boundary $bad) $priorEntries) {
            throw "Compensation borrowed foreign $field history."
        }
        $activeMutationCount++
    }
    $reordered = Copy-Value $commit
    [array]::Reverse($reordered.aggregate.operations)
    if (-not (Test-ActiveCommit $reordered)) { throw 'Descriptor order changed provisional supersession admission.' }
}

$first = $envelopes[0]
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$coverage = 'emitted_family_coverage=runtime_unlock_retry_lifecycle; shadow_family_coverage=; authoritative_family_coverage=runtime_unlock_retry_lifecycle'
function Effect($envelope, $operation, $receipt, $effectKind, $sequence) {
    $scope = $operation.runtimeUnlockRetry.sourceBoundary
    $scopeFields = foreach ($pair in @(@('route','route'),@('protocol_version','protocolVersion'),@('live_route_epoch','liveRouteEpoch'),
        @('route_publication_generation','routePublicationGeneration'),@('route_commit_generation','routeCommitGeneration'),
        @('runtime_generation','runtimeGeneration'),@('terminal_generation','terminalGeneration'),@('leg_generation','legGeneration'),
        @('transport_epoch','transportEpoch'),@('bridge_recovery_generation','bridgeRecoveryGeneration'),
        @('checkpoint_generation','checkpointGeneration'),@('checkpoint_request_id','checkpointRequestId'),
        @('data_session_identity','dataSessionIdentity'),@('data_session_generation','dataSessionGeneration'))) {
        "$($pair[0])=$($scope.($pair[1]))"
    }
    "event=filetransfer_coordinator_authority_effect; $prefix; transition_id=$($envelope.transitionId); coordinator_state_generation=$($envelope.aggregate.afterStateGeneration); operation_family=$($operation.family); operation_key=$($operation.operationKey); operation_authority_id=$($operation.authorityId); operation_generation=$($operation.operationGeneration); effect_kind=$effectKind; effect_identity=$effectKind-$($receipt.receiptIdentity); effect_generation=$($receipt.receiptGeneration); receipt_identity=$($receipt.receiptIdentity); receipt_generation=$($receipt.receiptGeneration); receipt_status=$($receipt.receiptStatus); receipt_fingerprint=$($receipt.receiptFingerprint); $($scopeFields -join '; '); authority_effect=applied; log_writer_id=fixture-writer; log_writer_sequence=$sequence"
}
$evictionMutations = [ordered]@{
    changed_b_eviction_receipt = { param($e,$b) $b.runtimeUnlockRetry.receipts[1].receiptFingerprint = 'changed-b-install-receipt'; $b.receiptFingerprint = 'changed-b-install-receipt' }
    changed_b_eviction_status = { param($e,$b) $b.beforeState = 'Settled'; $b.runtimeUnlockRetry.receipts[1].receiptStatus = 'Settled'; $b.receiptStatus = 'Settled' }
    changed_b_eviction_source = { param($e,$b) $b.runtimeUnlockRetry.sourceBoundary.dataSessionGeneration++ }
    foreign_b_eviction_subject = { param($e,$b)
        $b.authorityId = 'fte1-eeeeeeeeeeeeeeeeeeeeeeee'; $b.operationGeneration++; $b.runtimeUnlockRetry.authorityGeneration = $b.operationGeneration
        $b.operationKey = "runtime_unlock_retry_lifecycle|attempt_terminal|$($b.authorityId)|$($b.operationGeneration)|RetryAttempt"
    }
    different_pair_closure = { param($e,$b)
        foreach ($a in @($e.aggregate.operations | Where-Object authorityId -CEQ $shape.Restored[0].authorityId)) {
            $a.authorityId = 'fte1-aaaaaaaaaaaaaaaaaaaaaaaa'; $a.operationGeneration += 10L; $a.runtimeUnlockRetry.authorityGeneration = $a.operationGeneration
            $a.operationKey = "runtime_unlock_retry_lifecycle|$($a.operationRole)|$($a.authorityId)|$($a.operationGeneration)|RetryAttempt"
        }
    }
}
function Lines([string]$case) {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=$($first.aggregate.beforeStateGeneration); initial_state_hash=$($first.aggregate.beforeStateFingerprint); $coverage; authority_effect=none")
    $sequence = 0L
    $caseEnvelopes = @($envelopes)
    $syntheticAttackIds = @{}
    if ($case -ceq 'skipped_alias_execution') {
        $caseEnvelopes = @($envelopes | ForEach-Object { Copy-Value $_ })
        $alias = @($caseEnvelopes | Where-Object {
            $_.decisionKind -ceq 'ObserveRuntimeUnlockRetryDispatchExecutionState' -and
            @($_.aggregate.operations | Where-Object {
                $_.authorityId -ceq $shape.Restored[0].authorityId -and $_.beforeState -ceq 'Installed' -and $_.afterState -ceq 'AliasBound'
            }).Count -eq 1
        })
        $ownSend = @($caseEnvelopes | Where-Object {
            $_.decisionKind -ceq 'ObserveRuntimeUnlockRetryDispatchExecutionState' -and
            @($_.aggregate.operations | Where-Object {
                $_.authorityId -ceq $shape.Restored[0].authorityId -and $_.beforeState -ceq 'AliasBound' -and $_.afterState -ceq 'SendInFlight'
            }).Count -eq 1
        })
        if ($alias.Count -ne 1 -or $ownSend.Count -ne 1) { throw 'Execution-order control requires unique genuine alias and own-send entries.' }
        $forged = Copy-Value (@($ownSend[0].aggregate.operations | Where-Object authorityId -CEQ $shape.Restored[0].authorityId)[0])
        $forged.beforeState = 'Installed'
        $alias[0].aggregate.operations = @($alias[0].aggregate.operations | ForEach-Object {
            if ($_.operationKey -ceq $forged.operationKey) { $forged } else { $_ }
        })
        if (-not (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog -Envelope $alias[0] -Operation $forged)) {
            throw 'Skipped-alias control did not isolate participating execution history from local catalog admission.'
        }
        # This deliberately forged transition has no physical effect. Retain
        # the later real chain to prove it cannot erase the illegal skip.
        $syntheticAttackIds[$alias[0].transitionId] = $true
    }
    if ($case -in @('post_compensation_data_scope_aba', 'post_compensation_terminal_scope_aba', 'arbitrary_closed_prefix', 'reversed_execution_prefix', 'foreign_terminal_occupancy_aba')) {
        # Deliberately forged aggregate-history attacks, not production events.
        # Their envelope chain is internally continuous so a catalog/header gap
        # cannot substitute for validating the participating authority interval.
        $anchor = $compensation
        if ($case -ceq 'reversed_execution_prefix') {
            $ownSend = @($caseEnvelopes | Where-Object {
                $_.decisionKind -ceq 'ObserveRuntimeUnlockRetryDispatchExecutionState' -and
                @($_.aggregate.operations | Where-Object {
                    $_.authorityId -ceq $shape.Restored[0].authorityId -and $_.beforeState -ceq 'AliasBound' -and $_.afterState -ceq 'SendInFlight'
                }).Count -eq 1
            })
            if ($ownSend.Count -ne 1) { throw 'Reversal control requires the genuine own-send entry.' }
            $anchor = $ownSend[0]
        }
        $prefixEnvelopes = @($caseEnvelopes | Where-Object {
            $_.aggregate.afterStateGeneration -le $anchor.aggregate.afterStateGeneration
        })
        $continuation = @($caseEnvelopes | Where-Object {
            $_.aggregate.beforeStateGeneration -ge $anchor.aggregate.afterStateGeneration
        })
        $attackCount = if ($case -in @('arbitrary_closed_prefix', 'reversed_execution_prefix')) { 1 } else { 2 }
        $attacks = @()
        $previousGeneration = [long]$anchor.aggregate.afterStateGeneration
        $previousHash = [string]$anchor.aggregate.afterStateFingerprint
        $previousScope = Copy-Value $anchor.aggregate.afterBoundary
        for ($attackIndex = 0; $attackIndex -lt $attackCount; $attackIndex++) {
            $attack = Copy-Value $compensation
            $attackHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([Text.UTF8Encoding]::new($false).GetBytes("$case|$($compensation.transitionId)|$attackIndex"))
            $attack.transitionId = 'fte1-' + $attackHash.Substring(0, 24)
            $attack.decisionKind = 'ObserveRuntimeUnlockRetryDispatchExecutionState'
            $attack.decisionStatus = 'Accepted'
            $attack.decisionReason = if ($case -ceq 'reversed_execution_prefix') { $anchor.decisionReason } else { $continuation[0].decisionReason }
            $attack.aggregate.beforeStateGeneration = $previousGeneration
            $attack.aggregate.afterStateGeneration = $previousGeneration + 1L
            $attack.aggregate.beforeStateFingerprint = $previousHash
            $attack.aggregate.afterStateFingerprint = $attackHash
            $attack.aggregate.beforeBoundary = Copy-Value $previousScope
            $attack.aggregate.afterBoundary = Copy-Value $compensation.aggregate.afterBoundary
            $attack.aggregate.operations = @()
            $attack.aggregate.changedFamilies = @()
            if ($case -ceq 'foreign_terminal_occupancy_aba') {
                # C enters/leaves the singleton terminal slot without retiring
                # B. The later B-looking eviction cannot erase this overwrite.
                $foreign = Copy-Value $shape.Terminal
                $foreign.authorityId = 'fte1-cccccccccccccccccccccccc'
                $foreign.operationGeneration++; $foreign.runtimeUnlockRetry.authorityGeneration = $foreign.operationGeneration
                $foreign.operationKey = "runtime_unlock_retry_lifecycle|attempt_terminal|$($foreign.authorityId)|$($foreign.operationGeneration)|RetryAttempt"
                $foreign.beforeState = if ($attackIndex -eq 0) { 'None' } else { 'Invalidated' }
                $foreign.afterState = if ($attackIndex -eq 0) { 'Invalidated' } else { 'None' }
                $attack.aggregate.operations = @($foreign)
                $attack.aggregate.changedFamilies = @('runtime_unlock_retry_lifecycle')
                if (-not (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog -Envelope $attack -Operation $foreign)) {
                    throw 'Foreign terminal occupancy must reach the singleton-history guard, not fail local catalog admission.'
                }
            }
            elseif ($case -in @('arbitrary_closed_prefix', 'reversed_execution_prefix')) {
                $attack.aggregate.operations = @((Copy-Value $shape.Restored[0]))
                $attack.aggregate.operations[0].beforeState = if ($case -ceq 'arbitrary_closed_prefix') { 'AliasBound' } else { 'SendInFlight' }
                $attack.aggregate.operations[0].afterState = if ($case -ceq 'arbitrary_closed_prefix') { 'Closed' } else { 'AliasBound' }
                $attack.aggregate.changedFamilies = @('runtime_unlock_retry_lifecycle')
                if (-not (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog -Envelope $attack -Operation $attack.aggregate.operations[0])) {
                    throw "$case was rejected by the local catalog instead of isolating current-role history."
                }
            }
            elseif ($attackIndex -eq 0) {
                $field = if ($case -ceq 'post_compensation_data_scope_aba') { 'dataSessionGeneration' } else { 'terminalGeneration' }
                $attack.aggregate.afterBoundary.$field++
            }
            $attacks += $attack
            $syntheticAttackIds[$attack.transitionId] = $true
            $previousGeneration = [long]$attack.aggregate.afterStateGeneration
            $previousHash = [string]$attack.aggregate.afterStateFingerprint
            $previousScope = Copy-Value $attack.aggregate.afterBoundary
        }
        $caseEnvelopes = @($prefixEnvelopes) + @($attacks)
        if ($case -notin @('arbitrary_closed_prefix', 'reversed_execution_prefix')) {
            foreach ($originalContinuation in $continuation) {
                $next = Copy-Value $originalContinuation
                $next.aggregate.beforeStateGeneration += $attackCount
                $next.aggregate.afterStateGeneration += $attackCount
                $next.aggregate.beforeStateFingerprint = $previousHash
                $previousHash = [string]$next.aggregate.afterStateFingerprint
                $caseEnvelopes += $next
            }
        }
    }
    if ($case -in @('no_compensation', 'rejected_compensation_only', 'idempotent_compensation_only')) {
        $caseEnvelopes = @($envelopes | Where-Object { $_.aggregate.afterStateGeneration -le $commit.aggregate.afterStateGeneration })
        if ($case -cne 'no_compensation') {
            # Negative diagnostic-only input, not an accepted inverse or a new
            # physical observation. Keep the installed B subject unchanged.
            $diagnostic = Copy-Value $compensation
            $diagnostic.decisionStatus = if ($case -ceq 'rejected_compensation_only') { 'Rejected' } else { 'Idempotent' }
            $diagnostic.decisionReason = if ($case -ceq 'rejected_compensation_only') {
                'retry_dispatch_confirmation_compensation_scope_mismatch'
            } else { 'retry_dispatch_confirmation_already_compensated' }
            $diagnostic.aggregate.afterStateGeneration = $diagnostic.aggregate.beforeStateGeneration
            $diagnostic.aggregate.afterStateFingerprint = $diagnostic.aggregate.beforeStateFingerprint
            $diagnostic.aggregate.operations = @((Copy-Value $shape.Failed))
            $diagnostic.aggregate.operations[0].afterState = 'Installed'
            $diagnostic.aggregate.operations[0].stateApplied = $false
            $diagnostic.aggregate.operations[0].receiptStatus = $diagnostic.decisionStatus
            $caseEnvelopes += $diagnostic
        }
    }
    if ($case -ceq 'missing_commit_gap') {
        $caseEnvelopes = @($caseEnvelopes | Where-Object transitionId -CNE $commit.transitionId)
    }
    foreach ($sourceEnvelope in $caseEnvelopes) {
        $envelope = $sourceEnvelope
        if ($evictionMutations.Contains($case) -and $envelope.transitionId -ceq $ownSettlementEnvelope.transitionId) {
            $envelope = Copy-Value $envelope
            $b = @($envelope.aggregate.operations | Where-Object operationKey -CEQ $evictedTerminal.operationKey)
            if ($b.Count -ne 1) { throw 'Eviction mutation subject not unique.' }
            & ($evictionMutations[$case]) $envelope $b[0]
        }
        if ($case -in @('missing_b_terminal_origin', 'competing_b_terminal_origin') -and $envelope.transitionId -ceq $compensation.transitionId) {
            $envelope = Copy-Value $envelope
            if ($case -ceq 'missing_b_terminal_origin') {
                $envelope.aggregate.operations = @($envelope.aggregate.operations | Where-Object operationKey -CNE $shape.Terminal.operationKey)
            } else { $envelope.aggregate.operations += Copy-Value $shape.Terminal }
        }
        if ($case -in @('descriptor_order', 'missing_install_reordered') -and $envelope.transitionId -in @($commit.transitionId, $compensation.transitionId)) {
            $envelope = Copy-Value $envelope
            [array]::Reverse($envelope.aggregate.operations)
        }
        if ($case -ceq 'missing_sibling' -and $envelope.transitionId -ceq $commit.transitionId) {
            $envelope = Copy-Value $envelope
            $parts = Active-Parts $envelope
            $envelope.aggregate.operations = @($envelope.aggregate.operations | Where-Object operationKey -CNE $parts.Original.operationKey)
        }
        if ($case -in @('changed_retained_receipt', 'source_poison', 'terminal_poison', 'missing_restored_role') -and
            $envelope.transitionId -ceq $compensation.transitionId) {
            $envelope = Copy-Value $envelope
            switch ($case) {
                'changed_retained_receipt' {
                    $retained = @($envelope.aggregate.operations | Where-Object operationRole -CEQ 'attempt_retained_predecessor')[0]
                    $retained.runtimeUnlockRetry.receipts[2].receiptFingerprint = 'changed-retained-execution'
                }
                'source_poison' { $envelope.aggregate.beforeBoundary.dataSessionGeneration++; $envelope.aggregate.afterBoundary.dataSessionGeneration++ }
                'terminal_poison' { $envelope.aggregate.afterBoundary.terminalGeneration++ }
                'missing_restored_role' {
                    $envelope.aggregate.operations = @($envelope.aggregate.operations | Where-Object {
                        -not ($_.operationRole -ceq 'attempt_installed' -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'AliasBound')
                    })
                }
            }
        }
        $sequence += 10L
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($envelope | ConvertTo-Json -Depth 64 -Compress))
        $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
        $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
        $transitionLine = "event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=$($envelope.transitionId); before_state_generation=$($envelope.aggregate.beforeStateGeneration); after_state_generation=$($envelope.aggregate.afterStateGeneration); snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none; log_writer_id=fixture-writer; log_writer_sequence=$sequence"
        $lines.Add($transitionLine)
        if ($case -ceq 'exact_duplicate' -and $envelope.transitionId -ceq $commit.transitionId) { $lines.Add($transitionLine) }
        if ($case -ceq 'conflicting_duplicate' -and $envelope.transitionId -ceq $compensation.transitionId) {
            $conflict = Copy-Value $envelope
            $conflict.aggregate.operations[0].runtimeUnlockRetry.receipts[0].receiptFingerprint = 'conflicting-receipt'
            $conflictBytes = [Text.UTF8Encoding]::new($false).GetBytes(($conflict | ConvertTo-Json -Depth 64 -Compress))
            $conflictHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $conflictBytes
            $conflictPayload = [Convert]::ToBase64String($conflictBytes).TrimEnd('=').Replace('+','-').Replace('/','_')
            $lines.Add($transitionLine.Replace("snapshot_sha256=$hash", "snapshot_sha256=$conflictHash").Replace("snapshot_payload=$payload", "snapshot_payload=$conflictPayload"))
        }
        foreach ($operation in $envelope.aggregate.operations) {
            if ($syntheticAttackIds.ContainsKey($envelope.transitionId)) { continue }
            $stage = switch -Exact ($envelope.decisionKind) {
                'ObserveRuntimeUnlockRetryDispatchStage' { if ($operation.operationRole -ceq 'attempt_pending' -and $operation.stateApplied) { 'stage' }; break }
                'CommitRuntimeUnlockRetryDispatchInstall' { if ($operation.operationRole -ceq 'attempt_installed' -and $operation.beforeState -ceq 'None') { 'install' }; break }
                'ObserveRuntimeUnlockRetryDispatchExecutionState' { if ($operation.operationRole -ceq 'attempt_installed' -and $operation.stateApplied) { 'execution' }; break }
                'EvaluateRuntimeUnlockRetryDispatchDeadline' { if ($operation.operationRole -ceq 'attempt_installed' -and $operation.afterState -ceq 'Settled') { 'settlement' }; break }
                'ObserveRuntimeUnlockRetryDispatchSettlement' {
                    if (($operation.operationRole -ceq 'attempt_installed' -and $operation.afterState -ceq 'Settled') -or
                        ($settled -and $operation.operationRole -ceq 'attempt_terminal' -and $operation.beforeState -ceq 'None' -and
                         $operation.afterState -ceq 'Settled' -and $operation.authorityId -ceq $shape.Restored[0].authorityId -and
                         $operation.receiptStatus -ceq 'DispatchObserved')) { 'settlement' }
                    break
                }
                default { '' }
            }
            if ([string]::IsNullOrWhiteSpace($stage)) { continue }
            $receipt = @($operation.runtimeUnlockRetry.receipts | Where-Object stage -CEQ $stage)
            if ($receipt.Count -ne 1) { throw "Effect receipt not unique: $stage" }
            if ($case -in @('missing_install', 'missing_install_reordered') -and $envelope.transitionId -ceq $commit.transitionId -and $stage -ceq 'install') { continue }
            if ($case -ceq 'missing_original_install' -and $operation.authorityId -ceq $shape.Restored[0].authorityId -and $stage -ceq 'install') { continue }
            if ($case -ceq 'missing_restored_settlement' -and $operation.authorityId -ceq $shape.Restored[0].authorityId -and $stage -ceq 'settlement') { continue }
            if ($case -ceq 'missing_restored_execution' -and $envelope.transitionId -ceq $ownSendEnvelope.transitionId -and $stage -ceq 'execution') { continue }
            $effectKind = @{ stage='dispatch_stage_observed'; install='install_committed'; execution='execution_observed'; settlement='settlement_observed' }[$stage]
            $effectLine = Effect $envelope $operation $receipt[0] $effectKind ($sequence + 1)
            $originalBInstall = $settled -and $envelope.transitionId -ceq $commit.transitionId -and $stage -ceq 'install'
            if ($originalBInstall -and $case -ceq 'mutated_original_b_effect') {
                $effectLine = $effectLine.Replace("receipt_fingerprint=$($receipt[0].receiptFingerprint);", 'receipt_fingerprint=fte1-000000000000000000000000;')
            }
            $lines.Add($effectLine)
            if ($originalBInstall -and $case -ceq 'exact_effect_duplicate') { $lines.Add($effectLine) }
            if ($originalBInstall -and $case -ceq 'conflicting_original_b_effect') {
                $lines.Add($effectLine.Replace("receipt_fingerprint=$($receipt[0].receiptFingerprint);", 'receipt_fingerprint=fte1-000000000000000000000000;'))
            }
        }
        if ($case -ceq 'forged_compensation_effect' -and $envelope.transitionId -ceq $compensation.transitionId) {
            $lines.Add((Effect $envelope $shape.Terminal $shape.Terminal.runtimeUnlockRetry.receipts[1] 'install_committed' ($sequence + 1)))
        }
        if ($case -ceq 'forged_eviction_install' -and $envelope.transitionId -ceq $ownSettlementEnvelope.transitionId) {
            $lines.Add((Effect $envelope $evictedTerminal $evictedTerminal.runtimeUnlockRetry.receipts[1] 'install_committed' ($sequence + 2)))
        }
    }
    $last = $caseEnvelopes[-1]
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=$($last.aggregate.afterStateGeneration); final_state_hash=$($last.aggregate.afterStateFingerprint); transition_count=$($caseEnvelopes.Count); $coverage; authority_effect=none")
    $lines.ToArray()
}
$guiAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$null, [ref]$null)
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult', 'Get-TunaGuiExplicitFailureClassification',
    'Get-TunaGuiFileTransferSetupFailureClassification', 'Test-TunaGuiLogLinesContain', 'Test-TunaGuiLogLineContainsAll',
    'Test-TunaGuiFallbackFrontierRequestDispatchTupleMatch', 'Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing',
    'Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing', 'Test-TunaGuiBridgeBootstrapNotReadyEvidence',
    'Resolve-TunaGuiFailureClassification', 'Get-TunaGuiReadinessStateAfterBookmark', 'Get-AppLogLinesAfterBookmark',
    'Sync-GuiSmokeAppLogLedger', 'Get-AppLogPaths', 'Assert-GuiSmokeScenarioDeadline')) {
    $definitions = @($guiAst.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name }, $true))
    if ($definitions.Count -ne 1) { throw "GUI function not unique: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
$emptyAppData = Join-Path $OutputRoot 'empty-gui-appdata'
[void][IO.Directory]::CreateDirectory($emptyAppData)
$script:GuiSmokeScenarioDeadlineUtc = [DateTimeOffset]::MaxValue
$script:GuiSmokeScenarioDeadlineName = ''
function Selected-Metadata($boundary) {
    if ($null -eq $boundary) { return $null }
    [ordered]@{ BoundaryId=[string]$boundary.boundaryId; BoundaryHash=[string]$boundary.boundaryHash;
        Class=[string]$boundary.taxonomyClass; Phase=[string]$boundary.phase;
        Writer=[string]$boundary.writer; WriterSequence=[long]$boundary.writerSequence;
        Raw=[int]$boundary.rawCount; Distinct=[int]$boundary.distinctCount;
        Recovered=[int]$boundary.recoveredCount; Unresolved=[int]$boundary.unresolvedCount }
}
$summaries = @()
$exactMetadata = $null
$missingInstallMetadata = $null
$cases = @('exact', 'missing_install', 'forged_compensation_effect')
if ($active) { $cases += @('descriptor_order', 'exact_duplicate', 'missing_sibling',
    'no_compensation', 'rejected_compensation_only', 'idempotent_compensation_only',
    'missing_original_install', 'missing_install_reordered', 'missing_commit_gap',
    'changed_retained_receipt', 'source_poison', 'terminal_poison', 'missing_restored_role', 'conflicting_duplicate') }
if ($settled) { $cases = @('exact', 'descriptor_order', 'exact_duplicate', 'missing_original_install', 'missing_install', 'missing_restored_settlement',
    'post_compensation_data_scope_aba', 'post_compensation_terminal_scope_aba', 'arbitrary_closed_prefix',
    'skipped_alias_execution', 'reversed_execution_prefix', 'missing_restored_execution', 'forged_eviction_install',
    'mutated_original_b_effect', 'conflicting_original_b_effect', 'exact_effect_duplicate',
    'changed_b_eviction_receipt', 'changed_b_eviction_status', 'changed_b_eviction_source', 'foreign_b_eviction_subject',
    'different_pair_closure', 'missing_b_terminal_origin', 'competing_b_terminal_origin', 'foreign_terminal_occupancy_aba') }
foreach ($case in $cases) {
    $lines = @(Lines $case)
    if ($active -and @($lines | Where-Object { $_ -like '*event=filetransfer_coordinator_authority_effect;*' -and $_ -like '*receipt_status=Superseded;*' }).Count -ne 0) {
        throw 'Active fixture fabricated a Superseded physical settlement effect.'
    }
    if ($settled -and $case -in @('exact', 'descriptor_order', 'exact_duplicate', 'exact_effect_duplicate')) {
        $effects = @($lines | Where-Object { (Get-FileTransferCoordinatorSnapshotEventName -Line $_) -ceq 'filetransfer_coordinator_authority_effect' })
        $bInstalls = @($effects | Where-Object {
            (Get-FileTransferCoordinatorSnapshotLineField -Line $_ -Name 'operation_authority_id') -ceq $shape.Failed.authorityId -and
            (Get-FileTransferCoordinatorSnapshotLineField -Line $_ -Name 'effect_kind') -ceq 'install_committed'
        } | Select-Object -Unique)
        if ($bInstalls.Count -ne 1 -or
            (Get-FileTransferCoordinatorSnapshotLineField -Line $bInstalls[0] -Name 'transition_id') -cne $commit.transitionId -or
            (Get-FileTransferCoordinatorSnapshotLineField -Line $bInstalls[0] -Name 'receipt_status') -cne 'Installed' -or
            @($effects | Where-Object {
                (Get-FileTransferCoordinatorSnapshotLineField -Line $_ -Name 'transition_id') -ceq $compensation.transitionId -or
                ((Get-FileTransferCoordinatorSnapshotLineField -Line $_ -Name 'transition_id') -ceq $ownSettlementEnvelope.transitionId -and
                 (Get-FileTransferCoordinatorSnapshotLineField -Line $_ -Name 'operation_authority_id') -ceq $shape.Failed.authorityId)
            }).Count -ne 0) { throw 'B must have exactly its original Installed effect and no compensation/eviction observation.' }
        foreach ($pair in @(@($ownSendEnvelope.transitionId, 'execution_observed'), @($ownSettlementEnvelope.transitionId, 'settlement_observed'))) {
            if (@($effects | Where-Object {
                (Get-FileTransferCoordinatorSnapshotLineField -Line $_ -Name 'transition_id') -ceq $pair[0] -and
                (Get-FileTransferCoordinatorSnapshotLineField -Line $_ -Name 'effect_kind') -ceq $pair[1] -and
                (Get-FileTransferCoordinatorSnapshotLineField -Line $_ -Name 'operation_authority_id') -ceq $shape.Restored[0].authorityId
            }).Count -ne 1) { throw 'A continuation requires exactly its own execution and settlement observations.' }
        }
    }
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated') + $lines)
    $compactLines = @($lines | Where-Object {
        $eventName = Get-FileTransferCoordinatorSnapshotEventName -Line $_
        $catalog.SnapshotAuthorityV1 -contains $eventName -or $catalog.GenerationStampedEffects -contains $eventName
    })
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact).Verdict -cne 'pass') { throw "$case parity failed." }
    $exactCase = $case -in @('exact', 'descriptor_order', 'exact_duplicate', 'exact_effect_duplicate')
    if ($exactCase -and ($full.ArtifactEvidenceVerdict -cne 'pass' -or (-not $active -and @($full.Boundaries | Where-Object {
        $_.boundaryId -ceq $compensation.transitionId -and $_.verdict -ceq 'fail'
    }).Count -gt 0))) { throw "$case exact compensation catalog rejected: $($full.ArtifactEvidenceFailureReason)" }
    if ($active -and $exactCase) {
        $commitBoundary = @($full.Boundaries | Where-Object boundaryId -CEQ $commit.transitionId)
        $restoredBoundary = @($full.Boundaries | Where-Object boundaryId -CEQ $compensation.transitionId)
        if ($commitBoundary.Count -ne 1 -or $commitBoundary[0].verdict -cne 'pass' -or
            $commitBoundary[0].rawCount -ne 0 -or $commitBoundary[0].distinctCount -ne 0 -or
            $commitBoundary[0].recoveredCount -ne 0 -or $commitBoundary[0].unresolvedCount -ne 0) {
            throw 'Exactly cancelled provisional intent still requires a physical settlement effect.'
        }
        if (-not $settled -and ($restoredBoundary.Count -ne 1 -or $restoredBoundary[0].verdict -cne 'fail' -or
            $restoredBoundary[0].taxonomyClass -cne 'runtime_unlock_retry_dispatch_contract_unsettled' -or
            $restoredBoundary[0].rawCount -ne 1 -or $restoredBoundary[0].distinctCount -ne 1 -or
            $restoredBoundary[0].recoveredCount -ne 0 -or $restoredBoundary[0].unresolvedCount -ne 1 -or
            $full.FirstUnresolvedBoundary.boundaryId -cne $compensation.transitionId -or
            @($full.Boundaries | Where-Object verdict -CEQ 'fail').Count -ne 1)) {
            throw 'Exact compensation failed to preserve restored A as the sole unresolved open authority.'
        }
        if ($settled -and ($restoredBoundary.Count -ne 1 -or $restoredBoundary[0].verdict -cne 'pass' -or
            @($full.Boundaries | Where-Object verdict -CEQ 'fail').Count -ne 0 -or $null -ne $full.FirstUnresolvedBoundary)) {
            throw 'A genuine later dispatch and its own exact receipt-backed observation failed to close restored A.'
        }
        if ($settled) {
            $evictionBoundary = @($full.Boundaries | Where-Object boundaryId -CEQ $ownSettlementEnvelope.transitionId)
            if ($evictionBoundary.Count -ne 1 -or $evictionBoundary[0].verdict -cne 'pass' -or
                $evictionBoundary[0].rawCount -ne 0 -or $evictionBoundary[0].distinctCount -ne 0 -or
                $evictionBoundary[0].recoveredCount -ne 0 -or $evictionBoundary[0].unresolvedCount -ne 0) {
                throw 'Exact historical B eviction must be clean and must not count B as recovered.'
            }
        }
    }
    if ($case -in @('missing_install', 'missing_install_reordered') -and @($full.Boundaries | Where-Object {
        $_.boundaryId -ceq $commit.transitionId -and $_.verdict -ceq 'fail' -and
        $_.taxonomyClass -ceq 'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete'
    }).Count -ne 1) { throw 'Original failed-confirmation commit lost its install effect requirement.' }
    if ($case -ceq 'missing_original_install') {
        $originalInstall = @($envelopes | Where-Object {
            $_.decisionKind -ceq 'CommitRuntimeUnlockRetryDispatchInstall' -and
            @($_.aggregate.operations | Where-Object {
                $_.operationRole -ceq 'attempt_installed' -and $_.authorityId -ceq $shape.Restored[0].authorityId -and
                $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Installed'
            }).Count -eq 1
        })
        if ($originalInstall.Count -ne 1 -or @($full.Boundaries | Where-Object {
            $_.boundaryId -ceq $originalInstall[0].transitionId -and $_.verdict -ceq 'fail' -and
            $_.taxonomyClass -ceq 'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete'
        }).Count -ne 1) { throw 'Original A installation proof was borrowed from its restored role.' }
    }
    if ($case -in @('no_compensation', 'rejected_compensation_only', 'idempotent_compensation_only')) {
        if ($full.ArtifactEvidenceVerdict -cne 'pass' -or @($full.Boundaries | Where-Object {
            $_.boundaryId -ceq $commit.transitionId -and $_.verdict -ceq 'fail' -and
            $_.taxonomyClass -ceq 'runtime_unlock_retry_dispatch_contract_unsettled' -and
            $_.rawCount -eq 1 -and $_.distinctCount -eq 1 -and $_.recoveredCount -eq 0 -and $_.unresolvedCount -eq 1
        }).Count -ne 1) { throw "$case falsely cancelled the provisional settlement obligation." }
    }
    if ($case -in @('missing_commit_gap', 'changed_retained_receipt', 'source_poison', 'terminal_poison',
        'missing_restored_role', 'conflicting_duplicate') -and $full.ArtifactEvidenceVerdict -cne 'fail') {
        throw "$case accepted unbound or poisoned compensation history."
    }
    if ($case -in @('post_compensation_data_scope_aba', 'post_compensation_terminal_scope_aba', 'arbitrary_closed_prefix',
        'skipped_alias_execution', 'reversed_execution_prefix', 'foreign_terminal_occupancy_aba')) {
        if ($full.ArtifactEvidenceVerdict -cne 'fail' -or
            $full.ArtifactEvidenceFailureReason -match 'snapshot_operation_catalog_mismatch|snapshot_generation_or_hash_gap|snapshot_generation_transition_invalid|snapshot_header_payload_' -or
            @($full.Boundaries | Where-Object { $_.recoveredCount -ne 0 }).Count -ne 0) {
            throw "$case did not reject the participating-history attack independently of catalog/header validity."
        }
    }
    if (($evictionMutations.Contains($case) -or $case -in @('missing_b_terminal_origin', 'competing_b_terminal_origin')) -and
        ($full.ArtifactEvidenceVerdict -cne 'fail' -or @($full.Boundaries | Where-Object { $_.recoveredCount -ne 0 }).Count -ne 0)) {
        throw "$case borrowed an unrelated or altered invalidated terminal history."
    }
    if ($case -in @('missing_restored_execution', 'missing_restored_settlement')) {
        # SendInFlight still has dispatchObserved=false. Both own-observation
        # obligations mature at A's DispatchObserved closure, not the send row.
        $expected = $ownSettlementEnvelope.transitionId
        $missingOwnProof = $full.FirstUnresolvedBoundary
        if ($full.ArtifactEvidenceVerdict -cne 'pass' -or $null -eq $missingOwnProof -or
            $missingOwnProof.boundaryId -cne $expected -or
            $missingOwnProof.taxonomyClass -cne 'runtime_unlock_retry_dispatch_contract_unsettled' -or
            $missingOwnProof.rawCount -ne 1 -or $missingOwnProof.distinctCount -ne 1 -or
            $missingOwnProof.recoveredCount -ne 0 -or $missingOwnProof.unresolvedCount -ne 1 -or
            @($full.Boundaries | Where-Object verdict -CEQ 'fail').Count -ne 1) {
            throw "$case must select exactly A's own unobserved obligation, not a spurious B install finding."
        }
    }
    if ($case -ceq 'forged_eviction_install' -and
        $full.ArtifactEvidenceFailureReason -notlike '*snapshot_retry_confirmation_eviction_install_effect_forbidden:*') {
        throw 'Eviction accepted a fabricated Invalidated installation effect.'
    }
    if ($case -in @('mutated_original_b_effect', 'conflicting_original_b_effect') -and
        $full.ArtifactEvidenceVerdict -cne 'fail' -and @($full.Boundaries | Where-Object {
            $_.boundaryId -ceq $commit.transitionId -and $_.verdict -ceq 'fail' -and
            $_.taxonomyClass -ceq 'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete' -and $_.recoveredCount -eq 0
        }).Count -ne 1) { throw "$case substituted for B's exact original installation proof." }
    if ($case -ceq 'forged_compensation_effect' -and $full.ArtifactEvidenceFailureReason -notlike '*snapshot_retry_confirmation_compensation_effect_forbidden:*') {
        throw 'Compensation accepted a fabricated effect.'
    }
    if ($case -ceq 'missing_sibling' -and ($full.ArtifactEvidenceVerdict -cne 'fail' -or
        $full.FirstUnresolvedBoundary.taxonomyClass -cne 'filetransfer_coordinator_snapshot_evidence_invalid' -or
        $full.FirstUnresolvedBoundary.referencedTransitionId -cne $commit.transitionId -or
        $full.FirstUnresolvedBoundary.variant -cne
            'provisional_active_predecessor_superseded_receipt_omitted_from_family2_catalog')) {
        throw 'Missing active-commit sibling escaped the precise catalog failure.'
    }
    $boundary = $full.FirstUnresolvedBoundary
    $guiShadowFallback = $null
    $guiShadowFallbackJson = $null
    $metadata = Selected-Metadata $boundary
    $metadataJson = $metadata | ConvertTo-Json -Depth 8 -Compress
    if ($metadataJson -cne ((Selected-Metadata $compact.FirstUnresolvedBoundary) | ConvertTo-Json -Depth 8 -Compress)) {
        throw "$case full/compact selected metadata diverged."
    }
    if ($case -ceq 'exact') { $exactMetadata = $metadataJson }
    if ($case -ceq 'missing_install') { $missingInstallMetadata = $metadataJson }
    if ($case -in @('exact_duplicate', 'exact_effect_duplicate') -and $metadataJson -cne $exactMetadata) {
        throw "$case changed the exact selected boundary or quartet."
    }
    if ($case -in @('descriptor_order', 'missing_install_reordered')) {
        # The payload-derived hash covers array order. Compare the selected
        # identity/quartet across different payloads; above and below require
        # exact hashes between full, compact and GUI for each identical input.
        $referenceMetadata = if ($case -ceq 'descriptor_order') { $exactMetadata } else { $missingInstallMetadata }
        $originalSelection = $referenceMetadata | ConvertFrom-Json
        if (($null -eq $metadata) -ne ($null -eq $originalSelection)) { throw 'Descriptor reordering changed whether an unresolved boundary exists.' }
        if ($null -ne $metadata) {
            foreach ($field in @('BoundaryId', 'Class', 'Phase', 'Writer', 'WriterSequence',
                    'Raw', 'Distinct', 'Recovered', 'Unresolved')) {
                if ($metadata[$field] -cne $originalSelection.$field) {
                    throw "Descriptor reordering changed selected $field."
                }
            }
        }
    }
    if ($null -ne $boundary -or $settled) {
        foreach ($form in @('full', 'compact')) {
            $path = Join-Path $OutputRoot "$case-$form.log"
            $guiLines = if ($form -ceq 'full') { @('event=unrelated') + $lines } else { $compactLines }
            [IO.File]::WriteAllLines($path, [string[]]$guiLines)
            $environmentBefore = [ordered]@{}
            foreach ($name in @('LOCALAPPDATA', 'NLINK_FILETRANSFER_GUI_SCENARIO_DEADLINE_REQUEST_PATH', 'NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR')) {
                $environmentBefore[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
            }
            try {
                # Invoke real readiness/ledger code, but never consult ambient
                # live logs, cancellation files, or artifact destinations.
                [Environment]::SetEnvironmentVariable('LOCALAPPDATA', $emptyAppData, 'Process')
                [Environment]::SetEnvironmentVariable('NLINK_FILETRANSFER_GUI_SCENARIO_DEADLINE_REQUEST_PATH', $null, 'Process')
                [Environment]::SetEnvironmentVariable('NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR', $OutputRoot, 'Process')
                $script:GuiSmokeAppLogLedger = New-GuiSmokeLogLedgerState
                $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'diagnostic evidence evaluation' `
                    -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
            }
            finally {
                foreach ($name in $environmentBefore.Keys) {
                    [Environment]::SetEnvironmentVariable($name, $environmentBefore[$name], 'Process')
                }
            }
            if ($null -eq $boundary) {
                # Family-2-only Shadow evidence has no selected Coordinator
                # boundary. The real GUI returns its coarse fallback shape;
                # matching that result is not an authoritative GUI-pass claim.
                if ($null -ne $gui.PSObject.Properties['CoordinatorBoundaryId'] -or
                    -not $gui.ClassificationComplete -or $gui.ClassificationSource -cne 'immutable_evidence') {
                    throw "$case $form did not return the actual unselected Shadow GUI fallback."
                }
                $fallback = [ordered]@{}
                foreach ($property in @($gui.PSObject.Properties | Sort-Object Name)) {
                    if ($property.Name -notin @('EvidenceLineVisitCount', 'EvidenceParsedRecordCount', 'EvidenceLookupCount', 'ClassificationElapsedMilliseconds')) {
                        $fallback[$property.Name] = $property.Value
                    }
                }
                $fallbackJson = $fallback | ConvertTo-Json -Depth 8 -Compress
                if ($null -ne $guiShadowFallbackJson -and $guiShadowFallbackJson -cne $fallbackJson) {
                    throw "$case full/compact Shadow GUI fallback semantics diverged."
                }
                $guiShadowFallback = $fallback; $guiShadowFallbackJson = $fallbackJson
                continue
            }
            if ($gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or
                $gui.CoordinatorWriter -cne $boundary.writer -or $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or
                $gui.CoordinatorRawCount -ne $boundary.rawCount -or $gui.CoordinatorDistinctCount -ne $boundary.distinctCount -or
                $gui.CoordinatorRecoveredCount -ne $boundary.recoveredCount -or $gui.CoordinatorUnresolvedCount -ne $boundary.unresolvedCount) {
                throw "$case $form GUI metadata diverged."
            }
            if ($full.ArtifactEvidenceVerdict -ceq 'pass' -and
                ($gui.FailureClass -cne $boundary.taxonomyClass -or $gui.Phase -cne $boundary.phase)) {
                throw "$case $form GUI semantic classification diverged."
            }
        }
    }
    $unresolvedBoundaryCount = @($full.Boundaries | Where-Object verdict -CEQ 'fail').Count
    $summaries += [ordered]@{ Case=$case; Artifact=$full.ArtifactEvidenceVerdict;
        Unresolved=$unresolvedBoundaryCount;
        Selected=$metadata;
        GuiShadowFallback=$guiShadowFallback;
        ActiveMutationCount=$activeMutationCount }
}
Write-Output ('retry-confirmation-compensation-normalized=' + ($summaries | ConvertTo-Json -Depth 8 -Compress))
