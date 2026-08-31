# Production-generated receipt ownership, carry-forward, and fail-closed controls.
$commits = @($envelopes | Where-Object decisionKind -ceq 'CommitRegularV4FrontierRepairPressureRecoveryHandoff')
$originals = @($commits[0].aggregate.operations | Where-Object {
    $_.operationRole -ceq 'recovery_handoff_terminal' -and $_.beforeState -ceq 'None'
})
if ($originals.Count -ne 1) { throw 'Original handoff not unique.' }
$original = $originals[0]
if ($original.operationGeneration -eq $original.receiptGeneration -or
    -not (Test-FileTransferCoordinatorSnapshotRecoveryHandoffTerminalReceipt $original)) {
    throw 'Distinct handoff/settlement generation domains were not preserved.'
}
foreach ($mutation in @('missing_payload','missing_receipts','competing_receipts','missing_field','stage',
    'status','validated','top_fingerprint','top_generation','command_generation','install_generation',
    'recovery_generation','recovery_authority','clear_bits','key','role','kind','scope')) {
    $op = $original | ConvertTo-Json -Depth 64 | ConvertFrom-Json
    $payload = $op.regularV4FrontierRepairPressure
    switch -Exact ($mutation) {
        'missing_payload' { $op.regularV4FrontierRepairPressure=$null }
        'missing_receipts' { $payload.receipts=@() }
        'competing_receipts' { $payload.receipts=@($payload.receipts[0],$payload.receipts[0]) }
        'missing_field' { $payload.receipts[0].PSObject.Properties.Remove('commandFingerprint') }
        'stage' { $payload.receipts[0].stage='pressure_install' }
        'status' { $op.receiptStatus='Prepared'; $payload.receipts[0].receiptStatus='Prepared' }
        'validated' { $payload.receipts[0].receiptValidated=$false }
        'top_fingerprint' { $op.receiptFingerprint += '-mutated' }
        'top_generation' { $op.receiptGeneration++ }
        'command_generation' { $payload.receipts[0].commandGeneration++ }
        'install_generation' { $payload.pressureInstallGeneration++ }
        'recovery_generation' { $payload.recoveryOperationGeneration++ }
        'recovery_authority' { $payload.recoveryAuthorityId='(none)' }
        'clear_bits' { $payload.retainedPredecessorCleared=$true }
        'key' { $op.operationKey += '-mutated' }
        'role' { $op.operationRole='pressure_binding_installed' }
        'kind' { $op.operationKind='PressureInstall' }
        'scope' { $payload.effectiveBoundary.dataSessionIdentity='(none)' }
    }
    if (Test-FileTransferCoordinatorSnapshotRecoveryHandoffTerminalReceipt $op) {
        throw "Malformed retained receipt accepted: $mutation"
    }
}

# A carry must not create a new handoff effect, including route/checkpoint carries.
foreach ($envelope in $envelopes) {
    if ($envelope.decisionKind -ceq 'CommitRegularV4FrontierRepairPressureRecoveryHandoff') { continue }
    if (@($lines | Where-Object { $_.Contains("transition_id=$($envelope.transitionId);") -and
            $_ -match 'effect_kind=(recovery_handoff_committed|projection_detached);' }).Count -ne 0) {
        throw 'Carry-forward re-emitted a handoff effect.'
    }
}
$shuffled = $envelopes | ConvertTo-Json -Depth 64 | ConvertFrom-Json
foreach ($envelope in $shuffled) { [array]::Reverse($envelope.aggregate.operations) }
Assert-Parity (New-Lines $shuffled) $false $true

foreach ($mutation in @('historical_missing_receipt','immutable_receipt_mutation','data_session_mutation','retired_authority_aba')) {
    $values = $envelopes | ConvertTo-Json -Depth 64 | ConvertFrom-Json
    $settlements = @($values | Where-Object decisionKind -ceq 'CommitRegularV4FrontierRepairPressureSettlement')
    if ($settlements.Count -ne 1) { throw 'Independent settlement not unique.' }
    $candidate = $settlements[0]
    if ($mutation -ceq 'retired_authority_aba') { $candidate = $values[-1] }
    $carry = $original | ConvertTo-Json -Depth 64 | ConvertFrom-Json
    $carry.beforeState='Committed'; $carry.afterState='Committed'
    $receipt = $carry.regularV4FrontierRepairPressure.receipts[0]
    switch -Exact ($mutation) {
        'historical_missing_receipt' {
            $carry.receiptStatus='Prepared'; $carry.receiptValidated=$false
            $carry.receiptFingerprint='(none)'
            $receipt.receiptStatus='Prepared'; $receipt.receiptValidated=$false
            $receipt.receiptFingerprint='(none)'
        }
        'immutable_receipt_mutation' {
            $carry.receiptFingerprint += '-mutated'; $receipt.receiptFingerprint=$carry.receiptFingerprint
        }
        'data_session_mutation' { $carry.regularV4FrontierRepairPressure.sourceBoundary.dataSessionGeneration++ }
    }
    $candidate.aggregate.operations = @($candidate.aggregate.operations) + @($carry)
    $badLines = @(New-Lines $values)
    Assert-Parity $badLines $true
    $result = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $badLines
    $boundary=$result.FirstUnresolvedBoundary
    if ($boundary.decisionReason -cne 'snapshot_regular_v4_pressure_recovery_handoff_terminal_receipt_invalid' -or
        $boundary.referencedTransitionId -cne $candidate.transitionId -or $boundary.rawCount -ne 1 -or
        $boundary.distinctCount -ne 1 -or $boundary.recoveredCount -ne 0 -or $boundary.unresolvedCount -ne 1) {
        throw "Malformed carry was not selected exactly: $mutation; expected=$($candidate.transitionId); actual=$($boundary | Select-Object * -ExcludeProperty operations | ConvertTo-Json -Depth 4 -Compress)"
    }
}
$conflicting = $envelopes | ConvertTo-Json -Depth 64 | ConvertFrom-Json
$conflicting[0].decisionReason += '-conflicting-copy'
$conflictLine = @(New-Lines $conflicting)[1]
Assert-Parity (@($lines[0]) + @($conflictLine) + $lines[1..($lines.Count-1)]) $true
'Retained handoff receipt mutation and full/compact/GUI controls passed.'
