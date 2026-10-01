Set-StrictMode -Version Latest

$script:FileTransferCoordinatorCheckpointRepairContinuityCatalogVersion =
    'snapshot-authority-v1-receiver-barrier-v1-checkpoint-repair-continuity-v1'
$script:FileTransferCoordinatorCheckpointRepairContinuityCatalogHash =
    'e6c3b5ff7cf8b44bcde0782ec325d2623cb96ba8ff362edca1902f9a1929059d'
$script:FileTransferCoordinatorCheckpointRepairContinuityCatalog = [ordered]@{
    version = 1
    family = 'route_runtime'
    operationKind = 'FallbackCheckpointRepairScopeContinuity'
    operationRoles = @('checkpoint_acceptance_retained', 'checkpoint_repair_continuity_attempt')
    decisionKinds = @('FallbackCheckpointAccepted', 'CheckpointRepairContinuityInvalidated', 'CheckpointRepairContinuityValidated')
    acceptanceStates = @('None', 'Retained')
    validationStates = @('Validated', 'Rejected', 'NotApplicable')
    receiptStages = @('checkpoint_acceptance', 'checkpoint_repair_continuity')
    effectKinds = @()
    payloadName = 'checkpointRepairContinuity'
    payloadFields = [ordered]@{
        role = 'string'
        coordinatorInstanceId = 'string'
        sessionId = 'string'
        transferId = 'string'
        direction = 'string'
        acceptedStateGeneration = 'int64'
        acceptanceIdentity = 'string'
        originOperationKey = 'string'
        originReceiptIdentity = 'string'
        originReceiptFingerprint = 'string'
        sourceScope = 'CheckpointRepairContinuityScope'
        clearedScope = 'CheckpointRepairContinuityScope'
        currentScope = 'CheckpointRepairContinuityScope'
        acceptedCheckpoint = 'AcceptedCheckpointEvidence'
        repair = 'CheckpointRepairEvidence?'
        receipts = 'FileTransferCoordinatorReceiptTransitionSnapshot[]'
    }
    scopeFields = [ordered]@{
        boundary = 'FileTransferCoordinatorStateBoundarySnapshot'
        runtimeOwnerFingerprint = 'string'
        publicationFingerprint = 'string'
        commitFingerprint = 'string'
        bridgeDecisionGeneration = 'int64'
        aggregateBridgeDecisionGeneration = 'int64'
        aggregateTransportEpoch = 'int64'
        retainedLegCheckpointGeneration = 'int64'
        retainedLegCheckpointRequestId = 'string'
        leaseGeneration = 'int64'
        scopeFingerprint = 'string'
    }
    checkpointFields = [ordered]@{
        leaseGeneration = 'int64'
        checkpointGeneration = 'int64'
        checkpointRequestId = 'string'
        status = 'string'
        reason = 'string'
        leaseFingerprint = 'string'
    }
    repairFields = [ordered]@{
        authorityId = 'string'
        predecessorAuthorityId = 'string'
        decisionGeneration = 'int64'
        canonicalRepairKey = 'string'
        rangeFingerprint = 'string'
        frontierChunkIndex = 'int64'
        durableReceivedHighestChunkIndex = 'int64'
        rangeCount = 'int64'
        requestedChunkCount = 'int64'
        sourceKind = 'string'
        targetTransport = 'string'
        protocolRepairRequestId = 'string'
        requestCorrelationId = 'string'
        checkpointRequestId = 'string'
        repairFingerprint = 'string'
    }
    diagnosticFields = [ordered]@{
        continuity_profile = 'profile'
        continuity_coordinator_id = 'fte1'
        continuity_source_fingerprint = 'fte1'
        continuity_acceptance_id = 'fte1'
        continuity_accepted_state_generation = 'int64'
        continuity_origin_receipt_id = 'fte1'
        continuity_origin_receipt_fingerprint = 'fte1'
        continuity_receipt_id = 'fte1'
        continuity_receipt_fingerprint = 'fte1'
        continuity_current_scope_fingerprint = 'fte1'
        continuity_repair_authority_id = 'fte1'
        continuity_repair_generation = 'int64'
        continuity_request_id = 'fte1'
        continuity_correlation_id = 'fte1'
    }
}

function Test-FileTransferCoordinatorReceiverBarrierCatalogCapability {
    param($Catalog)
    return [string]$Catalog.Version -cin @(
        $script:FileTransferCoordinatorReceiverBarrierCatalogVersion,
        $script:FileTransferCoordinatorCheckpointRepairContinuityCatalogVersion,
        $script:FileTransferCoordinatorBootstrapCorrelationCatalogVersion,
        $script:FileTransferCoordinatorBootstrapInitiationCatalogVersion, $script:FileTransferCoordinatorBootstrapCallbackCatalogVersion,
        $script:FileTransferCoordinatorRepairOwnerCatalogVersion, $script:FileTransferCoordinatorActivationRetryCatalogVersion, $script:FileTransferCoordinatorReceiverPreAdmissionCatalogVersion, $script:FileTransferCoordinatorInitialDataSessionCatalogVersion, $script:FileTransferCoordinatorEarlyCheckpointProofCatalogVersion)
}

