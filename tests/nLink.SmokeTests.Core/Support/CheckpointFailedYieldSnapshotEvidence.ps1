param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot,
    [string]$CheckpointTransitionId, [string]$FailedTransitionId, [string]$PriorTerminalTransitionId,
    [string]$RunNegativeControls)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') {
    throw 'Frozen catalog changed.'
}
# Direct assignment preserves JSON arrays in Windows PowerShell 5.1.
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
$retryOperations = @($envelopes.aggregate.operations | Where-Object {
    $null -ne (Get-FileTransferCoordinatorSnapshotObjectProperty $_.routeTransition 'activationRetryBinding')
})
if ($retryOperations.Count -gt 0) {
    foreach ($operation in $retryOperations) {
        if (Test-FileTransferActivationRetryOperation $operation $catalog) { throw 'Base catalog accepted unsupported activation retry.' }
    }
    $catalog = Get-FileTransferCoordinatorActivationRetryCatalog
    if ($catalog.Hash -cne '6f8667e92dae781e679bbe6f59fbd4f3d6284cd893ad2f643a9e47a21d451b00') { throw 'Frozen activation-retry catalog changed.' }
}
foreach ($source in @(
    @{ Path='tests/nLink.SmokeTests.Core/Support/PeerFirstFailedYieldSnapshotEvidence.ps1'; Names=@('Copy-Value','Subject','Convert-Envelope') },
    @{ Path='tests/nLink.SmokeTests.Core/Support/FileTunaPressureSettlementSnapshotEvidence.ps1'; Names=@('New-Effect','New-Lines') },
    @{ Path='tools/GuiSmoke-Windows.ps1'; Names=@('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification','Get-TunaGuiFileTransferSetupFailureClassification') })) {
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot $source.Path),[ref]$null,[ref]$null)
    foreach ($name in $source.Names) {
        $definitions = @($ast.FindAll({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
        }, $true))
        if ($definitions.Count -ne 1) { throw "Function not unique: $name" }
        . ([scriptblock]::Create($definitions[0].Extent.Text))
    }
}
function Exact-Envelope($values, [string]$transitionId) {
    $matches = @($values | Where-Object { [string]$_.transitionId -ceq $transitionId })
    if ($matches.Count -ne 1) { throw "Exact fixture transition not unique: $transitionId" }
    return $matches[0]
}
function Failed-Terminal($envelope) {
    $matches = @($envelope.aggregate.operations | Where-Object {
        $_.family -ceq 'fallback_tail_yield' -and $_.operationRole -ceq 'yield_terminal' -and
            $_.operationKind -ceq 'Failed' -and $_.afterState -ceq 'Invalidated'
    })
    if ($matches.Count -ne 1) { throw 'Exact Failed terminal not unique.' }
    return $matches[0]
}
function Stage($operation, [string]$name) {
    $matches = @($operation.fallbackTailYield.receipts | Where-Object { $_.stage -ceq $name })
    if ($matches.Count -ne 1) { throw "Exact receipt stage not unique: $name" }
    return $matches[0]
}

