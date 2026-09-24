Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'DataPathRejectedIngressExplanation.ps1')

# Exact retained rejected-ingress evidence rules. Callers initialize the existing
# LogParsing.ps1 field helpers before use; this module never imports or resets it.

function Test-FileTransferFileTunaV4DataPathEvent {
    param([Parameter(Mandatory = $true)]$Event)

    return (Get-FileTransferEventField -Event $Event -Name 'route' -Default '') -eq 'file_tuna_v4' -and
        (Get-FileTransferEventField -Event $Event -Name 'protocol_version' -Default '') -eq '4' -and
        (Get-FileTransferEventField -Event $Event -Name 'target_transport' -Default '') -eq 'tuna' -and
        (Get-FileTransferEventInt64Field -Event $Event -Name 'route_publication_generation' -Default 0) -gt 0
}

function Test-FileTransferFileTunaV4PayloadDataPathEvent {
    param([Parameter(Mandatory = $true)]$Event)

    $rawBytes = Get-FileTransferEventInt64Field -Event $Event -Name 'raw_payload_bytes' -Default -1
    if ($rawBytes -lt 0) {
        $rawBytes = Get-FileTransferEventInt64Field -Event $Event -Name 'raw_bytes' -Default -1
    }
    if ($rawBytes -lt 0) {
        $rawBytes = Get-FileTransferEventInt64Field -Event $Event -Name 'payload_bytes' -Default 0
    }

    $batchChunkCount = Get-FileTransferEventInt64Field -Event $Event -Name 'batch_chunk_count' -Default 0
    if ($batchChunkCount -le 0) {
        $batchChunkCount = Get-FileTransferEventInt64Field -Event $Event -Name 'sent_chunk_count' -Default 0
    }

    if ($rawBytes -le 0 -or $batchChunkCount -le 0) {
        return $false
    }

    $frameType = Get-FileTransferEventField -Event $Event -Name 'frame_type' -Default ''
    if ($frameType -eq 'filetransfer.chunk_batch.v4') {
        return $true
    }

    $batchProfile = Get-FileTransferEventField -Event $Event -Name 'batch_profile' -Default ''
    return -not [string]::IsNullOrWhiteSpace($batchProfile) -and
        $batchProfile.StartsWith('v4_', [System.StringComparison]::OrdinalIgnoreCase)
}

function Test-FileTransferFileTunaV4DataPathActualEvent {
    param([Parameter(Mandatory = $true)]$Event)

    if ($Event.EventName -ne 'filetransfer_data_path_send_result' -and
        $Event.EventName -ne 'filetransfer_data_path_receive_result' -and
        $Event.EventName -ne 'filetransfer_tuna_data_path_peer_visible' -and
        $Event.EventName -ne 'filetransfer_tuna_data_path_mismatch') {
        return $false
    }

    return (Test-FileTransferFileTunaV4DataPathEvent -Event $Event) -and
        (Test-FileTransferFileTunaV4PayloadDataPathEvent -Event $Event)
}

function Get-FileTransferFileTunaV4DataPathPayloadIdentityKey {
    param([Parameter(Mandatory = $true)]$Event)

    if (-not (Test-FileTransferFileTunaV4DataPathHasRange -Event $Event)) {
        return ''
    }

    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($fieldName in @('session_id', 'transfer_id', 'frame_type')) {
        $value = Get-FileTransferEventField -Event $Event -Name $fieldName -Default ''
        if ([string]::IsNullOrWhiteSpace($value)) {
            return ''
        }

        $parts.Add($value.ToLowerInvariant()) | Out-Null
    }

    $rangeStart = Get-FileTransferFileTunaV4DataPathRangeStart -Event $Event
    $rangeEndExclusive =
        Get-FileTransferFileTunaV4DataPathRangeEndExclusive -Event $Event
    $rawPayloadBytes =
        Get-FileTransferEventInt64Field -Event $Event -Name 'raw_payload_bytes' -Default 0
    $batchChunkCount =
        Get-FileTransferEventInt64Field -Event $Event -Name 'batch_chunk_count' -Default 0
    if ($rangeStart -lt 0 -or
        $rangeEndExclusive -le $rangeStart -or
        $rawPayloadBytes -le 0 -or
        $batchChunkCount -le 0) {
        return ''
    }

    $parts.Add([string]$rangeStart) | Out-Null
    $parts.Add([string]$rangeEndExclusive) | Out-Null
    $parts.Add([string]$rawPayloadBytes) | Out-Null
    $parts.Add([string]$batchChunkCount) | Out-Null
    return [string]::Join('|', $parts)
}

function Get-FileTransferFileTunaV4DataPathTransferScopeKey {
    param([Parameter(Mandatory = $true)]$Event)

    $sessionId = Get-FileTransferEventField -Event $Event -Name 'session_id' -Default ''
    $transferId = Get-FileTransferEventField -Event $Event -Name 'transfer_id' -Default ''
    if ([string]::IsNullOrWhiteSpace($sessionId) -or
        [string]::IsNullOrWhiteSpace($transferId)) {
        return ''
    }

    return ("{0}|{1}" -f $sessionId.ToLowerInvariant(), $transferId.ToLowerInvariant())
}

function Get-FileTransferFileTunaV4RejectedReceiveCorrelationKey {
    param([Parameter(Mandatory = $true)]$Event)

    if ($Event.EventName -notin @(
            'filetransfer_data_path_receive_result',
            'filetransfer_tuna_data_path_mismatch',
            'filetransfer_data_frame_ignored')) {
        return ''
    }

    $rangeStart = Get-FileTransferFileTunaV4DataPathRangeStart -Event $Event
    $rangeEndExclusive = Get-FileTransferFileTunaV4DataPathRangeEndExclusive -Event $Event
    if ($rangeStart -lt 0 -or $rangeEndExclusive -le $rangeStart) {
        return ''
    }

    $parts = New-Object System.Collections.Generic.List[string]
    foreach ($fieldName in @(
            'log_writer_id',
            'session_id',
            'transfer_id',
            'frame_type',
            'lane',
            'route',
            'protocol_version',
            'actual_transport',
            'target_transport')) {
        $value = Get-FileTransferEventField -Event $Event -Name $fieldName -Default ''
        if ([string]::IsNullOrWhiteSpace($value)) {
            return ''
        }

        $parts.Add($value.ToLowerInvariant()) | Out-Null
    }

    $parts.Add([string]$rangeStart) | Out-Null
    $parts.Add([string]$rangeEndExclusive) | Out-Null
    return [string]::Join('|', $parts)
}

