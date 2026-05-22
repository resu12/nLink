Set-StrictMode -Version Latest

function Select-FileTransferIdForAnalysis {
    param(
        [object[]]$Events,
        [string]$RequestedTransferId = ''
    )

    if (-not [string]::IsNullOrWhiteSpace($RequestedTransferId)) {
        return $RequestedTransferId
    }

    $progressTimeout = @(
        $Events |
            Where-Object {
                $_.EventName -eq 'filetransfer_live_progress_timeout' -and
                -not [string]::IsNullOrWhiteSpace($_.TransferId) -and
                $_.TransferId -ne '(all)'
            } |
            Sort-Object Sequence
    )
    if ($progressTimeout.Count -gt 0) {
        return [string]$progressTimeout[-1].TransferId
    }

    $terminal = @(
        $Events |
            Where-Object {
                (Test-FileTransferTerminalEvent -Event $_) -and
                -not [string]::IsNullOrWhiteSpace($_.TransferId)
            } |
            Sort-Object Sequence
    )
    if ($terminal.Count -gt 0) {
        return [string]$terminal[-1].TransferId
    }

    $v4Runtime = @(
        $Events |
            Where-Object {
                $_.EventName -like 'filetransfer_v4_*' -and
                -not [string]::IsNullOrWhiteSpace($_.TransferId)
            } |
            Sort-Object Sequence
    )
    if ($v4Runtime.Count -gt 0) {
        return [string]$v4Runtime[-1].TransferId
    }

    $binary = @(
        $Events |
            Where-Object {
                ($_.EventName -eq 'filetransfer_binary_frame_sent' -or $_.EventName -eq 'filetransfer_binary_frame_received') -and
                -not [string]::IsNullOrWhiteSpace($_.TransferId)
            } |
            Sort-Object Sequence
    )
    if ($binary.Count -gt 0) {
        return [string]$binary[-1].TransferId
    }

    $anyProgressTimeout = @(
        $Events |
            Where-Object {
                $_.EventName -eq 'filetransfer_live_progress_timeout' -and
                -not [string]::IsNullOrWhiteSpace($_.TransferId)
            } |
            Sort-Object Sequence
    )
    if ($anyProgressTimeout.Count -gt 0) {
        return [string]$anyProgressTimeout[-1].TransferId
    }

    return ''
}

function Get-FileTransferEventsByName {
    param(
        [object[]]$Events,
        [string[]]$Names
    )

    return @($Events | Where-Object { $Names -contains $_.EventName })
}

function Get-FileTransferTerminalDirection {
    param([Parameter(Mandatory = $true)]$Event)

    if ($Event.EventName -eq 'file_transfer_inbound_terminal') {
        return 'inbound'
    }

    if ($Event.EventName -eq 'file_transfer_outbound_terminal') {
        return 'outbound'
    }

    if ($Event.EventName -eq 'transfer_terminal') {
        $direction = Get-FileTransferEventField -Event $Event -Name 'direction' -Default ''
        if ([string]::Equals($direction, 'inbound', [System.StringComparison]::OrdinalIgnoreCase)) {
            return 'inbound'
        }

        if ([string]::Equals($direction, 'outbound', [System.StringComparison]::OrdinalIgnoreCase)) {
            return 'outbound'
        }
    }

    return ''
}

function Test-FileTransferTerminalEvent {
    param([Parameter(Mandatory = $true)]$Event)

    return -not [string]::IsNullOrWhiteSpace((Get-FileTransferTerminalDirection -Event $Event))
}

function Normalize-FileTransferTerminalEvent {
    param([Parameter(Mandatory = $true)]$Event)

    $fields = @{}
    if ($null -ne $Event.Fields) {
        foreach ($key in @($Event.Fields.Keys)) {
            $fields[$key] = $Event.Fields[$key]
        }
    }

    $state = Get-FileTransferEventField -Event $Event -Name 'state' -Default ''
    if ([string]::IsNullOrWhiteSpace($state)) {
        $errorCode = Get-FileTransferEventField -Event $Event -Name 'error_code' -Default '(none)'
        $reason = Get-FileTransferEventField -Event $Event -Name 'reason' -Default ''
        if ($errorCode -eq '(none)' -and
            ([string]::IsNullOrWhiteSpace($reason) -or $reason.IndexOf('complete', [System.StringComparison]::OrdinalIgnoreCase) -ge 0)) {
            $fields['state'] = 'Completed'
        }
        else {
            $fields['state'] = 'Failed'
        }
    }

    return [pscustomobject]@{
        TimestampUtc = $Event.TimestampUtc
        TimestampText = $Event.TimestampText
        Level = $Event.Level
        Source = $Event.Source
        EventName = $Event.EventName
        Fields = $fields
        TransferId = $Event.TransferId
        FilePath = $Event.FilePath
        FileName = $Event.FileName
        LineNumber = $Event.LineNumber
        Sequence = $Event.Sequence
        Message = $Event.Message
        RawLine = $Event.RawLine
    }
}

function Get-FileTransferTerminalEvents {
    param([object[]]$Events)

    return @(
        $Events |
            Where-Object { Test-FileTransferTerminalEvent -Event $_ } |
            ForEach-Object { Normalize-FileTransferTerminalEvent -Event $_ }
    )
}

function Get-FileTransferEventCount {
    param(
        [object[]]$Events,
        [string]$Name
    )

    return @($Events | Where-Object { $_.EventName -eq $Name }).Count
}

function Normalize-FileTransferRouteRuntimeProfile {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return ''
    }

    $normalized = $Value.Trim().ToLowerInvariant()
    switch ($normalized) {
        'regular_nkn_v4_fast' { return 'regular_nkn_v4_fast' }
        'file_tuna_v4_fast' { return 'file_tuna_v4_fast' }
        'default_v6' { return 'default_v6' }
        'default' { return 'default_v6' }
        'primary_regular_nkn_bulk_v6' { return 'primary_regular_nkn_bulk_v6' }
        'primaryregularnknbulkv6' { return 'primary_regular_nkn_bulk_v6' }
        default { return $normalized }
    }
}

