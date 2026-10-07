param([string]$RepoRoot, [string]$FixturePath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
$fixtures = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
$matched = $fixtures[0]
$stale = $fixtures[1]
function Copy-Value($value) { $value | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json }
function Subject($envelope, [string]$role) {
    $subjects = @($envelope.aggregate.operations | Where-Object operationRole -CEQ $role)
    if ($subjects.Count -ne 1) { throw "Fixture subject not unique: $role" }
    return $subjects[0]
}

foreach ($envelope in @($matched[1], $matched[2], $matched[3], $stale[-1])) {
    if (-not (Test-FileTransferCoordinatorSnapshotFallbackTailFailedEnvelope $envelope)) {
        throw "Valid Failed boundary rejected: $($envelope.decisionStatus), $($envelope.transitionId)"
    }
    foreach ($operation in $envelope.aggregate.operations) {
        if (-not (Test-FileTransferCoordinatorSnapshotFamily4OperationCatalog $envelope $operation)) {
            throw 'Valid Failed operation rejected.'
        }
    }
}
$pending = Subject $stale[-1] 'peer_proof_pending_initial'
$current = Subject $stale[-1] 'yield_current'
if ($pending.beforeState -cne 'Pending' -or $pending.afterState -cne 'None' -or
    $current.afterState -cne 'YieldIssued' -or
    $current.fallbackTailYield.sourceBoundary.checkpointGeneration -ne
        ($pending.fallbackTailYield.sourceBoundary.checkpointGeneration + 1)) {
    throw 'Production fixture did not reproduce checkpoint-stale retirement.'
}

foreach ($mutation in @(
    { param($e,$p,$y) $e.aggregate.operations += Copy-Value $p },
    { param($e,$p,$y) $p.operationKey += '-forged' },
    { param($e,$p,$y) $p.receiptIdentity = 'forged' },
    { param($e,$p,$y) $p.fallbackTailYield.ownerKey = 'foreign' },
    { param($e,$p,$y) $p.fallbackTailYield.pairKey = 'foreign' },
    { param($e,$p,$y) $p.fallbackTailYield.peerProofIdentity = 'forged' },
    { param($e,$p,$y) $p.fallbackTailYield.pairedReleaseAuthorityId = 'forged' },
    { param($e,$p,$y) $y.fallbackTailYield.peerProofIdentity = $p.fallbackTailYield.peerProofIdentity },
    { param($e,$p,$y) $y.fallbackTailYield.failedSupersessionCarriedPeerProof = $true },
    { param($e,$p,$y) $y.fallbackTailYield.pairedReleaseGeneration = 1 },
    { param($e,$p,$y) foreach ($s in @('sourceBoundary','effectiveBoundary','targetBoundary')) { $p.fallbackTailYield.$s.checkpointGeneration++ } },
    { param($e,$p,$y) foreach ($s in @('sourceBoundary','effectiveBoundary','targetBoundary')) { $p.fallbackTailYield.$s.transportEpoch++ } },
    { param($e,$p,$y) foreach ($s in @('sourceBoundary','effectiveBoundary','targetBoundary')) { $p.fallbackTailYield.$s.checkpointRequestId = 'foreign' } },
    { param($e,$p,$y) foreach ($s in @('sourceBoundary','effectiveBoundary','targetBoundary')) { $p.fallbackTailYield.$s.dataSessionIdentity = 'foreign' } }
)) {
    $bad = Copy-Value $stale[-1]
    & $mutation $bad (Subject $bad 'peer_proof_pending_initial') (Subject $bad 'yield_current')
    if (Test-FileTransferCoordinatorSnapshotFallbackTailFailedEnvelope $bad) {
        throw "Malformed stale retirement admitted: $mutation"
    }
}

# Preserve the existing no-peer shape independently of pending-proof retirement.
$local = Copy-Value $stale[-1]
$local.aggregate.operations = @(Subject $local 'yield_current')
if (-not (Test-FileTransferCoordinatorSnapshotFallbackTailFailedEnvelope $local)) {
    throw 'Unpaired Failed yield without a pending proof was rejected.'
}

function New-Lines([object[]]$values) {
    $first = $values[0]
    $prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
    $families = $catalog.RequiredFamilies -join ','
    $coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=$($first.aggregate.beforeStateGeneration); initial_state_hash=$($first.aggregate.beforeStateFingerprint); $coverage; authority_effect=none; log_writer_id=fixture-writer; log_writer_sequence=1")
    for ($i = 0; $i -lt $values.Count; $i++) {
        $e = $values[$i]; $sequence = 10L * ($i + 1L)
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($e | ConvertTo-Json -Depth 64 -Compress))
        $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
        $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
        $lines.Add("event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=$($e.transitionId); before_state_generation=$($e.aggregate.beforeStateGeneration); after_state_generation=$($e.aggregate.afterStateGeneration); snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none; log_writer_id=fixture-writer; log_writer_sequence=$sequence")
        $op = $null
        if ($i -eq 0) { $op = Subject $e 'peer_proof_pending_initial' }
        if ($null -ne $op) {
            $s = $op.fallbackTailYield.sourceBoundary
            $lines.Add("event=filetransfer_coordinator_authority_effect; $prefix; transition_id=$($e.transitionId); coordinator_state_generation=$($e.aggregate.afterStateGeneration); operation_family=$($op.family); operation_key=$($op.operationKey); operation_authority_id=$($op.authorityId); operation_generation=$($op.operationGeneration); effect_kind=peer_proof_observed; effect_identity=peer-$sequence; effect_generation=$($op.receiptGeneration); receipt_identity=$($op.receiptIdentity); receipt_generation=$($op.receiptGeneration); receipt_status=$($op.receiptStatus); receipt_fingerprint=$($op.receiptFingerprint); route=$($s.route); protocol_version=$($s.protocolVersion); live_route_epoch=$($s.liveRouteEpoch); route_publication_generation=$($s.routePublicationGeneration); route_commit_generation=$($s.routeCommitGeneration); runtime_generation=$($s.runtimeGeneration); terminal_generation=$($s.terminalGeneration); leg_generation=$($s.legGeneration); transport_epoch=$($s.transportEpoch); bridge_recovery_generation=$($s.bridgeRecoveryGeneration); checkpoint_generation=$($s.checkpointGeneration); checkpoint_request_id=$($s.checkpointRequestId); data_session_identity=$($s.dataSessionIdentity); data_session_generation=$($s.dataSessionGeneration); authority_effect=applied; log_writer_id=fixture-writer; log_writer_sequence=$($sequence + 1)")
        }
    }
    $last = $values[-1]
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=$($last.aggregate.afterStateGeneration); final_state_hash=$($last.aggregate.afterStateFingerprint); transition_count=$($values.Count); $coverage; authority_effect=none; log_writer_id=fixture-writer; log_writer_sequence=99")
    return $lines.ToArray()
}
function Assert-Evaluation([string[]]$lines, [bool]$valid) {
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated') + $lines)
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
    $actualValid = $full.ArtifactEvidenceVerdict -ceq 'pass' -and $null -eq $full.FirstUnresolvedBoundary
    if ($actualValid -ne $valid -or
        (Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass') {
        throw "Unexpected history result: valid=$valid; reason=$($full.ArtifactEvidenceFailureReason); boundary=$($full.FirstUnresolvedBoundary | ConvertTo-Json -Depth 8 -Compress)"
    }
}
Assert-Evaluation (New-Lines @($matched[0], $matched[1], $matched[2])) $true
# The fixture starts mid-transfer and does not include route-publication or
# checkpoint-repair effect history. Exercise the retirement reducer directly;
# the retained live run supplies the complete cross-family integration gate.
function Assert-RetirementHistory([object[]]$values, [bool]$valid, [string]$mutation = '') {
    $entries = @(for ($i = 0; $i -lt $values.Count; $i++) {
        $e = $values[$i]
        [pscustomobject]@{
            SessionId=$e.sessionId; TransferId=$e.transferId; Direction=$e.direction
            BeforeBoundary=$e.aggregate.beforeBoundary; AfterBoundary=$e.aggregate.afterBoundary
            Boundary=[pscustomobject]@{
                boundaryId=$e.transitionId; coordinatorInstanceId=$e.coordinatorInstanceId
                writer='fixture-writer'; writerSequence=(10L * ($i + 1L)); evidenceLineIndex=($i + 1)
                sessionId=$e.sessionId; transferId=$e.transferId; direction=$e.direction
                decisionKind=$e.decisionKind; decisionStatus=$e.decisionStatus; decisionReason=$e.decisionReason
                beforeStateGeneration=$e.aggregate.beforeStateGeneration; afterStateGeneration=$e.aggregate.afterStateGeneration
                beforeStateHash=$e.aggregate.beforeStateFingerprint; afterStateHash=$e.aggregate.afterStateFingerprint
                operations=$e.aggregate.operations; families=$e.aggregate.changedFamilies
            }
        }
    })
    $origin = $entries[0].Boundary
    $op = Subject $values[0] 'peer_proof_pending_initial'
    $effect = @{
        LineIndex=1; Writer='fixture-writer'; WriterSequence=11
        CoordinatorInstanceId=$origin.coordinatorInstanceId; TransitionId=$origin.boundaryId
        StateGeneration=$origin.afterStateGeneration; Family=$op.family; OperationKey=$op.operationKey
        AuthorityId=$op.authorityId; OperationGeneration=$op.operationGeneration
        EffectKind='peer_proof_observed'; EffectIdentity='fixture-origin'
        ReceiptIdentity=$op.receiptIdentity; ReceiptGeneration=$op.receiptGeneration
        ReceiptStatus=$op.receiptStatus; ReceiptFingerprint=$op.receiptFingerprint
    }
    foreach ($field in $op.fallbackTailYield.sourceBoundary.PSObject.Properties) {
        $effect[$field.Name] = $field.Value
    }
    $effects = @([pscustomobject]$effect)
    if ($mutation -ceq 'missing-effect') { $effects = @() }
    if ($mutation -ceq 'foreign-writer') { $effects[0].Writer = 'foreign' }
    if ($mutation -ceq 'copied-effect') {
        $copied = Copy-Value $effects[0]
        $copied.TransitionId = $entries[-1].Boundary.boundaryId
        $effects += $copied
    }
    if ($mutation -ceq 'missing-origin') { $entries = $entries[1..($entries.Count - 1)] }
    $bindings = @(Get-FileTransferCoordinatorSnapshotPendingPeerProofBindings -AggregateEntries $entries -Effects $effects)
    $index = New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries $entries
    $errors = @(Resolve-FileTransferCoordinatorFailedCheckpointYieldCandidates -AggregateIndex $index -Candidates @{} -InvalidBoundaries @{} -Effects $effects -AllBoundaries @($entries.Boundary) -PendingPeerProofBindings $bindings)
    if (($errors.Count -eq 0) -ne $valid) { throw "Unexpected retirement history: valid=$valid; mutation=$mutation; errors=$errors" }
}
Assert-RetirementHistory $stale $true
foreach ($mutation in @('missing-effect', 'foreign-writer', 'copied-effect', 'missing-origin')) {
    Assert-RetirementHistory $stale $false $mutation
}
$forged = @($stale | ForEach-Object { Copy-Value $_ })
(Subject $forged[-1] 'peer_proof_pending_initial').fallbackTailYield.peerProofFingerprint = 'forged-history'
Assert-RetirementHistory $forged $false
Write-Output 'Checkpoint-stale Failed yield: production shapes, strict negative controls, retirement history, and existing matched-proof parity passed.'