function Add-FileTransferFileTunaV4DataPathIndexEvent {
    param(
        [Parameter(Mandatory = $true)][hashtable]$Lookup,
        [Parameter(Mandatory = $true)][string]$Key,
        [Parameter(Mandatory = $true)]$Event
    )

    if ([string]::IsNullOrWhiteSpace($Key)) {
        return
    }

    if (-not $Lookup.ContainsKey($Key)) {
        $Lookup[$Key] = New-Object System.Collections.Generic.List[object]
    }

    $Lookup[$Key].Add($Event) | Out-Null
}

function New-FileTransferFileTunaV4DataPathCorrelationIndex {
    param([object[]]$Events = @())

    $payloadEvents = @{}
    $terminalEventsByScope = @{}
    $receiverTransitionsByWriterScope = @{}
    $rejectedReceiveEventsByKey = @{}
    $orderedEvents = @($Events | Sort-Object Sequence, LineNumber)
    $buildVisitCount = 0L

    foreach ($event in @($orderedEvents)) {
        $buildVisitCount++
        # Both index keys use the same range and transfer/frame identity. Extract
        # those once here; lookup helpers retain their existing matching rules.
        $rangeStart = Get-FileTransferFileTunaV4DataPathRangeStart -Event $event
        $rangeEndExclusive = Get-FileTransferFileTunaV4DataPathRangeEndExclusive -Event $event
        $payloadKey = ''
        $rejectedReceiveKey = ''
        if ($rangeStart -ge 0 -and $rangeEndExclusive -gt $rangeStart) {
            $identityParts = New-Object System.Collections.Generic.List[string]
            foreach ($fieldName in @('session_id', 'transfer_id', 'frame_type')) {
                $value = Get-FileTransferEventField -Event $event -Name $fieldName -Default ''
                if ([string]::IsNullOrWhiteSpace($value)) {
                    break
                }
                [void]$identityParts.Add($value.ToLowerInvariant())
            }

            if ($identityParts.Count -eq 3) {
                $rawPayloadBytes = Get-FileTransferEventInt64Field -Event $event -Name 'raw_payload_bytes' -Default 0
                $batchChunkCount = Get-FileTransferEventInt64Field -Event $event -Name 'batch_chunk_count' -Default 0
                if ($rawPayloadBytes -gt 0 -and $batchChunkCount -gt 0) {
                    $payloadKey = [string]::Join('|', [string[]]@(
                        $identityParts[0], $identityParts[1], $identityParts[2],
                        [string]$rangeStart, [string]$rangeEndExclusive,
                        [string]$rawPayloadBytes, [string]$batchChunkCount))
                }

                if ($event.EventName -in @(
                        'filetransfer_data_path_receive_result',
                        'filetransfer_tuna_data_path_mismatch',
                        'filetransfer_data_frame_ignored')) {
                    $writerId = Get-FileTransferEventField -Event $event -Name 'log_writer_id' -Default ''
                    if (-not [string]::IsNullOrWhiteSpace($writerId)) {
                        $rejectedParts = New-Object System.Collections.Generic.List[string]
                        [void]$rejectedParts.Add($writerId.ToLowerInvariant())
                        $rejectedParts.AddRange($identityParts)
                        foreach ($fieldName in @('lane', 'route', 'protocol_version', 'actual_transport', 'target_transport')) {
                            $value = Get-FileTransferEventField -Event $event -Name $fieldName -Default ''
                            if ([string]::IsNullOrWhiteSpace($value)) {
                                break
                            }
                            [void]$rejectedParts.Add($value.ToLowerInvariant())
                        }
                        if ($rejectedParts.Count -eq 9) {
                            [void]$rejectedParts.Add([string]$rangeStart)
                            [void]$rejectedParts.Add([string]$rangeEndExclusive)
                            $rejectedReceiveKey = [string]::Join('|', $rejectedParts)
                        }
                    }
                }
            }
        }
        if (-not [string]::IsNullOrWhiteSpace($payloadKey)) {
            Add-FileTransferFileTunaV4DataPathIndexEvent `
                -Lookup $payloadEvents `
                -Key $payloadKey `
                -Event $event
        }

        if (-not [string]::IsNullOrWhiteSpace($rejectedReceiveKey)) {
            Add-FileTransferFileTunaV4DataPathIndexEvent `
                -Lookup $rejectedReceiveEventsByKey `
                -Key $rejectedReceiveKey `
                -Event $event
        }

        if ($event.EventName -in @(
                'file_transfer_outbound_terminal',
                'file_transfer_inbound_terminal',
                'transfer_terminal',
                'filetransfer_runtime_terminalized')) {
            $scopeKey = Get-FileTransferFileTunaV4DataPathTransferScopeKey -Event $event
            if (-not [string]::IsNullOrWhiteSpace($scopeKey)) {
                Add-FileTransferFileTunaV4DataPathIndexEvent `
                    -Lookup $terminalEventsByScope `
                    -Key $scopeKey `
                    -Event $event
            }
        }

        if ($event.EventName -eq 'filetransfer_route_transitioned') {
            $writerId = Get-FileTransferEventField -Event $event -Name 'log_writer_id' -Default ''
            $scopeKey = Get-FileTransferFileTunaV4DataPathTransferScopeKey -Event $event
            if (-not [string]::IsNullOrWhiteSpace($writerId) -and
                -not [string]::IsNullOrWhiteSpace($scopeKey)) {
                $writerScopeKey = "{0}|{1}" -f $writerId.ToLowerInvariant(), $scopeKey
                Add-FileTransferFileTunaV4DataPathIndexEvent `
                    -Lookup $receiverTransitionsByWriterScope `
                    -Key $writerScopeKey `
                    -Event $event
            }
        }
    }

    return [pscustomobject]@{
        OrderedEvents = $orderedEvents
        PayloadEvents = $payloadEvents
        TerminalEventsByScope = $terminalEventsByScope
        ReceiverTransitionsByWriterScope = $receiverTransitionsByWriterScope
        RejectedReceiveEventsByKey = $rejectedReceiveEventsByKey
        Statistics = [ordered]@{
            build_visit_count = $buildVisitCount
            bounded_lookup_visit_count = 0L
            payload_key_count = $payloadEvents.Count
            rejected_receive_key_count = $rejectedReceiveEventsByKey.Count
        }
    }
}

