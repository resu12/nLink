Set-StrictMode -Version Latest

if ($null -eq (Get-Variable -Name FileTransferFallbackFrontierStallGraceLegacyCorrelation -Scope Script -ErrorAction SilentlyContinue)) {
    $script:FileTransferFallbackFrontierStallGraceLegacyCorrelation =
        (Get-Command Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation -CommandType Function).ScriptBlock
}
if ($null -eq (Get-Variable -Name FileTransferFallbackFrontierRecentProgressGraceLegacyCorrelation -Scope Script -ErrorAction SilentlyContinue)) {
    $script:FileTransferFallbackFrontierRecentProgressGraceLegacyCorrelation =
        (Get-Command Get-FileTransferFallbackFrontierRecentProgressGraceRecheckCorrelation -CommandType Function).ScriptBlock
}
if ($null -eq (Get-Variable -Name FileTransferFallbackFrontierGraceCachedScope -Scope Script -ErrorAction SilentlyContinue)) {
    $script:FileTransferFallbackFrontierGraceCachedScope = ''
    $script:FileTransferFallbackFrontierGraceCachedIndex = $null
    $script:FileTransferFallbackFrontierGraceCachedResults = @{}
}
if ($null -eq (Get-Variable -Name FileTransferFallbackFrontierGracePartitionWidth -Scope Script -ErrorAction SilentlyContinue)) {
    $script:FileTransferFallbackFrontierGracePartitionWidth = 8
}

function Get-FileTransferFallbackFrontierGraceCorrelationScopeKey {
    param([AllowEmptyCollection()][object[]]$Events = @())

    if ($Events.Count -eq 0) { return 'empty' }
    $first = $Events[0]
    $last = $Events[$Events.Count - 1]
    return @(
        $Events.Count,
        [int64]$first.Sequence,
        [int64]$last.Sequence,
        [string]$first.EventName,
        [string]$last.EventName,
        [string]$first.FilePath,
        [string]$last.FilePath,
        (Get-FileTransferEventField -Event $first -Name 'log_writer_id' -Default ''),
        (Get-FileTransferEventField -Event $last -Name 'log_writer_id' -Default '')) -join '|'
}

function Reset-FileTransferFallbackFrontierGraceCorrelationCache {
    $script:FileTransferFallbackFrontierGraceCachedScope = ''
    $script:FileTransferFallbackFrontierGraceCachedIndex = $null
    $script:FileTransferFallbackFrontierGraceCachedResults = @{}
}

function Get-FileTransferFallbackFrontierGraceCorrelationEventNames {
    return @(
        'filetransfer_v6_frontier_request_deferred',
        'filetransfer_v6_frontier_stall_grace_recheck_decided',
        'filetransfer_v6_frontier_recent_progress_grace_deferred',
        'filetransfer_v6_frontier_recent_progress_grace_recheck_decided',
        'filetransfer_v6_receiver_state_deferred',
        'filetransfer_fallback_frontier_repair_authority_granted',
        'filetransfer_fallback_frontier_repair_contract_settled',
        'filetransfer_fallback_frontier_repair_progress_observed',
        'filetransfer_v6_fallback_frontier_repair_progress_state_forced',
        'filetransfer_v6_receiver_state_sent',
        'filetransfer_v6_sparse_write_committed',
        'filetransfer_fallback_frontier_repair_dispatched',
        'filetransfer_v6_frontier_request_sent',
        'filetransfer_control_plane_delivery_observed',
        'filetransfer_fallback_tail_terminal_proof_consumed',
        'integrity_verify_passed',
        'temp_finalize_succeeded',
        'file_transfer_inbound_terminal',
        'filetransfer_live_route_epoch_terminal',
        'filetransfer_terminalized_by_session_end',
        'filetransfer_route_selected',
        'filetransfer_live_route_epoch_recovered',
        'filetransfer_route_hint_authorized_publication_accepted',
        'filetransfer_data_session_recreated',
        'filetransfer_data_session_removed')
}

