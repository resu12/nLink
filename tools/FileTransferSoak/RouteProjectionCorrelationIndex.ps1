Set-StrictMode -Version Latest

function Get-FileTransferRouteTransitionLiveEpochProjectionEventNames {
    return @(
        'filetransfer_route_transition_committed',
        'filetransfer_route_hint_authorized_publication_accepted',
        'filetransfer_route_transition_disposition_applied',
        'filetransfer_data_path_send_result',
        'filetransfer_data_path_receive_result',
        'filetransfer_tuna_data_path_peer_visible',
        'filetransfer_route_transitioned',
        'filetransfer_route_selected',
        'filetransfer_live_route_epoch_started',
        'filetransfer_live_route_epoch_recovered',
        'filetransfer_route_transition_retained_frame_applied')
}

function Get-FileTransferRouteTransitionLiveEpochProjectionField {
    param(
        [Parameter(Mandatory = $true)]$Event,
        [Parameter(Mandatory = $true)][string]$Name
    )

    [string[]]$aliases = switch ($Name) {
        'authority_id' { @('authority_id', 'transition_authority_id') }
        'transition_generation' { @('transition_generation', 'operation_generation') }
        'runtime_generation' { @('runtime_generation', 'target_runtime_generation') }
        'terminal_generation' { @('terminal_generation', 'target_terminal_generation') }
        'leg_generation' { @('leg_generation', 'target_leg_generation') }
        'route_publication_generation' {
            @(
                'route_publication_generation',
                'target_route_publication_generation',
                'resulting_route_publication_generation')
        }
        'route_commit_generation' {
            @(
                'route_commit_generation',
                'target_route_commit_generation',
                'resulting_route_commit_generation')
        }
        'route' { @('route', 'target_route', 'projected_target_route') }
        'protocol_version' { @('protocol_version', 'target_protocol_version') }
        default { @($Name) }
    }
    foreach ($alias in $aliases) {
        $value = Get-FileTransferEventField -Event $Event -Name $alias -Default ''
        if ([string]::IsNullOrWhiteSpace($value) -or
            $value -in @('(none)', 'none', '(missing)', '__missing__')) {
            continue
        }
        if ($Name -eq 'route') {
            $routeValue = switch ($value.ToLowerInvariant()) {
                'filetunav4' { 'file_tuna_v4' }
                'posttunafallbackv6' { 'post_tuna_fallback_v6' }
                'regularnknv4fast' { 'regular_nkn_v4_fast' }
                default { $value }
            }
            return $routeValue
        }
        return $value
    }
    return ''
}