$checkpoint = Exact-Envelope $envelopes $CheckpointTransitionId
$failed = Exact-Envelope $envelopes $FailedTransitionId
$current = Subject $checkpoint 'yield_current'
$terminal = Failed-Terminal $checkpoint
$oldTerminals = @($checkpoint.aggregate.operations | Where-Object {
    $_.family -ceq 'fallback_tail_yield' -and $_.operationRole -ceq 'yield_terminal' -and
        $_.operationKind -ceq 'Quiescent' -and $_.beforeState -ceq 'Invalidated' -and $_.afterState -ceq 'None'
})
$hasOldTerminal = $PriorTerminalTransitionId -cne '(none)'
if ($oldTerminals.Count -ne [int]$hasOldTerminal) { throw 'Unexpected retained terminal count.' }
if ($hasOldTerminal) {
    $oldOrigin = Exact-Envelope $envelopes $PriorTerminalTransitionId
    $oldOriginal = @($oldOrigin.aggregate.operations | Where-Object {
        $_.operationKey -ceq $oldTerminals[0].operationKey -and $_.operationRole -ceq 'yield_terminal' -and
            $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Invalidated'
    })
    if ($oldOriginal.Count -ne 1) { throw 'Production retained terminal origin missing.' }
}
$immutableCheckpoint = $checkpoint | ConvertTo-Json -Depth 64 -Compress
$immutableFailed = $failed | ConvertTo-Json -Depth 64 -Compress
if (-not (Test-FileTransferCoordinatorSnapshotFallbackCheckpointScopeMutation $checkpoint) -or
    -not (Test-FileTransferCoordinatorSnapshotFailedCheckpointYieldCandidate $checkpoint)) {
    throw "Production Failed peer-first checkpoint sibling rejected: $($checkpoint | ConvertTo-Json -Depth 64 -Compress)"
}
foreach ($operation in @($checkpoint.aggregate.operations | Where-Object family -CEQ 'fallback_tail_yield')) {
    if (-not (Test-FileTransferCoordinatorSnapshotFamily4OperationCatalog $checkpoint $operation)) {
        throw 'Production checkpoint operation catalog rejected.'
    }
}
if ($current.operationGeneration -eq $current.receiptGeneration -or
    $current.receiptGeneration -ne (Stage $current 'peer_proof').receiptGeneration -or
    $current.operationGeneration -ne (Stage $current 'yield').receiptGeneration -or
    $terminal.receiptGeneration -ne (Stage $terminal 'invalidation').receiptGeneration) {
    throw 'Production fixture did not preserve distinct peer/yield receipt generation domains.'
}
foreach ($name in @('yield','peer_proof')) {
    if (((Stage $current $name) | ConvertTo-Json -Depth 64 -Compress) -cne
        ((Stage $terminal $name) | ConvertTo-Json -Depth 64 -Compress)) {
        throw "Checkpoint changed immutable stage: $name"
    }
}
if (@($checkpoint.aggregate.operations | Where-Object { $_.family -ceq 'fallback_tail_yield' }).Count -ne (2 + [int]$hasOldTerminal) -or
    [long]$current.fallbackTailYield.retryRecheckGeneration -ne 0 -or
    [long]$terminal.fallbackTailYield.retryRecheckGeneration -ne 0) {
    throw 'Checkpoint inherited a stale retry child or metadata.'
}

