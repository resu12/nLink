param([string]$RepoRoot, [string]$InputPath, [string]$OutputRoot,
    [string]$SupersessionTransitionId, [string]$FailedTransitionId, [string]$PriorTerminalTransitionId)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion 'snapshot-authority-v1-receiver-barrier-v1'
if ($catalog.Hash -cne 'e507b186e037924e5785e563742fedd9111552684fc8cc2f46eef38225b0bb94') { throw 'Receiver catalog changed.' }
$legacy = Get-FileTransferCoordinatorEvidenceCatalog
if ($legacy.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') { throw 'Frozen catalog changed.' }
foreach ($source in @(
    @{ Path='tests/nLink.SmokeTests.Core/Support/CheckpointResponderCoexistenceSnapshotEvidence.ps1'; Names=@('Copy-Value','Decode','Replace-Envelope','Boundary-Semantics') },
    @{ Path='tools/GuiSmoke-Windows.ps1'; Names=@('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification','Get-TunaGuiFileTransferSetupFailureClassification') })) {
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot $source.Path), [ref]$null, [ref]$null)
    foreach ($name in $source.Names) {
        $definitions = @($ast.FindAll({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
        }, $true))
        if ($definitions.Count -ne 1) { throw "Shared function is not unique: $name" }
        . ([scriptblock]::Create($definitions[0].Extent.Text))
    }
}
function Subject($Envelope, [string]$Role, [string]$Kind) {
    $matches = @($Envelope.aggregate.operations | Where-Object {
        $_.family -ceq 'fallback_tail_yield' -and $_.operationRole -ceq $Role -and $_.operationKind -ceq $Kind
    })
    if ($matches.Count -ne 1) { throw "Production fixture subject not unique: $Role/$Kind" }
    $matches[0]
}
function Receipt($Operation, [string]$Stage) {
    $matches = @($Operation.fallbackTailYield.receipts | Where-Object stage -CEQ $Stage)
    if ($matches.Count -ne 1) { throw "Production fixture receipt not unique: $Stage" }
    $matches[0]
}
function Read-Effect([string]$Line, [int]$LineIndex = 0) {
    $effect = [ordered]@{ LineIndex=$LineIndex }
    foreach ($name in @('CoordinatorInstanceId','TransitionId','OperationKey','OperationGeneration','EffectKind','EffectIdentity',
        'ReceiptIdentity','ReceiptGeneration','ReceiptStatus','ReceiptFingerprint','Route','ProtocolVersion','LiveRouteEpoch',
        'RoutePublicationGeneration','RouteCommitGeneration','RuntimeGeneration','TerminalGeneration','LegGeneration',
        'TransportEpoch','BridgeRecoveryGeneration','CheckpointGeneration','CheckpointRequestId','DataSessionIdentity','DataSessionGeneration')) {
        $field = [regex]::Replace($name, '([a-z])([A-Z])', '$1_$2').ToLowerInvariant()
        $effect[$name] = Get-FileTransferCoordinatorSnapshotLineField $Line $field
    }
    foreach ($field in @{
        Writer='log_writer_id'; WriterSequence='log_writer_sequence'; StateGeneration='coordinator_state_generation'
        Family='operation_family'; AuthorityId='operation_authority_id'
    }.GetEnumerator()) { $effect[$field.Key] = Get-FileTransferCoordinatorSnapshotLineField $Line $field.Value }
    [pscustomobject]$effect
}
function Exact-Snapshot([string]$Id) {
    $matches = @($snapshots | Where-Object { $_.Envelope.transitionId -ceq $Id })
    if ($matches.Count -ne 1) { throw "Production fixture transition not unique: $Id" }
    $matches[0]
}
$originalHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([IO.File]::ReadAllBytes($InputPath))
$original = [IO.File]::ReadAllLines($InputPath)
$snapshots = @(for ($i = 0; $i -lt $original.Count; $i++) {
    if ((Get-FileTransferCoordinatorSnapshotEventName $original[$i]) -ceq 'filetransfer_coordinator_transition_snapshot') {
        [pscustomobject]@{ Index=$i; Envelope=(Decode $original[$i]) }
    }
})
$candidate = Exact-Snapshot $SupersessionTransitionId
$failed = Exact-Snapshot $FailedTransitionId
$birth = Exact-Snapshot $PriorTerminalTransitionId
$envelopes = @($snapshots | ForEach-Object Envelope)
$old = Subject $candidate.Envelope 'yield_terminal' 'Quiescent'
$successor = Subject $candidate.Envelope 'yield_current' 'Quiescent'
$oldBirth = Subject $birth.Envelope 'yield_terminal' 'Quiescent'
if ($candidate.Envelope.aggregate.operations.Count -ne 4 -or $old.beforeState -cne 'Invalidated' -or
    $old.afterState -cne 'None' -or $oldBirth.beforeState -cne 'None' -or $oldBirth.afterState -cne 'Invalidated') {
    throw 'Missing genuine four-operation terminal-slot replacement.'
}
if ($old.fallbackTailYield.sourceBoundary.transportEpoch -eq $successor.fallbackTailYield.sourceBoundary.transportEpoch -or
    $old.fallbackTailYield.localProofIdentity -ceq '(none)' -or $old.fallbackTailYield.pairedReleaseAuthorityId -ceq '(none)' -or
    $old.fallbackTailYield.reservationGeneration -le 0 -or $old.fallbackTailYield.consumingInstallGeneration -le 0 -or
    $old.fallbackTailYield.retryRecheckGeneration -le 0) { throw 'Production-width historical proof/release/install/recheck coverage missing.' }
