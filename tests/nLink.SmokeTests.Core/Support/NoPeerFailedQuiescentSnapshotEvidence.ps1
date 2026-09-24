param([string]$RepoRoot, [string]$InputPath, [string]$OutputRoot,
    [string]$SupersessionTransitionId, [string]$FailedTransitionId, [string]$PriorTerminalTransitionId)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$catalog = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion 'snapshot-authority-v1-receiver-barrier-v1'
if ($catalog.Hash -cne 'e507b186e037924e5785e563742fedd9111552684fc8cc2f46eef38225b0bb94') { throw 'Receiver catalog changed.' }
foreach ($source in @(
    @{ Path='tests/nLink.SmokeTests.Core/Support/CheckpointResponderCoexistenceSnapshotEvidence.ps1'; Names=@('Copy-Value','Decode','Replace-Envelope','Boundary-Semantics') },
    @{ Path='tests/nLink.SmokeTests.Core/Support/FailedQuiescentTerminalRolloverSnapshotEvidence.ps1'; Names=@('Subject','Receipt') },
    @{ Path='tools/GuiSmoke-Windows.ps1'; Names=@('New-TunaGuiFileTransferFailureClassificationResult','Get-TunaGuiExplicitFailureClassification','Get-TunaGuiFileTransferSetupFailureClassification') })) {
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $RepoRoot $source.Path), [ref]$null, [ref]$null)
    foreach ($name in $source.Names) {
        $definitions = @($ast.FindAll({ param($node)
            $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
        }, $true))
        if ($definitions.Count -ne 1) { throw "Shared function is not unique: $name" }
        . ([scriptblock]::Create($definitions[0].Extent.Text))
    }
}
$original = [IO.File]::ReadAllLines($InputPath)
$inputHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([IO.File]::ReadAllBytes($InputPath))
$snapshots = @(for ($i=0; $i -lt $original.Count; $i++) {
    if ((Get-FileTransferCoordinatorSnapshotEventName $original[$i]) -ceq 'filetransfer_coordinator_transition_snapshot') {
        [pscustomobject]@{ Index=$i; Envelope=(Decode $original[$i]) }
    }
})
$candidate = @($snapshots | Where-Object { $_.Envelope.transitionId -ceq $SupersessionTransitionId })
if ($candidate.Count -ne 1) { throw 'Supersession is not unique.' }
$candidate = $candidate[0]
$successor = Subject $candidate.Envelope 'yield_current' 'Quiescent'
$prior = Subject $candidate.Envelope 'yield_current' 'Failed'
$terminal = Subject $candidate.Envelope 'yield_terminal' 'Failed'
if ($prior.beforeState -cne 'YieldIssued' -or $successor.afterState -cne 'YieldIssued' -or
    $successor.fallbackTailYield.failedSupersessionCarriedPeerProof -or
    $prior.fallbackTailYield.receipts.Count -ne 1 -or $successor.fallbackTailYield.receipts.Count -ne 2 -or
    $terminal.fallbackTailYield.receipts.Count -ne 2) { throw 'Fixture is not genuine no-peer supersession.' }
foreach ($operation in $candidate.Envelope.aggregate.operations) {
    if (-not (Test-FileTransferCoordinatorSnapshotFamily4OperationCatalog $candidate.Envelope $operation)) {
        throw 'No-peer production catalog rejected.'
    }
}

