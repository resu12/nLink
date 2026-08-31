param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') {
    throw 'Frozen Snapshot V1 catalog changed.'
}
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
# Isolate the production-generated Family-2 graph; other families retain their own lanes.
foreach ($envelope in $envelopes) {
    $envelope.snapshotSelectionMode = 'SnapshotV1Shadow'
    $envelope.aggregate.operations = @($envelope.aggregate.operations | Where-Object {
        $_.family -ceq 'runtime_unlock_retry_lifecycle'
    })
    $envelope.aggregate.changedFamilies = @('runtime_unlock_retry_lifecycle')
}
function Copy-Value($value) { $value | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json }
function Install-Receipt($operation) {
    $receipts = @($operation.runtimeUnlockRetry.receipts | Where-Object { $_.stage -ceq 'install' })
    if ($receipts.Count -ne 1) { throw 'Fixture install receipt not unique.' }
    $receipts[0]
}
$issue = $envelopes[2]
$pending = @($issue.aggregate.operations | Where-Object { $_.operationRole -ceq 'attempt_pending' })[0]
$commit = $envelopes[4]
$retired = @($commit.aggregate.operations | Where-Object { $_.operationRole -ceq 'attempt_pending' })[0]
$installed = @($commit.aggregate.operations | Where-Object { $_.operationRole -ceq 'attempt_installed' })[0]
foreach ($envelope in $envelopes) {
    foreach ($operation in $envelope.aggregate.operations) {
        if (-not (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog -Envelope $envelope -Operation $operation)) {
            throw "Production catalog rejected $($envelope.decisionKind)/$($operation.operationRole): $($operation | ConvertTo-Json -Depth 12 -Compress)"
        }
    }
}

# Domain separation and historical authority scope are intentional, not aliases.
$separate = Copy-Value $issue
$subject = $separate.aggregate.operations[0]
$subject.operationGeneration = 5L
$subject.runtimeUnlockRetry.authorityGeneration = 5L
$subject.operationKey = "runtime_unlock_retry_lifecycle|attempt_pending|$($subject.authorityId)|5|RetryAttempt"
$subject.runtimeUnlockRetry.dispatchAttemptGeneration = 2L
$stage = @($subject.runtimeUnlockRetry.receipts | Where-Object { $_.stage -ceq 'stage' })[0]
$stage.commandGeneration = 2L; $stage.receiptGeneration = 2L
$install = Install-Receipt $subject
$install.commandGeneration = 7L; $install.receiptGeneration = 7L
$subject.commandGeneration = 7L; $subject.receiptGeneration = 7L
$subject.runtimeUnlockRetry.sourceBoundary.transportEpoch = 9L
$subject.runtimeUnlockRetry.sourceBoundary.bridgeRecoveryGeneration = 8L
if (-not (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog -Envelope $separate -Operation $subject)) {
    throw 'Independent authority/dispatch/install generations or retained source rejected.'
}

foreach ($mutation in @(
    { param($e,$o) $o.operationRole = 'attempt_installed' },
    { param($e,$o) $o.runtimeUnlockRetry.role = 'attempt_terminal' },
    { param($e,$o) $o.operationKey += '-wrong' },
    { param($e,$o) $o.authorityId = '(none)' },
    { param($e,$o) $o.runtimeUnlockRetry.authorityGeneration++ },
    { param($e,$o) $o.operationKind = 'ScopeInvalidation' },
    { param($e,$o) (Install-Receipt $o).commandGeneration++ },
    { param($e,$o) (Install-Receipt $o).commandFingerprint = 'wrong' },
    { param($e,$o) (Install-Receipt $o).receiptFingerprint = 'wrong' },
    { param($e,$o) (Install-Receipt $o).receiptValidated = $false },
    { param($e,$o) $o.runtimeUnlockRetry.receipts = @((Install-Receipt $o)) },
    { param($e,$o) $o.runtimeUnlockRetry.receipts += Copy-Value (Install-Receipt $o) },
    { param($e,$o) $e.aggregate.operations += Copy-Value $o },
    { param($e,$o) $o.runtimeUnlockRetry.sourceBoundary.dataSessionIdentity = '' },
    { param($e,$o) $e.decisionStatus = 'Rejected' },
    { param($e,$o) $e.aggregate.afterStateGeneration++ },
    { param($e,$o) $e.aggregate.afterStateFingerprint = $e.aggregate.beforeStateFingerprint }
)) {
    $bad = Copy-Value $issue
    $operation = $bad.aggregate.operations[0]
    & $mutation $bad $operation
    if (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog -Envelope $bad -Operation $operation) {
        throw "Malformed staged receipt passed: $mutation"
    }
}
foreach ($mutation in @(
    { param($e,$b) $e.aggregate.operations = @($e.aggregate.operations | Where-Object { $_.operationRole -cne 'attempt_installed' }) },
    { param($e,$b) $e.aggregate.operations += Copy-Value $b },
    { param($e,$b) $e.aggregate.operations += Copy-Value (@($e.aggregate.operations | Where-Object { $_.operationRole -ceq 'attempt_pending' })[0]) },
    { param($e,$b) $b.authorityId = 'unrelated' },
    { param($e,$b) $b.operationGeneration-- },
    { param($e,$b) $b.runtimeUnlockRetry.sourceBoundary.dataSessionGeneration++ },
    { param($e,$b) $b.runtimeUnlockRetry.sourceBoundary.transportEpoch++ },
    { param($e,$b) (Install-Receipt $b).receiptGeneration++ },
    { param($e,$b) (Install-Receipt $b).receiptFingerprint = 'wrong' }
)) {
    $bad = Copy-Value $commit
    $a = @($bad.aggregate.operations | Where-Object { $_.operationRole -ceq 'attempt_pending' })[0]
    $b = @($bad.aggregate.operations | Where-Object { $_.operationRole -ceq 'attempt_installed' })[0]
    & $mutation $bad $b
    if (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog -Envelope $bad -Operation $a) {
        throw "Malformed install entrant passed: $mutation"
    }
}
$rejected = Copy-Value $envelopes[3]
$rejected.decisionStatus = 'Rejected'
$rejected.aggregate.operations[0].receiptStatus = 'Rejected'
if (-not (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog -Envelope $rejected -Operation $rejected.aggregate.operations[0])) {
    throw 'Unchanged rejected decision confused with retained install receipt.'
}

