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
        'filetransfer_route_transition_committed',
        'filetransfer_route_transition_disposition_applied',
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
        $Map[$Key] = [System.Collections.Generic.List[object]]::new()
    }
    [void]$Map[$Key].Add($Event)
}

function New-FileTransferFallbackFrontierGraceCorrelationIndex {
    param([AllowEmptyCollection()][object[]]$Events = @(), [AllowNull()]$SnapshotEvidence = $null)

    $catalog = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($eventName in @(Get-FileTransferFallbackFrontierGraceCorrelationEventNames)) {
        [void]$catalog.Add($eventName)
    }

    $relevant = [System.Collections.Generic.List[object]]::new()
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
        # Match Get-FileTransferEventField's dictionary/default semantics once
        # per event. Writer copies outside the catalog must still be retained.
        $fieldsProperty = $event.PSObject.Properties['Fields']
        $fields = if ($null -ne $fieldsProperty -and $fieldsProperty.Value -is [System.Collections.IDictionary]) {
            $fieldsProperty.Value
        } else { $null }
        $writer = if ($null -ne $fields -and $fields.Contains('log_writer_id')) { [string]$fields['log_writer_id'] } else { '' }
        $writerSequence = if ($null -ne $fields -and $fields.Contains('log_writer_sequence')) { [string]$fields['log_writer_sequence'] } else { '' }
        $validWriterSequence = $writerSequence -match '^[1-9][0-9]*$'
        if (-not [string]::IsNullOrWhiteSpace($writer) -and $validWriterSequence) {
            Add-FileTransferFallbackFrontierGraceIndexedEvent -Map $eventsByWriterIdentity `
                -Key "$writer|$writerSequence" -Event $event
        }
        if (-not $catalog.Contains([string]$event.EventName)) { continue }
        [void]$relevant.Add($event)
        $session = if ($null -ne $fields -and $fields.Contains('session_id')) { [string]$fields['session_id'] } else { '' }
        $transfer = if ($null -ne $fields -and $fields.Contains('transfer_id')) { [string]$fields['transfer_id'] } else { '' }
        if ([string]::IsNullOrWhiteSpace($writer) -or [string]::IsNullOrWhiteSpace($session) -or
            [string]::IsNullOrWhiteSpace($transfer)) { continue }
        $transferKey = "$writer|$session|$transfer".ToLowerInvariant()
        if (-not $sourceValuesByTransfer.ContainsKey($transferKey)) {
            $sourceValuesByTransfer[$transferKey] = @{}
        }
        $sourceValues = $sourceValuesByTransfer[$transferKey]
        $sourceBoundary = $event.EventName -in @('file_transfer_inbound_terminal', 'filetransfer_live_route_epoch_terminal',
            'filetransfer_terminalized_by_session_end', 'filetransfer_data_session_recreated', 'filetransfer_data_session_removed') -or
            ($fields.Contains('transfer_paused') -and [string]$fields['transfer_paused'] -ceq '1')
        foreach ($field in $sourceFields) {
            if (-not $fields.ContainsKey($field)) { continue }
            $value = [string]$fields[$field]
            if ($sourceValues.ContainsKey($field) -and $sourceValues[$field] -cne $value) { $sourceBoundary = $true }
            $sourceValues[$field] = $value
        }
        if ($sourceBoundary) {
            Add-FileTransferFallbackFrontierGraceIndexedEvent -Map $sourceBoundariesByTransfer `
                -Key $transferKey -Event $event
        }
        if ($validWriterSequence) {
            Add-FileTransferFallbackFrontierGraceIndexedEvent -Map $eventsByWriterSequence -Key "$transferKey|$writerSequence" -Event $event
        }
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
            $frontierText = if ($fields.Contains($field)) { [string]$fields[$field] } else { '' }
            $frontier = 0L
            if ([long]::TryParse($frontierText, [ref]$frontier) -and $frontier -ge 0) { [void]$frontiers.Add($frontier) }
        }
        foreach ($frontier in $frontiers) {
            Add-FileTransferFallbackFrontierGraceIndexedEvent `
                -Map $eventsByTransferAndFrontier `
                -Key "$transferKey|$frontier" `
                -Event $event
        }

        $authority = if ($fields.Contains('authority_id')) { [string]$fields['authority_id'] } else { '' }
        if ($authority -notin @('', '(none)', 'none')) {
            Add-FileTransferFallbackFrontierGraceIndexedEvent `
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
        __CheckpointRepairContinuitySnapshotEvidence = $SnapshotEvidence
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
        [long]$ThroughSequence = [long]::MaxValue,
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
        if ([long]$Events[$position].Sequence -gt $ThroughSequence) { break }
        if (& $Predicate $Events[$position]) { return $Events[$position] }
    }
    return $null
}

