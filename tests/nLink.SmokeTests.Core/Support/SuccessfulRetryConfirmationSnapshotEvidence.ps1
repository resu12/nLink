param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot, [string]$Mode)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$settled=$Mode -ceq 'successful_settled'
if($settled) {
    . (Join-Path $RepoRoot 'tools/FileTransferAuthorityMatching.ps1')
    . (Join-Path $RepoRoot 'tools/FileTransferFallbackV6BootstrapDirectionCorrelation.ps1')
    . (Join-Path $RepoRoot 'tools/FileTransferFrontierGraceHandoff.ps1')
    . (Join-Path $RepoRoot 'tools/GuiSmokeLogLedger.ps1')
}
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2' -or
    $script:FileTransferCoordinatorSnapshotFamily2ReceiptStages['settlement'] -contains 'Superseded') { throw 'Frozen catalog changed.' }
function Copy-Value($value) { $value | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json }
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
$firstRetry = -1
for ($i=0; $i -lt $envelopes.Count; $i++) {
    if (@($envelopes[$i].aggregate.operations | Where-Object family -CEQ 'runtime_unlock_retry_lifecycle').Count -gt 0) { $firstRetry=$i; break }
}
if ($firstRetry -lt 0) { throw 'No real retry source graph.' }
$envelopes = @($envelopes[$firstRetry..($envelopes.Count-1)])
# Production-projected Coordinator graph, Family-2-only Shadow scope. Effects
# below are explicit receipt-backed analyzer unit fixtures, not service callbacks.
foreach ($envelope in $envelopes) {
    $envelope.snapshotSelectionMode='SnapshotV1Shadow'
    $envelope.aggregate.operations=@($envelope.aggregate.operations | Where-Object family -CEQ 'runtime_unlock_retry_lifecycle')
    $envelope.aggregate.changedFamilies=@('runtime_unlock_retry_lifecycle')
}
$retirements=@($envelopes | Where-Object decisionReason -CEQ 'retry_dispatch_settlement_superseded_predecessor')
if ($retirements.Count -ne 1) { throw 'Successful retirement not unique.' }
$retirement=$retirements[0]
$shape=Get-FileTransferCoordinatorSuccessfulRetryRetirementShape $retirement
if ($null -eq $shape) { throw 'Genuine successful retirement shape rejected.' }
$commits=@($envelopes | Where-Object {
    $_.decisionKind -ceq 'CommitRuntimeUnlockRetryDispatchInstall' -and
    @($_.aggregate.operations | Where-Object operationKey -CEQ $shape.Retained.operationKey).Count -eq 1
})
if ($commits.Count -ne 1) { throw 'Original active commit not unique.' }
$commit=$commits[0]
$original=@($commit.aggregate.operations | Where-Object {
    $_.operationRole -ceq 'attempt_installed' -and $_.beforeState -ceq 'AliasBound' -and $_.afterState -ceq 'None'
})[0]
$successor=@($commit.aggregate.operations | Where-Object {
    $_.operationRole -ceq 'attempt_installed' -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Installed'
})[0]
$ownSettlement=$null
if($settled) {
    $closures=@($envelopes | Where-Object {
        $_.decisionKind -ceq 'ObserveRuntimeUnlockRetryDispatchSettlement' -and $_.decisionStatus -ceq 'Accepted' -and
        $_.decisionReason -ceq 'retry_dispatch_settlement_accepted' -and
        @($_.aggregate.operations | Where-Object {
            $_.authorityId -ceq $successor.authorityId -and $_.operationRole -ceq 'attempt_terminal' -and
            $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Settled' -and $_.receiptStatus -ceq 'DispatchObserved'
        }).Count -eq 1
    })
    if($closures.Count -ne 1){throw 'Genuine B DispatchObserved closure is not unique.'}
    $ownSettlement=$closures[0]
    $evictions=@($ownSettlement.aggregate.operations | Where-Object {
        $_.authorityId -ceq $original.authorityId -and $_.operationRole -ceq 'attempt_terminal' -and
        $_.beforeState -ceq 'Settled' -and $_.afterState -ceq 'None'
    })
    $removals=@($ownSettlement.aggregate.operations | Where-Object {
        $_.authorityId -ceq $successor.authorityId -and $_.operationRole -ceq 'attempt_installed' -and
        $_.beforeState -ceq 'SendInFlight' -and $_.afterState -ceq 'None'
    })
    $expectedEviction=Copy-Value $shape.Terminal
    $expectedEviction.beforeState='Settled';$expectedEviction.afterState='None'
    if($evictions.Count -ne 1 -or $removals.Count -ne 1 -or $ownSettlement.aggregate.operations.Count -ne 3 -or
        $ownSettlement.aggregate.afterStateGeneration -ne ($ownSettlement.aggregate.beforeStateGeneration+1L) -or
        ($evictions[0] | ConvertTo-Json -Depth 32 -Compress) -cne ($expectedEviction | ConvertTo-Json -Depth 32 -Compress)) {
        throw 'B closure must atomically evict the exact immutable A terminal and remove its own installation.'
    }
}
$first=$envelopes[0]
$prefix="snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Shadow; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
function Effect($envelope,$operation,$receipt,$kind,$sequence) {
    $scope=$operation.runtimeUnlockRetry.sourceBoundary
    $scopeFields=foreach($pair in @(@('route','route'),@('protocol_version','protocolVersion'),@('live_route_epoch','liveRouteEpoch'),
        @('route_publication_generation','routePublicationGeneration'),@('route_commit_generation','routeCommitGeneration'),
        @('runtime_generation','runtimeGeneration'),@('terminal_generation','terminalGeneration'),@('leg_generation','legGeneration'),
        @('transport_epoch','transportEpoch'),@('bridge_recovery_generation','bridgeRecoveryGeneration'),
        @('checkpoint_generation','checkpointGeneration'),@('checkpoint_request_id','checkpointRequestId'),
        @('data_session_identity','dataSessionIdentity'),@('data_session_generation','dataSessionGeneration'))) { "$($pair[0])=$($scope.($pair[1]))" }
    "event=filetransfer_coordinator_authority_effect; $prefix; transition_id=$($envelope.transitionId); coordinator_state_generation=$($envelope.aggregate.afterStateGeneration); operation_family=$($operation.family); operation_key=$($operation.operationKey); operation_authority_id=$($operation.authorityId); operation_generation=$($operation.operationGeneration); effect_kind=$kind; effect_identity=$kind-$($receipt.receiptIdentity); effect_generation=$($receipt.receiptGeneration); receipt_identity=$($receipt.receiptIdentity); receipt_generation=$($receipt.receiptGeneration); receipt_status=$($receipt.receiptStatus); receipt_fingerprint=$($receipt.receiptFingerprint); $($scopeFields -join '; '); authority_effect=applied; log_writer_id=fixture-writer; log_writer_sequence=$sequence"
}
function Lines($case) {
    $caseEnvelopes=Copy-Value $envelopes
    if($case -ceq 'missing_a_origin') {
        foreach($envelope in $caseEnvelopes) {
            if($envelope.decisionKind -ceq 'CommitRuntimeUnlockRetryDispatchInstall' -and $envelope.transitionId -cne $commit.transitionId) {
                $envelope.aggregate.operations=@($envelope.aggregate.operations | Where-Object authorityId -CNE $original.authorityId)
                if($envelope.aggregate.operations.Count -eq 0){$envelope.aggregate.changedFamilies=@()}
            }
        }
    }
    if ($case -ceq 'missing_origin') { $caseEnvelopes=@($caseEnvelopes | Where-Object transitionId -CNE $commit.transitionId) }
    if ($case -in @('no_retirement','late_missing_retirement')) { $caseEnvelopes=@($caseEnvelopes | Where-Object transitionId -CNE $retirement.transitionId) }
    if ($case -ceq 'late_only_no_history') {
        # Retain the genuine state-neutral late envelopes, but deliberately omit
        # their authority history. A declared starting generation is not proof.
        $caseEnvelopes=@($caseEnvelopes | Where-Object {
            $_.decisionKind -ceq 'ObserveRuntimeUnlockRetryDispatchSettlement' -and $_.decisionStatus -in @('Idempotent','Rejected')
        })
        if($caseEnvelopes.Count -ne 2){throw 'Expected the two genuine late reports.'}
    }
    if ($case -in @('scope_aba','late_terminal_overwrite_restore','terminal_poison_aba')) {
        # Explicit forged history attacks, not production transitions. Continuous
        # generation/hash links isolate scope/terminal poisoning from header gaps.
        $anchor=if($settled -or $case -ceq 'late_terminal_overwrite_restore'){$retirement}else{$commit}
        $before=@($caseEnvelopes | Where-Object { $_.aggregate.afterStateGeneration -lt $anchor.aggregate.afterStateGeneration -or $_.transitionId -ceq $anchor.transitionId })
        $after=@($caseEnvelopes | Where-Object { $_.aggregate.beforeStateGeneration -ge $anchor.aggregate.afterStateGeneration -and $_.transitionId -cne $anchor.transitionId })
        $previous=$anchor.aggregate.afterStateFingerprint;$scope=Copy-Value $anchor.aggregate.afterBoundary
        $attacks=@()
        for($i=0;$i -lt 2;$i++) {
            $attack=Copy-Value $retirement
            $attack.transitionId="fte1-aaaaaaaaaaaaaaaaaaaaaaa$i"
            $attack.decisionKind='ObserveRuntimeUnlockRetryDispatchExecutionState';$attack.decisionReason='scope_attack'
            $attack.aggregate.beforeStateGeneration=$anchor.aggregate.afterStateGeneration+$i
            $attack.aggregate.afterStateGeneration=$attack.aggregate.beforeStateGeneration+1L
            $attack.aggregate.beforeStateFingerprint=$previous
            $attack.aggregate.afterStateFingerprint=Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([Text.Encoding]::UTF8.GetBytes("scope-attack-$i"))
            $attack.aggregate.beforeBoundary=Copy-Value $scope;$attack.aggregate.afterBoundary=Copy-Value $anchor.aggregate.afterBoundary
            if($case -ceq 'scope_aba' -and $i -eq 0){$attack.aggregate.afterBoundary.dataSessionGeneration++}
            $attack.aggregate.operations=@();$attack.aggregate.changedFamilies=@()
            if($case -in @('late_terminal_overwrite_restore','terminal_poison_aba')) {
                # C overwrites then vacates the singleton terminal slot without
                # retiring A. Genuine late A reports cannot reconstruct A's owner.
                $foreign=Copy-Value $successor
                $foreign.authorityId='fte1-cccccccccccccccccccccccc';$foreign.operationGeneration+=100L
                $foreign.operationRole='attempt_terminal';$foreign.runtimeUnlockRetry.role='attempt_terminal'
                $foreign.runtimeUnlockRetry.authorityGeneration=$foreign.operationGeneration
                $foreign.operationKey="runtime_unlock_retry_lifecycle|attempt_terminal|$($foreign.authorityId)|$($foreign.operationGeneration)|RetryAttempt"
                $foreign.beforeState=if($i -eq 0){'None'}else{'Invalidated'}
                $foreign.afterState=if($i -eq 0){'Invalidated'}else{'None'}
                $foreign.runtimeUnlockRetry.receipts[1].receiptStatus='Invalidated';$foreign.receiptStatus='Invalidated'
                $attack.aggregate.operations=@($foreign);$attack.aggregate.changedFamilies=@('runtime_unlock_retry_lifecycle')
                if(-not (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog -Envelope $attack -Operation $foreign)) {
                    throw 'Terminal overwrite attack must reach history validation, not fail the local catalog.'
                }
            }
            $scope=Copy-Value $attack.aggregate.afterBoundary;$previous=$attack.aggregate.afterStateFingerprint;$attacks+=$attack
        }
        foreach($next in $after){$next.aggregate.beforeStateGeneration+=2L;$next.aggregate.afterStateGeneration+=2L;
            $next.aggregate.beforeStateFingerprint=$previous
            if($next.aggregate.beforeStateGeneration -eq $next.aggregate.afterStateGeneration){$next.aggregate.afterStateFingerprint=$previous}
            $previous=$next.aggregate.afterStateFingerprint}
        $caseEnvelopes=@($before)+@($attacks)+@($after)
    }
    $lines=[Collections.Generic.List[string]]::new()
    $caseFirst=$caseEnvelopes[0]
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_started; $prefix; initial_state_generation=$($caseFirst.aggregate.beforeStateGeneration); initial_state_hash=$($caseFirst.aggregate.beforeStateFingerprint); emitted_family_coverage=runtime_unlock_retry_lifecycle; shadow_family_coverage=; authoritative_family_coverage=runtime_unlock_retry_lifecycle; authority_effect=none")
    $sequence=0L
    foreach ($envelope in $caseEnvelopes) {
        $isRetirement=$envelope.transitionId -ceq $retirement.transitionId
        $isClosure=$settled -and $envelope.transitionId -ceq $ownSettlement.transitionId
        $isLate=$envelope.decisionKind -ceq 'ObserveRuntimeUnlockRetryDispatchSettlement' -and $envelope.decisionStatus -in @('Rejected','Idempotent')
        if($isLate) {
            switch($case) {
                'late_changed_payload' { $envelope.aggregate.operations[0].runtimeUnlockRetry.receipts[2].receiptFingerprint='changed-terminal-history' }
                'late_wrong_reason' { $envelope.decisionReason='foreign-attempt' }
                'late_state_applied' { $envelope.aggregate.operations[0].stateApplied=$true }
            }
        }
        if ($case -ceq 'descriptor_order' -and ($isClosure -or $envelope.transitionId -in @($commit.transitionId,$retirement.transitionId))) { [array]::Reverse($envelope.aggregate.operations) }
        if($isClosure) {
            $outgoing=@($envelope.aggregate.operations | Where-Object authorityId -CEQ $original.authorityId)[0]
            $incoming=@($envelope.aggregate.operations | Where-Object authorityId -CEQ $successor.authorityId)
            switch($case) {
                'missing_a_removal' { $envelope.aggregate.operations=@($incoming) }
                'missing_b_terminal' { $envelope.aggregate.operations=@($envelope.aggregate.operations | Where-Object { $_.authorityId -cne $successor.authorityId -or $_.operationRole -cne 'attempt_terminal' }) }
                'missing_b_removal' { $envelope.aggregate.operations=@($envelope.aggregate.operations | Where-Object operationRole -CNE 'attempt_installed') }
                'duplicate_a_removal' { $envelope.aggregate.operations+=Copy-Value $outgoing }
                'duplicate_b_terminal' { $envelope.aggregate.operations+=Copy-Value (@($incoming | Where-Object operationRole -CEQ 'attempt_terminal')[0]) }
                'mutated_a_removal' { $outgoing.receiptFingerprint='foreign-a-receipt';$outgoing.runtimeUnlockRetry.receipts[3].receiptFingerprint='foreign-a-receipt' }
                'wrong_a_removal_generation' { $outgoing.operationGeneration++;$outgoing.runtimeUnlockRetry.authorityGeneration++;$outgoing.operationKey="runtime_unlock_retry_lifecycle|attempt_terminal|$($outgoing.authorityId)|$($outgoing.operationGeneration)|RetryAttempt" }
                'wrong_b_authority' { foreach($op in $incoming){$op.authorityId='fte1-bbbbbbbbbbbbbbbbbbbbbbbb';$op.operationKey="runtime_unlock_retry_lifecycle|$($op.operationRole)|$($op.authorityId)|$($op.operationGeneration)|RetryAttempt"} }
                'wrong_b_generation' { foreach($op in $incoming){$op.operationGeneration++;$op.runtimeUnlockRetry.authorityGeneration++;$op.operationKey="runtime_unlock_retry_lifecycle|$($op.operationRole)|$($op.authorityId)|$($op.operationGeneration)|RetryAttempt"} }
                'wrong_b_predecessor' { foreach($op in $incoming){$op.predecessorAuthorityId='fte1-cccccccccccccccccccccccc'} }
                'wrong_b_history_receipt' { foreach($op in $incoming){$op.runtimeUnlockRetry.receipts[1].receiptFingerprint='foreign-b-install'} }
                'terminal_execution_generation_with_effect' {
                    $terminal=@($incoming | Where-Object operationRole -CEQ 'attempt_terminal')[0]
                    $terminal.runtimeUnlockRetry.receipts[2].commandGeneration++
                    $terminal.runtimeUnlockRetry.receipts[2].receiptGeneration++
                }
                'wrong_b_source_scope' { foreach($op in $incoming){$op.runtimeUnlockRetry.sourceBoundary.runtimeGeneration++} }
                'wrong_b_data_scope' { foreach($op in $incoming){$op.runtimeUnlockRetry.sourceBoundary.dataSessionGeneration++} }
                'closure_scope_poison' { $envelope.aggregate.afterBoundary.dataSessionGeneration++ }
            }
        }
        if ($isRetirement) {
            switch ($case) {
                'missing_terminal' { $envelope.aggregate.operations=@($envelope.aggregate.operations | Where-Object operationRole -CNE 'attempt_terminal') }
                'missing_retained' { $envelope.aggregate.operations=@($envelope.aggregate.operations | Where-Object operationRole -CNE 'attempt_retained_predecessor') }
                'duplicate_terminal' { $envelope.aggregate.operations+=Copy-Value $shape.Terminal }
                'wrong_decision' { $envelope.decisionReason='retry_dispatch_settlement_accepted' }
                'scope_poison' { $envelope.aggregate.afterBoundary.dataSessionGeneration++ }
                'changed_history_receipt' {
                    foreach ($op in $envelope.aggregate.operations) { $op.runtimeUnlockRetry.receipts[2].receiptFingerprint='changed-retained-execution' }
                }
                'changed_settlement' {
                    foreach ($op in $envelope.aggregate.operations) { $op.runtimeUnlockRetry.receipts[3].receiptFingerprint='changed-settlement';$op.receiptFingerprint='changed-settlement' }
                }
                'wrong_successor' { foreach ($op in $envelope.aggregate.operations) { $op.runtimeUnlockRetry.successorAuthorityId='foreign-successor' } }
                'string_state_applied' { foreach ($op in $envelope.aggregate.operations) { $op.stateApplied='true' } }
                'wrong_offer_generation' { foreach ($op in $envelope.aggregate.operations) { $op.runtimeUnlockRetry.offerGeneration++ } }
                'foreign_terminal' {
                    $op=@($envelope.aggregate.operations | Where-Object operationRole -CEQ 'attempt_terminal')[0]
                    $op.authorityId='foreign-authority';$op.operationKey="runtime_unlock_retry_lifecycle|attempt_terminal|foreign-authority|$($op.operationGeneration)|RetryAttempt"
                }
            }
        }
        $sequence+=10L
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($envelope | ConvertTo-Json -Depth 64 -Compress))
        $hash=Get-FileTransferCoordinatorSnapshotSha256 -Bytes $bytes
        $payload=[Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
        $line="event=filetransfer_coordinator_transition_snapshot; $prefix; transition_id=$($envelope.transitionId); before_state_generation=$($envelope.aggregate.beforeStateGeneration); after_state_generation=$($envelope.aggregate.afterStateGeneration); snapshot_sha256=$hash; snapshot_payload=$payload; authority_effect=none; log_writer_id=fixture-writer; log_writer_sequence=$sequence"
        $lines.Add($line)
        if ($case -ceq 'exact_duplicate' -and ($isRetirement -or $isClosure)) { $lines.Add($line) }
        foreach ($operation in $envelope.aggregate.operations) {
            $stage=switch -Exact ($envelope.decisionKind) {
                'ObserveRuntimeUnlockRetryDispatchStage' { if ($operation.operationRole -ceq 'attempt_pending' -and $operation.stateApplied) { 'stage' };break }
                'CommitRuntimeUnlockRetryDispatchInstall' { if ($operation.operationRole -ceq 'attempt_installed' -and $operation.beforeState -ceq 'None') { 'install' };break }
                'ObserveRuntimeUnlockRetryDispatchExecutionState' { if ($operation.operationRole -ceq 'attempt_installed' -and $operation.stateApplied) { 'execution' };break }
                'ObserveRuntimeUnlockRetryDispatchSettlement' { if (($isRetirement -or $isClosure) -and $operation.operationRole -ceq 'attempt_terminal' -and $operation.beforeState -ceq 'None') { 'settlement' };break }
                default { '' }
            }
            if (-not $stage) { continue }
            if (($case -ceq 'missing_settlement' -and $stage -ceq 'settlement' -and $isRetirement) -or
                ($case -ceq 'missing_a_install' -and $stage -ceq 'install' -and $operation.authorityId -ceq $original.authorityId) -or
                ($case -ceq 'missing_b_install' -and $stage -ceq 'install' -and $operation.authorityId -ceq $successor.authorityId) -or
                ($case -in @('missing_b_execution','missing_send_effect_with_terminal_execution') -and $stage -ceq 'execution' -and $operation.authorityId -ceq $successor.authorityId -and $operation.afterState -ceq 'SendInFlight') -or
                ($case -ceq 'missing_b_settlement' -and $stage -ceq 'settlement' -and $isClosure)) { continue }
            $receipt=@($operation.runtimeUnlockRetry.receipts | Where-Object stage -CEQ $stage)
            if ($receipt.Count -ne 1) { throw 'Unit effect receipt is not unique.' }
            $kind=@{stage='dispatch_stage_observed';install='install_committed';execution='execution_observed';settlement='settlement_observed'}[$stage]
            $effect=Effect $envelope $operation $receipt[0] $kind ($sequence+1)
            if ($stage -ceq 'settlement' -and $isRetirement) {
                switch ($case) {
                    'wrong_effect_receipt' { $effect=$effect.Replace("receipt_fingerprint=$($receipt[0].receiptFingerprint);",'receipt_fingerprint=foreign;') }
                    'wrong_effect_generation' { $effect=$effect.Replace("operation_generation=$($operation.operationGeneration);",'operation_generation=999;') }
                    'wrong_effect_authority' { $effect=$effect.Replace("operation_authority_id=$($operation.authorityId);",'operation_authority_id=foreign;') }
                    'retained_effect' { $effect=Effect $envelope $shape.Retained $receipt[0] $kind ($sequence+1) }
                    'provisional_effect' { $effect=Effect $commit (@($commit.aggregate.operations | Where-Object operationRole -CEQ 'attempt_retained_predecessor')[0]) $receipt[0] $kind ($sequence+1) }
                }
            }
            if($isClosure -and $stage -ceq 'settlement') {
                switch($case) {
                    'wrong_b_effect_receipt' { $effect=$effect.Replace("receipt_fingerprint=$($receipt[0].receiptFingerprint);",'receipt_fingerprint=foreign;') }
                    'wrong_b_effect_generation' { $effect=$effect.Replace("operation_generation=$($operation.operationGeneration);",'operation_generation=999;') }
                    'wrong_b_effect_authority' { $effect=$effect.Replace("operation_authority_id=$($operation.authorityId);",'operation_authority_id=foreign;') }
                    'a_effect_for_b' { $effect=Effect $retirement $shape.Terminal $shape.Terminal.runtimeUnlockRetry.receipts[3] $kind ($sequence+1) }
                }
            }
            $lines.Add($effect)
            if ($stage -ceq 'settlement' -and $case -ceq 'exact_effect_duplicate') { $lines.Add($effect) }
            if ($stage -ceq 'settlement' -and $case -ceq 'duplicate_effect') { $lines.Add($effect.Replace("effect_identity=$kind-$($receipt[0].receiptIdentity);","effect_identity=other-observation;")) }
            if ($stage -ceq 'settlement' -and $isClosure -and $case -ceq 'duplicate_b_effect') { $lines.Add($effect.Replace("effect_identity=$kind-$($receipt[0].receiptIdentity);","effect_identity=other-b-observation;")) }
            if ($stage -ceq 'settlement' -and $isClosure -and $case -in @('terminal_execution_generation_with_effect','missing_send_effect_with_terminal_execution')) {
                # Forged observation attacks: a terminal-local execution effect
                # cannot replace B's original exact SendInFlight observation.
                $lines.Add((Effect $envelope $operation $operation.runtimeUnlockRetry.receipts[2] 'execution_observed' ($sequence+2)))
            }
        }
        if($isLate -and $case -ceq 'late_effect') {
            $op=$envelope.aggregate.operations[0]
            $lines.Add((Effect $envelope $op $op.runtimeUnlockRetry.receipts[3] 'settlement_observed' ($sequence+2)))
        }
    }
    $last=$caseEnvelopes[-1]
    $lines.Add("event=filetransfer_coordinator_snapshot_stream_completed; $prefix; final_state_generation=$($last.aggregate.afterStateGeneration); final_state_hash=$($last.aggregate.afterStateFingerprint); transition_count=$($caseEnvelopes.Count); emitted_family_coverage=runtime_unlock_retry_lifecycle; shadow_family_coverage=; authoritative_family_coverage=runtime_unlock_retry_lifecycle; authority_effect=none")
    return $lines.ToArray()
}
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1'),[ref]$null,[ref]$null)
$guiFunctions=@('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification','Get-TunaGuiFileTransferSetupFailureClassification')
if($settled) {
    $guiFunctions+=@('Test-TunaGuiLogLinesContain','Test-TunaGuiLogLineContainsAll',
        'Test-TunaGuiFallbackFrontierRequestDispatchTupleMatch','Test-TunaGuiLatestFallbackFrontierRequestDispatchMissing',
        'Test-TunaGuiPostTunaFallbackOutboundReceiveWorkerHandoffMissing','Test-TunaGuiBridgeBootstrapNotReadyEvidence',
        'Resolve-TunaGuiFailureClassification','Get-TunaGuiReadinessStateAfterBookmark','Get-AppLogLinesAfterBookmark',
        'Sync-GuiSmokeAppLogLedger','Get-AppLogPaths','Assert-GuiSmokeScenarioDeadline')
    $emptyAppData=Join-Path $OutputRoot 'empty-gui-appdata'
    [void][IO.Directory]::CreateDirectory($emptyAppData)
    $script:GuiSmokeScenarioDeadlineUtc=[DateTimeOffset]::MaxValue
    $script:GuiSmokeScenarioDeadlineName=''
}
foreach($name in $guiFunctions) {
    $definitions=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$true))
    if($definitions.Count -ne 1){throw "GUI definition not unique: $name"}
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}
function Metadata($boundary) {
    if ($null -eq $boundary) { return $null }
    [ordered]@{Id=$boundary.boundaryId;Hash=$boundary.boundaryHash;Class=$boundary.taxonomyClass;Phase=$boundary.phase;
        Writer=$boundary.writer;Sequence=$boundary.writerSequence;Raw=$boundary.rawCount;Distinct=$boundary.distinctCount;
        Recovered=$boundary.recoveredCount;Unresolved=$boundary.unresolvedCount}
}
$summaries=@()
$cases=@('exact','descriptor_order','exact_duplicate','exact_effect_duplicate','missing_settlement','no_retirement',
    'missing_a_install','missing_b_install','missing_origin','missing_terminal','missing_retained','duplicate_terminal',
    'wrong_decision','scope_poison','changed_history_receipt','changed_settlement','wrong_successor','string_state_applied',
    'wrong_offer_generation','foreign_terminal','wrong_effect_receipt','wrong_effect_generation','wrong_effect_authority',
    'retained_effect','provisional_effect','duplicate_effect','scope_aba')
