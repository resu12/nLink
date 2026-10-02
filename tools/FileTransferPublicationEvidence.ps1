Set-StrictMode -Version Latest

# Read-only indexes over the caller's already validated snapshot. Raw diagnostic
# fields are lookup keys, never authority or a reason to reevaluate the artifact.
function New-FileTransferPublicationEvidenceIndex {
    param($SnapshotEvidence)
    $index = @{ Boundaries = @{}; States = @{} }
    if ((Get-FileTransferCoordinatorSnapshotObjectProperty $SnapshotEvidence 'Mode') -cne 'SnapshotV1Authoritative' -or
        (Get-FileTransferCoordinatorSnapshotObjectProperty $SnapshotEvidence 'ArtifactEvidenceVerdict') -cne 'pass' -or
        (Get-FileTransferCoordinatorSnapshotObjectProperty $SnapshotEvidence 'StreamComplete') -ne $true -or
        'route_runtime' -cnotin @(Get-FileTransferCoordinatorSnapshotObjectProperty $SnapshotEvidence 'AuthoritativeFamilies' @())) { return $index }
    foreach ($boundary in @(Get-FileTransferCoordinatorSnapshotObjectProperty $SnapshotEvidence 'Boundaries' @())) {
        if ($boundary.verdict -cne 'pass') { continue }
        $key = "$($boundary.coordinatorInstanceId)|$($boundary.boundaryId)"
        if (-not $index.Boundaries.ContainsKey($key)) { $index.Boundaries[$key] = @() }
        $index.Boundaries[$key] += $boundary
        $stateKey = "$($boundary.coordinatorInstanceId)|$($boundary.afterStateGeneration)"
        if (-not $index.States.ContainsKey($stateKey)) { $index.States[$stateKey] = @() }
        $index.States[$stateKey] += $boundary
    }
    return $index
}

function Resolve-FileTransferPublicationEvidence {
    param([hashtable]$Index, $Event)
    function F([string]$Name) { return [string]$Event.Fields[$Name] }
    if ((F 'publication_evidence_version') -cne '1') { return $null }
    $coordinator = F 'publication_evidence_coordinator_id'
    $boundaryId = F 'publication_evidence_boundary_id'
    $session = F 'publication_evidence_session_id'; $transfer = F 'publication_evidence_transfer_id'
    $fingerprint = F 'publication_evidence_token_fingerprint'
    foreach ($value in @($coordinator, $boundaryId, $session, $transfer, $fingerprint)) {
        if ($value -cnotmatch '^fte1-[0-9a-f]{24}$') { return $null }
    }
    foreach ($scope in @(@('session_id', $session), @('transfer_id', $transfer))) {
        $raw = F $scope[0]
        if ([string]::IsNullOrWhiteSpace($raw) -or
            ($raw -cne '[redacted]' -and -not ($scope[0] -ceq 'session_id' -and $raw -ceq 'sess_[redacted]') -and
                (ConvertTo-FileTransferCoordinatorEvidenceToken $raw) -cne $scope[1])) { return $null }
    }
    if ($Event.Fields.ContainsKey('continuity_coordinator_id') -and
        (F 'continuity_coordinator_id') -cne $coordinator) { return $null }
    $generation = 0L; $observed = 0L
    if (-not [long]::TryParse((F 'publication_evidence_state_generation'), [ref]$generation) -or $generation -le 0 -or
        -not [long]::TryParse((F 'publication_evidence_observed_state_generation'), [ref]$observed) -or $observed -lt $generation) { return $null }
    $key = "$coordinator|$boundaryId"; $stateKey = "$coordinator|$observed"
    if (-not $Index.Boundaries.ContainsKey($key) -or $Index.Boundaries[$key].Count -ne 1 -or
        -not $Index.States.ContainsKey($stateKey)) { return $null }
    $boundary = $Index.Boundaries[$key][0]
    if ([long]$boundary.afterStateGeneration -ne $generation) { return $null }
    $operations = @($boundary.operations | Where-Object {
        $_.operationKey -ceq (F 'publication_evidence_operation_key') -and $_.family -ceq 'route_runtime' -and
        $_.operationRole -ceq 'route_publication' -and $_.afterState -ceq 'Published' -and $_.receiptValidated -eq $true -and
        $_.receiptStatus -ceq 'Accepted' -and $_.receiptFingerprint -ceq $fingerprint -and
        $_.receiptIdentity -ceq $_.authorityId -and $_.operationKind -cin @('RoutePublication','RouteCommitPublication') -and
        $null -ne $_.routeRuntime -and $_.routeRuntime.role -ceq 'route_publication' -and $_.routeRuntime.tokenFingerprint -ceq $fingerprint
    })
    if ($operations.Count -ne 1) { return $null }
    $protocol = @{ regular_nkn_v4_fast='4'; file_tuna_v4='4'; post_tuna_fallback_v6='6' }
    if (-not $protocol.ContainsKey((F 'route')) -or (F 'protocol_version') -cne $protocol[(F 'route')]) { return $null }
    $operation = $operations[0]
    if ([string]$operation.operationGeneration -cne (F 'route_publication_generation') -or
        [string]$operation.receiptGeneration -cne (F 'route_publication_generation')) { return $null }
    $mapping = @{ route='route'; protocol_version='protocolVersion'; live_route_epoch='liveRouteEpoch';
        runtime_generation='runtimeGeneration'; terminal_generation='terminalGeneration'; leg_generation='legGeneration';
        route_publication_generation='routePublicationGeneration'; route_commit_generation='routeCommitGeneration' }
    function SamePublication($Scope) {
        foreach ($name in $mapping.Keys) {
            if ([string]::IsNullOrWhiteSpace((F $name)) -or (F $name) -cne [string]$Scope.($mapping[$name])) { return $false }
        }
        return $true
    }
    if (-not (SamePublication $operation.routeRuntime.boundary)) { return $null }
    # The producer records its exact observed state generation, not a nearest
    # timestamp/sequence guess. A historical receipt cannot bind a changed owner.
    foreach ($current in @($boundary) + @($Index.States[$stateKey])) {
        if ($current.writer -cne (F 'log_writer_id') -or $current.coordinatorInstanceId -cne $coordinator -or
            $current.sessionId -cne $session -or $current.transferId -cne $transfer -or
            $current.direction -cne (F 'direction') -or -not (SamePublication $current.afterBoundary)) { return $null }
    }
    foreach ($current in $Index.States[$stateKey]) {
        if ($Event.Fields.ContainsKey('data_session_generation') -and
            (F 'data_session_generation') -cne [string]$current.afterBoundary.dataSessionGeneration) { return $null }
        if ($Event.Fields.ContainsKey('data_session_identity')) {
            $raw = F 'data_session_identity'
            if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
            $projected = ConvertTo-FileTransferCoordinatorEvidenceToken $raw
            if ($projected -cne [string]$current.afterBoundary.dataSessionIdentity) { return $null }
        }
    }
    return [pscustomobject]@{ CoordinatorId=$coordinator; SessionId=$session; TransferId=$transfer; TokenFingerprint=$fingerprint }
}