foreach ($operation in @($candidate.Envelope.aggregate.operations)) {
    if (-not (Test-FileTransferCoordinatorSnapshotFamily4OperationCatalog $candidate.Envelope $operation)) {
        throw 'Exact four-operation production catalog rejected.'
    }
}
$immutable = ConvertTo-Json -InputObject $envelopes -Depth 64 -Compress
$candidateEffects = @($original | Where-Object {
    (Get-FileTransferCoordinatorSnapshotEventName $_) -ceq 'filetransfer_coordinator_authority_effect' -and
    (Get-FileTransferCoordinatorSnapshotLineField $_ transition_id) -ceq $SupersessionTransitionId
})
if ($candidateEffects.Count -ne 1 -or
    (Get-FileTransferCoordinatorSnapshotLineField $candidateEffects[0] effect_kind) -cne 'recovery_owner_retired_after_yield' -or
    (Get-FileTransferCoordinatorSnapshotLineField $candidateEffects[0] operation_key) -cne $successor.operationKey -or
    (Get-FileTransferCoordinatorSnapshotLineField $candidateEffects[0] receipt_identity) -cne (Receipt $successor 'yield').receiptIdentity) {
    throw 'Production fresh-yield projector did not select only the exact successor yield receipt.'
}
$oldEffects = @($original | Where-Object {
    (Get-FileTransferCoordinatorSnapshotLineField $_ effect_kind) -ceq 'recovery_owner_retired_after_yield' -and
    (Get-FileTransferCoordinatorSnapshotLineField $_ receipt_identity) -ceq (Receipt $old 'yield').receiptIdentity
})
if ($oldEffects.Count -ne 1) { throw 'Historical yield must retain exactly one original recovery-owner retirement effect.' }
$oldEffect = Read-Effect $oldEffects[0]
$oldIssuance = Exact-Snapshot $oldEffect.TransitionId
$oldCurrent = Subject $oldIssuance.Envelope 'yield_current' 'Quiescent'
$oldBoundary = [pscustomobject]@{
    coordinatorInstanceId=$oldIssuance.Envelope.coordinatorInstanceId; boundaryId=$oldIssuance.Envelope.transitionId
    afterStateGeneration=$oldIssuance.Envelope.aggregate.afterStateGeneration
}
if ($oldCurrent.authorityId -cne $old.authorityId -or $oldCurrent.operationGeneration -ne $old.operationGeneration -or
    $oldEffect.TransitionId -ceq $SupersessionTransitionId -or $oldEffect.Writer -cne 'failed-quiescent-rollover-fixture' -or
    -not (Test-FileTransferCoordinatorSnapshotFallbackTailReceiptEqual (Receipt $oldCurrent 'yield') (Receipt $old 'yield')) -or
    -not (Test-FileTransferCoordinatorSnapshotExactEffectBinding $oldBoundary $oldCurrent $oldEffect)) {
    throw 'Historical yield effect lost its original exact issuance/receipt ownership.'
}
$pendingOrigins = @($snapshots | Where-Object {
    $_.Envelope.decisionKind -ceq 'ApplyFallbackTailPeerProofReceipt' -and
    @($_.Envelope.aggregate.operations | Where-Object operationRole -CEQ 'peer_proof_pending_initial').Count -eq 1
})
if ($pendingOrigins.Count -ne 1) { throw 'Missing unique genuine pending peer-proof origin.' }
$pending = Subject $pendingOrigins[0].Envelope 'peer_proof_pending_initial' 'PeerProof'
$peerEffects = @($original | Where-Object {
    (Get-FileTransferCoordinatorSnapshotLineField $_ effect_kind) -ceq 'peer_proof_observed' -and
    (Get-FileTransferCoordinatorSnapshotLineField $_ receipt_identity) -ceq (Receipt $successor 'peer_proof').receiptIdentity
})
if ($peerEffects.Count -ne 1 -or (Get-FileTransferCoordinatorSnapshotLineField $peerEffects[0] operation_key) -cne $pending.operationKey) {
    throw 'Pending proof must have exactly one original effect, not a fabricated Failed/successor effect.'
}

# Cheap strict shape controls run before the independently evaluated consumers.
# History-only descriptor changes are checked below against the slot reducer.
$shapeCases = @('duplicate_old','competing_old','wrong_role','wrong_kind','wrong_authority','wrong_generation','wrong_key',
    'wrong_lifecycle','not_applied','missing_typed','missing_receipt','wrong_receipt','missing_typed_receipts_property',
    'missing_scope_property','successor_typed_null','string_state_applied','string_receipt_validated','string_stage_receipt_validated',
    'missing_top_receipt_validated','missing_top_typed')
foreach ($case in $shapeCases) {
    $bad = Copy-Value $candidate.Envelope
    $terminal = Subject $bad 'yield_terminal' 'Quiescent'
    switch -CaseSensitive ($case) {
        'duplicate_old' { $bad.aggregate.operations += (Copy-Value $terminal) }
        'competing_old' {
            $other = Copy-Value $terminal; $other.authorityId = 'fte1-' + ('a' * 24)
            $other.operationKey = $other.operationKey.Replace($terminal.authorityId, $other.authorityId)
            $bad.aggregate.operations += $other
        }
        'wrong_role' { $terminal.operationRole = 'yield_current' }
        'wrong_kind' { $terminal.operationKind = 'Failed' }
        'wrong_authority' { $terminal.authorityId = $successor.authorityId }
        'wrong_generation' { $terminal.operationGeneration = $successor.operationGeneration }
        'wrong_key' { $terminal.operationKey += '-changed' }
        'wrong_lifecycle' { $terminal.beforeState = 'Released' }
        'not_applied' { $terminal.stateApplied = $false }
        'missing_typed' { $terminal.fallbackTailYield = $null }
        'missing_receipt' { $terminal.fallbackTailYield.receipts = @() }
        'wrong_receipt' { (Receipt $terminal 'invalidation').receiptFingerprint += '-changed' }
        'missing_typed_receipts_property' { $terminal.fallbackTailYield.PSObject.Properties.Remove('receipts') }
        'missing_scope_property' { $terminal.fallbackTailYield.PSObject.Properties.Remove('sourceBoundary') }
        'successor_typed_null' { (Subject $bad 'yield_current' 'Quiescent').fallbackTailYield = $null }
        'string_state_applied' { $terminal.stateApplied = 'false' }
        'string_receipt_validated' { $terminal.receiptValidated = 'false' }
        'string_stage_receipt_validated' { (Receipt $terminal 'invalidation').receiptValidated = 'false' }
        'missing_top_receipt_validated' { $terminal.PSObject.Properties.Remove('receiptValidated') }
        'missing_top_typed' { $terminal.PSObject.Properties.Remove('fallbackTailYield') }
    }
    if ($null -ne (Get-FileTransferCoordinatorSnapshotFallbackTailFailedSupersessionShape -Operations @($bad.aggregate.operations))) {
        throw "Malformed terminal-slot rollover shape admitted: $case"
    }
}

