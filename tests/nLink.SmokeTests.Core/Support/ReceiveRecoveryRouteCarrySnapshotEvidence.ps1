param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') { throw 'Frozen catalog changed.' }
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
$first = $envelopes[0]
$prefix = "snapshot_contract_version=1; snapshot_selection_mode=SnapshotV1Authoritative; snapshot_catalog_version=$($catalog.Version); snapshot_catalog_hash=$($catalog.Hash); coordinator_instance_id=$($first.coordinatorInstanceId); session_id=$($first.sessionId); transfer_id=$($first.transferId); direction=$($first.direction)"
$families = $catalog.RequiredFamilies -join ','
$coverage = "emitted_family_coverage=$families; shadow_family_coverage=; authoritative_family_coverage=$families"
function Import-Definitions([string]$path, [string[]]$names) {
    $ast = [Management.Automation.Language.Parser]::ParseFile($path,[ref]$null,[ref]$null)
    foreach ($name in $names) {
        $definitions = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name },$true))
        if ($definitions.Count -ne 1) { throw "Function not unique: $name" }
        . ([scriptblock]::Create(($definitions[0].Extent.Text -replace "function $name", "function script:$name")))
    }
}
Import-Definitions (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/FileTunaPressureSettlementSnapshotEvidence.ps1') @('New-Effect','New-Lines')
Import-Definitions (Join-Path $RepoRoot 'tools/GuiSmoke-Windows.ps1') @('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification','Get-TunaGuiFileTransferSetupFailureClassification')
$script:NewRouteLines = (Get-Command New-Lines).ScriptBlock
function Copy-Value($value) { $value | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json }
function Subject($envelope, [string]$role) {
    $matches = @($envelope.aggregate.operations | Where-Object { $_.family -ceq 'route_transition' -and $_.operationRole -ceq $role })
    if ($matches.Count -ne 1) { throw "Retained route subject not unique: $role" }
    $matches[0]
}
function Recovery-Subject($envelope, [string]$role) {
    $matches = @($envelope.aggregate.operations | Where-Object { $_.family -ceq 'receive_recovery' -and $_.operationRole -ceq $role })
    if ($matches.Count -ne 1) { throw "Recovery subject not unique: $role" }
    $matches[0]
}
function New-Lines([object[]]$values, [string]$extraKind = '', [string]$extraRole = 'terminal') {
    $byId = @{}
    foreach ($e in $values) { $byId[$e.transitionId] = $e }
    foreach ($line in @(& $script:NewRouteLines $values)) {
        $line
        if ((Get-FileTransferCoordinatorSnapshotEventName $line) -cne 'filetransfer_coordinator_transition_snapshot') { continue }
        $e = $byId[(Get-FileTransferCoordinatorSnapshotLineField $line transition_id)]
        $sequence = [long](Get-FileTransferCoordinatorSnapshotLineField $line log_writer_sequence)
        $kind = switch -Exact ($e.decisionKind) {
            'ObserveReceiveRecoveryExecutionReceipt' { 'execution_receipt_observed' }
            'ObserveReceiveRecoveryProof' { 'receive_proof_observed' }
        }
        if ($kind) {
            $operations = @($e.aggregate.operations | Where-Object {
                $_.family -ceq 'receive_recovery' -and $_.operationRole -in @('operation_current','operation_terminal') -and $_.afterState -cne 'None'
            })
            if ($operations.Count -ne 1) { throw 'Recovery effect subject not unique.' }
            New-Effect $e $operations[0] $kind ($sequence + 1)
        }
        if ($extraKind -and $e.decisionKind -ceq 'IssueReceiveRecoveryOperation') {
            New-Effect $e (Subject $e $extraRole) $extraKind ($sequence + 2)
        }
    }
}
function Semantic-Failures($evaluation) {
    @($evaluation.Boundaries | Where-Object { $_.unresolvedCount -gt 0 -and $_.taxonomyClass -ceq 'filetransfer_route_transition_incomplete' } | ForEach-Object {
        "$($_.boundaryId)|$($_.taxonomyClass)|$($_.rawCount)|$($_.distinctCount)|$($_.recoveredCount)|$($_.unresolvedCount)"
    } | Sort-Object) -join ';'
}
function Assert-Parity([string[]]$lines, [bool]$valid, [string]$reason = '', [string]$context = 'fixture') {
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated') + $lines)
    $compactLines = @($lines | Where-Object {
        $event = Get-FileTransferCoordinatorSnapshotEventName $_
        $catalog.SnapshotAuthorityV1 -contains $event -or $catalog.GenerationStampedEffects -contains $event
    })
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        $full.LegacySelectionCount -ne 0 -or ($full.ArtifactEvidenceVerdict -ceq 'pass') -ne $valid -or
        (-not $valid -and $full.ArtifactEvidenceFailureReason -notlike "*$reason*" -and $full.FirstUnresolvedBoundary.variant -cne $reason)) {
        $details = @($full.ArtifactEvidenceFailureReason -split ';' | Where-Object { $_ -match '^snapshot_operation_catalog_mismatch:(\d+)$' } | ForEach-Object {
            $line = (@('event=unrelated') + $lines)[[int]($_ -replace '^.*:', '')]
            $e = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $line snapshot_payload)).Value
            "candidate=$($e.decisionKind)"
            foreach ($op in $e.aggregate.operations) { foreach ($family in 1..6) {
                if (-not (& "Test-FileTransferCoordinatorSnapshotFamily${family}OperationCatalog" $e $op)) {
                    "$($e.decisionKind):family$family/$($op.family)/$($op.operationRole)/$($op.beforeState)->$($op.afterState)"
                }
            } }
        })
        throw "Shared receive-recovery carry failed: expected=$valid/$reason; actual=$($full.ArtifactEvidenceFailureReason); variant=$($full.FirstUnresolvedBoundary.variant); details=$($details -join ',')"
    }
    if ($valid -and (Semantic-Failures $full) -cne $script:BaselineSemanticFailures) {
        throw "Recovery carry changed the independent route control ($context): expected=$script:BaselineSemanticFailures; actual=$(Semantic-Failures $full)"
    }
    if ($valid) {
        $otherFailures = @($full.Boundaries | Where-Object {
            $_.unresolvedCount -gt 0 -and $_.taxonomyClass -cne 'filetransfer_route_transition_incomplete'
        })
        if ($otherFailures.Count -ne 0) { throw "Unexpected independent semantic finding ($context)." }
    }
    $path = Join-Path $OutputRoot 'compact.log'
    [IO.File]::WriteAllLines($path,$compactLines)
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
    $boundary = $full.FirstUnresolvedBoundary
    if ($null -eq $boundary) {
        if ($null -ne $gui) { throw 'GUI invented a boundary.' }
        return
    }
    if ($gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or $gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or
        $gui.FailureClass -cne $boundary.taxonomyClass -or
        $gui.FailureVariant -cne (Get-FileTransferCoordinatorSnapshotObjectProperty $boundary variant '(none)') -or
        $gui.CoordinatorWriter -cne $boundary.writer -or $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or
        $gui.CoordinatorRawCount -ne $boundary.rawCount -or $gui.CoordinatorDistinctCount -ne $boundary.distinctCount -or
        $gui.CoordinatorRecoveredCount -ne $boundary.recoveredCount -or $gui.CoordinatorUnresolvedCount -ne $boundary.unresolvedCount) {
        throw 'GUI receive-recovery carry boundary differs.'
    }
}
$carryKinds = @('IssueReceiveRecoveryOperation','ObserveReceiveRecoveryProof')
$carries = @($envelopes | Where-Object decisionKind -In $carryKinds)
if ($carries.Count -ne 2) { throw 'Expected exact recovery issue and proof carries.' }
$script:ProofTransitionId = [string](@($carries | Where-Object decisionKind -CEQ 'ObserveReceiveRecoveryProof')[0].transitionId)
foreach ($e in $carries) {
    foreach ($role in @('fallback_v6_bootstrap','terminal')) {
        if (-not (Test-FileTransferCoordinatorSnapshotFamily1OperationCatalog $e (Subject $e $role))) {
            throw "Production receive-recovery route carry rejected: $($e.decisionKind)/$role"
        }
    }
}
foreach ($e in $carries) {
    foreach ($role in @('fallback_v6_bootstrap','terminal')) {
        foreach ($mutation in @(
            { param($e,$o) $o.operationRole='current' },
            { param($e,$o) $o.operationKind='PostTunaFallbackV6ToTunaV4' },
            { param($e,$o) $o.operationKey+='-changed' },
            { param($e,$o) $o.authorityId='(none)' },
            { param($e,$o) $o.authorityId+='-changed' },
            { param($e,$o) $o.operationGeneration++ },
            { param($e,$o) $o.beforeState='None' },
            { param($e,$o) $o.afterState='None' },
            { param($e,$o) $o.stateApplied=$false },
            { param($e,$o) $o.stateApplied='True' },
            { param($e,$o) $o.receiptValidated=$false },
            { param($e,$o) $o.receiptValidated='True' },
            { param($e,$o) $o.commandIdentity='wrong' },
            { param($e,$o) $o.receiptFingerprint='wrong' },
            { param($e,$o) $o.routeTransition.role='current' },
            { param($e,$o) $o.routeTransition.targetRoute='file_tuna_v6' },
            { param($e,$o) $o.routeTransition.targetProtocolVersion=4 },
            { param($e,$o) $o.routeTransition.targetBoundary.bridgeRecoveryGeneration++ },
            { param($e,$o) $o.routeTransition.receipts=@() },
            { param($e,$o) $o.routeTransition.receipts+=Copy-Value $o.routeTransition.receipts[-1] },
            { param($e,$o) $o.routeTransition.receipts[-1].receiptValidated=$false },
            { param($e,$o) $e.aggregate.operations+=Copy-Value $o },
            { param($e,$o) $e.decisionKind='unrelated' },
            { param($e,$o) $e.decisionStatus='Rejected' },
            { param($e,$o) $e.decisionStatus='Idempotent' },
            { param($e,$o) $e.aggregate.afterStateGeneration++ },
            { param($e,$o) $e.aggregate.afterStateFingerprint=$e.aggregate.beforeStateFingerprint },
            { param($e,$o) $e.aggregate.afterBoundary.runtimeGeneration++ },
            { param($e,$o) $e.aggregate.afterBoundary.terminalGeneration++ },
            { param($e,$o) $e.aggregate.afterBoundary.transportEpoch++ },
            { param($e,$o) $e.aggregate.afterBoundary.bridgeRecoveryGeneration=$e.aggregate.beforeBoundary.bridgeRecoveryGeneration },
            { param($e,$o) $e.aggregate.afterBoundary.dataSessionIdentity+='-replacement' }
        )) {
            $bad = Copy-Value $e
            $op = Subject $bad $role
            & $mutation $bad $op
            if (Test-FileTransferCoordinatorSnapshotReceiveRecoveryRouteCarry $bad $op) { throw "Malformed carry admitted: $role/$mutation" }
        }
    }
}
foreach ($e in $carries) {
    foreach ($mutation in @(
        { param($e,$o) $e.aggregate.operations=@($e.aggregate.operations | Where-Object operationRole -CNE 'operation_current') },
        { param($e,$o) $e.aggregate.operations+=Copy-Value $o },
        { param($e,$o) $o.operationKind='RegularV4FrontierRepairPressure' },
        { param($e,$o) $o.authorityId+='-foreign' },
        { param($e,$o) $o.operationGeneration++ },
        { param($e,$o) $o.commandGeneration++ },
        { param($e,$o) $o.commandIdentity+='-foreign' },
        { param($e,$o) $o.commandFingerprint=if ($e.decisionKind -ceq 'IssueReceiveRecoveryOperation') { '(none)' } else { "$($o.commandFingerprint)-foreign" } },
        { param($e,$o) $o.receiveRecovery.bridgeDecisionGeneration++ },
        { param($e,$o) $o.receiveRecovery.sourceBoundary.transportEpoch++ },
        { param($e,$o) $o.receiveRecovery.executionState=if ($e.decisionKind -ceq 'IssueReceiveRecoveryOperation') { 'Completed' } else { 'Issued' } },
        { param($e,$o) $o.receiveRecovery.closureState='Consumed' },
        { param($e,$o) $o.stateApplied=$false },
        { param($e,$o) $o.receiptFingerprint+='-foreign' }
    )) {
        $bad = Copy-Value $e
        & $mutation $bad (Recovery-Subject $bad 'operation_current')
        if (Test-FileTransferCoordinatorSnapshotReceiveRecoveryRouteCarry $bad (Subject $bad 'terminal')) {
            throw "Malformed current recovery admitted: $($e.decisionKind)/$mutation"
        }
    }
}
foreach ($mutation in @(
    { param($e,$o) $e.aggregate.operations=@($e.aggregate.operations | Where-Object operationRole -CNE 'operation_terminal') },
    { param($e,$o) $e.aggregate.operations+=Copy-Value $o },
    { param($e,$o) $o.authorityId+='-foreign' },
    { param($e,$o) $o.operationGeneration++ },
    { param($e,$o) $o.receiveRecovery.bridgeDecisionGeneration++ },
    { param($e,$o) $o.receiveRecovery.proofIdentity='(none)' },
    { param($e,$o) $o.receiveRecovery.proofObservedUtcMs=0 },
    { param($e,$o) $o.receiptStatus='Applied' },
    { param($e,$o) $o.receiveRecovery.receipts[0].commandFingerprint+='-foreign' }
)) {
    $bad = Copy-Value (@($carries | Where-Object decisionKind -CEQ 'ObserveReceiveRecoveryProof')[0])
    & $mutation $bad (Recovery-Subject $bad 'operation_terminal')
    if (Test-FileTransferCoordinatorSnapshotReceiveRecoveryRouteCarry $bad (Subject $bad 'terminal')) {
        throw "Malformed proof subject admitted: $mutation"
    }
}
foreach ($mutation in @(
    { param($e,$o) $o.authorityId+='-foreign' },
    { param($e,$o) $o.operationGeneration++ },
    { param($e,$o) $o.stateApplied=$true },
    { param($e,$o) $o.receiveRecovery.receipts[0].receiptStatus='Rejected' }
)) {
    $bad = Copy-Value (@($carries | Where-Object decisionKind -CEQ 'IssueReceiveRecoveryOperation')[0])
    & $mutation $bad (Recovery-Subject $bad 'attempted_decision')
    if (Test-FileTransferCoordinatorSnapshotReceiveRecoveryRouteCarry $bad (Subject $bad 'terminal')) {
        throw "Malformed issued attempt subject admitted: $mutation"
    }
}
$issueIndex = 0
while ($envelopes[$issueIndex].decisionKind -cne 'IssueReceiveRecoveryOperation') { $issueIndex++ }
$checkpointBefore = Copy-Value $envelopes[$issueIndex-1].aggregate.afterBoundary
$targetBefore = Copy-Value (Subject $envelopes[$issueIndex-1] 'terminal').routeTransition.targetBoundary
foreach ($scope in @($checkpointBefore,$targetBefore)) {
    $scope.checkpointGeneration=0
    $scope.checkpointRequestId='(none)'
}
$checkpointRequested = Copy-Value $checkpointBefore
$checkpointRequested.checkpointGeneration=7
$checkpointRequested.checkpointRequestId='fte1-projected-checkpoint-7'
$targetRequested = Copy-Value $targetBefore
$targetRequested.checkpointGeneration=7
$targetRequested.checkpointRequestId='raw-checkpoint-request-7'
if (-not (Test-FileTransferCoordinatorReceiveRecoveryRouteTargetCarry $targetBefore $targetRequested $checkpointBefore $checkpointRequested 'terminal' 'FallbackCheckpointRequested') -or
    -not (Test-FileTransferCoordinatorReceiveRecoveryRouteTargetCarry $targetRequested $targetBefore $checkpointRequested $checkpointBefore 'terminal' 'FallbackCheckpointAccepted')) {
    throw 'Exact raw checkpoint target request/accept history rejected.'
}
$changedRequest = Copy-Value $targetRequested
$changedRequest.checkpointRequestId='another-raw-request'
if ((Test-FileTransferCoordinatorReceiveRecoveryRouteTargetCarry $targetRequested $changedRequest $checkpointRequested $checkpointRequested 'terminal' 'FallbackCheckpointRequested') -or
    (Test-FileTransferCoordinatorReceiveRecoveryRouteTargetCarry $targetBefore $targetRequested $checkpointBefore $checkpointRequested 'terminal' 'IssueReceiveRecoveryOperation') -or
    (Test-FileTransferCoordinatorReceiveRecoveryRouteTargetCarry $targetBefore $targetRequested $checkpointBefore $checkpointRequested 'fallback_v6_bootstrap' 'FallbackCheckpointRequested')) {
    throw 'Unrelated or bootstrap checkpoint target mutation admitted.'
}
$baseline = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (New-Lines $envelopes[0..($issueIndex-1)])
if ($baseline.ArtifactEvidenceVerdict -cne 'pass') { throw "Original route history malformed: $($baseline.ArtifactEvidenceFailureReason)" }
if ($null -ne $baseline.FirstUnresolvedBoundary) { throw 'The completed retained-frame route must be closed before recovery carries.' }
$script:BaselineSemanticFailures = Semantic-Failures $baseline
$lines = @(New-Lines $envelopes)
Assert-Parity $lines $true '' 'original'
$shuffled = Copy-Value $envelopes
foreach ($e in $shuffled) {
    $routeOperations = @($e.aggregate.operations | Where-Object family -CEQ 'route_transition')
    [array]::Reverse($routeOperations)
    $nextRoute = 0
    $e.aggregate.operations = @($e.aggregate.operations | ForEach-Object {
        if ($_.family -ceq 'route_transition') { $routeOperations[$nextRoute]; $nextRoute++ } else { $_ }
    })
}
Assert-Parity (New-Lines $shuffled) $true '' 'operation-order'
Assert-Parity (@($lines[0]) + $lines[1..($lines.Count-2)] + $lines[1..($lines.Count-2)] + @($lines[-1])) $true '' 'duplicate-records'
Assert-Parity @($lines | Where-Object { -not $_.Contains('effect_kind=receive_proof_observed;') }) $false 'snapshot_receive_recovery_required_effect_missing'
Assert-Parity @($lines | Where-Object { -not $_.Contains('effect_kind=execution_receipt_observed;') }) $false 'snapshot_receive_recovery_required_effect_missing'
$values = Copy-Value $envelopes
(Recovery-Subject $values[$issueIndex] 'operation_current').commandFingerprint+='-foreign'
Assert-Parity (New-Lines $values) $false 'receive_recovery_route_terminal_carry_history_invalid'
foreach ($mutation in @(
    { param($e) (Subject $e 'terminal').stateApplied=$false },
    { param($e) (Subject $e 'fallback_v6_bootstrap').authorityId+='-foreign' },
    { param($e) $e.aggregate.afterBoundary.runtimeGeneration++ }
)) {
    $values = Copy-Value $envelopes
    & $mutation $values[$issueIndex]
    Assert-Parity (New-Lines $values) $false 'receive_recovery_bridge_scope_carry_omitted_from_family1_catalog'
}
foreach ($role in @('fallback_v6_bootstrap','terminal')) {
    foreach ($mutation in @(
        { param($o) $o.routeTransition.proofBoundaryFingerprint+='-changed' },
        { param($o) $o.routeTransition.durableCheckpointFingerprint+='-changed' },
        { param($o) $o.routeTransition.targetBoundary.checkpointGeneration++ },
        { param($o) $o.routeTransition.targetBoundary.transportEpoch++ },
        { param($o) $o.routeTransition.targetBoundary.dataSessionIdentity+='-replacement' },
        { param($o) $o.receiptFingerprint+='-changed'; $o.routeTransition.receipts[-1].receiptFingerprint=$o.receiptFingerprint },
        { param($o) $o.commandFingerprint+='-changed'; $o.routeTransition.receipts[-1].commandFingerprint=$o.commandFingerprint }
    )) {
        $values = Copy-Value $envelopes
        $candidate = $values[$issueIndex]
        & $mutation (Subject $candidate $role)
        Assert-Parity (New-Lines $values) $false 'receive_recovery_route_terminal_carry_history_invalid'
    }
    $values = Copy-Value $envelopes
    for ($i=0; $i -lt $issueIndex; $i++) {
        $values[$i].aggregate.operations = @($values[$i].aggregate.operations | Where-Object {
            -not ($_.family -ceq 'route_transition' -and $_.operationRole -ceq $role)
        })
    }
    Assert-Parity (New-Lines $values) $false 'receive_recovery_route_terminal_carry_history_invalid'
    foreach ($kind in @('route_commit_adopted','target_runtime_leg_installed','retained_frame_applied','route_transition_disposition_applied')) {
        Assert-Parity (New-Lines $envelopes $kind $role) $false 'receive_recovery_route_terminal_carry_effect_invalid'
    }
}
function History-Entries($values) {
    for ($i=0; $i -lt $values.Count; $i++) {
        $e = $values[$i]
        [pscustomobject]@{
            Boundary=[pscustomobject]@{
                coordinatorInstanceId=$e.coordinatorInstanceId; writer='fixture-writer'; boundaryId=$e.transitionId
                beforeStateGeneration=$e.aggregate.beforeStateGeneration; afterStateGeneration=$e.aggregate.afterStateGeneration
                beforeStateHash=$e.aggregate.beforeStateFingerprint; afterStateHash=$e.aggregate.afterStateFingerprint
                decisionKind=$e.decisionKind; decisionStatus=$e.decisionStatus; decisionReason=$e.decisionReason
                operations=$e.aggregate.operations; evidenceLineIndex=$i
            }
            BeforeBoundary=Copy-Value $e.aggregate.beforeBoundary; AfterBoundary=Copy-Value $e.aggregate.afterBoundary
            SessionId=$e.sessionId; TransferId=$e.transferId; Direction=$e.direction
        }
    }
}
function Resolve-History($entries, $invalid = @{}) {
    $candidates = @{}
    foreach ($entry in $entries) {
        if ($entry.Boundary.decisionKind -in $carryKinds) {
            $candidates[$entry.Boundary.boundaryId] = @{ Line=$entry.Boundary.evidenceLineIndex }
        }
    }
    Resolve-FileTransferCoordinatorReceiveRecoveryRouteCarries (New-FileTransferCoordinatorSnapshotAggregateIntervalIndex $entries) $candidates $invalid @()
}
if (@(Resolve-History @(History-Entries (Copy-Value $envelopes))).Count -ne 0) { throw 'Exact carry history rejected.' }
foreach ($mutation in @('gap','hash','terminal','data-session','transport','transfer','direction','retirement','aba','ambiguous','invalid-predecessor','target-identity-propagated')) {
    $entries = @(History-Entries (Copy-Value $envelopes))
    $prior = $entries[$issueIndex-1]
    $terminal = @($prior.Boundary.operations | Where-Object { $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'terminal' })[0]
    $invalid = @{}
    switch ($mutation) {
        'gap' { $prior.Boundary.beforeStateGeneration++ }
        'hash' { $prior.Boundary.beforeStateHash+='-changed' }
        'terminal' { $prior.AfterBoundary.terminalGeneration++ }
        'data-session' { $prior.AfterBoundary.dataSessionIdentity+='-replacement' }
        'transport' { $prior.AfterBoundary.transportEpoch++ }
        'transfer' { $prior.TransferId+='-replacement' }
        'direction' { $prior.Direction=if ($prior.Direction -ceq 'inbound') { 'outbound' } else { 'inbound' } }
        'retirement' { $terminal.afterState='None' }
        'aba' {
            $terminal.authorityId='replacement-B'
            $terminal.operationKey="route_transition|terminal|replacement-B|$($terminal.operationGeneration)|$($terminal.operationKind)"
            $terminal.beforeState='None'
        }
        'ambiguous' { $prior.Boundary.operations+=Copy-Value $terminal }
        'invalid-predecessor' { $invalid[$prior.Boundary.boundaryId]=$true }
        'target-identity-propagated' {
            foreach ($entry in $entries[($issueIndex-1)..($entries.Count-1)]) {
                foreach ($operation in @($entry.Boundary.operations | Where-Object { $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'terminal' })) {
                    $operation.routeTransition.targetBoundary.dataSessionIdentity='forged-retained-target'
                }
            }
        }
    }
    $failures = @(Resolve-History $entries $invalid)
    if ($failures.Count -eq 0 -or @($failures | Where-Object { $_ -like '*receive_recovery_route_terminal_carry_history_invalid*' }).Count -eq 0) {
        throw "Poisoned retained history accepted: $mutation"
    }
}
Write-Output 'Receive-recovery route carry, immutable terminal history, no new route effects and full/compact/GUI parity passed.'
