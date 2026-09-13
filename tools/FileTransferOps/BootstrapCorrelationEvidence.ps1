Set-StrictMode -Version Latest

$script:FileTransferCoordinatorBootstrapCorrelationCatalogVersion =
    'snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1-fallback-bootstrap-correlation-v1'
$script:FileTransferCoordinatorBootstrapCorrelationCatalogHash = 'ebbaf19d291129caa47b34e69395de2b38985661560791c7c762c437ed6e679b'
$script:FileTransferCoordinatorBootstrapCorrelationCatalog = [ordered]@{
    version = 1
    family = 'route_transition'
    operationKinds = @('FallbackBootstrapAgreement', 'FallbackBootstrapExchange', 'FallbackBootstrapPhase')
    operationRoles = @('fallback_bootstrap_agreement', 'fallback_bootstrap_exchange', 'fallback_bootstrap_phase', 'fallback_bootstrap_attempt')
    decisionKinds = @('PinFallbackBootstrapOffer', 'ApplyFallbackBootstrapAccept', 'ApplyFallbackBootstrapSessionOpen',
        'StartFallbackBootstrapExchange', 'ObserveFallbackBootstrapPeerFrame', 'ApplyFallbackBootstrapPhaseReceipt',
        'InvalidateFallbackBootstrapExchange')
    agreementStates = @('Offered', 'Selected', 'Enabled')
    agreementTransitions = [ordered]@{
        outbound = [ordered]@{
            'None:Offered:0'='PinFallbackBootstrapOffer'
            'Offered:Selected:0'='ApplyFallbackBootstrapAccept'
            'Offered:Enabled:1'='ApplyFallbackBootstrapAccept'
        }
        inbound = [ordered]@{
            'None:Offered:0'='PinFallbackBootstrapOffer'
            'Offered:Selected:0'='ApplyFallbackBootstrapAccept'
            'Offered:Selected:1'='ApplyFallbackBootstrapAccept'
            'Selected:Selected:0'='ApplyFallbackBootstrapSessionOpen'
            'Selected:Enabled:1'='ApplyFallbackBootstrapSessionOpen'
        }
    }
    exchangeStates = @('Pending', 'Completed', 'Invalidated')
    phaseStates = @('Issued', 'Held', 'Delivered', 'Settled', 'Ready', 'Failed')
    phaseProgress = 'coordinator-receipt-and-history-v1'
    agreementBinding = 'pinned-offer-immutable-accept-single-legacy-open-v1'
    phases = @('Solicit', 'Announce', 'Response', 'Confirm')
    receiptStages = @('bootstrap_phase_delivered', 'bootstrap_phase_settled', 'bootstrap_phase_failed')
    effectKinds = @()
    payloadName = 'bootstrapCorrelation'
    payloadFields = [ordered]@{
        version='int64'; agreementEnabled='bool'; agreementFingerprint='string'
        senderIssuerFingerprint='string'; receiverIssuerFingerprint='string'
        firstPhase='string'; phase='string'; localIdFingerprint='string'; peerIdFingerprint='string'
        completedPeerIdFingerprint='string'; retainedPeerFrameIdentityFingerprint='string'; predecessorIdentity='string'
        epochFrameFingerprint='string'; proofFrameFingerprint='string'; proofFrameIdentityFingerprint='string'
        targetTransportEpoch='int64'
        held='bool'; submissionSealed='bool'; completed='bool'; invalidated='bool'
        deliveryCopies='BootstrapPhysicalCopy[]'; settlementCopies='BootstrapPhysicalCopy[]'; failureCopies='BootstrapPhysicalCopy[]'
    }
    copyFields = [ordered]@{
        lane='string'; frameType='string'; transportEpoch='int64'; frameFingerprint='string'; outcome='int64'
    }
    copyOutcomes = [ordered]@{ Pending=0; Succeeded=1; Failed=2; Canceled=3 }
    baseRecordTransitions = @('None:Pending', 'Pending:Delivered', 'Delivered:None')
    successorRetirement = 'completed-exchange-committed-reactivation-v1'
    receiptFields = [ordered]@{
        stage='string'; commandIdentity='string'; commandGeneration='int64'; commandFingerprint='string'
        receiptIdentity='string'; receiptGeneration='int64'; receiptStatus='string'; receiptReason='string'
        receiptFingerprint='string'; receiptValidated='bool'
    }
}

function Get-FileTransferCoordinatorBootstrapCorrelationCatalog {
    $parent = Get-FileTransferCoordinatorEvidenceCatalog -ProfileVersion $script:FileTransferCoordinatorCheckpointRepairContinuityCatalogVersion
    $json = ConvertTo-Json -InputObject $script:FileTransferCoordinatorBootstrapCorrelationCatalog -Depth 16 -Compress
    $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes (
        [Text.UTF8Encoding]::new($false).GetBytes("parentHash=$($parent.Hash)`nBootstrapCorrelationCatalog=$json"))
    if ($hash -cne $script:FileTransferCoordinatorBootstrapCorrelationCatalogHash) {
        throw "coordinator_snapshot_bootstrap_correlation_catalog_hash_invalid:$hash"
    }
    $extended = [ordered]@{}
    foreach ($property in $parent.PSObject.Properties) { $extended[$property.Name] = $property.Value }
    $extended.Version = $script:FileTransferCoordinatorBootstrapCorrelationCatalogVersion
    $extended.Hash = $hash
    $extended.ParentCatalogHash = $parent.Hash
    $extended.BootstrapCorrelationCatalog = $script:FileTransferCoordinatorBootstrapCorrelationCatalog
    return [pscustomobject]$extended
}

function Test-FileTransferCoordinatorBootstrapCorrelationMarker {
    param($Operation)
    if ($null -eq $Operation) { return $false }
    return ($Operation.PSObject.Properties.Name -contains 'operationRole' -and
            $Operation.operationRole -cin $script:FileTransferCoordinatorBootstrapCorrelationCatalog.operationRoles) -or
        ($Operation.PSObject.Properties.Name -contains 'operationKind' -and
            $Operation.operationKind -cin $script:FileTransferCoordinatorBootstrapCorrelationCatalog.operationKinds) -or
        ($Operation.PSObject.Properties.Name -contains 'routeTransition' -and $null -ne $Operation.routeTransition -and
            $Operation.routeTransition.PSObject.Properties.Name -contains 'bootstrapCorrelation')
}