function Get-FileTransferRouteExpectedProtocol {
    param([string]$Route)

    switch ($Route) {
        'regular_nkn_v4_fast' { return '4' }
        'file_tuna_v4' { return '4' }
        'file_tuna_v6' { return '6' }
        'post_tuna_fallback_v6' { return '6' }
        'diagnostic_regular_nkn_v6' { return '6' }
        default { return '' }
    }
}

function Get-FileTransferRouteExpectedRuntimeProfile {
    param([string]$Route)

    switch ($Route) {
        'regular_nkn_v4_fast' { return 'regular_nkn_v4_fast' }
        'file_tuna_v4' { return 'file_tuna_v4_fast' }
        'file_tuna_v6' { return 'default_v6' }
        'post_tuna_fallback_v6' { return 'default_v6' }
        'diagnostic_regular_nkn_v6' { return 'primary_regular_nkn_bulk_v6' }
        default { return '' }
    }
}

function Get-FileTransferRouteExpectedBridgePolicy {
    param([string]$Route)

    switch ($Route) {
        'regular_nkn_v4_fast' { return 'regular_nkn_v4_fast' }
        'file_tuna_v4' { return 'tuna_strict' }
        'file_tuna_v6' { return 'tuna_strict' }
        'post_tuna_fallback_v6' { return 'post_tuna_fallback_strict' }
        'diagnostic_regular_nkn_v6' { return 'primary_regular_nkn_quiet' }
        default { return '' }
    }
}

function Get-FileTransferRouteEventKey {
    param(
        [Parameter(Mandatory = $true)]$Event,
        [switch]$IncludeDirection
    )

    $transferId = if ([string]::IsNullOrWhiteSpace($Event.TransferId)) { '(none)' } else { [string]$Event.TransferId }
    if (-not $IncludeDirection) {
        return $transferId
    }

    $direction = Get-FileTransferRouteEventDirection -Event $Event
    if ([string]::IsNullOrWhiteSpace($direction)) {
        $direction = '(none)'
    }

    return ('{0}|{1}' -f $transferId, $direction.Trim().ToLowerInvariant())
}

function Get-FileTransferRouteEventDirection {
    param([Parameter(Mandatory = $true)]$Event)

    $direction = Get-FileTransferEventField -Event $Event -Name 'direction' -Default ''
    if ([string]::Equals($direction, 'outbound', [System.StringComparison]::OrdinalIgnoreCase)) {
        return 'outbound'
    }

    if ([string]::Equals($direction, 'inbound', [System.StringComparison]::OrdinalIgnoreCase)) {
        return 'inbound'
    }

    $terminalDirection = Get-FileTransferTerminalDirection -Event $Event
    if (-not [string]::IsNullOrWhiteSpace($terminalDirection)) {
        return $terminalDirection
    }

    $role = Get-FileTransferEventField -Event $Event -Name 'role' -Default ''
    if ([string]::Equals($role, 'sender', [System.StringComparison]::OrdinalIgnoreCase)) {
        return 'outbound'
    }

    if ([string]::Equals($role, 'receiver', [System.StringComparison]::OrdinalIgnoreCase)) {
        return 'inbound'
    }

    switch ($Event.EventName) {
        'filetransfer_v4_sender_started' { return 'outbound' }
        'filetransfer_v6_sender_started' { return 'outbound' }
        'filetransfer_v4_receiver_started' { return 'inbound' }
        'filetransfer_v6_receiver_started' { return 'inbound' }
        default { return '' }
    }
}

function Test-FileTransferRouteAwareEvent {
    param([Parameter(Mandatory = $true)]$Event)

    return $Event.EventName -eq 'filetransfer_route_selected' -or
        ($null -ne $Event.Fields -and $Event.Fields.ContainsKey('route'))
}

function Get-FileTransferSelectedRouteForEvent {
    param(
        [Parameter(Mandatory = $true)]$Event,
        [Parameter(Mandatory = $true)]$RouteSelectedEvents
    )

    $transferKey = Get-FileTransferRouteEventKey -Event $Event
    $direction = Get-FileTransferRouteEventDirection -Event $Event
    $candidates = @(
        $RouteSelectedEvents |
            Where-Object {
                $_.Sequence -lt $Event.Sequence -and
                (Get-FileTransferRouteEventKey -Event $_) -eq $transferKey
            } |
            Sort-Object Sequence
    )

    if (-not [string]::IsNullOrWhiteSpace($direction)) {
        $directionCandidates = @(
            $candidates |
                Where-Object {
                    [string]::Equals((Get-FileTransferRouteEventDirection -Event $_), $direction, [System.StringComparison]::OrdinalIgnoreCase)
                } |
                Sort-Object Sequence
        )
        if ($directionCandidates.Count -gt 0) {
            return $directionCandidates[-1]
        }
    }

    if ($candidates.Count -gt 0) {
        return $candidates[-1]
    }

    return $null
}

function Test-FileTransferRouteTerminalBetween {
    param(
        [Parameter(Mandatory = $true)][object[]]$TerminalEvents,
        [Parameter(Mandatory = $true)][string]$DirectionKey,
        [Parameter(Mandatory = $true)][long]$AfterSequence,
        [Parameter(Mandatory = $true)][long]$BeforeSequence
    )

    foreach ($terminal in @($TerminalEvents)) {
        if ($terminal.Sequence -le $AfterSequence -or $terminal.Sequence -ge $BeforeSequence) {
            continue
        }

        if ((Get-FileTransferRouteEventKey -Event $terminal -IncludeDirection) -eq $DirectionKey) {
            return $true
        }
    }

    return $false
}

function Add-FileTransferRouteConsistencyFinding {
    param(
        [Parameter(Mandatory = $true)]$Findings,
        [Parameter(Mandatory = $true)]$EvidenceEvents,
        [Parameter(Mandatory = $true)][string]$Finding,
        $Event
    )

    $Findings.Add($Finding) | Out-Null
    if ($null -ne $Event) {
        foreach ($existing in @($EvidenceEvents)) {
            if ($existing.Sequence -eq $Event.Sequence) {
                return
            }
        }

        $EvidenceEvents.Add($Event) | Out-Null
    }
}

