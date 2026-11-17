# Frozen pre-lazy-extraction proof for differential reporting tests.
function Invoke-ControlPlaneIsolationBaseline {
    param(
        [Parameter(Mandatory = $true)]$Summary
    )

    $staleGenerationTreadmillScoreLimit = 6
    [object[]]$events = @($Summary.TransferEvents)
    [object[]]$postTunaRouteSelected = @(
        $events |
            Where-Object {
                $_.EventName -eq 'filetransfer_route_selected' -and
                (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'post_tuna_fallback_v6'
            }
    )
    [object[]]$deliveryResultEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_control_plane_delivery_result' } | Sort-Object Sequence)
    [object[]]$observedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_control_plane_delivery_observed' } | Sort-Object Sequence)
    [object[]]$failedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_control_plane_delivery_failed' } | Sort-Object Sequence)
    [object[]]$unavailableEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_control_plane_delivery_unavailable' } | Sort-Object Sequence)
    [object[]]$retiredStaleSendEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_control_plane_send_retired_stale_generation' } | Sort-Object Sequence)
    [object[]]$checkpointExchangeRetiredEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_checkpoint_exchange_retired' } | Sort-Object Sequence)
    [object[]]$checkpointOwnerViolationEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_checkpoint_owner_violation' } | Sort-Object Sequence)
    [object[]]$staleDeferredReplayDiscardedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_post_tuna_fallback_state_refresh_deferred_discarded' } | Sort-Object Sequence)
    [object[]]$checkpointNotPendingReissueSuppressedEvents = @(
        $events |
            Where-Object {
                $_.EventName -eq 'filetransfer_fallback_checkpoint_reissue_suppressed' -and
                (Get-FileTransferEventField -Event $_ -Name 'reason' -Default '') -eq 'checkpoint_not_pending'
            } |
            Sort-Object Sequence
    )
    [object[]]$staleProofIgnoredEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_stale_proof_ignored' } | Sort-Object Sequence)
    [object[]]$staleProofReplaySuppressedEvents = @($events | Where-Object { $_.EventName -eq 'filetransfer_fallback_stale_proof_replay_suppressed' } | Sort-Object Sequence)
    [object[]]$sessionLivenessTimeoutEvents = @($events | Where-Object { $_.EventName -eq 'session_liveness_timeout' } | Sort-Object Sequence)
    [object[]]$terminalFailureEvents = @(
        $events |
            Where-Object {
                if ($_.EventName -eq 'file_transfer_inbound_terminal' -or
                    $_.EventName -eq 'file_transfer_outbound_terminal' -or
                    $_.EventName -eq 'transfer_terminal') {
                    return (Get-FileTransferEventField -Event $_ -Name 'error_code' -Default '(none)') -ne '(none)'
                }

                return $false
            } |
            Sort-Object Sequence
    )
    [object[]]$terminalCompletedEvents = @(
        $events |
            Where-Object {
                ($_.EventName -eq 'file_transfer_inbound_terminal' -or
                    $_.EventName -eq 'file_transfer_outbound_terminal' -or
                    $_.EventName -eq 'transfer_terminal') -and
                (Get-FileTransferEventField -Event $_ -Name 'state' -Default '') -eq 'Completed' -and
                (Get-FileTransferEventField -Event $_ -Name 'error_code' -Default '(none)') -eq '(none)'
            } |
            Sort-Object Sequence
    )

    $fallbackControlPlaneKinds = @(
        'fallback_checkpoint_request',
        'fallback_checkpoint_proof',
        'receiver_state',
        'frontier_request'
    )
    $checkpointOwnerEventNames = @(
        'filetransfer_fallback_leg_authority_started',
        'filetransfer_fallback_checkpoint_reissue_requested',
        'filetransfer_fallback_tail_reconciliation_requested'
    )
    $checkpointOwnerKinds = @(
        'fallback_checkpoint_request',
        'fallback_checkpoint_proof',
        'frontier_request'
    )
    $checkpointIdsByLeg = @{}
    $activeCheckpointByLeg = @{}
    $bridgeGenerationByLeg = @{}
    $inferredCheckpointOwnerViolationEvents = New-Object System.Collections.Generic.List[object]
    $nonCurrentLegGenerationSendEvents = New-Object System.Collections.Generic.List[object]
    $currentRoute = ''
    $currentLiveRouteEpoch = 0
    $currentFallbackLegGeneration = 0
    $currentRouteByDirection = @{}
    $currentFallbackLegGenerationByDirection = @{}

    function Get-FileTransferControlPlaneDirectionKey {
        param($Event)

        $direction = Get-FileTransferEventField -Event $Event -Name 'direction' -Default ''
        if ([string]::IsNullOrWhiteSpace($direction) -or
            $direction -eq '(none)' -or
            $direction -eq 'none') {
            return ''
        }

        return $direction.ToLowerInvariant()
    }

    function Add-FileTransferCheckpointIdForLeg {
        param(
            [int64]$LegGeneration,
            [string]$CheckpointId
        )

        if ($LegGeneration -le 0 -or
            [string]::IsNullOrWhiteSpace($CheckpointId) -or
            $CheckpointId -eq '(none)' -or
            $CheckpointId -eq 'none') {
            return
        }

        $key = [string]$LegGeneration
        if (-not $checkpointIdsByLeg.ContainsKey($key)) {
            $checkpointIdsByLeg[$key] = New-Object System.Collections.Generic.List[string]
        }

        if (-not $checkpointIdsByLeg[$key].Contains($CheckpointId)) {
            $checkpointIdsByLeg[$key].Add($CheckpointId) | Out-Null
        }
    }

    function Clear-FileTransferActiveCheckpointForLeg {
        param(
            [int64]$LegGeneration,
            [string]$CheckpointId = ''
        )

        if ($LegGeneration -le 0) {
            return
        }

        $key = [string]$LegGeneration
        if (-not $activeCheckpointByLeg.ContainsKey($key)) {
            return
        }

        if ([string]::IsNullOrWhiteSpace($CheckpointId) -or
            $CheckpointId -eq '(none)' -or
            $CheckpointId -eq 'none' -or
            [string]::Equals([string]$activeCheckpointByLeg[$key], $CheckpointId, [System.StringComparison]::OrdinalIgnoreCase)) {
            $activeCheckpointByLeg.Remove($key)
        }
    }

    foreach ($event in @($events | Sort-Object Sequence)) {
        if ($event.EventName -eq 'filetransfer_route_selected' -or
            $event.EventName -eq 'filetransfer_live_route_epoch_started' -or
            $event.EventName -eq 'filetransfer_live_route_epoch_recovered') {
            $directionKey = Get-FileTransferControlPlaneDirectionKey -Event $event
            $route = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
            if (-not [string]::IsNullOrWhiteSpace($route)) {
                $currentRoute = $route
                if (-not [string]::IsNullOrWhiteSpace($directionKey)) {
                    $currentRouteByDirection[$directionKey] = $route
                }
            }

            $liveRouteEpoch = Get-FileTransferEventInt64Field -Event $event -Name 'live_route_epoch' -Default $currentLiveRouteEpoch
            if ($liveRouteEpoch -gt 0) {
                $currentLiveRouteEpoch = $liveRouteEpoch
            }

            if ($route -ne 'post_tuna_fallback_v6') {
                $currentFallbackLegGeneration = 0
                if (-not [string]::IsNullOrWhiteSpace($directionKey)) {
                    $currentFallbackLegGenerationByDirection[$directionKey] = 0
                }
            }

            continue
        }

        if ($event.EventName -eq 'filetransfer_leg_started' -or
            $event.EventName -eq 'filetransfer_fallback_leg_authority_started') {
            $directionKey = Get-FileTransferControlPlaneDirectionKey -Event $event
            $route = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
            $legGeneration = Get-FileTransferEventInt64Field -Event $event -Name 'leg_generation' -Default 0
            $checkpointId = Get-FileTransferEventField -Event $event -Name 'checkpoint_request_id' -Default ''
            $bridgeGeneration = Get-FileTransferEventInt64Field -Event $event -Name 'bridge_recovery_generation' -Default -1
            if ($route -eq 'post_tuna_fallback_v6' -and $legGeneration -gt 0) {
                $currentFallbackLegGeneration = $legGeneration
                if (-not [string]::IsNullOrWhiteSpace($directionKey)) {
                    $currentFallbackLegGenerationByDirection[$directionKey] = $legGeneration
                    $currentRouteByDirection[$directionKey] = $route
                }
                Add-FileTransferCheckpointIdForLeg -LegGeneration $legGeneration -CheckpointId $checkpointId
                if (-not [string]::IsNullOrWhiteSpace($checkpointId) -and
                    $checkpointId -ne '(none)' -and
                    $checkpointId -ne 'none') {
                    $activeCheckpointByLeg[[string]$legGeneration] = $checkpointId
                }
                if ($bridgeGeneration -ge 0) {
                    $bridgeGenerationByLeg[[string]$legGeneration] = $bridgeGeneration
                }
            }

            continue
        }

        if ($event.EventName -eq 'filetransfer_fallback_checkpoint_exchange_retired') {
            $retiredLegGeneration = Get-FileTransferEventInt64Field -Event $event -Name 'retired_leg_generation' -Default 0
            $currentLegGeneration = Get-FileTransferEventInt64Field -Event $event -Name 'current_leg_generation' -Default 0
            $retiredCheckpointId = Get-FileTransferEventField -Event $event -Name 'retired_checkpoint_request_id' -Default ''
            $currentCheckpointId = Get-FileTransferEventField -Event $event -Name 'current_checkpoint_request_id' -Default ''
            Clear-FileTransferActiveCheckpointForLeg -LegGeneration $retiredLegGeneration -CheckpointId $retiredCheckpointId
            Add-FileTransferCheckpointIdForLeg -LegGeneration $retiredLegGeneration -CheckpointId $retiredCheckpointId
            Add-FileTransferCheckpointIdForLeg -LegGeneration $currentLegGeneration -CheckpointId $currentCheckpointId
            if ($currentLegGeneration -gt 0 -and
                -not [string]::IsNullOrWhiteSpace($currentCheckpointId) -and
                $currentCheckpointId -ne '(none)' -and
                $currentCheckpointId -ne 'none') {
                $activeCheckpointByLeg[[string]$currentLegGeneration] = $currentCheckpointId
            }

            continue
        }

        $eventKind = Get-FileTransferEventField -Event $event -Name 'kind' -Default ''
        $eventRoute = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
        $eventProtocol = Get-FileTransferEventInt64Field -Event $event -Name 'protocol_version' -Default 0
        $eventLegGeneration = Get-FileTransferEventInt64Field -Event $event -Name 'leg_generation' -Default 0
        $eventLiveRouteEpoch = Get-FileTransferEventInt64Field -Event $event -Name 'live_route_epoch' -Default 0
        $eventBridgeGeneration = Get-FileTransferEventInt64Field -Event $event -Name 'bridge_recovery_generation' -Default -1
        $eventCheckpointId = Get-FileTransferEventField -Event $event -Name 'checkpoint_request_id' -Default ''
        $eventDirectionKey = Get-FileTransferControlPlaneDirectionKey -Event $event

        if ($event.EventName -eq 'filetransfer_control_plane_delivery_result' -and
            $fallbackControlPlaneKinds -contains $eventKind) {
            $routeForEvent = $currentRoute
            $fallbackLegGenerationForEvent = $currentFallbackLegGeneration
            if (-not [string]::IsNullOrWhiteSpace($eventDirectionKey)) {
                if ($currentRouteByDirection.ContainsKey($eventDirectionKey)) {
                    $routeForEvent = [string]$currentRouteByDirection[$eventDirectionKey]
                }
                if ($currentFallbackLegGenerationByDirection.ContainsKey($eventDirectionKey)) {
                    $fallbackLegGenerationForEvent = [int64]$currentFallbackLegGenerationByDirection[$eventDirectionKey]
                }
            }

            if ($eventRoute -eq 'post_tuna_fallback_v6' -and
                $eventProtocol -eq 6 -and
                $eventLiveRouteEpoch -gt 0 -and
                $currentLiveRouteEpoch -eq $eventLiveRouteEpoch -and
                $currentRoute -eq 'post_tuna_fallback_v6') {
                $routeForEvent = $currentRoute
                $fallbackLegGenerationForEvent = $currentFallbackLegGeneration
            }

            $currentFallbackStateKnown = (-not [string]::IsNullOrWhiteSpace($routeForEvent)) -or
                $fallbackLegGenerationForEvent -gt 0
            $isNonCurrentFallbackSend = $eventRoute -eq 'post_tuna_fallback_v6' -and
                $currentFallbackStateKnown -and
                (
                    $routeForEvent -ne 'post_tuna_fallback_v6' -or
                    ($fallbackLegGenerationForEvent -gt 0 -and $eventLegGeneration -gt 0 -and $eventLegGeneration -ne $fallbackLegGenerationForEvent)
                )
            if ($isNonCurrentFallbackSend) {
                $nonCurrentLegGenerationSendEvents.Add($event) | Out-Null
            }
        }

        if ($eventRoute -eq 'post_tuna_fallback_v6' -and
            $eventProtocol -eq 6 -and
            $eventLegGeneration -gt 0) {
            Add-FileTransferCheckpointIdForLeg -LegGeneration $eventLegGeneration -CheckpointId $eventCheckpointId

            $legKey = [string]$eventLegGeneration
            if ($eventBridgeGeneration -ge 0) {
                if ($bridgeGenerationByLeg.ContainsKey($legKey) -and
                    $eventBridgeGeneration -gt [int64]$bridgeGenerationByLeg[$legKey]) {
                    $activeCheckpointByLeg.Remove($legKey)
                }

                $bridgeGenerationByLeg[$legKey] = $eventBridgeGeneration
            }

            if (($checkpointOwnerEventNames -contains $event.EventName) -or
                ($event.EventName -eq 'filetransfer_control_plane_delivery_result' -and $checkpointOwnerKinds -contains $eventKind)) {
                if (-not [string]::IsNullOrWhiteSpace($eventCheckpointId) -and
                    $eventCheckpointId -ne '(none)' -and
                    $eventCheckpointId -ne 'none') {
                    if ($activeCheckpointByLeg.ContainsKey($legKey) -and
                        -not [string]::Equals([string]$activeCheckpointByLeg[$legKey], $eventCheckpointId, [System.StringComparison]::OrdinalIgnoreCase)) {
                        $inferredCheckpointOwnerViolationEvents.Add($event) | Out-Null
                    }

                    $activeCheckpointByLeg[$legKey] = $eventCheckpointId
                }
            }

            if ($event.EventName -eq 'filetransfer_fallback_leg_authority_checkpoint_accepted' -or
                $event.EventName -eq 'filetransfer_fallback_tail_reconciliation_accepted') {
                Clear-FileTransferActiveCheckpointForLeg -LegGeneration $eventLegGeneration -CheckpointId $eventCheckpointId
            }
        }
    }

    $checkpointIdsByLegTextParts = New-Object System.Collections.Generic.List[string]
    $checkpointIdCountMaxPerLeg = 0
    foreach ($entry in @($checkpointIdsByLeg.GetEnumerator() | Sort-Object { [int64]$_.Key })) {
        [object[]]$ids = @($entry.Value)
        if ($ids.Length -gt $checkpointIdCountMaxPerLeg) {
            $checkpointIdCountMaxPerLeg = $ids.Length
        }

        $checkpointIdsByLegTextParts.Add(("{0}:{1}" -f $entry.Key, ($ids -join '|'))) | Out-Null
    }
    $checkpointIdsByLegText = if ($checkpointIdsByLegTextParts.Count -gt 0) {
        $checkpointIdsByLegTextParts.ToArray() -join ';'
    }
    else {
        '(none)'
    }

    [object[]]$peerVisibleEvents = @(
        $deliveryResultEvents |
            Where-Object { (Get-FileTransferEventInt64Field -Event $_ -Name 'peer_visible_any' -Default 0) -gt 0 }
    )
    [object[]]$localOnlyRejectedEvents = @(
        $deliveryResultEvents |
            Where-Object {
                (Get-FileTransferEventInt64Field -Event $_ -Name 'local_only_rejected' -Default 0) -gt 0 -or
                (
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'control_queue' -Default 0) -gt 0 -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'peer_visible_any' -Default 0) -le 0 -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'accepted_any' -Default 0) -le 0
                )
            }
    )
    [object[]]$acceptedFailureEvents = @(
        $deliveryResultEvents |
            Where-Object { (Get-FileTransferEventInt64Field -Event $_ -Name 'accepted_any' -Default 0) -le 0 }
    )
    [object[]]$checkpointExchangeEvents = @(
        $deliveryResultEvents |
            Where-Object {
                $kind = Get-FileTransferEventField -Event $_ -Name 'kind' -Default ''
                ($kind -eq 'fallback_checkpoint_request' -or $kind -eq 'fallback_checkpoint_proof') -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'peer_visible_any' -Default 0) -gt 0
            }
    )
    [object[]]$continuousReceiverStateProofEvents = @(
        $deliveryResultEvents |
            Where-Object {
                (Get-FileTransferEventField -Event $_ -Name 'kind' -Default '') -eq 'receiver_state' -and
                    (Get-FileTransferEventField -Event $_ -Name 'route' -Default '') -eq 'post_tuna_fallback_v6' -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'protocol_version' -Default 0) -eq 6 -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'leg_generation' -Default 0) -gt 0 -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'live_route_epoch' -Default 0) -gt 0 -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'transport_epoch' -Default -1) -ge 0 -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'peer_visible_any' -Default 0) -gt 0 -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'accepted_any' -Default 0) -gt 0 -and
                    (Get-FileTransferEventInt64Field -Event $_ -Name 'local_only_rejected' -Default 0) -le 0
            }
    )
    [object[]]$metadataCheckEvents = @(
        $deliveryResultEvents |
            Where-Object {
                $kind = Get-FileTransferEventField -Event $_ -Name 'kind' -Default ''
                $kind -eq 'fallback_checkpoint_request' -or
                    $kind -eq 'fallback_checkpoint_proof' -or
                    $kind -eq 'receiver_state' -or
                    $kind -eq 'frontier_request'
            }
    )

    $metadataMissing = New-Object System.Collections.Generic.List[object]
    foreach ($event in @($metadataCheckEvents)) {
        $kind = Get-FileTransferEventField -Event $event -Name 'kind' -Default ''
        $route = Get-FileTransferEventField -Event $event -Name 'route' -Default ''
        $protocol = Get-FileTransferEventInt64Field -Event $event -Name 'protocol_version' -Default 0
        $legGeneration = Get-FileTransferEventInt64Field -Event $event -Name 'leg_generation' -Default 0
        $liveRouteEpoch = Get-FileTransferEventInt64Field -Event $event -Name 'live_route_epoch' -Default 0
        $transportEpoch = Get-FileTransferEventInt64Field -Event $event -Name 'transport_epoch' -Default -1
        $checkpointId = Get-FileTransferEventField -Event $event -Name 'checkpoint_request_id' -Default ''
        $requiresCheckpointId = $kind -eq 'fallback_checkpoint_request' -or
            $kind -eq 'fallback_checkpoint_proof'
        $isRegularV4ControlPlaneProof =
            $route -eq 'regular_nkn_v4_fast' -and
            $protocol -eq 4 -and
            ($kind -eq 'receiver_state' -or $kind -eq 'frontier_request')
        if ($isRegularV4ControlPlaneProof) {
            continue
        }

        if ($route -ne 'post_tuna_fallback_v6' -or
            $protocol -ne 6 -or
            $legGeneration -le 0 -or
            $liveRouteEpoch -le 0 -or
            $transportEpoch -lt 0 -or
            ($requiresCheckpointId -and
                ([string]::IsNullOrWhiteSpace($checkpointId) -or
                    $checkpointId -eq '(none)'))) {
            $metadataMissing.Add($event) | Out-Null
        }
    }

    $timeoutDuringValidExchangeCount = 0
    if ($postTunaRouteSelected.Count -gt 0 -and
        $deliveryResultEvents.Count -gt 0 -and
        $sessionLivenessTimeoutEvents.Count -gt 0) {
        $timeoutDuringValidExchangeCount = $sessionLivenessTimeoutEvents.Count
    }

    $findings = New-Object System.Collections.Generic.List[string]
    $evidence = New-Object System.Collections.Generic.List[object]
    foreach ($event in @($metadataMissing.ToArray())) {
        $findings.Add(("control-plane metadata missing: {0}" -f (Format-FileTransferEvidenceLine -Event $event))) | Out-Null
        $evidence.Add($event) | Out-Null
    }

    if ($localOnlyRejectedEvents.Count -gt 0) {
        $findings.Add(("control-plane peer-visible proof rejected local-only delivery count={0}" -f $localOnlyRejectedEvents.Count)) | Out-Null
        foreach ($event in @($localOnlyRejectedEvents | Select-Object -First 5)) {
            $evidence.Add($event) | Out-Null
        }
    }

    $deliveryFailureCount = $failedEvents.Count + $acceptedFailureEvents.Count
    $recoveredDeliveryFailureCount = 0
    if ($deliveryFailureCount -gt 0 -and
        ($checkpointExchangeEvents.Count -gt 0 -or $continuousReceiverStateProofEvents.Count -gt 0) -and
        $localOnlyRejectedEvents.Count -eq 0 -and
        $metadataMissing.Count -eq 0 -and
        $timeoutDuringValidExchangeCount -eq 0 -and
        $terminalFailureEvents.Count -eq 0 -and
        $terminalCompletedEvents.Count -gt 0) {
        $recoveredDeliveryFailureCount = $deliveryFailureCount
    }

    if ($deliveryFailureCount -gt 0 -and $recoveredDeliveryFailureCount -eq 0) {
        $findings.Add(("control-plane delivery failed count={0}" -f $deliveryFailureCount)) | Out-Null
        foreach ($event in @($failedEvents + $acceptedFailureEvents | Select-Object -First 5)) {
            $evidence.Add($event) | Out-Null
        }
    }

    if ($postTunaRouteSelected.Count -gt 0 -and
        $checkpointExchangeEvents.Count -eq 0 -and
        $continuousReceiverStateProofEvents.Count -eq 0) {
        $findings.Add('post-Tuna fallback route missing peer-visible control-plane checkpoint exchange') | Out-Null
        foreach ($event in @($postTunaRouteSelected + $sessionLivenessTimeoutEvents + $terminalFailureEvents | Select-Object -First 8)) {
            $evidence.Add($event) | Out-Null
        }
    }

    if ($timeoutDuringValidExchangeCount -gt 0) {
        $findings.Add(("session liveness timeout occurred during a fallback control-plane exchange count={0}" -f $timeoutDuringValidExchangeCount)) | Out-Null
        foreach ($event in @($sessionLivenessTimeoutEvents | Select-Object -First 5)) {
            $evidence.Add($event) | Out-Null
        }
    }

    $checkpointOwnerViolationCount = $checkpointOwnerViolationEvents.Count + $inferredCheckpointOwnerViolationEvents.Count
    $staleDeferredReplayDiscardedCount = 0
    foreach ($event in @($staleDeferredReplayDiscardedEvents)) {
        $reason = Get-FileTransferEventField -Event $event -Name 'reason' -Default ''
        if ($reason -eq 'checkpoint_request_mismatch' -or
            $reason -eq 'transport_epoch_mismatch' -or
            $reason -eq 'checkpoint_not_pending') {
            $staleDeferredReplayDiscardedCount++
        }
    }
    $staleGenerationTreadmillScore =
        $staleProofIgnoredEvents.Count +
        $staleProofReplaySuppressedEvents.Count +
        $checkpointOwnerViolationCount +
        $staleDeferredReplayDiscardedCount
    if ($checkpointOwnerViolationCount -gt 0) {
        $findings.Add(("fallback checkpoint owner violation count={0}; checkpoint_ids_by_leg_generation={1}" -f $checkpointOwnerViolationCount, $checkpointIdsByLegText)) | Out-Null
        foreach ($event in @($checkpointOwnerViolationEvents + $inferredCheckpointOwnerViolationEvents.ToArray() | Select-Object -First 8)) {
            $evidence.Add($event) | Out-Null
        }
    }

    if ($nonCurrentLegGenerationSendEvents.Count -gt 0) {
        $findings.Add(("fallback control-plane send used non-current leg generation count={0}" -f $nonCurrentLegGenerationSendEvents.Count)) | Out-Null
        foreach ($event in @($nonCurrentLegGenerationSendEvents.ToArray() | Select-Object -First 8)) {
            $evidence.Add($event) | Out-Null
        }
    }

    if ($staleGenerationTreadmillScore -gt $staleGenerationTreadmillScoreLimit) {
        $findings.Add(("fallback control-plane stale generation treadmill: score={0}; limit={1}; stale_ignored={2}; stale_replay_suppressed={3}; retired_stale_send_diagnostic={4}; owner_violation={5}; stale_deferred_replay_discarded={6}; checkpoint_exchange_count={7}" -f $staleGenerationTreadmillScore, $staleGenerationTreadmillScoreLimit, $staleProofIgnoredEvents.Count, $staleProofReplaySuppressedEvents.Count, $retiredStaleSendEvents.Count, $checkpointOwnerViolationCount, $staleDeferredReplayDiscardedCount, $checkpointExchangeEvents.Count)) | Out-Null
        foreach ($event in @($retiredStaleSendEvents + $staleProofIgnoredEvents + $staleProofReplaySuppressedEvents + $staleDeferredReplayDiscardedEvents | Select-Object -First 8)) {
            $evidence.Add($event) | Out-Null
        }
    }

    $verdict = if ($findings.Count -eq 0) { 'pass' } else { 'fail' }
    if ($postTunaRouteSelected.Count -eq 0 -and $deliveryResultEvents.Count -eq 0 -and $observedEvents.Count -eq 0 -and $failedEvents.Count -eq 0 -and $unavailableEvents.Count -eq 0 -and $retiredStaleSendEvents.Count -eq 0) {
        $verdict = 'none'
    }

    return [pscustomobject]@{
        Verdict = $verdict
        DeliveryResultCount = $deliveryResultEvents.Count
        PeerVisibleCount = $peerVisibleEvents.Count
        LocalOnlyRejectedCount = $localOnlyRejectedEvents.Count
        CheckpointExchangeCount = $checkpointExchangeEvents.Count
        ContinuousReceiverStateProofCount = $continuousReceiverStateProofEvents.Count
        DeliveryFailureCount = $deliveryFailureCount
        RecoveredDeliveryFailureCount = $recoveredDeliveryFailureCount
        UnavailableCount = $unavailableEvents.Count
        MetadataMissingCount = $metadataMissing.Count
        LivenessTimeoutDuringValidExchangeCount = $timeoutDuringValidExchangeCount
        RetiredStaleSendCount = $retiredStaleSendEvents.Count
        CheckpointExchangeRetiredCount = $checkpointExchangeRetiredEvents.Count
        CheckpointOwnerViolationCount = $checkpointOwnerViolationCount
        StaleDeferredReplayDiscardedCount = $staleDeferredReplayDiscardedCount
        CheckpointNotPendingReissueSuppressedCount = $checkpointNotPendingReissueSuppressedEvents.Count
        StaleProofIgnoredCount = $staleProofIgnoredEvents.Count
        StaleProofReplaySuppressedCount = $staleProofReplaySuppressedEvents.Count
        StaleReplayTreadmillCount = $staleGenerationTreadmillScore
        StaleGenerationTreadmillScore = $staleGenerationTreadmillScore
        StaleGenerationTreadmillScoreLimit = $staleGenerationTreadmillScoreLimit
        CheckpointIdsByLegGeneration = $checkpointIdsByLegText
        CheckpointIdCountMaxPerLeg = $checkpointIdCountMaxPerLeg
        ControlPlaneNonCurrentLegGenerationSendCount = $nonCurrentLegGenerationSendEvents.Count
        Findings = $findings
        EvidenceEvents = @(@($evidence.ToArray()) + @($deliveryResultEvents) | Select-Object -First 20)
    }
}