function Test-FileTransferBootstrapEvidenceFields {
    param($Value, $Fields)
    if (-not (Test-FileTransferCheckpointRepairContinuityFields $Value $Fields)) { return $false }
    foreach ($name in $Fields.Keys) {
        if ($Fields[$name] -ceq 'bool' -and $Value.$name -isnot [bool]) { return $false }
        if ($Fields[$name].EndsWith('[]') -and $Value.$name -isnot [array]) { return $false }
    }
    return $true
}

function Test-FileTransferBootstrapPhysicalCopies {
    param($Copies, $Payload, [string]$Status)
    if (@($Copies).Count -gt 4 -or ($Status -cne 'Failed' -and @($Copies).Count -eq 0)) { return $false }
    $seen = @{}
    $proofType = if ($Payload.phase -cin @('Solicit', 'Confirm')) {
        'filetransfer.fallback_bootstrap_request.v6'
    } else { 'filetransfer.fallback_bootstrap_state.v6' }
    foreach ($copy in @($Copies)) {
        if (-not (Test-FileTransferBootstrapEvidenceFields $copy $script:FileTransferCoordinatorBootstrapCorrelationCatalog.copyFields) -or
            $copy.lane -cnotin @('control', 'bulk') -or $copy.transportEpoch -ne $Payload.targetTransportEpoch -or
            $copy.frameFingerprint -cnotmatch '^(?:[a-f0-9]{64}|fte1-[a-f0-9]{24})$' -or
            $copy.outcome -notin @(0,1,2,3) -or ($Status -ceq 'Settled' -and $copy.outcome -eq 0)) { return $false }
        if (-not (($copy.frameType -ceq $proofType -and $copy.frameFingerprint -ceq $Payload.proofFrameFingerprint) -or
            ($copy.frameType -ceq 'filetransfer.transport_epoch.v6' -and $copy.frameFingerprint -ceq $Payload.epochFrameFingerprint))) { return $false }
        $key = "$($copy.lane)|$($copy.frameFingerprint)"
        if ($seen.ContainsKey($key)) { return $false }
        $seen[$key] = $true
    }
    if ($Status -cin @('Delivered','Failed')) {
        $delivered = $true
        foreach ($fingerprint in @($Payload.proofFrameFingerprint, $Payload.epochFrameFingerprint) | Where-Object { $_ -cne '(none)' }) {
            if (@($Copies | Where-Object { $_.frameFingerprint -ceq $fingerprint -and $_.outcome -eq 1 }).Count -eq 0) { $delivered = $false }
        }
        if (($Status -ceq 'Delivered') -ne $delivered) { return $false }
    }
    return $true
}

