Set-StrictMode -Version Latest

function Add-FileTransferGateFinding {
    param(
        [Parameter(Mandatory = $true)]
        [AllowEmptyCollection()]
        [System.Collections.Generic.List[string]]$List,
        [Parameter(Mandatory = $true)][string]$Finding
    )

    if (-not [string]::IsNullOrWhiteSpace($Finding)) {
        $List.Add($Finding) | Out-Null
    }
}

function Test-FileTransferTerminalCompleted {
    param([object[]]$TerminalEvents)

    foreach ($event in @($TerminalEvents)) {
        $state = Get-FileTransferEventField -Event $event -Name 'state' -Default ''
        $errorCode = Get-FileTransferEventField -Event $event -Name 'error_code' -Default '(none)'
        if ($state -ne 'Completed' -or $errorCode -ne '(none)') {
            return $false
        }
    }

    return $TerminalEvents.Count -gt 0
}

function Test-FileTransferSummaryHasV6Evidence {
    param([Parameter(Mandatory = $true)]$Summary)

    if ((Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v6_negotiated') -gt 0 -or
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v6_sender_started') -gt 0 -or
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v6_receiver_started') -gt 0) {
        return $true
    }

    foreach ($event in @($Summary.TransferEvents)) {
        $frameType = Get-FileTransferEventField -Event $event -Name 'frame_type' -Default ''
        if ($frameType -like 'filetransfer.*.v6') {
            return $true
        }
    }

    return $false
}

function Get-FileTransferUnexpectedLegacyFrameEventsDuringV4 {
    param([Parameter(Mandatory = $true)]$Summary)

    return @(
        $Summary.TransferEvents |
            Where-Object {
                $_.EventName -eq 'filetransfer_binary_frame_sent' -or
                $_.EventName -eq 'filetransfer_binary_frame_received' -or
                $_.EventName -eq 'filetransfer_data_frame_dispatched'
            } |
            Where-Object {
                $frameType = Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default ''
                $frameType -like 'filetransfer.*' -and
                    $frameType -notlike 'filetransfer.*.v4' -and
                    $frameType -notlike 'filetransfer.*.v6'
            }
    )
}

function Get-FileTransferFirstCompletedTerminalSequenceByTransferId {
    param([Parameter(Mandatory = $true)]$Summary)

    $sequences = @{}
    foreach ($terminal in @($Summary.TerminalEvents)) {
        if ($null -eq $terminal) {
            continue
        }

        $transferId = [string]$terminal.TransferId
        if ([string]::IsNullOrWhiteSpace($transferId)) {
            continue
        }

        $state = Get-FileTransferEventField -Event $terminal -Name 'state' -Default ''
        $errorCode = Get-FileTransferEventField -Event $terminal -Name 'error_code' -Default '(none)'
        if ($state -ne 'Completed' -or $errorCode -ne '(none)') {
            continue
        }

        if (-not $sequences.ContainsKey($transferId) -or $terminal.Sequence -lt [int]$sequences[$transferId]) {
            $sequences[$transferId] = [int]$terminal.Sequence
        }
    }

    return $sequences
}

function Test-FileTransferBenignPostCompletionLateSenderFrame {
    param(
        $Event,
        [Parameter(Mandatory = $true)]$CompletedTerminalSequences
    )

    if ($null -eq $Event -or $null -eq $CompletedTerminalSequences) {
        return $false
    }

    if ($Event.EventName -ne 'filetransfer_data_frame_ignored') {
        return $false
    }

    $reason = Get-FileTransferEventField -Event $Event -Name 'reason' -Default ''
    if ($reason -ne 'post_completion_late_sender_frame') {
        return $false
    }

    $frameType = Get-FileTransferEventField -Event $Event -Name 'frame_type' -Default ''
    if ($frameType -ne 'filetransfer.manifest.v6' -and
        $frameType -ne 'filetransfer.chunk_batch.v6' -and
        $frameType -ne 'filetransfer.manifest.v4' -and
        $frameType -ne 'filetransfer.chunk_batch.v4') {
        return $false
    }

    $transferId = [string]$Event.TransferId
    if ([string]::IsNullOrWhiteSpace($transferId) -or -not $CompletedTerminalSequences.ContainsKey($transferId)) {
        return $false
    }

    return $Event.Sequence -gt [int]$CompletedTerminalSequences[$transferId]
}

function Test-FileTransferCleanTerminalCompletion {
    param([Parameter(Mandatory = $true)]$Summary)

    return $Summary.InboundTerminalEvents.Count -gt 0 -and
        $Summary.OutboundTerminalEvents.Count -gt 0 -and
        (Test-FileTransferTerminalCompleted -TerminalEvents $Summary.TerminalEvents)
}

function Test-FileTransferSummarySelectedRoute {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [Parameter(Mandatory = $true)][string]$Route
    )

    if ($null -eq $Summary.RouteConsistency) {
        return $false
    }

    foreach ($selected in @($Summary.RouteConsistency.RouteSelectedEvents)) {
        if ((Get-FileTransferEventField -Event $selected -Name 'route' -Default '') -eq $Route) {
            return $true
        }
    }

    return $false
}

function Test-FileTransferRouteConsistencyClean {
    param([Parameter(Mandatory = $true)]$Summary)

    if ($null -eq $Summary.RouteConsistency) {
        return $false
    }

    return @($Summary.RouteConsistency.Findings).Count -eq 0
}