$first = $envelopes[0]
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$families = $catalog.RequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
function Checkpoint-Lines([object[]]$values, [string]$forgedEffect = '') {
    $first = $values[0]
    # Envelopes are production output. Stream markers and exact receipt-backed
    # physical effect records are synthetic harness scaffolding only.
    $lines = [Collections.Generic.List[string]]::new()
    foreach ($line in @(New-Lines $values)) { $lines.Add($line) }
    for ($i = 0; $i -lt $values.Count; $i++) {
        $envelope = $values[$i]
        foreach ($operation in @($envelope.aggregate.operations | Where-Object family -CEQ 'fallback_tail_yield')) {
            $kind = ''
            if ($envelope.decisionKind -ceq 'ApplyFallbackTailPeerProofReceipt' -and
                $operation.operationRole -ceq 'peer_proof_pending_initial' -and $operation.afterState -ceq 'Pending') {
                $kind = 'peer_proof_observed'
            }
            elseif ($envelope.decisionKind -ceq 'BindFallbackTailYieldRetryDispatchHold' -and
                $operation.operationRole -ceq 'retry_hold_current' -and $operation.receiptValidated) {
                $kind = 'retry_dispatch_hold_observed'
            }
            elseif ($envelope.decisionKind -ceq 'ApplyFallbackTailYieldRetryDispatchRecheckReceipt' -and
                $operation.operationRole -ceq 'retry_recheck_terminal' -and $operation.afterState -ceq 'Queued' -and
                $envelope.decisionStatus -ceq 'Accepted') {
                $kind = 'retry_dispatch_recheck_observed'
            }
            if ($kind -cne '') { $lines.Insert($lines.Count - 1, (New-Effect $envelope $operation $kind (20L * ($i + 1) + 19))) }
        }
        if ($envelope.transitionId -ceq $CheckpointTransitionId -and $forgedEffect -cne '') {
            $lines.Insert($lines.Count - 1, (New-Effect $envelope (Subject $envelope 'yield_current') $forgedEffect (20L * ($i + 1) + 19)))
        }
    }
    return $lines.ToArray()
}
function Normalized-Json($evaluation) {
    $values = @($evaluation.Boundaries | Sort-Object coordinatorInstanceId,beforeStateGeneration,afterStateGeneration,boundaryId |
        Select-Object boundaryId,boundaryHash,coordinatorInstanceId,writer,writerSequence,decisionUtcMilliseconds,
            beforeStateGeneration,afterStateGeneration,beforeStateHash,afterStateHash,decisionKind,decisionStatus,
            decisionReason,families,operations,verdict,taxonomyClass,domain,phase,rawCount,distinctCount,recoveredCount,unresolvedCount)
    return ConvertTo-Json -InputObject $values -Depth 64 -Compress
}
function Assert-CheckpointParity([string[]]$lines, [bool]$valid, [string]$variant = '') {
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated_diagnostic') + $lines)
    $compactLines = @($lines | Where-Object {
        $name = Get-FileTransferCoordinatorSnapshotEventName $_
        $catalog.SnapshotAuthorityV1 -contains $name -or $catalog.GenerationStampedEffects -contains $name
    })
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        (Normalized-Json $full) -cne (Normalized-Json $compact) -or
        (($full.ArtifactEvidenceVerdict -ceq 'pass' -and $null -eq $full.FirstUnresolvedBoundary) -ne $valid)) {
        throw "Checkpoint parity: expected_valid=$valid; reason=$($full.ArtifactEvidenceFailureReason); boundary=$($full.FirstUnresolvedBoundary | ConvertTo-Json -Depth 12 -Compress)"
    }
    if ($valid) {
        if ($full.LegacySelectionCount -ne 0 -or @($full.Boundaries | Where-Object {
            $_.rawCount -ne 0 -or $_.distinctCount -ne 0 -or $_.recoveredCount -ne 0 -or $_.unresolvedCount -ne 0
        }).Count -ne 0) { throw 'Valid checkpoint did not produce strict 0/0/0/0.' }
    }
    elseif ($variant -cne '' -and $full.FirstUnresolvedBoundary.variant -cne $variant) {
        throw "Checkpoint variant: expected=$variant; actual=$($full.FirstUnresolvedBoundary.variant)"
    }
    $path = Join-Path $OutputRoot 'checkpoint-compact.log'
    [IO.File]::WriteAllLines($path, $compactLines)
    $guiEvaluation = Get-FileTransferCoordinatorSnapshotEvaluation -Lines ([IO.File]::ReadAllLines($path))
    if ((Normalized-Json $guiEvaluation) -cne (Normalized-Json $full) -or
        $guiEvaluation.BoundaryHash -cne $full.BoundaryHash) { throw 'Independent GUI-file evaluation differs.' }
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
    $boundary = $full.FirstUnresolvedBoundary
    if ($valid) {
        # These focused fixtures begin on an established fallback route without
        # its earlier publication history. Snapshot validity stays 0/0/0/0;
        # the shared GUI must retain the independently proven partial-route stop.
        $route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
        $compactRoute = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $compact
        if ((Compare-FileTransferCoordinatorSnapshotRouteConsistency $route $compactRoute).Verdict -cne 'pass' -or
            $route.BoundaryHash -cne $compactRoute.BoundaryHash) { throw 'Checkpoint partial-route parity changed.' }
        $publications = @{}
        foreach ($envelope in $envelopes) {
            foreach ($operation in @($envelope.aggregate.operations | Where-Object {
                $_.family -ceq 'route_runtime' -and $_.operationRole -ceq 'route_publication' -and $_.afterState -ceq 'Published'
            })) {
                $generation = [long]$operation.operationGeneration
                if ($publications.ContainsKey($generation)) { throw 'Checkpoint publication origin is ambiguous.' }
                $publications[$generation] = [pscustomobject]@{ Envelope=$envelope; Operation=$operation }
            }
        }
        $expectedRouteBoundaryId = $envelopes[0].transitionId
        $expectedFindings = @('snapshot authoritative route stream contains no route publications')
        if ($hasOldTerminal) {
            $firstGeneration = if ($first.direction -ceq 'inbound') { 4L } else { 5L }
            $nextGeneration = $firstGeneration + 1L
            if ($publications.Count -ne 2 -or -not $publications.ContainsKey($firstGeneration) -or
                -not $publications.ContainsKey($nextGeneration)) { throw 'Checkpoint partial-route publication generations changed.' }
            $expectedRouteBoundaryId = $publications[$firstGeneration].Envelope.transitionId
            $scope = "$($first.coordinatorInstanceId)|fixture-writer|$($first.sessionId)|$($first.transferId)|$($first.direction)"
            $expectedFindings = @("snapshot route publication generation is not contiguous: scope=$scope; generation=$firstGeneration")
            if ($first.direction -ceq 'outbound') {
                $expectedFindings += 'snapshot initial route publication has invalid kind or predecessor'
            }
            $expectedFindings += "snapshot route publication generation is not contiguous: scope=$scope; generation=$nextGeneration"
            if ($first.direction -ceq 'inbound') {
                $expectedFindings += 'snapshot route publication has no unique runtime owner entrant: route=post_tuna_fallback_v6'
                $expectedFindings += 'snapshot same-route publication has no exact fallback owner successor contract'
            }
            else {
                $expectedFindings += "snapshot route publication has no exact committed predecessor transition: route=post_tuna_fallback_v6; operation_key=$($publications[$nextGeneration].Operation.operationKey)"
            }
        }
        elseif ($publications.Count -ne 0) { throw 'Checkpoint no-prior fixture unexpectedly contains publication history.' }
        $expectedBoundaries = @($full.Boundaries | Where-Object boundaryId -CEQ $expectedRouteBoundaryId)
        $boundary = $route.FirstUnresolvedBoundary
        if ($route.Verdict -cne 'fail' -or $expectedBoundaries.Count -ne 1 -or $null -eq $boundary -or
            (ConvertTo-Json -InputObject @($route.Findings) -Compress) -cne
                (ConvertTo-Json -InputObject $expectedFindings -Compress) -or
            $boundary.boundaryId -cne $expectedRouteBoundaryId -or $boundary.boundaryHash -cne $expectedBoundaries[0].boundaryHash -or
            $boundary.taxonomyClass -cne 'filetransfer_route_consistency_analysis_parity_invalid' -or
            $boundary.variant -cne 'snapshot_authoritative_route_boundary_invalid' -or
            $boundary.rawCount -ne 1 -or $boundary.distinctCount -ne 1 -or $boundary.recoveredCount -ne 0 -or $boundary.unresolvedCount -ne 1 -or
            $gui.ClassificationSource -cne 'coordinator_snapshot_authority_v1_route_consistency') {
            throw 'Checkpoint scoped partial-route findings or selected origin changed.'
        }
    }
    if ($gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or
        $gui.CoordinatorWriter -cne $boundary.writer -or $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or
        $gui.FailureClass -cne $boundary.taxonomyClass -or $gui.FailureVariant -cne $boundary.variant -or
        $gui.CoordinatorRawCount -ne $boundary.rawCount -or $gui.CoordinatorDistinctCount -ne $boundary.distinctCount -or
        $gui.CoordinatorRecoveredCount -ne $boundary.recoveredCount -or $gui.CoordinatorUnresolvedCount -ne $boundary.unresolvedCount) {
        throw 'GUI classification lost exact checkpoint taxonomy, location, or quartet.'
    }
}

