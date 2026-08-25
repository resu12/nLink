Set-StrictMode -Version Latest

$script:FileTransferCoordinatorSnapshotContractVersion = 1
$script:FileTransferCoordinatorSnapshotCatalogVersion = 'snapshot-authority-v1'
$script:FileTransferCoordinatorSnapshotCatalogHash = '2ba980b5696114cbd09fde978ae85d27270adc08f6caead9bd222c0074e9d0c6'
$script:FileTransferLegacyV0CatalogVersion = 'legacy-v0-pinned-20260825'
$script:FileTransferLegacyV0CatalogHash = '7e463c2909cbc4ca5e11cdd50c38e74c3884212a74cefdd6f1544308de4462ee'
$script:FileTransferCoordinatorEvidenceCatalogCache = $null
$script:FileTransferCoordinatorSnapshotRequiredFamilies = @(
    'route_runtime',
    'route_transition',
    'runtime_unlock_retry_lifecycle',
    'receive_recovery',
    'fallback_tail_yield',
    'regular_v4_frontier_repair_pressure',
    'responder_probe_handoff'
)
$script:FileTransferCoordinatorSnapshotFamily1 = @(
    'route_runtime',
    'route_transition'
)
$script:FileTransferCoordinatorSnapshotFamily2 = @(
    'runtime_unlock_retry_lifecycle'
)
$script:FileTransferCoordinatorSnapshotFamily2EffectKinds = @(
    'dispatch_stage_observed',
    'install_committed',
    'execution_observed',
    'settlement_observed',
    'scope_invalidation_applied'
)
$script:FileTransferCoordinatorSnapshotFamily2DecisionKinds = @(
    'AuthorizeRuntimeUnlockRetryDispatch',
    'ObserveRuntimeUnlockRetryDispatchStage',
    'IssueRuntimeUnlockRetryDispatchInstallReceipt',
    'CommitRuntimeUnlockRetryDispatchInstall',
    'EvaluateRuntimeUnlockRetryDispatchDeadline',
    'ObserveRuntimeUnlockRetryDispatchExecutionState',
    'ObserveRuntimeUnlockRetryDispatchSettlement',
    'PrepareRuntimeUnlockRetryDispatchSuccessor',
    'CloseRuntimeUnlockRetryDispatchChain',
    'PrepareRuntimeUnlockRetryDispatchStageExpiry',
    'InvalidateRuntimeUnlockRetryDispatchPendingAttempt',
    'PrepareRuntimeUnlockRetryLifecycleScopeInvalidation',
    'CommitRuntimeUnlockRetryLifecycleScopeInvalidation'
)
$script:FileTransferCoordinatorSnapshotFamily2OperationRoles = @(
    'attempt_pending',
    'attempt_installed',
    'attempt_retained_predecessor',
    'attempt_terminal',
    'bounded_closure',
    'scope_invalidation',
    'attempted_decision'
)
$script:FileTransferCoordinatorSnapshotFamily2ReceiptStages = [ordered]@{
    stage = @('Staged', 'Idempotent', 'Rejected')
    install = @(
        'ReceiptIssued', 'Installed', 'AliasBound', 'SendInFlight',
        'Settled', 'Invalidated', 'Expired', 'Closed')
    execution = @('AliasBound', 'SendInFlight', 'Settled')
    settlement = @(
        'DispatchObserved', 'MissingDispatch', 'Rejected', 'Invalidated',
        'Expired', 'StaleRejected')
    bounded_closure = @('Closed')
    scope_invalidation = @(
        'Pending', 'Accepted', 'Idempotent', 'Rejected', 'Applied')
    decision = @(
        'Accepted', 'Idempotent', 'Rejected', 'Expired', 'Granted', 'Held',
        'Requested', 'ChainClosed')
}
$script:FileTransferCoordinatorSnapshotFamily1EffectKinds = @(
    'pause_installed',
    'physical_send_lease_registered',
    'physical_send_lease_retired',
    'completed_drain_receipt',
    'route_commit_adopted',
    'target_runtime_leg_installed',
    'route_publication_forwarded',
    'retained_frame_response',
    'retained_frame_applied',
    'route_transition_disposition_applied',
    'recovered_epoch_publication',
    'transition_pause_released',
    'terminalized_while_draining'
)
$script:FileTransferCoordinatorSnapshotFamily1RouteTransitionFields = @(
    'role',
    'transitionKind',
    'sourceBoundary',
    'targetBoundary',
    'sourceTransport',
    'proofBoundaryKind',
    'proofAdmissionMode',
    'proofBoundaryFingerprint',
    'durableCheckpointFingerprint',
    'targetRoute',
    'targetProtocolVersion',
    'targetTransport',
    'handoffKind',
    'dispositionKind',
    'retainedFrameType',
    'retainedFrameFingerprint',
    'receipts'
)
$script:FileTransferCoordinatorSnapshotFamily1ReceiptStages = [ordered]@{
    pause_bind = @('Installed', 'Idempotent', 'Rejected')
    pause_rebind = @('Installed', 'Idempotent', 'Rejected')
    drain = @('Completed')
    commit = @('Committed')
    retained_frame = @('Applied', 'Idempotent', 'Rejected')
    disposition = @('Applied', 'Idempotent', 'Rejected')
    pressure_settlement = @('Applied', 'Idempotent', 'Rejected')
    fallback_v6_bootstrap = @('Delivered')
}
$script:FileTransferCoordinatorSnapshotDecisionCatalog = [ordered]@{
    route_transition_incomplete = [ordered]@{
        family = 'route_transition'
        class = 'filetransfer_route_transition_incomplete'
        domain = 'route_runtime'
        phase = 'route_transition'
    }
    route_transition_bootstrap = [ordered]@{
        family = 'route_transition'
        class = 'filetransfer_route_transition_tuna_v4_to_post_tuna_fallback_v6_proof_bootstrap_missing'
        domain = 'route_runtime'
        phase = 'route_transition'
    }
    route_transition_draining_terminal = [ordered]@{
        family = 'route_transition'
        class = 'filetransfer_route_transition_draining_transport_grace_terminalization'
        domain = 'route_runtime'
        phase = 'route_transition'
    }
    route_publication = [ordered]@{
        family = 'route_runtime'
        class = 'publication_authority_missing'
        domain = 'route_runtime'
        phase = 'route_runtime'
    }
    retry_missing_dispatch_successor = [ordered]@{
        family = 'runtime_unlock_retry_lifecycle'
        class = 'runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch'
        domain = 'route_runtime'
        phase = 'runtime_unlock_retry'
    }
    retry_install_incomplete = [ordered]@{
        family = 'runtime_unlock_retry_lifecycle'
        class = 'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete'
        domain = 'route_runtime'
        phase = 'runtime_unlock_retry'
    }
    retry_contract_unsettled = [ordered]@{
        family = 'runtime_unlock_retry_lifecycle'
        class = 'runtime_unlock_retry_dispatch_contract_unsettled'
        domain = 'route_runtime'
        phase = 'runtime_unlock_retry'
    }
    retry_chain_closure = [ordered]@{
        family = 'runtime_unlock_retry_lifecycle'
        class = 'runtime_unlock_retry_dispatch_chain_closure_incomplete'
        domain = 'route_runtime'
        phase = 'runtime_unlock_retry'
    }
}
$script:FileTransferCoordinatorSnapshotFamily1DecisionKinds = @(
    'StartTransfer',
    'RuntimeUnlockCommitRequested',
    'RoutePublication',
    'RouteCommitPublication',
    'Terminalized',
    'PrepareFileTransferRouteTransition',
    'BeginFileTransferRouteTransitionPhysicalDrain',
    'StageFileTransferRouteTransitionSupersession',
    'CommitFileTransferRouteTransitionSupersession',
    'CommitFileTransferRouteTransition',
    'ApplyFileTransferRouteTransitionRetainedFrameReceipt',
    'InvalidateFileTransferRouteTransition',
    'ApplyFileTransferRouteTransitionDispositionReceipt',
    'IssueFileTransferFallbackV6ProofBootstrap',
    'ApplyFileTransferFallbackV6ProofBootstrapReceipt'
)
$script:FileTransferCoordinatorSnapshotFamily1OperationKinds = [ordered]@{
    runtime_owner = @('RuntimeOwner', 'StartTransfer')
    route_commit = @('RouteCommit', 'RuntimeUnlockCommitRequested')
    route_publication = @('RoutePublication', 'RouteCommitPublication')
    current = @(
        'RegularV4ToTunaV4',
        'TunaV4ToPostTunaFallbackV6',
        'PostTunaFallbackV6ToTunaV4',
        'UncommittedTunaToPostTunaFallbackV6')
    terminal = @(
        'RegularV4ToTunaV4',
        'TunaV4ToPostTunaFallbackV6',
        'PostTunaFallbackV6ToTunaV4',
        'UncommittedTunaToPostTunaFallbackV6')
    supersession = @('Supersession')
    fallback_v6_bootstrap = @('FallbackV6ProofBootstrap')
    inbound = @(
        'PrepareFileTransferRouteTransition',
        'BeginFileTransferRouteTransitionPhysicalDrain',
        'StageFileTransferRouteTransitionSupersession',
        'CommitFileTransferRouteTransitionSupersession',
        'CommitFileTransferRouteTransition',
        'ApplyFileTransferRouteTransitionRetainedFrameReceipt',
        'InvalidateFileTransferRouteTransition',
        'ApplyFileTransferRouteTransitionDispositionReceipt')
    outbound = @(
        'PrepareFileTransferRouteTransition',
        'BeginFileTransferRouteTransitionPhysicalDrain',
        'StageFileTransferRouteTransitionSupersession',
        'CommitFileTransferRouteTransitionSupersession',
        'CommitFileTransferRouteTransition',
        'ApplyFileTransferRouteTransitionRetainedFrameReceipt',
        'InvalidateFileTransferRouteTransition',
        'ApplyFileTransferRouteTransitionDispositionReceipt')
    attempt = @(
        'PrepareFileTransferRouteTransition',
        'BeginFileTransferRouteTransitionPhysicalDrain',
        'StageFileTransferRouteTransitionSupersession',
        'CommitFileTransferRouteTransitionSupersession',
        'CommitFileTransferRouteTransition',
        'ApplyFileTransferRouteTransitionRetainedFrameReceipt',
        'InvalidateFileTransferRouteTransition',
        'ApplyFileTransferRouteTransitionDispositionReceipt')
}