function Test-FileTransferCoordinatorCheckpointRepairContinuityCatalogCapability {
    param([string]$Version)
    return $Version -cin @($script:FileTransferCoordinatorCheckpointRepairContinuityCatalogVersion,
        $script:FileTransferCoordinatorBootstrapCorrelationCatalogVersion,
        $script:FileTransferCoordinatorBootstrapInitiationCatalogVersion, $script:FileTransferCoordinatorBootstrapCallbackCatalogVersion,
        $script:FileTransferCoordinatorRepairOwnerCatalogVersion, $script:FileTransferCoordinatorActivationRetryCatalogVersion, $script:FileTransferCoordinatorReceiverPreAdmissionCatalogVersion, $script:FileTransferCoordinatorInitialDataSessionCatalogVersion, $script:FileTransferCoordinatorEarlyCheckpointProofCatalogVersion)
}

function Get-FileTransferCoordinatorCheckpointRepairContinuityCatalog {
    $parent = Get-FileTransferCoordinatorEvidenceCatalog `
        -ProfileVersion $script:FileTransferCoordinatorReceiverBarrierCatalogVersion
    $extensionJson = ConvertTo-Json -InputObject $script:FileTransferCoordinatorCheckpointRepairContinuityCatalog -Depth 16 -Compress
    $canonical = "parentHash=$($parent.Hash)`nCheckpointRepairContinuityCatalog=$extensionJson"
    $hash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes (
        [System.Text.UTF8Encoding]::new($false).GetBytes($canonical))
    if ($hash -cne $script:FileTransferCoordinatorCheckpointRepairContinuityCatalogHash) {
        throw "coordinator_snapshot_checkpoint_repair_continuity_catalog_hash_invalid:$hash"
    }
    $extended = [ordered]@{}
    foreach ($property in $parent.PSObject.Properties) { $extended[$property.Name] = $property.Value }
    $extended.Version = $script:FileTransferCoordinatorCheckpointRepairContinuityCatalogVersion
    $extended.Hash = $hash
    $extended.ParentCatalogHash = $parent.Hash
    $extended.CheckpointRepairContinuityCatalog = $script:FileTransferCoordinatorCheckpointRepairContinuityCatalog
    return [pscustomobject]$extended
}

function Test-FileTransferCoordinatorCheckpointRepairContinuityMarker {
    param($Operation)
    if ($null -eq $Operation) { return $false }
    return $Operation.PSObject.Properties.Name -contains 'checkpointRepairContinuity' -or
        ($Operation.PSObject.Properties.Name -contains 'operationKind' -and
            [string]$Operation.operationKind -ceq 'FallbackCheckpointRepairScopeContinuity') -or
        ($Operation.PSObject.Properties.Name -contains 'operationRole' -and
            [string]$Operation.operationRole -cin $script:FileTransferCoordinatorCheckpointRepairContinuityCatalog.operationRoles)
}

function Test-FileTransferCheckpointRepairContinuityFields {
    param($Value, $Fields)
    if ($null -eq $Value -or @($Value.PSObject.Properties).Count -ne $Fields.Count) { return $false }
    foreach ($name in $Fields.Keys) {
        if ($Value.PSObject.Properties.Name -cnotcontains $name) { return $false }
        if ($Fields[$name] -ceq 'string' -and
            ($Value.$name -isnot [string] -or [string]::IsNullOrWhiteSpace($Value.$name) -or
             $Value.$name -cmatch '\[redacted\]')) { return $false }
        if ($Fields[$name] -ceq 'int64' -and
            $Value.$name -isnot [int] -and $Value.$name -isnot [long]) { return $false }
    }
    return $true
}

