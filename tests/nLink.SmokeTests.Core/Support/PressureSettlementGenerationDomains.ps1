param([string]$RepoRoot, [string]$FixturePath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
if ($envelopes.Count -ne 8) { throw 'Expected eight coordinator-produced envelopes.' }
$bindingFailure = 'snapshot_regular_v4_pressure_settlement_transition_invalid|pressure_settlement_attempt_binding_invalid'
$controls = 0
function Test-Case($Envelope, [string]$Expected, [string]$Name) {
    $actual = Get-FileTransferCoordinatorSnapshotFamily5PressureSettlementFailure -Envelope $Envelope
    if ($actual -cne $Expected) { throw "$Name expected '$Expected', got '$actual'." }
    $script:controls++
}
foreach ($envelope in $envelopes) {
    foreach ($operation in $envelope.aggregate.operations) {
        if (-not (Test-FileTransferCoordinatorSnapshotFamily5OperationCatalog -Envelope $envelope -Operation $operation)) {
            throw "Coordinator-produced operation violates the existing catalog: $($operation.operationRole)."
        }
    }
    Test-Case $envelope '' 'coordinator-produced settlement'
}
foreach ($envelope in $envelopes) {
    # Each negative case changes only the named evidence edge of a valid envelope.
    foreach ($mutation in @('missing','duplicate','successor','predecessor','install','terminal_install','settlement_receipt','zero_install',
        'operation','decision','command','receipt','order','fingerprint','source','effective','successor_mutation',
        'mirror','retirement')) {
        $copy = $envelope | ConvertTo-Json -Depth 32 | ConvertFrom-Json
        $attempt = @($copy.aggregate.operations | Where-Object operationRole -CEQ 'attempted_decision')[0]
        $terminal = @($copy.aggregate.operations | Where-Object operationRole -CEQ 'pressure_settlement_terminal')[0]
        $payload = $attempt.regularV4FrontierRepairPressure
        $expected = $bindingFailure
        switch ($mutation) {
            'missing' { $copy.aggregate.operations = @($copy.aggregate.operations | Where-Object operationRole -CNE 'attempted_decision') }
            'duplicate' { $copy.aggregate.operations = @($copy.aggregate.operations) + @($attempt) }
            'successor' { $attempt.authorityId += '-wrong' }
            'predecessor' { $attempt.predecessorAuthorityId += '-wrong' }
            'install' { $payload.pressureInstallGeneration++ }
            'terminal_install' { $terminal.regularV4FrontierRepairPressure.pressureInstallGeneration++ }
            'settlement_receipt' { $terminal.receiptGeneration++ }
            'zero_install' {
                $payload.pressureInstallGeneration = 0
                $terminal.regularV4FrontierRepairPressure.pressureInstallGeneration = 0
            }
            'operation' { $attempt.operationGeneration++ }
            'decision' { $payload.decisionGeneration++ }
            'command' { $attempt.commandGeneration++ }
            'receipt' { $attempt.receiptGeneration++ }
            'order' {
                $attempt.operationGeneration = $terminal.operationGeneration
                $attempt.commandGeneration = $terminal.operationGeneration
                $attempt.receiptGeneration = $terminal.operationGeneration
                $payload.decisionGeneration = $terminal.operationGeneration
            }
            'fingerprint' { $attempt.commandFingerprint += '-wrong' }
            'source' { $payload.sourceBoundary.runtimeGeneration++ }
            'effective' { $payload.effectiveBoundary.runtimeGeneration++ }
            'successor_mutation' {
                $mutationOperation = $attempt | ConvertTo-Json -Depth 32 | ConvertFrom-Json
                $mutationOperation.operationRole = 'pressure_binding_installed'
                $mutationOperation.stateApplied = $true
                $copy.aggregate.operations = @($copy.aggregate.operations) + @($mutationOperation)
                $expected = 'snapshot_regular_v4_pressure_settlement_transition_invalid'
            }
            'mirror' {
                $copy.aggregate.operations = @($copy.aggregate.operations | Where-Object operationRole -CNE 'repair_authority_terminal')
                $expected = 'snapshot_regular_v4_pressure_settlement_transition_invalid|pressure_settlement_entering_terminal_mirror_invalid'
            }
            'retirement' {
                $copy.aggregate.operations = @($copy.aggregate.operations | Where-Object operationRole -CNE 'pressure_binding_retained_predecessor')
                $expected = 'snapshot_regular_v4_pressure_settlement_transition_invalid'
            }
        }
        Test-Case $copy $expected $mutation
    }
    # Direct validator fixture: repair generations need ordering, not adjacency
    # or equality with the unchanged pressure-install domain.
    $ordered = $envelope | ConvertTo-Json -Depth 32 | ConvertFrom-Json
    $attempt = @($ordered.aggregate.operations | Where-Object operationRole -CEQ 'attempted_decision')[0]
    $attempt.operationGeneration += 5
    $attempt.commandGeneration = $attempt.operationGeneration
    $attempt.receiptGeneration = $attempt.operationGeneration
    $attempt.regularV4FrontierRepairPressure.decisionGeneration = $attempt.operationGeneration
    Test-Case $ordered '' 'non-adjacent ordered repair generation'
}
Write-Output "Pressure settlement generation controls passed: $controls across $($envelopes.Count) coordinator-produced envelopes."
