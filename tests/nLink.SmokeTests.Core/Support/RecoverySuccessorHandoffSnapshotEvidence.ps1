param([string]$RepoRoot, [string]$FixtureRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') { throw 'Frozen catalog changed.' }
$guiAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$null, [ref]$null)
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult', 'Get-TunaGuiExplicitFailureClassification', 'Get-TunaGuiFileTransferSetupFailureClassification')) {
    $matches = @($guiAst.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $true))
    if ($matches.Count -ne 1) { throw "Missing GUI consumer: $name" }
    . ([scriptblock]::Create($matches[0].Extent.Text))
}

function Get-Envelopes([string[]]$Lines) {
    foreach ($line in $Lines) {
        if ((Get-FileTransferCoordinatorSnapshotEventName $line) -ceq 'filetransfer_coordinator_transition_snapshot') {
            (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $line snapshot_payload)).Value
        }
    }
}

function Assert-Consumers([string]$Name, [string[]]$Lines, [bool]$EvidenceValid, [string]$ExpectedId = '') {
    $fullLines = @('event=unrelated_diagnostic; log_writer_id=diagnostic; log_writer_sequence=1') + $Lines
    $compactLines = @($fullLines | Where-Object {
        $event = Get-FileTransferCoordinatorSnapshotEventName $_
        $catalog.SnapshotAuthorityV1 -contains $event -or $catalog.GenerationStampedEffects -contains $event
    })
    # Independent inputs, not a reused result or synthetic full summary.
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $fullLines
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        $full.BoundaryHash -cne $compact.BoundaryHash -or
        ($full.ArtifactEvidenceVerdict -ceq 'pass') -ne $EvidenceValid -or $full.LegacySelectionCount -ne 0) {
        throw "$Name evidence/parity: $($full.ArtifactEvidenceFailureReason)"
    }
    $fullIds = @((Get-Envelopes $fullLines) | ForEach-Object transitionId)
    $compactIds = @((Get-Envelopes $compactLines) | ForEach-Object transitionId)
    if (($fullIds -join '|') -cne ($compactIds -join '|')) { throw "$Name lost snapshot input coverage." }
    $fullEffects = @($fullLines | Where-Object { (Get-FileTransferCoordinatorSnapshotEventName $_) -ceq 'filetransfer_coordinator_authority_effect' })
    $compactEffects = @($compactLines | Where-Object { (Get-FileTransferCoordinatorSnapshotEventName $_) -ceq 'filetransfer_coordinator_authority_effect' })
    if (($fullEffects -join "`n") -cne ($compactEffects -join "`n")) { throw "$Name lost effect witnesses." }
    $boundary = $full.FirstUnresolvedBoundary
    if ($EvidenceValid) {
        # This deliberately Regular-only contract fixture never establishes a
        # live route epoch. Preserve that separate projection finding; assert
        # receive-recovery closure independently without fabricating a route.
        # The evaluator has already validated byte-equivalent duplicates above.
        $publications = @((Get-Envelopes $Lines) | Where-Object decisionKind -CEQ 'RoutePublication' | Sort-Object transitionId -Unique)
        if ($publications.Count -ne 1) { throw "$Name lost its unique initial publication." }
        $terminalDecisions = @((Get-Envelopes $Lines) | Where-Object decisionKind -CEQ 'Terminalized' | Sort-Object transitionId -Unique)
        $projectionCount = if ($terminalDecisions.Count -eq 1) { 0 } else { 1 }
        $expectedFirstId = if ($projectionCount -eq 1) { $publications[0].transitionId } else { $ExpectedId }
        $expectedFirstClass = if ($projectionCount -eq 1) {
            'filetransfer_live_route_epoch_projection_missing'
        } else { 'filetransfer_receive_recovery_completed_unproven_successor_missing' }
        if ($null -eq $boundary -or
            $terminalDecisions.Count -gt 1 -or $boundary.boundaryId -cne $expectedFirstId -or
            $boundary.taxonomyClass -cne $expectedFirstClass) {
            throw "$Name changed the explicit fixture boundary: $($boundary | Select-Object * -ExcludeProperty operations | ConvertTo-Json -Compress)"
        }
        $recoveryBoundaries = @($full.Boundaries | Where-Object {
            $_.unresolvedCount -gt 0 -and $_.taxonomyClass -in @(
                'filetransfer_receive_recovery_operation_incomplete',
                'filetransfer_receive_recovery_completed_unproven_successor_missing',
                'filetransfer_receive_recovery_completed_proven_successor_missing')
        })
        if ($ExpectedId -ceq '') {
            if ($recoveryBoundaries.Count -ne 0) { throw "$Name invented a receive-recovery incident." }
        }
        elseif ($recoveryBoundaries.Count -ne 1 -or $recoveryBoundaries[0].boundaryId -cne $ExpectedId -or
            $recoveryBoundaries[0].taxonomyClass -cne 'filetransfer_receive_recovery_completed_unproven_successor_missing' -or
            $recoveryBoundaries[0].rawCount -ne 1 -or $recoveryBoundaries[0].distinctCount -ne 1 -or
            $recoveryBoundaries[0].recoveredCount -ne 0 -or $recoveryBoundaries[0].unresolvedCount -ne 1) {
            throw "$Name did not preserve the exact remaining recovery obligation."
        }
        $unresolved = @($full.Boundaries | Where-Object unresolvedCount -GT 0)
        if ($unresolved.Count -ne ($projectionCount + $recoveryBoundaries.Count)) {
            throw "$Name introduced an unrelated lifecycle failure."
        }
    }
    $guiPath = Join-Path $FixtureRoot "$Name-gui.log"
    [IO.File]::WriteAllLines($guiPath, $compactLines)
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($guiPath)
    if (-not $EvidenceValid -and ($null -eq $gui -or
        $gui.Phase -cne 'evidence_finalization' -or $gui.Reason -cne 'coordinator_snapshot_evidence_invalid' -or
        $gui.FailureClass -cne 'filetransfer_coordinator_snapshot_evidence_invalid')) {
        throw "$Name GUI did not fail closed on invalid snapshot evidence."
    }
    if ($null -eq $boundary) {
        if ($null -ne $gui) { throw "$Name GUI invented a boundary: $($gui | ConvertTo-Json -Depth 8 -Compress)" }
    }
    elseif ($gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or
        $gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or
        $gui.CoordinatorWriter -cne $boundary.writer -or $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or
        $gui.CoordinatorRawCount -ne $boundary.rawCount -or $gui.CoordinatorDistinctCount -ne $boundary.distinctCount -or
        $gui.CoordinatorRecoveredCount -ne $boundary.recoveredCount -or $gui.CoordinatorUnresolvedCount -ne $boundary.unresolvedCount) {
        throw "$Name GUI normalized boundary differs."
    }
}