function Get-FileTransferRouteTransitionLiveEpochProjectionKey {
    param(
        [Parameter(Mandatory = $true)]$Event,
        [ValidateSet('scope', 'transition', 'disposition')]
        [string]$Kind
    )

    [string[]]$fieldNames = switch ($Kind) {
        'scope' {
            @(
                'session_id',
                'transfer_id',
                'direction',
                'route',
                'protocol_version',
                'runtime_generation',
                'terminal_generation',
                'leg_generation',
                'route_publication_generation',
                'route_commit_generation')
        }
        'transition' {
            @(
                'session_id',
                'transfer_id',
                'direction',
                'authority_id',
                'transition_generation')
        }
        'disposition' {
            @(
                'session_id',
                'transfer_id',
                'direction',
                'authority_id',
                'transition_generation',
                'route_publication_generation',
                'route_commit_generation')
        }
    }
    $fieldsProperty = $Event.PSObject.Properties['Fields']
    if ($null -eq $fieldsProperty -or
        $fieldsProperty.Value -isnot [System.Collections.IDictionary]) {
        return ''
    }
    $fields = $fieldsProperty.Value
    $parts = [string[]]::new($fieldNames.Count)
    for ($fieldIndex = 0; $fieldIndex -lt $fieldNames.Count; $fieldIndex++) {
        $fieldName = $fieldNames[$fieldIndex]
        [string[]]$aliases = switch ($fieldName) {
            'authority_id' { @('authority_id', 'transition_authority_id') }
            'transition_generation' { @('transition_generation', 'operation_generation') }
            'runtime_generation' { @('runtime_generation', 'target_runtime_generation') }
            'terminal_generation' { @('terminal_generation', 'target_terminal_generation') }
            'leg_generation' { @('leg_generation', 'target_leg_generation') }
            'route_publication_generation' {
                @(
                    'route_publication_generation',
                    'target_route_publication_generation',
                    'resulting_route_publication_generation')
            }
            'route_commit_generation' {
                @(
                    'route_commit_generation',
                    'target_route_commit_generation',
                    'resulting_route_commit_generation')
            }
            'route' { @('route', 'target_route', 'projected_target_route') }
            'protocol_version' { @('protocol_version', 'target_protocol_version') }
            default { @($fieldName) }
        }
        $value = ''
        foreach ($alias in $aliases) {
            if (-not $fields.Contains($alias)) {
                continue
            }
            $candidate = [string]$fields[$alias]
            if (-not [string]::IsNullOrWhiteSpace($candidate) -and
                $candidate -notin @('(none)', 'none', '(missing)', '__missing__')) {
                $value = $candidate
                break
            }
        }
        if ($fieldName -eq 'route') {
            $value = switch ($value.ToLowerInvariant()) {
                'filetunav4' { 'file_tuna_v4' }
                'posttunafallbackv6' { 'post_tuna_fallback_v6' }
                'regularnknv4fast' { 'regular_nkn_v4_fast' }
                default { $value }
            }
        }
        if ([string]::IsNullOrWhiteSpace($value)) {
            return ''
        }
        $parts[$fieldIndex] = $value.ToLowerInvariant()
    }
    return [string]::Join('|', $parts)
}

function Add-FileTransferRouteTransitionLiveEpochProjectionIndexEvent {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Map,
        [AllowEmptyString()][string]$Key,
        [Parameter(Mandatory = $true)]$Event
    )

    if ([string]::IsNullOrWhiteSpace($Key)) {
        return
    }
    if (-not $Map.ContainsKey($Key)) {
        $Map[$Key] = New-Object System.Collections.Generic.List[object]
    }
    $Map[$Key].Add($Event) | Out-Null
}

