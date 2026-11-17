param([string]$RepoRoot, [string]$InputPath, [string]$SupersessionTransitionId, [string]$FailedTransitionId)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
foreach ($source in @(
    @{ Path='tests/nLink.SmokeTests.Core/Support/CheckpointResponderCoexistenceSnapshotEvidence.ps1'; Names=@('Copy-Value','Decode') },
    @{ Path='tests/nLink.SmokeTests.Core/Support/FailedQuiescentTerminalRolloverSnapshotEvidence.ps1'; Names=@('Receipt') })) {
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot $source.Path), [ref]$null, [ref]$null)
    foreach ($name in $source.Names) {
        $definitions = @($ast.FindAll({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
        }, $true))
        if ($definitions.Count -ne 1) { throw "Shared function is not unique: $name" }
        . ([scriptblock]::Create($definitions[0].Extent.Text))
    }
}
$lines = [IO.File]::ReadAllLines($InputPath)
$inputHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([IO.File]::ReadAllBytes($InputPath))
$entries = @(); $effects = @()
for ($i = 0; $i -lt $lines.Count; $i++) {
    $line = $lines[$i]; $event = Get-FileTransferCoordinatorSnapshotEventName $line
    if ($event -ceq 'filetransfer_coordinator_transition_snapshot') {
        $envelope = Decode $line
        $entries += [pscustomobject]@{
            Boundary=[pscustomobject]@{
                coordinatorInstanceId=$envelope.coordinatorInstanceId; writer='failed-quiescent-rollover-fixture'
                writerSequence=[long](Get-FileTransferCoordinatorSnapshotLineField $line log_writer_sequence)
                boundaryId=$envelope.transitionId; sessionId=$envelope.sessionId; transferId=$envelope.transferId; direction=$envelope.direction
                beforeStateGeneration=$envelope.aggregate.beforeStateGeneration; afterStateGeneration=$envelope.aggregate.afterStateGeneration
                beforeStateHash=$envelope.aggregate.beforeStateFingerprint; afterStateHash=$envelope.aggregate.afterStateFingerprint
                beforeBoundary=$envelope.aggregate.beforeBoundary; afterBoundary=$envelope.aggregate.afterBoundary
                decisionKind=$envelope.decisionKind; decisionStatus=$envelope.decisionStatus; decisionReason=$envelope.decisionReason
                decisionUtcMilliseconds=$envelope.decisionUtcMilliseconds; operations=$envelope.aggregate.operations
                families=$envelope.aggregate.changedFamilies; evidenceLineIndex=$i
            }
            BeforeBoundary=$envelope.aggregate.beforeBoundary; AfterBoundary=$envelope.aggregate.afterBoundary
            SessionId=$envelope.sessionId; TransferId=$envelope.transferId; Direction=$envelope.direction
        }
    }
    elseif ($event -ceq 'filetransfer_coordinator_authority_effect') {
        $effect = [ordered]@{ LineIndex=$i }
        foreach ($name in @('CoordinatorInstanceId','TransitionId','OperationKey','OperationGeneration','EffectKind','EffectIdentity',
            'ReceiptIdentity','ReceiptGeneration','ReceiptStatus','ReceiptFingerprint','Route','ProtocolVersion','LiveRouteEpoch',
            'RoutePublicationGeneration','RouteCommitGeneration','RuntimeGeneration','TerminalGeneration','LegGeneration',
            'TransportEpoch','BridgeRecoveryGeneration','CheckpointGeneration','CheckpointRequestId','DataSessionIdentity','DataSessionGeneration')) {
            $field = [regex]::Replace($name, '([a-z])([A-Z])', '$1_$2').ToLowerInvariant()
            $effect[$name] = Get-FileTransferCoordinatorSnapshotLineField $line $field
        }
        foreach ($field in @{
            Writer='log_writer_id'; WriterSequence='log_writer_sequence'; StateGeneration='coordinator_state_generation'
            Family='operation_family'; AuthorityId='operation_authority_id'
        }.GetEnumerator()) { $effect[$field.Key] = Get-FileTransferCoordinatorSnapshotLineField $line $field.Value }
        $effects += [pscustomobject]$effect
    }
}
function Operation($Entry, [string]$Role, [string]$Kind) {
    $subjects = @($Entry.Boundary.operations | Where-Object {
        $_.family -ceq 'fallback_tail_yield' -and $_.operationRole -ceq $Role -and $_.operationKind -ceq $Kind
    })
    if ($subjects.Count -ne 1) {
        throw "Production subject is not unique: transition=$($Entry.Boundary.boundaryId); $Role/$Kind; count=$($subjects.Count)"
    }
    $subjects[0]
}
$pendingEntries = @($entries | Where-Object {
    @($_.Boundary.operations | Where-Object { $_.operationRole -ceq 'peer_proof_pending_initial' -and $_.beforeState -ceq 'None' }).Count -eq 1
})
if ($pendingEntries.Count -ne 1) { throw 'Expected one genuine pending origin.' }
$pendingId = $pendingEntries[0].Boundary.boundaryId
$pending = Operation $pendingEntries[0] 'peer_proof_pending_initial' 'PeerProof'
$successorEntry = @($entries | Where-Object { $_.Boundary.boundaryId -ceq $SupersessionTransitionId })[0]
$successor = Operation $successorEntry 'yield_current' 'Quiescent'
$hasPriorTerminal = @($successorEntry.Boundary.operations | Where-Object {
    $_.operationRole -ceq 'yield_terminal' -and $_.operationKind -ceq 'Quiescent'
}).Count -eq 1
$peerEffects = @($effects | Where-Object { $_.EffectKind -ceq 'peer_proof_observed' -and
    $_.ReceiptIdentity -ceq (Receipt $pending 'peer_proof').receiptIdentity })
