# Loaded by the production-generated File-Tuna settlement fixture.
$entries = @($envelopes | ForEach-Object {
    [pscustomobject]@{
        Boundary = [pscustomobject]@{
            boundaryId=$_.transitionId; coordinatorInstanceId=$_.coordinatorInstanceId; writer='fixture-writer'
            beforeStateGeneration=$_.aggregate.beforeStateGeneration; afterStateGeneration=$_.aggregate.afterStateGeneration
            beforeStateHash=$_.aggregate.beforeStateFingerprint; afterStateHash=$_.aggregate.afterStateFingerprint
            evidenceLineIndex=$_.aggregate.afterStateGeneration; operations=$_.aggregate.operations
        }
        BeforeBoundary=$_.aggregate.beforeBoundary; AfterBoundary=$_.aggregate.afterBoundary
        SessionId=$_.sessionId; TransferId=$_.transferId; Direction=$_.direction
    }
})
$candidateId = [string]$envelopes[-1].transitionId
$failure = Get-FileTransferCoordinatorSnapshotFamily5PressureSettlementFailure -Envelope $envelopes[-1]
if ($failure -cne ('snapshot_regular_v4_pressure_settlement_transition_invalid|' +
    'terminal_settlement_rollover_compared_historical_source_with_current_route_source')) {
    throw "Cross-source rollover must require indexed validation: $failure"
}
$candidates = @{ $candidateId = @{ Failure=$failure; LineIndex=$entries[-1].Boundary.evidenceLineIndex } }
$originalId = [string](@($envelopes | Where-Object decisionKind -ceq 'CommitRegularV4FrontierRepairPressureSettlement')[0].transitionId)
$carryId = [string](@($envelopes | Where-Object decisionKind -ceq 'CommitFileTransferRouteTransition')[0].transitionId)
function Get-Terminals($entry) {
    @($entry.Boundary.operations | Where-Object { $_.operationRole -in @('pressure_settlement_terminal','repair_authority_terminal') })
}
function Assert-Reduction([object[]]$values, [bool]$invalid, [hashtable]$blocked = @{}) {
    $index = New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries $values
    $result = @(Resolve-FileTransferCoordinatorPressureTerminalRollovers -AggregateIndex $index -Candidates $candidates -InvalidBoundaries $blocked)
    if (($result.Count -gt 0) -ne $invalid) { throw "Unexpected terminal reducer outcome (invalid=$invalid): $result" }
    if ($invalid -and $result.Count -ne 1) { throw 'One candidate must yield one exact failure.' }
    if ($invalid) {
        foreach ($stream in $index.Values) {
            if ($stream.Boundaries.ContainsKey($candidateId)) { throw 'Failed candidate remained usable as validated history.' }
        }
    }
}
Assert-Reduction $entries $false
foreach ($mutation in @('missing_original','missing_carry','missing_original_mirror','missing_retired_mirror',
    'source','receipt','command','authority','operation_generation','operation_key','effective_scope','carry_source_aba',
    'data_session_aba','slot_aba','generation_gap','transfer_replacement','terminal_poison','blocked_original','conflicting_generation','conflicting_candidate')) {
    $values = $entries | ConvertTo-Json -Depth 64 | ConvertFrom-Json
    $original = @($values | Where-Object { $_.Boundary.boundaryId -ceq $originalId })[0]
    $carry = @($values | Where-Object { $_.Boundary.boundaryId -ceq $carryId })[0]
    $last = $values[-1]
    $retired = @(Get-Terminals $last | Where-Object afterState -ceq 'None')
    $blocked = @{}
    switch -Exact ($mutation) {
        'missing_original' { $values = @($values | Where-Object { $_.Boundary.boundaryId -cne $originalId }) }
        'missing_carry' { $carry.Boundary.operations = @($carry.Boundary.operations | Where-Object operationRole -notin @('pressure_settlement_terminal','repair_authority_terminal')) }
        'missing_original_mirror' { $original.Boundary.operations = @($original.Boundary.operations | Where-Object operationRole -cne 'repair_authority_terminal') }
        'missing_retired_mirror' { $last.Boundary.operations = @($last.Boundary.operations | Where-Object { -not ($_.operationRole -ceq 'repair_authority_terminal' -and $_.afterState -ceq 'None') }) }
        'source' { foreach ($op in $retired) { $op.regularV4FrontierRepairPressure.sourceBoundary.checkpointGeneration++ } }
        'receipt' { foreach ($op in $retired) { $op.receiptFingerprint += '-mutated' } }
        'command' { foreach ($op in $retired) { $op.commandFingerprint += '-mutated' } }
        'authority' { foreach ($op in $retired) { $op.authorityId += '-mutated' } }
        'operation_generation' { foreach ($op in $retired) { $op.operationGeneration++ } }
        'operation_key' { foreach ($op in $retired) { $op.operationKey += '-mutated' } }
        'effective_scope' { foreach ($op in $retired) { $op.regularV4FrontierRepairPressure.effectiveBoundary.bridgeRecoveryGeneration++ } }
        'carry_source_aba' { foreach ($op in @(Get-Terminals $carry)) { $op.regularV4FrontierRepairPressure.sourceBoundary.transportEpoch++ } }
        'data_session_aba' { $carry.AfterBoundary.dataSessionIdentity += '-replacement' }
        'slot_aba' {
            $firstReplacement = @($values | Where-Object { $_.Boundary.beforeStateGeneration -eq $original.Boundary.afterStateGeneration })[0]
            $restoration = @($values | Where-Object { $_.Boundary.beforeStateGeneration -eq $firstReplacement.Boundary.afterStateGeneration })[0]
            $a = Get-Terminals $original
            $x = $a | ConvertTo-Json -Depth 64 | ConvertFrom-Json
            foreach ($op in $x) {
                $oldAuthority = [string]$op.authorityId
                $op.authorityId += '-replacement'
                $op.operationKey = $op.operationKey.Replace($oldAuthority, $op.authorityId)
                $op.receiptIdentity += '-replacement'
            }
            $retireA = $a | ConvertTo-Json -Depth 64 | ConvertFrom-Json
            $retireX = $x | ConvertTo-Json -Depth 64 | ConvertFrom-Json
            foreach ($op in @($retireA) + @($retireX)) { $op.beforeState='Applied'; $op.afterState='None' }
            $firstReplacement.Boundary.operations = @($retireA) + @($x)
            $restoration.Boundary.operations = @($retireX) + @($a)
        }
        'generation_gap' { $values = @($values | Where-Object { $_.Boundary.beforeStateGeneration -ne $original.Boundary.afterStateGeneration }) }
        'transfer_replacement' { $carry.TransferId += '-replacement' }
        'terminal_poison' { $carry.AfterBoundary.terminalGeneration++ }
        'blocked_original' { $blocked[$originalId] = $true }
        'conflicting_generation' { $values += ($carry | ConvertTo-Json -Depth 64 | ConvertFrom-Json) }
        'conflicting_candidate' { $values += ($last | ConvertTo-Json -Depth 64 | ConvertFrom-Json) }
    }
    try { Assert-Reduction $values $true $blocked } catch { throw "$mutation : $_; $($_.ScriptStackTrace)" }
}