function Get-FileTransferFallbackFrontierGraceTransferKey {
    param([Parameter(Mandatory = $true)]$Event)

    $writer = Get-FileTransferEventField -Event $Event -Name 'log_writer_id' -Default ''
    $session = Get-FileTransferEventField -Event $Event -Name 'session_id' -Default ''
    $transfer = Get-FileTransferEventField -Event $Event -Name 'transfer_id' -Default ''
    if ([string]::IsNullOrWhiteSpace($writer) -or
        [string]::IsNullOrWhiteSpace($session) -or
        [string]::IsNullOrWhiteSpace($transfer)) {
        return ''
    }
    return "$writer|$session|$transfer".ToLowerInvariant()
}

function Add-FileTransferFallbackFrontierGraceIndexedEvent {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Map,
        [Parameter(Mandatory = $true)][string]$Key,
        [Parameter(Mandatory = $true)]$Event)

    if ([string]::IsNullOrWhiteSpace($Key)) { return }
    if (-not $Map.ContainsKey($Key)) {
        $Map[$Key] = New-Object System.Collections.Generic.List[object]
    }
    $Map[$Key].Add($Event) | Out-Null
}

function New-FileTransferFallbackFrontierGraceCorrelationIndex {
    param([AllowEmptyCollection()][object[]]$Events = @())

    $catalog = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($eventName in @(Get-FileTransferFallbackFrontierGraceCorrelationEventNames)) {
        [void]$catalog.Add($eventName)
    }

    $relevant = New-Object System.Collections.Generic.List[object]
    $eventsByTransfer = @{}
    $eventsByTransferAndName = @{}
    $eventsByTransferAndFrontier = @{}
    $eventsByTransferAndAuthority = @{}
    foreach ($event in @($Events)) {
        if ($null -eq $event -or -not $catalog.Contains([string]$event.EventName)) { continue }
        $relevant.Add($event) | Out-Null
        $transferKey = Get-FileTransferFallbackFrontierGraceTransferKey -Event $event
        if ([string]::IsNullOrWhiteSpace($transferKey)) { continue }
        Add-FileTransferFallbackFrontierGraceIndexedEvent -Map $eventsByTransfer -Key $transferKey -Event $event
        Add-FileTransferFallbackFrontierGraceIndexedEvent `
            -Map $eventsByTransferAndName `
            -Key "$transferKey|$([string]$event.EventName)" `
            -Event $event

        $frontiers = [System.Collections.Generic.HashSet[long]]::new()
        foreach ($field in @(
                'frontier_chunk_index', 'current_frontier_chunk_index',
                'previous_frontier_chunk_index', 'contiguous_committed_chunk_index',
                'next_chunk_index')) {
            $frontier = Get-FileTransferEventInt64Field -Event $event -Name $field -Default -1
            if ($frontier -ge 0) { [void]$frontiers.Add($frontier) }
        }
        foreach ($frontier in $frontiers) {
            Add-FileTransferFallbackFrontierGraceIndexedEvent `
                -Map $eventsByTransferAndFrontier `
                -Key "$transferKey|$frontier" `
                -Event $event
        }

        $authority = Get-FileTransferEventField -Event $event -Name 'authority_id' -Default ''
        if ($authority -notin @('', '(none)', 'none')) {
            Add-FileTransferFallbackFrontierGraceIndexedEvent `
                -Map $eventsByTransferAndAuthority `
                -Key "$transferKey|$authority" `
                -Event $event
        }
    }

    foreach ($map in @($eventsByTransfer, $eventsByTransferAndName, $eventsByTransferAndFrontier, $eventsByTransferAndAuthority)) {
        foreach ($key in @($map.Keys)) {
            $map[$key] = @($map[$key] | Sort-Object Sequence, LineNumber)
        }
    }
    return [pscustomobject]@{
        RelevantEvents = @($relevant | Sort-Object Sequence, LineNumber)
        EventsByTransfer = $eventsByTransfer
        EventsByTransferAndName = $eventsByTransferAndName
        EventsByTransferAndFrontier = $eventsByTransferAndFrontier
        EventsByTransferAndAuthority = $eventsByTransferAndAuthority
        IndexBuildVisitCount = [int64]$relevant.Count
        BoundedLookupVisitCount = 0L
    }
}

function Get-FileTransferFallbackFrontierGraceNamedEvents {
    param(
        [Parameter(Mandatory = $true)]$Index,
        [Parameter(Mandatory = $true)][string]$TransferKey,
        [Parameter(Mandatory = $true)][string]$EventName)

    $key = "$TransferKey|$EventName"
    $Index.BoundedLookupVisitCount++
    if (-not $Index.EventsByTransferAndName.ContainsKey($key)) { return @() }
    return @($Index.EventsByTransferAndName[$key])
}

function Find-FileTransferFallbackFrontierGraceEventAfterSequence {
    param(
        [Parameter(Mandatory = $true)]$Index,
        [AllowEmptyCollection()][object[]]$Events = @(),
        [Parameter(Mandatory = $true)][long]$AfterSequence,
        [Parameter(Mandatory = $true)][scriptblock]$Predicate)

    $low = 0
    $high = $Events.Count
    while ($low -lt $high) {
        $middle = $low + [int](($high - $low) / 2)
        $Index.BoundedLookupVisitCount++
        if ([int64]$Events[$middle].Sequence -le $AfterSequence) { $low = $middle + 1 }
        else { $high = $middle }
    }
    for ($position = $low; $position -lt $Events.Count; $position++) {
        $Index.BoundedLookupVisitCount++
        if (& $Predicate $Events[$position]) { return $Events[$position] }
    }
    return $null
}

function Get-FileTransferFallbackFrontierGracePartitionSlice {
    param(
        [Parameter(Mandatory = $true)]$Index,
        [Parameter(Mandatory = $true)][string]$TransferKey,
        [Parameter(Mandatory = $true)][long[]]$Frontiers)

    $selected = [ordered]@{}
    $addEvents = {
        param([object[]]$Candidates)
        foreach ($event in @($Candidates)) {
            $identity = '{0}|{1}|{2}|{3}' -f
                $event.EventName,
                [int64]$event.Sequence,
                [int64]$event.LineNumber,
                (Get-FileTransferEventField -Event $event -Name 'log_writer_id' -Default '')
            if (-not $selected.Contains($identity)) { $selected[$identity] = $event }
        }
    }

    foreach ($frontier in $Frontiers) {
        $frontierKey = "$TransferKey|$frontier"
        $Index.BoundedLookupVisitCount++
        if ($Index.EventsByTransferAndFrontier.ContainsKey($frontierKey)) {
            & $addEvents @($Index.EventsByTransferAndFrontier[$frontierKey])
        }
    }

    $broadNames = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($eventName in @(
            'filetransfer_fallback_tail_terminal_proof_consumed',
            'integrity_verify_passed',
            'temp_finalize_succeeded',
            'file_transfer_inbound_terminal',
            'filetransfer_live_route_epoch_terminal',
            'filetransfer_terminalized_by_session_end',
            'filetransfer_route_selected',
            'filetransfer_live_route_epoch_recovered',
            'filetransfer_route_hint_authorized_publication_accepted',
            'filetransfer_data_session_recreated',
            'filetransfer_data_session_removed')) {
        [void]$broadNames.Add($eventName)
    }
    $Index.BoundedLookupVisitCount++
    [object[]]$transferEvents = if ($Index.EventsByTransfer.ContainsKey($TransferKey)) {
        @($Index.EventsByTransfer[$TransferKey])
    }
    else { @() }
    & $addEvents @($transferEvents | Where-Object { $broadNames.Contains([string]$_.EventName) })

    [object[]]$stallDeferredPool = @(
        Get-FileTransferFallbackFrontierGraceNamedEvents `
            -Index $Index -TransferKey $TransferKey `
            -EventName 'filetransfer_v6_frontier_request_deferred')
    [object[]]$stallCandidates = @($selected.Values | Where-Object {
            $_.EventName -eq 'filetransfer_v6_frontier_request_deferred'
        })
    foreach ($candidate in $stallCandidates) {
        $candidateSequence = [int64]$candidate.Sequence
        $candidateFrontier = Get-FileTransferEventInt64Field -Event $candidate -Name 'frontier_chunk_index' -Default -1
        $candidateWake = Get-FileTransferEventInt64Field -Event $candidate -Name 'wake_generation' -Default 0
        $candidateTicks = Get-FileTransferEventInt64Field -Event $candidate -Name 'log_writer_attempt_utc_ticks' -Default 0
        $candidateGrace = Get-FileTransferEventInt64Field -Event $candidate -Name 'stall_grace_ms' -Default 0
        $deadlineTicks = if ($candidateTicks -gt 0 -and $candidateGrace -gt 0) {
            $candidateTicks + ($candidateGrace * [TimeSpan]::TicksPerMillisecond)
        }
        else { 0L }
        $deadlineUtc = if ($null -ne $candidate.TimestampUtc -and $candidateGrace -gt 0) {
            ([DateTimeOffset]$candidate.TimestampUtc).AddMilliseconds($candidateGrace)
        }
        else { [DateTimeOffset]::MinValue }
        $successorDeferral = $null
        if ($candidateWake -gt 0) {
            $successorDeferral = Find-FileTransferFallbackFrontierGraceEventAfterSequence `
                -Index $Index -Events $stallDeferredPool -AfterSequence $candidateSequence `
                -Predicate {
                    param($event)
                    if ((Get-FileTransferEventInt64Field -Event $event -Name 'frontier_chunk_index' -Default -1) -le $candidateFrontier -or
                        (Get-FileTransferEventInt64Field -Event $event -Name 'wake_generation' -Default 0) -le $candidateWake) {
                        return $false
                    }
                    $eventTicks = Get-FileTransferEventInt64Field -Event $event -Name 'log_writer_attempt_utc_ticks' -Default 0
                    if ($deadlineTicks -gt 0) { return $eventTicks -gt 0 -and $eventTicks -le $deadlineTicks }
                    return $deadlineUtc -ne [DateTimeOffset]::MinValue -and
                        $null -ne $event.TimestampUtc -and [DateTimeOffset]$event.TimestampUtc -le $deadlineUtc
                }
        }
        & $addEvents @($successorDeferral | Where-Object { $null -ne $_ })
    }

    $authorities = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($event in @($selected.Values)) {
        $authority = Get-FileTransferEventField -Event $event -Name 'authority_id' -Default ''
        if ($authority -notin @('', '(none)', 'none')) { [void]$authorities.Add($authority) }
    }
    foreach ($authority in $authorities) {
        $authorityKey = "$TransferKey|$authority"
        $Index.BoundedLookupVisitCount++
        if ($Index.EventsByTransferAndAuthority.ContainsKey($authorityKey)) {
            & $addEvents @($Index.EventsByTransferAndAuthority[$authorityKey])
        }
    }

    $requestKeys = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($event in @($selected.Values)) {
        $requestId = Get-FileTransferEventField -Event $event -Name 'primary_protocol_repair_request_id' -Default ''
        if ([string]::IsNullOrWhiteSpace($requestId)) {
            $requestId = Get-FileTransferEventField -Event $event -Name 'repair_request_id' -Default ''
        }
        $correlationId = Get-FileTransferEventField -Event $event -Name 'primary_request_correlation_id' -Default ''
        if ([string]::IsNullOrWhiteSpace($correlationId)) {
            $correlationId = Get-FileTransferEventField -Event $event -Name 'request_correlation_id' -Default ''
        }
        if ($requestId -notin @('', '(none)', 'none') -and
            $correlationId -notin @('', '(none)', 'none')) {
            [void]$requestKeys.Add("$requestId|$correlationId")
        }
    }
    & $addEvents @($transferEvents | Where-Object {
            if ($_.EventName -ne 'filetransfer_control_plane_delivery_observed') { return $false }
            $requestId = Get-FileTransferEventField -Event $_ -Name 'repair_request_id' -Default ''
            $correlationId = Get-FileTransferEventField -Event $_ -Name 'request_correlation_id' -Default ''
            return $requestKeys.Contains("$requestId|$correlationId")
        })

    & $addEvents @($transferEvents | Where-Object {
            $_.EventName -eq 'filetransfer_v6_receiver_state_sent' -and
            ((Get-FileTransferEventField -Event $_ -Name 'transfer_paused' -Default '0') -eq '1' -or
                (Get-FileTransferEventField -Event $_ -Name 'status' -Default '') -in @('Completed', 'Failed', 'Canceled'))
        })
    [object[]]$receiverStatePool = @(
        Get-FileTransferFallbackFrontierGraceNamedEvents `
            -Index $Index -TransferKey $TransferKey `
            -EventName 'filetransfer_v6_receiver_state_sent')
    [object[]]$sparseWritePool = @(
        Get-FileTransferFallbackFrontierGraceNamedEvents `
            -Index $Index -TransferKey $TransferKey `
            -EventName 'filetransfer_v6_sparse_write_committed')
    [object[]]$graceCandidates = @($selected.Values | Where-Object {
            $_.EventName -in @(
                'filetransfer_v6_frontier_request_deferred',
                'filetransfer_v6_receiver_state_deferred')
        })
    foreach ($candidate in $graceCandidates) {
        $candidateSequence = [int64]$candidate.Sequence
        $candidateFrontier = if ($candidate.EventName -eq 'filetransfer_v6_frontier_request_deferred') {
            Get-FileTransferEventInt64Field -Event $candidate -Name 'frontier_chunk_index' -Default -1
        }
        else {
            Get-FileTransferEventInt64Field -Event $candidate -Name 'next_chunk_index' -Default -1
        }
        $receiverProgress = Find-FileTransferFallbackFrontierGraceEventAfterSequence `
            -Index $Index -Events $receiverStatePool -AfterSequence $candidateSequence `
            -Predicate {
                param($event)
                (Get-FileTransferEventInt64Field -Event $event `
                    -Name 'contiguous_committed_chunk_index' -Default -1) -gt $candidateFrontier
            }
        $sparseProgress = Find-FileTransferFallbackFrontierGraceEventAfterSequence `
            -Index $Index -Events $sparseWritePool -AfterSequence $candidateSequence `
            -Predicate {
                param($event)
                (Get-FileTransferEventInt64Field -Event $event `
                    -Name 'next_chunk_index' -Default -1) -gt $candidateFrontier
            }
        [object[]]$naturalProgress = @(
            @($receiverProgress, $sparseProgress) |
                Where-Object { $null -ne $_ } |
                Sort-Object Sequence, LineNumber |
                Select-Object -First 1)
        & $addEvents $naturalProgress
    }
    [object[]]$progressReferences = @($selected.Values | Where-Object {
            $_.EventName -eq 'filetransfer_fallback_frontier_repair_progress_observed'
        })
    foreach ($progress in $progressReferences) {
        $progressFrontier = Get-FileTransferEventInt64Field -Event $progress -Name 'current_frontier_chunk_index' -Default -1
        $publishedStateMatch = Find-FileTransferFallbackFrontierGraceEventAfterSequence `
            -Index $Index -Events $receiverStatePool `
            -AfterSequence ([int64]$progress.Sequence) `
            -Predicate {
                param($event)
                (Get-FileTransferEventInt64Field -Event $event `
                    -Name 'contiguous_committed_chunk_index' -Default -1) -eq $progressFrontier
            }
        [object[]]$publishedState = @($publishedStateMatch | Where-Object { $null -ne $_ })
        & $addEvents $publishedState
    }
    return @($selected.Values | Sort-Object Sequence, LineNumber)
}

function Merge-FileTransferFallbackFrontierGraceCorrelationResults {
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Results,
        [Parameter(Mandatory = $true)]$Index,
        [Parameter(Mandatory = $true)][long]$LookupStart)

    $recordsByKey = [ordered]@{}
    $rawCountByRecordKey = @{}
    foreach ($result in @($Results)) {
        if ($null -eq $result) { continue }
        foreach ($record in @($result.CandidateRecords)) {
            $event = $record.Event
            $key = '{0}|{1}|{2}|{3}|{4}' -f
                (Get-FileTransferEventField -Event $event -Name 'log_writer_id' -Default ''),
                [int64]$event.Sequence,
                [int64]$event.LineNumber,
                [string]$record.Reason,
                [string]$record.Variant
            if (-not $recordsByKey.Contains($key)) { $recordsByKey[$key] = $record }
            $recordRawCount = @($record.RawEvents).Count
            if (@($result.CandidateRecords).Count -eq 1) {
                $recordRawCount = [Math]::Max($recordRawCount, [int]$result.RawCount)
            }
            if (-not $rawCountByRecordKey.ContainsKey($key) -or
                $recordRawCount -gt [int]$rawCountByRecordKey[$key]) {
                $rawCountByRecordKey[$key] = $recordRawCount
            }
        }
    }
    [object[]]$records = @($recordsByKey.Values | Sort-Object {
            [int64]$_.Event.Sequence
        }, {
            [int64]$_.Event.LineNumber
        })
    [object[]]$unresolved = @($records | Where-Object { -not [bool]$_.Recovered })
    return [pscustomobject]@{
        RawCount = [int](($rawCountByRecordKey.Values | Measure-Object -Sum).Sum)
        DistinctCount = $records.Count
        RecoveredCount = $records.Count - $unresolved.Count
        UnresolvedCount = $unresolved.Count
        Verdict = if ($unresolved.Count -gt 0) { 'fail' } else { 'pass' }
        CandidateRecords = $records
        UnresolvedCandidateRecords = $unresolved
        IndexBuildVisitCount = [int64]$Index.IndexBuildVisitCount
        BoundedLookupVisitCount = [int64]$Index.BoundedLookupVisitCount - $LookupStart
    }
}

function Invoke-FileTransferFallbackFrontierGraceIndexedCorrelation {
    param(
        [Parameter(Mandatory = $true)][ValidateSet('stall', 'recent_progress')][string]$Kind,
        [AllowEmptyCollection()][object[]]$Events = @(),
        [AllowNull()]$Index = $null)

    $scopeKey = ''
    $useScopeCache = $null -eq $Index
    if ($useScopeCache) {
        $scopeKey = Get-FileTransferFallbackFrontierGraceCorrelationScopeKey -Events $Events
        if ($script:FileTransferFallbackFrontierGraceCachedScope -ne $scopeKey -or
            $null -eq $script:FileTransferFallbackFrontierGraceCachedIndex) {
            $script:FileTransferFallbackFrontierGraceCachedScope = $scopeKey
            $script:FileTransferFallbackFrontierGraceCachedIndex =
                New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $Events
            $script:FileTransferFallbackFrontierGraceCachedResults = @{}
        }
        $Index = $script:FileTransferFallbackFrontierGraceCachedIndex
        if ($script:FileTransferFallbackFrontierGraceCachedResults.ContainsKey($Kind)) {
            return $script:FileTransferFallbackFrontierGraceCachedResults[$Kind]
        }
    }
    $lookupStart = [int64]$Index.BoundedLookupVisitCount
    $partitionKeys = [ordered]@{}
    foreach ($event in @($Index.RelevantEvents)) {
        $frontier = -1L
        if ($Kind -eq 'stall' -and $event.EventName -eq 'filetransfer_v6_frontier_request_deferred') {
            $frontier = Get-FileTransferEventInt64Field -Event $event -Name 'frontier_chunk_index' -Default -1
        }
        elseif ($Kind -eq 'recent_progress' -and $event.EventName -eq 'filetransfer_v6_receiver_state_deferred') {
            $frontier = Get-FileTransferEventInt64Field -Event $event -Name 'next_chunk_index' -Default -1
        }
        if ($frontier -lt 0) { continue }
        $transferKey = Get-FileTransferFallbackFrontierGraceTransferKey -Event $event
        if ([string]::IsNullOrWhiteSpace($transferKey)) { continue }
        $partitionKeys["$transferKey|$frontier"] = [pscustomobject]@{
            TransferKey = $transferKey
            Frontier = $frontier
        }
    }

    if ($partitionKeys.Count -eq 0) {
        $emptyResult = Merge-FileTransferFallbackFrontierGraceCorrelationResults `
            -Results @() -Index $Index -LookupStart $lookupStart
        if ($useScopeCache) {
            $script:FileTransferFallbackFrontierGraceCachedResults[$Kind] = $emptyResult
        }
        return $emptyResult
    }
    $partitionsByTransfer = [ordered]@{}
    foreach ($partition in $partitionKeys.Values) {
        if (-not $partitionsByTransfer.Contains($partition.TransferKey)) {
            $partitionsByTransfer[$partition.TransferKey] = New-Object System.Collections.Generic.List[long]
        }
        $partitionsByTransfer[$partition.TransferKey].Add([long]$partition.Frontier) | Out-Null
    }
    $legacy = if ($Kind -eq 'stall') {
        $script:FileTransferFallbackFrontierStallGraceLegacyCorrelation
    }
    else {
        $script:FileTransferFallbackFrontierRecentProgressGraceLegacyCorrelation
    }
    $results = New-Object System.Collections.Generic.List[object]
    foreach ($transferKey in $partitionsByTransfer.Keys) {
        [long[]]$frontiers = @($partitionsByTransfer[$transferKey] | Sort-Object -Unique)
        # Keep the legacy semantic reducer bounded as well as its input lookup.
        # Windows PowerShell 5.1 pays a steep quadratic cost above this width.
        $partitionWidth = [Math]::Max(1, [int]$script:FileTransferFallbackFrontierGracePartitionWidth)
        for ($offset = 0; $offset -lt $frontiers.Count; $offset += $partitionWidth) {
            $count = [Math]::Min($partitionWidth, $frontiers.Count - $offset)
            [long[]]$batch = @($frontiers[$offset..($offset + $count - 1)])
            [object[]]$slice = @(Get-FileTransferFallbackFrontierGracePartitionSlice `
                -Index $Index `
                -TransferKey $transferKey `
                -Frontiers $batch)
            $results.Add((& $legacy -Events $slice)) | Out-Null
        }
    }
    $mergedResult = Merge-FileTransferFallbackFrontierGraceCorrelationResults `
        -Results @($results.ToArray()) -Index $Index -LookupStart $lookupStart
    if ($useScopeCache) {
        $script:FileTransferFallbackFrontierGraceCachedResults[$Kind] = $mergedResult
    }
    return $mergedResult
}

function Get-FileTransferFallbackFrontierStallGraceRecheckCorrelation {
    param(
        [object[]]$Events = @(),
        [AllowNull()]$Index = $null)

    return Invoke-FileTransferFallbackFrontierGraceIndexedCorrelation `
        -Kind 'stall' -Events $Events -Index $Index
}

function Get-FileTransferFallbackFrontierRecentProgressGraceRecheckCorrelation {
    param(
        [object[]]$Events = @(),
        [AllowNull()]$Index = $null)

    return Invoke-FileTransferFallbackFrontierGraceIndexedCorrelation `
        -Kind 'recent_progress' -Events $Events -Index $Index
}
