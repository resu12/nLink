Set-StrictMode -Version Latest

$script:FileTransferCoordinatorInitialDataSessionCatalogVersion =
    'snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1-fallback-bootstrap-initiation-v1-source-callback-v1-repair-owner-v1-activation-retry-v1-receiver-pre-admission-v1-initial-data-session-v1'
$script:FileTransferCoordinatorInitialDataSessionCatalogHash = '90cf0fdafc50d49c866bbb9bdda9097b3306ae5a7fd9d1198b03c02c66ef18ab'
$script:FileTransferCoordinatorInitialDataSessionCatalog = [ordered]@{
    version=1; family='route_runtime'; operationKind='InitialFallbackDataSessionBinding'
    operationRole='initial_data_session_binding'; decisionKind='BindInitialFallbackDataSession'
    payloadName='initialDataSessionBinding'; states=@('None','Bound'); receiptStatus='Accepted'
    effectKinds=@(); routeAuthority=$false
    payloadFields=[ordered]@{
        sourceBoundary='FileTransferCoordinatorStateBoundarySnapshot'
        effectiveBoundary='FileTransferCoordinatorStateBoundarySnapshot'
        runtimeOwnerFingerprint='string'; publicationFingerprint='string'
    }
    binding='first-fallback-epoch-zero-data-session-0-to-1-owner-publication-preserved-v1'
}

function Get-FileTransferCoordinatorInitialDataSessionCatalog {
    $parent=Get-FileTransferCoordinatorReceiverPreAdmissionCatalog
    $json=ConvertTo-Json -InputObject $script:FileTransferCoordinatorInitialDataSessionCatalog -Depth 16 -Compress
    $hash=Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([Text.UTF8Encoding]::new($false).GetBytes(
        "parentHash=$($parent.Hash)`nInitialDataSessionCatalog=$json"))
    if ($hash -cne $script:FileTransferCoordinatorInitialDataSessionCatalogHash) { throw "initial_data_session_catalog_hash_invalid:$hash" }
    $result=[ordered]@{}
    foreach ($property in $parent.PSObject.Properties) { $result[$property.Name]=$property.Value }
    $result.Version=$script:FileTransferCoordinatorInitialDataSessionCatalogVersion
    $result.Hash=$hash; $result.ParentCatalogHash=$parent.Hash
    $result.InitialDataSessionCatalog=$script:FileTransferCoordinatorInitialDataSessionCatalog
    [pscustomobject]$result
}

function Test-FileTransferCoordinatorInitialDataSessionMarker {
    param($Operation)
    return $null -ne $Operation -and (
        $Operation.PSObject.Properties.Name -contains 'initialDataSessionBinding' -or
        $Operation.operationKind -ceq 'InitialFallbackDataSessionBinding' -or
        $Operation.operationRole -ceq 'initial_data_session_binding')
}