$complete = [IO.File]::ReadAllLines((Join-Path $FixtureRoot 'complete.log'))
$unproven = [IO.File]::ReadAllLines((Join-Path $FixtureRoot 'unproven.log'))
$terminalUnproven = [IO.File]::ReadAllLines((Join-Path $FixtureRoot 'terminal-unproven.log'))
$envelopes = @(Get-Envelopes $complete)
$handoffs = @($envelopes | Where-Object {
    $_.decisionKind -ceq 'CommitRegularV4FrontierRepairPressureRecoveryHandoff' -and $_.decisionStatus -ceq 'Committed'
})
if ($handoffs.Count -ne 1) { throw 'Expected one production handoff commit.' }
$handoff = $handoffs[0]
$completions = @((Get-Envelopes $unproven) | Where-Object decisionKind -CEQ 'ObserveReceiveRecoveryExecutionReceipt')
if ($completions.Count -ne 2) { throw 'Expected exact predecessor and successor completions.' }
$effectLines = @($complete | Where-Object {
    (Get-FileTransferCoordinatorSnapshotEventName $_) -ceq 'filetransfer_coordinator_authority_effect' -and
    (Get-FileTransferCoordinatorSnapshotLineField $_ transition_id) -ceq $handoff.transitionId
})
if ($effectLines.Count -ne 5) { throw 'Real service did not emit five handoff effects.' }
foreach ($line in $effectLines) {
    $key = Get-FileTransferCoordinatorSnapshotLineField $line operation_key
    $operation = @($handoff.aggregate.operations | Where-Object operationKey -CEQ $key)
    if ($operation.Count -ne 1 -or $key -match '\[redacted\]') { throw 'Projected/redacted effect key lost exact ownership.' }
}
Assert-Consumers 'completed' $complete $true
Assert-Consumers 'successor-unproven' $unproven $true $completions[-1].transitionId
$terminalCompletions = @((Get-Envelopes $terminalUnproven) | Where-Object decisionKind -CEQ 'ObserveReceiveRecoveryExecutionReceipt')
if ($terminalCompletions.Count -ne 2) { throw 'Expected two completions before terminal poison.' }
Assert-Consumers 'terminal-successor-unproven' $terminalUnproven $true $terminalCompletions[-1].transitionId
Assert-Consumers 'identical-duplicates' (@($complete[0]) + $complete[1..($complete.Count-2)] + $complete[1..($complete.Count-2)] + @($complete[-1])) $true
foreach ($kind in @('pressure_settlement_bridge_applied', 'local_contract_settled', 'local_contract_reference_cleared', 'recovery_handoff_committed', 'projection_detached')) {
    $without = @($complete | Where-Object {
        -not ((Get-FileTransferCoordinatorSnapshotLineField $_ transition_id) -ceq $handoff.transitionId -and
            (Get-FileTransferCoordinatorSnapshotLineField $_ effect_kind) -ceq $kind)
    })
    Assert-Consumers "missing-$kind" $without $false
}
foreach ($field in @('operation_key', 'receipt_fingerprint', 'data_session_identity', 'coordinator_state_generation')) {
    $changed = @($complete | ForEach-Object {
        if ($_ -ceq $effectLines[0]) {
            $value = Get-FileTransferCoordinatorSnapshotLineField $_ $field
            $replacement = if ($field -ceq 'coordinator_state_generation') { [string]([long]$value + 1L) } else { "$value-mutated" }
            $_.Replace("$field=$value;", "$field=$replacement;")
        }
        else { $_ }
    })
    Assert-Consumers "wrong-$field" $changed $false
}
foreach ($mutation in @('wrong-predecessor', 'generation-gap', 'effective-scope-drift')) {
    $changed = @($complete)
    $indices = @(for ($i = 0; $i -lt $changed.Count; $i++) {
        if ((Get-FileTransferCoordinatorSnapshotEventName $changed[$i]) -ceq 'filetransfer_coordinator_transition_snapshot' -and
            (Get-FileTransferCoordinatorSnapshotLineField $changed[$i] transition_id) -ceq $handoff.transitionId) { $i }
    })
    if ($indices.Count -ne 1) { throw "$mutation handoff snapshot is not unique." }
    $index = $indices[0]
    $originalLine = $changed[$index]
    $envelope = @(Get-Envelopes @($originalLine))[0]
    $successors = @($envelope.aggregate.operations | Where-Object {
        $_.family -ceq 'receive_recovery' -and $_.operationRole -ceq 'operation_current' -and
        $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Issued:None'
    })
    if ($successors.Count -ne 1) { throw "$mutation successor descriptor is not unique." }
    $successor = $successors[0]
    $before = $successor | ConvertTo-Json -Depth 64 -Compress
    switch ($mutation) {
        'wrong-predecessor' { $successor.predecessorAuthorityId = $successor.authorityId }
        'generation-gap' { $successor.operationGeneration = [long]$successor.operationGeneration + 1L }
        'effective-scope-drift' {
            $successor.receiveRecovery.effectiveBoundary.dataSessionGeneration =
                [long]$successor.receiveRecovery.effectiveBoundary.dataSessionGeneration + 1L
        }
    }
    $after = $successor | ConvertTo-Json -Depth 64 -Compress
    if ($before -ceq $after) { throw "$mutation did not change its successor descriptor." }
    # Preserve the real emitter's other records and witnesses. Rehash the changed
    # envelope so these controls test descriptor validation, not corrupt encoding.
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($envelope | ConvertTo-Json -Depth 64 -Compress))
    $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
    $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $oldHash = Get-FileTransferCoordinatorSnapshotLineField $originalLine snapshot_sha256
    $oldPayload = Get-FileTransferCoordinatorSnapshotLineField $originalLine snapshot_payload
    $changed[$index] = $originalLine.Replace("snapshot_sha256=$oldHash;", "snapshot_sha256=$hash;").Replace(
        "snapshot_payload=$oldPayload;", "snapshot_payload=$payload;")
    $observedEnvelope = @(Get-Envelopes @($changed[$index]))[0]
    $observedSuccessor = @($observedEnvelope.aggregate.operations | Where-Object operationKey -CEQ $successor.operationKey)
    if ($changed[$index] -ceq $originalLine -or $observedSuccessor.Count -ne 1 -or
        ($observedSuccessor[0] | ConvertTo-Json -Depth 64 -Compress) -cne $after -or
        (Get-FileTransferCoordinatorSnapshotLineField $changed[$index] snapshot_sha256) -cne $hash) {
        throw "$mutation was not retained in the rehashed snapshot input."
    }
    Assert-Consumers "successor-$mutation" $changed $false
}
Assert-Consumers 'conflicting-effect' (@($complete[0]) + $complete[1..($complete.Count-2)] + @($effectLines[0].Replace('receipt_status=Applied;', 'receipt_status=Rejected;')) + @($complete[-1])) $false
'Recovery successor real-emitter/full/compact/GUI contract passed.'