function New-FileTransferRouteTransitionLiveEpochProjectionIndex {
    param([object[]]$Events = @())

    $catalog = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($eventName in @(Get-FileTransferRouteTransitionLiveEpochProjectionEventNames)) {
        [void]$catalog.Add($eventName)
    }
    $payloadNames = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($eventName in @(
            'filetransfer_data_path_send_result',
            'filetransfer_data_path_receive_result',
            'filetransfer_tuna_data_path_peer_visible')) {
        [void]$payloadNames.Add($eventName)
    }
    $legacyNames = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($eventName in @(
            'filetransfer_route_transitioned',
            'filetransfer_route_selected',
            'filetransfer_live_route_epoch_started',
            'filetransfer_live_route_epoch_recovered')) {
        [void]$legacyNames.Add($eventName)
    }

    $commits = New-Object System.Collections.Generic.List[object]
    $publicationsByScope = @{}
    $dispositionsByKey = @{}
    $payloadsByScope = @{}
    $legacyByNameAndScope = @{}
    $framesByTransition = @{}
    $buildVisitCount = 0L
    foreach ($event in @($Events)) {
        if ($null -eq $event -or -not $catalog.Contains([string]$event.EventName)) {
            continue
        }
        $buildVisitCount++
        $eventName = [string]$event.EventName
        if ($payloadNames.Contains($eventName)) {
            $deliveryResult = Get-FileTransferEventField `
                -Event $event `
                -Name 'delivery_result' `
                -Default ''
            if ($deliveryResult -in @('', 'sent', 'received', 'accepted', 'applied')) {
                $key = Get-FileTransferRouteTransitionLiveEpochProjectionKey `
                    -Event $event `
                    -Kind scope
                Add-FileTransferRouteTransitionLiveEpochProjectionIndexEvent `
                    -Map $payloadsByScope `
                    -Key $key `
                    -Event $event
            }
            continue
        }
        if ($legacyNames.Contains($eventName)) {
            $scopeKey = Get-FileTransferRouteTransitionLiveEpochProjectionKey `
                -Event $event `
                -Kind scope
            Add-FileTransferRouteTransitionLiveEpochProjectionIndexEvent `
                -Map $legacyByNameAndScope `
                -Key ("{0}|{1}" -f $eventName.ToLowerInvariant(), $scopeKey) `
                -Event $event
            continue
        }
        switch ($eventName) {
            'filetransfer_route_transition_committed' {
                if ((Get-FileTransferEventField -Event $event -Name 'decision_status' -Default '') -in
                    @('Committed', 'Idempotent')) {
                    $commits.Add($event) | Out-Null
                }
            }
            'filetransfer_route_hint_authorized_publication_accepted' {
                $key = Get-FileTransferRouteTransitionLiveEpochProjectionKey `
                    -Event $event `
                    -Kind scope
                Add-FileTransferRouteTransitionLiveEpochProjectionIndexEvent `
                    -Map $publicationsByScope `
                    -Key $key `
                    -Event $event
            }
            'filetransfer_route_transition_disposition_applied' {
                $disposition = Get-FileTransferEventField `
                    -Event $event `
                    -Name 'disposition' `
                    -Default ''
                $status = Get-FileTransferEventField `
                    -Event $event `
                    -Name 'decision_status' `
                    -Default ''
                if ($disposition -eq 'committed' -or
                    $status -in @('Applied', 'Committed', 'Idempotent')) {
                    $key = Get-FileTransferRouteTransitionLiveEpochProjectionKey `
                        -Event $event `
                        -Kind disposition
                    Add-FileTransferRouteTransitionLiveEpochProjectionIndexEvent `
                        -Map $dispositionsByKey `
                        -Key $key `
                        -Event $event
                }
            }
            'filetransfer_route_transition_retained_frame_applied' {
                $key = Get-FileTransferRouteTransitionLiveEpochProjectionKey `
                    -Event $event `
                    -Kind transition
                Add-FileTransferRouteTransitionLiveEpochProjectionIndexEvent `
                    -Map $framesByTransition `
                    -Key $key `
                    -Event $event
            }
        }
    }

    foreach ($map in @(
            $publicationsByScope,
            $dispositionsByKey,
            $payloadsByScope,
            $legacyByNameAndScope,
            $framesByTransition)) {
        foreach ($key in @($map.Keys)) {
            $map[$key] = @($map[$key] | Sort-Object Sequence, LineNumber)
        }
    }
    return [pscustomobject]@{
        Commits = @($commits | Sort-Object Sequence, LineNumber)
        PublicationsByScope = $publicationsByScope
        DispositionsByKey = $dispositionsByKey
        PayloadsByScope = $payloadsByScope
        LegacyByNameAndScope = $legacyByNameAndScope
        FramesByTransition = $framesByTransition
        IndexBuildVisitCount = $buildVisitCount
        BoundedLookupVisitCount = 0L
    }
}