function Get-FileTransferRouteConsistency {
    param([object[]]$TransferEvents)

    $routeSelectedEvents = @(Get-FileTransferEventsByName -Events $TransferEvents -Names @('filetransfer_route_selected') | Sort-Object Sequence)
    $routeAwareEvents = @($TransferEvents | Where-Object { Test-FileTransferRouteAwareEvent -Event $_ } | Sort-Object Sequence)
    $terminalEvents = @(Get-FileTransferTerminalEvents -Events $TransferEvents | Sort-Object Sequence)
    $findings = New-Object System.Collections.ArrayList
    $evidenceEvents = New-Object System.Collections.ArrayList
    $lastSelectedByDirectionKey = @{}

    foreach ($selected in @($routeSelectedEvents)) {
        $route = Get-FileTransferEventField -Event $selected -Name 'route' -Default ''
        $directionKey = Get-FileTransferRouteEventKey -Event $selected -IncludeDirection
        if ($lastSelectedByDirectionKey.ContainsKey($directionKey)) {
            $previous = $lastSelectedByDirectionKey[$directionKey]
            $previousRoute = Get-FileTransferEventField -Event $previous -Name 'route' -Default ''
            if ($route -ne $previousRoute -and
                -not (Test-FileTransferRouteTerminalBetween -TerminalEvents $terminalEvents -DirectionKey $directionKey -AfterSequence $previous.Sequence -BeforeSequence $selected.Sequence)) {
                Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route changed before prior terminal: transfer_direction={0}; previous_route={1}; new_route={2}; event={3}" -f $directionKey, $previousRoute, $route, (Format-FileTransferEvidenceLine -Event $selected)) -Event $selected
            }
        }

        $lastSelectedByDirectionKey[$directionKey] = $selected

        $expectedProtocol = Get-FileTransferRouteExpectedProtocol -Route $route
        if ([string]::IsNullOrWhiteSpace($expectedProtocol)) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("unknown route selected: {0}" -f (Format-FileTransferEvidenceLine -Event $selected)) -Event $selected
            continue
        }

        $protocolVersion = Get-FileTransferEventField -Event $selected -Name 'protocol_version' -Default ''
        if ($protocolVersion -ne $expectedProtocol) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route selected protocol mismatch: route={0}; expected_protocol={1}; event={2}" -f $route, $expectedProtocol, (Format-FileTransferEvidenceLine -Event $selected)) -Event $selected
        }

        $runtimeProfile = Normalize-FileTransferRouteRuntimeProfile -Value (Get-FileTransferEventField -Event $selected -Name 'runtime_profile' -Default '')
        $expectedRuntimeProfile = Get-FileTransferRouteExpectedRuntimeProfile -Route $route
        if (-not [string]::IsNullOrWhiteSpace($runtimeProfile) -and $runtimeProfile -ne $expectedRuntimeProfile) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route selected runtime mismatch: route={0}; expected_runtime={1}; actual_runtime={2}; event={3}" -f $route, $expectedRuntimeProfile, $runtimeProfile, (Format-FileTransferEvidenceLine -Event $selected)) -Event $selected
        }

        $bridgePolicy = Get-FileTransferEventField -Event $selected -Name 'bridge_recovery_policy' -Default ''
        $expectedBridgePolicy = Get-FileTransferRouteExpectedBridgePolicy -Route $route
        if (-not [string]::IsNullOrWhiteSpace($bridgePolicy) -and $bridgePolicy -ne $expectedBridgePolicy) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route selected bridge policy mismatch: route={0}; expected_bridge_policy={1}; actual_bridge_policy={2}; event={3}" -f $route, $expectedBridgePolicy, $bridgePolicy, (Format-FileTransferEvidenceLine -Event $selected)) -Event $selected
        }

        $diagnosticEnabled = Get-FileTransferEventField -Event $selected -Name 'diagnostic_regular_nkn_v6' -Default '0'
        if ($route -eq 'diagnostic_regular_nkn_v6' -and $diagnosticEnabled -ne '1') {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("diagnostic route selected without diagnostic marker: {0}" -f (Format-FileTransferEvidenceLine -Event $selected)) -Event $selected
        }
    }

    if ($routeAwareEvents.Count -gt 0 -and $routeSelectedEvents.Count -eq 0) {
        Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding 'route-aware transfer events were present but no filetransfer_route_selected event was found' -Event $routeAwareEvents[0]
    }

    foreach ($event in @($routeAwareEvents)) {
        if ($event.EventName -eq 'filetransfer_route_selected') {
            continue
        }

        $selected = Get-FileTransferSelectedRouteForEvent -Event $event -RouteSelectedEvents $routeSelectedEvents
        if ($null -eq $selected) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route-aware event has no selected route: {0}" -f (Format-FileTransferEvidenceLine -Event $event)) -Event $event
            continue
        }

        $selectedRoute = Get-FileTransferEventField -Event $selected -Name 'route' -Default ''
        $expectedProtocol = Get-FileTransferRouteExpectedProtocol -Route $selectedRoute
        $expectedRuntimeProfile = Get-FileTransferRouteExpectedRuntimeProfile -Route $selectedRoute
        $expectedBridgePolicy = Get-FileTransferRouteExpectedBridgePolicy -Route $selectedRoute

        $eventRoute = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
        if (-not [string]::IsNullOrWhiteSpace($eventRoute) -and $eventRoute -ne $selectedRoute) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route token mismatch: selected_route={0}; event_route={1}; event={2}" -f $selectedRoute, $eventRoute, (Format-FileTransferEvidenceLine -Event $event)) -Event $event
        }

        $eventProtocol = Get-FileTransferEventField -Event $event -Name 'protocol_version' -Default ''
        if (-not [string]::IsNullOrWhiteSpace($eventProtocol) -and -not [string]::IsNullOrWhiteSpace($expectedProtocol) -and $eventProtocol -ne $expectedProtocol) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route protocol mismatch: route={0}; expected_protocol={1}; actual_protocol={2}; event={3}" -f $selectedRoute, $expectedProtocol, $eventProtocol, (Format-FileTransferEvidenceLine -Event $event)) -Event $event
        }

        $eventRuntimeProfile = Normalize-FileTransferRouteRuntimeProfile -Value (Get-FileTransferEventField -Event $event -Name 'runtime_profile' -Default '')
        if (-not [string]::IsNullOrWhiteSpace($eventRuntimeProfile) -and -not [string]::IsNullOrWhiteSpace($expectedRuntimeProfile) -and $eventRuntimeProfile -ne $expectedRuntimeProfile) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route runtime mismatch: route={0}; expected_runtime={1}; actual_runtime={2}; event={3}" -f $selectedRoute, $expectedRuntimeProfile, $eventRuntimeProfile, (Format-FileTransferEvidenceLine -Event $event)) -Event $event
        }

        $eventBridgePolicy = Get-FileTransferEventField -Event $event -Name 'bridge_recovery_policy' -Default ''
        if (-not [string]::IsNullOrWhiteSpace($eventBridgePolicy) -and -not [string]::IsNullOrWhiteSpace($expectedBridgePolicy) -and $eventBridgePolicy -ne $expectedBridgePolicy) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("route bridge policy mismatch: route={0}; expected_bridge_policy={1}; actual_bridge_policy={2}; event={3}" -f $selectedRoute, $expectedBridgePolicy, $eventBridgePolicy, (Format-FileTransferEvidenceLine -Event $event)) -Event $event
        }

        if ($selectedRoute -eq 'regular_nkn_v4_fast' -and
            ($event.EventName -eq 'filetransfer_v6_sender_started' -or $event.EventName -eq 'filetransfer_v6_receiver_started')) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("regular NKN V4 route entered V6 runtime: {0}" -f (Format-FileTransferEvidenceLine -Event $event)) -Event $event
        }

        if (($selectedRoute -eq 'file_tuna_v6' -or $selectedRoute -eq 'post_tuna_fallback_v6' -or $selectedRoute -eq 'diagnostic_regular_nkn_v6') -and
            ($event.EventName -eq 'filetransfer_v4_sender_started' -or $event.EventName -eq 'filetransfer_v4_receiver_started')) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("V6 route entered regular V4 runtime: route={0}; event={1}" -f $selectedRoute, (Format-FileTransferEvidenceLine -Event $event)) -Event $event
        }

        if ($selectedRoute -eq 'file_tuna_v4' -and
            ($event.EventName -eq 'filetransfer_v6_sender_started' -or $event.EventName -eq 'filetransfer_v6_receiver_started')) {
            Add-FileTransferRouteConsistencyFinding -Findings $findings -EvidenceEvents $evidenceEvents -Finding ("file Tuna V4 route entered V6 runtime: {0}" -f (Format-FileTransferEvidenceLine -Event $event)) -Event $event
        }
    }

    $verdict = if ($findings.Count -gt 0) {
        'fail'
    }
    elseif ($routeAwareEvents.Count -eq 0) {
        'legacy'
    }
    else {
        'pass'
    }

    return [pscustomobject]@{
        Verdict = $verdict
        RouteSelectedEvents = @($routeSelectedEvents)
        RouteAwareEvents = @($routeAwareEvents)
        Findings = @($findings.ToArray())
        EvidenceEvents = @($evidenceEvents.ToArray())
    }
}