function Get-FileTransferFileTunaV4DataPathIndexedPayloadEvents {
    param(
        [Parameter(Mandatory = $true)]$Index,
        [Parameter(Mandatory = $true)]$Event
    )

    $key = Get-FileTransferFileTunaV4DataPathPayloadIdentityKey -Event $Event
    if ([string]::IsNullOrWhiteSpace($key) -or
        -not $Index.PayloadEvents.ContainsKey($key)) {
        return @()
    }

    [object[]]$events = $Index.PayloadEvents[$key].ToArray()
    $Index.Statistics.bounded_lookup_visit_count += $events.Count
    return $events
}

function Get-FileTransferFileTunaV4DataPathIndexedTerminalEvents {
    param(
        [Parameter(Mandatory = $true)]$Index,
        [Parameter(Mandatory = $true)]$Event
    )

    $key = Get-FileTransferFileTunaV4DataPathTransferScopeKey -Event $Event
    if ([string]::IsNullOrWhiteSpace($key) -or
        -not $Index.TerminalEventsByScope.ContainsKey($key)) {
        return @()
    }

    [object[]]$events = $Index.TerminalEventsByScope[$key].ToArray()
    $Index.Statistics.bounded_lookup_visit_count += $events.Count
    return $events
}

function Get-FileTransferFileTunaV4DataPathIndexedReceiverTransitions {
    param(
        [Parameter(Mandatory = $true)]$Index,
        [Parameter(Mandatory = $true)]$Event,
        [Parameter(Mandatory = $true)][string]$WriterId
    )

    $scopeKey =
        Get-FileTransferFileTunaV4DataPathTransferScopeKey -Event $Event
    if ([string]::IsNullOrWhiteSpace($scopeKey) -or
        [string]::IsNullOrWhiteSpace($WriterId)) {
        return @()
    }

    $key = "{0}|{1}" -f $WriterId.ToLowerInvariant(), $scopeKey
    if (-not $Index.ReceiverTransitionsByWriterScope.ContainsKey($key)) {
        return @()
    }

    [object[]]$events =
        $Index.ReceiverTransitionsByWriterScope[$key].ToArray()
    $Index.Statistics.bounded_lookup_visit_count += $events.Count
    return $events
}

