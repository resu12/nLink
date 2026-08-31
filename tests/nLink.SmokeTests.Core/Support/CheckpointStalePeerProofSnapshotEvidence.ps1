param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') { throw 'Frozen catalog changed.' }
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
function Copy-Value($value) { ConvertTo-Json -InputObject $value -Depth 64 -Compress | ConvertFrom-Json }
function Subject($envelope, [string]$role) {
    $subjects = @($envelope.aggregate.operations | Where-Object { $_.operationRole -ceq $role })
    if ($subjects.Count -ne 1) { throw "Fixture subject not unique: $role" }
    $subjects[0]
}
$discard = $envelopes[-1]
if (-not (Test-FileTransferCoordinatorSnapshotFallbackTailQuiescentEnvelope $discard)) {
    throw "Production discard rejected: $($discard | ConvertTo-Json -Depth 64 -Compress)"
}
foreach ($mutation in @(
    { param($e,$p,$y) $e.decisionStatus = 'Rejected' },
    { param($e,$p,$y) $e.decisionStatus = 'Idempotent' },
    { param($e,$p,$y) $e.decisionKind = 'unrelated' },
    { param($e,$p,$y) $e.aggregate.afterStateGeneration++ },
    { param($e,$p,$y) $e.aggregate.afterStateFingerprint = $e.aggregate.beforeStateFingerprint },
    { param($e,$p,$y) $e.aggregate.operations = @($y) + @($p,$p) },
    { param($e,$p,$y) $foreign = Copy-Value $y; $foreign.family = 'receive_recovery'; $e.aggregate.operations += $foreign },
    { param($e,$p,$y) $p.operationRole = 'peer_proof_pending_reauthorization' },
    { param($e,$p,$y) $p.operationKind = 'Quiescent' },
    { param($e,$p,$y) $p.operationKey += '-wrong' },
    { param($e,$p,$y) $p.authorityId = '(none)' },
    { param($e,$p,$y) $p.operationGeneration = 0 },
    { param($e,$p,$y) $p.receiptValidated = $false },
    { param($e,$p,$y) $p.receiptFingerprint = 'wrong' },
    { param($e,$p,$y) $p.fallbackTailYield.receipts = @() },
    { param($e,$p,$y) $p.fallbackTailYield.receipts += Copy-Value $p.fallbackTailYield.receipts[0] },
    { param($e,$p,$y) $p.fallbackTailYield.ownerKey = 'wrong' },
    { param($e,$p,$y) $p.fallbackTailYield.pairKey = 'wrong' },
    { param($e,$p,$y) $p.fallbackTailYield.sourceBoundary.checkpointGeneration = $y.fallbackTailYield.sourceBoundary.checkpointGeneration },
    { param($e,$p,$y) $p.fallbackTailYield.sourceBoundary.checkpointGeneration = 0 },
    { param($e,$p,$y) $p.fallbackTailYield.sourceBoundary.checkpointRequestId = 'wrong' },
    { param($e,$p,$y) $y.fallbackTailYield.sourceBoundary.transportEpoch++ },
    { param($e,$p,$y) $y.fallbackTailYield.sourceBoundary.dataSessionGeneration++ },
    { param($e,$p,$y) $y.fallbackTailYield.peerProofIdentity = 'forged' },
    { param($e,$p,$y) $y.fallbackTailYield.pairedReleaseAuthorityId = 'forged' },
    { param($e,$p,$y) $y.fallbackTailYield.pairingGeneration = 1 },
    { param($e,$p,$y) $y.fallbackTailYield.receipts[0].receiptStatus = 'Observed' }
)) {
    $bad = Copy-Value $discard
    & $mutation $bad (Subject $bad 'peer_proof_pending_initial') (Subject $bad 'yield_current')
    if (Test-FileTransferCoordinatorSnapshotFallbackTailQuiescentEnvelope $bad) { throw "Malformed discard admitted: $mutation" }
}

