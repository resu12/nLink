param([string]$RepoRoot, [string]$InputPath)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
. (Join-Path $RepoRoot 'tools/FileTransferSoak/LogParsing.ps1')
$lines = [IO.File]::ReadAllLines($InputPath)
function Name([string]$Line) { Get-FileTransferCoordinatorSnapshotEventName $Line }
function Field([string]$Line, [string]$Name) { Get-FileTransferCoordinatorSnapshotLineField $Line $Name }
$profiles = @($lines | Where-Object { (Name $_) -ceq 'filetransfer_coordinator_snapshot_stream_started' } |
    ForEach-Object { Field $_ 'snapshot_catalog_version' } | Select-Object -Unique)
if ($profiles.Count -ne 1) { throw 'Expected one original producer profile.' }
$catalog = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $profiles[0]
function Evaluate([string[]]$InputLines, [bool]$Valid) {
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $InputLines
    $compactLines = @($InputLines | Where-Object {
        $name = Name $_
        $catalog.SnapshotAuthorityV1 -contains $name -or $catalog.GenerationStampedEffects -contains $name
    })
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        ($full.ArtifactEvidenceVerdict -ceq 'pass') -ne $Valid -or
        ($Valid -and (-not $full.StreamComplete -or $full.LegacySelectionCount -ne 0 -or $null -ne $full.FirstUnresolvedBoundary))) {
        throw "Snapshot validation/parity failed: $($full.ArtifactEvidenceFailureReason)"
    }
    return $full
}
$evaluation = Evaluate $lines $true
$origins = @($evaluation.__CheckpointRepairContinuityEvidence.OriginsByKey.Values)
$oldOrigins = @($origins | Where-Object {
    $null -ne $_.RetiredBoundary -and $_.RetiredBoundary.decisionKind -ceq 'StartTransfer'
})
if ($oldOrigins.Count -ne 1) { throw 'Expected one exact old checkpoint retired by republication.' }
$old = $oldOrigins[0]
$retirement = $old.RetiredBoundary
$newOrigins = @($origins | Where-Object {
    $_.Boundary.coordinatorInstanceId -ceq $retirement.coordinatorInstanceId -and
    $_.Boundary.beforeStateGeneration -ge $retirement.afterStateGeneration -and
    $_.Operation.checkpointRepairContinuity.sourceScope.boundary.runtimeGeneration -eq $retirement.afterBoundary.runtimeGeneration -and
    $_.Operation.checkpointRepairContinuity.sourceScope.boundary.routePublicationGeneration -eq $retirement.afterBoundary.routePublicationGeneration
})
if ($newOrigins.Count -ne 1) { throw 'Expected one successor checkpoint under the exact new owner.' }
$new = $newOrigins[0]
if ($old.Operation.receiptIdentity -ceq $new.Operation.receiptIdentity -or
    $old.Operation.checkpointRepairContinuity.acceptedCheckpoint.checkpointRequestId -ceq
        $new.Operation.checkpointRepairContinuity.acceptedCheckpoint.checkpointRequestId -or
    $old.Operation.checkpointRepairContinuity.clearedScope.boundary.dataSessionIdentity -cne
        $new.Operation.checkpointRepairContinuity.clearedScope.boundary.dataSessionIdentity -or
    $old.Operation.checkpointRepairContinuity.clearedScope.boundary.dataSessionGeneration -ne
        $new.Operation.checkpointRepairContinuity.clearedScope.boundary.dataSessionGeneration) {
    throw 'Old/new checkpoint identity or data ownership differs from the fixture contract.'
}