# Full/compact/GUI must reject a changed historical receipt, not merely the direct reducer.
$mutated = $envelopes | ConvertTo-Json -Depth 64 | ConvertFrom-Json
foreach ($operation in @($mutated[-1].aggregate.operations | Where-Object {
    $_.operationRole -in @('pressure_settlement_terminal','repair_authority_terminal') -and $_.afterState -ceq 'None'
})) {
    $operation.receiptFingerprint += '-mutated'
    $operation.regularV4FrontierRepairPressure.receipts[0].receiptFingerprint = $operation.receiptFingerprint
}
Assert-Parity (New-Lines $mutated) $true
$shuffled = $envelopes | ConvertTo-Json -Depth 64 | ConvertFrom-Json
foreach ($envelope in $shuffled) { [array]::Reverse($envelope.aggregate.operations) }
Assert-Parity (New-Lines $shuffled) $false

# A missing route effect remains Family 1's independent failure; it is not excused by slot history.
$missingPublication = @($lines | Where-Object { $_ -notmatch 'effect_kind=route_publication_forwarded;' })
Assert-Parity $missingPublication $true
$routeFailure = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $missingPublication
if ($routeFailure.ArtifactEvidenceFailureReason -match 'terminal_settlement_rollover' -or
    @($routeFailure.Boundaries | Where-Object taxonomyClass -ceq 'publication_authority_missing').Count -eq 0) {
    throw 'Missing publication was accepted or relabeled as a terminal rollover.'
}
'Cross-source terminal-slot mutation and parity controls passed.'
