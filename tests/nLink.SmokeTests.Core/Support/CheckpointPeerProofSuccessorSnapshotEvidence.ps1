param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')

$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') {
    throw 'Frozen catalog changed.'
}
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
function Copy-Value($value) {
    ConvertTo-Json -InputObject $value -Depth 64 -Compress | ConvertFrom-Json
}
function Get-PendingProof($envelope, [string]$afterState) {
    $matches = @($envelope.aggregate.operations | Where-Object {
            $_.family -ceq 'fallback_tail_yield' -and
            $_.operationRole -ceq 'peer_proof_pending_initial' -and
            $_.afterState -ceq $afterState
        })
    if ($matches.Count -ne 1) { throw "Pending proof is not unique: $afterState" }
    $matches[0]
}

$successor = $envelopes[-1]
if (-not (Test-FileTransferCoordinatorSnapshotCheckpointPeerProofSuccessor $successor)) {
    throw 'Production checkpoint successor envelope was rejected.'
}
foreach ($mutation in @(
    { param($e,$a,$b) $e.decisionStatus = 'Rejected' },
    { param($e,$a,$b) $e.decisionReason = 'wrong' },
    { param($e,$a,$b) $e.aggregate.afterStateGeneration++ },
    { param($e,$a,$b) $e.aggregate.afterStateFingerprint = $e.aggregate.beforeStateFingerprint },
    { param($e,$a,$b) $e.aggregate.operations += Copy-Value $b },
    { param($e,$a,$b) $a.afterState = 'Pending' },
    { param($e,$a,$b) $b.beforeState = 'Pending' },
    { param($e,$a,$b) $b.operationGeneration = $a.operationGeneration },
    { param($e,$a,$b) $b.authorityId = $a.authorityId },
    { param($e,$a,$b) $b.fallbackTailYield.ownerKey = 'wrong' },
    { param($e,$a,$b) $b.fallbackTailYield.pairKey = 'wrong' },
    { param($e,$a,$b) $b.fallbackTailYield.sourceBoundary.checkpointGeneration = $a.fallbackTailYield.sourceBoundary.checkpointGeneration },
    { param($e,$a,$b) $b.fallbackTailYield.sourceBoundary.transportEpoch++ },
    { param($e,$a,$b) $b.fallbackTailYield.peerProofIdentity = '(none)' },
    { param($e,$a,$b) $b.fallbackTailYield.receipts[0].receiptValidated = $false },
    { param($e,$a,$b) ($e.aggregate.operations | Where-Object operationRole -ceq 'attempted_decision')[0].authorityId = $a.authorityId }
)) {
    $bad = Copy-Value $successor
    & $mutation $bad (Get-PendingProof $bad 'None') (Get-PendingProof $bad 'Pending')
    if (Test-FileTransferCoordinatorSnapshotCheckpointPeerProofSuccessor $bad) {
        throw "Malformed checkpoint successor admitted: $mutation"
    }
}