if($Mode -ceq 'successful_late'){$cases=@('exact','descriptor_order','late_changed_payload','late_wrong_reason','late_state_applied','late_missing_retirement','late_effect','late_only_no_history','late_terminal_overwrite_restore')}
if($settled) {
    $cases=@('exact','descriptor_order','exact_duplicate','exact_effect_duplicate',
        'missing_settlement','missing_a_install','missing_b_install','missing_b_execution','missing_b_settlement',
        'missing_a_origin','missing_origin','missing_a_removal','mutated_a_removal','wrong_a_removal_generation',
        'missing_b_terminal','missing_b_removal','duplicate_a_removal','duplicate_b_terminal',
        'wrong_b_authority','wrong_b_generation','wrong_b_predecessor','wrong_b_history_receipt',
        'terminal_execution_generation_with_effect','missing_send_effect_with_terminal_execution',
        'wrong_b_source_scope','wrong_b_data_scope','closure_scope_poison','scope_aba','terminal_poison_aba',
        'wrong_effect_receipt','wrong_effect_generation','wrong_effect_authority','duplicate_effect',
        'wrong_b_effect_receipt','wrong_b_effect_generation','wrong_b_effect_authority','duplicate_b_effect','a_effect_for_b')
}
foreach ($case in $cases) {
    $lines=@(Lines $case)
    $compactLines=@($lines | Where-Object { $name=Get-FileTransferCoordinatorSnapshotEventName -Line $_;
        $catalog.SnapshotAuthorityV1 -contains $name -or $catalog.GenerationStampedEffects -contains $name })
    $full=Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated')+$lines)
    $compact=Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations -Full $full -Compact $compact).Verdict -cne 'pass') { throw "$case full/compact mismatch." }
    $exact=$case -in @('exact','descriptor_order','exact_duplicate','exact_effect_duplicate')
    if ($exact -or $case -in @('missing_settlement','no_retirement','missing_a_install','missing_b_install','missing_b_execution','missing_b_settlement')) {
        if ($full.ArtifactEvidenceVerdict -cne 'pass') { throw "$case catalog/history rejected: $($full.ArtifactEvidenceFailureReason)" }
    } elseif ($case -notin @('duplicate_effect','duplicate_b_effect','a_effect_for_b','missing_send_effect_with_terminal_execution') -and $full.ArtifactEvidenceVerdict -cne 'fail') { throw "$case malformed evidence was accepted." }
    $boundary=$full.FirstUnresolvedBoundary
    if($settled -and $exact) {
        if($null -ne $boundary -or @($full.Boundaries | Where-Object verdict -CEQ 'fail').Count -ne 0) {
            throw 'Genuine B DispatchObserved continuation with its own effects must close B and preserve completed A.'
        }
        foreach($id in @($commit.transitionId,$retirement.transitionId,$ownSettlement.transitionId)) {
            $clean=@($full.Boundaries | Where-Object boundaryId -CEQ $id)
            if($clean.Count -ne 1 -or $clean[0].verdict -cne 'pass' -or $clean[0].rawCount -ne 0 -or
                $clean[0].distinctCount -ne 0 -or $clean[0].recoveredCount -ne 0 -or $clean[0].unresolvedCount -ne 0) {
                throw 'A retirement and B closure must remain individually clean without borrowed recovery.'
            }
        }
    } elseif ($null -eq $boundary -or $boundary.recoveredCount -ne 0) { throw "$case must remain unresolved without counting A as recovered." }
    if($case -in @('terminal_execution_generation_with_effect','missing_send_effect_with_terminal_execution') -and
        @($full.Boundaries | Where-Object { $_.recoveredCount -ne 0 }).Count -ne 0) {
        throw "$case borrowed terminal-local execution evidence as recovery."
    }
    if (-not $settled -and $exact -and ($boundary.boundaryId -cne $commit.transitionId -or
        $boundary.taxonomyClass -cne 'runtime_unlock_retry_dispatch_contract_unsettled' -or
        $boundary.rawCount -ne 1 -or $boundary.distinctCount -ne 1 -or $boundary.unresolvedCount -ne 1 -or
        @($full.Boundaries | Where-Object verdict -CEQ 'fail').Count -ne 1 -or
        @($full.Boundaries | Where-Object { $_.boundaryId -ceq $retirement.transitionId -and $_.verdict -cne 'pass' }).Count -ne 0)) {
        throw 'Exact terminal observation must leave only B Installed/open at its original commit.'
    }
    if ($case -in @('missing_a_install','missing_b_install') -and $boundary.taxonomyClass -cne 'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete') {
        throw "$case lost its original installation requirement."
    }
    if($settled -and $case -in @('missing_b_execution','missing_b_settlement') -and
        ($boundary.boundaryId -cne $ownSettlement.transitionId -or
         $boundary.taxonomyClass -cne 'runtime_unlock_retry_dispatch_contract_unsettled' -or
         $boundary.rawCount -ne 1 -or $boundary.distinctCount -ne 1 -or $boundary.unresolvedCount -ne 1 -or
         @($full.Boundaries | Where-Object verdict -CEQ 'fail').Count -ne 1)) {
        throw "$case must select only B's own closure obligation; A's observed retirement cannot substitute."
    }
    if($case -ceq 'scope_aba' -and $full.ArtifactEvidenceFailureReason -match 'snapshot_operation_catalog_mismatch|snapshot_generation_or_hash_gap') {
        throw 'Scope ABA must reach the live role history guard with a continuous envelope chain.'
    }
    if($case -ceq 'late_only_no_history' -and
        ($full.FirstUnresolvedBoundary.variant -cne 'successful_retry_retirement_origin_missing' -or
         $full.ArtifactEvidenceFailureReason -match 'snapshot_operation_catalog_mismatch|snapshot_generation_or_hash_gap')) {
        throw 'A late-only stream must fail its missing authority origin, not local catalog or header continuity.'
    }
    if($case -in @('late_terminal_overwrite_restore','terminal_poison_aba') -and
        ($full.FirstUnresolvedBoundary.variant -cne 'provisional_retry_role_history_invalid' -or
         $full.ArtifactEvidenceFailureReason -match 'snapshot_operation_catalog_mismatch|snapshot_generation_or_hash_gap|snapshot_generation_transition_invalid' -or
         @($full.Boundaries | Where-Object { $_.recoveredCount -ne 0 }).Count -ne 0)) {
        throw 'Late terminal overwrite/restore must poison exact ownership without borrowed recovery or header masking.'
    }
    if($case -ceq 'missing_a_origin' -and $full.ArtifactEvidenceFailureReason -match 'snapshot_generation_or_hash_gap|snapshot_generation_transition_invalid') {
        throw 'Missing A installation origin must fail authority history without a header continuity gap.'
    }
    # A and B findings can share the same commit boundary. Exercise the exact
    # production observation predicate too, so aggregate counts cannot hide A.
    if($settled -or $exact -or $case -in @('missing_settlement','duplicate_effect','wrong_effect_receipt','wrong_effect_generation',
        'wrong_effect_authority','retained_effect','provisional_effect')) {
        $parsedEffects=@(foreach($line in @($lines | Select-Object -Unique)) {
            if((Get-FileTransferCoordinatorSnapshotEventName -Line $line) -cne 'filetransfer_coordinator_authority_effect'){continue}
            $effect=[ordered]@{}
            foreach($pair in @(@('CoordinatorInstanceId','coordinator_instance_id'),@('Writer','log_writer_id'),@('TransitionId','transition_id'),
                @('StateGeneration','coordinator_state_generation'),@('Family','operation_family'),@('OperationKey','operation_key'),
                @('AuthorityId','operation_authority_id'),@('OperationGeneration','operation_generation'),@('EffectKind','effect_kind'),
                @('ReceiptIdentity','receipt_identity'),@('ReceiptGeneration','receipt_generation'),@('ReceiptStatus','receipt_status'),@('ReceiptFingerprint','receipt_fingerprint'),
                @('Route','route'),@('ProtocolVersion','protocol_version'),@('LiveRouteEpoch','live_route_epoch'),@('RoutePublicationGeneration','route_publication_generation'),
                @('RouteCommitGeneration','route_commit_generation'),@('RuntimeGeneration','runtime_generation'),@('TerminalGeneration','terminal_generation'),
                @('LegGeneration','leg_generation'),@('TransportEpoch','transport_epoch'),@('BridgeRecoveryGeneration','bridge_recovery_generation'),
                @('CheckpointGeneration','checkpoint_generation'),@('CheckpointRequestId','checkpoint_request_id'),
                @('DataSessionIdentity','data_session_identity'),@('DataSessionGeneration','data_session_generation'))) {
                $effect[$pair[0]]=Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name $pair[1]
            }
            [pscustomobject]$effect
        })
        $terminal=[pscustomobject]@{Operation=$shape.Terminal;Boundary=[pscustomobject]@{writer='fixture-writer';
            coordinatorInstanceId=$retirement.coordinatorInstanceId;boundaryId=$retirement.transitionId;afterStateGeneration=$retirement.aggregate.afterStateGeneration}}
        $observed=Test-FileTransferCoordinatorSuccessfulRetryRetirementObservation -Terminal $terminal -Effects $parsedEffects
        $expectedAObservation=if($settled){$case -notin @('missing_settlement','duplicate_effect','wrong_effect_receipt',
            'wrong_effect_generation','wrong_effect_authority','a_effect_for_b')}else{$exact}
        if($observed -ne $expectedAObservation){throw "$case did not preserve the original A terminal-only observation requirement."}
        if($settled -and $exact) {
            if(@($parsedEffects | Where-Object { $_.AuthorityId -ceq $original.authorityId -and $_.EffectKind -ceq 'settlement_observed' }).Count -ne 1) {
                throw 'A retirement must be observed exactly once at its original terminal entry, never at eviction.'
            }
            $bEffects=@($parsedEffects | Where-Object AuthorityId -CEQ $successor.authorityId)
            foreach($required in @(@('install_committed',1),@('execution_observed',2),@('settlement_observed',1))) {
                if(@($bEffects | Where-Object EffectKind -CEQ $required[0]).Count -ne $required[1]) {
                    throw 'B requires its own exact installation, alias/send execution and settlement observations.'
                }
            }
            foreach($effect in $bEffects) {
                # Use the immutable production fixture: descriptor-order and exact
                # duplicates do not change effect authority or receipt binding.
                $source=@($envelopes | Where-Object transitionId -CEQ $effect.TransitionId)
                $operation=@($source[0].aggregate.operations | Where-Object operationKey -CEQ $effect.OperationKey)
                $effectBoundary=[pscustomobject]@{writer='fixture-writer';coordinatorInstanceId=$source[0].coordinatorInstanceId;
                    boundaryId=$source[0].transitionId;afterStateGeneration=$source[0].aggregate.afterStateGeneration}
                if($source.Count -ne 1 -or $operation.Count -ne 1 -or
                    -not (Test-FileTransferCoordinatorSnapshotExactEffectBinding -Boundary $effectBoundary -Operation $operation[0] -Effect $effect) -or
                    @($operation[0].runtimeUnlockRetry.receipts | Where-Object {
                        $_.receiptIdentity -ceq $effect.ReceiptIdentity -and $_.receiptGeneration -eq $effect.ReceiptGeneration -and
                        $_.receiptStatus -ceq $effect.ReceiptStatus -and $_.receiptFingerprint -ceq $effect.ReceiptFingerprint
                    }).Count -ne 1) { throw 'B observation borrowed an authority, generation or receipt from another operation.' }
            }
        }
    }
    $metadata=Metadata $boundary
    if (($metadata | ConvertTo-Json -Compress) -cne ((Metadata $compact.FirstUnresolvedBoundary) | ConvertTo-Json -Compress)) { throw "$case selected metadata mismatch." }
    $guiShadowFallback=$null;$guiShadowFallbackJson=$null
    foreach ($form in @('full','compact')) {
        $path=Join-Path $OutputRoot "successful-$case-$form.log"
        $inputLines=if($form -ceq 'full'){@('event=unrelated')+$lines}else{$compactLines}
        [IO.File]::WriteAllLines($path,[string[]]$inputLines)
        $environmentBefore=[ordered]@{}
        if($settled) {
            foreach($name in @('LOCALAPPDATA','NLINK_FILETRANSFER_GUI_SCENARIO_DEADLINE_REQUEST_PATH','NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR')) {
                $environmentBefore[$name]=[Environment]::GetEnvironmentVariable($name,'Process')
            }
        }
        try {
            if($settled) {
                [Environment]::SetEnvironmentVariable('LOCALAPPDATA',$emptyAppData,'Process')
                [Environment]::SetEnvironmentVariable('NLINK_FILETRANSFER_GUI_SCENARIO_DEADLINE_REQUEST_PATH',$null,'Process')
                [Environment]::SetEnvironmentVariable('NLINK_FILETRANSFER_SOAK_ARTIFACT_DIR',$OutputRoot,'Process')
                $script:GuiSmokeAppLogLedger=New-GuiSmokeLogLedgerState
            }
            $gui=Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'receipt-backed successful retirement unit fixture' `
                -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
        } finally {
            foreach($name in $environmentBefore.Keys){[Environment]::SetEnvironmentVariable($name,$environmentBefore[$name],'Process')}
        }
        if($settled -and $null -eq $boundary) {
            # Family-2-only Shadow has no selected Coordinator failure. Compare
            # the actual coarse fallback; this is not an authoritative GUI pass.
            if($null -ne $gui.PSObject.Properties['CoordinatorBoundaryId'] -or -not $gui.ClassificationComplete -or
                $gui.ClassificationSource -cne 'immutable_evidence'){throw "$case $form selected a spurious GUI Coordinator failure."}
            $fallback=[ordered]@{}
            foreach($property in @($gui.PSObject.Properties | Sort-Object Name)) {
                if($property.Name -notin @('EvidenceLineVisitCount','EvidenceParsedRecordCount','EvidenceLookupCount','ClassificationElapsedMilliseconds')) {
                    $fallback[$property.Name]=$property.Value
                }
            }
            $fallbackJson=$fallback | ConvertTo-Json -Depth 8 -Compress
            if($null -ne $guiShadowFallbackJson -and $guiShadowFallbackJson -cne $fallbackJson){throw "$case full/compact GUI fallback mismatch."}
            $guiShadowFallback=$fallback;$guiShadowFallbackJson=$fallbackJson
            continue
        }
        if ($gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or $gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or
            $gui.CoordinatorWriter -cne $boundary.writer -or $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or
            $gui.FailureClass -cne $boundary.taxonomyClass -or $gui.Phase -cne $boundary.phase -or
            $gui.CoordinatorRawCount -ne $boundary.rawCount -or $gui.CoordinatorDistinctCount -ne $boundary.distinctCount -or
            $gui.CoordinatorRecoveredCount -ne $boundary.recoveredCount -or $gui.CoordinatorUnresolvedCount -ne $boundary.unresolvedCount) { throw "$case actual GUI metadata mismatch." }
    }
    $summary=[ordered]@{Case=$case;Artifact=$full.ArtifactEvidenceVerdict;BoundarySetHash=$full.BoundaryHash;Selected=$metadata}
    if($settled){$summary['GuiShadowFallback']=$guiShadowFallback}
    $summaries+=$summary
}
Write-Output ('retry-confirmation-compensation-normalized='+($summaries | ConvertTo-Json -Depth 16 -Compress))