function Find-FileTransferRouteTransitionLiveEpochProjectionEventAfter {
    param(
        [Parameter(Mandatory = $true)]$Index,
        [Parameter(Mandatory = $true)][hashtable]$Map,
        [AllowEmptyString()][string]$Key,
        [Parameter(Mandatory = $true)][long]$AfterSequence,
        [long]$BeforeSequence = [long]::MaxValue
    )

    [void]($Index.BoundedLookupVisitCount++)
    if ([string]::IsNullOrWhiteSpace($Key) -or -not $Map.ContainsKey($Key)) {
        return $null
    }
    [object[]]$items = $Map[$Key]
    if ($items.Count -eq 0) {
        return $null
    }
    $low = 0
    $high = $items.Count
    while ($low -lt $high) {
        $middle = $low + [int](($high - $low) / 2)
        [void]($Index.BoundedLookupVisitCount++)
        if ([int64]$items[$middle].Sequence -le $AfterSequence) {
            $low = $middle + 1
        }
        else {
            $high = $middle
        }
    }
    if ($low -ge $items.Count) {
        return $null
    }
    [void]($Index.BoundedLookupVisitCount++)
    if ([int64]$items[$low].Sequence -ge $BeforeSequence) {
        return $null
    }
    return $items[$low]
}

function Get-FileTransferRouteTransitionLiveEpochProjectionIndexedCorrelation {
    param(
        [Parameter(Mandatory = $true)]$CorrelationIndex
    )

    $lookupStart = [int64]$CorrelationIndex.BoundedLookupVisitCount
    $candidateRecords = New-Object System.Collections.Generic.List[object]
    $authoritativeRecords = New-Object System.Collections.Generic.List[object]
    $distinctKeys = New-Object 'System.Collections.Generic.HashSet[string]'
    foreach ($commit in @($CorrelationIndex.Commits)) {
        $authorityId = Get-FileTransferRouteTransitionLiveEpochProjectionField `
            -Event $commit `
            -Name 'authority_id'
        $transitionGeneration =
            Get-FileTransferRouteTransitionLiveEpochProjectionField `
                -Event $commit `
                -Name 'transition_generation'
        $direction = Get-FileTransferRouteTransitionLiveEpochProjectionField `
            -Event $commit `
            -Name 'direction'
        $sessionId = Get-FileTransferRouteTransitionLiveEpochProjectionField `
            -Event $commit `
            -Name 'session_id'
        $transferId = Get-FileTransferRouteTransitionLiveEpochProjectionField `
            -Event $commit `
            -Name 'transfer_id'
        if (@(
                $authorityId,
                $transitionGeneration,
                $direction,
                $sessionId,
                $transferId,
                (Get-FileTransferRouteTransitionLiveEpochProjectionField -Event $commit -Name 'route'),
                (Get-FileTransferRouteTransitionLiveEpochProjectionField -Event $commit -Name 'protocol_version'),
                (Get-FileTransferRouteTransitionLiveEpochProjectionField -Event $commit -Name 'runtime_generation'),
                (Get-FileTransferRouteTransitionLiveEpochProjectionField -Event $commit -Name 'leg_generation'),
                (Get-FileTransferRouteTransitionLiveEpochProjectionField -Event $commit -Name 'route_publication_generation'),
                (Get-FileTransferRouteTransitionLiveEpochProjectionField -Event $commit -Name 'route_commit_generation')) |
                Where-Object { [string]::IsNullOrWhiteSpace($_) } |
                Select-Object -First 1) {
            continue
        }

        $key = @(
            $sessionId,
            $transferId,
            $direction,
            $authorityId,
            $transitionGeneration) -join '|'
        if (-not $distinctKeys.Add($key.ToLowerInvariant())) {
            continue
        }

        $scopeKey = Get-FileTransferRouteTransitionLiveEpochProjectionKey `
            -Event $commit `
            -Kind scope
        $dispositionKey = Get-FileTransferRouteTransitionLiveEpochProjectionKey `
            -Event $commit `
            -Kind disposition
        $publication = Find-FileTransferRouteTransitionLiveEpochProjectionEventAfter `
            -Index $CorrelationIndex `
            -Map $CorrelationIndex.PublicationsByScope `
            -Key $scopeKey `
            -AfterSequence ([int64]$commit.Sequence)
        $disposition = Find-FileTransferRouteTransitionLiveEpochProjectionEventAfter `
            -Index $CorrelationIndex `
            -Map $CorrelationIndex.DispositionsByKey `
            -Key $dispositionKey `
            -AfterSequence ([int64]$commit.Sequence)
        if ($null -eq $publication -or $null -eq $disposition) {
            continue
        }

        $targetRoute = Get-FileTransferRouteTransitionLiveEpochProjectionField `
            -Event $commit `
            -Name 'route'
        if ($targetRoute -eq 'post_tuna_fallback_v6') {
            $transitionKey = Get-FileTransferRouteTransitionLiveEpochProjectionKey `
                -Event $commit `
                -Kind transition
            $frame = Find-FileTransferRouteTransitionLiveEpochProjectionEventAfter `
                -Index $CorrelationIndex `
                -Map $CorrelationIndex.FramesByTransition `
                -Key $transitionKey `
                -AfterSequence ([int64]$commit.Sequence) `
                -BeforeSequence ([int64]$disposition.Sequence)
            if ($null -eq $frame) {
                continue
            }
        }

        $payload = Find-FileTransferRouteTransitionLiveEpochProjectionEventAfter `
            -Index $CorrelationIndex `
            -Map $CorrelationIndex.PayloadsByScope `
            -Key $scopeKey `
            -AfterSequence ([int64]$disposition.Sequence)
        if ($null -eq $payload) {
            continue
        }

        $authoritativeRecords.Add([pscustomobject]@{
                Commit = $commit
                Publication = $publication
                Disposition = $disposition
                Payload = $payload
                Key = $key
            }) | Out-Null

        $missingProjection = $false
        foreach ($projectionName in @(
                'filetransfer_route_transitioned',
                'filetransfer_route_selected',
                'filetransfer_live_route_epoch_started',
                'filetransfer_live_route_epoch_recovered')) {
            $projectionKey = "{0}|{1}" -f $projectionName.ToLowerInvariant(), $scopeKey
            $projection = Find-FileTransferRouteTransitionLiveEpochProjectionEventAfter `
                -Index $CorrelationIndex `
                -Map $CorrelationIndex.LegacyByNameAndScope `
                -Key $projectionKey `
                -AfterSequence ([int64]$commit.Sequence)
            if ($null -eq $projection) {
                $missingProjection = $true
                break
            }
        }
        if ($missingProjection) {
            $candidateRecords.Add([pscustomobject]@{
                    Event = $commit
                    Reason =
                        'committed_route_transition_not_projected_to_live_route_epoch_evidence'
                    DistinctKey = $key
                }) | Out-Null
        }
    }

    return [pscustomobject]@{
        RawCount = $candidateRecords.Count
        DistinctCount = $candidateRecords.Count
        RecoveredCount = 0
        UnresolvedCount = $candidateRecords.Count
        Verdict = if ($candidateRecords.Count -gt 0) { 'fail' } else { 'pass' }
        AuthoritativeRecords = @($authoritativeRecords.ToArray())
        CandidateRecords = @($candidateRecords.ToArray())
        UnresolvedCandidateRecords = @($candidateRecords.ToArray())
        IndexBuildVisitCount = [int64]$CorrelationIndex.IndexBuildVisitCount
        BoundedLookupVisitCount =
            [int64]$CorrelationIndex.BoundedLookupVisitCount - $lookupStart
    }
}

function Get-FileTransferRouteTransitionLiveEpochProjectionCorrelation {
    param(
        [object[]]$Events = @(),
        $CorrelationIndex = $null
    )

    if ($null -eq $CorrelationIndex) {
        $CorrelationIndex =
            New-FileTransferRouteTransitionLiveEpochProjectionIndex -Events $Events
    }
    return Get-FileTransferRouteTransitionLiveEpochProjectionIndexedCorrelation `
        -CorrelationIndex $CorrelationIndex
}