function Test-FileTransferCheckpointRepairContinuityScope {
    param($Scope)
    if (-not (Test-FileTransferCheckpointRepairContinuityFields $Scope `
            $script:FileTransferCoordinatorCheckpointRepairContinuityCatalog.scopeFields) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete $Scope.boundary) -or
        @($Scope.boundary.PSObject.Properties).Count -ne 14 -or
        [long]$Scope.bridgeDecisionGeneration -lt 0 -or
        [long]$Scope.aggregateBridgeDecisionGeneration -lt 0 -or
        [long]$Scope.aggregateTransportEpoch -lt 0 -or
        [long]$Scope.retainedLegCheckpointGeneration -lt 0 -or
        [long]$Scope.leaseGeneration -lt 0) { return $false }
    foreach ($name in @('protocolVersion', 'liveRouteEpoch', 'routePublicationGeneration', 'routeCommitGeneration',
            'runtimeGeneration', 'terminalGeneration', 'legGeneration', 'transportEpoch',
            'bridgeRecoveryGeneration', 'checkpointGeneration', 'dataSessionGeneration')) {
        if (($Scope.boundary.$name -isnot [int] -and $Scope.boundary.$name -isnot [long]) -or
            [long]$Scope.boundary.$name -lt 0) { return $false }
    }
    foreach ($name in @('route', 'checkpointRequestId', 'dataSessionIdentity')) {
        if ($Scope.boundary.$name -isnot [string] -or $Scope.boundary.$name -cmatch '\[redacted\]') { return $false }
    }
    foreach ($name in @('runtimeOwnerFingerprint', 'publicationFingerprint', 'scopeFingerprint')) {
        if ($Scope.$name -cnotmatch '^(?:[0-9a-f]{64}|fte1-[0-9a-f]{24})$') { return $false }
    }
    return $Scope.commitFingerprint -cmatch '^(?:\(none\)|[0-9a-f]{64}|fte1-[0-9a-f]{24})$'
}

function Test-FileTransferCheckpointRepairContinuityScopeEqual {
    param($Left, $Right)
    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $Left.boundary $Right.boundary)) { return $false }
    foreach ($name in @('runtimeOwnerFingerprint', 'publicationFingerprint', 'commitFingerprint',
            'bridgeDecisionGeneration', 'aggregateBridgeDecisionGeneration', 'aggregateTransportEpoch', 'retainedLegCheckpointGeneration',
            'retainedLegCheckpointRequestId', 'leaseGeneration', 'scopeFingerprint')) {
        if ([string]$Left.$name -cne [string]$Right.$name) { return $false }
    }
    return $true
}

function Test-FileTransferCheckpointRepairContinuityOriginScope {
    param($Payload)
    $source = $Payload.sourceScope
    $cleared = $Payload.clearedScope
    $checkpoint = $Payload.acceptedCheckpoint
    if (-not (Test-FileTransferCheckpointRepairContinuityScope $source) -or
        -not (Test-FileTransferCheckpointRepairContinuityScope $cleared) -or
        -not (Test-FileTransferCheckpointRepairContinuityFields $checkpoint `
            $script:FileTransferCoordinatorCheckpointRepairContinuityCatalog.checkpointFields) -or
        $checkpoint.status -cne 'Accepted' -or [long]$checkpoint.leaseGeneration -le 0 -or
        [long]$checkpoint.checkpointGeneration -le 0 -or $checkpoint.checkpointRequestId -ceq '(none)' -or
        $checkpoint.leaseFingerprint -cnotmatch '^(?:[0-9a-f]{64}|fte1-[0-9a-f]{24})$' -or
        $source.boundary.route -cne 'post_tuna_fallback_v6' -or
        [int]$source.boundary.protocolVersion -ne 6 -or
        [long]$source.boundary.transportEpoch -le 0 -or
        [long]$source.boundary.routePublicationGeneration -le 0 -or
        [long]$source.boundary.dataSessionGeneration -le 0 -or
        $source.boundary.dataSessionIdentity -ceq '(none)' -or
        [long]$source.leaseGeneration -ne [long]$checkpoint.leaseGeneration -or
        [long]$source.boundary.checkpointGeneration -ne [long]$checkpoint.checkpointGeneration -or
        $source.boundary.checkpointRequestId -cne $checkpoint.checkpointRequestId -or
        [long]$source.retainedLegCheckpointGeneration -ne [long]$checkpoint.checkpointGeneration -or
        $source.retainedLegCheckpointRequestId -cne $checkpoint.checkpointRequestId -or
        [long]$cleared.leaseGeneration -ne 0 -or
        [long]$cleared.boundary.checkpointGeneration -ne 0 -or
        $cleared.boundary.checkpointRequestId -cne '(none)' -or
        [long]$cleared.retainedLegCheckpointGeneration -ne [long]$source.retainedLegCheckpointGeneration -or
        $cleared.retainedLegCheckpointRequestId -cne '(none)' -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryEqualExceptCheckpoint $source.boundary $cleared.boundary)) { return $false }
    foreach ($name in @('runtimeOwnerFingerprint', 'publicationFingerprint', 'commitFingerprint', 'bridgeDecisionGeneration',
            'aggregateBridgeDecisionGeneration', 'aggregateTransportEpoch')) {
        if ([string]$source.$name -cne [string]$cleared.$name) { return $false }
    }
    return $true
}

function Test-FileTransferCheckpointRepairContinuityAggregateScope {
    param($Scope, $Boundary)
    $projected = $Scope.boundary | Select-Object *
    $projected.bridgeRecoveryGeneration = [long]$Scope.aggregateBridgeDecisionGeneration
    $projected.transportEpoch = [long]$Scope.aggregateTransportEpoch
    return Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $projected $Boundary
}