function Get-FileTransferCoordinatorEvidenceCatalog {
    [CmdletBinding()]
    param()

    if ($null -ne $script:FileTransferCoordinatorEvidenceCatalogCache) {
        return $script:FileTransferCoordinatorEvidenceCatalogCache
    }

    $snapshotEvents = @(
        'filetransfer_coordinator_snapshot_stream_started',
        'filetransfer_coordinator_transition_snapshot',
        'filetransfer_coordinator_snapshot_stream_completed'
    )
    $generationStampedEffects = @('filetransfer_coordinator_authority_effect')
    $nonAuthorityEvidence = @(
        'filetransfer_transfer_terminal',
        'filetransfer_live_route_epoch_terminal',
        'integrity_verify_passed',
        'temp_finalize_succeeded'
    )
    $canonical = @(
        "contract=$script:FileTransferCoordinatorSnapshotContractVersion"
        "SnapshotAuthorityV1=$($snapshotEvents -join ',')"
        "GenerationStampedEffects=$($generationStampedEffects -join ',')"
        "NonAuthorityEvidence=$($nonAuthorityEvidence -join ',')"
        "RequiredFamilies=$($script:FileTransferCoordinatorSnapshotRequiredFamilies -join ',')"
        "Family1=$($script:FileTransferCoordinatorSnapshotFamily1 -join ',')"
        "Family1Effects=$($script:FileTransferCoordinatorSnapshotFamily1EffectKinds -join ',')"
        "Family1RouteTransitionFields=$($script:FileTransferCoordinatorSnapshotFamily1RouteTransitionFields -join ',')"
        "Family1ReceiptStages=$((@($script:FileTransferCoordinatorSnapshotFamily1ReceiptStages.Keys | ForEach-Object { '{0}:{1}' -f $_, ($script:FileTransferCoordinatorSnapshotFamily1ReceiptStages[$_] -join '|') })) -join ',')"
        "Family1DecisionKinds=$($script:FileTransferCoordinatorSnapshotFamily1DecisionKinds -join ',')"
        "Family1OperationKinds=$((@($script:FileTransferCoordinatorSnapshotFamily1OperationKinds.Keys | ForEach-Object { '{0}:{1}' -f $_, ($script:FileTransferCoordinatorSnapshotFamily1OperationKinds[$_] -join '|') })) -join ',')"
        "Family2=$($script:FileTransferCoordinatorSnapshotFamily2 -join ',')"
        "Family2Effects=$($script:FileTransferCoordinatorSnapshotFamily2EffectKinds -join ',')"
        "Family2DecisionKinds=$($script:FileTransferCoordinatorSnapshotFamily2DecisionKinds -join ',')"
        "Family2OperationRoles=$($script:FileTransferCoordinatorSnapshotFamily2OperationRoles -join ',')"
        "Family2ReceiptStages=$((@($script:FileTransferCoordinatorSnapshotFamily2ReceiptStages.Keys | ForEach-Object { '{0}:{1}' -f $_, ($script:FileTransferCoordinatorSnapshotFamily2ReceiptStages[$_] -join '|') })) -join ',')"
        "DecisionCatalog=$((@($script:FileTransferCoordinatorSnapshotDecisionCatalog.Keys | ForEach-Object { $entry = $script:FileTransferCoordinatorSnapshotDecisionCatalog[$_]; '{0}|{1}|{2}|{3}|{4}' -f $_, $entry.family, $entry.class, $entry.domain, $entry.phase })) -join ',')"
    ) -join "`n"
    $actualHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes (
        [System.Text.UTF8Encoding]::new($false).GetBytes($canonical))
    if ($actualHash -ne $script:FileTransferCoordinatorSnapshotCatalogHash) {
        throw "coordinator_snapshot_catalog_hash_invalid:$actualHash"
    }

    $legacyCatalogPath = Join-Path $PSScriptRoot 'LegacyV0EvidenceCatalog.json'
    if (-not (Test-Path -LiteralPath $legacyCatalogPath -PathType Leaf)) {
        throw "legacy_v0_evidence_catalog_missing:$legacyCatalogPath"
    }
    $legacyBytes = [System.IO.File]::ReadAllBytes($legacyCatalogPath)
    $legacyHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $legacyBytes
    if ($legacyHash -ne $script:FileTransferLegacyV0CatalogHash) {
        throw "legacy_v0_evidence_catalog_hash_invalid:$legacyHash"
    }
    $legacyCatalog = (
        [System.Text.UTF8Encoding]::new($false, $true).GetString($legacyBytes)) |
        ConvertFrom-Json
    if ([string]$legacyCatalog.version -ne $script:FileTransferLegacyV0CatalogVersion) {
        throw "legacy_v0_evidence_catalog_version_unknown:$($legacyCatalog.version)"
    }

    $result = [pscustomobject]@{
        Version = $script:FileTransferCoordinatorSnapshotCatalogVersion
        Hash = $script:FileTransferCoordinatorSnapshotCatalogHash
        SnapshotContractVersion = $script:FileTransferCoordinatorSnapshotContractVersion
        SnapshotAuthorityV1 = $snapshotEvents
        GenerationStampedEffects = $generationStampedEffects
        NonAuthorityEvidence = $nonAuthorityEvidence
        RequiredFamilies = $script:FileTransferCoordinatorSnapshotRequiredFamilies
        Family1 = $script:FileTransferCoordinatorSnapshotFamily1
        Family1EffectKinds = $script:FileTransferCoordinatorSnapshotFamily1EffectKinds
        Family1RouteTransitionFields = $script:FileTransferCoordinatorSnapshotFamily1RouteTransitionFields
        Family1ReceiptStages = $script:FileTransferCoordinatorSnapshotFamily1ReceiptStages
        Family1DecisionKinds = $script:FileTransferCoordinatorSnapshotFamily1DecisionKinds
        Family1OperationKinds = $script:FileTransferCoordinatorSnapshotFamily1OperationKinds
        Family2 = $script:FileTransferCoordinatorSnapshotFamily2
        Family2EffectKinds = $script:FileTransferCoordinatorSnapshotFamily2EffectKinds
        Family2DecisionKinds = $script:FileTransferCoordinatorSnapshotFamily2DecisionKinds
        Family2OperationRoles = $script:FileTransferCoordinatorSnapshotFamily2OperationRoles
        Family2ReceiptStages = $script:FileTransferCoordinatorSnapshotFamily2ReceiptStages
        DecisionCatalog = $script:FileTransferCoordinatorSnapshotDecisionCatalog
        LegacyV0 = [pscustomobject]@{
            Version = $script:FileTransferLegacyV0CatalogVersion
            Hash = $script:FileTransferLegacyV0CatalogHash
            EventCount = @($legacyCatalog.events).Count
            CorrelatorCount = @($legacyCatalog.correlators).Count
        }
    }
    $script:FileTransferCoordinatorEvidenceCatalogCache = $result
    return $result
}

function Get-FileTransferCoordinatorSnapshotLineField {
    param(
        [Parameter(Mandatory = $true)][string]$Line,
        [Parameter(Mandatory = $true)][string]$Name,
        [string]$Default = ''
    )

    $match = [regex]::Match(
        $Line,
        '(?:^|;\s*)' + [regex]::Escape($Name) + '=([^;\r\n]*)',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        return $Default
    }
    return $match.Groups[1].Value.Trim()
}

function Get-FileTransferCoordinatorSnapshotEventName {
    param([Parameter(Mandatory = $true)][string]$Line)

    $match = [regex]::Match(
        $Line,
        '(?:^|[;\s])event=([a-z0-9_]+)',
        [System.Text.RegularExpressions.RegexOptions]::CultureInvariant)
    if (-not $match.Success) {
        return ''
    }
    return $match.Groups[1].Value
}

function Get-FileTransferCoordinatorSnapshotObjectProperty {
    param(
        $InputObject,
        [Parameter(Mandatory = $true)][string]$Name,
        $Default = $null
    )

    if ($null -eq $InputObject -or
        $InputObject.PSObject.Properties.Name -notcontains $Name) {
        return $Default
    }
    return $InputObject.$Name
}

function Get-FileTransferCoordinatorSnapshotSha256 {
    param([Parameter(Mandatory = $true)][byte[]]$Bytes)

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha.ComputeHash($Bytes))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $sha.Dispose()
    }
}