$first = $envelopes[0]
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$families = $catalog.RequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
function New-Effect($envelope, $op, [string]$kind, [int]$sequence) {
    $scope = $op.fallbackTailYield.sourceBoundary
    "event=filetransfer_coordinator_authority_effect; $prefix; transition_id=$($envelope.transitionId); coordinator_state_generation=$($envelope.aggregate.afterStateGeneration); operation_family=$($op.family); operation_key=$($op.operationKey); operation_authority_id=$($op.authorityId); operation_generation=$($op.operationGeneration); effect_kind=$kind; effect_identity=$kind-$sequence; effect_generation=$($op.receiptGeneration); receipt_identity=$($op.receiptIdentity); receipt_generation=$($op.receiptGeneration); receipt_status=$($op.receiptStatus); receipt_fingerprint=$($op.receiptFingerprint); route=$($scope.route); protocol_version=$($scope.protocolVersion); live_route_epoch=$($scope.liveRouteEpoch); route_publication_generation=$($scope.routePublicationGeneration); route_commit_generation=$($scope.routeCommitGeneration); runtime_generation=$($scope.runtimeGeneration); terminal_generation=$($scope.terminalGeneration); leg_generation=$($scope.legGeneration); transport_epoch=$($scope.transportEpoch); bridge_recovery_generation=$($scope.bridgeRecoveryGeneration); checkpoint_generation=$($scope.checkpointGeneration); checkpoint_request_id=$($scope.checkpointRequestId); data_session_identity=$($scope.dataSessionIdentity); data_session_generation=$($scope.dataSessionGeneration); authority_effect=applied; log_writer_id=fixture-writer; log_writer_sequence=$sequence"
}
function New-Lines([object[]]$values, [string]$effectCase = '') {
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
        if ($envelope.decisionKind -ceq 'ApplyFallbackTailPeerProofReceipt' -and $effectCase -cne 'missing-original') {
            $lines.Add((New-Effect $envelope (Subject $envelope 'peer_proof_pending_initial') 'peer_proof_observed' ($sequence+1)))
        }
        if ($envelope.decisionKind -ceq 'FallbackTailQuiescent') {
            $yield = Subject $envelope 'yield_current'
            if ($effectCase -cne 'missing-yield') { $lines.Add((New-Effect $envelope $yield 'recovery_owner_retired_after_yield' ($sequence+1))) }
            if ($effectCase -ceq 'copied-peer') { $lines.Add((New-Effect $envelope $yield 'peer_proof_observed' ($sequence+2))) }
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
function Assert-Parity([string[]]$inputLines, [bool]$valid) {
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated') + $inputLines)
    $compactLines = @($inputLines | Where-Object {
        $name = Get-FileTransferCoordinatorSnapshotEventName -Line $_
        $catalog.SnapshotAuthorityV1 -contains $name -or $catalog.GenerationStampedEffects -contains $name
    })
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        ($full.ArtifactEvidenceVerdict -ceq 'pass') -ne $valid -or
        ($valid -and $null -ne $full.FirstUnresolvedBoundary)) {
        throw "Unexpected shared result: valid=$valid; actual=$($full.ArtifactEvidenceFailureReason); boundary=$($full.FirstUnresolvedBoundary | ConvertTo-Json -Depth 10 -Compress)"
    }
    $path = Join-Path $OutputRoot 'compact.log'; [IO.File]::WriteAllLines($path, $compactLines)
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
    if ($valid) { if ($null -ne $gui) { throw 'GUI rejected valid discard.' } }
    else {
        $boundary = $full.FirstUnresolvedBoundary
        if ($gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or
            $gui.CoordinatorWriter -cne $boundary.writer -or $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or
            $gui.CoordinatorRawCount -ne $boundary.rawCount -or $gui.CoordinatorDistinctCount -ne $boundary.distinctCount -or
            $gui.CoordinatorRecoveredCount -ne $boundary.recoveredCount -or $gui.CoordinatorUnresolvedCount -ne $boundary.unresolvedCount) { throw 'GUI boundary diverged.' }
    }
}
Assert-Parity (New-Lines $envelopes) $true
$reordered = @($envelopes | ForEach-Object { Copy-Value $_ })
$reordered[-1].aggregate.operations = @($reordered[-1].aggregate.operations | Sort-Object operationKey -Descending)
Assert-Parity (New-Lines $reordered) $true
foreach ($effectCase in @('missing-original','missing-yield','copied-peer')) { Assert-Parity (New-Lines $envelopes $effectCase) $false }
$lines = @(New-Lines $envelopes)
Assert-Parity (@($lines[0],$lines[1],$lines[1]) + $lines[2..($lines.Count-1)]) $true
$conflict = $lines[1] -replace 'snapshot_sha256=[^;]+', 'snapshot_sha256=conflict'
Assert-Parity (@($lines[0],$lines[1],$conflict) + $lines[2..($lines.Count-1)]) $false
Assert-Parity (@($lines[0..2]) + @($lines[2]) + $lines[3..($lines.Count-1)]) $true
Assert-Parity (New-Lines @($discard)) $false
Assert-Parity (New-Lines @($envelopes[0],$envelopes[2],$discard)) $false

# Keep the chain valid while removing authority history. A later candidate must
# not borrow a similarly shaped record from another operation or checkpoint.
foreach ($mutation in @(
    { param($v) $v[0].aggregate.operations = @() },
    { param($v) $v[1].decisionKind = 'UnrelatedCheckpointRequest' },
    { param($v) $v[2].decisionKind = 'UnrelatedCheckpointAcceptance' },
    { param($v) $p = Subject $v[0] 'peer_proof_pending_initial'; $p.commandFingerprint = 'mutated'; $p.fallbackTailYield.receipts[0].commandFingerprint = 'mutated' },
    { param($v) $p = Subject $v[0] 'peer_proof_pending_initial'; $p.fallbackTailYield.peerProofFingerprint = 'mutated' },
    { param($v) $v[2].aggregate.beforeBoundary.checkpointRequestId = 'wrong' },
    { param($v) $v[1].aggregate.afterBoundary.dataSessionGeneration++; $v[2].aggregate.beforeBoundary.dataSessionGeneration++ },
    { param($v) $v[1].aggregate.afterBoundary.terminalGeneration++; $v[2].aggregate.beforeBoundary.terminalGeneration++ }
)) {
    $bad = @($envelopes | ForEach-Object { Copy-Value $_ }); & $mutation $bad
    # No effect can be manufactured when the pending subject was removed.
    $effectCase = if (@($bad[0].aggregate.operations).Count -eq 0) { 'missing-original' } else { '' }
    Assert-Parity (New-Lines $bad $effectCase) $false
}

# A retired and then restored identical pending key cannot reestablish ownership.
$retirement = Copy-Value $envelopes[0]
$retirement.transitionId += '-retired'
(Subject $retirement 'peer_proof_pending_initial').beforeState = 'Pending'
(Subject $retirement 'peer_proof_pending_initial').afterState = 'None'
$restored = Copy-Value $envelopes[0]; $restored.transitionId += '-restored'
$aba = @((Copy-Value $envelopes[0]),$retirement,$restored) + @($envelopes[1..3] | ForEach-Object { Copy-Value $_ })
for ($i=0; $i -lt $aba.Count; $i++) {
    $aba[$i].aggregate.beforeStateGeneration = $i
    $aba[$i].aggregate.afterStateGeneration = $i+1
    $aba[$i].aggregate.beforeStateFingerprint = "aba-state-$i"
    $aba[$i].aggregate.afterStateFingerprint = "aba-state-$($i+1)"
}
Assert-Parity (New-Lines $aba) $false
Write-Output 'Checkpoint-stale discard: production scopes, strict history/effects, and full/compact/GUI parity passed.'