function Test-FileTransferCoordinatorBootstrapCorrelationOperation {
    param($Envelope, $Operation, $Catalog)
    try {
        $definition = $script:FileTransferCoordinatorBootstrapCorrelationCatalog
        if (-not (Test-FileTransferCoordinatorBootstrapCorrelationCatalogCapability $Catalog.Version) -or
            $Operation.family -cne 'route_transition' -or $Operation.operationRole -cnotin $definition.operationRoles -or
            $Operation.operationKey -cne "route_transition|$($Operation.operationRole)|$($Operation.authorityId)|$($Operation.operationGeneration)|$($Operation.operationKind)" -or
            $Operation.commandIdentity -cne $Operation.authorityId -or $Operation.commandGeneration -ne $Operation.operationGeneration -or
            $Operation.stateApplied -isnot [bool] -or $Operation.receiptValidated -isnot [bool]) { return $false }
        $typed = $Operation.routeTransition
        if ($null -eq $typed -or $typed.role -cne $Operation.operationRole -or
            $typed.transitionKind -cne 'TunaV4ToPostTunaFallbackV6' -or
            $typed.proofBoundaryKind -cne 'fallback-bootstrap-correlation-v1' -or
            $typed.proofAdmissionMode -cne 'fallback-bootstrap-correlation-v1' -or
            $typed.targetRoute -cne 'post_tuna_fallback_v6' -or $typed.targetProtocolVersion -ne 6 -or
            $typed.sourceTransport -cne 'Tuna' -or $typed.targetTransport -cne 'RegularNkn' -or
            $typed.handoffKind -cne 'TunaToNormalFallback' -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $typed.sourceBoundary) -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $typed.sourceBoundary $typed.targetBoundary)) { return $false }
        foreach ($name in @('routeRuntime','runtimeUnlockRetry','receiveRecovery','fallbackTailYield',
                'regularV4FrontierRepairPressure','responderProbeHandoff','receiverBarrier','checkpointRepairContinuity')) {
            if ($Operation.PSObject.Properties.Name -contains $name -and $null -ne $Operation.$name) { return $false }
        }
        $receipts = @($typed.receipts)
        if ($Operation.operationRole -ceq 'fallback_bootstrap_attempt') {
            $decisionKinds=@($definition.decisionKinds)
            if ($Catalog.Version -cin @($script:FileTransferCoordinatorBootstrapInitiationCatalogVersion, $script:FileTransferCoordinatorBootstrapCallbackCatalogVersion, $script:FileTransferCoordinatorRepairOwnerCatalogVersion, $script:FileTransferCoordinatorActivationRetryCatalogVersion)) {
                $decisionKinds+=@($script:FileTransferCoordinatorBootstrapInitiationCatalog.decisionKinds)
            }
            return $Envelope.decisionKind -cin $decisionKinds -and $Operation.operationKind -ceq $Envelope.decisionKind -and
                $Envelope.decisionStatus -ceq 'Rejected' -and -not $Operation.stateApplied -and -not $Operation.receiptValidated -and
                $Operation.beforeState -ceq 'None' -and $Operation.afterState -ceq 'Rejected' -and $receipts.Count -eq 0 -and
                $Operation.receiptIdentity -ceq '(none)' -and $Operation.receiptStatus -ceq '(none)' -and $Operation.receiptGeneration -eq 0 -and
                $typed.PSObject.Properties.Name -cnotcontains 'bootstrapCorrelation' -and
                $Envelope.aggregate.beforeStateGeneration -eq $Envelope.aggregate.afterStateGeneration -and
                $Envelope.aggregate.beforeStateFingerprint -ceq $Envelope.aggregate.afterStateFingerprint
        }
        $payload = $typed.bootstrapCorrelation
        if (-not (Test-FileTransferBootstrapEvidenceFields $payload $definition.payloadFields) -or
            -not $Operation.stateApplied -or $payload.agreementFingerprint -ceq '(none)' -or
            ($payload.completed -and $payload.invalidated)) { return $false }
        foreach ($name in @('agreementFingerprint','senderIssuerFingerprint','receiverIssuerFingerprint',
                'localIdFingerprint','peerIdFingerprint','completedPeerIdFingerprint','retainedPeerFrameIdentityFingerprint')) {
            if ($payload.$name -cnotmatch '^(?:[a-f0-9]{64}|fte1-[a-f0-9]{24}|\(none\))$') { return $false }
        }
        if ($Operation.operationRole -ceq 'fallback_bootstrap_agreement') {
            if ($payload.version -eq 0) {
                if ($Operation.afterState -ceq 'Enabled' -or $payload.receiverIssuerFingerprint -cne '(none)') { return $false }
            } elseif ($payload.version -eq 1) {
                if ($payload.senderIssuerFingerprint -ceq '(none)' -or $payload.receiverIssuerFingerprint -ceq '(none)' -or
                    $Operation.afterState -ceq 'Offered' -or
                    ($Operation.afterState -ceq 'Selected' -and $Envelope.direction -cne 'inbound')) { return $false }
            } else { return $false }
            if ($Operation.beforeState -cne $Operation.afterState) {
                $transitions = $definition.agreementTransitions.($Envelope.direction)
                $key = "$($Operation.beforeState):$($Operation.afterState):$($payload.version)"
                if ($key -cnotin @($transitions.Keys) -or $Envelope.decisionKind -cne $transitions[$key] -or
                    $Envelope.decisionStatus -cne 'Accepted') { return $false }
            }
            return $Operation.operationKind -ceq 'FallbackBootstrapAgreement' -and
                $Operation.operationGeneration -eq 1 -and
                $payload.agreementFingerprint -ceq $Operation.commandFingerprint -and
                $Operation.beforeState -cin (@('None') + $definition.agreementStates) -and
                $Operation.afterState -cin $definition.agreementStates -and
                $payload.version -in @(0,1) -and ($payload.agreementEnabled -eq ($Operation.afterState -ceq 'Enabled')) -and
                $payload.phase -ceq '(none)' -and $payload.firstPhase -ceq '(none)' -and $receipts.Count -eq 0 -and
                -not $payload.completed -and -not $payload.invalidated -and -not $payload.held -and -not $payload.submissionSealed -and
                @($payload.deliveryCopies).Count -eq 0 -and @($payload.settlementCopies).Count -eq 0 -and @($payload.failureCopies).Count -eq 0
        }
        if ($payload.version -ne 1 -or -not $payload.agreementEnabled -or
            $payload.targetTransportEpoch -le 0 -or
            $payload.localIdFingerprint -ceq '(none)' -or $payload.senderIssuerFingerprint -ceq '(none)' -or
            $payload.receiverIssuerFingerprint -ceq '(none)' -or
            ($payload.completed -and ($payload.peerIdFingerprint -ceq '(none)' -or
                $payload.retainedPeerFrameIdentityFingerprint -ceq '(none)' -or
                $payload.completedPeerIdFingerprint -cne $payload.peerIdFingerprint)) -or
            $typed.sourceBoundary.route -cne 'file_tuna_v4' -or $typed.sourceBoundary.protocolVersion -ne 4 -or
            $payload.firstPhase -cnotin @('Solicit','Announce','Response') -or
            ($Envelope.direction -ceq 'outbound' -and $payload.firstPhase -cne 'Solicit') -or
            ($Envelope.direction -ceq 'inbound' -and $payload.firstPhase -ceq 'Solicit')) { return $false }
        if ($Operation.operationRole -ceq 'fallback_bootstrap_exchange') {
            return $Operation.operationKind -ceq 'FallbackBootstrapExchange' -and
                $Operation.beforeState -cin (@('None') + $definition.exchangeStates) -and
                $Operation.afterState -cin (@('None') + $definition.exchangeStates) -and
                ($Operation.afterState -ceq 'None' -or ($payload.completed -eq ($Operation.afterState -ceq 'Completed') -and
                    $payload.invalidated -eq ($Operation.afterState -ceq 'Invalidated'))) -and
                $payload.phase -ceq '(none)' -and $receipts.Count -eq 0 -and -not $payload.held -and -not $payload.submissionSealed -and
                @($payload.deliveryCopies).Count -eq 0 -and @($payload.settlementCopies).Count -eq 0 -and @($payload.failureCopies).Count -eq 0
        }
        if ($Operation.operationKind -cne 'FallbackBootstrapPhase' -or $payload.phase -cnotin $definition.phases -or
            $Operation.beforeState -cnotin (@('None') + $definition.phaseStates) -or
            ($Envelope.direction -ceq 'outbound' -and $payload.phase -cnotin @('Solicit','Confirm')) -or
            ($Envelope.direction -ceq 'inbound' -and $payload.phase -cnotin @('Announce','Response')) -or
            $payload.proofFrameFingerprint -cnotmatch '^(?:[a-f0-9]{64}|fte1-[a-f0-9]{24})$' -or
            $payload.proofFrameIdentityFingerprint -cnotmatch '^(?:[a-f0-9]{64}|fte1-[a-f0-9]{24})$' -or
            $Operation.predecessorAuthorityId -cne $payload.predecessorIdentity) { return $false }
        $first = $payload.phase -ceq $payload.firstPhase
        if ($Operation.beforeState -cne $Operation.afterState) {
            if ($Envelope.decisionStatus -cne 'Accepted') { return $false }
            if ($Operation.afterState -ceq 'Issued') {
                $issuers = if ($first) { @('StartFallbackBootstrapExchange') }
                    else { @('ObserveFallbackBootstrapPeerFrame', 'ApplyFallbackBootstrapPhaseReceipt') }
                if ($Operation.beforeState -cne 'None' -or $Envelope.decisionKind -cnotin $issuers) { return $false }
            } elseif ($Operation.afterState -ceq 'None') {
                if ($Operation.beforeState -cne 'Ready' -or $Envelope.decisionKind -cne 'StartFallbackBootstrapExchange') { return $false }
            } elseif ($Operation.beforeState -cin @('None','Ready','Failed') -or
                $Envelope.decisionKind -cne 'ApplyFallbackBootstrapPhaseReceipt') { return $false }
        }
        if (($first -and ($payload.predecessorIdentity -cne '(none)' -or $payload.epochFrameFingerprint -ceq '(none)')) -or
            (-not $first -and ($payload.predecessorIdentity -ceq '(none)' -or $payload.epochFrameFingerprint -cne '(none)'))) { return $false }
        $statuses = @{}
        foreach ($receipt in $receipts) {
            if (-not (Test-FileTransferBootstrapEvidenceFields $receipt $definition.receiptFields) -or
                $receipt.receiptStatus -cnotin @('Delivered','Settled','Failed') -or
                $receipt.stage -cne ('bootstrap_phase_' + $receipt.receiptStatus.ToLowerInvariant()) -or
                -not $receipt.receiptValidated -or $statuses.ContainsKey($receipt.receiptStatus)) { return $false }
            foreach ($name in @('commandIdentity','commandGeneration','commandFingerprint')) {
                if ([string]$receipt.$name -cne [string]$Operation.$name) { return $false }
            }
            if ($receipt.receiptGeneration -ne $Operation.operationGeneration -or $receipt.receiptIdentity -ceq '(none)' -or
                $receipt.receiptFingerprint -cnotmatch '^(?:[a-f0-9]{64}|fte1-[a-f0-9]{24})$') { return $false }
            $statuses[$receipt.receiptStatus] = $receipt
        }
        foreach ($pair in @(@('Delivered','deliveryCopies'), @('Settled','settlementCopies'), @('Failed','failureCopies'))) {
            if (($statuses.ContainsKey($pair[0]) -and -not (Test-FileTransferBootstrapPhysicalCopies $payload.($pair[1]) $payload $pair[0])) -or
                (-not $statuses.ContainsKey($pair[0]) -and @($payload.($pair[1])).Count -ne 0)) { return $false }
        }
        if ($statuses.Count -gt 0) {
            if (-not $payload.submissionSealed -or $payload.held -or -not $Operation.receiptValidated) { return $false }
            $last = $receipts[-1]
            foreach ($name in @('receiptIdentity','receiptGeneration','receiptStatus','receiptFingerprint')) {
                if ([string]$Operation.$name -cne [string]$last.$name) { return $false }
            }
        } elseif ($payload.submissionSealed -or $Operation.receiptValidated -or $Operation.receiptIdentity -cne '(none)') { return $false }
        if ($statuses.ContainsKey('Delivered') -and $statuses.ContainsKey('Settled')) {
            if (@($payload.deliveryCopies).Count -ne @($payload.settlementCopies).Count) { return $false }
            foreach ($copy in $payload.deliveryCopies) {
                $match = @($payload.settlementCopies | Where-Object { $_.lane -ceq $copy.lane -and
                    $_.frameFingerprint -ceq $copy.frameFingerprint -and $_.frameType -ceq $copy.frameType -and
                    $_.transportEpoch -eq $copy.transportEpoch -and ($copy.outcome -eq 0 -or $_.outcome -eq $copy.outcome) })
                if ($match.Count -ne 1) { return $false }
            }
        }
        $expected = if ($statuses.ContainsKey('Failed')) { 'Failed' }
            elseif ($statuses.ContainsKey('Delivered') -and $statuses.ContainsKey('Settled')) { 'Ready' }
            elseif ($payload.held) { 'Held' } elseif ($statuses.ContainsKey('Delivered')) { 'Delivered' }
            elseif ($statuses.ContainsKey('Settled')) { 'Settled' } else { 'Issued' }
        return $Operation.afterState -cin @('None', $expected) -and
            (-not $payload.completed -or $expected -ceq 'Ready') -and
            (-not $statuses.ContainsKey('Failed') -or $payload.invalidated)
    } catch { return $false }
}