$first = $envelopes[0]
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$coverage = 'emitted_family_coverage=runtime_unlock_retry_lifecycle; shadow_family_coverage=; authoritative_family_coverage=runtime_unlock_retry_lifecycle'
function New-Effect($envelope, $operation, $receipt, [string]$kind, [long]$sequence) {
    $scope = $operation.runtimeUnlockRetry.sourceBoundary
    "event=filetransfer_coordinator_authority_effect; $prefix; transition_id=$($envelope.transitionId); coordinator_state_generation=$($envelope.aggregate.afterStateGeneration); operation_family=$($operation.family); operation_key=$($operation.operationKey); operation_authority_id=$($operation.authorityId); operation_generation=$($operation.operationGeneration); effect_kind=$kind; effect_identity=$kind-$($receipt.receiptIdentity); effect_generation=$($receipt.receiptGeneration); receipt_identity=$($receipt.receiptIdentity); receipt_generation=$($receipt.receiptGeneration); receipt_status=$($receipt.receiptStatus); receipt_fingerprint=$($receipt.receiptFingerprint); route=$($scope.route); protocol_version=$($scope.protocolVersion); live_route_epoch=$($scope.liveRouteEpoch); route_publication_generation=$($scope.routePublicationGeneration); route_commit_generation=$($scope.routeCommitGeneration); runtime_generation=$($scope.runtimeGeneration); terminal_generation=$($scope.terminalGeneration); leg_generation=$($scope.legGeneration); transport_epoch=$($scope.transportEpoch); bridge_recovery_generation=$($scope.bridgeRecoveryGeneration); checkpoint_generation=$($scope.checkpointGeneration); checkpoint_request_id=$($scope.checkpointRequestId); data_session_identity=$($scope.dataSessionIdentity); data_session_generation=$($scope.dataSessionGeneration); authority_effect=applied; log_writer_id=fixture-writer; log_writer_sequence=$sequence"
}
function New-Lines([bool]$includeCommit = $true, [bool]$forgedPending = $false) {
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=$($first.aggregate.beforeStateGeneration); initial_state_hash=$($first.aggregate.beforeStateFingerprint); $coverage; authority_effect=none")
    for ($i = 0; $i -lt $envelopes.Count; $i++) {
        $envelope = $envelopes[$i]
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes(($envelope | ConvertTo-Json -Depth 64 -Compress))
        $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
        $payload = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
        $sequence = 10L * ($i + 1)
        $lines.Add("event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=$($envelope.transitionId); before_state_generation=$($envelope.aggregate.beforeStateGeneration); after_state_generation=$($envelope.aggregate.afterStateGeneration); snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none; log_writer_id=fixture-writer; log_writer_sequence=$sequence")
        if ($i -eq 1) {
            $op = @($envelope.aggregate.operations | Where-Object { $_.operationRole -ceq 'attempt_pending' })[0]
            $receipt = @($op.runtimeUnlockRetry.receipts | Where-Object { $_.stage -ceq 'stage' })[0]
            $lines.Add((New-Effect $envelope $op $receipt 'dispatch_stage_observed' ($sequence + 1)))
        }
        if ($i -eq 2 -and $forgedPending) {
            $lines.Add((New-Effect $envelope $pending (Install-Receipt $pending) 'install_committed' ($sequence + 1)))
        }
        if ($i -eq 4 -and $includeCommit) {
            $lines.Add((New-Effect $envelope $installed (Install-Receipt $installed) 'install_committed' ($sequence + 1)))
        }
    }
    $last = $envelopes[$envelopes.Count - 1]
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=$($last.aggregate.afterStateGeneration); final_state_hash=$($last.aggregate.afterStateFingerprint); transition_count=$($envelopes.Count); $coverage; authority_effect=none")
    return $lines.ToArray()
}