function Get-FileTransferMaxField {
    param(
        [object[]]$Events,
        [string]$FieldName
    )

    $max = 0L
    foreach ($event in @($Events)) {
        $value = Get-FileTransferEventInt64Field -Event $event -Name $FieldName -Default 0
        if ($value -gt $max) {
            $max = $value
        }
    }

    return $max
}

function Get-FileTransferSumField {
    param(
        [object[]]$Events,
        [string]$FieldName
    )

    $sum = 0L
    foreach ($event in @($Events)) {
        $sum += Get-FileTransferEventInt64Field -Event $event -Name $FieldName -Default 0
    }

    return $sum
}

function Get-FileTransferLiveProgressTimeoutEvidence {
    param(
        [string]$ArtifactDir = '',
        [string[]]$CandidateStdoutPaths = @()
    )

    $candidatePaths = New-Object System.Collections.Generic.List[string]
    if (-not [string]::IsNullOrWhiteSpace($ArtifactDir)) {
        $candidatePaths.Add((Join-Path $ArtifactDir 'gui-smoke-stdout.log')) | Out-Null
    }

    foreach ($path in @($CandidateStdoutPaths)) {
        if (-not [string]::IsNullOrWhiteSpace($path)) {
            $candidatePaths.Add($path) | Out-Null
        }
    }

    $count = 0
    $reason = ''
    $receiverNext = 0L
    $receiverHighest = 0L
    $progressEvents = 0L
    $totalWaitSeconds = 0L
    $progressTimeoutSeconds = 0L
    $lastStdoutPath = ''
    $pattern = 'Timed out waiting for live file-transfer progress:\s*(?<reason>.*?);\s*total_wait_s=(?<total>\d+);\s*receiver_next_chunk=(?<next>[-+]?\d+);\s*receiver_highest_chunk=(?<highest>[-+]?\d+);\s*progress_events=(?<events>\d+)'

    foreach ($candidate in @($candidatePaths | Select-Object -Unique)) {
        if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
            continue
        }

        foreach ($line in [System.IO.File]::ReadLines($candidate)) {
            $match = [regex]::Match(
                $line,
                $pattern,
                [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if (-not $match.Success) {
                continue
            }

            $count++
            $lastStdoutPath = $candidate
            $reason = $match.Groups['reason'].Value.Trim()
            [long]::TryParse($match.Groups['total'].Value, [ref]$totalWaitSeconds) | Out-Null
            [long]::TryParse($match.Groups['next'].Value, [ref]$receiverNext) | Out-Null
            [long]::TryParse($match.Groups['highest'].Value, [ref]$receiverHighest) | Out-Null
            [long]::TryParse($match.Groups['events'].Value, [ref]$progressEvents) | Out-Null

            $timeoutMatch = [regex]::Match($reason, 'for\s+(?<seconds>\d+)s', [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            if ($timeoutMatch.Success) {
                [long]::TryParse($timeoutMatch.Groups['seconds'].Value, [ref]$progressTimeoutSeconds) | Out-Null
            }
        }
    }

    return [pscustomobject]@{
        Count = $count
        Reason = $reason
        ReceiverNextChunk = $receiverNext
        ReceiverHighestChunk = $receiverHighest
        ProgressEventCount = $progressEvents
        TotalWaitSeconds = $totalWaitSeconds
        ProgressTimeoutSeconds = $progressTimeoutSeconds
        StdoutPath = $lastStdoutPath
    }
}

function Get-FileTransferPercentileField {
    param(
        [object[]]$Events,
        [string]$FieldName,
        [double]$Percentile
    )

    $values = @(
        foreach ($event in @($Events)) {
            Get-FileTransferEventInt64Field -Event $event -Name $FieldName -Default 0
        }
    )
    $values = @($values | Sort-Object)

    if ($values.Count -eq 0) {
        return 0L
    }

    $index = [int]([Math]::Ceiling(($Percentile / 100D) * $values.Count) - 1)
    if ($index -lt 0) {
        $index = 0
    }
    elseif ($index -ge $values.Count) {
        $index = $values.Count - 1
    }
    return [long]$values[$index]
}

function Get-FileTransferFrameTypeCounts {
    param([object[]]$Events)

    $counts = @{}
    foreach ($event in @($Events | Where-Object { $_.EventName -eq 'filetransfer_binary_frame_sent' -or $_.EventName -eq 'filetransfer_binary_frame_received' })) {
        $frameType = Get-FileTransferEventField -Event $event -Name 'frame_type' -Default '(unknown)'
        if (-not $counts.ContainsKey($frameType)) {
            $counts[$frameType] = 0
        }

        $counts[$frameType] = [int]$counts[$frameType] + 1
    }

    return $counts
}

function New-FileTransferRetainedSummary {
    param(
        [object[]]$Events,
        [string[]]$LogFiles,
        [string]$RequestedTransferId = '',
        [switch]$AllTransfers
    )

    $allEvents = @($Events | Sort-Object Sequence)
    $transferEvents = @()
    $selectedTransferId = ''
    if ($AllTransfers -and [string]::IsNullOrWhiteSpace($RequestedTransferId)) {
        $transferEvents = @($allEvents | Where-Object { -not [string]::IsNullOrWhiteSpace($_.TransferId) })
        if ($transferEvents.Count -gt 0) {
            $selectedTransferId = '(all)'
        }
    }
    else {
        $selectedTransferId = Select-FileTransferIdForAnalysis -Events $allEvents -RequestedTransferId $RequestedTransferId
    }

    if ($transferEvents.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($selectedTransferId)) {
        $transferEvents = @($allEvents | Where-Object { $_.TransferId -eq $selectedTransferId })
    }

    $terminalEvents = @(Get-FileTransferTerminalEvents -Events $transferEvents)
    $inboundTerminalEvents = @($terminalEvents | Where-Object { (Get-FileTransferTerminalDirection -Event $_) -eq 'inbound' })
    $outboundTerminalEvents = @($terminalEvents | Where-Object { (Get-FileTransferTerminalDirection -Event $_) -eq 'outbound' })
    $timestampedTransferEvents = @($transferEvents | Where-Object { $null -ne $_.TimestampUtc })
    $windowStartUtc = $null
    $windowEndUtc = $null
    if ($timestampedTransferEvents.Count -gt 0) {
        $windowStartUtc = ($timestampedTransferEvents | Sort-Object TimestampUtc | Select-Object -First 1).TimestampUtc.AddSeconds(-5)
        $windowEndUtc = ($timestampedTransferEvents | Sort-Object TimestampUtc | Select-Object -Last 1).TimestampUtc.AddSeconds(5)
    }

    $globalEvents = @(
        $allEvents |
            Where-Object {
                (
                    $_.EventName -like 'nkn_bridge_bulk_*' -or
                    $_.EventName -eq 'nkn_bridge_inbound_delivery_summary' -or
                    $_.EventName -eq 'nkn_bridge_inbound_delivery_failed' -or
                    $_.EventName -eq 'nkn_inbound_envelope_received' -or
                    $_.EventName -eq 'nkn_inbound_envelope_drop' -or
                    $_.EventName -like 'nkn_bridge_receive_stall_*' -or
                    $_.EventName -like 'nkn_bridge_control_receive_*' -or
                    $_.EventName -eq 'filetransfer_v4_receive_liveness_summary' -or
                    $_.EventName -eq 'screenshare_bridge_media_send_summary' -or
                    $_.EventName -eq 'screenshare_bridge_queue_state' -or
                    $_.EventName -eq 'screenshare_bridge_transport_health_summary'
                ) -and
                (
                    $null -eq $windowStartUtc -or
                    $null -eq $_.TimestampUtc -or
                    ($_.TimestampUtc -ge $windowStartUtc -and $_.TimestampUtc -le $windowEndUtc)
                )
            }
    )

    $evidenceEvents = @($transferEvents + $globalEvents | Sort-Object Sequence)
    $windowEvents = if ($transferEvents.Count -gt 0) { $transferEvents } else { $allEvents }
    $timestampedWindowEvents = @($windowEvents | Where-Object { $null -ne $_.TimestampUtc })
    $firstTimestamp = ''
    $lastTimestamp = ''
    if ($timestampedWindowEvents.Count -gt 0) {
        $firstTimestamp = ($timestampedWindowEvents | Sort-Object TimestampUtc | Select-Object -First 1).TimestampUtc.ToString('u')
        $lastTimestamp = ($timestampedWindowEvents | Sort-Object TimestampUtc | Select-Object -Last 1).TimestampUtc.ToString('u')
    }

    $frameTypeCounts = Get-FileTransferFrameTypeCounts -Events $transferEvents
    $senderThroughputEvents = @(Get-FileTransferEventsByName -Events $transferEvents -Names @('filetransfer_v4_sender_throughput_summary'))
    $senderPipelineEvents = @(Get-FileTransferEventsByName -Events $transferEvents -Names @('filetransfer_v4_sender_pipeline_summary'))
    $senderFeedEvents = @(Get-FileTransferEventsByName -Events $transferEvents -Names @('filetransfer_v4_sender_feed_summary'))
    $senderCacheEvents = @(Get-FileTransferEventsByName -Events $transferEvents -Names @('filetransfer_sender_repair_cache_policy', 'filetransfer_sender_repair_cache_summary', 'filetransfer_sender_repair_cache_pressure_entered', 'filetransfer_sender_repair_cache_pressure_exited', 'filetransfer_sender_cache_exhausted', 'filetransfer_sender_repair_unavailable'))
    $v4SenderPumpSummaryEvents = @(Get-FileTransferEventsByName -Events $transferEvents -Names @('filetransfer_v4_sender_pump_summary'))
    $v4EfficiencyEvents = @(Get-FileTransferEventsByName -Events $transferEvents -Names @('filetransfer_v4_efficiency_summary'))
    $chunkBatchTransportSummaryEvents = @(Get-FileTransferEventsByName -Events $transferEvents -Names @('filetransfer_chunk_batch_transport_summary'))
    $v4OutboundEfficiencyEvents = @($v4EfficiencyEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'outbound' })
    $v4InboundEfficiencyEvents = @($v4EfficiencyEvents | Where-Object { (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'inbound' })
    $batchSentAsBatchCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_chunk_batch_sent_as_batch'
    if ($batchSentAsBatchCount -eq 0) {
        $batchSentAsBatchCount = [Math]::Max(
            (Get-FileTransferMaxField -Events $v4SenderPumpSummaryEvents -FieldName 'batch_frames_sent_total'),
            [Math]::Max(
                (Get-FileTransferMaxField -Events $v4OutboundEfficiencyEvents -FieldName 'batch_frames_sent_total'),
                (Get-FileTransferMaxField -Events $chunkBatchTransportSummaryEvents -FieldName 'batch_frames_sent_total')))
    }
    $receiverBufferWriteBatchCommittedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_receiver_write_batch_committed'
    if ($receiverBufferWriteBatchCommittedCount -eq 0) {
        $receiverBufferWriteBatchCommittedCount = Get-FileTransferMaxField -Events $v4InboundEfficiencyEvents -FieldName 'sparse_write_batch_count_total'
    }
    $receiverFeedbackEvents = @(Get-FileTransferEventsByName -Events $transferEvents -Names @('filetransfer_v4_receiver_feedback_pump_started', 'filetransfer_v4_receiver_feedback_enqueued', 'filetransfer_v4_receiver_feedback_coalesced', 'filetransfer_v4_receiver_feedback_sent', 'filetransfer_v4_receiver_feedback_summary', 'filetransfer_v4_receiver_feedback_failed'))
    $receiverFeedbackPumpStartedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_v4_receiver_feedback_pump_started'
    $receiverFeedbackPumpModeEventCount = @(
        $receiverFeedbackEvents |
            Where-Object { (Get-FileTransferEventField -Event $_ -Name 'mode' -Default '') -eq 'pump' }
    ).Count
    $receiverFeedbackPumpActiveCount = if ($receiverFeedbackPumpStartedCount -gt 0 -or $receiverFeedbackPumpModeEventCount -gt 0) { 1 } else { 0 }
    $receiverFeedbackSliceStartedAfterPumpStart = if ($receiverFeedbackPumpStartedCount -eq 0 -and $receiverFeedbackPumpModeEventCount -gt 0) { 1 } else { 0 }
    $liveProgressTimeoutEvents = @(Get-FileTransferEventsByName -Events $transferEvents -Names @('filetransfer_live_progress_timeout'))
    $lastLiveProgressTimeout = @($liveProgressTimeoutEvents | Sort-Object Sequence | Select-Object -Last 1)
    $liveMatrixIncompleteEvents = @(Get-FileTransferEventsByName -Events $transferEvents -Names @('filetransfer_live_matrix_incomplete'))
    $artifactSliceSummaryEvents = @(Get-FileTransferEventsByName -Events $allEvents -Names @('filetransfer_artifact_slice_summary'))
    $lastArtifactSliceSummary = @($artifactSliceSummaryEvents | Sort-Object Sequence | Select-Object -Last 1)
    $routeConsistency = Get-FileTransferRouteConsistency -TransferEvents $transferEvents
    $cleanTerminalPair = $false
    if ($inboundTerminalEvents.Count -gt 0 -and $outboundTerminalEvents.Count -gt 0) {
        $cleanTerminalPair = $true
        foreach ($terminal in @($terminalEvents)) {
            $state = Get-FileTransferEventField -Event $terminal -Name 'state' -Default ''
            $errorCode = Get-FileTransferEventField -Event $terminal -Name 'error_code' -Default '(none)'
            if ($state -ne 'Completed' -or $errorCode -ne '(none)') {
                $cleanTerminalPair = $false
                break
            }
        }
    }
    $progressTimeoutMatrixIncomplete = $false
    foreach ($event in @($liveProgressTimeoutEvents + $liveMatrixIncompleteEvents)) {
        if ((Get-FileTransferEventInt64Field -Event $event -Name 'requested_matrix_incomplete' -Default 0) -ne 0 -or
            ($event.EventName -eq 'filetransfer_live_matrix_incomplete' -and
                (Get-FileTransferEventInt64Field -Event $event -Name 'gui_progress_timeout' -Default 0) -ne 0)) {
            $progressTimeoutMatrixIncomplete = $true
            break
        }
    }
    $terminalMissingAfterProgressTimeout = if ($liveProgressTimeoutEvents.Count -gt 0 -and (-not $cleanTerminalPair -or $progressTimeoutMatrixIncomplete)) { 1 } else { 0 }

    return [pscustomobject]@{
        TransferId = $selectedTransferId
        RequestedTransferId = $RequestedTransferId
        LogFiles = @($LogFiles)
        AllEvents = @($allEvents)
        TransferEvents = @($transferEvents)
        GlobalEvents = @($globalEvents)
        EvidenceEvents = @($evidenceEvents)
        TerminalEvents = @($terminalEvents)
        InboundTerminalEvents = @($inboundTerminalEvents)
        OutboundTerminalEvents = @($outboundTerminalEvents)
        RouteConsistency = $routeConsistency
        HasTransferEvidence = ($transferEvents.Count -gt 0)
        HasTerminalEvidence = ($terminalEvents.Count -gt 0)
        FirstTimestamp = $firstTimestamp
        LastTimestamp = $lastTimestamp
        FrameTypeCounts = $frameTypeCounts
        ReorderEventCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_reorder_pressure'
        MaxLateArrivalDistance = Get-FileTransferMaxField -Events $transferEvents -FieldName 'late_arrival_distance'
        RequestTimeoutCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_request_timeout_detected'
        RetryRequestedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_chunk_retry_requested'
        RetrySentCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_chunk_retry_sent'
        RepairSetRequestedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_repair_set_requested'
        RepairSetReceivedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_repair_set_received'
        RepairSetSentCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_repair_set_sent'
        RepairRequestSuppressedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_repair_request_suppressed'
        ProactiveFrontierRepairRequestedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_frontier_gap_repair_requested'
        ProactiveFrontierRepairEligibleCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_frontier_gap_repair_eligible'
        ProactiveFrontierRepairSkippedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_frontier_gap_repair_skipped'
        ProactiveFrontierRepairSuppressedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_frontier_gap_repair_suppressed'
        ProactiveFrontierRepairSenderReceivedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_frontier_gap_repair_sender_received'
        ProactiveFrontierRepairSenderScheduledCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_frontier_gap_repair_sender_scheduled'
        ProactiveFrontierRepairSenderSentCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_frontier_gap_repair_sender_sent'
        ProactiveFrontierRepairFilledCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_frontier_gap_repair_filled'
        MaxFrontierRepairRequestToFillMs = Get-FileTransferMaxField -Events $transferEvents -FieldName 'request_to_fill_ms'
        MaxProactiveFrontierRepairGapAgeMs = Get-FileTransferMaxField -Events (Get-FileTransferEventsByName -Events $transferEvents -Names @('filetransfer_frontier_gap_repair_eligible', 'filetransfer_frontier_gap_repair_requested', 'filetransfer_frontier_gap_repair_skipped', 'filetransfer_frontier_gap_repair_suppressed')) -FieldName 'gap_stall_age_ms'
        MaxRepairSetRanges = Get-FileTransferMaxField -Events $transferEvents -FieldName 'range_count'
        MaxRepairSetChunks = Get-FileTransferMaxField -Events $transferEvents -FieldName 'requested_chunk_count'
        MaxConservativeStartupDurationMs = Get-FileTransferMaxField -Events $transferEvents -FieldName 'conservative_startup_duration_ms'
        MaxBytesBeforeStartupExit = Get-FileTransferMaxField -Events $transferEvents -FieldName 'bytes_before_startup_exit'
        MaxStartupProbeWindowBytes = Get-FileTransferMaxField -Events $transferEvents -FieldName 'startup_probe_window_bytes'
        FirstRepairOrTimeoutBeforeStartupExitCount = @($transferEvents | Where-Object { (Get-FileTransferEventInt64Field -Event $_ -Name 'first_repair_or_timeout_before_startup_exit' -Default 0) -gt 0 }).Count
        BatchSentAsBatchCount = $batchSentAsBatchCount
        BatchSplitCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_chunk_batch_split_for_transport'
        PayloadBudgetCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_transport_payload_budget'
        PayloadRejectedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_transport_payload_rejected'
        DataFrameDecodeFailedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_data_frame_decode_failed'
        ChunkRejectedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_chunk_rejected'
        MessageRejectedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_message_rejected'
        DegradedEnteredCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_session_degraded_entered'
        DegradedExitedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_session_degraded_exited'
        BulkUnhealthyCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_bulk_unhealthy_detected'
        BulkFallbackEnteredCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_bulk_fallback_entered'
        BulkFallbackExitedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_bulk_fallback_exited'
        ReceiverBufferPressureEnteredCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_receiver_buffer_pressure_entered'
        ReceiverBufferPressureExitedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_receiver_buffer_pressure_exited'
        ReceiverBufferGrantClampedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_receiver_grant_clamped_for_buffer'
        ReceiverBufferWriteBatchCommittedCount = $receiverBufferWriteBatchCommittedCount
        ReceiverSparseModeSelectedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_receiver_sparse_mode_selected'
        ReceiverSparseWriteSummaryCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_receiver_sparse_write_summary'
        ReceiverSparseCommitSummaryCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_receiver_sparse_commit_summary'
        MaxReceiverSparseWriteBytesPerSecond = Get-FileTransferMaxField -Events $transferEvents -FieldName 'sparse_write_bytes_per_second'
        MaxReceiverSparseWrittenAheadBytes = Get-FileTransferMaxField -Events $transferEvents -FieldName 'sparse_written_ahead_bytes'
        MaxReceiverSparseGapCount = Get-FileTransferMaxField -Events $transferEvents -FieldName 'sparse_gap_count'
        SenderCacheExhaustedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_sender_cache_exhausted'
        SenderRepairUnavailableCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_sender_repair_unavailable'
        SenderRepairChunkSkippedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_sender_repair_chunk_skipped'
        MaxSenderRepairCacheBytes = Get-FileTransferMaxField -Events @($senderThroughputEvents + $senderCacheEvents) -FieldName 'cache_bytes'
        MaxSenderRepairCacheHardLimitBytes = Get-FileTransferMaxField -Events @($senderThroughputEvents + $senderCacheEvents) -FieldName 'cache_hard_limit_bytes'
        SenderRepairCacheHitCount = Get-FileTransferSumField -Events $senderThroughputEvents -FieldName 'cache_hit_count'
        SenderRepairCacheMissCount = Get-FileTransferSumField -Events $senderThroughputEvents -FieldName 'cache_miss_count'
        SenderRepairSourceRereadCount = Get-FileTransferSumField -Events $senderThroughputEvents -FieldName 'source_reread_count'
        SenderRepairCacheEvictionCount = Get-FileTransferSumField -Events $senderThroughputEvents -FieldName 'cache_eviction_count'
        SenderPipelineSummaryCount = $senderPipelineEvents.Count
        MaxSenderPipelineConfiguredDepth = Get-FileTransferMaxField -Events $senderPipelineEvents -FieldName 'configured_depth'
        MaxSenderPipelineEffectiveDepth = Get-FileTransferMaxField -Events $senderPipelineEvents -FieldName 'effective_depth'
        MaxSenderPipelineInFlightFrames = Get-FileTransferMaxField -Events $senderPipelineEvents -FieldName 'in_flight_frames_max'
        MaxSenderPipelineInFlightBytes = Get-FileTransferMaxField -Events $senderPipelineEvents -FieldName 'in_flight_bytes_max'
        SenderPipelineScheduledFrames = Get-FileTransferSumField -Events $senderPipelineEvents -FieldName 'scheduled_frames'
        SenderPipelineCompletedFrames = Get-FileTransferSumField -Events $senderPipelineEvents -FieldName 'completed_frames'
        SenderPipelineFailedFrames = Get-FileTransferSumField -Events $senderPipelineEvents -FieldName 'failed_frames'
        MaxSenderPipelineFifoWaitMs = Get-FileTransferMaxField -Events $senderPipelineEvents -FieldName 'fifo_wait_max_ms'
        MaxSenderPipelineAcceptedProgressLagBytes = Get-FileTransferMaxField -Events $senderPipelineEvents -FieldName 'accepted_progress_lag_bytes_max'
        SenderFeedSummaryCount = $senderFeedEvents.Count
        SenderFeedChunkFramesPrepared = Get-FileTransferSumField -Events $senderFeedEvents -FieldName 'chunk_frames_prepared'
        SenderFeedBatchFramesPrepared = Get-FileTransferSumField -Events $senderFeedEvents -FieldName 'batch_frames_prepared'
        SenderFeedChunkCountPrepared = Get-FileTransferSumField -Events $senderFeedEvents -FieldName 'chunk_count_prepared'
        SenderFeedRawBytesPrepared = Get-FileTransferSumField -Events $senderFeedEvents -FieldName 'raw_bytes_prepared'
        SenderFeedReadDurationMs = Get-FileTransferSumField -Events $senderFeedEvents -FieldName 'read_duration_ms'
        SenderFeedBatchPrepareDurationMs = Get-FileTransferSumField -Events $senderFeedEvents -FieldName 'batch_prepare_duration_ms'
        SenderFeedScheduleDurationMs = Get-FileTransferSumField -Events $senderFeedEvents -FieldName 'send_async_schedule_duration_ms'
        MaxSenderFeedInterScheduleGapP95Ms = Get-FileTransferMaxField -Events $senderFeedEvents -FieldName 'inter_schedule_gap_p95_ms'
        MaxSenderFeedInterScheduleGapMs = Get-FileTransferMaxField -Events $senderFeedEvents -FieldName 'inter_schedule_gap_max_ms'
        SenderFeedCreditWaitDurationMs = Get-FileTransferSumField -Events $senderFeedEvents -FieldName 'credit_wait_duration_ms'
        SenderFeedPipelineSlotWaitDurationMs = Get-FileTransferSumField -Events $senderFeedEvents -FieldName 'pipeline_slot_wait_duration_ms'
        SenderFeedSourceReadErrorCount = Get-FileTransferSumField -Events $senderFeedEvents -FieldName 'source_read_error_count'
        ReceiverFeedbackPumpStartedCount = $receiverFeedbackPumpStartedCount
        ReceiverFeedbackPumpActiveCount = $receiverFeedbackPumpActiveCount
        ReceiverFeedbackSliceStartedAfterPumpStart = $receiverFeedbackSliceStartedAfterPumpStart
        ReceiverFeedbackEnqueuedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_v4_receiver_feedback_enqueued'
        ReceiverFeedbackSentCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_v4_receiver_feedback_sent'
        ReceiverFeedbackCoalescedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_v4_receiver_feedback_coalesced'
        ReceiverFeedbackSummaryCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_v4_receiver_feedback_summary'
        ReceiverFeedbackFailedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_v4_receiver_feedback_failed'
        MaxReceiverFeedbackQueueDepth = Get-FileTransferMaxField -Events $receiverFeedbackEvents -FieldName 'queue_depth'
        MaxReceiverFeedbackSummaryQueueDepth = Get-FileTransferMaxField -Events $receiverFeedbackEvents -FieldName 'max_queue_depth'
        MaxReceiverFeedbackEnqueueToSendAgeMs = Get-FileTransferMaxField -Events $receiverFeedbackEvents -FieldName 'enqueue_to_send_age_ms'
        MaxReceiverFeedbackSummaryEnqueueToSendAgeMs = Get-FileTransferMaxField -Events $receiverFeedbackEvents -FieldName 'max_enqueue_to_send_age_ms'
        MaxReceiverFeedbackSendDurationMs = Get-FileTransferMaxField -Events $receiverFeedbackEvents -FieldName 'send_duration_ms'
        MaxReceiverFeedbackSummarySendDurationMs = Get-FileTransferMaxField -Events $receiverFeedbackEvents -FieldName 'max_send_duration_ms'
        LiveProgressTimeoutCount = $liveProgressTimeoutEvents.Count
        GuiProgressTimeoutReason = if ($lastLiveProgressTimeout.Count -gt 0) { Get-FileTransferEventField -Event $lastLiveProgressTimeout[0] -Name 'reason' -Default '' } else { '' }
        LastReceiverNextChunk = if ($lastLiveProgressTimeout.Count -gt 0) { Get-FileTransferEventInt64Field -Event $lastLiveProgressTimeout[0] -Name 'receiver_next_chunk' -Default 0 } else { 0 }
        LastReceiverHighestChunk = if ($lastLiveProgressTimeout.Count -gt 0) { Get-FileTransferEventInt64Field -Event $lastLiveProgressTimeout[0] -Name 'receiver_highest_chunk' -Default 0 } else { 0 }
        LastProgressEventCount = if ($lastLiveProgressTimeout.Count -gt 0) { Get-FileTransferEventInt64Field -Event $lastLiveProgressTimeout[0] -Name 'progress_events' -Default 0 } else { 0 }
        TerminalMissingAfterProgressTimeout = $terminalMissingAfterProgressTimeout
        ArtifactSliceStartReason = if ($lastArtifactSliceSummary.Count -gt 0) { Get-FileTransferEventField -Event $lastArtifactSliceSummary[0] -Name 'artifact_slice_start_reason' -Default '' } else { '' }
        ArtifactSliceEndReason = if ($lastArtifactSliceSummary.Count -gt 0) { Get-FileTransferEventField -Event $lastArtifactSliceSummary[0] -Name 'artifact_slice_end_reason' -Default '' } else { '' }
        MaxReceiverPendingBytes = Get-FileTransferMaxField -Events $transferEvents -FieldName 'pending_bytes'
        MaxReceiverWriteBatchBytes = Get-FileTransferMaxField -Events $transferEvents -FieldName 'batch_bytes'
        MaxReceiverWriteDurationMs = Get-FileTransferMaxField -Events $transferEvents -FieldName 'write_duration_ms'
    }
}