function Test-FileTransferBootstrapCorrelationBaseRecord {
    param($Envelope, $Operation, $Catalog)
    if ($null -eq $Catalog -or -not (Test-FileTransferCoordinatorBootstrapCorrelationCatalogCapability $Catalog.Version) -or
        $Operation.family -cne 'route_transition' -or $Operation.operationRole -cne 'fallback_v6_bootstrap' -or
        $Operation.operationKind -cne 'FallbackV6ProofBootstrap' -or -not $Operation.stateApplied -or
        $Envelope.decisionStatus -cne 'Accepted') { return $false }
    $exchanges = @($Envelope.aggregate.operations | Where-Object {
        $_.operationRole -ceq 'fallback_bootstrap_exchange' -and $_.authorityId -ceq $Operation.authorityId -and
        $_.operationGeneration -eq $Operation.operationGeneration -and
        (Test-FileTransferCoordinatorBootstrapCorrelationOperation $Envelope $_ $Catalog)
    })
    if ($exchanges.Count -ne 1) { return $false }
    $exchange = $exchanges[0].routeTransition.bootstrapCorrelation
    if ($Operation.beforeState -ceq 'Delivered' -and $Operation.afterState -ceq 'None') {
        return $null -ne (Get-FileTransferBootstrapCorrelationRetirementLink $Envelope $Operation $Catalog)
    }
    $phases = @($Envelope.aggregate.operations | Where-Object {
        $_.operationRole -ceq 'fallback_bootstrap_phase' -and $_.operationGeneration -eq $Operation.operationGeneration -and
        (Test-FileTransferCoordinatorBootstrapCorrelationOperation $Envelope $_ $Catalog) -and
        $_.routeTransition.bootstrapCorrelation.phase -ceq $exchange.firstPhase -and
        $_.routeTransition.bootstrapCorrelation.localIdFingerprint -ceq $exchange.localIdFingerprint -and
        (Test-FileTransferCoordinatorSnapshotBoundaryEqual $_.routeTransition.sourceBoundary $Operation.routeTransition.sourceBoundary)
    })
    if ($phases.Count -ne 1) { return $false }
    if ($Operation.beforeState -ceq 'None' -and $Operation.afterState -ceq 'Pending') {
        return $Envelope.decisionKind -ceq 'StartFallbackBootstrapExchange' -and $phases[0].afterState -ceq 'Issued' -and
            @($Operation.routeTransition.receipts).Count -eq 0 -and -not $Operation.receiptValidated
    }
    $receipt = @($Operation.routeTransition.receipts)
    $delivery = @($phases[0].routeTransition.receipts | Where-Object receiptStatus -CEQ 'Delivered')
    return $Envelope.decisionKind -ceq 'ApplyFallbackBootstrapPhaseReceipt' -and
        $Operation.beforeState -ceq 'Pending' -and $Operation.afterState -ceq 'Delivered' -and $Operation.receiptValidated -and
        $receipt.Count -eq 1 -and $delivery.Count -eq 1 -and $receipt[0].stage -ceq 'fallback_v6_bootstrap' -and
        $receipt[0].receiptStatus -ceq 'Delivered' -and $receipt[0].receiptReason -ceq 'bootstrap_first_phase_delivered' -and
        $Operation.routeTransition.retainedFrameFingerprint -ceq $phases[0].routeTransition.bootstrapCorrelation.proofFrameIdentityFingerprint
}