$structuralVariant = 'checkpoint_failed_peer_first_invalidation_terminal_rollover_omitted_from_catalog'
$historyVariant = 'checkpoint_failed_peer_first_invalidation_terminal_rollover_history_unproven'
$effectVariant = 'checkpoint_failed_peer_first_invalidation_terminal_rollover_forbidden_effect'
$lines = @(Checkpoint-Lines $envelopes)
Assert-CheckpointParity $lines $true
if ($RunNegativeControls -cne 'True') {
    Write-Output 'Checkpoint Failed peer-first yield: production normalized full/compact/GUI parity passed.'
    return
}
$reordered = Copy-Value $envelopes
foreach ($envelope in $reordered) { [array]::Reverse($envelope.aggregate.operations) }
Assert-CheckpointParity (Checkpoint-Lines $reordered) $true
Assert-CheckpointParity (@($lines[0],$lines[1],$lines[1]) + $lines[2..($lines.Count - 1)]) $true
$conflict = $lines[1] -replace 'snapshot_sha256=[^;]+', 'snapshot_sha256=conflict'
Assert-CheckpointParity (@($lines[0],$lines[1],$conflict) + $lines[2..($lines.Count - 1)]) $false
foreach ($effect in @('peer_proof_observed','local_proof_sent','paired_release_published','retry_dispatch_recheck_observed')) {
    Assert-CheckpointParity (Checkpoint-Lines $envelopes $effect) $false $effectVariant
}