function Test-FileTransferCoordinatorInitialDataSessionOperation {
    param($Envelope, $Operation, $Catalog)
    try {
        $payload=$Operation.initialDataSessionBinding
        $before=$Envelope.aggregate.beforeBoundary; $after=$Envelope.aggregate.afterBoundary
        if ($Catalog.Version -cnotin @($script:FileTransferCoordinatorInitialDataSessionCatalogVersion, $script:FileTransferCoordinatorEarlyCheckpointProofCatalogVersion, $script:FileTransferCoordinatorFallbackRepairHandoffCatalogVersion) -or
            $Envelope.decisionKind -cne 'BindInitialFallbackDataSession' -or $Envelope.decisionStatus -cne 'Accepted' -or
            $Envelope.decisionReason -cne 'initial_fallback_data_session_bound' -or
            $Operation.family -cne 'route_runtime' -or $Operation.operationKind -cne 'InitialFallbackDataSessionBinding' -or
            $Operation.operationRole -cne 'initial_data_session_binding' -or
            $Operation.operationKey -cne "route_runtime|initial_data_session_binding|$($Operation.authorityId)|1|InitialFallbackDataSessionBinding" -or
            $Operation.beforeState -cne 'None' -or $Operation.afterState -cne 'Bound' -or
            $Operation.stateApplied -isnot [bool] -or -not $Operation.stateApplied -or
            $Operation.receiptValidated -isnot [bool] -or -not $Operation.receiptValidated -or
            $Operation.operationGeneration -ne 1 -or $Operation.commandGeneration -ne 1 -or $Operation.receiptGeneration -ne 1 -or
            $Operation.predecessorAuthorityId -cne '(none)' -or $Operation.receiptStatus -cne 'Accepted' -or
            $Operation.commandIdentity -cne $Operation.authorityId -or $Operation.commandFingerprint -cne $Operation.authorityId -or
            @($Envelope.aggregate.operations).Count -ne 1 -or
            $Envelope.aggregate.afterStateGeneration -ne ($Envelope.aggregate.beforeStateGeneration + 1) -or
            $Envelope.aggregate.beforeStateFingerprint -ceq $Envelope.aggregate.afterStateFingerprint -or
            -not (Test-FileTransferCheckpointRepairContinuityFields $payload $script:FileTransferCoordinatorInitialDataSessionCatalog.payloadFields) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $payload.sourceBoundary $before) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $payload.effectiveBoundary $after)) { return $false }
        foreach ($name in @('authorityId','commandIdentity','commandFingerprint','receiptIdentity','receiptFingerprint')) {
            if ([string]$Operation.$name -in @('', '(none)')) { return $false }
        }
        foreach ($name in @('runtimeOwnerFingerprint','publicationFingerprint')) {
            if ([string]$payload.$name -in @('', '(none)')) { return $false }
        }
        foreach ($name in @('routeRuntime','routeTransition','runtimeUnlockRetry','receiveRecovery','fallbackTailYield',
                'regularV4FrontierRepairPressure','responderProbeHandoff','receiverBarrier','checkpointRepairContinuity','repairOwner')) {
            if ($null -ne (Get-FileTransferCoordinatorSnapshotObjectProperty $Operation $name)) { return $false }
        }
        if ($before.route -cne 'post_tuna_fallback_v6' -or $before.protocolVersion -ne 6 -or
            $before.liveRouteEpoch -ne 0 -or $before.routePublicationGeneration -ne 1 -or
            $before.routeCommitGeneration -ne 0 -or $before.terminalGeneration -ne 0 -or
            $before.runtimeGeneration -le 0 -or $before.legGeneration -le 0 -or
            $before.transportEpoch -ne 0 -or $before.bridgeRecoveryGeneration -ne 0 -or
            $before.checkpointGeneration -ne 0 -or $before.checkpointRequestId -cne '(none)' -or
            $before.dataSessionIdentity -cne '(none)' -or $before.dataSessionGeneration -ne 0 -or
            $after.dataSessionIdentity -in @('', '(none)') -or $after.dataSessionGeneration -ne 1) { return $false }
        foreach ($name in @('route','protocolVersion','liveRouteEpoch','routePublicationGeneration','routeCommitGeneration',
                'runtimeGeneration','terminalGeneration','legGeneration','transportEpoch','bridgeRecoveryGeneration',
                'checkpointGeneration','checkpointRequestId')) {
            if ([string]$before.$name -cne [string]$after.$name) { return $false }
        }
        return $true
    }
    catch { return $false }
}