function Test-FileTransferEventNearRecoveryMarker {
    param(
        $Event,
        [Parameter(Mandatory = $true)]$Summary,
        [int]$SequenceWindow = 120
    )

    if ($null -eq $Event) {
        return $false
    }

    $eventSequence = 0
    if (-not [int]::TryParse([string]$Event.Sequence, [ref]$eventSequence)) {
        return $false
    }

    foreach ($candidate in @($Summary.GlobalEvents + $Summary.TransferEvents)) {
        if ($null -eq $candidate) {
            continue
        }

        $candidateSequence = 0
        if (-not [int]::TryParse([string]$candidate.Sequence, [ref]$candidateSequence)) {
            continue
        }

        if ([Math]::Abs($candidateSequence - $eventSequence) -gt $SequenceWindow) {
            continue
        }

        switch ($candidate.EventName) {
            'nkn_bridge_receive_stall_detected' { return $true }
            'nkn_bridge_receive_stall_recovery_started' { return $true }
            'nkn_bridge_receive_stall_recovery_completed' { return $true }
            'nkn_bridge_receive_stall_recovery_receive_resumed' { return $true }
            'nkn_bridge_receive_stall_recovery_hard_restart' { return $true }
            'nkn_bridge_control_receive_recovery_forced' { return $true }
            'bridge_spawned' { return $true }
            'bridge_ready' { return $true }
            'filetransfer_post_tuna_recovery_started' { return $true }
            'filetransfer_transport_paused' { return $true }
            'filetransfer_transport_epoch_started_while_unavailable' { return $true }
            'filetransfer_v6_receiver_state_deferred_for_recovery' { return $true }
            'filetransfer_primary_regular_nkn_frontier_feedback_failed_recoverable' { return $true }
            'filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_send_timeout' { return $true }
            'filetransfer_primary_regular_nkn_bulk_v6_checkpoint_request_send_failed' { return $true }
            'filetransfer_primary_regular_nkn_bulk_v6_checkpoint_receive_recovery_requested' { return $true }
            'filetransfer_primary_regular_nkn_bulk_v6_checkpoint_receive_recovery_suppressed' { return $true }
            'filetransfer_data_session_availability_observed' {
                $isAvailable = Get-FileTransferEventInt64Field -Event $candidate -Name 'is_available' -Default 1
                $requiresResume = Get-FileTransferEventInt64Field -Event $candidate -Name 'requires_resume_request' -Default 0
                if ($isAvailable -eq 0 -or $requiresResume -gt 0) {
                    return $true
                }
            }
            'filetransfer_v6_epoch_started' {
                $reason = Get-FileTransferEventField -Event $candidate -Name 'reason' -Default ''
                $handoffKind = Get-FileTransferEventField -Event $candidate -Name 'handoff_kind' -Default ''
                if ($reason -eq 'receive_stall_recovery' -or
                    $reason -eq 'transport_recovered_unproven' -or
                    $handoffKind -eq 'regular_nkn_recovery') {
                    return $true
                }
            }
            'filetransfer_v6_epoch_reused' {
                $reason = Get-FileTransferEventField -Event $candidate -Name 'reason' -Default ''
                $handoffKind = Get-FileTransferEventField -Event $candidate -Name 'handoff_kind' -Default ''
                if ($reason -eq 'receive_stall_recovery' -or
                    $reason -eq 'transport_recovered_unproven' -or
                    $handoffKind -eq 'regular_nkn_recovery') {
                    return $true
                }
            }
        }
    }

    return $false
}

function Test-FileTransferSummaryUsesPrimaryRegularNknQuietBridgePolicy {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [string]$TransferId = ''
    )

    foreach ($candidate in @($Summary.TransferEvents + $Summary.GlobalEvents)) {
        if ($null -eq $candidate) {
            continue
        }

        $candidateTransferId = [string]$candidate.TransferId
        if (-not [string]::IsNullOrWhiteSpace($TransferId) -and
            -not [string]::IsNullOrWhiteSpace($candidateTransferId) -and
            $candidateTransferId -ne '(all)' -and
            $candidateTransferId -ne $TransferId) {
            continue
        }

        if ($candidate.EventName -ne 'filetransfer_bridge_recovery_policy_selected' -and
            $candidate.EventName -ne 'filetransfer_primary_regular_nkn_bulk_v6_selected') {
            continue
        }

        $policy = Get-FileTransferEventField -Event $candidate -Name 'bridge_recovery_policy' -Default ''
        $runtimeProfile = Get-FileTransferEventField -Event $candidate -Name 'runtime_profile' -Default ''
        $recoveryProfile = Get-FileTransferEventField -Event $candidate -Name 'recovery_profile' -Default ''
        if ($policy -eq 'primary_regular_nkn_quiet' -or
            ($runtimeProfile -eq 'PrimaryRegularNknBulkV6' -and $recoveryProfile -eq 'regular_nkn_quiet')) {
            return $true
        }
    }

    return $false
}