foreach ($mutation in @(
    { param($e,$c,$t) $e.aggregate.operations = @() },
    { param($e,$c,$t) $e.aggregate.operations = @($e.aggregate.operations | Where-Object operationRole -CNE 'yield_current') },
    { param($e,$c,$t) $e.aggregate.operations = @($e.aggregate.operations | Where-Object { $_.operationKey -cne $t.operationKey }) },
    { param($e,$c,$t) $e.aggregate.operations += Copy-Value $c },
    { param($e,$c,$t) $e.aggregate.operations += Copy-Value $t },
    { param($e,$c,$t) $c.operationRole = 'retry_recheck_terminal' },
    { param($e,$c,$t) $c.operationKind = 'Quiescent' },
    { param($e,$c,$t) $c.beforeState = 'LocalProofObserved' },
    { param($e,$c,$t) $c.afterState = 'PeerProofObserved' },
    { param($e,$c,$t) $t.afterState = 'Released' },
    { param($e,$c,$t) $c.operationKey += '-wrong' },
    { param($e,$c,$t) $c.authorityId = '(none)' },
    { param($e,$c,$t) $t.authorityId += '-foreign' },
    { param($e,$c,$t) $c.operationGeneration = 0 },
    { param($e,$c,$t) $t.operationGeneration++ },
    { param($e,$c,$t) $c.stateApplied = $false },
    { param($e,$c,$t) $c.receiptValidated = $false },
    { param($e,$c,$t) $c.fallbackTailYield.role = 'yield_terminal' },
    { param($e,$c,$t) $t.fallbackTailYield.yieldKind = 'Quiescent' },
    { param($e,$c,$t) $c.fallbackTailYield.ownerKey += '-wrong' },
    { param($e,$c,$t) $t.fallbackTailYield.pairKey += '-wrong' },
    { param($e,$c,$t) $c.fallbackTailYield.peerProofAuthorityId += '-wrong' },
    { param($e,$c,$t) $t.fallbackTailYield.peerProofYieldGeneration++ },
    { param($e,$c,$t) $c.fallbackTailYield.peerProofIdentity += '-wrong' },
    { param($e,$c,$t) $t.fallbackTailYield.peerProofFingerprint += '-wrong' },
    { param($e,$c,$t) $c.fallbackTailYield.receipts = @() },
    { param($e,$c,$t) $c.fallbackTailYield.receipts = @($c.fallbackTailYield.receipts | Where-Object stage -CNE 'yield') },
    { param($e,$c,$t) $t.fallbackTailYield.receipts = @($t.fallbackTailYield.receipts | Where-Object stage -CNE 'peer_proof') },
    { param($e,$c,$t) $t.fallbackTailYield.receipts = @($t.fallbackTailYield.receipts | Where-Object stage -CNE 'invalidation') },
    { param($e,$c,$t) $t.fallbackTailYield.receipts += Copy-Value (Stage $t 'invalidation') },
    { param($e,$c,$t) (Stage $t 'invalidation').receiptFingerprint += '-wrong' },
    { param($e,$c,$t) (Stage $t 'invalidation').receiptReason = 'fallback_checkpoint_accepted' },
    { param($e,$c,$t) $t.fallbackTailYield.invalidationReason = 'fallback_checkpoint_accepted' },
    { param($e,$c,$t) $c.commandGeneration = $c.operationGeneration },
    { param($e,$c,$t) $c.receiptGeneration = $c.operationGeneration },
    { param($e,$c,$t) $t.receiptGeneration = $c.receiptGeneration },
    { param($e,$c,$t) (Stage $c 'peer_proof').receiptGeneration = $c.operationGeneration },
    { param($e,$c,$t) (Stage $t 'yield').commandGeneration = $c.receiptGeneration },
    { param($e,$c,$t) $c.fallbackTailYield.sourceBoundary.dataSessionGeneration++ },
    { param($e,$c,$t) $t.fallbackTailYield.effectiveBoundary.transportEpoch++ },
    { param($e,$c,$t) $t.fallbackTailYield.targetBoundary.checkpointGeneration++ },
    { param($e,$c,$t) $c.fallbackTailYield.localProofIdentity = 'forged' },
    { param($e,$c,$t) $t.fallbackTailYield.pairedReleaseAuthorityId = 'forged' },
    { param($e,$c,$t) $c.fallbackTailYield.reservationGeneration = 1 },
    { param($e,$c,$t) $t.fallbackTailYield.consumingInstallGeneration = 1 },
    { param($e,$c,$t) $c.fallbackTailYield.reauthorizationGeneration = 1 },
    { param($e,$c,$t) $c.fallbackTailYield.retryHoldGeneration = 1 },
    { param($e,$c,$t) $t.fallbackTailYield.retryRecheckGeneration = 5 },
    { param($e,$c,$t) $e.decisionKind = 'FallbackCheckpointAccepted' },
    { param($e,$c,$t) $e.decisionStatus = 'Rejected' },
    { param($e,$c,$t) $e.decisionStatus = 'Idempotent' },
    { param($e,$c,$t) $e.aggregate.afterStateGeneration++ },
    { param($e,$c,$t) $e.aggregate.afterStateFingerprint = $e.aggregate.beforeStateFingerprint }
)) {
    $bad = Copy-Value $checkpoint
    & $mutation $bad (Subject $bad 'yield_current') (Failed-Terminal $bad)
    if (Test-FileTransferCoordinatorSnapshotFailedCheckpointYieldCandidate $bad) { throw "Malformed checkpoint admitted: $mutation" }
}
foreach ($missing in @('typed-null','typed-missing','stage-field-missing','scope-null','retry-metadata-missing')) {
    $bad = Copy-Value $checkpoint
    $operation = Subject $bad 'yield_current'
    switch ($missing) {
        'typed-null' { $operation.fallbackTailYield = $null }
        'typed-missing' { $operation.PSObject.Properties.Remove('fallbackTailYield') }
        'stage-field-missing' { (Stage $operation 'yield').PSObject.Properties.Remove('receiptFingerprint') }
        'scope-null' { $operation.fallbackTailYield.sourceBoundary = $null }
        'retry-metadata-missing' { $operation.fallbackTailYield.PSObject.Properties.Remove('retryRecheckGeneration') }
    }
    if (Test-FileTransferCoordinatorSnapshotFailedCheckpointYieldCandidate $bad) { throw "Incomplete checkpoint admitted: $missing" }
}
# Representative malformed structure must retain the same precise normalized
# diagnostic through each independently evaluated evidence surface.
$badStructure = Copy-Value $envelopes
(Subject (Exact-Envelope $badStructure $CheckpointTransitionId) 'yield_current').fallbackTailYield.retryRecheckGeneration = 5
Assert-CheckpointParity (Checkpoint-Lines $badStructure) $false $structuralVariant
if ($hasOldTerminal) {
    $retainedRechecks = @($envelopes | Where-Object {
        $_.decisionKind -ceq 'ApplyFallbackTailYieldRetryDispatchRecheckReceipt' -and $_.decisionStatus -ceq 'Accepted'
    } | ForEach-Object { $_.aggregate.operations | Where-Object {
        $_.family -ceq 'fallback_tail_yield' -and $_.operationRole -ceq 'retry_recheck_terminal' -and
            $_.authorityId -ceq $oldTerminals[0].authorityId -and $_.afterState -ceq 'Queued'
    } })
    if ($retainedRechecks.Count -ne 1) { throw 'Exact retained recheck control missing.' }
    $bad = Copy-Value $envelopes
    $candidate = Exact-Envelope $bad $CheckpointTransitionId
    $rebound = Copy-Value $retainedRechecks[0]
    $rebound.authorityId = $current.authorityId
    $rebound.operationKey = "fallback_tail_yield|retry_recheck_terminal|$($current.authorityId)|$($rebound.operationGeneration)|RetryDispatchRecheck"
    $rebound.beforeState = 'Queued'; $rebound.afterState = 'None'
    $candidate.aggregate.operations += $rebound
    Assert-CheckpointParity (Checkpoint-Lines $bad) $false $structuralVariant
}