function Test-FileTransferCoordinatorInitialFallbackStartupAgreement {
    param([object[]]$Steps, [object[]]$StreamBoundaries, $Previous, $Catalog)

    # Negotiation does not replace the owner/publication proof. Its immutable
    # offer-time scope is validated at its own birth, not at publication time.
    $offers = @($StreamBoundaries | Where-Object {
        $_.decisionKind -ceq 'PinFallbackBootstrapOffer' -and
        [long]$_.afterStateGeneration -le [long]$Previous.Boundary.beforeStateGeneration
    })
    if ($offers.Count -ne 1 -or $Steps.Count -ne 2) { return $false }
    $offer = $offers[0]; $accept = $Steps[0]; $open = $Steps[1]
    if ($offer.decisionStatus -cne 'Accepted' -or $offer.decisionReason -cne 'bootstrap_offer_pinned' -or
        @($offer.operations).Count -ne 1 -or $offer.operations[0].beforeState -cne 'None' -or
        $offer.operations[0].afterState -cne 'Offered' -or
        $accept.decisionKind -cne 'ApplyFallbackBootstrapAccept' -or
        $accept.decisionStatus -cne 'Accepted' -or $accept.decisionReason -cne 'bootstrap_accept_pinned' -or
        @($accept.operations).Count -ne 1 -or $accept.operations[0].beforeState -cne 'Offered' -or
        $open.decisionKind -cne 'ApplyFallbackBootstrapSessionOpen') { return $false }
    if ($Previous.Boundary.direction -ceq 'outbound') {
        if ($accept.operations[0].afterState -cne 'Enabled' -or $open.decisionStatus -cne 'Idempotent' -or
            $open.decisionReason -cne 'bootstrap_session_open_validated' -or @($open.operations).Count -ne 0 -or
            $open.beforeStateGeneration -ne $open.afterStateGeneration -or
            $open.beforeStateHash -cne $open.afterStateHash) { return $false }
    }
    elseif ($Previous.Boundary.direction -ceq 'inbound') {
        if ($accept.operations[0].afterState -cne 'Selected' -or $open.decisionStatus -cne 'Accepted' -or
            $open.decisionReason -cne 'bootstrap_receiver_session_open_applied' -or @($open.operations).Count -ne 1 -or
            $open.operations[0].beforeState -cne 'Selected' -or $open.operations[0].afterState -cne 'Enabled') { return $false }
    }
    else { return $false }
    foreach ($step in @($offer) + $Steps) {
        foreach ($field in @('coordinatorInstanceId','writer','sessionId','transferId','direction')) {
            if ([string]$step.$field -cne [string]$Previous.Boundary.$field) { return $false }
        }
        if (-not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $step.beforeBoundary $step.afterBoundary) -or
            ($step -ne $offer -and -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $step.beforeBoundary $Previous.RouteBoundary))) {
            return $false
        }
        foreach ($operation in @($step.operations)) {
            if ($operation.operationRole -cne 'fallback_bootstrap_agreement' -or
                -not (Test-FileTransferCoordinatorBootstrapCorrelationOperation $step $operation $Catalog) -or
                $operation.predecessorAuthorityId -cne '(none)' -or $operation.receiptValidated -or
                $operation.receiptIdentity -cne '(none)' -or $operation.receiptGeneration -ne 0 -or
                $operation.receiptStatus -cne '(none)' -or $operation.receiptFingerprint -cne '(none)' -or
                $step.afterStateGeneration -ne ($step.beforeStateGeneration + 1) -or
                $step.beforeStateHash -ceq $step.afterStateHash -or
                ($step -ne $offer -and $operation.routeTransition.bootstrapCorrelation.version -ne 1) -or
                -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $operation.routeTransition.sourceBoundary $offer.beforeBoundary) -or
                -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $operation.routeTransition.targetBoundary $offer.beforeBoundary)) {
                return $false
            }
        }
    }
    return @(Get-FileTransferBootstrapCorrelationHistoryErrors -Boundaries (@($offer) + $Steps)).Count -eq 0
}