function Test-FileTransferBenignControlledFallbackCancelFeedbackFailure {
    param(
        $Event,
        [Parameter(Mandatory = $true)]$Summary
    )

    if ($null -eq $Event -or $Event.EventName -ne 'filetransfer_v4_feedback_both_failed') {
        return $false
    }

    if (-not (Test-FileTransferCleanTerminalCompletion -Summary $Summary) -or
        -not (Test-FileTransferRouteConsistencyClean -Summary $Summary) -or
        -not (Test-FileTransferSummarySelectedRoute -Summary $Summary -Route 'post_tuna_fallback_v6')) {
        return $false
    }

    $frameType = Get-FileTransferEventField -Event $Event -Name 'frame_type' -Default ''
    if ($frameType -ne 'filetransfer.cancel.v4') {
        return $false
    }

    $firstError = Get-FileTransferEventField -Event $Event -Name 'first_error' -Default ''
    $secondError = Get-FileTransferEventField -Event $Event -Name 'second_error' -Default ''
    return "$firstError $secondError" -like '*OperationCanceledException*'
}

function Test-FileTransferRecoverableV6FeedbackFailure {
    param(
        $Event,
        [Parameter(Mandatory = $true)]$Summary
    )

    if ($null -eq $Event -or $Event.EventName -ne 'filetransfer_v4_feedback_both_failed') {
        return $false
    }

    if (-not (Test-FileTransferCleanTerminalCompletion -Summary $Summary)) {
        return $false
    }

    $frameType = Get-FileTransferEventField -Event $Event -Name 'frame_type' -Default ''
    if ($frameType -ne 'filetransfer.receiver_state.v6' -and
        $frameType -ne 'filetransfer.frontier_request.v6') {
        return $false
    }

    if (-not (Test-FileTransferEventNearRecoveryMarker -Event $Event -Summary $Summary)) {
        return $false
    }

    $firstError = Get-FileTransferEventField -Event $Event -Name 'first_error' -Default ''
    $secondError = Get-FileTransferEventField -Event $Event -Name 'second_error' -Default ''
    $errorText = "$firstError $secondError"
    $transferId = [string]$Event.TransferId
    if ($frameType -eq 'filetransfer.frontier_request.v6' -and
        $errorText -like '*OperationCanceledException*' -and
        (Test-FileTransferSummaryUsesPrimaryRegularNknQuietBridgePolicy -Summary $Summary -TransferId $transferId)) {
        return $true
    }

    $recoverableError =
        $errorText -like '*InvalidOperationException*' -or
        $errorText -like '*bridge is not running*' -or
        $errorText -like '*Not connected*' -or
        $errorText -like '*client not ready*'
    return $recoverableError
}

function Test-FileTransferRecoverableBridgeBulkFailure {
    param(
        $Event,
        [Parameter(Mandatory = $true)]$Summary
    )

    if ($null -eq $Event) {
        return $false
    }

    if (-not (Test-FileTransferCleanTerminalCompletion -Summary $Summary)) {
        return $false
    }

    if ($Event.EventName -eq 'nkn_bridge_bulk_queue_state') {
        return $false
    }

    if ($Event.EventName -ne 'nkn_bridge_bulk_send_summary') {
        return $false
    }

    $sendFailures = Get-FileTransferEventInt64Field -Event $Event -Name 'send_failures' -Default 0
    $queueClears = Get-FileTransferEventInt64Field -Event $Event -Name 'queue_clears' -Default 0
    if ($sendFailures -le 0 -or $queueClears -gt 0) {
        return $false
    }

    $payloadBytesSent = Get-FileTransferEventInt64Field -Event $Event -Name 'payload_bytes_sent' -Default 0
    $payloadBytesEnqueued = Get-FileTransferEventInt64Field -Event $Event -Name 'payload_bytes_enqueued' -Default 0
    if ([Math]::Max($payloadBytesSent, $payloadBytesEnqueued) -gt 1048576) {
        return $false
    }

    return Test-FileTransferEventNearRecoveryMarker -Event $Event -Summary $Summary -SequenceWindow 180
}

function Test-FileTransferRecoverablePostTunaFallbackBridgeClear {
    param(
        $Event,
        [Parameter(Mandatory = $true)]$Summary
    )

    if ($null -eq $Event) {
        return $false
    }

    if (-not (Test-FileTransferCleanTerminalCompletion -Summary $Summary) -or
        -not (Test-FileTransferRouteConsistencyClean -Summary $Summary) -or
        -not (Test-FileTransferSummarySelectedRoute -Summary $Summary -Route 'post_tuna_fallback_v6')) {
        return $false
    }

    if ($Event.EventName -ne 'nkn_bridge_bulk_send_summary' -and
        $Event.EventName -ne 'nkn_bridge_bulk_queue_state') {
        return $false
    }

    $sendFailures = Get-FileTransferEventInt64Field -Event $Event -Name 'send_failures' -Default 0
    if ($sendFailures -gt 0) {
        return $false
    }

    $queueClears = Get-FileTransferEventInt64Field -Event $Event -Name 'queue_clears' -Default 0
    $clearedSinceLast = Get-FileTransferEventInt64Field -Event $Event -Name 'cleared_since_last' -Default 0
    if ($queueClears -le 0 -and $clearedSinceLast -le 0) {
        return $false
    }

    return Test-FileTransferEventNearRecoveryMarker -Event $Event -Summary $Summary -SequenceWindow 260
}