# The current B and optional retained A may not be reconstructed from the
# checkpoint retirement alone, even when the stream markers are self-consistent.
Assert-CheckpointParity (Checkpoint-Lines @($checkpoint)) $false $historyVariant
$missingCurrent = Copy-Value $envelopes
(Exact-Envelope $missingCurrent $FailedTransitionId).aggregate.operations = @()
Assert-CheckpointParity (Checkpoint-Lines $missingCurrent) $false
foreach ($mutation in @(
    { param($e,$c,$t) $c.fallbackTailYield.ownerKey += '-mutated'; $t.fallbackTailYield.ownerKey = $c.fallbackTailYield.ownerKey },
    { param($e,$c,$t) (Stage $c 'yield').receiptFingerprint += '-mutated'; (Stage $t 'yield').receiptFingerprint = (Stage $c 'yield').receiptFingerprint },
    { param($e,$c,$t) (Stage $c 'peer_proof').commandFingerprint += '-mutated'; (Stage $t 'peer_proof').commandFingerprint = (Stage $c 'peer_proof').commandFingerprint; $c.commandFingerprint = (Stage $c 'peer_proof').commandFingerprint }
)) {
    $values = Copy-Value $envelopes
    $candidate = Exact-Envelope $values $CheckpointTransitionId
    & $mutation $candidate (Subject $candidate 'yield_current') (Failed-Terminal $candidate)
    Assert-CheckpointParity (Checkpoint-Lines $values) $false
}
if ($hasOldTerminal) {
    foreach ($mode in @('missing-old','duplicate-old','wrong-kind','same-generation','source-mutation','receipt-mutation','history-missing')) {
        $values = Copy-Value $envelopes
        $candidate = Exact-Envelope $values $CheckpointTransitionId
        $old = @($candidate.aggregate.operations | Where-Object { $_.operationKey -ceq $oldTerminals[0].operationKey })[0]
        switch ($mode) {
            'missing-old' { $candidate.aggregate.operations = @($candidate.aggregate.operations | Where-Object { $_.operationKey -cne $old.operationKey }) }
            'duplicate-old' { $candidate.aggregate.operations += Copy-Value $old }
            'wrong-kind' { $old.operationKind = 'Failed' }
            'same-generation' { $old.operationGeneration = $current.operationGeneration }
            'source-mutation' { $old.fallbackTailYield.sourceBoundary.transportEpoch++ }
            'receipt-mutation' { $old.receiptFingerprint += '-mutated'; (Stage $old 'invalidation').receiptFingerprint = $old.receiptFingerprint }
            'history-missing' { (Exact-Envelope $values $PriorTerminalTransitionId).aggregate.operations = @() }
        }
        Assert-CheckpointParity (Checkpoint-Lines $values) $false
    }
}