function Get-FileTransferBootstrapCorrelationRetirementLink {
    param($Envelope, $Retired, $Catalog)
    if ($null -eq $Catalog -or -not (Test-FileTransferCoordinatorBootstrapCorrelationCatalogCapability $Catalog.Version) -or
        $Envelope.decisionKind -cne 'StartFallbackBootstrapExchange' -or $Envelope.decisionStatus -cne 'Accepted' -or
        $Retired.operationRole -cne 'fallback_v6_bootstrap' -or $Retired.operationKind -cne 'FallbackV6ProofBootstrap' -or
        $Retired.beforeState -cne 'Delivered' -or $Retired.afterState -cne 'None' -or -not $Retired.stateApplied -or
        -not $Retired.receiptValidated -or $Retired.receiptStatus -cne 'Delivered') { return $null }
    $operations = @($Envelope.aggregate.operations)
    $old = @($operations | Where-Object { $_.operationRole -ceq 'fallback_bootstrap_exchange' -and
        $_.authorityId -ceq $Retired.authorityId -and $_.operationGeneration -eq $Retired.operationGeneration -and
        $_.beforeState -ceq 'Completed' -and $_.afterState -ceq 'None' -and
        (Test-FileTransferCoordinatorBootstrapCorrelationOperation $Envelope $_ $Catalog) })
    $next = @($operations | Where-Object { $_.operationRole -ceq 'fallback_bootstrap_exchange' -and
        $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Pending' -and
        $_.operationGeneration -eq ([long]$Retired.operationGeneration + 1) -and $_.authorityId -cne $Retired.authorityId -and
        (Test-FileTransferCoordinatorBootstrapCorrelationOperation $Envelope $_ $Catalog) })
    if ($old.Count -ne 1 -or $next.Count -ne 1) { return $null }
    $prior = $old[0].routeTransition.bootstrapCorrelation; $current = $next[0].routeTransition.bootstrapCorrelation
    if (-not $prior.completed -or $prior.invalidated -or $current.completed -or $current.invalidated -or
        $prior.localIdFingerprint -ceq $current.localIdFingerprint -or
        $prior.completedPeerIdFingerprint -ceq '(none)') { return $null }
    foreach ($field in @('agreementFingerprint','senderIssuerFingerprint','receiverIssuerFingerprint','completedPeerIdFingerprint')) {
        if ($prior.$field -cne $current.$field) { return $null }
    }
    $source = $old[0].routeTransition.sourceBoundary; $target = $next[0].routeTransition.sourceBoundary
    # The legacy-shaped base descriptor carries the current aggregate boundary;
    # the correlated exchange/phase descriptors retain the command's frozen source.
    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $target $Retired.routeTransition.sourceBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $target $Envelope.aggregate.beforeBoundary) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $target $Envelope.aggregate.afterBoundary) -or
        [long]$target.liveRouteEpoch -ne ([long]$source.liveRouteEpoch + 2)) { return $null }
    foreach ($field in @('terminalGeneration','dataSessionIdentity','dataSessionGeneration')) {
        if ([string]$source.$field -cne [string]$target.$field) { return $null }
    }
    $phases = @($operations | Where-Object { $_.operationRole -ceq 'fallback_bootstrap_phase' -and
        $_.operationGeneration -eq $Retired.operationGeneration -and $_.beforeState -ceq 'Ready' -and $_.afterState -ceq 'None' -and
        $_.routeTransition.bootstrapCorrelation.localIdFingerprint -ceq $prior.localIdFingerprint -and
        (Test-FileTransferCoordinatorSnapshotBoundaryEqual $_.routeTransition.sourceBoundary $source) -and
        (Test-FileTransferCoordinatorBootstrapCorrelationOperation $Envelope $_ $Catalog) })
    $expected = if ($Envelope.direction -ceq 'outbound' -or $prior.firstPhase -ceq 'Announce') { 2 } else { 1 }
    $first = @($phases | Where-Object { $_.routeTransition.bootstrapCorrelation.phase -ceq $prior.firstPhase })
    $baseReceipt = @($Retired.routeTransition.receipts)
    if ($phases.Count -ne $expected -or $first.Count -ne 1 -or $baseReceipt.Count -ne 1 -or
        $baseReceipt[0].stage -cne 'fallback_v6_bootstrap' -or $baseReceipt[0].receiptReason -cne 'bootstrap_first_phase_delivered' -or
        $Retired.routeTransition.retainedFrameFingerprint -cne $first[0].routeTransition.bootstrapCorrelation.proofFrameIdentityFingerprint) { return $null }
    foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity','receiptGeneration','receiptStatus','receiptFingerprint','receiptValidated')) {
        if ([string]$baseReceipt[0].$field -cne [string]$Retired.$field) { return $null }
    }
    $newBase = @($operations | Where-Object { $_.operationRole -ceq 'fallback_v6_bootstrap' -and
        $_.authorityId -ceq $next[0].authorityId -and (Test-FileTransferBootstrapCorrelationBaseRecord $Envelope $_ $Catalog) })
    if ($newBase.Count -ne 1) { return $null }
    return [pscustomobject]@{ Retired=$Retired; PreviousExchange=$old[0]; SuccessorExchange=$next[0]; Successor=$newBase[0]; Phases=$phases }
}