function Get-FileTransferHardFailureEvents {
    param([Parameter(Mandatory = $true)]$Summary)

    $completedTerminalSequences = Get-FileTransferFirstCompletedTerminalSequenceByTransferId -Summary $Summary

    return @(
        $Summary.TransferEvents |
            Where-Object {
                $null -ne $_ -and
                -not (Test-FileTransferBenignPostCompletionLateSenderFrame -Event $_ -CompletedTerminalSequences $completedTerminalSequences) -and
                -not (Test-FileTransferBenignControlledFallbackCancelFeedbackFailure -Event $_ -Summary $Summary) -and
                -not (Test-FileTransferRecoverableV6FeedbackFailure -Event $_ -Summary $Summary) -and
                ($_.EventName -eq 'filetransfer_transport_payload_rejected' -or
                $_.EventName -eq 'filetransfer_data_frame_decode_failed' -or
                $_.EventName -eq 'filetransfer_chunk_rejected' -or
                $_.EventName -eq 'filetransfer_message_rejected' -or
                $_.EventName -eq 'filetransfer_local_soak_cycle_failed' -or
                $_.EventName -eq 'filetransfer_v6_required_transport_incompatible' -or
                $_.EventName -eq 'filetransfer_v4_required_transport_incompatible' -or
                $_.EventName -eq 'filetransfer_v4_receiver_failed' -or
                $_.EventName -eq 'filetransfer_v4_sender_failed' -or
                $_.EventName -eq 'filetransfer_v4_feedback_both_failed' -or
                $_.EventName -eq 'filetransfer_receiver_buffer_exhausted' -or
                $_.EventName -eq 'filetransfer_sender_cache_exhausted' -or
                $_.EventName -eq 'filetransfer_sender_repair_unavailable' -or
                $_.EventName -eq 'filetransfer_v4_receiver_feedback_failed' -or
                ($_.EventName -eq 'filetransfer_data_frame_ignored' -and (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -like '*session_id_mismatch*'))
            }
    )
}

function Get-FileTransferLegacyProtocolStartedEvents {
    param([Parameter(Mandatory = $true)]$Summary)

    return @(
        $Summary.TransferEvents |
            Where-Object {
                if ($_.EventName -ne 'filetransfer_session_opened') {
                    return $false
                }

                $protocolVersion = Get-FileTransferEventField -Event $_ -Name 'protocol_version' -Default ''
                return -not [string]::IsNullOrWhiteSpace($protocolVersion) -and $protocolVersion -ne '4' -and $protocolVersion -ne '6'
            }
    )
}

function Get-FileTransferBridgeBulkFailureEvents {
    param([Parameter(Mandatory = $true)]$Summary)

    return @(
        $Summary.GlobalEvents |
            Where-Object {
                -not (Test-FileTransferRecoverableBridgeBulkFailure -Event $_ -Summary $Summary) -and
                -not (Test-FileTransferRecoverablePostTunaFallbackBridgeClear -Event $_ -Summary $Summary) -and
                (
                    ($_.EventName -eq 'nkn_bridge_bulk_send_summary' -and
                        ((Get-FileTransferEventInt64Field -Event $_ -Name 'send_failures' -Default 0) -gt 0 -or
                         (Get-FileTransferEventInt64Field -Event $_ -Name 'queue_clears' -Default 0) -gt 0)) -or
                    ($_.EventName -eq 'nkn_bridge_bulk_queue_state' -and
                        (Get-FileTransferEventInt64Field -Event $_ -Name 'cleared_since_last' -Default 0) -gt 0)
                )
            }
    )
}

function Get-FileTransferPostTunaFallbackBridgeClearWarningEvents {
    param([Parameter(Mandatory = $true)]$Summary)

    return @(
        $Summary.GlobalEvents |
            Where-Object { Test-FileTransferRecoverablePostTunaFallbackBridgeClear -Event $_ -Summary $Summary }
    )
}

function Test-FileTransferPostTunaFallbackWarningEligible {
    param([Parameter(Mandatory = $true)]$Summary)

    return (Test-FileTransferCleanTerminalCompletion -Summary $Summary) -and
        (Test-FileTransferRouteConsistencyClean -Summary $Summary) -and
        (Test-FileTransferSummarySelectedRoute -Summary $Summary -Route 'post_tuna_fallback_v6')
}

function Get-FileTransferPostTunaFallbackSendTimeoutWarningEvents {
    param([Parameter(Mandatory = $true)]$Summary)

    if (-not (Test-FileTransferPostTunaFallbackWarningEligible -Summary $Summary)) {
        return @()
    }

    return @(
        $Summary.TransferEvents |
            Where-Object {
                $_.EventName -eq 'filetransfer_v6_chunk_batch_send_timeout' -or
                $_.EventName -eq 'filetransfer_v6_post_tuna_fallback_send_timeout_requeued' -or
                $_.EventName -eq 'filetransfer_v6_post_tuna_fallback_send_timeout_frontier_repair_queued'
            } |
            Select-Object -First 10
    )
}

function Get-FileTransferPostTunaFallbackFrontierRepairWarningEvents {
    param([Parameter(Mandatory = $true)]$Summary)

    if (-not (Test-FileTransferPostTunaFallbackWarningEligible -Summary $Summary)) {
        return @()
    }

    $events = @(
        $Summary.TransferEvents |
            Where-Object {
                ($_.EventName -eq 'filetransfer_v6_frontier_request_sent' -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'post_tuna_fallback_survival' -Default 0) -gt 0) -or
                $_.EventName -eq 'filetransfer_v6_post_tuna_fallback_frontier_rescue_requested' -or
                $_.EventName -eq 'filetransfer_v6_post_tuna_fallback_frontier_rescue_widened' -or
                $_.EventName -eq 'filetransfer_v6_frontier_request_duplicate_ignored'
            }
    )

    if ($events.Count -lt 10) {
        return @()
    }

    return @($events | Select-Object -First 10)
}