# Shape mutations are negative controls only. The positive comes from the Coordinator.
$shapeControls = 0
foreach ($roleKind in @(@('yield_current','Failed'),@('yield_current','Quiescent'),@('yield_terminal','Failed'))) {
    foreach ($field in @('peerProofAuthorityId','peerProofIdentity','peerProofFingerprint','peerProofYieldGeneration',
        'failedSupersessionCarriedPeerProof','localProofIdentity','localProofFingerprint','pairedReleaseAuthorityId','reservationGeneration')) {
        $bad = Copy-Value $candidate.Envelope
        $typed = (Subject $bad $roleKind[0] $roleKind[1]).fallbackTailYield
        $typed.$field = if ($field -ceq 'failedSupersessionCarriedPeerProof') { $true }
            elseif ($field -in @('peerProofYieldGeneration','reservationGeneration')) { 1 } else { 'forged' }
        if ($null -ne (Get-FileTransferCoordinatorSnapshotFallbackTailFailedSupersessionShape $bad.aggregate.operations)) {
            throw "No-peer shape accepted $($roleKind -join '/')/$field"
        }
        $shapeControls++
    }
}
foreach ($case in @('mixed_prior_state','mixed_successor_state','string_flag','missing_flag','extra_receipt','missing_receipt',
    'wrong_receipt','wrong_primary_receipt','wrong_predecessor','wrong_generation','wrong_scope')) {
    $bad = Copy-Value $candidate.Envelope
    $a = Subject $bad 'yield_current' 'Failed'; $b = Subject $bad 'yield_current' 'Quiescent'
    switch ($case) {
        'mixed_prior_state' { $a.beforeState='PeerProofObserved' }
        'mixed_successor_state' { $b.afterState='PeerProofObserved' }
        'string_flag' { $b.fallbackTailYield.failedSupersessionCarriedPeerProof='false' }
        'missing_flag' { $b.fallbackTailYield.PSObject.Properties.Remove('failedSupersessionCarriedPeerProof') }
        'extra_receipt' { $b.fallbackTailYield.receipts += (Copy-Value $b.fallbackTailYield.receipts[0]) }
        'missing_receipt' { $b.fallbackTailYield.receipts=@($b.fallbackTailYield.receipts | Where-Object stage -CNE 'failed_supersession') }
        'wrong_receipt' { (Receipt (Subject $bad 'yield_terminal' 'Failed') 'yield').receiptFingerprint += '-forged' }
        'wrong_primary_receipt' { $a.receiptFingerprint += '-forged' }
        'wrong_predecessor' { $b.fallbackTailYield.failedPredecessorAuthorityId='forged' }
        'wrong_generation' { $b.operationGeneration=$a.operationGeneration }
        'wrong_scope' { $b.fallbackTailYield.sourceBoundary.runtimeGeneration++ }
    }
    if ($null -ne (Get-FileTransferCoordinatorSnapshotFallbackTailFailedSupersessionShape $bad.aggregate.operations)) {
        throw "No-peer shape accepted $case"
    }
    $shapeControls++
}