$first = $envelopes[0]
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$families = $catalog.RequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
function New-Effect($envelope, $operation, [int]$sequence) {
    $scope = $operation.fallbackTailYield.sourceBoundary
    "event=filetransfer_coordinator_authority_effect; $prefix; transition_id=$($envelope.transitionId); coordinator_state_generation=$($envelope.aggregate.afterStateGeneration); operation_family=$($operation.family); operation_key=$($operation.operationKey); operation_authority_id=$($operation.authorityId); operation_generation=$($operation.operationGeneration); effect_kind=peer_proof_observed; effect_identity=peer-proof-$sequence; effect_generation=$($operation.receiptGeneration); receipt_identity=$($operation.receiptIdentity); receipt_generation=$($operation.receiptGeneration); receipt_status=$($operation.receiptStatus); receipt_fingerprint=$($operation.receiptFingerprint); route=$($scope.route); protocol_version=$($scope.protocolVersion); live_route_epoch=$($scope.liveRouteEpoch); route_publication_generation=$($scope.routePublicationGeneration); route_commit_generation=$($scope.routeCommitGeneration); runtime_generation=$($scope.runtimeGeneration); terminal_generation=$($scope.terminalGeneration); leg_generation=$($scope.legGeneration); transport_epoch=$($scope.transportEpoch); bridge_recovery_generation=$($scope.bridgeRecoveryGeneration); checkpoint_generation=$($scope.checkpointGeneration); checkpoint_request_id=$($scope.checkpointRequestId); data_session_identity=$($scope.dataSessionIdentity); data_session_generation=$($scope.dataSessionGeneration); authority_effect=applied; log_writer_id=fixture-writer; log_writer_sequence=$sequence"
}
function New-Lines([object[]]$values, [string]$effectCase = '') {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=$($values[0].aggregate.beforeStateGeneration); initial_state_hash=$($values[0].aggregate.beforeStateFingerprint); $coverage; authority_effect=none")
    for ($i = 0; $i -lt $values.Count; $i++) {
        $envelope = $values[$i]
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($envelope | ConvertTo-Json -Depth 64 -Compress))
        $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
        $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
        $sequence = 10 * ($i + 1)
        $lines.Add("event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=$($envelope.transitionId); before_state_generation=$($envelope.aggregate.beforeStateGeneration); after_state_generation=$($envelope.aggregate.afterStateGeneration); snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none; log_writer_id=fixture-writer; log_writer_sequence=$sequence")
        if ($envelope.decisionKind -ceq 'ApplyFallbackTailPeerProofReceipt') {
            $operation = Get-PendingProof $envelope 'Pending'
            if (($i -eq 0 -and $effectCase -cne 'missing-initial') -or
                ($i -gt 0 -and $effectCase -cne 'missing-successor')) {
                $lines.Add((New-Effect $envelope $operation ($sequence + 1)))
            }
            if ($i -gt 0 -and $effectCase -ceq 'copied-retired') {
                $lines.Add((New-Effect $envelope (Get-PendingProof $envelope 'None') ($sequence + 2)))
            }
        }
    }
    $last = $values[-1]
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=$($last.aggregate.afterStateGeneration); final_state_hash=$($last.aggregate.afterStateFingerprint); transition_count=$($values.Count); $coverage; authority_effect=none")
    $lines.ToArray()
}

$ast = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$null, [ref]$null)
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification','Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definitions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name }, $true))
    if ($definitions.Count -ne 1) { throw "GUI function not unique: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
function Assert-Parity([string[]]$inputLines, [bool]$valid) {
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated') + $inputLines)
    $compactLines = @($inputLines | Where-Object {
            $name = Get-FileTransferCoordinatorSnapshotEventName -Line $_
            $catalog.SnapshotAuthorityV1 -contains $name -or
                $catalog.GenerationStampedEffects -contains $name
        })
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        (($full.ArtifactEvidenceVerdict -ceq 'pass') -ne $valid) -or
        ($valid -and $null -ne $full.FirstUnresolvedBoundary)) {
        throw "Unexpected shared result: valid=$valid; reason=$($full.ArtifactEvidenceFailureReason); boundary=$($full.FirstUnresolvedBoundary | ConvertTo-Json -Depth 10 -Compress)"
    }
    $path = Join-Path $OutputRoot 'compact.log'
    [IO.File]::WriteAllLines($path, $compactLines)
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
    if ($valid) {
        if ($null -ne $gui) { throw 'GUI rejected valid checkpoint successor.' }
    }
    else {
        $boundary = $full.FirstUnresolvedBoundary
        if ($gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or
            $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or
            $gui.CoordinatorWriter -cne $boundary.writer -or
            $gui.CoordinatorWriterSequence -ne $boundary.writerSequence) {
            throw 'GUI boundary diverged.'
        }
    }
}

Assert-Parity (New-Lines $envelopes) $true
$reordered = @($envelopes | ForEach-Object { Copy-Value $_ })
$reordered[-1].aggregate.operations = @($reordered[-1].aggregate.operations | Sort-Object operationKey -Descending)
Assert-Parity (New-Lines $reordered) $true
foreach ($case in @('missing-initial','missing-successor','copied-retired')) {
    Assert-Parity (New-Lines $envelopes $case) $false
}
Assert-Parity (New-Lines @($successor)) $false
foreach ($removeAt in @(1,2)) {
    $missingCheckpoint = [Collections.Generic.List[object]]::new()
    foreach ($envelope in $envelopes) { $missingCheckpoint.Add((Copy-Value $envelope)) }
    $missingCheckpoint.RemoveAt($removeAt)
    Assert-Parity (New-Lines $missingCheckpoint.ToArray()) $false
}
Write-Output 'Checkpoint peer-proof successor: exact rollover, history/effects, and full/compact/GUI parity passed.'