function Test-FileTransferBootstrapCorrelationRetirementHistory {
    param($Link, [object[]]$History)
    try {
        if ($null -eq $Link) { return $false }
        $entries = @(foreach ($boundary in $History) {
            foreach ($operation in $boundary.operations) { [pscustomobject]@{ Boundary=$boundary; Operation=$operation } }
        })
        $completedAt = -1L
        foreach ($retired in @($Link.Retired, $Link.PreviousExchange) + @($Link.Phases)) {
            $prior = @($entries | Where-Object { $_.Operation.operationKey -ceq $retired.operationKey } | Select-Object -Last 1)
            if ($prior.Count -ne 1) { return $false }
            $operation = $prior[0].Operation
            if ($operation.afterState -cne $retired.beforeState -or -not $operation.stateApplied) { return $false }
            foreach ($field in @('commandIdentity','commandGeneration','commandFingerprint','receiptIdentity',
                    'receiptGeneration','receiptStatus','receiptFingerprint','receiptValidated')) {
                if ([string]$operation.$field -cne [string]$retired.$field) { return $false }
            }
            if ((ConvertTo-Json @($operation.routeTransition.receipts) -Depth 16 -Compress) -cne
                (ConvertTo-Json @($retired.routeTransition.receipts) -Depth 16 -Compress)) { return $false }
            if ($retired.operationRole -cne 'fallback_v6_bootstrap' -and
                -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $operation.routeTransition.sourceBoundary $retired.routeTransition.sourceBoundary)) { return $false }
            if ($retired.operationRole -ceq 'fallback_bootstrap_exchange') {
                if ((ConvertTo-Json $operation.routeTransition.bootstrapCorrelation -Depth 16 -Compress) -cne
                    (ConvertTo-Json $retired.routeTransition.bootstrapCorrelation -Depth 16 -Compress)) { return $false }
                $completedAt = [long]$prior[0].Boundary.evidenceLineIndex
            }
        }
        $cycle = @($History | Where-Object { $_.evidenceLineIndex -gt $completedAt })
        $commits = @(foreach ($boundary in $cycle) {
            foreach ($operation in @($boundary.operations | Where-Object { $_.operationRole -ceq 'terminal' -and
                    $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Committed' })) {
                [pscustomobject]@{ Boundary=$boundary; Operation=$operation }
            }
        })
        if ($commits.Count -ne 2) { return $false }
        $source = $Link.PreviousExchange.routeTransition.sourceBoundary
        $target = $Link.SuccessorExchange.routeTransition.sourceBoundary
        $lastDisposition = $null
        for ($i=0; $i -lt 2; $i++) {
            $commit = $commits[$i]; $operation = $commit.Operation; $route = $operation.routeTransition
            $kind = if ($i -eq 0) { 'TunaV4ToPostTunaFallbackV6' } else { 'PostTunaFallbackV6ToTunaV4' }
            if ($commit.Boundary.decisionKind -cne 'CommitFileTransferRouteTransition' -or
                $commit.Boundary.decisionStatus -cne 'Committed' -or -not $operation.stateApplied -or
                $route.transitionKind -cne $kind) { return $false }
            # Reactivation's transport/bridge scope belongs to its prepared proof authority,
            # not the aggregate live-leg projection. Bind it unchanged through prepare/commit/disposition below.
            foreach ($field in @('route','protocolVersion','liveRouteEpoch','routePublicationGeneration','routeCommitGeneration',
                    'runtimeGeneration','terminalGeneration','legGeneration','dataSessionIdentity','dataSessionGeneration')) {
                if ([string]$route.sourceBoundary.$field -cne [string]$commit.Boundary.beforeBoundary.$field) { return $false }
            }
            if ($i -eq 0) {
                if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint $source $route.sourceBoundary) -or
                    -not (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint $route.sourceBoundary $commit.Boundary.beforeBoundary) -or
                    $route.durableCheckpointFingerprint -cne $Link.PreviousExchange.routeTransition.durableCheckpointFingerprint -or
                    $route.retainedFrameFingerprint -cne $Link.PreviousExchange.routeTransition.bootstrapCorrelation.retainedPeerFrameIdentityFingerprint -or
                    $commit.Boundary.afterBoundary.route -cne 'post_tuna_fallback_v6' -or $commit.Boundary.afterBoundary.protocolVersion -ne 6 -or
                    $commit.Boundary.afterBoundary.liveRouteEpoch -ne ([long]$source.liveRouteEpoch + 1)) { return $false }
            } else {
                if ($route.sourceBoundary.route -cne 'post_tuna_fallback_v6' -or $route.sourceBoundary.protocolVersion -ne 6 -or
                    $route.sourceBoundary.liveRouteEpoch -ne ([long]$source.liveRouteEpoch + 1) -or
                    $route.sourceBoundary.transportEpoch -ne $Link.PreviousExchange.routeTransition.bootstrapCorrelation.targetTransportEpoch -or
                    $route.sourceBoundary.terminalGeneration -ne $source.terminalGeneration -or
                    $route.sourceBoundary.dataSessionIdentity -cne $source.dataSessionIdentity -or
                    $route.sourceBoundary.dataSessionGeneration -ne $source.dataSessionGeneration -or
                    -not (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint $commit.Boundary.afterBoundary $target)) { return $false }
            }
            $prepared = @($entries | Where-Object { $_.Boundary.evidenceLineIndex -gt $completedAt -and
                $_.Boundary.evidenceLineIndex -lt $commit.Boundary.evidenceLineIndex -and
                ($null -eq $lastDisposition -or $_.Boundary.evidenceLineIndex -gt $lastDisposition.evidenceLineIndex) -and
                $_.Boundary.decisionKind -ceq 'PrepareFileTransferRouteTransition' -and
                $_.Operation.operationRole -ceq 'current' -and $_.Operation.beforeState -ceq 'None' -and
                $_.Operation.afterState -ceq 'Prepared' -and $_.Operation.stateApplied -and
                $_.Operation.authorityId -ceq $operation.authorityId -and $_.Operation.operationGeneration -eq $operation.operationGeneration })
            $disposed = @($entries | Where-Object { $_.Boundary.evidenceLineIndex -gt $commit.Boundary.evidenceLineIndex -and
                $_.Boundary.decisionKind -ceq 'ApplyFileTransferRouteTransitionDispositionReceipt' -and
                $_.Boundary.decisionStatus -ceq 'Committed' -and $_.Operation.operationRole -ceq 'terminal' -and
                $_.Operation.afterState -ceq 'Committed' -and $_.Operation.stateApplied -and
                $_.Operation.authorityId -ceq $operation.authorityId -and $_.Operation.operationGeneration -eq $operation.operationGeneration })
            if ($prepared.Count -ne 1 -or $disposed.Count -ne 1) { return $false }
            foreach ($terminal in @($commit, $disposed[0])) {
                $projectedTarget = [ordered]@{}
                foreach ($property in $terminal.Operation.routeTransition.targetBoundary.PSObject.Properties) {
                    $projectedTarget[$property.Name] = if ($property.Name -cin @('dataSessionIdentity','checkpointRequestId')) {
                        ConvertTo-FileTransferCoordinatorEvidenceToken $property.Value
                    } else { $property.Value }
                }
                if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual ([pscustomobject]$projectedTarget) $terminal.Boundary.afterBoundary) -or
                    $terminal.Operation.routeTransition.retainedFrameFingerprint -cne $route.retainedFrameFingerprint) { return $false }
            }
            foreach ($other in @($prepared[0].Operation, $disposed[0].Operation)) {
                foreach ($field in @('transitionKind','durableCheckpointFingerprint','proofBoundaryFingerprint','targetRoute','targetProtocolVersion')) {
                    if ([string]$other.routeTransition.$field -cne [string]$route.$field) { return $false }
                }
                if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $other.routeTransition.sourceBoundary $route.sourceBoundary)) { return $false }
            }
            foreach ($stage in @('pause_bind','drain','commit')) {
                $receipt = @($route.receipts | Where-Object stage -CEQ $stage)
                if ($receipt.Count -ne 1 -or -not $receipt[0].receiptValidated) { return $false }
                $preserved = @($disposed[0].Operation.routeTransition.receipts | Where-Object stage -CEQ $stage)
                if ($preserved.Count -ne 1 -or (ConvertTo-Json $receipt[0] -Compress) -cne (ConvertTo-Json $preserved[0] -Compress)) { return $false }
            }
            foreach ($stage in @(if ($i -eq 0) { 'retained_frame' }; 'disposition')) {
                $receipt = @($disposed[0].Operation.routeTransition.receipts | Where-Object stage -CEQ $stage)
                if ($receipt.Count -ne 1 -or -not $receipt[0].receiptValidated -or $receipt[0].receiptStatus -cnotin @('Applied','Idempotent')) { return $false }
            }
            $lastDisposition = $disposed[0].Boundary
        }
        # The successor must still name the published reactivation target, not an ABA-restored owner.
        foreach ($boundary in @($cycle | Where-Object { $_.evidenceLineIndex -ge $commits[1].Boundary.evidenceLineIndex })) {
            if (-not (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint $boundary.afterBoundary $target)) { return $false }
        }
        return $true
    } catch { return $false }
}

