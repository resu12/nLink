param([string]$RepoRoot, [string]$FixturePath, [string]$IndependentGenerations = 'False', [string]$Correlated = 'False')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $RepoRoot 'tools/FileTransferOps/CoordinatorSnapshotEvidence.ps1')
$lines = [IO.File]::ReadAllLines($FixturePath)
$full = Get-FileTransferCoordinatorSnapshotEvaluation -Lines $lines
$records = @(for ($i = 0; $i -lt $lines.Count; $i++) { [pscustomobject]@{ Line=$lines[$i]; LineIndex=$i } })
$compact = Get-FileTransferCoordinatorSnapshotEvaluation -SnapshotRecords $records
$route = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $full
$compactRoute = Get-FileTransferCoordinatorSnapshotRouteConsistency -Evaluation $compact
$catalog = Get-FileTransferCoordinatorInitialDataSessionCatalog
$parent = Get-FileTransferCoordinatorReceiverPreAdmissionCatalog
if ($catalog.ParentCatalogHash -cne $parent.Hash -or
    $parent.Hash -cne '17196929ff74bd92c4866ccb8da33de05eb5b22d5436f56488f3a7fd2f4d086b' -or
    $full.CatalogHash -cne $catalog.Hash) { throw 'Binding profile lost its pinned parent or emitted catalog.' }
if ($full.ArtifactEvidenceVerdict -cne 'pass' -or -not $full.StreamComplete -or
    (Compare-FileTransferCoordinatorSnapshotEvaluations $full $compact).Verdict -cne 'pass' -or
    (Compare-FileTransferCoordinatorSnapshotRouteConsistency $route $compactRoute).Verdict -cne 'pass') {
    throw "Fresh service evidence or parity failed: $($full.ArtifactEvidenceFailureReason)"
}
$startups = @($full.Boundaries | Where-Object {
    $_.decisionKind -ceq 'StartTransfer' -and $_.decisionReason -ceq 'post_tuna_fallback_v6_route'
})
if ($startups.Count -ne 2 -or @($startups.direction | Sort-Object -Unique).Count -ne 2) {
    throw 'Expected actual inbound and outbound fallback startup decisions.'
}
if ($route.Verdict -cne 'pass' -or $route.Findings.Count -ne 0 -or
    $null -ne $route.FirstUnresolvedBoundary) {
    throw "Exact startup successor rejected: $($route.Findings -join ';')"
}
$index = New-FileTransferCoordinatorSnapshotAggregateIntervalIndex -Entries @(
    $full.Boundaries | ForEach-Object { [pscustomobject]@{ Boundary=$_ } })