function Test-FileTransferCoordinatorSnapshotEffectScope {
    param(
        [Parameter(Mandatory = $true)]$Operation,
        [Parameter(Mandatory = $true)]$Effect
    )

    $scope = if ($null -ne $Operation.routeRuntime) {
        $Operation.routeRuntime.boundary
    }
    elseif ($null -ne $Operation.routeTransition) {
        $Operation.routeTransition.sourceBoundary
    }
    else {
        $Operation.runtimeUnlockRetry.sourceBoundary
    }
    if ($null -eq $scope) {
        return $false
    }

    foreach ($pair in @(
            @('route', 'Route'),
            @('protocolVersion', 'ProtocolVersion'),
            @('liveRouteEpoch', 'LiveRouteEpoch'),
            @('routePublicationGeneration', 'RoutePublicationGeneration'),
            @('routeCommitGeneration', 'RouteCommitGeneration'),
            @('runtimeGeneration', 'RuntimeGeneration'),
            @('terminalGeneration', 'TerminalGeneration'),
            @('legGeneration', 'LegGeneration'),
            @('transportEpoch', 'TransportEpoch'),
            @('bridgeRecoveryGeneration', 'BridgeRecoveryGeneration'),
            @('checkpointGeneration', 'CheckpointGeneration'),
            @('checkpointRequestId', 'CheckpointRequestId'),
            @('dataSessionIdentity', 'DataSessionIdentity'),
            @('dataSessionGeneration', 'DataSessionGeneration'))) {
        $expected = [string](
            Get-FileTransferCoordinatorSnapshotObjectProperty `
                -InputObject $scope `
                -Name $pair[0] `
                -Default '')
        if ([string]::IsNullOrWhiteSpace($expected) -or
            [string]::IsNullOrWhiteSpace([string]$Effect.($pair[1])) -or
            $expected -cne [string]$Effect.($pair[1])) {
            return $false
        }
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotBoundaryComplete {
    param([Parameter(Mandatory = $true)]$Boundary)

    foreach ($name in @(
            'route',
            'protocolVersion',
            'liveRouteEpoch',
            'routePublicationGeneration',
            'routeCommitGeneration',
            'runtimeGeneration',
            'terminalGeneration',
            'legGeneration',
            'transportEpoch',
            'bridgeRecoveryGeneration',
            'checkpointGeneration',
            'checkpointRequestId',
            'dataSessionIdentity',
            'dataSessionGeneration')) {
        if ($Boundary.PSObject.Properties.Name -notcontains $name -or
            [string]::IsNullOrWhiteSpace([string]$Boundary.$name)) {
            return $false
        }
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotBoundaryEqual {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Left,
        [Parameter(Mandatory = $true)]$Right
    )

    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Left) -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Right)) {
        return $false
    }

    return (ConvertTo-Json -InputObject $Left -Depth 16 -Compress) -ceq
        (ConvertTo-Json -InputObject $Right -Depth 16 -Compress)
}

function Test-FileTransferCoordinatorSnapshotFamily1OperationCatalog {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        [Parameter(Mandatory = $true)]$Operation
    )

    if ([string]$Operation.family -notin
        $script:FileTransferCoordinatorSnapshotFamily1) {
        return $true
    }
    $expectedOperationKey = @(
        [string]$Operation.family,
        [string]$Operation.operationRole,
        [string]$Operation.authorityId,
        ([long]$Operation.operationGeneration).ToString(
            [System.Globalization.CultureInfo]::InvariantCulture),
        [string]$Operation.operationKind) -join '|'
    if ([string]$Operation.operationKey -cne $expectedOperationKey) {
        return $false
    }
    if ([string]$Envelope.decisionKind -notin
        $script:FileTransferCoordinatorSnapshotFamily1DecisionKinds -or
        [string]$Envelope.decisionStatus -notin @(
            'Accepted', 'Idempotent', 'Rejected', 'Prepared', 'Draining',
            'Committed', 'Invalidated', 'SupersessionStaged') -or
        [string]::IsNullOrWhiteSpace([string]$Envelope.decisionReason)) {
        return $false
    }

    $role = [string]$Operation.operationRole
    if (-not $script:FileTransferCoordinatorSnapshotFamily1OperationKinds.Contains($role) -or
        [string]$Operation.operationKind -notin
            @($script:FileTransferCoordinatorSnapshotFamily1OperationKinds[$role])) {
        return $false
    }
    if ([string]$Operation.family -ceq 'route_runtime') {
        return [string]$Operation.beforeState -in @(
            'None', 'Attempted', 'Current', 'Committed', 'Published', 'Terminal') -and
            [string]$Operation.afterState -in @(
                'None', 'Attempted', 'Current', 'Committed', 'Published', 'Terminal')
    }

    foreach ($name in @(
            'role', 'transitionKind', 'sourceTransport', 'proofBoundaryKind',
            'proofAdmissionMode',
            'proofBoundaryFingerprint', 'durableCheckpointFingerprint',
            'targetRoute', 'targetProtocolVersion', 'targetTransport',
            'handoffKind', 'dispositionKind', 'retainedFrameType',
            'retainedFrameFingerprint')) {
        if ($Operation.routeTransition.PSObject.Properties.Name -notcontains $name -or
            [string]::IsNullOrWhiteSpace([string]$Operation.routeTransition.$name)) {
            return $false
        }
    }
    if (-not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Operation.routeTransition.targetBoundary)) {
        return $false
    }

    if ([string]$Operation.beforeState -notin @(
            'None', 'Unknown', 'Current', 'Prepared', 'Draining', 'Committed',
            'Invalidated', 'Staged', 'Rebound', 'Pending', 'Delivered',
            'Idempotent', 'Rejected') -or
        [string]$Operation.afterState -notin @(
            'None', 'Unknown', 'Current', 'Prepared', 'Draining', 'Committed',
            'Invalidated', 'Staged', 'Rebound', 'Pending', 'Delivered',
            'Idempotent', 'Rejected')) {
        return $false
    }
    foreach ($receipt in @($Operation.routeTransition.receipts)) {
        $stage = [string]$receipt.stage
        if (-not $script:FileTransferCoordinatorSnapshotFamily1ReceiptStages.Contains($stage)) {
            return $false
        }
        $allowed = @($script:FileTransferCoordinatorSnapshotFamily1ReceiptStages[$stage])
        if ([string]$receipt.receiptStatus -notin $allowed) {
            return $false
        }
    }
    return $true
}

function Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog {
    param(
        [Parameter(Mandatory = $true)]$Envelope,
        [Parameter(Mandatory = $true)]$Operation
    )

    if ([string]$Operation.family -notin
        $script:FileTransferCoordinatorSnapshotFamily2) {
        return $true
    }
    $expectedOperationKey = @(
        [string]$Operation.family,
        [string]$Operation.operationRole,
        [string]$Operation.authorityId,
        ([long]$Operation.operationGeneration).ToString(
            [System.Globalization.CultureInfo]::InvariantCulture),
        [string]$Operation.operationKind) -join '|'
    if ([string]$Operation.operationKey -cne $expectedOperationKey -or
        [string]$Envelope.decisionKind -notin
            $script:FileTransferCoordinatorSnapshotFamily2DecisionKinds -or
        [string]$Operation.operationRole -notin
            $script:FileTransferCoordinatorSnapshotFamily2OperationRoles -or
        $null -eq $Operation.runtimeUnlockRetry -or
        -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
            -Boundary $Operation.runtimeUnlockRetry.sourceBoundary)) {
        return $false
    }

    $role = [string]$Operation.operationRole
    $kind = [string]$Operation.operationKind
    if (($role -like 'attempt_*' -and
         $role -cne 'attempted_decision' -and
         $kind -cne 'RetryAttempt') -or
        ($role -ceq 'bounded_closure' -and $kind -cne 'BoundedClosure') -or
        ($role -ceq 'scope_invalidation' -and $kind -cne 'ScopeInvalidation') -or
        ($role -ceq 'attempted_decision' -and
         $kind -cne [string]$Envelope.decisionKind)) {
        return $false
    }

    $states = @(
        'None', 'Authorized', 'Staged', 'ReceiptIssued', 'Installed',
        'AliasBound', 'SendInFlight', 'Settled', 'Invalidated', 'Expired',
        'Closed', 'Pending', 'Accepted', 'Idempotent', 'Rejected')
    if ([string]$Operation.beforeState -notin $states -or
        [string]$Operation.afterState -notin $states) {
        return $false
    }
    foreach ($receipt in @($Operation.runtimeUnlockRetry.receipts)) {
        $stage = [string]$receipt.stage
        if (-not $script:FileTransferCoordinatorSnapshotFamily2ReceiptStages.Contains($stage) -or
            [string]$receipt.receiptStatus -notin
                @($script:FileTransferCoordinatorSnapshotFamily2ReceiptStages[$stage])) {
            return $false
        }
    }
    return $true
}

function ConvertFrom-FileTransferCoordinatorSnapshotPayload {
    param([Parameter(Mandatory = $true)][string]$Payload)

    $base64 = $Payload.Replace('-', '+').Replace('_', '/')
    switch ($base64.Length % 4) {
        2 { $base64 += '==' }
        3 { $base64 += '=' }
        1 { throw 'snapshot_payload_invalid_base64url_length' }
    }
    $bytes = [Convert]::FromBase64String($base64)
    $json = [System.Text.UTF8Encoding]::new($false, $true).GetString($bytes)
    [pscustomobject]@{
        Bytes = $bytes
        Json = $json
        Value = ($json | ConvertFrom-Json)
    }
}

function Test-FileTransferCoordinatorRouteTransitionOperationClosed {
    param(
        [Parameter(Mandatory = $true)]$Operation,
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Effects
    )

    $state = [string]$Operation.afterState
    $receipts = @($Operation.routeTransition.receipts)
    $hasReceipt = {
        param([string]$Stage)
        @($receipts | Where-Object {
                [string]$_.stage -ceq $Stage -and [bool]$_.receiptValidated
            }).Count -eq 1
    }
    $hasEffect = {
        param([string]$Kind)
        @($Effects | Where-Object {
                [string]$_.OperationKey -ceq [string]$Operation.operationKey -and
                [string]$_.EffectKind -ceq $Kind
            }).Count -ge 1
    }

    if ($state -ceq 'Committed') {
        if (-not (& $hasReceipt 'pause_bind') -or
            -not (& $hasReceipt 'drain') -or
            -not (& $hasReceipt 'commit') -or
            -not (& $hasReceipt 'disposition') -or
            -not (& $hasEffect 'pause_installed') -or
            -not (& $hasEffect 'completed_drain_receipt') -or
            -not (& $hasEffect 'route_commit_adopted') -or
            -not (& $hasEffect 'target_runtime_leg_installed') -or
            -not (& $hasEffect 'route_transition_disposition_applied') -or
            -not (& $hasEffect 'transition_pause_released') -or
            -not (& $hasEffect 'recovered_epoch_publication')) {
            return $false
        }
        $retainedFingerprint = [string]$Operation.routeTransition.retainedFrameFingerprint
        $retainedFrameType = [string]$Operation.routeTransition.retainedFrameType
        if ($retainedFingerprint -cne '(none)' -and
            (-not (& $hasReceipt 'retained_frame') -or
             -not (& $hasEffect 'retained_frame_applied'))) {
            return $false
        }
        if ($retainedFrameType -ceq 'filetransfer.frontier_request.v6' -and
            -not (& $hasEffect 'retained_frame_response')) {
            return $false
        }
        return $true
    }
    if ($state -ceq 'Invalidated') {
        return (& $hasReceipt 'disposition') -and
               (& $hasEffect 'route_transition_disposition_applied') -and
               (& $hasEffect 'transition_pause_released')
    }
    return $false
}

function Get-FileTransferCoordinatorSnapshotEvaluation {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][string[]]$Lines)

    $catalog = Get-FileTransferCoordinatorEvidenceCatalog
    $records = [System.Collections.Generic.List[object]]::new()
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        $eventName = Get-FileTransferCoordinatorSnapshotEventName -Line ([string]$Lines[$index])
        if ($catalog.SnapshotAuthorityV1 -notcontains $eventName -and
            $catalog.GenerationStampedEffects -notcontains $eventName) {
            continue
        }
        $records.Add([pscustomobject]@{
                LineIndex = $index
                Line = [string]$Lines[$index]
                EventName = $eventName
                CoordinatorInstanceId = Get-FileTransferCoordinatorSnapshotLineField -Line ([string]$Lines[$index]) -Name 'coordinator_instance_id'
            })
    }

    if ($records.Count -eq 0) {
        return [pscustomobject]@{
            Mode = 'LegacyV0'
            ContractVersion = 0
            CatalogVersion = $catalog.LegacyV0.Version
            CatalogHash = $catalog.LegacyV0.Hash
            StreamComplete = $false
            ArtifactEvidenceVerdict = 'pass'
            ArtifactEvidenceFailureReason = ''
            TransitionCount = 0
            EffectCount = 0
            BoundaryCount = 0
            BoundaryHash = ''
            Boundaries = @()
            FirstUnresolvedBoundary = $null
            LegacySelectionCount = 1
            RouteFamilyLegacySelectionCount = 2
            RuntimeUnlockRetryFamilyLegacySelectionCount = 1
        }
    }

    $errors = [System.Collections.Generic.List[string]]::new()
    $boundaries = [System.Collections.Generic.List[object]]::new()
    $effects = [System.Collections.Generic.List[object]]::new()
    $seenTransitions = @{}
    $seenEffects = @{}
    $streams = @{}
    $selectionModes = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::Ordinal)

    foreach ($record in @($records)) {
        $line = $record.Line
        $instanceId = $record.CoordinatorInstanceId
        if ([string]::IsNullOrWhiteSpace($instanceId)) {
            $errors.Add("snapshot_instance_missing:$($record.LineIndex)")
            continue
        }
        if (-not $streams.ContainsKey($instanceId)) {
            $streams[$instanceId] = [pscustomobject]@{
                Started = $false
                Completed = $false
                InitialGeneration = 0L
                InitialHash = ''
                CurrentGeneration = 0L
                CurrentHash = ''
                TransitionCount = 0L
                EmittedFamilies = @()
                ShadowFamilies = @()
                AuthoritativeFamilies = @()
                SessionId = ''
                TransferId = ''
                Direction = ''
            }
        }
        $stream = $streams[$instanceId]
        $recordSessionId = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'session_id'
        $recordTransferId = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'transfer_id'
        $recordDirection = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'direction'
        if ([string]::IsNullOrWhiteSpace($recordSessionId) -or
            [string]::IsNullOrWhiteSpace($recordTransferId) -or
            [string]::IsNullOrWhiteSpace($recordDirection)) {
            $errors.Add("snapshot_stream_scope_missing:$($record.LineIndex)")
        }
        elseif ([string]::IsNullOrWhiteSpace($stream.SessionId)) {
            $stream.SessionId = $recordSessionId
            $stream.TransferId = $recordTransferId
            $stream.Direction = $recordDirection
        }
        elseif ($stream.SessionId -cne $recordSessionId -or
            $stream.TransferId -cne $recordTransferId -or
            $stream.Direction -cne $recordDirection) {
            $errors.Add("snapshot_stream_scope_conflict:$($record.LineIndex)")
        }
        $contractVersion = 0
        if (-not [int]::TryParse(
                (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'snapshot_contract_version'),
                [ref]$contractVersion) -or
            $contractVersion -ne $script:FileTransferCoordinatorSnapshotContractVersion) {
            $errors.Add("snapshot_contract_version_unknown:$($record.LineIndex)")
            continue
        }
        $catalogVersion = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'snapshot_catalog_version'
        $catalogHash = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'snapshot_catalog_hash'
        if ($catalogVersion -ne $catalog.Version -or $catalogHash -ne $catalog.Hash) {
            $errors.Add("snapshot_catalog_mismatch:$($record.LineIndex)")
        }
        $selectionMode = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'snapshot_selection_mode'
        if ($selectionMode -notin @(
                'SnapshotV1Shadow',
                'SnapshotV1Authoritative')) {
            $errors.Add("snapshot_selection_mode_unknown:$($record.LineIndex)")
        }
        else {
            [void]$selectionModes.Add($selectionMode)
        }

        switch ($record.EventName) {
            'filetransfer_coordinator_snapshot_stream_started' {
                if ($stream.Started) {
                    $duplicateGeneration = 0L
                    [void][long]::TryParse(
                        (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'initial_state_generation'),
                        [ref]$duplicateGeneration)
                    $duplicateHash = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'initial_state_hash'
                    $duplicateEmitted = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'emitted_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                    $duplicateShadow = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'shadow_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                    $duplicateAuthoritative = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'authoritative_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                    if ($duplicateGeneration -ne $stream.InitialGeneration -or
                        $duplicateHash -cne $stream.InitialHash -or
                        (@($duplicateEmitted | Sort-Object) -join ',') -cne (@($stream.EmittedFamilies | Sort-Object) -join ',') -or
                        (@($duplicateShadow | Sort-Object) -join ',') -cne (@($stream.ShadowFamilies | Sort-Object) -join ',') -or
                        (@($duplicateAuthoritative | Sort-Object) -join ',') -cne (@($stream.AuthoritativeFamilies | Sort-Object) -join ',')) {
                        $errors.Add("snapshot_stream_start_conflicting_duplicate:$instanceId")
                    }
                    break
                }
                $generation = 0L
                if (-not [long]::TryParse(
                        (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'initial_state_generation'),
                        [ref]$generation)) {
                    $errors.Add("snapshot_initial_generation_invalid:$instanceId")
                    break
                }
                $stream.Started = $true
                $stream.InitialGeneration = $generation
                $stream.CurrentGeneration = $generation
                $stream.InitialHash = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'initial_state_hash'
                $stream.CurrentHash = $stream.InitialHash
                $stream.EmittedFamilies = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'emitted_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                $stream.ShadowFamilies = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'shadow_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                $stream.AuthoritativeFamilies = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'authoritative_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                if ([string]::IsNullOrWhiteSpace($stream.InitialHash)) {
                    $errors.Add("snapshot_initial_hash_missing:$instanceId")
                }
                $distinctFamilies = @($stream.EmittedFamilies | Sort-Object -Unique)
                $unknownFamilies = @(
                    $distinctFamilies | Where-Object {
                        $script:FileTransferCoordinatorSnapshotRequiredFamilies -notcontains $_
                    })
                $invalidShadow = @($stream.ShadowFamilies | Where-Object { $stream.EmittedFamilies -notcontains $_ })
                $invalidAuthoritative = @($stream.AuthoritativeFamilies | Where-Object { $stream.EmittedFamilies -notcontains $_ })
                $overlappingCoverage = @(
                    $stream.ShadowFamilies | Where-Object {
                        $stream.AuthoritativeFamilies -contains $_
                    })
                if ($stream.EmittedFamilies.Count -eq 0 -or
                    $distinctFamilies.Count -ne $stream.EmittedFamilies.Count -or
                    $unknownFamilies.Count -gt 0 -or
                    $invalidShadow.Count -gt 0 -or
                    $invalidAuthoritative.Count -gt 0 -or
                    $overlappingCoverage.Count -gt 0) {
                    $errors.Add("snapshot_declared_coverage_invalid:$instanceId")
                }
            }
            'filetransfer_coordinator_transition_snapshot' {
                if (-not $stream.Started -or $stream.Completed) {
                    $errors.Add("snapshot_transition_outside_stream:$($record.LineIndex)")
                    break
                }
                $transitionId = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'transition_id'
                $payload = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'snapshot_payload'
                $declaredHash = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'snapshot_sha256'
                if ([string]::IsNullOrWhiteSpace($transitionId) -or
                    [string]::IsNullOrWhiteSpace($payload) -or
                    [string]::IsNullOrWhiteSpace($declaredHash)) {
                    $errors.Add("snapshot_transition_identity_incomplete:$($record.LineIndex)")
                    break
                }
                try {
                    $decoded = ConvertFrom-FileTransferCoordinatorSnapshotPayload -Payload $payload
                }
                catch {
                    $errors.Add("snapshot_payload_invalid:$($record.LineIndex)")
                    break
                }
                $actualHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes $decoded.Bytes
                if ($actualHash -ne $declaredHash) {
                    $errors.Add("snapshot_payload_hash_mismatch:$($record.LineIndex)")
                    break
                }
                $envelope = $decoded.Value
                if ([int]$envelope.snapshotContractVersion -ne $contractVersion -or
                    [string]$envelope.coordinatorInstanceId -cne $instanceId -or
                    [string]$envelope.transitionId -ne $transitionId -or
                    [string]$envelope.snapshotSelectionMode -cne $selectionMode -or
                    [string]$envelope.sessionId -cne $recordSessionId -or
                    [string]$envelope.transferId -cne $recordTransferId -or
                    [string]$envelope.direction -cne $recordDirection) {
                    $errors.Add("snapshot_header_payload_identity_mismatch:$($record.LineIndex)")
                    break
                }
                $beforeGeneration = [long]$envelope.aggregate.beforeStateGeneration
                $afterGeneration = [long]$envelope.aggregate.afterStateGeneration
                $beforeHash = [string]$envelope.aggregate.beforeStateFingerprint
                $afterHash = [string]$envelope.aggregate.afterStateFingerprint
                $headerBeforeGeneration = 0L
                $headerAfterGeneration = 0L
                if (-not [long]::TryParse(
                        (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'before_state_generation'),
                        [ref]$headerBeforeGeneration) -or
                    -not [long]::TryParse(
                        (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'after_state_generation'),
                        [ref]$headerAfterGeneration) -or
                    $headerBeforeGeneration -ne $beforeGeneration -or
                    $headerAfterGeneration -ne $afterGeneration) {
                    $errors.Add("snapshot_header_payload_generation_mismatch:$($record.LineIndex)")
                    break
                }
                $transitionKey = "$instanceId|$transitionId"
                if ($seenTransitions.ContainsKey($transitionKey)) {
                    if ($seenTransitions[$transitionKey] -ne $actualHash) {
                        $errors.Add("snapshot_transition_conflicting_duplicate:$transitionId")
                    }
                    break
                }
                $seenTransitions[$transitionKey] = $actualHash
                if ($beforeGeneration -ne $stream.CurrentGeneration -or $beforeHash -ne $stream.CurrentHash) {
                    $errors.Add("snapshot_generation_or_hash_gap:$($record.LineIndex)")
                    break
                }
                $stateChanged = $beforeHash -ne $afterHash
                if (($stateChanged -and $afterGeneration -ne ($beforeGeneration + 1L)) -or
                    (-not $stateChanged -and $afterGeneration -ne $beforeGeneration)) {
                    $errors.Add("snapshot_generation_transition_invalid:$($record.LineIndex)")
                    break
                }
                $decisionStatus = [string]$envelope.decisionStatus
                if ([string]::IsNullOrWhiteSpace($decisionStatus) -or
                    ($stateChanged -and $decisionStatus -in @('Idempotent', 'Rejected')) -or
                    (-not $stateChanged -and $decisionStatus -eq 'Accepted')) {
                    $errors.Add("snapshot_decision_state_transition_invalid:$($record.LineIndex)")
                    break
                }
                foreach ($operation in @($envelope.aggregate.operations)) {
                    if ([string]::IsNullOrWhiteSpace([string]$operation.family) -or
                        [string]::IsNullOrWhiteSpace([string]$operation.operationRole) -or
                        [string]::IsNullOrWhiteSpace([string]$operation.operationKey) -or
                        [string]::IsNullOrWhiteSpace([string]$operation.operationKind) -or
                        [string]::IsNullOrWhiteSpace([string]$operation.authorityId) -or
                        [long]$operation.operationGeneration -le 0 -or
                        [string]::IsNullOrWhiteSpace([string]$operation.commandIdentity) -or
                        [long]$operation.commandGeneration -le 0 -or
                        [string]::IsNullOrWhiteSpace([string]$operation.commandFingerprint) -or
                        [string]::IsNullOrWhiteSpace([string]$operation.receiptIdentity) -or
                        [string]::IsNullOrWhiteSpace([string]$operation.receiptStatus) -or
                        [string]::IsNullOrWhiteSpace([string]$operation.receiptFingerprint)) {
                        $errors.Add("snapshot_operation_identity_incomplete:$($record.LineIndex)")
                        break
                    }
                    if (-not (Test-FileTransferCoordinatorSnapshotFamily1OperationCatalog `
                            -Envelope $envelope `
                            -Operation $operation)) {
                        $errors.Add("snapshot_operation_catalog_mismatch:$($record.LineIndex)")
                        break
                    }
                    if (-not (Test-FileTransferCoordinatorSnapshotFamily2OperationCatalog `
                            -Envelope $envelope `
                            -Operation $operation)) {
                        $errors.Add("snapshot_operation_catalog_mismatch:$($record.LineIndex)")
                        break
                    }
                    if ([string]$operation.family -ceq 'route_runtime' -and
                        ($null -eq $operation.routeRuntime -or
                         -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                            -Boundary $operation.routeRuntime.boundary)) -or
                        [string]$operation.family -ceq 'route_transition' -and
                        ($null -eq $operation.routeTransition -or
                         -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                            -Boundary $operation.routeTransition.sourceBoundary)) -or
                        [string]$operation.family -ceq 'runtime_unlock_retry_lifecycle' -and
                        ($null -eq $operation.runtimeUnlockRetry -or
                         -not (Test-FileTransferCoordinatorSnapshotBoundaryComplete `
                            -Boundary $operation.runtimeUnlockRetry.sourceBoundary))) {
                        $errors.Add("snapshot_operation_scope_incomplete:$($record.LineIndex)")
                        break
                    }
                    if ([string]$operation.family -ceq 'route_transition') {
                        foreach ($receipt in @($operation.routeTransition.receipts)) {
                            if ([string]::IsNullOrWhiteSpace([string]$receipt.stage) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.commandIdentity) -or
                                [long]$receipt.commandGeneration -le 0 -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.commandFingerprint) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptIdentity) -or
                                [long]$receipt.receiptGeneration -le 0 -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptStatus) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptFingerprint)) {
                                $errors.Add("snapshot_operation_receipt_incomplete:$($record.LineIndex)")
                                break
                            }
                        }
                    }
                    if ([string]$operation.family -ceq
                        'runtime_unlock_retry_lifecycle') {
                        foreach ($receipt in @($operation.runtimeUnlockRetry.receipts)) {
                            if ([string]::IsNullOrWhiteSpace([string]$receipt.stage) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.commandIdentity) -or
                                [long]$receipt.commandGeneration -le 0 -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.commandFingerprint) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptIdentity) -or
                                [long]$receipt.receiptGeneration -le 0 -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptStatus) -or
                                [string]::IsNullOrWhiteSpace([string]$receipt.receiptFingerprint)) {
                                $errors.Add("snapshot_operation_receipt_incomplete:$($record.LineIndex)")
                                break
                            }
                        }
                    }
                    if ([bool]$operation.receiptValidated -and
                        ([string]$operation.receiptIdentity -ceq '(none)' -or
                         [string]$operation.receiptStatus -ceq '(none)' -or
                         [string]$operation.receiptFingerprint -ceq '(none)' -or
                         [long]$operation.receiptGeneration -le 0)) {
                        $errors.Add("snapshot_operation_validated_receipt_incomplete:$($record.LineIndex)")
                        break
                    }
                }
                $decisionUtcMilliseconds = 0L
                if ($envelope.PSObject.Properties.Name -contains 'decisionUtcMilliseconds') {
                    $decisionUtcMilliseconds = [long]$envelope.decisionUtcMilliseconds
                }
                $boundary = [ordered]@{
                    boundaryId = $transitionId
                    boundaryHash = $actualHash
                    coordinatorInstanceId = $instanceId
                    evidenceLineIndex = [int]$record.LineIndex
                    writer = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'log_writer_id'
                    writerSequence = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'log_writer_sequence'
                    decisionUtcMilliseconds = $decisionUtcMilliseconds
                    beforeStateGeneration = $beforeGeneration
                    afterStateGeneration = $afterGeneration
                    beforeStateHash = $beforeHash
                    afterStateHash = $afterHash
                    decisionKind = [string]$envelope.decisionKind
                    decisionStatus = $decisionStatus
                    decisionReason = [string]$envelope.decisionReason
                    families = @($envelope.aggregate.changedFamilies)
                    operations = @($envelope.aggregate.operations)
                    verdict = 'pass'
                    taxonomyClass = '(none)'
                    domain = 'route_runtime'
                    phase = 'coordinator_transition'
                    rawCount = 0
                    distinctCount = 0
                    recoveredCount = 0
                    unresolvedCount = 0
                }
                $boundaries.Add([pscustomobject]$boundary)
                $stream.CurrentGeneration = $afterGeneration
                $stream.CurrentHash = $afterHash
                $stream.TransitionCount++
            }
            'filetransfer_coordinator_snapshot_stream_completed' {
                if (-not $stream.Started) {
                    $errors.Add("snapshot_stream_completion_invalid:$instanceId")
                    break
                }
                $finalGeneration = 0L
                $transitionCount = 0L
                [void][long]::TryParse((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'final_state_generation'), [ref]$finalGeneration)
                [void][long]::TryParse((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'transition_count'), [ref]$transitionCount)
                $finalHash = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'final_state_hash'
                $completionEmitted = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'emitted_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                $completionShadow = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'shadow_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                $completionAuthoritative = @((Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'authoritative_family_coverage').Split(',', [System.StringSplitOptions]::RemoveEmptyEntries))
                if ($finalGeneration -ne $stream.CurrentGeneration -or
                    $finalHash -ne $stream.CurrentHash -or
                    $transitionCount -ne $stream.TransitionCount -or
                    (@($completionEmitted | Sort-Object) -join ',') -cne (@($stream.EmittedFamilies | Sort-Object) -join ',') -or
                    (@($completionShadow | Sort-Object) -join ',') -cne (@($stream.ShadowFamilies | Sort-Object) -join ',') -or
                    (@($completionAuthoritative | Sort-Object) -join ',') -cne (@($stream.AuthoritativeFamilies | Sort-Object) -join ',')) {
                    $errors.Add("snapshot_stream_completion_mismatch:$instanceId")
                    break
                }
                $stream.Completed = $true
            }
            'filetransfer_coordinator_authority_effect' {
                if (-not $stream.Started -or $stream.Completed) {
                    $errors.Add("snapshot_authority_effect_outside_stream:$($record.LineIndex)")
                    break
                }
                $stateGeneration = 0L
                $operationGeneration = 0L
                $receiptGeneration = 0L
                if (-not [long]::TryParse(
                        (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'coordinator_state_generation'),
                        [ref]$stateGeneration) -or
                    -not [long]::TryParse(
                        (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'operation_generation'),
                        [ref]$operationGeneration) -or
                    -not [long]::TryParse(
                        (Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'receipt_generation'),
                        [ref]$receiptGeneration)) {
                    $errors.Add("snapshot_authority_effect_generation_invalid:$($record.LineIndex)")
                    break
                }
                $effect = [pscustomobject]@{
                        LineIndex = $record.LineIndex
                        CoordinatorInstanceId = $instanceId
                        TransitionId = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'transition_id'
                        StateGeneration = $stateGeneration
                        Family = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'operation_family'
                        OperationKey = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'operation_key'
                        AuthorityId = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'operation_authority_id'
                        OperationGeneration = $operationGeneration
                        EffectKind = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'effect_kind'
                        EffectIdentity = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'effect_identity'
                        ReceiptIdentity = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'receipt_identity'
                        ReceiptGeneration = $receiptGeneration
                        ReceiptStatus = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'receipt_status'
                        ReceiptFingerprint = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'receipt_fingerprint'
                        Route = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'route'
                        ProtocolVersion = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'protocol_version'
                        LiveRouteEpoch = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'live_route_epoch'
                        RoutePublicationGeneration = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'route_publication_generation'
                        RouteCommitGeneration = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'route_commit_generation'
                        RuntimeGeneration = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'runtime_generation'
                        TerminalGeneration = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'terminal_generation'
                        LegGeneration = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'leg_generation'
                        TransportEpoch = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'transport_epoch'
                        BridgeRecoveryGeneration = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'bridge_recovery_generation'
                        CheckpointGeneration = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'checkpoint_generation'
                        CheckpointRequestId = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'checkpoint_request_id'
                        DataSessionIdentity = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'data_session_identity'
                        DataSessionGeneration = Get-FileTransferCoordinatorSnapshotLineField -Line $line -Name 'data_session_generation'
                    }
                $effectIdentity = "$instanceId|$($effect.TransitionId)|$stateGeneration|$($effect.Family)|$($effect.OperationKey)|$($effect.AuthorityId)|$operationGeneration|$($effect.EffectKind)|$($effect.EffectIdentity)|$($effect.ReceiptIdentity)|$receiptGeneration"
                $effectFingerprint = [string]$line
                if ($seenEffects.ContainsKey($effectIdentity)) {
                    if ($seenEffects[$effectIdentity] -cne $effectFingerprint) {
                        $errors.Add("snapshot_authority_effect_conflicting_duplicate:$($record.LineIndex)")
                    }
                    break
                }
                $seenEffects[$effectIdentity] = $effectFingerprint
                $effects.Add($effect) | Out-Null
            }
        }
    }

    foreach ($effect in $effects) {
        if ([string]::IsNullOrWhiteSpace($effect.Family) -or
            [string]::IsNullOrWhiteSpace($effect.TransitionId) -or
            [string]::IsNullOrWhiteSpace($effect.OperationKey) -or
            [string]::IsNullOrWhiteSpace($effect.AuthorityId) -or
            [string]::IsNullOrWhiteSpace($effect.EffectKind) -or
            [string]::IsNullOrWhiteSpace($effect.EffectIdentity) -or
            [string]::IsNullOrWhiteSpace($effect.ReceiptIdentity) -or
            [string]::IsNullOrWhiteSpace($effect.ReceiptStatus) -or
            [string]::IsNullOrWhiteSpace($effect.ReceiptFingerprint)) {
            $errors.Add("snapshot_authority_effect_identity_incomplete:$($effect.LineIndex)")
            continue
        }
        if ($effect.Family -in $script:FileTransferCoordinatorSnapshotFamily1 -and
            $script:FileTransferCoordinatorSnapshotFamily1EffectKinds -notcontains
                [string]$effect.EffectKind) {
            $errors.Add("snapshot_authority_effect_kind_unknown:$($effect.LineIndex)")
            continue
        }
        if ($effect.Family -in $script:FileTransferCoordinatorSnapshotFamily2 -and
            $script:FileTransferCoordinatorSnapshotFamily2EffectKinds -notcontains
                [string]$effect.EffectKind) {
            $errors.Add("snapshot_authority_effect_kind_unknown:$($effect.LineIndex)")
            continue
        }
        $matches = @(
            $boundaries | Where-Object {
                $_.coordinatorInstanceId -ceq $effect.CoordinatorInstanceId -and
                $_.boundaryId -ceq $effect.TransitionId -and
                [long]$_.afterStateGeneration -eq [long]$effect.StateGeneration -and
                @($_.operations | Where-Object {
                        [string]$_.family -ceq [string]$effect.Family -and
                        [string]$_.operationKey -ceq [string]$effect.OperationKey -and
                        [string]$_.authorityId -ceq [string]$effect.AuthorityId -and
                        [long]$_.operationGeneration -eq [long]$effect.OperationGeneration -and
                        (Test-FileTransferCoordinatorSnapshotEffectScope `
                            -Operation $_ `
                            -Effect $effect) -and
                        (([string]$_.receiptIdentity -ceq [string]$effect.ReceiptIdentity -and
                          [long]$_.receiptGeneration -eq [long]$effect.ReceiptGeneration -and
                          [string]$_.receiptStatus -ceq [string]$effect.ReceiptStatus -and
                          [string]$_.receiptFingerprint -ceq [string]$effect.ReceiptFingerprint -and
                          [bool]$_.receiptValidated) -or
                         ($null -ne $_.routeTransition -and
                          @($_.routeTransition.receipts | Where-Object {
                                [string]$_.receiptIdentity -ceq [string]$effect.ReceiptIdentity -and
                                [long]$_.receiptGeneration -eq [long]$effect.ReceiptGeneration -and
                                [string]$_.receiptStatus -ceq [string]$effect.ReceiptStatus -and
                                [string]$_.receiptFingerprint -ceq [string]$effect.ReceiptFingerprint -and
                                [bool]$_.receiptValidated
                            }).Count -eq 1) -or
                         ($_.PSObject.Properties.Name -contains
                            'runtimeUnlockRetry' -and
                          $null -ne $_.runtimeUnlockRetry -and
                          @($_.runtimeUnlockRetry.receipts | Where-Object {
                                [string]$_.receiptIdentity -ceq [string]$effect.ReceiptIdentity -and
                                [long]$_.receiptGeneration -eq [long]$effect.ReceiptGeneration -and
                                [string]$_.receiptStatus -ceq [string]$effect.ReceiptStatus -and
                                [string]$_.receiptFingerprint -ceq [string]$effect.ReceiptFingerprint -and
                                [bool]$_.receiptValidated
                            }).Count -eq 1))
                    }).Count -eq 1
            })
        if ($matches.Count -ne 1) {
            $errors.Add("snapshot_authority_effect_unbound:$($effect.LineIndex)")
        }
    }

    $operationHistory = @(
        foreach ($boundary in @($boundaries)) {
            foreach ($operation in @($boundary.operations)) {
                if ([string]$operation.family -in
                    (@($script:FileTransferCoordinatorSnapshotFamily1) +
                     @($script:FileTransferCoordinatorSnapshotFamily2))) {
                    [pscustomobject]@{
                        Boundary = $boundary
                        Operation = $operation
                        Key = "$($boundary.coordinatorInstanceId)|$([string]$operation.operationKey)"
                    }
                }
            }
        }
    )
    foreach ($group in @($operationHistory | Group-Object Key)) {
        $history = @($group.Group | Sort-Object {
                [long]$_.Boundary.decisionUtcMilliseconds
            }, {
                [long]$_.Boundary.afterStateGeneration
            }, {
                [string]$_.Boundary.boundaryId
            })
        if ($history.Count -eq 0) {
            continue
        }
        $latest = $history[-1]
        $family = [string]$latest.Operation.family
        if ($family -ceq 'route_transition') {
            $isBootstrap = [string]$latest.Operation.operationRole -ceq
                'fallback_v6_bootstrap'
            $closed = if ($isBootstrap) {
                [string]$latest.Operation.afterState -ceq 'Delivered' -and
                @($latest.Operation.routeTransition.receipts | Where-Object {
                        [string]$_.stage -ceq 'fallback_v6_bootstrap' -and
                        [bool]$_.receiptValidated
                    }).Count -eq 1
            }
            else {
                Test-FileTransferCoordinatorRouteTransitionOperationClosed `
                    -Operation $latest.Operation `
                    -Effects @($effects)
            }
            $rejected = @($history | Where-Object {
                    @($_.Operation.routeTransition.receipts | Where-Object {
                            -not [bool]$_.receiptValidated -and
                            [string]$_.receiptStatus -ceq 'Rejected'
                        }).Count -gt 0 -or
                    [string]$_.Boundary.decisionStatus -ceq 'Rejected'
                })
            if (-not $closed -or $rejected.Count -gt 0) {
                $candidate = if ($rejected.Count -gt 0) {
                    $rejected[0]
                }
                else {
                    @($history | Where-Object {
                            [string]$_.Operation.afterState -in @(
                                'Prepared', 'Draining', 'Committed', 'Invalidated')
                        } | Select-Object -First 1)[0]
                }
                if ($null -ne $candidate) {
                    $class = 'filetransfer_route_transition_incomplete'
                    if ($isBootstrap) {
                        $class = 'filetransfer_route_transition_tuna_v4_to_post_tuna_fallback_v6_proof_bootstrap_missing'
                    }
                    elseif (@($effects | Where-Object {
                            [string]$_.OperationKey -ceq
                                [string]$candidate.Operation.operationKey -and
                            [string]$_.EffectKind -ceq
                                'terminalized_while_draining'
                        }).Count -gt 0) {
                        $class = 'filetransfer_route_transition_draining_transport_grace_terminalization'
                    }
                    $candidate.Boundary.taxonomyClass = $class
                    $candidate.Boundary.domain = 'route_runtime'
                    $candidate.Boundary.phase = 'route_transition'
                    $candidate.Boundary.rawCount = [Math]::Max(1, $rejected.Count)
                    $candidate.Boundary.distinctCount = 1
                    $candidate.Boundary.recoveredCount = if ($closed) { 1 } else { 0 }
                    $candidate.Boundary.unresolvedCount = if ($closed) { 0 } else { 1 }
                    $candidate.Boundary.verdict = if ($closed) { 'pass' } else { 'fail' }
                }
            }
        }
        elseif ($family -ceq 'route_runtime' -and
            [string]$latest.Operation.operationRole -ceq 'route_publication' -and
            [string]$latest.Operation.afterState -ceq 'Published' -and
            [bool]$latest.Operation.receiptValidated) {
            $publicationEffects = @($effects | Where-Object {
                    [string]$_.OperationKey -ceq [string]$latest.Operation.operationKey -and
                    [string]$_.EffectKind -ceq 'route_publication_forwarded'
                })
            if ($publicationEffects.Count -eq 0) {
                $latest.Boundary.taxonomyClass = 'publication_authority_missing'
                $latest.Boundary.domain = 'route_runtime'
                $latest.Boundary.phase = 'route_runtime'
                $latest.Boundary.rawCount = 1
                $latest.Boundary.distinctCount = 1
                $latest.Boundary.recoveredCount = 0
                $latest.Boundary.unresolvedCount = 1
                $latest.Boundary.verdict = 'fail'
            }
            else {
                $runtimeBoundary = $latest.Operation.routeRuntime.boundary
                $liveRouteEpoch = [long](
                    Get-FileTransferCoordinatorSnapshotObjectProperty `
                        -InputObject $runtimeBoundary `
                        -Name 'liveRouteEpoch' `
                        -Default 0L)
                $runtimeGeneration = [long](
                    Get-FileTransferCoordinatorSnapshotObjectProperty `
                        -InputObject $runtimeBoundary `
                        -Name 'runtimeGeneration' `
                        -Default 0L)
                $legGeneration = [long](
                    Get-FileTransferCoordinatorSnapshotObjectProperty `
                        -InputObject $runtimeBoundary `
                        -Name 'legGeneration' `
                        -Default 0L)
                if ($liveRouteEpoch -le 0 -or
                    $runtimeGeneration -le 0 -or
                    $legGeneration -le 0) {
                $latest.Boundary.taxonomyClass =
                    'filetransfer_live_route_epoch_projection_missing'
                $latest.Boundary.domain = 'route_runtime'
                $latest.Boundary.phase = 'route_runtime'
                $latest.Boundary.rawCount = 1
                $latest.Boundary.distinctCount = 1
                $latest.Boundary.recoveredCount = 0
                $latest.Boundary.unresolvedCount = 1
                $latest.Boundary.verdict = 'fail'
                }
            }
        }
    }

    $retryEntries = @($operationHistory | Where-Object {
            [string]$_.Operation.family -ceq
                'runtime_unlock_retry_lifecycle' -and
            [string]$_.Operation.operationRole -ne 'attempted_decision'
        })
    $retryAttempts = @($retryEntries | Where-Object {
            [string]$_.Operation.operationKind -ceq 'RetryAttempt'
        })
    foreach ($authorityGroup in @($retryAttempts | Group-Object {
                "$($_.Boundary.coordinatorInstanceId)|$([string]$_.Operation.authorityId)"
            })) {
        $history = @($authorityGroup.Group | Sort-Object {
                [long]$_.Boundary.decisionUtcMilliseconds
            }, {
                [long]$_.Boundary.afterStateGeneration
            }, {
                [string]$_.Boundary.boundaryId
            })
        if ($history.Count -eq 0) {
            continue
        }

        $authorityId = [string]$history[0].Operation.authorityId
        $instanceId = [string]$history[0].Boundary.coordinatorInstanceId
        $sourceBoundary = $history[0].Operation.runtimeUnlockRetry.sourceBoundary
        $exactEffects = @($effects | Where-Object {
                [string]$_.CoordinatorInstanceId -ceq $instanceId -and
                [string]$_.AuthorityId -ceq $authorityId
            })
        $missingRequiredReceiptEffect = $null
        foreach ($entry in $history) {
            foreach ($receipt in @($entry.Operation.runtimeUnlockRetry.receipts)) {
                if (-not [bool]$receipt.receiptValidated) {
                    continue
                }
                $requiredEffectKind = switch -Exact ([string]$receipt.stage) {
                    'stage' { 'dispatch_stage_observed'; break }
                    'install' { 'install_committed'; break }
                    'execution' {
                        if ([bool]$entry.Operation.runtimeUnlockRetry.dispatchObserved) {
                            'execution_observed'
                        }
                        break
                    }
                    'settlement' { 'settlement_observed'; break }
                    default { '' }
                }
                if ([string]::IsNullOrWhiteSpace($requiredEffectKind)) {
                    continue
                }
                $matchingEffects = @($exactEffects | Where-Object {
                        [string]$_.EffectKind -ceq $requiredEffectKind -and
                        [string]$_.ReceiptIdentity -ceq
                            [string]$receipt.receiptIdentity -and
                        [long]$_.ReceiptGeneration -eq
                            [long]$receipt.receiptGeneration -and
                        [string]$_.ReceiptStatus -ceq
                            [string]$receipt.receiptStatus -and
                        [string]$_.ReceiptFingerprint -ceq
                            [string]$receipt.receiptFingerprint
                    })
                if ($matchingEffects.Count -ne 1) {
                    $missingRequiredReceiptEffect = [pscustomobject]@{
                        Entry = $entry
                        Stage = [string]$receipt.stage
                    }
                    break
                }
            }
            if ($null -ne $missingRequiredReceiptEffect) {
                break
            }
        }
        if ($null -ne $missingRequiredReceiptEffect) {
            $candidate = $missingRequiredReceiptEffect.Entry
            $candidate.Boundary.taxonomyClass =
                if ($missingRequiredReceiptEffect.Stage -in @('stage', 'install')) {
                    'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete'
                }
                else {
                    'runtime_unlock_retry_dispatch_contract_unsettled'
                }
            $candidate.Boundary.domain = 'route_runtime'
            $candidate.Boundary.phase = 'runtime_unlock_retry'
            $candidate.Boundary.rawCount = 1
            $candidate.Boundary.distinctCount = 1
            $candidate.Boundary.recoveredCount = 0
            $candidate.Boundary.unresolvedCount = 1
            $candidate.Boundary.verdict = 'fail'
        }
        $missingDispatch = @($history | Where-Object {
                @($_.Operation.runtimeUnlockRetry.receipts | Where-Object {
                        [string]$_.stage -ceq 'settlement' -and
                        [string]$_.receiptStatus -ceq 'MissingDispatch' -and
                        [bool]$_.receiptValidated
                    }).Count -eq 1
            } | Select-Object -First 1) | Select-Object -First 1
        $closureRejections = @(
            $history | Where-Object {
                [string]$_.Boundary.decisionKind -ceq
                    'PrepareRuntimeUnlockRetryDispatchSuccessor' -and
                [string]$_.Boundary.decisionStatus -ceq 'Rejected' -and
                [string]$_.Boundary.decisionReason -ceq
                    'retry_dispatch_missing_successor_limit_exhausted' -and
                (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                    -Left $_.Operation.runtimeUnlockRetry.sourceBoundary `
                    -Right $sourceBoundary)
            } | Group-Object {
                [string]$_.Boundary.boundaryId
            } | ForEach-Object { $_.Group | Select-Object -First 1 }
        )
        foreach ($closureRejection in $closureRejections) {
            $matchingClosures = @($retryEntries | Where-Object {
                    [string]$_.Boundary.coordinatorInstanceId -ceq $instanceId -and
                    [string]$_.Operation.operationRole -ceq 'bounded_closure' -and
                    [string]$_.Operation.authorityId -ceq $authorityId -and
                    [string]$_.Operation.afterState -ceq 'Closed' -and
                    [bool]$_.Operation.receiptValidated -and
                    [long]$_.Boundary.decisionUtcMilliseconds -ge
                        [long]$closureRejection.Boundary.decisionUtcMilliseconds -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                        -Left $_.Operation.runtimeUnlockRetry.sourceBoundary `
                        -Right $sourceBoundary) -and
                    @($_.Operation.runtimeUnlockRetry.receipts | Where-Object {
                            [string]$_.stage -ceq 'bounded_closure' -and
                            [string]$_.receiptStatus -ceq 'Closed' -and
                            [bool]$_.receiptValidated
                        }).Count -eq 1
                })
            $closureRejection.Boundary.taxonomyClass =
                'runtime_unlock_retry_dispatch_chain_closure_incomplete'
            $closureRejection.Boundary.domain = 'route_runtime'
            $closureRejection.Boundary.phase = 'runtime_unlock_retry'
            $closureRejection.Boundary.rawCount = 1
            $closureRejection.Boundary.distinctCount = 1
            $closureRejection.Boundary.recoveredCount =
                if ($matchingClosures.Count -eq 1) { 1 } else { 0 }
            $closureRejection.Boundary.unresolvedCount =
                if ($matchingClosures.Count -eq 1) { 0 } else { 1 }
            $closureRejection.Boundary.verdict =
                if ($matchingClosures.Count -eq 1) { 'pass' } else { 'fail' }
        }
        if ($null -ne $missingDispatch) {
            $successors = @($retryAttempts | Where-Object {
                    [string]$_.Boundary.coordinatorInstanceId -ceq $instanceId -and
                    [string]$_.Operation.predecessorAuthorityId -ceq $authorityId -and
                    [string]$_.Operation.authorityId -cne $authorityId -and
                    (Test-FileTransferCoordinatorSnapshotBoundaryEqual `
                        -Left $_.Operation.runtimeUnlockRetry.sourceBoundary `
                        -Right $sourceBoundary) -and
                    [string]$_.Operation.runtimeUnlockRetry.releaseKind -ceq
                        [string]$history[0].Operation.runtimeUnlockRetry.releaseKind -and
                    [string]$_.Operation.runtimeUnlockRetry.releaseAuthorityId -ceq
                        [string]$history[0].Operation.runtimeUnlockRetry.releaseAuthorityId -and
                    [long]$_.Operation.runtimeUnlockRetry.releaseGeneration -eq
                        [long]$history[0].Operation.runtimeUnlockRetry.releaseGeneration -and
                    [long]$_.Boundary.decisionUtcMilliseconds -ge
                        [long]$missingDispatch.Boundary.decisionUtcMilliseconds
                })
            $successorRecovered = $false
            foreach ($successorGroup in @($successors | Group-Object {
                        [string]$_.Operation.authorityId
                    })) {
                $successorHistory = @($successorGroup.Group)
                $install = @($successorHistory | Where-Object {
                        @($_.Operation.runtimeUnlockRetry.receipts | Where-Object {
                                [string]$_.stage -ceq 'install' -and
                                [bool]$_.receiptValidated
                            }).Count -eq 1
                    })
                $dispatch = @($successorHistory | Where-Object {
                        [bool]$_.Operation.runtimeUnlockRetry.dispatchObserved -and
                        @($_.Operation.runtimeUnlockRetry.receipts | Where-Object {
                                [string]$_.stage -ceq 'execution' -and
                                [string]$_.receiptReason -ceq 'dispatch_observed' -and
                                [bool]$_.receiptValidated
                            }).Count -eq 1
                    })
                $dispatchEffects = @($effects | Where-Object {
                        [string]$_.CoordinatorInstanceId -ceq $instanceId -and
                        [string]$_.AuthorityId -ceq
                            [string]$successorHistory[0].Operation.authorityId -and
                        [string]$_.EffectKind -ceq 'execution_observed'
                    })
                if ($install.Count -gt 0 -and $dispatch.Count -gt 0 -and
                    $dispatchEffects.Count -eq 1) {
                    $successorRecovered = $true
                    break
                }
            }
            $closed = @($retryEntries | Where-Object {
                    [string]$_.Boundary.coordinatorInstanceId -ceq $instanceId -and
                    [string]$_.Operation.operationRole -ceq 'bounded_closure' -and
                    [string]$_.Operation.authorityId -ceq $authorityId -and
                    [string]$_.Operation.afterState -ceq 'Closed' -and
                    [bool]$_.Operation.receiptValidated
                }).Count -gt 0
            if (-not $closed -and $closureRejections.Count -eq 0) {
                $missingDispatch.Boundary.taxonomyClass =
                    'runtime_unlock_retry_dispatch_successor_missing_after_missing_dispatch'
                $missingDispatch.Boundary.domain = 'route_runtime'
                $missingDispatch.Boundary.phase = 'runtime_unlock_retry'
                $missingDispatch.Boundary.rawCount = 1
                $missingDispatch.Boundary.distinctCount = 1
                $missingDispatch.Boundary.recoveredCount =
                    if ($successorRecovered) { 1 } else { 0 }
                $missingDispatch.Boundary.unresolvedCount =
                    if ($successorRecovered) { 0 } else { 1 }
                $missingDispatch.Boundary.verdict =
                    if ($successorRecovered) { 'pass' } else { 'fail' }
            }
        }

        $latest = $history[-1]
        $latestState = [string]$latest.Operation.afterState
        if ($latestState -in @(
                'Authorized', 'Staged', 'ReceiptIssued', 'Installed',
                'AliasBound', 'SendInFlight')) {
            $hasInstall = @($history | Where-Object {
                    @($_.Operation.runtimeUnlockRetry.receipts | Where-Object {
                            [string]$_.stage -ceq 'install' -and
                            [bool]$_.receiptValidated
                        }).Count -eq 1
                }).Count -gt 0
            $latest.Boundary.taxonomyClass = if ($hasInstall) {
                'runtime_unlock_retry_dispatch_contract_unsettled'
            }
            else {
                'runtime_unlock_retry_dispatch_install_grant_evidence_incomplete'
            }
            $latest.Boundary.domain = 'route_runtime'
            $latest.Boundary.phase = 'runtime_unlock_retry'
            $latest.Boundary.rawCount = 1
            $latest.Boundary.distinctCount = 1
            $latest.Boundary.recoveredCount = 0
            $latest.Boundary.unresolvedCount = 1
            $latest.Boundary.verdict = 'fail'
        }
    }

    $committedRouteRuntimeOperations = @(
        $operationHistory | Where-Object {
            [string]$_.Operation.family -ceq 'route_runtime' -and
            [string]$_.Operation.operationRole -ceq 'route_commit' -and
            [string]$_.Operation.afterState -ceq 'Committed' -and
            [bool]$_.Operation.receiptValidated
        })
    foreach ($commitEntry in $committedRouteRuntimeOperations) {
        $commit = $commitEntry.Operation
        $commitBoundary = $commitEntry.Boundary
        $publicationMatches = @(
            $operationHistory | Where-Object {
                $_.Boundary.coordinatorInstanceId -ceq
                    $commitBoundary.coordinatorInstanceId -and
                [string]$_.Operation.family -ceq 'route_runtime' -and
                [string]$_.Operation.operationRole -ceq 'route_publication' -and
                [string]$_.Operation.predecessorAuthorityId -ceq
                    [string]$commit.authorityId -and
                [string]$_.Operation.afterState -ceq 'Published' -and
                [bool]$_.Operation.receiptValidated -and
                [long]$_.Operation.routeRuntime.boundary.routeCommitGeneration -eq
                    [long]$commit.operationGeneration -and
                [long]$_.Boundary.decisionUtcMilliseconds -ge
                    [long]$commitBoundary.decisionUtcMilliseconds
            })
        $commitEffects = @(
            $effects | Where-Object {
                $_.CoordinatorInstanceId -ceq
                    $commitBoundary.coordinatorInstanceId -and
                [string]$_.ReceiptIdentity -ceq
                    [string]$commit.receiptIdentity -and
                [long]$_.ReceiptGeneration -eq
                    [long]$commit.receiptGeneration -and
                [string]$_.ReceiptStatus -ceq
                    [string]$commit.receiptStatus -and
                [string]$_.ReceiptFingerprint -ceq
                    [string]$commit.receiptFingerprint
            })
        $hasCommitAdoption = @($commitEffects | Where-Object {
                [string]$_.EffectKind -ceq 'route_commit_adopted'
            }).Count -eq 1
        $hasRuntimeLegInstall = @($commitEffects | Where-Object {
                [string]$_.EffectKind -ceq 'target_runtime_leg_installed'
            }).Count -eq 1
        if ($publicationMatches.Count -ne 1 -or -not $hasCommitAdoption) {
            $commitBoundary.taxonomyClass = 'publication_authority_missing'
            $commitBoundary.domain = 'route_runtime'
            $commitBoundary.phase = 'route_runtime'
            $commitBoundary.rawCount = 1
            $commitBoundary.distinctCount = 1
            $commitBoundary.recoveredCount = 0
            $commitBoundary.unresolvedCount = 1
            $commitBoundary.verdict = 'fail'
        }
        elseif (-not $hasRuntimeLegInstall) {
            $commitBoundary.taxonomyClass =
                'filetransfer_live_route_epoch_projection_missing'
            $commitBoundary.domain = 'route_runtime'
            $commitBoundary.phase = 'route_runtime'
            $commitBoundary.rawCount = 1
            $commitBoundary.distinctCount = 1
            $commitBoundary.recoveredCount = 0
            $commitBoundary.unresolvedCount = 1
            $commitBoundary.verdict = 'fail'
        }
    }

    if ($selectionModes.Count -ne 1) {
        $errors.Add('snapshot_selection_mode_conflict')
    }
    $mode = if ($selectionModes.Contains('SnapshotV1Authoritative')) {
        'SnapshotV1Authoritative'
    }
    else {
        'SnapshotV1Shadow'
    }
    foreach ($entry in $streams.GetEnumerator()) {
        if (-not $entry.Value.Completed) {
            $errors.Add("snapshot_stream_incomplete:$($entry.Key)")
        }
        if ($mode -eq 'SnapshotV1Authoritative') {
            $missingFamilies = @($script:FileTransferCoordinatorSnapshotRequiredFamilies | Where-Object { $entry.Value.AuthoritativeFamilies -notcontains $_ })
            if ($missingFamilies.Count -gt 0) {
                $errors.Add("snapshot_declared_coverage_incomplete:$($entry.Key):$($missingFamilies -join ',')")
            }
        }
    }

    $firstUnresolvedBoundary = $null
    if ($errors.Count -gt 0) {
        $normalizedFailureReason = [regex]::Replace(
            [string]$errors[0],
            ':\d+$',
            '')
        $failureIdentity = "coordinator_snapshot_evidence|$mode|$normalizedFailureReason"
        $failureHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes (
            [System.Text.UTF8Encoding]::new($false).GetBytes($failureIdentity))
        $firstUnresolvedBoundary = [pscustomobject][ordered]@{
            boundaryId = $failureHash
            boundaryHash = $failureHash
            coordinatorInstanceId = '(artifact)'
            evidenceLineIndex = -1
            writer = '(unknown)'
            writerSequence = 0
            decisionUtcMilliseconds = 0L
            beforeStateGeneration = 0L
            afterStateGeneration = 0L
            beforeStateHash = '(none)'
            afterStateHash = '(none)'
            decisionKind = 'ArtifactEvidenceValidation'
            decisionStatus = 'Rejected'
            decisionReason = $normalizedFailureReason
            families = @()
            operations = @()
            verdict = 'fail'
            taxonomyClass = 'filetransfer_coordinator_snapshot_evidence_invalid'
            domain = 'evidence_harness'
            phase = 'evidence_finalization'
            rawCount = $errors.Count
            distinctCount = 1
            recoveredCount = 0
            unresolvedCount = 1
        }
        $boundaries.Add($firstUnresolvedBoundary) | Out-Null
    }
    if ($null -eq $firstUnresolvedBoundary) {
        $firstUnresolvedBoundary = @(
            $boundaries | Where-Object { $_.verdict -ceq 'fail' } |
                Sort-Object decisionUtcMilliseconds, writer, writerSequence, boundaryId |
                Select-Object -First 1
        ) | Select-Object -First 1
    }

    $normalizedBoundaries = @(
        $boundaries |
            Sort-Object coordinatorInstanceId, beforeStateGeneration, afterStateGeneration, boundaryId |
            ForEach-Object {
                [ordered]@{
                    boundaryId = $_.boundaryId
                    boundaryHash = $_.boundaryHash
                    coordinatorInstanceId = $_.coordinatorInstanceId
                    writer = $_.writer
                    writerSequence = $_.writerSequence
                    decisionUtcMilliseconds = $_.decisionUtcMilliseconds
                    beforeStateGeneration = $_.beforeStateGeneration
                    afterStateGeneration = $_.afterStateGeneration
                    beforeStateHash = $_.beforeStateHash
                    afterStateHash = $_.afterStateHash
                    decisionKind = $_.decisionKind
                    decisionStatus = $_.decisionStatus
                    decisionReason = $_.decisionReason
                    families = @($_.families)
                    operations = @($_.operations)
                    verdict = $_.verdict
                    taxonomyClass = $_.taxonomyClass
                    domain = $_.domain
                    phase = $_.phase
                    rawCount = $_.rawCount
                    distinctCount = $_.distinctCount
                    recoveredCount = $_.recoveredCount
                    unresolvedCount = $_.unresolvedCount
                }
            })
    $normalizedBoundaryJson = ConvertTo-Json -InputObject $normalizedBoundaries -Depth 64 -Compress
    $boundaryHash = Get-FileTransferCoordinatorSnapshotSha256 -Bytes ([System.Text.UTF8Encoding]::new($false).GetBytes($normalizedBoundaryJson))
    $artifactVerdict = if ($errors.Count -eq 0) { 'pass' } else { 'fail' }
    [pscustomobject]@{
        Mode = $mode
        ContractVersion = $script:FileTransferCoordinatorSnapshotContractVersion
        CatalogVersion = $catalog.Version
        CatalogHash = $catalog.Hash
        StreamComplete = @($streams.Values | Where-Object { -not $_.Completed }).Count -eq 0
        ArtifactEvidenceVerdict = $artifactVerdict
        ArtifactEvidenceFailureReason = ($errors -join ';')
        TransitionCount = $seenTransitions.Count
        EffectCount = $effects.Count
        BoundaryCount = $boundaries.Count
        BoundaryHash = $boundaryHash
        Boundaries = @($boundaries)
        FirstUnresolvedBoundary = $firstUnresolvedBoundary
        AuthoritativeFamilies = @($streams.Values | ForEach-Object { $_.AuthoritativeFamilies } | Sort-Object -Unique)
        ShadowFamilies = @($streams.Values | ForEach-Object { $_.ShadowFamilies } | Sort-Object -Unique)
        RouteFamilyLegacySelectionCount = @(
            $script:FileTransferCoordinatorSnapshotFamily1 | Where-Object {
                $authoritativeFamilies = @($streams.Values |
                    ForEach-Object { $_.AuthoritativeFamilies } |
                    Sort-Object -Unique)
                $authoritativeFamilies -notcontains $_
            }).Count
        RuntimeUnlockRetryFamilyLegacySelectionCount = if (
            @($streams.Values | ForEach-Object {
                    $_.AuthoritativeFamilies
                } | Sort-Object -Unique) -contains
                'runtime_unlock_retry_lifecycle') {
            0
        }
        else {
            1
        }
        LegacySelectionCount = if ($mode -eq 'SnapshotV1Authoritative') {
            0
        }
        else {
            @($script:FileTransferCoordinatorSnapshotRequiredFamilies |
                Where-Object {
                    $authoritativeFamilies = @($streams.Values |
                        ForEach-Object { $_.AuthoritativeFamilies } |
                        Sort-Object -Unique)
                    $authoritativeFamilies -notcontains $_
                }).Count
        }
    }
}

function Test-FileTransferCoordinatorSnapshotFamilyAuthoritative {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Evaluation,
        [Parameter(Mandatory = $true)][string]$Family
    )

    return $Evaluation.Mode -ne 'LegacyV0' -and
        $Evaluation.ArtifactEvidenceVerdict -ceq 'pass' -and
        @($Evaluation.AuthoritativeFamilies) -contains $Family
}

function Get-FileTransferCoordinatorSnapshotCorrelationResult {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Evaluation,
        [Parameter(Mandatory = $true)][string]$Family,
        [string[]]$TaxonomyClasses = @()
    )

    [object[]]$boundaries = @($Evaluation.Boundaries | Where-Object {
            @($_.families) -contains $Family -and
            ($TaxonomyClasses.Count -eq 0 -or
             $TaxonomyClasses -contains [string]$_.taxonomyClass) -and
            ([int]$_.rawCount -gt 0 -or [string]$_.verdict -ceq 'fail')
        })
    [object[]]$records = @(
        foreach ($boundary in $boundaries) {
            $sequence = 0L
            [void][long]::TryParse([string]$boundary.writerSequence, [ref]$sequence)
            if ($sequence -le 0) {
                $sequence = [long]$boundary.evidenceLineIndex + 1L
            }
            $event = [pscustomobject]@{
                EventName = 'filetransfer_coordinator_transition_snapshot'
                Sequence = $sequence
                LineNumber = [int]$boundary.evidenceLineIndex + 1
                FilePath = '(coordinator_snapshot)'
                RawLine = "event=filetransfer_coordinator_transition_snapshot; decision_reason=$($boundary.decisionReason); log_writer_id=$($boundary.writer); log_writer_sequence=$sequence"
            }
            [pscustomobject]@{
                Event = $event
                Class = [string]$boundary.taxonomyClass
                Reason = [string]$boundary.decisionReason
                Boundary = $boundary
                Recovered = [int]$boundary.recoveredCount -gt 0 -and
                    [int]$boundary.unresolvedCount -eq 0
            }
        })
    [object[]]$unresolved = @($records | Where-Object {
            [int]$_.Boundary.unresolvedCount -gt 0 })
    [object[]]$recovered = @($records | Where-Object {
            [int]$_.Boundary.recoveredCount -gt 0 })
    $rawCount = 0
    $distinctCount = 0
    $recoveredCount = 0
    $unresolvedCount = 0
    foreach ($boundary in $boundaries) {
        $rawCount += [int]$boundary.rawCount
        $distinctCount += [int]$boundary.distinctCount
        $recoveredCount += [int]$boundary.recoveredCount
        $unresolvedCount += [int]$boundary.unresolvedCount
    }
    [pscustomobject]@{
        RawCount = $rawCount
        DistinctCount = $distinctCount
        RecoveredCount = $recoveredCount
        UnresolvedCount = $unresolvedCount
        Verdict = if ($unresolved.Count -gt 0) { 'fail' } else { 'pass' }
        CandidateRecords = $records
        RecoveredCandidateRecords = $recovered
        UnresolvedCandidateRecords = $unresolved
        CandidateEvents = @($records | ForEach-Object { $_.Event })
        RecoveredCandidateEvents = @($recovered | ForEach-Object { $_.Event })
        UnresolvedCandidateEvents = @($unresolved | ForEach-Object { $_.Event })
        AuthoritativeRecords = @($records | ForEach-Object { $_.Event })
        Source = 'SnapshotAuthorityV1'
    }
}

function Compare-FileTransferCoordinatorSnapshotEvaluations {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$Full,
        [Parameter(Mandatory = $true)]$Compact
    )

    $reasons = [System.Collections.Generic.List[string]]::new()
    foreach ($field in @(
            'Mode',
            'ContractVersion',
            'CatalogVersion',
            'CatalogHash',
            'StreamComplete',
            'ArtifactEvidenceVerdict',
            'TransitionCount',
            'EffectCount',
            'BoundaryCount',
            'BoundaryHash',
            'LegacySelectionCount',
            'RouteFamilyLegacySelectionCount',
            'RuntimeUnlockRetryFamilyLegacySelectionCount')) {
        if ([string]$Full.$field -cne [string]$Compact.$field) {
            $reasons.Add("snapshot_full_compact_$($field.ToLowerInvariant())_mismatch")
        }
    }

    [pscustomobject]@{
        Verdict = if ($reasons.Count -eq 0) { 'pass' } else { 'fail' }
        FailureReason = $reasons -join ';'
        FullMode = $Full.Mode
        CompactMode = $Compact.Mode
        FullBoundaryHash = $Full.BoundaryHash
        CompactBoundaryHash = $Compact.BoundaryHash
        FullBoundaryCount = $Full.BoundaryCount
        CompactBoundaryCount = $Compact.BoundaryCount
        FullTransitionCount = $Full.TransitionCount
        CompactTransitionCount = $Compact.TransitionCount
        FullEffectCount = $Full.EffectCount
        CompactEffectCount = $Compact.EffectCount
    }
}