function Get-FileTransferPostTunaFallbackReceiverStateWarningEvents {
    param([Parameter(Mandatory = $true)]$Summary)

    if (-not (Test-FileTransferPostTunaFallbackWarningEligible -Summary $Summary)) {
        return @()
    }

    $events = @(
        $Summary.TransferEvents |
            Where-Object {
                $_.EventName -eq 'filetransfer_v6_receiver_state_deferred' -or
                $_.EventName -eq 'filetransfer_v6_receiver_state_coalesced'
            }
    )

    if ($events.Count -lt 256) {
        return @()
    }

    return @($events | Select-Object -First 10)
}

function Test-FileTransferBenignControlOnlyReceiveEvent {
    param($Event)

    if ($null -eq $Event) {
        return $false
    }

    $bulkLastReceivedAgeMs = Get-FileTransferEventInt64Field -Event $Event -Name 'bulk_last_received_age_ms' -Default -1
    $bulkReceiveFresh = $bulkLastReceivedAgeMs -ge 0 -and $bulkLastReceivedAgeMs -lt 6000
    $bulkReceiveActive = (Get-FileTransferEventInt64Field -Event $Event -Name 'bulk_messages_received_since_last' -Default 0) -gt 0

    if ($Event.EventName -eq 'nkn_bridge_control_receive_recovery_suppressed') {
        $reason = Get-FileTransferEventField -Event $Event -Name 'reason' -Default ''
        return $reason -eq 'bulk_receive_active' -or
            $reason -eq 'filetransfer_bulk_receive_active' -or
            $reason -eq 'bulk_receive_fresh' -or
            $reason -eq 'filetransfer_bulk_receive_fresh'
    }

    if ($Event.EventName -eq 'nkn_bridge_control_receive_degraded') {
        $activeFileTransferSessions = Get-FileTransferEventInt64Field -Event $Event -Name 'active_file_transfer_sessions' -Default 0
        return $activeFileTransferSessions -gt 0 -and ($bulkReceiveActive -or $bulkReceiveFresh)
    }

    if ($Event.EventName -eq 'screenshare_bridge_transport_health_summary') {
        $hasTransportChurn =
            (Get-FileTransferEventInt64Field -Event $Event -Name 'disconnect_count_since_last' -Default 0) -gt 0 -or
            (Get-FileTransferEventInt64Field -Event $Event -Name 'connect_failed_count_since_last' -Default 0) -gt 0 -or
            (Get-FileTransferEventInt64Field -Event $Event -Name 'ws_error_count_since_last' -Default 0) -gt 0 -or
            (Get-FileTransferEventInt64Field -Event $Event -Name 'rpc_fallback_attempt_count_since_last' -Default 0) -gt 0
        if ($hasTransportChurn) {
            return $false
        }

        $framesSentSinceLast = Get-FileTransferEventInt64Field -Event $Event -Name 'frames_sent_since_last' -Default 0
        $totalMessagesReceivedSinceLast = Get-FileTransferEventInt64Field -Event $Event -Name 'total_messages_received_since_last' -Default 1
        if ($framesSentSinceLast -le 0 -or $totalMessagesReceivedSinceLast -ne 0) {
            return $false
        }

        $controlLastReceivedAgeMs = Get-FileTransferEventInt64Field -Event $Event -Name 'control_last_received_age_ms' -Default -1
        $bulkReceiveNeverObserved = $controlLastReceivedAgeMs -lt 0 -and $bulkLastReceivedAgeMs -lt 0
        return $bulkReceiveFresh -or $bulkReceiveNeverObserved
    }

    return $false
}

function Get-FileTransferExternalTransportWarningEvents {
    param([Parameter(Mandatory = $true)]$Summary)

    return @(
        $Summary.GlobalEvents |
            Where-Object {
                if (Test-FileTransferBenignControlOnlyReceiveEvent -Event $_) {
                    return $false
                }

                $_.EventName -eq 'nkn_bridge_receive_stall_detected' -or
                $_.EventName -eq 'nkn_bridge_receive_stall_recovery_started' -or
                $_.EventName -eq 'nkn_bridge_receive_stall_recovery_completed' -or
                $_.EventName -eq 'nkn_bridge_receive_stall_recovery_failed' -or
                $_.EventName -eq 'nkn_bridge_receive_stall_recovery_cooldown_bypassed' -or
                $_.EventName -eq 'nkn_bridge_receive_stall_recovery_unproven' -or
                $_.EventName -eq 'nkn_bridge_receive_stall_recovery_receive_resumed' -or
                $_.EventName -eq 'nkn_bridge_control_receive_degraded' -or
                $_.EventName -eq 'nkn_bridge_control_receive_recovery_suppressed' -or
                $_.EventName -eq 'nkn_bridge_inbound_delivery_failed' -or
                ($_.EventName -eq 'nkn_bridge_inbound_delivery_summary' -and
                    ((Get-FileTransferEventInt64Field -Event $_ -Name 'subscriber_missing_count' -Default 0) -gt 0 -or
                     (Get-FileTransferEventInt64Field -Event $_ -Name 'handler_failure_count' -Default 0) -gt 0)) -or
                ($_.EventName -eq 'screenshare_bridge_transport_health_summary' -and
                (
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'disconnect_count_since_last' -Default 0) -gt 0 -or
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'connect_failed_count_since_last' -Default 0) -gt 0 -or
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'ws_error_count_since_last' -Default 0) -gt 0 -or
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'rpc_fallback_attempt_count_since_last' -Default 0) -gt 0 -or
                    (
                        (Get-FileTransferEventInt64Field -Event $_ -Name 'control_ready' -Default 0) -gt 0 -and
                        (Get-FileTransferEventInt64Field -Event $_ -Name 'media_ready' -Default 0) -gt 0 -and
                        (Get-FileTransferEventInt64Field -Event $_ -Name 'bulk_ready' -Default 0) -gt 0 -and
                        (Get-FileTransferEventInt64Field -Event $_ -Name 'frames_sent_since_last' -Default 0) -gt 0 -and
                        (Get-FileTransferEventInt64Field -Event $_ -Name 'total_messages_received_since_last' -Default 1) -eq 0
                    )
                ))
            }
    )
}

