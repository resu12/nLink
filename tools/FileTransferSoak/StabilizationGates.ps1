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

function Test-FileTransferSummaryHasV4Evidence {
    param([Parameter(Mandatory = $true)]$Summary)

    if ((Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_negotiated') -gt 0 -or
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_sender_started') -gt 0 -or
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_receiver_started') -gt 0 -or
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_state_sent') -gt 0 -or
        (Get-FileTransferEventCount -Events $Summary.TransferEvents -Name 'filetransfer_v4_state_received') -gt 0) {
        return $true
    }

    foreach ($event in @($Summary.TransferEvents)) {
        $frameType = Get-FileTransferEventField -Event $event -Name 'frame_type' -Default ''
        if ($frameType -like 'filetransfer.*.v4') {
            return $true
        }
    }

    return $false
}

function Get-FileTransferUnexpectedLegacyFrameEventsDuringV4 {
    param([Parameter(Mandatory = $true)]$Summary)

    if (-not (Test-FileTransferSummaryHasV4Evidence -Summary $Summary)) {
        return @()
    }

    return @(
        $Summary.TransferEvents |
            Where-Object {
                $_.EventName -eq 'filetransfer_binary_frame_sent' -or
                $_.EventName -eq 'filetransfer_binary_frame_received' -or
                $_.EventName -eq 'filetransfer_data_frame_dispatched'
            } |
            Where-Object {
                $frameType = Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default ''
                $frameType -like 'filetransfer.*' -and $frameType -notlike 'filetransfer.*.v4'
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

function Test-FileTransferPostTerminalDataFrameCleanupReject {
    param(
        $Event,
        [Parameter(Mandatory = $true)]$CompletedTerminalSequences
    )

    if ($null -eq $Event -or $null -eq $CompletedTerminalSequences) {
        return $false
    }

    if ($Event.EventName -ne 'filetransfer_message_rejected') {
        return $false
    }

    $reason = Get-FileTransferEventField -Event $Event -Name 'reason' -Default ''
    if ($reason -ne 'unknown_transfer_id' -and $reason -ne 'transfer_already_terminal') {
        return $false
    }

    $messageType = Get-FileTransferEventField -Event $Event -Name 'message_type' -Default ''
    if ($messageType -ne 'file_transfer_data_frame') {
        return $false
    }

    $transferId = [string]$Event.TransferId
    if ([string]::IsNullOrWhiteSpace($transferId) -or -not $CompletedTerminalSequences.ContainsKey($transferId)) {
        return $false
    }

    return $Event.Sequence -gt [int]$CompletedTerminalSequences[$transferId]
}

function Get-FileTransferHardFailureEvents {
    param([Parameter(Mandatory = $true)]$Summary)

    $completedTerminalSequences = Get-FileTransferFirstCompletedTerminalSequenceByTransferId -Summary $Summary

    return @(
        $Summary.TransferEvents |
            Where-Object {
                $null -ne $_ -and
                -not (Test-FileTransferPostTerminalDataFrameCleanupReject -Event $_ -CompletedTerminalSequences $completedTerminalSequences) -and
                ($_.EventName -eq 'filetransfer_transport_payload_rejected' -or
                $_.EventName -eq 'filetransfer_data_frame_decode_failed' -or
                $_.EventName -eq 'filetransfer_chunk_rejected' -or
                $_.EventName -eq 'filetransfer_message_rejected' -or
                $_.EventName -eq 'filetransfer_local_soak_cycle_failed' -or
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
                return -not [string]::IsNullOrWhiteSpace($protocolVersion) -and
                    $protocolVersion -ne '4'
            }
    )
}

function Get-FileTransferBridgeBulkFailureEvents {
    param([Parameter(Mandatory = $true)]$Summary)

    return @(
        $Summary.GlobalEvents |
            Where-Object {
                ($_.EventName -eq 'nkn_bridge_bulk_send_summary' -and
                    ((Get-FileTransferEventInt64Field -Event $_ -Name 'send_failures' -Default 0) -gt 0 -or
                     (Get-FileTransferEventInt64Field -Event $_ -Name 'queue_clears' -Default 0) -gt 0)) -or
                ($_.EventName -eq 'nkn_bridge_bulk_queue_state' -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'cleared_since_last' -Default 0) -gt 0)
            }
    )
}

function Get-FileTransferExternalTransportWarningEvents {
    param([Parameter(Mandatory = $true)]$Summary)

    return @(
        $Summary.GlobalEvents |
            Where-Object {
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
        Add-FileTransferGateFinding -List $hardFailures -Finding ("legacy data frame observed during V4 transfer: {0}" -f (Format-FileTransferEvidenceLine -Event $event))
    }

    $bridgeBulkFailures = @(Get-FileTransferBridgeBulkFailureEvents -Summary $Summary)
    foreach ($event in @($bridgeBulkFailures)) {
        Add-FileTransferGateFinding -List $hardFailures -Finding ("bridge bulk send failure/clear: {0}" -f (Format-FileTransferEvidenceLine -Event $event))
    }

    if ($hardFailures.Count -gt 0) {
        return [pscustomobject]@{
            Verdict = 'FAIL_PROTOCOL_OR_INTEGRITY'
            GateStatus = 'fail'
            HardFailures = @($hardFailures)
            Warnings = @()
            NextArtifact = 'stability-gates-summary.txt'
            EvidenceEvents = @($Summary.TerminalEvents + $hardFailureEvents + $legacyProtocolStartedEvents + $unexpectedLegacyFramesDuringV4 + $bridgeBulkFailures | Select-Object -First 20)
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
    $pressureWarnings = @(Get-FileTransferRecoveredPressureWarningEvents -Summary $Summary)

    if ($cohabitationWarnings.Count -gt 0) {
        $verdict = 'WARN_COHABITATION_PRESSURE'
        $nextArtifact = 'coexistence-summary.txt'
        Add-FileTransferGateFinding -List $warnings -Finding 'screen-share media pressure overlapped the completed transfer'
    }
    elseif ($externalWarnings.Count -gt 0) {
        $verdict = 'WARN_EXTERNAL_TRANSPORT'
        $nextArtifact = 'external-transport-health-summary.txt'
        Add-FileTransferGateFinding -List $warnings -Finding 'external bridge/NKN health churn overlapped the completed transfer'
    }
    elseif ($pressureWarnings.Count -gt 0) {
        $verdict = 'WARN_RECOVERED_PRESSURE'
        $nextArtifact = 'repair-reorder-summary.txt'
        Add-FileTransferGateFinding -List $warnings -Finding 'repair/reorder/degraded pressure recovered before terminal completion'
    }

    $evidence = @($cohabitationWarnings + $externalWarnings + $pressureWarnings + $Summary.TerminalEvents | Select-Object -First 30)

    return [pscustomobject]@{
        Verdict = $verdict
        GateStatus = if ($verdict -eq 'PASS') { 'pass' } else { 'warn' }
        HardFailures = @($hardFailures)
        Warnings = @($warnings)
        NextArtifact = $nextArtifact
        EvidenceEvents = @($evidence)
    }
}