function Get-FileTransferFallbackFrontierGraceSequenceRange {
    param(
        [Parameter(Mandatory = $true)]$Index,
        [AllowEmptyCollection()][object[]]$Events = @(),
        [Parameter(Mandatory = $true)][long]$AfterSequence,
        [Parameter(Mandatory = $true)][long]$ThroughSequence)

    $low = 0
    $high = $Events.Count
    while ($low -lt $high) {
        $middle = $low + [int](($high - $low) / 2)
        $Index.BoundedLookupVisitCount++
        if ([long]$Events[$middle].Sequence -le $AfterSequence) { $low = $middle + 1 }
        else { $high = $middle }
    }
    for ($position = $low; $position -lt $Events.Count; $position++) {
        $Index.BoundedLookupVisitCount++
        if ([long]$Events[$position].Sequence -gt $ThroughSequence) { break }
        $Events[$position]
    }
}

function Get-FileTransferFallbackFrontierGracePartitionSlice {
    param(
        [Parameter(Mandatory = $true)]$Index,
        [Parameter(Mandatory = $true)][string]$TransferKey,
        [Parameter(Mandatory = $true)][long[]]$Frontiers)

    $selected = [ordered]@{}
    $addEvents = {
        param([object[]]$Candidates, [switch]$IncludeWriterCopies)
        foreach ($candidate in @($Candidates)) {
            $writerSequence = Get-FileTransferEventField -Event $candidate -Name 'log_writer_sequence' -Default ''
            $copies = @($candidate)
            $Index.BoundedLookupVisitCount++
            $writer = Get-FileTransferEventField -Event $candidate -Name 'log_writer_id' -Default ''
            if ($IncludeWriterCopies -and $Index.EventsByWriterIdentity.ContainsKey("$writer|$writerSequence")) {
                $copies = $Index.EventsByWriterIdentity["$writer|$writerSequence"].ToArray()
            }
            elseif ($Index.EventsByWriterSequence.ContainsKey("$TransferKey|$writerSequence")) {
                $copies = $Index.EventsByWriterSequence["$TransferKey|$writerSequence"].ToArray()
            }
            $copyIndex = 0
            foreach ($event in $copies) {
                $Index.BoundedLookupVisitCount++
                $identity = '{0}|{1}|{2}|{3}|{4}|{5}' -f
                    $event.EventName,
                    [int64]$event.Sequence,
                    [int64]$event.LineNumber,
                    (Get-FileTransferEventField -Event $event -Name 'log_writer_id' -Default ''),
                    (Get-FileTransferEventField -Event $event -Name 'log_writer_sequence' -Default ''),
                    $copyIndex++
                if (-not $selected.Contains($identity)) { $selected[$identity] = $event }
            }
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
            'filetransfer_route_transition_committed',
            'filetransfer_route_transition_disposition_applied',
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
    [object[]]$receiverStatePool = @(
        Get-FileTransferFallbackFrontierGraceNamedEvents `
            -Index $Index -TransferKey $TransferKey `
            -EventName 'filetransfer_v6_receiver_state_sent')
    [object[]]$sparseWritePool = @(
        Get-FileTransferFallbackFrontierGraceNamedEvents `
            -Index $Index -TransferKey $TransferKey `
            -EventName 'filetransfer_v6_sparse_write_committed')
    [object[]]$stallCandidates = @($selected.Values | Where-Object {
            $_.EventName -eq 'filetransfer_v6_frontier_request_deferred'
        })
    foreach ($candidate in $stallCandidates) {
        $candidateSequence = [int64]$candidate.Sequence
        $candidateFrontier = Get-FileTransferEventInt64Field -Event $candidate -Name 'frontier_chunk_index' -Default -1
        $candidateWake = Get-FileTransferEventInt64Field -Event $candidate -Name 'wake_generation' -Default 0
        # A current-source rebind needs the complete bounded interval from its
        # historical progress to the first advancing state, including records
        # whose mutated frontier would otherwise put them in another bucket.
        $currentFrontierKey = "$TransferKey|$candidateFrontier"
        if ($Index.EventsByTransferAndFrontier.ContainsKey($currentFrontierKey)) {
            $historicalProgress = $null
            foreach ($event in $Index.EventsByTransferAndFrontier[$currentFrontierKey]) {
                $Index.BoundedLookupVisitCount++
                if ([long]$event.Sequence -ge $candidateSequence) { break }
                if ($event.EventName -eq 'filetransfer_fallback_frontier_repair_progress_observed' -and
                    (Get-FileTransferEventInt64Field -Event $event -Name 'current_frontier_chunk_index' -Default -1) -eq $candidateFrontier) {
                    $historicalProgress = $event
                }
            }
            if ($null -ne $historicalProgress -and
                (Get-FileTransferEventField -Event $historicalProgress -Name 'runtime_generation' -Default '') -cne
                    (Get-FileTransferEventField -Event $candidate -Name 'runtime_generation' -Default '')) {
                $closingState = Find-FileTransferFallbackFrontierGraceEventAfterSequence `
                    -Index $Index -Events $receiverStatePool -AfterSequence $candidateSequence -Predicate {
                        param($event)
                        (Get-FileTransferEventInt64Field -Event $event -Name 'contiguous_committed_chunk_index' -Default -1) -gt $candidateFrontier
                    }
                $through = if ($null -ne $closingState) { [long]$closingState.Sequence } else { [long]::MaxValue }
                & $addEvents @(Get-FileTransferFallbackFrontierGraceSequenceRange `
                    -Index $Index -Events $transferEvents -AfterSequence ([long]$historicalProgress.Sequence - 1) `
                    -ThroughSequence $through) -IncludeWriterCopies
            }
        }
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

        # A consumed exact wake can be followed by independent committed
        # progress after its deadline. Import that proof without admitting it.
        $frontierKey = "$TransferKey|$candidateFrontier"
        if ($Index.EventsByTransferAndFrontier.ContainsKey($frontierKey)) {
            foreach ($recheck in $Index.EventsByTransferAndFrontier[$frontierKey]) {
                $Index.BoundedLookupVisitCount++
                if ($recheck.EventName -ne 'filetransfer_v6_frontier_stall_grace_recheck_decided' -or
                    (Get-FileTransferEventInt64Field -Event $recheck -Name 'wake_generation' -Default 0) -ne $candidateWake) { continue }
                if ((Get-FileTransferEventField -Event $recheck -Name 'decision_status' -Default '') -ceq 'stale' -and
                    (Get-FileTransferEventField -Event $recheck -Name 'decision_reason' -Default '') -ceq 'frontier_scope_changed') {
                    # Import subjects and their whole proof interval, not just
                    # favorable outcomes. The semantic helper owns validation.
                    & $addEvents @($candidate, $recheck) -IncludeWriterCopies
                    $lifetimeEnd = $null
                    if ($Index.SourceBoundariesByTransfer.ContainsKey($TransferKey)) {
                        $lifetimeEnd = Find-FileTransferFallbackFrontierGraceEventAfterSequence `
                            -Index $Index -Events $Index.SourceBoundariesByTransfer[$TransferKey] `
                            -AfterSequence $candidateSequence -Predicate { param($event) $true }
                    }
                    $throughSequence = if ($null -ne $lifetimeEnd) { [long]$lifetimeEnd.Sequence } else { [long]::MaxValue }
                    & $addEvents @($lifetimeEnd | Where-Object { $null -ne $_ }) -IncludeWriterCopies
                    # Natural progress may close before a later request send.
                    # Keep the first subjects, even when their proof fails.
                    $sparseAdvance = Find-FileTransferFallbackFrontierGraceEventAfterSequence `
                        -Index $Index -Events $sparseWritePool `
                        -AfterSequence ([long]$recheck.Sequence) -ThroughSequence $throughSequence `
                        -Predicate {
                            param($event)
                            (Get-FileTransferEventInt64Field -Event $event -Name 'next_chunk_index' -Default -1) -gt $candidateFrontier
                        }
                    if ($null -ne $sparseAdvance) {
                        $naturalState = Find-FileTransferFallbackFrontierGraceEventAfterSequence `
                            -Index $Index -Events $receiverStatePool `
                            -AfterSequence ([long]$sparseAdvance.Sequence) -ThroughSequence $throughSequence `
                            -Predicate { param($event) $true }
                        if ($null -ne $naturalState) {
                            & $addEvents @(Get-FileTransferFallbackFrontierGraceSequenceRange `
                                -Index $Index -Events $transferEvents -AfterSequence $candidateSequence `
                                -ThroughSequence ([long]$naturalState.Sequence)) -IncludeWriterCopies
                        }
                    }
                    $currentSend = Find-FileTransferFallbackFrontierGraceEventAfterSequence `
                        -Index $Index -Events $Index.EventsByTransferAndFrontier[$frontierKey] `
                        -AfterSequence ([long]$recheck.Sequence) -ThroughSequence $throughSequence `
                        -Predicate {
                            param($event)
                            $event.EventName -eq 'filetransfer_v6_frontier_request_sent' -and
                                (Get-FileTransferEventInt64Field -Event $event -Name 'frontier_chunk_index' -Default -1) -eq $candidateFrontier
                        }
                    if ($null -eq $currentSend) { continue }
                    & $addEvents @($currentSend) -IncludeWriterCopies
                    $authority = Get-FileTransferEventField -Event $currentSend -Name 'authority_id' -Default ''
                    $authorityKey = "$TransferKey|$authority"
                    if ($Index.EventsByTransferAndAuthority.ContainsKey($authorityKey)) {
                        $retirement = Find-FileTransferFallbackFrontierGraceEventAfterSequence `
                            -Index $Index -Events $Index.EventsByTransferAndAuthority[$authorityKey] `
                            -AfterSequence ([long]$currentSend.Sequence) -ThroughSequence $throughSequence `
                            -Predicate { param($event) $event.EventName -eq 'filetransfer_fallback_frontier_repair_contract_settled' }
                        if ($null -ne $retirement) {
                            $throughSequence = [long]$retirement.Sequence
                            & $addEvents @($retirement) -IncludeWriterCopies
                        }
                    }
                    $advance = Find-FileTransferFallbackFrontierGraceEventAfterSequence `
                        -Index $Index -Events $stallDeferredPool -AfterSequence ([long]$currentSend.Sequence) `
                        -ThroughSequence $throughSequence -Predicate {
                            param($event)
                            (Get-FileTransferEventInt64Field -Event $event -Name 'frontier_chunk_index' -Default -1) -gt $candidateFrontier
                        }
                    if ($null -eq $advance) { continue }
                    & $addEvents @($advance) -IncludeWriterCopies
                    $advanceFrontier = Get-FileTransferEventInt64Field -Event $advance -Name 'frontier_chunk_index' -Default -1
                    $state = Find-FileTransferFallbackFrontierGraceEventAfterSequence `
                        -Index $Index -Events $receiverStatePool -AfterSequence ([long]$advance.Sequence) `
                        -ThroughSequence $throughSequence -Predicate {
                            param($event)
                            (Get-FileTransferEventInt64Field -Event $event -Name 'contiguous_committed_chunk_index' -Default -1) -ge $advanceFrontier
                        }
                    if ($null -eq $state) { continue }
                    & $addEvents @(Get-FileTransferFallbackFrontierGraceSequenceRange `
                        -Index $Index -Events $transferEvents -AfterSequence $candidateSequence `
                        -ThroughSequence ([long]$state.Sequence)) -IncludeWriterCopies
                    continue
                }
                if ((Get-FileTransferEventField -Event $recheck -Name 'decision_status' -Default '') -cne 'dispatched' -or
                    (Get-FileTransferEventField -Event $recheck -Name 'decision_reason' -Default '') -cne 'exact_receiver_request_dispatched') { continue }
                $committedAdvance = Find-FileTransferFallbackFrontierGraceEventAfterSequence `
                    -Index $Index -Events $stallDeferredPool -AfterSequence ([long]$recheck.Sequence) `
                    -Predicate {
                        param($event)
                        if ((Get-FileTransferEventInt64Field -Event $event -Name 'frontier_chunk_index' -Default -1) -le $candidateFrontier -or
                            (Get-FileTransferEventInt64Field -Event $event -Name 'wake_generation' -Default 0) -le $candidateWake -or
                            (Get-FileTransferEventField -Event $event -Name 'reason' -Default '') -cne 'chunk_batch_committed') { return $false }
                        foreach ($field in @('direction', 'route', 'protocol_version', 'live_route_epoch', 'runtime_generation',
                                'terminal_generation', 'leg_generation', 'transport_epoch', 'bridge_recovery_generation',
                                'bridge_decision_generation', 'checkpoint_request_id', 'checkpoint_generation',
                                'route_publication_generation', 'route_commit_generation', 'data_session_identity', 'data_session_generation')) {
                            $value = Get-FileTransferEventField -Event $candidate -Name $field -Default ''
                            if ([string]::IsNullOrWhiteSpace($value) -or
                                $value -cne (Get-FileTransferEventField -Event $event -Name $field -Default '')) { return $false }
                        }
                        return $true
                    }
                & $addEvents @($committedAdvance | Where-Object { $null -ne $_ })
            }
        }
    }

    $authorities = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
    foreach ($event in @($selected.Values)) {
        $authority = Get-FileTransferEventField -Event $event -Name 'authority_id' -Default ''
        if ($authority -notin @('', '(none)', 'none')) { [void]$authorities.Add($authority) }
        if ($event.EventName -eq 'filetransfer_fallback_frontier_repair_authority_granted' -and
            (Get-FileTransferEventField -Event $event -Name 'source_kind' -Default '') -ceq 'ReceiverRequest') {
            # A published-source obligation has no predecessor-progress event.
            # Retain its grant's exact one-hop authority subject without admitting
            # that predecessor's unrelated frontier obligations.
            $predecessor = Get-FileTransferEventField -Event $event -Name 'predecessor_authority_id' -Default ''
            $predecessorKey = "$TransferKey|$predecessor"
            if ($predecessor -notin @('', '(none)', 'none') -and $Index.EventsByTransferAndAuthority.ContainsKey($predecessorKey)) {
                $Index.BoundedLookupVisitCount++
                & $addEvents @($Index.EventsByTransferAndAuthority[$predecessorKey] | Where-Object {
                    $_.EventName -eq 'filetransfer_fallback_frontier_repair_authority_granted'
                }) -IncludeWriterCopies
            }
        }
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
        if ($candidate.EventName -eq 'filetransfer_v6_receiver_state_deferred' -and $null -ne $sparseProgress) {
            $nextFrontier = Get-FileTransferEventInt64Field -Event $sparseProgress -Name 'next_chunk_index' -Default -1
            $highest = Get-FileTransferEventInt64Field -Event $sparseProgress -Name 'highest_received_chunk_index' -Default -1
            $publishedProgress = Find-FileTransferFallbackFrontierGraceEventAfterSequence `
                -Index $Index -Events $receiverStatePool -AfterSequence ([int64]$sparseProgress.Sequence) `
                -Predicate {
                    param($event)
                    (Get-FileTransferEventInt64Field -Event $event -Name 'contiguous_committed_chunk_index' -Default -1) -eq $nextFrontier -and
                    (Get-FileTransferEventInt64Field -Event $event -Name 'durable_received_highest_chunk_index' -Default -1) -eq $highest
                }
            & $addEvents @($publishedProgress | Where-Object { $null -ne $_ })
            if ($null -ne $publishedProgress) {
                # Preserve competing publication evidence within the handed-off
                # grace, not just the first matching publication.
                $deadlineTicks = 0L
                foreach ($stall in $graceCandidates) {
                    if ($stall.EventName -ne 'filetransfer_v6_frontier_request_deferred' -or
                        [long]$stall.Sequence -le $candidateSequence -or
                        [long]$stall.Sequence -ge [long]$sparseProgress.Sequence -or
                        (Get-FileTransferEventInt64Field -Event $stall -Name 'frontier_chunk_index' -Default -1) -ne $candidateFrontier) { continue }
                    $deadline = [DateTimeOffset]::MinValue
                    if ([DateTimeOffset]::TryParse((Get-FileTransferEventField -Event $stall -Name 'wake_deadline_utc' -Default ''), [ref]$deadline)) {
                        $deadlineTicks = [Math]::Max($deadlineTicks, $deadline.UtcTicks)
                    }
                }
                $publicationKey = "$TransferKey|$nextFrontier"
                $Index.BoundedLookupVisitCount++
                if ($deadlineTicks -gt 0 -and $Index.EventsByTransferAndFrontier.ContainsKey($publicationKey)) {
                    foreach ($publication in $Index.EventsByTransferAndFrontier[$publicationKey]) {
                        $Index.BoundedLookupVisitCount++
                        if ($publication.EventName -eq 'filetransfer_v6_receiver_state_sent' -and
                            [long]$publication.Sequence -gt [long]$sparseProgress.Sequence -and
                            (Get-FileTransferEventInt64Field -Event $publication -Name 'log_writer_attempt_utc_ticks' -Default 0) -lt $deadlineTicks) {
                            & $addEvents @($publication)
                        }
                    }
                }
            }
        }
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
    $rawCount = 0
    foreach ($result in @($Results)) {
        if ($null -eq $result) { continue }
        # Partitions admit disjoint candidate sets. Include raw-only obligations
        # cancelled before their deadline, even when they produce no record.
        $rawCount += [int]$result.RawCount
        foreach ($record in @($result.CandidateRecords)) {
            $event = $record.Event
            $key = '{0}|{1}|{2}|{3}|{4}' -f
                (Get-FileTransferEventField -Event $event -Name 'log_writer_id' -Default ''),
                [int64]$event.Sequence,
                [int64]$event.LineNumber,
                [string]$record.Reason,
                [string]$record.Variant
            if (-not $recordsByKey.Contains($key)) { $recordsByKey[$key] = $record }
        }
    }
    [object[]]$records = @($recordsByKey.Values | Sort-Object {
            [int64]$_.Event.Sequence
        }, {
            [int64]$_.Event.LineNumber
        })
    [object[]]$unresolved = @($records | Where-Object { -not [bool]$_.Recovered })
    return [pscustomobject]@{
        RawCount = $rawCount
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
        [AllowNull()]$Index = $null,
        [AllowNull()]$SnapshotEvidence = $null)

    $scopeKey = ''
    $useScopeCache = $null -eq $Index -and $null -eq $SnapshotEvidence
    if ($null -eq $Index -and $null -ne $SnapshotEvidence) {
        $Index = New-FileTransferFallbackFrontierGraceCorrelationIndex -Events $Events -SnapshotEvidence $SnapshotEvidence
    }
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
            $ownedFrontiers = [System.Collections.Generic.HashSet[long]]::new($batch)
            $ownedCandidates = [System.Collections.Generic.HashSet[object]]::new()
            foreach ($event in $slice) {
                $Index.BoundedLookupVisitCount++
                $frontier = -1L
                if ($Kind -eq 'stall' -and $event.EventName -eq 'filetransfer_v6_frontier_request_deferred') {
                    $frontier = Get-FileTransferEventInt64Field -Event $event -Name 'frontier_chunk_index' -Default -1
                }
                elseif ($Kind -eq 'recent_progress' -and $event.EventName -eq 'filetransfer_v6_receiver_state_deferred') {
                    $frontier = Get-FileTransferEventInt64Field -Event $event -Name 'next_chunk_index' -Default -1
                }
                if ($ownedFrontiers.Contains($frontier) -and
                    (Get-FileTransferFallbackFrontierGraceTransferKey -Event $event) -eq $transferKey) {
                    [void]$ownedCandidates.Add($event)
                }
            }
            # Successor deferrals remain evidence, but only their own partition
            # may open/count them as obligations. Do not prefer a recovered copy.
            if ($Kind -ceq 'stall') {
                $snapshotInput = if ($null -ne $SnapshotEvidence) { $SnapshotEvidence }
                    elseif ($Index.PSObject.Properties.Name -contains '__CheckpointRepairContinuitySnapshotEvidence') {
                        $Index.__CheckpointRepairContinuitySnapshotEvidence
                    } else { $null }
                $results.Add((& $legacy -Events $slice -OwnedCandidateEvents $ownedCandidates -SnapshotEvidence $snapshotInput)) | Out-Null
            }
            else {
                $results.Add((& $legacy -Events $slice -OwnedCandidateEvents $ownedCandidates)) | Out-Null
            }
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
        [AllowNull()]$Index = $null,
        [AllowNull()]$SnapshotEvidence = $null)

    return Invoke-FileTransferFallbackFrontierGraceIndexedCorrelation `
        -Kind 'stall' -Events $Events -Index $Index -SnapshotEvidence $SnapshotEvidence
}

function Get-FileTransferFallbackFrontierRecentProgressGraceRecheckCorrelation {
    param(
        [object[]]$Events = @(),
        [AllowNull()]$Index = $null)

    return Invoke-FileTransferFallbackFrontierGraceIndexedCorrelation `
        -Kind 'recent_progress' -Events $Events -Index $Index
}