function Get-FileTransferFileTunaV4DataPathIndexedRejectedReceiveEvents {
    param(
        [Parameter(Mandatory = $true)]$Index,
        [Parameter(Mandatory = $true)]$Event
    )

    $key = Get-FileTransferFileTunaV4RejectedReceiveCorrelationKey -Event $Event
    if ([string]::IsNullOrWhiteSpace($key) -or
        -not $Index.RejectedReceiveEventsByKey.ContainsKey($key)) {
        return @()
    }

    [object[]]$events = $Index.RejectedReceiveEventsByKey[$key].ToArray()
    $Index.Statistics.bounded_lookup_visit_count += $events.Count
    return @($events | Sort-Object {
            Get-FileTransferEventInt64Field `
                -Event $_ `
                -Name 'log_writer_sequence' `
                -Default 0
        })
}

function Test-FileTransferFileTunaV4DataPathSameReceiveTuple {
    param(
        [Parameter(Mandatory = $true)]$Reference,
        [Parameter(Mandatory = $true)]$Candidate
    )

    if ($Candidate.EventName -ne 'filetransfer_data_path_receive_result') {
        return $false
    }

    $referencePayloadKey =
        Get-FileTransferFileTunaV4DataPathPayloadIdentityKey -Event $Reference
    $candidatePayloadKey =
        Get-FileTransferFileTunaV4DataPathPayloadIdentityKey -Event $Candidate
    if ([string]::IsNullOrWhiteSpace($referencePayloadKey) -or
        [string]::IsNullOrWhiteSpace($candidatePayloadKey) -or
        -not [string]::Equals(
            $referencePayloadKey,
            $candidatePayloadKey,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    foreach ($fieldName in @(
            'log_writer_id',
            'direction',
            'route',
            'protocol_version',
            'live_route_epoch',
            'runtime_generation',
            'terminal_generation',
            'leg_generation',
            'route_publication_generation',
            'route_commit_generation',
            'actual_transport',
            'delivery_transport',
            'target_transport')) {
        $referenceValue =
            Get-FileTransferEventField -Event $Reference -Name $fieldName -Default ''
        $candidateValue =
            Get-FileTransferEventField -Event $Candidate -Name $fieldName -Default ''
        if ([string]::IsNullOrWhiteSpace($referenceValue) -or
            [string]::IsNullOrWhiteSpace($candidateValue) -or
            -not [string]::Equals(
                $referenceValue,
                $candidateValue,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    return $true
}

function Test-FileTransferFileTunaV4DataPathIgnoredCompanionMatches {
    param(
        [Parameter(Mandatory = $true)]$Reference,
        [Parameter(Mandatory = $true)]$Candidate
    )

    if ($Candidate.EventName -ne 'filetransfer_data_frame_ignored' -or
        (Get-FileTransferEventField -Event $Candidate -Name 'reason' -Default '') -ne
            'file_tuna_v4_payload_requires_tuna') {
        return $false
    }

    foreach ($fieldName in @('log_writer_id', 'transfer_id', 'session_id', 'frame_type', 'route', 'protocol_version')) {
        $referenceValue =
            Get-FileTransferEventField -Event $Reference -Name $fieldName -Default ''
        $candidateValue =
            Get-FileTransferEventField -Event $Candidate -Name $fieldName -Default ''
        if ([string]::IsNullOrWhiteSpace($referenceValue) -or
            [string]::IsNullOrWhiteSpace($candidateValue) -or
            -not [string]::Equals(
                $referenceValue,
                $candidateValue,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    return (Test-FileTransferFileTunaV4DataPathRangeMatches -Intent $Reference -Actual $Candidate) -and
        (Get-FileTransferEventField -Event $Candidate -Name 'actual_transport' -Default '') -eq 'regular_nkn' -and
        (Get-FileTransferEventField -Event $Candidate -Name 'target_transport' -Default '') -eq 'tuna'
}

function Test-FileTransferFileTunaV4DataPathMismatchCompanionMatches {
    param(
        [Parameter(Mandatory = $true)]$Reference,
        [Parameter(Mandatory = $true)]$Candidate
    )

    if ($Candidate.EventName -ne 'filetransfer_tuna_data_path_mismatch' -or
        (Get-FileTransferEventField -Event $Candidate -Name 'proof_direction' -Default '') -ne 'receive' -or
        (Get-FileTransferEventField -Event $Candidate -Name 'mismatch_reason' -Default '') -ne
            'file_tuna_v4_resolved_to_regular_nkn' -or
        (Get-FileTransferEventField -Event $Candidate -Name 'actual_transport' -Default '') -ne 'regular_nkn' -or
        (Get-FileTransferEventField -Event $Candidate -Name 'delivery_transport' -Default '') -ne 'regular_nkn' -or
        (Get-FileTransferEventField -Event $Candidate -Name 'target_transport' -Default '') -ne 'tuna' -or
        (Get-FileTransferEventInt64Field -Event $Candidate -Name 'raw_payload_bytes' -Default 0) -ne
            (Get-FileTransferEventInt64Field -Event $Reference -Name 'raw_payload_bytes' -Default 0) -or
        (Get-FileTransferEventInt64Field -Event $Candidate -Name 'batch_chunk_count' -Default 0) -ne
            (Get-FileTransferEventInt64Field -Event $Reference -Name 'batch_chunk_count' -Default 0) -or
        -not (Test-FileTransferFileTunaV4DataPathRangeMatches -Intent $Reference -Actual $Candidate)) {
        return $false
    }

    foreach ($fieldName in @(
            'log_writer_id',
            'transfer_id',
            'session_id',
            'frame_type',
            'lane',
            'direction',
            'route',
            'protocol_version',
            'live_route_epoch',
            'runtime_generation',
            'terminal_generation',
            'leg_generation',
            'route_publication_generation',
            'route_commit_generation',
            'target_route',
            'target_protocol_version',
            'target_transport')) {
        $referenceValue =
            Get-FileTransferEventField -Event $Reference -Name $fieldName -Default ''
        $candidateValue =
            Get-FileTransferEventField -Event $Candidate -Name $fieldName -Default ''
        if ([string]::IsNullOrWhiteSpace($referenceValue) -or
            [string]::IsNullOrWhiteSpace($candidateValue) -or
            -not [string]::Equals(
                $referenceValue,
                $candidateValue,
                [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }

    return (Test-FileTransferFileTunaV4DataPathHasRange -Event $Reference) -and
        (Test-FileTransferFileTunaV4DataPathHasRange -Event $Candidate)
}

function Get-FileTransferFileTunaV4DataPathRejectedReceiveChain {
    param(
        [Parameter(Mandatory = $true)]$Reference,
        [Parameter(Mandatory = $true)]$DataPathIndex,
        [switch]$ReturnBeforeIgnore
    )

    [object[]]$chainEvents = @(
        Get-FileTransferFileTunaV4DataPathIndexedRejectedReceiveEvents `
            -Index $DataPathIndex `
            -Event $Reference)
    if ($chainEvents.Count -eq 0) {
        return $null
    }

    $referenceWriterSequence =
        Get-FileTransferEventInt64Field `
            -Event $Reference `
            -Name 'log_writer_sequence' `
            -Default 0
    if ($referenceWriterSequence -le 0) {
        return $null
    }

    $receive = $null
    if ($Reference.EventName -eq 'filetransfer_data_path_receive_result') {
        $receive = $Reference
    }
    elseif ($Reference.EventName -eq 'filetransfer_tuna_data_path_mismatch') {
        [object[]]$precedingReceives = @(
            $chainEvents |
            Where-Object {
                $_.EventName -eq 'filetransfer_data_path_receive_result' -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'log_writer_sequence' -Default 0) -lt
                    $referenceWriterSequence -and
                (Test-FileTransferFileTunaV4DataPathSameReceiveTuple `
                    -Reference $Reference `
                    -Candidate $_)
            } |
            Sort-Object {
                Get-FileTransferEventInt64Field `
                    -Event $_ `
                    -Name 'log_writer_sequence' `
                    -Default 0
            } -Descending)
        if ($precedingReceives.Count -eq 0) {
            return $null
        }

        $receive = $precedingReceives[0]
    }
    else {
        return $null
    }

    $receiveWriterSequence =
        Get-FileTransferEventInt64Field `
            -Event $receive `
            -Name 'log_writer_sequence' `
            -Default 0
    $nextReceiveBoundarySequence = 0L
    foreach ($candidate in @($chainEvents)) {
        $candidateWriterSequence =
            Get-FileTransferEventInt64Field `
                -Event $candidate `
                -Name 'log_writer_sequence' `
                -Default 0
        if ($candidateWriterSequence -le $receiveWriterSequence -or
            -not (Test-FileTransferFileTunaV4DataPathSameReceiveTuple `
                -Reference $receive `
                -Candidate $candidate)) {
            continue
        }

        $nextReceiveBoundarySequence = $candidateWriterSequence
        break
    }

    [object[]]$mismatchEvents = @(
        $chainEvents |
        Where-Object {
            $candidateWriterSequence =
                Get-FileTransferEventInt64Field `
                    -Event $_ `
                    -Name 'log_writer_sequence' `
                    -Default 0
            $candidateWriterSequence -gt $receiveWriterSequence -and
                ($nextReceiveBoundarySequence -le 0 -or
                    $candidateWriterSequence -lt $nextReceiveBoundarySequence) -and
                (Test-FileTransferFileTunaV4DataPathMismatchCompanionMatches `
                    -Reference $receive `
                    -Candidate $_)
        })
    if ($mismatchEvents.Count -ne 1) {
        return $null
    }

    $mismatch = $mismatchEvents[0]
    $mismatchWriterSequence =
        Get-FileTransferEventInt64Field `
            -Event $mismatch `
            -Name 'log_writer_sequence' `
            -Default 0
    if ($Reference.EventName -eq 'filetransfer_tuna_data_path_mismatch' -and
        $mismatchWriterSequence -ne $referenceWriterSequence) {
        return $null
    }

    if ($ReturnBeforeIgnore) {
        return [pscustomobject]@{
            Receive = $receive
            Mismatch = $mismatch
            Ignored = $null
            NextReceiveBoundarySequence = $nextReceiveBoundarySequence
        }
    }

    [object[]]$ignoredEvents = @(
        $chainEvents |
        Where-Object {
            $candidateWriterSequence =
                Get-FileTransferEventInt64Field `
                    -Event $_ `
                    -Name 'log_writer_sequence' `
                    -Default 0
            $candidateWriterSequence -gt $mismatchWriterSequence -and
                ($nextReceiveBoundarySequence -le 0 -or
                    $candidateWriterSequence -lt $nextReceiveBoundarySequence) -and
                (Test-FileTransferFileTunaV4DataPathIgnoredCompanionMatches `
                    -Reference $receive `
                    -Candidate $_)
        })
    if ($ignoredEvents.Count -ne 1) {
        return $null
    }

    $ignored = $ignoredEvents[0]
    [object[]]$terminalBoundaries = @(
        Get-FileTransferFileTunaV4DataPathIndexedTerminalEvents `
            -Index $DataPathIndex `
            -Event $receive |
        Where-Object {
            [int64]$_.Sequence -gt [int64]$mismatch.Sequence -and
            [int64]$_.Sequence -lt [int64]$ignored.Sequence
        })
    if ($terminalBoundaries.Count -gt 0) {
        return $null
    }

    [object[]]$routeBoundaries = @(
        Get-FileTransferFileTunaV4DataPathIndexedReceiverTransitions `
            -Index $DataPathIndex `
            -Event $receive `
            -WriterId (Get-FileTransferEventField -Event $receive -Name 'log_writer_id' -Default '') |
        Where-Object {
            [int64]$_.Sequence -gt [int64]$mismatch.Sequence -and
            [int64]$_.Sequence -lt [int64]$ignored.Sequence
        })
    if ($routeBoundaries.Count -gt 0) {
        return $null
    }

    return [pscustomobject]@{
        Receive = $receive
        Mismatch = $mismatch
        Ignored = $ignored
        NextReceiveBoundarySequence = $nextReceiveBoundarySequence
    }
}

function Get-FileTransferFileTunaV4DataPathMismatchRejectedFailClosedResult {
    param(
        [Parameter(Mandatory = $true)]$Mismatch,
        [Parameter(Mandatory = $true)][object[]]$Events,
        $DataPathIndex = $null
    )

    $result = [pscustomobject]@{
        Recovered = $false
        RejectionChainMatched = $false
        ReceiveDisposition = '(unproven)'
        Reason = 'not_receive_candidate'
        Variant = '(none)'
    }
    if ($null -eq $DataPathIndex) {
        $DataPathIndex =
            New-FileTransferFileTunaV4DataPathCorrelationIndex -Events $Events
    }

    $proofDirection = Get-FileTransferEventField -Event $Mismatch -Name 'proof_direction' -Default ''
    if ($Mismatch.EventName -ne 'filetransfer_data_path_receive_result' -and
        -not ($Mismatch.EventName -eq 'filetransfer_tuna_data_path_mismatch' -and $proofDirection -eq 'receive')) {
        return $result
    }

    $writerId = Get-FileTransferEventField -Event $Mismatch -Name 'log_writer_id' -Default ''
    $writerSequence = Get-FileTransferEventInt64Field -Event $Mismatch -Name 'log_writer_sequence' -Default 0
    if ([string]::IsNullOrWhiteSpace($writerId) -or $writerSequence -le 0) {
        $result.Reason = 'missing_writer_identity'
        return $result
    }

    $rejectedReceiveChain =
        Get-FileTransferFileTunaV4DataPathRejectedReceiveChain `
            -Reference $Mismatch `
            -DataPathIndex $DataPathIndex
    if ($null -eq $rejectedReceiveChain) {
        $result.Reason = 'missing_rejection_companion'
        return $result
    }

    # This records rejection evidence only; recovery still requires every
    # existing source, boundary, and Tuna-payload predicate below.
    $result.RejectionChainMatched = $true
    $result.ReceiveDisposition = 'rejected_before_dispatch'
    $sourceSendMatched = $false
    $sourceSendBeforeTerminal = $false
    $receiverTransitionAmbiguous = $false
    $tunaResendMissing = $false
    $tunaReceiptMissing = $false
    $receive = $rejectedReceiveChain.Receive
    $explicitMismatch = $rejectedReceiveChain.Mismatch
    $ignoredWindowAnchorSequence =
        Get-FileTransferEventInt64Field `
            -Event $explicitMismatch `
            -Name 'log_writer_sequence' `
            -Default 0
    $mismatchSequence =
        if ($null -ne $receive.PSObject.Properties['Sequence']) {
            [int64]$receive.Sequence
        }
        else {
            0L
        }
    [object[]]$ignoredEvents = @($rejectedReceiveChain.Ignored)

    foreach ($ignored in @($ignoredEvents)) {
        $ignoredWriterId = Get-FileTransferEventField -Event $ignored -Name 'log_writer_id' -Default ''
        $ignoredWriterSequence = Get-FileTransferEventInt64Field -Event $ignored -Name 'log_writer_sequence' -Default 0
        if (-not [string]::Equals($writerId, $ignoredWriterId, [System.StringComparison]::OrdinalIgnoreCase) -or
            $ignoredWriterSequence -le $ignoredWindowAnchorSequence) {
            continue
        }

        $mismatchTransferId = Get-FileTransferEventField -Event $Mismatch -Name 'transfer_id' -Default ''
        $mismatchSessionId = Get-FileTransferEventField -Event $Mismatch -Name 'session_id' -Default ''
        $mismatchFrameType = Get-FileTransferEventField -Event $Mismatch -Name 'frame_type' -Default ''
        $mismatchRangeStart = Get-FileTransferFileTunaV4DataPathRangeStart -Event $Mismatch
        $mismatchRangeEndExclusive = Get-FileTransferFileTunaV4DataPathRangeEndExclusive -Event $Mismatch
        $mismatchRawBytes = Get-FileTransferEventInt64Field -Event $Mismatch -Name 'raw_payload_bytes' -Default 0
        $mismatchChunkCount = Get-FileTransferEventInt64Field -Event $Mismatch -Name 'batch_chunk_count' -Default 0
        [object[]]$sourceSends = @(
            Get-FileTransferFileTunaV4DataPathIndexedPayloadEvents `
                -Index $DataPathIndex `
                -Event $Mismatch |
            Where-Object {
                $_.EventName -eq 'filetransfer_data_path_send_result' -and
                $_.Sequence -lt $mismatchSequence -and
                (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'outbound' -and
                (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'regular_nkn_v4_fast' -and
                (Get-FileTransferEventField -Event $_ -Name 'protocol_version' -Default '') -eq '4' -and
                (Get-FileTransferEventField -Event $_ -Name 'actual_transport' -Default '') -eq 'regular_nkn' -and
                (Get-FileTransferEventField -Event $_ -Name 'delivery_transport' -Default '') -eq 'regular_nkn' -and
                (Get-FileTransferEventField -Event $_ -Name 'delivery_result' -Default '') -eq 'sent' -and
                (Get-FileTransferEventField -Event $_ -Name 'target_transport' -Default '') -eq 'regular_nkn' -and
                (Get-FileTransferEventField -Event $_ -Name 'target_route' -Default '') -eq 'regular_nkn_v4_fast' -and
                (Get-FileTransferEventField -Event $_ -Name 'target_protocol_version' -Default '') -eq '4' -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'runtime_generation' -Default 0) -gt 0 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'leg_generation' -Default 0) -gt 0 -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'route_publication_generation' -Default 0) -gt 0 -and
                [string]::Equals((Get-FileTransferEventField -Event $_ -Name 'transfer_id' -Default ''), $mismatchTransferId, [System.StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals((Get-FileTransferEventField -Event $_ -Name 'session_id' -Default ''), $mismatchSessionId, [System.StringComparison]::OrdinalIgnoreCase) -and
                [string]::Equals((Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default ''), $mismatchFrameType, [System.StringComparison]::OrdinalIgnoreCase) -and
                (Get-FileTransferFileTunaV4DataPathRangeStart -Event $_) -eq $mismatchRangeStart -and
                (Get-FileTransferFileTunaV4DataPathRangeEndExclusive -Event $_) -eq $mismatchRangeEndExclusive -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'raw_payload_bytes' -Default 0) -eq $mismatchRawBytes -and
                (Get-FileTransferEventInt64Field -Event $_ -Name 'batch_chunk_count' -Default 0) -eq $mismatchChunkCount
            } | Sort-Object Sequence -Descending)
        foreach ($sourceSend in @($sourceSends)) {
            $sourceWriterId = Get-FileTransferEventField -Event $sourceSend -Name 'log_writer_id' -Default ''
            if ([string]::IsNullOrWhiteSpace($sourceWriterId) -or
                [string]::Equals($sourceWriterId, $writerId, [System.StringComparison]::OrdinalIgnoreCase)) {
                continue
            }

            $sourceRawBytes = Get-FileTransferEventInt64Field -Event $sourceSend -Name 'raw_payload_bytes' -Default 0
            $sourceChunkCount = Get-FileTransferEventInt64Field -Event $sourceSend -Name 'batch_chunk_count' -Default 0
            if ($sourceRawBytes -le 0 -or $mismatchRawBytes -le 0 -or $sourceRawBytes -ne $mismatchRawBytes -or
                $sourceChunkCount -le 0 -or $mismatchChunkCount -le 0 -or $sourceChunkCount -ne $mismatchChunkCount) {
                continue
            }

            $sourceSendMatched = $true
            [object[]]$terminalBoundaries = @(
                Get-FileTransferFileTunaV4DataPathIndexedTerminalEvents `
                    -Index $DataPathIndex `
                    -Event $Mismatch |
                Where-Object {
                    $_.Sequence -gt $sourceSend.Sequence -and
                    $_.Sequence -lt $mismatchSequence -and
                    $_.EventName -in @(
                        'file_transfer_outbound_terminal',
                        'file_transfer_inbound_terminal',
                        'transfer_terminal',
                        'filetransfer_runtime_terminalized') -and
                    [string]::Equals(
                        (Get-FileTransferEventField -Event $_ -Name 'transfer_id' -Default ''),
                        $mismatchTransferId,
                        [System.StringComparison]::OrdinalIgnoreCase) -and
                    [string]::Equals(
                        (Get-FileTransferEventField -Event $_ -Name 'session_id' -Default ''),
                        $mismatchSessionId,
                        [System.StringComparison]::OrdinalIgnoreCase)
                })
            if ($terminalBoundaries.Count -gt 0) {
                continue
            }

            $sourceSendBeforeTerminal = $true
            [object[]]$receiverTransitions = @(
                Get-FileTransferFileTunaV4DataPathIndexedReceiverTransitions `
                    -Index $DataPathIndex `
                    -Event $Mismatch `
                    -WriterId $writerId |
                Where-Object {
                    $_.EventName -eq 'filetransfer_route_transitioned' -and
                    $_.Sequence -gt $sourceSend.Sequence -and
                    $_.Sequence -lt $mismatchSequence -and
                    (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'inbound' -and
                    (Get-FileTransferEventField -Event $_ -Name 'previous_route' -Default '') -in @(
                        'regular_nkn_v4_fast',
                        'post_tuna_fallback_v6') -and
                    (Get-FileTransferEventField -Event $_ -Name 'new_route' -Default '') -eq 'file_tuna_v4' -and
                    [string]::Equals(
                        (Get-FileTransferEventField -Event $_ -Name 'log_writer_id' -Default ''),
                        $writerId,
                        [System.StringComparison]::OrdinalIgnoreCase)
                } | Sort-Object Sequence -Descending)
            $matchingReceiverTransitionCount = 0
            $matchingReceiverScopeCount = 0
            $missingTunaResendCount = 0
            $missingTunaReceiptCount = 0
            foreach ($receiverTransition in @($receiverTransitions)) {
                $transitionMatches = $true
                foreach ($fieldName in @(
                        'transfer_id',
                        'session_id',
                        'route',
                        'protocol_version',
                        'live_route_epoch',
                        'runtime_generation',
                        'terminal_generation',
                        'leg_generation',
                        'route_publication_generation',
                        'route_commit_generation')) {
                    $transitionValue = Get-FileTransferEventField -Event $receiverTransition -Name $fieldName -Default ''
                    $mismatchValue = Get-FileTransferEventField -Event $Mismatch -Name $fieldName -Default ''
                    if ([string]::IsNullOrWhiteSpace($transitionValue) -or
                        [string]::IsNullOrWhiteSpace($mismatchValue) -or
                        -not [string]::Equals($transitionValue, $mismatchValue, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $transitionMatches = $false
                        break
                    }
                }

                if (-not $transitionMatches) {
                    continue
                }

                $matchingReceiverScopeCount++
                $previousRoute = Get-FileTransferEventField `
                    -Event $receiverTransition `
                    -Name 'previous_route' `
                    -Default ''
                if ($previousRoute -eq 'post_tuna_fallback_v6') {
                    $sourceWriterSequence = Get-FileTransferEventInt64Field -Event $sourceSend -Name 'log_writer_sequence' -Default 0
                    $transitionWriterSequence = Get-FileTransferEventInt64Field -Event $receiverTransition -Name 'log_writer_sequence' -Default 0
                    $receiveWriterSequence = Get-FileTransferEventInt64Field -Event $receive -Name 'log_writer_sequence' -Default 0
                    [object[]]$tunaResends = @(
                        Get-FileTransferFileTunaV4DataPathIndexedPayloadEvents `
                            -Index $DataPathIndex `
                            -Event $Mismatch |
                        Where-Object {
                            $_.Sequence -gt $sourceSend.Sequence -and
                            $_.Sequence -lt $mismatchSequence -and
                            $_.EventName -eq 'filetransfer_data_path_send_result' -and
                            $sourceWriterSequence -gt 0 -and
                            [string]::Equals(
                                (Get-FileTransferEventField -Event $_ -Name 'log_writer_id' -Default ''),
                                $sourceWriterId, [System.StringComparison]::OrdinalIgnoreCase) -and
                            (Get-FileTransferEventInt64Field -Event $_ -Name 'log_writer_sequence' -Default 0) -gt $sourceWriterSequence -and
                            (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'outbound' -and
                            (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'file_tuna_v4' -and
                            (Get-FileTransferEventField -Event $_ -Name 'protocol_version' -Default '') -eq '4' -and
                            (Get-FileTransferEventField -Event $_ -Name 'actual_transport' -Default '') -eq 'tuna' -and
                            (Get-FileTransferEventField -Event $_ -Name 'delivery_transport' -Default '') -eq 'tuna' -and
                            (Get-FileTransferEventField -Event $_ -Name 'delivery_result' -Default '') -eq 'sent' -and
                            (Get-FileTransferEventInt64Field -Event $_ -Name 'raw_payload_bytes' -Default 0) -eq $mismatchRawBytes -and
                            (Get-FileTransferEventInt64Field -Event $_ -Name 'batch_chunk_count' -Default 0) -eq $mismatchChunkCount -and
                            (Test-FileTransferFileTunaV4DataPathRangeMatches -Intent $Mismatch -Actual $_)
                        })
                    [object[]]$tunaReceipts = @(
                        Get-FileTransferFileTunaV4DataPathIndexedPayloadEvents `
                            -Index $DataPathIndex `
                            -Event $Mismatch |
                        Where-Object {
                            $_.Sequence -gt $sourceSend.Sequence -and
                            $_.Sequence -gt $receiverTransition.Sequence -and
                            $_.Sequence -lt $mismatchSequence -and
                            $_.EventName -eq 'filetransfer_data_path_receive_result' -and
                            $transitionWriterSequence -gt 0 -and
                            [string]::Equals(
                                (Get-FileTransferEventField -Event $_ -Name 'log_writer_id' -Default ''),
                                $writerId, [System.StringComparison]::OrdinalIgnoreCase) -and
                            (Get-FileTransferEventInt64Field -Event $_ -Name 'log_writer_sequence' -Default 0) -gt $transitionWriterSequence -and
                            (Get-FileTransferEventInt64Field -Event $_ -Name 'log_writer_sequence' -Default 0) -lt $receiveWriterSequence -and
                            (Get-FileTransferEventField -Event $_ -Name 'direction' -Default '') -eq 'inbound' -and
                            (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'file_tuna_v4' -and
                            (Get-FileTransferEventField -Event $_ -Name 'protocol_version' -Default '') -eq '4' -and
                            (Get-FileTransferEventField -Event $_ -Name 'actual_transport' -Default '') -eq 'tuna' -and
                            (Get-FileTransferEventField -Event $_ -Name 'delivery_transport' -Default '') -eq 'tuna' -and
                            (Get-FileTransferEventField -Event $_ -Name 'target_route' -Default '') -eq 'file_tuna_v4' -and
                            (Get-FileTransferEventField -Event $_ -Name 'target_protocol_version' -Default '') -eq '4' -and
                            (Get-FileTransferEventField -Event $_ -Name 'target_transport' -Default '') -eq 'tuna' -and
                            (Get-FileTransferEventInt64Field -Event $_ -Name 'raw_payload_bytes' -Default 0) -eq $mismatchRawBytes -and
                            (Get-FileTransferEventInt64Field -Event $_ -Name 'batch_chunk_count' -Default 0) -eq $mismatchChunkCount -and
                            (Test-FileTransferFileTunaV4DataPathRangeMatches -Intent $Mismatch -Actual $_)
                        } | Where-Object {
                            # These are receiver-local bindings, not a comparison
                            # with the sender's independently numbered authority.
                            foreach ($fieldName in @('live_route_epoch', 'runtime_generation',
                                    'terminal_generation', 'leg_generation', 'route_publication_generation',
                                    'route_commit_generation', 'target_route', 'target_protocol_version', 'target_transport')) {
                                $receiptValue = Get-FileTransferEventField -Event $_ -Name $fieldName -Default ''
                                $transitionValue = Get-FileTransferEventField -Event $receiverTransition -Name $fieldName -Default ''
                                if ([string]::IsNullOrWhiteSpace($receiptValue) -or
                                    -not [string]::Equals($receiptValue, $transitionValue, [System.StringComparison]::OrdinalIgnoreCase)) {
                                    return $false
                                }
                            }
                            return $true
                        })
                    if ($tunaResends.Count -eq 0 -or $tunaReceipts.Count -eq 0) {
                        if ($tunaResends.Count -eq 0) {
                            $missingTunaResendCount++
                        }
                        else {
                            $missingTunaReceiptCount++
                        }
                        continue
                    }
                }

                $matchingReceiverTransitionCount++
            }
            if ($matchingReceiverTransitionCount -eq 1) {
                $result.Recovered = $true
                $result.Reason = 'recovered'
                return $result
            }
            if ($matchingReceiverScopeCount -eq 1) {
                $tunaResendMissing = $tunaResendMissing -or $missingTunaResendCount -eq 1
                $tunaReceiptMissing = $tunaReceiptMissing -or $missingTunaReceiptCount -eq 1
            }
            elseif ($matchingReceiverScopeCount -gt 1) {
                $receiverTransitionAmbiguous = $true
            }
        }
    }

    $result.Reason = if ($tunaReceiptMissing) {
        $result.Variant = 'second_tuna_rejected_regular_ingress_exact_tuna_receive_missing'
        'missing_exact_tuna_receive'
    }
    elseif ($tunaResendMissing) {
        $result.Variant = 'second_tuna_rejected_regular_ingress_exact_tuna_resend_missing'
        'missing_tuna_resend'
    }
    elseif ($receiverTransitionAmbiguous) { 'ambiguous_receiver_route_transition' }
    elseif ($sourceSendBeforeTerminal) { 'missing_receiver_route_transition' }
    elseif ($sourceSendMatched) { 'terminal_boundary' }
    else { 'missing_earlier_source_send' }
    $explanation = Get-FileTransferRejectedIngressExplanation -Recovered $result.Recovered `
        -RejectionChainMatched $result.RejectionChainMatched -ReceiveDisposition $result.ReceiveDisposition `
        -Reason $result.Reason -Variant $result.Variant
    if ($null -ne $explanation) {
        $result | Add-Member -NotePropertyName Explanation -NotePropertyValue $explanation
    }
    return $result
}

function Test-FileTransferFileTunaV4DataPathMismatchRejectedFailClosed {
    param(
        [Parameter(Mandatory = $true)]$Mismatch,
        [Parameter(Mandatory = $true)][object[]]$Events,
        $DataPathIndex = $null
    )

    $result = Get-FileTransferFileTunaV4DataPathMismatchRejectedFailClosedResult `
        -Mismatch $Mismatch -Events $Events -DataPathIndex $DataPathIndex
    return [bool]$result.Recovered
}

function Get-FileTransferFileTunaV4DataPathRangeStart {
    param([Parameter(Mandatory = $true)]$Event)

    $start = Get-FileTransferEventInt64Field -Event $Event -Name 'start_chunk_index' -Default -1
    if ($start -ge 0) {
        return $start
    }

    $start = Get-FileTransferEventInt64Field -Event $Event -Name 'first_start_chunk_index' -Default -1
    if ($start -ge 0) {
        return $start
    }

    $chunkIndex = Get-FileTransferEventField -Event $Event -Name 'chunk_index' -Default ''
    if ($chunkIndex -match '^(\d+)(?:-(\d+))?$') {
        return [int64]$Matches[1]
    }

    return -1
}

function Get-FileTransferFileTunaV4DataPathRangeEndExclusive {
    param([Parameter(Mandatory = $true)]$Event)

    $endExclusive = Get-FileTransferEventInt64Field -Event $Event -Name 'last_end_chunk_exclusive' -Default -1
    if ($endExclusive -gt 0) {
        return $endExclusive
    }

    $start = Get-FileTransferFileTunaV4DataPathRangeStart -Event $Event
    if ($start -lt 0) {
        return -1
    }

    $chunkIndex = Get-FileTransferEventField -Event $Event -Name 'chunk_index' -Default ''
    if ($chunkIndex -match '^\d+-(\d+)$') {
        return [int64]$Matches[1] + 1
    }

    $count = Get-FileTransferEventInt64Field -Event $Event -Name 'batch_chunk_count' -Default 0
    if ($count -le 0) {
        $count = Get-FileTransferEventInt64Field -Event $Event -Name 'sent_chunk_count' -Default 0
    }
    if ($count -le 0) {
        $count = Get-FileTransferEventInt64Field -Event $Event -Name 'frame_chunk_count' -Default 0
    }

    if ($count -le 0) {
        return -1
    }

    return $start + $count
}

function Test-FileTransferFileTunaV4DataPathHasRange {
    param([Parameter(Mandatory = $true)]$Event)

    $start = Get-FileTransferFileTunaV4DataPathRangeStart -Event $Event
    $endExclusive = Get-FileTransferFileTunaV4DataPathRangeEndExclusive -Event $Event
    return $start -ge 0 -and $endExclusive -gt $start
}

function Test-FileTransferFileTunaV4DataPathRangeMatches {
    param(
        [Parameter(Mandatory = $true)]$Intent,
        [Parameter(Mandatory = $true)]$Actual
    )

    if (-not (Test-FileTransferFileTunaV4DataPathHasRange -Event $Intent) -or
        -not (Test-FileTransferFileTunaV4DataPathHasRange -Event $Actual)) {
        return $true
    }

    $intentStart = Get-FileTransferFileTunaV4DataPathRangeStart -Event $Intent
    $intentEndExclusive = Get-FileTransferFileTunaV4DataPathRangeEndExclusive -Event $Intent
    $actualStart = Get-FileTransferFileTunaV4DataPathRangeStart -Event $Actual
    $actualEndExclusive = Get-FileTransferFileTunaV4DataPathRangeEndExclusive -Event $Actual

    if ($Intent.EventName -eq 'filetransfer_v4_repair_sent') {
        return $actualStart -ge $intentStart -and
            $actualEndExclusive -le $intentEndExclusive
    }

    return $actualStart -eq $intentStart -and
        $actualEndExclusive -eq $intentEndExclusive
}