function Test-FileTransferCoordinatorCheckpointRepairContinuityOperation {
    param($Envelope, $Operation, $Catalog)
    try {
        if (-not (Test-FileTransferCoordinatorCheckpointRepairContinuityCatalogCapability $Catalog.Version) -or
            $Operation.family -cne 'route_runtime' -or
            $Operation.operationKind -cne 'FallbackCheckpointRepairScopeContinuity' -or
            $Operation.operationRole -cnotin $script:FileTransferCoordinatorCheckpointRepairContinuityCatalog.operationRoles -or
            $Operation.PSObject.Properties.Name -cnotcontains 'checkpointRepairContinuity') { return $false }
        foreach ($name in @('routeRuntime', 'routeTransition', 'runtimeUnlockRetry', 'receiveRecovery',
                'fallbackTailYield', 'regularV4FrontierRepairPressure', 'responderProbeHandoff', 'receiverBarrier')) {
            if ($Operation.PSObject.Properties.Name -contains $name -and $null -ne $Operation.$name) { return $false }
        }
        $payload = $Operation.checkpointRepairContinuity
        if (-not (Test-FileTransferCheckpointRepairContinuityFields $payload `
                $script:FileTransferCoordinatorCheckpointRepairContinuityCatalog.payloadFields) -or
            $payload.role -cne $Operation.operationRole -or $payload.direction -cne 'inbound' -or
            [long]$payload.acceptedStateGeneration -le 0 -or
            $payload.acceptanceIdentity -cnotmatch '^(?:[0-9a-f]{64}|fte1-[0-9a-f]{24})$' -or
            $payload.originReceiptFingerprint -cnotmatch '^(?:[0-9a-f]{64}|fte1-[0-9a-f]{24})$' -or
            -not (Test-FileTransferCheckpointRepairContinuityOriginScope $payload) -or
            -not (Test-FileTransferCheckpointRepairContinuityScope $payload.currentScope) -or
            $Operation.stateApplied -isnot [bool] -or $Operation.receiptValidated -isnot [bool]) { return $false }
        foreach ($name in @('coordinatorInstanceId', 'sessionId', 'transferId', 'direction')) {
            if ([string]$payload.$name -cne [string]$Envelope.$name) { return $false }
        }
        $expectedOriginKey = "route_runtime|checkpoint_acceptance_retained|$($payload.acceptanceIdentity)|$($payload.acceptedStateGeneration)|FallbackCheckpointRepairScopeContinuity"
        if ($payload.originOperationKey -cne $expectedOriginKey -or
            $Operation.operationKey -cne "route_runtime|$($Operation.operationRole)|$($Operation.authorityId)|$($Operation.operationGeneration)|FallbackCheckpointRepairScopeContinuity" -or
            @($payload.receipts).Count -ne 1) { return $false }
        $receipt = @($payload.receipts)[0]
        $receiptFields = [ordered]@{ stage='string'; commandIdentity='string'; commandGeneration='int64';
            commandFingerprint='string'; receiptIdentity='string'; receiptGeneration='int64'; receiptStatus='string';
            receiptReason='string'; receiptFingerprint='string'; receiptValidated='bool' }
        if (-not (Test-FileTransferCheckpointRepairContinuityFields $receipt $receiptFields) -or
            $receipt.receiptValidated -isnot [bool] -or
            [bool]$receipt.receiptValidated -ne [bool]$Operation.receiptValidated -or
            $receipt.commandFingerprint -cnotmatch '^(?:[0-9a-f]{64}|fte1-[0-9a-f]{24})$' -or
            $receipt.receiptFingerprint -cnotmatch '^(?:[0-9a-f]{64}|fte1-[0-9a-f]{24})$' -or
            [long]$receipt.commandGeneration -le 0 -or [long]$receipt.receiptGeneration -le 0) { return $false }
        foreach ($name in @('commandIdentity', 'commandGeneration', 'commandFingerprint', 'receiptIdentity',
                'receiptGeneration', 'receiptStatus', 'receiptFingerprint')) {
            if ([string]$Operation.$name -cne [string]$receipt.$name -or [string]$receipt.$name -ceq '(none)') { return $false }
        }
        if ($payload.role -ceq 'checkpoint_acceptance_retained') {
            if ($null -ne $payload.repair -or $Operation.authorityId -cne $payload.acceptanceIdentity -or
                $Operation.predecessorAuthorityId -cne '(none)' -or
                [long]$Operation.operationGeneration -ne [long]$payload.acceptedStateGeneration -or
                $receipt.stage -cne 'checkpoint_acceptance' -or $receipt.receiptStatus -cne 'Accepted' -or
                -not [bool]$receipt.receiptValidated -or -not [bool]$Operation.stateApplied -or
                [long]$receipt.commandGeneration -ne [long]$payload.acceptedStateGeneration -or
                [long]$receipt.receiptGeneration -ne [long]$payload.acceptedStateGeneration -or
                $receipt.receiptIdentity -cne $payload.originReceiptIdentity -or
                $receipt.receiptFingerprint -cne $payload.originReceiptFingerprint -or
                $receipt.receiptReason -cne $payload.acceptedCheckpoint.reason -or
                -not (Test-FileTransferCheckpointRepairContinuityScopeEqual $payload.currentScope $payload.clearedScope)) { return $false }
            if ($Operation.beforeState -ceq 'None' -and $Operation.afterState -ceq 'Retained') {
                return $Envelope.decisionKind -ceq 'FallbackCheckpointAccepted' -and
                    $Envelope.decisionStatus -ceq 'Accepted' -and
                    [long]$Envelope.aggregate.afterStateGeneration -eq [long]$payload.acceptedStateGeneration -and
                    [long]$Envelope.aggregate.afterStateGeneration -eq ([long]$Envelope.aggregate.beforeStateGeneration + 1) -and
                    (Test-FileTransferCheckpointRepairContinuityAggregateScope $payload.sourceScope $Envelope.aggregate.beforeBoundary) -and
                    (Test-FileTransferCheckpointRepairContinuityAggregateScope $payload.clearedScope $Envelope.aggregate.afterBoundary)
            }
            if ($Operation.beforeState -cne 'Retained' -or $Operation.afterState -cne 'None' -or
                [long]$Envelope.aggregate.afterStateGeneration -le [long]$payload.acceptedStateGeneration) { return $false }
            if ($Envelope.decisionStatus -ceq 'Accepted') { return $true }

            # The receipt remains the original Accepted checkpoint receipt. A
            # route commit retires that evidence under its own Committed status.
            $before = $Envelope.aggregate.beforeBoundary
            $after = $Envelope.aggregate.afterBoundary
            if ($Envelope.decisionKind -cne 'CommitFileTransferRouteTransition' -or
                $Envelope.decisionStatus -cne 'Committed' -or
                $Envelope.decisionReason -cne 'filetransfer_route_transition_committed' -or
                [long]$Envelope.aggregate.afterStateGeneration -ne ([long]$Envelope.aggregate.beforeStateGeneration + 1) -or
                $Envelope.aggregate.beforeStateFingerprint -ceq $Envelope.aggregate.afterStateFingerprint -or
                $before.route -cne 'post_tuna_fallback_v6' -or [int]$before.protocolVersion -ne 6 -or
                $after.route -cne 'file_tuna_v4' -or [int]$after.protocolVersion -ne 4 -or
                $before.dataSessionIdentity -cne $after.dataSessionIdentity -or
                [long]$before.dataSessionGeneration -ne [long]$after.dataSessionGeneration -or
                [long]$before.terminalGeneration -ne [long]$after.terminalGeneration -or
                -not (Test-FileTransferCheckpointRepairContinuityAggregateScope $payload.clearedScope $before) -or
                @($Envelope.aggregate.operations | Where-Object {
                    $_.operationRole -ceq 'checkpoint_acceptance_retained'
                }).Count -ne 1) { return $false }
            $commits = @($Envelope.aggregate.operations | Where-Object {
                $_.family -ceq 'route_runtime' -and $_.operationRole -ceq 'route_commit' -and
                $_.operationKind -ceq 'RouteCommit' -and $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Committed'
            })
            $terminals = @($Envelope.aggregate.operations | Where-Object {
                $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'terminal' -and
                $_.operationKind -ceq 'PostTunaFallbackV6ToTunaV4' -and
                $_.beforeState -ceq 'None' -and $_.afterState -ceq 'Committed'
            })
            $currents = @($Envelope.aggregate.operations | Where-Object {
                $_.family -ceq 'route_transition' -and $_.operationRole -ceq 'current' -and
                $_.operationKind -ceq 'PostTunaFallbackV6ToTunaV4' -and
                $_.beforeState -ceq 'Draining' -and $_.afterState -ceq 'None'
            })
            if ($commits.Count -ne 1 -or $terminals.Count -ne 1 -or $currents.Count -ne 1) { return $false }
            $commit = $commits[0]; $terminal = $terminals[0]; $current = $currents[0]
            foreach ($subject in @($commit, $terminal, $current)) {
                if (-not $subject.stateApplied -or -not $subject.receiptValidated -or
                    -not (Test-FileTransferCoordinatorSnapshotFamily1OperationCatalog $Envelope $subject)) { return $false }
            }
            # Reuse Family 1's source/target domains; its shared lifecycle pass
            # remains responsible for the complete receipt and physical effects.
            return $commit.receiptStatus -ceq 'Committed' -and $terminal.receiptStatus -ceq 'Committed' -and
                [long]$commit.operationGeneration -eq [long]$after.routeCommitGeneration -and
                (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $commit.routeRuntime.boundary $after) -and
                $current.authorityId -ceq $terminal.authorityId -and
                [long]$current.operationGeneration -eq [long]$terminal.operationGeneration -and
                (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $current.routeTransition.sourceBoundary $terminal.routeTransition.sourceBoundary) -and
                (Test-FileTransferCoordinatorSnapshotRouteTransitionSourceCurrent $terminal.routeTransition.sourceBoundary $before) -and
                (Test-FileTransferCoordinatorSnapshotRouteTransitionTargetCurrent $terminal.routeTransition.targetBoundary $after)
        }
        $repair = $payload.repair
        if (-not (Test-FileTransferCheckpointRepairContinuityFields $repair `
                $script:FileTransferCoordinatorCheckpointRepairContinuityCatalog.repairFields) -or
            $Envelope.decisionKind -cne 'CheckpointRepairContinuityValidated' -or
            @($Envelope.aggregate.operations).Count -ne 1 -or
            $receipt.receiptReason -cne $Envelope.decisionReason -or
            [bool]$Operation.stateApplied -or
            $Operation.beforeState -cne $Operation.afterState -or
            $Operation.afterState -cnotin $script:FileTransferCoordinatorCheckpointRepairContinuityCatalog.validationStates -or
            [long]$Envelope.aggregate.beforeStateGeneration -ne [long]$Envelope.aggregate.afterStateGeneration -or
            $Envelope.aggregate.beforeStateFingerprint -cne $Envelope.aggregate.afterStateFingerprint -or
            -not (Test-FileTransferCoordinatorSnapshotBoundaryTupleEqual $Envelope.aggregate.beforeBoundary $Envelope.aggregate.afterBoundary) -or
            $receipt.stage -cne 'checkpoint_repair_continuity' -or
            $Operation.authorityId -cne $repair.authorityId -or
            $Operation.predecessorAuthorityId -cne $repair.predecessorAuthorityId -or
            [long]$Operation.operationGeneration -ne [long]$repair.decisionGeneration -or
            [long]$receipt.commandGeneration -ne [long]$repair.decisionGeneration -or
            [long]$receipt.receiptGeneration -ne [long]$repair.decisionGeneration) { return $false }
        if ($Operation.afterState -cne 'Validated') {
            return $Envelope.decisionStatus -ceq 'Rejected' -and -not [bool]$receipt.receiptValidated -and
                $receipt.receiptStatus -ceq $Operation.afterState
        }
        return $Envelope.decisionStatus -ceq 'Idempotent' -and [bool]$receipt.receiptValidated -and
            $receipt.receiptStatus -ceq 'Validated' -and
            [long]$Envelope.aggregate.afterStateGeneration -ge [long]$payload.acceptedStateGeneration -and
            (Test-FileTransferCheckpointRepairContinuityScopeEqual $payload.currentScope $payload.clearedScope) -and
            (Test-FileTransferCheckpointRepairContinuityAggregateScope $payload.currentScope $Envelope.aggregate.afterBoundary) -and
            $repair.sourceKind -ceq 'ReceiverRequest' -and $repair.targetTransport -ceq 'RegularNkn' -and
            [long]$repair.decisionGeneration -gt 0 -and [long]$repair.frontierChunkIndex -ge 0 -and
            [long]$repair.durableReceivedHighestChunkIndex -ge -1 -and
            [long]$repair.rangeCount -gt 0 -and [long]$repair.requestedChunkCount -gt 0 -and
            $repair.authorityId -cne '(none)' -and $repair.canonicalRepairKey -cne '(none)' -and
            $repair.rangeFingerprint -cne '(none)' -and $repair.protocolRepairRequestId -cne '(none)' -and
            $repair.requestCorrelationId -cne '(none)' -and $repair.checkpointRequestId -ceq '(none)' -and
            $repair.repairFingerprint -cmatch '^(?:[0-9a-f]{64}|fte1-[0-9a-f]{24})$'
    }
    catch { return $false }
}