function New-RolloverHistory([object[]]$Values) {
    foreach ($envelope in $Values) {
        [pscustomobject]@{
            Boundary = [pscustomobject]@{
                coordinatorInstanceId=$envelope.coordinatorInstanceId; writer='failed-quiescent-rollover-fixture'; boundaryId=$envelope.transitionId
                writerSequence=$envelope.aggregate.afterStateGeneration; sessionId=$envelope.sessionId
                transferId=$envelope.transferId; direction=$envelope.direction; families=$envelope.aggregate.changedFamilies
                decisionUtcMilliseconds=$envelope.decisionUtcMilliseconds
                beforeStateGeneration=$envelope.aggregate.beforeStateGeneration; afterStateGeneration=$envelope.aggregate.afterStateGeneration
                beforeStateHash=$envelope.aggregate.beforeStateFingerprint; afterStateHash=$envelope.aggregate.afterStateFingerprint
                decisionKind=$envelope.decisionKind; decisionStatus=$envelope.decisionStatus; decisionReason=$envelope.decisionReason
                operations=$envelope.aggregate.operations; evidenceLineIndex=$envelope.aggregate.afterStateGeneration
            }
            BeforeBoundary=Copy-Value $envelope.aggregate.beforeBoundary; AfterBoundary=Copy-Value $envelope.aggregate.afterBoundary
            SessionId=$envelope.sessionId; TransferId=$envelope.transferId; Direction=$envelope.direction
        }
    }
}
$historyEffects = @(Read-Effect $candidateEffects[0] ($candidate.Index + 1))
function Displacement-Key($Entry) {
    "$($Entry.Boundary.coordinatorInstanceId)|$($Entry.Boundary.writer)|$(Get-FileTransferCoordinatorSnapshotFallbackTailYieldEntryKey -Entry $Entry)"
}
function Assert-History([object[]]$Entries, [bool]$Valid, [string]$Case, [bool]$MarkerExpected = $Valid) {
    $candidates = @{}; $candidates[$SupersessionTransitionId] = @{ Line=$candidate.Envelope.aggregate.afterStateGeneration }
    $caseEffects = @((Copy-Value $historyEffects[0]))
    $candidateEntries = @($Entries | Where-Object { $_.Boundary.boundaryId -ceq $SupersessionTransitionId })
    if ($candidateEntries.Count -eq 1) {
        # Direct hostile histories may renumber aggregate generations. Keep the
        # supporting yield effect exact so an ABA fails ancestry, not binding.
        $caseEffects[0].StateGeneration = $candidateEntries[0].Boundary.afterStateGeneration
        $yield = @($candidateEntries[0].Boundary.operations | Where-Object {
            $_.operationRole -ceq 'yield_current' -and $_.operationKind -ceq 'Quiescent'
        })
        if ($yield.Count -ne 1 -or -not (Test-FileTransferCoordinatorSnapshotExactEffectBinding `
            -Boundary $candidateEntries[0].Boundary -Operation $yield[0] -Effect $caseEffects[0])) {
            throw "Direct history control lost exact successor yield effect: $Case"
        }
    }
    $displaced = @{}
    $failures = @(Resolve-FileTransferCoordinatorFailedCheckpointYieldCandidates -Candidates @{} -SupersessionCandidates $candidates `
        -AggregateIndex (New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries $Entries) `
        -InvalidBoundaries @{} -Effects $caseEffects -AllBoundaries @($Entries | ForEach-Object Boundary) `
        -ValidatedDisplacedTerminals $displaced)
    if (($failures.Count -eq 0) -ne $Valid) { throw "Terminal-slot history ${Case}: $($failures -join ';')" }
    if (-not $Valid -and @($failures | Where-Object {
        $_ -notlike '*|failed_to_quiescent_terminal_slot_rollover_history_unproven:*'
    }).Count -ne 0) { throw "Terminal-slot history lost precise diagnostic: $Case" }
    if ($displaced.Count -ne [int]$MarkerExpected) { throw "Terminal-slot history published an inexact displacement marker: $Case" }
    if ($MarkerExpected) {
        $subject = [pscustomobject]@{ Boundary=$candidateEntries[0].Boundary; Operation=$old }
        if (-not $displaced.ContainsKey((Displacement-Key $subject)) -or $displaced[(Displacement-Key $subject)] -cne $true) {
            throw "Accepted terminal-slot history lost exact scoped displacement marker: $Case"
        }
        foreach ($field in @('coordinatorInstanceId','writer','boundaryId','operationKey','operationRole','beforeState','afterState')) {
            $other = Copy-Value $subject
            if ($field -cin @('coordinatorInstanceId','writer','boundaryId')) { $other.Boundary.$field += '-other' }
            else { $other.Operation.$field += '-other' }
            if ($displaced.ContainsKey((Displacement-Key $other))) { throw "Displacement marker leaked across ${field}: $Case" }
        }
    }
}
Assert-History @(New-RolloverHistory $envelopes) $true 'production'
Assert-History @() $false 'empty'
Assert-History @(New-RolloverHistory @($candidate.Envelope)) $false 'singleton'
$historyCases = @('missing_birth','missing_current_mirror','missing_old_retirement','immutable_payload','immutable_receipt',
    'wrong_source','wrong_effective','wrong_data_session','historical_scope','generation_gap','hash_gap','replacement',
    'terminal_poison','transfer_scope','wrong_coordinator','wrong_writer','wrong_authority','wrong_generation','wrong_operation',
    'duplicate_conflict','terminal_overwrite_aba','terminal_retire_aba','current_mutation_aba')
foreach ($case in $historyCases) {
    $entries = @(New-RolloverHistory (Copy-Value $envelopes))
    $origin = @($entries | Where-Object { $_.Boundary.boundaryId -ceq $PriorTerminalTransitionId })[0]
    $issuance = @($entries | Where-Object { $_.Boundary.boundaryId -ceq $FailedTransitionId })[0]
    $rollover = @($entries | Where-Object { $_.Boundary.boundaryId -ceq $SupersessionTransitionId })[0]
    $terminal = @($rollover.Boundary.operations | Where-Object { $_.operationKey -ceq $old.operationKey })[0]
    switch -CaseSensitive ($case) {
        'missing_birth' { $origin.Boundary.operations = @($origin.Boundary.operations | Where-Object { $_.operationKey -cne $old.operationKey }) }
        'missing_current_mirror' { $origin.Boundary.operations = @($origin.Boundary.operations | Where-Object {
            -not ($_.family -ceq 'fallback_tail_yield' -and $_.operationRole -ceq 'yield_current' -and $_.authorityId -ceq $old.authorityId)
        }) }
        'missing_old_retirement' { $rollover.Boundary.operations = @($rollover.Boundary.operations | Where-Object { $_.operationKey -cne $old.operationKey }) }
        'immutable_payload' { $terminal.fallbackTailYield.ownerKey += '-changed' }
        'immutable_receipt' {
            $terminal.receiptFingerprint += '-changed'
            (Receipt $terminal 'invalidation').receiptFingerprint = $terminal.receiptFingerprint
        }
        'wrong_source' { $terminal.fallbackTailYield.sourceBoundary.transportEpoch++ }
        'wrong_effective' { $terminal.fallbackTailYield.effectiveBoundary.transportEpoch++ }
        'wrong_data_session' { $terminal.fallbackTailYield.sourceBoundary.dataSessionGeneration++ }
        'historical_scope' {
            $originTerminal = @($origin.Boundary.operations | Where-Object { $_.operationKey -ceq $old.operationKey })[0]
            foreach ($operation in @($originTerminal,$terminal)) {
                foreach ($field in @('sourceBoundary','effectiveBoundary','targetBoundary')) {
                    $operation.fallbackTailYield.$field.transportEpoch++
                }
            }
        }
        'generation_gap' { $rollover.Boundary.beforeStateGeneration++ }
        'hash_gap' { $rollover.Boundary.beforeStateHash += '-gap' }
        'replacement' { $issuance.AfterBoundary.dataSessionGeneration++; $rollover.BeforeBoundary.dataSessionGeneration++ }
        'terminal_poison' { $issuance.AfterBoundary.terminalGeneration++; $rollover.BeforeBoundary.terminalGeneration++ }
        'transfer_scope' { $rollover.TransferId += '-replacement' }
        'wrong_coordinator' { $origin.Boundary.coordinatorInstanceId += '-other' }
        'wrong_writer' { $origin.Boundary.writer += '-other' }
        'wrong_authority' { $terminal.authorityId += '-other' }
        'wrong_generation' { $terminal.operationGeneration++ }
        'wrong_operation' { $terminal.operationKey += '-other' }
        'duplicate_conflict' {
            $duplicate = Copy-Value $origin; $duplicate.Boundary.boundaryId += '-competing'
            $entries += $duplicate
        }
        default {
            # These deliberately hostile reducer observations are not claimed
            # production transitions. Even a restored byte-exact terminal may
            # not erase an intervening slot overwrite or retirement.
            $changed = Copy-Value $issuance; $restored = Copy-Value $issuance
            $changed.Boundary.boundaryId += '-changed'; $restored.Boundary.boundaryId += '-restored'
            $changed.Boundary.decisionKind = 'SyntheticHistoryMutation'; $restored.Boundary.decisionKind = 'SyntheticHistoryRestore'
            foreach ($entry in @($changed,$restored)) {
                $entry.BeforeBoundary = Copy-Value $issuance.AfterBoundary; $entry.AfterBoundary = Copy-Value $issuance.AfterBoundary
            }
            $currentMutation = $case -ceq 'current_mutation_aba'
            $source = if ($currentMutation) { @($issuance.Boundary.operations | Where-Object operationRole -CEQ 'yield_current')[0] } else { $old }
            $changedOp = Copy-Value $source; $restoredOp = Copy-Value $source
            $state = if ($currentMutation) { 'PeerProofObserved' } else { 'Invalidated' }
            $changedOp.beforeState = $state; $changedOp.afterState = $state
            $restoredOp.beforeState = $state; $restoredOp.afterState = $state
            if ($case -ceq 'terminal_retire_aba') { $changedOp.afterState = 'None'; $restoredOp.beforeState = 'None' }
            else { $changedOp.fallbackTailYield.ownerKey += '-changed' }
            $changed.Boundary.operations = @($changedOp); $restored.Boundary.operations = @($restoredOp)
            $entries = @($entries | Where-Object { $_.Boundary.afterStateGeneration -le $issuance.Boundary.afterStateGeneration }) + @($changed,$restored,$rollover)
            for ($i = 0; $i -lt $entries.Count; $i++) {
                $entries[$i].Boundary.beforeStateGeneration = $i; $entries[$i].Boundary.afterStateGeneration = $i + 1
                $entries[$i].Boundary.beforeStateHash = "hostile-history-$i"; $entries[$i].Boundary.afterStateHash = "hostile-history-$($i + 1)"
            }
        }
    }
    Assert-History $entries $false $case
}

foreach ($case in @('wrong_decision','rejected_decision')) {
    $entries = @(New-RolloverHistory (Copy-Value $envelopes))
    $rollover = @($entries | Where-Object { $_.Boundary.boundaryId -ceq $SupersessionTransitionId })[0]
    if ($case -ceq 'wrong_decision') { $rollover.Boundary.decisionKind = 'ApplyFallbackTailPeerProofReceipt' }
    else { $rollover.Boundary.decisionStatus = 'Rejected' }
    # These are deliberately caller-injected candidates, not snapshots that
    # the real evaluator queues. They must not receive semantic authorization.
    Assert-History $entries $true $case $false
}

foreach ($case in @('unchanged_terminal_neutral','terminal_neutral_aba','terminal_neutral_hash','terminal_neutral_state','terminal_neutral_applied')) {
    $entries = @(New-RolloverHistory (Copy-Value $envelopes))
    $anchor = @($entries | Where-Object { $_.Boundary.boundaryId -ceq $FailedTransitionId })[0]
    $observation = Copy-Value $anchor
    $observation.Boundary.boundaryId += '-neutral'
    $observation.Boundary.beforeStateGeneration = $anchor.Boundary.afterStateGeneration
    $observation.Boundary.beforeStateHash = $anchor.Boundary.afterStateHash
    $observation.BeforeBoundary = Copy-Value $anchor.AfterBoundary
    $terminal = Copy-Value $old
    $terminal.afterState = $terminal.beforeState; $terminal.stateApplied = $false
    $observation.Boundary.operations = @($terminal)
    $restored = Copy-Value $observation; $restored.Boundary.boundaryId += '-restored'
    switch -CaseSensitive ($case) {
        'terminal_neutral_aba' { $terminal.fallbackTailYield.ownerKey += '-mutated' }
        'terminal_neutral_hash' { $observation.Boundary.afterStateHash += '-wrong' }
        'terminal_neutral_state' { $terminal.beforeState = 'Released'; $terminal.afterState = 'Released' }
        'terminal_neutral_applied' { $terminal.stateApplied = $true }
    }
    # The byte-exact restoration must not erase poison from the preceding
    # neutral observation. Unchanged neutral retention remains valid.
    $entries += @($observation,$restored)
    Assert-History $entries ($case -ceq 'unchanged_terminal_neutral') $case
}

# Already rejected candidates cannot publish a marker even when their shape is
# byte-identical to production and there is no new reducer diagnostic to emit.
$entries = @(New-RolloverHistory (Copy-Value $envelopes))
$displaced = @{}; $invalid = @{}; $invalid[$SupersessionTransitionId] = $true
$candidates = @{}; $candidates[$SupersessionTransitionId] = @{ Line=1 }
$null = @(Resolve-FileTransferCoordinatorFailedCheckpointYieldCandidates -Candidates @{} -SupersessionCandidates $candidates `
    -AggregateIndex (New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries $entries) -InvalidBoundaries $invalid `
    -Effects $historyEffects -AllBoundaries @($entries | ForEach-Object Boundary) -ValidatedDisplacedTerminals $displaced)
if ($displaced.Count -ne 0) { throw 'Already rejected supersession published a displacement marker.' }

$outputs = [Collections.Generic.List[object]]::new()
$baselineSemantics = $null
$baselineRouteHash = $null
$routePublications = @{}
foreach ($snapshot in $snapshots) {
    foreach ($operation in @($snapshot.Envelope.aggregate.operations | Where-Object {
        $_.family -ceq 'route_runtime' -and $_.operationRole -ceq 'route_publication' -and $_.afterState -ceq 'Published'
    })) {
        $generation = [long]$operation.operationGeneration
        if ($routePublications.ContainsKey($generation)) { throw 'Partial route fixture publication generation is ambiguous.' }
        $routePublications[$generation] = [pscustomobject]@{ Snapshot=$snapshot; Operation=$operation }
    }
}
if ($routePublications.Count -ne 2 -or -not $routePublications.ContainsKey(5L) -or -not $routePublications.ContainsKey(6L)) {
    throw 'Expected the existing partial route fixture publications at generations 5 and 6.'
}
$first = $envelopes[0]
$routeScope = "$($first.coordinatorInstanceId)|failed-quiescent-rollover-fixture|$($first.sessionId)|$($first.transferId)|$($first.direction)"
$expectedRouteFindings = @(
    "snapshot route publication generation is not contiguous: scope=$routeScope; generation=5",
    'snapshot initial route publication has invalid kind or predecessor',
    "snapshot route publication generation is not contiguous: scope=$routeScope; generation=6",
    "snapshot route publication has no exact committed predecessor transition: route=post_tuna_fallback_v6; operation_key=$($routePublications[6L].Operation.operationKey)"
)
$semanticVariant = '(none)'
$missingEffectVariant = 'failed_to_quiescent_supersession_ambiguous_yield_receipts_suppressed_exact_successor_effect'
foreach ($case in @('exact','reordered','exact_duplicate','exact_peer_effect_duplicate','exact_old_yield_effect_duplicate',
    'missing_old_birth','conflicting_snapshot','missing_historical_yield_effect','mismatched_historical_yield_effect',
    'competing_historical_yield_effect','mismatched_historical_yield_receipt','missing_successor_effect','forged_old_effect','reused_old_effect')) {
    Write-Output "failed-quiescent-rollover-case=$case"
    $lines = @($original)
    if ($case -ceq 'reordered') {
        $changed = Copy-Value $candidate.Envelope
        [array]::Reverse($changed.aggregate.operations)
        $lines[$candidate.Index] = Replace-Envelope $lines[$candidate.Index] $changed
    }
    if ($case -ceq 'missing_old_birth') {
        $changed = Copy-Value $birth.Envelope
        $changed.aggregate.operations = @($changed.aggregate.operations | Where-Object { $_.operationKey -cne $old.operationKey })
        $lines[$birth.Index] = Replace-Envelope $lines[$birth.Index] $changed
    }
    if ($case -cin @('exact_duplicate','conflicting_snapshot')) {
        $copy = $lines[$candidate.Index]
        if ($case -ceq 'conflicting_snapshot') {
            $changed = Copy-Value $candidate.Envelope
            (Subject $changed 'yield_terminal' 'Quiescent').fallbackTailYield.ownerKey += '-conflict'
            $copy = Replace-Envelope $copy $changed
        }
        $lines = @($lines[0..$candidate.Index]) + @($copy) + @($lines[($candidate.Index + 1)..($lines.Count - 1)])
    }
    if ($case -ceq 'exact_peer_effect_duplicate') {
        # Byte-identical physical evidence may coalesce, but never becomes a
        # second observation or a new effect on Failed/the successor.
        $peerIndex = [array]::IndexOf($lines, $peerEffects[0])
        if ($peerIndex -lt 0) { throw 'Missing original peer effect for exact-copy control.' }
        $lines = @($lines[0..$peerIndex]) + @($peerEffects[0]) + @($lines[($peerIndex + 1)..($lines.Count - 1)])
    }
    if ($case -cin @('exact_old_yield_effect_duplicate','missing_historical_yield_effect','mismatched_historical_yield_effect','competing_historical_yield_effect')) {
        $oldIndex = [array]::IndexOf($lines, $oldEffects[0])
        if ($oldIndex -lt 0) { throw 'Missing historical yield effect for hostile control.' }
        if ($case -ceq 'missing_historical_yield_effect') { $lines = @($lines | Where-Object { $_ -cne $oldEffects[0] }) }
        elseif ($case -ceq 'mismatched_historical_yield_effect') {
            $lines[$oldIndex] = $lines[$oldIndex].Replace('receipt_fingerprint=' + $oldEffect.ReceiptFingerprint,
                'receipt_fingerprint=' + $oldEffect.ReceiptFingerprint + '-mismatched')
        }
        else {
            $copy = $oldEffects[0]
            if ($case -ceq 'competing_historical_yield_effect') {
                $copy = $copy.Replace('effect_identity=' + $oldEffect.EffectIdentity, 'effect_identity=' + $oldEffect.EffectIdentity + '-competing')
            }
            $lines = @($lines[0..$oldIndex]) + @($copy) + @($lines[($oldIndex + 1)..($lines.Count - 1)])
        }
    }
    if ($case -ceq 'mismatched_historical_yield_receipt') {
        $changed = Copy-Value $candidate.Envelope
        (Receipt (Subject $changed 'yield_terminal' 'Quiescent') 'yield').receiptFingerprint += '-mismatched'
        $lines[$candidate.Index] = Replace-Envelope $lines[$candidate.Index] $changed
    }
    if ($case -ceq 'missing_successor_effect') {
        $lines = @($lines | Where-Object { $_ -cne $candidateEffects[0] })
        if ($lines.Count -ne $original.Count - 1) { throw 'Missing-effect control did not remove exactly one production effect.' }
    }
    if ($case -cin @('forged_old_effect','reused_old_effect')) {
        # Hostile evidence only: transplant the old origin's exact yield effect
        # onto its displaced terminal at the current transition. Never baseline.
        $forged = $oldEffects[0]
        foreach ($field in @{
            transition_id=$SupersessionTransitionId; coordinator_state_generation=$candidate.Envelope.aggregate.afterStateGeneration
            operation_key=$old.operationKey
            effect_identity=$(if ($case -ceq 'reused_old_effect') { $oldEffect.EffectIdentity } else { $old.operationKey + ':forged-old-effect' })
            log_writer_sequence=99999
        }.GetEnumerator()) {
            $forged = [regex]::Replace($forged, ('(?<=\b' + [regex]::Escape($field.Key) + '=)[^;]*'), [string]$field.Value)
        }
        $lines = @($lines[0..($lines.Count - 2)]) + @($forged,$lines[-1])
    }
    $fullLines = @('event=unrelated_fixture_diagnostic; note=independent_compaction') + $lines
    $compactRecords = @(for ($i = 0; $i -lt $fullLines.Count; $i++) {
        $name = Get-FileTransferCoordinatorSnapshotEventName $fullLines[$i]
        if ($catalog.SnapshotAuthorityV1 -contains $name -or $catalog.GenerationStampedEffects -contains $name) {
            [pscustomobject]@{ Line=$fullLines[$i]; LineIndex=$i }
        }
    })
    if ($compactRecords.Count -ge $fullLines.Count) { throw 'Compact records were not independently selected.' }
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $fullLines
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $compactRecords
    $historicalSemanticFailure = $case -cin @('missing_historical_yield_effect','competing_historical_yield_effect')
    $expected = if ($historicalSemanticFailure -or
        $case -cin @('exact','reordered','exact_duplicate','exact_peer_effect_duplicate','exact_old_yield_effect_duplicate')) { 'pass' } else { 'fail' }
    if ($full.ArtifactEvidenceVerdict -cne $expected -or $compact.ArtifactEvidenceVerdict -cne $expected) {
        throw "$case expected snapshot=$expected; actual=$($full.ArtifactEvidenceVerdict); reason=$($full.ArtifactEvidenceFailureReason)"
    }
    $json = ConvertTo-Json -InputObject @($full.Boundaries) -Depth 100 -Compress
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        $json -cne (ConvertTo-Json -InputObject @($compact.Boundaries) -Depth 100 -Compress)) { throw "$case exact full/compact boundary parity failed." }
    if ($full.LegacySelectionCount -ne 0) { throw "$case selected legacy evidence." }
    $boundary = $full.FirstUnresolvedBoundary
    $guiBoundary = $boundary
    $guiSource = 'coordinator_snapshot_evidence'
    $routeSummary = $null
    if ($expected -ceq 'pass') {
        # Aggregate semantic findings include all descriptors. Query the shared
        # lifecycle at the exact successor subject so hostile historical-effect
        # controls cannot incorrectly fail its independent pending provenance.
        $proofBindings = @($full.__FallbackTailPeerProofBindings | Where-Object BindingKind -CEQ 'pending_peer_proof')
        $proofEffects = @($historyEffects) + @($proofBindings | ForEach-Object PeerProofEffect)
        $proofEntries = @(New-RolloverHistory $envelopes)
        $proofOrigins = @{}; $proofCandidates = @{}; $proofCandidates[$SupersessionTransitionId] = @{ Line=1 }
        $proofFailures = @(Resolve-FileTransferCoordinatorFailedCheckpointYieldCandidates -Candidates @{} -SupersessionCandidates $proofCandidates `
            -AggregateIndex (New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries $proofEntries) -InvalidBoundaries @{} `
            -Effects $proofEffects -AllBoundaries @($proofEntries | ForEach-Object Boundary) `
            -PendingPeerProofBindings $proofBindings -SupersessionPeerProofOrigins $proofOrigins)
        $proofHistory = @(foreach ($entry in $proofEntries) {
            foreach ($operation in $entry.Boundary.operations) { [pscustomobject]@{ Boundary=$entry.Boundary; Operation=$operation } }
        })
        $successorHistory = @($proofHistory | Where-Object { $_.Operation.authorityId -ceq $successor.authorityId -and
            $_.Operation.operationRole -in @('yield_current','yield_terminal','yield_retained_predecessor') })
        $proofLifecycle = Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofLifecycle -YieldHistory $successorHistory `
            -OperationHistory $proofHistory -Effects $proofEffects -SupersessionPeerProofOrigins $proofOrigins
        if ($proofFailures.Count -ne 0 -or $proofOrigins.Count -ne 1 -or $proofLifecycle.Count -ne 1 -or
            @($proofLifecycle.Values | Where-Object { -not $_.Valid }).Count -ne 0) {
            throw "$case lost pending-origin successor provenance: $(ConvertTo-Json -InputObject $proofLifecycle -Depth 16 -Compress)"
        }
        # Slot displacement is not another yield issuance. A missing or
        # competing original effect still fails the historical receipt gate;
        # it cannot be excused by the exact accepted displacement marker.
        $rolloverBoundaries = @($full.Boundaries | Where-Object { $_.boundaryId -ceq $SupersessionTransitionId })
        $semanticVerdict = if ($historicalSemanticFailure) { 'fail' } else { 'pass' }
        $semanticCount = [int]$historicalSemanticFailure
        if (-not $full.StreamComplete -or $full.ArtifactEvidenceFailureReason -cne '' -or
            @($full.Boundaries | Where-Object taxonomyClass -CEQ 'filetransfer_coordinator_snapshot_evidence_invalid').Count -ne 0 -or
            $rolloverBoundaries.Count -ne 1 -or $rolloverBoundaries[0].verdict -cne $semanticVerdict -or
            (Get-FileTransferCoordinatorSnapshotObjectProperty $rolloverBoundaries[0] 'variant' '(none)') -cne $semanticVariant -or
            @($rolloverBoundaries[0].operations | Where-Object { $_.operationKey -ceq $old.operationKey }).Count -ne 1 -or
            $rolloverBoundaries[0].rawCount -ne $semanticCount -or $rolloverBoundaries[0].distinctCount -ne $semanticCount -or
            $rolloverBoundaries[0].recoveredCount -ne 0 -or $rolloverBoundaries[0].unresolvedCount -ne $semanticCount) {
            throw "$case displaced-terminal sibling differed from expected semantic verdict=$semanticVerdict : $($rolloverBoundaries | ConvertTo-Json -Depth 16 -Compress)"
        }
        if ($historicalSemanticFailure) {
            $originalBoundary = @($full.Boundaries | Where-Object { $_.boundaryId -ceq $oldIssuance.Envelope.transitionId })
            foreach ($subject in @($originalBoundary) + @($rolloverBoundaries)) {
                if ($subject.taxonomyClass -cne 'fallback_tail_yield_authority_invalid' -or
                    $subject.domain -cne 'route_runtime' -or $subject.phase -cne 'fallback_tail_yield' -or
                    $subject.verdict -cne 'fail' -or $subject.rawCount -ne 1 -or $subject.distinctCount -ne 1 -or
                    $subject.recoveredCount -ne 0 -or $subject.unresolvedCount -ne 1) {
                    throw "$case historical yield failure was excused or recovered: $(ConvertTo-Json -InputObject $subject -Depth 16 -Compress)"
                }
            }
            if ($originalBoundary.Count -ne 1 -or $null -eq $boundary -or $boundary.boundaryId -cne $oldIssuance.Envelope.transitionId) {
                throw "$case lost the original issuance as its first semantic failure."
            }
        }
        $semantics = Boundary-Semantics @($full.Boundaries)
        if ($case -ceq 'exact') { $baselineSemantics = $semantics }
        elseif (-not $historicalSemanticFailure -and $semantics -cne $baselineSemantics) { throw "$case changed semantic findings." }
        $route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
        $compactRoute = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $compact
        if ((Compare-FileTransferCoordinatorSnapshotRouteConsistency $route $compactRoute).Verdict -cne 'pass' -or
            $route.BoundaryHash -cne $compactRoute.BoundaryHash) {
            throw "$case route-result parity failed."
        }
        # This existing fixture begins after earlier route publication history.
        # Pin its four precise route findings; do not invent missing history or
        # confuse this partial fixture with the immutable complete GUI artifact.
        if ($route.Verdict -cne 'fail' -or
            (ConvertTo-Json -InputObject @($route.Findings) -Compress) -cne
                (ConvertTo-Json -InputObject $expectedRouteFindings -Compress) -or
            $route.FirstUnresolvedBoundary.boundaryId -cne $routePublications[5L].Snapshot.Envelope.transitionId -or
            $route.FirstUnresolvedBoundary.boundaryHash -cne
                (Get-FileTransferCoordinatorSnapshotLineField $original[$routePublications[5L].Snapshot.Index] snapshot_sha256)) {
            throw "$case changed scoped partial-route findings: $(ConvertTo-Json -InputObject $route -Depth 32 -Compress)"
        }
        if ($case -ceq 'exact') { $baselineRouteHash = $route.BoundaryHash }
        elseif ($route.BoundaryHash -cne $baselineRouteHash) { throw "$case changed the partial-route result hash." }
        # The unchanged shared GUI classifier gives route evidence precedence
        # over snapshot semantics. Both results remain independently pinned.
        $guiBoundary = $route.FirstUnresolvedBoundary
        $guiSource = 'coordinator_snapshot_authority_v1_route_consistency'
        $routeSummary = [ordered]@{ Verdict=$route.Verdict; Hash=$route.BoundaryHash; Findings=@($route.Findings) }
    }
    elseif ($case -ceq 'missing_successor_effect' -and
        ($boundary.taxonomyClass -cne 'filetransfer_coordinator_snapshot_evidence_invalid' -or
         $boundary.decisionReason -cne 'snapshot_fallback_tail_recovery_owner_retirement_effect_missing' -or
         $boundary.variant -cne $missingEffectVariant)) {
        throw "Missing fresh successor yield effect lost its exact diagnostic: $(ConvertTo-Json -InputObject $boundary -Depth 12 -Compress)"
    }
    $guiOutputs = @()
    foreach ($input in @([pscustomobject]@{ Name='full'; Lines=$fullLines },
            [pscustomobject]@{ Name='compact'; Lines=@($compactRecords | ForEach-Object Line) })) {
        $path = Join-Path $OutputRoot ($input.Name + '-rollover-gui.log')
        [IO.File]::WriteAllLines($path, [string[]]$input.Lines)
        $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
        if ($null -eq $gui -or $gui.ClassificationSource -cne $guiSource -or
            $gui.CoordinatorBoundaryId -cne $guiBoundary.boundaryId -or $gui.CoordinatorBoundaryHash -cne $guiBoundary.boundaryHash -or
            $gui.CoordinatorWriter -cne $guiBoundary.writer -or $gui.CoordinatorWriterSequence -ne $guiBoundary.writerSequence -or
            $gui.FailureClass -cne $guiBoundary.taxonomyClass -or
            $gui.FailureVariant -cne (Get-FileTransferCoordinatorSnapshotObjectProperty $guiBoundary 'variant' '(none)') -or
            $gui.CoordinatorRawCount -ne $guiBoundary.rawCount -or $gui.CoordinatorDistinctCount -ne $guiBoundary.distinctCount -or
            $gui.CoordinatorRecoveredCount -ne $guiBoundary.recoveredCount -or $gui.CoordinatorUnresolvedCount -ne $guiBoundary.unresolvedCount) {
            $guiSummary = ConvertTo-Json -InputObject @($gui | Select-Object ClassificationSource,FailureClass,FailureVariant,
                CoordinatorBoundaryId,CoordinatorBoundaryHash,CoordinatorWriter,CoordinatorWriterSequence,
                CoordinatorRawCount,CoordinatorDistinctCount,CoordinatorRecoveredCount,CoordinatorUnresolvedCount) -Depth 8 -Compress
            $boundarySummary = ConvertTo-Json -InputObject ($guiBoundary | Select-Object boundaryId,boundaryHash,writer,writerSequence,
                taxonomyClass,variant,rawCount,distinctCount,recoveredCount,unresolvedCount) -Depth 8 -Compress
            throw "$case/$($input.Name) shared GUI boundary differs: gui=$guiSummary; expected=$boundarySummary"
        }
        $guiOutputs += [ordered]@{ BoundaryId=$gui.CoordinatorBoundaryId; BoundaryHash=$gui.CoordinatorBoundaryHash; Variant=$gui.FailureVariant }
    }
    $outputs.Add([ordered]@{ Case=$case; Verdict=$full.ArtifactEvidenceVerdict; Boundaries=$json; Route=$routeSummary; Gui=$guiOutputs })
}
if ((Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([IO.File]::ReadAllBytes($InputPath))) -cne $originalHash -or
    (ConvertTo-Json -InputObject $envelopes -Depth 64 -Compress) -cne $immutable) { throw 'Hostile controls changed immutable production evidence.' }
'failed-quiescent-rollover-normalized=' + (ConvertTo-Json -InputObject $outputs.ToArray() -Depth 100 -Compress)
'failed-quiescent-rollover-evidence-ok'
