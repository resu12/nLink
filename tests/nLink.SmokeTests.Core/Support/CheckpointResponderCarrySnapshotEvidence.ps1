param([string]$RepoRoot, [string]$FixturePath, [string]$OutputRoot)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog
if ($catalog.Hash -cne '17b5e4a90a18a5f4173ebd1b2081aeee1db48773b0213f1bb45549e0a0f057e2') { throw 'Frozen catalog changed.' }
$envelopes = Get-Content -LiteralPath $FixturePath -Raw | ConvertFrom-Json
if (@($envelopes.aggregate.operations | Where-Object { Test-FileTransferCoordinatorCheckpointRepairContinuityMarker $_ }).Count -gt 0) {
    foreach ($envelope in $envelopes) {
        foreach ($operation in @($envelope.aggregate.operations | Where-Object { Test-FileTransferCoordinatorCheckpointRepairContinuityMarker $_ })) {
            if (Test-FileTransferCoordinatorCheckpointRepairContinuityOperation $envelope $operation $catalog) {
                throw 'Base catalog accepted unsupported checkpoint continuity.'
            }
        }
    }
    $catalog = Get-FileTransferCoordinatorCheckpointRepairContinuityCatalog
    if ($catalog.Hash -cne 'e6c3b5ff7cf8b44bcde0782ec325d2623cb96ba8ff362edca1902f9a1929059d') { throw 'Frozen continuity catalog changed.' }
}
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
function Copy-Value($value) { $value | ConvertTo-Json -Depth 64 -Compress | ConvertFrom-Json }
function Subject($envelope) {
    $matches = @($envelope.aggregate.operations | Where-Object { $_.operationRole -ceq 'handoff_terminal' -and $_.operationKind -ceq 'ResponderPeerProbeRouteTransitionHandoff' })
    if ($matches.Count -ne 1) { throw 'Terminal subject not unique.' }
    $matches[0]
}
function Assert-Parity([string[]]$lines, [bool]$valid, [string]$reason = '') {
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines (@('event=unrelated') + $lines)
    $compactLines = @($lines | Where-Object {
        $event = Get-FileTransferCoordinatorSnapshotEventName $_
        $catalog.SnapshotAuthorityV1 -contains $event -or $catalog.GenerationStampedEffects -contains $event
    })
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $compactLines
    if ((Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
        ($full.ArtifactEvidenceVerdict -ceq 'pass') -ne $valid -or
        (-not $valid -and $full.ArtifactEvidenceFailureReason -notlike "*$reason*" -and $full.FirstUnresolvedBoundary.variant -cne $reason)) {
        $catalogDetails = @($full.ArtifactEvidenceFailureReason -split ';' | Where-Object { $_ -match '^snapshot_operation_catalog_mismatch:(\d+)$' } | ForEach-Object {
            $line = (@('event=unrelated')+$lines)[[int]($_ -replace '^.*:', '')]
            $e = (ConvertFrom-FileTransferCoordinatorSnapshotPayload (Get-FileTransferCoordinatorSnapshotLineField $line snapshot_payload)).Value
            "candidate=$($e.decisionKind)"
            foreach($o in $e.aggregate.operations) { foreach($family in 1..6) {
                if(-not (& "Test-FileTransferCoordinatorSnapshotFamily${family}OperationCatalog" -Envelope $e -Operation $o)) { "$($e.decisionKind):$($o.family):$($o.operationRole):$($o.beforeState)->$($o.afterState)" }
            } }
        })
        throw "Shared carry failed: $($full.ArtifactEvidenceFailureReason); variant=$($full.FirstUnresolvedBoundary.variant); expected=$reason; details=$($catalogDetails -join ',')"
    }
    if ($valid) {
        foreach ($failure in @($full.Boundaries | Where-Object unresolvedCount -gt 0)) {
            # Preserve the independently known retained-frame route reducer control.
            if ($failure.taxonomyClass -cne 'filetransfer_route_transition_incomplete' -or
                @($failure.operations | Where-Object { $_.family -ceq 'route_transition' -and $_.operationKind -ceq 'TunaV4ToPostTunaFallbackV6' }).Count -eq 0) {
                throw "Unexpected semantic failure: $($failure.taxonomyClass)"
            }
        }
    }
    $boundary = $full.FirstUnresolvedBoundary
    if ($valid) {
        $route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
        $compactRoute = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $compact
        if ((Compare-FileTransferCoordinatorSnapshotRouteConsistency $route $compactRoute).Verdict -cne 'pass') {
            throw 'Checkpoint fixture route-result parity differs.'
        }
        # This fixture deliberately begins after Tuna publication. Its first
        # retained publication is therefore not an initial route publication.
        $publications = @($envelopes.aggregate.operations | Where-Object {
            $_.family -ceq 'route_runtime' -and $_.operationRole -ceq 'route_publication' -and
            $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Published'
        })
        if ($publications.Count -ne 1 -or $publications[0].operationGeneration -le 1) { throw 'Fixture route entry changed.' }
        $scope = "$($first.coordinatorInstanceId)|fixture-writer|$($first.sessionId)|$($first.transferId)|$($first.direction)"
        $expectedFindings = @(
            "snapshot route publication generation is not contiguous: scope=$scope; generation=$($publications[0].operationGeneration)"
        )
        $routeBoundary = $route.FirstUnresolvedBoundary
        if ($route.Verdict -cne 'fail' -or (@($route.Findings) -join "`n") -cne ($expectedFindings -join "`n") -or
            $null -eq $routeBoundary -or $routeBoundary.taxonomyClass -cne 'filetransfer_route_consistency_analysis_parity_invalid' -or
            $routeBoundary.variant -cne 'snapshot_authoritative_route_boundary_invalid' -or
            $routeBoundary.writer -cne 'fixture-writer' -or $routeBoundary.writerSequence -ne 60) {
            throw "Unexpected checkpoint fixture route finding: $(ConvertTo-Json -InputObject $route -Depth 12 -Compress)"
        }
        $boundary = $routeBoundary
    }
    $path = Join-Path $OutputRoot 'compact.log'; [IO.File]::WriteAllLines($path,$compactLines)
    $gui = Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
    if ($null -eq $boundary) { if ($null -ne $gui) { throw "GUI invented a boundary: $(ConvertTo-Json -InputObject $gui -Depth 8 -Compress)" }; return }
    if ($gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or $gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or
        $gui.CoordinatorWriter -cne $boundary.writer -or $gui.CoordinatorWriterSequence -ne $boundary.writerSequence -or
        $gui.CoordinatorRawCount -ne $boundary.rawCount -or $gui.CoordinatorDistinctCount -ne $boundary.distinctCount -or
        $gui.CoordinatorRecoveredCount -ne $boundary.recoveredCount -or $gui.CoordinatorUnresolvedCount -ne $boundary.unresolvedCount) { throw 'GUI carry boundary differs.' }
}
$requests = @($envelopes | Where-Object decisionKind -CEQ 'FallbackCheckpointRequested')
if ($requests.Count -ne 1) { throw 'Request not unique.' }
$request = $requests[0]
foreach ($e in @($envelopes | Where-Object { $_.decisionKind -in @('FallbackCheckpointRequested','FallbackCheckpointAccepted') })) {
    if (-not (Test-FileTransferCoordinatorSnapshotCheckpointResponderCarry $e (Subject $e)) -or
        -not (Test-FileTransferCoordinatorSnapshotFamily6OperationCatalog $e (Subject $e))) { throw 'Production carry rejected.' }
}
foreach ($mutation in @(
    { param($e,$o) $o.operationRole='handoff_current' },
    { param($e,$o) $o.operationKind='InitialActivation' },
    { param($e,$o) $o.operationKey+='-mutated' },
    { param($e,$o) $o.authorityId='(none)' },
    { param($e,$o) $o.operationGeneration++ },
    { param($e,$o) $o.beforeState='None' },
    { param($e,$o) $o.afterState='Accepted' },
    { param($e,$o) $o.stateApplied=$false },
    { param($e,$o) $o.receiptValidated='True' },
    { param($e,$o) $o.commandIdentity='wrong' },
    { param($e,$o) $o.receiptFingerprint='wrong' },
    { param($e,$o) $o.responderProbeHandoff.receipts=@() },
    { param($e,$o) $o.responderProbeHandoff.receipts+=Copy-Value $o.responderProbeHandoff.receipts[0] },
    { param($e,$o) $o.responderProbeHandoff.receipts[0].stage='handoff_bind' },
    { param($e,$o) $o.responderProbeHandoff.receipts[0].receiptValidated=$false },
    { param($e,$o) $o.responderProbeHandoff.role='owner_work_hold' },
    { param($e,$o) $o.responderProbeHandoff.PSObject.Properties.Remove('probeId') },
    { param($e,$o) $o.responderProbeHandoff.receipts[0].PSObject.Properties.Remove('commandFingerprint') },
    { param($e,$o) $o.responderProbeHandoff.handoffGeneration++ },
    { param($e,$o) $o.responderProbeHandoff.linkedTransitionAuthorityId='wrong' },
    { param($e,$o) $o.responderProbeHandoff.probeState='Pending' },
    { param($e,$o) $o.responderProbeHandoff.pairedReleaseAuthorityId='(none)' },
    { param($e,$o) $o.responderProbeHandoff.sourceBoundary.dataSessionGeneration++ },
    { param($e,$o) $o.responderProbeHandoff.effectiveBoundary.checkpointGeneration++ },
    { param($e,$o) $o.responderProbeHandoff.targetBoundary.route='post_tuna_fallback_v6' },
    { param($e,$o) $e.aggregate.operations+=Copy-Value $o },
    { param($e,$o) $e.decisionStatus='Rejected' },
    { param($e,$o) $e.aggregate.afterStateGeneration++ },
    { param($e,$o) $e.aggregate.afterBoundary.runtimeGeneration++ }
)) {
    $e=Copy-Value $request; $o=Subject $e; & $mutation $e $o
    if (Test-FileTransferCoordinatorSnapshotCheckpointResponderCarry $e $o) { throw "Malformed carry passed: $mutation" }
}
$lines = @(New-Lines $envelopes)
Assert-Parity $lines $true
$shuffled = Copy-Value $envelopes
foreach($e in $shuffled) { [array]::Reverse($e.aggregate.operations) }
Assert-Parity (New-Lines $shuffled) $true
Assert-Parity (@($lines[0]) + $lines[1..($lines.Count-2)] + $lines[1..($lines.Count-2)] + @($lines[-1])) $true
Assert-Parity @($lines | Where-Object { -not ($_.Contains("transition_id=$($request.transitionId);") -and $_.Contains('effect_kind=handoff_invalidated;')) }) $false 'snapshot_responder_probe_handoff_required_effect_missing'
foreach ($mutation in @(
    { param($o) $o.responderProbeHandoff.ackMessageId+='-changed' },
    { param($o) $o.responderProbeHandoff.peerProbeFrameFingerprint+='-changed' },
    { param($o) $o.responderProbeHandoff.sourceBoundary.checkpointGeneration++ },
    { param($o) $o.responderProbeHandoff.pairKey+='-changed' },
    { param($o) $o.receiptFingerprint='changed'; $o.responderProbeHandoff.receipts[0].receiptFingerprint='changed' }
)) {
    $values=Copy-Value $envelopes
    $candidate=@($values | Where-Object decisionKind -CEQ 'FallbackCheckpointRequested')[0]
    & $mutation (Subject $candidate)
    Assert-Parity (New-Lines $values) $false 'checkpoint_responder_terminal_carry_history_invalid'
}
$values=Copy-Value $envelopes
$values[0].aggregate.operations=@($values[0].aggregate.operations | Where-Object { $_.operationKind -cne 'ResponderPeerProbeRouteTransitionHandoff' })
Assert-Parity (New-Lines $values) $false 'checkpoint_responder_terminal_carry_history_invalid'
function History-Entries($values) {
    foreach($e in $values) {
        [pscustomobject]@{
            Boundary=[pscustomobject]@{ coordinatorInstanceId=$e.coordinatorInstanceId; writer='fixture-writer'; boundaryId=$e.transitionId
                beforeStateGeneration=$e.aggregate.beforeStateGeneration; afterStateGeneration=$e.aggregate.afterStateGeneration
                beforeStateHash=$e.aggregate.beforeStateFingerprint; afterStateHash=$e.aggregate.afterStateFingerprint
                decisionKind=$e.decisionKind; operations=$e.aggregate.operations; evidenceLineIndex=$e.aggregate.afterStateGeneration }
            BeforeBoundary=Copy-Value $e.aggregate.beforeBoundary; AfterBoundary=Copy-Value $e.aggregate.afterBoundary
            SessionId=$e.sessionId; TransferId=$e.transferId; Direction=$e.direction
        }
    }
}
foreach($mutation in @('gap','terminal','replacement','transfer','retirement','aba')) {
    $entries=@(History-Entries (Copy-Value $envelopes))
    $commit=@($entries | Where-Object { $_.Boundary.decisionKind -ceq 'CommitFileTransferRouteTransition' })[0]
    $terminal=@($commit.Boundary.operations | Where-Object operationRole -CEQ 'handoff_terminal')[0]
    switch($mutation) {
        'gap' { $commit.Boundary.beforeStateGeneration++ }
        'terminal' { $commit.AfterBoundary.terminalGeneration++ }
        'replacement' { $commit.AfterBoundary.dataSessionGeneration++ }
        'transfer' { $commit.TransferId+='-replacement' }
        'retirement' { $terminal.afterState='None' }
        'aba' { $terminal.authorityId='replacement-B'; $terminal.operationKey+='-B'; $terminal.beforeState='None' }
    }
    $failures=@(Resolve-FileTransferCoordinatorCheckpointResponderCarries (New-FileTransferCoordinatorSnapshotAggregateIntervalIndex $entries) @{})
    if($failures.Count -eq 0) { throw "History poison accepted: $mutation" }
}
Write-Output 'Checkpoint responder carry, immutable invalidation history, exact effects and full/compact/GUI parity passed.'