if ($peerEffects.Count -ne 1 -or $peerEffects[0].OperationKey -cne $pending.operationKey -or
    $peerEffects[0].TransitionId -cne $pendingId) { throw 'Only the exact pending origin may emit this peer proof.' }
if ((Receipt $pending 'peer_proof').receiptReason -cne 'fallback_tail_peer_proof_retained' -or
    (Receipt $successor 'peer_proof').receiptReason -cne 'fallback_tail_peer_proof_observed' -or
    (Receipt $pending 'peer_proof').receiptGeneration -eq $successor.operationGeneration) {
    throw 'Role-specific reason mapping and independent proof/yield generations were lost.'
}
$immutable = ConvertTo-Json -InputObject @($entries,$effects) -Depth 100 -Compress
$results = [Collections.Generic.List[object]]::new()
function Assert-Provenance([object[]]$Entries, [object[]]$Effects, [bool]$Expected, [string]$Case, $Invalid = @{}) {
    Write-Output "pending-failed-quiescent-case=$Case"
    $candidates = @{}; $candidates[$SupersessionTransitionId] = @{ Line=1 }
    $origins = @{}; $displaced = @{}
    $bindings = @(Get-FileTransferCoordinatorSnapshotPendingPeerProofBindings -AggregateEntries $Entries -Effects $Effects)
    $failures = @(Resolve-FileTransferCoordinatorFailedCheckpointYieldCandidates -Candidates @{} -SupersessionCandidates $candidates `
        -AggregateIndex (New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries $Entries) `
        -InvalidBoundaries $Invalid -Effects $Effects -AllBoundaries @($Entries | ForEach-Object Boundary) `
        -PendingPeerProofBindings $bindings -SupersessionPeerProofOrigins $origins -ValidatedDisplacedTerminals $displaced)
    $history = @(foreach ($entry in $Entries) {
        foreach ($operation in $entry.Boundary.operations) { [pscustomobject]@{ Boundary=$entry.Boundary; Operation=$operation } }
    })
    $candidate = @($history | Where-Object { $_.Boundary.boundaryId -ceq $SupersessionTransitionId -and
        $_.Operation.operationRole -ceq 'yield_current' -and $_.Operation.operationKind -ceq 'Quiescent' })
    $actual = $candidate.Count -eq 1 -and (Test-FileTransferCoordinatorSnapshotFallbackTailPeerProofEffect `
        -Entry $candidate[0] -OperationHistory $history -Effects $Effects -SupersessionPeerProofOrigins $origins)
    if ($actual -ne $Expected) { throw "Pending→Failed→Quiescent ${Case}: expected=$Expected; actual=$actual; failures=$($failures -join ';')" }
    $markerExpected = $null
    if (-not $hasPriorTerminal) { $markerExpected = $false }
    elseif ($Expected -or $Case -cin @('missing_effect','competing_effect','wrong_effect_writer','wrong_effect_coordinator',
            'wrong_effect_transition','wrong_effect_operation','wrong_effect_authority','wrong_effect_generation',
            'wrong_effect_state','wrong_effect_receipt','pending_neutral_aba','failed_neutral_aba')) {
        # Missing pending proof does not grant successor provenance, but also
        # must not erase independently validated terminal-slot displacement.
        $markerExpected = $true
    }
    elseif ($Case -cin @('empty','singleton','wrong_coordinator','wrong_writer','wrong_session','wrong_transfer','wrong_direction',
            'generation_gap','hash_gap','terminal_poison','data_session_replacement','duplicate_conflict',
            'rejected_origin','rejected_failed','terminal_neutral_aba','failed_mutation_aba','repeated_consumption')) {
        $markerExpected = $false
    }
    if ($null -ne $markerExpected -and $displaced.Count -ne [int]$markerExpected) {
        throw "Pending→Failed→Quiescent ${Case}: terminal displacement was coupled to pending provenance or leaked from invalid history."
    }
    if ($hasPriorTerminal) {
        $terminalEntry = [pscustomobject]@{ Boundary=$successorEntry.Boundary; Operation=(Operation $successorEntry 'yield_terminal' 'Quiescent') }
        $terminalKey = "$($terminalEntry.Boundary.coordinatorInstanceId)|$($terminalEntry.Boundary.writer)|$(Get-FileTransferCoordinatorSnapshotFallbackTailYieldEntryKey -Entry $terminalEntry)"
        if (@($displaced.Keys | Where-Object { $_ -cne $terminalKey }).Count -ne 0) {
            throw "Pending→Failed→Quiescent ${Case}: displacement marker escaped the exact terminal subject."
        }
    }
    if ($Expected) {
        if ($bindings.Count -ne 1 -or $origins.Count -ne 1) { throw "Non-unique proven origin: $Case" }
        $key = Get-FileTransferCoordinatorSnapshotFallbackTailYieldEntryKey -Entry $candidate[0]
        if ($origins[$key].Origin.Binding.PeerProofEffect.EffectIdentity -cne $peerEffects[0].EffectIdentity -or
            $origins[$key].Origin.Binding.OperationKey -cne $pending.operationKey) { throw "Origin was reconstructed or replaced: $Case" }
        $yieldHistory = @($history | Where-Object { $_.Operation.authorityId -ceq $successor.authorityId -and
            $_.Operation.operationRole -in @('yield_current','yield_terminal','yield_retained_predecessor') })
        $lifecycle = Get-FileTransferCoordinatorSnapshotFallbackTailPeerProofLifecycle `
            -YieldHistory $yieldHistory -OperationHistory $history -Effects $Effects -SupersessionPeerProofOrigins $origins
        if ($yieldHistory.Count -lt 4 -or @($lifecycle.Values | Where-Object { -not $_.Valid }).Count -ne 0) {
            throw "Later real paired/current/terminal carry lost immutable origin: $Case"
        }
    }
    $results.Add([ordered]@{ Case=$Case; Valid=[bool]$actual; Origins=$origins.Count; DisplacedTerminals=$displaced.Count })
}
Assert-Provenance (Copy-Value $entries) (Copy-Value $effects) $true 'production'
Assert-Provenance @() @() $false 'empty'
Assert-Provenance @((Copy-Value $successorEntry)) (Copy-Value $effects) $false 'singleton'
$cases = @('missing_effect','competing_effect','wrong_effect_writer','wrong_effect_coordinator','wrong_effect_transition',
    'wrong_effect_operation','wrong_effect_authority','wrong_effect_generation','wrong_effect_state','wrong_effect_receipt',
    'missing_pending_birth','missing_pending_retirement','malformed_failed_birth','wrong_supersession','stale_failed_authority',
    'wrong_coordinator','wrong_writer','wrong_session','wrong_transfer','wrong_direction','generation_gap','hash_gap',
    'terminal_poison','data_session_replacement','duplicate_conflict','rejected_origin','rejected_failed',
    'copied_failed_effect','copied_successor_effect','appended_failed_effect','appended_successor_effect',
    'pending_mutation_aba','failed_mutation_aba','pending_neutral_aba','failed_neutral_aba','repeated_consumption',
    'pending_reason_mapping','failed_reason_mapping',
    'commandIdentity','commandGeneration','commandFingerprint','receiptIdentity','receiptGeneration','receiptFingerprint',
    'receiptValidated','receiptReason','ownerKey','pairKey','peerProofAuthorityId','peerProofYieldGeneration',
    'peerProofIdentity','peerProofFingerprint','sourceBoundary','effectiveBoundary','targetBoundary','dataSessionIdentity','dataSessionGeneration')
if ($hasPriorTerminal) { $cases += 'terminal_neutral_aba' }
foreach ($case in $cases) {
    $changed = Copy-Value $entries; $caseEffects = Copy-Value $effects; $invalid = @{}
    $origin = @($changed | Where-Object { $_.Boundary.boundaryId -ceq $pendingId })[0]
    $failed = @($changed | Where-Object { $_.Boundary.boundaryId -ceq $FailedTransitionId })[0]
    $current = @($changed | Where-Object { $_.Boundary.boundaryId -ceq $SupersessionTransitionId })[0]
    $pendingOp = Operation $origin 'peer_proof_pending_initial' 'PeerProof'
    $failedOp = Operation $failed 'yield_current' 'Failed'
    $successorOp = Operation $current 'yield_current' 'Quiescent'
    $effect = @($caseEffects | Where-Object { $_.EffectIdentity -ceq $peerEffects[0].EffectIdentity })[0]
    switch -CaseSensitive ($case) {
        'missing_effect' { $caseEffects = @($caseEffects | Where-Object { $_.EffectIdentity -cne $effect.EffectIdentity }) }
        'competing_effect' { $copy = Copy-Value $effect; $copy.EffectIdentity += '-competing'; $caseEffects += $copy }
        'wrong_effect_writer' { $effect.Writer += '-wrong' }
        'wrong_effect_coordinator' { $effect.CoordinatorInstanceId += '-wrong' }
        'wrong_effect_transition' { $effect.TransitionId += '-wrong' }
        'wrong_effect_operation' { $effect.OperationKey += '-wrong' }
        'wrong_effect_authority' { $effect.AuthorityId += '-wrong' }
        'wrong_effect_generation' { $effect.OperationGeneration = [long]$effect.OperationGeneration + 1 }
        'wrong_effect_state' { $effect.StateGeneration = [long]$effect.StateGeneration + 1 }
        'wrong_effect_receipt' { $effect.ReceiptFingerprint += '-wrong' }
        'missing_pending_birth' { $origin.Boundary.operations = @($origin.Boundary.operations | Where-Object { $_.operationKey -cne $pendingOp.operationKey }) }
        'missing_pending_retirement' { $failed.Boundary.operations = @($failed.Boundary.operations | Where-Object operationRole -CNE 'peer_proof_pending_initial') }
        'malformed_failed_birth' { $failedOp.beforeState = 'YieldIssued' }
        'wrong_supersession' { $successorOp.fallbackTailYield.failedPredecessorAuthorityId += '-wrong' }
        'stale_failed_authority' { (Operation $current 'yield_current' 'Failed').authorityId += '-stale' }
        'wrong_coordinator' { $failed.Boundary.coordinatorInstanceId += '-wrong' }
        'wrong_writer' { $failed.Boundary.writer += '-wrong' }
        'wrong_session' { $failed.SessionId += '-wrong'; $failed.Boundary.sessionId = $failed.SessionId }
        'wrong_transfer' { $failed.TransferId += '-wrong'; $failed.Boundary.transferId = $failed.TransferId }
        'wrong_direction' { $failed.Direction = 'wrong'; $failed.Boundary.direction = 'wrong' }
        'generation_gap' { $current.Boundary.beforeStateGeneration++ }
        'hash_gap' { $current.Boundary.beforeStateHash += '-gap' }
        'terminal_poison' { $failed.AfterBoundary.terminalGeneration++; $current.BeforeBoundary.terminalGeneration++ }
        'data_session_replacement' { $failed.AfterBoundary.dataSessionGeneration++; $current.BeforeBoundary.dataSessionGeneration++ }
        'duplicate_conflict' { $copy = Copy-Value $origin; $copy.Boundary.boundaryId += '-competing'; $changed += $copy }
        'rejected_origin' { $invalid[$pendingId] = $true }
        'rejected_failed' { $invalid[$FailedTransitionId] = $true }
        'pending_reason_mapping' { (Receipt $pendingOp 'peer_proof').receiptReason = 'fallback_tail_peer_proof_observed' }
        'failed_reason_mapping' { (Receipt $failedOp 'peer_proof').receiptReason = 'fallback_tail_peer_proof_retained' }
        { $_ -cin @('copied_failed_effect','copied_successor_effect','appended_failed_effect','appended_successor_effect') } {
            if ($case -clike 'appended_*') { $effect = Copy-Value $effect; $caseEffects += $effect }
            $targetEntry = if ($case -clike '*_failed_effect') { $failed } else { $current }
            $targetOp = if ($case -clike '*_failed_effect') { $failedOp } else { $successorOp }
            $effect.TransitionId = $targetEntry.Boundary.boundaryId; $effect.StateGeneration = $targetEntry.Boundary.afterStateGeneration
            $effect.OperationKey = $targetOp.operationKey; $effect.AuthorityId = $targetOp.authorityId
            $effect.OperationGeneration = $targetOp.operationGeneration; $effect.EffectIdentity += '-copied'
        }
        { $_ -cin @('pending_mutation_aba','failed_mutation_aba','pending_neutral_aba','failed_neutral_aba','terminal_neutral_aba','repeated_consumption') } {
            $anchor = if ($case -clike 'pending_*') { $origin } else { $failed }
            $source = if ($case -clike 'pending_*') { $pendingOp }
                elseif ($case -ceq 'terminal_neutral_aba') {
                    $retained = Copy-Value (Operation $current 'yield_terminal' 'Quiescent')
                    $retained.afterState = $retained.beforeState
                    $retained
                } else { $failedOp }
            $changedEntry = Copy-Value $anchor; $restoredEntry = Copy-Value $anchor
            $changedOp = Copy-Value $source; $restoredOp = Copy-Value $source
            foreach ($operation in @($changedOp,$restoredOp)) { $operation.beforeState = $source.afterState; $operation.afterState = $source.afterState }
            if ($case -ceq 'repeated_consumption') { $changedOp.afterState = 'None'; $restoredOp.beforeState = 'None' }
            else { $changedOp.fallbackTailYield.ownerKey += '-mutated' }
            $changedEntry.Boundary.operations = @($changedOp); $restoredEntry.Boundary.operations = @($restoredOp)
            $changedEntry.Boundary.boundaryId += '-mutated'; $restoredEntry.Boundary.boundaryId += '-restored'
            foreach ($entry in @($changedEntry,$restoredEntry)) {
                $entry.Boundary.decisionKind = 'SyntheticHistoryMutation'; $entry.BeforeBoundary = Copy-Value $anchor.AfterBoundary
                $entry.AfterBoundary = Copy-Value $anchor.AfterBoundary
            }
            $position = [array]::IndexOf($changed,$anchor)
            $changed = @($changed[0..$position]) + @($changedEntry,$restoredEntry) + @($changed[($position + 1)..($changed.Count - 1)])
            if ($case -clike '*_neutral_aba') {
                foreach ($entry in @($changedEntry,$restoredEntry)) {
                    $entry.Boundary.beforeStateGeneration = $anchor.Boundary.afterStateGeneration
                    $entry.Boundary.beforeStateHash = $anchor.Boundary.afterStateHash
                    foreach ($operation in $entry.Boundary.operations) { $operation.stateApplied = $false }
                }
            }
            else {
                for ($j = 0; $j -lt $changed.Count; $j++) {
                    $changed[$j].Boundary.beforeStateGeneration = $j; $changed[$j].Boundary.afterStateGeneration = $j + 1
                    $changed[$j].Boundary.beforeStateHash = "hostile-$j"; $changed[$j].Boundary.afterStateHash = "hostile-$($j + 1)"
                    foreach ($boundEffect in @($caseEffects | Where-Object TransitionId -CEQ $changed[$j].Boundary.boundaryId)) { $boundEffect.StateGeneration = $j + 1 }
                }
            }
        }
        { $_ -cin @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity','receiptGeneration','receiptFingerprint','receiptValidated','receiptReason') } {
            $receipt = Receipt $successorOp 'peer_proof'
            if ($case -clike '*Generation') { $receipt.$case++ }
            elseif ($case -ceq 'receiptValidated') { $receipt.$case = $false }
            else { $receipt.$case += '-wrong' }
        }
        { $_ -cin @('sourceBoundary','effectiveBoundary','targetBoundary') } { $successorOp.fallbackTailYield.$case.transportEpoch++ }
        'dataSessionIdentity' { $successorOp.fallbackTailYield.sourceBoundary.dataSessionIdentity += '-wrong' }
        'dataSessionGeneration' { $successorOp.fallbackTailYield.sourceBoundary.dataSessionGeneration++ }
        default {
            if ($case -ceq 'peerProofYieldGeneration') { $successorOp.fallbackTailYield.$case++ }
            else { $successorOp.fallbackTailYield.$case += '-wrong' }
        }
    }
    Assert-Provenance $changed $caseEffects $false $case $invalid
}
$reordered = Copy-Value $entries
foreach ($entry in $reordered) { [array]::Reverse($entry.Boundary.operations) }
Assert-Provenance $reordered (Copy-Value $effects) $true 'descriptor_reorder'
$positiveCases = @('unchanged_pending_neutral','unchanged_failed_neutral','unrelated_neutral','unrelated_mutation')
if ($hasPriorTerminal) { $positiveCases += 'unchanged_terminal_neutral' }
foreach ($case in $positiveCases) {
    $changed = Copy-Value $entries; $caseEffects = Copy-Value $effects
    $anchorId = if ($case -ceq 'unchanged_pending_neutral') { $pendingId } else { $FailedTransitionId }
    $anchor = @($changed | Where-Object { $_.Boundary.boundaryId -ceq $anchorId })[0]
    $observation = Copy-Value $anchor
    $observation.Boundary.boundaryId += '-' + $case
    $observation.BeforeBoundary = Copy-Value $anchor.AfterBoundary
    $observation.Boundary.beforeBoundary = Copy-Value $anchor.AfterBoundary
    $observation.Boundary.beforeStateGeneration = $anchor.Boundary.afterStateGeneration
    $observation.Boundary.beforeStateHash = $anchor.Boundary.afterStateHash
    if ($case -clike 'unchanged_*') {
        $operation = if ($case -ceq 'unchanged_pending_neutral') {
            Operation $observation 'peer_proof_pending_initial' 'PeerProof'
        } elseif ($case -ceq 'unchanged_terminal_neutral') {
            $retained = Copy-Value (Operation $successorEntry 'yield_terminal' 'Quiescent')
            $retained.afterState = $retained.beforeState
            $retained
        } else { Operation $observation 'yield_current' 'Failed' }
        $operation.beforeState = $operation.afterState; $operation.stateApplied = $false
        $observation.Boundary.operations = @($operation)
    }
    else {
        # Hostile/neutral reducer fixtures exercise ancestry only. No claim is
        # made that these synthetic other-family records are full snapshots.
        $observation.Boundary.operations = @()
        $observation.Boundary.families = @('repair_replay')
    }
    $position = [array]::IndexOf($changed,$anchor)
    $changed = @($changed[0..$position]) + @($observation) + @($changed[($position + 1)..($changed.Count - 1)])
    if ($case -ceq 'unrelated_mutation') {
        for ($j = 0; $j -lt $changed.Count; $j++) {
            $changed[$j].Boundary.beforeStateGeneration = $j; $changed[$j].Boundary.afterStateGeneration = $j + 1
            $changed[$j].Boundary.beforeStateHash = "unrelated-$j"; $changed[$j].Boundary.afterStateHash = "unrelated-$($j + 1)"
            foreach ($effect in @($caseEffects | Where-Object TransitionId -CEQ $changed[$j].Boundary.boundaryId)) { $effect.StateGeneration = $j + 1 }
        }
    }
    Assert-Provenance $changed $caseEffects $true $case
}
if ((ConvertTo-Json -InputObject @($entries,$effects) -Depth 100 -Compress) -cne $immutable -or
    (Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([IO.File]::ReadAllBytes($InputPath))) -cne $inputHash) {
    throw 'Direct hostile controls mutated immutable production evidence.'
}
'pending-failed-quiescent-normalized=' + (ConvertTo-Json -InputObject $results.ToArray() -Depth 100 -Compress)