function Get-FileTransferBootstrapCorrelationRetirementEvidence {
    param([AllowEmptyCollection()][object[]]$Boundaries, $Catalog)
    $validated = @{}; $errors = [Collections.Generic.List[string]]::new()
    foreach ($stream in @($Boundaries | Group-Object coordinatorInstanceId)) {
        $prior = [Collections.Generic.List[object]]::new()
        foreach ($boundary in @($stream.Group | Sort-Object evidenceLineIndex)) {
            if ($boundary.decisionKind -ceq 'StartFallbackBootstrapExchange') {
                $envelope = [pscustomobject]@{ direction=$boundary.direction; decisionKind=$boundary.decisionKind;
                    decisionStatus=$boundary.decisionStatus; aggregate=[pscustomobject]@{
                        beforeBoundary=$boundary.beforeBoundary; afterBoundary=$boundary.afterBoundary; operations=$boundary.operations } }
                foreach ($retired in @($boundary.operations | Where-Object { $_.operationRole -ceq 'fallback_v6_bootstrap' -and $_.afterState -ceq 'None' })) {
                    $link = Get-FileTransferBootstrapCorrelationRetirementLink $envelope $retired $Catalog
                    $key = "$($boundary.coordinatorInstanceId)|$($boundary.boundaryId)|$($retired.operationKey)"
                    if (Test-FileTransferBootstrapCorrelationRetirementHistory $link @($prior)) { $validated[$key] = $true }
                    else { $errors.Add("snapshot_bootstrap_correlation_retirement_history_invalid:$($boundary.evidenceLineIndex)") }
                }
            }
            $prior.Add($boundary)
        }
    }
    return [pscustomobject]@{ Validated=$validated; Errors=@($errors) }
}