$lines = @(New-Lines)
$full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated') + $lines)
$compactLines = @($lines | Where-Object {
    $catalog.SnapshotAuthorityV1 -contains (Get-FileTransferCoordinatorSnapshotEventName -Line $_) -or
    $catalog.GenerationStampedEffects -contains (Get-FileTransferCoordinatorSnapshotEventName -Line $_)
})
$compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
$parity = Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact
if ($full.ArtifactEvidenceVerdict -cne 'pass' -or $parity.Verdict -cne 'pass' -or
    @($full.Boundaries | Where-Object {
        $_.taxonomyClass -ceq 'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete'
    }).Count -ne 0 -or
    $full.FirstUnresolvedBoundary.boundaryId -cne $envelopes[5].transitionId -or
    $full.FirstUnresolvedBoundary.taxonomyClass -cne 'runtime_unlock_retry_dispatch_contract_unsettled') {
    throw "Production issuance-to-install fixture failed: $($full | Select-Object * -ExcludeProperty Boundaries | ConvertTo-Json -Depth 16 -Compress)"
}
$missing = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (New-Lines $false)
if ($null -eq $missing.FirstUnresolvedBoundary -or
    $missing.FirstUnresolvedBoundary.boundaryId -cne $commit.transitionId -or
    $missing.FirstUnresolvedBoundary.taxonomyClass -cne 'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete') {
    throw 'Genuine missing committed-install effect was hidden or attributed to issuance.'
}
$forged = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (New-Lines $true $true)
if ($forged.ArtifactEvidenceFailureReason -notlike '*snapshot_authority_effect_install_before_commit:*') {
    throw 'Forged pending-install effect did not fail artifact validation.'
}
$duplicateLines = @($lines | ForEach-Object { $_; if ($_ -like '*effect_kind=install_committed;*') { $_ } })
if ((Get-FileTransferCoordinatorSnapshotEvaluation -Lines $duplicateLines).ArtifactEvidenceVerdict -cne 'pass') {
    throw 'Byte-identical effect duplicates did not coalesce.'
}
$conflictLines = @($lines | ForEach-Object {
    $_; if ($_ -like '*effect_kind=install_committed;*') { $_.Replace('receipt_status=Installed;', 'receipt_status=Staged;') }
})
if ((Get-FileTransferCoordinatorSnapshotEvaluation -Lines $conflictLines).ArtifactEvidenceVerdict -cne 'fail') {
    throw 'Conflicting effect duplicates passed.'
}

# Exercise the real GUI consumer, not a second lifecycle implementation.
$guiAst = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'), [ref]$null, [ref]$null)
foreach ($name in @('New-TunaGuiFileTransferFailureClassificationResult',
        'Get-TunaGuiExplicitFailureClassification', 'Get-TunaGuiFileTransferSetupFailureClassification')) {
    $definitions = @($guiAst.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $true))
    if ($definitions.Count -ne 1) { throw "GUI function not unique: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
foreach ($case in @(
    @{ Lines = $compactLines; Evaluation = $full },
    @{ Lines = @(New-Lines $false); Evaluation = $missing },
    @{ Lines = @(New-Lines $true $true); Evaluation = $forged }
)) {
    $compactPath = Join-Path $OutputRoot 'compact.log'
    [IO.File]::WriteAllLines($compactPath, [string[]]$case.Lines)
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'diagnostic evidence evaluation' `
        -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($compactPath)
    $boundary = $case.Evaluation.FirstUnresolvedBoundary
    if ($gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or
        $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or
        $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or
        $gui.CoordinatorRawCount -ne $boundary.rawCount) {
        throw "Shared GUI boundary diverged: $($gui | ConvertTo-Json -Depth 8 -Compress)"
    }
}
Write-Output 'Staged-install production fixtures, exact mutations, effects, and full/compact/GUI parity passed.'
