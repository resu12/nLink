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
    param(
        [AllowEmptyCollection()]
        [object[]]$TerminalEvents
    )

    foreach ($event in @($TerminalEvents)) {
        $state = Get-FileTransferEventField -Event $event -Name 'state' -Default ''
        $errorCode = Get-FileTransferEventField -Event $event -Name 'error_code' -Default '(none)'
        if ($state -ne 'Completed' -or $errorCode -ne '(none)') {
            return $false
        }
    }

    return $TerminalEvents.Count -gt 0
}

function Get-FileTransferBridgeLivenessIntegrationProof {
    param(
        [Parameter(Mandatory = $true)]$Summary
    )

    [object[]]$events = @()
    if ($null -ne $Summary.PSObject.Properties['AllEvents']) {
        $events = @($Summary.AllEvents)
    }

    if ($events.Count -eq 0) {
        $events = @($Summary.GlobalEvents + $Summary.TransferEvents)
    }

    [object[]]$authorityEvents = @(
        $events |
            Where-Object { ([string]$_.EventName).StartsWith('filetransfer_fallback_leg_authority_', [System.StringComparison]::OrdinalIgnoreCase) } |
            Sort-Object Sequence
    )
    [object[]]$proofAuthorityEvents = @(
        $authorityEvents |
            Where-Object {
                $_.EventName -ne 'filetransfer_fallback_leg_authority_superseded_by_route_hint'
            }
    )
    [object[]]$currentDeferralEvents = @($events | Where-Object { $_.EventName -eq 'session_liveness_timeout_deferred_for_current_filetransfer_recovery' })
    [object[]]$bridgeDeferralEvents = @($events | Where-Object { $_.EventName -eq 'session_liveness_timeout_deferred_for_bridge_filetransfer_recovery' })
    [object[]]$timeoutEvents = @($events | Where-Object { $_.EventName -eq 'session_liveness_timeout' })
    [object[]]$checkpointAcceptedEvents = @($authorityEvents | Where-Object { $_.EventName -eq 'filetransfer_fallback_leg_authority_checkpoint_accepted' })
    [object[]]$receiveResumedEvents = @($events | Where-Object {
        $_.EventName -eq 'bridge_receive_stall_recovery_receive_resumed' -or
        $_.EventName -eq 'nkn_bridge_receive_stall_recovery_receive_resumed' -or
        $_.EventName -eq 'filetransfer_fallback_leg_authority_checkpoint_accepted' -or
        $_.EventName -eq 'session_liveness_peer_proof_observed' -and
            (Get-FileTransferEventField -Event $_ -Name 'proof_kind' -Default '') -eq 'bridge_receive_stall_recovery_receive_resumed'
    })
    [object[]]$exhaustedEvents = @($events | Where-Object {
        $_.EventName -eq 'bridge_receive_stall_recovery_exhausted' -or
        $_.EventName -eq 'nkn_bridge_receive_stall_recovery_exhausted_for_filetransfer'
    })

    $findings = New-Object System.Collections.Generic.List[string]
    $evidence = New-Object System.Collections.Generic.List[object]
    $activeAuthorities = @{}
    $pendingStaleDeferrals = New-Object System.Collections.Generic.List[object]
    $timeoutDuringValidRecovery = 0
    $staleDeferralCount = 0
    $exhaustedWithoutProofCount = 0

    foreach ($event in @($events | Sort-Object Sequence)) {
        $eventName = [string]$event.EventName
        if ($eventName.StartsWith('filetransfer_fallback_leg_authority_', [System.StringComparison]::OrdinalIgnoreCase)) {
            $sessionId = Get-FileTransferEventField -Event $event -Name 'session_id' -Default ''
            $transferId = Get-FileTransferEventField -Event $event -Name 'transfer_id' -Default ''
            $legGeneration = Get-FileTransferEventField -Event $event -Name 'leg_generation' -Default ''
            $key = "$sessionId|$transferId|$legGeneration"
            if ($eventName -eq 'filetransfer_fallback_leg_authority_started' -or
                $eventName -eq 'filetransfer_fallback_leg_authority_bridge_recovery_requested' -or
                $eventName -eq 'filetransfer_fallback_leg_authority_bridge_recovery_escalated') {
                if (-not [string]::IsNullOrWhiteSpace($sessionId) -and
                    -not [string]::IsNullOrWhiteSpace($transferId) -and
                    -not [string]::IsNullOrWhiteSpace($legGeneration)) {
                    $activeAuthorities[$key] = $event
                }
            }
            elseif ($eventName -eq 'filetransfer_fallback_leg_authority_checkpoint_accepted' -or
                    $eventName -eq 'filetransfer_fallback_leg_authority_completed' -or
                    $eventName -eq 'filetransfer_fallback_leg_authority_superseded_by_route_hint') {
                if ($activeAuthorities.ContainsKey($key)) {
                    $activeAuthorities.Remove($key)
                }
            }
        }

        if ($eventName -eq 'session_liveness_timeout_deferred_for_current_filetransfer_recovery') {
            $sessionId = Get-FileTransferEventField -Event $event -Name 'session_id' -Default ''
            $transferId = Get-FileTransferEventField -Event $event -Name 'transfer_id' -Default ''
            $legGeneration = Get-FileTransferEventField -Event $event -Name 'leg_generation' -Default ''
            $key = "$sessionId|$transferId|$legGeneration"
            if (-not $activeAuthorities.ContainsKey($key)) {
                $pendingStaleDeferrals.Add($event) | Out-Null
            }
        }

        if ($pendingStaleDeferrals.Count -gt 0 -and
            ($eventName -eq 'filetransfer_fallback_leg_authority_checkpoint_accepted' -or
             $eventName -eq 'filetransfer_fallback_leg_authority_completed' -or
             $eventName -eq 'filetransfer_fallback_leg_authority_superseded_by_route_hint' -or
             $eventName -eq 'file_transfer_inbound_terminal' -or
             $eventName -eq 'file_transfer_outbound_terminal' -or
             $eventName -eq 'transfer_terminal')) {
            $proofSessionId = Get-FileTransferEventField -Event $event -Name 'session_id' -Default ''
            $proofTransferId = Get-FileTransferEventField -Event $event -Name 'transfer_id' -Default ''
            $proofLegGeneration = Get-FileTransferEventField -Event $event -Name 'leg_generation' -Default ''
            for ($pendingIndex = $pendingStaleDeferrals.Count - 1; $pendingIndex -ge 0; $pendingIndex--) {
                $pending = $pendingStaleDeferrals[$pendingIndex]
                $pendingSessionId = Get-FileTransferEventField -Event $pending -Name 'session_id' -Default ''
                $pendingTransferId = Get-FileTransferEventField -Event $pending -Name 'transfer_id' -Default ''
                $pendingLegGeneration = Get-FileTransferEventField -Event $pending -Name 'leg_generation' -Default ''

                $sameSession = -not [string]::IsNullOrWhiteSpace($pendingSessionId) -and
                    -not [string]::IsNullOrWhiteSpace($proofSessionId) -and
                    [string]::Equals($pendingSessionId, $proofSessionId, [System.StringComparison]::Ordinal)
                $sameTransfer = -not [string]::IsNullOrWhiteSpace($pendingTransferId) -and
                    -not [string]::IsNullOrWhiteSpace($proofTransferId) -and
                    [string]::Equals($pendingTransferId, $proofTransferId, [System.StringComparison]::Ordinal)
                $sameLeg = [string]::IsNullOrWhiteSpace($proofLegGeneration) -or
                    [string]::IsNullOrWhiteSpace($pendingLegGeneration) -or
                    [string]::Equals($pendingLegGeneration, $proofLegGeneration, [System.StringComparison]::Ordinal)

                if ($sameSession -and $sameTransfer -and $sameLeg) {
                    $pendingStaleDeferrals.RemoveAt($pendingIndex)
                }
            }
        }

        if ($eventName -eq 'session_liveness_timeout' -and $activeAuthorities.Count -gt 0) {
            $timeoutDuringValidRecovery++
            $findings.Add(("session liveness timeout during valid fallback recovery authority: {0}" -f (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
            $evidence.Add($event) | Out-Null
            foreach ($authority in @($activeAuthorities.Values | Select-Object -First 3)) {
                $evidence.Add($authority) | Out-Null
            }
        }

        if (($eventName -eq 'bridge_receive_stall_recovery_exhausted' -or
             $eventName -eq 'nkn_bridge_receive_stall_recovery_exhausted_for_filetransfer') -and
            $activeAuthorities.Count -gt 0) {
            $exhaustedWithoutProofCount++
            foreach ($key in @($activeAuthorities.Keys)) {
                $activeAuthorities.Remove($key)
            }
        }
    }

    foreach ($event in @($pendingStaleDeferrals.ToArray())) {
        $staleDeferralCount++
        $findings.Add(("stale fallback recovery liveness deferral: {0}" -f (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
        $evidence.Add($event) | Out-Null
    }

    $verdict = if ($findings.Count -eq 0) { 'pass' } else { 'fail' }
    if ($proofAuthorityEvents.Count -eq 0 -and
        $currentDeferralEvents.Count -eq 0 -and
        $bridgeDeferralEvents.Count -eq 0 -and
        $timeoutEvents.Count -eq 0) {
        $verdict = 'none'
    }

    return [pscustomobject]@{
        Verdict = $verdict
        CurrentRecoveryDeferralCount = $currentDeferralEvents.Count
        BridgeRecoveryDeferralCount = $bridgeDeferralEvents.Count
        TimeoutDuringValidRecoveryCount = $timeoutDuringValidRecovery
        ReceiveResumedCount = $receiveResumedEvents.Count + $checkpointAcceptedEvents.Count
        RecoveryExhaustedWithoutProofCount = $exhaustedWithoutProofCount
        FallbackLegAuthorityLivenessDeferralCount = $currentDeferralEvents.Count
        StaleDeferralCount = $staleDeferralCount
        Findings = $findings
        EvidenceEvents = @($evidence.ToArray() | Select-Object -First 20)
    }
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

    $reason = Get-FileTransferEventField -Event $Event -Name 'reason' -Default ''
    if ($Event.EventName -eq 'filetransfer_data_frame_ignored') {
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
    }
    elseif ($Event.EventName -eq 'filetransfer_message_rejected') {
        $messageType = Get-FileTransferEventField -Event $Event -Name 'message_type' -Default ''
        if ($messageType -ne 'file_transfer_data_frame' -or
            $reason -ne 'lifecycle_data_frame_unsupported') {
            return $false
        }
    }
    else {
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

function Test-FileTransferEventInInitialRegularNknV4Route {
    param(
        $Event,
        [Parameter(Mandatory = $true)]$Summary
    )

    if ($null -eq $Event -or $null -eq $Summary.RouteConsistency) {
        return $false
    }

    [object[]]$selectedEvents = @($Summary.RouteConsistency.RouteSelectedEvents | Sort-Object Sequence)
    [object[]]$regularSelections = @(
        $selectedEvents |
            Where-Object { (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'regular_nkn_v4_fast' }
    )
    if ($regularSelections.Count -eq 0) {
        return $false
    }

    $firstRegularSequence = [int]$regularSelections[0].Sequence
    if ([int]$Event.Sequence -lt $firstRegularSequence) {
        return $false
    }

    [object[]]$firstNonRegularAfterInitial = @(
        $selectedEvents |
            Where-Object {
                [int]$_.Sequence -gt $firstRegularSequence -and
                (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -ne 'regular_nkn_v4_fast'
            } |
            Sort-Object Sequence |
            Select-Object -First 1
    )

    return $firstNonRegularAfterInitial.Count -eq 0 -or [int]$Event.Sequence -lt [int]$firstNonRegularAfterInitial[0].Sequence
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

    [object[]]$candidateEvents = @()
    $allEventsProperty = $Summary.PSObject.Properties['AllEvents']
    if ($null -ne $allEventsProperty) {
        $candidateEvents = @($Summary.AllEvents)
    }

    if ($candidateEvents.Count -eq 0) {
        $candidateEvents = @($Summary.GlobalEvents + $Summary.TransferEvents)
    }

    foreach ($candidate in @($candidateEvents)) {
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
            'filetransfer_v6_regular_nkn_state_refresh_send_timeout' { return $true }
            'filetransfer_v6_regular_nkn_state_refresh_send_failed' { return $true }
            'filetransfer_post_tuna_fallback_state_refresh_receive_recovery_requested' { return $true }
            'filetransfer_post_tuna_fallback_state_refresh_receive_recovery_suppressed' { return $true }
            'filetransfer_post_tuna_fallback_state_refresh_receive_recovery_deferred' { return $true }
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

function Test-FileTransferEventNearRuntimeUnlockRecoveryMarker {
    param(
        $Event,
        [Parameter(Mandatory = $true)]$Summary,
        [int]$SequenceWindow = 360
    )

    if ($null -eq $Event) {
        return $false
    }

    $eventSequence = 0
    if (-not [int]::TryParse([string]$Event.Sequence, [ref]$eventSequence)) {
        return $false
    }

    $hasUnobservedRuntimeUnlockOffer = $false
    $hasRecoveryOrRetryProof = $false
    [object[]]$candidateEvents = @()
    $allEventsProperty = $Summary.PSObject.Properties['AllEvents']
    if ($null -ne $allEventsProperty) {
        $candidateEvents = @($Summary.AllEvents)
    }

    if ($candidateEvents.Count -eq 0) {
        $candidateEvents = @($Summary.GlobalEvents + $Summary.TransferEvents)
    }

    foreach ($candidate in @($candidateEvents)) {
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
            'tuna_acceleration_activation_offer_not_observed' {
                $trigger = Get-FileTransferEventField -Event $candidate -Name 'trigger' -Default ''
                $reason = Get-FileTransferEventField -Event $candidate -Name 'reason' -Default ''
                $retryReason = Get-FileTransferEventField -Event $candidate -Name 'retry_reason' -Default ''
                if ($trigger -eq 'runtime_unlock' -or
                    $reason -like '*runtime_unlock*' -or
                    $retryReason -like '*runtime_unlock*') {
                    $hasUnobservedRuntimeUnlockOffer = $true
                }
            }
            'tuna_activation_control_send_recovery_requested' { $hasRecoveryOrRetryProof = $true }
            'tuna_acceleration_runtime_unlock_retry_after_recovery_armed' { $hasRecoveryOrRetryProof = $true }
            'tuna_acceleration_runtime_unlock_retry_after_recovery_scheduled' { $hasRecoveryOrRetryProof = $true }
            'filetransfer_tuna_activation_negotiation_regular_nkn_resumed' { $hasRecoveryOrRetryProof = $true }
            'nkn_bridge_receive_stall_recovery_receive_resumed' { $hasRecoveryOrRetryProof = $true }
            'bridge_ready' { $hasRecoveryOrRetryProof = $true }
        }
    }

    return $hasUnobservedRuntimeUnlockOffer -and $hasRecoveryOrRetryProof
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

function Test-FileTransferPostTunaFallbackV6RecoveryEvidence {
    param([Parameter(Mandatory = $true)]$Summary)

    if (-not (Test-FileTransferRouteConsistencyClean -Summary $Summary) -or
        -not (Test-FileTransferSummarySelectedRoute -Summary $Summary -Route 'post_tuna_fallback_v6')) {
        return $false
    }

    $fallbackDiagnostics = Get-FileTransferFallbackV6Diagnostics -Summary $Summary
    if ($null -eq $fallbackDiagnostics) {
        return $false
    }

    return [long]$fallbackDiagnostics.SenderRepairActiveEvidenceCount -gt 0 -or
        [long]$fallbackDiagnostics.FrontierRequestCount -gt 0 -or
        [long]$fallbackDiagnostics.ReceiverStateDeferredCount -gt 0
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

    if ($frameType -eq 'filetransfer.frontier_request.v6' -and
        $errorText -like '*OperationCanceledException*' -and
        (Test-FileTransferPostTunaFallbackV6RecoveryEvidence -Summary $Summary)) {
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

function Test-FileTransferRecoverableRuntimeUnlockBridgeClear {
    param(
        $Event,
        [Parameter(Mandatory = $true)]$Summary
    )

    if ($null -eq $Event) {
        return $false
    }

    if (-not (Test-FileTransferCleanTerminalCompletion -Summary $Summary) -or
        -not (Test-FileTransferRouteConsistencyClean -Summary $Summary)) {
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

    return Test-FileTransferEventNearRuntimeUnlockRecoveryMarker -Event $Event -Summary $Summary -SequenceWindow 360
}

function Test-FileTransferRecoverableRegularNknV4BridgeClear {
    param(
        $Event,
        [Parameter(Mandatory = $true)]$Summary
    )

    if ($null -eq $Event) {
        return $false
    }

    if (-not (Test-FileTransferCleanTerminalCompletion -Summary $Summary) -or
        -not (Test-FileTransferRouteConsistencyClean -Summary $Summary) -or
        -not (Test-FileTransferEventInInitialRegularNknV4Route -Event $Event -Summary $Summary)) {
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
    return $queueClears -gt 0 -or $clearedSinceLast -gt 0
}

function Test-FileTransferRegularV4ProgressTimeoutRecoveryStorm {
    param([Parameter(Mandatory = $true)]$Summary)

    if ($Summary.LiveProgressTimeoutCount -le 0 -or
        $Summary.TerminalMissingAfterProgressTimeout -eq 0) {
        return $false
    }

    if (-not (Test-FileTransferRouteConsistencyClean -Summary $Summary) -or
        -not (Test-FileTransferSummarySelectedRoute -Summary $Summary -Route 'regular_nkn_v4_fast')) {
        return $false
    }

    $routeEvidence = @(
        $Summary.TransferEvents |
            Where-Object {
                (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'file_tuna_v6' -or
                (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'post_tuna_fallback_v6' -or
                (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'diagnostic_regular_nkn_v6'
            }
    )
    if ($routeEvidence.Count -gt 0) {
        return $false
    }

    $recoveryStormEvents = @(
        $Summary.GlobalEvents |
            Where-Object {
                $_.EventName -eq 'nkn_bridge_receive_stall_recovery_protocol_repair_exhausted' -or
                $_.EventName -eq 'nkn_bridge_receive_stall_recovery_regular_v4_unproven_escalation_allowed' -or
                (
                    $_.EventName -eq 'nkn_bridge_receive_stall_recovery_suppressed' -and
                    (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'filetransfer_protocol_repair_only'
                ) -or
                (
                    $_.EventName -eq 'nkn_bridge_receive_stall_recovery_requested' -and
                    (Get-FileTransferEventField -Event $_ -Name 'stall_reason' -Default '') -eq 'regular_v4_unproven_recovery_escalation'
                )
            }
    )

    return $recoveryStormEvents.Count -gt 0
}

function Test-FileTransferRegularV4ProgressTimeoutRecoveryStormBridgeClear {
    param(
        $Event,
        [Parameter(Mandatory = $true)]$Summary
    )

    if ($null -eq $Event) {
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

    return Test-FileTransferRegularV4ProgressTimeoutRecoveryStorm -Summary $Summary
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
                -not (Test-FileTransferRecoverableRuntimeUnlockBridgeClear -Event $_ -Summary $Summary) -and
                -not (Test-FileTransferRecoverableRegularNknV4BridgeClear -Event $_ -Summary $Summary) -and
                -not (Test-FileTransferRegularV4ProgressTimeoutRecoveryStormBridgeClear -Event $_ -Summary $Summary) -and
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

function Get-FileTransferRuntimeUnlockBridgeClearWarningEvents {
    param([Parameter(Mandatory = $true)]$Summary)

    return @(
        $Summary.GlobalEvents |
            Where-Object { Test-FileTransferRecoverableRuntimeUnlockBridgeClear -Event $_ -Summary $Summary }
    )
}

function Get-FileTransferRegularNknV4BridgeClearWarningEvents {
    param([Parameter(Mandatory = $true)]$Summary)

    return @(
        $Summary.GlobalEvents |
            Where-Object { Test-FileTransferRecoverableRegularNknV4BridgeClear -Event $_ -Summary $Summary }
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

        $activeFileTransferRuntimeSessions = Get-FileTransferEventInt64Field -Event $Event -Name 'active_file_transfer_runtime_sessions' -Default 0
        if ($activeFileTransferRuntimeSessions -gt 0) {
            return $true
        }

        $controlLastReceivedAgeMs = Get-FileTransferEventInt64Field -Event $Event -Name 'control_last_received_age_ms' -Default -1
        $bulkReceiveNeverObserved = $controlLastReceivedAgeMs -lt 0 -and $bulkLastReceivedAgeMs -lt 0
        return $bulkReceiveFresh -or $bulkReceiveNeverObserved
    }

    return $false
}

function Test-FileTransferEventInsideObservedTransferWindow {
    param(
        [Parameter(Mandatory = $true)]$Event,
        [Parameter(Mandatory = $true)]$Summary
    )

    if ($null -eq $Event.TimestampUtc) {
        return $true
    }

    $start = [datetimeoffset]::MinValue
    $end = [datetimeoffset]::MaxValue
    if (-not [datetimeoffset]::TryParse([string]$Summary.FirstTimestamp, [ref]$start) -or
        -not [datetimeoffset]::TryParse([string]$Summary.LastTimestamp, [ref]$end)) {
        return $true
    }

    return $Event.TimestampUtc -ge $start -and $Event.TimestampUtc -le $end
}

function Get-FileTransferExternalTransportWarningEvents {
    param([Parameter(Mandatory = $true)]$Summary)

    return @(
        $Summary.GlobalEvents |
            Where-Object {
                if (-not (Test-FileTransferEventInsideObservedTransferWindow -Event $_ -Summary $Summary)) {
                    return $false
                }

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

function Get-FileTransferWarningKindToken {
    param([AllowEmptyString()][string]$WarningText)

    if ($WarningText -eq 'external bridge/NKN health churn overlapped the completed transfer') {
        return 'external_transport_churn'
    }
    if ($WarningText -eq 'recovered post-Tuna fallback bridge queue clear overlapped the completed transfer') {
        return 'recovered_post_tuna_fallback_bridge_clear'
    }
    if ($WarningText -eq 'recovered runtime-unlock bridge queue clear overlapped the completed transfer' -or
        $WarningText -eq 'recovered runtime unlock bridge queue clear overlapped the completed transfer') {
        return 'recovered_runtime_unlock_bridge_clear'
    }
    if ($WarningText -eq 'recovered regular NKN V4 bridge queue clear overlapped the completed transfer') {
        return 'recovered_regular_v4_bridge_clear'
    }
    if ($WarningText -eq 'post-Tuna fallback V6 send timeout churn recovered before terminal completion') {
        return 'fallback_v6_send_timeout_churn'
    }
    if ($WarningText -eq 'post-Tuna fallback frontier repair churn recovered before terminal completion') {
        return 'fallback_frontier_repair_churn'
    }
    if ($WarningText -eq 'post-Tuna fallback receiver state churn recovered before terminal completion') {
        return 'fallback_receiver_state_churn'
    }
    if ($WarningText -eq 'screen-share media pressure overlapped the completed transfer') {
        return 'cohabitation_pressure'
    }
    if ($WarningText -eq 'repair/reorder/degraded pressure recovered before terminal completion') {
        return 'recovered_pressure'
    }
    if ($WarningText -eq 'progress_timeout_with_receiver_gap_stall') {
        return 'progress_timeout_with_receiver_gap_stall'
    }
    if (-not [string]::IsNullOrWhiteSpace($WarningText)) {
        return ($WarningText.ToLowerInvariant() -replace '[^a-z0-9]+', '_' -replace '^_+|_+$', '')
    }

    return ''
}

function Get-FileTransferObservationDurationSeconds {
    param([Parameter(Mandatory = $true)]$Summary)

    $start = [datetimeoffset]::MinValue
    $end = [datetimeoffset]::MinValue
    if ([datetimeoffset]::TryParse([string]$Summary.FirstTimestamp, [ref]$start) -and
        [datetimeoffset]::TryParse([string]$Summary.LastTimestamp, [ref]$end) -and
        $end -gt $start) {
        return [Math]::Max(1.0, ($end - $start).TotalSeconds)
    }

    return 1.0
}

function Test-FileTransferExternalTransportCapWarningEvent {
    param(
        [Parameter(Mandatory = $true)]$Event,
        [bool]$HasActiveReceiveStall
    )

    if ($Event.EventName -eq 'screenshare_bridge_transport_health_summary') {
        return (Get-FileTransferEventInt64Field -Event $Event -Name 'disconnect_count_since_last' -Default 0) -gt 0 -or
            (Get-FileTransferEventInt64Field -Event $Event -Name 'connect_failed_count_since_last' -Default 0) -gt 0 -or
            (Get-FileTransferEventInt64Field -Event $Event -Name 'ws_error_count_since_last' -Default 0) -gt 0 -or
            (Get-FileTransferEventInt64Field -Event $Event -Name 'rpc_fallback_attempt_count_since_last' -Default 0) -gt 0
    }

    if (([string]$Event.EventName).StartsWith('nkn_bridge_receive_stall_', [System.StringComparison]::OrdinalIgnoreCase)) {
        if ($Event.EventName -eq 'nkn_bridge_receive_stall_detected') {
            return (Get-FileTransferEventInt64Field -Event $Event -Name 'active_file_transfer_sessions' -Default 0) -gt 0 -or
                (Get-FileTransferEventInt64Field -Event $Event -Name 'active_file_transfer_runtime_sessions' -Default 0) -gt 0
        }

        return $HasActiveReceiveStall
    }

    if ($Event.EventName -eq 'nkn_bridge_control_receive_degraded') {
        return (Get-FileTransferEventInt64Field -Event $Event -Name 'active_file_transfer_sessions' -Default 0) -gt 0 -or
            (Get-FileTransferEventInt64Field -Event $Event -Name 'active_file_transfer_runtime_sessions' -Default 0) -gt 0
    }

    return $true
}

function Get-FileTransferExternalTransportCapWarningEvents {
    param([object[]]$Events = @())

    $hasActiveReceiveStall = @(
        $Events |
            Where-Object {
                $_.EventName -eq 'nkn_bridge_receive_stall_detected' -and
                (
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'active_file_transfer_sessions' -Default 0) -gt 0 -or
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'active_file_transfer_runtime_sessions' -Default 0) -gt 0
                )
            }
    ).Count -gt 0

    return @(
        $Events |
            Where-Object { Test-FileTransferExternalTransportCapWarningEvent -Event $_ -HasActiveReceiveStall $hasActiveReceiveStall }
    )
}

function Get-FileTransferWarningEventTimeBucket {
    param(
        $Event,
        [int]$BucketSeconds = 30
    )

    if ($null -eq $Event) {
        return 'unknown'
    }

    $timestampProperty = $Event.PSObject.Properties['TimestampUtc']
    if ($null -ne $timestampProperty -and $null -ne $timestampProperty.Value) {
        $timestampValue = $timestampProperty.Value
        $timestamp = [datetimeoffset]::MinValue
        if ($timestampValue -is [datetimeoffset]) {
            $timestamp = $timestampValue
        }
        elseif ($timestampValue -is [datetime]) {
            $timestamp = [datetimeoffset]$timestampValue
        }
        elseif ([datetimeoffset]::TryParse([string]$timestampValue, [ref]$timestamp)) {
            # parsed above
        }

        if ($timestamp -ne [datetimeoffset]::MinValue) {
            return [string]([Math]::Floor($timestamp.ToUnixTimeSeconds() / [double]$BucketSeconds))
        }
    }

    $sequence = 0
    if ([int]::TryParse([string]$Event.Sequence, [ref]$sequence)) {
        return [string]([Math]::Floor($sequence / 120.0))
    }

    return 'unknown'
}

function Get-FileTransferWarningEventUnixSeconds {
    param($Event)

    if ($null -eq $Event) {
        return $null
    }

    $timestampProperty = $Event.PSObject.Properties['TimestampUtc']
    if ($null -eq $timestampProperty -or $null -eq $timestampProperty.Value) {
        return $null
    }

    $timestampValue = $timestampProperty.Value
    $timestamp = [datetimeoffset]::MinValue
    if ($timestampValue -is [datetimeoffset]) {
        $timestamp = $timestampValue
    }
    elseif ($timestampValue -is [datetime]) {
        $timestamp = [datetimeoffset]$timestampValue
    }
    elseif (-not [datetimeoffset]::TryParse([string]$timestampValue, [ref]$timestamp)) {
        return $null
    }

    return $timestamp.ToUnixTimeSeconds()
}

function Test-FileTransferReceiveStallEventsSameWarningIncident {
    param(
        $Previous,
        $Current,
        [int]$MaxSeconds = 120,
        [int]$MaxSequenceDistance = 1200
    )

    if ($null -eq $Previous -or $null -eq $Current) {
        return $false
    }

    $previousSeconds = Get-FileTransferWarningEventUnixSeconds -Event $Previous
    $currentSeconds = Get-FileTransferWarningEventUnixSeconds -Event $Current
    if ($null -ne $previousSeconds -and $null -ne $currentSeconds) {
        return [Math]::Abs([long]$currentSeconds - [long]$previousSeconds) -le $MaxSeconds
    }

    $previousSequence = 0
    $currentSequence = 0
    if ([int]::TryParse([string]$Previous.Sequence, [ref]$previousSequence) -and
        [int]::TryParse([string]$Current.Sequence, [ref]$currentSequence)) {
        return [Math]::Abs($currentSequence - $previousSequence) -le $MaxSequenceDistance
    }

    return $false
}

function Get-FileTransferWarningIncidentKey {
    param(
        [Parameter(Mandatory = $true)][string]$Kind,
        $Event
    )

    if ($null -eq $Event) {
        return ('{0}:null' -f $Kind)
    }

    $eventName = [string]$Event.EventName
    $transferId = Get-FileTransferEventField -Event $Event -Name 'transfer_id' -Default ''
    $route = Get-FileTransferEventField -Event $Event -Name 'route' -Default ''
    $connectKey = Get-FileTransferEventField -Event $Event -Name 'connect_key' -Default ''
    $attempt = Get-FileTransferEventField -Event $Event -Name 'attempt' -Default ''
    $recoveryCount = Get-FileTransferEventField -Event $Event -Name 'recovery_count' -Default ''
    $transportEpoch = Get-FileTransferEventField -Event $Event -Name 'transport_epoch' -Default ''
    $bucket = Get-FileTransferWarningEventTimeBucket -Event $Event

    if ($Kind -eq 'external_transport_churn') {
        if ($eventName -eq 'screenshare_bridge_transport_health_summary') {
            $hasTransportChurn =
                (Get-FileTransferEventInt64Field -Event $Event -Name 'disconnect_count_since_last' -Default 0) -gt 0 -or
                (Get-FileTransferEventInt64Field -Event $Event -Name 'connect_failed_count_since_last' -Default 0) -gt 0 -or
                (Get-FileTransferEventInt64Field -Event $Event -Name 'ws_error_count_since_last' -Default 0) -gt 0 -or
                (Get-FileTransferEventInt64Field -Event $Event -Name 'rpc_fallback_attempt_count_since_last' -Default 0) -gt 0
            if ($hasTransportChurn) {
                if (-not [string]::IsNullOrWhiteSpace($connectKey)) {
                    $wideBucket = Get-FileTransferWarningEventTimeBucket -Event $Event -BucketSeconds 120
                    return ('{0}:health:{1}:bucket:{2}' -f $Kind, $connectKey, $wideBucket)
                }

                return ('{0}:health:{1}' -f $Kind, $Event.Sequence)
            }

            return ('{0}:receive_stall_health:{1}' -f $Kind, $bucket)
        }

        if ($eventName.StartsWith('nkn_bridge_receive_stall_', [System.StringComparison]::OrdinalIgnoreCase)) {
            if ([string]::IsNullOrWhiteSpace($connectKey)) {
                $connectKey = 'unknown'
            }

            $wideBucket = Get-FileTransferWarningEventTimeBucket -Event $Event -BucketSeconds 120
            return ('{0}:receive_stall:{1}:bucket:{2}' -f $Kind, $connectKey, $wideBucket)
        }

        if ($eventName -eq 'nkn_bridge_control_receive_degraded' -or
            $eventName -eq 'nkn_bridge_control_receive_recovery_suppressed') {
            if ([string]::IsNullOrWhiteSpace($connectKey)) {
                $connectKey = 'unknown'
            }

            $reason = Get-FileTransferEventField -Event $Event -Name 'reason' -Default ''
            return ('{0}:control_receive:{1}:{2}:{3}' -f $Kind, $connectKey, $reason, $bucket)
        }

        return ('{0}:{1}:{2}' -f $Kind, $eventName, $Event.Sequence)
    }

    if ($Kind -eq 'fallback_v6_send_timeout_churn') {
        $wideBucket = Get-FileTransferWarningEventTimeBucket -Event $Event -BucketSeconds 120
        return ('{0}:{1}:{2}:{3}' -f $Kind, $transferId, $route, $wideBucket)
    }

    if ($Kind -eq 'fallback_frontier_repair_churn') {
        $frontier = Get-FileTransferEventField -Event $Event -Name 'frontier_chunk_index' -Default ''
        if ([string]::IsNullOrWhiteSpace($frontier) -or $frontier -eq '-1') {
            $repairRequestId = Get-FileTransferEventField -Event $Event -Name 'repair_request_id' -Default ''
            if (-not [string]::IsNullOrWhiteSpace($repairRequestId) -and
                $repairRequestId.StartsWith('v6-frontier:', [System.StringComparison]::OrdinalIgnoreCase)) {
                $repairRequestParts = @($repairRequestId.Split(':') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
                if ($repairRequestParts.Count -ge 3) {
                    $parsedFrontier = $repairRequestParts[$repairRequestParts.Count - 2]
                    if ($parsedFrontier -match '^\d+$') {
                        $frontier = $parsedFrontier
                    }
                }
            }
        }
        if ([string]::IsNullOrWhiteSpace($frontier)) {
            $frontier = Get-FileTransferEventField -Event $Event -Name 'start_chunk_index' -Default ''
        }
        if ([string]::IsNullOrWhiteSpace($frontier)) {
            $frontier = Get-FileTransferEventField -Event $Event -Name 'first_start_chunk_index' -Default ''
        }
        if ([string]::IsNullOrWhiteSpace($frontier)) {
            $frontier = Get-FileTransferEventField -Event $Event -Name 'remote_frontier_chunk_index' -Default ''
        }

        if (-not [string]::IsNullOrWhiteSpace($frontier)) {
            if ([string]::IsNullOrWhiteSpace($route)) {
                $route = 'post_tuna_fallback_v6'
            }

            return ('{0}:{1}:{2}:{3}' -f $Kind, $transferId, $route, $frontier)
        }

        return ('{0}:{1}:{2}:{3}:{4}' -f $Kind, $transferId, $route, $eventName, $bucket)
    }

    if ($Kind -eq 'fallback_receiver_state_churn') {
        $wideBucket = Get-FileTransferWarningEventTimeBucket -Event $Event -BucketSeconds 120
        return ('{0}:{1}:{2}:{3}' -f $Kind, $transferId, $route, $wideBucket)
    }

    if ($Kind -eq 'recovered_post_tuna_fallback_bridge_clear') {
        $wideBucket = Get-FileTransferWarningEventTimeBucket -Event $Event -BucketSeconds 120
        return ('{0}:{1}' -f $Kind, $wideBucket)
    }

    if ($Kind -eq 'recovered_runtime_unlock_bridge_clear') {
        $wideBucket = Get-FileTransferWarningEventTimeBucket -Event $Event -BucketSeconds 120
        return ('{0}:{1}' -f $Kind, $wideBucket)
    }

    if ($Kind -eq 'recovered_regular_v4_bridge_clear') {
        $wideBucket = Get-FileTransferWarningEventTimeBucket -Event $Event -BucketSeconds 120
        return ('{0}:{1}' -f $Kind, $wideBucket)
    }

    return ('{0}:{1}:{2}' -f $Kind, $eventName, $Event.Sequence)
}

function Get-FileTransferWarningIncidentEvents {
    param(
        [Parameter(Mandatory = $true)][string]$Kind,
        [object[]]$Events = @()
    )

    $seen = @{}
    $incidents = New-Object System.Collections.Generic.List[object]
    $lastReceiveStallIncident = $null
    foreach ($event in @($Events | Sort-Object Sequence)) {
        $eventName = [string]$event.EventName
        if ($Kind -eq 'external_transport_churn' -and
            $eventName.StartsWith('nkn_bridge_receive_stall_', [System.StringComparison]::OrdinalIgnoreCase)) {
            if ($null -ne $lastReceiveStallIncident -and
                (Test-FileTransferReceiveStallEventsSameWarningIncident -Previous $lastReceiveStallIncident -Current $event)) {
                continue
            }

            $incidents.Add($event) | Out-Null
            $lastReceiveStallIncident = $event
            continue
        }

        $key = Get-FileTransferWarningIncidentKey -Kind $Kind -Event $event
        if ($seen.ContainsKey($key)) {
            continue
        }

        $seen[$key] = $true
        $incidents.Add($event) | Out-Null
    }

    return @($incidents.ToArray())
}

function Get-FileTransferWarningRouteContext {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [Parameter(Mandatory = $true)][string]$Kind,
        [object[]]$Events = @()
    )

    if ($Kind -like 'fallback_*' -or $Kind -eq 'recovered_post_tuna_fallback_bridge_clear') {
        return 'post_tuna_fallback'
    }

    if ($Kind -eq 'recovered_runtime_unlock_bridge_clear') {
        return 'runtime_unlock'
    }

    if ($Kind -eq 'recovered_regular_v4_bridge_clear') {
        return 'regular_nkn'
    }

    [object[]]$eventRoutes = @(
        $Events |
            ForEach-Object { Get-FileTransferEventField -Event $_ -Name 'route' -Default '' } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -Unique
    )

    [object[]]$selectedRoutes = @()
    if ($null -ne $Summary.RouteConsistency) {
        $selectedRoutes = @(
            $Summary.RouteConsistency.RouteSelectedEvents |
                ForEach-Object { Get-FileTransferEventField -Event $_ -Name 'route' -Default '' } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                Select-Object -Unique
        )
    }

    [object[]]$routes = @($eventRoutes + $selectedRoutes | Select-Object -Unique)
    if ($routes.Count -eq 0) {
        return 'unknown'
    }

    if ($routes -contains 'post_tuna_fallback_v6' -and $routes.Count -gt 1) {
        return 'mixed'
    }

    if ($routes -contains 'post_tuna_fallback_v6') {
        return 'post_tuna_fallback'
    }

    if ($routes -contains 'file_tuna_v4') {
        return 'active_tuna'
    }

    if ($routes -contains 'regular_nkn_v4_fast') {
        return 'regular_nkn'
    }

    return 'unknown'
}

function Get-FileTransferMaxEventField {
    param(
        [object[]]$Events = @(),
        [string[]]$FieldNames = @()
    )

    $max = -1L
    foreach ($event in @($Events)) {
        foreach ($fieldName in @($FieldNames)) {
            $value = Get-FileTransferEventInt64Field -Event $event -Name $fieldName -Default -1
            if ($value -gt $max) {
                $max = $value
            }
        }
    }

    return $max
}

function Get-FileTransferFallbackV6Diagnostics {
    param([Parameter(Mandatory = $true)]$Summary)

    [object[]]$fallbackEvents = @(
        $Summary.TransferEvents |
            Where-Object {
                (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'post_tuna_fallback_v6' -or
                (Get-FileTransferEventField -Event $_ -Name 'frame_type' -Default '') -like 'filetransfer.*.v6' -or
                $_.EventName -like 'filetransfer_v6_*' -or
                $_.EventName -like 'filetransfer_post_tuna_fallback_*'
            }
    )

    $hasFallbackRoute = Test-FileTransferSummarySelectedRoute -Summary $Summary -Route 'post_tuna_fallback_v6'
    if (-not $hasFallbackRoute -and $fallbackEvents.Count -eq 0) {
        return $null
    }

    $terminalReason = '(none)'
    if (-not $Summary.HasTerminalEvidence) {
        $terminalReason = 'terminal_evidence_missing'
    }
    elseif ($Summary.LiveProgressTimeoutCount -gt 0 -and $Summary.TerminalMissingAfterProgressTimeout -ne 0) {
        $terminalReason = 'progress_timeout_terminal_missing'
    }
    elseif (-not (Test-FileTransferTerminalCompleted -TerminalEvents $Summary.TerminalEvents)) {
        $terminalReason = 'terminal_not_completed'
    }

    $sendTimeoutEvents = @($fallbackEvents | Where-Object { $_.EventName -eq 'filetransfer_v6_chunk_batch_send_timeout' })
    $frontierEvents = @($fallbackEvents | Where-Object { $_.EventName -eq 'filetransfer_v6_frontier_request_sent' -or $_.EventName -eq 'filetransfer_v6_frontier_request_duplicate_ignored' })
    $deferredEvents = @($fallbackEvents | Where-Object { $_.EventName -eq 'filetransfer_v6_receiver_state_deferred' })
    $coalescedEvents = @($fallbackEvents | Where-Object { $_.EventName -eq 'filetransfer_v6_receiver_state_coalesced' })
    $repairActiveEvents = @(
        $fallbackEvents |
            Where-Object {
                $_.EventName -eq 'filetransfer_v6_chunk_batch_send_timeout' -or
                $_.EventName -eq 'filetransfer_v6_post_tuna_fallback_send_timeout_requeued' -or
                $_.EventName -eq 'filetransfer_v6_post_tuna_fallback_send_timeout_frontier_repair_queued' -or
                $_.EventName -eq 'filetransfer_v6_frontier_request_sent' -or
                $_.EventName -eq 'filetransfer_v6_frontier_request_duplicate_ignored' -or
                $_.EventName -eq 'filetransfer_v6_post_tuna_fallback_frontier_rescue_requested' -or
                $_.EventName -eq 'filetransfer_v6_post_tuna_fallback_frontier_rescue_widened' -or
                $_.EventName -eq 'filetransfer_v6_regular_nkn_state_refresh_send_timeout' -or
                $_.EventName -eq 'filetransfer_v6_regular_nkn_state_refresh_send_failed' -or
                $_.EventName -eq 'filetransfer_post_tuna_fallback_state_refresh_receive_recovery_requested' -or
                $_.EventName -eq 'filetransfer_post_tuna_fallback_state_refresh_receive_recovery_suppressed' -or
                $_.EventName -eq 'filetransfer_post_tuna_fallback_state_refresh_receive_recovery_deferred'
            }
    )

    return [pscustomobject]@{
        HasPostTunaFallbackEvidence = if ($hasFallbackRoute -or $fallbackEvents.Count -gt 0) { 1 } else { 0 }
        TerminalMissingReason = $terminalReason
        LastCommittedChunkIndex = Get-FileTransferMaxEventField -Events $fallbackEvents -FieldNames @('contiguous_committed_chunk_index', 'last_committed_chunk_index')
        HighestObservedChunkIndex = Get-FileTransferMaxEventField -Events $fallbackEvents -FieldNames @('highest_received_chunk_index', 'durable_received_highest_chunk_index', 'receiver_highest_chunk', 'highest_observed_chunk_index')
        OldestUnrecoveredGapAgeMs = Get-FileTransferMaxEventField -Events $fallbackEvents -FieldNames @('oldest_gap_age_ms', 'gap_stall_age_ms')
        V6ChunkSendTimeoutCount = $sendTimeoutEvents.Count
        FrontierRequestCount = $frontierEvents.Count
        ReceiverStateDeferredCount = $deferredEvents.Count
        ReceiverStateCoalescedCount = $coalescedEvents.Count
        SenderRepairActiveEvidenceCount = $repairActiveEvents.Count
        SenderStillRepairing = if ($repairActiveEvents.Count -gt 0 -and $terminalReason -ne '(none)') { 1 } else { 0 }
    }
}

function Get-FileTransferWarningCapResult {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [object[]]$WarningGroups = @(),
        $FallbackDiagnostics = $null
    )

    $countLimit = 3
    $rateLimit = 0.05
    $durationSeconds = Get-FileTransferObservationDurationSeconds -Summary $Summary
    $culture = [System.Globalization.CultureInfo]::InvariantCulture
    $kindCounts = New-Object System.Collections.Generic.List[string]
    $rawKindCounts = New-Object System.Collections.Generic.List[string]
    $kindRates = New-Object System.Collections.Generic.List[string]
    $kindContexts = New-Object System.Collections.Generic.List[string]
    $exceededKinds = New-Object System.Collections.Generic.List[string]
    $exceededContexts = New-Object System.Collections.Generic.List[string]
    $exceededDetails = New-Object System.Collections.Generic.List[string]
    $exceededEvents = New-Object System.Collections.Generic.List[object]
    $exemptedKinds = New-Object System.Collections.Generic.List[string]
    $exemptedDetails = New-Object System.Collections.Generic.List[string]

    foreach ($group in @($WarningGroups)) {
        $kind = [string]$group.Kind
        [object[]]$events = @($group.Events)
        if ($events.Count -le 0 -or [string]::IsNullOrWhiteSpace($kind)) {
            continue
        }

        [object[]]$incidentEvents = @(Get-FileTransferWarningIncidentEvents -Kind $kind -Events $events)
        $count = $incidentEvents.Count
        $rawCount = $events.Count
        if ($count -le 0) {
            continue
        }

        $context = Get-FileTransferWarningRouteContext -Summary $Summary -Kind $kind -Events $events
        $rate = [double]$count / [double]$durationSeconds
        $kindCounts.Add(('{0}:{1}' -f $kind, $count)) | Out-Null
        $rawKindCounts.Add(('{0}:{1}' -f $kind, $rawCount)) | Out-Null
        $kindRates.Add(('{0}:{1}' -f $kind, $rate.ToString('0.###', $culture))) | Out-Null
        $kindContexts.Add(('{0}:{1}' -f $kind, $context)) | Out-Null
        $countExceeded = $count -gt $countLimit
        $rateExceeded = $rate -gt $rateLimit
        if ($countExceeded -or $rateExceeded) {
            if ((Test-FileTransferWarningCapExemption `
                    -Summary $Summary `
                    -FallbackDiagnostics $FallbackDiagnostics `
                    -Kind $kind `
                    -Context $context `
                    -CountExceeded $countExceeded `
                    -RateExceeded $rateExceeded)) {
                $exemptedKinds.Add($kind) | Out-Null
                $exemptedDetails.Add(('warning cap exemption: kind={0}; context={1}; incident_count={2}; raw_event_count={3}; rate_per_second={4}; count_limit={5}; rate_limit_per_second={6}; reason=completed_post_tuna_fallback_frontier_terminal_proof' -f $kind, $context, $count, $rawCount, $rate.ToString('0.###', $culture), $countLimit, $rateLimit.ToString('0.###', $culture))) | Out-Null
                continue
            }

            $exceededKinds.Add($kind) | Out-Null
            $exceededContexts.Add(('{0}:{1}' -f $kind, $context)) | Out-Null
            $exceededDetails.Add(('warning cap exceeded: kind={0}; context={1}; incident_count={2}; raw_event_count={3}; rate_per_second={4}; count_limit={5}; rate_limit_per_second={6}' -f $kind, $context, $count, $rawCount, $rate.ToString('0.###', $culture), $countLimit, $rateLimit.ToString('0.###', $culture))) | Out-Null
            foreach ($event in @($incidentEvents | Select-Object -First 10)) {
                $exceededEvents.Add($event) | Out-Null
            }
        }
    }

    return [pscustomobject]@{
        Policy = 'strict_small'
        CountUnit = 'incident'
        CountLimit = $countLimit
        RateLimitPerSecond = $rateLimit.ToString('0.###', $culture)
        DurationSeconds = $durationSeconds
        KindCounts = if ($kindCounts.Count -gt 0) { $kindCounts.ToArray() -join ',' } else { '(none)' }
        RawKindCounts = if ($rawKindCounts.Count -gt 0) { $rawKindCounts.ToArray() -join ',' } else { '(none)' }
        KindRatesPerSecond = if ($kindRates.Count -gt 0) { $kindRates.ToArray() -join ',' } else { '(none)' }
        KindContexts = if ($kindContexts.Count -gt 0) { $kindContexts.ToArray() -join ',' } else { '(none)' }
        ExceededKinds = @($exceededKinds.ToArray())
        ExceededKindsText = if ($exceededKinds.Count -gt 0) { $exceededKinds.ToArray() -join ',' } else { '(none)' }
        ExceededContextsText = if ($exceededContexts.Count -gt 0) { $exceededContexts.ToArray() -join ',' } else { '(none)' }
        ExceededDetails = @($exceededDetails.ToArray())
        ExceededEvents = @($exceededEvents.ToArray())
        ExemptedKindsText = if ($exemptedKinds.Count -gt 0) { $exemptedKinds.ToArray() -join ',' } else { '(none)' }
        ExemptedDetails = @($exemptedDetails.ToArray())
    }
}

function Test-FileTransferWarningCapExemption {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        $FallbackDiagnostics = $null,
        [Parameter(Mandatory = $true)][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Context,
        [Parameter(Mandatory = $true)][bool]$CountExceeded,
        [Parameter(Mandatory = $true)][bool]$RateExceeded
    )

    if ($Kind -ne 'fallback_frontier_repair_churn' -or
        $Context -ne 'post_tuna_fallback' -or
        (-not $CountExceeded -and -not $RateExceeded)) {
        return $false
    }

    if ($RateExceeded -and $CountExceeded) {
        return $false
    }

    if ($Summary.InboundTerminalEvents.Count -eq 0 -or
        $Summary.OutboundTerminalEvents.Count -eq 0 -or
        -not (Test-FileTransferTerminalCompleted -TerminalEvents $Summary.TerminalEvents)) {
        return $false
    }

    if ($null -ne $FallbackDiagnostics) {
        if ([string]$FallbackDiagnostics.TerminalMissingReason -ne '(none)') {
            return $false
        }

        if ([int]$FallbackDiagnostics.SenderStillRepairing -ne 0) {
            return $false
        }
    }

    return $true
}

function Get-FileTransferStabilizationGateResult {
    param(
        [Parameter(Mandatory = $true)]$Summary,
        [ValidateSet('None', 'SwitchOff', 'MultiToggle', 'RegularActivationCycle')]
        [string]$LiveRouteProofMode = 'None'
    )

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

    $fallbackDiagnostics = Get-FileTransferFallbackV6Diagnostics -Summary $Summary

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
                FallbackDiagnostics = $fallbackDiagnostics
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

    $liveRouteProof = Get-FileTransferLiveRouteEpochProof -TransferEvents $Summary.TransferEvents -Mode $LiveRouteProofMode
    foreach ($finding in @($liveRouteProof.Findings)) {
        $operatorFinding = ([string]$finding).Replace('=', ':')
        Add-FileTransferGateFinding -List $hardFailures -Finding ("live route epoch proof: {0}" -f $operatorFinding)
    }

    $bridgeLivenessProof = Get-FileTransferBridgeLivenessIntegrationProof -Summary $Summary
    foreach ($finding in @($bridgeLivenessProof.Findings)) {
        $operatorFinding = ([string]$finding).Replace('=', ':')
        Add-FileTransferGateFinding -List $hardFailures -Finding ("bridge liveness integration: {0}" -f $operatorFinding)
    }

    if ($hardFailures.Count -gt 0) {
        [object[]]$routeEvidenceEvents = @()
        if ($null -ne $Summary.RouteConsistency) {
            $routeEvidenceEvents = @($Summary.RouteConsistency.EvidenceEvents)
        }
        $nextArtifact = if ($routeConsistencyFindings.Count -gt 0 -or $liveRouteProof.Findings.Count -gt 0 -or $bridgeLivenessProof.Findings.Count -gt 0) {
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
            EvidenceEvents = @($Summary.TerminalEvents + $hardFailureEvents + $legacyProtocolStartedEvents + $unexpectedLegacyFramesDuringV4 + $bridgeBulkFailures + $routeEvidenceEvents + $liveRouteProof.EvidenceEvents + $bridgeLivenessProof.EvidenceEvents | Select-Object -First 20)
            LiveRouteProof = $liveRouteProof
            BridgeLivenessProof = $bridgeLivenessProof
            FallbackDiagnostics = $fallbackDiagnostics
        }
    }

    if ($Summary.LiveProgressTimeoutCount -gt 0 -and $Summary.TerminalMissingAfterProgressTimeout -ne 0) {
        $warnings = New-Object System.Collections.Generic.List[string]
        Add-FileTransferGateFinding -List $warnings -Finding 'live progress timeout before requested matrix completed'
        Add-FileTransferGateFinding -List $warnings -Finding 'progress_timeout_with_receiver_gap_stall'
        if (Test-FileTransferRegularV4ProgressTimeoutRecoveryStorm -Summary $Summary) {
            Add-FileTransferGateFinding -List $warnings -Finding 'public_nkn_regular_v4_recovery_storm'
        }

        $progressTimeoutEvents = @($Summary.TransferEvents | Where-Object { $_.EventName -eq 'filetransfer_live_progress_timeout' })
        return [pscustomobject]@{
            Verdict = 'INCONCLUSIVE_PROGRESS_TIMEOUT'
            GateStatus = 'inconclusive'
            HardFailures = @()
            Warnings = @($warnings)
            NextArtifact = 'throughput-decomposition-summary.txt'
            EvidenceEvents = @($progressTimeoutEvents + $Summary.TerminalEvents | Select-Object -First 20)
            FallbackDiagnostics = $fallbackDiagnostics
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
            FallbackDiagnostics = $fallbackDiagnostics
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
            FallbackDiagnostics = $fallbackDiagnostics
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
            FallbackDiagnostics = $fallbackDiagnostics
        }
    }

    $cohabitationWarnings = @(Get-FileTransferCohabitationWarningEvents -Summary $Summary)
    $externalWarnings = @(Get-FileTransferExternalTransportWarningEvents -Summary $Summary)
    $fallbackBridgeClearWarnings = @(Get-FileTransferPostTunaFallbackBridgeClearWarningEvents -Summary $Summary)
    $runtimeUnlockBridgeClearWarnings = @(Get-FileTransferRuntimeUnlockBridgeClearWarningEvents -Summary $Summary)
    $regularNknV4BridgeClearWarnings = @(Get-FileTransferRegularNknV4BridgeClearWarningEvents -Summary $Summary)
    $fallbackSendTimeoutWarnings = @(Get-FileTransferPostTunaFallbackSendTimeoutWarningEvents -Summary $Summary)
    $fallbackFrontierRepairWarnings = @(Get-FileTransferPostTunaFallbackFrontierRepairWarningEvents -Summary $Summary)
    $fallbackReceiverStateWarnings = @(Get-FileTransferPostTunaFallbackReceiverStateWarningEvents -Summary $Summary)
    $pressureWarnings = @(Get-FileTransferRecoveredPressureWarningEvents -Summary $Summary)
    $externalCapWarnings = @(Get-FileTransferExternalTransportCapWarningEvents -Events $externalWarnings)
    $warningGroups = @(
        [pscustomobject]@{ Kind = 'recovered_post_tuna_fallback_bridge_clear'; Events = @($fallbackBridgeClearWarnings) },
        [pscustomobject]@{ Kind = 'recovered_runtime_unlock_bridge_clear'; Events = @($runtimeUnlockBridgeClearWarnings) },
        [pscustomobject]@{ Kind = 'recovered_regular_v4_bridge_clear'; Events = @($regularNknV4BridgeClearWarnings) },
        [pscustomobject]@{ Kind = 'fallback_v6_send_timeout_churn'; Events = @($fallbackSendTimeoutWarnings) },
        [pscustomobject]@{ Kind = 'fallback_frontier_repair_churn'; Events = @($fallbackFrontierRepairWarnings) },
        [pscustomobject]@{ Kind = 'fallback_receiver_state_churn'; Events = @($fallbackReceiverStateWarnings) },
        [pscustomobject]@{ Kind = 'external_transport_churn'; Events = @($externalCapWarnings) }
    )

    if ($cohabitationWarnings.Count -gt 0) {
        Add-FileTransferGateFinding -List $warnings -Finding 'screen-share media pressure overlapped the completed transfer'
    }

    if ($fallbackBridgeClearWarnings.Count -gt 0) {
        Add-FileTransferGateFinding -List $warnings -Finding 'recovered post-Tuna fallback bridge queue clear overlapped the completed transfer'
    }

    if ($runtimeUnlockBridgeClearWarnings.Count -gt 0) {
        Add-FileTransferGateFinding -List $warnings -Finding 'recovered runtime-unlock bridge queue clear overlapped the completed transfer'
    }

    if ($regularNknV4BridgeClearWarnings.Count -gt 0) {
        Add-FileTransferGateFinding -List $warnings -Finding 'recovered regular NKN V4 bridge queue clear overlapped the completed transfer'
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

    if ($pressureWarnings.Count -gt 0) {
        Add-FileTransferGateFinding -List $warnings -Finding 'repair/reorder/degraded pressure recovered before terminal completion'
    }

    $warningCap = Get-FileTransferWarningCapResult -Summary $Summary -WarningGroups $warningGroups -FallbackDiagnostics $fallbackDiagnostics
    if ($warningCap.ExceededKinds.Count -gt 0) {
        $hardFailures = New-Object System.Collections.Generic.List[string]
        foreach ($detail in @($warningCap.ExceededDetails)) {
            Add-FileTransferGateFinding -List $hardFailures -Finding $detail
        }

        return [pscustomobject]@{
            Verdict = 'FAIL_EXTERNAL_TRANSPORT_CHURN'
            GateStatus = 'fail'
            HardFailures = @($hardFailures)
            Warnings = @($warnings)
            NextArtifact = 'stability-gates-summary.txt'
            EvidenceEvents = @($warningCap.ExceededEvents + $Summary.TerminalEvents | Select-Object -First 30)
            WarningCap = $warningCap
            LiveRouteProof = $liveRouteProof
            BridgeLivenessProof = $bridgeLivenessProof
            FallbackDiagnostics = $fallbackDiagnostics
        }
    }

    if ($cohabitationWarnings.Count -gt 0) {
        $verdict = 'WARN_COHABITATION_PRESSURE'
        $nextArtifact = 'coexistence-summary.txt'
    }
    elseif ($fallbackBridgeClearWarnings.Count -gt 0 -or
            $runtimeUnlockBridgeClearWarnings.Count -gt 0 -or
            $regularNknV4BridgeClearWarnings.Count -gt 0 -or
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
        elseif ($runtimeUnlockBridgeClearWarnings.Count -gt 0) {
            'stability-gates-summary.txt'
        }
        elseif ($regularNknV4BridgeClearWarnings.Count -gt 0) {
            'stability-gates-summary.txt'
        }
        else {
            'external-transport-health-summary.txt'
        }
    }
    elseif ($pressureWarnings.Count -gt 0) {
        $verdict = 'WARN_RECOVERED_PRESSURE'
        $nextArtifact = 'repair-reorder-summary.txt'
    }

    $evidence = @($cohabitationWarnings + $fallbackBridgeClearWarnings + $runtimeUnlockBridgeClearWarnings + $regularNknV4BridgeClearWarnings + $fallbackSendTimeoutWarnings + $fallbackFrontierRepairWarnings + $fallbackReceiverStateWarnings + $externalWarnings + $pressureWarnings + $Summary.TerminalEvents | Select-Object -First 30)

    return [pscustomobject]@{
        Verdict = $verdict
        GateStatus = if ($verdict -eq 'PASS') { 'pass' } else { 'warn' }
        HardFailures = @($hardFailures)
        Warnings = @($warnings)
        NextArtifact = $nextArtifact
        EvidenceEvents = @($evidence)
        WarningCap = $warningCap
        LiveRouteProof = $liveRouteProof
        BridgeLivenessProof = $bridgeLivenessProof
        FallbackDiagnostics = $fallbackDiagnostics
    }
}
