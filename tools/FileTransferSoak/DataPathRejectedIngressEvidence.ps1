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

function Get-FileTransferTunaRouteCycleAcceptance {
    param([object[]]$Events, $MismatchProof, $SnapshotEvaluation, $DataPathIndex = $null, $ComparisonEvaluation = $null)

    # The canonical route-cycle policy separates stale ingress safety from
    # delivery. Legacy exact-redelivery decisions remain unchanged.
    $publications = New-FileTransferPublicationEvidenceIndex $SnapshotEvaluation
    $owners = @{}
    $security = @{}
    $uses = @{}
    $sourceByWriterSequence = @{}
    foreach ($event in $Events) {
        $writer = Get-FileTransferEventField $event 'log_writer_id' ''
        $sequence = Get-FileTransferEventField $event 'log_writer_sequence' ''
        $sourceByWriterSequence["$writer|$sequence"] = $event
        if ($event.EventName -in @('filetransfer_security_context_installed', 'filetransfer_security_context_retired')) {
            $key = "$writer|$(Get-FileTransferEventField $event 'security_transport_id' '')"
            if (-not $security.ContainsKey($key)) { $security[$key] = @() }
            $security[$key] += $event
        }
    }
    foreach ($items in $publications.Boundaries.Values) {
        foreach ($boundary in $items) {
            $key = "$($boundary.coordinatorInstanceId)|$($boundary.writer)|$($boundary.direction)|$($boundary.sessionId)|$($boundary.transferId)"
            if (-not $owners.ContainsKey($key)) { $owners[$key] = @() }
            $owners[$key] += $boundary
        }
    }
    $routeFields = @{route='route'; protocol_version='protocolVersion'; live_route_epoch='liveRouteEpoch';
        runtime_generation='runtimeGeneration'; terminal_generation='terminalGeneration'; leg_generation='legGeneration';
        route_publication_generation='routePublicationGeneration'; route_commit_generation='routeCommitGeneration'}
    function BoundUse($Event, [string]$Kind) {
        $cacheKey = "$($Event.Sequence)|$Kind"
        if ($uses.ContainsKey($cacheKey)) { return $uses[$cacheKey] }
        $uses[$cacheKey] = $null
        $f = ConvertFrom-GuiSmokeLogLedgerStructuredFields $Event.RawLine
        if ($f.ContainsKey('__nlink_duplicate_structured_field') -or $f['publication_use_kind'] -cne $Kind -or
            $f['security_context_status'] -cne 'captured' -or $f['frame_type'] -cne 'filetransfer.chunk_batch.v4') { return $null }
        if ($f['target_route'] -cne $f['route'] -or $f['target_protocol_version'] -cne $f['protocol_version'] -or
            $f['target_transport'] -cne $(if ($f['route'] -ceq 'file_tuna_v4') {'tuna'} else {'regular_nkn'})) { return $null }
        if ($Kind -ceq 'v4_physical_submission' -and
            ($f['event'] -cne 'filetransfer_data_path_send_result' -or $f['direction'] -cne 'outbound')) { return $null }
        if ($Kind -ceq 'receive_guard' -and ($f['direction'] -cne 'inbound' -or $f['route'] -cne 'file_tuna_v4' -or
            $f['receive_disposition'] -cne 'rejected_before_dispatch' -or $f['authority_effect'] -cne 'none')) { return $null }
        if ($Kind -ceq 'receive_dispatch' -and ($f['event'] -cne 'filetransfer_data_path_receive_result' -or
            $f['direction'] -cne 'inbound' -or $f['receive_disposition'] -cne 'dispatch_entered')) { return $null }
        $shapes = @{security_transport_id='ftst1-[0-9a-f]{32}'; security_installation_id='ftsi1-[0-9a-f]{32}';
            security_establishment_fingerprint='ftest1-[0-9a-f]{64}'; security_payload_fingerprint='ftpay1-[0-9a-f]{64}';
            data_path_envelope_fingerprint='ftenv1-[0-9a-f]{64}'}
        foreach ($name in $shapes.Keys) {
            if ([string]$f[$name] -cnotmatch ('\A'+$shapes[$name]+'\z')) { return $null }
        }
        foreach ($name in @('log_writer_sequence','security_installation_generation','security_sequence')) {
            $number = 0L
            if ([string]$f[$name] -cnotmatch '\A[1-9][0-9]*\z' -or
                -not [long]::TryParse([string]$f[$name], [ref]$number)) { return $null }
        }
        foreach ($scope in @('session','transfer')) {
            $id = [string]$f["publication_evidence_${scope}_id"]
            if ($id -cnotmatch '\Afte1-[0-9a-f]{24}\z' -or $f["security_${scope}_id"] -cne $id -or
                $f["data_path_${scope}_id"] -cne $id) { return $null }
        }
        $strictEvent = [pscustomobject]@{Fields=$f}
        $reference = Resolve-FileTransferPublicationEvidence -Index $publications -Event $strictEvent
        if ($null -eq $reference) { return $null }
        $boundary = $publications.Boundaries["$($reference.CoordinatorId)|$($f['publication_evidence_boundary_id'])"][0]
        if (@($boundary.operations | Where-Object { $_.operationKey -ceq $f['publication_evidence_operation_key'] -and $_.stateApplied }).Count -ne 1) { return $null }
        $useSequence = [long]$f['log_writer_sequence']
        if ([long]$boundary.writerSequence -ge $useSequence) { return $null }
        $stateKey = "$($reference.CoordinatorId)|$($f['publication_evidence_observed_state_generation'])"
        if (@($publications.States[$stateKey].afterStateHash | Sort-Object -Unique).Count -ne 1) { return $null }
        $ownerKey = "$($boundary.coordinatorInstanceId)|$($boundary.writer)|$($boundary.direction)|$($boundary.sessionId)|$($boundary.transferId)"
        foreach ($later in $owners[$ownerKey]) {
            if ([long]$later.writerSequence -le [long]$boundary.writerSequence -or [long]$later.writerSequence -ge $useSequence) { continue }
            foreach ($name in $routeFields.Keys) {
                if ([string]$later.afterBoundary.($routeFields[$name]) -cne [string]$f[$name]) { return $null }
            }
        }
        $securityKey = "$($f['log_writer_id'])|$($f['security_transport_id'])"
        if (-not $security.ContainsKey($securityKey)) { return $null }
        $lifecycle = @($security[$securityKey] | Where-Object {
            (Get-FileTransferEventField $_ 'security_installation_id' '') -ceq $f['security_installation_id']
        })
        $install = @($lifecycle | Where-Object EventName -CEQ 'filetransfer_security_context_installed')
        $retire = @($lifecycle | Where-Object EventName -CEQ 'filetransfer_security_context_retired')
        if ($install.Count -ne 1 -or $retire.Count -ne 1) { return $null }
        foreach ($row in $lifecycle) {
            $fields = ConvertFrom-GuiSmokeLogLedgerStructuredFields $row.RawLine
            if ($fields.ContainsKey('__nlink_duplicate_structured_field')) { return $null }
            foreach ($name in @('log_writer_id','security_transport_id','security_installation_id',
                    'security_installation_generation','security_establishment_fingerprint','security_session_id')) {
                if ([string]$fields[$name] -cne [string]$f[$name]) { return $null }
            }
        }
        $start = Get-FileTransferEventInt64Field $install[0] 'log_writer_sequence' 0
        $end = Get-FileTransferEventInt64Field $retire[0] 'log_writer_sequence' 0
        if ($start -le 0 -or $start -ge $useSequence -or $end -le $useSequence -or
            (Get-FileTransferEventField $install[0] 'security_context_status' '') -cne 'captured' -or
            (Get-FileTransferEventField $retire[0] 'security_context_status' '') -cne 'retired') { return $null }
        foreach ($other in $security[$securityKey]) {
            if ((Get-FileTransferEventField $other 'security_installation_id' '') -ceq $f['security_installation_id']) { continue }
            $sequence = Get-FileTransferEventInt64Field $other 'log_writer_sequence' 0
            if ($sequence -ge $start -and $sequence -le $end) { return $null }
            # An older unretired context must not enclose the selected span.
            if ($other.EventName -ceq 'filetransfer_security_context_installed' -and $sequence -lt $start) {
                $mates = @($security[$securityKey] | Where-Object {
                    $_.EventName -ceq 'filetransfer_security_context_retired' -and
                    (Get-FileTransferEventField $_ 'security_installation_id' '') -ceq
                        (Get-FileTransferEventField $other 'security_installation_id' '')
                })
                if ($mates.Count -ne 1 -or (Get-FileTransferEventInt64Field $mates[0] 'log_writer_sequence' 0) -ge $start) { return $null }
            }
        }
        if ($Event.EventName -cne 'filetransfer_data_frame_ignored' -and
            [string]::IsNullOrWhiteSpace((Get-FileTransferFileTunaV4DataPathPayloadIdentityKey $strictEvent))) { return $null }
        $uses[$cacheKey] = [pscustomobject]@{Event=$Event;Fields=$f;Reference=$reference;Boundary=$boundary}
        return $uses[$cacheKey]
    }
    function SameEnvelope($Left,$Right) {
        $names = @('frame_type','data_path_envelope_fingerprint','security_payload_fingerprint','security_sequence',
            'security_establishment_fingerprint','data_path_session_id','data_path_transfer_id','security_session_id','security_transfer_id')
        # The ignored companion has only chunk_index, not repeated payload
        # statistics. Its exact fingerprint and existing chain bind the payload.
        if ($Right.Event.EventName -ceq 'filetransfer_data_frame_ignored') { $names += 'chunk_index' }
        else { $names += @('start_chunk_index','last_end_chunk_exclusive','raw_payload_bytes','batch_chunk_count','frame_chunk_count') }
        foreach ($name in $names) {
            if ([string]::IsNullOrWhiteSpace([string]$Left.Fields[$name]) -or
                [string]$Left.Fields[$name] -cne [string]$Right.Fields[$name]) { return $false }
        }
        return $true
    }
    function TunaPublications($OwnerKey) {
        @(foreach ($b in @($owners[$OwnerKey] | Sort-Object { [long]$_.writerSequence })) {
            foreach ($op in $b.operations) {
                if ($op.family -ceq 'route_runtime' -and $op.operationRole -ceq 'route_publication' -and
                    $op.afterState -ceq 'Published' -and $op.stateApplied -and $op.receiptValidated -and
                    $op.receiptStatus -ceq 'Accepted' -and $op.routeRuntime.boundary.route -ceq 'file_tuna_v4' -and
                    $op.routeRuntime.boundary.protocolVersion -eq 4) { $b }
            }
        })
    }
    $senderOwners = @($owners.Keys | Where-Object { $owners[$_][0].direction -ceq 'outbound' })
    $receiverOwners = @($owners.Keys | Where-Object { $owners[$_][0].direction -ceq 'inbound' })
    $sendPublications = @(); $receivePublications = @()
    if ($senderOwners.Count -eq 1 -and $receiverOwners.Count -eq 1) {
        $sendPublications = @(TunaPublications $senderOwners[0])
        $receivePublications = @(TunaPublications $receiverOwners[0])
    }
    $index = $DataPathIndex
    if ($null -eq $index) { $index = New-FileTransferFileTunaV4DataPathCorrelationIndex -Events $Events }
    $blocking = New-Object 'System.Collections.Generic.List[object]'
    $candidateReports = @(foreach ($record in @($MismatchProof.CandidateRecords | Sort-Object { $_.Event.Sequence })) {
        $event = $record.Event
        $chain = Get-FileTransferFileTunaV4DataPathRejectedReceiveChain -Reference $event -DataPathIndex $index
        $safe = $false
        if ($null -ne $chain) {
            $receive = BoundUse $chain.Receive 'receive_guard'
            $mismatch = BoundUse $chain.Mismatch 'receive_guard'
            $ignored = BoundUse $chain.Ignored[0] 'receive_guard'
            if ($null -ne $receive -and $null -ne $mismatch -and $null -ne $ignored -and
                (SameEnvelope $receive $mismatch) -and (SameEnvelope $receive $ignored)) {
                $sourceSends = @(Get-FileTransferFileTunaV4DataPathIndexedPayloadEvents -Index $index -Event $chain.Receive | Where-Object {
                    $_.EventName -ceq 'filetransfer_data_path_send_result' -and
                    (Get-FileTransferEventField $_ 'data_path_envelope_fingerprint' '') -ceq $receive.Fields['data_path_envelope_fingerprint']
                })
                if ($sourceSends.Count -eq 1) {
                    $source = BoundUse $sourceSends[0] 'v4_physical_submission'
                    if ($null -ne $source -and (SameEnvelope $source $receive) -and
                        $source.Fields['route'] -ceq 'regular_nkn_v4_fast' -and $source.Fields['protocol_version'] -ceq '4' -and
                        $source.Fields['actual_transport'] -ceq 'regular_nkn' -and $source.Fields['delivery_transport'] -ceq 'regular_nkn' -and
                        $source.Fields['delivery_result'] -ceq 'sent' -and $source.Fields['direction'] -ceq 'outbound' -and
                        $source.Fields['log_writer_id'] -cne $receive.Fields['log_writer_id'] -and
                        $sendPublications.Count -ge 1 -and
                        $source.Reference.CoordinatorId -ceq $sendPublications[0].coordinatorInstanceId -and
                        [long]$source.Fields['log_writer_sequence'] -lt [long]$sendPublications[0].writerSequence) { $safe = $true }
                }
            }
        }
        if (-not $safe) {
            $class = if ((Get-FileTransferEventField $event 'direction' '') -ceq 'outbound' -or
                (Get-FileTransferEventField $event 'receive_disposition' '') -ceq 'dispatch_entered') {
                'file_tuna_data_path_mismatch'
            } else { 'filetransfer_authenticated_stale_rejection_unproven' }
            $blocking.Add([pscustomobject]@{Class=$class;Event=$event;Reason='authenticated_stale_rejection_unproven';Variant='authenticated_stale_rejection_unproven'}) | Out-Null
        }
        [pscustomobject]@{source_path=$event.FilePath;source_line=$event.LineNumber;source_sequence=$event.Sequence;
            classification=$(if ($safe) {'authenticated_stale_rejected'} else {'blocking_mismatch'});legacy_recovered=[bool]$record.Recovered}
    })
    $deliveryFailures = New-Object 'System.Collections.Generic.List[object]'
    $legs = @(foreach ($activation in @(0,1)) {
        $send = $null; $receive = $null
        if ($sendPublications.Count -eq 2 -and $receivePublications.Count -eq 2) {
            $sb = $sendPublications[$activation]; $rb = $receivePublications[$activation]
            foreach ($event in $Events) {
                if ($event.EventName -cne 'filetransfer_data_path_send_result' -or
                    (Get-FileTransferEventField $event 'publication_evidence_boundary_id' '') -cne $sb.boundaryId -or
                    (Get-FileTransferEventField $event 'actual_transport' '') -cne 'tuna') { continue }
                $s = BoundUse $event 'v4_physical_submission'
                if ($null -eq $s -or $s.Fields['delivery_result'] -cne 'sent' -or $s.Fields['delivery_transport'] -cne 'tuna') { continue }
                $matches = @(Get-FileTransferFileTunaV4DataPathIndexedPayloadEvents -Index $index -Event $event | Where-Object {
                    $_.EventName -ceq 'filetransfer_data_path_receive_result' -and
                    (Get-FileTransferEventField $_ 'data_path_envelope_fingerprint' '') -ceq $s.Fields['data_path_envelope_fingerprint']
                })
                if ($matches.Count -ne 1) { continue }
                $r = BoundUse $matches[0] 'receive_dispatch'
                if ($null -ne $r -and (SameEnvelope $s $r) -and $r.Fields['actual_transport'] -ceq 'tuna' -and
                    $r.Fields['delivery_transport'] -ceq 'tuna' -and $r.Fields['delivery_result'] -ceq 'received' -and
                    $r.Fields['receive_disposition'] -ceq 'dispatch_entered' -and $r.Boundary.boundaryId -ceq $rb.boundaryId -and
                    $s.Reference.CoordinatorId -cne $r.Reference.CoordinatorId) { $send=$s.Event; $receive=$r.Event; break }
            }
        }
        if ($null -eq $receive) {
            $anchor = $null
            if ($sendPublications.Count -gt $activation) {
                $b = $sendPublications[$activation]
                $key = "$($b.writer)|$($b.writerSequence)"
                if ($sourceByWriterSequence.ContainsKey($key)) { $anchor = $sourceByWriterSequence[$key] }
            }
            if ($null -eq $anchor -and $Events.Count -gt 0) { $anchor = $Events[0] }
            $deliveryFailures.Add([pscustomobject]@{Class='filetransfer_tuna_activation_delivery_unproven';Event=$anchor;
                Reason=('tuna_activation_{0}_matched_delivery_unproven' -f ($activation+1));Variant=('activation_{0}' -f ($activation+1))}) | Out-Null
        }
        [pscustomobject]@{activation=$activation+1;verdict=$(if ($null -ne $receive) {'pass'} else {'unproven'});
            send_source_line=$(if ($null -ne $send) {$send.LineNumber} else {$null});
            receive_source_line=$(if ($null -ne $receive) {$receive.LineNumber} else {$null})}
    })
    $deliveryPass = @($legs | Where-Object verdict -CNE 'pass').Count -eq 0
    $result = [pscustomobject]@{
        BlockingCandidateRecords=@($blocking.ToArray())
        DeliveryFailureRecords=@($deliveryFailures.ToArray())
        Report=[pscustomobject]@{contract='authenticated_stale_rejection_and_tuna_activation_delivery_v1';
            verdict=$(if ($blocking.Count -eq 0 -and $deliveryPass) {'pass'} else {'fail'});
            candidate_count=$candidateReports.Count;safe_rejected_count=$candidateReports.Count-$blocking.Count;blocking_count=$blocking.Count;
            delivery_verdict=$(if ($deliveryPass) {'pass'} else {'unproven'});activations=$legs;candidates=$candidateReports}
    }
    if ($null -ne $ComparisonEvaluation) {
        $other = Get-FileTransferTunaRouteCycleAcceptance -Events $Events -MismatchProof $MismatchProof `
            -SnapshotEvaluation $ComparisonEvaluation -DataPathIndex $index
        $same = ($result.Report | ConvertTo-Json -Depth 8 -Compress) -ceq ($other.Report | ConvertTo-Json -Depth 8 -Compress)
        $result.Report | Add-Member -NotePropertyName full_compact_parity_verdict -NotePropertyValue $(if ($same) {'pass'} else {'fail'})
        if (-not $same) {
            $result.Report.verdict = 'fail'
            $result.DeliveryFailureRecords += [pscustomobject]@{Class='filetransfer_tuna_route_cycle_acceptance_parity_unproven';
                Event=$(if ($Events.Count -gt 0) {$Events[0]} else {$null});Reason='tuna_route_cycle_acceptance_full_compact_parity_failed';Variant='parity'}
        }
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