$initial = Get-FileTransferCoordinatorSnapshotInitialPublications -AggregateIndex $index -Effects $full.__RoutePublicationEffects
if ($initial.Count -ne 2 -or $full.__RoutePublicationEffects.Count -ne 4) {
    throw 'Initial owner proofs or actual publication-forwarding effects were lost.'
}
foreach ($startup in $startups) {
    $publications = @($full.Boundaries | Where-Object {
        $_.coordinatorInstanceId -ceq $startup.coordinatorInstanceId -and
        $_.decisionKind -ceq 'RoutePublication' -and $_.afterBoundary.routePublicationGeneration -eq 1
    })
    if ($publications.Count -ne 1) { throw 'Initial publication is not unique.' }
    $publication = $publications[0]
    $bindings = @($full.Boundaries | Where-Object {
        $_.coordinatorInstanceId -ceq $startup.coordinatorInstanceId -and
        $_.decisionKind -ceq 'BindInitialFallbackDataSession'
    })
    if ($bindings.Count -ne 1) { throw 'Initial attachment witness is not unique.' }
    $binding = $bindings[0]
    $interval = @($full.Boundaries | Where-Object {
        $_.coordinatorInstanceId -ceq $startup.coordinatorInstanceId -and
        $_.beforeStateGeneration -ge $publication.afterStateGeneration -and
        $_.beforeStateGeneration -lt $binding.beforeStateGeneration
    } | Sort-Object { [long]$_.writerSequence })
    if ($Correlated -ceq 'True') {
        $accepts = @($interval | Where-Object decisionKind -CEQ 'ApplyFallbackBootstrapAccept')
        $opens = @($full.Boundaries | Where-Object {
            $_.coordinatorInstanceId -ceq $startup.coordinatorInstanceId -and
            $_.decisionKind -ceq 'ApplyFallbackBootstrapSessionOpen' -and
            $_.beforeStateGeneration -ge $publication.afterStateGeneration -and
            $_.beforeStateGeneration -le $binding.beforeStateGeneration
        })
        if ($accepts.Count -ne 1 -or $opens.Count -ne 1 -or
            $accepts[0].decisionStatus -cne 'Accepted' -or
            $accepts[0].operations[0].operationKind -cne 'FallbackBootstrapAgreement' -or
            $accepts[0].operations[0].routeTransition.bootstrapCorrelation.version -ne 1) {
            throw 'Correlated fixture did not exercise actual accepted negotiation before binding.'
        }
    }
    elseif ($interval.Count -ne 0) { throw 'Uncorrelated fixture unexpectedly contains an intervening mutation.' }
    $generation=$publication.afterStateGeneration; $hash=$publication.afterStateHash
    foreach ($step in $interval) {
        if ($step.beforeStateGeneration -ne $generation -or $step.beforeStateHash -cne $hash -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $publication.afterBoundary $step.beforeBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $step.beforeBoundary $step.afterBoundary)) {
            throw 'Negotiation lost aggregate continuity or changed route/runtime scope.'
        }
        $generation=$step.afterStateGeneration; $hash=$step.afterStateHash
    }
    $before = $startup.beforeBoundary; $after = $startup.afterBoundary
    if ($IndependentGenerations -ceq 'True' -and
        ($before.runtimeGeneration -ne 7 -or $after.runtimeGeneration -ne 8 -or
         $before.legGeneration -ne 13 -or $after.legGeneration -ne 14)) {
        throw 'Independent runtime and leg generation domains were not exercised.'
    }
    if ($startup.decisionStatus -cne 'Accepted' -or
        $generation -ne $binding.beforeStateGeneration -or
        $hash -cne $binding.beforeStateHash -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $publication.afterBoundary $binding.beforeBoundary) -or
        $binding.afterStateGeneration -ne $startup.beforeStateGeneration -or
        $binding.afterStateHash -cne $startup.beforeStateHash -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $binding.afterBoundary $before) -or
        $publication.afterBoundary.dataSessionGeneration -ne 0 -or
        $publication.afterBoundary.dataSessionIdentity -cne '(none)' -or
        $before.dataSessionGeneration -ne 1 -or $before.dataSessionIdentity -in @('', '(none)') -or
        $after.dataSessionGeneration -ne 1 -or $after.dataSessionIdentity -cne $before.dataSessionIdentity -or
        $before.route -cne 'post_tuna_fallback_v6' -or $before.protocolVersion -ne 6 -or
        $before.liveRouteEpoch -ne 0 -or $before.routePublicationGeneration -ne 1 -or
        $after.routePublicationGeneration -ne 2 -or
        $after.runtimeGeneration -ne ($before.runtimeGeneration + 1) -or
        $after.legGeneration -ne ($before.legGeneration + 1) -or
        @($startup.operations | Where-Object family -CEQ 'route_transition').Count -ne 0) {
        throw 'Fresh service lost its exact recorded first attachment or owner replacement.'
    }
    foreach ($role in @('runtime_owner', 'route_publication')) {
        $state = if ($role -ceq 'runtime_owner') { 'Current' } else { 'Published' }
        $operations = @($startup.operations | Where-Object { $_.operationRole -ceq $role -and $_.stateApplied })
        if ($operations.Count -ne 2 -or
            @($operations | Where-Object { $_.beforeState -ceq $state -and $_.afterState -ceq 'None' }).Count -ne 1 -or
            @($operations | Where-Object { $_.beforeState -ceq 'None' -and $_.afterState -ceq $state }).Count -ne 1) {
            throw "Startup lost exact $role retirement/successor."
        }
    }
}
$bindingEnvelopes = @(foreach ($line in $lines) {
    if ($line -match 'snapshot_payload=([^;]+)') {
        $payload = $Matches[1].Replace('-', '+').Replace('_', '/')
        $decoded = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload.PadRight([int]([Math]::Ceiling($payload.Length / 4.0) * 4), '='))) | ConvertFrom-Json
        if ($decoded.decisionKind -ceq 'BindInitialFallbackDataSession') { $decoded }
    }
})
if ($bindingEnvelopes.Count -ne 2) { throw 'Missing emitted binding envelopes.' }
$controls = @('old_profile','missing_payload','missing_owner_fingerprint','missing_publication_fingerprint',
    'source_mismatch','effective_mismatch','wrong_command','wrong_command_generation','wrong_receipt_generation',
    'missing_receipt','missing_receipt_fingerprint','rejected_receipt','unvalidated_receipt','unapplied_binding',
    'wrong_operation','rejected_decision','unchanged_hash','state_generation_gap','duplicate_operation',
    'already_bound','later_publication','invalid_tuna_v6','fallback_protocol4','second_data_session')