function Get-FileTransferCohabitationWarningEvents {
    param([Parameter(Mandatory = $true)]$Summary)

    return @(
        $Summary.GlobalEvents |
            Where-Object {
                ($_.EventName -eq 'screenshare_bridge_media_send_summary' -and
                    ((Get-FileTransferEventInt64Field -Event $_ -Name 'queue_drops' -Default 0) -gt 0 -or
                     (Get-FileTransferEventInt64Field -Event $_ -Name 'send_failures' -Default 0) -gt 0 -or
                     (Get-FileTransferEventField -Event $_ -Name 'queue_mode' -Default 'normal') -eq 'severe')) -or
                ($_.EventName -eq 'screenshare_bridge_queue_state' -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'severe' -Default 0) -gt 0)
            }
    )
}

function Get-FileTransferRecoveredPressureWarningEvents {
    param([Parameter(Mandatory = $true)]$Summary)

    $events = New-Object System.Collections.Generic.List[object]
    foreach ($event in @($Summary.TransferEvents)) {
        if ($event.EventName -eq 'filetransfer_session_degraded_entered' -or
            $event.EventName -eq 'filetransfer_bulk_unhealthy_detected' -or
            $event.EventName -eq 'filetransfer_bulk_fallback_entered' -or
            $event.EventName -eq 'filetransfer_receiver_buffer_pressure_entered' -or
            $event.EventName -eq 'filetransfer_frontier_gap_repair_requested' -or
            $event.EventName -eq 'filetransfer_repair_set_requested' -or
            $event.EventName -eq 'filetransfer_request_timeout_detected') {
            $events.Add($event) | Out-Null
        }
    }

    $reorderPolicyEvents = @($Summary.TransferEvents | Where-Object { $_.EventName -eq 'filetransfer_v4_reorder_policy_decision' })
    $fileOnlySparseReorderLimited = @(
        $reorderPolicyEvents |
            Where-Object {
                $decision = Get-FileTransferEventField -Event $_ -Name 'decision' -Default ''
                $decision -eq 'soft_limited' -or $decision -eq 'limited'
            }
    ).Count -gt 0
    $hasRepairTimeoutOrPressure = $Summary.RequestTimeoutCount -gt 0 -or
        $Summary.ProactiveFrontierRepairRequestedCount -gt 0 -or
        $Summary.RepairSetRequestedCount -gt 0 -or
        $Summary.RetryRequestedCount -ge 20 -or
        $Summary.ReceiverBufferPressureEnteredCount -gt 0 -or
        $Summary.DegradedEnteredCount -gt 0 -or
        $Summary.BulkUnhealthyCount -gt 0 -or
        $Summary.BulkFallbackEnteredCount -gt 0
    $shouldWarnForReorder =
        ($Summary.ReorderEventCount -ge 100 -or $Summary.MaxLateArrivalDistance -ge 64 -or $Summary.RetryRequestedCount -ge 20) -and
        ($reorderPolicyEvents.Count -eq 0 -or $fileOnlySparseReorderLimited -or $hasRepairTimeoutOrPressure)

    if ($shouldWarnForReorder) {
        foreach ($event in @($Summary.TransferEvents | Where-Object { $_.EventName -eq 'filetransfer_reorder_pressure' -or $_.EventName -eq 'filetransfer_chunk_retry_requested' } | Select-Object -First 5)) {
            $events.Add($event) | Out-Null
        }
    }

    return $events.ToArray()
}