function Test-FileTransferCoordinatorInitialFallbackStartupInterval {
    param($Boundary, $Previous, $AggregateIndex, $InitialPublications, [string]$CatalogVersion)

    if ($CatalogVersion -cnotin @($script:FileTransferCoordinatorInitialDataSessionCatalogVersion, $script:FileTransferCoordinatorEarlyCheckpointProofCatalogVersion, $script:FileTransferCoordinatorFallbackRepairHandoffCatalogVersion) -or
        $null -ne $Previous.LastTransition -or $null -eq $Previous.RuntimeOwner -or
        $Previous.Operation.operationGeneration -ne 1 -or
        $Previous.RouteBoundary.liveRouteEpoch -ne 0 -or
        $Previous.RouteBoundary.dataSessionGeneration -ne 0 -or
        $Previous.RouteBoundary.dataSessionIdentity -cne '(none)' -or
        $Boundary.beforeBoundary.routePublicationGeneration -ne 1 -or
        $Boundary.beforeBoundary.liveRouteEpoch -ne 0 -or
        $Boundary.beforeBoundary.dataSessionGeneration -ne 1 -or
        @($Boundary.operations).Count -ne 5) { return $false }
    $streamKey = "$($Boundary.coordinatorInstanceId)|$($Boundary.writer)"
    $initialKey = "$streamKey|$($Previous.Boundary.boundaryId)|$($Previous.Operation.operationKey)"
    if ($null -eq $InitialPublications -or -not $InitialPublications.ContainsKey($initialKey) -or
        $InitialPublications[$initialKey].PreviousPublicationKey -cne '(none)' -or
        -not $AggregateIndex.ContainsKey($streamKey)) { return $false }
    $catalog = Get-FileTransferCoordinatorInitialDataSessionCatalog
    $generation = [long]$Previous.Boundary.afterStateGeneration
    $hash = [string]$Previous.Boundary.afterStateHash; $scope = $Previous.RouteBoundary
    $bindingCount = 0
    # Include non-mutating observations: a retire/restore or foreign scope must
    # not disappear just because it did not advance the aggregate generation.
    $steps = @($AggregateIndex[$streamKey].Boundaries.Values | ForEach-Object Boundary | Where-Object {
        $_.boundaryId -cne $Boundary.boundaryId -and $_.boundaryId -cne $Previous.Boundary.boundaryId -and
        [long]$_.beforeStateGeneration -ge $generation -and
        [long]$_.beforeStateGeneration -le [long]$Boundary.beforeStateGeneration
    } | Sort-Object { [long]$_.beforeStateGeneration }, { [long]$_.afterStateGeneration }, { [long]$_.writerSequence })
    $negotiations = @($steps | Where-Object {
        $_.decisionKind -cin @('ApplyFallbackBootstrapAccept','ApplyFallbackBootstrapSessionOpen') -or
        @($_.operations | Where-Object { Test-FileTransferCoordinatorBootstrapCorrelationMarker $_ }).Count -gt 0
    })
    if ($negotiations.Count -gt 0 -and -not (Test-FileTransferCoordinatorInitialFallbackStartupAgreement `
            -Steps $negotiations -StreamBoundaries @($AggregateIndex[$streamKey].Boundaries.Values | ForEach-Object Boundary) `
            -Previous $Previous -Catalog $catalog)) { return $false }
    foreach ($step in $steps) {
        foreach ($field in @('coordinatorInstanceId','writer','sessionId','transferId','direction')) {
            if ([string]$step.$field -cne [string]$Boundary.$field -or
                [string]$Previous.Boundary.$field -cne [string]$Boundary.$field) { return $false }
        }
        if ([long]$step.beforeStateGeneration -ne $generation -or $step.beforeStateHash -cne $hash -or
            [long]$step.afterStateGeneration -notin @($generation, ($generation + 1L)) -or
            (([long]$step.afterStateGeneration -eq $generation) -ne ($step.beforeStateHash -ceq $step.afterStateHash)) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $step.beforeBoundary $scope)) { return $false }
        $bindings = @($step.operations | Where-Object { Test-FileTransferCoordinatorInitialDataSessionMarker $_ })
        if ($bindings.Count -gt 0) {
            $envelope = [pscustomobject]@{
                decisionKind=$step.decisionKind; decisionStatus=$step.decisionStatus; decisionReason=$step.decisionReason
                aggregate=[pscustomobject]@{
                    beforeBoundary=$step.beforeBoundary; afterBoundary=$step.afterBoundary; operations=$step.operations
                    beforeStateGeneration=$step.beforeStateGeneration; afterStateGeneration=$step.afterStateGeneration
                    beforeStateFingerprint=$step.beforeStateHash; afterStateFingerprint=$step.afterStateHash
                }
            }
            if ($bindings.Count -ne 1 -or $bindingCount -ne 0 -or
                -not (Test-FileTransferCoordinatorInitialDataSessionOperation $envelope $bindings[0] $catalog) -or
                $bindings[0].initialDataSessionBinding.runtimeOwnerFingerprint -cne $Previous.RuntimeOwner.commandFingerprint -or
                $bindings[0].initialDataSessionBinding.publicationFingerprint -cne $Previous.Operation.commandFingerprint) { return $false }
            $bindingCount++
        }
        else {
            if (-not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $step.beforeBoundary $step.afterBoundary)) { return $false }
            foreach ($operation in @($step.operations)) {
                if ($operation.operationRole -ceq 'fallback_bootstrap_agreement') { continue }
                $prior = if ($operation.operationKey -ceq $Previous.Operation.operationKey) { $Previous.Operation }
                    elseif ($operation.operationKey -ceq $Previous.RuntimeOwner.operationKey) { $Previous.RuntimeOwner } else { $null }
                if ($null -eq $prior -or -not $operation.stateApplied -or
                    $operation.beforeState -cne $prior.afterState -or $operation.afterState -cne $prior.afterState -or
                    $operation.routeRuntime.tokenFingerprint -cne $prior.routeRuntime.tokenFingerprint -or
                    -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $operation.routeRuntime.boundary $scope)) { return $false }
                foreach ($field in @('family','operationRole','operationKind','authorityId','operationGeneration','predecessorAuthorityId',
                        'commandIdentity','commandGeneration','commandFingerprint','receiptIdentity','receiptGeneration',
                        'receiptStatus','receiptFingerprint','receiptValidated')) {
                    if ([string]$operation.$field -cne [string]$prior.$field) { return $false }
                }
            }
        }
        $generation = [long]$step.afterStateGeneration; $hash = [string]$step.afterStateHash; $scope = $step.afterBoundary
    }
    return $bindingCount -eq 1 -and $generation -eq [long]$Boundary.beforeStateGeneration -and
        $hash -ceq $Boundary.beforeStateHash -and
        (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $scope $Boundary.beforeBoundary)
}

