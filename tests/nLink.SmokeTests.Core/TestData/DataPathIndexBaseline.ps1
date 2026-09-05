# Frozen pre-extraction-reuse data-path index; only the function name differs from the original.
function Invoke-DataPathIndexBaseline {
    param([object[]]$Events = @())

    $payloadEvents = @{}
    $terminalEventsByScope = @{}
    $receiverTransitionsByWriterScope = @{}
    $rejectedReceiveEventsByKey = @{}
    $orderedEvents = @($Events | Sort-Object Sequence, LineNumber)
    $buildVisitCount = 0L

    foreach ($event in @($orderedEvents)) {
        $buildVisitCount++
        $payloadKey =
            Get-FileTransferFileTunaV4DataPathPayloadIdentityKey -Event $event
        if (-not [string]::IsNullOrWhiteSpace($payloadKey)) {
            Add-FileTransferFileTunaV4DataPathIndexEvent `
                -Lookup $payloadEvents `
                -Key $payloadKey `
                -Event $event
        }

        $rejectedReceiveKey =
            Get-FileTransferFileTunaV4RejectedReceiveCorrelationKey -Event $event
        if (-not [string]::IsNullOrWhiteSpace($rejectedReceiveKey)) {
            Add-FileTransferFileTunaV4DataPathIndexEvent `
                -Lookup $rejectedReceiveEventsByKey `
                -Key $rejectedReceiveKey `
                -Event $event
        }

        $writerId =
            Get-FileTransferEventField -Event $event -Name 'log_writer_id' -Default ''
        $scopeKey =
            Get-FileTransferFileTunaV4DataPathTransferScopeKey -Event $event
        if ($event.EventName -in @(
                'file_transfer_outbound_terminal',
                'file_transfer_inbound_terminal',
                'transfer_terminal',
                'filetransfer_runtime_terminalized') -and
            -not [string]::IsNullOrWhiteSpace($scopeKey)) {
            Add-FileTransferFileTunaV4DataPathIndexEvent `
                -Lookup $terminalEventsByScope `
                -Key $scopeKey `
                -Event $event
        }

        if ($event.EventName -eq 'filetransfer_route_transitioned' -and
            -not [string]::IsNullOrWhiteSpace($writerId) -and
            -not [string]::IsNullOrWhiteSpace($scopeKey)) {
            $writerScopeKey =
                "{0}|{1}" -f $writerId.ToLowerInvariant(), $scopeKey
            Add-FileTransferFileTunaV4DataPathIndexEvent `
                -Lookup $receiverTransitionsByWriterScope `
                -Key $writerScopeKey `
                -Event $event
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
