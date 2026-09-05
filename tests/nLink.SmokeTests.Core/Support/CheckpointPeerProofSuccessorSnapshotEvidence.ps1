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

$successors = @($envelopes | Where-Object {
        $_.decisionKind -ceq 'ApplyFallbackTailPeerProofReceipt' -and
        @($_.aggregate.operations | Where-Object {
                $_.operationRole -ceq 'peer_proof_pending_initial'
            }).Count -eq 2
    })
if ($successors.Count -ne 1) { throw 'Checkpoint successor envelope is not unique.' }
$successor = $successors[0]
$consumptions = @($envelopes | Where-Object {
        $_.decisionKind -ceq 'FallbackTailQuiescent' -and
        @($_.aggregate.operations | Where-Object {
                $_.operationRole -ceq 'peer_proof_pending_initial' -and
                $_.beforeState -ceq 'Pending' -and
                $_.afterState -ceq 'None'
            }).Count -eq 1
    })
if ($consumptions.Count -ne 1) { throw 'Checkpoint successor consumption is not unique.' }
$consumption = $consumptions[0]
$pairings = @($envelopes | Where-Object {
        $_.decisionKind -ceq 'ApplyFallbackTailLocalProofReceipt' -and
        @($_.aggregate.operations | Where-Object {
                $_.operationRole -ceq 'yield_current' -and
                $_.beforeState -ceq 'PeerProofObserved' -and
                $_.afterState -ceq 'Paired'
            }).Count -eq 1
    })