function Checkpoint-History([object[]]$values) {
    foreach ($e in $values) {
        [pscustomobject]@{
            Boundary = [pscustomobject]@{
                coordinatorInstanceId=$e.coordinatorInstanceId; writer='fixture-writer'; boundaryId=$e.transitionId
                beforeStateGeneration=$e.aggregate.beforeStateGeneration; afterStateGeneration=$e.aggregate.afterStateGeneration
                beforeStateHash=$e.aggregate.beforeStateFingerprint; afterStateHash=$e.aggregate.afterStateFingerprint
                decisionKind=$e.decisionKind; decisionStatus=$e.decisionStatus; decisionReason=$e.decisionReason
                operations=$e.aggregate.operations; evidenceLineIndex=$e.aggregate.afterStateGeneration
            }
            BeforeBoundary=Copy-Value $e.aggregate.beforeBoundary; AfterBoundary=Copy-Value $e.aggregate.afterBoundary
            SessionId=$e.sessionId; TransferId=$e.transferId; Direction=$e.direction
        }
    }
}
function Assert-CheckpointHistory([object[]]$entries, [bool]$valid, [string]$mode) {
    $candidates = @{}; $candidates[$CheckpointTransitionId] = @{ Line=$checkpoint.aggregate.afterStateGeneration }
    $failures = @(Resolve-FileTransferCoordinatorFailedCheckpointYieldCandidates `
        -AggregateIndex (New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries $entries) `
        -Candidates $candidates -InvalidBoundaries @{} -Effects @() `
        -AllBoundaries @($entries | ForEach-Object Boundary))
    if (($failures.Count -eq 0) -ne $valid) { throw "Checkpoint history ${mode}: $($failures -join ';')" }
    if (-not $valid -and @($failures | Where-Object { $_ -notlike "*|${historyVariant}:*" }).Count -ne 0) {
        throw "Checkpoint history lost precise variant: $mode"
    }
}
$history = @(Checkpoint-History $envelopes)
Assert-CheckpointHistory $history $true 'production'
Assert-CheckpointHistory @() $false 'empty'
Assert-CheckpointHistory @(Checkpoint-History @($checkpoint)) $false 'singleton'
foreach ($mode in @('generation-gap','hash-gap','source-mutation','data-session-replacement','terminal-poison',
        'transfer-replacement','missing-current','duplicate-conflict','current-mutation-and-restore',
        'terminal-overwrite-and-restore','terminal-retire-and-restore','coordinated-terminal-origin-scope')) {
    $entries = @(Checkpoint-History (Copy-Value $envelopes))
    $issuance = @($entries | Where-Object { $_.Boundary.boundaryId -ceq $FailedTransitionId })[0]
    $candidate = @($entries | Where-Object { $_.Boundary.boundaryId -ceq $CheckpointTransitionId })[0]
    switch ($mode) {
        'generation-gap' { $candidate.Boundary.beforeStateGeneration++ }
        'hash-gap' { $candidate.Boundary.beforeStateHash += '-gap' }
        'source-mutation' { $issuance.AfterBoundary.transportEpoch++ }
        'data-session-replacement' { $issuance.AfterBoundary.dataSessionGeneration++; $candidate.BeforeBoundary.dataSessionGeneration++ }
        'terminal-poison' { $issuance.AfterBoundary.terminalGeneration++; $candidate.BeforeBoundary.terminalGeneration++ }
        'transfer-replacement' { $candidate.TransferId += '-replacement' }
        'missing-current' { $issuance.Boundary.operations = @() }
        'duplicate-conflict' {
            $duplicate = Copy-Value $issuance
            $duplicate.Boundary.boundaryId += '-conflicting'
            $entries += $duplicate
        }
        'coordinated-terminal-origin-scope' {
            $originEntry = @($entries | Where-Object { $_.Boundary.boundaryId -ceq $PriorTerminalTransitionId })[0]
            $originTerminal = @($originEntry.Boundary.operations | Where-Object { $_.operationKey -ceq $oldTerminals[0].operationKey })[0]
            $retirement = @($candidate.Boundary.operations | Where-Object { $_.operationKey -ceq $oldTerminals[0].operationKey })[0]
            foreach ($operation in @($originTerminal,$retirement)) {
                foreach ($field in @('sourceBoundary','effectiveBoundary','targetBoundary')) {
                    $operation.fallbackTailYield.$field.transportEpoch++
                }
            }
        }
        default {
            # These are direct reducer controls, not claimed production
            # envelopes. Insert hostile slot observations after B issuance.
            $changed = Copy-Value $issuance
            $restored = Copy-Value $issuance
            $changed.Boundary.boundaryId += '-changed'; $restored.Boundary.boundaryId += '-restored'
            $changed.Boundary.decisionKind = 'SyntheticHistoryMutation'; $restored.Boundary.decisionKind = 'SyntheticHistoryRestore'
            $changed.BeforeBoundary = Copy-Value $issuance.AfterBoundary; $changed.AfterBoundary = Copy-Value $issuance.AfterBoundary
            $restored.BeforeBoundary = Copy-Value $issuance.AfterBoundary; $restored.AfterBoundary = Copy-Value $issuance.AfterBoundary
            if ($mode -ceq 'current-mutation-and-restore') {
                $original = @($issuance.Boundary.operations | Where-Object operationRole -CEQ 'yield_current')[0]
            }
            else { $original = $oldTerminals[0] }
            $changedOp = Copy-Value $original; $restoredOp = Copy-Value $original
            $state = if ($mode -ceq 'current-mutation-and-restore') { 'PeerProofObserved' } else { 'Invalidated' }
            $changedOp.beforeState = $state; $changedOp.afterState = $state
            $restoredOp.beforeState = $state; $restoredOp.afterState = $state
            if ($mode -ceq 'terminal-retire-and-restore') {
                $changedOp.afterState = 'None'; $restoredOp.beforeState = 'None'
            }
            else { $changedOp.fallbackTailYield.ownerKey += '-mutated' }
            $changed.Boundary.operations = @($changedOp); $restored.Boundary.operations = @($restoredOp)
            $prefixEntries = @($entries | Where-Object { $_.Boundary.afterStateGeneration -le $issuance.Boundary.afterStateGeneration })
            $entries = $prefixEntries + @($changed,$restored,$candidate)
            for ($i = 0; $i -lt $entries.Count; $i++) {
                $entries[$i].Boundary.beforeStateGeneration = $i
                $entries[$i].Boundary.afterStateGeneration = $i + 1
                $entries[$i].Boundary.beforeStateHash = "history-$i"
                $entries[$i].Boundary.afterStateHash = "history-$($i + 1)"
            }
        }
    }
    Assert-CheckpointHistory $entries $false $mode
}
if (($checkpoint | ConvertTo-Json -Depth 64 -Compress) -cne $immutableCheckpoint -or
    ($failed | ConvertTo-Json -Depth 64 -Compress) -cne $immutableFailed) {
    throw 'Negative controls mutated production evidence.'
}
Write-Output 'Checkpoint Failed peer-first yield: production receipts, exact history/effects, and normalized full/compact/GUI parity passed.'