$effects = @($original | Where-Object {
    (Get-FileTransferCoordinatorSnapshotEventName $_) -ceq 'filetransfer_coordinator_authority_effect' -and
    (Get-FileTransferCoordinatorSnapshotLineField $_ transition_id) -ceq $SupersessionTransitionId
})
if ($effects.Count -ne 1 -or
    (Get-FileTransferCoordinatorSnapshotLineField $effects[0] effect_kind) -cne 'recovery_owner_retired_after_yield' -or
    (Get-FileTransferCoordinatorSnapshotLineField $effects[0] operation_key) -cne $successor.operationKey) {
    throw 'No-peer successor effect is not uniquely bound.'
}
$cases = @('exact')
$expectedRouteFindings = $null
if ($PriorTerminalTransitionId -cne '(none)') {
    # The established-route fixture omits earlier route publications. Its final
    # StartTransfer retires the paired successor, not a route-owner handoff.
    $routePublications = @{}
    foreach ($snapshot in $snapshots) {
        foreach ($operation in @($snapshot.Envelope.aggregate.operations | Where-Object {
            $_.family -ceq 'route_runtime' -and $_.operationRole -ceq 'route_publication' -and $_.afterState -ceq 'Published'
        })) { $routePublications[[long]$operation.operationGeneration] = $operation }
    }
    $first = $snapshots[0].Envelope
    $scope = "$($first.coordinatorInstanceId)|failed-quiescent-rollover-fixture|$($first.sessionId)|$($first.transferId)|$($first.direction)"
    if ($first.direction -ceq 'outbound') {
        if ($routePublications.Count -ne 3 -or -not $routePublications.ContainsKey(5L) -or
            -not $routePublications.ContainsKey(6L) -or -not $routePublications.ContainsKey(7L)) { throw 'Outbound partial route fixture changed.' }
        $expectedRouteFindings = @(
            "snapshot route publication generation is not contiguous: scope=$scope; generation=5",
            'snapshot initial route publication has invalid kind or predecessor',
            "snapshot route publication generation is not contiguous: scope=$scope; generation=6",
            "snapshot route publication has no exact committed predecessor transition: route=post_tuna_fallback_v6; operation_key=$($routePublications[6L].operationKey)",
            "snapshot route publication generation is not contiguous: scope=$scope; generation=7",
            'snapshot same-route publication has no exact fallback owner successor contract')
    }
    else {
        if ($routePublications.Count -ne 3 -or -not $routePublications.ContainsKey(4L) -or
            -not $routePublications.ContainsKey(5L) -or -not $routePublications.ContainsKey(6L)) { throw 'Inbound partial route fixture changed.' }
        $expectedRouteFindings = @(
            "snapshot route publication generation is not contiguous: scope=$scope; generation=4",
            "snapshot route publication generation is not contiguous: scope=$scope; generation=5",
            'snapshot route publication has no unique runtime owner entrant: route=post_tuna_fallback_v6',
            'snapshot same-route publication has no exact fallback owner successor contract',
            "snapshot route publication generation is not contiguous: scope=$scope; generation=6",
            'snapshot same-route publication has no exact fallback owner successor contract')
    }
}
if ($candidate.Envelope.direction -ceq 'outbound' -and $PriorTerminalTransitionId -cne '(none)') {
    $cases += @('reordered','exact_duplicate','missing_failed_birth','missing_terminal_birth','missing_successor_effect',
        'forged_displaced_effect','wrong_terminal_identity','mixed_peer_state')
}
$outputs = @(); $baseline = $null
foreach ($case in $cases) {
    $lines = @($original)
    if ($case -in @('reordered','wrong_terminal_identity','mixed_peer_state')) {
        $changed = Copy-Value $candidate.Envelope
        if ($case -ceq 'reordered') { [array]::Reverse($changed.aggregate.operations) }
        elseif ($case -ceq 'wrong_terminal_identity') { (Subject $changed 'yield_terminal' 'Quiescent').fallbackTailYield.ownerKey += '-forged' }
        else { (Subject $changed 'yield_current' 'Quiescent').afterState='PeerProofObserved' }
        $lines[$candidate.Index] = Replace-Envelope $lines[$candidate.Index] $changed
    }
    if ($case -ceq 'exact_duplicate') {
        $lines = @($lines[0..$candidate.Index]) + @($lines[$candidate.Index]) + @($lines[($candidate.Index+1)..($lines.Count-1)])
    }
    if ($case -in @('missing_failed_birth','missing_terminal_birth')) {
        $id = if ($case -ceq 'missing_failed_birth') { $FailedTransitionId } else { $PriorTerminalTransitionId }
        $lines = @($lines | Where-Object {
            -not ((Get-FileTransferCoordinatorSnapshotEventName $_) -ceq 'filetransfer_coordinator_transition_snapshot' -and
                (Get-FileTransferCoordinatorSnapshotLineField $_ transition_id) -ceq $id)
        })
    }
    if ($case -ceq 'missing_successor_effect') { $lines = @($lines | Where-Object { $_ -cne $effects[0] }) }
    if ($case -ceq 'forged_displaced_effect') {
        $old = Subject $candidate.Envelope 'yield_terminal' 'Quiescent'
        $forged = $effects[0].Replace($successor.operationKey, $old.operationKey).Replace($successor.authorityId, $old.authorityId)
        $lines = @($lines[0..($lines.Count-2)]) + @($forged,$lines[-1])
    }
    $fullLines = @('event=unrelated_fixture_diagnostic; note=independent_compaction') + $lines
    $records = @(for ($i=0; $i -lt $fullLines.Count; $i++) {
        $name=Get-FileTransferCoordinatorSnapshotEventName $fullLines[$i]
        if ($catalog.SnapshotAuthorityV1 -contains $name -or $catalog.GenerationStampedEffects -contains $name) {
            [pscustomobject]@{Line=$fullLines[$i];LineIndex=$i}
        }
    })
    if ($records.Count -ge $fullLines.Count) { throw 'Compaction did not independently select records.' }
    $full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $fullLines
    $compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
    $expected = if ($case -in @('exact','reordered','exact_duplicate')) { 'pass' } else { 'fail' }
    if ($full.ArtifactEvidenceVerdict -cne $expected -or $compact.ArtifactEvidenceVerdict -cne $expected -or
        (Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass') {
        throw "$case expected=$expected; actual=$($full.ArtifactEvidenceVerdict); reason=$($full.ArtifactEvidenceFailureReason)"
    }
    $boundary = $full.FirstUnresolvedBoundary
    $guiSource = 'coordinator_snapshot_evidence'
    $route = $null
    if ($expected -ceq 'pass') {
        if (-not $full.StreamComplete -or @($full.Boundaries | Where-Object verdict -CNE 'pass').Count -ne 0 -or
            $full.LegacySelectionCount -ne 0) { throw "$case semantic history failed: $($full.Boundaries | Where-Object verdict -CNE 'pass' | ConvertTo-Json -Depth 16 -Compress)" }
        $semantics = Boundary-Semantics $full.Boundaries
        if ($case -ceq 'exact') { $baseline=$semantics }
        elseif ($semantics -cne $baseline) { throw "$case changed semantic boundaries." }
        $route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
        $compactRoute = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $compact
        if ((Compare-FileTransferCoordinatorSnapshotRouteConsistency $route $compactRoute).Verdict -cne 'pass') { throw 'Route parity failed.' }
        if ($null -ne $expectedRouteFindings -and ($route.Verdict -cne 'fail' -or
            (ConvertTo-Json -InputObject @($route.Findings) -Compress) -cne
            (ConvertTo-Json -InputObject $expectedRouteFindings -Compress))) { throw "$case partial-route findings changed." }
        if ($route.Verdict -ceq 'fail') {
            # These fixtures intentionally start with an established fallback route.
            if ($null -eq $expectedRouteFindings -and @($route.Findings | Where-Object {
                $_ -cnotmatch '^snapshot route publication generation is not contiguous:' -and
                $_ -cne 'snapshot initial route publication has invalid kind or predecessor' -and
                $_ -cnotmatch '^snapshot route publication has no exact committed predecessor transition:' -and
                $_ -cne 'snapshot has no route publication operations'
            }).Count -gt 0) { throw "Unexpected partial-route findings: $($route.Findings -join ';')" }
            $boundary=$route.FirstUnresolvedBoundary; $guiSource='coordinator_snapshot_authority_v1_route_consistency'
        }
    }
    foreach ($input in @([pscustomobject]@{Name='full';Lines=$fullLines},[pscustomobject]@{Name='compact';Lines=@($records | ForEach-Object Line)})) {
        $path=Join-Path $OutputRoot ($input.Name + '-no-peer-gui.log')
        [IO.File]::WriteAllLines($path,[string[]]$input.Lines)
        $gui=Get-TunaGuiFileTransferSetupFailureClassification -Bookmark 0 -ErrorMessage 'fixture' -RouteMode 'regular-v4-live-activation-off-on-off-256mb' -ImmutableEvidencePaths @($path)
        if ($null -ne $boundary) {
            if ($null -eq $gui -or $gui.ClassificationSource -cne $guiSource -or $gui.CoordinatorBoundaryId -cne $boundary.boundaryId -or
                $gui.CoordinatorBoundaryHash -cne $boundary.boundaryHash -or $gui.FailureClass -cne $boundary.taxonomyClass) { throw "$case/$($input.Name) GUI boundary changed." }
        }
        elseif ($null -ne $gui) { throw "$case/$($input.Name) unexpected GUI failure." }
    }
    $routeSummary = if ($null -eq $route) { $null } else {
        [ordered]@{Verdict=$route.Verdict;Hash=$route.BoundaryHash;Findings=@($route.Findings)}
    }
    $outputs += [ordered]@{Case=$case;Artifact=$full.ArtifactEvidenceVerdict;BoundaryHash=$full.BoundaryHash;Boundaries=(Boundary-Semantics $full.Boundaries);Route=$routeSummary}
}
if ($inputHash -cne (Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([IO.File]::ReadAllBytes($InputPath)))) { throw 'Production evidence changed.' }
'no-peer-supersession-normalized=' + (ConvertTo-Json -InputObject ([ordered]@{ShapeControls=$shapeControls;Cases=$outputs}) -Depth 100 -Compress)