if ($pairings.Count -ne 1) { throw 'Checkpoint successor pairing is not unique.' }
if (-not (Test-FileTransferCoordinatorSnapshotCheckpointPeerProofSuccessor $successor)) {
    throw 'Production checkpoint successor envelope was rejected.'
}
if (-not (Test-FileTransferCoordinatorSnapshotFallbackTailQuiescentEnvelope $consumption)) {
    throw 'Production checkpoint successor consumption was rejected.'
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
function Get-Receipt($operation, [string]$stage) {
    $receipts = @($operation.fallbackTailYield.receipts | Where-Object {
            $_.stage -ceq $stage
        })
    if ($receipts.Count -ne 1) { throw "Receipt is not unique: $stage" }
    $receipts[0]
}
function New-Effect($envelope, $operation, $receipt, [string]$kind, [int]$sequence) {
    $scope = $operation.fallbackTailYield.sourceBoundary
    "event=filetransfer_coordinator_authority_effect; $prefix; transition_id=$($envelope.transitionId); coordinator_state_generation=$($envelope.aggregate.afterStateGeneration); operation_family=$($operation.family); operation_key=$($operation.operationKey); operation_authority_id=$($operation.authorityId); operation_generation=$($operation.operationGeneration); effect_kind=$kind; effect_identity=$kind-$sequence; effect_generation=$($receipt.receiptGeneration); receipt_identity=$($receipt.receiptIdentity); receipt_generation=$($receipt.receiptGeneration); receipt_status=$($receipt.receiptStatus); receipt_fingerprint=$($receipt.receiptFingerprint); route=$($scope.route); protocol_version=$($scope.protocolVersion); live_route_epoch=$($scope.liveRouteEpoch); route_publication_generation=$($scope.routePublicationGeneration); route_commit_generation=$($scope.routeCommitGeneration); runtime_generation=$($scope.runtimeGeneration); terminal_generation=$($scope.terminalGeneration); leg_generation=$($scope.legGeneration); transport_epoch=$($scope.transportEpoch); bridge_recovery_generation=$($scope.bridgeRecoveryGeneration); checkpoint_generation=$($scope.checkpointGeneration); checkpoint_request_id=$($scope.checkpointRequestId); data_session_identity=$($scope.dataSessionIdentity); data_session_generation=$($scope.dataSessionGeneration); authority_effect=applied; log_writer_id=fixture-writer; log_writer_sequence=$sequence"
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
            $receipt = Get-Receipt $operation 'peer_proof'
            if (($i -eq 0 -and $effectCase -cne 'missing-initial') -or
                ($i -gt 0 -and $effectCase -cne 'missing-successor')) {
                $lines.Add((New-Effect $envelope $operation $receipt 'peer_proof_observed' ($sequence + 1)))
            }
            if ($i -gt 0 -and $effectCase -ceq 'copied-retired') {
                $retired = Get-PendingProof $envelope 'None'
                $lines.Add((New-Effect $envelope $retired (Get-Receipt $retired 'peer_proof') 'peer_proof_observed' ($sequence + 2)))
            }
        }
        if ($envelope.decisionKind -ceq 'FallbackTailQuiescent') {
            $yield = @($envelope.aggregate.operations | Where-Object {
                    $_.operationRole -ceq 'yield_current'
            })
            if ($yield.Count -ne 1) { throw 'Consumed successor yield is not unique.' }
            $receipt = Get-Receipt $yield[0] 'yield'
            $lines.Add((New-Effect $envelope $yield[0] $receipt 'recovery_owner_retired_after_yield' ($sequence + 1)))
        }
        if ($envelope.decisionKind -ceq 'ApplyFallbackTailLocalProofReceipt') {
            $yield = @($envelope.aggregate.operations | Where-Object {
                    $_.operationRole -ceq 'yield_current'
                })
            if ($yield.Count -ne 1) { throw 'Paired successor yield is not unique.' }
            $localReceipt = Get-Receipt $yield[0] 'local_proof'
            $releaseReceipt = Get-Receipt $yield[0] 'paired_release'
            $lines.Add((New-Effect $envelope $yield[0] $localReceipt 'local_proof_sent' ($sequence + 1)))
            $lines.Add((New-Effect $envelope $yield[0] $localReceipt 'local_proof_applied' ($sequence + 2)))
            $lines.Add((New-Effect $envelope $yield[0] $releaseReceipt 'paired_release_published' ($sequence + 3)))
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
    $fullValid = $full.ArtifactEvidenceVerdict -ceq 'pass' -and
        $null -eq $full.FirstUnresolvedBoundary
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        $fullValid -ne $valid) {
        throw "Unexpected shared result: valid=$valid; reason=$($full.ArtifactEvidenceFailureReason); boundary=$($full.FirstUnresolvedBoundary | ConvertTo-Json -Depth 10 -Compress)"
    }
    $path = Join-Path $OutputRoot 'compact.log'
    [IO.File]::WriteAllLines($path, $compactLines)
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
    if ($valid) {
        # This focused stream begins on an already established fallback route,
        # so the top-level GUI may separately report the intentionally omitted
        # route-publication history. It must not reject the Family-4 evidence.
        if ($null -ne $gui -and
            [string]$gui.FailureClass -cne
                'filetransfer_route_consistency_analysis_parity_invalid') {
            throw "GUI rejected valid checkpoint successor: $($gui | ConvertTo-Json -Depth 10 -Compress)"
        }
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
$reorderedSuccessor = @($reordered | Where-Object {
        $_.transitionId -ceq $successor.transitionId
    })
if ($reorderedSuccessor.Count -ne 1) { throw 'Reordered successor is not unique.' }
$reorderedSuccessor[0].aggregate.operations = @(
    $reorderedSuccessor[0].aggregate.operations | Sort-Object operationKey -Descending)
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

# A later recreation of the consumed successor key is ABA reuse, not a valid
# continuation of the original pending proof.
$aba = @($envelopes | ForEach-Object { Copy-Value $_ })
$recreated = Copy-Value $successor
$recreated.transitionId += '-aba'
$aba += $recreated
for ($i = 0; $i -lt $aba.Count; $i++) {
    $aba[$i].aggregate.beforeStateGeneration = $i
    $aba[$i].aggregate.afterStateGeneration = $i + 1
    $aba[$i].aggregate.beforeStateFingerprint = "aba-state-$i"
    $aba[$i].aggregate.afterStateFingerprint = "aba-state-$($i + 1)"
}
Assert-Parity (New-Lines $aba) $false
Write-Output 'Checkpoint peer-proof successor: exact rollover, history/effects, and full/compact/GUI parity passed.'
