param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot, [string]$Mode)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') { throw 'Frozen catalog changed.' }
# Reuse only the existing fixture serialization helpers, not its test execution.
$helpers = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/RetryConfirmationCompensationSnapshotEvidence.ps1'), [ref]$null, [ref]$null)
foreach ($name in @('Copy-Value', 'Boundary', 'Prior-Entry', 'Effect')) {
    $definitions = @($helpers.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -ceq $name }, $true))
    if ($definitions.Count -ne 1) { throw "Fixture helper not unique: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
# Retain the real paired-yield lineage as well as Family 2, including the
# intervening supersession. Do not fabricate empty quiescent transitions.
# Physical effects below are unit fixtures, not evidence of a live route.
foreach ($envelope in $envelopes) {
    $envelope.snapshotSelectionMode='SnapshotV1Shadow'
    $envelope.aggregate.operations=@($envelope.aggregate.operations | Where-Object family -CIn @('runtime_unlock_retry_lifecycle','fallback_tail_yield'))
    $envelope.aggregate.changedFamilies=@($envelope.aggregate.operations.family | Select-Object -Unique)
}
$compensation = $envelopes[-1]
$shape = Get-FileTransferCoordinatorRetryConfirmationCompensationShape $compensation
if ($null -eq $shape -or $shape.Operations.Count -ne 3 -or $shape.Restored.Count -ne 0) { throw 'Production independent terminal eviction shape rejected.' }
$priorEntries = @($envelopes[0..($envelopes.Count-2)] | ForEach-Object { Prior-Entry $_ })
if (-not (Test-FileTransferCoordinatorRetryConfirmationCompensationHistory $compensation (Boundary $compensation) $priorEntries)) {
    throw 'Production independent terminal eviction history rejected.'
}
$terminalKey = $shape.Displaced[0].operationKey
$origins = @($priorEntries | Where-Object {
    @($_.Boundary.operations | Where-Object { $_.operationKey -ceq $terminalKey -and $_.stateApplied }).Count -gt 0
})
if ($origins.Count -ne 1) { throw 'Old terminal origin not unique.' }
$origin = $origins[0]
$mutations = [ordered]@{
    missing_origin = { param($h) $e=@($h | Where-Object { $_.Boundary.afterStateGeneration -eq $origin.Boundary.afterStateGeneration })[0]; $e.Boundary.operations=@($e.Boundary.operations | Where-Object operationKey -CNE $terminalKey); $h }
    changed_receipt = { param($h) $op=@($h.Boundary.operations | Where-Object operationKey -CEQ $terminalKey)[0]; $op.runtimeUnlockRetry.receipts[-1].receiptFingerprint='foreign'; $h }
    rejected_origin = { param($h) $e=@($h | Where-Object { $_.Boundary.afterStateGeneration -eq $origin.Boundary.afterStateGeneration })[0]; $e.Boundary.decisionStatus='Rejected'; $h }
    foreign_writer = { param($h) $e=@($h | Where-Object { $_.Boundary.afterStateGeneration -eq $origin.Boundary.afterStateGeneration })[0]; $e.Boundary.writer='foreign'; $h }
    missing_commit = { param($h) @($h | Where-Object { $_.Boundary.afterStateGeneration -ne $compensation.aggregate.beforeStateGeneration }) }
    terminal_remove_restore = { param($h)
        foreach ($states in @(@('Settled','None'),@('None','Settled'))) {
            $e=Copy-Value $origin; $e.Boundary.afterStateGeneration++
            $op=Copy-Value (@($e.Boundary.operations | Where-Object operationKey -CEQ $terminalKey)[0])
            $op.beforeState=$states[0]; $op.afterState=$states[1]; $e.Boundary.operations=@($op); $h+=@($e)
        }; $h
    }
    foreign_terminal_occupancy_aba = { param($h)
        foreach ($states in @(@('None','Settled'),@('Settled','None'))) {
            $e=Copy-Value $origin; $e.Boundary.afterStateGeneration++
            $op=Copy-Value (@($e.Boundary.operations | Where-Object operationKey -CEQ $terminalKey)[0])
            $op.operationKey='foreign-terminal-key'; $op.authorityId='foreign-authority'
            $op.beforeState=$states[0]; $op.afterState=$states[1]; $e.Boundary.operations=@($op); $h+=@($e)
        }; $h
    }
    commit_terminal_overwrite = { param($h)
        $e=@($h | Where-Object { $_.Boundary.afterStateGeneration -eq $compensation.aggregate.beforeStateGeneration })[0]
        $op=Copy-Value (@($origin.Boundary.operations | Where-Object operationKey -CEQ $terminalKey)[0])
        $op.operationKey='foreign-terminal-key'; $op.authorityId='foreign-authority'; $e.Boundary.operations+=@($op); $h
    }
}
foreach ($case in $mutations.Keys) {
    $history = @(& $mutations[$case] (Copy-Value $priorEntries))
    if (Test-FileTransferCoordinatorRetryConfirmationCompensationHistory $compensation (Boundary $compensation) $history) { throw "Invalid terminal history accepted: $case" }
}
$shapeMutations = [ordered]@{
    wrong_count = { param($e) $e.aggregate.operations+=Copy-Value $e.aggregate.operations[0] }
    wrong_generation = { param($e) $e.aggregate.afterStateGeneration++ }
    restored_independent_terminal = { param($e) $op=Copy-Value $shape.Displaced[0]; $op.operationRole='attempt_installed'; $op.runtimeUnlockRetry.role='attempt_installed'; $op.beforeState='None'; $op.afterState='Settled'; $e.aggregate.operations+=@($op) }
    changed_terminal_source = { param($e) $op=@($e.aggregate.operations | Where-Object operationKey -CEQ $terminalKey)[0]; $op.runtimeUnlockRetry.sourceBoundary.dataSessionGeneration++ }
    missing_dispatch_receipt = { param($e) $op=@($e.aggregate.operations | Where-Object operationKey -CEQ $terminalKey)[0]; $op.runtimeUnlockRetry.receipts[-1].receiptStatus='MissingDispatch'; $op.receiptStatus='MissingDispatch' }
    false_dispatch = { param($e) $op=@($e.aggregate.operations | Where-Object operationKey -CEQ $terminalKey)[0]; $op.runtimeUnlockRetry.dispatchObserved=$false }
    foreign_scope = { param($e) $e.transferId='foreign-transfer' }
    changed_install_receipt = { param($e) foreach ($op in @($e.aggregate.operations | Where-Object authorityId -CEQ $shape.Failed.authorityId)) { $op.runtimeUnlockRetry.receipts[1].receiptFingerprint='forged'; $op.receiptFingerprint='forged' } }
}
foreach ($case in $shapeMutations.Keys) {
    $bad=Copy-Value $compensation; & $shapeMutations[$case] $bad
    if (Test-FileTransferCoordinatorRetryConfirmationCompensationHistory $bad (Boundary $bad) $priorEntries) { throw "Invalid compensation accepted: $case" }
}
$first=$envelopes[0]
$prefix="snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$coverage='emitted_family_coverage=runtime_unlock_retry_lifecycle,fallback_tail_yield; shadow_family_coverage=; authoritative_family_coverage=runtime_unlock_retry_lifecycle,fallback_tail_yield'
function Lines($case) {
    $lines=[Collections.Generic.List[string]]::new()
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=$($first.aggregate.beforeStateGeneration); initial_state_hash=$($first.aggregate.beforeStateFingerprint); $coverage; authority_effect=none")
    $sequence=0L
    foreach ($original in $envelopes) {
        $envelope=Copy-Value $original
        if ($case -ceq 'descriptor_order') { [array]::Reverse($envelope.aggregate.operations) }
        if ($case -ceq 'missing_terminal_origin' -and $envelope.aggregate.afterStateGeneration -eq $origin.Boundary.afterStateGeneration) {
            $envelope.aggregate.operations=@($envelope.aggregate.operations | Where-Object operationKey -CNE $terminalKey)
        }
        $sequence+=10L
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($envelope | ConvertTo-Json -Depth 64 -Compress))
        $hash=Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
        $payload=[Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
        $line="event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=$($envelope.transitionId); before_state_generation=$($envelope.aggregate.beforeStateGeneration); after_state_generation=$($envelope.aggregate.afterStateGeneration); snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none; log_writer_id=fixture-writer; log_writer_sequence=$sequence"
        $lines.Add($line)
        if ($case -ceq 'exact_duplicate' -and $envelope.transitionId -ceq $compensation.transitionId) { $lines.Add($line) }
        foreach ($op in $envelope.aggregate.operations) {
            $stage=switch -Exact ($envelope.decisionKind) {
                'ObserveRuntimeUnlockRetryDispatchStage' { if ($op.operationRole -ceq 'attempt_pending' -and $op.stateApplied) { 'stage' }; break }
                'CommitRuntimeUnlockRetryDispatchInstall' { if ($op.operationRole -ceq 'attempt_installed' -and $op.beforeState -ceq 'None') { 'install' }; break }
                'ObserveRuntimeUnlockRetryDispatchExecutionState' { if ($op.operationRole -ceq 'attempt_installed' -and $op.stateApplied) { 'execution' }; break }
                'ObserveRuntimeUnlockRetryDispatchSettlement' { if ($op.operationRole -ceq 'attempt_terminal' -and $op.beforeState -ceq 'None') { 'settlement' }; break }
                default { '' }
            }
            if ([string]::IsNullOrWhiteSpace($stage)) { continue }
            $receipt=@($op.runtimeUnlockRetry.receipts | Where-Object stage -CEQ $stage)
            if ($receipt.Count -ne 1) { throw "Receipt not unique: $stage" }
            if ($case -ceq 'missing_new_install' -and $stage -ceq 'install' -and $op.authorityId -ceq $shape.Failed.authorityId) { continue }
            $effect=@{stage='dispatch_stage_observed';install='install_committed';execution='execution_observed';settlement='settlement_observed'}[$stage]
            $lines.Add((Effect $envelope $op $receipt[0] $effect ($sequence+1)))
        }
        if ($case -ceq 'forged_compensation_effect' -and $envelope.transitionId -ceq $compensation.transitionId) {
            $lines.Add((Effect $envelope $shape.Terminal $shape.Terminal.runtimeUnlockRetry.receipts[1] 'install_committed' ($sequence+1)))
        }
    }
    $last=$envelopes[-1]
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=$($last.aggregate.afterStateGeneration); final_state_hash=$($last.aggregate.afterStateFingerprint); transition_count=$($envelopes.Count); $coverage; authority_effect=none")
    $lines.ToArray()
}
$summaries=@()
foreach ($case in @('exact','descriptor_order','exact_duplicate','missing_terminal_origin','missing_new_install','forged_compensation_effect')) {
    $lines=@(Lines $case)
    $lines | Set-Content -LiteralPath (Join-Path $OutputRoot "$case.log") -Encoding UTF8
    $full=Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated')+$lines)
    $compact=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact).Verdict -cne 'pass') { throw "$case full/compact mismatch." }
    $positive=$case -in @('exact','descriptor_order','exact_duplicate')
    if ($positive -and ($full.ArtifactEvidenceVerdict -cne 'pass' -or @($full.Boundaries | Where-Object {
        $_.boundaryId -ceq $compensation.transitionId -and $_.verdict -ceq 'fail'
    }).Count -gt 0)) { throw "$case production compensation rejected: $($full.ArtifactEvidenceFailureReason)" }
    if (-not $positive -and $full.ArtifactEvidenceVerdict -ceq 'pass' -and @($full.Boundaries | Where-Object verdict -CEQ 'fail').Count -eq 0) { throw "$case missing/forged evidence accepted." }
    if ($case -ceq 'missing_terminal_origin' -and $full.ArtifactEvidenceFailureReason -notlike '*snapshot_retry_confirmation_compensation_invalid*') { throw 'Missing terminal origin escaped history validation.' }
    if ($case -ceq 'missing_new_install' -and @($full.Boundaries | Where-Object {
        $_.boundaryId -ceq $envelopes[-2].transitionId -and $_.verdict -ceq 'fail' -and
        $_.taxonomyClass -ceq 'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete'
    }).Count -ne 1) { throw 'Compensation hid the original install effect requirement.' }
    if ($case -ceq 'forged_compensation_effect' -and $full.ArtifactEvidenceFailureReason -notlike '*snapshot_retry_confirmation_compensation_effect_forbidden*') { throw 'Compensation was allowed to emit a physical effect.' }
    $summaries += [ordered]@{case=$case;artifact=$full.ArtifactEvidenceVerdict;parity='pass'}
}
$normalized=[ordered]@{historyControls=$mutations.Count;shapeControls=$shapeMutations.Count;cases=$summaries} | ConvertTo-Json -Depth 8 -Compress
$artifactRoot=[Environment]::GetEnvironmentVariable('NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR')
if (-not [string]::IsNullOrWhiteSpace($artifactRoot) -and (Test-Path -LiteralPath $artifactRoot -PathType Container)) {
    $normalized | Set-Content -LiteralPath (Join-Path $artifactRoot "independent-terminal-evidence-ps$($PSVersionTable.PSVersion.Major).json") -Encoding UTF8
}
'retry-confirmation-compensation-normalized='+$normalized