function Get-FileTransferCheckpointRepairContinuityOriginKey {
    param($Payload)
    return @($Payload.coordinatorInstanceId, $Payload.acceptedStateGeneration,
        $Payload.originOperationKey, $Payload.originReceiptIdentity, $Payload.originReceiptFingerprint) -join [char]31
}

function Get-FileTransferCheckpointRepairContinuityOriginSignature {
    param($Payload)
    return ConvertTo-Json -Depth 32 -Compress -InputObject ([ordered]@{
        coordinatorInstanceId = $Payload.coordinatorInstanceId
        sessionId = $Payload.sessionId
        transferId = $Payload.transferId
        direction = $Payload.direction
        acceptedStateGeneration = $Payload.acceptedStateGeneration
        acceptanceIdentity = $Payload.acceptanceIdentity
        originOperationKey = $Payload.originOperationKey
        originReceiptIdentity = $Payload.originReceiptIdentity
        originReceiptFingerprint = $Payload.originReceiptFingerprint
        sourceScope = $Payload.sourceScope
        clearedScope = $Payload.clearedScope
        acceptedCheckpoint = $Payload.acceptedCheckpoint
    })
}

function Get-FileTransferCheckpointRepairContinuityEvidence {
    param([AllowEmptyCollection()][object[]]$Boundaries, $Catalog)
    $errors = [System.Collections.Generic.List[string]]::new()
    $origins = @{}
    $bindings = @{}
    if (-not (Test-FileTransferCoordinatorCheckpointRepairContinuityCatalogCapability $Catalog.Version)) {
        return [pscustomobject]@{ CatalogVersion=$Catalog.Version; CatalogHash=$Catalog.Hash;
            OriginsByKey=$origins; BindingsByReceiptKey=$bindings; Errors=@() }
    }
    foreach ($stream in @($Boundaries | Group-Object coordinatorInstanceId)) {
        $current = $null
        # These entries already passed the stream's exact generation/hash chain.
        # Order by that causal contract; location and time never grant continuity.
        foreach ($boundary in @($stream.Group | Sort-Object { [long]$_.beforeStateGeneration },
                { [long]$_.afterStateGeneration }, { [string]$_.boundaryId })) {
            $operations = @($boundary.operations | Where-Object { Test-FileTransferCoordinatorCheckpointRepairContinuityMarker $_ })
            $removals = @($operations | Where-Object {
                $_.operationRole -ceq 'checkpoint_acceptance_retained' -and $_.afterState -ceq 'None' })
            $additions = @($operations | Where-Object {
                $_.operationRole -ceq 'checkpoint_acceptance_retained' -and $_.afterState -ceq 'Retained' })
            if ($removals.Count -gt 1 -or $additions.Count -gt 1) {
                $errors.Add("snapshot_checkpoint_repair_continuity_origin_ambiguous:$($boundary.evidenceLineIndex)")
                $current = $null
                continue
            }
            foreach ($operation in $removals) {
                $payload = $operation.checkpointRepairContinuity
                $key = Get-FileTransferCheckpointRepairContinuityOriginKey $payload
                $receiptMatches = $null -ne $current
                if ($receiptMatches) {
                    $originalReceipt = $current.Operation.checkpointRepairContinuity.receipts[0]
                    $retiredReceipt = $payload.receipts[0]
                    foreach ($field in @('stage', 'commandIdentity', 'commandGeneration', 'commandFingerprint',
                        'receiptIdentity', 'receiptGeneration', 'receiptStatus', 'receiptReason',
                        'receiptFingerprint', 'receiptValidated')) {
                        if ([string]$originalReceipt.$field -cne [string]$retiredReceipt.$field) {
                            $receiptMatches = $false
                            break
                        }
                    }
                }
                if ($null -eq $current -or $current.Key -cne $key -or
                    $current.Signature -cne (Get-FileTransferCheckpointRepairContinuityOriginSignature $payload) -or
                    -not $receiptMatches) {
                    $errors.Add("snapshot_checkpoint_repair_continuity_retirement_unbound:$($boundary.evidenceLineIndex)")
                }
                if ($null -ne $current) { $current.RetiredBoundary = $boundary }
                $current = $null
            }
            foreach ($operation in $additions) {
                $payload = $operation.checkpointRepairContinuity
                $key = Get-FileTransferCheckpointRepairContinuityOriginKey $payload
                if ($null -ne $current -or $origins.ContainsKey($key)) {
                    $errors.Add("snapshot_checkpoint_repair_continuity_origin_reused:$($boundary.evidenceLineIndex)")
                    $current = $null
                    continue
                }
                $current = [pscustomobject]@{ Key=$key; Boundary=$boundary; Operation=$operation; RetiredBoundary=$null;
                    Signature=(Get-FileTransferCheckpointRepairContinuityOriginSignature $payload) }
                $origins[$key] = $current
            }
            if ($null -ne $current -and
                -not (Test-FileTransferCheckpointRepairContinuityAggregateScope `
                    $current.Operation.checkpointRepairContinuity.clearedScope $boundary.afterBoundary)) {
                $errors.Add("snapshot_checkpoint_repair_continuity_scope_mutation_not_retired:$($boundary.evidenceLineIndex)")
                $current.RetiredBoundary = $boundary
                $current = $null
            }
            foreach ($operation in @($operations | Where-Object {
                    $_.operationRole -ceq 'checkpoint_repair_continuity_attempt' -and $_.afterState -ceq 'Validated' })) {
                $payload = $operation.checkpointRepairContinuity
                $key = Get-FileTransferCheckpointRepairContinuityOriginKey $payload
                if ($null -eq $current -or $current.Key -cne $key -or
                    $current.Signature -cne (Get-FileTransferCheckpointRepairContinuityOriginSignature $payload)) {
                    $errors.Add("snapshot_checkpoint_repair_continuity_origin_unbound:$($boundary.evidenceLineIndex)")
                    continue
                }
                $bindingKey = @($payload.coordinatorInstanceId, $operation.receiptIdentity,
                    $operation.receiptFingerprint) -join [char]31
                $signature = ConvertTo-Json -InputObject $operation -Depth 32 -Compress
                if ($bindings.ContainsKey($bindingKey)) {
                    if ($bindings[$bindingKey].Signature -cne $signature) {
                        $errors.Add("snapshot_checkpoint_repair_continuity_binding_conflict:$($boundary.evidenceLineIndex)")
                    }
                    continue
                }
                $bindings[$bindingKey] = [pscustomobject]@{
                    Key=$bindingKey; Origin=$current; Boundary=$boundary; Operation=$operation; Signature=$signature
                }
            }
        }
    }
    return [pscustomobject]@{ CatalogVersion=$Catalog.Version; CatalogHash=$Catalog.Hash;
        OriginsByKey=$origins; BindingsByReceiptKey=$bindings; Errors=@($errors.ToArray()) }
}

function ConvertTo-FileTransferCheckpointRepairContinuityDiagnosticStamp {
    param($Fields, [switch]$SourceOnly)
    $stamp = [ordered]@{}
    $names = if ($SourceOnly) {
        @('continuity_profile', 'continuity_coordinator_id', 'continuity_source_fingerprint')
    } else { @($script:FileTransferCoordinatorCheckpointRepairContinuityCatalog.diagnosticFields.Keys) }
    foreach ($name in $names) {
        $value = if ($Fields -is [System.Collections.IDictionary] -and @($Fields.Keys) -ccontains $name) { $Fields[$name] }
            elseif ($null -ne $Fields -and $Fields.PSObject.Properties.Name -ccontains $name) { $Fields.$name }
            else { $null }
        if ($null -eq $value) { return $null }
        $kind = $script:FileTransferCoordinatorCheckpointRepairContinuityCatalog.diagnosticFields[$name]
        if ($kind -ceq 'profile') {
            if (-not (Test-FileTransferCoordinatorCheckpointRepairContinuityCatalogCapability ([string]$value))) { return $null }
            $stamp[$name] = [string]$value
        }
        elseif ($kind -ceq 'fte1') {
            if ([string]$value -cnotmatch '^fte1-[0-9a-f]{24}$') { return $null }
            $stamp[$name] = [string]$value
        }
        else {
            $number = 0L
            if ([string]$value -cnotmatch '^[1-9][0-9]*$' -or
                -not [long]::TryParse([string]$value, [ref]$number)) { return $null }
            $stamp[$name] = $number
        }
    }
    return [pscustomobject]$stamp
}

function Resolve-FileTransferCheckpointRepairContinuityBinding {
    param($SnapshotEvidence, $SourceFields, $CurrentFields, [Nullable[long]]$EvidenceUtcMilliseconds = $null)
    if ($null -eq $SnapshotEvidence -or $SnapshotEvidence.ArtifactEvidenceVerdict -cne 'pass' -or
        $SnapshotEvidence.PSObject.Properties.Name -notcontains '__CheckpointRepairContinuityEvidence') { return $null }
    $index = $SnapshotEvidence.__CheckpointRepairContinuityEvidence
    if ($null -eq $index -or -not (Test-FileTransferCoordinatorCheckpointRepairContinuityCatalogCapability $index.CatalogVersion)) { return $null }
    $source = ConvertTo-FileTransferCheckpointRepairContinuityDiagnosticStamp $SourceFields -SourceOnly
    $current = ConvertTo-FileTransferCheckpointRepairContinuityDiagnosticStamp $CurrentFields
    if ($null -eq $source -or $null -eq $current -or
        $source.continuity_profile -cne $index.CatalogVersion -or $current.continuity_profile -cne $index.CatalogVersion -or
        $source.continuity_coordinator_id -cne $current.continuity_coordinator_id -or
        $source.continuity_source_fingerprint -cne $current.continuity_source_fingerprint) { return $null }
    $key = @($current.continuity_coordinator_id, $current.continuity_receipt_id,
        $current.continuity_receipt_fingerprint) -join [char]31
    if (-not $index.BindingsByReceiptKey.ContainsKey($key)) { return $null }
    $binding = $index.BindingsByReceiptKey[$key]
    # Diagnostic time can reject a poisoned historical stamp; it never creates a binding.
    if ($null -ne $EvidenceUtcMilliseconds -and $null -ne $binding.Origin.RetiredBoundary -and
        [long]$EvidenceUtcMilliseconds -ge [long]$binding.Origin.RetiredBoundary.decisionUtcMilliseconds) { return $null }
    $payload = $binding.Operation.checkpointRepairContinuity
    $expected = [ordered]@{
        continuity_source_fingerprint=$payload.sourceScope.scopeFingerprint
        continuity_acceptance_id=$payload.acceptanceIdentity
        continuity_accepted_state_generation=$payload.acceptedStateGeneration
        continuity_origin_receipt_id=$payload.originReceiptIdentity
        continuity_origin_receipt_fingerprint=$payload.originReceiptFingerprint
        continuity_current_scope_fingerprint=$payload.currentScope.scopeFingerprint
        continuity_repair_authority_id=$payload.repair.authorityId
        continuity_repair_generation=$payload.repair.decisionGeneration
        continuity_request_id=$payload.repair.protocolRepairRequestId
        continuity_correlation_id=$payload.repair.requestCorrelationId
    }
    foreach ($name in $expected.Keys) {
        if ([string]$current.$name -cne [string]$expected[$name]) { return $null }
    }
    return $binding
}