function Get-FileTransferCoordinatorInitialFallbackStartupPublications {
    param($AggregateIndex, $InitialPublications, [object[]]$Effects, [string]$CatalogVersion)

    $result = @{}
    if ($CatalogVersion -cnotin @($script:FileTransferCoordinatorInitialDataSessionCatalogVersion, $script:FileTransferCoordinatorEarlyCheckpointProofCatalogVersion, $script:FileTransferCoordinatorFallbackRepairHandoffCatalogVersion)) { return $result }
    foreach ($streamKey in $AggregateIndex.Keys) {
        $boundaries = @($AggregateIndex[$streamKey].Boundaries.Values | ForEach-Object Boundary)
        $initial = @(foreach ($boundary in $boundaries) {
            foreach ($operation in @($boundary.operations | Where-Object {
                $_.family -ceq 'route_runtime' -and $_.operationRole -ceq 'route_publication' -and
                $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Published' -and $_.operationGeneration -eq 1
            })) {
                $key = "$streamKey|$($boundary.boundaryId)|$($operation.operationKey)"
                if ($InitialPublications.ContainsKey($key)) {
                    [pscustomobject]@{ Boundary=$boundary; Operation=$operation; RouteBoundary=$operation.routeRuntime.boundary
                        RuntimeOwner=$InitialPublications[$key].RuntimeOwner; LastTransition=$null; Valid=$true }
                }
            }
        })
        if ($initial.Count -ne 1) { continue }
        foreach ($boundary in @($boundaries | Where-Object {
            $_.decisionKind -ceq 'StartTransfer' -and $_.decisionReason -ceq 'post_tuna_fallback_v6_route'
        })) {
            $owners = @($boundary.operations | Where-Object { $_.family -ceq 'route_runtime' -and $_.operationRole -ceq 'runtime_owner' })
            $publications = @($boundary.operations | Where-Object { $_.family -ceq 'route_runtime' -and $_.operationRole -ceq 'route_publication' })
            $newOwners = @($owners | Where-Object { $_.operationKind -ceq 'RuntimeOwner' -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Current' -and $_.stateApplied })
            $newPublications = @($publications | Where-Object { $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Published' -and $_.stateApplied })
            if ($newOwners.Count -ne 1 -or $newPublications.Count -ne 1) { continue }
            $entry = [pscustomobject]@{ Boundary=$boundary; Operation=$newPublications[0]; RuntimeOwner=$newOwners[0] }
            if (Test-FileTransferCoordinatorSnapshotFallbackOwnerPublication -Entry $entry -Previous $initial[0] `
                    -RetiredPublications @($publications | Where-Object { $_.beforeState -ceq 'Published' -and $_.afterState -ceq 'None' -and $_.stateApplied }) `
                    -RetiredOwners @($owners | Where-Object { $_.beforeState -ceq 'Current' -and $_.afterState -ceq 'None' -and $_.stateApplied }) `
                    -RuntimeOperations $owners -PublicationOperations $publications -Effects $Effects `
                    -AggregateIndex $AggregateIndex -InitialPublications $InitialPublications -CatalogVersion $CatalogVersion) {
                $result["$streamKey|$($boundary.boundaryId)|$($entry.Operation.operationKey)"] = $true
            }
        }
    }
    return $result
}
