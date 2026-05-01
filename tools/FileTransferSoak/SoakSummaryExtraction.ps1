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
        BatchSentAsBatchCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_chunk_batch_sent_as_batch'
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
        ReceiverBufferWriteBatchCommittedCount = Get-FileTransferEventCount -Events $transferEvents -Name 'filetransfer_receiver_write_batch_committed'
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
