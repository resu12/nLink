# Frozen pre-optimization builder for exact bucket/reference parity tests only.
function Add-ReferenceFrontierGraceIndexedEvent {
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

function New-ReferenceFrontierGraceCorrelationIndex {
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
    $eventsByWriterSequence = @{}
    $eventsByWriterIdentity = @{}
    $sourceBoundariesByTransfer = @{}
    $sourceValuesByTransfer = @{}
    $sourceFields = @('direction', 'route', 'protocol_version', 'live_route_epoch', 'runtime_generation',
        'terminal_generation', 'leg_generation', 'transport_epoch', 'bridge_recovery_generation',
        'bridge_decision_generation', 'checkpoint_request_id', 'checkpoint_generation',
        'route_publication_generation', 'route_commit_generation', 'data_session_identity', 'data_session_generation')
    # Source changes are indexed in the same ordered build pass as the evidence.
    # Retain same-writer copies before requiring an intact session/transfer key.
    foreach ($event in @($Events | Sort-Object Sequence, LineNumber)) {
        if ($null -eq $event) { continue }
        $writer = Get-FileTransferEventField -Event $event -Name 'log_writer_id' -Default ''
        $writerSequence = Get-FileTransferEventField -Event $event -Name 'log_writer_sequence' -Default ''
        if (-not [string]::IsNullOrWhiteSpace($writer) -and $writerSequence -match '^[1-9][0-9]*$') {
            Add-ReferenceFrontierGraceIndexedEvent -Map $eventsByWriterIdentity `
                -Key "$writer|$writerSequence" -Event $event
        }
        if (-not $catalog.Contains([string]$event.EventName)) { continue }
        $relevant.Add($event) | Out-Null
        $transferKey = Get-FileTransferFallbackFrontierGraceTransferKey -Event $event
        if ([string]::IsNullOrWhiteSpace($transferKey)) { continue }
        if (-not $sourceValuesByTransfer.ContainsKey($transferKey)) {
            $sourceValuesByTransfer[$transferKey] = @{}
        }
        $sourceValues = $sourceValuesByTransfer[$transferKey]
        $sourceBoundary = $event.EventName -in @('file_transfer_inbound_terminal', 'filetransfer_live_route_epoch_terminal',
            'filetransfer_terminalized_by_session_end', 'filetransfer_data_session_recreated', 'filetransfer_data_session_removed') -or
            (Get-FileTransferEventField -Event $event -Name 'transfer_paused' -Default '') -ceq '1'
        foreach ($field in $sourceFields) {
            if (-not $event.Fields.ContainsKey($field)) { continue }
            $value = [string]$event.Fields[$field]
            if ($sourceValues.ContainsKey($field) -and $sourceValues[$field] -cne $value) { $sourceBoundary = $true }
            $sourceValues[$field] = $value
        }
        if ($sourceBoundary) {
            Add-ReferenceFrontierGraceIndexedEvent -Map $sourceBoundariesByTransfer `
                -Key $transferKey -Event $event
        }
        if ($writerSequence -match '^[1-9][0-9]*$') {
            Add-ReferenceFrontierGraceIndexedEvent -Map $eventsByWriterSequence -Key "$transferKey|$writerSequence" -Event $event
        }
        Add-ReferenceFrontierGraceIndexedEvent -Map $eventsByTransfer -Key $transferKey -Event $event
        Add-ReferenceFrontierGraceIndexedEvent `
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
            Add-ReferenceFrontierGraceIndexedEvent `
                -Map $eventsByTransferAndFrontier `
                -Key "$transferKey|$frontier" `
                -Event $event
        }

        $authority = Get-FileTransferEventField -Event $event -Name 'authority_id' -Default ''
        if ($authority -notin @('', '(none)', 'none')) {
            Add-ReferenceFrontierGraceIndexedEvent `
                -Map $eventsByTransferAndAuthority `
                -Key "$transferKey|$authority" `
                -Event $event
        }
    }

    foreach ($map in @($eventsByTransfer, $eventsByTransferAndName, $eventsByTransferAndFrontier,
            $eventsByTransferAndAuthority, $sourceBoundariesByTransfer)) {
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
        EventsByWriterSequence = $eventsByWriterSequence
        EventsByWriterIdentity = $eventsByWriterIdentity
        SourceBoundariesByTransfer = $sourceBoundariesByTransfer
        IndexBuildVisitCount = [int64]$relevant.Count
        BoundedLookupVisitCount = 0L
    }
}
