param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot, [string]$EmitterProfile, [string]$EmitterHash)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEarlyCheckpointProofCatalog
if ($catalog.Version -cne $EmitterProfile -or $catalog.Hash -cne $EmitterHash -or
    (Get-FileTransferCoordinatorEvidenceCatalog).Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2' -or
    (Get-FileTransferCoordinatorInitialDataSessionCatalog).Hash -cne '90cf0fdafc50d49c866bbb9bdda9097b3306ae5a7fd9d1198b03c02c66ef18ab') {
    throw 'Emitter/catalog mismatch or frozen parent changed.'
}
$script:FileTransferCoordinatorEarlyCheckpointProofCatalog.copiedObservationAllowed = $true
$rejected = $false
try { $null = Get-FileTransferCoordinatorEarlyCheckpointProofCatalog }
catch { $rejected = $_.Exception.Message -like 'early_checkpoint_proof_catalog_hash_invalid:*' }
finally { $script:FileTransferCoordinatorEarlyCheckpointProofCatalog.copiedObservationAllowed = $false }
if (-not $rejected) { throw 'Modified extension bypassed its pinned hash.' }
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$null,[ref]$null)
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification','Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definitions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name },$true))
    if ($definitions.Count -ne 1) { throw "GUI classifier function not unique: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
function Copy-Value($value) { ConvertTo-Json -InputObject $value -Depth 64 -Compress | ConvertFrom-Json }
function Subject($envelope, [string]$role) {
    $values = @($envelope.aggregate.operations | Where-Object { $_.operationRole -ceq $role })
    if ($values.Count -ne 1) { throw "Subject not unique: $role" }
    return $values[0]
}
function Receipt($operation, [string]$stage) {
    $values = @($operation.fallbackTailYield.receipts | Where-Object { $_.stage -ceq $stage })
    if ($values.Count -ne 1) { throw "Receipt not unique: $stage" }
    return $values[0]
}
$first = $envelopes[0]
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$families = $catalog.RequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
function New-Effect($e, $op, $receipt, [string]$kind, [int]$sequence) {
    $s = $op.fallbackTailYield.sourceBoundary
    "event=filetransfer_coordinator_authority_effect; $prefix; transition_id=$($e.transitionId); coordinator_state_generation=$($e.aggregate.afterStateGeneration); operation_family=$($op.family); operation_key=$($op.operationKey); operation_authority_id=$($op.authorityId); operation_generation=$($op.operationGeneration); effect_kind=$kind; effect_identity=$kind-$sequence; effect_generation=$($receipt.receiptGeneration); receipt_identity=$($receipt.receiptIdentity); receipt_generation=$($receipt.receiptGeneration); receipt_status=$($receipt.receiptStatus); receipt_fingerprint=$($receipt.receiptFingerprint); route=$($s.route); protocol_version=$($s.protocolVersion); live_route_epoch=$($s.liveRouteEpoch); route_publication_generation=$($s.routePublicationGeneration); route_commit_generation=$($s.routeCommitGeneration); runtime_generation=$($s.runtimeGeneration); terminal_generation=$($s.terminalGeneration); leg_generation=$($s.legGeneration); transport_epoch=$($s.transportEpoch); bridge_recovery_generation=$($s.bridgeRecoveryGeneration); checkpoint_generation=$($s.checkpointGeneration); checkpoint_request_id=$($s.checkpointRequestId); data_session_identity=$($s.dataSessionIdentity); data_session_generation=$($s.dataSessionGeneration); authority_effect=applied; log_writer_id=fixture-writer; log_writer_sequence=$sequence"
}
function New-Lines([object[]]$values, [string]$effectCase = '') {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=$($values[0].aggregate.beforeStateGeneration); initial_state_hash=$($values[0].aggregate.beforeStateFingerprint); $coverage; authority_effect=none")
    for ($i = 0; $i -lt $values.Count; $i++) {
        $e = $values[$i]; $sequence = 10 * ($i + 1)
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($e | ConvertTo-Json -Depth 64 -Compress))
        $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
        $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
        $lines.Add("event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=$($e.transitionId); before_state_generation=$($e.aggregate.beforeStateGeneration); after_state_generation=$($e.aggregate.afterStateGeneration); snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none; log_writer_id=fixture-writer; log_writer_sequence=$sequence")
        if ($e.decisionKind -ceq 'ApplyFallbackTailPeerProofReceipt' -and $effectCase -cne 'missing-original' -and
            ($e.decisionStatus -ceq 'Accepted' -or ($effectCase -ceq 'copied-duplicate' -and $e.decisionStatus -ceq 'Idempotent'))) {
            # Idempotent snapshots contain only the attempted decision, not unchanged custody.
            $source = if ($e.decisionStatus -ceq 'Idempotent') { $first } else { $e }
            $op = @($source.aggregate.operations | Where-Object operationRole -ceq 'peer_proof_awaiting_checkpoint')[0]
            $lines.Add((New-Effect $e $op (Receipt $op 'peer_proof') 'peer_proof_observed' ($sequence + 1)))
        }
        if ($e.decisionKind -ceq 'FallbackCheckpointAccepted' -and $e.decisionStatus -ceq 'Accepted' -and $effectCase -ceq 'copied-promotion') {
            $op = Subject $e 'peer_proof_pending_initial'
            $lines.Add((New-Effect $e $op (Receipt $op 'peer_proof') 'peer_proof_observed' ($sequence + 1)))
        }
        if ($e.decisionKind -ceq 'FallbackTailQuiescent') {
            $op = Subject $e 'yield_current'
            $lines.Add((New-Effect $e $op (Receipt $op 'yield') 'recovery_owner_retired_after_yield' ($sequence + 1)))
        }
        if ($e.decisionKind -ceq 'ApplyFallbackTailLocalProofReceipt') {
            $op = Subject $e 'yield_current'
            $lines.Add((New-Effect $e $op (Receipt $op 'local_proof') 'local_proof_sent' ($sequence + 1)))
            $lines.Add((New-Effect $e $op (Receipt $op 'local_proof') 'local_proof_applied' ($sequence + 2)))
            $lines.Add((New-Effect $e $op (Receipt $op 'paired_release') 'paired_release_published' ($sequence + 3)))
        }
    }
    $last = $values[-1]
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=$($last.aggregate.afterStateGeneration); final_state_hash=$($last.aggregate.afterStateFingerprint); transition_count=$($values.Count); $coverage; authority_effect=none")
    $lines.ToArray()
}
function Assert-Parity([string[]]$lines, [bool]$valid, [string]$name) {
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated') + $lines)
    $records = @(for ($i = 0; $i -lt $lines.Count; $i++) { [pscustomobject]@{ Line=$lines[$i]; LineIndex=$i } })
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
    $path = Join-Path $OutputRoot "$name.log"
    [IO.File]::WriteAllLines($path, $lines)
    [IO.File]::WriteAllText((Join-Path $OutputRoot "$name.json"), ($full | ConvertTo-Json -Depth 64))
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        (($full.ArtifactEvidenceVerdict -ceq 'pass' -and $null -eq $full.FirstUnresolvedBoundary) -ne $valid)) {
        throw "$name expected valid=$valid; reason=$($full.ArtifactEvidenceFailureReason); boundary=$($full.FirstUnresolvedBoundary | ConvertTo-Json -Depth 12 -Compress)"
    }
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
    if ($valid) {
        # This focused stream omits the earlier route-publication history.
        if ($null -ne $gui -and $gui.FailureClass -cne 'filetransfer_route_consistency_analysis_parity_invalid') { throw "GUI rejected valid custody: $name" }
    }
    elseif ($gui.CoordinatorBoundaryHash -cne $full.FirstUnresolvedBoundary.boundaryHash -or
        $gui.CoordinatorBoundaryId -cne $full.FirstUnresolvedBoundary.boundaryId -or
        $gui.CoordinatorWriter -cne $full.FirstUnresolvedBoundary.writer -or
        $gui.CoordinatorWriterSequence -ne $full.FirstUnresolvedBoundary.writerSequence) { throw "GUI boundary diverged: $name" }
}
Assert-Parity (New-Lines $envelopes) $true 'positive'
$parent = Get-FileTransferCoordinatorInitialDataSessionCatalog
$oldProfile = @(New-Lines $envelopes | ForEach-Object { $_.Replace($catalog.Version, $parent.Version).Replace($catalog.Hash, $parent.Hash) })
Assert-Parity $oldProfile $false 'old-profile'
foreach ($case in @('retired', 'successor', 'yield')) {
    $discard = Get-Content -LiteralPath (Join-Path $OutputRoot "$case.json") -Raw | ConvertFrom-Json
    Assert-Parity (New-Lines $discard) $true "discard-$case"
    Assert-Parity (New-Lines (@($discard) + @($envelopes[4..6]))) $false "resurrect-after-$case"
}
foreach ($case in @('missing-original', 'copied-promotion', 'copied-duplicate')) {
    Assert-Parity (New-Lines $envelopes $case) $false $case
}
foreach ($case in @('missing-writer', 'foreign-effect-writer', 'earlier-effect')) {
    $bad = @(New-Lines $envelopes | ForEach-Object {
        if ($case -ceq 'missing-writer') { $_.Replace('log_writer_id=fixture-writer', 'log_writer_id=(none)') }
        elseif ($_ -like '*effect_kind=peer_proof_observed;*') {
            if ($case -ceq 'foreign-effect-writer') { $_.Replace('log_writer_id=fixture-writer', 'log_writer_id=foreign') }
            else { $_.Replace('log_writer_sequence=11', 'log_writer_sequence=9') }
        }
        else { $_ }
    })
    Assert-Parity $bad $false $case
}
$mutations = [ordered]@{
    wrongStatus = { param($e) $e[4].decisionStatus = 'Rejected' }
    wrongDecision = { param($e) $e[4].decisionKind = 'FallbackCheckpointRetired' }
    wrongCheckpoint = { param($e) $e[4].aggregate.beforeBoundary.checkpointRequestId = 'wrong' }
    changedGeneration = { param($e) (Subject $e[4] 'peer_proof_pending_initial').fallbackTailYield.sourceBoundary.checkpointGeneration++ }
    changedTransport = { param($e) (Subject $e[4] 'peer_proof_pending_initial').fallbackTailYield.sourceBoundary.transportEpoch++ }
    changedProof = { param($e) (Subject $e[4] 'peer_proof_pending_initial').commandFingerprint = 'wrong' }
    changedIdentity = { param($e) (Subject $e[4] 'peer_proof_pending_initial').receiptIdentity = 'wrong' }
    changedOwner = { param($e) (Subject $e[4] 'peer_proof_pending_initial').fallbackTailYield.ownerKey = 'wrong' }
    inventedRelease = { param($e) (Subject $e[0] 'peer_proof_awaiting_checkpoint').fallbackTailYield.pairedReleaseGeneration = 1 }
    duplicateCustody = { param($e) $e[0].aggregate.operations += Copy-Value (Subject $e[0] 'peer_proof_awaiting_checkpoint') }
    duplicatePromotion = { param($e) $e[4].aggregate.operations += Copy-Value (Subject $e[4] 'peer_proof_pending_initial') }
    wrongWriter = { param($e) $e[4].coordinatorInstanceId = 'foreign' }
}
foreach ($case in $mutations.Keys) {
    $bad = @($envelopes | ForEach-Object { Copy-Value $_ })
    & $mutations[$case] $bad
    Assert-Parity (New-Lines $bad) $false $case
}
foreach ($index in @(0,4)) {
    $missing = @($envelopes | Where-Object { $_.transitionId -cne $envelopes[$index].transitionId })
    Assert-Parity (New-Lines $missing) $false "missing-$index"
}
$reordered = @($envelopes | ForEach-Object { Copy-Value $_ })
foreach ($e in $reordered) { $e.aggregate.operations = @($e.aggregate.operations | Sort-Object operationKey -Descending) }
Assert-Parity (New-Lines $reordered) $true 'reordered-operations'
Write-Output 'Early checkpoint custody and promotion full/compact/GUI classifier parity passed.'