foreach ($original in $bindingEnvelopes) {
    if (-not (Test-FileTransferCoordinatorInitialDataSessionOperation $original $original.aggregate.operations[0] $catalog)) {
        throw 'Actual emitted binding marker rejected.'
    }
    foreach ($control in $controls) {
        $envelope = ConvertFrom-Json (ConvertTo-Json -InputObject $original -Depth 64 -Compress)
        $operation = $envelope.aggregate.operations[0]
        $profile = $catalog
        $tuples = @($envelope.aggregate.beforeBoundary, $envelope.aggregate.afterBoundary,
            $operation.initialDataSessionBinding.sourceBoundary, $operation.initialDataSessionBinding.effectiveBoundary)
        switch ($control) {
            'old_profile' { $profile = $parent }
            'missing_payload' { $operation.initialDataSessionBinding = $null }
            'missing_owner_fingerprint' { $operation.initialDataSessionBinding.runtimeOwnerFingerprint = '(none)' }
            'missing_publication_fingerprint' { $operation.initialDataSessionBinding.publicationFingerprint = '(none)' }
            'source_mismatch' { $operation.initialDataSessionBinding.sourceBoundary.runtimeGeneration++ }
            'effective_mismatch' { $operation.initialDataSessionBinding.effectiveBoundary.dataSessionGeneration++ }
            'wrong_command' { $operation.commandIdentity = 'foreign' }
            'wrong_command_generation' { $operation.commandGeneration++ }
            'wrong_receipt_generation' { $operation.receiptGeneration++ }
            'missing_receipt' { $operation.receiptIdentity = '(none)' }
            'missing_receipt_fingerprint' { $operation.receiptFingerprint = '(none)' }
            'rejected_receipt' { $operation.receiptStatus = 'Rejected' }
            'unvalidated_receipt' { $operation.receiptValidated = $false }
            'unapplied_binding' { $operation.stateApplied = $false }
            'wrong_operation' { $operation.operationKind = 'Other' }
            'rejected_decision' { $envelope.decisionStatus = 'Rejected' }
            'unchanged_hash' { $envelope.aggregate.afterStateFingerprint = $envelope.aggregate.beforeStateFingerprint }
            'state_generation_gap' { $envelope.aggregate.afterStateGeneration++ }
            'duplicate_operation' { $envelope.aggregate.operations = @($operation, $operation) }
            'already_bound' { $tuples[0].dataSessionGeneration = 1; $tuples[2].dataSessionGeneration = 1 }
            'later_publication' { foreach ($tuple in $tuples) { $tuple.routePublicationGeneration = 2 } }
            'invalid_tuna_v6' { foreach ($tuple in $tuples) { $tuple.route = 'file_tuna_v6' } }
            'fallback_protocol4' { foreach ($tuple in $tuples) { $tuple.protocolVersion = 4 } }
            'second_data_session' { $tuples[1].dataSessionGeneration = 2; $tuples[3].dataSessionGeneration = 2 }
        }
        if (Test-FileTransferCoordinatorInitialDataSessionOperation $envelope $operation $profile) {
            throw "Invalid binding accepted: $control"
        }
    }
}
$successorControls = & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/InitialFallbackStartupSuccessorControls.ps1') -RepoRoot $RepoRoot -Full $full -Compact $compact
$agreementControls = 0
if ($Correlated -ceq 'True') {
    $agreement = & (Join-Path $RepoRoot 'tests/nLink.SmokeTests.Core/Support/InitialFallbackStartupAgreementControls.ps1') -RepoRoot $RepoRoot -Full $full -Compact $compact
    $agreementControls = $agreement.CasesPerDirection * $agreement.Directions * $agreement.Consumers
}
[ordered]@{
    artifactEvidenceVerdict=$full.ArtifactEvidenceVerdict; streamComplete=$full.StreamComplete
    snapshotParity='pass'; routeParity='pass'; routeVerdict=$route.Verdict
    startupDirections=@($startups.direction | Sort-Object); initialProofs=$initial.Count
    witnessedInitialAttachments=$startups.Count; ownerSuccessors=$startups.Count
    bindingNegativeControls=($controls.Count * $bindingEnvelopes.Count)
    successorNegativeControls=($successorControls.CasesPerDirection * $successorControls.Directions * $successorControls.Consumers)
    agreementNegativeControls=$agreementControls
    findings=@($route.Findings)
} | ConvertTo-Json -Depth 4 -Compress
Write-Output 'initial-fallback-startup-evidence-ok'