function Get-FileTransferBootstrapCorrelationHistoryErrors {
    param([AllowEmptyCollection()][object[]]$Boundaries)
    foreach ($stream in @($Boundaries | Group-Object coordinatorInstanceId)) {
        $phases = @{}
        $agreement = $null
        $legacyOpenApplied = $false
        foreach ($boundary in @($stream.Group | Sort-Object evidenceLineIndex)) {
            $operations = @($boundary.operations | Where-Object { Test-FileTransferCoordinatorBootstrapCorrelationMarker $_ })
            foreach ($operation in @($operations | Where-Object operationRole -CEQ 'fallback_bootstrap_agreement')) {
                $priorState = if ($null -eq $agreement) { 'None' } else { $agreement.afterState }
                if ($operation.beforeState -cne $priorState) {
                    "snapshot_bootstrap_correlation_agreement_before_state_mismatch:$($boundary.evidenceLineIndex)"
                }
                if ($null -ne $agreement) {
                    $payload = $operation.routeTransition.bootstrapCorrelation
                    $priorPayload = $agreement.routeTransition.bootstrapCorrelation
                    $mutated = $operation.authorityId -cne $agreement.authorityId -or
                        $operation.operationGeneration -ne $agreement.operationGeneration -or
                        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqual $operation.routeTransition.sourceBoundary $agreement.routeTransition.sourceBoundary) -or
                        $payload.senderIssuerFingerprint -cne $priorPayload.senderIssuerFingerprint
                    if ($priorState -cin @('Selected','Enabled') -and
                        ($payload.version -ne $priorPayload.version -or
                            $payload.receiverIssuerFingerprint -cne $priorPayload.receiverIssuerFingerprint)) { $mutated = $true }
                    if ($operation.afterState -ceq $priorState) {
                        $samePayload = $true
                        foreach ($field in $script:FileTransferCoordinatorBootstrapCorrelationCatalog.payloadFields.Keys) {
                            if ($field -ceq 'agreementFingerprint') { continue }
                            if ((ConvertTo-Json -InputObject $payload.$field -Depth 16 -Compress) -cne
                                (ConvertTo-Json -InputObject $priorPayload.$field -Depth 16 -Compress)) { $samePayload = $false }
                        }
                        if (-not $samePayload) { $mutated = $true }
                        if ($operation.commandFingerprint -cne $agreement.commandFingerprint) {
                            $key = "$($operation.beforeState):$($operation.afterState):$($payload.version)"
                            $transitions = $script:FileTransferCoordinatorBootstrapCorrelationCatalog.agreementTransitions.($boundary.direction)
                            if ($legacyOpenApplied -or $key -cnotin @($transitions.Keys) -or
                                $boundary.decisionKind -cne $transitions[$key] -or $boundary.decisionStatus -cne 'Accepted') {
                                $mutated = $true
                            } else { $legacyOpenApplied = $true }
                        }
                    } elseif ($operation.commandFingerprint -ceq $agreement.commandFingerprint) { $mutated = $true }
                    if ($mutated) { "snapshot_bootstrap_correlation_agreement_mutated:$($boundary.evidenceLineIndex)" }
                }
                $agreement = $operation
            }
            foreach ($operation in @($operations | Where-Object { $_.operationRole -cin @('fallback_bootstrap_exchange','fallback_bootstrap_phase') })) {
                $payload = $operation.routeTransition.bootstrapCorrelation
                $bound = $null -ne $agreement -and $agreement.afterState -ceq 'Enabled'
                if ($bound) {
                    foreach ($field in @('agreementFingerprint','senderIssuerFingerprint','receiverIssuerFingerprint')) {
                        if ($payload.$field -cne $agreement.routeTransition.bootstrapCorrelation.$field) { $bound = $false }
                    }
                }
                if (-not $bound) { "snapshot_bootstrap_correlation_agreement_unbound:$($boundary.evidenceLineIndex)" }
            }
            foreach ($operation in @($operations | Where-Object operationRole -CEQ 'fallback_bootstrap_phase')) {
                $payload = $operation.routeTransition.bootstrapCorrelation
                $identity = [string]$operation.authorityId
                $priorReceiptCount = 0
                if ($phases.ContainsKey($identity)) {
                    $prior = $phases[$identity]
                    $priorReceiptCount = @($prior.routeTransition.receipts).Count
                    if ($operation.beforeState -cne $prior.afterState) {
                        "snapshot_bootstrap_correlation_phase_before_state_mismatch:$($boundary.evidenceLineIndex)"
                    }
                    if ($prior.afterState -ceq 'None' -or $prior.commandFingerprint -cne $operation.commandFingerprint -or
                        $prior.operationGeneration -ne $operation.operationGeneration) {
                        "snapshot_bootstrap_correlation_phase_reissued:$($boundary.evidenceLineIndex)"
                    }
                    foreach ($receipt in @($prior.routeTransition.receipts)) {
                        $matches = @($operation.routeTransition.receipts | Where-Object {
                            $_.stage -ceq $receipt.stage -and $_.receiptFingerprint -ceq $receipt.receiptFingerprint -and
                            $_.receiptIdentity -ceq $receipt.receiptIdentity
                        })
                        if ($matches.Count -ne 1) { "snapshot_bootstrap_correlation_receipt_mutated:$($boundary.evidenceLineIndex)" }
                    }
                } elseif ($operation.beforeState -cne 'None') {
                    "snapshot_bootstrap_correlation_phase_issue_missing:$($boundary.evidenceLineIndex)"
                }
                if (@($operation.routeTransition.receipts).Count -gt $priorReceiptCount -and
                    ($boundary.decisionKind -cne 'ApplyFallbackBootstrapPhaseReceipt' -or $boundary.decisionStatus -cne 'Accepted')) {
                    "snapshot_bootstrap_correlation_receipt_decision_invalid:$($boundary.evidenceLineIndex)"
                }
                $phases[$identity] = $operation
            }
            foreach ($operation in @($operations | Where-Object operationRole -CEQ 'fallback_bootstrap_phase')) {
                $payload = $operation.routeTransition.bootstrapCorrelation
                if ($payload.predecessorIdentity -ceq '(none)' -or $operation.afterState -ceq 'None') { continue }
                $predecessor = if ($phases.ContainsKey($payload.predecessorIdentity)) { $phases[$payload.predecessorIdentity] } else { $null }
                if ($null -eq $predecessor -or $predecessor.afterState -cne 'Ready' -or
                    $predecessor.routeTransition.bootstrapCorrelation.localIdFingerprint -cne $payload.localIdFingerprint -or
                    $predecessor.routeTransition.bootstrapCorrelation.phase -cne $payload.firstPhase -or
                    $predecessor.operationGeneration -ne $operation.operationGeneration) {
                    "snapshot_bootstrap_correlation_predecessor_not_ready:$($boundary.evidenceLineIndex)"
                }
            }
            foreach ($operation in @($operations | Where-Object { $_.operationRole -ceq 'fallback_bootstrap_exchange' -and $_.afterState -ceq 'Completed' })) {
                $payload = $operation.routeTransition.bootstrapCorrelation
                $expected = @(if ($boundary.direction -ceq 'outbound') { 'Solicit'; 'Confirm' }
                    elseif ($payload.firstPhase -ceq 'Announce') { 'Announce'; 'Response' } else { 'Response' })
                $owned = @($phases.Values | Where-Object {
                    $_.routeTransition.bootstrapCorrelation.localIdFingerprint -ceq $payload.localIdFingerprint -and $_.afterState -cne 'None'
                })
                $invalid = $owned.Count -ne $expected.Count
                foreach ($phase in $expected) {
                    $matching = @($owned | Where-Object {
                        $_.routeTransition.bootstrapCorrelation.phase -ceq $phase -and $_.afterState -ceq 'Ready' -and
                        $_.operationGeneration -eq $operation.operationGeneration -and
                        (Test-FileTransferCoordinatorSnapshotBoundaryEqual $_.routeTransition.sourceBoundary $operation.routeTransition.sourceBoundary)
                    })
                    if ($matching.Count -ne 1) { $invalid = $true }
                }
                $type = if ($boundary.direction -ceq 'outbound') { 'filetransfer.fallback_bootstrap_state.v6' }
                    else { 'filetransfer.fallback_bootstrap_request.v6' }
                if ($invalid -or $operation.routeTransition.retainedFrameType -cne $type -or
                    $operation.routeTransition.retainedFrameFingerprint -cnotmatch '^(?:[a-f0-9]{64}|fte1-[a-f0-9]{24})$') {
                    "snapshot_bootstrap_correlation_completion_without_exact_phases:$($boundary.evidenceLineIndex)"
                }
            }
        }
    }
}