function Get-FileTransferStabilizationGateResult {
    param([Parameter(Mandatory = $true)]$Summary)

    $hardFailures = New-Object System.Collections.Generic.List[string]
    $warnings = New-Object System.Collections.Generic.List[string]
    $nextArtifact = 'transfer-terminal-summary.txt'
    $verdict = 'PASS'

    if ($Summary.LogFiles.Count -eq 0) {
        return [pscustomobject]@{
            Verdict = 'INVALID_SETUP'
            GateStatus = 'invalid_setup'
            HardFailures = @('no readable log files were found')
            Warnings = @()
            NextArtifact = 'filetransfer-operator-verdict.txt'
            EvidenceEvents = @()
        }
    }

    if ([string]::IsNullOrWhiteSpace($Summary.TransferId) -or -not $Summary.HasTransferEvidence) {
        return [pscustomobject]@{
            Verdict = 'INVALID_SETUP'
            GateStatus = 'invalid_setup'
            HardFailures = @('no file-transfer evidence was found')
            Warnings = @()
            NextArtifact = 'filetransfer-operator-verdict.txt'
            EvidenceEvents = @()
        }
    }

    foreach ($terminal in @($Summary.TerminalEvents)) {
        $state = Get-FileTransferEventField -Event $terminal -Name 'state' -Default ''
        $errorCode = Get-FileTransferEventField -Event $terminal -Name 'error_code' -Default '(none)'
        if ($state -eq 'Canceled' -or $state -eq 'Declined') {
            return [pscustomobject]@{
                Verdict = 'INVALID_SETUP'
                GateStatus = 'invalid_setup'
                HardFailures = @("transfer ended by user action: state=$state")
                Warnings = @()
                NextArtifact = 'transfer-terminal-summary.txt'
                EvidenceEvents = @($terminal)
            }
        }

        if ($state -eq 'Failed' -or $errorCode -ne '(none)') {
            Add-FileTransferGateFinding -List $hardFailures -Finding ("terminal failure: state={0}; error_code={1}" -f $state, $errorCode)
        }
    }

    $hardFailureEvents = @(Get-FileTransferHardFailureEvents -Summary $Summary)
    foreach ($event in @($hardFailureEvents)) {
        Add-FileTransferGateFinding -List $hardFailures -Finding ("hard protocol/integrity event: {0}" -f (Format-FileTransferEvidenceLine -Event $event))
    }

    $legacyProtocolStartedEvents = @(Get-FileTransferLegacyProtocolStartedEvents -Summary $Summary)
    foreach ($event in @($legacyProtocolStartedEvents)) {
        Add-FileTransferGateFinding -List $hardFailures -Finding ("legacy data protocol started: {0}" -f (Format-FileTransferEvidenceLine -Event $event))
    }

    $unexpectedLegacyFramesDuringV4 = @(Get-FileTransferUnexpectedLegacyFrameEventsDuringV4 -Summary $Summary)
    foreach ($event in @($unexpectedLegacyFramesDuringV4)) {
        Add-FileTransferGateFinding -List $hardFailures -Finding ("legacy data frame observed during V6 transfer: {0}" -f (Format-FileTransferEvidenceLine -Event $event))
    }

    $bridgeBulkFailures = @(Get-FileTransferBridgeBulkFailureEvents -Summary $Summary)
    foreach ($event in @($bridgeBulkFailures)) {
        Add-FileTransferGateFinding -List $hardFailures -Finding ("bridge bulk send failure/clear: {0}" -f (Format-FileTransferEvidenceLine -Event $event))
    }

    [object[]]$routeConsistencyFindings = @()
    if ($null -ne $Summary.RouteConsistency) {
        $routeConsistencyFindings = @($Summary.RouteConsistency.Findings)
    }
    if ($routeConsistencyFindings.Count -gt 0) {
        foreach ($finding in @($routeConsistencyFindings)) {
            $operatorFinding = ([string]$finding).Replace('=', ':')
            Add-FileTransferGateFinding -List $hardFailures -Finding ("route consistency: {0}" -f $operatorFinding)
        }
    }

    if ($hardFailures.Count -gt 0) {
        [object[]]$routeEvidenceEvents = @()
        if ($null -ne $Summary.RouteConsistency) {
            $routeEvidenceEvents = @($Summary.RouteConsistency.EvidenceEvents)
        }
        $nextArtifact = if ($routeConsistencyFindings.Count -gt 0) {
            'filetransfer-route-consistency-summary.txt'
        }
        else {
            'stability-gates-summary.txt'
        }

        return [pscustomobject]@{
            Verdict = 'FAIL_PROTOCOL_OR_INTEGRITY'
            GateStatus = 'fail'
            HardFailures = @($hardFailures)
            Warnings = @()
            NextArtifact = $nextArtifact
            EvidenceEvents = @($Summary.TerminalEvents + $hardFailureEvents + $legacyProtocolStartedEvents + $unexpectedLegacyFramesDuringV4 + $bridgeBulkFailures + $routeEvidenceEvents | Select-Object -First 20)
        }
    }

    if ($Summary.LiveProgressTimeoutCount -gt 0 -and $Summary.TerminalMissingAfterProgressTimeout -ne 0) {
        $warnings = New-Object System.Collections.Generic.List[string]
        Add-FileTransferGateFinding -List $warnings -Finding 'live progress timeout before requested matrix completed'
        Add-FileTransferGateFinding -List $warnings -Finding 'progress_timeout_with_receiver_gap_stall'

        $progressTimeoutEvents = @($Summary.TransferEvents | Where-Object { $_.EventName -eq 'filetransfer_live_progress_timeout' })
        return [pscustomobject]@{
            Verdict = 'INCONCLUSIVE_PROGRESS_TIMEOUT'
            GateStatus = 'inconclusive'
            HardFailures = @()
            Warnings = @($warnings)
            NextArtifact = 'throughput-decomposition-summary.txt'
            EvidenceEvents = @($progressTimeoutEvents + $Summary.TerminalEvents | Select-Object -First 20)
        }
    }

    if (-not $Summary.HasTerminalEvidence) {
        $warnings = New-Object System.Collections.Generic.List[string]
        Add-FileTransferGateFinding -List $warnings -Finding 'transfer frames are present but terminal evidence is missing'
        if ($Summary.LiveProgressTimeoutCount -gt 0) {
            Add-FileTransferGateFinding -List $warnings -Finding 'progress_timeout_with_receiver_gap_stall'
        }

        return [pscustomobject]@{
            Verdict = 'INCONCLUSIVE'
            GateStatus = 'inconclusive'
            HardFailures = @()
            Warnings = @($warnings)
            NextArtifact = 'transfer-terminal-summary.txt'
            EvidenceEvents = @($Summary.TransferEvents | Select-Object -First 20)
        }
    }

    if ($Summary.InboundTerminalEvents.Count -eq 0 -or $Summary.OutboundTerminalEvents.Count -eq 0) {
        return [pscustomobject]@{
            Verdict = 'INCONCLUSIVE'
            GateStatus = 'inconclusive'
            HardFailures = @()
            Warnings = @('only one terminal side is visible in the retained logs')
            NextArtifact = 'transfer-terminal-summary.txt'
            EvidenceEvents = @($Summary.TerminalEvents | Select-Object -First 20)
        }
    }

    if (-not (Test-FileTransferTerminalCompleted -TerminalEvents $Summary.TerminalEvents)) {
        return [pscustomobject]@{
            Verdict = 'INCONCLUSIVE'
            GateStatus = 'inconclusive'
            HardFailures = @()
            Warnings = @('terminal evidence is present but does not prove clean completion')
            NextArtifact = 'transfer-terminal-summary.txt'
            EvidenceEvents = @($Summary.TerminalEvents | Select-Object -First 20)
        }
    }

    $cohabitationWarnings = @(Get-FileTransferCohabitationWarningEvents -Summary $Summary)
    $externalWarnings = @(Get-FileTransferExternalTransportWarningEvents -Summary $Summary)
    $fallbackBridgeClearWarnings = @(Get-FileTransferPostTunaFallbackBridgeClearWarningEvents -Summary $Summary)
    $fallbackSendTimeoutWarnings = @(Get-FileTransferPostTunaFallbackSendTimeoutWarningEvents -Summary $Summary)
    $fallbackFrontierRepairWarnings = @(Get-FileTransferPostTunaFallbackFrontierRepairWarningEvents -Summary $Summary)
    $fallbackReceiverStateWarnings = @(Get-FileTransferPostTunaFallbackReceiverStateWarningEvents -Summary $Summary)
    $pressureWarnings = @(Get-FileTransferRecoveredPressureWarningEvents -Summary $Summary)

    if ($cohabitationWarnings.Count -gt 0) {
        $verdict = 'WARN_COHABITATION_PRESSURE'
        $nextArtifact = 'coexistence-summary.txt'
        Add-FileTransferGateFinding -List $warnings -Finding 'screen-share media pressure overlapped the completed transfer'
    }
    elseif ($fallbackBridgeClearWarnings.Count -gt 0 -or
            $fallbackSendTimeoutWarnings.Count -gt 0 -or
            $fallbackFrontierRepairWarnings.Count -gt 0 -or
            $fallbackReceiverStateWarnings.Count -gt 0 -or
            $externalWarnings.Count -gt 0) {
        $verdict = 'WARN_EXTERNAL_TRANSPORT'
        $nextArtifact = if ($fallbackSendTimeoutWarnings.Count -gt 0 -or
            $fallbackFrontierRepairWarnings.Count -gt 0 -or
            $fallbackReceiverStateWarnings.Count -gt 0) {
            'repair-reorder-summary.txt'
        }
        elseif ($fallbackBridgeClearWarnings.Count -gt 0) {
            'stability-gates-summary.txt'
        }
        else {
            'external-transport-health-summary.txt'
        }

        if ($fallbackBridgeClearWarnings.Count -gt 0) {
            Add-FileTransferGateFinding -List $warnings -Finding 'recovered post-Tuna fallback bridge queue clear overlapped the completed transfer'
        }

        if ($fallbackSendTimeoutWarnings.Count -gt 0) {
            Add-FileTransferGateFinding -List $warnings -Finding 'post-Tuna fallback V6 send timeout churn recovered before terminal completion'
        }

        if ($fallbackFrontierRepairWarnings.Count -gt 0) {
            Add-FileTransferGateFinding -List $warnings -Finding 'post-Tuna fallback frontier repair churn recovered before terminal completion'
        }

        if ($fallbackReceiverStateWarnings.Count -gt 0) {
            Add-FileTransferGateFinding -List $warnings -Finding 'post-Tuna fallback receiver state churn recovered before terminal completion'
        }

        if ($externalWarnings.Count -gt 0) {
            Add-FileTransferGateFinding -List $warnings -Finding 'external bridge/NKN health churn overlapped the completed transfer'
        }
    }
    elseif ($pressureWarnings.Count -gt 0) {
        $verdict = 'WARN_RECOVERED_PRESSURE'
        $nextArtifact = 'repair-reorder-summary.txt'
        Add-FileTransferGateFinding -List $warnings -Finding 'repair/reorder/degraded pressure recovered before terminal completion'
    }

    $evidence = @($cohabitationWarnings + $fallbackBridgeClearWarnings + $fallbackSendTimeoutWarnings + $fallbackFrontierRepairWarnings + $fallbackReceiverStateWarnings + $externalWarnings + $pressureWarnings + $Summary.TerminalEvents | Select-Object -First 30)

    return [pscustomobject]@{
        Verdict = $verdict
        GateStatus = if ($verdict -eq 'PASS') { 'pass' } else { 'warn' }
        HardFailures = @($hardFailures)
        Warnings = @($warnings)
        NextArtifact = $nextArtifact
        EvidenceEvents = @($evidence)
    }
}