# Resolve each original diagnostic through the already validated snapshot. This
# is issuance evidence, not a claim that a settled repair is still current.
$grants = [Collections.Generic.List[object]]::new()
$settlements = [Collections.Generic.List[object]]::new()
for ($i = 0; $i -lt $lines.Count; $i++) {
    $name = Name $lines[$i]
    if ($name -cnotin @('filetransfer_fallback_frontier_repair_authority_granted',
            'filetransfer_fallback_frontier_repair_contract_settled')) { continue }
    $event = ConvertFrom-FileTransferLogLine -Line $lines[$i] -FilePath $InputPath -LineNumber ($i + 1) -Sequence ($i + 1)
    $binding = Resolve-FileTransferRepairOwnerEvidence -SnapshotEvidence $evaluation -Event $event
    if ($null -eq $binding) { throw "Unbound original repair event at line $($i + 1)." }
    if ($binding.Boundary.coordinatorInstanceId -cne $retirement.coordinatorInstanceId) { continue }
    $entry = [pscustomobject]@{ Event=$event; Binding=$binding }
    if ($name -ceq 'filetransfer_fallback_frontier_repair_authority_granted') { $grants.Add($entry) }
    else { $settlements.Add($entry) }
}
if ($grants.Count -ne 3 -or $settlements.Count -ne 3) { throw 'Expected exactly three source repair attempts.' }
$ordered = @($grants | Sort-Object { $_.Binding.Operation.operationGeneration })
for ($i = 0; $i -lt $ordered.Count; $i++) {
    $grant = $ordered[$i].Binding
    $settled = @($settlements | Where-Object { $_.Binding.Operation.operationKey -ceq $grant.Operation.operationKey })
    if ($settled.Count -ne 1 -or
        $grant.Operation.repairOwner.scope.boundary.runtimeGeneration -ne $retirement.beforeBoundary.runtimeGeneration -or
        $grant.Operation.repairOwner.scope.boundary.routePublicationGeneration -ne $retirement.beforeBoundary.routePublicationGeneration -or
        $grant.Boundary.afterStateGeneration -gt $retirement.beforeStateGeneration) { throw 'Repair source scope/settlement is not exact.' }
    $expected = if ($i -eq 2) { 'StaleRejected' } else { 'SettledStalled' }
    if ($settled[0].Event.Fields.settlement_outcome -cne $expected) { throw 'Historical repair outcome changed.' }
    if ($i -gt 0 -and $grant.Operation.predecessorAuthorityId -cne $ordered[$i - 1].Binding.Operation.authorityId) {
        throw 'Broken repair predecessor lineage.'
    }
    if ($i -eq 2 -and $settled[0].Event.Fields.decision_reason -cne 'runtime_owner_retired_state_refresh_received') {
        throw 'Current repair was not retired by the observed replacement.'
    }
    foreach ($field in @('repair_owner_receipt_id', 'runtime_generation', 'predecessor_authority_id')) {
        $fields = @{}
        foreach ($key in $ordered[$i].Event.Fields.Keys) { $fields[$key] = $ordered[$i].Event.Fields[$key] }
        $fields[$field] = if ($field -ceq 'runtime_generation') { [string](1 + [long]$fields[$field]) } else { 'foreign-receipt-or-predecessor' }
        if ($null -ne (Resolve-FileTransferRepairOwnerEvidence -SnapshotEvidence $evaluation -Event ([pscustomobject]@{ Fields=$fields }))) {
            throw "Foreign repair linkage passed: $field"
        }
    }
}
Write-Output 'pass repair_owner_substitutions'

# Mutate only copies of original checkpoint payloads. Recompute the wire hash so
# validation must inspect receipt/ownership semantics, not just checksum damage.
function Replace-Payload([string]$Line, $Envelope) {
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Envelope -Depth 64 -Compress))
    $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
    $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
    [regex]::Replace([regex]::Replace($Line, '(?<=snapshot_sha256=)[^;]*', $hash), '(?<=snapshot_payload=)[^;]*', $payload)
}
foreach ($case in @('missing_origin', 'missing_retirement', 'foreign_receipt', 'duplicate_retirement')) {
    $copy = [string[]]$lines.Clone()
    $subject = if ($case -ceq 'missing_origin') { $old.Boundary } else { $retirement }
    $indices = @(for ($i = 0; $i -lt $copy.Count; $i++) {
        if ((Name $copy[$i]) -ceq 'filetransfer_coordinator_transition_snapshot' -and
            (Field $copy[$i] 'transition_id') -ceq $subject.boundaryId) { $i }
    })
    if ($indices.Count -ne 1) { throw 'Mutation source boundary is ambiguous.' }
    $index = $indices[0]
    $envelope = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Field $copy[$index] 'snapshot_payload')).Value
    $operations = @($envelope.aggregate.operations | Where-Object { $_.operationKey -ceq $old.Operation.operationKey })
    if ($operations.Count -ne 1) { throw 'Mutation source operation is ambiguous.' }
    $operation = $operations[0]
    switch ($case) {
        'missing_origin' { $envelope.aggregate.operations = @($envelope.aggregate.operations | Where-Object { $_ -ne $operation }) }
        'missing_retirement' { $envelope.aggregate.operations = @($envelope.aggregate.operations | Where-Object { $_ -ne $operation }) }
        'foreign_receipt' {
            $foreign = 'fte1-' + ('a' * 24)
            $operation.receiptIdentity = $foreign
            $operation.checkpointRepairContinuity.originReceiptIdentity = $foreign
            $operation.checkpointRepairContinuity.receipts[0].receiptIdentity = $foreign
        }
        'duplicate_retirement' { $envelope.aggregate.operations += $operation }
    }
    $copy[$index] = Replace-Payload $copy[$index] $envelope
    $null = Evaluate $copy $false
    Write-Output "pass $case"
}
[pscustomobject]@{
    Marker='fallback-republication-checkpoint-evidence-ok'; BoundaryCount=$evaluation.BoundaryCount
    BoundaryHash=$evaluation.BoundaryHash; OldAcceptanceBoundary=$old.Boundary.boundaryId
    RetirementBoundary=$retirement.boundaryId; NewAcceptanceBoundary=$new.Boundary.boundaryId
    RepairAttempts=$ordered.Count; Stalled=2; Retired=1
    Scope='local receipt linkage only; no cross-peer supersession eligibility'
} | ConvertTo-Json -Compress
